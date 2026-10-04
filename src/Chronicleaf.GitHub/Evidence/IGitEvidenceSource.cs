namespace Chronicleaf.GitHub.Evidence;

/// <summary>
/// What to collect from one repository, and the caps the collection must not exceed
/// (GITHUB-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The caps live on the request rather than inside the client for one reason: they are this
/// product's promises about how much of somebody else's repository becomes evidence, and a promise
/// that lives in a transport is a promise no caller can see. The client enforces whatever it is
/// given, reports what it had to leave out, and the caller can therefore read the caps off the
/// request it wrote.
/// </para>
/// <para>
/// A request is expressed in refs, never in SHAs. The resolution to an immutable revision is the
/// client's job and happens before anything is collected, because a ref that moves mid-collection
/// would otherwise produce a set of facts that never coexisted at any one commit.
/// </para>
/// </remarks>
/// <param name="BaseRef">The ref to compare from. Null means "no comparison was asked for".</param>
/// <param name="HeadRef">The ref to collect. Required.</param>
/// <param name="MaxCommits">At most this many commits are read and recorded.</param>
/// <param name="MaxChangedFiles">At most this many changed files are read and recorded.</param>
/// <param name="MaxDiffBytes">At most this many bytes of diff text are read and recorded.</param>
/// <param name="IssueNumber">An issue to read as well, or <c>null</c>.</param>
/// <param name="PullRequestNumber">A pull request to read as well, or <c>null</c>.</param>
/// <param name="ReleaseTag">A release tag to read as well, or <c>null</c>.</param>
public sealed record GitEvidenceRequest
{
    public GitEvidenceRequest(
        string baseRef,
        string headRef,
        int maxCommits = 100,
        int maxChangedFiles = 100,
        int maxDiffBytes = 200 * 1024,
        int? issueNumber = null,
        int? pullRequestNumber = null,
        string? releaseTag = null)
    {
        BaseRef = baseRef;
        HeadRef = headRef;
        MaxCommits = maxCommits;
        MaxChangedFiles = maxChangedFiles;
        MaxDiffBytes = maxDiffBytes;
        IssueNumber = issueNumber;
        PullRequestNumber = pullRequestNumber;
        ReleaseTag = releaseTag;
    }

    /// <summary>The ref to compare from, or <c>null</c> when no comparison was asked for.</summary>
    public string? BaseRef { get; }

    /// <summary>The ref to collect.</summary>
    public string HeadRef { get; }

    /// <summary>At most this many commits are read and recorded.</summary>
    public int MaxCommits { get; }

    /// <summary>At most this many changed files are read and recorded.</summary>
    public int MaxChangedFiles { get; }

    /// <summary>At most this many bytes of diff text are read and recorded.</summary>
    public int MaxDiffBytes { get; }

    /// <summary>An issue to read as well, or <c>null</c>.</summary>
    public int? IssueNumber { get; }

    /// <summary>A pull request to read as well, or <c>null</c>.</summary>
    public int? PullRequestNumber { get; }

    /// <summary>A release tag to read as well, or <c>null</c>.</summary>
    public string? ReleaseTag { get; }
}

/// <summary>
/// One read-only source of GitHub facts (GITHUB-EVIDENCE-001).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every method here is a read.</b> The interface has no write, no create, no comment and no
/// merge, and a repository this product can see is a repository it only reads. That is a property
/// of the shape rather than of a policy that could be forgotten: there is no verb to call.
/// </para>
/// <para>
/// An implementation must be safe to call concurrently, must answer with a
/// <see cref="GitEvidenceResult"/> rather than an exception for a refusal the caller has to
/// classify, and must never log or return the credential it was given.
/// </para>
/// </remarks>
public interface IGitEvidenceSource
{
    /// <summary>
    /// Confirms the repository is publicly readable and reports what GitHub says about it, including
    /// the default branch. This is the check a connection is validated with, and it never writes.
    /// </summary>
    Task<GitEvidenceResult> DescribeRepositoryAsync(
        RepoRef repository,
        string? credential,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the requested refs to immutable revisions and collects the bounded facts between
    /// them. Both refs are resolved before any fact is read.
    /// </summary>
    Task<GitEvidenceResult> FetchAsync(
        RepoRef repository,
        GitEvidenceRequest request,
        string? credential,
        CancellationToken cancellationToken = default);
}
