using Chronicleaf.Core;

namespace Chronicleaf.Application.Operations;

/// <summary>What a caller asks for when it submits an operation.</summary>
/// <param name="ScopeId">The owner scope the operation belongs to.</param>
/// <param name="Kind">The handler kind.</param>
/// <param name="TargetId">The record the work acts on.</param>
/// <param name="InputRevision">The target revision this work is bound to.</param>
/// <param name="IdempotencyKey">The caller's key, unique within (scope, kind).</param>
public sealed record EnqueueOperationRequest(
    Guid ScopeId,
    string Kind,
    Guid TargetId,
    long InputRevision,
    string IdempotencyKey);

/// <summary>The outcome of an enqueue.</summary>
/// <param name="Operation">The operation that exists - the new one, or the original of a replay.</param>
/// <param name="Created">False when an identical request had already created this operation.</param>
public sealed record EnqueueOperationResult(Operation Operation, bool Created);

/// <summary>
/// The operation use cases the API layer and later packages call.
/// </summary>
public interface IOperationService
{
    /// <summary>
    /// Creates the operation and its outbox row in one transaction.
    /// </summary>
    /// <remarks>
    /// A repeated (scope, kind, idempotency key) whose payload matches returns the original operation
    /// and <see cref="EnqueueOperationResult.Created"/> is false. A repeated key with a different
    /// payload is a conflict and never creates a second row.
    /// </remarks>
    /// <exception cref="OperationIdempotencyConflictException">
    /// The key exists for this scope and kind, bound to different work.
    /// </exception>
    /// <exception cref="ArgumentException">The kind or key is empty, or the revision is not positive.</exception>
    Task<EnqueueOperationResult> EnqueueAsync(
        EnqueueOperationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one operation inside a scope.</summary>
    /// <exception cref="OperationNotFoundException">No such operation in this scope.</exception>
    Task<Operation> GetAsync(Guid scopeId, Guid operationId, CancellationToken cancellationToken = default);

    /// <summary>Lists a scope's operations, newest first, bounded by the shared list policy.</summary>
    Task<IReadOnlyList<Operation>> ListAsync(
        Guid scopeId,
        int? limit = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks for an operation to stop. The status becomes terminal <c>cancelled</c> immediately, which
    /// is what discards any result the worker is about to commit.
    /// </summary>
    /// <exception cref="OperationNotFoundException">No such operation in this scope.</exception>
    /// <exception cref="OperationNotCancellableException">The operation has already reached a terminal status.</exception>
    Task<Operation> CancelAsync(Guid scopeId, Guid operationId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The narrow write surface a worker uses. Every method is a compare-and-set, and the return value
/// reports whether this caller won it - which is the single mechanism behind every exactly-once
/// guarantee in this package.
/// </summary>
public interface IOperationStore
{
    /// <summary>
    /// Inserts one operation and takes the (scope, kind, idempotency key) uniqueness in the same
    /// statement, so a simultaneous identical request cannot both create.
    /// </summary>
    /// <returns>False when the key is already bound; nothing is written in that case.</returns>
    Task<bool> TryCreateAsync(
        Guid operationId,
        Guid scopeId,
        string kind,
        Guid targetId,
        long inputRevision,
        string idempotencyKey,
        string fingerprint,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends the outbox row that asks for the operation to be dispatched. It is written inside the
    /// creating transaction, so an operation never exists without the intent to run it.
    /// </summary>
    /// <returns>The outbox row's id, or <c>null</c> when a row for this revision is already there.</returns>
    Task<Guid?> TryAppendOutboxAsync(
        Guid operationId,
        string kind,
        Guid scopeId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the operation a key is already bound to, with the fingerprint it was bound to.
    /// </summary>
    /// <returns><c>null</c> when the key is unbound or bound in another scope or kind.</returns>
    Task<IdempotencyBinding?> FindByIdempotencyKeyAsync(
        Guid scopeId,
        string kind,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims the operation for a run, and only from a status that permits a claim.
    /// </summary>
    /// <remarks>
    /// A claim succeeds from <c>queued</c>, or from <c>running</c> when the lease has expired and has
    /// not yet been recovered. The expired case increments the recovery counter, and the counter's
    /// bound is what makes recovery happen exactly once: a second worker attempting the same
    /// takeover matches no row, because the first already moved the counter past it.
    /// </remarks>
    /// <returns>The claimed operation, or <c>null</c> when this caller did not win the claim.</returns>
    Task<Operation?> TryClaimAsync(
        Guid operationId,
        Guid leaseToken,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Extends the lease of a run this worker still owns.
    /// </summary>
    /// <returns>False when the lease is gone - it expired, or another worker took the run over.</returns>
    Task<bool> TryHeartbeatAsync(
        Guid operationId,
        Guid leaseToken,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits the result and the success in one statement, and only if the input revision the worker
    /// read is still the one the operation is bound to.
    /// </summary>
    /// <returns>
    /// The terminal status the operation now holds, or <c>null</c> when this statement matched no row -
    /// because the run was cancelled, superseded or taken over, in which case the result is discarded.
    /// </returns>
    Task<OperationStatus?> TryCommitResultAsync(
        Guid operationId,
        Guid leaseToken,
        string resultRef,
        long observedInputRevision,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a terminal failure. A permanent failure is written on the first attempt; a transient
    /// one only after the attempt budget is spent.
    /// </summary>
    Task<bool> TryMarkFailedAsync(
        Guid operationId,
        Guid leaseToken,
        string errorCode,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Hands the operation back to <c>queued</c> for another attempt at <paramref name="nextAttemptAt"/>,
    /// recording the schedule so the backoff is observable state and not only a queue detail.
    /// </summary>
    Task<bool> TryScheduleRetryAsync(
        Guid operationId,
        Guid leaseToken,
        DateTimeOffset nextAttemptAt,
        string errorCode,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves an operation to terminal <c>cancelled</c> from a status that may still be cancelled.
    /// </summary>
    /// <returns>False when the operation is not in this scope, or is already terminal.</returns>
    Task<bool> TryMarkCancelledAsync(
        Guid operationId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the current input revision of a target, for the supersede check at commit.</summary>
    /// <returns>The current revision, or <c>null</c> when the target is not in this scope.</returns>
    Task<long?> ReadTargetRevisionAsync(
        Guid scopeId,
        string kind,
        Guid targetId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// An existing operation together with the payload its idempotency key was bound to.
/// </summary>
/// <param name="Operation">The operation the key created.</param>
/// <param name="Fingerprint">The request the key is bound to; a different fingerprint is a conflict.</param>
public sealed record IdempotencyBinding(Operation Operation, string Fingerprint);
