using Chronicleaf.Application.Common;
using Chronicleaf.Application.GitHub;
using Chronicleaf.Application.GitHubCandidates;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Operations;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Application.Schedules;

/// <summary>
/// The scheduled-project-sync use cases over the delivered stores, queue and candidate contracts
/// (SCHEDULED-PROJECT-SYNC-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>The tick never trusts its own read.</b> The due list is a moment in time, so every schedule is
/// re-read inside the firing step: a row that was disabled, moved or already advanced since the list
/// was taken is skipped rather than fired. The firing write itself is a compare-and-set on the
/// schedule's revision, so two ticks - or a tick and an owner's edit - cannot both advance one row.
/// </para>
/// <para>
/// <b>The queue carries an operation id and nothing else.</b> The tick enqueues through the delivered
/// operation service and writes the candidate run row the delivered poll reads back, exactly as the
/// manual poll route does. Nothing about the repository, the window or the credential travels in a
/// job payload.
/// </para>
/// </remarks>
public sealed class ProjectSyncScheduleService(
    IProjectStore projects,
    IGitHubConnectionStore connections,
    IProjectSyncScheduleStore schedules,
    IGitHubCandidateRunStore runs,
    IGitHubCandidateService candidates,
    IOperationService operations,
    IOperationQuery operationQuery,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<ProjectSyncScheduleService> logger) : IProjectSyncScheduleService
{
    /// <summary>The audit action recorded when a schedule is stored.</summary>
    public const string ScheduleStoredAction = "project.sync-schedule.stored";

    /// <summary>The audit action recorded when a schedule is turned off.</summary>
    public const string ScheduleDisabledAction = "project.sync-schedule.disabled";

    private static readonly IReadOnlyCollection<string> ActiveKinds =
    [
        IProjectSyncScheduleService.ScheduledSyncOperationKind,
        GitHubCandidateService.SyncOperationKind,
    ];

    /// <inheritdoc />
    public async Task<ProjectSyncScheduleView?> ReadAsync(
        OwnerScope owner,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        // A project this owner may not reach is a 404 rather than an absent schedule, so this read
        // cannot be used to discover another installation's project.
        _ = await projects.FindAsync(owner.ScopeId, projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("project");

        var stored = await schedules
            .FindForProjectAsync(owner.ScopeId, projectId, cancellationToken)
            .ConfigureAwait(false);

        if (stored is null)
        {
            return null;
        }

        return await ViewAsync(owner.ScopeId, stored, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ProjectSyncScheduleView> PutAsync(
        OwnerScope owner,
        Guid projectId,
        ProjectSyncScheduleRequest request,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        if (!ProjectSyncSchedulePolicy.IsStorableInterval(request.IntervalMinutes))
        {
            throw new ValidationFailedException(
                "intervalMinutes",
                ProjectSyncSchedulePolicy.IntervalMessage(request.IntervalMinutes));
        }

        if (expectedRevision < 0)
        {
            throw new ValidationFailedException("If-Match", "A schedule revision is zero or greater.");
        }

        var now = clock.GetUtcNow();

        var outcome = await unitOfWork.ExecuteAsync(
            async token =>
            {
                var project = await projects.FindAsync(owner.ScopeId, projectId, token).ConfigureAwait(false)
                    ?? throw new ResourceNotFoundException("project");

                if (project.ArchivedAt is not null)
                {
                    throw new ProjectArchivedException();
                }

                _ = await connections
                    .FindAsync(owner.ScopeId, projectId, token)
                    .ConfigureAwait(false)
                    ?? throw new ScheduleConnectionMissingException();

                var stored = await schedules
                    .FindForProjectAsync(owner.ScopeId, projectId, token)
                    .ConfigureAwait(false);

                if (stored is null)
                {
                    if (expectedRevision != 0)
                    {
                        throw new RevisionConflictException(0);
                    }

                    if (request.Enabled)
                    {
                        await RequireQuotaAsync(owner.ScopeId, token).ConfigureAwait(false);
                    }

                    var created = new ProjectSyncSchedule(
                        Guid.NewGuid(),
                        owner.ScopeId,
                        projectId,
                        request.Enabled,
                        request.IntervalMinutes,
                        now.AddMinutes(request.IntervalMinutes),
                        null,
                        now,
                        now);

                    if (!await schedules.TryInsertAsync(created, token).ConfigureAwait(false))
                    {
                        // Another writer created the row first. Its revision is 1, and this request
                        // was conditional on zero, so it loses rather than overwriting the winner.
                        throw new RevisionConflictException(1);
                    }

                    audit.Record(new DomainAudit(
                        owner.ActorId,
                        ScheduleStoredAction,
                        projectId,
                        created.Revision,
                        "sync schedule stored"));

                    return new PutOutcome(created, true);
                }

                if (stored.Revision != expectedRevision)
                {
                    throw new RevisionConflictException(stored.Revision);
                }

                if (stored.Enabled == request.Enabled && stored.IntervalMinutes == request.IntervalMinutes)
                {
                    // The stored intent already says this. A duplicate PUT is a no-op with no write
                    // and no audit row, for the same reason a duplicate assignment is.
                    return new PutOutcome(stored, false);
                }

                if (request.Enabled && !stored.Enabled)
                {
                    await RequireQuotaAsync(owner.ScopeId, token).ConfigureAwait(false);
                }

                var nextRunAt = stored.NextRunAt;
                if (request.Enabled && (!stored.Enabled || stored.IntervalMinutes != request.IntervalMinutes))
                {
                    // A new cadence starts now: the first run is one interval out, never immediate
                    // and never a burst for the time the schedule spent off.
                    nextRunAt = now.AddMinutes(request.IntervalMinutes);
                }

                stored.Change(request.Enabled, request.IntervalMinutes, nextRunAt, stored.LastOperationId, now);

                if (!await schedules.TryUpdateAsync(stored, expectedRevision, token).ConfigureAwait(false))
                {
                    var current = await schedules
                        .FindForProjectAsync(owner.ScopeId, projectId, token)
                        .ConfigureAwait(false);

                    throw new RevisionConflictException(current?.Revision ?? expectedRevision);
                }

                audit.Record(new DomainAudit(
                    owner.ActorId,
                    request.Enabled ? ScheduleStoredAction : ScheduleDisabledAction,
                    projectId,
                    stored.Revision,
                    request.Enabled ? "sync schedule stored" : "sync schedule disabled"));

                return new PutOutcome(stored, true);
            },
            cancellationToken).ConfigureAwait(false);

        if (!outcome.Written)
        {
            return await ViewAsync(owner.ScopeId, outcome.Schedule, cancellationToken).ConfigureAwait(false);
        }

        // A schedule that was just turned off cancels the polls it already asked for but that never
        // started. A running poll is left to finish: its candidates are still only candidates, and
        // discarding them mid-read would be a claim about work nobody observed.
        if (!outcome.Schedule.Enabled)
        {
            await CancelPendingAsync(owner.ScopeId, projectId, cancellationToken).ConfigureAwait(false);
        }

        logger.LogInformation(
            "Sync schedule stored for project {ProjectId} (enabled {Enabled}, every {Interval} minutes).",
            projectId,
            outcome.Schedule.Enabled,
            outcome.Schedule.IntervalMinutes);

        return await ViewAsync(owner.ScopeId, outcome.Schedule, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ProjectSyncTickResult> TickAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var due = await schedules
            .ListDueAsync(now, ProjectSyncSchedulePolicy.MaxDuePerTick, cancellationToken)
            .ConfigureAwait(false);

        var dispatched = 0;
        var disabled = 0;
        var skipped = 0;

        foreach (var listed in due)
        {
            var result = await FireAsync(listed.ScopeId, listed.ProjectId, now, cancellationToken)
                .ConfigureAwait(false);

            switch (result)
            {
                case FireOutcome.Dispatched: dispatched++; break;
                case FireOutcome.Disabled: disabled++; break;
                default: skipped++; break;
            }
        }

        if (due.Count > 0)
        {
            logger.LogInformation(
                "Sync tick fired {Dispatched} schedule(s), disabled {Disabled}, skipped {Skipped}.",
                dispatched,
                disabled,
                skipped);
        }

        return new ProjectSyncTickResult(due.Count, dispatched, disabled, skipped);
    }

    /// <inheritdoc />
    public async Task<string> RunScheduledSyncAsync(
        OwnerScope owner,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        // The poll re-reads the connection, the credential and the run row itself, so a schedule
        // whose repository was disconnected after the tick fails here with the delivered code rather
        // than polling a repository nobody connected. The scope is the operation's, never a
        // request's: a job runs long after the request that created it is gone.
        var polled = await candidates
            .PollAsync(owner, operationId, cancellationToken)
            .ConfigureAwait(false);

        return $"{IProjectSyncScheduleService.ScheduledSyncOperationKind}:{polled.CandidateCount}/{polled.Recorded}";
    }

    private async Task<FireOutcome> FireAsync(
        Guid scopeId,
        Guid projectId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // The firing decision is re-read inside one transaction: the due list is stale the moment it
        // is taken, and only a row that is still enabled and still due may fire.
        var prepared = await unitOfWork.ExecuteAsync(
            async token =>
            {
                var stored = await schedules
                    .FindForProjectAsync(scopeId, projectId, token)
                    .ConfigureAwait(false);

                if (stored is null || !stored.Enabled || stored.NextRunAt > now)
                {
                    return null;
                }

                var project = await projects.FindAsync(scopeId, projectId, token).ConfigureAwait(false);
                if (project is null || project.ArchivedAt is not null)
                {
                    return new PreparedFire(stored, null, null, FireOutcome.Disabled);
                }

                var connection = await connections
                    .FindAsync(scopeId, projectId, token)
                    .ConfigureAwait(false);
                if (connection is null)
                {
                    return new PreparedFire(stored, null, null, FireOutcome.Disabled);
                }

                var active = await schedules
                    .ListActiveOperationsAsync(scopeId, projectId, ActiveKinds, token)
                    .ConfigureAwait(false);
                if (active.Count > 0)
                {
                    // One active sync per project. The due instant stays where it is, so the poll
                    // happens when the running one finishes rather than beside it.
                    return new PreparedFire(stored, null, null, FireOutcome.Skipped);
                }

                return new PreparedFire(stored, project, connection, FireOutcome.Dispatched);
            },
            cancellationToken).ConfigureAwait(false);

        if (prepared is null || prepared.Outcome is not FireOutcome.Dispatched)
        {
            if (prepared?.Outcome is FireOutcome.Disabled)
            {
                await DisableAsync(prepared.Schedule, now, cancellationToken).ConfigureAwait(false);
                return FireOutcome.Disabled;
            }

            return FireOutcome.Skipped;
        }

        var key = ProjectSyncSchedulePolicy.IdempotencyKey(prepared.Schedule.Id, prepared.Schedule.NextRunAt);

        // The job binds to the project's revision, which is a signal nothing this package's own work
        // moves: candidates are not milestones, so recording one never advances a project's
        // revision. A project that was archived, renamed or reconnected after the tick therefore ends
        // the operation superseded rather than polling against a connection that has since changed.
        var accepted = await operations.EnqueueAsync(
            new EnqueueOperationRequest(
                scopeId,
                IProjectSyncScheduleService.ScheduledSyncOperationKind,
                projectId,
                prepared.Project!.Revision,
                key),
            cancellationToken).ConfigureAwait(false);

        if (!accepted.Created)
        {
            // Another tick already fired this due instant and holds the operation for it. The next
            // run stays where the winner put it.
            return FireOutcome.Skipped;
        }

        // The run row is written after the job exists, because it names the job. A replay under the
        // same idempotency key gets the original job back and its own insert changes no row.
        var run = new GitHubCandidateRun(
            Guid.NewGuid(),
            scopeId,
            accepted.Operation.Id,
            projectId,
            prepared.Project.Revision,
            prepared.Connection!.Owner,
            prepared.Connection.Repo,
            null,
            now);

        var fired = await unitOfWork.ExecuteAsync(
            async token =>
            {
                _ = await runs.TryInsertAsync(run, token).ConfigureAwait(false);

                var stored = await schedules
                    .FindForProjectAsync(scopeId, projectId, token)
                    .ConfigureAwait(false);

                if (stored is null || !stored.Enabled || stored.Id != prepared.Schedule.Id)
                {
                    // Disabled or replaced while the job was being created. The operation stands -
                    // the disable path cancels it if it never started - and the row is left for the
                    // next tick to reconcile rather than advanced here.
                    return false;
                }

                stored.MarkFired(now, accepted.Operation.Id);

                return await schedules
                    .TryUpdateAsync(stored, prepared.Schedule.Revision, token)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        if (!fired)
        {
            logger.LogInformation(
                "Sync tick for project {ProjectId} lost its schedule write; the next pass reconciles it.",
                projectId);
        }

        audit.Record(new DomainAudit(
            null,
            ScheduleStoredAction,
            projectId,
            prepared.Schedule.Revision + 1,
            "scheduled sync dispatched"));

        return FireOutcome.Dispatched;
    }

    private async Task DisableAsync(
        ProjectSyncSchedule schedule,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var disabled = await unitOfWork.ExecuteAsync(
            async token =>
            {
                var stored = await schedules
                    .FindForProjectAsync(schedule.ScopeId, schedule.ProjectId, token)
                    .ConfigureAwait(false);

                if (stored is null || !stored.Enabled)
                {
                    return false;
                }

                stored.Disable(now);

                if (!await schedules.TryUpdateAsync(stored, schedule.Revision, token).ConfigureAwait(false))
                {
                    return false;
                }

                audit.Record(new DomainAudit(
                    null,
                    ScheduleDisabledAction,
                    schedule.ProjectId,
                    stored.Revision,
                    "sync schedule disabled: project archived or disconnected"));

                return true;
            },
            cancellationToken).ConfigureAwait(false);

        if (disabled)
        {
            logger.LogInformation(
                "Sync schedule for project {ProjectId} disabled: archived or disconnected.",
                schedule.ProjectId);

            await CancelPendingAsync(schedule.ScopeId, schedule.ProjectId, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task CancelPendingAsync(
        Guid scopeId,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var active = await schedules
            .ListActiveOperationsAsync(
                scopeId,
                projectId,
                [IProjectSyncScheduleService.ScheduledSyncOperationKind],
                cancellationToken)
            .ConfigureAwait(false);

        foreach (var pending in active)
        {
            if (!string.Equals(pending.Status, OperationStatuses.ToWire(OperationStatus.Queued), StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                await operations.CancelAsync(scopeId, pending.OperationId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationNotCancellableException)
            {
                // Started or finished while the disable was being recorded. A running poll keeps
                // the delivered outcome; only work that never started is withdrawn.
            }
            catch (OperationNotFoundException)
            {
                // Left the scope while the disable was being recorded. Nothing to withdraw.
            }
        }
    }

    private async Task RequireQuotaAsync(Guid scopeId, CancellationToken cancellationToken)
    {
        var enabled = await schedules.CountEnabledAsync(scopeId, cancellationToken).ConfigureAwait(false);
        if (enabled >= ProjectSyncSchedule.MaxEnabledPerScope)
        {
            throw new ScheduleQuotaExceededException(ProjectSyncSchedule.MaxEnabledPerScope);
        }
    }

    private async Task<ProjectSyncScheduleView> ViewAsync(
        Guid scopeId,
        ProjectSyncSchedule stored,
        CancellationToken cancellationToken)
    {
        ProjectSyncLastOperationView? last = null;

        if (stored.LastOperationId is { } operationId)
        {
            var operation = await operationQuery
                .FindAsync(scopeId, operationId, cancellationToken)
                .ConfigureAwait(false);

            if (operation is not null)
            {
                last = new ProjectSyncLastOperationView(
                    operation.Id,
                    OperationStatuses.ToWire(operation.Status),
                    operation.ErrorCode,
                    operation.CompletedAt);
            }
        }

        return new ProjectSyncScheduleView(
            stored.ProjectId,
            stored.Enabled,
            stored.IntervalMinutes,
            stored.NextRunAt,
            last,
            stored.Revision,
            stored.UpdatedAt);
    }

    private sealed record PutOutcome(ProjectSyncSchedule Schedule, bool Written);

    private sealed record PreparedFire(
        ProjectSyncSchedule Schedule,
        Project? Project,
        GitHubConnection? Connection,
        FireOutcome Outcome);

    private enum FireOutcome
    {
        Dispatched,
        Disabled,
        Skipped,
    }
}
