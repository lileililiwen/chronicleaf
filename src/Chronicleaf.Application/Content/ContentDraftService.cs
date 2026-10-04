using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chronicleaf.AI.Contracts;
using Chronicleaf.Application.AI;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Evidence;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Operations;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Voice;
using Chronicleaf.Core;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Application.Content;

/// <summary>
/// The grounded-draft use cases, over the delivered consent, evidence, voice, queue and audit
/// contracts (GROUNDED-MARKDOWN-DRAFTS-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>The two phases, and why there are two.</b> Phase 1 asks a provider for structure - claims, each
/// naming the evidence it came from - and this package validates that structure against the frozen
/// snapshot before any prose exists. Phase 2 asks for Markdown built from the accepted summary and the
/// ids it fixed, and this package checks the prose's citations against the claim map. The order is the
/// requirement, and it is also the only order that works: a model that invents something loses while
/// the invented thing is still a field a validator can read, rather than becoming a sentence in a
/// document an owner is about to believe.
/// </para>
/// <para>
/// <b>The provider is reached from exactly one method.</b> Everything else - the milestone's status, the
/// consent, the evidence revision, the voice revision, whether anything was collected - is decided
/// first, and all of it is decidable with no adapter, no credential and no socket. A stale consent
/// therefore costs the owner nothing.
/// </para>
/// <para>
/// <b>Nothing here logs prose.</b> The logger receives identifiers, codes and counts. The summary and
/// the body are read from a stored document, handed to a provider and written to a revision; neither
/// appears in a log line, in an exception message or in an audit detail, because all three outlive the
/// request and are readable more widely than the draft is.
/// </para>
/// <para>
/// <b>No repair call is spent on a grounding failure, and no provider failure is turned into a
/// draft.</b> The one repair call the design permits is spent only on a summary that broke its schema.
/// </para>
/// </remarks>
public sealed class ContentDraftService(
    IMilestoneStore milestones,
    IProjectStore projects,
    IEvidenceStore evidence,
    IVoiceProfileService voice,
    IGenerationPreviewStore previews,
    ILLMProviderResolver providers,
    IContentAssetStore assets,
    IDraftRequestStore requests,
    IOperationService operations,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    TimeProvider clock,
    GenerationCallOptions call,
    ILogger<ContentDraftService> logger) : IContentDraftService
{
    /// <summary>
    /// The job kind this package owns. The leading segment names the delivered target it binds to: a
    /// milestone, so the delivered supersede check reads the milestone's own revision and turns a
    /// milestone edited mid-run into a superseded operation rather than a commit against a record
    /// somebody else has since changed.
    /// </summary>
    public const string DraftOperationKind = "milestone.grounded-draft";

    /// <summary>The audit action recorded when a draft is asked for.</summary>
    public const string DraftRequestedAction = "content.draft.requested";

    /// <summary>The audit action recorded when a draft job committed a revision.</summary>
    public const string DraftCommittedAction = "content.draft.committed";

    /// <summary>The audit action recorded when a draft job ended without committing.</summary>
    public const string DraftRefusedAction = "content.draft.refused";

    private const int MaxIdempotencyKeyLength = 200;

    // ==============================================================================================
    // Asking for a draft.
    // ==============================================================================================

    /// <inheritdoc />
    public async Task<ContentDraftAccepted> RequestDraftAsync(
        OwnerScope owner,
        Guid milestoneId,
        ContentDraftRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        if (request.PreviewId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "previewId",
                "A draft is built from an authorized generation preview, so name the preview.");
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey) ||
            request.IdempotencyKey.Length > MaxIdempotencyKeyLength)
        {
            throw new ValidationFailedException(
                "idempotencyKey",
                $"An idempotency key must be 1 to {MaxIdempotencyKeyLength} characters.");
        }

        if (request.EvidenceRevision < 1)
        {
            throw new ValidationFailedException(
                "evidenceRevision",
                "A draft is asked for against a committed evidence revision, so name one.");
        }

        if (request.VoiceRevision < 0)
        {
            throw new ValidationFailedException("voiceRevision", "A voice revision is zero or greater.");
        }

        // The milestone, its project and its own revision, all read before a job exists so a request
        // that could never run is refused here rather than as a failed job an owner has to go and read.
        var prepared = await unitOfWork.ExecuteAsync(
            async token =>
            {
                var milestone = await milestones.FindAsync(owner.ScopeId, milestoneId, token).ConfigureAwait(false)
                    ?? throw new ResourceNotFoundException("milestone");

                var project = await projects
                    .FindAsync(owner.ScopeId, milestone.ProjectId, token)
                    .ConfigureAwait(false)
                    ?? throw new ResourceNotFoundException("project");

                if (project.ArchivedAt is not null)
                {
                    throw new ProjectArchivedException();
                }

                // Only a confirmed milestone counts as history, so only a confirmed milestone gets a
                // story written about it. The check is here rather than at commit, so an owner finds out
                // before they have read a redacted snapshot and are about to press the button that
                // spends money - and before a job exists that could only ever fail.
                if (!string.Equals(milestone.Status, MilestoneStatus.Confirmed, StringComparison.Ordinal))
                {
                    throw new MilestoneNotConfirmedException(milestone.Status);
                }

                return new PreparedDraft(milestone.ProjectId, milestone.Revision);
            },
            cancellationToken).ConfigureAwait(false);

        // The consent is verified here, at the request, and again at commit. Verifying it twice is the
        // point rather than the duplication: an owner can press the button, walk away, and come back to
        // a job that took minutes - and in between the evidence they authorized may have been appended
        // to by a collection. The second check is the one that matters, and it costs one read.
        var preview = await previews
            .FindAsync(owner.ScopeId, request.PreviewId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("generation preview");

        var revoked = ConsentVerification.Revoke(preview, owner.ScopeId, request.ConsentHash, clock.GetUtcNow());
        if (revoked is not null)
        {
            throw new ConsentStaleException(revoked);
        }

        var snapshot = ConsentVerification.SnapshotOf(preview);

        if (!string.Equals(snapshot.Purpose, GenerationPreviewPurposes.MilestoneStory, StringComparison.Ordinal) ||
            !string.Equals(snapshot.AggregateRef, milestoneId.ToString("d"), StringComparison.Ordinal))
        {
            // A preview for a different aggregate is not a preview this route can build from. The row is
            // real and this owner's, so this is a 422 about the request rather than a 404 about a
            // missing resource - and the message says what a milestone draft must be based on.
            throw new ValidationFailedException(
                "previewId",
                "This preview is not a milestone story for the milestone in the address. Authorize a "
                + "milestone story preview for it first.");
        }

        if (!string.Equals(snapshot.ProviderId, request.ProviderId, StringComparison.Ordinal) ||
            !string.Equals(snapshot.Model, request.Model, StringComparison.Ordinal))
        {
            // A body that disagrees with the authorized row is refused rather than resolved in favour of
            // one of them. Picking the body would let a stale client describe a draft it never asked for.
            throw new ValidationFailedException(
                "providerId",
                "The provider and model must be the ones this preview is bound to. Read the preview again "
                + "and use the values it reports.");
        }

        if (snapshot.EvidenceRevision != request.EvidenceRevision)
        {
            throw new ValidationFailedException(
                "evidenceRevision",
                "The evidence revision must be the one this preview is bound to. Read the evidence again "
                + "and preview again.");
        }

        if (snapshot.VoiceRevision != request.VoiceRevision)
        {
            throw new ValidationFailedException(
                "voiceRevision",
                "The voice revision must be the one this preview is bound to. Read the voice again and "
                + "preview again.");
        }

        // The head bundle has to be the one the preview froze, and it has to hold something that was
        // actually collected. The delivered preview already refused a milestone with no evidence at all;
        // this is the narrower and later question, and the one the design answers with "record manual
        // notes before generation".
        var head = await evidence.FindHeadAsync(owner.ScopeId, milestoneId, cancellationToken).ConfigureAwait(false)
            ?? throw new ValidationFailedException(
                "evidenceRevision",
                "This milestone has no evidence, so there is nothing to draft from. Record evidence first.");

        if (head.Id != snapshot.EvidenceBundleId || head.Revision != snapshot.EvidenceRevision)
        {
            throw new ValidationFailedException(
                "evidenceRevision",
                "The evidence changed since it was shown. Read the new evidence and preview again.");
        }

        if (!GroundingPresent(head, snapshot.InputEvidenceIds))
        {
            throw new DraftGroundingMissingException();
        }

        var accepted = await operations.EnqueueAsync(
            new EnqueueOperationRequest(
                owner.ScopeId,
                DraftOperationKind,
                milestoneId,
                prepared.MilestoneRevision,
                request.IdempotencyKey.Trim()),
            cancellationToken).ConfigureAwait(false);

        // The request row is written after the job exists, because it names the job. A replay under the
        // same key gets the original job back and its own insert changes no row, so one press of the
        // button can never grow two request rows.
        var row = new ContentDraftRequestRow(
            Guid.NewGuid(),
            owner.ScopeId,
            accepted.Operation.Id,
            prepared.ProjectId,
            milestoneId,
            request.PreviewId,
            request.ConsentHash!,
            request.EvidenceRevision,
            request.VoiceRevision,
            snapshot.ProviderId,
            snapshot.Model,
            clock.GetUtcNow());

        var inserted = await unitOfWork.ExecuteAsync(
            async token =>
            {
                var created = await requests.TryInsertAsync(row, token).ConfigureAwait(false);

                if (created)
                {
                    audit.Record(new DomainAudit(
                        owner.ActorId,
                        DraftRequestedAction,
                        row.Id,
                        request.EvidenceRevision,
                        $"draft requested;provider={snapshot.ProviderId};model={snapshot.Model};"));
                }

                return created;
            },
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Grounded draft requested for milestone {MilestoneId} as operation {OperationId} ({Created}).",
            milestoneId,
            accepted.Operation.Id,
            inserted);

        return new ContentDraftAccepted(accepted.Operation, accepted.Created);
    }

    // ==============================================================================================
    // Running the job.
    // ==============================================================================================

    /// <inheritdoc />
    public async Task<ContentDraftOutcome> RunAsync(
        OwnerScope owner,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        // A job is handed an operation id and nothing else, so the words the owner typed are read back
        // from the request row. A job whose row is gone cannot be drafted honestly and says so rather
        // than guessing at an evidence selection, which would be a draft built from nobody's consent.
        var row = await requests
            .FindByOperationAsync(owner.ScopeId, operationId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new OperationPermanentException(ContentError.RequestMissing);

        var preview = await previews
            .FindAsync(owner.ScopeId, row.PreviewId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new OperationPermanentException(ContentError.Superseded);

        var revoked = ConsentVerification.Revoke(preview, owner.ScopeId, row.ConsentHash, clock.GetUtcNow());
        if (revoked is not null)
        {
            // The consent's fifteen minutes ran out, or the stored bytes are no longer the bytes the
            // owner saw, between pressing the button and the job running. Neither is worth a second
            // attempt from the same authorized snapshot: the owner asks again, which re-runs the whole
            // consent against what is current.
            throw new OperationPermanentException(ContentError.Superseded);
        }

        var snapshot = ConsentVerification.SnapshotOf(preview);

        if (!string.Equals(snapshot.Purpose, GenerationPreviewPurposes.MilestoneStory, StringComparison.Ordinal) ||
            !string.Equals(snapshot.AggregateRef, row.MilestoneId.ToString("d"), StringComparison.Ordinal) ||
            !string.Equals(snapshot.ProviderId, row.ProviderId, StringComparison.Ordinal) ||
            !string.Equals(snapshot.Model, row.Model, StringComparison.Ordinal) ||
            snapshot.EvidenceRevision != row.EvidenceRevision ||
            snapshot.VoiceRevision != row.VoiceRevision)
        {
            throw new OperationPermanentException(ContentError.Superseded);
        }

        // The authorized evidence, read out of the stored snapshot's own bytes. It is not re-read from
        // the evidence store here: the frozen document is what the owner read and consented to, and
        // re-reading would be the "make it look current" path the whole generation contract forbids.
        var authorized = ReadAuthorizedEvidence(snapshot);

        var adapter = ResolveAdapter(snapshot.ProviderId, snapshot.Model);

        // --- Phase 1: the structured summary. ------------------------------------------------------------
        var summaryCalls = 0;
        var repairCalls = 0;

        var summaryText = await CallOnceAsync(
                adapter,
                SummaryRequest(owner.ScopeId, snapshot, row.MilestoneId),
                cancellationToken)
            .ConfigureAwait(false);
        summaryCalls++;

        var structure = GroundedSummarySchema.ValidateStructure(summaryText);

        if (structure is not null)
        {
            // The one repair call the design permits, and the only thing it is ever spent on. It goes to
            // the same provider with the same authorized bytes, and it says which rule was broken
            // without quoting what the provider answered. The cost is recorded on the outcome, because
            // a second call on one draft is a second thing the owner may be billed for.
            logger.LogWarning(
                "Event=content.draft_summary_repair Provider={Provider} Reason={Reason}",
                snapshot.ProviderId,
                structure);

            var repaired = await CallOnceAsync(
                    adapter,
                    RepairRequest(owner.ScopeId, snapshot, row.MilestoneId, structure),
                    cancellationToken)
                .ConfigureAwait(false);

            summaryCalls++;
            repairCalls = 1;

            structure = GroundedSummarySchema.ValidateStructure(repaired);

            if (structure is not null)
            {
                await RecordRefusalAsync(owner, row, ContentError.InvalidSummary, summaryCalls, repairCalls, cancellationToken)
                    .ConfigureAwait(false);

                throw new OperationPermanentException(ContentError.InvalidSummary);
            }

            summaryText = repaired;
        }

        var summary = GroundedSummarySchema.Read(summaryText);
        var claims = ClaimMap.Build(summary);

        // Grounding is decided before any prose is asked for, and a failure here is terminal for the
        // run. A repair call would be a second request to the same model to reconsider having invented
        // something, which is not a fix and may be billed.
        var verdict = GroundingValidator.Validate(claims, authorized.Ids, authorized.TextById);
        if (verdict.Failed)
        {
            await RecordRefusalAsync(owner, row, verdict.FailureCode!, summaryCalls, repairCalls, cancellationToken)
                .ConfigureAwait(false);

            throw new OperationPermanentException(verdict.FailureCode!);
        }

        // --- Phase 2: the Markdown body. ----------------------------------------------------------------
        var envelope = await CallOnceAsync(
                adapter,
                BodyRequest(owner.ScopeId, snapshot, row.MilestoneId, claims),
                cancellationToken)
            .ConfigureAwait(false);

        // The phase-2 shape is read here rather than handed straight to the validator, because the
        // validator checks Markdown and the provider answered with a single-field object. A provider that
        // ignored the shape is a permanent failure and no repair call: the design permits a repair for
        // invalid summary JSON, and a second call asking the same model the same question is a second
        // thing the owner may be billed for.
        var body = ContentPrompt.ReadBodyMarkdown(envelope);
        if (body is null)
        {
            logger.LogWarning(
                "Event=content.draft_body_envelope_invalid Provider={Provider}",
                snapshot.ProviderId);

            await RecordRefusalAsync(owner, row, ContentError.ProviderInvalid, summaryCalls, repairCalls, cancellationToken)
                .ConfigureAwait(false);

            throw new OperationPermanentException(ContentError.ProviderInvalid);
        }

        var markdown = MarkdownDraftValidator.Validate(
            body,
            claims,
            summary.KnownLimitations,
            authorized.AllText);

        if (markdown.FailureCode is not null)
        {
            await RecordRefusalAsync(owner, row, markdown.FailureCode, summaryCalls, repairCalls, cancellationToken)
                .ConfigureAwait(false);

            throw new OperationPermanentException(markdown.FailureCode);
        }

        // --- Commit. ---------------------------------------------------------------------------------
        // Scope, time and revision are checked again here, immediately before the write, because the
        // job spent two provider calls in between and any of them could have been long enough for
        // somebody else to have appended evidence, saved a voice or edited the milestone. The delivered
        // conditional commit is the authority on the milestone; these three are the ones it cannot see.
        var head = await evidence.FindHeadAsync(owner.ScopeId, row.MilestoneId, cancellationToken).ConfigureAwait(false);

        if (head is null ||
            head.Id != snapshot.EvidenceBundleId ||
            head.Revision != snapshot.EvidenceRevision ||
            !ContainsAll(head, snapshot.InputEvidenceIds))
        {
            logger.LogInformation(
                "Grounded draft {OperationId} was superseded: the evidence moved from revision {Revision}.",
                operationId,
                snapshot.EvidenceRevision);

            throw new OperationPermanentException(ContentError.Superseded);
        }

        var currentVoice = await voice
            .GetAsync(owner.ScopeId, row.ProjectId, cancellationToken)
            .ConfigureAwait(false);

        if (currentVoice.Revision != snapshot.VoiceRevision)
        {
            logger.LogInformation(
                "Grounded draft {OperationId} was superseded: the voice profile moved from revision {Revision}.",
                operationId,
                snapshot.VoiceRevision);

            throw new OperationPermanentException(ContentError.Superseded);
        }

        (Guid AssetId, long Revision) committed;
        try
        {
            committed = await CommitAsync(
                owner,
                row,
                snapshot,
                currentVoice,
                summary,
                body,
                summaryCalls,
                repairCalls,
                markdown.CitationCount,
                cancellationToken).ConfigureAwait(false);
        }
        catch (SupersededCommitException)
        {
            // Somebody else advanced this asset's head between the read above and the write. Nothing was
            // committed - the transaction rolls the asset row back too - and the previous draft is
            // exactly as it was. From the owner's side that is the same event as a moved evidence
            // bundle, so it gets the same terminal state and the same code.
            logger.LogInformation(
                "Grounded draft {OperationId} was superseded: the asset's head revision moved before the commit.",
                operationId);

            await RecordRefusalAsync(owner, row, ContentError.Superseded, summaryCalls, repairCalls, cancellationToken)
                .ConfigureAwait(false);

            throw new OperationPermanentException(ContentError.Superseded);
        }

        logger.LogInformation(
            "Grounded draft {OperationId} committed asset {AssetId} revision {Revision} with {Citations} citations.",
            operationId,
            committed.Item1,
            committed.Item2,
            markdown.CitationCount);

        return new ContentDraftOutcome(
            committed.Item1,
            committed.Item2,
            snapshot.EvidenceRevision,
            snapshot.VoiceRevision,
            ContentRevision.Hash(body),
            summaryCalls,
            repairCalls,
            markdown.CitationCount,
            verdict.Findings);
    }

    /// <summary>
    /// Writes the asset and its revision, and the audit row, in one transaction.
    /// </summary>
    /// <remarks>
    /// Split out of <see cref="RunAsync"/> so the transaction body reads as one decision rather than as
    /// the tail of a long method: find or create the asset, compare-and-set the head, insert the
    /// revision, record what was decided. Every one of those is inside the same unit of work, so a
    /// failure anywhere leaves the previous draft exactly as it was - including the case where this run
    /// created the asset row itself and then lost the head.
    /// </remarks>
    private async Task<(Guid AssetId, long Revision)> CommitAsync(
        OwnerScope owner,
        ContentDraftRequestRow row,
        GenerationSnapshot snapshot,
        VoiceProfileView currentVoice,
        GroundedSummary summary,
        string body,
        int summaryCalls,
        int repairCalls,
        int citationCount,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        return await unitOfWork.ExecuteAsync(
            async token =>
            {
                var asset = await assets
                    .FindForMilestoneAsync(owner.ScopeId, row.MilestoneId, ContentAssetTypes.MarkdownStory, token)
                    .ConfigureAwait(false);

                long expected;

                if (asset is null)
                {
                    // The asset row and its first revision commit together or not at all. A committed
                    // asset with no revision would be a milestone that appears to have a draft and has
                    // none, which is the one state a panel cannot explain.
                    var created = ContentAsset.Create(
                        Guid.NewGuid(),
                        owner.ScopeId,
                        row.ProjectId,
                        row.MilestoneId,
                        now);

                    if (await assets.TryCreateAsync(created, token).ConfigureAwait(false))
                    {
                        asset = created;
                        expected = 0;
                    }
                    else
                    {
                        asset = await assets
                            .FindForMilestoneAsync(owner.ScopeId, row.MilestoneId, ContentAssetTypes.MarkdownStory, token)
                            .ConfigureAwait(false)
                            ?? throw new SupersededCommitException();
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
                    snapshot.EvidenceBundleId,
                    snapshot.EvidenceRevision,
                    snapshot.VoiceRevision,
                    CurrentVoiceSnapshotJson(currentVoice, now),
                    summary.ToCanonicalJson(),
                    body,
                    ContentRevision.Hash(body),
                    snapshot.ProviderId,
                    snapshot.Model,
                    ContentLimits.PromptVersion,
                    GroundedSummarySchema.Version,
                    now);

                if (!await assets
                        .TryAppendRevisionAsync(revision, expected, now, token)
                        .ConfigureAwait(false))
                {
                    // Another job advanced the head between this read and this write. Nothing was
                    // committed - the transaction rolls the asset row back too - and the previous draft
                    // is exactly as it was.
                    throw new SupersededCommitException();
                }

                audit.Record(new DomainAudit(
                    ActorId: null,
                    Action: DraftCommittedAction,
                    TargetId: asset.Id,
                    Revision: revision.Revision,
                    // Identifiers, revisions and counts. Not the summary, not the body, not one byte of
                    // the evidence: this row is readable by anyone with audit access and it outlives
                    // the request by years.
                    Detail: $"milestone={row.MilestoneId:D};assetRevision={revision.Revision.ToString(CultureInfo.InvariantCulture)};"
                        + $"evidenceRevision={snapshot.EvidenceRevision.ToString(CultureInfo.InvariantCulture)};"
                        + $"voiceRevision={snapshot.VoiceRevision.ToString(CultureInfo.InvariantCulture)};"
                        + $"provider={snapshot.ProviderId};model={snapshot.Model};"
                        + $"prompt={ContentLimits.PromptVersion};schema={GroundedSummarySchema.Version};"
                        + $"summaryCalls={summaryCalls};repairCalls={repairCalls};"
                        + $"citations={citationCount.ToString(CultureInfo.InvariantCulture)};"));

                return (asset.Id, revision.Revision);
            },
            cancellationToken).ConfigureAwait(false);
    }

    // ==============================================================================================
    // Reading a draft.
    // ==============================================================================================

    /// <inheritdoc />
    public async Task<ContentAssetView> ReadAssetAsync(
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

        return await DescribeAsync(owner, asset, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ContentAssetView?> ReadAssetForMilestoneAsync(
        OwnerScope owner,
        Guid milestoneId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        // The milestone is read first and scoped, so a milestone in another installation is a 404
        // rather than an empty draft that happens to look like "nothing generated yet".
        _ = await milestones.FindAsync(owner.ScopeId, milestoneId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("milestone");

        var asset = await assets
            .FindForMilestoneAsync(owner.ScopeId, milestoneId, ContentAssetTypes.MarkdownStory, cancellationToken)
            .ConfigureAwait(false);

        return asset is null ? null : await DescribeAsync(owner, asset, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the read view for one asset, deriving every finding from the stored revision rather than
    /// from anything remembered about how it was made.
    /// </summary>
    /// <remarks>
    /// Two of the three inputs are the stored provenance, not the world: the claims come from the
    /// summary the revision carries, and the availabilities come from the frozen bundle that revision
    /// names. So a finding list cannot drift from the draft - and a bundle that has since been appended
    /// to cannot change what this revision reported, because this revision's findings are about the
    /// revision, not about the milestone's current head.
    /// </remarks>
    private async Task<ContentAssetView> DescribeAsync(
        OwnerScope owner,
        ContentAsset asset,
        CancellationToken cancellationToken)
    {
        if (asset.CurrentRevision < 1)
        {
            return new ContentAssetView(asset, null, [], [], []);
        }

        var revision = await assets
            .FindRevisionAsync(owner.ScopeId, asset.Id, asset.CurrentRevision, cancellationToken)
            .ConfigureAwait(false)
            ?? throw UnreadableContentRow();

        var summary = GroundedSummary.FromCanonicalJson(revision.SummaryJson)
            ?? throw UnreadableContentRow();

        var voiceSnapshot = VoiceSnapshotCodec.Deserialize(revision.VoiceSnapshotJson)
            ?? throw UnreadableContentRow();

        var claims = ClaimMap.Build(summary);
        var cited = claims.SelectMany(claim => claim.EvidenceIds).Distinct().ToArray();
        var availability = await assets
            .ResolveAvailabilityAsync(owner.ScopeId, revision.EvidenceBundleId, cited, cancellationToken)
            .ConfigureAwait(false);

        return new ContentAssetView(
            asset,
            revision,
            claims,
            GroundingValidator.Order(GroundingValidator.ForStoredRevision(claims, availability)),
            VoiceCheckService.Check(voiceSnapshot.Values, summary.MilestoneTitle, revision.BodyMarkdown));
    }

    // ==============================================================================================
    // The provider call, and the two requests.
    // ==============================================================================================

    /// <summary>
    /// Makes exactly one provider call and turns anything but a usable answer into a classified job
    /// failure.
    /// </summary>
    /// <remarks>
    /// <b>No inner retry, deliberately.</b> The delivered queue already has a bounded backoff for a
    /// transient failure, and it retries the whole job - which is the honest level, because the job's
    /// input is a consent and a second attempt of the first phase inside one attempt would be a second
    /// thing the owner may be billed for. Every provider refusal here is permanent or rate-limited, so
    /// the delivered classification and backoff do the work.
    /// </remarks>
    private async Task<string> CallOnceAsync(
        ILLMProvider adapter,
        GenerationRequest request,
        CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(call.TimeoutSeconds));

        GenerationResult result;
        try
        {
            result = await adapter.GenerateAsync(request, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // The budget, not the owner. A timeout is ambiguous - the provider may have accepted the
            // request and started billing it - so it is reported and never retried inside this job.
            logger.LogWarning(
                "Event=content.draft_timeout Provider={Provider} Phase={Phase}",
                request.ProviderId,
                request.SystemInstruction.StartsWith("You are writing the Markdown", StringComparison.Ordinal)
                    ? "markdown"
                    : "summary");

            throw new OperationTransientException(OperationErrorCodes.Timeout);
        }

        if (result.Status == GenerationStatuses.Completed)
        {
            return result.Text!;
        }

        throw Classify(result);
    }

    /// <summary>Turns a non-completed provider result into the job's classified failure.</summary>
    private static OperationFailedException Classify(GenerationResult result)
    {
        if (string.Equals(result.Status, GenerationStatuses.Refused, StringComparison.Ordinal))
        {
            return new OperationPermanentException(ContentError.ProviderRefused);
        }

        if (string.Equals(result.ErrorCode, GenerationErrorCodes.CredentialRejected, StringComparison.Ordinal))
        {
            return new OperationPermanentException(ContentError.CredentialRejected);
        }

        if (string.Equals(result.ErrorCode, GenerationErrorCodes.RateLimited, StringComparison.Ordinal))
        {
            return new OperationRateLimitedException();
        }

        if (string.Equals(result.ErrorCode, GenerationErrorCodes.ProviderUnavailable, StringComparison.Ordinal))
        {
            return new OperationTransientException(OperationErrorCodes.ProviderUnavailable);
        }

        return new OperationPermanentException(ContentError.ProviderInvalid);
    }

    /// <summary>
    /// The adapter for the preview's slot, or a permanent job failure naming the fact.
    /// </summary>
    /// <remarks>
    /// The delivered <c>provider_not_installed</c> and <c>model_not_supported</c> are envelope codes
    /// with statuses, and neither belongs in a job: raising them from a handler would leave the worker
    /// to classify an <c>ApiException</c> as transient and retry a refusal that will refuse again. So
    /// the same two facts are reported here as job codes instead.
    /// </remarks>
    private ILLMProvider ResolveAdapter(string providerId, string model)
    {
        var adapter = providers.Find(providerId);
        if (adapter is null)
        {
            throw new OperationPermanentException(ContentError.AdapterUnavailable);
        }

        if (!adapter.SupportsModel(model))
        {
            throw new OperationPermanentException(ContentError.ModelUnsupported);
        }

        return adapter;
    }

    private GenerationRequest SummaryRequest(Guid scopeId, GenerationSnapshot snapshot, Guid milestoneId) =>
        new()
        {
            RequestId = Guid.NewGuid(),
            ScopeId = scopeId,
            ProviderId = snapshot.ProviderId,
            Model = snapshot.Model,
            CredentialRef = snapshot.CredentialId,
            EndpointRef = snapshot.EndpointRef,
            SystemInstruction = ContentPrompt.SummaryInstruction(snapshot, milestoneId),
            UntrustedEvidenceJson = snapshot.UntrustedEvidenceJson,
            ResponseSchema = GroundedSummarySchema.Json,
            MaxOutputTokens = GenerationLimits.MaxOutputTokens,
            TimeoutSeconds = call.TimeoutSeconds,
        };

    private GenerationRequest RepairRequest(
        Guid scopeId,
        GenerationSnapshot snapshot,
        Guid milestoneId,
        string reason) =>
        new()
        {
            RequestId = Guid.NewGuid(),
            ScopeId = scopeId,
            ProviderId = snapshot.ProviderId,
            Model = snapshot.Model,
            CredentialRef = snapshot.CredentialId,
            EndpointRef = snapshot.EndpointRef,
            SystemInstruction = ContentPrompt.SummaryInstruction(snapshot, milestoneId, reason),
            UntrustedEvidenceJson = snapshot.UntrustedEvidenceJson,
            ResponseSchema = GroundedSummarySchema.Json,
            MaxOutputTokens = GenerationLimits.MaxOutputTokens,
            TimeoutSeconds = call.TimeoutSeconds,
        };

    /// <summary>
    /// The phase-2 request. Its untrusted section is the claim map, not the evidence.
    /// </summary>
    /// <remarks>
    /// The design says phase 2 generates Markdown "using only accepted summary and immutable input ids",
    /// and that is exactly what is sent: the accepted claims and the evidence ids they name. The
    /// evidence text is deliberately not repeated - it was already in the call that produced these
    /// claims, and a second copy of the whole payload would be a second thing to bill for with no new
    /// information in it.
    /// <para>
    /// And the claim map arrives as <em>data</em>, because it came from a model. It is the same
    /// untrusted-data treatment the evidence payload gets, and the instruction says so, which is what
    /// stops a claim whose text reads like an instruction from being obeyed in the prose phase.
    /// </para>
    /// </remarks>
    private GenerationRequest BodyRequest(
        Guid scopeId,
        GenerationSnapshot snapshot,
        Guid milestoneId,
        IReadOnlyList<DraftClaim> claims) =>
        new()
        {
            RequestId = Guid.NewGuid(),
            ScopeId = scopeId,
            ProviderId = snapshot.ProviderId,
            Model = snapshot.Model,
            CredentialRef = snapshot.CredentialId,
            EndpointRef = snapshot.EndpointRef,
            SystemInstruction = ContentPrompt.BodyInstruction(snapshot, claims),
            UntrustedEvidenceJson = ClaimMap.ToCanonicalJson(claims),
            ResponseSchema = ContentPrompt.MarkdownSchemaJson,
            MaxOutputTokens = GenerationLimits.MaxOutputTokens,
            TimeoutSeconds = call.TimeoutSeconds,
        };

    // ==============================================================================================
    // Helpers.
    // ==============================================================================================

    /// <summary>
    /// The authorized evidence, read out of the frozen snapshot's own bytes.
    /// </summary>
    /// <remarks>
    /// The ids, each item's text and the joined text of all of them. Built from the stored document
    /// rather than from the evidence store, so the grounding checks run against exactly what the owner
    /// read - and so a bundle that has since been appended to cannot make a claim look supported by
    /// material that was not part of the consent.
    /// </remarks>
    private static AuthorizedEvidence ReadAuthorizedEvidence(GenerationSnapshot snapshot)
    {
        var ids = new HashSet<Guid>();
        var textById = new Dictionary<Guid, string>();
        var all = new StringBuilder();

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(snapshot.UntrustedEvidenceJson);
        }
        catch (JsonException)
        {
            return new AuthorizedEvidence(ids, textById, string.Empty);
        }

        if (root is not JsonArray items)
        {
            return new AuthorizedEvidence(ids, textById, string.Empty);
        }

        foreach (var node in items)
        {
            if (node is not JsonObject item ||
                item["id"] is not JsonValue idValue ||
                !idValue.TryGetValue<string>(out var idText) ||
                !Guid.TryParse(idText, out var id))
            {
                continue;
            }

            var text = item["text"] is JsonValue textValue && textValue.TryGetValue<string>(out var value)
                ? value
                : string.Empty;

            ids.Add(id);
            textById[id] = text;
            all.Append(text).Append('\n');
        }

        return new AuthorizedEvidence(ids, textById, all.ToString());
    }

    /// <summary>
    /// True when at least one of the selected items actually carries something that was collected.
    /// </summary>
    /// <remarks>
    /// This is the design's "empty evidence requires manual notes before generation". An
    /// <c>unavailable</c> item holds the reason its source could not be read, not a fact, and a summary
    /// drawn from those would be a summary of refusals.
    /// </remarks>
    private static bool GroundingPresent(EvidenceBundle bundle, IReadOnlyList<Guid> selected)
    {
        var chosen = new HashSet<Guid>(selected);
        var present = false;

        foreach (var item in bundle.Items)
        {
            if (chosen.Contains(item.Id) && item.WasCollected)
            {
                present = true;
            }
        }

        return present;
    }

    private static bool ContainsAll(EvidenceBundle bundle, IReadOnlyList<Guid> ids)
    {
        var known = new HashSet<Guid>(bundle.Items.Select(item => item.Id));
        return ids.All(known.Contains);
    }

    /// <summary>
    /// The voice the prose was written under, as the delivered canonical snapshot JSON.
    /// </summary>
    /// <remarks>
    /// Copied as a value, from the resolved profile at this instant. That is the requirement: a profile
    /// edit afterwards produces a new revision for future work and cannot reach this revision, because
    /// there is no profile id in the stored bytes for anything to follow. The read path deserializes
    /// these same bytes and runs the delivered checker against them, so the voice findings on a stored
    /// draft are findings under the voice it was written with - not under whatever the profile says
    /// now.
    /// </remarks>
    private static string CurrentVoiceSnapshotJson(VoiceProfileView profile, DateTimeOffset now) =>
        VoiceSnapshotCodec.Serialize(new VoiceSnapshot(
            profile.Resolved.PresetId,
            profile.Revision,
            now,
            profile.Resolved.Effective));

    private async Task RecordRefusalAsync(
        OwnerScope owner,
        ContentDraftRequestRow row,
        string code,
        int summaryCalls,
        int repairCalls,
        CancellationToken cancellationToken)
    {
        await unitOfWork.ExecuteAsync(
            token =>
            {
                audit.Record(new DomainAudit(
                    ActorId: null,
                    Action: DraftRefusedAction,
                    TargetId: row.Id,
                    Revision: row.EvidenceRevision,
                    // The code and the counts, and nothing else. A grounding refusal says which rule
                    // fired; it does not say which claim, because the claim is the owner's evidence
                    // echoed back into a row that outlives the request.
                    Detail: $"code={code};milestone={row.MilestoneId:D};"
                        + $"summaryCalls={summaryCalls};repairCalls={repairCalls};"));
                return Task.FromResult(true);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private sealed record PreparedDraft(Guid ProjectId, long MilestoneRevision);

    private sealed record AuthorizedEvidence(
        IReadOnlySet<Guid> Ids,
        IReadOnlyDictionary<Guid, string> TextById,
        string AllText);

    /// <summary>
    /// The head revision moved between the read and the write, so nothing was committed.
    /// </summary>
    /// <remarks>
    /// A distinct type from every other refusal here because the reason a human would want to know is
    /// different: not "the input was wrong" but "somebody else got there first". It is caught by
    /// <see cref="RunAsync"/> and mapped to the one <see cref="ContentError.Superseded"/> code, which
    /// is the same terminal state a moved evidence bundle produces - because from the owner's side
    /// those are the same event.
    /// </remarks>
    private sealed class SupersededCommitException()
        : Exception("The draft's head revision moved before it could be committed.");

    /// <summary>
    /// A stored row this build cannot read (500).
    /// </summary>
    /// <remarks>
    /// The right answer rather than an empty draft, and a named one rather than a bare 500. A revision
    /// whose summary or voice snapshot cannot be decoded has lost the provenance that makes it
    /// reviewable, so showing its body beside an empty claim list and no findings would present
    /// ungrounded text as though nothing were wrong with it. The message says what is wrong and names no
    /// stored value.
    /// </remarks>
    private static ApiException UnreadableContentRow() =>
        new InternalFailureException(
            "A stored content revision could not be read by this build, so its provenance cannot be shown. "
            + "The revision itself is unchanged.");
}
