namespace Chronicleaf.Core;

/// <summary>
/// One record of one GitHub relationship-enrichment run (GITHUB-GRAPHQL-ENRICHMENT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>The row is written when the owner asks, and never changed afterwards.</b> That is what makes
/// the request itself durable: a job payload carries an operation id and nothing else, so the words
/// the owner typed - which evidence revision they were looking at - have to live somewhere the
/// worker can read without trusting a queue message. A retry of the same operation reads the same
/// row and appends the same facts, which the delivered idempotent append recognises as the same
/// statement rather than as new evidence.
/// </para>
/// <para>
/// What the run observed - the provider node ids, the observed states, the endpoints consulted -
/// is recorded on the evidence bundle the run appended, not here. This row says what was asked;
/// the bundle says what was read. Keeping the detail in one place means this row can be read as a
/// request without ever being the only place a limitation is recorded.
/// </para>
/// </remarks>
public sealed class EnrichmentRun
{
    /// <param name="id">This record's identifier.</param>
    /// <param name="scopeId">The owning scope. Every read of this row is scoped by it.</param>
    /// <param name="operationId">The operation that performs the enrichment. One run, one operation.</param>
    /// <param name="milestoneId">The milestone the relationships are appended to.</param>
    /// <param name="expectedBundleRevision">The evidence revision the owner was looking at when they asked.</param>
    /// <param name="owner">The account or organisation of the connection that was used.</param>
    /// <param name="repo">The repository of the connection that was used.</param>
    /// <param name="createdAt">When the run was asked for.</param>
    public EnrichmentRun(
        Guid id,
        Guid scopeId,
        Guid operationId,
        Guid milestoneId,
        long expectedBundleRevision,
        string owner,
        string repo,
        DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);

        if (id == Guid.Empty)
        {
            throw new ArgumentException("An enrichment run needs an id.", nameof(id));
        }

        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("An enrichment run needs the operation that performs it.", nameof(operationId));
        }

        if (milestoneId == Guid.Empty)
        {
            throw new ArgumentException("An enrichment run needs a milestone.", nameof(milestoneId));
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

    public DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// A new instance for EF Core materialization. Application code uses the constructor, so an
    /// enrichment run is never built half-formed outside the mapper.
    /// </summary>
#pragma warning disable CS8618
    private EnrichmentRun()
    {
    }
#pragma warning restore CS8618
}
