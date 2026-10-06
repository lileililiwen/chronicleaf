namespace Chronicleaf.Publishers.GitHubPages;

/// <summary>
/// The redacted failure codes this adapter reports
/// (PUBLISHER-GITHUB-PAGES-ADAPTER-002).
/// </summary>
/// <remarks>
/// <para>
/// Vocabulary, not an envelope: these codes travel in the attempt row and the operation result
/// the contract owns, never through the error middleware, so no middleware arm is added for
/// them. Every code is fixed words. No code carries a body GitHub wrote, a token, a URL or a
/// host path - the receipt says what happened, and the request that caused it is identified by
/// the attempt and operation ids beside it.
/// </para>
/// <para>
/// The unknown path deliberately has no code here. An ambiguous write is recorded by the
/// contract as <c>publication_unknown</c>: this adapter raises the contract's own
/// <c>PublisherTimeoutException</c> and the service owns the rest.
/// </para>
/// </remarks>
public static class GitHubPagesError
{
    /// <summary>
    /// The leased credential was refused (401, or 403 without a rate-limit signal). The remedy
    /// is a valid credential with contents-write access to the configured repository, not a retry
    /// of the refused bytes.
    /// </summary>
    public const string Unauthorized = "github_pages_unauthorized";

    /// <summary>
    /// The upstream asked for a delay (429, or 403 with an exhausted rate budget). The run does
    /// not wait it out: the receipt records the refusal and the owner publishes again later.
    /// </summary>
    public const string RateLimited = "github_pages_rate_limited";

    /// <summary>
    /// The configured repository is absent or invisible to the leased credential. The remedy is
    /// the repository or the credential's access, not a retry.
    /// </summary>
    public const string NotFound = "github_pages_not_found";

    /// <summary>
    /// The branch moved between the base read and the ref write: the conditional write was
    /// refused and nothing reachable was changed. The remedy is a fresh publish, not a force push.
    /// </summary>
    public const string UpstreamMoved = "github_pages_upstream_moved";

    /// <summary>
    /// The upstream refused the content itself (422 outside a ref write). The remedy is different
    /// content, not a retry of the refused bytes.
    /// </summary>
    public const string Rejected = "github_pages_rejected";

    /// <summary>
    /// The upstream or the wire failed before the ref moved: a 5xx, a refused redirect, a reset
    /// connection or a timeout while nothing reachable could have changed. The branch still points
    /// where it pointed, so this is failed rather than unknown.
    /// </summary>
    public const string Unavailable = "github_pages_unavailable";
}
