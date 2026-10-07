using Chronicleaf.Application.Milestones;

namespace Chronicleaf.Application.Xiaohongshu;

/// <summary>
/// What an owner sends when they adapt an approved story into a Xiaohongshu pack
/// (XIAOHONGSHU-CONTENT-PACKAGE-001).
/// </summary>
/// <param name="EvidenceRevision">The evidence head the owner adapted against; must still be current.</param>
/// <param name="VoiceRevision">The voice profile revision the owner adapted against; must still be current.</param>
/// <param name="Title">The title the owner chose from the derived candidates; must name one exactly.</param>
/// <param name="Tags">The owner's own tags, without invention. At most ten.</param>
/// <param name="CoverCopy">The owner's cover line, or null for no cover text.</param>
/// <param name="CallToAction">The owner's own closing line, or null for an informational note.</param>
/// <param name="Selections">The pictures this pack carries in the owner's image order, each with the owner's accessibility words.</param>
public sealed record AdaptXiaohongshuPackRequest(
    long EvidenceRevision,
    long VoiceRevision,
    string Title,
    IReadOnlyList<string> Tags,
    string? CoverCopy,
    string? CallToAction,
    IReadOnlyList<XiaohongshuMediaSelection> Selections);

/// <summary>One picture the pack carries, with the owner's accessibility words.</summary>
/// <param name="MediaId">A complete screenshot's media in the frozen evidence bundle.</param>
/// <param name="AltText">The owner's accessibility words, 1 to 500 characters.</param>
public sealed record XiaohongshuMediaSelection(Guid MediaId, string AltText);

/// <summary>One adapted Xiaohongshu pack, as the adapt route answers it.</summary>
/// <param name="AssetId">The pack asset, new or already holding earlier revisions.</param>
/// <param name="Revision">The revision this adaptation appended.</param>
/// <param name="BodyHash">Lowercase SHA-256 hex over the stored pack bytes.</param>
/// <param name="TitleCandidates">The derived titles the owner chose from, in order.</param>
/// <param name="SelectedTitle">The title this revision carries.</param>
/// <param name="CharCount">The stored text's length in characters.</param>
/// <param name="LengthWarning">True when the text reached the package's warning threshold.</param>
/// <param name="SourceAssetId">The approved story this pack was derived from.</param>
/// <param name="SourceRevision">That story's head revision at adapt time.</param>
/// <param name="EvidenceRevision">The evidence head the pack is grounded in.</param>
/// <param name="VoiceRevision">The voice profile revision the pack carries a snapshot of.</param>
/// <param name="ImageOrder">The pictures this revision carries, in the owner's image order.</param>
/// <param name="HasCallToAction">True when the owner supplied a closing line.</param>
public sealed record AdaptedXiaohongshuPack(
    Guid AssetId,
    long Revision,
    string BodyHash,
    IReadOnlyList<string> TitleCandidates,
    string SelectedTitle,
    int CharCount,
    bool LengthWarning,
    Guid SourceAssetId,
    long SourceRevision,
    long EvidenceRevision,
    long VoiceRevision,
    IReadOnlyList<Guid> ImageOrder,
    bool HasCallToAction);

/// <summary>One approved Xiaohongshu pack as a downloadable package (XIAOHONGSHU-CONTENT-PACKAGE-002).</summary>
/// <param name="FileName">The attachment name, carrying the asset id and revision.</param>
/// <param name="ContentType">Always the ZIP media type: text plus the selected pictures.</param>
/// <param name="Bytes">The package bytes.</param>
/// <param name="Sha256">Lowercase SHA-256 hex over <see cref="Bytes"/>.</param>
/// <param name="AssetId">The exported asset.</param>
/// <param name="Revision">The exported revision, exactly the approved one.</param>
public sealed record XiaohongshuPackExport(
    string FileName,
    string ContentType,
    byte[] Bytes,
    string Sha256,
    Guid AssetId,
    long Revision);

/// <summary>
/// The Xiaohongshu pack use cases (XIAOHONGSHU-CONTENT-PACKAGE-001/002).
/// </summary>
/// <remarks>
/// Every method is scope-bound: an id belonging to another scope is answered as absent. Nothing
/// here opens a socket, leases a credential, logs into an account or reaches any Xiaohongshu
/// endpoint: adaptation renders deterministically from stored rows, export serves approved bytes
/// to the owner that asked for them, and publication is refused unconditionally. The finished
/// pack is copy the owner posts by hand; the labels in it are suggestions, never a promise of
/// platform acceptance or growth.
/// </remarks>
public interface IXiaohongshuPackService
{
    /// <summary>
    /// Renders an approved story into a Xiaohongshu pack and stores it as a draft revision of the
    /// milestone's pack asset, with no approval row of its own.
    /// </summary>
    /// <exception cref="Common.ResourceNotFoundException">The source is absent or in another scope (404).</exception>
    /// <exception cref="Common.ValidationFailedException">The source is not an approvable story, or the title, tags, cover, call to action or selection is not one this package stores (422).</exception>
    /// <exception cref="Content.ApprovalRequiredException">The source was never approved (409).</exception>
    /// <exception cref="Content.ApprovalStaleException">The source approval no longer describes the stored state (409).</exception>
    /// <exception cref="Common.RevisionConflictException">The named evidence or voice head has moved (409).</exception>
    /// <exception cref="Content.MediaUnavailableException">A selected picture is not a complete screenshot in the frozen bundle, or its bytes are gone (409).</exception>
    /// <exception cref="Application.Common.ApiException">The rendered pack is over the internal ceiling (422 <c>xiaohongshu_pack_too_long</c>).</exception>
    Task<AdaptedXiaohongshuPack> AdaptAsync(
        OwnerScope owner,
        Guid sourceAssetId,
        AdaptXiaohongshuPackRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Serves the approved revision as a ZIP holding <c>copy.txt</c>, <c>package.json</c>, the
    /// selected pictures in the owner's image order and their manifest. Never approved is
    /// <c>approval_required</c>; once approved but moved is <c>approval_stale</c> - the review
    /// package's own answers, re-checked rather than redefined. A picture whose bytes are gone
    /// is <c>media_unavailable</c>: the text stays previewable through the review routes, but no
    /// complete image archive is served without its images.
    /// </summary>
    /// <exception cref="Common.ResourceNotFoundException">The asset is absent or in another scope (404).</exception>
    /// <exception cref="Common.ValidationFailedException">The asset is not a Xiaohongshu pack (422).</exception>
    /// <exception cref="Content.ApprovalRequiredException">Nothing live authorizes this export (409).</exception>
    /// <exception cref="Content.ApprovalStaleException">The approval no longer describes the stored state (409).</exception>
    /// <exception cref="Content.MediaUnavailableException">A selected picture's bytes are gone (409).</exception>
    Task<XiaohongshuPackExport> ExportAsync(
        OwnerScope owner,
        Guid assetId,
        long? revision,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Always refuses with <c>publisher_unsupported</c> (422). The asset is resolved first so a
    /// foreign id is still the delivered 404, but no state is read beyond existence and nothing
    /// is written, leased, logged into or sent.
    /// </summary>
    /// <exception cref="Common.ResourceNotFoundException">The asset is absent or in another scope (404).</exception>
    /// <exception cref="PublisherUnsupportedException">Always (422).</exception>
    Task PublishAsync(
        OwnerScope owner,
        Guid assetId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Xiaohongshu package's own selection rows (XIAOHONGSHU-CONTENT-PACKAGE-001).
/// </summary>
/// <remarks>
/// Reads and writes are scope-bound like every other store in this repository: a row from another
/// scope is never returned and never written. Rows are inserted beside the revision they belong
/// to in the owner's image order and never updated or deleted.
/// </remarks>
public interface IXiaohongshuPackMediaStore
{
    /// <summary>Inserts one row per selected picture in the owner's image order. The caller holds the unit of work.</summary>
    Task AddAsync(
        Guid scopeId,
        Guid assetId,
        long revision,
        IReadOnlyList<XiaohongshuMediaSelection> selections,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one revision's selection in the owner's image order, or empty when it carries none.</summary>
    Task<IReadOnlyList<XiaohongshuMediaSelection>> ListAsync(
        Guid scopeId,
        Guid assetId,
        long revision,
        CancellationToken cancellationToken = default);
}
