namespace Chronicleaf.Core;

/// <summary>
/// One public GitHub repository a project collects evidence from (GITHUB-EVIDENCE-001).
/// </summary>
/// <remarks>
/// <para>
/// A connection is two names and a scope. There is deliberately no URL column: a row that could
/// hold a host would be a row a caller could point anywhere, and the one thing this package must
/// never do is send a request - with a credential on it - to a host an owner chose. The host is a
/// constant in the transport, and a repository is addressed by owner and name only.
/// </para>
/// <para>
/// <b><see cref="CredentialRef"/> holds a vault reference and never a token.</b> The value is the
/// credential's own id, so a read of this table - by a route, an export, an audit or a test - learns
/// that a credential is configured for GitHub and nothing whatsoever about it. A connection with no
/// credential is valid and is the ordinary case: public repositories are readable without one.
/// </para>
/// <para>
/// <see cref="DefaultBranch"/> is what GitHub reported at validation time. It is recorded so the UI
/// can offer a real default and a reader can see which branch the owner was looking at; it is not
/// used as a silent fallback when a requested ref does not resolve, because a collection that fell
/// back to a branch nobody asked for would be a different collection.
/// </para>
/// </remarks>
public sealed class GitHubConnection
{
    /// <param name="id">This connection's identifier.</param>
    /// <param name="scopeId">The owning scope. Every read of this row is scoped by it.</param>
    /// <param name="projectId">The project this repository belongs to.</param>
    /// <param name="owner">The account or organisation, exactly as GitHub spells it.</param>
    /// <param name="repo">The repository name, exactly as GitHub spells it.</param>
    /// <param name="credentialRef">The vault reference of an optional personal access token, never the token.</param>
    /// <param name="defaultBranch">The default branch GitHub reported, when it reported one.</param>
    /// <param name="repositoryId">GitHub's own numeric identity for the repository, when it answered.</param>
    /// <param name="createdAt">When the connection was first validated and stored.</param>
    /// <param name="updatedAt">When the connection last changed.</param>
    /// <param name="authMode">
    /// How this connection authenticates: <c>pat</c> for the delivered vault token, or
    /// <c>installation</c> for a scoped App installation token that is minted per collection and
    /// never stored. Every row written before the App package existed carries <c>pat</c>.
    /// </param>
    /// <param name="installationId">
    /// GitHub's numeric identity for the App installation, when <paramref name="authMode"/> is
    /// <c>installation</c>. Never a token: the token is minted when it is needed and held only in
    /// memory until just before it expires.
    /// </param>
    public GitHubConnection(
        Guid id,
        Guid scopeId,
        Guid projectId,
        string owner,
        string repo,
        Guid? credentialRef,
        string? defaultBranch,
        long? repositoryId,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        string authMode = GitHubAuthModes.Pat,
        long? installationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);

        if (id == Guid.Empty)
        {
            throw new ArgumentException("A connection needs an id.", nameof(id));
        }

        if (scopeId == Guid.Empty)
        {
            throw new ArgumentException("A connection needs a scope.", nameof(scopeId));
        }

        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("A connection needs a project.", nameof(projectId));
        }

        if (!string.Equals(authMode, GitHubAuthModes.Pat, StringComparison.Ordinal) &&
            !string.Equals(authMode, GitHubAuthModes.Installation, StringComparison.Ordinal))
        {
            throw new ArgumentException("A connection authenticates with a token or an installation.", nameof(authMode));
        }

        if (string.Equals(authMode, GitHubAuthModes.Installation, StringComparison.Ordinal) && installationId is null or < 1)
        {
            throw new ArgumentException("An installation connection names its installation.", nameof(installationId));
        }

        Id = id;
        ScopeId = scopeId;
        ProjectId = projectId;
        Owner = owner;
        Repo = repo;
        CredentialRef = credentialRef;
        DefaultBranch = defaultBranch;
        RepositoryId = repositoryId;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
        AuthMode = authMode;
        InstallationId = installationId;
    }

    public Guid Id { get; }

    public Guid ScopeId { get; }

    public Guid ProjectId { get; }

    public string Owner { get; }

    public string Repo { get; }

    /// <summary>The vault reference of the token used for this repository, or <c>null</c>.</summary>
    public Guid? CredentialRef { get; }

    public string? DefaultBranch { get; }

    public long? RepositoryId { get; }

    /// <summary>How this connection authenticates: <c>pat</c> or <c>installation</c>.</summary>
    public string AuthMode { get; }

    /// <summary>GitHub's installation id, when this connection authenticates through an App.</summary>
    public long? InstallationId { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; }

    /// <summary>The two names, joined the way GitHub writes them.</summary>
    public string FullName => $"{Owner}/{Repo}";
}

/// <summary>
/// The two ways a connection authenticates (GITHUB-APP-CONNECTION-001/002).
/// </summary>
public static class GitHubAuthModes
{
    /// <summary>The delivered personal-access-token path, and the value every existing row carries.</summary>
    public const string Pat = "pat";

    /// <summary>A scoped GitHub App installation token, minted per collection and never stored.</summary>
    public const string Installation = "installation";
}

/// <summary>
/// One GitHub App installation bound to one scope (GITHUB-APP-CONNECTION-001).
/// </summary>
/// <remarks>
/// <para>
/// The row names the installation and the account it was installed on. It holds no token and no
/// secret of any kind: an installation token is minted when it is needed, cached in memory until
/// just before it expires, and never written here.
/// </para>
/// <para>
/// One installation belongs to one scope. The unique index on the installation id is what refuses a
/// second tenant's attach of an installation id it guessed: the first binding wins and the second
/// is a mismatch, not a share.
/// </para>
/// </remarks>
public sealed class GitHubAppInstallation
{
    /// <param name="id">This binding's identifier.</param>
    /// <param name="scopeId">The owning scope. Every read of this row is scoped by it.</param>
    /// <param name="installationId">GitHub's numeric identity for the installation.</param>
    /// <param name="accountLogin">The account or organisation the App was installed on.</param>
    /// <param name="repositorySelection">Whether the installation covers every repository or a selection.</param>
    /// <param name="status">Whether the installation is live here: active, suspended or removed.</param>
    /// <param name="createdAt">When the binding was first stored.</param>
    /// <param name="updatedAt">When the binding last changed.</param>
    public GitHubAppInstallation(
        Guid id,
        Guid scopeId,
        long installationId,
        string accountLogin,
        string repositorySelection,
        string status,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountLogin);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositorySelection);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        if (id == Guid.Empty)
        {
            throw new ArgumentException("An installation binding needs an id.", nameof(id));
        }

        if (scopeId == Guid.Empty)
        {
            throw new ArgumentException("An installation binding needs a scope.", nameof(scopeId));
        }

        if (installationId < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(installationId), installationId, "An installation id is one or greater.");
        }

        Id = id;
        ScopeId = scopeId;
        InstallationId = installationId;
        AccountLogin = accountLogin;
        RepositorySelection = repositorySelection;
        Status = status;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public Guid Id { get; }

    public Guid ScopeId { get; }

    public long InstallationId { get; }

    public string AccountLogin { get; }

    public string RepositorySelection { get; }

    public string Status { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; }

    /// <summary>True while this installation may still mint tokens.</summary>
    public bool IsLive => string.Equals(Status, GitHubAppInstallationStatuses.Active, StringComparison.Ordinal);
}

/// <summary>The states an installation binding may be in.</summary>
public static class GitHubAppInstallationStatuses
{
    public const string Active = "active";
    public const string Suspended = "suspended";
    public const string Removed = "removed";
}
/// </summary>
/// <remarks>
/// <para>
/// <b>The row is written when the owner asks, and completed when the run finishes.</b> That is what
/// makes the request itself durable: a job payload carries an operation id and nothing else, so the
/// words the owner typed - which refs, which issue - have to live somewhere the worker can read
/// without trusting a queue message. A row with a null <see cref="BaseSha"/> or a null
/// <see cref="CompletedAt"/> is therefore a run that was asked for and did not finish, which is a
/// true statement about it; it is never a placeholder standing in for a fact.
/// </para>
/// <para>
/// <b>The markers are the reason this table exists.</b> <see cref="Truncated"/> and
/// <see cref="ExcludedFileCount"/> say that something was left out, and the bundle carries which
/// segments and which files. Keeping the detail in one place means this row can be read as a
/// summary without ever being the only place a limitation is recorded.
/// </para>
/// <para>
/// <see cref="Endpoints"/> is the list of paths consulted, never full URLs: a stored URL would be a
/// place a host could end up, and the host is a constant of this product, not a property of a run.
/// </para>
/// </remarks>
public sealed class SyncSource
{
    /// <param name="id">This record's identifier.</param>
    /// <param name="scopeId">The owning scope. Every read of this row is scoped by it.</param>
    /// <param name="operationId">The operation that performs the collection. One run, one operation.</param>
    /// <param name="milestoneId">The milestone the evidence is appended to.</param>
    /// <param name="expectedBundleRevision">The evidence revision the owner was looking at when they asked.</param>
    /// <param name="owner">The account or organisation of the connection that was used.</param>
    /// <param name="repo">The repository of the connection that was used.</param>
    /// <param name="headRef">The ref the owner asked to collect.</param>
    /// <param name="baseRef">The ref the owner asked to compare from, when they gave one.</param>
    /// <param name="releaseTag">The release tag the owner asked for, when they gave one.</param>
    /// <param name="issueNumber">The issue the owner asked for, when they gave one.</param>
    /// <param name="pullRequestNumber">The pull request the owner asked for, when they gave one.</param>
    /// <param name="createdAt">When the run was asked for.</param>
    public SyncSource(
        Guid id,
        Guid scopeId,
        Guid operationId,
        Guid milestoneId,
        long expectedBundleRevision,
        string owner,
        string repo,
        string headRef,
        string? baseRef,
        string? releaseTag,
        int? issueNumber,
        int? pullRequestNumber,
        DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentException.ThrowIfNullOrWhiteSpace(headRef);

        if (id == Guid.Empty)
        {
            throw new ArgumentException("A sync source needs an id.", nameof(id));
        }

        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A sync source needs the operation that collects it.", nameof(operationId));
        }

        if (milestoneId == Guid.Empty)
        {
            throw new ArgumentException("A sync source needs a milestone.", nameof(milestoneId));
        }

        if (expectedBundleRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedBundleRevision),
                expectedBundleRevision,
                "A bundle revision is zero or greater; zero is a milestone with no evidence yet.");
        }

        Id = id;
        ScopeId = scopeId;
        OperationId = operationId;
        MilestoneId = milestoneId;
        ExpectedBundleRevision = expectedBundleRevision;
        Owner = owner;
        Repo = repo;
        HeadRef = headRef;
        BaseRef = baseRef;
        ReleaseTag = releaseTag;
        IssueNumber = issueNumber;
        PullRequestNumber = pullRequestNumber;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public Guid ScopeId { get; }

    public Guid OperationId { get; }

    public Guid MilestoneId { get; }

    /// <summary>The evidence revision this run was asked against, and therefore the one it must extend.</summary>
    public long ExpectedBundleRevision { get; }

    public string Owner { get; }

    public string Repo { get; }

    public string HeadRef { get; }

    public string? BaseRef { get; }

    public string? ReleaseTag { get; }

    public int? IssueNumber { get; }

    public int? PullRequestNumber { get; }

    /// <summary>The immutable base revision the base ref resolved to, once it was resolved.</summary>
    public string? BaseSha { get; private set; }

    /// <summary>The immutable head revision the head ref resolved to, once it was resolved.</summary>
    public string? HeadSha { get; private set; }

    /// <summary>The paths consulted, comma separated, once they were consulted.</summary>
    public string? Endpoints { get; private set; }

    /// <summary>When the last of those requests answered.</summary>
    public DateTimeOffset? FetchedAt { get; private set; }

    public int CommitCount { get; private set; }

    public int ChangedFileCount { get; private set; }

    /// <summary>True when at least one segment stopped at a cap rather than ending.</summary>
    public bool Truncated { get; private set; }

    public int ExcludedFileCount { get; private set; }

    /// <summary>When the run finished and recorded what it read, or null while it has not.</summary>
    public DateTimeOffset? CompletedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// Records what the run actually read. The only state this row ever takes after it is created,
    /// and it can be taken once: a completed run is a statement about one collection, and a second
    /// completion would be a second claim about the same operation.
    /// </summary>
    /// <exception cref="InvalidOperationException">The run has already been completed.</exception>
    public void Complete(
        string? resolvedBaseSha,
        string resolvedHeadSha,
        string endpoints,
        DateTimeOffset fetchedAt,
        int commitCount,
        int changedFileCount,
        bool truncated,
        int excludedFileCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedHeadSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoints);

        if (CompletedAt is not null)
        {
            throw new InvalidOperationException("A completed sync source cannot be completed again.");
        }

        BaseSha = resolvedBaseSha;
        HeadSha = resolvedHeadSha;
        Endpoints = endpoints;
        FetchedAt = fetchedAt;
        CommitCount = commitCount;
        ChangedFileCount = changedFileCount;
        Truncated = truncated;
        ExcludedFileCount = excludedFileCount;
        CompletedAt = fetchedAt;
    }
}
