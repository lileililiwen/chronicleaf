using System.Globalization;
using System.Text.Json.Serialization;
using Chronicleaf.Api.Access;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Teams;

namespace Chronicleaf.Api.Teams;

/// <summary>
/// The private Hosted team invitation and project-role routes
/// (TEAM-PROJECT-PERMISSIONS-001/002).
/// </summary>
/// <remarks>
/// Every route here is private, and that is the access package's default rather than something
/// these handlers re-check: a matched <c>/api</c> endpoint without the public marker has already
/// been given an authenticated session, the trusted origin and the antiforgery value. In
/// Community mode the service refuses every call exactly like the tenancy boundary, so these
/// routes change no Community behavior; in Hosted mode the scope and the actor always come
/// from the validated session, never from a request body.
/// <para>
/// Membership administration - inviting, revoking invitations, listing members, changing roles
/// and removing members - is a tenant-owner act on the invitation's or member's own tenant. A
/// project grant names the project and the user, and the service resolves the tenant from the
/// stored project row: a foreign or invented project is 404 before any grant is read. Every
/// role change takes its revision from <c>If-Match</c>, and a grant that is already held is a
/// no-op that writes nothing.
/// </para>
/// </remarks>
public static class TeamsEndpoints
{
    public static IEndpointRouteBuilder MapTeamsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/api/hosted/tenants/{tenantId:guid}/invitations", InviteAsync).RequireOwnerSession();
        endpoints.MapGet("/api/hosted/tenants/{tenantId:guid}/invitations", ListInvitationsAsync).RequireOwnerSession();
        endpoints.MapPost("/api/hosted/invitations/accept", AcceptAsync).RequireOwnerSession();
        endpoints.MapDelete(
            "/api/hosted/tenants/{tenantId:guid}/invitations/{invitationId:guid}",
            RevokeInvitationAsync).RequireOwnerSession();

        // The membership administration lives under team-members rather than members: the
        // tenant boundary already owns DELETE .../members/{userId} for revocation, and two
        // packages answering one path would make every removal ambiguous. Listing, role
        // changes and guarded removals are the team package's; the raw revocation stays the
        // dependency's.
        endpoints.MapGet("/api/hosted/tenants/{tenantId:guid}/team-members", ListMembersAsync).RequireOwnerSession();
        endpoints.MapPatch("/api/hosted/tenants/{tenantId:guid}/team-members/{userId:guid}", UpdateMemberAsync).RequireOwnerSession();
        endpoints.MapDelete("/api/hosted/tenants/{tenantId:guid}/team-members/{userId:guid}", RemoveMemberAsync).RequireOwnerSession();

        endpoints.MapPost("/api/projects/{projectId:guid}/members", GrantProjectPermissionAsync).RequireOwnerSession();
        endpoints.MapDelete(
            "/api/projects/{projectId:guid}/members/{userId:guid}/{permission}",
            RevokeProjectPermissionAsync).RequireOwnerSession();
        endpoints.MapGet("/api/projects/{projectId:guid}/team-access", GetProjectAccessAsync).RequireOwnerSession();
        endpoints.MapPost("/api/projects/{projectId:guid}/team-check", CheckProjectAccessAsync).RequireOwnerSession();

        return endpoints;
    }

    /// <summary>
    /// Invites one person into a tenant the caller owns. The issuance response carries the
    /// bearer token exactly once, for the owner to copy manually: no email is ever sent.
    /// </summary>
    private static async Task<IResult> InviteAsync(
        Guid tenantId,
        HttpContext context,
        InviteRequest? request,
        ITeamPermissionService teams,
        CancellationToken cancellationToken)
    {
        var created = await teams.InviteAsync(
            AccessEndpoints.OwnerSession(context),
            tenantId,
            request?.Email,
            request?.Role,
            cancellationToken).ConfigureAwait(false);

        return Results.Created(
            $"/api/hosted/tenants/{tenantId:D}/invitations/{created.Id}",
            new CreatedInvitationResponse(
                created.Id,
                created.Email,
                created.Role,
                created.ExpiresAt,
                created.Token));
    }

    /// <summary>Lists the pending invitations of a tenant the caller owns.</summary>
    private static async Task<IResult> ListInvitationsAsync(
        Guid tenantId,
        HttpContext context,
        ITeamPermissionService teams,
        CancellationToken cancellationToken)
    {
        var invitations = await teams.ListInvitationsAsync(
            AccessEndpoints.OwnerSession(context),
            tenantId,
            cancellationToken).ConfigureAwait(false);

        return Results.Json(new InvitationListResponse(
            invitations.Select(invitation => new InvitationResponse(
                invitation.Id,
                invitation.Email,
                invitation.Role,
                invitation.ExpiresAt)).ToArray()));
    }

    /// <summary>
    /// Accepts a live invitation: the verified caller joins the invitation's tenant once, and
    /// the session selects that tenant.
    /// </summary>
    private static async Task<IResult> AcceptAsync(
        HttpContext context,
        AcceptInvitationRequest? request,
        ITeamPermissionService teams,
        CancellationToken cancellationToken)
    {
        var accepted = await teams.AcceptAsync(
            AccessEndpoints.OwnerSession(context),
            request?.Token,
            cancellationToken).ConfigureAwait(false);

        return Results.Json(new AcceptedInvitationResponse(accepted.TenantId, accepted.Role));
    }

    /// <summary>Revokes a pending invitation in a tenant the caller owns. The row is kept.</summary>
    private static async Task<IResult> RevokeInvitationAsync(
        Guid tenantId,
        Guid invitationId,
        HttpContext context,
        ITeamPermissionService teams,
        CancellationToken cancellationToken)
    {
        await teams.RevokeInvitationAsync(
            AccessEndpoints.OwnerSession(context),
            tenantId,
            invitationId,
            cancellationToken).ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <summary>Lists the live memberships of a tenant the caller owns, oldest first.</summary>
    private static async Task<IResult> ListMembersAsync(
        Guid tenantId,
        HttpContext context,
        ITeamPermissionService teams,
        CancellationToken cancellationToken)
    {
        var members = await teams.ListMembersAsync(
            AccessEndpoints.OwnerSession(context),
            tenantId,
            cancellationToken).ConfigureAwait(false);

        return Results.Json(new TeamMemberListResponse(
            members.Select(member => new TeamMemberResponse(
                member.UserId,
                member.Role,
                member.Revision,
                member.CreatedAt)).ToArray()));
    }

    /// <summary>
    /// Changes one member's tenant role at the revision <c>If-Match</c> names. Demoting the
    /// last live owner is 409.
    /// </summary>
    private static async Task<IResult> UpdateMemberAsync(
        Guid tenantId,
        Guid userId,
        HttpContext context,
        UpdateMemberRequest? request,
        ITeamPermissionService teams,
        CancellationToken cancellationToken)
    {
        var member = await teams.UpdateMemberAsync(
            AccessEndpoints.OwnerSession(context),
            tenantId,
            userId,
            request?.Role,
            RequiredRevision(context, "member"),
            cancellationToken).ConfigureAwait(false);

        PublishRevision(context, member.Revision);

        return Results.Json(new TeamMemberResponse(
            member.UserId,
            member.Role,
            member.Revision,
            member.CreatedAt));
    }

    /// <summary>
    /// Revokes one member's tenant membership and the sessions that acted under it. An owner
    /// cannot revoke themselves, and removing the last live owner is 409.
    /// </summary>
    private static async Task<IResult> RemoveMemberAsync(
        Guid tenantId,
        Guid userId,
        HttpContext context,
        ITeamPermissionService teams,
        CancellationToken cancellationToken)
    {
        await teams.RemoveMemberAsync(
            AccessEndpoints.OwnerSession(context),
            tenantId,
            userId,
            cancellationToken).ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <summary>
    /// Grants one project permission to a live member of the project's tenant. Only a tenant
    /// owner may grant; a grant that is already held is a no-op that writes nothing.
    /// </summary>
    private static async Task<IResult> GrantProjectPermissionAsync(
        Guid projectId,
        HttpContext context,
        GrantProjectRequest? request,
        ITeamPermissionService teams,
        CancellationToken cancellationToken)
    {
        if (request?.UserId is not { } rawUserId
            || !Guid.TryParse(rawUserId, out var userId)
            || userId == Guid.Empty)
        {
            throw new ValidationFailedException("userId", "A member id is required.");
        }

        var grant = await teams.GrantProjectPermissionAsync(
            AccessEndpoints.OwnerSession(context),
            projectId,
            userId,
            request?.Permission,
            cancellationToken).ConfigureAwait(false);

        return Results.Created(
            $"/api/projects/{projectId:D}/members/{grant.UserId}/{grant.Permission}",
            new ProjectGrantResponse(
                grant.ProjectId,
                grant.UserId,
                grant.Permission,
                grant.Revision));
    }

    /// <summary>Revokes one project permission grant. Revoking a grant that is not held is a no-op.</summary>
    private static async Task<IResult> RevokeProjectPermissionAsync(
        Guid projectId,
        Guid userId,
        string permission,
        HttpContext context,
        ITeamPermissionService teams,
        CancellationToken cancellationToken)
    {
        await teams.RevokeProjectPermissionAsync(
            AccessEndpoints.OwnerSession(context),
            projectId,
            userId,
            permission,
            cancellationToken).ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <summary>
    /// Reads the caller's effective project permissions, evaluated in the service against the
    /// stored membership and grants - never from a request claim.
    /// </summary>
    private static async Task<IResult> GetProjectAccessAsync(
        Guid projectId,
        HttpContext context,
        ITeamPermissionService teams,
        CancellationToken cancellationToken)
    {
        var access = await teams.GetProjectAccessAsync(
            AccessEndpoints.OwnerSession(context),
            projectId,
            cancellationToken).ConfigureAwait(false);

        return Results.Json(new ProjectAccessResponse(
            access.CanRead,
            access.CanEdit,
            access.CanApprove,
            access.CanPublish,
            access.Permissions.ToArray()));
    }

    /// <summary>
    /// Authorizes one project action at the service layer and answers the verdict: 200 carries
    /// the grant, 403 carries the denial, and a publisher naming an unapproved head carries
    /// the approval conflict.
    /// </summary>
    private static async Task<IResult> CheckProjectAccessAsync(
        Guid projectId,
        HttpContext context,
        CheckProjectAccessRequest? request,
        ITeamPermissionService teams,
        CancellationToken cancellationToken)
    {
        await teams.RequireProjectPermissionAsync(
            AccessEndpoints.OwnerSession(context),
            projectId,
            request?.Action ?? string.Empty,
            request?.Approved ?? false,
            cancellationToken).ConfigureAwait(false);

        return Results.Json(new CheckProjectAccessResponse(true));
    }

    /// <summary>
    /// Reads the revision a membership change states. A change without one is 428, never an
    /// overwrite of whatever is stored now.
    /// </summary>
    private static long RequiredRevision(HttpContext context, string resource)
    {
        ArgumentNullException.ThrowIfNull(context);

        var presented = context.Request.Headers.IfMatch.ToString().Trim();

        if (presented.Length == 0)
        {
            throw new IfMatchRequiredException(resource);
        }

        if (presented.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            presented = presented[2..];
        }

        presented = presented.Trim().Trim('"');

        if (!long.TryParse(presented, NumberStyles.None, CultureInfo.InvariantCulture, out var revision)
            || revision <= 0)
        {
            throw new ValidationFailedException(
                "If-Match",
                "An If-Match revision greater than zero is required.");
        }

        return revision;
    }

    /// <summary>Publishes a membership revision as the response entity-tag, like every record.</summary>
    private static void PublishRevision(HttpContext context, long revision) =>
        context.Response.Headers.ETag = $"\"{revision.ToString(CultureInfo.InvariantCulture)}\"";
}

/// <summary>The <c>POST .../invitations</c> body: who is invited, and with which tenant role.</summary>
public sealed record InviteRequest(
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("role")] string? Role);

/// <summary>One invitation as served: ids, labels and expiry, never the token.</summary>
public sealed record InvitationResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("expiresAt")] string ExpiresAt);

/// <summary>A newly issued invitation. The token is disclosed exactly once, in this response.</summary>
public sealed record CreatedInvitationResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("expiresAt")] string ExpiresAt,
    [property: JsonPropertyName("token")] string Token);

/// <summary>The pending invitations of one tenant.</summary>
public sealed record InvitationListResponse(
    [property: JsonPropertyName("invitations")] InvitationResponse[] Invitations);

/// <summary>The <c>POST /api/hosted/invitations/accept</c> body: the bearer token.</summary>
public sealed record AcceptInvitationRequest(
    [property: JsonPropertyName("token")] string? Token);

/// <summary>One accepted invitation: the tenant joined and the role granted.</summary>
public sealed record AcceptedInvitationResponse(
    [property: JsonPropertyName("tenantId")] string TenantId,
    [property: JsonPropertyName("role")] string Role);

/// <summary>One live membership as served: who, which role, and at which revision.</summary>
public sealed record TeamMemberResponse(
    [property: JsonPropertyName("userId")] string UserId,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("createdAt")] string CreatedAt);

/// <summary>The live memberships of one tenant.</summary>
public sealed record TeamMemberListResponse(
    [property: JsonPropertyName("members")] TeamMemberResponse[] Members);

/// <summary>The <c>PATCH .../members/{userId}</c> body: the role the member moves to.</summary>
public sealed record UpdateMemberRequest(
    [property: JsonPropertyName("role")] string? Role);

/// <summary>The <c>POST /api/projects/{id}/members</c> body: who gains which permission.</summary>
public sealed record GrantProjectRequest(
    [property: JsonPropertyName("userId")] string? UserId,
    [property: JsonPropertyName("permission")] string? Permission);

/// <summary>One stored project grant as served: who may do what on which project.</summary>
public sealed record ProjectGrantResponse(
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("userId")] string UserId,
    [property: JsonPropertyName("permission")] string Permission,
    [property: JsonPropertyName("revision")] long Revision);

/// <summary>The caller's effective project permissions, evaluated server-side.</summary>
public sealed record ProjectAccessResponse(
    [property: JsonPropertyName("canRead")] bool CanRead,
    [property: JsonPropertyName("canEdit")] bool CanEdit,
    [property: JsonPropertyName("canApprove")] bool CanApprove,
    [property: JsonPropertyName("canPublish")] bool CanPublish,
    [property: JsonPropertyName("permissions")] string[] Permissions);

/// <summary>The <c>POST .../team-check</c> body: which action, and whether the head is approved.</summary>
public sealed record CheckProjectAccessRequest(
    [property: JsonPropertyName("action")] string? Action,
    [property: JsonPropertyName("approved")] bool Approved = false);

/// <summary>One authorized project action.</summary>
public sealed record CheckProjectAccessResponse(
    [property: JsonPropertyName("allowed")] bool Allowed);
