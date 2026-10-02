namespace Chronicleaf.Core.Access;

/// <summary>
/// One bounded owner session (COMMUNITY-ACCESS-001).
/// </summary>
/// <remarks>
/// The opaque session token the browser holds is 32 random bytes and is never stored: only its
/// SHA-256 hex <see cref="IdHash"/> is, so a database disclosure does not hand out a usable
/// session. <see cref="ActorId"/> is the singleton owner identity from
/// <c>installation.owner_actor_id</c>; it is retained across token and session rotation.
/// <see cref="IdleExpiresAt"/> is refreshed on every authenticated request (12h idle timeout)
/// and can never pass <see cref="ExpiresAt"/>, the 7d absolute lifetime. A revoked session keeps
/// its row so the revocation is auditable; <see cref="IsUsable"/> is the single rule that decides
/// whether a stored session may still act.
/// </remarks>
public sealed class OwnerSession
{
    /// <summary>The SHA-256 hex of the session token: 64 lower-case hex characters.</summary>
    public const int IdHashLength = 64;

    public OwnerSession(
        Guid id,
        string idHash,
        Guid actorId,
        DateTimeOffset createdAt,
        DateTimeOffset lastSeenAt,
        DateTimeOffset idleExpiresAt,
        DateTimeOffset expiresAt,
        string antiforgeryTokenHash,
        DateTimeOffset? revokedAt = null)
    {
        Id = id;
        IdHash = idHash;
        ActorId = actorId;
        CreatedAt = createdAt;
        LastSeenAt = lastSeenAt;
        IdleExpiresAt = idleExpiresAt;
        ExpiresAt = expiresAt;
        AntiforgeryTokenHash = antiforgeryTokenHash;
        RevokedAt = revokedAt;
    }

    public Guid Id { get; }

    /// <summary>SHA-256 hex of the opaque session token; unique across sessions.</summary>
    public string IdHash { get; }

    /// <summary>The singleton owner identity; must equal <c>installation.owner_actor_id</c>.</summary>
    public Guid ActorId { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset LastSeenAt { get; }

    /// <summary>Idle deadline, refreshed by each authenticated request.</summary>
    public DateTimeOffset IdleExpiresAt { get; }

    /// <summary>Absolute deadline; never extended by activity.</summary>
    public DateTimeOffset ExpiresAt { get; }

    /// <summary>
    /// SHA-256 hex of the per-session antiforgery token. The value itself is issued once through
    /// the owner-only metadata response and is never stored, logged or returned again.
    /// </summary>
    public string AntiforgeryTokenHash { get; }

    public DateTimeOffset? RevokedAt { get; }

    public bool IsRevoked => RevokedAt.HasValue;

    /// <summary>
    /// The one rule that decides whether this stored session may still act. It is evaluated by the
    /// access service against the request clock, never by the client.
    /// </summary>
    public bool IsUsable(DateTimeOffset now) =>
        !IsRevoked && now < IdleExpiresAt && now < ExpiresAt;
}

/// <summary>
/// One recorded failed login attempt, used only to throttle repeated guessing
/// (COMMUNITY-ACCESS-002). The presented token is never stored: only a hash of the client key
/// that identifies the source of the attempt, and the instant it happened.
/// </summary>
public sealed class LoginAttempt
{
    public LoginAttempt(Guid id, string clientKeyHash, DateTimeOffset occurredAt)
    {
        Id = id;
        ClientKeyHash = clientKeyHash;
        OccurredAt = occurredAt;
    }

    public Guid Id { get; }

    /// <summary>SHA-256 hex of the client key; the raw address or agent string is not persisted.</summary>
    public string ClientKeyHash { get; }

    public DateTimeOffset OccurredAt { get; }
}

/// <summary>
/// The singleton record that remembers which access-token hash this installation was last started
/// with, so a rotated token can durably revoke every existing session.
/// </summary>
/// <remarks>
/// The stored value is a fingerprint (SHA-256 hex) of the configured token hash, not the token
/// hash itself. Both are secrets; neither is a credential that grants access on its own, and
/// neither is ever returned to a client or written to a log.
/// </remarks>
public sealed class AccessTokenState
{
    /// <summary>The single row id; this table holds exactly one row.</summary>
    public const int SingletonId = 1;

    public AccessTokenState(int id, string tokenHashFingerprint, DateTimeOffset updatedAt)
    {
        Id = id;
        TokenHashFingerprint = tokenHashFingerprint;
        UpdatedAt = updatedAt;
    }

    public int Id { get; }

    public string TokenHashFingerprint { get; }

    public DateTimeOffset UpdatedAt { get; }
}
