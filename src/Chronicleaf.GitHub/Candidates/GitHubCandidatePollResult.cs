namespace Chronicleaf.GitHub.Candidates;

/// <summary>
/// The tags and releases one repository has published, or why it could not be read
/// (GITHUB-RELEASE-CANDIDATES-001).
/// </summary>
/// <remarks>
/// <para>
/// A separate value from the delivered <c>GitEvidenceResult</c> on purpose. That one is a collection
/// between two revisions, and its caps are about commits, files and diff bytes - none of which is
/// what this package needs. A poll asks a different question of the same repository, and folding it
/// into the delivered result would mean every field of an evidence collection had to be null for a
/// candidate poll, which is a shape a reader cannot tell from a collection that read nothing.
/// </para>
/// <para>
/// A refusal is a value here rather than an exception, for the same reason it is one there: four
/// different provider answers mean four different things to a job, and a caller that had to re-derive
/// the classification from a message either retried a refusal forever or abandoned a throttled answer.
/// </para>
/// <para>
/// <b>It is a record because the client fills it in stages rather than building it once.</b> A poll reads
/// releases and tags on two separate resources, and a tags page that cannot be read does not un-read the
/// releases that were. <c>with</c> on a class is not a thing, so a class here would have forced either a
/// mutable poll shared across two awaits or a second copy of the classification - and a poll object a
/// caller could still mutate after it had been handed a candidate would be a worse shape than one it
/// cannot.
/// </para>
/// </remarks>
public sealed record GitHubCandidatePoll
{
    private static readonly IReadOnlyList<GitHubRelease> NoReleases = [];
    private static readonly IReadOnlyList<GitHubTag> NoTags = [];

    public GitHubCandidatePoll(
        GitHubCandidateOutcome outcome,
        string repository,
        string? refName,
        DateTimeOffset fetchedAt,
        TimeSpan? retryAfter = null)
    {
        Outcome = outcome;
        Repository = repository;
        RefName = refName;
        FetchedAt = fetchedAt;
        RetryAfter = retryAfter;
    }

    /// <summary>How the poll ended.</summary>
    public GitHubCandidateOutcome Outcome { get; }

    /// <summary>The repository that was read, as the owner and name the connection stored.</summary>
    public string Repository { get; }

    /// <summary>The ref the tags were read from, when one was asked for.</summary>
    public string? RefName { get; }

    /// <summary>Every published, non-draft release, bounded and newest first as GitHub returned it.</summary>
    public IReadOnlyList<GitHubRelease> Releases { get; init; } = NoReleases;

    /// <summary>Every tag, bounded and newest first as GitHub returned it.</summary>
    public IReadOnlyList<GitHubTag> Tags { get; init; } = NoTags;

    /// <summary>When the answer was produced.</summary>
    public DateTimeOffset FetchedAt { get; }

    /// <summary>What the provider asked for on a throttled answer, clamped by the caller's policy.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>A poll that read the repository's releases and tags.</summary>
    public static GitHubCandidatePoll Collected(
        string repository,
        string? refName,
        DateTimeOffset fetchedAt,
        IReadOnlyList<GitHubRelease> releases,
        IReadOnlyList<GitHubTag> tags) =>
        new(GitHubCandidateOutcome.Collected, repository, refName, fetchedAt)
        {
            Releases = releases,
            Tags = tags,
        };

    /// <summary>GitHub answered 404. A private repository and an absent one are the same answer.</summary>
    public static GitHubCandidatePoll RepositoryUnavailable(DateTimeOffset fetchedAt) =>
        new(GitHubCandidateOutcome.RepositoryUnavailable, string.Empty, null, fetchedAt);

    /// <summary>GitHub answered 401 or 403: the credential was refused, or is absent and needed.</summary>
    public static GitHubCandidatePoll CredentialRejected(DateTimeOffset fetchedAt) =>
        new(GitHubCandidateOutcome.CredentialRejected, string.Empty, null, fetchedAt);

    /// <summary>GitHub answered 429 and said when it may be asked again.</summary>
    public static GitHubCandidatePoll RateLimited(DateTimeOffset fetchedAt, TimeSpan? retryAfter) =>
        new(GitHubCandidateOutcome.RateLimited, string.Empty, null, fetchedAt, retryAfter);

    /// <summary>GitHub refused the request itself, or answered with something this client cannot read.</summary>
    public static GitHubCandidatePoll Rejected(DateTimeOffset fetchedAt) =>
        new(GitHubCandidateOutcome.Rejected, string.Empty, null, fetchedAt);

    /// <summary>GitHub could not answer right now: a 5xx, a transport failure or a timeout.</summary>
    public static GitHubCandidatePoll Unavailable(DateTimeOffset fetchedAt) =>
        new(GitHubCandidateOutcome.Unavailable, string.Empty, null, fetchedAt);
}

/// <summary>The answers a poll can give, and nothing else.</summary>
public enum GitHubCandidateOutcome
{
    /// <summary>What was asked for was read.</summary>
    Collected,

    /// <summary>The repository is absent or private. GitHub does not distinguish them.</summary>
    RepositoryUnavailable,

    /// <summary>The credential was refused, or one was required and none was configured.</summary>
    CredentialRejected,

    /// <summary>GitHub asked us to slow down and said when.</summary>
    RateLimited,

    /// <summary>The request was refused as malformed, or a ref did not resolve.</summary>
    Rejected,

    /// <summary>GitHub could not answer right now.</summary>
    Unavailable,
}

/// <summary>One published release, bounded. Only what a candidate needs to name.</summary>
/// <param name="Id">GitHub's own numeric identity for the release, which is the candidate's source key.</param>
/// <param name="Tag">The tag name the release was cut from.</param>
/// <param name="Name">The release name, which may be empty.</param>
/// <param name="Sha">The commit the tag names, when GitHub reported one.</param>
/// <param name="PreRelease">Whether GitHub marks the release a pre-release.</param>
/// <param name="PublishedAt">When the release was published.</param>
public sealed record GitHubRelease(
    long Id,
    string Tag,
    string Name,
    string? Sha,
    bool PreRelease,
    DateTimeOffset? PublishedAt);

/// <summary>One tag, bounded. The sha is resolved by the client because a tag listing does not carry one.</summary>
/// <param name="Name">The tag name, as GitHub spells it.</param>
/// <param name="Sha">The immutable commit the tag names, or <c>null</c> when it could not be resolved.</param>
public sealed record GitHubTag(string Name, string? Sha);
