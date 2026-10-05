using Chronicleaf.Application.Milestones;
using Chronicleaf.Core;

namespace Chronicleaf.Application.Content;

/// <summary>
/// What an owner asks for when they edit a draft's text (MARKDOWN-REVIEW-EXPORT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// An edit never rewrites a revision: it appends a new one carrying the owner's words. The title
/// is the summary's milestone title rewritten, so the claim map survives an edit that only
/// rewords the prose - and a body that drops every citation is stored with that fact visible in
/// its findings rather than refused, because these are the owner's words and a refusal would be
/// this system editing by other means.
/// </para>
/// </remarks>
/// <param name="ExpectedRevision">The head revision the owner was looking at.</param>
/// <param name="BodyMarkdown">The full edited Markdown, replacing the stored body.</param>
/// <param name="Title">The edited title, or null to keep the stored one.</param>
public sealed record EditContentRequest(
    long ExpectedRevision,
    string? BodyMarkdown,
    string? Title);

/// <summary>
/// What an owner asks for when they approve a revision (MARKDOWN-REVIEW-EXPORT-001).
/// </summary>
/// <remarks>
/// <para>
/// Both revisions travel in the body because both must be current: the content revision has to be
/// the asset's head and the evidence revision has to be the bundle's head. Approving anything else
/// would bind a signature to text or evidence the owner was not looking at.
/// </para>
/// <para>
/// <see cref="VoiceFindingsAcknowledged"/> exists so a client can state it, and the service
/// refuses it when set: the design requires blocking findings to be corrected, and a flag that
/// cleared them would be the silent bypass it forbids.
/// </para>
/// </remarks>
/// <param name="ContentRevision">The revision being approved.</param>
/// <param name="EvidenceRevision">The evidence head revision the approver was looking at.</param>
/// <param name="VoiceFindingsAcknowledged">Must be false; true is refused.</param>
public sealed record ApproveContentRequest(
    long ContentRevision,
    long EvidenceRevision,
    bool VoiceFindingsAcknowledged);

/// <summary>
/// One edited revision, as the edit route answers it (MARKDOWN-REVIEW-EXPORT-001).
/// </summary>
/// <param name="AssetId">The asset the revision belongs to.</param>
/// <param name="Revision">The new head revision number.</param>
/// <param name="BodyHash">The digest of exactly the bytes that were stored.</param>
public sealed record EditedContentRevision(
    Guid AssetId,
    long Revision,
    string BodyHash);

/// <summary>
/// One recorded approval, as the approve route answers it (MARKDOWN-REVIEW-EXPORT-001).
/// </summary>
/// <param name="ApprovalId">This approval's identifier.</param>
/// <param name="AssetId">The asset the approved revision belongs to.</param>
/// <param name="ContentRevision">The approved revision number.</param>
/// <param name="PayloadKind">Always <c>text</c>: the only kind this package approves.</param>
/// <param name="BodyHash">The digest of exactly the bytes that were approved.</param>
/// <param name="EvidenceRevision">The evidence head revision the approval is bound to.</param>
/// <param name="ApprovedAt">When the approval was recorded; UTC.</param>
public sealed record ContentApprovalRecord(
    Guid ApprovalId,
    Guid AssetId,
    long ContentRevision,
    string PayloadKind,
    string BodyHash,
    long EvidenceRevision,
    DateTimeOffset ApprovedAt);

/// <summary>
/// The review state of one asset, as the review route returns it
/// (MARKDOWN-REVIEW-EXPORT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The stored <c>status</c> column is the drafts package's and always reads <c>draft</c>; this is
/// the review state derived from the approvals table. <c>approved</c> means a live approval names
/// the head revision and the evidence it was bound to is still the head; <c>invalidated</c> means
/// an approval exists but no longer authorizes an export; <c>draft</c> means nothing was ever
/// approved.
/// </para>
/// </remarks>
/// <param name="AssetId">The asset this state is about.</param>
/// <param name="CurrentRevision">The asset's head revision.</param>
/// <param name="ReviewStatus">One of <c>draft</c>, <c>approved</c> or <c>invalidated</c>.</param>
/// <param name="Approval">The live approval, or null when there is none.</param>
public sealed record ContentReviewView(
    Guid AssetId,
    long CurrentRevision,
    string ReviewStatus,
    ContentApprovalRecord? Approval);

/// <summary>
/// One approved export, as the export route serves it (MARKDOWN-REVIEW-EXPORT-002).
/// </summary>
/// <param name="FileName">The attachment name, derived from the asset id and the revision.</param>
/// <param name="BodyMarkdown">The approved revision's exact stored bytes.</param>
/// <param name="ContentRevision">The revision the bytes are.</param>
/// <param name="BodyHash">The digest of exactly those bytes.</param>
public sealed record ApprovedContentExport(
    string FileName,
    string BodyMarkdown,
    long ContentRevision,
    string BodyHash);

/// <summary>
/// The Chronicleaf-owned side of the approvals table (MARKDOWN-REVIEW-EXPORT-001/002).
/// </summary>
public interface IContentReviewStore
{
    /// <summary>
    /// Reads the live approval for one revision inside a scope, or <c>null</c> when none is live.
    /// </summary>
    Task<ContentApproval?> FindLiveAsync(
        Guid scopeId,
        Guid assetId,
        long contentRevision,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads every approval for one asset inside a scope, newest first.
    /// </summary>
    Task<IReadOnlyList<ContentApproval>> ListForAssetAsync(
        Guid scopeId,
        Guid assetId,
        CancellationToken cancellationToken = default);

    /// <summary>Inserts an approval. Returns false when one already names this revision.</summary>
    Task<bool> TryInsertAsync(ContentApproval approval, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks every live approval for one asset invalidated with a reason. Returns how many moved.
    /// </summary>
    Task<int> InvalidateLiveAsync(
        Guid scopeId,
        Guid assetId,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The review use cases the API layer calls (MARKDOWN-REVIEW-EXPORT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here generates anything.</b> An edit appends a human revision beside the generated
/// ones; an approval records a decision about a stored revision; an export serves approved bytes.
/// There is no provider, no credential and no socket behind any of these, so a review stays
/// available when every provider is down.
/// </para>
/// </remarks>
public interface IContentReviewService
{
    /// <summary>
    /// Appends a human revision carrying the owner's edited body and title.
    /// </summary>
    /// <exception cref="ResourceNotFoundException">The asset is absent or in another scope (404).</exception>
    /// <exception cref="IfMatchRequiredException">No If-Match revision was presented (428).</exception>
    /// <exception cref="RevisionConflictException">The head moved since the owner read it (409).</exception>
    /// <exception cref="ValidationFailedException">The body or title is not one this package stores (422).</exception>
    Task<EditedContentRevision> EditAsync(
        OwnerScope owner,
        Guid assetId,
        long? presentedRevision,
        EditContentRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a human approval of the head revision against the head evidence.
    /// </summary>
    /// <exception cref="ResourceNotFoundException">The asset is absent or in another scope (404).</exception>
    /// <exception cref="RevisionConflictException">The named revision is not the head (409).</exception>
    /// <exception cref="ApprovalBlockedException">Deterministic findings remain (422).</exception>
    /// <exception cref="ValidationFailedException">The request names no approvable revision (422).</exception>
    Task<ContentApprovalRecord> ApproveAsync(
        OwnerScope owner,
        Guid assetId,
        ApproveContentRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Withdraws the live approval, keeping its row as history.
    /// </summary>
    /// <exception cref="ResourceNotFoundException">The asset is absent or in another scope (404).</exception>
    /// <exception cref="ApprovalRequiredException">Nothing live authorizes an export (409).</exception>
    Task<ContentApprovalRecord> UnapproveAsync(
        OwnerScope owner,
        Guid assetId,
        string? reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the review state of one asset.
    /// </summary>
    /// <exception cref="ResourceNotFoundException">The asset is absent or in another scope (404).</exception>
    Task<ContentReviewView> ReadReviewAsync(
        OwnerScope owner,
        Guid assetId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Serves the approved revision's exact bytes, only while a live approval authorizes them.
    /// </summary>
    /// <exception cref="ResourceNotFoundException">The asset is absent or in another scope (404).</exception>
    /// <exception cref="ApprovalRequiredException">Nothing live authorizes an export (409).</exception>
    /// <exception cref="ApprovalStaleException">The approval no longer describes the stored state (409).</exception>
    /// <exception cref="ValidationFailedException">The format is not one this package serves (422).</exception>
    Task<ApprovedContentExport> ExportAsync(
        OwnerScope owner,
        Guid assetId,
        string? format,
        long? revision,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Serves the approved revision as a sanitized portable HTML artifact, only while a live
    /// approval authorizes it (HTML-CONTENT-EXPORT-001/002).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The authorization is the review package's own: the same live approval, the same body hash,
    /// the same evidence head and the same media selection the Markdown export re-checks. This
    /// method reuses that check rather than redefining it, then renders through the export
    /// adapter. An export serves the approved revision's exact bytes in a second shape - it never
    /// approves, never edits and never publishes.
    /// </para>
    /// </remarks>
    /// <exception cref="ResourceNotFoundException">The asset is absent or in another scope (404).</exception>
    /// <exception cref="ApprovalRequiredException">Nothing live authorizes an export (409).</exception>
    /// <exception cref="ApprovalStaleException">The approval no longer describes the stored state (409).</exception>
    /// <exception cref="MediaUnavailableException">An approved picture's bytes are gone (409).</exception>
    /// <exception cref="Media.UnsupportedMediaTypeException">An approved picture is not a type
    /// this template packs (415).</exception>
    Task<ApprovedHtmlExport> ExportHtmlAsync(
        OwnerScope owner,
        Guid assetId,
        long? revision,
        CancellationToken cancellationToken = default);
}
