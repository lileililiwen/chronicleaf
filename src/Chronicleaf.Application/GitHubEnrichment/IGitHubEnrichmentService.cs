using Chronicleaf.Application.Milestones;
using Chronicleaf.Core;

namespace Chronicleaf.Application.GitHubEnrichment;

/// <summary>
/// The GitHub relationship-enrichment use cases (GITHUB-GRAPHQL-ENRICHMENT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Two operations, and the split between them is the one the design fixes. Asking for an
/// enrichment returns a job and writes nothing but a run row. The enrichment itself happens later,
/// in the job, and is the only path that appends relationship evidence.
/// </para>
/// <para>
/// That is why <see cref="RequestEnrichmentAsync"/> returns an operation rather than a bundle: a
/// GraphQL read is a network call with a rate limit and a timeout attached to it, and holding a
/// request open for it would make an owner's browser the retry budget. The delivered operation
/// route is how progress is read.
/// </para>
/// <para>
/// The scope always comes from the owner session. Nothing in this contract accepts a scope, a
/// credential value or a host from a caller.
/// </para>
/// </remarks>
public interface IGitHubEnrichmentService
{
    /// <summary>
    /// Asks for an enrichment and returns the job that will perform it. A repeat under the same
    /// idempotency key returns the job the first request created.
    /// </summary>
    /// <exception cref="ValidationFailedException">The revision or key is not one a request may name.</exception>
    /// <exception cref="ResourceNotFoundException">The milestone is not in the owner's scope.</exception>
    /// <exception cref="ProjectArchivedException">The project is archived.</exception>
    /// <exception cref="GitHub.GitHubConnectionMissingException">The project has no repository connection.</exception>
    Task<GitHubEnrichmentAccepted> RequestEnrichmentAsync(
        OwnerScope owner,
        Guid milestoneId,
        GitHubEnrichmentRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs one enrichment: reads the milestone's stored commits, reads their attributed
    /// relationships, appends only the new attributed facts to the milestone's evidence and records
    /// nothing anywhere else. This is what the job handler calls.
    /// </summary>
    /// <remarks>
    /// Nothing here is retried internally. A refusal is raised as the delivered classified job
    /// failure, so the attempt budget, the backoff and the terminal status are the delivered ones.
    /// </remarks>
    Task<GitHubEnrichmentCollected> EnrichAsync(
        OwnerScope owner,
        Guid operationId,
        CancellationToken cancellationToken = default);
}
