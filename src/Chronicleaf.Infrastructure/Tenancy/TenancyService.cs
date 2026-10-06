using System.Security.Cryptography;
using System.Text;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Tenancy;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Chronicleaf.Infrastructure.Tenancy;

/// <summary>
/// The PostgreSQL tenant boundary (CLOUD-TENANT-BOUNDARY-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Reads are ordinary queries and every one of them carries the tenant: a lookup by id can only
/// ever return a row inside the caller's own membership, and a foreign or invented id is the
/// same 404. Writes are explicit statements whose WHERE clause is the whole safety property, so
/// two requests racing decide between themselves instead of both landing.
/// </para>
/// <para>
/// Every tenant-scoped transaction also carries the tenant through
/// <c>SET LOCAL app.tenant_id</c>, so the row-level policies the migration installs see the same
/// tenant the predicates name - including on a pooled connection, where SET LOCAL dies with the
/// transaction rather than leaking into the next borrower. The Community path never sets it, and
/// the policies stay permissive while it is unset, so a deployment without tenants behaves
/// exactly as before.
/// </para>
/// </remarks>
public sealed class TenancyService(
    ChronicleafDbContext dbContext,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    TenancySettings settings,
    AccessSettings accessSettings,
    IOidcTransport oidc) : ITenancyService
{
    /// <summary>How long a login start stays completable.</summary>
    private static readonly TimeSpan LoginLifetime = TimeSpan.FromMinutes(10);

    /// <summary>The callback path the authorization URL points back at.</summary>
    internal const string CallbackPath = "/api/hosted/login/callback";

    public async Task<HostedLoginStart> StartLoginAsync(CancellationToken cancellationToken = default)
    {
        RequireHosted();

        var redirectUri = BuildRedirectUri();
        var state = OpaqueToken();
        var nonce = OpaqueToken();
        var verifier = OpaqueToken();
        var now = clock.GetUtcNow();

        await unitOfWork.ExecuteAsync<object?>(
            async token =>
            {
                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO hosted_login_requests
                         (id, state_hash, nonce_hash, code_verifier, created_at, expires_at, consumed_at)
                     VALUES
                         ({Guid.NewGuid()}, {Sha256.Sha256Hex(state)}, {Sha256.Sha256Hex(nonce)},
                          {verifier}, {now}, {now.Add(LoginLifetime)}, NULL)
                     """,
                    token).ConfigureAwait(false);
                return null;
            },
            cancellationToken).ConfigureAwait(false);

        var issuer = settings.NormalizedIssuer!;
        var authorizationUrl =
            issuer.TrimEnd('/') + "/authorize"
            + "?response_type=code"
            + "&client_id=" + Uri.EscapeDataString(settings.OidcClientId!)
            + "&redirect_uri=" + Uri.EscapeDataString(redirectUri)
            + "&scope=" + Uri.EscapeDataString("openid")
            + "&state=" + Uri.EscapeDataString(state)
            + "&nonce=" + Uri.EscapeDataString(nonce)
            + "&code_challenge=" + Uri.EscapeDataString(ChallengeOf(verifier))
            + "&code_challenge_method=S256";

        return new HostedLoginStart(authorizationUrl, state);
    }

    public async Task<CreatedHostedSession> CompleteLoginAsync(
        string? code,
        string? state,
        CancellationToken cancellationToken = default)
    {
        RequireHosted();

        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            throw new OidcLoginFailedException();
        }

        var now = clock.GetUtcNow();
        var stateHash = Sha256.Sha256Hex(state);

        // Single-use before any network call: the compare-and-set consumes the request, so a
        // replayed state finds nothing even when the first exchange is still running.
        var consumed = await ConsumeLoginRequestAsync(stateHash, now, cancellationToken).ConfigureAwait(false);
        if (consumed is null)
        {
            throw new OidcLoginFailedException();
        }

        var issuer = settings.NormalizedIssuer!;
        OidcDiscovery discovery;
        OidcTokenResponse tokens;
        string keySet;
        try
        {
            discovery = await oidc.GetDiscoveryAsync(issuer, cancellationToken).ConfigureAwait(false);
            tokens = await oidc.ExchangeCodeAsync(
                discovery.TokenEndpoint,
                code,
                consumed.CodeVerifier ?? string.Empty,
                settings.OidcClientId!,
                BuildRedirectUri(),
                cancellationToken).ConfigureAwait(false);
            keySet = await oidc.GetKeySetAsync(discovery.JwksUri, cancellationToken).ConfigureAwait(false);
        }
        catch (OidcLoginFailedException)
        {
            throw;
        }
        catch (Exception)
        {
            // The issuer is a dependency, but its answers never become envelope text: every
            // failure here is the same generic refusal as a mis-signed token.
            throw new OidcLoginFailedException();
        }

        var identity = OidcIdTokenValidator.Validate(
            tokens.IdToken,
            issuer,
            settings.OidcClientId!,
            consumed.NonceHash,
            keySet,
            now);

        return await IssueSessionAsync(identity, now, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TenantMembershipView>> ListTenantsAsync(
        OwnerSessionContext session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        RequireHosted();

        var user = await RequireHostedUserAsync(session, cancellationToken).ConfigureAwait(false);

        // Ordered by the entities' own columns before projecting, and projected to raw
        // values first: GUID formatting happens client-side, because it is not SQL.
        var rows = await dbContext.TenantMemberships.AsNoTracking()
            .Where(membership => membership.UserId == user.Id && membership.RevokedAt == null)
            .Join(
                dbContext.Tenants.AsNoTracking(),
                membership => membership.TenantId,
                tenant => tenant.Id,
                (membership, tenant) => new { membership.Role, Tenant = tenant })
            .Join(
                dbContext.TenantScopes.AsNoTracking(),
                joined => joined.Tenant.Id,
                scope => scope.TenantId,
                (joined, scope) => new { joined.Tenant.Id, joined.Tenant.Name, joined.Role, ScopeId = scope.Id })
            .OrderBy(joined => joined.Name)
            .ThenBy(joined => joined.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Select(row => new TenantMembershipView(
                row.Id.ToString("D"),
                row.Name,
                row.Role,
                row.ScopeId.ToString("D")))
            .ToList();
    }

    public async Task<TenantView> CreateTenantAsync(
        OwnerSessionContext session,
        string? name,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        RequireHosted();

        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > Tenant.NameMaxLength)
        {
            throw new ValidationFailedException(
                "name",
                $"The tenant name must be 1 to {Tenant.NameMaxLength} characters.");
        }

        var user = await RequireHostedUserAsync(session, cancellationToken).ConfigureAwait(false);
        var installation = await RequireInstallationAsync(cancellationToken).ConfigureAwait(false);

        var owned = await dbContext.TenantMemberships.AsNoTracking()
            .Where(membership =>
                membership.UserId == user.Id
                && membership.RevokedAt == null
                && membership.Role == TenantRoles.Owner)
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);
        if (owned >= settings.MaxTenantsPerUser)
        {
            throw new TenantLimitReachedException(settings.MaxTenantsPerUser);
        }

        var now = clock.GetUtcNow();
        var tenantId = Guid.NewGuid();
        var scopeId = Guid.NewGuid();

        await unitOfWork.ExecuteAsync<object?>(
            async token =>
            {
                await SetTenantContextAsync(tenantId, token).ConfigureAwait(false);
                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO tenants (id, name, created_at, revision)
                     VALUES ({tenantId}, {trimmed}, {now}, {1L})
                     """,
                    token).ConfigureAwait(false);
                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO tenant_memberships (tenant_id, user_id, role, created_at, revoked_at)
                     VALUES ({tenantId}, {user.Id}, {TenantRoles.Owner}, {now}, NULL)
                     """,
                    token).ConfigureAwait(false);
                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO tenant_scopes (id, tenant_id, installation_id, created_at)
                     VALUES ({scopeId}, {tenantId}, {installation.Id}, {now})
                     """,
                    token).ConfigureAwait(false);
                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE hosted_sessions SET active_tenant_id = {tenantId} WHERE id = {session.SessionId} AND revoked_at IS NULL",
                    token).ConfigureAwait(false);
                RecordAudit(TenantCreatedAction, user.Id, tenantId, now);
                return null;
            },
            cancellationToken).ConfigureAwait(false);

        return new TenantView(tenantId.ToString("D"), trimmed, 1, TenantRoles.Owner);
    }

    public async Task<TenantView> SelectTenantAsync(
        OwnerSessionContext session,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        RequireHosted();

        var user = await RequireHostedUserAsync(session, cancellationToken).ConfigureAwait(false);
        var membership = await FindMembershipAsync(tenantId, user.Id, cancellationToken).ConfigureAwait(false);
        if (membership is null)
        {
            throw new TenantNotFoundException();
        }

        if (!membership.IsLive)
        {
            throw new TenantAccessDeniedException();
        }

        var now = clock.GetUtcNow();
        await unitOfWork.ExecuteAsync<object?>(
            async token =>
            {
                await SetTenantContextAsync(tenantId, token).ConfigureAwait(false);
                var affected = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE hosted_sessions SET active_tenant_id = {tenantId} WHERE id = {session.SessionId} AND revoked_at IS NULL",
                    token).ConfigureAwait(false);
                if (affected != 1)
                {
                    throw new UnauthenticatedException();
                }

                RecordAudit(TenantSelectedAction, user.Id, tenantId, now);
                return null;
            },
            cancellationToken).ConfigureAwait(false);

        var tenant = await dbContext.Tenants.AsNoTracking()
            .Where(row => row.Id == tenantId)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);

        return new TenantView(tenant.Id.ToString("D"), tenant.Name, tenant.Revision, membership.Role);
    }

    public async Task RevokeMembershipAsync(
        OwnerSessionContext session,
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        RequireHosted();

        var caller = await RequireHostedUserAsync(session, cancellationToken).ConfigureAwait(false);
        var callerMembership = await FindMembershipAsync(tenantId, caller.Id, cancellationToken).ConfigureAwait(false);
        if (callerMembership is null || !callerMembership.IsLive)
        {
            // No live membership of the caller's own: the same denial whether the tenant is
            // foreign, invented or merely no longer theirs.
            throw new TenantAccessDeniedException();
        }

        if (callerMembership.Role != TenantRoles.Owner)
        {
            throw new TenantAccessDeniedException();
        }

        if (userId == caller.Id)
        {
            throw new ValidationFailedException("userId", "An owner cannot revoke their own membership.");
        }

        var target = await FindMembershipAsync(tenantId, userId, cancellationToken).ConfigureAwait(false);
        if (target is null || !target.IsLive)
        {
            throw new TenantNotFoundException();
        }

        var now = clock.GetUtcNow();
        await unitOfWork.ExecuteAsync<object?>(
            async token =>
            {
                await SetTenantContextAsync(tenantId, token).ConfigureAwait(false);
                var affected = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     UPDATE tenant_memberships SET revoked_at = {now}
                     WHERE tenant_id = {tenantId} AND user_id = {userId} AND revoked_at IS NULL
                     """,
                    token).ConfigureAwait(false);
                if (affected != 1)
                {
                    throw new TenantNotFoundException();
                }

                // Removing the membership revokes the sessions that acted under it: a revoked
                // member's cookie stops authenticating before any domain lookup runs.
                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     UPDATE hosted_sessions SET revoked_at = {now}
                     WHERE user_id = {userId} AND active_tenant_id = {tenantId} AND revoked_at IS NULL
                     """,
                    token).ConfigureAwait(false);
                RecordAudit(MembershipRevokedAction, caller.Id, tenantId, now);
                return null;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordImportAsync(
        OwnerSessionContext session,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        RequireHosted();

        var user = await RequireHostedUserAsync(session, cancellationToken).ConfigureAwait(false);
        var membership = await FindMembershipAsync(tenantId, user.Id, cancellationToken).ConfigureAwait(false);
        if (membership is null)
        {
            throw new TenantNotFoundException();
        }

        if (!membership.IsLive)
        {
            throw new TenantAccessDeniedException();
        }

        var installation = await RequireInstallationAsync(cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();

        // Idempotent: the acknowledgment is recorded once, and repeating it writes nothing new.
        await unitOfWork.ExecuteAsync<object?>(
            async token =>
            {
                await SetTenantContextAsync(tenantId, token).ConfigureAwait(false);
                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO hosted_imports (id, installation_id, tenant_id, user_id, created_at)
                     VALUES ({Guid.NewGuid()}, {installation.Id}, {tenantId}, {user.Id}, {now})
                     ON CONFLICT (installation_id, tenant_id) DO NOTHING
                     """,
                    token).ConfigureAwait(false);
                RecordAudit(ImportRecordedAction, user.Id, tenantId, now);
                return null;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Guid?> ResolveTenantScopeAsync(
        OwnerSessionContext session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        try
        {
            return await RequireTenantScopeAsync(session, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception
            is TenantSelectionRequiredException
            or TenantAccessDeniedException
            or UnauthenticatedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Resolves the domain scope id for a validated Hosted session, refusing with the reason
    /// rather than with a null: no selected tenant is <c>tenant_selection_required</c>, a
    /// revoked or missing membership is <c>tenant_access_denied</c>, and a session that no
    /// longer authenticates is <c>unauthenticated</c>. Community sessions never reach here.
    /// </summary>
    internal async Task<Guid> RequireTenantScopeAsync(
        OwnerSessionContext session,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var hosted = await dbContext.HostedSessions.AsNoTracking()
            .Where(row => row.Id == session.SessionId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (hosted is null
            || hosted.UserId != session.ActorId
            || !hosted.IsUsable(clock.GetUtcNow()))
        {
            throw new UnauthenticatedException();
        }

        if (hosted.ActiveTenantId is not { } tenantId)
        {
            throw new TenantSelectionRequiredException();
        }

        var membership = await FindMembershipAsync(tenantId, hosted.UserId, cancellationToken).ConfigureAwait(false);
        if (membership is null || !membership.IsLive)
        {
            throw new TenantAccessDeniedException();
        }

        var scope = await dbContext.TenantScopes.AsNoTracking()
            .Where(row => row.TenantId == tenantId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (scope is null)
        {
            throw new TenantAccessDeniedException();
        }

        // A Community volume is never silently reassigned: acting under a tenant on an
        // installation that already holds Community domain rows additionally requires the
        // explicit import for exactly this tenant. Tenant administration itself - listing,
        // selecting, revoking, recording the import - needs no import, because none of it
        // addresses a Community row.
        await RequireImportForCommunityVolumeAsync(scope.InstallationId, tenantId, cancellationToken)
            .ConfigureAwait(false);

        return scope.Id;
    }

    // -------------------------------------------------------------------------------------------
    // Session validation shared with the access decorator. The decorator authenticates the cookie;
    // this service owns what the session may do.
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// Resolves a presented Hosted session cookie to a usable session, refreshing the idle
    /// deadline. Returns <c>null</c> for every refusal, like the owner validation it stands
    /// beside.
    /// </summary>
    internal async Task<OwnerSessionContext?> ValidateHostedAsync(
        string? sessionToken,
        CancellationToken cancellationToken)
    {
        if (!settings.IsHosted || string.IsNullOrEmpty(sessionToken))
        {
            return null;
        }

        var presentedHash = Sha256.Sha256Hex(sessionToken);
        var stored = await dbContext.HostedSessions.AsNoTracking()
            .Where(row => row.IdHash == presentedHash)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (stored is null
            || !Sha256.FixedTimeEqualsHex(stored.IdHash, presentedHash))
        {
            return null;
        }

        var now = clock.GetUtcNow();
        if (!stored.IsUsable(now))
        {
            return null;
        }

        var refreshedIdleExpiry = now.Add(AccessSettings.IdleTimeout);
        var idleExpiresAt = refreshedIdleExpiry < stored.ExpiresAt ? refreshedIdleExpiry : stored.ExpiresAt;

        if (idleExpiresAt > stored.IdleExpiresAt)
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE hosted_sessions SET last_seen_at = {now}, idle_expires_at = {idleExpiresAt} WHERE id = {stored.Id}",
                cancellationToken).ConfigureAwait(false);
        }

        return new OwnerSessionContext(
            stored.Id,
            stored.UserId,
            idleExpiresAt,
            stored.ExpiresAt,
            stored.AntiforgeryTokenHash);
    }

    /// <summary>True when the session id belongs to a Hosted session row.</summary>
    internal Task<bool> IsHostedSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        dbContext.HostedSessions.AsNoTracking()
            .Where(row => row.Id == sessionId)
            .AnyAsync(cancellationToken);

    /// <summary>
    /// Issues a fresh antiforgery value for a Hosted session. The owner path never reaches here:
    /// the decorator routes by stored row, not by claim.
    /// </summary>
    internal async Task<string> IssueHostedAntiforgeryAsync(
        OwnerSessionContext session,
        CancellationToken cancellationToken)
    {
        var antiforgeryToken = OpaqueToken();
        var now = clock.GetUtcNow();

        await unitOfWork.ExecuteAsync<object?>(
            async token =>
            {
                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE hosted_sessions SET antiforgery_token_hash = {Sha256.Sha256Hex(antiforgeryToken)} WHERE id = {session.SessionId}",
                    token).ConfigureAwait(false);
                RecordAudit(AntiforgeryRotatedAction, session.ActorId, session.SessionId, now);
                return null;
            },
            cancellationToken).ConfigureAwait(false);

        return antiforgeryToken;
    }

    /// <summary>Revokes the Hosted session a cookie names. Unknown tokens are a no-op.</summary>
    internal async Task RevokeHostedAsync(string? sessionToken, CancellationToken cancellationToken)
    {
        if (!settings.IsHosted || string.IsNullOrEmpty(sessionToken))
        {
            return;
        }

        var stored = await dbContext.HostedSessions.AsNoTracking()
            .Where(row => row.IdHash == Sha256.Sha256Hex(sessionToken))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (stored is null || stored.RevokedAt is not null)
        {
            return;
        }

        var now = clock.GetUtcNow();
        await unitOfWork.ExecuteAsync<object?>(
            async token =>
            {
                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE hosted_sessions SET revoked_at = {now} WHERE id = {stored.Id} AND revoked_at IS NULL",
                    token).ConfigureAwait(false);
                RecordAudit(SessionRevokedAction, stored.UserId, stored.Id, now);
                return null;
            },
            cancellationToken).ConfigureAwait(false);
    }

    // -------------------------------------------------------------------------------------------
    // Private helpers.
    // -------------------------------------------------------------------------------------------

    private void RequireHosted()
    {
        if (!settings.IsHosted)
        {
            // Hosted identity does not exist in Community: the same generic refusal as a bad
            // exchange, so Community behavior is unchanged and nothing about the mode leaks.
            throw new OidcLoginFailedException();
        }
    }

    private string BuildRedirectUri()
    {
        // The callback the authorization URL points back at is this site's own: the public origin
        // the operator configured for mutations, plus the fixed callback path. Without a public
        // origin no login can complete, so starting one is refused rather than pointed elsewhere.
        var origin = accessSettings.PublicOriginUri?.GetLeftPart(UriPartial.Authority);
        if (string.IsNullOrEmpty(origin))
        {
            throw new OidcLoginFailedException();
        }

        return origin.TrimEnd('/') + CallbackPath;
    }

    private async Task<HostedUser> RequireHostedUserAsync(
        OwnerSessionContext session,
        CancellationToken cancellationToken)
    {
        var hosted = await dbContext.HostedSessions.AsNoTracking()
            .Where(row => row.Id == session.SessionId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (hosted is null
            || hosted.UserId != session.ActorId
            || !hosted.IsUsable(clock.GetUtcNow()))
        {
            throw new UnauthenticatedException();
        }

        var user = await dbContext.HostedUsers.AsNoTracking()
            .Where(row => row.Id == hosted.UserId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return user ?? throw new UnauthenticatedException();
    }

    private async Task<ConsumedLoginRequest?> ConsumeLoginRequestAsync(
        string stateHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ConsumedLoginRequest? consumed = null;

        await unitOfWork.ExecuteAsync<object?>(
            async token =>
            {
                var pending = await dbContext.HostedLoginRequests.AsNoTracking()
                    .Where(row => row.StateHash == stateHash)
                    .SingleOrDefaultAsync(token)
                    .ConfigureAwait(false);

                if (pending is null
                    || !Sha256.FixedTimeEqualsHex(pending.StateHash, stateHash)
                    || !pending.IsPending(now))
                {
                    return null;
                }

                var affected = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE hosted_login_requests SET consumed_at = {now} WHERE id = {pending.Id} AND consumed_at IS NULL",
                    token).ConfigureAwait(false);

                if (affected == 1)
                {
                    consumed = new ConsumedLoginRequest(pending.NonceHash, pending.CodeVerifier);
                }

                return null;
            },
            cancellationToken).ConfigureAwait(false);

        return consumed;
    }

    private async Task<CreatedHostedSession> IssueSessionAsync(
        OidcIdentity identity,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var sessionToken = OpaqueToken();
        var antiforgeryToken = OpaqueToken();
        var sessionId = Guid.NewGuid();
        var idleExpiresAt = now.Add(AccessSettings.IdleTimeout);
        var expiresAt = idleExpiresAt < now.Add(AccessSettings.AbsoluteLifetime)
            ? idleExpiresAt
            : now.Add(AccessSettings.AbsoluteLifetime);

        Guid userId = Guid.Empty;

        await unitOfWork.ExecuteAsync<object?>(
            async token =>
            {
                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO hosted_users (id, issuer, subject, created_at)
                     VALUES ({Guid.NewGuid()}, {identity.Issuer}, {identity.Subject}, {now})
                     ON CONFLICT (issuer, subject) DO NOTHING
                     """,
                    token).ConfigureAwait(false);

                userId = await dbContext.HostedUsers.AsNoTracking()
                    .Where(row => row.Issuer == identity.Issuer && row.Subject == identity.Subject)
                    .Select(row => row.Id)
                    .SingleAsync(token)
                    .ConfigureAwait(false);

                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO hosted_sessions
                         (id, id_hash, user_id, active_tenant_id, created_at, last_seen_at,
                          idle_expires_at, expires_at, antiforgery_token_hash, revoked_at)
                     VALUES
                         ({sessionId}, {Sha256.Sha256Hex(sessionToken)}, {userId}, NULL,
                          {now}, {now}, {idleExpiresAt}, {expiresAt}, {Sha256.Sha256Hex(antiforgeryToken)}, NULL)
                     """,
                    token).ConfigureAwait(false);

                RecordAudit(SessionCreatedAction, userId, sessionId, now);
                return null;
            },
            cancellationToken).ConfigureAwait(false);

        return new CreatedHostedSession(
            sessionId,
            userId,
            sessionToken,
            antiforgeryToken,
            idleExpiresAt,
            expiresAt);
    }

    private Task<TenantMembership?> FindMembershipAsync(
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken) =>
        dbContext.TenantMemberships.AsNoTracking()
            .Where(membership => membership.TenantId == tenantId && membership.UserId == userId)
            .SingleOrDefaultAsync(cancellationToken);

    private async Task<Installation> RequireInstallationAsync(CancellationToken cancellationToken)
    {
        var installation = await dbContext.Installations.AsNoTracking()
            .OrderBy(row => row.CreatedAt)
            .ThenBy(row => row.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return installation ?? throw new InstallationNotProvisionedException();
    }

    /// <summary>
    /// Refuses tenant domain access on an installation that already holds Community domain rows
    /// until the explicit import acknowledges them for exactly this tenant. The check names no
    /// row and moves none: the Community scope stays unreachable either way.
    /// </summary>
    private async Task RequireImportForCommunityVolumeAsync(
        Guid installationId,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        // Milestones and evidence live under projects, so projects, media and credentials
        // cover every Community volume: anything collected is attached to a project row.
        var communityRows =
            await dbContext.Projects.AsNoTracking()
                .Where(project => project.ScopeId == installationId)
                .AnyAsync(cancellationToken)
                .ConfigureAwait(false)
            || await dbContext.Media.AsNoTracking()
                .Where(media => media.ScopeId == installationId)
                .AnyAsync(cancellationToken)
                .ConfigureAwait(false)
            || await dbContext.Credentials.AsNoTracking()
                .Where(credential => credential.ScopeId == installationId)
                .AnyAsync(cancellationToken)
                .ConfigureAwait(false);

        if (!communityRows)
        {
            return;
        }

        var imported = await dbContext.HostedImports.AsNoTracking()
            .Where(row => row.InstallationId == installationId && row.TenantId == tenantId)
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!imported)
        {
            throw new CommunityImportRequiredException();
        }
    }

    /// <summary>
    /// Carries the tenant through the transaction for the row-level policies, on the pooled
    /// connection this unit of work holds. SET LOCAL dies with the transaction, so the next
    /// borrower of the pooled connection inherits nothing.
    /// </summary>
    private Task SetTenantContextAsync(Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT set_config('app.tenant_id', {tenantId.ToString("D")}, true)",
            cancellationToken);

    private void RecordAudit(string action, Guid? actorId, Guid targetId, DateTimeOffset occurredAt) =>
        dbContext.AuditEvents.Add(new AuditEvent(
            Guid.NewGuid(),
            actorId,
            action,
            targetId,
            occurredAt,
            System.Text.Json.JsonSerializer.Serialize(
                new TenancyAuditMetadata(action),
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))));

    /// <summary>The PKCE S256 challenge for a verifier: base64url of its SHA-256.</summary>
    private static string ChallengeOf(string verifier)
    {
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Convert.ToBase64String(hash).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string OpaqueToken() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private sealed record ConsumedLoginRequest(string NonceHash, string? CodeVerifier);

    private sealed record TenancyAuditMetadata(string Action);

    internal const string TenantCreatedAction = "tenant_created";
    internal const string TenantSelectedAction = "tenant_selected";
    internal const string MembershipRevokedAction = "tenant_membership_revoked";
    internal const string ImportRecordedAction = "hosted_import_recorded";
    internal const string SessionCreatedAction = "hosted_session_created";
    internal const string SessionRevokedAction = "hosted_session_revoked";
    internal const string AntiforgeryRotatedAction = "hosted_antiforgery_rotated";
}
