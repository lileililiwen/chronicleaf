using System.Globalization;
using System.Text.Json.Serialization;
using Chronicleaf.Api.Access;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Archive;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Teams;
using Chronicleaf.Application.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Chronicleaf.Api.Archive;

/// <summary>
/// The hosted-archive-operations routes (HOSTED-ARCHIVE-OPERATIONS-002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Three private routes, and none of them erases.</b> The DELETE confirms the project's own
/// name at the project's own revision, suspends the projection and the poll through the
/// delivered contracts, and then writes the tombstone; the GET reads the stored window; the
/// restore lifts a live deletion inside that window at the tombstone's own revision. Evidence, milestones, assets, approvals
/// and media are never deleted by any route here: only the operator purge removes anything, and
/// it is a service method rather than a route, because a purge is an operator's act rather than
/// an owner's request.
/// </para>
/// <para>
/// <b>No route here serves a backup manifest.</b> The backup reference names the operator's
/// snapshot inventory and the key version names the operator's key history; neither belongs in
/// an owner-facing response, so manifests are recorded and rehearsed through the service by
/// operator tooling, never through these routes.
/// </para>
/// </remarks>
public static class ArchiveEndpoints
{
    public static IEndpointRouteBuilder MapArchiveEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapGet("/api/projects/{projectId:guid}/archive-data", ReadAsync)
            .RequireOwnerSession();

        endpoints
            .MapDelete("/api/projects/{projectId:guid}/archive-data", DeleteAsync)
            .RequireOwnerSession();

        endpoints
            .MapPost("/api/projects/{projectId:guid}/archive-data/restore", RestoreAsync)
            .RequireOwnerSession();

        return endpoints;
    }

    private static async Task<IResult> ReadAsync(
        Guid projectId,
        HttpContext context,
        IArchiveRetentionService retention,
        IOwnerAccessService access,
        ITeamPermissionService teams,
        TenancySettings tenancy,
        CancellationToken cancellationToken)
    {
        var session = AccessEndpoints.OwnerSession(context);
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        // HOSTED-ARCHIVE-OPERATIONS-002: in Hosted mode reading the archive state is a granted
        // read, not a membership read. The Community path takes no branch on team state.
        if (tenancy.IsHosted)
        {
            await teams.RequireProjectPermissionAsync(
                session,
                projectId,
                "read",
                false,
                cancellationToken).ConfigureAwait(false);
        }

        var stored = await retention.ReadAsync(owner, projectId, cancellationToken).ConfigureAwait(false);

        if (stored is null)
        {
            throw new ResourceNotFoundException("project archive");
        }

        context.Response.Headers.ETag = $"\"{stored.Revision.ToString(CultureInfo.InvariantCulture)}\"";

        return Results.Json(ArchiveTombstoneResponse.From(stored));
    }

    private static async Task<IResult> DeleteAsync(
        Guid projectId,
        HttpContext context,
        [FromBody] ArchiveDeleteRequestBody? request,
        IArchiveRetentionService retention,
        IOwnerAccessService access,
        ITeamPermissionService teams,
        TenancySettings tenancy,
        CancellationToken cancellationToken)
    {
        var session = AccessEndpoints.OwnerSession(context);
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        // HOSTED-ARCHIVE-OPERATIONS-002: in Hosted mode deleting is a granted edit. The
        // Community path takes no branch on team state.
        if (tenancy.IsHosted)
        {
            await teams.RequireProjectPermissionAsync(
                session,
                projectId,
                "edit",
                false,
                cancellationToken).ConfigureAwait(false);
        }

        var body = request ?? new ArchiveDeleteRequestBody(null);

        if (string.IsNullOrWhiteSpace(body.ConfirmProjectName))
        {
            throw new ValidationFailedException(
                "confirmProjectName", "Deleting archive data names the project being deleted.");
        }

        var expected = RequireRevision(context, "project");

        var deleted = await retention.DeleteAsync(
            owner,
            projectId,
            body.ConfirmProjectName,
            expected,
            cancellationToken).ConfigureAwait(false);

        context.Response.Headers.ETag = $"\"{deleted.Revision.ToString(CultureInfo.InvariantCulture)}\"";

        return Results.Json(ArchiveTombstoneResponse.From(deleted));
    }

    private static async Task<IResult> RestoreAsync(
        Guid projectId,
        HttpContext context,
        IArchiveRetentionService retention,
        IOwnerAccessService access,
        ITeamPermissionService teams,
        TenancySettings tenancy,
        CancellationToken cancellationToken)
    {
        var session = AccessEndpoints.OwnerSession(context);
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        // HOSTED-ARCHIVE-OPERATIONS-002: in Hosted mode restoring is a granted edit, like the
        // delete it lifts. The Community path takes no branch on team state.
        if (tenancy.IsHosted)
        {
            await teams.RequireProjectPermissionAsync(
                session,
                projectId,
                "edit",
                false,
                cancellationToken).ConfigureAwait(false);
        }

        var expected = RequireRevision(context, "project's archive");

        var restored = await retention.RestoreAsync(
            owner,
            projectId,
            expected,
            cancellationToken).ConfigureAwait(false);

        context.Response.Headers.ETag = $"\"{restored.Revision.ToString(CultureInfo.InvariantCulture)}\"";

        return Results.Json(ArchiveTombstoneResponse.From(restored));
    }

    /// <summary>
    /// The revision a mutation is conditional on, from the header alone. Archive mutations carry
    /// no revision in the body: the header is the whole precondition, so a writer that never read
    /// the state cannot construct a request that looks conditional.
    /// </summary>
    private static long RequireRevision(HttpContext context, string resource)
    {
        var header = context.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            throw new IfMatchRequiredException(resource);
        }

        var token = header.Trim().Trim('"');
        if (!long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var revision))
        {
            throw new ValidationFailedException(
                "If-Match", "An archive revision is the stored integer revision.");
        }

        return revision;
    }

    private static async Task<OwnerScope> OwnerScopeAsync(
        HttpContext context,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var session = AccessEndpoints.OwnerSession(context);
        var identity = await access.EnsureOwnerAsync(cancellationToken).ConfigureAwait(false);

        return new OwnerScope(identity.InstallationId, session.ActorId);
    }
}

/// <summary>
/// What a client sends to delete archive data: the project's own name, spelled exactly. A name
/// that does not match is refused before the revision is compared, so a caller holding a stale
/// revision for the wrong project learns nothing about the right one.
/// </summary>
public sealed record ArchiveDeleteRequestBody(
    [property: JsonPropertyName("confirmProjectName")] string? ConfirmProjectName);

/// <summary>
/// One tombstone as served: the window, the erase intent, the hold and the outcome, with the
/// revision the restore must present.
/// </summary>
public sealed record ArchiveTombstoneResponse(
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("projectName")] string ProjectName,
    [property: JsonPropertyName("deletedAt")] string DeletedAt,
    [property: JsonPropertyName("recoveryUntil")] string RecoveryUntil,
    [property: JsonPropertyName("eraseRequested")] bool EraseRequested,
    [property: JsonPropertyName("holdReason")] string? HoldReason,
    [property: JsonPropertyName("restoredAt")] string? RestoredAt,
    [property: JsonPropertyName("purgedAt")] string? PurgedAt,
    [property: JsonPropertyName("live")] bool Live,
    [property: JsonPropertyName("revision")] long Revision)
{
    public static ArchiveTombstoneResponse From(ArchiveTombstoneView tombstone)
    {
        ArgumentNullException.ThrowIfNull(tombstone);

        return new ArchiveTombstoneResponse(
            tombstone.ProjectId.ToString("D"),
            tombstone.ProjectName,
            Stamp(tombstone.DeletedAt),
            Stamp(tombstone.RecoveryUntil),
            tombstone.EraseRequested,
            tombstone.HoldReason,
            tombstone.RestoredAt is { } restored ? Stamp(restored) : null,
            tombstone.PurgedAt is { } purged ? Stamp(purged) : null,
            tombstone.Live,
            tombstone.Revision);
    }

    internal static string Stamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
}
