using System.Globalization;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Content;
using Chronicleaf.Application.Evidence;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Narrative;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Voice;
using Chronicleaf.Core;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Application.ContentVariants;

/// <summary>
/// The variant use cases, over the delivered evidence, voice, narrative, asset, review and audit
/// contracts (CONTENT-ASSET-VARIANTS-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>A variant is derived, never generated.</b> The source is an approved <c>markdown-story</c>
/// revision; the derivation re-renders that revision's accepted summary through a deterministic
/// template. There is no provider behind this service, so there is no consent to check and no key
/// to lease - and nothing a caller could supply that would put a new fact into the body.
/// </para>
/// <para>
/// <b>Approval is reused, never redefined.</b> The source must carry a live approval that still
/// describes the stored state, under exactly the bindings the review package's export re-checks:
/// the head revision, the body hash and the evidence head. A source that was never approved is
/// <c>approval_required</c>; one whose evidence moved is <c>approval_stale</c>. The derivative
/// itself starts with no approval row at all, so approving the source never approves the variant.
/// </para>
/// <para>
/// <b>Voice precedence is structural.</b> The renderer receives the profile's resolved effective
/// values, in which project overrides have already replaced preset values. The stored revision
/// carries that snapshot as a value, so a later profile edit cannot reach it - the same
/// provenance the drafts package stores.
/// </para>
/// <para>
/// <b>Nothing here logs prose.</b> The logger receives identifiers, codes and counts. The summary
/// and the body travel from a stored document into a stored revision; neither appears in a log
/// line, in an exception message or in an audit detail.
/// </para>
/// </remarks>
public sealed class ContentVariantService(
    IMilestoneStore milestones,
    IProjectStore projects,
    IEvidenceStore evidence,
    IVoiceProfileService voice,
    INarrativeService narrative,
    IContentAssetStore assets,
    IContentReviewStore reviews,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    ILogger<ContentVariantService> logger) : IContentVariantService
{
    /// <summary>The audit action recorded when a variant is derived.</summary>
    public const string VariantDerivedAction = "content.variant.derived";

    /// <summary>How many narrative lines one variant may carry. A bound, not a preference.</summary>
    public const int MaxHistoryLines = 5;

    /// <summary>How many characters of one narrative line one variant may carry.</summary>
    public const int MaxHistoryLineChars = 200;

    /// <inheritdoc />
    public async Task<DerivedVariant> DeriveAsync(
        OwnerScope owner,
        Guid milestoneId,
        DeriveVariantRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        if (!ContentVariantTypes.IsVariant(request.Type))
        {
            throw new ValidationFailedException(
                "type",
                "The format must be one this product derives: "
                + string.Join(", ", ContentVariantTypes.All) + ".");
        }

        if (request.EvidenceRevision < 1)
        {
            throw new ValidationFailedException(
                "evidenceRevision",
                "A variant is derived against a committed evidence revision, so name one.");
        }

        if (request.VoiceRevision < 0)
        {
            throw new ValidationFailedException("voiceRevision", "A voice revision is zero or greater.");
        }

        var prepared = await unitOfWork.ExecuteAsync(
            async token =>
            {
                var milestone = await milestones
                    .FindAsync(owner.ScopeId, milestoneId, token)
                    .ConfigureAwait(false)
                    ?? throw new ResourceNotFoundException("milestone");

                var project = await projects
                    .FindAsync(owner.ScopeId, milestone.ProjectId, token)
                    .ConfigureAwait(false)
                    ?? throw new ResourceNotFoundException("project");

                if (project.ArchivedAt is not null)
                {
                    throw new ProjectArchivedException();
                }

                return new PreparedVariant(milestone.ProjectId, milestone.Title);
            },
            cancellationToken).ConfigureAwait(false);

        // The source story: named, or the milestone's own. It must be a markdown story for this
        // milestone - deriving one variant from another would let provenance drift one hop at a
        // time until no row names the evidence anything was checked against.
        var source = request.SourceAssetId is { } sourceId
            ? await assets.FindAsync(owner.ScopeId, sourceId, cancellationToken).ConfigureAwait(false)
                ?? throw new ResourceNotFoundException("content asset")
            : await assets.FindForMilestoneAsync(
                    owner.ScopeId, milestoneId, ContentAssetTypes.MarkdownStory, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new ResourceNotFoundException("content asset");

        if (!string.Equals(source.Type, ContentAssetTypes.MarkdownStory, StringComparison.Ordinal) ||
            source.MilestoneId != milestoneId)
        {
            throw new ValidationFailedException(
                "sourceAssetId",
                "A variant is derived from the milestone's approved Markdown story, not from "
                + "another variant or another milestone's content.");
        }

        if (source.CurrentRevision < 1)
        {
            throw new ResourceNotFoundException("content asset");
        }

        var sourceRevision = await assets
            .FindRevisionAsync(owner.ScopeId, source.Id, source.CurrentRevision, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("content asset");

        // The approval bindings, re-checked rather than redefined: a live row must name the head
        // revision and its body hash, and the evidence head must still be the one the approval was
        // bound to. Never approved and once approved are two different answers, exactly as on the
        // export route.
        var approvals = await reviews
            .ListForAssetAsync(owner.ScopeId, source.Id, cancellationToken)
            .ConfigureAwait(false);

        var live = approvals.FirstOrDefault(approval => approval.IsLive);

        if (live is null)
        {
            if (approvals.Count == 0)
            {
                throw new ApprovalRequiredException();
            }

            throw new ApprovalStaleException("invalidated");
        }

        if (live.ContentRevision != source.CurrentRevision ||
            !string.Equals(live.BodyHash, sourceRevision.BodyHash, StringComparison.Ordinal))
        {
            throw new ApprovalStaleException("revision");
        }

        var head = await evidence
            .FindHeadAsync(owner.ScopeId, milestoneId, cancellationToken)
            .ConfigureAwait(false);

        if (head is null ||
            head.Id != sourceRevision.EvidenceBundleId ||
            head.Revision != live.EvidenceRevision)
        {
            throw new ApprovalStaleException("evidence");
        }

        // The revisions the owner was looking at must still be current. An owner who derived
        // against a head that has since moved gets the same stale answer the export gives, not a
        // variant bound to provenance they did not see.
        if (request.EvidenceRevision != head.Revision)
        {
            throw new RevisionConflictException(head.Revision);
        }

        var profile = await voice
            .GetAsync(owner.ScopeId, prepared.ProjectId, cancellationToken)
            .ConfigureAwait(false);

        if (request.VoiceRevision != profile.Revision)
        {
            throw new RevisionConflictException(profile.Revision);
        }

        var summary = GroundedSummary.FromCanonicalJson(sourceRevision.SummaryJson)
            ?? throw UnreadableContentRow();

        var claims = ClaimMap.Build(summary);

        // The stored revision's own grounding, recomputed from the frozen bundle it names. A claim
        // citing an item the bundle does not hold means the source moved under its approval, and a
        // variant of it would carry a citation to nothing.
        var availability = await assets.ResolveAvailabilityAsync(
                owner.ScopeId,
                sourceRevision.EvidenceBundleId,
                [.. claims.SelectMany(claim => claim.EvidenceIds).Distinct()],
                cancellationToken)
            .ConfigureAwait(false);

        var stored = GroundingValidator.ForStoredRevision(claims, availability);

        if (stored.Any(finding =>
                string.Equals(finding.Rule, GroundingRule.ClaimEvidenceMissing, StringComparison.Ordinal)))
        {
            throw new ApprovalStaleException("evidence");
        }

        // The project's narrative entries, read structured rather than composed: the context
        // builder's composed lines carry machine tokens - dates, entry refs, evidence ids - that no
        // evidence-cited body could legally contain, so history arrives here as the owner's own words
        // with their ids beside them rather than inside them. Only exportable entries are
        // readable: a private entry without an explicit selection stays absent, under the same rule
        // the context builder enforces. A line carrying a figure the cited evidence never stated is
        // omitted rather than rendered, which is the design's "missing fields omitted, never
        // fabricated" applied to history. The entry ids travel in the audit row, where identifiers
        // belong: the numeral rule would read them as figures appearing in no evidence item.
        var now = clock.GetUtcNow();
        var entries = await narrative.ListEntriesAsync(
                owner,
                prepared.ProjectId,
                limit: null,
                cancellationToken)
            .ConfigureAwait(false);

        var cited = new HashSet<Guid>(claims.SelectMany(claim => claim.EvidenceIds));
        var corpus = string.Join(
            "\n",
            head.Items
                .Where(item => cited.Contains(item.Id))
                .Select(item => item.Text));

        var history = entries.Items
            .Where(entry =>
                string.Equals(entry.Visibility, NarrativeVisibility.Exportable, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(entry.Text)
                && ClaimMap.NumbersIn(entry.Text)
                    .All(number => ClaimMap.ContainsNumber(corpus, number)))
            .OrderBy(entry => entry.OccurredAt)
            .ThenBy(entry => entry.Id)
            .Take(MaxHistoryLines)
            .Select(entry => (
                entry.Kind,
                entry.Text.Length > MaxHistoryLineChars
                    ? entry.Text[..MaxHistoryLineChars].TrimEnd() + "…"
                    : entry.Text))
            .ToList();

        var historyRefs = entries.Items
            .Where(entry =>
                string.Equals(entry.Visibility, NarrativeVisibility.Exportable, StringComparison.Ordinal))
            .OrderBy(entry => entry.OccurredAt)
            .ThenBy(entry => entry.Id)
            .Take(MaxHistoryLines)
            .Select(entry => $"entry:{entry.Id:D}:{entry.Revision.ToString(CultureInfo.InvariantCulture)}")
            .ToList();

        var (title, body) = ContentVariantRenderer.Render(
            request.Type!,
            string.IsNullOrWhiteSpace(summary.MilestoneTitle) ? prepared.MilestoneTitle : summary.MilestoneTitle,
            summary,
            claims,
            profile.Resolved.Effective,
            history);

        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ValidationFailedException(
                "type",
                "The source story carries no title to derive a variant from.");
        }

        // The rendered body is held to the same local citation rules as a generated draft: every
        // footnote must name the claim map, the known limitations must be carried, and no figure
        // may appear that the cited evidence never stated. The corpus is the cited items' own
        // texts - not the availability words the store reports - because the rule compares figures
        // against what was collected. A template defect is refused here rather than stored as an
        // ungrounded promise.
        var markdown = MarkdownDraftValidator.Validate(body, claims, summary.KnownLimitations, corpus);

        if (markdown.FailureCode is not null)
        {
            logger.LogWarning(
                "Event=content.variant_invalid Type={Type} Code={Code}",
                request.Type,
                markdown.FailureCode);

            throw new ValidationFailedException(
                "type",
                $"The {request.Type} template produced text that failed its own grounding check "
                + $"({markdown.FailureCode}), so nothing was stored.");
        }

        var committed = await unitOfWork.ExecuteAsync(
            async token =>
            {
                var asset = await assets.FindForMilestoneAsync(
                        owner.ScopeId, milestoneId, request.Type!, token)
                    .ConfigureAwait(false);

                long expected;

                if (asset is null)
                {
                    var created = new ContentAsset(
                        Guid.NewGuid(),
                        owner.ScopeId,
                        prepared.ProjectId,
                        milestoneId,
                        request.Type!,
                        0,
                        now,
                        now);

                    if (await assets.TryCreateAsync(created, token).ConfigureAwait(false))
                    {
                        asset = created;
                        expected = 0;
                    }
                    else
                    {
                        asset = await assets.FindForMilestoneAsync(
                                owner.ScopeId, milestoneId, request.Type!, token)
                            .ConfigureAwait(false)
                            ?? throw new ResourceNotFoundException("content asset");
                        expected = asset.CurrentRevision;
                    }
                }
                else
                {
                    expected = asset.CurrentRevision;
                }

                var revision = new ContentRevision(
                    Guid.NewGuid(),
                    asset.Id,
                    owner.ScopeId,
                    expected + 1,
                    sourceRevision.EvidenceBundleId,
                    sourceRevision.EvidenceRevision,
                    profile.Revision,
                    VoiceSnapshotCodec.Serialize(new VoiceSnapshot(
                        profile.Resolved.PresetId,
                        profile.Revision,
                        now,
                        profile.Resolved.Effective)),
                    summary.ToCanonicalJson(),
                    body,
                    ContentRevision.Hash(body),
                    "local-template",
                    ContentVariantRenderer.TemplateVersion,
                    ContentVariantRenderer.TemplateVersion,
                    sourceRevision.SchemaVersion,
                    now);

                if (!await assets
                        .TryAppendRevisionAsync(revision, expected, now, token)
                        .ConfigureAwait(false))
                {
                    var reread = await assets.FindAsync(owner.ScopeId, asset.Id, token).ConfigureAwait(false);
                    throw new RevisionConflictException(reread?.CurrentRevision ?? asset.CurrentRevision);
                }

                audit.Record(new DomainAudit(
                    owner.ActorId,
                    VariantDerivedAction,
                    asset.Id,
                    revision.Revision,
                    // Identifiers, revisions and the derivation chain. Not the body, not one byte
                    // of the evidence, and the narrative entry refs name rows rather than quoting
                    // their words: this row outlives the request.
                    Detail: $"type={request.Type};source={source.Id:D};"
                        + $"sourceRevision={sourceRevision.Revision.ToString(CultureInfo.InvariantCulture)};"
                        + $"evidenceRevision={sourceRevision.EvidenceRevision.ToString(CultureInfo.InvariantCulture)};"
                        + $"voiceRevision={profile.Revision.ToString(CultureInfo.InvariantCulture)};"
                        + $"history={string.Join(",", historyRefs)};"));

                return (asset.Id, revision.Revision, revision.BodyHash);
            },
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Content variant {Type} derived for milestone {MilestoneId} as asset {AssetId} revision {Revision}.",
            request.Type,
            milestoneId,
            committed.Item1,
            committed.Item2);

        return new DerivedVariant(
            committed.Item1,
            request.Type!,
            committed.Item2,
            committed.Item3,
            source.Id,
            sourceRevision.Revision,
            sourceRevision.EvidenceRevision,
            profile.Revision);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VariantSummary>> ListAsync(
        OwnerScope owner,
        Guid milestoneId,
        CancellationToken cancellationToken = default,
        bool includePacks = false)
    {
        ArgumentNullException.ThrowIfNull(owner);

        _ = await milestones.FindAsync(owner.ScopeId, milestoneId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("milestone");

        var listed = new List<VariantSummary>();

        // CONTENT-PACK-PANELS-004. The picker surface for packs is this same
        // delivered route with the pack kinds asked for explicitly: the two
        // pack kinds the delivered backends store are listed alongside the
        // variant formats only when includePacks is set, so the default read
        // the variant panel and every older caller rely on is unchanged. The
        // story itself and the video-story kind stay out in both modes: the
        // story has its own draft read, and video-story selection is that
        // package's own surface.
        var types = includePacks
            ? ContentVariantTypes.All.Concat(
                [ContentAssetTypes.LinkedinCopy, ContentAssetTypes.XiaohongshuPack])
            : ContentVariantTypes.All;

        foreach (var type in types)
        {
            var asset = await assets
                .FindForMilestoneAsync(owner.ScopeId, milestoneId, type, cancellationToken)
                .ConfigureAwait(false);

            if (asset is null || asset.CurrentRevision < 1)
            {
                continue;
            }

            var revision = await assets
                .FindRevisionAsync(owner.ScopeId, asset.Id, asset.CurrentRevision, cancellationToken)
                .ConfigureAwait(false);

            if (revision is null)
            {
                continue;
            }

            listed.Add(new VariantSummary(
                asset.Id,
                asset.Type,
                asset.CurrentRevision,
                revision.BodyHash,
                revision.EvidenceRevision,
                revision.VoiceRevision,
                asset.UpdatedAt));
        }

        return listed;
    }

    private sealed record PreparedVariant(Guid ProjectId, string MilestoneTitle);

    /// <summary>
    /// A stored row this build cannot read (500).
    /// </summary>
    private static ApiException UnreadableContentRow() =>
        new InternalFailureException(
            "A stored content revision could not be read by this build, so no variant can be "
            + "derived from it. The revision itself is unchanged.");
}
