using Chronicleaf.Application.Common;

namespace Chronicleaf.Application.Teams;

/// <summary>
/// The team invitation and project-role envelope codes. They extend the shared envelope rather
/// than replacing it: the body shape stays <c>{code,message,traceId,fieldErrors?}</c> and the
/// messages are authored here, never taken from a token, an email or a database message.
/// </summary>
public static class TeamPermissionError
{
    /// <summary>The caller holds no grant for the project action it attempted (403).</summary>
    public const string AccessDenied = "team_access_denied";

    /// <summary>The invitation expired before it was accepted (410).</summary>
    public const string InvitationExpired = "invitation_expired";

    /// <summary>The invitation was already used or revoked (409).</summary>
    public const string InvitationReused = "invitation_reused";

    /// <summary>A live invitation already names this email in this tenant (409).</summary>
    public const string InvitationExists = "invitation_exists";

    /// <summary>Removing or demoting this member would leave no live owner (409).</summary>
    public const string LastOwner = "team_last_owner";
}

/// <summary>
/// The caller holds no live grant for the project action it attempted. A missing grant and a
/// revoked membership are the same refusal, and a foreign project is never named: that path
/// answers the delivered 404 before this refusal is reachable.
/// </summary>
public sealed class TeamAccessDeniedException()
    : ApiException(TeamPermissionError.AccessDenied, "This project action is not available to the current user.");

/// <summary>
/// The invitation expired before it was accepted. The remedy is a fresh invitation, never a
/// retry of the same token.
/// </summary>
public sealed class InvitationExpiredException()
    : ApiException(TeamPermissionError.InvitationExpired, "This invitation has expired.");

/// <summary>
/// The invitation was already accepted or revoked. A consumed token is single-use, so no new
/// membership is created.
/// </summary>
public sealed class InvitationReusedException()
    : ApiException(TeamPermissionError.InvitationReused, "This invitation has already been used.");

/// <summary>
/// A live invitation already names this email in this tenant. The remedy is to revoke the
/// pending one first, rather than to hold two live tokens for the same person.
/// </summary>
public sealed class InvitationExistsException()
    : ApiException(TeamPermissionError.InvitationExists, "A live invitation already names this email.");

// TEAM-PROJECT-PERMISSIONS-002: the last live owner of a tenant cannot be removed or demoted.
// The remedy is to promote another member first, never an ownerless tenant.
public sealed class LastOwnerConflictException()
    : ApiException(TeamPermissionError.LastOwner, "The last owner of this tenant cannot be removed or demoted.");
