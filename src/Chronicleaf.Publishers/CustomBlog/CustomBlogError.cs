namespace Chronicleaf.Publishers.CustomBlog;

/// <summary>
/// The redacted failure codes this adapter reports (PUBLISHER-CUSTOM-BLOG-ADAPTER-002).
/// </summary>
/// <remarks>
/// <para>
/// Vocabulary, not an envelope: these codes travel in the attempt row and the operation result
/// the contract owns, never through the error middleware, so no middleware arm is added for
/// them. Every code is fixed words. No code carries a body the server wrote, a token, a URL
/// or a host path - the receipt says what happened, and the request that caused it is
/// identified by the attempt and operation ids beside it.
/// </para>
/// <para>
/// The unknown path deliberately has no code here. An ambiguous write is recorded by the
/// contract as <c>publication_unknown</c>: this adapter raises the contract's own
/// <c>PublisherTimeoutException</c> - with no remote id, because a create that lost its
/// answer names nothing to ask about - and the service owns the rest. The server's own
/// operations endpoint exists for a human reconciliation by hand, carrying the operation id
/// the accept response already named; this adapter never calls it, because the reconciler
/// holds no credential and an unauthenticated read is not a receipt.
/// </para>
/// </remarks>
public static class CustomBlogError
{
    /// <summary>
    /// The leased API key was refused (401). The remedy is a valid stored credential, not a
    /// retry of refused bytes.
    /// </summary>
    public const string CredentialRejected = "customblog_credential_rejected";

    /// <summary>
    /// The key is valid but the server refused the write (403). The remedy is a different
    /// grant, not a retry of the refused bytes.
    /// </summary>
    public const string PublishForbidden = "customblog_publish_forbidden";

    /// <summary>
    /// The server refused the content itself (400, 415, 422). The remedy is different
    /// content, not a retry of the refused bytes.
    /// </summary>
    public const string InvalidPayload = "customblog_invalid_payload";

    /// <summary>
    /// The server reported a hash conflict (409): the content hash names a state this create
    /// collided with. The remedy is a reconciled remote state by hand, never a blind second
    /// create.
    /// </summary>
    public const string ContentConflict = "customblog_content_conflict";

    /// <summary>
    /// The server speaks another contract (412), or its capabilities document does not
    /// advertise schema version 1 with idempotent writes and an allowed content format. The
    /// remedy is a compatible server, not a retry of unanswered bytes.
    /// </summary>
    public const string IncompatibleRemoteContract = "customblog_incompatible_remote_contract";

    /// <summary>
    /// The server asked for a delay (429). The run does not wait it out and does not reissue:
    /// the receipt records the refusal and the owner publishes again later.
    /// </summary>
    public const string RateLimited = "customblog_rate_limited";

    /// <summary>
    /// The server answered with a redirect, which this transport never follows: the second hop
    /// is the one construct that could carry the leased credential to another host.
    /// </summary>
    public const string RedirectRefused = "customblog_redirect_refused";

    /// <summary>
    /// The server or the wire failed with an answered verdict that names no refusal above: a
    /// 5xx, a missing route, or a reset connection before anything could have been created.
    /// </summary>
    public const string Unavailable = "customblog_unavailable";
}
