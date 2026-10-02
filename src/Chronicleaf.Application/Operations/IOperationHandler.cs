using Chronicleaf.Core;

namespace Chronicleaf.Application.Operations;

/// <summary>
/// Everything a handler is told about the work, and nothing about how it is stored or executed.
/// </summary>
/// <remarks>
/// This is deliberately <em>not</em> the <see cref="Operation"/> record. A handler cannot write a
/// status, a result reference or an error code from here, because it holds no store and no store is
/// given to it: the only way a result reaches the database is the worker's own conditional commit,
/// which the handler never sees. That is the structural reason a provider cannot own operation state
/// even if it wants to.
/// <para>
/// The revision is here so a handler can decide what it is working on, and so it can notice that the
/// input it read has moved; it is not a licence to write to the target, which the target's own
/// package owns.
/// </para>
/// </remarks>
/// <param name="Id">The operation identity. The only thing a job payload ever carries.</param>
/// <param name="ScopeId">The owner scope the work runs under.</param>
/// <param name="Kind">The handler's own kind, echoed back for logging.</param>
/// <param name="TargetId">The record the work acts on.</param>
/// <param name="InputRevision">The target revision this operation was created against.</param>
/// <param name="Attempt">Which attempt this is, counting from one.</param>
/// <param name="IdempotencyKey">The key that made a repeated request return this same operation.</param>
public sealed record OperationRef(
    Guid Id,
    Guid ScopeId,
    string Kind,
    Guid TargetId,
    long InputRevision,
    int Attempt,
    string IdempotencyKey);

/// <summary>
/// What a successful run produced, as an opaque reference.
/// </summary>
/// <remarks>
/// The value is a reference, not a payload: it names something already stored, and it is bounded
/// because it becomes a database column. A handler that produced a large artifact writes that
/// artifact through its own package's contract and returns the reference to it, which is what keeps
/// a result out of the job queue.
/// </remarks>
/// <param name="ResultRef">The stored reference to the produced result.</param>
public sealed record OperationResult(string ResultRef);

/// <summary>
/// Handles one kind of durable operation.
/// </summary>
/// <remarks>
/// A handler is written by the package that owns the kind, not by this one. It must be idempotent
/// with respect to being run more than once - the queue may deliver the same operation twice - because
/// the operation's own claim is what guarantees only one <em>result</em> is committed, not that the
/// work is performed once.
/// <para>
/// It must honour <paramref name="cancellationToken"/>. Cancellation here is cooperative: the worker
/// passes a token that is cancelled when the owner asks to stop, when the lease is lost, or when the
/// attempt's timeout elapses, and a handler that ignores it can only delay the terminal state.
/// </para>
/// </remarks>
public interface IOperationHandler
{
    /// <summary>The kind this handler owns, matching <c>operations.kind</c> exactly.</summary>
    string Kind { get; }

    /// <summary>Performs the work and names what it produced.</summary>
    /// <param name="operation">What to work on. Carries no stored state the handler may change.</param>
    /// <param name="cancellationToken">Cancelled on owner request, lease loss or timeout.</param>
    Task<OperationResult> HandleAsync(OperationRef operation, CancellationToken cancellationToken = default);
}
