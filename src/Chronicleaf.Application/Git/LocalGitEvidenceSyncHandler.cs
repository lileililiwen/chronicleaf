using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Operations;

namespace Chronicleaf.Application.Git;

/// <summary>
/// The job that performs one local Git collection (LOCAL-GIT-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// This handler is almost empty on purpose, for the delivered GitHub package's reason. It is handed an
/// operation id, resolves the owner scope the operation was created in, and asks the service to do the
/// work. It holds no store, so it cannot write a status, a result reference or an error code: the only
/// way anything reaches the operation row is the worker's own conditional commit, which this class never
/// sees.
/// </para>
/// <para>
/// <b>Every refusal is the delivered classified kind.</b> The service raises
/// <see cref="OperationPermanentException"/> for something a second attempt cannot change,
/// <see cref="OperationTransientException"/> for a Git process that may answer next time, and the
/// delivered worker owns the attempt budget, the backoff and the terminal statuses.
/// </para>
/// <para>
/// <b>The scope is the operation's, never a request's.</b> A job runs long after the request that created
/// it is gone, so the identity it acts under comes from the operation row the worker read.
/// </para>
/// </remarks>
public sealed class LocalGitEvidenceSyncHandler(ILocalGitEvidenceService evidence) : IOperationHandler
{
    /// <inheritdoc />
    public string Kind => LocalGitEvidenceService.SyncOperationKind;

    /// <inheritdoc />
    public async Task<OperationResult> HandleAsync(
        OperationRef operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (!string.Equals(operation.Kind, Kind, StringComparison.Ordinal))
        {
            // A handler only runs for its own kind. Being handed another one is a wiring mistake, and
            // refusing it here is what stops this class from collecting anything under a kind it does not
            // own.
            throw new InvalidOperationException(
                $"This handler serves {Kind} and was handed {operation.Kind}.");
        }

        var owner = new OwnerScope(operation.ScopeId, Guid.Empty);
        var collected = await evidence
            .CollectAsync(owner, operation.Id, cancellationToken)
            .ConfigureAwait(false);

        // The result reference is the evidence revision this run produced, which is the one thing a reader
        // of the operation row can act on. It carries no evidence text, no repository name and no
        // directory. A run that appended nothing never reaches here: it fails with a named code instead, so
        // a result reference always means a bundle exists.
        return new OperationResult($"{Kind}:{collected.BundleRevision}");
    }
}
