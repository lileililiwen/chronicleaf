using Chronicleaf.Application.Milestones;

namespace Chronicleaf.Application.Medium;

/// <summary>
/// One approved article as a manual Medium import package (MEDIUM-IMPORT-PACKAGE-001).
/// </summary>
/// <param name="FileName">The attachment name, carrying the asset id and revision.</param>
/// <param name="ContentType">Always the ZIP media type: the article, its pictures and their manifest.</param>
/// <param name="Bytes">The package bytes.</param>
/// <param name="Sha256">Lowercase SHA-256 hex over <see cref="Bytes"/>.</param>
/// <param name="AssetId">The exported asset.</param>
/// <param name="Revision">The exported revision, exactly the approved one.</param>
public sealed record MediumImportExport(
    string FileName,
    string ContentType,
    byte[] Bytes,
    string Sha256,
    Guid AssetId,
    long Revision);

/// <summary>
/// The Medium import use cases (MEDIUM-IMPORT-PACKAGE-001/002).
/// </summary>
/// <remarks>
/// Every method is scope-bound: an id belonging to another scope is answered as absent. Nothing
/// here opens a socket, leases a credential or reaches any Medium endpoint: export serves
/// approved bytes the owner asked for, packaged for a manual import the owner performs by hand,
/// and publication is refused unconditionally.
/// </remarks>
public interface IMediumImportService
{
    /// <summary>
    /// Serves the approved revision as a ZIP holding <c>article.html</c>, <c>article.md</c>,
    /// the approval-covered pictures, their manifest and the manual import instructions. Never
    /// approved is <c>approval_required</c>; once approved but moved is <c>approval_stale</c> -
    /// the review package's own answers, re-checked rather than redefined through the
    /// delivered Markdown and HTML exports.
    /// </summary>
    /// <exception cref="Common.ResourceNotFoundException">The asset is absent or in another scope (404).</exception>
    /// <exception cref="Content.ApprovalRequiredException">Nothing live authorizes this export (409).</exception>
    /// <exception cref="Content.ApprovalStaleException">The approval no longer describes the stored state (409).</exception>
    /// <exception cref="Content.MediaUnavailableException">An approval-covered picture's bytes are gone (409).</exception>
    /// <exception cref="Media.UnsupportedMediaTypeException">An approval-covered picture is not a type
    /// the HTML template packs (415).</exception>
    /// <exception cref="Common.PayloadTooLargeException">The packaged material would exceed the
    /// archive bound (413).</exception>
    Task<MediumImportExport> ExportAsync(
        OwnerScope owner,
        Guid assetId,
        long? revision,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Always refuses with <c>publisher_unsupported</c> (422). The asset is resolved first so a
    /// foreign id is still the delivered 404, but no state is read beyond existence and nothing
    /// is written, leased or sent.
    /// </summary>
    /// <exception cref="Common.ResourceNotFoundException">The asset is absent or in another scope (404).</exception>
    /// <exception cref="MediumPublishUnsupportedException">Always (422).</exception>
    Task PublishAsync(
        OwnerScope owner,
        Guid assetId,
        CancellationToken cancellationToken = default);
}
