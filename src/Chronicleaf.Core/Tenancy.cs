namespace Chronicleaf.Core;

/// <summary>
/// One Hosted end-user identity (CLOUD-TENANT-BOUNDARY-001), bound to exactly one site.
/// </summary>
/// <remarks>
/// A user is identified by the pair <see cref="Issuer"/> and <see cref="Subject"/>: the issuer is
/// the site-specific OIDC issuer the operator configured, and the subject is that issuer's stable
/// identifier for the person. The pair is unique in the database, so one issuer can never claim
/// another issuer's subject, and there is no cross-site login identity. A user carries no password
/// and no operator token: authentication is the verified OIDC exchange, and what the API issues
/// afterwards is a local opaque session.
/// </remarks>
public sealed class HostedUser
{
    public HostedUser(
        Guid id,
        string issuer,
        string subject,
        DateTimeOffset createdAt)
    {
        Id = id;
        Issuer = issuer;
        Subject = subject;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    /// <summary>The exact OIDC issuer URL the identity was verified against.</summary>
    public string Issuer { get; }

    /// <summary>The issuer's stable subject identifier; never a name, an email or a secret.</summary>
    public string Subject { get; }

    public DateTimeOffset CreatedAt { get; }
}

/// <summary>
/// One Hosted tenant: the isolation boundary every scoped row belongs to
/// (CLOUD-TENANT-BOUNDARY-002).
/// </summary>
/// <remarks>
/// A tenant owns its scope row (see <see cref="TenantScope"/>), and every project, credential,
/// media record, operation and evidence bundle created under a tenant's session carries that
/// scope's id. Two tenants therefore never share a <c>scope_id</c> value, so a scoped lookup can
/// never return a foreign row however the id was guessed. A tenant is created by an explicit
/// verified-user action, never by opening a Community database in Hosted mode.
/// </remarks>
public sealed class Tenant
{
    /// <summary>At most 160 characters; see the <c>tenants.name</c> column.</summary>
    public const int NameMaxLength = 160;

    public Tenant(
        Guid id,
        string name,
        DateTimeOffset createdAt,
        long revision)
    {
        Id = id;
        Name = name;
        CreatedAt = createdAt;
        Revision = revision;
    }

    public Guid Id { get; }

    /// <summary>1 to <see cref="NameMaxLength"/> characters after trimming.</summary>
    public string Name { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>The optimistic revision; the first committed version is 1.</summary>
    public long Revision { get; }
}

/// <summary>
/// The role one user holds in one tenant (CLOUD-TENANT-BOUNDARY-001).
/// </summary>
/// <remarks>
/// Stored as text. Only <c>owner</c> grants membership administration in this package; project
/// reads, edits, approval and publishing stay closed until the teams package gates them.
/// </remarks>
public static class TenantRoles
{
    /// <summary>May administer membership and act under the tenant.</summary>
    public const string Owner = "owner";

    /// <summary>May act under the tenant; cannot administer membership.</summary>
    public const string Editor = "editor";

    /// <summary>May read under the tenant; cannot mutate or administer membership.</summary>
    public const string Viewer = "viewer";

    /// <summary>Every role this package stores.</summary>
    public static readonly IReadOnlyList<string> All = [Owner, Editor, Viewer];

    /// <summary>True for exactly the stored role vocabulary.</summary>
    public static bool IsKnown(string? role) =>
        string.Equals(role, Owner, StringComparison.Ordinal)
        || string.Equals(role, Editor, StringComparison.Ordinal)
        || string.Equals(role, Viewer, StringComparison.Ordinal);
}

/// <summary>
/// One user's membership in one tenant (CLOUD-TENANT-BOUNDARY-001).
/// </summary>
/// <remarks>
/// Membership is the only thing that authorizes a hosted session to act under a tenant: a
/// revoked membership denies subsequent access, and removing it revokes the sessions that acted
/// under it. The pair <c>(tenant_id, user_id)</c> is unique, so a user holds at most one live
/// membership per tenant and revocation is a stamp on that row rather than a deletion that would
/// erase the history of who belonged where.
/// </remarks>
public sealed class TenantMembership
{
    public TenantMembership(
        Guid tenantId,
        Guid userId,
        string role,
        DateTimeOffset createdAt,
        DateTimeOffset? revokedAt = null)
    {
        TenantId = tenantId;
        UserId = userId;
        Role = role;
        CreatedAt = createdAt;
        RevokedAt = revokedAt;
    }

    public Guid TenantId { get; }

    public Guid UserId { get; }

    /// <summary>One of <see cref="TenantRoles.All"/>.</summary>
    public string Role { get; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>When the membership was revoked, or <c>null</c> while it is live.</summary>
    public DateTimeOffset? RevokedAt { get; }

    /// <summary>True while the membership authorizes access under the tenant.</summary>
    public bool IsLive => RevokedAt is null;
}

/// <summary>
/// The scope row one tenant acts under (CLOUD-TENANT-BOUNDARY-002).
/// </summary>
/// <remarks>
/// <see cref="Id"/> is the <c>scope_id</c> value domain rows carry: it is generated fresh per
/// tenant, so it can never equal the installation scope a Community database already uses. That
/// is what makes silent reassignment impossible by construction - a Hosted tenant has no way to
/// address the Community rows, and a Community deployment has no tenant scope to address either.
/// The row also records which installation it was provisioned on, so an import decision is
/// explicit and auditable rather than inferred.
/// </remarks>
public sealed class TenantScope
{
    public TenantScope(
        Guid id,
        Guid tenantId,
        Guid installationId,
        DateTimeOffset createdAt)
    {
        Id = id;
        TenantId = tenantId;
        InstallationId = installationId;
        CreatedAt = createdAt;
    }

    /// <summary>The scope id domain rows carry; generated fresh per tenant.</summary>
    public Guid Id { get; }

    public Guid TenantId { get; }

    /// <summary>The installation the tenant was provisioned on.</summary>
    public Guid InstallationId { get; }

    public DateTimeOffset CreatedAt { get; }
}

/// <summary>
/// One local Hosted session: the opaque token the API issues after a verified OIDC exchange
/// (CLOUD-TENANT-BOUNDARY-001).
/// </summary>
/// <remarks>
/// The shape mirrors the Community owner session deliberately, so the same boundary rules apply:
/// the token is stored as a SHA-256 hash, activity refreshes only the idle deadline, and a
/// mutation still needs the session's own antiforgery value from a trusted origin. What differs
/// is the identity: the actor is a <see cref="HostedUser"/> rather than the installation owner,
/// and the scope is the <see cref="ActiveTenantId"/> tenant's scope rather than the installation
/// scope. A session without an active tenant authenticates the user but authorizes no domain
/// access: domain routes answer <c>tenant_selection_required</c> until one is selected.
/// </remarks>
public sealed class HostedSession
{
    /// <summary>The stored hash length: a SHA-256 hex digest.</summary>
    public const int IdHashLength = 64;

    public HostedSession(
        Guid id,
        string idHash,
        Guid userId,
        Guid? activeTenantId,
        DateTimeOffset createdAt,
        DateTimeOffset lastSeenAt,
        DateTimeOffset idleExpiresAt,
        DateTimeOffset expiresAt,
        string antiforgeryTokenHash,
        DateTimeOffset? revokedAt = null)
    {
        Id = id;
        IdHash = idHash;
        UserId = userId;
        ActiveTenantId = activeTenantId;
        CreatedAt = createdAt;
        LastSeenAt = lastSeenAt;
        IdleExpiresAt = idleExpiresAt;
        ExpiresAt = expiresAt;
        AntiforgeryTokenHash = antiforgeryTokenHash;
        RevokedAt = revokedAt;
    }

    public Guid Id { get; }

    /// <summary>The SHA-256 hex of the opaque token; the token itself is never stored.</summary>
    public string IdHash { get; }

    public Guid UserId { get; }

    /// <summary>The tenant this session acts under, or <c>null</c> before one is selected.</summary>
    public Guid? ActiveTenantId { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset LastSeenAt { get; }

    public DateTimeOffset IdleExpiresAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    /// <summary>The SHA-256 hex of the current antiforgery value; never the value itself.</summary>
    public string AntiforgeryTokenHash { get; }

    /// <summary>When the session was revoked, or <c>null</c> while it is live.</summary>
    public DateTimeOffset? RevokedAt { get; }

    /// <summary>True while the session authenticates its user: live, unexpired and idle-usable.</summary>
    public bool IsUsable(DateTimeOffset now) =>
        RevokedAt is null && now < IdleExpiresAt && now < ExpiresAt;
}

/// <summary>
/// One pending OIDC authorization: the state and nonce the callback must present back
/// (CLOUD-TENANT-BOUNDARY-001).
/// </summary>
/// <remarks>
/// Only hashes are stored, so a database disclosure hands out nothing a caller could complete a
/// login with. A request is single-use and short-lived: consuming it stamps it, and a replayed
/// state is refused rather than exchanged twice.
/// </remarks>
public sealed class HostedLoginRequest
{
    public HostedLoginRequest(
        Guid id,
        string stateHash,
        string nonceHash,
        string? codeVerifier,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        DateTimeOffset? consumedAt = null)
    {
        Id = id;
        StateHash = stateHash;
        NonceHash = nonceHash;
        CodeVerifier = codeVerifier;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        ConsumedAt = consumedAt;
    }

    public Guid Id { get; }

    /// <summary>The SHA-256 hex of the state the authorization URL carried.</summary>
    public string StateHash { get; }

    /// <summary>The SHA-256 hex of the nonce the ID token must carry back.</summary>
    public string NonceHash { get; }

    /// <summary>The PKCE verifier for the code exchange, or <c>null</c> when PKCE is not used.</summary>
    public string? CodeVerifier { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    /// <summary>When the request was consumed, or <c>null</c> while it is pending.</summary>
    public DateTimeOffset? ConsumedAt { get; }

    /// <summary>True while the request may still be completed: pending and unexpired.</summary>
    public bool IsPending(DateTimeOffset now) => ConsumedAt is null && now < ExpiresAt;
}

/// <summary>
/// The explicit record that a Community volume was acknowledged before Hosted tenants act on
/// this installation (CLOUD-TENANT-BOUNDARY-002 boundary).
/// </summary>
/// <remarks>
/// Opening a Community database in Hosted mode never reassigns its rows: the Community scope
/// keeps belonging to nobody a tenant can address. Acting under a tenant on an installation
/// that already holds Community domain rows additionally requires this row, written by an
/// explicit import action, so the operator's acknowledgment is stored rather than assumed. The
/// Community rows themselves are never moved by the import.
/// </remarks>
public sealed class HostedImport
{
    public HostedImport(
        Guid id,
        Guid installationId,
        Guid tenantId,
        Guid userId,
        DateTimeOffset createdAt)
    {
        Id = id;
        InstallationId = installationId;
        TenantId = tenantId;
        UserId = userId;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public Guid InstallationId { get; }

    public Guid TenantId { get; }

    public Guid UserId { get; }

    public DateTimeOffset CreatedAt { get; }
}
