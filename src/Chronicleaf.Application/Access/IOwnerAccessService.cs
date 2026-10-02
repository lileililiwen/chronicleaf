using Chronicleaf.Core.Access;

namespace Chronicleaf.Application.Access;

/// <summary>
/// The singleton owner identity of this installation, as stored in
/// <c>installation.owner_actor_id</c>.
/// </summary>
public sealed record OwnerIdentity(Guid InstallationId, Guid ActorId);

/// <summary>
/// A newly created owner session. <see cref="SessionToken"/> and <see cref="AntiforgeryToken"/>
/// are the plaintext values and exist only in this result: they are never stored, never logged and
/// never returned again after the response that carries them.
/// </summary>
public sealed record CreatedOwnerSession(
    Guid SessionId,
    Guid ActorId,
    string SessionToken,
    string AntiforgeryToken,
    DateTimeOffset IdleExpiresAt,
    DateTimeOffset ExpiresAt);

/// <summary>
/// The session a validated request proved. It carries the stored antiforgery <em>hash</em> so a
/// mutation can be checked in constant time without another read; the plaintext value is never
/// held here, and this record is never serialized to a client.
/// </summary>
public sealed record OwnerSessionContext(
    Guid SessionId,
    Guid ActorId,
    DateTimeOffset IdleExpiresAt,
    DateTimeOffset ExpiresAt,
    string AntiforgeryTokenHash);

/// <summary>
/// The access boundary's own contract, kept free of HTTP and of storage: everything the API layer
/// needs to turn a request into a decision, and everything a later package needs in order to read
/// the authenticated owner without re-implementing the check.
/// </summary>
public interface IOwnerAccessService
{
    /// <summary>
    /// Verifies a presented setup token and creates a bounded owner session. Every failure is the
    /// same generic <c>invalid_credentials</c> answer: a caller can never learn whether the token
    /// exists, was malformed, or was already rotated.
    /// </summary>
    /// <param name="presentedToken">The setup token the caller submitted.</param>
    /// <param name="clientKey">The key failed logins are counted against; hashed before storage.</param>
    /// <param name="currentSessionToken">
    /// The session cookie the request already carried, if any. Signing in rotates it: the old
    /// session is revoked in the same transaction as the new one, so the previous cookie stops
    /// working the moment the new one is issued.
    /// </param>
    /// <param name="cancellationToken">Cancels the work in progress.</param>
    /// <exception cref="TooManyRequestsException">
    /// Thrown before any comparison when the client is inside its throttle window.
    /// </exception>
    /// <exception cref="InvalidCredentialsException">Thrown for any unverified token.</exception>
    Task<CreatedOwnerSession> SignInAsync(
        string? presentedToken,
        string clientKey,
        string? currentSessionToken = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves a presented session cookie to a usable owner session, refreshing the idle
    /// deadline. Returns <c>null</c> for every refusal, so a caller cannot branch on the reason.
    /// </summary>
    Task<OwnerSessionContext?> ValidateAsync(
        string? sessionToken,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Constant-time check of an antiforgery value against the session it was issued for. A
    /// session-scoped token from another session is not accepted.
    /// </summary>
    bool VerifyAntiforgery(OwnerSessionContext session, string? presentedToken);

    /// <summary>
    /// Issues a fresh antiforgery value for a session and replaces the one the session had, so a
    /// value that leaked stops working as soon as the owner refreshes it. This is the only place a
    /// plaintext antiforgery value is ever produced.
    /// </summary>
    Task<string> IssueAntiforgeryAsync(
        OwnerSessionContext session,
        CancellationToken cancellationToken = default);

    /// <summary>Revokes one session. Idempotent: revoking an already revoked session is a no-op.</summary>
    Task RevokeAsync(string? sessionToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rotates the current access token: revokes every existing session and records the new
    /// fingerprint durably, so the revocation survives a restart.
    /// </summary>
    /// <returns>How many sessions were revoked.</returns>
    Task<int> RevokeAllSessionsAsync(CancellationToken cancellationToken = default);

    /// <summary>The owner identity, generating and persisting it exactly once if it is still null.</summary>
    Task<OwnerIdentity> EnsureOwnerAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Throttles repeated login guessing (COMMUNITY-ACCESS-002). Counters are durable, so a restart
/// does not hand a caller a fresh allowance.
/// </summary>
public interface ILoginThrottle
{
    /// <summary>
    /// How long the presenting client must wait, or <c>null</c> when it may still attempt a login.
    /// The value is derived from the oldest counted failure, so it is a real deadline.
    /// </summary>
    Task<TimeSpan?> BlockedForAsync(string clientKey, CancellationToken cancellationToken = default);

    /// <summary>Records one failed attempt, after pruning the ones that left the window.</summary>
    Task RecordFailureAsync(string clientKey, CancellationToken cancellationToken = default);

    /// <summary>Clears a client's counted failures after a successful login.</summary>
    Task ClearAsync(string clientKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// The durable marker that remembers which access-token hash this installation last started with.
/// </summary>
public interface IAccessTokenRotationGuard
{
    /// <summary>
    /// Applies the configured access token hash at most once per process: the first call writes
    /// the fingerprint, and any later call with a different fingerprint revokes every session
    /// before recording the new one. Safe to call concurrently and on every login.
    /// </summary>
    Task<int> EnsureAppliedAsync(CancellationToken cancellationToken = default);

    /// <summary>How many sessions the last application revoked; used by the tests as evidence.</summary>
    int LastRevokedSessionCount { get; }
}
