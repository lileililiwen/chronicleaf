using Chronicleaf.Application.Milestones;
using Chronicleaf.Core;

namespace Chronicleaf.Application.Schedules;

/// <summary>
/// One schedule as served to its owner, with the last outcome the operations table reports.
/// </summary>
/// <param name="ProjectId">The project this schedule polls.</param>
/// <param name="Enabled">Whether the tick may fire for this row.</param>
/// <param name="IntervalMinutes">The cadence the owner asked for.</param>
/// <param name="NextRunAt">When the next poll is due.</param>
/// <param name="LastOperation">The most recent tick's operation, when a tick has fired.</param>
/// <param name="Revision">The optimistic revision the next change must present.</param>
/// <param name="UpdatedAt">When the schedule last changed.</param>
public sealed record ProjectSyncScheduleView(
    Guid ProjectId,
    bool Enabled,
    int IntervalMinutes,
    DateTimeOffset NextRunAt,
    ProjectSyncLastOperationView? LastOperation,
    long Revision,
    DateTimeOffset UpdatedAt);

/// <summary>
/// The last operation a schedule's tick created, as the operation row describes it. The error is a
/// redacted code, never a provider body, because that is all the operation table holds.
/// </summary>
public sealed record ProjectSyncLastOperationView(
    Guid OperationId,
    string Status,
    string? ErrorCode,
    DateTimeOffset? CompletedAt);

/// <summary>What an owner asks to store: the switch and the cadence.</summary>
public sealed record ProjectSyncScheduleRequest(bool Enabled, int IntervalMinutes);

/// <summary>What one tick pass did, for the loop's own log line and nothing else.</summary>
public sealed record ProjectSyncTickResult(int Due, int Dispatched, int Disabled, int Skipped);

/// <summary>
/// The narrow write surface the scheduled-project-sync package owns. Every method is scoped: an id
/// that belongs to another scope answers as absent, so the boundary cannot be probed for what exists
/// elsewhere.
/// </summary>
public interface IProjectSyncScheduleStore
{
    /// <summary>Reads one scope's schedule for a project, or <c>null</c> when it has none.</summary>
    Task<ProjectSyncSchedule?> FindForProjectAsync(
        Guid scopeId,
        Guid projectId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the due enabled schedules across scopes, oldest due first, bounded.</summary>
    Task<IReadOnlyList<ProjectSyncSchedule>> ListDueAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Counts one scope's enabled schedules, for the activation quota.</summary>
    Task<long> CountEnabledAsync(Guid scopeId, CancellationToken cancellationToken = default);

    /// <summary>Inserts one schedule. Returns false when the project already holds one.</summary>
    Task<bool> TryInsertAsync(ProjectSyncSchedule schedule, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the schedule only when its stored revision is still
    /// <paramref name="expectedRevision"/>. Returns false when another writer moved it first.
    /// </summary>
    Task<bool> TryUpdateAsync(
        ProjectSyncSchedule schedule,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The non-terminal operations a project's sync kinds currently hold, oldest first. A tick
    /// that finds one does not enqueue another; a disable cancels the ones that never started.
    /// </summary>
    Task<IReadOnlyList<ActiveSyncOperation>> ListActiveOperationsAsync(
        Guid scopeId,
        Guid projectId,
        IReadOnlyCollection<string> kinds,
        CancellationToken cancellationToken = default);
}

/// <summary>One non-terminal operation a project's sync kinds hold: whose, which kind, which state.</summary>
public sealed record ActiveSyncOperation(Guid OperationId, string Kind, string Status);
