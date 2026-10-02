using Chronicleaf.Application.Common;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;

namespace Chronicleaf.Application.Operations;

/// <summary>
/// The operation use cases over real PostgreSQL stores (BACKGROUND-JOBS-001, BACKGROUND-JOBS-002).
/// </summary>
/// <remarks>
/// <para><b>Creation.</b> The operation row and its outbox row are written in one transaction. That is
/// the only place either is created, so an operation cannot exist without the intent to run it and a
/// queued intent cannot exist without an operation to run. The outbox row is what makes the
/// enqueue survive a process that dies immediately afterwards: the dispatcher replays whatever is
/// still marked undispatched, which is why a replay may enqueue the same operation twice and why the
/// worker's claim - not the enqueue - is what guarantees one result.</para>
///
/// <para><b>Idempotency.</b> The unique key is (scope, kind, idempotency key), and it is decided by
/// the database in a single insert that either takes the key or changes no row. A repeat that binds
/// the same work returns the original operation and reports <c>Created == false</c>; a repeat that
/// tries to bind the same key to different work is a conflict and creates nothing. Neither path is a
/// read-then-write, so two simultaneous identical requests cannot both create.</para>
///
/// <para><b>The payload fingerprint.</b> "Identical payload" has to mean something the database can
/// compare, so a request is reduced to a fingerprint over its target and its input revision before the
/// key is taken. Two requests agree if and only if they would produce the same operation, which is
/// the property a caller actually means by retrying with the same key.</para>
///
/// <para><b>Cancellation.</b> A cancel writes the terminal <c>cancelled</c> status itself rather than
/// asking a worker to do it later. That is deliberate: the status is what the worker's result commit
/// is conditional on, so making it terminal from the request side is what guarantees a result that
/// arrives afterwards is discarded instead of attached. The worker observes the same row and stops
/// cooperatively; the request never waits for it.</para>
///
/// <para>Nothing here reaches outside the process: no network call, no clock other than the injected
/// <see cref="TimeProvider"/>, and no scope or actor taken from a request body.</para>
/// </remarks>
public sealed class OperationService(
    IOperationStore store,
    IOperationQuery query,
    IUnitOfWork unitOfWork,
    TimeProvider clock) : IOperationService
{
    /// <summary>The outbox kind the dispatcher watches for.</summary>
    public const string OperationQueuedKind = "operation.queued";

    /// <summary>The audit action recorded when an operation is created.</summary>
    public const string OperationCreatedAction = "operation.created";

    /// <summary>The audit action recorded when an owner asks for an operation to stop.</summary>
    public const string OperationCancelledAction = "operation.cancelled";

    private const long MinimumInputRevision = 1;
    private const int MaxKindLength = 80;
    private const int MaxIdempotencyKeyLength = 200;

    public async Task<EnqueueOperationResult> EnqueueAsync(
        EnqueueOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);

        var now = clock.GetUtcNow();
        var fingerprint = Fingerprint(request);

        // The insert takes the unique key or changes no row; the read only runs when it did not.
        return await unitOfWork.ExecuteAsync(
            async token =>
            {
                var operationId = Guid.NewGuid();
                var created = await store.TryCreateAsync(
                    operationId,
                    request.ScopeId,
                    request.Kind.Trim(),
                    request.TargetId,
                    request.InputRevision,
                    request.IdempotencyKey.Trim(),
                    fingerprint,
                    now,
                    token).ConfigureAwait(false);

                if (!created)
                {
                    // The key is already bound. An identical request gets the original operation back
                    // and no second row; a different one is a conflict and still creates nothing.
                    var existing = await store.FindByIdempotencyKeyAsync(
                        request.ScopeId,
                        request.Kind.Trim(),
                        request.IdempotencyKey.Trim(),
                        token).ConfigureAwait(false)
                        ?? throw new InvalidOperationException(
                            "The idempotency key was not taken, but no binding for it can be read.");

                    if (!string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                    {
                        throw new OperationIdempotencyConflictException(
                            request.IdempotencyKey.Trim(),
                            existing.Fingerprint);
                    }

                    return new EnqueueOperationResult(existing.Operation, Created: false);
                }

                // The outbox row carries the operation id and nothing else: no target content, no
                // credential, no evidence. A dispatcher reading it learns which operation to run and
                // has to go to PostgreSQL for everything else.
                var outboxId = await store.TryAppendOutboxAsync(
                    operationId,
                    OperationQueuedKind,
                    request.ScopeId,
                    now,
                    token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        "The operation row was created but its outbox row could not be appended.");

                _ = outboxId;

                return new EnqueueOperationResult(
                    await query.FindAsync(request.ScopeId, operationId, token).ConfigureAwait(false)
                        ?? throw new InvalidOperationException("The created operation cannot be read back."),
                    Created: true);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<Operation> GetAsync(
        Guid scopeId,
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        await query.FindAsync(scopeId, operationId, cancellationToken).ConfigureAwait(false)
        ?? throw new OperationNotFoundException(operationId);

    public Task<IReadOnlyList<Operation>> ListAsync(
        Guid scopeId,
        int? limit = null,
        CancellationToken cancellationToken = default) =>
        query.ListAsync(scopeId, ListQueryPolicy.Clamp(limit), cancellationToken);

    public async Task<Operation> CancelAsync(
        Guid scopeId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();

        var cancelled = await unitOfWork.ExecuteAsync(
            async token =>
            {
                if (!await store.TryMarkCancelledAsync(operationId, now, token).ConfigureAwait(false))
                {
                    return false;
                }

                return await query.FindAsync(scopeId, operationId, token).ConfigureAwait(false) is not null;
            },
            cancellationToken).ConfigureAwait(false);

        if (cancelled)
        {
            return await GetAsync(scopeId, operationId, cancellationToken).ConfigureAwait(false);
        }

        // Nothing changed. Either the operation is not in this scope, or it has already finished -
        // two different answers, so the stored row decides which one the caller gets.
        var latest = await query.FindAsync(scopeId, operationId, cancellationToken).ConfigureAwait(false);

        return latest is null
            ? throw new OperationNotFoundException(operationId)
            : throw new OperationNotCancellableException(latest.Status);
    }

    private static void Validate(EnqueueOperationRequest request)
    {
        if (request.ScopeId == Guid.Empty)
        {
            throw new ArgumentException("An operation needs an owner scope.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Kind))
        {
            throw new ArgumentException("An operation needs a kind.", nameof(request));
        }

        if (request.Kind.Trim().Length > MaxKindLength)
        {
            throw new ArgumentException(
                $"An operation kind may be at most {MaxKindLength} characters.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            throw new ArgumentException("An operation needs an idempotency key.", nameof(request));
        }

        if (request.IdempotencyKey.Trim().Length > MaxIdempotencyKeyLength)
        {
            throw new ArgumentException(
                $"An idempotency key may be at most {MaxIdempotencyKeyLength} characters.", nameof(request));
        }

        if (request.InputRevision < MinimumInputRevision)
        {
            throw new ArgumentException(
                $"An input revision must be {MinimumInputRevision} or greater.", nameof(request));
        }
    }

    /// <summary>
    /// The value that decides whether two requests under one key are the same request. Only the work
    /// identity takes part - the scope and kind are already in the unique key - so a repeat that
    /// targets the same record at the same revision is a replay, and one that targets a different
    /// record or a different revision is a conflict.
    /// </summary>
    private static string Fingerprint(EnqueueOperationRequest request) =>
        $"{request.TargetId:N}|{request.InputRevision}";
}
