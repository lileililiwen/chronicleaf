using Chronicleaf.Application.Common;

namespace Chronicleaf.Application.LinkedIn;

/// <summary>
/// The codes this package adds to the delivered error envelope (LINKEDIN-COPY-PACKAGE-001/002).
/// </summary>
/// <remarks>
/// The envelope itself is the delivered <c>{code,message,traceId,fieldErrors?}</c> and its status
/// mapping is unchanged; this file is vocabulary, not a second envelope. Every message is authored
/// here in fixed words, because the middleware writes <c>Exception.Message</c> verbatim into the
/// envelope. No message names a claim, an evidence text, a URL, a token or a host path.
/// </remarks>
public static class LinkedInError
{
    /// <summary>
    /// Native LinkedIn publication was asked for, which this product does not perform (422). The
    /// remedy is the copy-ready export, which the owner posts by hand.
    /// </summary>
    public const string PublisherUnsupported = "publisher_unsupported";

    /// <summary>
    /// The rendered copy is over the package's own internal text bound (422). The remedy is a
    /// shorter call to action, fewer hashtags, or a source story with less text.
    /// </summary>
    public const string CopyTooLong = "linkedin_copy_too_long";
}

/// <summary>
/// Native publication was requested (422).
/// </summary>
/// <remarks>
/// Unconditional by design: there is no account, no token, no OAuth scope and no transport behind
/// this package, so no state of the copy - approved or not - could make publication possible. The
/// refusal carries no URL and triggers no outbound request, because there is nothing to call.
/// </remarks>
public sealed class PublisherUnsupportedException()
    : ApiException(
        LinkedInError.PublisherUnsupported,
        "This product does not publish to LinkedIn. Export the approved copy and post it by hand; "
        + "no account action was taken.");

/// <summary>
/// The rendered copy is over the package's own internal ceiling (422).
/// </summary>
/// <remarks>
/// Its own type rather than a bare <c>ApiException</c> because the middleware answers an
/// unrecognized failure as an opaque 500: a refusal the envelope cannot name is a refusal the
/// owner cannot act on. The message carries the count and the ceiling, never the prose.
/// </remarks>
public sealed class LinkedInCopyTooLongException(int charCount, int ceiling)
    : ApiException(
        LinkedInError.CopyTooLong,
        $"The LinkedIn copy is {charCount} characters, over this product's own "
        + $"{ceiling}-character ceiling. Shorten the call to action, use fewer "
        + "hashtags, or adapt a story with less text.");
