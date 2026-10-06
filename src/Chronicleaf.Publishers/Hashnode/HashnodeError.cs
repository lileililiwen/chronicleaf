namespace Chronicleaf.Publishers.Hashnode;

/// <summary>
/// The redacted failure codes this adapter reports (PUBLISHER-HASHNODE-ADAPTER-002).
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
/// <c>PublisherTimeoutException</c> - carrying the created draft id when one is known - and
/// the service owns the rest.
/// </para>
/// </remarks>
public static class HashnodeError
{
    /// <summary>
    /// The leased personal access token was refused (401, or GraphQL UNAUTHENTICATED). The
    /// remedy is a valid token, not a retry of the refused bytes.
    /// </summary>
    public const string CredentialRejected = "hashnode_credential_rejected";

    /// <summary>
    /// The token is valid but the role may not publish (403, or GraphQL FORBIDDEN without a
    /// plan cause): a contributor role never publishes. The remedy is a different role, not a
    /// retry of the refused bytes.
    /// </summary>
    public const string PublishForbidden = "hashnode_publish_forbidden";

    /// <summary>
    /// The publication's plan gates writes (GraphQL FORBIDDEN caused by the plan): a
    /// permanent outcome, not a retry and never an automatic upgrade.
    /// </summary>
    public const string CapabilityUnavailable = "hashnode_capability_unavailable";

    /// <summary>
    /// The provider refused the content itself (422, or a GraphQL validation error). The
    /// remedy is different content, not a retry of the refused bytes.
    /// </summary>
    public const string InvalidPayload = "hashnode_invalid_payload";

    /// <summary>
    /// The provider asked for a delay (429). The run does not wait it out and does not reissue:
    /// the receipt records the refusal and the owner publishes again later.
    /// </summary>
    public const string RateLimited = "hashnode_rate_limited";

    /// <summary>
    /// The provider answered with a redirect, which this transport never follows: the second hop
    /// is the one construct that could carry the leased token to another host.
    /// </summary>
    public const string RedirectRefused = "hashnode_redirect_refused";

    /// <summary>
    /// The provider or the wire failed with an answered verdict that names no refusal above: a
    /// 5xx, a missing route, or a reset connection before anything could have been created.
    /// </summary>
    public const string Unavailable = "hashnode_unavailable";
}
