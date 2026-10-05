using Chronicleaf.Application.Milestones;
using Chronicleaf.Core;

namespace Chronicleaf.Application.Git;

/// <summary>
/// The local Git evidence use cases (LOCAL-GIT-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Four operations, and the split is the one the design fixes. Registering and reading a repository
/// answer immediately and write nothing but a registration. Asking for a collection returns a job and
/// writes nothing but a job. The collection itself happens later, in the job, and is the only path
/// that appends evidence.
/// </para>
/// <para>
/// That is why <see cref="RequestSyncAsync"/> returns an operation rather than a bundle: reading a
/// repository runs several Git processes against a disk, and holding a request open across them would
/// make an owner's browser the timeout. The delivered operation route is how progress is read.
/// </para>
/// <para>
/// <b>The scope always comes from the owner session.</b> Nothing in this contract accepts a scope, a
/// directory or a credential value from a caller. There is no credential in this package at all: a
/// local repository is read with the operator's own Git and needs no token, which is why this surface
/// has nothing that could leak one.
/// </para>
/// </remarks>
public interface ILocalGitEvidenceService
{
    /// <summary>
    /// Confirms the named path is a Git repository inside an operator-registered root, and stores it as
    /// this project's local repository, replacing any registration the project already had.
    /// </summary>
    /// <remarks>
    /// The validation is a real read of the repository's own metadata, made by running Git against the
    /// resolved directory: a registration this package stores is one Git could read, so a path that is
    /// not a repository, or one that escapes its root through a symbolic link, is refused here rather
    /// than at the end of a later collection.
    /// </remarks>
    /// <exception cref="ValidationFailedException">The alias or the path is not a shape a request may carry.</exception>
    /// <exception cref="ResourceNotFoundException">The project is not in the owner's scope.</exception>
    /// <exception cref="ProjectArchivedException">The project is archived.</exception>
    /// <exception cref="LocalGitNotAvailableInHostedException">This deployment may not read local directories.</exception>
    /// <exception cref="LocalGitRootUnknownException">The alias is not one an operator registered.</exception>
    /// <exception cref="LocalGitOutsideRootException">The path escapes its registered root.</exception>
    /// <exception cref="LocalGitNotARepositoryException">The path is not a Git repository.</exception>
    /// <exception cref="LocalGitUnavailableException">Git is not installed or not on this process's PATH.</exception>
    /// <exception cref="LocalGitTimeoutException">Git outlived this package's per-process bound.</exception>
    Task<LocalRepository> RegisterAsync(
        OwnerScope owner,
        Guid projectId,
        LocalRepositoryRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Reads a project's local repository, or <c>null</c> when it has none.</summary>
    /// <exception cref="LocalGitNotAvailableInHostedException">This deployment may not read local directories.</exception>
    /// <exception cref="ResourceNotFoundException">The project is not in the owner's scope.</exception>
    Task<LocalRepository?> ReadAsync(
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
    /// <exception cref="LocalGitRepositoryMissingException">The project has no local repository registered.</exception>
    /// <exception cref="LocalGitNotAvailableInHostedException">This deployment may not read local directories.</exception>
    Task<LocalGitSyncAccepted> RequestSyncAsync(
        OwnerScope owner,
        Guid milestoneId,
        LocalGitSyncRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs one collection: resolves the refs to immutable revisions, reads the bounded committed
    /// facts, appends them to the milestone's evidence and records the run. This is what the job handler
    /// calls.
    /// </summary>
    /// <remarks>
    /// Nothing here is retried internally. A refusal is raised as the delivered classified job
    /// failure, so the attempt budget, the backoff and the terminal statuses are the delivered ones.
    /// </remarks>
    Task<LocalGitCollected> CollectAsync(
        OwnerScope owner,
        Guid operationId,
        CancellationToken cancellationToken = default);
}
