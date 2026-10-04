using Chronicleaf.Application.Milestones;
using Chronicleaf.Core;
using Chronicleaf.GitHub.Evidence;

namespace Chronicleaf.Application.GitHub;

/// <summary>
/// The GitHub evidence use cases (GITHUB-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Four operations, and the split between them is the one the design fixes. Connecting and reading
/// a connection answer immediately and write nothing but a connection. Asking for a collection
/// returns a job and writes nothing but a job. The collection itself happens later, in the job, and
/// is the only path that appends evidence.
/// </para>
/// <para>
/// That is why <see cref="RequestSyncAsync"/> returns an operation rather than a bundle: a GitHub
/// read is a network call with a rate limit and a timeout attached to it, and holding a request open
/// for it would make an owner's browser the retry budget. The delivered operation route is how
/// progress is read.
/// </para>
/// <para>
/// The scope always comes from the owner session. Nothing in this contract accepts a scope, a
/// credential value or a host from a caller.
/// </para>
/// </remarks>
public interface IGitHubEvidenceService
{
    /// <summary>
    /// Validates that the named repository is publicly readable and stores it as this project's
    /// connection, replacing any connection the project already had.
    /// </summary>
    /// <remarks>
    /// The validation is a real read of the repository's own metadata: a connection this package
    /// stores is one GitHub answered for, so a private or misspelled repository is refused here
    /// rather than at the end of a later collection.
    /// </remarks>
    /// <exception cref="ValidationFailedException">The owner or repository is not one GitHub names.</exception>
    /// <exception cref="ResourceNotFoundException">The project is not in the owner's scope.</exception>
    /// <exception cref="ProjectArchivedException">The project is archived.</exception>
    /// <exception cref="GitHubRepositoryUnavailableException">The repository is private or does not exist.</exception>
    /// <exception cref="GitHubCredentialRejectedException">GitHub refused the supplied credential.</exception>
    /// <exception cref="GitHubUnavailableException">GitHub could not be read.</exception>
    Task<GitHubConnection> ConnectAsync(
        OwnerScope owner,
        Guid projectId,
        GitHubConnectionRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a project's connection, or <c>null</c> when it has none.</summary>
    /// <exception cref="ResourceNotFoundException">The project is not in the owner's scope.</exception>
    Task<GitHubConnection?> ReadConnectionAsync(
        OwnerScope owner,
        Guid projectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks for a collection and returns the job that will perform it. A repeat under the same
    /// idempotency key returns the job the first request created.
    /// </summary>
    /// <exception cref="ValidationFailedException">The refs are not ones a request may name.</exception>
    /// <exception cref="ResourceNotFoundException">The milestone is not in the owner's scope.</exception>
    /// <exception cref="ProjectArchivedException">The project is archived.</exception>
    /// <exception cref="GitHubConnectionMissingException">The project has no repository connection.</exception>
    Task<GitHubSyncAccepted> RequestSyncAsync(
        OwnerScope owner,
        Guid milestoneId,
        GitHubSyncRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs one collection: resolves the refs, reads the bounded facts, appends them to the
    /// milestone's evidence and records the run. This is what the job handler calls.
    /// </summary>
    /// <remarks>
    /// Nothing here is retried internally. A refusal is raised as the delivered classified job
    /// failure, so the attempt budget, the backoff and the terminal status are the delivered ones.
    /// </remarks>
    Task<GitHubCollected> CollectAsync(
        OwnerScope owner,
        Guid operationId,
        CancellationToken cancellationToken = default);
}
