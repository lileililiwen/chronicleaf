namespace Chronicleaf.Application.Content;

/// <summary>
/// One approved picture with the bytes the export verified (HTML-CONTENT-EXPORT-001).
/// </summary>
/// <remarks>
/// <para>
/// These are the only pictures an HTML export may carry: the selected pictures of the evidence
/// head the live approval is bound to, with the bytes read back out of the delivered media store
/// at export time. A picture whose bytes are gone is a refused export, never a silently
/// picture-less one - which is why the bytes travel here rather than an id the renderer would
/// resolve on its own.
/// </para>
/// </remarks>
/// <param name="MediaId">The stored media this picture was read from.</param>
/// <param name="Caption">The owner's caption, rendered as figure text after escaping.</param>
/// <param name="ContentType">The decoded media type of the stored bytes.</param>
/// <param name="Bytes">The exact stored bytes, packaged under a generated relative path.</param>
public sealed record ApprovedExportPicture(
    Guid MediaId,
    string Caption,
    string ContentType,
    byte[] Bytes);

/// <summary>
/// Everything the HTML renderer may read: the approved revision's exact text and the
/// approval-covered pictures (HTML-CONTENT-EXPORT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// This is a closed world on purpose. The renderer receives bytes and pictures and nothing else:
/// no URL to fetch, no media id to resolve, no remote reference to follow. A remote image in the
/// source Markdown therefore cannot become a request, because there is no code path here that
/// could make one - it is rendered as inert characters instead.
/// </para>
/// </remarks>
/// <param name="AssetId">The asset the approved revision belongs to.</param>
/// <param name="ContentRevision">The approved revision number.</param>
/// <param name="Title">The milestone title the revision carries, rendered escaped.</param>
/// <param name="BodyMarkdown">The approved revision's exact stored bytes.</param>
/// <param name="BodyHash">The digest of exactly those bytes, carried as the export receipt.</param>
/// <param name="EvidenceRevision">The evidence head revision the approval was bound to.</param>
/// <param name="Pictures">The approval-covered pictures, in media-id order.</param>
public sealed record ApprovedAssetSnapshot(
    Guid AssetId,
    long ContentRevision,
    string Title,
    string BodyMarkdown,
    string BodyHash,
    long EvidenceRevision,
    IReadOnlyList<ApprovedExportPicture> Pictures);

/// <summary>
/// One portable artifact, as the export route serves it (HTML-CONTENT-EXPORT-002).
/// </summary>
/// <remarks>
/// <para>
/// A single <c>.html</c> file when the approval covers no pictures, or a ZIP holding the HTML,
/// the pictures under generated relative paths and a checksum manifest when it does. Either way
/// the bytes open offline: the stylesheet is embedded from the fixed template and no entry
/// reaches outside the artifact, because every path is generated here rather than taken from the
/// content.
/// </para>
/// </remarks>
/// <param name="MediaType">Either <c>text/html</c> or <c>application/zip</c>, and nothing else.</param>
/// <param name="FileName">The attachment name, generated from the asset id and the revision.</param>
/// <param name="Sha256">Lowercase hex SHA-256 over exactly <paramref name="Bytes"/>.</param>
/// <param name="Bytes">The artifact bytes the route serves as a private download.</param>
/// <param name="ManifestJson">The checksum manifest: the inner files in the ZIP, or the single
/// HTML file's own receipt when there is nothing to pack.</param>
public sealed record ExportArtifact(
    string MediaType,
    string FileName,
    string Sha256,
    byte[] Bytes,
    string ManifestJson);

/// <summary>
/// The Chronicleaf-owned side of the HTML export (HTML-CONTENT-EXPORT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// A pure renderer and packager: it converts approved bytes to a sanitized document and packs it,
/// and it owns no approval, no evidence and no media. Whether the snapshot it is handed describes
/// the stored state is decided before it is called, by the review package's own contracts - this
/// interface never redefines them.
/// </para>
/// </remarks>
public interface IContentExportAdapter
{
    /// <summary>
    /// Renders the approved snapshot to a sanitized portable artifact.
    /// </summary>
    /// <exception cref="Media.MediaNotFoundException">Never thrown here: a missing picture is the
    /// caller's refusal, not the renderer's. Documented so a reader does not go looking for it.</exception>
    /// <exception cref="Media.UnsupportedMediaTypeException">A picture's type is not one this
    /// template packs (415).</exception>
    /// <exception cref="Common.PayloadTooLargeException">The artifact would exceed the archive
    /// bound (413).</exception>
    Task<ExportArtifact> ExportAsync(
        ApprovedAssetSnapshot snapshot,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One approved HTML export, as the export route serves it (HTML-CONTENT-EXPORT-001/002).
/// </summary>
/// <param name="FileName">The attachment name, generated from the asset id and the revision.</param>
/// <param name="ContentType">Either <c>text/html; charset=utf-8</c> or <c>application/zip</c>.</param>
/// <param name="Bytes">The artifact bytes: the sanitized document, or the ZIP holding it.</param>
/// <param name="Sha256">Lowercase hex SHA-256 over exactly <paramref name="Bytes"/>.</param>
public sealed record ApprovedHtmlExport(
    string FileName,
    string ContentType,
    byte[] Bytes,
    string Sha256);
