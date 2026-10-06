using Chronicleaf.Application.Common;
using Chronicleaf.Core;

namespace Chronicleaf.Application.Content;

/// <summary>
/// The codes this package adds to the delivered error envelope and to the operation row
/// (STATIC-BLOG-EXPORT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The envelope is the delivered <c>{code,message,traceId,fieldErrors?}</c> and its status mapping
/// is unchanged; this file is vocabulary, not a second envelope. Every message is authored here in
/// fixed words, because the middleware writes <c>Exception.Message</c> verbatim into the envelope.
/// </para>
/// <para>
/// The request refusals deliberately reuse the review and HTML export packages' vocabulary -
/// <c>approval_required</c>, <c>approval_stale</c> and <c>media_unavailable</c> - because the
/// condition and the remedy are the same and a second spelling would pretend otherwise. Only the
/// job's supersede has its own code, and like the drafts package's job codes it is written to the
/// operation row, never to a response body: no route answers an export that never completed.
/// </para>
/// </remarks>
public static class StaticBlogError
{
    /// <summary>
    /// An included approval no longer describes the stored state, so the run ended with no
    /// completed archive (operation row only). The remedy is a new approval, not a retry.
    /// </summary>
    public const string Superseded = "static_blog_superseded";
}

/// <summary>
/// What an owner sends when they ask for a static blog export (STATIC-BLOG-EXPORT-001/002).
/// </summary>
/// <param name="ApprovedAssetIds">The approved assets to depict, each named once.</param>
/// <param name="BasePath">The deployment base path, such as <c>/journey</c>; never a filesystem path.</param>
/// <param name="ExpectedSelectionHash">The selection hash the owner read, or null to skip the pin.</param>
/// <param name="IdempotencyKey">The caller's key, unique within (scope, kind).</param>
public sealed record StaticBlogExportRequest(
    IReadOnlyList<Guid> ApprovedAssetIds,
    string? BasePath,
    string? ExpectedSelectionHash,
    string? IdempotencyKey);

/// <summary>
/// One accepted export: the job that will build it (STATIC-BLOG-EXPORT-001/002).
/// </summary>
/// <param name="OperationId">The export job the owner watches.</param>
/// <param name="Created">False when an identical request had already created this job.</param>
public sealed record StaticBlogExportAccepted(Guid OperationId, bool Created);

/// <summary>
/// What one export job committed (STATIC-BLOG-EXPORT-002).
/// </summary>
/// <param name="OperationId">The job that ran.</param>
/// <param name="ContentSha256">Lowercase SHA-256 hex over the completed manifest bytes.</param>
/// <param name="PostCount">How many posts the archive depicts.</param>
/// <param name="ImageCount">How many distinct pictures the archive packs.</param>
public sealed record StaticBlogExportOutcome(
    Guid OperationId,
    string ContentSha256,
    int PostCount,
    int ImageCount);

/// <summary>
/// One export job as the status route answers it (STATIC-BLOG-EXPORT-001/002).
/// </summary>
/// <param name="OperationId">The job.</param>
/// <param name="Status">The wire status: queued, running, succeeded, failed, cancelled or superseded.</param>
/// <param name="ErrorCode">The redacted job code, when the job ended without an archive.</param>
/// <param name="ContentSha256">The completed manifest digest, when the job succeeded.</param>
/// <param name="PostCount">How many posts the archive depicts, when the job succeeded.</param>
/// <param name="ImageCount">How many distinct pictures the archive packs, when the job succeeded.</param>
public sealed record StaticBlogExportView(
    Guid OperationId,
    string Status,
    string? ErrorCode,
    string? ContentSha256,
    int? PostCount,
    int? ImageCount);

/// <summary>
/// One frozen per-asset binding: what the request saw and what the run must still see
/// (STATIC-BLOG-EXPORT-001/002).
/// </summary>
/// <param name="AssetId">The selected asset.</param>
/// <param name="MilestoneId">The milestone the asset belongs to.</param>
/// <param name="ContentRevision">The approved head revision at request time.</param>
/// <param name="BodyHash">The digest of exactly those approved bytes.</param>
/// <param name="EvidenceBundleId">The evidence bundle the revision is grounded in.</param>
/// <param name="EvidenceRevision">The evidence head revision the approval was bound to.</param>
/// <param name="MediaSelectionHash">The selected-pictures hash the approval was bound to.</param>
public sealed record FrozenAssetBinding(
    Guid AssetId,
    Guid MilestoneId,
    long ContentRevision,
    string BodyHash,
    Guid EvidenceBundleId,
    long EvidenceRevision,
    string MediaSelectionHash);

/// <summary>
/// The request-row side of the static blog export storage (STATIC-BLOG-EXPORT-001/002).
/// </summary>
public interface IStaticBlogExportRequestStore
{
    /// <summary>
    /// Reads the request a job was created from, or <c>null</c> when there is none in this scope.
    /// </summary>
    /// <remarks>
    /// A job is handed an operation id and nothing else, so this is the only way it can learn what
    /// was authorized. A job whose row is gone says so as a permanent failure rather than guessing
    /// at a selection, which would be an archive built from nobody's authorization.
    /// </remarks>
    Task<StaticBlogExportRequestRow?> FindByOperationAsync(
        Guid scopeId,
        Guid operationId,
        CancellationToken cancellationToken = default);

    /// <summary>Inserts a request row. Returns false when this job already has one.</summary>
    Task<bool> TryInsertAsync(StaticBlogExportRequestRow row, CancellationToken cancellationToken = default);
}

/// <summary>
/// The static blog export use cases the API layer and the job call (STATIC-BLOG-EXPORT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Two halves and a wall between them. <see cref="RequestExportAsync"/> answers a request and
/// creates a job; it decides everything that can be decided with no rendering, so an owner learns
/// that a selection is not approved, or that a picture is already gone, before a job exists that
/// could only ever fail. <see cref="RunAsync"/> is the job: it re-resolves every frozen binding
/// and renders the deterministic archive, and a binding that moved ends the run superseded with
/// no completed archive.
/// </para>
/// <para>
/// <b>Nothing here approves, generates or publishes anything.</b> The archive is a deterministic
/// rendering of approved bytes through the HTML export package's own sanitizing template, packed
/// under generated paths. Approval stays the review package's table, generation stays the drafts
/// package's, and no byte here reaches GitHub or any other host: the archive is a private download
/// until its owner deploys it somewhere.
/// </para>
/// </remarks>
public interface IStaticBlogExportService
{
    /// <summary>
    /// Validates a static blog export request and creates the job that will build it.
    /// </summary>
    /// <remarks>
    /// Every selected asset must carry a live approval that still binds the head revision, the
    /// body hash, the evidence head and the selected pictures; the milestones must be confirmed;
    /// the base path must be a relative URL segment. Every one of those is decided before a job
    /// exists.
    /// </remarks>
    /// <exception cref="ResourceNotFoundException">The project or an asset is absent from this scope (404).</exception>
    /// <exception cref="ApprovalRequiredException">A selected asset carries no live approval (409).</exception>
    /// <exception cref="ApprovalStaleException">A selected approval no longer describes the stored state (409).</exception>
    /// <exception cref="MediaUnavailableException">An approved picture's bytes are gone (409).</exception>
    /// <exception cref="MilestoneNotConfirmedException">A selected milestone is not confirmed (422).</exception>
    /// <exception cref="ValidationFailedException">The selection, base path or key is not one this package accepts (422).</exception>
    Task<StaticBlogExportAccepted> RequestExportAsync(
        Milestones.OwnerScope owner,
        Guid projectId,
        StaticBlogExportRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Performs the work of one export job, and returns what it committed.</summary>
    Task<StaticBlogExportOutcome> RunAsync(
        Milestones.OwnerScope owner,
        Guid operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads one export job inside a scope and project.
    /// </summary>
    /// <exception cref="ResourceNotFoundException">The job is absent, foreign or not an export (404).</exception>
    Task<StaticBlogExportView> ReadExportAsync(
        Milestones.OwnerScope owner,
        Guid projectId,
        Guid operationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renders the completed archive for download, re-checking every binding first.
    /// </summary>
    /// <remarks>
    /// The bytes are rendered deterministically from the approved state rather than stored, so a
    /// download after a revocation is refused instead of serving an archive whose authorization
    /// has lapsed. The digest of the served bytes is the job's committed digest: anything else is
    /// an internal failure, not a download.
    /// </remarks>
    /// <exception cref="ResourceNotFoundException">The job is absent, foreign or not an export (404).</exception>
    /// <exception cref="ApprovalRequiredException">No live approval authorizes the download (409).</exception>
    /// <exception cref="ApprovalStaleException">The job never completed, or its bindings moved (409).</exception>
    /// <exception cref="MediaUnavailableException">An approved picture's bytes are gone (409).</exception>
    Task<StaticBlogDownload> RenderDownloadAsync(
        Milestones.OwnerScope owner,
        Guid projectId,
        Guid operationId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One completed archive as the download route serves it (STATIC-BLOG-EXPORT-002).
/// </summary>
/// <param name="FileName">The attachment name, generated from the project id and the digest.</param>
/// <param name="Bytes">The deterministic ZIP bytes.</param>
/// <param name="ContentSha256">Lowercase SHA-256 hex over the completed manifest bytes.</param>
public sealed record StaticBlogDownload(string FileName, byte[] Bytes, string ContentSha256);
