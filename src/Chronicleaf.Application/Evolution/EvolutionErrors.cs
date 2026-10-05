using Chronicleaf.Application.Common;
using Chronicleaf.Application.Content;

namespace Chronicleaf.Application.Evolution;

/// <summary>
/// The evolution codes this package adds to the delivered envelope
/// (SCREENSHOT-EVOLUTION-IMAGES-001/002).
/// </summary>
/// <remarks>
/// They extend the repository's <c>{code,message,traceId,fieldErrors?}</c> shape without changing
/// it. Approval vocabulary is reused from the review package rather than redefined: an evolution
/// export without a live approval is the same <c>approval_required</c>, and one whose sources
/// moved is the same <c>approval_stale</c> - because the condition and the remedy are the same,
/// and a second spelling would let a client believe they are different.
/// </remarks>
public static class EvolutionError
{
    /// <summary>An id this caller may not reach (404). The delivered code, reused deliberately.</summary>
    public const string NotFound = ApiError.NotFound;

    /// <summary>The request parsed but named something this package will not compare (422).</summary>
    public const string ValidationFailed = ApiError.ValidationFailed;

    /// <summary>
    /// The depicted pictures span more than one viewport outside the gallery layout (422). The
    /// remedy is choosing one viewport group or the gallery, never a comparison the manifest
    /// would then mislabel.
    /// </summary>
    public const string ComparisonViewportMismatch = "comparison_viewport_mismatch";

    /// <summary>
    /// Fewer than two milestones depict a selected screenshot (422). A gap is reported in the
    /// manifest; a comparison with nothing to compare against is refused.
    /// </summary>
    public const string InsufficientSelected = "insufficient_selected";

    /// <summary>
    /// A selected picture's bytes are gone, so the artifact cannot be built from them (409).
    /// The remedy is reselecting or re-capturing the picture and rendering again - the same
    /// shape as the HTML export's missing-picture refusal.
    /// </summary>
    public const string MediaUnavailable = Content.HtmlExportError.MediaUnavailable;

    /// <summary>
    /// The rendered canvas exceeds the pixel or byte ceiling (413). The delivered
    /// payload-too-large code: an oversized render and an oversized body mean the same thing to
    /// a client. Nothing usable is stored.
    /// </summary>
    public const string OutputTooLarge = ApiError.PayloadTooLarge;

    /// <summary>
    /// The selection moved between the owner's review and the write (409). The delivered
    /// revision-conflict code: a caption edit, a (de)selection or an evidence append all move
    /// the hash the request was conditional on, so the remedy is reading again rather than
    /// approving a manifest nobody reviewed.
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
/// The depicted pictures span more than one viewport outside the gallery layout (422).
/// </summary>
public sealed class ComparisonViewportMismatchException()
    : ApiException(
        EvolutionError.ComparisonViewportMismatch,
        "The selected screenshots span more than one viewport. Compare one viewport group, or use the gallery layout.");

/// <summary>
/// Fewer than two milestones depict a selected screenshot (422).
/// </summary>
public sealed class InsufficientSelectedException(string detail)
    : ApiException(
        EvolutionError.InsufficientSelected,
        "Fewer than two milestones depict a selected screenshot. Select a picture for at least two milestones first.")
{
    /// <summary>Which milestones depict nothing, for the panel that explains it. Never stored bytes.</summary>
    public string Detail { get; } = detail;
}

/// <summary>
/// A selected picture's bytes are gone (409).
/// </summary>
public sealed class EvolutionMediaUnavailableException(Guid mediaId)
    : ApiException(
        EvolutionError.MediaUnavailable,
        "A selected screenshot's bytes are no longer stored. Reselect or re-capture the picture and render again.")
{
    /// <summary>The media id that is gone. An identifier, never bytes.</summary>
    public Guid MediaId { get; } = mediaId;
}

/// <summary>
/// The rendered canvas exceeds the pixel or byte ceiling (413). Nothing usable is stored.
/// </summary>
public sealed class EvolutionTooLargeException()
    : ApiException(
        EvolutionError.OutputTooLarge,
        "The rendered comparison exceeds the pixel or byte ceiling. Narrow the milestones or choose fewer pictures.");

/// <summary>
/// The selection moved between the owner's review and the write (409).
/// </summary>
public sealed class SelectionSupersededException()
    : ApiException(
        EvolutionError.SelectionSuperseded,
        "The selected screenshots or their captions moved since the reviewed manifest. Read the current manifest and approve it again.");
