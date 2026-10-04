namespace Chronicleaf.GitHub.Evidence;

/// <summary>
/// What one collection attempt produced, or why it produced nothing (GITHUB-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// A refusal is a value here, not an exception. The four ways GitHub can answer "no" mean four
/// different things to a job: a repository that is absent or private is permanent, a rejected
/// credential is permanent, a 429 is worth another attempt after the provider's own delay, and
/// anything else is a dependency that may come back. Collapsing those into a thrown exception would
/// force every caller to re-derive the classification from an error message, and the caller that
/// guessed wrong either retried a refusal forever or gave up on a throttling response.
/// </para>
/// <para>
/// A successful result carries what was read <em>and</em> what was left out. Truncation and
/// exclusion are separate lists rather than a boolean, because "we stopped after 100 commits" and
/// "we dropped .env" are different facts about a different segment, and a reader has to be able to
/// name which one happened.
/// </para>
/// </remarks>
public sealed class GitEvidenceResult
{
    private static readonly IReadOnlyList<GitEndpoint> NoEndpoints = [];
    private static readonly IReadOnlyList<GitCommit> NoCommits = [];
    private static readonly IReadOnlyList<GitIssue> NoIssues = [];
    private static readonly IReadOnlyList<GitPullRequest> NoPullRequests = [];
    private static readonly IReadOnlyList<GitExclusion> NoExclusions = [];
    private static readonly IReadOnlyList<GitTruncation> NoTruncations = [];

    /// <summary>
    /// Builds a result. Public because this contract has more than one implementation: any
    /// transport that satisfies <see cref="IGitEvidenceSource"/> has to be able to say what it read,
    /// and a private constructor would tie this value shape to the client that happens to ship first.
    /// </summary>
    public GitEvidenceResult(
        GitEvidenceOutcome outcome,
        RepoRef? repository,
        DateTimeOffset fetchedAt,
        TimeSpan? retryAfter = null)
    {
        Outcome = outcome;
        Repository = repository;
        FetchedAt = fetchedAt;
        RetryAfter = retryAfter;
    }

    /// <summary>How the collection ended.</summary>
    public GitEvidenceOutcome Outcome { get; }

    /// <summary>The repository that was read, when the attempt reached one.</summary>
    public RepoRef? Repository { get; }

    /// <summary>GitHub's own identity for the repository, when it answered.</summary>
    public long? RepositoryId { get; init; }

    /// <summary>The default branch GitHub reports, when the repository metadata was read.</summary>
    public string? DefaultBranch { get; init; }

    /// <summary>The immutable commit the base ref resolved to, or <c>null</c> when none was asked for.</summary>
    public string? ResolvedBaseSha { get; init; }

    /// <summary>The immutable commit the head ref resolved to, or <c>null</c> when it could not be resolved.</summary>
    public string? ResolvedHeadSha { get; init; }

    /// <summary>At most the requested number of commits, in the order GitHub returned them.</summary>
    public IReadOnlyList<GitCommit> Commits { get; init; } = NoCommits;

    /// <summary>The compare between the resolved base and head, when one was collected.</summary>
    public GitCompare? Compare { get; init; }

    /// <summary>At most the requested number of issues.</summary>
    public IReadOnlyList<GitIssue> Issues { get; init; } = NoIssues;

    /// <summary>At most the requested number of pull requests.</summary>
    public IReadOnlyList<GitPullRequest> PullRequests { get; init; } = NoPullRequests;

    /// <summary>The release or tag, when one was asked for and found.</summary>
    public GitRelease? Release { get; init; }

    /// <summary>
    /// Every optional part of the request that was asked for and could not be read, each with the
    /// reason. A part that is missing is recorded as missing here rather than being dropped, so an
    /// owner is never told a comparison covers an issue it does not mention.
    /// </summary>
    public IReadOnlyList<GitUnavailable> Unavailable { get; init; } = [];

    /// <summary>Every endpoint that was actually consulted, in order.</summary>
    public IReadOnlyList<GitEndpoint> Endpoints { get; init; } = NoEndpoints;

    /// <summary>Every file whose content was deliberately not collected, each with its reason.</summary>
    public IReadOnlyList<GitExclusion> Exclusions { get; init; } = NoExclusions;

    /// <summary>Every segment that stopped at a cap, each with what it stopped at.</summary>
    public IReadOnlyList<GitTruncation> Truncations { get; init; } = NoTruncations;

    /// <summary>When the answer was produced.</summary>
    public DateTimeOffset FetchedAt { get; }

    /// <summary>
    /// The delay the provider asked for on a throttled answer, clamped by the caller. Never
    /// trusted as-is: a response body must not be able to park an operation for a year.
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>A collection that read what it asked for.</summary>
    public static GitEvidenceResult Collected(RepoRef repository, DateTimeOffset fetchedAt) =>
        new GitEvidenceResult(GitEvidenceOutcome.Collected, repository, fetchedAt, retryAfter: null);

    /// <summary>
    /// GitHub answered 404. A private repository and a repository that does not exist are the same
    /// answer on purpose, so an owner cannot use this to discover which private repositories exist.
    /// </summary>
    public static GitEvidenceResult RepositoryUnavailable(DateTimeOffset fetchedAt) =>
        new(GitEvidenceOutcome.RepositoryUnavailable, repository: null, fetchedAt, retryAfter: null);

    /// <summary>GitHub answered 401 or 403: the credential was refused, or is absent and needed.</summary>
    public static GitEvidenceResult CredentialRejected(DateTimeOffset fetchedAt) =>
        new(GitEvidenceOutcome.CredentialRejected, repository: null, fetchedAt, retryAfter: null);

    /// <summary>GitHub answered 429 and said when it may be asked again.</summary>
    public static GitEvidenceResult RateLimited(DateTimeOffset fetchedAt, TimeSpan? retryAfter) =>
        new(GitEvidenceOutcome.RateLimited, repository: null, fetchedAt, retryAfter);

    /// <summary>
    /// GitHub could not answer: a 5xx, a transport failure, a timeout, or a response that was not
    /// the JSON this client can read. Worth another attempt, never reported as a fact.
    /// </summary>
    public static GitEvidenceResult CouldNotBeRead(DateTimeOffset fetchedAt) =>
        new(GitEvidenceOutcome.Unavailable, repository: null, fetchedAt, retryAfter: null);

    /// <summary>GitHub refused the request itself: a malformed ref, or a repository that is not public.</summary>
    public static GitEvidenceResult Rejected(DateTimeOffset fetchedAt) =>
        new(GitEvidenceOutcome.Rejected, repository: null, fetchedAt, retryAfter: null);
}

/// <summary>The five answers a collection attempt can give, and nothing else.</summary>
public enum GitEvidenceOutcome
{
    /// <summary>What was asked for was read.</summary>
    Collected,

    /// <summary>The repository is absent or private. GitHub does not distinguish them.</summary>
    RepositoryUnavailable,

    /// <summary>The credential was refused, or one was required and none was configured.</summary>
    CredentialRejected,

    /// <summary>GitHub asked us to slow down and said when.</summary>
    RateLimited,

    /// <summary>The request was refused as malformed or as pointing at something that is not public.</summary>
    Rejected,

    /// <summary>GitHub could not answer right now.</summary>
    Unavailable,
}

/// <summary>
/// The closed vocabulary a missing part of a collection is recorded in. A reason is never a message
/// copied out of a response body, so nothing GitHub says can end up in stored evidence.
/// </summary>
public static class GitUnavailableReasons
{
    /// <summary>GitHub answered 404 for the part that was asked for.</summary>
    public const string NotFound = "not-found";

    /// <summary>GitHub refused the credential.</summary>
    public const string CredentialRejected = "credential-rejected";

    /// <summary>GitHub asked us to slow down.</summary>
    public const string RateLimited = "rate-limited";

    /// <summary>GitHub refused the request itself.</summary>
    public const string Rejected = "rejected";

    /// <summary>GitHub could not answer, or answered with something this client cannot read.</summary>
    public const string Unreachable = "unreachable";

    /// <summary>GitHub returned the file and no patch for it, which is what a binary file gets.</summary>
    public const string PatchAbsent = "patch-absent";

    /// <summary>The diff text budget was spent before this file's patch was read.</summary>
    public const string DiffTextLimit = "diff-text-limit";
}

/// <summary>
/// One part of the request that was asked for and could not be read. The segment names what was
/// missing and the reason names why, so a reader can tell an absent fact from a collected one.
/// </summary>
/// <param name="Segment">Which part was missing: <c>issue:12</c>, <c>pr:34</c> or <c>release:v1</c>.</param>
/// <param name="Reason">Why it is missing, from this package's own fixed vocabulary.</param>
public sealed record GitUnavailable(string Segment, string Reason);

/// <summary>One endpoint that was consulted, recorded so a reader can see where a fact came from.</summary>/// <param name="Method">The HTTP method used. Always GET here: this package never writes to GitHub.</param>
/// <param name="Path">The path relative to the API host, without the host itself.</param>
/// <param name="Status">The status GitHub answered, or 0 when the request never got one.</param>
public sealed record GitEndpoint(string Method, string Path, int Status);

/// <summary>One commit, bounded: the fields a fact about a commit needs and nothing more.</summary>
/// <param name="Sha">The immutable commit id.</param>
/// <param name="Summary">The first line of the commit message, bounded in length.</param>
/// <param name="Author">The author login or name as GitHub reported it, bounded in length.</param>
/// <param name="AuthoredAt">When the commit says it was authored, when GitHub reports it.</param>
public sealed record GitCommit(string Sha, string Summary, string? Author, DateTimeOffset? AuthoredAt);

/// <summary>One issue, as the source reported it, bounded.</summary>
/// <param name="Number">The issue number within the repository.</param>
/// <param name="Title">The issue title, bounded in length.</param>
/// <param name="State">GitHub's own state: open or closed.</param>
/// <param name="Reference">The issue's own web address, when the source gave one.</param>
public sealed record GitIssue(int Number, string Title, string State, string? Reference);

/// <summary>One pull request, as the source reported it, bounded.</summary>
/// <param name="Number">The pull request number within the repository.</param>
/// <param name="Title">The pull request title, bounded in length.</param>
/// <param name="State">GitHub's own state.</param>
/// <param name="Merged">Whether GitHub reports the pull request as merged.</param>
/// <param name="Reference">The pull request's own web address, when the source gave one.</param>
public sealed record GitPullRequest(int Number, string Title, string State, bool Merged, string? Reference);

/// <summary>One published release or tag, bounded.</summary>
/// <param name="Tag">The tag name.</param>
/// <param name="Name">The release name, which may be empty.</param>
/// <param name="Draft">Whether GitHub marks the release a draft.</param>
/// <param name="PreRelease">Whether GitHub marks the release a pre-release.</param>
/// <param name="PublishedAt">When the release was published, when GitHub reports it.</param>
public sealed record GitRelease(string Tag, string Name, bool Draft, bool PreRelease, DateTimeOffset? PublishedAt);

/// <summary>The comparison between two resolved revisions.</summary>
/// <param name="Status">GitHub's own compare status: ahead, behind, identical or diverged.</param>
/// <param name="TotalCommits">How many commits the comparison contains, as GitHub counted them.</param>
/// <param name="Files">The changed files, bounded and filtered.</param>
public sealed record GitCompare(string Status, int TotalCommits, IReadOnlyList<GitFile> Files);

/// <summary>One changed file within a comparison.</summary>
/// <param name="Path">The path as the source reported it.</param>
/// <param name="Status">GitHub's own status for the file: added, modified, removed or renamed.</param>
/// <param name="Additions">Lines added, as GitHub counted them.</param>
/// <param name="Deletions">Lines removed, as GitHub counted them.</param>
/// <param name="Patch">The unified diff text, or <c>null</c> when there is none to record.</param>
/// <param name="PatchMissing">
/// True when GitHub returned no patch for this file - a binary file, a file past GitHub's own
/// patch limit, or one this client refused to read. It is never turned into an empty diff.
/// </param>
/// <param name="ExclusionReason">Why the content was not collected, when it was not collected.</param>
public sealed record GitFile(
    string Path,
    string Status,
    int Additions,
    int Deletions,
    string? Patch,
    bool PatchMissing,
    string? ExclusionReason);

/// <summary>
/// One file whose content was deliberately not collected, named with the reason. Excluded content is
/// recorded as a fact about the exclusion, never as the excluded bytes.
/// </summary>
/// <param name="Path">The file that was excluded.</param>
/// <param name="Reason">Why it was excluded, from this package's own fixed vocabulary.</param>
public sealed record GitExclusion(string Path, string Reason);

/// <summary>
/// One segment that stopped at a cap. The cap and the reason are both recorded, because a truncated
/// segment that does not say so is indistinguishable from a short one.
/// </summary>
/// <param name="Segment">Which part of the collection stopped: commits, files or diff text.</param>
/// <param name="Kept">How much was kept.</param>
/// <param name="Cap">The cap that stopped it.</param>
public sealed record GitTruncation(string Segment, int Kept, int Cap);
