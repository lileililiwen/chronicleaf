using System.Globalization;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Evidence;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Application.Curation;

/// <summary>
/// The screenshot curation service: what an owner may choose about captured pictures, and what
/// that choice publishes into evidence (SCREENSHOT-CURATION-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>One transaction for both stores.</b> A curation touches the candidate row and, when the
/// change moves the selection, the evidence bundle. Both writes happen inside one unit of work,
/// so a lost bundle race rolls the candidate change back with it: the row never says selected
/// for a revision the bundle does not hold, and the bundle never holds a picture the row does
/// not show.
/// </para>
/// <para>
/// <b>Selection is publication, deselection is a new revision.</b> Entering selected appends a
/// screenshot item that references the picture's media and caption. Leaving selected appends a
/// new bundle revision that no longer carries that picture's items. Earlier bundles keep their
/// own rows - the database refuses the rewrite outright - so content approved against an earlier
/// revision still reads what it was approved against.
/// </para>
/// <para>
/// <b>What this class will not do.</b> It does not capture, and a capture re-run never moves the
/// columns this class owns. It does not approve or export anything: a selected picture is
/// evidence a human may act on, and approval is the review package's.
/// </para>
/// </remarks>
public sealed class ScreenshotCurationService(
    IProjectStore projects,
    IMilestoneStore milestones,
    IScreenshotCurationStore curation,
    IEvidenceStore evidence,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<ScreenshotCurationService> logger) : IScreenshotCurationService
{
    /// <summary>The audit action recorded when a picture is selected.</summary>
    public const string ScreenshotSelectedAction = "screenshot.selected";

    /// <summary>The audit action recorded when a picture leaves the selected set.</summary>
    public const string ScreenshotDeselectedAction = "screenshot.deselected";

    /// <summary>The audit action recorded when a caption or the primary flag changes without moving the selection.</summary>
    public const string ScreenshotEditedAction = "screenshot.edited";

    /// <summary>The audit action recorded when the selected pictures are reordered.</summary>
    public const string ScreenshotsOrderedAction = "screenshots.ordered";

    /// <inheritdoc />
    public async Task<IReadOnlyList<CuratedScreenshot>> ListAsync(
        OwnerScope owner,
        Guid milestoneId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        // The milestone is read first and scoped, so a milestone in another installation is a 404
        // rather than an empty list that happens to look like "nothing captured yet".
        _ = await milestones.FindAsync(owner.ScopeId, milestoneId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("milestone");

        return await curation
            .ListAsync(owner.ScopeId, milestoneId, ListQueryPolicy.MaxPageSize, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<CuratedScreenshotOutcome> CurateAsync(
        OwnerScope owner,
        Guid screenshotId,
        ScreenshotCurateRequest request,
        CancellationToken cancellationToken = default) =>
        unitOfWork.ExecuteAsync(
            token => CurateCoreAsync(owner, screenshotId, request, token),
            cancellationToken);

    /// <inheritdoc />
    public Task<ScreenshotOrderOutcome> OrderAsync(
        OwnerScope owner,
        Guid milestoneId,
        ScreenshotOrderRequest request,
        CancellationToken cancellationToken = default) =>
        unitOfWork.ExecuteAsync(
            token => OrderCoreAsync(owner, milestoneId, request, token),
            cancellationToken);

    // -----------------------------------------------------------------------------------------
    // Curating one picture.
    // -----------------------------------------------------------------------------------------

    private async Task<CuratedScreenshotOutcome> CurateCoreAsync(
        OwnerScope owner,
        Guid screenshotId,
        ScreenshotCurateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        RequireRevisions(request.ExpectedRevision, request.ExpectedBundleRevision);

        var milestoneId = await LockedMilestoneAsync(owner, screenshotId, cancellationToken).ConfigureAwait(false);

        var current = await curation
            .FindAsync(owner.ScopeId, screenshotId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("screenshot");

        var desired = ResolveDesired(current, request);

        if (IsUnchanged(current, desired))
        {
            // Nothing to change and nothing to publish. A repeated select with the same caption
            // is not a new revision and not a conflict: it changed nothing, and saying otherwise
            // would make a retried request look like new evidence.
            var head = await evidence
                .FindHeadAsync(owner.ScopeId, current.MilestoneId, cancellationToken)
                .ConfigureAwait(false);

            return new CuratedScreenshotOutcome(current, head?.Revision ?? 0);
        }

        var makePrimary = desired.IsPrimary && !current.IsPrimary;

        if (makePrimary)
        {
            // The old primary clears before the new one is set, in the same transaction: the
            // one-primary index would refuse the update while the old row still claims the flag,
            // so clearing first is what makes the switch atomic rather than impossible.
            await curation
                .ClearOtherPrimariesAsync(owner.ScopeId, current.MilestoneId, screenshotId, cancellationToken)
                .ConfigureAwait(false);
        }

        var updated = await curation.TryUpdateAsync(
            owner.ScopeId,
            screenshotId,
            request.ExpectedRevision,
            desired.Status,
            desired.Caption,
            desired.IsPrimary,
            cancellationToken).ConfigureAwait(false);

        if (!updated)
        {
            throw new RevisionConflictException(
                (await curation.FindAsync(owner.ScopeId, screenshotId, cancellationToken).ConfigureAwait(false))
                ?.Revision ?? request.ExpectedRevision);
        }

        var outcome = new CuratedScreenshotOutcome(
            await curation.FindAsync(owner.ScopeId, screenshotId, cancellationToken).ConfigureAwait(false)
                ?? throw new ResourceNotFoundException("screenshot"),
            0);

        // The evidence effect of the transition, if any. A primary flip or a caption on an
        // unselected picture moves no evidence: the bundle holds selected pictures, not flags.
        if (!current.IsSelected && desired.IsSelected)
        {
            var bundle = await AppendSelectionAsync(
                owner,
                current,
                desired.Caption,
                desired.Revision,
                supersedesId: null,
                request.ExpectedBundleRevision,
                ScreenshotSelectedAction,
                cancellationToken).ConfigureAwait(false);

            return outcome with { BundleRevision = bundle.Revision };
        }

        if (current.IsSelected && !desired.IsSelected)
        {
            var bundle = await RemoveSelectionAsync(
                owner,
                current,
                request.ExpectedBundleRevision,
                cancellationToken).ConfigureAwait(false);

            // A null bundle means the head already held nothing for this picture, so deselecting
            // it is a row change only: the revision reported is the head this call read.
            var bundleRevision = bundle?.Revision
                ?? await HeadRevisionAsync(owner, current.MilestoneId, cancellationToken).ConfigureAwait(false);

            return outcome with { BundleRevision = bundleRevision };
        }

        if (current.IsSelected && desired.IsSelected && !string.Equals(current.Caption, desired.Caption, StringComparison.Ordinal))
        {
            var bundle = await AppendSelectionAsync(
                owner,
                current,
                desired.Caption,
                desired.Revision,
                await TipItemIdAsync(owner, current, cancellationToken).ConfigureAwait(false),
                request.ExpectedBundleRevision,
                ScreenshotEditedAction,
                cancellationToken).ConfigureAwait(false);

            return outcome with { BundleRevision = bundle.Revision };
        }

        audit.Record(new DomainAudit(
            owner.ActorId,
            ScreenshotEditedAction,
            screenshotId,
            desired.Revision,
            "screenshot_curated"));

        logger.LogInformation(
            "Screenshot {ScreenshotId} curated for milestone {MilestoneId} without an evidence change.",
            screenshotId,
            milestoneId);

        return outcome with { BundleRevision = await HeadRevisionAsync(owner, current.MilestoneId, cancellationToken).ConfigureAwait(false) };
    }

    // -----------------------------------------------------------------------------------------
    // Ordering the selected pictures.
    // -----------------------------------------------------------------------------------------

    private async Task<ScreenshotOrderOutcome> OrderCoreAsync(
        OwnerScope owner,
        Guid milestoneId,
        ScreenshotOrderRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        if (request.ExpectedBundleRevision < 0)
        {
            throw new ValidationFailedException(
                "expectedBundleRevision",
                "The expected bundle revision is zero or greater.");
        }

        if (!await curation.LockMilestoneAsync(owner.ScopeId, milestoneId, cancellationToken).ConfigureAwait(false))
        {
            throw new ResourceNotFoundException("milestone");
        }

        await LiveMilestoneAsync(owner, milestoneId, cancellationToken).ConfigureAwait(false);

        var head = await evidence
            .FindHeadAsync(owner.ScopeId, milestoneId, cancellationToken)
            .ConfigureAwait(false);

        if ((head?.Revision ?? 0) != request.ExpectedBundleRevision)
        {
            throw new RevisionConflictException(head?.Revision ?? 0);
        }

        var pictures = await curation
            .ListAsync(owner.ScopeId, milestoneId, ListQueryPolicy.MaxPageSize, cancellationToken)
            .ConfigureAwait(false);

        var selected = pictures.Where(picture => picture.IsSelected).ToArray();

        RequireExactOrder(request.Ids, selected);

        var positions = request.Ids
            .Select((id, index) => (Id: id, Position: index))
            .ToDictionary(entry => entry.Id, entry => entry.Position);

        foreach (var picture in selected)
        {
            var stored = await curation.TryOrderAsync(
                owner.ScopeId,
                picture.Id,
                picture.Revision,
                positions[picture.Id],
                cancellationToken).ConfigureAwait(false);

            if (!stored)
            {
                // The row moved under this order - another curation committed first. The order is
                // refused rather than partially applied, so a retried order never leaves half a
                // sequence behind.
                var reread = await curation
                    .FindAsync(owner.ScopeId, picture.Id, cancellationToken)
                    .ConfigureAwait(false);

                throw new RevisionConflictException(reread?.Revision ?? picture.Revision);
            }
        }

        if (selected.Length > 0)
        {
            audit.Record(new DomainAudit(
                owner.ActorId,
                ScreenshotsOrderedAction,
                milestoneId,
                request.ExpectedBundleRevision,
                "screenshots_ordered"));
        }

        var ordered = await curation
            .ListAsync(owner.ScopeId, milestoneId, ListQueryPolicy.MaxPageSize, cancellationToken)
            .ConfigureAwait(false);

        logger.LogInformation(
            "Screenshots ordered for milestone {MilestoneId}: {Count} selected pictures sequenced.",
            milestoneId,
            selected.Length);

        return new ScreenshotOrderOutcome(
            [.. ordered.Where(picture => picture.IsSelected).OrderBy(picture => picture.DisplayOrder).ThenBy(picture => picture.CreatedAt)],
            head?.Revision ?? 0);
    }

    // -----------------------------------------------------------------------------------------
    // Selection evidence.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Publishes one picture's selection as a screenshot evidence item at the next bundle revision.
    /// </summary>
    private async Task<EvidenceBundle> AppendSelectionAsync(
        OwnerScope owner,
        CuratedScreenshot current,
        string caption,
        long newScreenshotRevision,
        Guid? supersedesId,
        long expectedBundleRevision,
        string action,
        CancellationToken cancellationToken)
    {
        var item = SelectionItem(current, caption, newScreenshotRevision, supersedesId);

        Throw(EvidencePolicy.Validate([item], ownerWritableOnly: false));

        await RequireReferenceableMediaAsync(owner, current, item, cancellationToken).ConfigureAwait(false);

        var head = await evidence
            .FindHeadAsync(owner.ScopeId, current.MilestoneId, cancellationToken)
            .ConfigureAwait(false);

        RequireCurrent(expectedBundleRevision, head?.Revision ?? 0);

        if (head is not null && HeadAlreadyHolds(head, [item]))
        {
            return head;
        }

        var bundleId = Guid.NewGuid();
        var createdAt = clock.GetUtcNow();
        var stored = Assemble(bundleId, current.MilestoneId, head, [item], omitSourceRef: null);

        EvidencePolicy.ValidateBundleBounds(stored);

        var bundle = new EvidenceBundle(bundleId, current.MilestoneId, expectedBundleRevision + 1, createdAt, stored);

        if (await evidence.TryInsertBundleAsync(
                owner.ScopeId,
                current.MilestoneId,
                bundleId,
                expectedBundleRevision,
                createdAt,
                cancellationToken).ConfigureAwait(false) == EvidenceCommitStatus.RevisionTaken)
        {
            return await LostSelectionAppendAsync(owner, current, [item], cancellationToken).ConfigureAwait(false);
        }

        await evidence.InsertItemsAsync(owner.ScopeId, bundleId, stored, cancellationToken).ConfigureAwait(false);
        await evidence.TryAppendOutboxAsync(owner.ScopeId, current.MilestoneId, bundle.Revision, createdAt, cancellationToken).ConfigureAwait(false);

        audit.Record(new DomainAudit(
            owner.ActorId,
            action,
            bundleId,
            bundle.Revision,
            "screenshot_evidence_appended"));

        logger.LogInformation(
            "Screenshot {ScreenshotId} selected for milestone {MilestoneId} at evidence revision {Revision}.",
            current.Id,
            current.MilestoneId,
            bundle.Revision);

        return bundle;
    }

    /// <summary>
    /// Publishes one picture's deselection as a new bundle revision that no longer carries it.
    /// Earlier bundles keep their own rows, so approved historical content still reads what it was
    /// approved against.
    /// </summary>
    /// <returns>The new bundle, or null when the head bundle already holds no item for the picture.</returns>
    private async Task<EvidenceBundle?> RemoveSelectionAsync(
        OwnerScope owner,
        CuratedScreenshot current,
        long expectedBundleRevision,
        CancellationToken cancellationToken)
    {
        var sourceRef = current.Id.ToString("D");

        var head = await evidence
            .FindHeadAsync(owner.ScopeId, current.MilestoneId, cancellationToken)
            .ConfigureAwait(false);

        RequireCurrent(expectedBundleRevision, head?.Revision ?? 0);

        if (head is null || !head.Items.Any(item => IsScreenshotOf(item, sourceRef)))
        {
            // Nothing to retract: the head holds no item for this picture, so deselecting it is a
            // row change only and creates no revision.
            return null;
        }

        var bundleId = Guid.NewGuid();
        var createdAt = clock.GetUtcNow();
        var stored = Assemble(bundleId, current.MilestoneId, head, [], omitSourceRef: sourceRef);

        EvidencePolicy.ValidateBundleBounds(stored);

        var bundle = new EvidenceBundle(bundleId, current.MilestoneId, expectedBundleRevision + 1, createdAt, stored);

        if (await evidence.TryInsertBundleAsync(
                owner.ScopeId,
                current.MilestoneId,
                bundleId,
                expectedBundleRevision,
                createdAt,
                cancellationToken).ConfigureAwait(false) == EvidenceCommitStatus.RevisionTaken)
        {
            return await LostDeselectAsync(owner, current, sourceRef, cancellationToken).ConfigureAwait(false);
        }

        await evidence.InsertItemsAsync(owner.ScopeId, bundleId, stored, cancellationToken).ConfigureAwait(false);
        await evidence.TryAppendOutboxAsync(owner.ScopeId, current.MilestoneId, bundle.Revision, createdAt, cancellationToken).ConfigureAwait(false);

        audit.Record(new DomainAudit(
            owner.ActorId,
            ScreenshotDeselectedAction,
            bundleId,
            bundle.Revision,
            "screenshot_evidence_deselected"));

        logger.LogInformation(
            "Screenshot {ScreenshotId} deselected for milestone {MilestoneId} at evidence revision {Revision}.",
            current.Id,
            current.MilestoneId,
            bundle.Revision);

        return bundle;
    }

    // -----------------------------------------------------------------------------------------
    // Small helpers.
    // -----------------------------------------------------------------------------------------

    private static void RequireRevisions(long expectedRevision, long expectedBundleRevision)
    {
        if (expectedRevision < 0)
        {
            throw new ValidationFailedException("If-Match", "The screenshot revision is zero or greater.");
        }

        if (expectedBundleRevision < 0)
        {
            throw new ValidationFailedException(
                "expectedBundleRevision",
                "The expected bundle revision is zero or greater.");
        }
    }

    private async Task<Guid> LockedMilestoneAsync(
        OwnerScope owner,
        Guid screenshotId,
        CancellationToken cancellationToken)
    {
        // The candidate names its milestone, and the milestone row is locked before anything is
        // read, so two curations of one milestone serialize on the row instead of racing on the
        // one-primary index. A picture in another installation answers 404 here rather than
        // revealing which milestone it belongs to.
        var probe = await curation.FindAsync(owner.ScopeId, screenshotId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("screenshot");

        if (!await curation.LockMilestoneAsync(owner.ScopeId, probe.MilestoneId, cancellationToken).ConfigureAwait(false))
        {
            throw new ResourceNotFoundException("screenshot");
        }

        await LiveMilestoneAsync(owner, probe.MilestoneId, cancellationToken).ConfigureAwait(false);

        return probe.MilestoneId;
    }

    private async Task<Milestone> LiveMilestoneAsync(OwnerScope owner, Guid milestoneId, CancellationToken token)
    {
        var milestone = await milestones.FindAsync(owner.ScopeId, milestoneId, token).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("milestone");

        var project = await projects.FindAsync(owner.ScopeId, milestone.ProjectId, token).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("project");

        if (project.IsArchived)
        {
            throw new ProjectArchivedException();
        }

        return milestone;
    }

    private sealed record DesiredCuration(string Status, string Caption, bool IsPrimary, long Revision)
    {
        public bool IsSelected => string.Equals(Status, ScreenshotCurationStatus.Selected, StringComparison.Ordinal);
    }

    private static DesiredCuration ResolveDesired(CuratedScreenshot current, ScreenshotCurateRequest request)
    {
        var status = request.Status ?? current.Status;

        if (!ScreenshotCurationStatus.IsKnown(status))
        {
            throw new ValidationFailedException(
                "status",
                "The status is one of candidate, selected or rejected.");
        }

        var caption = request.Caption ?? current.Caption;

        if (caption.Length > ScreenshotCurationLimits.CaptionMaxLength)
        {
            throw new ValidationFailedException(
                "caption",
                $"A caption is at most {ScreenshotCurationLimits.CaptionMaxLength} characters.");
        }

        var selected = string.Equals(status, ScreenshotCurationStatus.Selected, StringComparison.Ordinal);

        if (request.IsPrimary == true && !selected)
        {
            // Only a selected picture may be primary. A request that promotes a rejected or
            // unoffered picture is refused without changing anything, so a rejected picture can
            // never become the picture the milestone shows first.
            throw new ValidationFailedException(
                "isPrimary",
                "Only a selected screenshot may be the primary one.");
        }

        // Leaving the selected set clears the primary flag in the same write - including when the
        // request did not name it, because keeping the row's own flag would violate the invariant
        // above from the other side. A primary picture that is no longer selected would otherwise
        // be refused as its own promotion, and requiring the caller to clear it first would turn
        // one decision into two requests that can race each other.
        var isPrimary = selected && (request.IsPrimary ?? current.IsPrimary);

        return new DesiredCuration(status, caption, isPrimary, request.ExpectedRevision + 1);
    }

    private static bool IsUnchanged(CuratedScreenshot current, DesiredCuration desired) =>
        string.Equals(current.Status, desired.Status, StringComparison.Ordinal) &&
        string.Equals(current.Caption, desired.Caption, StringComparison.Ordinal) &&
        current.IsPrimary == desired.IsPrimary;

    private static void RequireExactOrder(IReadOnlyList<Guid> ids, CuratedScreenshot[] selected)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (ids.Count != selected.Length || ids.Distinct().Count() != ids.Count ||
            !ids.All(id => selected.Any(picture => picture.Id == id)))
        {
            errors["ids"] = ["The order must name exactly the currently selected screenshots, once each."];
            throw new ValidationFailedException(errors);
        }
    }

    private static EvidenceAppendItem SelectionItem(
        CuratedScreenshot current,
        string caption,
        long newScreenshotRevision,
        Guid? supersedesId)
    {
        var text = caption.Trim();

        var provisional = new EvidenceAppendItem(
            EvidenceKinds.Screenshot,
            current.Id.ToString("D"),
            newScreenshotRevision.ToString(CultureInfo.InvariantCulture),
            current.CapturedAt,
            string.Empty,
            string.IsNullOrEmpty(text)
                ? $"Screenshot captured from {current.SourceUrl} at {current.Viewport.Name}."
                : text,
            mediaId: current.MediaId,
            redactions: null,
            availability: EvidenceAvailability.Complete,
            supersedesId: supersedesId);

        return new EvidenceAppendItem(
            provisional.Kind,
            provisional.SourceRef,
            provisional.SourceRevision,
            provisional.CapturedAt,
            EvidencePolicy.ItemPayloadSha256(provisional),
            provisional.Text,
            provisional.MediaId,
            provisional.Redactions,
            provisional.Availability,
            provisional.SupersedesId);
    }

    private async Task RequireReferenceableMediaAsync(
        OwnerScope owner,
        CuratedScreenshot current,
        EvidenceAppendItem item,
        CancellationToken token)
    {
        var resolved = await evidence
            .ResolveMediaAsync(owner.ScopeId, current.ProjectId, [item.MediaId!.Value], token)
            .ConfigureAwait(false);

        if (resolved.Any(reference => !reference.IsReferenceable))
        {
            throw new ResourceNotFoundException("media");
        }
    }

    private async Task<Guid?> TipItemIdAsync(
        OwnerScope owner,
        CuratedScreenshot current,
        CancellationToken cancellationToken)
    {
        var head = await evidence
            .FindHeadAsync(owner.ScopeId, current.MilestoneId, cancellationToken)
            .ConfigureAwait(false);

        if (head is null)
        {
            return null;
        }

        var sourceRef = current.Id.ToString("D");
        var ours = head.Items.Where(item => IsScreenshotOf(item, sourceRef)).ToArray();
        var superseded = new HashSet<Guid>(ours.Where(item => item.SupersedesId.HasValue).Select(item => item.SupersedesId!.Value));

        return ours.FirstOrDefault(item => !superseded.Contains(item.Id))?.Id;
    }

    private static bool IsScreenshotOf(EvidenceItem item, string sourceRef) =>
        string.Equals(item.Kind, EvidenceKinds.Screenshot, StringComparison.Ordinal) &&
        string.Equals(item.SourceRef, sourceRef, StringComparison.Ordinal);

    private static void RequireCurrent(long expectedRevision, long storedRevision)
    {
        if (expectedRevision != storedRevision)
        {
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

    private async Task<EvidenceBundle> LostSelectionAppendAsync(
        OwnerScope owner,
        CuratedScreenshot current,
        IReadOnlyList<EvidenceAppendItem> items,
        CancellationToken token)
    {
        var head = await evidence.FindHeadAsync(owner.ScopeId, current.MilestoneId, token).ConfigureAwait(false);

        if (HeadAlreadyHolds(head, items))
        {
            return head!;
        }

        if (head is null)
        {
            throw new ResourceNotFoundException("milestone");
        }

        await LiveMilestoneAsync(owner, current.MilestoneId, token).ConfigureAwait(false);

        throw new RevisionConflictException(head.Revision);
    }

    private async Task<EvidenceBundle?> LostDeselectAsync(
        OwnerScope owner,
        CuratedScreenshot current,
        string sourceRef,
        CancellationToken token)
    {
        var head = await evidence.FindHeadAsync(owner.ScopeId, current.MilestoneId, token).ConfigureAwait(false);

        if (head is null)
        {
            throw new ResourceNotFoundException("milestone");
        }

        if (!head.Items.Any(item => IsScreenshotOf(item, sourceRef)))
        {
            // Somebody else's revision already removed this picture. Their bundle already says
            // what this call wanted to say, so reporting a conflict would make the caller retry
            // something that is already true.
            return head;
        }

        await LiveMilestoneAsync(owner, current.MilestoneId, token).ConfigureAwait(false);

        throw new RevisionConflictException(head.Revision);
    }

    private static bool HeadAlreadyHolds(EvidenceBundle? head, IReadOnlyList<EvidenceAppendItem> items)
    {
        if (head is null)
        {
            return false;
        }

        var held = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in head.Items)
        {
            held.Add(EvidenceValues.ItemKey(head.MilestoneId, item.Kind, item.SourceRef, item.SourceRevision, item.PayloadSha256));
        }

        return items.All(item => held.Contains(
            EvidenceValues.ItemKey(head.MilestoneId, item.Kind, item.SourceRef, item.SourceRevision, item.PayloadSha256)));
    }

    /// <summary>
    /// The resulting snapshot: everything the head bundle held, carried into the new bundle, plus
    /// the new items - or minus one picture's items when deselecting. Carried items keep their
    /// correction links, so a caption edit still reads as a correction of the earlier caption.
    /// </summary>
    private static List<EvidenceItem> Assemble(
        Guid bundleId,
        Guid milestoneId,
        EvidenceBundle? head,
        IReadOnlyList<EvidenceAppendItem> appended,
        string? omitSourceRef)
    {
        var items = new List<EvidenceItem>((head?.Items.Count ?? 0) + appended.Count);

        foreach (var item in head?.Items ?? [])
        {
            if (omitSourceRef is not null && IsScreenshotOf(item, omitSourceRef))
            {
                continue;
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

        var knownSupersessions = new HashSet<Guid>(
            (head?.Items ?? []).Select(item => item.Id));

        foreach (var item in appended)
        {
            if (item.SupersedesId is { } supersedes && !knownSupersessions.Contains(supersedes))
            {
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

        items.Sort(static (left, right) =>
            string.CompareOrdinal(left.Id.ToString("D"), right.Id.ToString("D")));

        return items;
    }

    private async Task<long> HeadRevisionAsync(OwnerScope owner, Guid milestoneId, CancellationToken token) =>
        (await evidence.FindHeadAsync(owner.ScopeId, milestoneId, token).ConfigureAwait(false))?.Revision ?? 0;
}
