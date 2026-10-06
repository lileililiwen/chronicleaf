using Chronicleaf.Application.Milestones;

namespace Chronicleaf.Application.Schedules;

/// <summary>
/// The scheduled-project-sync use cases over the delivered stores, queue and candidate contracts
/// (SCHEDULED-PROJECT-SYNC-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>The dependency's contracts are consumed, never redefined.</b> The repository a scheduled poll
/// reads comes from the delivered <c>GitHubConnection</c> row, the work is a delivered operation on
/// the delivered queue, and the poll itself is the delivered candidate poll with its own run row -
/// this package adds one table, two routes and one tick, and no second poll, no second transport and
/// no store under a delivered name.
/// </para>
/// <para>
/// <b>A scheduled sync proposes and nothing else.</b> There is no method here that confirms a
/// candidate, appends evidence, writes a draft or publishes, and no method that could be described
/// as doing so by accident: the job this package's tick creates runs the delivered candidate poll,
/// whose own contract already refuses to confirm anything.
/// </para>
/// </remarks>
public interface IProjectSyncScheduleService
{
    /// <summary>The job kind a firing tick enqueues. The target is the project, so the delivered supersede check reads its revision.</summary>
    const string ScheduledSyncOperationKind = "project.scheduled-sync";

    /// <summary>Reads one project's schedule, or <c>null</c> when it has none.</summary>
    Task<ProjectSyncScheduleView?> ReadAsync(
        OwnerScope owner,
        Guid projectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores a project's schedule at exactly <paramref name="expectedRevision"/>: zero for a
    /// project that holds none, the stored revision otherwise. Enabling past the scope quota is
    /// refused, and a project with no readable connection cannot be scheduled.
    /// </summary>
    Task<ProjectSyncScheduleView> PutAsync(
        OwnerScope owner,
        Guid projectId,
        ProjectSyncScheduleRequest request,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fires every due enabled schedule once: one operation per schedule, the next run collapsed to
    /// one interval past now, and a schedule whose project is archived or disconnected turned off
    /// rather than polled.
    /// </summary>
    Task<ProjectSyncTickResult> TickAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs the poll a scheduled operation was created for: the delivered candidate poll, after
    /// re-checking that the schedule is still enabled and the project still connected.
    /// </summary>
    Task<string> RunScheduledSyncAsync(
        OwnerScope owner,
        Guid operationId,
        CancellationToken cancellationToken = default);
}
