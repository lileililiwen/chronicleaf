using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Operations;

namespace Chronicleaf.Application.GitHubEnrichment;

/// <summary>
/// The job that performs one GitHub relationship enrichment (GITHUB-GRAPHQL-ENRICHMENT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// This handler is almost empty on purpose. It is handed an operation id, resolves the owner scope
/// the operation was created in, and asks the service to do the work. It holds no store, so it
/// cannot write a status, a result reference or an error code: the only way anything reaches the
/// operation row is the worker's own conditional commit, which this class never sees.
/// </para>
/// <para>
/// <b>Every refusal is the delivered classified kind.</b> The service raises
/// <see cref="OperationPermanentException"/> for something a second attempt cannot change,
/// <see cref="OperationRateLimitedException"/> for GitHub's own bounded delay, and
/// <see cref="OperationTransientException"/> for a dependency that may come back. The attempt
/// budget, the backoff schedule and the terminal statuses are therefore the delivered ones rather
/// than a second policy written here.
/// </para>
/// <para>
/// <b>The scope is the operation's, never a request's.</b> A job runs long after the request that
/// created it is gone, so the identity it acts under comes from the operation row the worker read.
/// </para>
/// </remarks>
public sealed class GitHubEnrichmentHandler(IGitHubEnrichmentService enrichment) : IOperationHandler
{
    /// <inheritdoc />
    public string Kind => GitHubEnrichmentService.EnrichmentOperationKind;

    /// <inheritdoc />
    public async Task<OperationResult> HandleAsync(
        OperationRef operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (!string.Equals(operation.Kind, Kind, StringComparison.Ordinal))
        {
            // A handler only runs for its own kind. Being handed another one is a wiring mistake, and
            // refusing it here is what stops this class from enriching anything under a kind it does
            // not own.
            throw new InvalidOperationException(
                $"This handler serves {Kind} and was handed {operation.Kind}.");
        }

        var owner = new OwnerScope(operation.ScopeId, Guid.Empty);
        var collected = await enrichment
            .EnrichAsync(owner, operation.Id, cancellationToken)
            .ConfigureAwait(false);

        // The result reference is the evidence revision this run produced, which is the one thing a
        // reader of the operation row can act on. It carries no evidence text, no repository name
        // and no credential. A run that appended nothing never reaches here: it fails with
        // `github_enrichment_job_superseded` instead, so a result reference always means a bundle
        // exists.
        return new OperationResult($"{Kind}:{collected.BundleRevision}");
    }
}
