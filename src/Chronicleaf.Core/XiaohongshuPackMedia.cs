namespace Chronicleaf.Core;

/// <summary>
/// One picture a Xiaohongshu pack revision carries, with the owner's own accessibility words
/// (XIAOHONGSHU-CONTENT-PACKAGE-001).
/// </summary>
/// <remarks>
/// <para>
/// A row is written once, beside the revision it belongs to, in the same unit of work that
/// appends that revision. There is no update and no delete anywhere in this repository: an
/// edited pack is a new revision with its own rows, so an approval bound to an earlier
/// revision still describes exactly the pictures it was bound to.
/// </para>
/// <para>
/// The media id must name a complete screenshot item with stored bytes in the frozen evidence
/// bundle the revision is grounded in. That is checked before any row exists, so a selection
/// that names nothing selectable is a refusal rather than a manifest with a hole in it.
/// </para>
/// <para>
/// Unlike the LinkedIn copy's set-ordered pictures, a pack carries an explicit image order:
/// the owner arranges the pictures and the export serves them in that arrangement. The
/// position is the zero-based index in the request order, so a reorder is a new revision
/// whose approval no longer names the old arrangement.
/// </para>
/// </remarks>
/// <param name="assetId">The Xiaohongshu pack asset this selection belongs to.</param>
/// <param name="revision">The pack revision this selection was made for, from 1.</param>
/// <param name="scopeId">The owning scope, carried on the row so a scoped read never trusts a join.</param>
/// <param name="position">The zero-based position in the owner's image order.</param>
/// <param name="mediaId">The selected picture's media.</param>
/// <param name="altText">The owner's accessibility words for the picture, 1 to 500 characters.</param>
public sealed class XiaohongshuPackMedia
{
    /// <summary>How many pictures one pack revision may carry. A bound, not a platform claim.</summary>
    public const int MaxPictures = 20;

    /// <summary>How many characters of accessibility words one picture may carry.</summary>
    public const int MaxAltTextChars = 500;

    public XiaohongshuPackMedia(
        Guid assetId,
        long revision,
        Guid scopeId,
        int position,
        Guid mediaId,
        string altText)
    {
        if (assetId == Guid.Empty)
        {
            throw new ArgumentException("A selection needs a non-empty asset.", nameof(assetId));
        }

        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revision), revision, "A selection belongs to a stored revision, from 1.");
        }

        if (position < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(position), position, "An image position is zero or greater.");
        }

        if (mediaId == Guid.Empty)
        {
            throw new ArgumentException("A selection needs a non-empty media.", nameof(mediaId));
        }

        if (string.IsNullOrWhiteSpace(altText) || altText.Length > MaxAltTextChars)
        {
            throw new ArgumentException(
                $"Accessibility words are required, up to {MaxAltTextChars} characters.",
                nameof(altText));
        }

        AssetId = assetId;
        Revision = revision;
        ScopeId = scopeId;
        Position = position;
        MediaId = mediaId;
        AltText = altText;
    }

    public Guid AssetId { get; }

    public long Revision { get; }

    public Guid ScopeId { get; }

    public int Position { get; }

    public Guid MediaId { get; }

    public string AltText { get; }
}
