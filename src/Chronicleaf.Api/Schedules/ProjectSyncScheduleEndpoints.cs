using System.Globalization;
using System.Text.Json.Serialization;
using Chronicleaf.Api.Access;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Schedules;
using Chronicleaf.Application.Teams;
using Chronicleaf.Application.Tenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Chronicleaf.Api.Schedules;

/// <summary>
/// The scheduled-project-sync routes (SCHEDULED-PROJECT-SYNC-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two private routes, and both of them read the project's own revision through the schedule's.</b>
/// The GET answers the stored intent with the last operation the tick created, so a panel can show a
/// failure with its redacted code and the next bounded attempt without inventing progress. The PUT
/// creates or replaces the schedule at exactly the <c>If-Match</c> revision, because a schedule that
/// raced a firing tick must lose loudly rather than silently overwrite the tick's advance.
/// </para>
/// <para>
/// <b>Nothing here confirms anything.</b> There is no approve verb, no milestone write and no
/// evidence append anywhere in this file. The most a route can do is arm a future poll, and the most
/// a response can carry is the cadence and the last outcome of one.
/// </para>
/// </remarks>
public static class ProjectSyncScheduleEndpoints
{
    public static IEndpointRouteBuilder MapProjectSyncScheduleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapGet("/api/projects/{projectId:guid}/sync-schedule", ReadAsync)
            .RequireOwnerSession();

        endpoints
            .MapPut("/api/projects/{projectId:guid}/sync-schedule", PutAsync)
            .RequireOwnerSession();

        return endpoints;
    }

    private static async Task<IResult> ReadAsync(
        Guid projectId,
        HttpContext context,
        IProjectSyncScheduleService schedules,
        IOwnerAccessService access,
        ITeamPermissionService teams,
        TenancySettings tenancy,
        CancellationToken cancellationToken)
    {
        var session = AccessEndpoints.OwnerSession(context);
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        // SCHEDULED-PROJECT-SYNC-001: in Hosted mode reading a schedule is a granted read, not a
        // membership read. The Community path takes no branch on team state.
        if (tenancy.IsHosted)
        {
            await teams.RequireProjectPermissionAsync(
                session,
                projectId,
                "read",
                false,
                cancellationToken).ConfigureAwait(false);
        }

        var stored = await schedules.ReadAsync(owner, projectId, cancellationToken).ConfigureAwait(false);

        if (stored is null)
        {
            throw new ResourceNotFoundException("project sync schedule");
        }

        context.Response.Headers.ETag = $"\"{stored.Revision.ToString(CultureInfo.InvariantCulture)}\"";

        return Results.Json(ProjectSyncScheduleResponse.From(stored));
    }

    private static async Task<IResult> PutAsync(
        Guid projectId,
        HttpContext context,
        ProjectSyncScheduleRequestBody? request,
        IProjectSyncScheduleService schedules,
        IOwnerAccessService access,
        ITeamPermissionService teams,
        TenancySettings tenancy,
        CancellationToken cancellationToken)
    {
        var session = AccessEndpoints.OwnerSession(context);
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        // SCHEDULED-PROJECT-SYNC-001: in Hosted mode arming a schedule is a granted edit. A reader
        // with no edit grant is refused here before the compare-and-set runs; the Community path
        // takes no branch on team state.
        if (tenancy.IsHosted)
        {
            await teams.RequireProjectPermissionAsync(
                session,
                projectId,
                "edit",
                false,
                cancellationToken).ConfigureAwait(false);
        }

        var body = request ?? new ProjectSyncScheduleRequestBody(null, null);

        if (body.Enabled is not { } enabled)
        {
            throw new ValidationFailedException("enabled", "A sync schedule says whether it is enabled.");
        }

        if (body.IntervalMinutes is not { } intervalMinutes)
        {
            throw new ValidationFailedException("intervalMinutes", "A sync schedule names its interval in minutes.");
        }

        var expected = RequireExpectedScheduleRevision(context);

        var stored = await schedules.PutAsync(
            owner,
            projectId,
            new ProjectSyncScheduleRequest(enabled, intervalMinutes),
            expected,
            cancellationToken).ConfigureAwait(false);

        context.Response.Headers.ETag = $"\"{stored.Revision.ToString(CultureInfo.InvariantCulture)}\"";

        return Results.Json(ProjectSyncScheduleResponse.From(stored));
    }

    /// <summary>
    /// The schedule revision a mutation is conditional on, from the header alone. Zero creates:
    /// a project that holds no schedule is at revision zero, so a PUT that names zero is the only
    /// one that may create it.
    /// </summary>
    private static long RequireExpectedScheduleRevision(HttpContext context)
    {
        var header = context.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            throw new IfMatchRequiredException("project's sync schedule");
        }

        var raw = header.Trim().Trim('"');
        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new ValidationFailedException("If-Match", "An If-Match revision is a whole number.");
        }

        if (parsed < 0)
        {
            throw new ValidationFailedException("If-Match", "A schedule revision is zero or greater.");
        }

        return parsed;
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
/// What a client sends to arm a schedule: the switch and the cadence in minutes. Both are required:
/// a schedule that silently kept its old cadence after an owner asked for a new one would look like
/// a correct answer to a question nobody asked.
/// </summary>
public sealed record ProjectSyncScheduleRequestBody(
    [property: JsonPropertyName("enabled")] bool? Enabled,
    [property: JsonPropertyName("intervalMinutes")] int? IntervalMinutes);

/// <summary>
/// One schedule as served: the switch, the cadence, the next due instant, the last operation the
/// tick created with its redacted outcome, and the revision the next change must present.
/// </summary>
public sealed record ProjectSyncScheduleResponse(
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("intervalMinutes")] int IntervalMinutes,
    [property: JsonPropertyName("nextRunAt")] string NextRunAt,
    [property: JsonPropertyName("lastOperation")] ProjectSyncLastOperationResponse? LastOperation,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("updatedAt")] string UpdatedAt)
{
    public static ProjectSyncScheduleResponse From(ProjectSyncScheduleView schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        return new ProjectSyncScheduleResponse(
            schedule.ProjectId.ToString("D"),
            schedule.Enabled,
            schedule.IntervalMinutes,
            Stamp(schedule.NextRunAt),
            schedule.LastOperation is { } last
                ? new ProjectSyncLastOperationResponse(
                    last.OperationId.ToString("D"),
                    last.Status,
                    last.ErrorCode,
                    last.CompletedAt is { } completed ? Stamp(completed) : null)
                : null,
            schedule.Revision,
            Stamp(schedule.UpdatedAt));
    }

    internal static string Stamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
}

/// <summary>
/// The last operation a schedule's tick created: its status, its redacted error code and when it
/// finished. A panel that shows this shows the truth about a failed poll - the code and the next
/// bounded attempt - without anything that looks like progress.
/// </summary>
public sealed record ProjectSyncLastOperationResponse(
    [property: JsonPropertyName("operationId")] string OperationId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("errorCode")] string? ErrorCode,
    [property: JsonPropertyName("completedAt")] string? CompletedAt);
