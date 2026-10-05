using Chronicleaf.Application.Common;
using Chronicleaf.Application.Media;

namespace Chronicleaf.Application.Content;

/// <summary>
/// The codes this package adds to the delivered error envelope (HTML-CONTENT-EXPORT-001).
/// </summary>
/// <remarks>
/// <para>
/// The envelope is the delivered <c>{code,message,traceId,fieldErrors?}</c> and its status mapping
/// is unchanged; this file is vocabulary, not a second envelope. The message is authored here in
/// fixed words, because the middleware writes <c>Exception.Message</c> verbatim into the envelope.
/// </para>
/// </remarks>
public static class HtmlExportError
{
    /// <summary>
    /// An approval-covered picture whose bytes are no longer in the media store (409). The remedy
    /// is to reselect or re-capture the picture and approve again - not a retry of the export, and
    /// never a picture-less artifact served as if it were complete.
    /// </summary>
    public const string MediaUnavailable = "media_unavailable";
}

/// <summary>
/// An approved picture is bound by a live approval but its bytes cannot be read (409).
/// </summary>
public sealed class MediaUnavailableException()
    : ApiException(
        HtmlExportError.MediaUnavailable,
        "An approved picture is no longer available in media storage. "
        + "Reselect or re-capture the picture and approve the revision again; "
        + "the export is refused rather than served incomplete.");
