using Chronicleaf.Application.Common;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;

namespace Chronicleaf.Application.Evidence;

/// <summary>
/// The evidence use cases over real PostgreSQL (MILESTONE-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// <para><b>Append-only.</b> Nothing in this file updates or deletes a bundle or an item, and the
/// database refuses both outright. A new bundle is written at <c>expectedRevision + 1</c> and every
/// earlier bundle keeps its own rows, so a story built on revision 3 stays readable after revision 9
/// exists. A bundle is a complete snapshot rather than a delta: the items the milestone already had
/// are carried into the new revision together with what this append added, which is why adding one
/// limitation cannot cost the story a commit.</para>
///
/// <para><b>Idempotence.</b> A fact is identified by its milestone, kind, source, source revision and
/// payload hash. If the head bundle already holds every requested fact, the append returns that
/// bundle and no revision is created - so a collector that re-runs after a crash, or an owner who
/// presses save twice, does not get a second copy of the same statement. The identity is also
/// computed canonically, so the same facts collected in a different order hash the same way.</para>
///
/// <para><b>Concurrency.</b> The write is a compare-and-set on the revision the caller presented, and
/// the decision belongs to a unique index rather than to a read this file took earlier. Two appends
/// presenting the same revision therefore produce one committed bundle; the loser re-reads, finds its
/// facts already recorded and returns the winner's bundle, or is told the real revision in a 409.
/// Either way nothing is merged and nothing is lost.</para>
///
/// <para><b>Honesty.</b> An unavailable source is stored as unavailable with the reason it could not
/// be read, and the database refuses an unavailable item that claims a payload or points at media.
/// Nothing here upgrades availability: the caller states it, and a gap stays a gap however often it
/// is appended.</para>
///
/// <para><b>Scope.</b> The scope and actor arrive in <see cref="OwnerScope"/> from the validated
/// session. The milestone, the project and every referenced media id are resolved inside that scope
/// and each of them answers the same 404 when it is absent or foreign, so an append cannot be used
/// to find out what another installation holds.</para>
/// </remarks>
public sealed class MilestoneEvidenceService(
    IEvidenceStore evidence,
    IMilestoneStore milestones,
    IProjectStore projects,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    TimeProvider clock) : IAppendEvidence, IReadEvidence
{
    /// <summary>The audit action recorded when source material is appended.</summary>
    public const string EvidenceAppendedAction = "evidence.appended";

    /// <summary>The audit action recorded when the owner appends evidence by hand.</summary>
    public const string ManualEvidenceAppendedAction = "evidence.manual_appended";

    /// <summary>The outbox kind published with every committed bundle.</summary>
    public const string EvidenceChangedKind = "evidence.changed";

    /// <summary>The source reference every owner-written item carries.</summary>
    public const string ManualSourceRef = "owner";

    public async Task<EvidencePage> ListAsync(
        OwnerScope owner,
        Guid milestoneId,
        PageRequest page,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(page);

        var limit = MilestonePolicy.ClampPageSize(page.Limit);
        var (beforeRevision, beforeId) = page.Cursor is null
            ? ((long?)null, (Guid?)null)
            : EvidenceCursor.Decode(page.Cursor, owner.ScopeId);

        // Resolved before reading, so a milestone this owner may not reach is a 404 rather than an
        // empty list that looks like a milestone with no evidence.
        await LiveMilestoneAsync(owner, milestoneId, cancellationToken).ConfigureAwait(false);

        // One row beyond the page, so a next cursor is only handed out when there is another bundle.
        var rows = await evidence
            .ListAsync(owner.ScopeId, milestoneId, beforeRevision, beforeId, limit + 1, cancellationToken)
            .ConfigureAwait(false);

        var items = rows.Take(limit).ToArray();
        var last = items.Length == limit ? items[^1] : null;

        return new EvidencePage(
            items,
            last is null ? null : EvidenceCursor.Encode(owner.ScopeId, last.Revision, last.Id));
    }

    public Task<EvidenceBundle> AppendAsync(
        OwnerScope owner,
        Guid milestoneId,
        long expectedRevision,
        IReadOnlyList<EvidenceAppendItem> items,
        CancellationToken cancellationToken = default) =>
        AppendCoreAsync(owner, milestoneId, expectedRevision, items, EvidenceAppendedAction, cancellationToken);

    public Task<EvidenceBundle> AppendManualAsync(
        OwnerScope owner,
        Guid milestoneId,
        ManualEvidenceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        Throw(ValidateManual(request));
        RequireRevision(request.ExpectedRevision);

        var items = ManualItems(request, clock.GetUtcNow());
        return AppendCoreAsync(owner, milestoneId, request.ExpectedRevision, items, ManualEvidenceAppendedAction, cancellationToken);
    }

    // -----------------------------------------------------------------------------------------
    // Manual input.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Turns what the owner wrote into the items to append. Every item's source revision is the
    /// revision the owner was looking at, so a re-sent identical statement is recognised as the same
    /// one and a reader can see which evidence state the owner was answering.
    /// </summary>
    private static List<EvidenceAppendItem> ManualItems(ManualEvidenceRequest request, DateTimeOffset capturedAt)
    {
        var items = new List<EvidenceAppendItem>(1 + request.KnownLimitations.Count + request.NextSteps.Count);
        var sourceRevision = request.ExpectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (!string.IsNullOrWhiteSpace(request.Notes))
        {
            items.Add(ManualItem(EvidenceKinds.Manual, request.Notes.Trim(), sourceRevision, capturedAt));
        }

        foreach (var limitation in request.KnownLimitations)
        {
            items.Add(ManualItem(EvidenceKinds.Limitations, limitation.Trim(), sourceRevision, capturedAt));
        }

        foreach (var nextStep in request.NextSteps)
        {
            items.Add(ManualItem(EvidenceKinds.NextSteps, nextStep.Trim(), sourceRevision, capturedAt));
        }

        return items;
    }

    private static EvidenceAppendItem ManualItem(string kind, string text, string sourceRevision, DateTimeOffset capturedAt)
    {
        var item = new EvidenceAppendItem(
            kind,
            ManualSourceRef,
            sourceRevision,
            capturedAt,
            string.Empty,
            text,
            mediaId: null,
            redactions: null,
            availability: EvidenceAvailability.Complete);

        // The hash is computed here from the canonical form rather than supplied, so an owner-written
        // item hashes the same way whoever appends it and re-sending the same statement is a no-op.
        return new EvidenceAppendItem(
            kind,
            ManualSourceRef,
            sourceRevision,
            capturedAt,
            EvidencePolicy.ItemPayloadSha256(item),
            item.Text,
            item.MediaId,
            item.Redactions,
            item.Availability);
    }

    private static Dictionary<string, string[]> ValidateManual(ManualEvidenceRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var notes = request.Notes?.Trim() ?? string.Empty;
        if (notes.Length > Milestone.NotesMaxLength)
        {
            errors["notes"] = [$"At most {Milestone.NotesMaxLength} characters."];
        }

        AddListErrors(request.KnownLimitations, "knownLimitations", errors);
        AddListErrors(request.NextSteps, "nextSteps", errors);

        if (notes.Length == 0 &&
            request.KnownLimitations.All(string.IsNullOrWhiteSpace) &&
            request.NextSteps.All(string.IsNullOrWhiteSpace))
        {
            // Committing a revision here would advance the milestone's evidence and add nothing to it.
            // A caller that believes it recorded something has to be told it recorded nothing.
            errors["notes"] = ["At least one of notes, knownLimitations or nextSteps must carry something."];
        }

        return errors;
    }

    private static void AddListErrors(
        IReadOnlyList<string> values,
        string field,
        Dictionary<string, string[]> errors)
    {
        var written = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();

        if (written.Length > ManualEvidenceRequest.MaxListItems)
        {
            errors[field] = [$"At most {ManualEvidenceRequest.MaxListItems} entries."];
        }

        if (written.Any(value => value.Trim().Length > Milestone.ListItemMaxLength))
        {
            errors[field] = [$"Each entry must be at most {Milestone.ListItemMaxLength} characters."];
        }
    }

    // -----------------------------------------------------------------------------------------
    // The append itself.
    // -----------------------------------------------------------------------------------------

    private async Task<EvidenceBundle> AppendCoreAsync(
        OwnerScope owner,
        Guid milestoneId,
        long expectedRevision,
        IReadOnlyList<EvidenceAppendItem> items,
        string action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);

        Throw(EvidencePolicy.Validate(items, ownerWritableOnly: false));
        RequireRevision(expectedRevision);

        return await unitOfWork.ExecuteAsync(
            async token =>
            {
                var milestone = await LiveMilestoneAsync(owner, milestoneId, token).ConfigureAwait(false);

                await RequireReferenceableMediaAsync(owner, milestone.ProjectId, items, token).ConfigureAwait(false);

                var head = await evidence.FindHeadAsync(owner.ScopeId, milestoneId, token).ConfigureAwait(false);
                RequireCurrent(expectedRevision, head?.Revision ?? 0);

                if (HeadAlreadyHolds(head, items))
                {
                    // The facts are recorded. No new revision, no new bundle, nothing to audit: this
                    // call changed nothing, and saying otherwise would make a repeated collection look
                    // like new evidence.
                    return head!;
                }

                var bundleId = Guid.NewGuid();
                var createdAt = clock.GetUtcNow();
                var stored = Assemble(bundleId, milestoneId, expectedRevision, createdAt, head, items);

                // The bounds are on the resulting snapshot, not on this append, because the snapshot
                // is what a reader receives.
                EvidencePolicy.ValidateBundleBounds(stored);

                var bundle = new EvidenceBundle(bundleId, milestoneId, expectedRevision + 1, createdAt, stored);

                var committed = await evidence.TryInsertBundleAsync(
                    owner.ScopeId,
                    milestoneId,
                    bundleId,
                    expectedRevision,
                    createdAt,
                    token).ConfigureAwait(false);

                if (committed == EvidenceCommitStatus.RevisionTaken)
                {
                    return await LostAppendAsync(owner, milestoneId, items, token).ConfigureAwait(false);
                }

                await evidence
                    .InsertItemsAsync(owner.ScopeId, bundleId, stored, token)
                    .ConfigureAwait(false);

                await evidence
                    .TryAppendOutboxAsync(owner.ScopeId, milestoneId, bundle.Revision, createdAt, token)
                    .ConfigureAwait(false);

                audit.Record(new DomainAudit(
                    owner.ActorId,
                    action,
                    bundleId,
                    bundle.Revision,
                    // Ids, an action and the revision. Never the collected text, never a source
                    // payload, never media bytes: the audit log says what moved, not what it said.
                    "evidence_bundle_appended"));

                return bundle;
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The resulting snapshot: everything the head bundle held, carried into the new bundle, plus the
    /// new items. Carrying rather than referencing keeps each bundle self-contained, which is what
    /// lets a reader take one revision and be sure it holds the whole story at that point.
    /// </summary>
    private static List<EvidenceItem> Assemble(
        Guid bundleId,
        Guid milestoneId,
        long expectedRevision,
        DateTimeOffset createdAt,
        EvidenceBundle? head,
        IReadOnlyList<EvidenceAppendItem> appended)
    {
        var items = new List<EvidenceItem>((head?.Items.Count ?? 0) + appended.Count);

        foreach (var item in head?.Items ?? [])
        {
            // A new row with the same content, carrying the correction link forward. The earlier
            // bundle keeps its own copy, so a correction made now cannot change what revision 3 said.
            items.Add(new EvidenceItem(
                Guid.NewGuid(),
                bundleId,
                milestoneId,
                item.Kind,
                item.SourceRef,
                item.SourceRevision,
                item.CapturedAt,
                item.PayloadSha256,
                item.Text,
                item.MediaId,
                item.Redactions,
                item.Availability,
                item.SupersedesId));
        }

        var knownSupersessions = new HashSet<Guid>(
            (head?.Items ?? []).Select(item => item.Id));

        foreach (var item in appended)
        {
            if (item.SupersedesId is { } supersedes && !knownSupersessions.Contains(supersedes))
            {
                // A correction has to correct something real. Accepting an id that is not in the
                // current snapshot would let a caller record a retraction of a fact nobody holds.
                throw new ValidationFailedException(
                    "items[].supersedesId",
                    "The superseded item is not part of this milestone's current evidence.");
            }

            items.Add(new EvidenceItem(
                Guid.NewGuid(),
                bundleId,
                milestoneId,
                item.Kind,
                item.SourceRef,
                item.SourceRevision,
                item.CapturedAt,
                item.PayloadSha256,
                item.Text,
                item.MediaId,
                item.Redactions,
                item.Availability,
                item.SupersedesId));
        }

        // Stable id order - by the id's text, which is the same order the store reads them back in,
        // so a bundle returned by an append and the same bundle returned by a later read are
        // identical rather than merely equal.
        items.Sort(static (left, right) =>
            string.CompareOrdinal(left.Id.ToString("D"), right.Id.ToString("D")));

        return items;
    }

    /// <summary>
    /// True when the head bundle already records every requested fact. A fact is identified by its
    /// milestone, kind, source, source revision and payload hash - the same tuple for both sides, so
    /// a comparison here and a unique index in the database are asking the same question.
    /// </summary>
    private static bool HeadAlreadyHolds(EvidenceBundle? head, IReadOnlyList<EvidenceAppendItem> items)
    {
        if (head is null)
        {
            return false;
        }

        var held = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in head.Items)
        {
            held.Add(FactKey(head.MilestoneId, item.Kind, item.SourceRef, item.SourceRevision, item.PayloadSha256));
        }

        return items.All(item => held.Contains(
            FactKey(head.MilestoneId, item.Kind, item.SourceRef, item.SourceRevision, item.PayloadSha256)));
    }

    private static string FactKey(Guid milestoneId, string kind, string sourceRef, string sourceRevision, string payloadSha256) =>
        EvidenceValues.ItemKey(milestoneId, kind, sourceRef, sourceRevision, payloadSha256);

    /// <summary>
    /// Works out what an append that committed no bundle deserves. The revision may have moved, or
    /// the same facts may have landed first - and those two are different answers, so the head is
    /// re-read rather than guessed at.
    /// </summary>
    private async Task<EvidenceBundle> LostAppendAsync(
        OwnerScope owner,
        Guid milestoneId,
        IReadOnlyList<EvidenceAppendItem> items,
        CancellationToken token)
    {
        var head = await evidence.FindHeadAsync(owner.ScopeId, milestoneId, token).ConfigureAwait(false);

        if (HeadAlreadyHolds(head, items))
        {
            // Somebody committed the identical facts a moment before this one. Their bundle already
            // says it; this append has nothing left to add, and reporting a conflict would make a
            // caller retry something that is already true.
            return head!;
        }

        if (head is null)
        {
            throw NotFound("milestone");
        }

        // The revision moved, or the same row was refused for a reason other than a race. Re-reading
        // the milestone distinguishes an archived project - which is a conflict with the record's own
        // state - from a plain lost race, so the answer names what actually happened.
        await LiveMilestoneAsync(owner, milestoneId, token).ConfigureAwait(false);

        throw new RevisionConflictException(head.Revision);
    }

    // -----------------------------------------------------------------------------------------
    // Scope, media and shared rules.
    // -----------------------------------------------------------------------------------------

    private async Task<Milestone> LiveMilestoneAsync(OwnerScope owner, Guid milestoneId, CancellationToken token)
    {
        var milestone = await milestones.FindAsync(owner.ScopeId, milestoneId, token).ConfigureAwait(false)
            ?? throw NotFound("milestone");

        var project = await projects.FindAsync(owner.ScopeId, milestone.ProjectId, token).ConfigureAwait(false)
            ?? throw NotFound("project");

        if (project.IsArchived)
        {
            // An archived project's history stays readable, but its evidence stops moving. Refusing
            // here rather than in a route means a collector is refused too.
            throw new ProjectArchivedException();
        }

        return milestone;
    }

    /// <summary>
    /// Resolves every media id this append references. One unresolvable id fails the whole append
    /// before anything is written, so a bundle never holds a reference to media the caller may not
    /// see - and a failed append leaves no bundle behind, which is what lets a caller retry with a
    /// corrected id.
    /// </summary>
    private async Task RequireReferenceableMediaAsync(
        OwnerScope owner,
        Guid projectId,
        IReadOnlyList<EvidenceAppendItem> items,
        CancellationToken token)
    {
        var mediaIds = items
            .Where(item => item.MediaId is not null)
            .Select(item => item.MediaId!.Value)
            .Distinct()
            .ToArray();

        if (mediaIds.Length == 0)
        {
            return;
        }

        var resolved = await evidence
            .ResolveMediaAsync(owner.ScopeId, projectId, mediaIds, token)
            .ConfigureAwait(false);

        var refused = resolved.Where(reference => !reference.IsReferenceable).ToArray();

        if (refused.Length > 0)
        {
            // The same 404 as an absent id, an id in another installation and an id in another project
            // inside this installation. One answer for all of them, so this append cannot be used to
            // ask which media exist elsewhere.
            throw NotFound("media");
        }
    }

    /// <summary>
    /// Checks the presented revision. Zero is a real revision here: it is what a milestone with no
    /// evidence reports, and the append that presents it creates revision 1. Only a negative value -
    /// which no evidence has ever had - is refused before the store is touched.
    /// </summary>
    private static void RequireRevision(long expectedRevision)
    {
        if (expectedRevision < 0)
        {
            throw new ValidationFailedException("If-Match", "the revision must be a non-negative integer.");
        }
    }

    private static void RequireCurrent(long expectedRevision, long storedRevision)
    {
        if (expectedRevision != storedRevision)
        {
            // The newer bundle is kept and the stale append is discarded. Nothing in this path merges
            // one revision's items into another's.
            throw new RevisionConflictException(storedRevision);
        }
    }

    private static void Throw(IReadOnlyDictionary<string, string[]> errors)
    {
        if (errors.Count > 0)
        {
            throw new ValidationFailedException(errors);
        }
    }

    private static ResourceNotFoundException NotFound(string resource) => new(resource);
}
