using Chronicleaf.Application.Common;

namespace Chronicleaf.Application.Xiaohongshu;

/// <summary>
/// The codes this package adds to the delivered error envelope (XIAOHONGSHU-CONTENT-PACKAGE-001/002).
/// </summary>
/// <remarks>
/// The envelope itself is the delivered <c>{code,message,traceId,fieldErrors?}</c> and its status
/// mapping is unchanged; this file is vocabulary, not a second envelope. Every message is authored
/// here in fixed words, because the middleware writes <c>Exception.Message</c> verbatim into the
/// envelope. No message names a claim, an evidence text, a URL, a token or a host path.
/// </remarks>
public static class XiaohongshuPackError
{
    /// <summary>
    /// Native Xiaohongshu publication was asked for, which this product does not perform (422). The
    /// remedy is the copy-ready export, which the owner posts by hand.
    /// </summary>
    public const string PublisherUnsupported = "publisher_unsupported";

    /// <summary>
    /// The rendered pack is over the package's own internal text bound (422). The remedy is a
    /// shorter title, cover, call to action, fewer tags, or a source story with less text.
    /// </summary>
    public const string PackTooLong = "xiaohongshu_pack_too_long";
}

/// <summary>
/// Native publication was requested (422).
/// </summary>
/// <remarks>
/// Unconditional by design: there is no account, no login, no cookie, no browser automation, no
/// token and no transport behind this package, so no state of the pack - approved or not - could
/// make publication possible. The refusal carries no URL and triggers no outbound request,
/// because there is nothing to call.
/// </remarks>
public sealed class PublisherUnsupportedException()
    : ApiException(
        XiaohongshuPackError.PublisherUnsupported,
        "This product does not publish to Xiaohongshu. Export the approved pack and post it by hand; "
        + "no account action was taken.");

// The name collides with the LinkedIn and Medium packages' refusal by design: all three answer
// the same envelope code for the same decision, and the middleware tells them apart by type.

/// <summary>
/// The rendered pack is over the package's own internal ceiling (422).
/// </summary>
/// <remarks>
/// Its own type rather than a bare <c>ApiException</c> because the middleware answers an
/// unrecognized failure as an opaque 500: a refusal the envelope cannot name is a refusal the
/// owner cannot act on. The message carries the count and the ceiling, never the prose.
/// </remarks>
public sealed class XiaohongshuPackTooLongException(int charCount, int ceiling)
    : ApiException(
        XiaohongshuPackError.PackTooLong,
        $"The Xiaohongshu pack is {charCount} characters, over this product's own "
        + $"{ceiling}-character ceiling. Shorten the title, cover, call to action or tags, "
        + "or adapt a story with less text.");
