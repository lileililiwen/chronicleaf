// The delivered repository reference is consumed, never redefined: this package names a repository the
// same way github-evidence and local-git-evidence do, as an owner and a name, and the type that parses
// and renders it - including the refusal to build a URL from a local one - is that package's.
using Chronicleaf.GitHub.Evidence;

namespace Chronicleaf.GitHub.Candidates;

/// <summary>What to poll for, and the caps the poll must not exceed (GITHUB-RELEASE-CANDIDATES-001).</summary>
/// <remarks>
/// <para>
/// The caps live on the request rather than inside the client, for the reason they do in the delivered
/// evidence contract: they are this product's promises about how much of somebody else's repository
/// becomes a proposal, and a promise that lives in a transport is a promise no caller can see.
/// </para>
/// <para>
/// <b>A <c>Since</c> is a floor the caller applies, and it is applied to a bounded read.</b> GitHub's
/// releases and tags resources have no <c>since</c> parameter, so pretending one was sent would be a
/// lie about the wire; this client therefore reads one bounded page and hands back everything on it,
/// and the service decides which of those entries the floor admits. The cap is what bounds the cost,
/// which is why it is a request field rather than a constant here: a caller who wants a wider window
/// says so, and a caller who does not cannot be charged for a repository's whole history.
/// </para>
/// </remarks>
/// <param name="Since">Only entries at or after this instant are read, when the caller named one.</param>
/// <param name="Ref">The ref tags are read from, or <c>null</c> for the repository's default branch.</param>
/// <param name="MaxReleases">At most this many releases are read.</param>
/// <param name="MaxTags">At most this many tags are read.</param>
/// <param name="ResolveTagShas">Whether each tag is resolved to the commit it names.</param>
public sealed record GitHubCandidateRequest(
    DateTimeOffset? Since,
    string? Ref,
    int MaxReleases,
    int MaxTags,
    bool ResolveTagShas)
{
    /// <summary>How many releases a poll reads when the caller names no cap.</summary>
    public const int DefaultMaxReleases = 50;

    /// <summary>How many tags a poll reads when the caller names no cap.</summary>
    public const int DefaultMaxTags = 50;

    /// <summary>A request for a repository's releases and tags, with this package's own caps.</summary>
    public static GitHubCandidateRequest Default(DateTimeOffset? since, string? reference = null) =>
        new(since, reference, DefaultMaxReleases, DefaultMaxTags, ResolveTagShas: true);
}

/// <summary>
/// One read-only source of tag and release facts (GITHUB-RELEASE-CANDIDATES-001).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a third registration name, and that is the whole reason it exists.</b>
/// <c>IGitEvidenceSource</c> is the hosted transport's and <c>ILocalGitSource</c> is the local one's, and
/// a second registration under either name would move every collection onto the wrong reader - a
/// silent no-op that reads as a pass. A name of its own is what keeps all three reachable at once, and
/// the suite that ships with this package asserts from the other side that the two delivered names
/// still resolve to the transports that own them.
/// </para>
/// <para>
/// <b>Every method here is a read.</b> There is no write, no create, no comment and no merge, so a
/// repository this product can see is one it only reads. An implementation must be safe to call
/// concurrently, must answer with a <see cref="GitHubCandidatePoll"/> rather than an exception for a
/// refusal the caller has to classify, and must never log or return the credential it was given.
/// </para>
/// </remarks>
public interface IGitHubCandidateSource
{
    /// <summary>
    /// Reads the repository's published releases and its tags, resolving each tag to the commit it
    /// names when the caller asked for that.
    /// </summary>
    Task<GitHubCandidatePoll> PollAsync(
        RepoRef repository,
        GitHubCandidateRequest request,
        string? credential,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves one ref to the immutable commit it names, or <c>null</c> when it does not name one.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="PollAsync"/> because a signed <c>create</c> event names a tag and does
    /// not carry the commit that tag points at. The candidate's source key is <c>tag:{name}:{sha}</c>,
    /// so the sha is resolved before the key is built rather than guessed from the ref name - and a
    /// ref that does not resolve produces no candidate instead of one keyed to a branch.
    /// </remarks>
    Task<string?> ResolveRefAsync(
        RepoRef repository,
        string reference,
        string? credential,
        CancellationToken cancellationToken = default);
}
