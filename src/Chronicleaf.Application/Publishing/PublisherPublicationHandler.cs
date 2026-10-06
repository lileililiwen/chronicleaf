using Chronicleaf.Application.Operations;

namespace Chronicleaf.Application.Publishing;

/// <summary>
/// The job that sends one reviewed publication (PUBLISHER-CONTRACT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// This handler is almost empty on purpose, on exactly the same terms the delivered draft
/// handler is. It is handed an operation id, resolves the owner scope the operation was created
/// in, and asks the service to do the work. It holds no store, so it cannot write a status, a
/// result reference or an error code: the only way anything reaches the operation row is the
/// worker's own conditional commit, which this class never sees.
/// </para>
/// <para>
/// <b>Every refusal is a classified kind.</b> The service raises
/// <see cref="OperationPermanentException"/> for anything a second attempt cannot change - a
/// lapsed approval, a stale preview, a missing credential, a refused or an ambiguous write -
/// and the attempt budget, the backoff schedule and the terminal statuses are therefore the
/// delivered ones rather than a second policy written here. An ambiguous write in particular
/// is permanent, never transient: retrying a possibly-completed remote write would duplicate
/// a public post.
/// </para>
/// <para>
/// <b>The scope is the operation's, never a request's.</b> A job runs long after the request that
/// created it is gone, so the identity it acts under comes from the operation row the worker read.
/// The actor id is deliberately the empty one: a background publish is not an owner clicking a
/// button, and the delivered audit writer records the operation's own actor for it.
/// </para>
/// </remarks>
public sealed class PublisherPublicationHandler(IPublisherService publications) : IOperationHandler
{
    /// <inheritdoc />
    public string Kind => PublisherService.PublishOperationKind;

    /// <inheritdoc />
    public async Task<OperationResult> HandleAsync(
        OperationRef operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (!string.Equals(operation.Kind, Kind, StringComparison.Ordinal))
        {
            // A handler only runs for its own kind. Being handed another one is a wiring mistake, and
            // refusing it here is what stops this class from publishing anything under a kind it does not
            // own.
            throw new InvalidOperationException(
                $"This handler serves {Kind} and was handed {operation.Kind}.");
        }

        var owner = new Milestones.OwnerScope(operation.ScopeId, Guid.Empty);

        // The result reference names the attempt and its terminal state. It carries no post text,
        // no evidence and no credential: a reference is a bounded column on the operation row, and
        // approved content read through a queue payload would put reviewed text somewhere it does
        // not belong. A reader fetches the receipt through the read route, which is scoped.
        return await publications.RunAsync(owner, operation.Id, cancellationToken).ConfigureAwait(false);
    }
}
