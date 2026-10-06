using Chronicleaf.Application.Access;

namespace Chronicleaf.Application.Teams;

/// <summary>
/// The Hosted team invitation and project-role contract
/// (TEAM-PROJECT-PERMISSIONS-001/002).
/// </summary>
/// <remarks>
/// Everything a caller supplies is an id, an email label, a role name or a bearer token; tenant
/// scope is always derived server-side from the validated session's active tenant, never from a
/// request body. The Community owner path never reaches this service: with no Hosted
/// configuration every method refuses exactly like the tenancy boundary, so Community behavior
/// is unchanged.
/// </remarks>
public interface ITeamPermissionService
{
    /// <summary>
    /// Invites one person into a tenant the caller owns. Only a tenant owner may invite, and the
    /// plaintext token exists only in this result: it is stored as a hash and never returned
    /// again. Issuance always reports pending delivery: no email is ever sent by this package.
    /// </summary>
    Task<CreatedInvitationView> InviteAsync(
        OwnerSessionContext session,
        Guid tenantId,
        string? email,
        string? role,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the live (pending) invitations of a tenant the caller owns.</summary>
    Task<IReadOnlyList<InvitationView>> ListInvitationsAsync(
        OwnerSessionContext session,
        Guid tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Accepts a live invitation: the verified caller joins the invitation's tenant with the
    /// invited role, exactly once per token, and the session selects that tenant. An expired
    /// token is 410, a used or revoked one is 409.
    /// </summary>
    Task<AcceptedInvitationView> AcceptAsync(
        OwnerSessionContext session,
        string? token,
        CancellationToken cancellationToken = default);

    /// <summary>Revokes a pending invitation in a tenant the caller owns. The row is kept.</summary>
    Task RevokeInvitationAsync(
        OwnerSessionContext session,
        Guid tenantId,
        Guid invitationId,
        CancellationToken cancellationToken = default);

    /// <summary>Lists the live memberships of a tenant the caller owns, oldest first.</summary>
    Task<IReadOnlyList<TeamMemberView>> ListMembersAsync(
        OwnerSessionContext session,
        Guid tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes one member's tenant role at exactly <paramref name="expectedRevision"/>. Only a
    /// tenant owner may change roles, and demoting the last live owner is 409.
    /// </summary>
    Task<TeamMemberView> UpdateMemberAsync(
        OwnerSessionContext session,
        Guid tenantId,
        Guid userId,
        string? role,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes one member's tenant membership and the sessions that acted under it, and leaves
    /// their project grants inert. Only a tenant owner may revoke, an owner cannot revoke
    /// themselves, and removing the last live owner is 409.
    /// </summary>
    Task RemoveMemberAsync(
        OwnerSessionContext session,
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Grants one project permission to a live member of the project's tenant. Only a tenant
    /// owner may grant; granting a permission the row already holds is a no-op that writes
    /// nothing. The target must hold a live tenant membership.
    /// </summary>
    Task<ProjectGrantView> GrantProjectPermissionAsync(
        OwnerSessionContext session,
        Guid projectId,
        Guid userId,
        string? permission,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes one project permission grant. Only a tenant owner may revoke; revoking a grant
    /// that is not held is a no-op that writes nothing.
    /// </summary>
    Task RevokeProjectPermissionAsync(
        OwnerSessionContext session,
        Guid projectId,
        Guid userId,
        string? permission,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the caller's effective project permissions, evaluated in the service against the
    /// stored tenant membership and project grants - never from a request claim. A foreign or
    /// invented project is 404.
    /// </summary>
    Task<ProjectAccessView> GetProjectAccessAsync(
        OwnerSessionContext session,
        Guid projectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Authorizes one project action at the service layer: <c>read</c>, <c>edit</c>,
    /// <c>approve</c> or <c>publish</c>. A reader reads; an editor edits; an approver approves
    /// and exports; a publisher publishes only while <paramref name="approved"/> describes an
    /// approved head, otherwise 409; a tenant owner does all. This is the check domain paths
    /// call rather than re-implementing, so a route cannot authorize what the service refuses.
    /// </summary>
    Task RequireProjectPermissionAsync(
        OwnerSessionContext session,
        Guid projectId,
        string action,
        bool approved,
        CancellationToken cancellationToken = default);
}

/// <summary>One invitation as served to the tenant owner: ids, labels and expiry, never a secret.</summary>
public sealed record InvitationView(
    string Id,
    string Email,
    string Role,
    string ExpiresAt);

/// <summary>
/// A newly issued invitation. The token exists only here, like every secret: the owner copies
/// it manually, and no later read returns it.
/// </summary>
public sealed record CreatedInvitationView(
    string Id,
    string Email,
    string Role,
    string ExpiresAt,
    string Token);

/// <summary>One accepted invitation: the tenant joined and the role granted.</summary>
public sealed record AcceptedInvitationView(
    string TenantId,
    string Role);

/// <summary>One live membership as served: who, which role, and at which revision.</summary>
public sealed record TeamMemberView(
    string UserId,
    string Role,
    long Revision,
    string CreatedAt);

/// <summary>One stored project grant as served: who may do what on which project.</summary>
public sealed record ProjectGrantView(
    string ProjectId,
    string UserId,
    string Permission,
    long Revision);

/// <summary>The caller's effective project permissions, evaluated server-side.</summary>
public sealed record ProjectAccessView(
    bool CanRead,
    bool CanEdit,
    bool CanApprove,
    bool CanPublish,
    IReadOnlyList<string> Permissions);
