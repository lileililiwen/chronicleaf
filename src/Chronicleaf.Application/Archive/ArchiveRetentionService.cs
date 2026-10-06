using Chronicleaf.Application.Common;
using Chronicleaf.Application.Journey;
using Chronicleaf.Application.Media;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Schedules;
using Chronicleaf.Core;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Application.Archive;

/// <summary>
/// The owner-run retention use cases over the delivered stores (HOSTED-ARCHIVE-OPERATIONS-002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Deletion suspends; it never erases.</b> The delete route confirms the project's own name,
/// presents the project's own revision, suspends the public projection and the recurring poll
/// through the delivered contracts, and then writes the tombstone. Units of work never nest:
/// each step owns its own commit and each step is idempotent, so a retry after a partial
/// failure finishes the suspension and either inserts the tombstone or loses loudly to the
/// delete that already did. Scope credentials are untouched - they belong to the scope, not the
/// project - and no evidence, milestone, asset, approval or media row is deleted by this
/// package's delete path.
/// </para>
/// <para>
/// <b>The purge keeps what it cannot prove is eligible.</b> After the thirty-day window, the
/// operator pass retires the project's unreferenced media through the delivered media store -
/// which refuses anything still referenced with <c>media_in_use</c> - and stamps the tombstone
/// with a receipt. A live approval, a live projection, a hold, or an open window each stop the
/// purge for that tombstone before a single byte moves. Evidence, milestones, assets and
/// approvals are never purge-eligible and are itemized as retained.
/// </para>
/// <para>
/// <b>Restores supersede rather than merge.</b> A restore inside the window lifts the deletion
/// and withdraws the erase request; nothing is reconstructed because nothing was moved. A
/// restore past the window is expired, and restoring what is not live is not found - neither is
/// merged into a new state.
/// </para>
/// </remarks>
public sealed class ArchiveRetentionService(
    IProjectStore projects,
    IArchiveTombstoneStore tombstones,
    IArchiveDigestReader digests,
    IProjectJourneyService journeys,
    IProjectSyncScheduleService schedules,
    IMediaStore media,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<ArchiveRetentionService> logger) : IArchiveRetentionService
{
    /// <summary>The audit action recorded when an owner deletes a project's archive data.</summary>
    public const string ArchiveDeletedAction = "project.archive-deleted";

    /// <summary>The audit action recorded when an owner restores inside the window.</summary>
    public const string ArchiveRestoredAction = "project.archive-restored";

    /// <summary>The audit action recorded when the operator purge removes eligible data.</summary>
    public const string ArchivePurgedAction = "project.archive-purged";

    /// <summary>The audit action recorded when an operator holds or releases a tombstone.</summary>
    public const string ArchiveHoldAction = "project.archive-hold";

    /// <summary>How many media ids one purge pass retires per project, so a pass stays finite.</summary>
    public const int PurgeMediaLimit = 100;

    /// <inheritdoc />
    public async Task<ArchiveTombstoneView?> ReadAsync(
        OwnerScope owner,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        _ = await projects.FindAsync(owner.ScopeId, projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("project");

        var stored = await tombstones.FindLiveAsync(owner.ScopeId, projectId, cancellationToken)
            .ConfigureAwait(false);

        if (stored is null)
        {
            return null;
        }

        return ArchiveTombstoneView.From(stored);
    }

    /// <inheritdoc />
    public async Task<ArchiveTombstoneView> DeleteAsync(
        OwnerScope owner,
        Guid projectId,
        string confirmProjectName,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (string.IsNullOrWhiteSpace(confirmProjectName))
        {
            throw new ValidationFailedException(
                "confirmProjectName", "Deleting archive data names the project being deleted.");
        }

        if (expectedRevision < 1)
        {
            throw new ValidationFailedException(
                "If-Match", "Deleting archive data presents the project's stored revision.");
        }

        var now = clock.GetUtcNow();

        // The guards read before anything writes: a delete that cannot name the project, the
        // revision or the window loses before a tombstone exists.
        var project = await projects.FindAsync(owner.ScopeId, projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("project");

        if (!string.Equals(project.Name, confirmProjectName, StringComparison.Ordinal))
        {
            throw new ValidationFailedException(
                "confirmProjectName", "The confirmation does not name this project.");
        }

        if (project.Revision != expectedRevision)
        {
            throw new RevisionConflictException(project.Revision);
        }

        var existing = await tombstones.FindLiveAsync(owner.ScopeId, projectId, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            throw new ArchiveAlreadyDeletedException();
        }

        // The suspensions run before the tombstone, each in its own unit of work through the
        // delivered contract, and each is idempotent: a retry after a partial failure revokes
        // what is still live, disables what is still enabled, and then either inserts the
        // tombstone or loses loudly to the delete that already did. Units of work never nest
        // here - the delivered services own theirs - so a failure in one step rolls back only
        // that step, never a half-committed combination.
        await RevokeJourneyAsync(owner, projectId, cancellationToken).ConfigureAwait(false);
        await DisableScheduleAsync(owner, projectId, cancellationToken).ConfigureAwait(false);

        var deleted = await unitOfWork.ExecuteAsync(
            async token =>
            {
                var tombstone = new ArchiveTombstone(
                    Guid.NewGuid(),
                    owner.ScopeId,
                    projectId,
                    project.Name,
                    now);

                if (!await tombstones.TryInsertAsync(tombstone, token).ConfigureAwait(false))
                {
                    throw new ArchiveAlreadyDeletedException();
                }

                audit.Record(new DomainAudit(
                    owner.ActorId,
                    ArchiveDeletedAction,
                    projectId,
                    tombstone.Revision,
                    "project archive deleted"));

                return tombstone;
            },
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Project {ProjectId} archive deleted in scope {ScopeId}.",
            projectId,
            owner.ScopeId);

        return ArchiveTombstoneView.From(deleted);
    }

    /// <inheritdoc />
    public async Task<ArchiveTombstoneView> RestoreAsync(
        OwnerScope owner,
        Guid projectId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (expectedRevision < 1)
        {
            throw new ValidationFailedException(
                "If-Match", "Restoring archive data presents the tombstone's stored revision.");
        }

        var now = clock.GetUtcNow();

        var restored = await unitOfWork.ExecuteAsync(
            async token =>
            {
                _ = await projects.FindAsync(owner.ScopeId, projectId, token).ConfigureAwait(false)
                    ?? throw new ResourceNotFoundException("project");

                var live = await tombstones.FindLiveAsync(owner.ScopeId, projectId, token).ConfigureAwait(false)
                    ?? throw new ResourceNotFoundException("project archive");

                if (live.Revision != expectedRevision)
                {
                    throw new RevisionConflictException(live.Revision);
                }

                if (live.IsWindowElapsed(now))
                {
                    throw new ArchiveRestoreExpiredException();
                }

                live.Restore(now);

                if (!await tombstones.TryUpdateAsync(live, expectedRevision, token).ConfigureAwait(false))
                {
                    throw new RevisionConflictException(expectedRevision + 1);
                }

                audit.Record(new DomainAudit(
                    owner.ActorId,
                    ArchiveRestoredAction,
                    projectId,
                    live.Revision,
                    "project archive restored"));

                return live;
            },
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Project {ProjectId} archive restored in scope {ScopeId}.",
            projectId,
            owner.ScopeId);

        return ArchiveTombstoneView.From(restored);
    }

    /// <inheritdoc />
    public async Task<ArchiveTombstoneView> HoldAsync(
        OwnerScope owner,
        Guid tombstoneId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ValidationFailedException("reason", "Holding a tombstone names why it is held.");
        }

        var now = clock.GetUtcNow();

        var held = await unitOfWork.ExecuteAsync(
            async token =>
            {
                var stored = await tombstones.FindAsync(owner.ScopeId, tombstoneId, token).ConfigureAwait(false)
                    ?? throw new ResourceNotFoundException("project archive");

                stored.Hold(reason.Trim(), now);

                if (!await tombstones.TryUpdateAsync(stored, stored.Revision - 1, token).ConfigureAwait(false))
                {
                    throw new RevisionConflictException(stored.Revision);
                }

                audit.Record(new DomainAudit(
                    owner.ActorId,
                    ArchiveHoldAction,
                    stored.ProjectId,
                    stored.Revision,
                    "project archive held"));

                return stored;
            },
            cancellationToken).ConfigureAwait(false);

        return ArchiveTombstoneView.From(held);
    }

    /// <inheritdoc />
    public async Task<ArchiveTombstoneView> ClearHoldAsync(
        OwnerScope owner,
        Guid tombstoneId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var now = clock.GetUtcNow();

        var released = await unitOfWork.ExecuteAsync(
            async token =>
            {
                var stored = await tombstones.FindAsync(owner.ScopeId, tombstoneId, token).ConfigureAwait(false)
                    ?? throw new ResourceNotFoundException("project archive");

                stored.ClearHold(now);

                if (!await tombstones.TryUpdateAsync(stored, stored.Revision - 1, token).ConfigureAwait(false))
                {
                    throw new RevisionConflictException(stored.Revision);
                }

                audit.Record(new DomainAudit(
                    owner.ActorId,
                    ArchiveHoldAction,
                    stored.ProjectId,
                    stored.Revision,
                    "project archive hold cleared"));

                return stored;
            },
            cancellationToken).ConfigureAwait(false);

        return ArchiveTombstoneView.From(released);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArchivePurgeReceipt>> PurgeExpiredAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var candidates = await tombstones.ListPurgeCandidatesAsync(cancellationToken).ConfigureAwait(false);
        var receipts = new List<ArchivePurgeReceipt>(candidates.Count);

        foreach (var candidate in candidates)
        {
            receipts.Add(await PurgeOneAsync(candidate, now, cancellationToken).ConfigureAwait(false));
        }

        return receipts;
    }

    private async Task<ArchivePurgeReceipt> PurgeOneAsync(
        ArchiveTombstone candidate,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        // The window is the delete's own promise: a tombstone whose window is still open is
        // skipped with its reason, and nothing moves for it.
        if (!candidate.IsWindowElapsed(now))
        {
            return Skip(candidate, "window_open");
        }

        var stored = await tombstones.FindAsync(candidate.ScopeId, candidate.Id, cancellationToken)
            .ConfigureAwait(false);

        if (stored is null || !stored.IsLive || !stored.EraseRequested)
        {
            return Skip(candidate, "not_eligible");
        }

        if (!stored.IsWindowElapsed(now))
        {
            return Skip(candidate, "window_open");
        }

        if (stored.HoldReason is not null)
        {
            return Skip(candidate, "held");
        }

        // Evidence under an active approval is never purge-eligible: the purge stops before
        // deleting anything, and the receipt says why.
        if (await digests.HasLiveApprovalAsync(stored.ScopeId, stored.ProjectId, cancellationToken)
            .ConfigureAwait(false))
        {
            return Skip(candidate, "approval_active");
        }

        if (await digests.HasLiveJourneyAsync(stored.ScopeId, stored.ProjectId, cancellationToken)
            .ConfigureAwait(false))
        {
            return Skip(candidate, "journey_live");
        }

        // The retirements run before the stamp, each through the delivered media store in its
        // own unit of work: a referenced object refuses with media_in_use and is retained, and
        // a retry after a partial failure retires what is left and re-reads a tombstone that
        // already says purged. Units of work never nest here for the same reason as the
        // delete: each step owns its own commit.
        var retireOwner = new OwnerScope(stored.ScopeId, Guid.Empty);
        var retired = new List<Guid>();
        var retained = new List<Guid>();

        var mediaIds = await digests.ListProjectMediaAsync(
            stored.ScopeId, stored.ProjectId, PurgeMediaLimit, cancellationToken).ConfigureAwait(false);

        foreach (var mediaId in mediaIds)
        {
            try
            {
                await media.RetireAsync(retireOwner, mediaId, cancellationToken).ConfigureAwait(false);
                retired.Add(mediaId);
            }
            catch (MediaInUseException)
            {
                retained.Add(mediaId);
            }
            catch (MediaNotFoundException)
            {
                // Moved on between the listing and the retire: already gone, not a failure.
            }
        }

        var expected = stored.Revision;
        stored.MarkPurged(now);

        var stamped = await unitOfWork.ExecuteAsync(
            async token =>
            {
                if (!await tombstones.TryUpdateAsync(stored, expected, token).ConfigureAwait(false))
                {
                    throw new RevisionConflictException(stored.Revision);
                }

                var bundles = await digests.CountProjectBundlesAsync(
                    stored.ScopeId, stored.ProjectId, token).ConfigureAwait(false);
                var assets = await digests.CountProjectAssetsAsync(
                    stored.ScopeId, stored.ProjectId, token).ConfigureAwait(false);

                audit.Record(new DomainAudit(
                    null,
                    ArchivePurgedAction,
                    stored.ProjectId,
                    stored.Revision,
                    $"project archive purged: {retired.Count} media retired, {retained.Count} retained in use"));

                return (bundles, assets);
            },
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Project {ProjectId} archive purged in scope {ScopeId}: {Retired} retired, {Retained} retained.",
            stored.ProjectId,
            stored.ScopeId,
            retired.Count,
            retained.Count);

        return new ArchivePurgeReceipt(
            stored.Id,
            stored.ProjectId,
            now,
            retired,
            retained,
            stamped.bundles,
            stamped.assets,
            Skipped: false,
            SkipReason: null);
    }

    private static ArchivePurgeReceipt Skip(ArchiveTombstone candidate, string reason) =>
        new(
            candidate.Id,
            candidate.ProjectId,
            candidate.RecoveryUntil,
            [],
            [],
            0,
            0,
            Skipped: true,
            SkipReason: reason);

    /// <summary>
    /// Revokes a live public projection through the delivered journey contract, so the anonymous
    /// routes stop serving the project. A project that was never published revokes nothing, and
    /// revoking twice is a no-op read rather than a second write.
    /// </summary>
    private async Task RevokeJourneyAsync(OwnerScope owner, Guid projectId, CancellationToken token)
    {
        JourneyView stored;
        try
        {
            stored = await journeys.ReadAsync(owner, projectId, token).ConfigureAwait(false);
        }
        catch (ResourceNotFoundException)
        {
            return;
        }

        if (!stored.Enabled || stored.RevokedAt is not null)
        {
            return;
        }

        await journeys.PublishAsync(
            owner,
            projectId,
            new JourneyPublishRequest(false, null, stored.Revision, null, null),
            token).ConfigureAwait(false);
    }

    /// <summary>
    /// Switches an enabled recurring poll off through the delivered schedule contract, so the
    /// tick fires nothing further for the project. A project with no schedule, or one already
    /// off, changes nothing; a project whose connection is already gone cannot poll either.
    /// </summary>
    private async Task DisableScheduleAsync(OwnerScope owner, Guid projectId, CancellationToken token)
    {
        var stored = await schedules.ReadAsync(owner, projectId, token).ConfigureAwait(false);
        if (stored is null || !stored.Enabled)
        {
            return;
        }

        try
        {
            await schedules.PutAsync(
                owner,
                projectId,
                new ProjectSyncScheduleRequest(false, stored.IntervalMinutes),
                stored.Revision,
                token).ConfigureAwait(false);
        }
        catch (ScheduleConnectionMissingException)
        {
            // The connection is already gone, so the tick already treats this schedule as
            // disabled: there is nothing left to poll with and nothing to switch off.
        }
    }
}
