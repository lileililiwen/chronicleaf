namespace Chronicleaf.Core;

/// <summary>
/// One local Git repository a project collects evidence from (LOCAL-GIT-EVIDENCE-002).
/// </summary>
/// <remarks>
/// <para>
/// A connection is an operator-registered root alias and a path relative to it. That is the whole of
/// it, and the two properties are the point.
/// </para>
/// <para>
/// <b>The alias is a name, never a path.</b> The absolute path lives in the operator's configuration
/// and nowhere else: this row cannot hold it, a response cannot publish it, and a log line cannot
/// carry it. An owner chooses between the roots an operator registered, and a project that named
/// anything outside one of them is refused before Git is asked about anything. A stored row that
/// could hold a directory would be a row a request could aim the server's filesystem reads at.
/// </para>
/// <para>
/// <b>The relative path is validated and re-validated.</b> It is checked for shape when the owner
/// connects the repository, and the resolved physical path is checked for containment again
/// immediately before every collection, because a path that was inside a root when it was stored can
/// be a symlink pointing outside it by the time a job runs. Both checks are made by the filesystem
/// rather than by the spelling, so a name that looks harmless and a link that points at
/// <c>/etc</c> are refused the same way.
/// </para>
/// <para>
/// <b>No credential column, and no remote.</b> A local repository is read from the operator's own
/// disk with the operator's own Git, needs no token, and this package never writes to it - so there
/// is nothing here a secret could be written into and no remote this product could push to.
/// </para>
/// </remarks>
/// <param name="id">This connection's identifier.</param>
/// <param name="scopeId">The owning scope. Every read of this row is scoped by it.</param>
/// <param name="projectId">The project this repository belongs to.</param>
/// <param name="rootAlias">The operator-registered root's alias, never a path.</param>
/// <param name="relativePath">The repository's canonical path inside that root, relative and bounded.</param>
/// <param name="defaultBranch">The branch the repository's HEAD names, when it names one.</param>
/// <param name="headSha">The revision HEAD resolved to when the connection was last validated.</param>
/// <param name="createdAt">When the connection was first validated and stored.</param>
/// <param name="updatedAt">When the connection last changed.</param>
public sealed class LocalRepository
{
    /// <param name="id">This connection's identifier.</param>
    /// <param name="scopeId">The owning scope. Every read of this row is scoped by it.</param>
    /// <param name="projectId">The project this repository belongs to.</param>
    /// <param name="rootAlias">The operator-registered root's alias, never a path.</param>
    /// <param name="relativePath">The repository's canonical path inside that root, relative and bounded.</param>
    /// <param name="defaultBranch">The branch the repository's HEAD names, when it names one.</param>
    /// <param name="headSha">The revision HEAD resolved to when the connection was last validated.</param>
    /// <param name="createdAt">When the connection was first validated and stored.</param>
    /// <param name="updatedAt">When the connection last changed.</param>
    public LocalRepository(
        Guid id,
        Guid scopeId,
        Guid projectId,
        string rootAlias,
        string relativePath,
        string? defaultBranch,
        string? headSha,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootAlias);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (id == Guid.Empty)
        {
            throw new ArgumentException("A local repository needs an id.", nameof(id));
        }

        if (scopeId == Guid.Empty)
        {
            throw new ArgumentException("A local repository needs a scope.", nameof(scopeId));
        }

        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("A local repository needs a project.", nameof(projectId));
        }

        if (Path.IsPathRooted(relativePath))
        {
            // An absolute path is a directory, and a stored directory is the one thing this package
            // must never be handed by a request. It is refused by the record itself as well as by the
            // request policy, so no caller of this constructor can build one.
            throw new ArgumentException(
                "A local repository is named by a path relative to a registered root, never absolutely.",
                nameof(relativePath));
        }

        Id = id;
        ScopeId = scopeId;
        ProjectId = projectId;
        RootAlias = rootAlias;
        RelativePath = relativePath;
        DefaultBranch = defaultBranch;
        HeadSha = headSha;
        CreatedAt = createdAt;
        UpdatedAt = updatedAt;
    }

    public Guid Id { get; }

    public Guid ScopeId { get; }

    public Guid ProjectId { get; }

    /// <summary>The operator-registered root's alias. A name, never a directory.</summary>
    public string RootAlias { get; }

    /// <summary>The repository's path inside that root. Relative, bounded, and free of traversal.</summary>
    public string RelativePath { get; }

    /// <summary>The branch HEAD named when the connection was validated, when it named one.</summary>
    public string? DefaultBranch { get; }

    /// <summary>
    /// The revision HEAD resolved to at validation time. Recorded, and never used as a fallback: a
    /// collection that fell back to a revision nobody asked for would be a different collection.
    /// </summary>
    public string? HeadSha { get; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; }

    /// <summary>The two parts, joined the way an operator reads them. Never a usable path.</summary>
    public string FullName => $"{RootAlias}/{RelativePath}";
}

/// <summary>
/// One record of one local collection run (LOCAL-GIT-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>The row is written when the owner asks and completed when the run finishes.</b> A job payload
/// carries an operation id and nothing else, so the words the owner typed - which root, which refs -
/// have to live somewhere the worker can read without trusting a queue message. A row with a null
/// <see cref="HeadSha"/> or a null <see cref="CompletedAt"/> is therefore a run that was asked for
/// and did not finish, which is a true statement about it; it is never a placeholder standing in for
/// a fact.
/// </para>
/// <para>
/// <b><see cref="CommandLog"/> records the verbs, never the operands.</b> The GitHub run log keeps the
/// API paths it consulted, which are what a reader needs in order to repeat the read. A local run's
/// equivalent is which subcommands ran and in which order, and the operands are exactly the things
/// that must not be persisted: an operator's root path, a ref an owner typed, and whatever a
/// repository happens to be called on a shared machine. So the log holds <c>rev-parse</c>,
/// <c>log</c> and <c>diff</c>, and the bound is a list of words rather than a path.
/// </para>
/// <para>
/// <see cref="UncommittedCount"/> is the honest part about a dirty working tree. It says how many
/// changes existed that were deliberately not read, so a reader is never left believing the evidence
/// covers the state of the directory rather than the state of the commit. It is a count and never a
/// list of names, because one of those names could be a file nobody meant to publish.
/// </para>
/// </remarks>
/// <param name="id">This record's identifier.</param>
/// <param name="scopeId">The owning scope. Every read of this row is scoped by it.</param>
/// <param name="operationId">The operation that performs the collection. One run, one operation.</param>
/// <param name="milestoneId">The milestone the evidence is appended to.</param>
/// <param name="expectedBundleRevision">The evidence revision the owner was looking at when they asked.</param>
/// <param name="rootAlias">The registered root the repository was read from.</param>
/// <param name="relativePath">The repository's path inside that root.</param>
/// <param name="headRef">The ref the owner asked to collect.</param>
/// <param name="baseRef">The ref the owner asked to compare from, when they gave one.</param>
/// <param name="createdAt">When the run was asked for.</param>
public sealed class LocalSyncSource
{
    /// <param name="id">This record's identifier.</param>
    /// <param name="scopeId">The owning scope. Every read of this row is scoped by it.</param>
    /// <param name="operationId">The operation that performs the collection. One run, one operation.</param>
    /// <param name="milestoneId">The milestone the evidence is appended to.</param>
    /// <param name="expectedBundleRevision">The evidence revision the owner was looking at when they asked.</param>
    /// <param name="rootAlias">The registered root the repository was read from.</param>
    /// <param name="relativePath">The repository's path inside that root.</param>
    /// <param name="headRef">The ref the owner asked to collect.</param>
    /// <param name="baseRef">The ref the owner asked to compare from, when they gave one.</param>
    /// <param name="createdAt">When the run was asked for.</param>
    public LocalSyncSource(
        Guid id,
        Guid scopeId,
        Guid operationId,
        Guid milestoneId,
        long expectedBundleRevision,
        string rootAlias,
        string relativePath,
        string headRef,
        string? baseRef,
        DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootAlias);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(headRef);

        if (id == Guid.Empty)
        {
            throw new ArgumentException("A local sync source needs an id.", nameof(id));
        }

        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("A local sync source needs the operation that collects it.", nameof(operationId));
        }

        if (milestoneId == Guid.Empty)
        {
            throw new ArgumentException("A local sync source needs a milestone.", nameof(milestoneId));
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
        RootAlias = rootAlias;
        RelativePath = relativePath;
        HeadRef = headRef;
        BaseRef = baseRef;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public Guid ScopeId { get; }

    public Guid OperationId { get; }

    public Guid MilestoneId { get; }

    /// <summary>The evidence revision this run was asked against, and therefore the one it must extend.</summary>
    public long ExpectedBundleRevision { get; }

    public string RootAlias { get; }

    public string RelativePath { get; }

    public string HeadRef { get; }

    public string? BaseRef { get; }

    /// <summary>The immutable base revision the base ref resolved to, once it was resolved.</summary>
    public string? BaseSha { get; private set; }

    /// <summary>The immutable head revision the head ref resolved to, once it was resolved.</summary>
    public string? HeadSha { get; private set; }

    /// <summary>
    /// The subcommands this run issued, comma separated, with no operand. Never a path and never a ref.
    /// </summary>
    public string? CommandLog { get; private set; }

    /// <summary>When the last of those commands answered.</summary>
    public DateTimeOffset? FetchedAt { get; private set; }

    public int CommitCount { get; private set; }

    public int ChangedFileCount { get; private set; }

    /// <summary>True when at least one segment stopped at a cap rather than ending.</summary>
    public bool Truncated { get; private set; }

    public int ExcludedFileCount { get; private set; }

    /// <summary>
    /// How many uncommitted changes existed in the working tree and were deliberately not collected.
    /// Zero means either a clean tree or a tree this run could not enumerate; the evidence item says
    /// which, and this column never claims the first when only the second is known.
    /// </summary>
    public int UncommittedCount { get; private set; }

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
        string commandLog,
        DateTimeOffset fetchedAt,
        int commitCount,
        int changedFileCount,
        bool truncated,
        int excludedFileCount,
        int uncommittedCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resolvedHeadSha);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandLog);

        if (CompletedAt is not null)
        {
            throw new InvalidOperationException("A completed local sync source cannot be completed again.");
        }

        BaseSha = resolvedBaseSha;
        HeadSha = resolvedHeadSha;
        CommandLog = commandLog;
        FetchedAt = fetchedAt;
        CommitCount = commitCount;
        ChangedFileCount = changedFileCount;
        Truncated = truncated;
        ExcludedFileCount = excludedFileCount;
        UncommittedCount = uncommittedCount;
        CompletedAt = fetchedAt;
    }
}
