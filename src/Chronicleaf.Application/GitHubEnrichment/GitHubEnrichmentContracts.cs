using Chronicleaf.Core;

namespace Chronicleaf.Application.GitHubEnrichment;

/// <summary>
/// What an owner asked a milestone to enrich (GITHUB-GRAPHQL-ENRICHMENT-001/002).
/// </summary>
/// <remarks>
/// Enrichment reads relationships for evidence that is already stored; it names no refs, no
/// branches and no commit SHAs of its own. The SHAs it queries are the ones the milestone's own
/// bundle already carries, so a request cannot point the read at anything the owner has not
/// already collected.
/// </remarks>
/// <param name="ExpectedBundleRevision">The evidence revision the owner was looking at.</param>
/// <param name="IdempotencyKey">The caller's key: a repeat of the same request returns the same job.</param>
public sealed record GitHubEnrichmentRequest(long ExpectedBundleRevision, string IdempotencyKey);

/// <summary>
/// An enrichment request that was accepted, and the durable job that will carry it out.
/// </summary>
/// <param name="Operation">
/// The operation that exists - the new one, or the original of a replay. A repeat of the same
/// idempotency key returns the original job rather than starting a second enrichment.
/// </param>
/// <param name="Created">False when an identical request had already created this operation.</param>
public sealed record GitHubEnrichmentAccepted(Operation Operation, bool Created);

/// <summary>
/// What one enrichment produced, as the operation's result reference and the numbers a reader needs
/// (GITHUB-GRAPHQL-ENRICHMENT-001/002).
/// </summary>
/// <param name="BundleRevision">The evidence revision the append produced.</param>
/// <param name="ItemCount">How many evidence items were appended.</param>
/// <param name="RelationCount">How many attributed issue/PR relationships were recorded.</param>
/// <param name="QueriedCommitCount">How many collected commits were queried.</param>
/// <param name="Truncated">True when the relationship read stopped at its cap.</param>
public sealed record GitHubEnrichmentCollected(
    long BundleRevision,
    int ItemCount,
    int RelationCount,
    int QueriedCommitCount,
    bool Truncated);

/// <summary>The run-log side of storage: one row per enrichment run.</summary>
public interface IEnrichmentRunStore
{
    /// <summary>Inserts the row for a run that was just asked for.</summary>
    /// <returns>False when a row for this operation is already there, which a replayed request is.</returns>
    Task<bool> TryInsertAsync(EnrichmentRun run, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the run a job was asked to carry out. The job is handed an operation id and nothing
    /// else, so this is the only way it can learn which evidence revision the owner was looking at.
    /// </summary>
    Task<EnrichmentRun?> FindByOperationAsync(
        Guid scopeId,
        Guid operationId,
        CancellationToken cancellationToken = default);
}
