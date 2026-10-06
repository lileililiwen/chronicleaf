using System.Security.Cryptography;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Content;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Teams;
using Chronicleaf.Application.Tenancy;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Chronicleaf.Infrastructure.Teams;

/// <summary>
/// The Hosted team invitation and project-role use cases over real PostgreSQL
/// (TEAM-PROJECT-PERMISSIONS-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Reads name the tenant explicitly in every predicate: a lookup by id can only ever return a
/// row inside the caller's own active tenant, and a foreign or invented id is the same 404.
/// Writes that touch the tenant tables run under <c>SET LOCAL app.tenant_id</c> like the
/// tenancy boundary's own, so the row-level policies see the same tenant the predicates name.
/// </para>
/// <para>
/// A tenant owner holds every project permission implicitly and needs no grant row; every other
/// user acts only through the <c>project_memberships</c> rows that name them, and only while
/// their tenant membership is live. Revoking the tenant membership therefore denies every
/// project action without touching the grant rows, so the history of who could do what stays
/// readable while authorizing nothing.
/// </para>
/// <para>
/// In Community mode every method refuses exactly like the tenancy boundary, so the
/// single-user owner path is untouched: no Community endpoint consults this service.
/// </para>
/// </remarks>
public sealed class TeamPermissionService(
    ChronicleafDbContext dbContext,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    TenancySettings settings) : ITeamPermissionService
{
    public async Task<CreatedInvitationView> InviteAsync(
        OwnerSessionContext session,
        Guid tenantId,
        string? email,
        string? role,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        RequireHosted();

        var caller = await RequireOwnerAsync(session, tenantId, cancellationToken).ConfigureAwait(false);
        var normalizedEmail = NormalizeEmail(email);
        var normalizedRole = NormalizeRole(role);
        var now = clock.GetUtcNow();

        var created = await unitOfWork.ExecuteAsync(
            async token =>
            {
                await SetTenantContextAsync(tenantId, token).ConfigureAwait(false);

                var duplicate = await dbContext.TenantInvitations.AsNoTracking()
                    .Where(invitation =>
                        invitation.TenantId == tenantId
                        && invitation.Email == normalizedEmail
                        && invitation.AcceptedAt == null
                        && invitation.RevokedAt == null)
                    .AnyAsync(token)
                    .ConfigureAwait(false);
                if (duplicate)
                {
                    throw new InvitationExistsException();
                }

                var plaintext = OpaqueToken();
                var invitation = new TenantInvitation(
                    Guid.NewGuid(),
                    tenantId,
                    normalizedEmail,
                    normalizedRole,
                    Sha256.Sha256Hex(plaintext),
                    caller.UserId,
                    now,
                    now.Add(TenantInvitation.TimeToLive));

                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO tenant_invitations
                         (id, tenant_id, email, role, token_hash, created_by, created_at,
                          expires_at, accepted_at, revoked_at)
                     VALUES
                         ({invitation.Id}, {invitation.TenantId}, {invitation.Email},
                          {invitation.Role}, {invitation.TokenHash}, {invitation.CreatedBy},
                          {invitation.CreatedAt}, {invitation.ExpiresAt}, NULL, NULL)
                     """,
                    token).ConfigureAwait(false);
                RecordAudit(InvitationCreatedAction, caller.UserId, invitation.Id, now);

                return (invitation, plaintext);
            },
            cancellationToken).ConfigureAwait(false);

        // The plaintext token is disclosed exactly once, in the issuance response the owner
        // copies manually: delivery is an operator-configured transport this package never
        // exercises, so issuance always reports pending delivery rather than a sent email.
        return new CreatedInvitationView(
            created.invitation.Id.ToString("D"),
            created.invitation.Email,
            created.invitation.Role,
            created.invitation.ExpiresAt.ToString("O"),
            created.plaintext);
    }

    public async Task<IReadOnlyList<InvitationView>> ListInvitationsAsync(
        OwnerSessionContext session,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        RequireHosted();

        _ = await RequireOwnerAsync(session, tenantId, cancellationToken).ConfigureAwait(false);

        var rows = await dbContext.TenantInvitations.AsNoTracking()
            .Where(invitation =>
                invitation.TenantId == tenantId
                && invitation.AcceptedAt == null
                && invitation.RevokedAt == null)
            .OrderBy(invitation => invitation.CreatedAt)
            .ThenBy(invitation => invitation.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Select(invitation => new InvitationView(
                invitation.Id.ToString("D"),
                invitation.Email,
                invitation.Role,
                invitation.ExpiresAt.ToString("O")))
            .ToList();
    }

    public async Task<AcceptedInvitationView> AcceptAsync(
        OwnerSessionContext session,
        string? token,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        RequireHosted();

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvitationReusedException();
        }

        var user = await RequireHostedUserAsync(session, cancellationToken).ConfigureAwait(false);
        var tokenHash = Sha256.Sha256Hex(token.Trim());
        var now = clock.GetUtcNow();

        return await unitOfWork.ExecuteAsync(
            async dbToken =>
            {
                var invitation = await dbContext.TenantInvitations.AsNoTracking()
                    .Where(row => row.TokenHash == tokenHash)
                    .SingleOrDefaultAsync(dbToken)
                    .ConfigureAwait(false);

                // An unknown token names no invitation at all: the same 404 a foreign id gets,
                // so a caller cannot probe for another tenant's invitations by comparing codes.
                if (invitation is null
                    || !Sha256.FixedTimeEqualsHex(invitation.TokenHash, tokenHash))
                {
                    throw new ResourceNotFoundException("invitation");
                }

                await SetTenantContextAsync(invitation.TenantId, dbToken).ConfigureAwait(false);

                if (invitation.AcceptedAt is not null || invitation.RevokedAt is not null)
                {
                    throw new InvitationReusedException();
                }

                if (now >= invitation.ExpiresAt)
                {
                    throw new InvitationExpiredException();
                }

                var existing = await dbContext.TenantMemberships.AsNoTracking()
                    .Where(membership =>
                        membership.TenantId == invitation.TenantId
                        && membership.UserId == user.Id)
                    .SingleOrDefaultAsync(dbToken)
                    .ConfigureAwait(false);

                if (existing is { RevokedAt: null })
                {
                    // Already a live member: no second membership is created, and the consumed
                    // token stays single-use for everyone else.
                    throw new InvitationReusedException();
                }

                if (existing is not null)
                {
                    // A revoked member rejoins with the invited role: the row is reactivated
                    // rather than duplicated, so the history of who belonged where stays on it.
                    var reactivated = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                        $"""
                         UPDATE tenant_memberships
                         SET revoked_at = NULL, role = {invitation.Role}, revision = {existing.Revision + 1}
                         WHERE tenant_id = {invitation.TenantId} AND user_id = {user.Id}
                           AND revoked_at IS NOT NULL
                         """,
                        dbToken).ConfigureAwait(false);
                    if (reactivated != 1)
                    {
                        throw new InvitationReusedException();
                    }
                }
                else
                {
                    await dbContext.Database.ExecuteSqlInterpolatedAsync(
                        $"""
                         INSERT INTO tenant_memberships (tenant_id, user_id, role, created_at, revoked_at, revision)
                         VALUES ({invitation.TenantId}, {user.Id}, {invitation.Role}, {now}, NULL, {1L})
                         """,
                        dbToken).ConfigureAwait(false);
                }

                var consumed = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     UPDATE tenant_invitations SET accepted_at = {now}
                     WHERE id = {invitation.Id} AND accepted_at IS NULL AND revoked_at IS NULL
                     """,
                    dbToken).ConfigureAwait(false);
                if (consumed != 1)
                {
                    throw new InvitationReusedException();
                }

                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE hosted_sessions SET active_tenant_id = {invitation.TenantId} WHERE id = {session.SessionId} AND revoked_at IS NULL",
                    dbToken).ConfigureAwait(false);
                RecordAudit(InvitationAcceptedAction, user.Id, invitation.TenantId, now);

                return new AcceptedInvitationView(
                    invitation.TenantId.ToString("D"),
                    invitation.Role);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RevokeInvitationAsync(
        OwnerSessionContext session,
        Guid tenantId,
        Guid invitationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        RequireHosted();

        var caller = await RequireOwnerAsync(session, tenantId, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();

        await unitOfWork.ExecuteAsync<object?>(
            async token =>
            {
                await SetTenantContextAsync(tenantId, token).ConfigureAwait(false);

                var invitation = await dbContext.TenantInvitations.AsNoTracking()
                    .Where(row => row.Id == invitationId && row.TenantId == tenantId)
                    .SingleOrDefaultAsync(token)
                    .ConfigureAwait(false);
                if (invitation is null)
                {
                    throw new ResourceNotFoundException("invitation");
                }

                if (invitation.AcceptedAt is not null)
                {
                    throw new InvitationReusedException();
                }

                if (invitation.RevokedAt is null)
                {
                    await dbContext.Database.ExecuteSqlInterpolatedAsync(
                        $"""
                         UPDATE tenant_invitations SET revoked_at = {now}
                         WHERE id = {invitationId} AND revoked_at IS NULL
                         """,
                        token).ConfigureAwait(false);
                    RecordAudit(InvitationRevokedAction, caller.UserId, invitationId, now);
                }

                return null;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TeamMemberView>> ListMembersAsync(
        OwnerSessionContext session,
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        RequireHosted();

        _ = await RequireOwnerAsync(session, tenantId, cancellationToken).ConfigureAwait(false);

        var rows = await dbContext.TenantMemberships.AsNoTracking()
            .Where(membership => membership.TenantId == tenantId && membership.RevokedAt == null)
            .OrderBy(membership => membership.CreatedAt)
            .ThenBy(membership => membership.UserId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows
            .Select(membership => new TeamMemberView(
                membership.UserId.ToString("D"),
                membership.Role,
                membership.Revision,
                membership.CreatedAt.ToString("O")))
            .ToList();
    }

    public async Task<TeamMemberView> UpdateMemberAsync(
        OwnerSessionContext session,
        Guid tenantId,
        Guid userId,
        string? role,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        RequireHosted();

        var caller = await RequireOwnerAsync(session, tenantId, cancellationToken).ConfigureAwait(false);
        var normalizedRole = NormalizeRole(role);
        RequireRevision(expectedRevision);
        var now = clock.GetUtcNow();

        return await unitOfWork.ExecuteAsync(
            async token =>
            {
                await SetTenantContextAsync(tenantId, token).ConfigureAwait(false);

                var target = await dbContext.TenantMemberships.AsNoTracking()
                    .Where(membership => membership.TenantId == tenantId && membership.UserId == userId)
                    .SingleOrDefaultAsync(token)
                    .ConfigureAwait(false);
                if (target is null || target.RevokedAt is not null)
                {
                    throw new ResourceNotFoundException("member");
                }

                if (!string.Equals(target.Role, normalizedRole, StringComparison.Ordinal)
                    && string.Equals(target.Role, TenantRoles.Owner, StringComparison.Ordinal)
                    && !string.Equals(normalizedRole, TenantRoles.Owner, StringComparison.Ordinal)
                    && await LiveOwnerCountAsync(tenantId, token).ConfigureAwait(false) <= 1)
                {
                    throw new LastOwnerConflictException();
                }

                var affected = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     UPDATE tenant_memberships SET role = {normalizedRole}, revision = {target.Revision + 1}
                     WHERE tenant_id = {tenantId} AND user_id = {userId}
                       AND revoked_at IS NULL AND revision = {expectedRevision}
                     """,
                    token).ConfigureAwait(false);
                if (affected != 1)
                {
                    var current = await dbContext.TenantMemberships.AsNoTracking()
                        .Where(membership => membership.TenantId == tenantId && membership.UserId == userId)
                        .SingleOrDefaultAsync(token)
                        .ConfigureAwait(false);
                    if (current is null || current.RevokedAt is not null)
                    {
                        throw new ResourceNotFoundException("member");
                    }

                    throw new RevisionConflictException(current.Revision);
                }

                RecordAudit(MemberRoleChangedAction, caller.UserId, userId, now);

                return new TeamMemberView(
                    userId.ToString("D"),
                    normalizedRole,
                    target.Revision + 1,
                    target.CreatedAt.ToString("O"));
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveMemberAsync(
        OwnerSessionContext session,
        Guid tenantId,
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        RequireHosted();

        var caller = await RequireOwnerAsync(session, tenantId, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();

        await unitOfWork.ExecuteAsync<object?>(
            async token =>
            {
                await SetTenantContextAsync(tenantId, token).ConfigureAwait(false);

                var target = await dbContext.TenantMemberships.AsNoTracking()
                    .Where(membership => membership.TenantId == tenantId && membership.UserId == userId)
                    .SingleOrDefaultAsync(token)
                    .ConfigureAwait(false);
                if (target is null || target.RevokedAt is not null)
                {
                    throw new ResourceNotFoundException("member");
                }

                // The continuity check runs before the self-removal refusal: removing the last
                // live owner is rejected even when the owner removes themselves, because an
                // ownerless tenant authorizes nobody to repair it.
                if (string.Equals(target.Role, TenantRoles.Owner, StringComparison.Ordinal)
                    && await LiveOwnerCountAsync(tenantId, token).ConfigureAwait(false) <= 1)
                {
                    throw new LastOwnerConflictException();
                }

                if (userId == caller.UserId)
                {
                    // An owner cannot revoke their own membership while another owner remains:
                    // the remedy is another owner removing them, never a self-removal that
                    // could strand the tenant's administration.
                    throw new ValidationFailedException("userId", "An owner cannot revoke their own membership.");
                }

                var affected = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     UPDATE tenant_memberships SET revoked_at = {now}, revision = {target.Revision + 1}
                     WHERE tenant_id = {tenantId} AND user_id = {userId} AND revoked_at IS NULL
                     """,
                    token).ConfigureAwait(false);
                if (affected != 1)
                {
                    throw new ResourceNotFoundException("member");
                }

                // Removing the membership revokes the sessions that acted under it: a revoked
                // member's cookie stops authenticating before any domain lookup runs. The
                // project grants stay on their rows as history, but authorize nothing without
                // a live tenant membership behind them.
                await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     UPDATE hosted_sessions SET revoked_at = {now}
                     WHERE user_id = {userId} AND active_tenant_id = {tenantId} AND revoked_at IS NULL
                     """,
                    token).ConfigureAwait(false);
                RecordAudit(MemberRemovedAction, caller.UserId, userId, now);

                return null;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectGrantView> GrantProjectPermissionAsync(
        OwnerSessionContext session,
        Guid projectId,
        Guid userId,
        string? permission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        RequireHosted();

        var normalized = NormalizePermission(permission);
        var project = await RequireTenantProjectAsync(session, projectId, cancellationToken).ConfigureAwait(false);
        var caller = await RequireOwnerAsync(session, project.TenantId, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();

        return await unitOfWork.ExecuteAsync(
            async token =>
            {
                await SetTenantContextAsync(project.TenantId, token).ConfigureAwait(false);

                var member = await dbContext.TenantMemberships.AsNoTracking()
                    .Where(membership =>
                        membership.TenantId == project.TenantId
                        && membership.UserId == userId)
                    .SingleOrDefaultAsync(token)
                    .ConfigureAwait(false);
                if (member is null || member.RevokedAt is not null)
                {
                    throw new ValidationFailedException(
                        "userId",
                        "A project permission needs a live tenant membership first.");
                }

                var inserted = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     INSERT INTO project_memberships (project_id, user_id, permission, revision, created_at)
                     VALUES ({projectId}, {userId}, {normalized}, {1L}, {now})
                     ON CONFLICT (project_id, user_id, permission) DO NOTHING
                     """,
                    token).ConfigureAwait(false);

                var stored = await dbContext.ProjectMemberships.AsNoTracking()
                    .Where(grant =>
                        grant.ProjectId == projectId
                        && grant.UserId == userId
                        && grant.Permission == normalized)
                    .SingleAsync(token)
                    .ConfigureAwait(false);

                // A duplicate grant at the stored row is a no-op with no write and no audit row:
                // repeating the same grant cannot mint history.
                if (inserted == 1)
                {
                    RecordAudit(ProjectGrantedAction, caller.UserId, projectId, now);
                }

                return new ProjectGrantView(
                    projectId.ToString("D"),
                    userId.ToString("D"),
                    normalized,
                    stored.Revision);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RevokeProjectPermissionAsync(
        OwnerSessionContext session,
        Guid projectId,
        Guid userId,
        string? permission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        RequireHosted();

        var normalized = NormalizePermission(permission);
        var project = await RequireTenantProjectAsync(session, projectId, cancellationToken).ConfigureAwait(false);
        var caller = await RequireOwnerAsync(session, project.TenantId, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();

        await unitOfWork.ExecuteAsync<object?>(
            async token =>
            {
                await SetTenantContextAsync(project.TenantId, token).ConfigureAwait(false);

                var deleted = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                     DELETE FROM project_memberships
                     WHERE project_id = {projectId} AND user_id = {userId} AND permission = {normalized}
                     """,
                    token).ConfigureAwait(false);

                if (deleted == 1)
                {
                    RecordAudit(ProjectGrantRevokedAction, caller.UserId, projectId, now);
                }

                return null;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<ProjectAccessView> GetProjectAccessAsync(
        OwnerSessionContext session,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        RequireHosted();

        var project = await RequireTenantProjectAsync(session, projectId, cancellationToken).ConfigureAwait(false);
        var grants = await ReadProjectGrantsAsync(project, session.ActorId, cancellationToken).ConfigureAwait(false);

        return AccessOf(grants.IsOwner, grants.Permissions);
    }

    public async Task RequireProjectPermissionAsync(
        OwnerSessionContext session,
        Guid projectId,
        string action,
        bool approved,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        RequireHosted();

        var normalized = NormalizeAction(action);
        var project = await RequireTenantProjectAsync(session, projectId, cancellationToken).ConfigureAwait(false);
        var grants = await ReadProjectGrantsAsync(project, session.ActorId, cancellationToken).ConfigureAwait(false);
        var access = AccessOf(grants.IsOwner, grants.Permissions);

        var allowed = normalized switch
        {
            "read" => access.CanRead,
            "edit" => access.CanEdit,
            "approve" => access.CanApprove,
            "publish" => access.CanPublish,
            _ => false,
        };

        if (!allowed)
        {
            throw new TeamAccessDeniedException();
        }

        // A publisher publishes only an approved head: the grant authorizes the act, and the
        // approval state authorizes the object. An unapproved head is a conflict with the
        // record's own state, not a body the caller can correct.
        if (string.Equals(normalized, "publish", StringComparison.Ordinal) && !approved)
        {
            throw new ApprovalRequiredException();
        }
    }

    // -------------------------------------------------------------------------------------------
    // Private helpers.
    // -------------------------------------------------------------------------------------------

    private void RequireHosted()
    {
        if (!settings.IsHosted)
        {
            // Team grants do not exist in Community: the same generic refusal as a bad login
            // exchange, so Community behavior is unchanged and nothing about the mode leaks.
            throw new OidcLoginFailedException();
        }
    }

    private static void RequireRevision(long expectedRevision)
    {
        if (expectedRevision <= 0)
        {
            throw new ValidationFailedException(
                "If-Match",
                "An If-Match revision greater than zero is required.");
        }
    }

    private static string NormalizeEmail(string? email)
    {
        var normalized = email?.Trim().ToLowerInvariant() ?? string.Empty;

        if (normalized.Length == 0
            || normalized.Length > TenantInvitation.EmailMaxLength
            || normalized.Contains(' ', StringComparison.Ordinal))
        {
            throw new ValidationFailedException("email", "An email address is required.");
        }

        var at = normalized.IndexOf('@');
        if (at <= 0 || at != normalized.LastIndexOf('@') || at == normalized.Length - 1)
        {
            throw new ValidationFailedException("email", "An email address is required.");
        }

        return normalized;
    }

    private static string NormalizeRole(string? role)
    {
        var normalized = role?.Trim().ToLowerInvariant() ?? string.Empty;

        if (!TenantRoles.IsKnown(normalized))
        {
            throw new ValidationFailedException(
                "role",
                $"A role of {string.Join(", ", TenantRoles.All)} is required.");
        }

        return normalized;
    }

    private static string NormalizePermission(string? permission)
    {
        var normalized = permission?.Trim().ToLowerInvariant() ?? string.Empty;

        if (!ProjectPermissions.IsKnown(normalized))
        {
            throw new ValidationFailedException(
                "permission",
                $"A permission of {string.Join(", ", ProjectPermissions.All)} is required.");
        }

        return normalized;
    }

    private static string NormalizeAction(string? action)
    {
        var normalized = action?.Trim().ToLowerInvariant() ?? string.Empty;

        if (!string.Equals(normalized, "read", StringComparison.Ordinal)
            && !string.Equals(normalized, "edit", StringComparison.Ordinal)
            && !string.Equals(normalized, "approve", StringComparison.Ordinal)
            && !string.Equals(normalized, "publish", StringComparison.Ordinal))
        {
            throw new ValidationFailedException("action", "An action of read, edit, approve or publish is required.");
        }

        return normalized;
    }

    private static ProjectAccessView AccessOf(bool isOwner, IReadOnlyCollection<string> permissions)
    {
        if (isOwner)
        {
            return new ProjectAccessView(true, true, true, true, ProjectPermissions.All);
        }

        var canRead = permissions.Count != 0;
        var canEdit = permissions.Contains(ProjectPermissions.Editor, StringComparer.Ordinal);
        var canApprove = permissions.Contains(ProjectPermissions.Approver, StringComparer.Ordinal);
        var canPublish = permissions.Contains(ProjectPermissions.Publisher, StringComparer.Ordinal);

        return new ProjectAccessView(
            canRead,
            canEdit,
            canApprove,
            canPublish,
            [.. permissions.OrderBy(permission => permission, StringComparer.Ordinal)]);
    }

    /// <summary>
    /// The caller's live owner membership in exactly <paramref name="tenantId"/>. A tenant the
    /// caller holds no live membership in is denied; a tenant that names nothing at all is the
    /// same 404 a foreign id gets.
    /// </summary>
    private async Task<ResolvedCaller> RequireOwnerAsync(
        OwnerSessionContext session,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var user = await RequireHostedUserAsync(session, cancellationToken).ConfigureAwait(false);
        var tenantExists = await dbContext.Tenants.AsNoTracking()
            .Where(row => row.Id == tenantId)
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!tenantExists)
        {
            throw new TenantNotFoundException();
        }

        var membership = await dbContext.TenantMemberships.AsNoTracking()
            .Where(row => row.TenantId == tenantId && row.UserId == user.Id)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (membership is null
            || membership.RevokedAt is not null
            || !string.Equals(membership.Role, TenantRoles.Owner, StringComparison.Ordinal))
        {
            throw new TenantAccessDeniedException();
        }

        return new ResolvedCaller(user.Id, tenantId);
    }

    /// <summary>
    /// The project as seen through the caller's own active tenant: the project's scope must be
    /// the active tenant's scope, so a guessed id from another tenant is 404 without exposure.
    /// </summary>
    private async Task<ResolvedProject> RequireTenantProjectAsync(
        OwnerSessionContext session,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var user = await RequireHostedUserAsync(session, cancellationToken).ConfigureAwait(false);
        var tenantId = await RequireActiveTenantAsync(session, user.Id, cancellationToken).ConfigureAwait(false);
        var scope = await dbContext.TenantScopes.AsNoTracking()
            .Where(row => row.TenantId == tenantId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (scope is null)
        {
            throw new TenantAccessDeniedException();
        }

        var projectExists = await dbContext.Projects.AsNoTracking()
            .Where(project => project.Id == projectId && project.ScopeId == scope.Id)
            .AnyAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!projectExists)
        {
            throw new TenantNotFoundException();
        }

        return new ResolvedProject(projectId, tenantId);
    }

    private async Task<ResolvedGrants> ReadProjectGrantsAsync(
        ResolvedProject project,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var membership = await dbContext.TenantMemberships.AsNoTracking()
            .Where(row => row.TenantId == project.TenantId && row.UserId == userId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (membership is null || membership.RevokedAt is not null)
        {
            throw new TenantAccessDeniedException();
        }

        if (string.Equals(membership.Role, TenantRoles.Owner, StringComparison.Ordinal))
        {
            return new ResolvedGrants(true, ProjectPermissions.All);
        }

        var permissions = await dbContext.ProjectMemberships.AsNoTracking()
            .Where(grant => grant.ProjectId == project.ProjectId && grant.UserId == userId)
            .Select(grant => grant.Permission)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ResolvedGrants(false, permissions);
    }

    private async Task<Guid> RequireActiveTenantAsync(
        OwnerSessionContext session,
        Guid userId,
        CancellationToken cancellationToken)
    {
        var hosted = await dbContext.HostedSessions.AsNoTracking()
            .Where(row => row.Id == session.SessionId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (hosted is null
            || hosted.UserId != session.ActorId
            || hosted.UserId != userId
            || !hosted.IsUsable(clock.GetUtcNow()))
        {
            throw new UnauthenticatedException();
        }

        if (hosted.ActiveTenantId is not { } tenantId)
        {
            throw new TenantSelectionRequiredException();
        }

        var membership = await dbContext.TenantMemberships.AsNoTracking()
            .Where(row => row.TenantId == tenantId && row.UserId == userId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (membership is null || membership.RevokedAt is not null)
        {
            throw new TenantAccessDeniedException();
        }

        return tenantId;
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

    private Task<long> LiveOwnerCountAsync(Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.TenantMemberships.AsNoTracking()
            .Where(membership =>
                membership.TenantId == tenantId
                && membership.RevokedAt == null
                && membership.Role == TenantRoles.Owner)
            .LongCountAsync(cancellationToken);

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
                new TeamsAuditMetadata(action),
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))));

    /// <summary>The bearer token: 32 random bytes, hex-encoded like every opaque token here.</summary>
    private static string OpaqueToken() =>
        Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));

    private sealed record ResolvedCaller(Guid UserId, Guid TenantId);

    private sealed record ResolvedProject(Guid ProjectId, Guid TenantId);

    private sealed record ResolvedGrants(bool IsOwner, IReadOnlyList<string> Permissions);

    private sealed record TeamsAuditMetadata(string Action);

    internal const string InvitationCreatedAction = "team_invitation_created";
    internal const string InvitationAcceptedAction = "team_invitation_accepted";
    internal const string InvitationRevokedAction = "team_invitation_revoked";
    internal const string MemberRoleChangedAction = "team_member_role_changed";
    internal const string MemberRemovedAction = "team_member_removed";
    internal const string ProjectGrantedAction = "team_project_granted";
    internal const string ProjectGrantRevokedAction = "team_project_grant_revoked";
}
