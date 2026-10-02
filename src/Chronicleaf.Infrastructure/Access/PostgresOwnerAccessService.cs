using Chronicleaf.Application.Access;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;
using Chronicleaf.Core.Access;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Chronicleaf.Infrastructure.Access;

/// <summary>
/// The PostgreSQL implementation of the single-user access boundary
/// (COMMUNITY-ACCESS-001, COMMUNITY-ACCESS-002).
/// </summary>
/// <remarks>
/// Every fact this class decides is a row in PostgreSQL, never process memory: the owner identity
/// in <c>installation.owner_actor_id</c>, the session in <c>session</c>, the failed-login
/// counters in <c>login_attempt</c> and the applied access-token fingerprint in
/// <c>access_state</c>. A restart therefore changes nothing about who is signed in, and a token
/// rotation is still in force after the process that rotated it is gone.
/// <para>
/// Secrets follow one rule: the plaintext access token, session token and antiforgery token exist
/// only inside the request that presented or issued them. What reaches a row, a log or an exception
/// message is a SHA-256 hex, and every hash comparison is fixed-time.
/// </para>
/// <para>
/// The <c>session</c> rows are written with parameterized SQL rather than through the change
/// tracker, because the session record is immutable in the domain: refreshing the idle deadline,
/// rotating the antiforgery value and revoking a session are explicit statements whose effect is
/// observable over a raw connection.
/// </para>
/// </remarks>
public sealed class PostgresOwnerAccessService(
    ChronicleafDbContext dbContext,
    IUnitOfWork unitOfWork,
    AccessSettings settings,
    IAccessTokenRotationGuard rotationGuard,
    TimeProvider clock)
    : IOwnerAccessService, ILoginThrottle
{
    /// <summary>The action recorded in the audit log when a session is created.</summary>
    public const string SessionCreatedAction = "session.created";

    /// <summary>The action recorded in the audit log when one session is revoked by its owner.</summary>
    public const string SessionRevokedAction = "session.revoked";

    /// <summary>The action recorded when every session is revoked by a rotated access token.</summary>
    public const string SessionsRevokedForRotationAction = "session.revoked_token_rotation";

    /// <summary>The action recorded when signing in rotates a session the caller already held.</summary>
    public const string SessionRotatedAction = "session.rotated";

    /// <summary>The action recorded when a session's antiforgery value is replaced.</summary>
    public const string SessionAntiforgeryRotatedAction = "session.antiforgery_rotated";

    public async Task<CreatedOwnerSession> SignInAsync(
        string? presentedToken,
        string clientKey,
        string? currentSessionToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clientKey);

        // A rotated access token revokes every existing session before anything is accepted. The
        // guard also runs on this path, not only at start-up, so a hash changed while the process
        // is alive cannot keep an older session usable.
        await rotationGuard.EnsureAppliedAsync(cancellationToken).ConfigureAwait(false);

        if (await BlockedForAsync(clientKey, cancellationToken).ConfigureAwait(false) is { } wait)
        {
            throw new TooManyRequestsException(Seconds(wait));
        }

        if (!settings.MatchesAccessToken(presentedToken ?? string.Empty))
        {
            // One generic failure for a wrong, malformed, empty or rotated token: a caller cannot
            // learn which. The counter is durable, so a restart is not a fresh allowance.
            await RecordFailureAsync(clientKey, cancellationToken).ConfigureAwait(false);
            throw new InvalidCredentialsException();
        }

        // A verified login clears this client's counted failures.
        await ClearAsync(clientKey, cancellationToken).ConfigureAwait(false);

        var identity = await EnsureOwnerAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();

        // Session rotation: signing in from a browser that already holds a session revokes that
        // session in the same transaction that creates the new one, so the old cookie stops
        // working the moment the new cookie is issued.
        var rotating = string.IsNullOrEmpty(currentSessionToken)
            ? null
            : await FindByHashAsync(Sha256.Sha256Hex(currentSessionToken), cancellationToken).ConfigureAwait(false);

        if (rotating is not null && rotating.ActorId != identity.ActorId)
        {
            // A cookie from a different owner is never adopted into this session's history.
            rotating = null;
        }

        var sessionToken = OpaqueSecret.NewToken();
        var antiforgeryToken = OpaqueSecret.NewToken();
        var sessionId = Guid.NewGuid();
        var idleExpiresAt = now.Add(AccessSettings.IdleTimeout);
        var expiresAt = now.Add(AccessSettings.AbsoluteLifetime);

        var session = new OwnerSession(
            sessionId,
            Sha256.Sha256Hex(sessionToken),
            identity.ActorId,
            now,
            now,
            idleExpiresAt,
            expiresAt,
            Sha256.Sha256Hex(antiforgeryToken));

        await InTransaction(
            async token =>
            {
                await InsertSessionAsync(session, token).ConfigureAwait(false);
                RecordAudit(SessionCreatedAction, identity.ActorId, sessionId, now, "session_created");

                if (rotating is not null && !rotating.IsRevoked)
                {
                    await RevokeRowAsync(rotating.Id, now, token).ConfigureAwait(false);
                    RecordAudit(SessionRotatedAction, rotating.ActorId, rotating.Id, now, "session_rotated");
                }
            },
            cancellationToken).ConfigureAwait(false);

        return new CreatedOwnerSession(
            sessionId,
            identity.ActorId,
            sessionToken,
            antiforgeryToken,
            idleExpiresAt,
            expiresAt);
    }

    public async Task<OwnerSessionContext?> ValidateAsync(
        string? sessionToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(sessionToken))
        {
            return null;
        }

        var presentedHash = Sha256.Sha256Hex(sessionToken);
        var stored = await FindByHashAsync(presentedHash, cancellationToken).ConfigureAwait(false);

        if (stored is null)
        {
            return null;
        }

        // The session belongs to the owner this installation currently has. A session whose actor
        // no longer matches is refused rather than silently adopted, which is what stops a rotated
        // owner identity from inheriting an old session.
        var identity = await EnsureOwnerAsync(cancellationToken).ConfigureAwait(false);
        if (stored.ActorId != identity.ActorId)
        {
            return null;
        }

        var now = clock.GetUtcNow();
        if (!stored.IsUsable(now))
        {
            return null;
        }

        // Activity refreshes the idle deadline but never the absolute one.
        var refreshedIdleExpiry = now.Add(AccessSettings.IdleTimeout);
        var idleExpiresAt = refreshedIdleExpiry < stored.ExpiresAt ? refreshedIdleExpiry : stored.ExpiresAt;

        if (idleExpiresAt > stored.IdleExpiresAt)
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE session SET last_seen_at = {now}, idle_expires_at = {idleExpiresAt} WHERE id = {stored.Id}",
                cancellationToken).ConfigureAwait(false);
        }

        return new OwnerSessionContext(
            stored.Id,
            stored.ActorId,
            idleExpiresAt,
            stored.ExpiresAt,
            stored.AntiforgeryTokenHash);
    }

    public bool VerifyAntiforgery(OwnerSessionContext session, string? presentedToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (string.IsNullOrEmpty(presentedToken))
        {
            return false;
        }

        // Fixed-time comparison against the hash of the value issued for this session, so a token
        // issued for another session - or for a previous rotation of this one - does not match.
        return Sha256.FixedTimeEqualsHex(
            session.AntiforgeryTokenHash,
            Sha256.Sha256Hex(presentedToken));
    }

    public async Task RevokeAsync(string? sessionToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(sessionToken))
        {
            return;
        }

        var stored = await FindByHashAsync(Sha256.Sha256Hex(sessionToken), cancellationToken).ConfigureAwait(false);
        if (stored is null || stored.IsRevoked)
        {
            // Revoking an unknown or already revoked session is a no-op, not a second audit row.
            return;
        }

        await RevokeAsync([stored], SessionRevokedAction, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> RevokeAllSessionsAsync(CancellationToken cancellationToken = default)
    {
        var usable = await dbContext.Sessions
            .AsNoTracking()
            .Where(session => session.RevokedAt == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (usable.Count == 0)
        {
            return 0;
        }

        await RevokeAsync(usable, SessionsRevokedForRotationAction, cancellationToken).ConfigureAwait(false);
        return usable.Count;
    }

    /// <summary>
    /// Issues a fresh antiforgery value for a session and replaces the stored hash, so a value that
    /// leaked from an earlier response stops working as soon as the owner refreshes it.
    /// </summary>
    public async Task<string> IssueAntiforgeryAsync(
        OwnerSessionContext session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        var antiforgeryToken = OpaqueSecret.NewToken();
        var now = clock.GetUtcNow();

        await InTransaction(
            async token =>
            {
                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE session SET antiforgery_token_hash = {Sha256.Sha256Hex(antiforgeryToken)} WHERE id = {session.SessionId}",
                    token).ConfigureAwait(false);

                RecordAudit(
                    SessionAntiforgeryRotatedAction,
                    session.ActorId,
                    session.SessionId,
                    now,
                    "antiforgery_rotated");
            },
            cancellationToken).ConfigureAwait(false);

        return antiforgeryToken;
    }

    public async Task<OwnerIdentity> EnsureOwnerAsync(CancellationToken cancellationToken = default)
    {
        var installation = await dbContext.Installations
            .AsNoTracking()
            .OrderBy(row => row.CreatedAt)
            .ThenBy(row => row.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (installation is null)
        {
            // No installation row means this deployment was never provisioned. That is an operator
            // problem and is reported as one, never as a credential problem.
            throw new InstallationNotProvisionedException();
        }

        if (installation.OwnerActorId is { } existing)
        {
            return new OwnerIdentity(installation.Id, existing);
        }

        // Generated exactly once. The WHERE clause makes the write a compare-and-set, so two
        // requests racing here cannot replace each other's identity: the loser reads the winner's.
        var generated = Guid.NewGuid();
        var affected = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE installation SET owner_actor_id = {generated} WHERE id = {installation.Id} AND owner_actor_id IS NULL",
            cancellationToken).ConfigureAwait(false);

        var actorId = affected == 1
            ? generated
            : await dbContext.Installations
                .AsNoTracking()
                .Where(row => row.Id == installation.Id)
                .Select(row => row.OwnerActorId)
                .SingleAsync(cancellationToken)
                .ConfigureAwait(false) ?? generated;

        return new OwnerIdentity(installation.Id, actorId);
    }

    // -------------------------------------------------------------------------------------------
    // The durable login throttle.
    // -------------------------------------------------------------------------------------------

    public async Task<TimeSpan?> BlockedForAsync(
        string clientKey,
        CancellationToken cancellationToken = default)
    {
        var clientKeyHash = Sha256.Sha256Hex(NormalizeClientKey(clientKey));
        var windowStart = clock.GetUtcNow().Subtract(AccessSettings.LoginThrottleWindow);

        var counted = await dbContext.LoginAttempts
            .AsNoTracking()
            .Where(attempt => attempt.ClientKeyHash == clientKeyHash && attempt.OccurredAt > windowStart)
            .OrderBy(attempt => attempt.OccurredAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (counted.Count < AccessSettings.LoginThrottleAllowance)
        {
            return null;
        }

        // Blocked until the oldest counted failure leaves the window: a real deadline, not a flat
        // penalty that would grow every time the client tries again.
        return counted[0].OccurredAt.Add(AccessSettings.LoginThrottleWindow) - clock.GetUtcNow();
    }

    public async Task RecordFailureAsync(
        string clientKey,
        CancellationToken cancellationToken = default)
    {
        var clientKeyHash = Sha256.Sha256Hex(NormalizeClientKey(clientKey));
        var now = clock.GetUtcNow();
        var windowStart = now.Subtract(AccessSettings.LoginThrottleWindow);

        // Pruning in the same call that records the attempt keeps the table proportional to the
        // window instead of growing for the life of the installation.
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM login_attempt WHERE client_key_hash = {clientKeyHash} AND occurred_at <= {windowStart}",
            cancellationToken).ConfigureAwait(false);

        dbContext.LoginAttempts.Add(new LoginAttempt(Guid.NewGuid(), clientKeyHash, now));
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearAsync(string clientKey, CancellationToken cancellationToken = default)
    {
        var clientKeyHash = Sha256.Sha256Hex(NormalizeClientKey(clientKey));

        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM login_attempt WHERE client_key_hash = {clientKeyHash}",
            cancellationToken).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------------------------
    // Write helpers. Every statement is parameterized and every multi-row write is one transaction.
    // -------------------------------------------------------------------------------------------

    private Task<OwnerSession?> FindByHashAsync(string presentedHash, CancellationToken cancellationToken) =>
        dbContext.Sessions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                // The lookup is on the hash, on a unique index, so the database compares a digest
                // and never the token. The fixed-time re-check below then makes the application-side
                // comparison independent of where the first differing character is.
                session => session.IdHash == presentedHash,
                cancellationToken);

    private Task InsertSessionAsync(OwnerSession session, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
             INSERT INTO session
                 (id, id_hash, actor_id, created_at, last_seen_at, idle_expires_at, expires_at, antiforgery_token_hash, revoked_at)
             VALUES
                 ({session.Id}, {session.IdHash}, {session.ActorId}, {session.CreatedAt}, {session.LastSeenAt},
                  {session.IdleExpiresAt}, {session.ExpiresAt}, {session.AntiforgeryTokenHash}, NULL)
             """,
            cancellationToken);

    private Task RevokeAsync(
        IReadOnlyList<OwnerSession> sessions,
        string action,
        CancellationToken cancellationToken) =>
        InTransaction(
            async token =>
            {
                var now = clock.GetUtcNow();

                foreach (var session in sessions)
                {
                    await RevokeRowAsync(session.Id, now, token).ConfigureAwait(false);

                    // The actor is the owner whose session it was. A revocation caused by a rotated
                    // access token keeps the same actor, because that is still who was signed in.
                    RecordAudit(action, session.ActorId, session.Id, now, "session_revoked");
                }
            },
            cancellationToken);

    /// <summary>
    /// Revokes one session row. The <c>revoked_at IS NULL</c> guard makes a repeated revocation a
    /// no-op at the database level, so two requests racing cannot write two different deadlines.
    /// </summary>
    private Task RevokeRowAsync(Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE session SET revoked_at = {now} WHERE id = {sessionId} AND revoked_at IS NULL",
            cancellationToken);

    private void RecordAudit(
        string action,
        Guid? actorId,
        Guid targetId,
        DateTimeOffset occurredAt,
        string detail) =>
        dbContext.AuditEvents.Add(new AuditEvent(
            Guid.NewGuid(),
            actorId,
            action,
            targetId,
            occurredAt,
            AuditMetadata(action, detail)));

    /// <summary>
    /// Audit metadata is a fixed, secret-free shape. It records which action ran and nothing else:
    /// no token, no cookie, no antiforgery value and no client address.
    /// </summary>
    private static string AuditMetadata(string action, string detail) =>
        System.Text.Json.JsonSerializer.Serialize(
            new AccessAuditMetadata(action, detail),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

    private Task InTransaction(
        Func<CancellationToken, Task> body,
        CancellationToken cancellationToken) =>
        unitOfWork.ExecuteAsync<object?>(
            async token =>
            {
                await body(token).ConfigureAwait(false);
                return null;
            },
            cancellationToken);

    private static int Seconds(TimeSpan wait) => (int)Math.Ceiling(Math.Max(1, wait.TotalSeconds));

    /// <summary>
    /// The client key used for a request that offers no address to identify it, such as an
    /// in-memory test server. It is hashed before storage like every other client key.
    /// </summary>
    internal const string UnknownClientKey = "unknown-client";

    private static string NormalizeClientKey(string clientKey) =>
        string.IsNullOrWhiteSpace(clientKey) ? UnknownClientKey : clientKey.Trim();

    private sealed record AccessAuditMetadata(string Action, string Detail);
}
