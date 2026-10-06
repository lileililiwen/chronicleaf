using Chronicleaf.Application.Common;

namespace Chronicleaf.Application.Medium;

/// <summary>
/// The codes this package adds to the delivered error envelope (MEDIUM-IMPORT-PACKAGE-001).
/// </summary>
/// <remarks>
/// The envelope itself is the delivered <c>{code,message,traceId,fieldErrors?}</c> and its status
/// mapping is unchanged; this file is vocabulary, not a second envelope. Every message is authored
/// here in fixed words, because the middleware writes <c>Exception.Message</c> verbatim into the
/// envelope. No message names a claim, an evidence text, a URL, a token or a host path.
/// </remarks>
public static class MediumError
{
    /// <summary>
    /// Native Medium publication was asked for, which this product does not perform (422). The
    /// remedy is the manual import package, which the owner carries into Medium by hand.
    /// </summary>
    public const string PublisherUnsupported = "publisher_unsupported";
}

/// <summary>
/// Native publication was requested (422).
/// </summary>
/// <remarks>
/// Unconditional by design: there is no account, no token, no OAuth scope and no transport behind
/// this package, so no state of the article - approved or not - could make publication possible.
/// The refusal carries no URL and triggers no outbound request, because there is nothing to call.
/// </remarks>
public sealed class MediumPublishUnsupportedException()
    : ApiException(
        MediumError.PublisherUnsupported,
        "This product does not publish to Medium. Export the approved article as a manual "
        + "import package and carry it into Medium by hand; no account action was taken.");
