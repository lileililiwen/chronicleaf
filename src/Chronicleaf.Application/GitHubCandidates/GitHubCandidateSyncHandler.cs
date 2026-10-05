using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Operations;

namespace Chronicleaf.Application.GitHubCandidates;

/// <summary>
/// The job that performs one candidate poll (GITHUB-RELEASE-CANDIDATES-001).
/// </summary>
/// <remarks>
/// <para>
/// This handler is almost empty on purpose, for the same reason the delivered GitHub collection handler
/// is: it is handed an operation id, resolves the scope the operation was created in, and asks the
/// service to do the work. It holds no store, so it cannot write a status, a result reference or an
/// error code - the only way anything reaches the operation row is the worker's own conditional commit,
/// which this class never sees.
/// </para>
/// <para>
/// <b>Every refusal is the delivered classified kind.</b> The service raises the delivered permanent,
/// rate-limited and transient failures, so the attempt budget, the backoff schedule and the terminal
/// statuses are the delivered ones rather than a second policy written here.
/// </para>
/// <para>
/// <b>The scope is the operation's, never a request's.</b> A job runs long after the request that
/// created it is gone, so the identity it acts under comes from the operation row the worker read.
/// </para>
/// </remarks>
public sealed class GitHubCandidateSyncHandler(IGitHubCandidateService candidates) : IOperationHandler
{
    /// <inheritdoc />
    public string Kind => GitHubCandidateService.SyncOperationKind;

    /// <inheritdoc />
    public async Task<OperationResult> HandleAsync(
        OperationRef operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (!string.Equals(operation.Kind, Kind, StringComparison.Ordinal))
        {
            // A handler only runs for its own kind. Being handed another one is a wiring mistake, and
            // refusing it here is what stops this class proposing anything under a kind it does not own.
            throw new InvalidOperationException(
                $"This handler serves {Kind} and was handed {operation.Kind}.");
        }

        var owner = new OwnerScope(operation.ScopeId, Guid.Empty);
        var polled = await candidates
            .PollAsync(owner, operation.Id, cancellationToken)
            .ConfigureAwait(false);

        // The result reference is the number of candidates this poll proposed and the number that were
        // new, which is the one thing a reader of the operation row can act on. It carries no repository
        // name, no title and no payload text, and it is not a claim that anything was confirmed: a poll
        // that read forty releases and proposed nothing new still succeeded.
        return new OperationResult($"{Kind}:{polled.CandidateCount}/{polled.Recorded}");
    }
}
