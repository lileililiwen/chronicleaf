using Chronicleaf.Application.Common;
using Chronicleaf.Application.Content;

namespace Chronicleaf.Application.EvolutionGifs;

/// <summary>
/// The evolution GIF codes this package adds to the delivered envelope
/// (EVOLUTION-GIF-EXPORT-001/002).
/// </summary>
/// <remarks>
/// They extend the repository's <c>{code,message,traceId,fieldErrors?}</c> shape without changing
/// it. Approval vocabulary is reused from the review package rather than redefined: a GIF export
/// without a live approval is the same <c>approval_required</c>, and one whose sources moved is
/// the same <c>approval_stale</c> - because the condition and the remedy are the same, and a second
/// spelling would let a client believe they are different. The missing-bytes refusal reuses the
/// HTML export's <c>media_unavailable</c> for the same reason.
/// </remarks>
public static class EvolutionGifError
{
    /// <summary>An id this caller may not reach (404). The delivered code, reused deliberately.</summary>
    public const string NotFound = ApiError.NotFound;

    /// <summary>The request parsed but named something this package will not animate (422).</summary>
    public const string ValidationFailed = ApiError.ValidationFailed;

    /// <summary>
    /// A selected picture's bytes are gone, so the artifact cannot be built from them (409).
    /// The remedy is reselecting or re-capturing the picture and rendering again - the same
    /// shape as the HTML export's missing-picture refusal.
    /// </summary>
    public const string MediaUnavailable = Content.HtmlExportError.MediaUnavailable;

    /// <summary>
    /// The rendered canvas exceeds the dimension or byte ceiling (413). The delivered
    /// payload-too-large code: an oversized render and an oversized body mean the same thing to
    /// a client. Nothing usable is stored.
    /// </summary>
    public const string OutputTooLarge = ApiError.PayloadTooLarge;

    /// <summary>
    /// The selection moved between the owner's review and the write (409). The delivered
    /// revision-conflict code: a caption edit, a (de)selection, a duration or order edit, or an
    /// evidence append all move the hash the request was conditional on, so the remedy is
    /// reading again rather than approving a manifest nobody reviewed.
    /// </summary>
    public const string SelectionSuperseded = ApiError.RevisionConflict;

    /// <summary>
    /// No live approval authorizes this export (409). The review package's code, reused: the
    /// condition and the remedy - approve the exact artifact first - are the same.
    /// </summary>
    public const string ApprovalRequired = ReviewError.ApprovalRequired;

    /// <summary>
    /// The approval no longer describes the stored selection or artifact (409). The review
    /// package's code, reused for the same reason.
    /// </summary>
    public const string ApprovalStale = ReviewError.ApprovalStale;
}

/// <summary>
/// A selected picture's bytes are gone (409).
/// </summary>
public sealed class EvolutionGifMediaUnavailableException(Guid mediaId)
    : ApiException(
        EvolutionGifError.MediaUnavailable,
        "A selected screenshot's bytes are no longer stored. Reselect or re-capture the picture and render again.")
{
    /// <summary>The media id that is gone. An identifier, never bytes.</summary>
    public Guid MediaId { get; } = mediaId;
}

/// <summary>
/// The rendered GIF exceeds the dimension or byte ceiling (413). Nothing usable is stored.
/// </summary>
public sealed class EvolutionGifTooLargeException()
    : ApiException(
        EvolutionGifError.OutputTooLarge,
        "The rendered animation exceeds the dimension or byte ceiling. Narrow the milestones, shorten the durations or choose fewer pictures.");

/// <summary>
/// The selection moved between the owner's review and the write (409).
/// </summary>
public sealed class EvolutionGifSelectionSupersededException()
    : ApiException(
        EvolutionGifError.SelectionSuperseded,
        "The selected screenshots, their captions or the playback moved since the reviewed manifest. Read the current manifest and approve it again.");
