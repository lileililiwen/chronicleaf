using System.Globalization;
using System.Text;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Evidence;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Voice;
using Chronicleaf.Core;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Application.Content;

/// <summary>
/// The review use cases, over the delivered evidence, voice, asset and audit contracts
/// (MARKDOWN-REVIEW-EXPORT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>An edit appends; an approval binds; an export serves.</b> A human edit is a new revision
/// carrying the owner's words beside the generated ones, so no edit ever rewrites the text an
/// approval was bound to. An approval names a revision, a body hash and an evidence revision, and
/// an export serves the approved bytes only while every one of those still describes the stored
/// state. That is what makes "editing approved text or evidence requires re-approval" a property
/// of the reads rather than a promise of the writers.
/// </para>
/// <para>
/// <b>Blocking means any deterministic finding the owner has not corrected.</b> Every voice
/// finding blocks, because the voice checker reports spans into the owner's own text and a finding
/// left standing is a style the owner asked for and then ignored. Of the grounding findings, only
/// <c>claim_evidence_missing</c> blocks: it says a claim cites an item the frozen bundle does not
/// contain, which is a fact that cannot be true. The other two annotate a stored draft for a human
/// to judge and do not stop an approval, because a system that refused every unreferenced claim
/// would be deciding truth rather than reporting it.
/// </para>
/// <para>
/// <b>Nothing here logs prose.</b> The logger receives identifiers, codes and counts. The body is
/// read from a stored revision and written to a revision; it never appears in a log line, in an
/// exception message or in an audit detail.
/// </para>
/// </remarks>
public sealed class ContentReviewService(
    IContentAssetStore assets,
    IContentReviewStore reviews,
    IEvidenceStore evidence,
    IVoiceProfileService voice,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<ContentReviewService> logger) : IContentReviewService
{
    /// <summary>The audit action recorded when a human revision is appended.</summary>
    public const string EditedAction = "content.review.edited";

    /// <summary>The audit action recorded when a revision is approved.</summary>
    public const string ApprovedAction = "content.review.approved";

    /// <summary>The audit action recorded when an approval is withdrawn.</summary>
    public const string UnapprovedAction = "content.review.unapproved";

    /// <summary>The audit action recorded when an approved revision is exported.</summary>
    public const string ExportedAction = "content.review.exported";

    /// <summary>The only export format this package serves.</summary>
    public const string MarkdownFormat = "markdown";

    /// <inheritdoc />
    public async Task<EditedContentRevision> EditAsync(
        OwnerScope owner,
        Guid assetId,
        long? presentedRevision,
        EditContentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        if (assetId == Guid.Empty)
        {
            throw new ValidationFailedException("assetId", "An asset id is required.");
        }

        if (presentedRevision is null)
        {
            throw new IfMatchRequiredException("content asset");
        }

        if (string.IsNullOrWhiteSpace(request.BodyMarkdown))
        {
            throw new ValidationFailedException("bodyMarkdown", "An edited body must carry text.");
        }

        var body = request.BodyMarkdown;
        var bodyBytes = Encoding.UTF8.GetByteCount(body);
        if (bodyBytes > ContentAssetLimits.MaxBodyBytes)
        {
            throw new PayloadTooLargeException(ContentAssetLimits.MaxBodyBytes);
        }

        var title = request.Title?.Trim();
        if (title is not null)
        {
            if (title.Length == 0)
            {
                throw new ValidationFailedException("title", "An edited title must carry text.");
            }

            if (title.Length > ContentLimits.MaxTitleLength)
            {
                throw new ValidationFailedException(
                    "title",
                    $"An edited title is at most {ContentLimits.MaxTitleLength} characters.");
            }
        }

        if (request.ExpectedRevision != presentedRevision.Value)
        {
            throw new ValidationFailedException(
                "expectedRevision",
                "The expected revision must be the one presented in If-Match.");
        }

        if (request.ExpectedRevision < 1)
        {
            throw new ValidationFailedException(
                "expectedRevision",
                "An edited revision follows a stored one, so name one.");
        }

        var now = clock.GetUtcNow();

        return await unitOfWork.ExecuteAsync(
            async token =>
            {
                var asset = await assets.FindAsync(owner.ScopeId, assetId, token).ConfigureAwait(false)
                    ?? throw new ResourceNotFoundException("content asset");

                if (asset.CurrentRevision != request.ExpectedRevision)
                {
                    throw new RevisionConflictException(asset.CurrentRevision);
                }

                var parent = await assets
                    .FindRevisionAsync(owner.ScopeId, assetId, asset.CurrentRevision, token)
                    .ConfigureAwait(false)
                    ?? throw UnreadableContentRow();

                // The edit is rebased onto the current evidence head and the current voice, because
                // the owner edited what they were looking at and the new revision describes that
                // state - not the state the parent was generated from. A head that moved since the
                // parent is exactly what the approval binds, so carrying the old one forward would
                // let an edit smuggle a stale binding into a fresh revision.
                var head = await evidence
                    .FindHeadAsync(owner.ScopeId, asset.MilestoneId, token)
                    .ConfigureAwait(false)
                    ?? throw new ValidationFailedException(
                        "expectedRevision",
                        "This milestone has no evidence, so an edited revision has nothing to be bound to.");

                var profile = await voice
                    .GetAsync(owner.ScopeId, asset.ProjectId, token)
                    .ConfigureAwait(false);

                var summary = GroundedSummary.FromCanonicalJson(parent.SummaryJson)
                    ?? throw UnreadableContentRow();

                if (title is not null)
                {
                    summary = summary with { MilestoneTitle = title };
                }

                var revision = new ContentRevision(
                    Guid.NewGuid(),
                    asset.Id,
                    owner.ScopeId,
                    asset.CurrentRevision + 1,
                    head.Id,
                    head.Revision,
                    profile.Revision,
                    VoiceSnapshotCodec.Serialize(new VoiceSnapshot(
                        profile.Resolved.PresetId,
                        profile.Revision,
                        now,
                        profile.Resolved.Effective)),
                    summary.ToCanonicalJson(),
                    body,
                    ContentRevision.Hash(body),
                    parent.ProviderId,
                    parent.Model,
                    parent.PromptVersion,
                    parent.SchemaVersion,
                    now);

                if (!await assets
                        .TryAppendRevisionAsync(revision, asset.CurrentRevision, now, token)
                        .ConfigureAwait(false))
                {
                    var reread = await assets.FindAsync(owner.ScopeId, assetId, token).ConfigureAwait(false);
                    throw new RevisionConflictException(reread?.CurrentRevision ?? asset.CurrentRevision);
                }

                // A new revision means no earlier approval authorizes an export anymore. The rows
                // stay as history; their invalidation is recorded with the reason so a reader can
                // see that an edit - not an evidence change - ended them.
                await reviews.InvalidateLiveAsync(
                    owner.ScopeId, assetId, "content_edited", now, token).ConfigureAwait(false);

                audit.Record(new DomainAudit(
                    owner.ActorId,
                    EditedAction,
                    asset.Id,
                    revision.Revision,
                    $"revision={revision.Revision.ToString(CultureInfo.InvariantCulture)};"));

                logger.LogInformation(
                    "Content asset {AssetId} edited to revision {Revision}.",
                    assetId,
                    revision.Revision);

                return new EditedContentRevision(asset.Id, revision.Revision, revision.BodyHash);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ContentApprovalRecord> ApproveAsync(
        OwnerScope owner,
        Guid assetId,
        ApproveContentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        if (assetId == Guid.Empty)
        {
            throw new ValidationFailedException("assetId", "An asset id is required.");
        }

        if (request.ContentRevision < 1)
        {
            throw new ValidationFailedException(
                "contentRevision",
                "An approval names a stored revision, so name one.");
        }

        if (request.EvidenceRevision < 1)
        {
            throw new ValidationFailedException(
                "evidenceRevision",
                "An approval names the evidence head it was bound to, so name one.");
        }

        if (request.VoiceFindingsAcknowledged)
        {
            // The bypass the design forbids: a flag that cleared blocking findings without
            // correcting the text would let an approval mean "reviewed except for the parts that
            // failed review".
            throw new ValidationFailedException(
                "voiceFindingsAcknowledged",
                "Blocking findings are corrected, not acknowledged. Correct the text and ask again.");
        }

        var now = clock.GetUtcNow();

        return await unitOfWork.ExecuteAsync(
            async token =>
            {
                var asset = await assets.FindAsync(owner.ScopeId, assetId, token).ConfigureAwait(false)
                    ?? throw new ResourceNotFoundException("content asset");

                // Only the head can be approved. A revision that is not the head is either stale -
                // somebody edited after the approver read - or a replay of an older decision, and in
                // neither case is it the text the owner is looking at now.
                if (request.ContentRevision != asset.CurrentRevision)
                {
                    throw new RevisionConflictException(asset.CurrentRevision);
                }

                var revision = await assets
                    .FindRevisionAsync(owner.ScopeId, assetId, asset.CurrentRevision, token)
                    .ConfigureAwait(false)
                    ?? throw UnreadableContentRow();

                if (!string.Equals(ContentRevision.Hash(revision.BodyMarkdown), revision.BodyHash, StringComparison.Ordinal))
                {
                    throw UnreadableContentRow();
                }

                if (revision.EvidenceRevision != request.EvidenceRevision)
                {
                    throw new RevisionConflictException(asset.CurrentRevision);
                }

                var head = await evidence
                    .FindHeadAsync(owner.ScopeId, asset.MilestoneId, token)
                    .ConfigureAwait(false)
                    ?? throw new RevisionConflictException(asset.CurrentRevision);

                if (head.Id != revision.EvidenceBundleId || head.Revision != request.EvidenceRevision)
                {
                    throw new RevisionConflictException(asset.CurrentRevision);
                }

                // Idempotent: an approval that already names this revision is the same decision
                // answered again, not a second decision.
                var existing = await reviews
                    .FindLiveAsync(owner.ScopeId, assetId, asset.CurrentRevision, token)
                    .ConfigureAwait(false);

                if (existing is not null &&
                    string.Equals(existing.BodyHash, revision.BodyHash, StringComparison.Ordinal) &&
                    existing.EvidenceRevision == request.EvidenceRevision)
                {
                    return ToRecord(existing);
                }

                await ThrowWhenBlockedAsync(owner, revision, token).ConfigureAwait(false);

                var approval = new ContentApproval(
                    Guid.NewGuid(),
                    owner.ScopeId,
                    asset.Id,
                    asset.CurrentRevision,
                    owner.ActorId,
                    ContentPayloadKinds.Text,
                    revision.BodyHash,
                    request.EvidenceRevision,
                    ContentApproval.Hash(revision.VoiceSnapshotJson),
                    ContentApproval.MediaSelectionHashFor(SelectedPictures(head)),
                    artifactHash: null,
                    now,
                    invalidatedAt: null,
                    reason: null);

                if (!await reviews.TryInsertAsync(approval, token).ConfigureAwait(false))
                {
                    var live = await reviews
                        .FindLiveAsync(owner.ScopeId, assetId, asset.CurrentRevision, token)
                        .ConfigureAwait(false);

                    if (live is not null)
                    {
                        return ToRecord(live);
                    }

                    throw new RevisionConflictException(asset.CurrentRevision);
                }

                audit.Record(new DomainAudit(
                    owner.ActorId,
                    ApprovedAction,
                    asset.Id,
                    revision.Revision,
                    $"revision={revision.Revision.ToString(CultureInfo.InvariantCulture)};"
                    + $"evidenceRevision={request.EvidenceRevision.ToString(CultureInfo.InvariantCulture)};"));

                logger.LogInformation(
                    "Content asset {AssetId} revision {Revision} approved.",
                    assetId,
                    revision.Revision);

                return ToRecord(approval);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ContentApprovalRecord> UnapproveAsync(
        OwnerScope owner,
        Guid assetId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (assetId == Guid.Empty)
        {
            throw new ValidationFailedException("assetId", "An asset id is required.");
        }

        var trimmed = reason?.Trim();
        if (trimmed?.Length > 500)
        {
            throw new ValidationFailedException("reason", "A reason is at most 500 characters.");
        }

        var now = clock.GetUtcNow();

        return await unitOfWork.ExecuteAsync(
            async token =>
            {
                _ = await assets.FindAsync(owner.ScopeId, assetId, token).ConfigureAwait(false)
                    ?? throw new ResourceNotFoundException("content asset");

                var approvals = await reviews
                    .ListForAssetAsync(owner.ScopeId, assetId, token)
                    .ConfigureAwait(false);

                var live = approvals.FirstOrDefault(approval => approval.IsLive)
                    ?? throw new ApprovalRequiredException();

                await reviews.InvalidateLiveAsync(
                    owner.ScopeId, assetId, trimmed is { Length: > 0 } ? trimmed : "withdrawn", now, token)
                    .ConfigureAwait(false);

                var reread = await reviews
                    .ListForAssetAsync(owner.ScopeId, assetId, token)
                    .ConfigureAwait(false);

                var withdrawn = reread.FirstOrDefault(approval => approval.Id == live.Id) ?? live;

                audit.Record(new DomainAudit(
                    owner.ActorId,
                    UnapprovedAction,
                    assetId,
                    withdrawn.ContentRevision,
                    $"revision={withdrawn.ContentRevision.ToString(CultureInfo.InvariantCulture)};"));

                logger.LogInformation(
                    "Content asset {AssetId} approval withdrawn for revision {Revision}.",
                    assetId,
                    withdrawn.ContentRevision);

                return ToRecord(withdrawn);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ContentReviewView> ReadReviewAsync(
        OwnerScope owner,
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (assetId == Guid.Empty)
        {
            throw new ValidationFailedException("assetId", "An asset id is required.");
        }

        var asset = await assets.FindAsync(owner.ScopeId, assetId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("content asset");

        var approvals = await reviews
            .ListForAssetAsync(owner.ScopeId, assetId, cancellationToken)
            .ConfigureAwait(false);

        var live = approvals.FirstOrDefault(approval => approval.IsLive);

        if (live is null)
        {
            return new ContentReviewView(
                asset.Id,
                asset.CurrentRevision,
                approvals.Count == 0 ? "draft" : "invalidated",
                null);
        }

        // A live row is only `approved` while it still describes the stored state. Anything that
        // moved since - the head, the body, the evidence or the pictures - makes the review
        // `invalidated` on read, which is the same answer the export gives without a second
        // vocabulary for one condition.
        var status = await LiveOrStaleAsync(owner, asset, live, cancellationToken).ConfigureAwait(false);

        return new ContentReviewView(
            asset.Id,
            asset.CurrentRevision,
            status,
            status == "approved" ? ToRecord(live) : null);
    }

    /// <inheritdoc />
    public async Task<ApprovedContentExport> ExportAsync(
        OwnerScope owner,
        Guid assetId,
        string? format,
        long? revision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (assetId == Guid.Empty)
        {
            throw new ValidationFailedException("assetId", "An asset id is required.");
        }

        var requested = (format ?? MarkdownFormat).Trim().ToLowerInvariant();
        if (!string.Equals(requested, MarkdownFormat, StringComparison.Ordinal))
        {
            throw new ValidationFailedException(
                "format",
                "This package serves Markdown. Name format=markdown.");
        }

        var asset = await assets.FindAsync(owner.ScopeId, assetId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("content asset");

        var approvals = await reviews
            .ListForAssetAsync(owner.ScopeId, assetId, cancellationToken)
            .ConfigureAwait(false);

        // Never approved and once approved are two different answers: the first has no decision
        // behind it, and the second has one that no longer describes the stored state. A client
        // that saw one as the other would either approve something already decided or re-review
        // text nobody ever looked at.
        var live = approvals.FirstOrDefault(approval => approval.IsLive);

        if (live is null)
        {
            if (approvals.Count == 0)
            {
                throw new ApprovalRequiredException();
            }

            throw new ApprovalStaleException("invalidated");
        }

        // An export names the revision it wants, or takes the approved one. Either way the
        // approval has to name exactly that revision: a concurrent edit appends a newer head and
        // the older approval can no longer authorize anything, which is the boundary scenario.
        var wanted = revision ?? live.ContentRevision;
        if (wanted != live.ContentRevision)
        {
            throw new ApprovalStaleException("revision");
        }

        var stored = await assets
            .FindRevisionAsync(owner.ScopeId, assetId, wanted, cancellationToken)
            .ConfigureAwait(false)
            ?? throw UnreadableContentRow();

        if (!string.Equals(ContentRevision.Hash(stored.BodyMarkdown), live.BodyHash, StringComparison.Ordinal) ||
            !string.Equals(stored.BodyHash, live.BodyHash, StringComparison.Ordinal))
        {
            throw new ApprovalStaleException("body");
        }

        var head = await evidence
            .FindHeadAsync(owner.ScopeId, asset.MilestoneId, cancellationToken)
            .ConfigureAwait(false);

        if (head is null || head.Id != stored.EvidenceBundleId || head.Revision != live.EvidenceRevision)
        {
            throw new ApprovalStaleException("evidence");
        }

        if (!string.Equals(
                ContentApproval.MediaSelectionHashFor(SelectedPictures(head)),
                live.MediaSelectionHash,
                StringComparison.Ordinal))
        {
            throw new ApprovalStaleException("media");
        }

        // The audit row names the revision and the approval, never the prose: an export is a
        // record that bytes left the building, not a copy of them.
        await unitOfWork.ExecuteAsync(
            token =>
            {
                audit.Record(new DomainAudit(
                    owner.ActorId,
                    ExportedAction,
                    asset.Id,
                    stored.Revision,
                    $"revision={stored.Revision.ToString(CultureInfo.InvariantCulture)};"
                    + $"approval={live.Id:D};"));
                return Task.FromResult(true);
            },
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Content asset {AssetId} revision {Revision} exported.",
            assetId,
            stored.Revision);

        return new ApprovedContentExport(
            $"chronicleaf-{assetId:N}-r{stored.Revision}.md",
            stored.BodyMarkdown,
            stored.Revision,
            stored.BodyHash);
    }

    // ==============================================================================================
    // Helpers.
    // ==============================================================================================

    /// <summary>
    /// Refuses an approval while deterministic findings remain on the stored revision.
    /// </summary>
    /// <remarks>
    /// Both lists are recomputed from the stored revision rather than trusted from anywhere else,
    /// so what blocks an approval cannot depend on whether the caller asked before or after the
    /// revision was written. The voice list runs against the frozen snapshot the revision carries -
    /// the voice the text was judged under - and the grounding list against the frozen bundle it
    /// names.
    /// </remarks>
    private async Task ThrowWhenBlockedAsync(OwnerScope owner, ContentRevision revision, CancellationToken cancellationToken)
    {
        var summary = GroundedSummary.FromCanonicalJson(revision.SummaryJson)
            ?? throw UnreadableContentRow();

        var voiceSnapshot = VoiceSnapshotCodec.Deserialize(revision.VoiceSnapshotJson)
            ?? throw UnreadableContentRow();

        var voiceFindings = VoiceCheckService.Check(
            voiceSnapshot.Values, summary.MilestoneTitle, revision.BodyMarkdown);

        var claims = ClaimMap.Build(summary);
        var availability = claims
            .SelectMany(claim => claim.EvidenceIds)
            .Distinct()
            .ToDictionary(id => id, _ => (string?)null);

        // The store answers which of the cited ids the frozen bundle holds. Asked inside the
        // approval rather than cached anywhere, because a bundle is append-only and the frozen one
        // cannot have changed - a second answer would either agree or be about another bundle.
        var resolved = await assets
            .ResolveAvailabilityAsync(owner.ScopeId, revision.EvidenceBundleId, [.. availability.Keys], cancellationToken)
            .ConfigureAwait(false);

        var grounding = GroundingValidator.ForStoredRevision(claims, resolved);

        var fieldErrors = new Dictionary<string, string[]>();
        var blocking = new List<string>();

        foreach (var finding in voiceFindings)
        {
            blocking.Add($"voice:{finding.Rule}:{finding.Field}");
        }

        foreach (var finding in grounding)
        {
            if (string.Equals(finding.Rule, GroundingRule.ClaimEvidenceMissing, StringComparison.Ordinal))
            {
                blocking.Add($"grounding:{finding.Rule}:{finding.Section}");
            }
        }

        if (blocking.Count > 0)
        {
            fieldErrors["approval"] = [
                "This revision carries findings that must be corrected before it can be approved: "
                + string.Join("; ", blocking.OrderBy(entry => entry, StringComparer.Ordinal)) + "."];

            throw new ApprovalBlockedException(fieldErrors);
        }
    }

    /// <summary>
    /// Whether a live approval row still describes the stored state.
    /// </summary>
    private async Task<string> LiveOrStaleAsync(
        OwnerScope owner,
        ContentAsset asset,
        ContentApproval live,
        CancellationToken cancellationToken)
    {
        if (live.ContentRevision != asset.CurrentRevision)
        {
            return "invalidated";
        }

        var stored = await assets
            .FindRevisionAsync(owner.ScopeId, asset.Id, live.ContentRevision, cancellationToken)
            .ConfigureAwait(false);

        if (stored is null ||
            !string.Equals(stored.BodyHash, live.BodyHash, StringComparison.Ordinal))
        {
            return "invalidated";
        }

        var head = await evidence
            .FindHeadAsync(owner.ScopeId, asset.MilestoneId, cancellationToken)
            .ConfigureAwait(false);

        if (head is null ||
            head.Id != stored.EvidenceBundleId ||
            head.Revision != live.EvidenceRevision ||
            !string.Equals(
                ContentApproval.MediaSelectionHashFor(SelectedPictures(head)),
                live.MediaSelectionHash,
                StringComparison.Ordinal))
        {
            return "invalidated";
        }

        return "approved";
    }

    /// <summary>
    /// The selected pictures of one evidence head, as the media hash binds them.
    /// </summary>
    private static IReadOnlyList<(Guid MediaId, string Caption)> SelectedPictures(EvidenceBundle head)
    {
        ArgumentNullException.ThrowIfNull(head);

        return [.. head.Items
            .Where(item => string.Equals(item.Kind, EvidenceKinds.Screenshot, StringComparison.Ordinal)
                && item.MediaId is not null
                && string.Equals(item.Availability, EvidenceAvailability.Complete, StringComparison.Ordinal))
            .Select(item => (item.MediaId!.Value, item.Text))];
    }

    private static ContentApprovalRecord ToRecord(ContentApproval approval) => new(
        approval.Id,
        approval.AssetId,
        approval.ContentRevision,
        approval.PayloadKind,
        approval.BodyHash,
        approval.EvidenceRevision,
        approval.ApprovedAt);

    /// <summary>
    /// A stored row this build cannot read (500).
    /// </summary>
    private static ApiException UnreadableContentRow() =>
        new InternalFailureException(
            "A stored content revision could not be read by this build, so it cannot be reviewed. "
            + "The revision itself is unchanged.");
}
