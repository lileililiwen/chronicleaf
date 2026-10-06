namespace Chronicleaf.Core;

/// <summary>
/// The project permissions one user may hold on one project
/// (TEAM-PROJECT-PERMISSIONS-001).
/// </summary>
/// <remarks>
/// Stored as text on <c>project_memberships</c>, one row per granted permission: a user may hold
/// several permissions on the same project, and each grant is explicit. The vocabulary is closed
/// at the database by a check constraint, so no writer can invent a permission the policy never
/// defined. A tenant owner holds every permission on every project of the tenant implicitly and
/// needs no row; every other authenticated user acts only through the rows that name them.
/// </remarks>
public static class ProjectPermissions
{
    /// <summary>May read the assigned project and its readable history.</summary>
    public const string Reader = "reader";

    /// <summary>May create and edit evidence and drafts on the assigned project.</summary>
    public const string Editor = "editor";

    /// <summary>May approve and export on the assigned project.</summary>
    public const string Approver = "approver";

    /// <summary>May publish an approved asset on the assigned project, never an unapproved one.</summary>
    public const string Publisher = "publisher";

    /// <summary>Every permission this package stores.</summary>
    public static readonly IReadOnlyList<string> All = [Reader, Editor, Approver, Publisher];

    /// <summary>True for exactly the stored permission vocabulary.</summary>
    public static bool IsKnown(string? permission) =>
        string.Equals(permission, Reader, StringComparison.Ordinal)
        || string.Equals(permission, Editor, StringComparison.Ordinal)
        || string.Equals(permission, Approver, StringComparison.Ordinal)
        || string.Equals(permission, Publisher, StringComparison.Ordinal);
}

/// <summary>
/// One explicit grant of a project permission to a Hosted user
/// (TEAM-PROJECT-PERMISSIONS-001).
/// </summary>
/// <remarks>
/// The grant authorizes nothing on its own: it takes effect only while the named user holds a
/// live membership in the tenant the project belongs to, which the service checks in the same
/// statement that reads the grant. Revoking the tenant membership therefore denies every project
/// action without touching this row, so the history of who could do what is preserved.
/// <see cref="Revision"/> is the optimistic concurrency token for future grant edits; the first
/// committed version is 1.
/// </remarks>
public sealed class ProjectMembership
{
    public ProjectMembership(
        Guid projectId,
        Guid userId,
        string permission,
        long revision,
        DateTimeOffset createdAt)
    {
        ProjectId = projectId;
        UserId = userId;
        Permission = permission;
        Revision = revision;
        CreatedAt = createdAt;
    }

    public Guid ProjectId { get; }

    public Guid UserId { get; }

    /// <summary>One of <see cref="ProjectPermissions.All"/>.</summary>
    public string Permission { get; }

    /// <summary>The optimistic revision; the first committed version is 1.</summary>
    public long Revision { get; }

    public DateTimeOffset CreatedAt { get; }
}

/// <summary>
/// One pending invitation of a person into a Hosted tenant
/// (TEAM-PROJECT-PERMISSIONS-002).
/// </summary>
/// <remarks>
/// The invitation is a bearer grant: whoever presents the 32-byte token within its 7-day life
/// joins the tenant with the invited role, exactly once. Only the SHA-256 hash of the token is
/// stored, so a database disclosure hands out nothing an acceptor could present; the plaintext
/// exists only in the issuance response the owner copies manually, because delivery is an
/// operator-configured transport that is never exercised here. The email is the owner's label
/// for who the invitation is for - Hosted identity is an issuer/subject pair with no email
/// claim, so the email never authenticates. Accepting stamps the row, revoking stamps it
/// without deleting it, and an expired or already-used token creates no membership.
/// </remarks>
public sealed class TenantInvitation
{
    /// <summary>How long an invitation stays acceptable after issuance.</summary>
    public static readonly TimeSpan TimeToLive = TimeSpan.FromDays(7);

    /// <summary>At most 320 characters; see the <c>tenant_invitations.email</c> column.</summary>
    public const int EmailMaxLength = 320;

    public TenantInvitation(
        Guid id,
        Guid tenantId,
        string email,
        string role,
        string tokenHash,
        Guid createdBy,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt,
        DateTimeOffset? acceptedAt = null,
        DateTimeOffset? revokedAt = null)
    {
        Id = id;
        TenantId = tenantId;
        Email = email;
        Role = role;
        TokenHash = tokenHash;
        CreatedBy = createdBy;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
        AcceptedAt = acceptedAt;
        RevokedAt = revokedAt;
    }

    public Guid Id { get; }

    public Guid TenantId { get; }

    /// <summary>The owner's label for who the invitation is for; never an authenticator.</summary>
    public string Email { get; }

    /// <summary>One of <see cref="TenantRoles.All"/>; the role the acceptor joins with.</summary>
    public string Role { get; }

    /// <summary>The SHA-256 hex of the bearer token; the token itself is never stored.</summary>
    public string TokenHash { get; }

    public Guid CreatedBy { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset ExpiresAt { get; }

    /// <summary>When the invitation was accepted, or <c>null</c> while it is live.</summary>
    public DateTimeOffset? AcceptedAt { get; }

    /// <summary>When the invitation was revoked, or <c>null</c> while it is live.</summary>
    public DateTimeOffset? RevokedAt { get; }

    /// <summary>True while the invitation may still be accepted at <paramref name="now"/>.</summary>
    public bool IsLive(DateTimeOffset now) =>
        AcceptedAt is null && RevokedAt is null && now < ExpiresAt;
}
