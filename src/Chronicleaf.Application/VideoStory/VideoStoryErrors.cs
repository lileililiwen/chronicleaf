using Chronicleaf.Application.Common;

namespace Chronicleaf.Application.VideoStory;

/// <summary>
/// The codes this package adds to the delivered error envelope (VIDEO-STORY-SCRIPTS-001/002).
/// </summary>
/// <remarks>
/// The envelope itself is the delivered <c>{code,message,traceId,fieldErrors?}</c> and its status
/// mapping is unchanged; this file is vocabulary, not a second envelope. Every message is authored
/// here in fixed words, because the middleware writes <c>Exception.Message</c> verbatim into the
/// envelope. No message names a claim, an evidence text, a URL, a token or a host path.
/// </remarks>
public static class VideoStoryError
{
    /// <summary>
    /// The shot list does not satisfy the storyboard schema (422). The remedy is a list that
    /// covers the runtime exactly with material the frozen evidence holds.
    /// </summary>
    public const string InvalidStorySchema = "invalid_story_schema";

    /// <summary>
    /// Native video publication was asked for, which this product does not perform (422). The
    /// remedy is the scripted export, which the owner acts on by hand.
    /// </summary>
    public const string PublisherUnsupported = "publisher_unsupported";
}

/// <summary>
/// The shot list failed the storyboard schema (422).
/// </summary>
/// <remarks>
/// Its own type rather than a bare <c>ApiException</c> because the middleware answers an
/// unrecognized failure as an opaque 500: a refusal the envelope cannot name is a refusal the
/// owner cannot act on. The field names the shot and member that failed
/// (<c>durationSeconds</c>, <c>shots</c>, <c>shots[2].startMs</c>), never the prose.
/// </remarks>
public sealed class InvalidStorySchemaException(string field, string message)
    : ApiException(
        VideoStoryError.InvalidStorySchema,
        message,
        new Dictionary<string, string[]> { [field] = [message] });

/// <summary>
/// Native video publication was requested (422).
/// </summary>
/// <remarks>
/// Unconditional by design: there is no renderer, no encoder, no account, no login and no
/// transport behind this package, so no state of the script - approved or not - could make
/// publication possible. The refusal carries no URL and triggers no outbound request, because
/// there is nothing to call.
/// </remarks>
// The name collides with the LinkedIn, Medium and Xiaohongshu packages' refusal by design: all
// four answer the same envelope code for the same decision, and the middleware tells them apart
// by type.
public sealed class VideoStoryPublishUnsupportedException()
    : ApiException(
        VideoStoryError.PublisherUnsupported,
        "This product does not publish videos. Export the approved scripts and act on them by hand; "
        + "no video was synthesized and no account action was taken.");
