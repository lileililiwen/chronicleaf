namespace Chronicleaf.Publishers.Ghost;

/// <summary>
/// The redacted failure codes this adapter reports (PUBLISHER-GHOST-ADAPTER-002).
/// </summary>
/// <remarks>
/// <para>
/// Vocabulary, not an envelope: these codes travel in the attempt row and the operation result
/// the contract owns, never through the error middleware, so no middleware arm is added for
/// them. Every code is fixed words. No code carries a body the provider wrote, a token, a URL
/// or a host path - the receipt says what happened, and the request that caused it is
/// identified by the attempt and operation ids beside it.
/// </para>
/// <para>
/// The unknown path deliberately has no code here. An ambiguous write is recorded by the
/// contract as <c>publication_unknown</c>: this adapter raises the contract's own
/// <c>PublisherTimeoutException</c> - with no remote id, because a create that lost its
/// answer names nothing to ask about - and the service owns the rest.
/// </para>
/// </remarks>
public static class GhostError
{
    /// <summary>
    /// The leased integration key was refused (401), or it was not an id:secret the transport
    /// could sign with at all. The remedy is a valid key, not a retry of refused bytes - and no
    /// byte left this process in the second case.
    /// </summary>
    public const string CredentialRejected = "ghost_credential_rejected";

    /// <summary>
    /// The key is valid but the role may not publish (403). The remedy is a different role,
    /// not a retry of the refused bytes.
    /// </summary>
    public const string PublishForbidden = "ghost_publish_forbidden";

    /// <summary>
    /// The provider refused the content itself (400, 415, 422). The remedy is different
    /// content, not a retry of the refused bytes.
    /// </summary>
    public const string InvalidPayload = "ghost_invalid_payload";

    /// <summary>
    /// The provider reported a conflict (409): a slug or post this create collided with. The
    /// remedy is a reconciled remote state by hand, never a blind second create.
    /// </summary>
    public const string UpdateConflict = "ghost_update_conflict";

    /// <summary>
    /// The provider asked for a delay (429). The run does not wait it out and does not reissue:
    /// the receipt records the refusal and the owner publishes again later.
    /// </summary>
    public const string RateLimited = "ghost_rate_limited";

    /// <summary>
    /// The provider answered with a redirect, which this transport never follows: the second hop
    /// is the one construct that could carry the leased token to another host.
    /// </summary>
    public const string RedirectRefused = "ghost_redirect_refused";

    /// <summary>
    /// The configured Admin API major is not one this transport speaks. The remedy is a
    /// supported version in the operator's target, not a guessed alternative endpoint - and no
    /// byte left this process.
    /// </summary>
    public const string IncompatibleTarget = "ghost_incompatible_target";

    /// <summary>
    /// The provider or the wire failed with an answered verdict that names no refusal above: a
    /// 5xx, a missing route, or a reset connection before anything could have been created.
    /// </summary>
    public const string Unavailable = "ghost_unavailable";
}
