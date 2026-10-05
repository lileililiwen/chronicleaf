using Chronicleaf.Application.Common;

namespace Chronicleaf.Application.Content;

/// <summary>
/// The codes this package adds to the delivered error envelope (MARKDOWN-REVIEW-EXPORT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The envelope is the delivered <c>{code,message,traceId,fieldErrors?}</c> and its status mapping
/// is unchanged; this file is vocabulary, not a second envelope. Every message is authored here in
/// fixed words, because the middleware writes <c>Exception.Message</c> verbatim into the envelope.
/// </para>
/// </remarks>
public static class ReviewError
{
    /// <summary>
    /// No live approval authorizes this export (409). The remedy is an approval, not a retry.
    /// </summary>
    public const string ApprovalRequired = "approval_required";

    /// <summary>
    /// A live-looking approval no longer describes the stored revision, the evidence head or the
    /// selected pictures (409). The remedy is a new approval, not a retry of the export.
    /// </summary>
    public const string ApprovalStale = "approval_stale";

    /// <summary>
    /// The revision carries deterministic findings a human has not corrected (422). There is no
    /// acknowledgment flag that clears this: the design refuses a silent bypass, so the text is
    /// corrected and approval is asked for again.
    /// </summary>
    public const string ApprovalBlocked = "approval_blocked";
}

/// <summary>
/// Export was asked for without a live approval (409).
/// </summary>
public sealed class ApprovalRequiredException()
    : ApiException(
        ReviewError.ApprovalRequired,
        "Nothing approved authorizes this export. Review the current revision and approve it first.");

/// <summary>
/// The approval no longer matches the stored revision, the evidence head or the pictures (409).
/// </summary>
public sealed class ApprovalStaleException(string detail)
    : ApiException(
        ReviewError.ApprovalStale,
        "The approval no longer describes the current revision, the current evidence or the "
        + "selected pictures. Review the current state and approve it again.")
{
    /// <summary>Which binding moved, for the panel that explains it. Never a stored value.</summary>
    public string Detail { get; } = detail;
}

/// <summary>
/// The revision carries deterministic findings, so it cannot be approved as it stands (422).
/// </summary>
public sealed class ApprovalBlockedException(IReadOnlyDictionary<string, string[]> fieldErrors)
    : ApiException(
        ReviewError.ApprovalBlocked,
        "This revision carries findings a human has to correct before it can be approved. "
        + "Correct the text and ask for approval again; no acknowledgment clears this.",
        fieldErrors)
{
    public ApprovalBlockedException(string field, string message)
        : this(new Dictionary<string, string[]> { [field] = [message] })
    {
    }
}
