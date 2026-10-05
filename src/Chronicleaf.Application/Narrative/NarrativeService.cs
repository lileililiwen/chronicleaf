using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Content;
using Chronicleaf.Application.Evidence;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Operations;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Application.Narrative;

/// <summary>
/// The narrative use cases and the bounded context builder (NARRATIVE-MEMORY-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Entries are structured and attributed.</b> Every entry cites the evidence item ids it is drawn
/// from, and every cited id is checked against the project's own evidence before the entry is
/// stored: a line that names material the owner never collected is refused rather than stored
/// without its source. The check is membership, not content - the service never copies evidence
/// text into an entry.
/// </para>
/// <para>
/// <b>Writes are compare-and-set, never merges.</b> An edit presents the revision it read and the
/// database advances it; a stale writer loses and is told the stored revision by the 409 the
/// middleware renders. A successful create or edit marks the project summary stale, because the
/// sources a stored body was built from have moved and no regeneration has read the new ones yet.
/// </para>
/// <para>
/// <b>Nothing here logs prose.</b> The logger receives identifiers, codes and counts. Entry text,
/// milestone notes and summary bodies are read from stored rows and written to stored rows;
/// none appears in a log line, in an exception message or in an audit detail, because all three
/// outlive the request and are readable more widely than the narrative is.
/// </para>
/// <para>
/// <b>No approval is mutated here.</b> The context builder reads live approvals to excerpt them,
/// and the refresh commits a summary; neither writes to the review package's table, so a summary
/// refresh can never approve, unapprove or invalidate a human decision.
/// </para>
/// </remarks>
public sealed class NarrativeService(
    IProjectStore projects,
    IMilestoneStore milestones,
    INarrativeStore narrative,
    IEvidenceStore evidence,
    IContentAssetStore assets,
    IContentReviewStore reviews,
    IOperationService operations,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    INarrativeSummaryBuilder summaries,
    ILogger<NarrativeService> logger) : INarrativeService, IProjectNarrativeContext
{
    /// <summary>The job kind this package owns: a summary refresh for one project.</summary>
    public const string RefreshOperationKind = "project.narrative-refresh";

    /// <summary>The audit action recorded when a narrative entry is created.</summary>
    public const string EntryCreatedAction = "narrative.entry.created";

    /// <summary>The audit action recorded when a narrative entry is edited.</summary>
    public const string EntryEditedAction = "narrative.entry.edited";

    /// <summary>The audit action recorded when a summary refresh is asked for.</summary>
    public const string RefreshRequestedAction = "narrative.refresh.requested";

    /// <summary>The audit action recorded when a refresh job committed a summary.</summary>
    public const string RefreshCommittedAction = "narrative.refresh.committed";

    /// <summary>The audit action recorded when a refresh job ended without committing.</summary>
    public const string RefreshRefusedAction = "narrative.refresh.refused";

    private const int MaxIdempotencyKeyLength = 200;

    // ==============================================================================================
    // Creating and editing entries.
    // ==============================================================================================

    /// <inheritdoc />
    public async Task<NarrativeEntryView> CreateEntryAsync(
        OwnerScope owner,
        Guid projectId,
        CreateNarrativeEntryInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(input);

        var kind = ValidateKind(input.Kind);
        var visibility = ValidateVisibility(input.Visibility);
        var text = ValidateText(input.Text);
        var occurredAt = input.OccurredAt.ToUniversalTime();
        var citations = NormalizeCitations(input.SourceEvidenceIds);

        return await unitOfWork.ExecuteAsync(
            async token =>
            {
                var project = await projects.FindAsync(owner.ScopeId, projectId, token).ConfigureAwait(false)
                    ?? throw new ResourceNotFoundException("project");

                if (project.ArchivedAt is not null)
                {
                    throw new ProjectArchivedException();
                }

                await RequireCitationsBelongAsync(owner.ScopeId, projectId, citations, token).ConfigureAwait(false);

                var entry = new NarrativeEntry(
                    Guid.NewGuid(),
                    projectId,
                    kind,
                    occurredAt,
                    text,
                    citations,
                    revision: 1,
                    visibility);

                var inserted = await narrative
                    .InsertEntryAsync(entry, owner.ScopeId, token)
                    .ConfigureAwait(false);

                if (!inserted)
                {
                    // A fresh id that already exists is a collision, not a success: the caller is
                    // told to retry rather than being handed a row it did not write.
                    throw new InternalFailureException("The narrative entry could not be recorded.");
                }

                await MarkStaleAsync(owner.ScopeId, projectId, token).ConfigureAwait(false);

                audit.Record(new DomainAudit(
                    owner.ActorId,
                    EntryCreatedAction,
                    entry.Id,
                    entry.Revision,
                    "narrative_entry_created"));

                logger.LogInformation(
                    "Narrative entry {EntryId} created for project {ProjectId} with {CitationCount} citations.",
                    entry.Id,
                    projectId,
                    citations.Count);

                return NarrativeEntryView.From(entry);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<NarrativeEntryView> PatchEntryAsync(
        OwnerScope owner,
        Guid entryId,
        long expectedRevision,
        NarrativeEntryPatch patch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(patch);

        if (expectedRevision < 1)
        {
            throw new IfMatchRequiredException("narrative entry");
        }

        return await unitOfWork.ExecuteAsync(
            async token =>
            {
                var stored = await narrative.FindEntryAsync(owner.ScopeId, entryId, token).ConfigureAwait(false)
                    ?? throw new ResourceNotFoundException("narrative entry");

                var project = await projects.FindAsync(owner.ScopeId, stored.ProjectId, token).ConfigureAwait(false)
                    ?? throw new ResourceNotFoundException("project");

                if (project.ArchivedAt is not null)
                {
                    throw new ProjectArchivedException();
                }

                if (stored.Revision != expectedRevision)
                {
                    // A stale writer loses rather than merging: a merged history would be a history
                    // nobody wrote, and the 409 names the stored revision so the caller can re-read.
                    throw new RevisionConflictException(stored.Revision);
                }

                var kind = patch.Kind is null ? stored.Kind : ValidateKind(patch.Kind);
                var visibility = patch.Visibility is null ? stored.Visibility : ValidateVisibility(patch.Visibility);
                var text = patch.Text is null ? stored.Text : ValidateText(patch.Text);
                var occurredAt = (patch.OccurredAt ?? stored.OccurredAt).ToUniversalTime();
                var citations = patch.ClearSourceEvidenceIds
                    ? []
                    : patch.SourceEvidenceIds is null
                        ? stored.SourceEvidenceIds
                        : NormalizeCitations(patch.SourceEvidenceIds);

                await RequireCitationsBelongAsync(owner.ScopeId, stored.ProjectId, citations, token)
                    .ConfigureAwait(false);

                var updated = await narrative.UpdateEntryAsync(
                    owner.ScopeId,
                    entryId,
                    expectedRevision,
                    kind,
                    occurredAt,
                    text,
                    citations,
                    visibility,
                    token).ConfigureAwait(false);

                if (updated == 0)
                {
                    // The row moved between the read and the write: another writer won the race, and
                    // this write lost it. Re-reading says whether the entry is gone or just newer.
                    var current = await narrative.FindEntryAsync(owner.ScopeId, entryId, token).ConfigureAwait(false)
                        ?? throw new ResourceNotFoundException("narrative entry");

                    throw new RevisionConflictException(current.Revision);
                }

                await MarkStaleAsync(owner.ScopeId, stored.ProjectId, token).ConfigureAwait(false);

                audit.Record(new DomainAudit(
                    owner.ActorId,
                    EntryEditedAction,
                    entryId,
                    expectedRevision + 1,
                    "narrative_entry_edited"));

                logger.LogInformation(
                    "Narrative entry {EntryId} edited at revision {Revision}.",
                    entryId,
                    expectedRevision + 1);

                return NarrativeEntryView.From(new NarrativeEntry(
                    stored.Id,
                    stored.ProjectId,
                    kind,
                    occurredAt,
                    text,
                    citations,
                    expectedRevision + 1,
                    visibility));
            },
            cancellationToken).ConfigureAwait(false);
    }

    // ==============================================================================================
    // Reading.
    // ==============================================================================================

    /// <inheritdoc />
    public async Task<NarrativeEntryPage> ListEntriesAsync(
        OwnerScope owner,
        Guid projectId,
        int? limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var bounded = ListQueryPolicy.Clamp(limit);

        var project = await projects.FindAsync(owner.ScopeId, projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("project");

        _ = project;

        var entries = await narrative
            .ListEntriesAsync(owner.ScopeId, projectId, bounded, cancellationToken)
            .ConfigureAwait(false);

        return new NarrativeEntryPage([.. entries.Select(NarrativeEntryView.From)], NextCursor: null);
    }

    /// <inheritdoc />
    public async Task<(NarrativeEntryPage Entries, ProjectSummaryView? Summary)> ReadNarrativeAsync(
        OwnerScope owner,
        Guid projectId,
        int? limit,
        CancellationToken cancellationToken = default)
    {
        var entries = await ListEntriesAsync(owner, projectId, limit, cancellationToken).ConfigureAwait(false);

        var summary = await narrative.FindSummaryAsync(owner.ScopeId, projectId, cancellationToken).ConfigureAwait(false);

        return (entries, summary is null ? null : ProjectSummaryView.From(summary));
    }

    // ==============================================================================================
    // Asking for a refresh.
    // ==============================================================================================

    /// <inheritdoc />
    public async Task<EnqueueOperationResult> RefreshSummaryAsync(
        OwnerScope owner,
        Guid projectId,
        NarrativeRefreshRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            request.IdempotencyKey.Length > MaxIdempotencyKeyLength)
        {
            throw new ValidationFailedException(
                "idempotencyKey",
                $"An idempotency key must be 1 to {MaxIdempotencyKeyLength} characters.");
        }

        var cutoff = (request.Cutoff ?? clock.GetUtcNow()).ToUniversalTime();
        var selected = request.SelectedEntryIds.Distinct().OrderBy(id => id).ToArray();

        // The reads happen first, inside one unit of work, so a request that could never run is
        // refused before a job exists. The enqueue opens its own unit of work, and the request
        // row is written in a third one after the job exists, because it names the job: nested
        // units of work are rejected, so the three are sequential rather than nested.
        var prepared = await unitOfWork.ExecuteAsync(
            async token =>
            {
                var project = await projects.FindAsync(owner.ScopeId, projectId, token).ConfigureAwait(false)
                    ?? throw new ResourceNotFoundException("project");

                if (project.ArchivedAt is not null)
                {
                    throw new ProjectArchivedException();
                }

                // Every selected private entry must be the project's own: a refresh cannot be used
                // to probe another project's history by naming its ids.
                if (selected.Length != 0)
                {
                    var known = await narrative
                        .ListEntriesAsync(owner.ScopeId, projectId, NarrativeMemoryPolicy.MaxListLimit, token)
                        .ConfigureAwait(false);

                    var knownIds = new HashSet<Guid>(known.Select(entry => entry.Id));
                    var foreign = selected.FirstOrDefault(id => !knownIds.Contains(id));
                    if (foreign != Guid.Empty)
                    {
                        throw new ResourceNotFoundException("narrative entry");
                    }
                }

                return project.Revision;
            },
            cancellationToken).ConfigureAwait(false);

        var accepted = await operations.EnqueueAsync(
            new EnqueueOperationRequest(
                owner.ScopeId,
                RefreshOperationKind,
                projectId,
                prepared,
                request.IdempotencyKey.Trim()),
            cancellationToken).ConfigureAwait(false);

        // The request row is written after the job exists, because it names the job. A replay
        // under the same key gets the original job back and its own insert changes no row, so one
        // press of the button can never grow two request rows.
        await unitOfWork.ExecuteAsync(
            async token =>
            {
                await narrative.InsertRefreshRequestAsync(
                    new NarrativeRefreshRequestRow(
                        Guid.NewGuid(),
                        owner.ScopeId,
                        accepted.Operation.Id,
                        projectId,
                        cutoff,
                        selected,
                        clock.GetUtcNow()),
                    token).ConfigureAwait(false);

                audit.Record(new DomainAudit(
                    owner.ActorId,
                    RefreshRequestedAction,
                    accepted.Operation.Id,
                    accepted.Operation.Attempt,
                    "narrative_refresh_requested"));

                return true;
            },
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Narrative refresh {OperationId} accepted for project {ProjectId}.",
            accepted.Operation.Id,
            projectId);

        return accepted;
    }

    // ==============================================================================================
    // The refresh job. Called by NarrativeRefreshHandler through the operation queue.
    // ==============================================================================================

    /// <summary>
    /// Rebuilds one project's summary for one enqueued job. Idempotent: a fingerprint that
    /// matches the stored row commits nothing, and a build failure keeps the prior row marked
    /// stale rather than deleting it or writing a body the build did not produce.
    /// </summary>
    public async Task<NarrativeRefreshOutcome> RunRefreshAsync(
        OwnerScope owner,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        return await unitOfWork.ExecuteAsync(
            async token =>
            {
                var row = await narrative
                    .FindRefreshRequestByOperationAsync(owner.ScopeId, operationId, token)
                    .ConfigureAwait(false);

                if (row is null)
                {
                    // The request row for this job is gone, so there is nothing to refresh from
                    // and no honest way to guess what cutoff and selection were authorized.
                    await RefuseAsync(owner, operationId, NarrativeError.BuildFailed, "narrative_refresh_no_request", token)
                        .ConfigureAwait(false);

                    throw new OperationPermanentException(NarrativeError.BuildFailed);
                }

                var projectId = row.ProjectId;
                var cutoff = row.Cutoff;
                var selectedEntryIds = row.SelectedEntryIds;

                var project = await projects.FindAsync(owner.ScopeId, projectId, token).ConfigureAwait(false);
                if (project is null)
                {
                    await RefuseAsync(owner, operationId, NarrativeError.ProjectGone, "narrative_refresh_no_project", token)
                        .ConfigureAwait(false);

                    throw new OperationPermanentException(NarrativeError.ProjectGone);
                }

                if (project.ArchivedAt is not null)
                {
                    await RefuseAsync(owner, operationId, NarrativeError.ProjectGone, "narrative_refresh_archived", token)
                        .ConfigureAwait(false);

                    throw new OperationPermanentException(NarrativeError.ProjectGone);
                }

                var fingerprint = await FingerprintAsync(owner.ScopeId, projectId, cutoff, selectedEntryIds, token)
                    .ConfigureAwait(false);

                var stored = await narrative.FindSummaryAsync(owner.ScopeId, projectId, token).ConfigureAwait(false);

                if (stored is not null && string.Equals(stored.InputHash, fingerprint.InputHash, StringComparison.Ordinal))
                {
                    // The sources have not moved since the stored body was built: the refresh is a
                    // re-read, not a regeneration, and the row is untouched.
                    audit.Record(new DomainAudit(
                        owner.ActorId,
                        RefreshCommittedAction,
                        projectId,
                        stored.Revision,
                        "narrative_refresh_unchanged"));

                    return new NarrativeRefreshOutcome(stored.Id, stored.Revision, Regenerated: false, stored.Stale);
                }

                NarrativeContextView context;
                try
                {
                    context = await BuildAsync(owner, projectId, cutoff, NarrativeMemoryPolicy.MaxContextBytes, selectedEntryIds, token)
                        .ConfigureAwait(false);
                }
                catch (NarrativeContextTooLargeException)
                {
                    // The context does not fit without losing a pinned fact, and the build refuses
                    // to proceed on a context that lost one. The prior summary stays readable under
                    // its stale mark rather than being replaced by a partial one.
                    await KeepStaleAsync(owner.ScopeId, projectId, stored, token).ConfigureAwait(false);
                    await RefuseAsync(owner, operationId, NarrativeError.ContextTooLarge, "narrative_refresh_context_too_large", token)
                        .ConfigureAwait(false);

                    throw new OperationPermanentException(NarrativeError.ContextTooLarge);
                }

                string body;
                try
                {
                    body = await summaries
                        .BuildAsync(context, fingerprint.Milestones, token)
                        .ConfigureAwait(false);
                }
                catch (OperationFailedException)
                {
                    // The builder already classified its own failure: keep the classification, but
                    // keep the prior summary first so the failure never deletes history.
                    await KeepStaleAsync(owner.ScopeId, projectId, stored, token).ConfigureAwait(false);
                    await RefuseAsync(owner, operationId, NarrativeError.BuildFailed, "narrative_refresh_build_failed", token)
                        .ConfigureAwait(false);

                    throw;
                }
                catch (Exception exception)
                {
                    // A build failure keeps the prior summary available labelled stale: the failure
                    // is recorded on the operation, never by deleting or overwriting the last good
                    // answer with a body the build did not produce.
                    await KeepStaleAsync(owner.ScopeId, projectId, stored, token).ConfigureAwait(false);
                    await RefuseAsync(owner, operationId, NarrativeError.BuildFailed, "narrative_refresh_build_failed", token)
                        .ConfigureAwait(false);

                    throw new OperationPermanentException(NarrativeError.BuildFailed, exception);
                }

                var now = clock.GetUtcNow();

                if (stored is null)
                {
                    var created = new ProjectSummary(
                        Guid.NewGuid(),
                        projectId,
                        context.ThroughMilestoneId,
                        fingerprint.InputHash,
                        body,
                        revision: 1,
                        stale: false,
                        now);

                    var inserted = await narrative.TryInsertSummaryAsync(created, owner.ScopeId, token).ConfigureAwait(false);
                    if (!inserted)
                    {
                        // Another refresh won the race: re-read, and accept its row when it already
                        // covers these sources rather than writing a second one.
                        var winner = await narrative.FindSummaryAsync(owner.ScopeId, projectId, token).ConfigureAwait(false);
                        if (winner is not null
                            && string.Equals(winner.InputHash, fingerprint.InputHash, StringComparison.Ordinal))
                        {
                            return new NarrativeRefreshOutcome(winner.Id, winner.Revision, Regenerated: false, winner.Stale);
                        }

                        throw new RevisionConflictException(winner?.Revision ?? 0);
                    }

                    audit.Record(new DomainAudit(
                        owner.ActorId,
                        RefreshCommittedAction,
                        projectId,
                        created.Revision,
                        "narrative_refresh_committed"));

                    logger.LogInformation(
                        "Narrative summary {SummaryId} committed for project {ProjectId}.",
                        created.Id,
                        projectId);

                    return new NarrativeRefreshOutcome(created.Id, created.Revision, Regenerated: true, Stale: false);
                }

                var updated = await narrative.UpdateSummaryAsync(
                    owner.ScopeId,
                    projectId,
                    stored.Revision,
                    context.ThroughMilestoneId,
                    fingerprint.InputHash,
                    body,
                    now,
                    token).ConfigureAwait(false);

                if (updated == 0)
                {
                    var current = await narrative.FindSummaryAsync(owner.ScopeId, projectId, token).ConfigureAwait(false);
                    if (current is not null
                        && string.Equals(current.InputHash, fingerprint.InputHash, StringComparison.Ordinal))
                    {
                        return new NarrativeRefreshOutcome(current.Id, current.Revision, Regenerated: false, current.Stale);
                    }

                    throw new RevisionConflictException(current?.Revision ?? stored.Revision);
                }

                audit.Record(new DomainAudit(
                    owner.ActorId,
                    RefreshCommittedAction,
                    projectId,
                    stored.Revision + 1,
                    "narrative_refresh_committed"));

                logger.LogInformation(
                    "Narrative summary for project {ProjectId} regenerated at revision {Revision}.",
                    projectId,
                    stored.Revision + 1);

                return new NarrativeRefreshOutcome(stored.Id, stored.Revision + 1, Regenerated: true, Stale: false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    // ==============================================================================================
    // The bounded context builder.
    // ==============================================================================================

    /// <inheritdoc />
    public async Task<NarrativeContextView> BuildAsync(
        OwnerScope owner,
        Guid projectId,
        DateTimeOffset cutoff,
        long maxBytes,
        IReadOnlyList<Guid> selectedEntryIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(selectedEntryIds);

        var normalizedCutoff = cutoff.ToUniversalTime();
        var budget = maxBytes > 0 ? maxBytes : NarrativeMemoryPolicy.MaxContextBytes;
        var selected = new HashSet<Guid>(selectedEntryIds);

        var project = await projects.FindAsync(owner.ScopeId, projectId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("project");

        _ = project;

        var entries = await narrative
            .ListEntriesAsync(owner.ScopeId, projectId, NarrativeMemoryPolicy.MaxListLimit, cancellationToken)
            .ConfigureAwait(false);

        // Confirmed history only: a candidate is something the owner has not yet said happened,
        // and a dismissed record is something they said did not. Both stay out of the context a
        // draft is built on.
        var confirmed = await milestones.ListChronologicalAsync(
            owner.ScopeId,
            projectId,
            includeCandidates: false,
            occurredAfter: null,
            idAfter: null,
            limit: NarrativeMemoryPolicy.MaxListLimit,
            cancellationToken).ConfigureAwait(false);

        // A retrospective cutoff forbids future milestones: a later milestone is excluded, never
        // summarised into a build that claims to describe the past.
        var covered = confirmed
            .Where(milestone =>
                string.Equals(milestone.Status, MilestoneStatus.Confirmed, StringComparison.Ordinal)
                && milestone.OccurredAt.ToUniversalTime() <= normalizedCutoff)
            .ToArray();

        var latestIds = new HashSet<Guid>(covered.TakeLast(NarrativeMemoryPolicy.PreferredMilestoneCount).Select(m => m.Id));

        var candidates = new List<(int Priority, DateTimeOffset At, string Ref, NarrativeContextLine Line)>();

        foreach (var entry in entries.OrderBy(e => e.OccurredAt).ThenBy(e => e.Id))
        {
            var readable = string.Equals(entry.Visibility, NarrativeVisibility.Exportable, StringComparison.Ordinal)
                || selected.Contains(entry.Id);

            if (!readable)
            {
                // A private entry without an explicit selection is absent from the context: no
                // public projection and no consented model input inherits it.
                continue;
            }

            var priority = NarrativeKinds.IsPinned(entry.Kind) ? 1
                : NarrativeKinds.IsPreferred(entry.Kind) ? 0
                : 3;

            candidates.Add((
                priority,
                entry.OccurredAt.ToUniversalTime(),
                $"entry:{entry.Id:D}",
                new NarrativeContextLine(
                    entry.Kind == NarrativeKinds.Decision ? "decisions"
                        : entry.Kind == NarrativeKinds.Feedback ? "feedback"
                        : "history",
                    $"entry:{entry.Id:D}",
                    $"[{entry.Kind}] {entry.Text} (sources: {string.Join(",", entry.SourceEvidenceIds.Select(id => id.ToString("D")))})")));
        }

        foreach (var milestone in covered)
        {
            var priority = latestIds.Contains(milestone.Id) ? 2 : 4;
            var excerpt = await ApprovedExcerptAsync(owner.ScopeId, milestone.Id, cancellationToken).ConfigureAwait(false);

            candidates.Add((
                priority,
                milestone.OccurredAt.ToUniversalTime(),
                $"milestone:{milestone.Id:D}",
                new NarrativeContextLine(
                    "history",
                    $"milestone:{milestone.Id:D}",
                    $"[milestone] {milestone.Title} ({milestone.OccurredAt:yyyy-MM-dd})"
                        + (excerpt is null ? string.Empty : $" Approved story: {excerpt}"))));
        }

        // Priority first, then chronological: preferred entries and pinned decisions are measured
        // against the budget before older milestones, so the budget protects what matters rather
        // than what arrived first.
        var ordered = candidates
            .OrderBy(c => c.Priority)
            .ThenBy(c => c.At)
            .ThenBy(c => c.Ref, StringComparer.Ordinal)
            .ToArray();

        var accepted = new List<(DateTimeOffset At, NarrativeContextLine Line)>();
        long bytes = 0;

        foreach (var candidate in ordered)
        {
            var size = Encoding.UTF8.GetByteCount(candidate.Line.Text) + 1;

            if (candidate.Priority == 1)
            {
                // Pinned decisions are never silently dropped: when even they do not fit, the
                // build is refused rather than continued on a context that lost a fact.
                if (bytes + size > budget)
                {
                    throw new NarrativeContextTooLargeException(bytes + size, budget);
                }

                accepted.Add((candidate.At, candidate.Line));
                bytes += size;
                continue;
            }

            if (bytes + size > budget)
            {
                continue;
            }

            accepted.Add((candidate.At, candidate.Line));
            bytes += size;
        }

        var chronological = accepted
            .OrderBy(item => item.At)
            .ThenBy(item => item.Line.Ref, StringComparer.Ordinal)
            .Select(item => item.Line)
            .ToArray();

        var hasFeedback = chronological.Any(line => string.Equals(line.Section, "feedback", StringComparison.Ordinal));

        var lines = chronological.ToList();
        if (!hasFeedback)
        {
            // Unknown history is labelled unknown, never inferred: a context with no feedback
            // says so rather than inventing users.
            const string unknown = "[feedback] unknown: no user feedback has been recorded.";
            var size = Encoding.UTF8.GetByteCount(unknown) + 1;
            if (bytes + size > budget)
            {
                throw new NarrativeContextTooLargeException(bytes + size, budget);
            }

            lines.Add(new NarrativeContextLine("feedback", "feedback:unknown", unknown));
            bytes += size;
        }

        var through = covered.Length == 0 ? null : (Guid?)covered[^1].Id;

        return new NarrativeContextView(
            projectId,
            normalizedCutoff,
            lines,
            bytes,
            through,
            FeedbackUnknown: !hasFeedback);
    }

    // ==============================================================================================
    // Private helpers.
    // ==============================================================================================

    private async Task<string?> ApprovedExcerptAsync(
        Guid scopeId,
        Guid milestoneId,
        CancellationToken cancellationToken)
    {
        // The excerpt names the live approval's binding - asset, revision, body hash - rather than
        // carrying approved prose into the context. The review package's table is read, never
        // written: a context build cannot approve, unapprove or invalidate anything.
        var asset = await assets
            .FindForMilestoneAsync(scopeId, milestoneId, ContentAssetTypes.MarkdownStory, cancellationToken)
            .ConfigureAwait(false);

        if (asset is null || asset.CurrentRevision < 1)
        {
            return null;
        }

        var approval = await reviews
            .FindLiveAsync(scopeId, asset.Id, asset.CurrentRevision, cancellationToken)
            .ConfigureAwait(false);

        return approval is null
            ? null
            : $"asset:{asset.Id:D}#r{asset.CurrentRevision.ToString(CultureInfo.InvariantCulture)} body:{approval.BodyHash}";
    }

    private async Task<NarrativeFingerprint> FingerprintAsync(
        Guid scopeId,
        Guid projectId,
        DateTimeOffset cutoff,
        IReadOnlyList<Guid> selectedEntryIds,
        CancellationToken cancellationToken)
    {
        var entries = await narrative
            .ListEntriesAsync(scopeId, projectId, NarrativeMemoryPolicy.MaxListLimit, cancellationToken)
            .ConfigureAwait(false);

        var confirmed = await milestones.ListChronologicalAsync(
            scopeId,
            projectId,
            includeCandidates: false,
            occurredAfter: null,
            idAfter: null,
            limit: NarrativeMemoryPolicy.MaxListLimit,
            cancellationToken).ConfigureAwait(false);

        var covered = confirmed
            .Where(milestone =>
                string.Equals(milestone.Status, MilestoneStatus.Confirmed, StringComparison.Ordinal)
                && milestone.OccurredAt.ToUniversalTime() <= cutoff.ToUniversalTime())
            .ToArray();

        var lines = new List<string> { "narrative-fingerprint/1", $"cutoff:{cutoff.ToUniversalTime():O}" };
        var sources = new List<MilestoneNarrativeSource>();

        foreach (var milestone in covered.OrderBy(m => m.OccurredAt).ThenBy(m => m.Id))
        {
            lines.Add($"milestone:{milestone.Id:D}:{milestone.Revision.ToString(CultureInfo.InvariantCulture)}:{milestone.OccurredAt.ToUniversalTime():O}");

            var head = await evidence.FindHeadAsync(scopeId, milestone.Id, cancellationToken).ConfigureAwait(false);
            lines.Add($"evidence:{milestone.Id:D}:{(head?.Revision ?? 0).ToString(CultureInfo.InvariantCulture)}");

            var excerpt = await ApprovedExcerptAsync(scopeId, milestone.Id, cancellationToken).ConfigureAwait(false);
            if (excerpt is not null)
            {
                lines.Add($"approved:{milestone.Id:D}:{excerpt}");
            }

            sources.Add(new MilestoneNarrativeSource(
                milestone.Id,
                milestone.Title,
                milestone.OccurredAt,
                milestone.Type,
                milestone.Revision,
                excerpt));
        }

        foreach (var entry in entries.OrderBy(e => e.Id))
        {
            var textHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Text))).ToLowerInvariant();
            lines.Add($"entry:{entry.Id:D}:{entry.Revision.ToString(CultureInfo.InvariantCulture)}:{entry.Kind}:{entry.OccurredAt.ToUniversalTime():O}:{textHash}:{entry.Visibility}:{string.Join(",", entry.SourceEvidenceIds.Select(id => id.ToString("D")))}");
        }

        lines.Add($"selected:{string.Join(",", selectedEntryIds.Distinct().OrderBy(id => id).Select(id => id.ToString("D")))}");

        var inputHash = Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines))))
            .ToLowerInvariant();

        return new NarrativeFingerprint(inputHash, sources);
    }

    private async Task KeepStaleAsync(
        Guid scopeId,
        Guid projectId,
        ProjectSummary? stored,
        CancellationToken cancellationToken)
    {
        if (stored is null)
        {
            return;
        }

        await narrative.MarkSummaryStaleAsync(scopeId, projectId, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    private Task RefuseAsync(
        OwnerScope owner,
        Guid operationId,
        string code,
        string detail,
        CancellationToken cancellationToken)
    {
        audit.Record(new DomainAudit(owner.ActorId, RefreshRefusedAction, operationId, 0, detail));

        logger.LogInformation("Narrative refresh {OperationId} refused: {Code}.", operationId, code);

        return Task.CompletedTask;
    }

    private async Task MarkStaleAsync(Guid scopeId, Guid projectId, CancellationToken cancellationToken)
    {
        await narrative.MarkSummaryStaleAsync(scopeId, projectId, clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    private async Task RequireCitationsBelongAsync(
        Guid scopeId,
        Guid projectId,
        IReadOnlyList<Guid> citations,
        CancellationToken cancellationToken)
    {
        if (citations.Count == 0)
        {
            return;
        }

        var belong = await narrative
            .CitationsBelongToProjectAsync(scopeId, projectId, citations, cancellationToken)
            .ConfigureAwait(false);

        if (!belong)
        {
            // The position is reported; the id is not echoed, because a citation check must never
            // become a probe an owner can use to learn which ids exist elsewhere.
            throw new NarrativeForeignEvidenceException(position: 0);
        }
    }

    private static string ValidateKind(string? kind)
    {
        if (!NarrativeKinds.IsKnown(kind))
        {
            throw new ValidationFailedException("kind", "The kind is one of origin, audience, technical-route, decision, problem or feedback.");
        }

        return kind!;
    }

    private static string ValidateVisibility(string? visibility)
    {
        if (!NarrativeVisibility.IsKnown(visibility))
        {
            throw new ValidationFailedException("visibility", "The visibility is private or exportable.");
        }

        return visibility!;
    }

    private static string ValidateText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ValidationFailedException("text", "An entry carries the owner's own words.");
        }

        var trimmed = text.Trim();
        if (trimmed.Length > NarrativeEntry.TextMaxLength)
        {
            throw new ValidationFailedException("text", $"An entry is at most {NarrativeEntry.TextMaxLength} characters.");
        }

        return trimmed;
    }

    private static IReadOnlyList<Guid> NormalizeCitations(IReadOnlyList<Guid>? citations)
    {
        if (citations is null || citations.Count == 0)
        {
            return [];
        }

        if (citations.Any(id => id == Guid.Empty))
        {
            throw new ValidationFailedException("sourceEvidenceIds", "Every cited evidence id must be non-empty.");
        }

        if (citations.Count > NarrativeMemoryPolicy.MaxListLimit)
        {
            throw new ValidationFailedException("sourceEvidenceIds", $"An entry cites at most {NarrativeMemoryPolicy.MaxListLimit} evidence items.");
        }

        return [.. citations.Distinct().OrderBy(id => id)];
    }

    private sealed record NarrativeFingerprint(string InputHash, IReadOnlyList<MilestoneNarrativeSource> Milestones);
}

/// <summary>What one refresh run concluded.</summary>
/// <param name="Regenerated">False when the stored body already covered the sources.</param>
/// <param name="Stale">The stored row's stale mark after this run.</param>
public sealed record NarrativeRefreshOutcome(Guid SummaryId, long Revision, bool Regenerated, bool Stale);
