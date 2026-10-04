using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Operations;
using Chronicleaf.Core;

namespace Chronicleaf.Application.Content;

/// <summary>
/// What an owner asks for when they ask a milestone to draft itself
/// (GROUNDED-MARKDOWN-DRAFTS-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The exact design shape, with one addition that is not optional.
/// <see cref="PreviewId"/> names the owner-authorized generation preview the draft will be built from.
/// The design's field list carries <c>providerId</c>, <c>model</c> and <c>consentHash</c>, and a
/// binding is not addressable on its own: the delivered generation contract is explicit that the hash
/// grants nothing by itself and the preview is what is looked up inside the caller's own scope. So the
/// request names the row and presents the hash, and the row is what decides.
/// </para>
/// <para>
/// <see cref="ProviderId"/> and <see cref="Model"/> are required and are checked against the preview.
/// They are not a second way to choose a provider - they are the request restating what it believes
/// the preview says, and a disagreement is refused rather than resolved in favour of one of them. That
/// is the same rule the delivered GitHub route applies to a body that disagrees with <c>If-Match</c>,
/// and it exists for the same reason: silently picking one of two contradictory claims is how a stale
/// client gets a draft written against input it did not describe.
/// </para>
/// </remarks>
/// <param name="PreviewId">The authorized generation preview to build from.</param>
/// <param name="ConsentHash">The SHA-256 binding the owner presented for that preview.</param>
/// <param name="ProviderId">The provider slot the owner believes the preview names.</param>
/// <param name="Model">The model the owner believes the preview names.</param>
/// <param name="EvidenceRevision">The evidence head revision the owner was looking at.</param>
/// <param name="VoiceRevision">The project voice profile revision the owner was looking at.</param>
/// <param name="IdempotencyKey">The caller's key, unique within the scope and kind.</param>
public sealed record ContentDraftRequest(
    Guid PreviewId,
    string? ConsentHash,
    string? ProviderId,
    string? Model,
    long EvidenceRevision,
    long VoiceRevision,
    string? IdempotencyKey);

/// <summary>The 202 a draft request answers with (GROUNDED-MARKDOWN-DRAFTS-001).</summary>
/// <param name="Operation">The operation that will do the generation, or the original of a replay.</param>
/// <param name="Created">False when this idempotency key had already asked for this draft.</param>
public sealed record ContentDraftAccepted(Operation Operation, bool Created)
{
    /// <summary>Where an owner reads this job's progress. Published rather than left to be built.</summary>
    public string OperationUrl => $"/api/operations/{Operation.Id:D}";
}

/// <summary>
/// What one draft job produced (GROUNDED-MARKDOWN-DRAFTS-001/002).
/// </summary>
/// <remarks>
/// <para>
/// It is the result reference, decoded. Every field is a fact about what was stored rather than about
/// the text: the asset, the revision, the two provenance values it is bound to, and the digest of the
/// exact bytes. <b>The body is not here</b> - it is read through the asset route, so a result reference
/// on an operation row can never carry a draft's prose into a queue, a log or a client that did not ask
/// for the asset.
/// </para>
/// <para>
/// <see cref="SummaryCalls"/> and <see cref="RepairCalls"/> are published because "a second call was
/// made to repair the answer" is a billing fact the owner is entitled to know before they look at the
/// text. A run that spent its repair call has spent the owner's money twice on one draft, and the panel
/// that renders this has to be able to say so rather than discover it on an invoice.
/// </para>
/// </remarks>
/// <param name="AssetId">The asset the revision belongs to.</param>
/// <param name="AssetRevision">The revision this run committed, from 1.</param>
/// <param name="EvidenceRevision">The evidence revision the claims were checked against.</param>
/// <param name="VoiceRevision">The voice profile revision the prose was written under.</param>
/// <param name="BodyHash">The digest of exactly the bytes that were stored.</param>
/// <param name="SummaryCalls">How many times the summary phase called the provider.</param>
/// <param name="RepairCalls">How many of those calls were repairs. At most one.</param>
/// <param name="CitationCount">How many footnote references the stored body used.</param>
/// <param name="Findings">The grounding findings a human has to judge before trusting the draft.</param>
public sealed record ContentDraftOutcome(
    Guid AssetId,
    long AssetRevision,
    long EvidenceRevision,
    long VoiceRevision,
    string BodyHash,
    int SummaryCalls,
    int RepairCalls,
    int CitationCount,
    IReadOnlyList<GroundingFinding> Findings);

/// <summary>
/// The current draft of one asset, as the read route returns it
/// (GROUNDED-MARKDOWN-DRAFTS-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The whole of what an owner needs to judge a draft, and nothing they do not. The summary is here
/// because it is the provenance - the claims, their evidence ids, and the limitations - and a reader
/// who only saw prose would be judging text whose sources they cannot see.
/// </para>
/// <para>
/// <b>Voice findings are computed, not stored.</b> They come from the delivered deterministic checker
/// run against the frozen voice snapshot the revision carries, which is the voice the prose was
/// actually written under rather than the profile's current one. Recomputing on every read means the
/// findings cannot drift from the text, and running the delivered rule set is what keeps this package
/// from inventing a second opinion about style.
/// </para>
/// <para>
/// <b>The status is <c>draft</c> and there is no field that could say otherwise.</b> Approval is the
/// review package's table, bound to a revision and a body hash; nothing here reports an approval that
/// has not happened.
/// </para>
/// </remarks>
/// <param name="Asset">The asset and its head revision.</param>
/// <param name="Current">The head revision, or <c>null</c> when the asset has no revision yet.</param>
/// <param name="ClaimMap">The claims, with the footnote ids the body cites.</param>
/// <param name="Findings">The grounding findings, for this text against this bundle.</param>
/// <param name="VoiceFindings">The delivered voice checker's findings, under the frozen snapshot.</param>
public sealed record ContentAssetView(
    ContentAsset Asset,
    ContentRevision? Current,
    IReadOnlyList<DraftClaim> ClaimMap,
    IReadOnlyList<GroundingFinding> Findings,
    IReadOnlyList<VoiceFinding> VoiceFindings);

/// <summary>
/// The Chronicleaf-owned side of the content tables (GROUNDED-MARKDOWN-DRAFTS-001/002).
/// </summary>
/// <remarks>
/// Separate from the media and evidence stores because these rows are Chronicleaf's own. Every method is
/// scoped, and the scope is in the <c>WHERE</c> clause rather than in a filter applied afterwards.
/// </remarks>
public interface IContentAssetStore
{
    /// <summary>
    /// Reads one asset inside a scope, or <c>null</c> when it is absent or owned by another scope.
    /// </summary>
    Task<ContentAsset?> FindAsync(Guid scopeId, Guid assetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the asset a milestone holds for a kind, or <c>null</c> when it has none yet.
    /// </summary>
    /// <remarks>
    /// The unique index on (scope, milestone, type) is what makes this a single row, and it is read
    /// rather than inferred from a list, so a milestone can have exactly one draft story and the
    /// discovery route for the panel does not have to scan.
    /// </remarks>
    Task<ContentAsset?> FindForMilestoneAsync(
        Guid scopeId,
        Guid milestoneId,
        string type,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the asset row for a milestone's first revision, or reports that it already exists.
    /// </summary>
    /// <remarks>
    /// The decision is the database's, made atomically by the unique index, so two jobs that both
    /// decided there was no asset produce one row and one conflict rather than two assets.
    /// </remarks>
    /// <returns>True when this call created the row.</returns>
    Task<bool> TryCreateAsync(ContentAsset asset, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a revision and advances the asset's head, only when the head is still
    /// <paramref name="expectedRevision"/>.
    /// </summary>
    /// <remarks>
    /// One statement for both writes on purpose. A revision whose head was not advanced would be
    /// unreachable through the asset, and an advanced head with no revision would point at nothing - so
    /// the compare-and-set and the insert commit together or not at all, and the caller learns which
    /// happened from the row count.
    /// </remarks>
    /// <returns>True when the revision became the head.</returns>
    Task<bool> TryAppendRevisionAsync(
        ContentRevision revision,
        long expectedRevision,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one revision inside a scope, or <c>null</c>.</summary>
    Task<ContentRevision?> FindRevisionAsync(
        Guid scopeId,
        Guid assetId,
        long revision,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Answers the availability of each requested evidence id inside one frozen bundle, with
    /// <c>null</c> for an id the bundle does not hold.
    /// </summary>
    /// <remarks>
    /// A content-package read over Chronicleaf's own table rather than a new method on the delivered
    /// evidence contract. Adding to that interface would change a package this one depends on, and the
    /// only thing needed here is one bounded question about one bundle: which of these ids does it hold,
    /// and what does each say about itself.
    /// </remarks>
    Task<IReadOnlyDictionary<Guid, string?>> ResolveAvailabilityAsync(
        Guid scopeId,
        Guid bundleId,
        IReadOnlyList<Guid> evidenceIds,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The request-row side of the draft storage (GROUNDED-MARKDOWN-DRAFTS-001/002).
/// </summary>
public interface IDraftRequestStore
{
    /// <summary>
    /// Reads the request a job was created from, or <c>null</c> when there is none in this scope.
    /// </summary>
    /// <remarks>
    /// A job is handed an operation id and nothing else, so this is the only way it can learn what was
    /// authorized. A job whose row is gone says so as a permanent failure rather than guessing at an
    /// evidence selection, which would be a draft built from nobody's authorization.
    /// </remarks>
    Task<ContentDraftRequestRow?> FindByOperationAsync(
        Guid scopeId,
        Guid operationId,
        CancellationToken cancellationToken = default);

    /// <summary>Inserts a request row. Returns false when this job already has one.</summary>
    Task<bool> TryInsertAsync(ContentDraftRequestRow row, CancellationToken cancellationToken = default);
}

/// <summary>
/// The grounded-draft use cases the API layer and the job call (GROUNDED-MARKDOWN-DRAFTS-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Two halves and a wall between them. <see cref="RequestDraftAsync"/> answers a request and creates a
/// job; it decides everything that can be decided with no provider and no money spent, so an owner
/// learns that their milestone is not confirmed, or that nothing in their evidence was collected, before
/// they have read a redacted snapshot. <see cref="RunAsync"/> is the job, and it is the only code in this
/// repository that reaches a provider for a draft.
/// </para>
/// <para>
/// <b>Nothing here approves anything.</b> The result is a draft with a status of <c>draft</c>, and the
/// review package owns approval as a table bound to a revision and a body hash. A method here that could
/// move a draft to an approved state would be a method that could be called by a background job, and that
/// is the specific shape of the defect the package split exists to prevent.
/// </para>
/// </remarks>
public interface IContentDraftService
{
    /// <summary>
    /// Validates a draft request and creates the job that will run it.
    /// </summary>
    /// <remarks>
    /// The milestone must be confirmed, the preview must be this owner's, unexpired, bound to this
    /// milestone, and its consent must match the presented hash; the provider and model must agree with
    /// it; the evidence and voice revisions must be the current ones; and something in the selected
    /// evidence must have been collected. Every one of those is decided before a job exists.
    /// </remarks>
    /// <exception cref="ResourceNotFoundException">The milestone or the preview is absent from this scope (404).</exception>
    /// <exception cref="ProjectArchivedException">The milestone's project is archived (409).</exception>
    /// <exception cref="MilestoneNotConfirmedException">The milestone is not confirmed (422).</exception>
    /// <exception cref="DraftGroundingMissingException">Nothing in the selection was collected (422).</exception>
    /// <exception cref="ConsentStaleException">The consent no longer covers the input (409).</exception>
    /// <exception cref="ValidationFailedException">A field is not one this package accepts (422).</exception>
    Task<ContentDraftAccepted> RequestDraftAsync(
        OwnerScope owner,
        Guid milestoneId,
        ContentDraftRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Performs the work of one draft job, and returns what it committed.</summary>
    Task<ContentDraftOutcome> RunAsync(
        OwnerScope owner,
        Guid operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one asset with its head revision, its claims, its grounding findings and its voice
    /// findings.
    /// </summary>
    /// <exception cref="ResourceNotFoundException">The asset is absent or in another scope (404).</exception>
    Task<ContentAssetView> ReadAssetAsync(
        OwnerScope owner,
        Guid assetId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the asset a milestone holds, or <c>null</c> when it has none.
    /// </summary>
    /// <remarks>
    /// The discovery route a panel needs, and deliberately nullable rather than a 404: "this milestone
    /// has no draft yet" is a normal state on a fresh installation and is not a missing resource. The
    /// milestone itself is still read and still 404s when it is absent or in another scope, so the
    /// empty answer cannot be used to probe for another owner's milestones.
    /// </remarks>
    /// <exception cref="ResourceNotFoundException">The milestone is absent or in another scope (404).</exception>
    Task<ContentAssetView?> ReadAssetForMilestoneAsync(
        OwnerScope owner,
        Guid milestoneId,
        CancellationToken cancellationToken = default);
}
