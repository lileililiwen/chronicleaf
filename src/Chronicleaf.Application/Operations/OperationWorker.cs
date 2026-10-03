using Chronicleaf.Core;

namespace Chronicleaf.Application.Operations;

/// <summary>
/// Runs one attempt of one operation, and owns every decision that makes an attempt count.
/// </summary>
/// <remarks>
/// <para><b>The claim is the only entry.</b> An attempt begins by winning
/// <see cref="IOperationStore.TryClaimAsync"/>, and a worker that loses the claim returns without
/// touching anything. That single compare-and-set is what the two-worker boundary scenario turns on:
/// both workers may be handed the same operation, exactly one of them runs the handler, and the
/// other's later write is not merely discouraged but unable to match a row.</para>
///
/// <para><b>The result commit is one statement.</b> Success, the result reference and the check that
/// the input revision has not moved are a single conditional update. A process that dies between
/// "the handler returned" and "the result was written" has committed no result and therefore cannot
/// claim success; a stop and restart replays the attempt, the claim succeeds again from
/// <c>running</c> only if the lease has expired and only once, and only one of those runs can commit.</para>
///
/// <para><b>Late results cannot commit.</b> The commit is conditional on the operation still being
/// <c>running</c> under this worker's lease token. A cancellation writes terminal <c>cancelled</c>
/// from the request side and a supersede writes terminal <c>superseded</c>, so in both cases the
/// commit matches no row, the status it would have written is not the one that stands, and the result
/// is discarded rather than attached. There is no path in this class that writes a result reference
/// outside that statement.</para>
///
/// <para><b>Nothing here is provider-shaped.</b> The worker knows timeouts, leases, retries and the
/// supersede rule; it does not know what any handler does, and it is given no HTTP client, no
/// connection string and no payload other than the operation id. A provider cannot become the owner
/// of operation state because it is never handed a way to write it.</para>
/// </remarks>
public sealed class OperationWorker(
    IOperationStore store,
    IOperationQuery query,
    IOperationHandlerRegistry handlers,
    IOperationRetryScheduler retryScheduler,
    TimeProvider clock)
{
    /// <summary>What one attempt did, for the caller's log and for the tests' assertions.</summary>
    public enum AttemptOutcome
    {
        /// <summary>This worker did not win the claim, so it performed no work.</summary>
        NotClaimed,

        /// <summary>The result was committed; the operation is <c>succeeded</c>.</summary>
        Succeeded,

        /// <summary>The input moved before the commit; the operation is <c>superseded</c>.</summary>
        Superseded,

        /// <summary>The owner asked to stop; the operation is <c>cancelled</c>.</summary>
        Cancelled,

        /// <summary>No handler owns this kind, so nothing could be run.</summary>
        NoHandler,

        /// <summary>The attempt failed permanently; the operation is <c>failed</c>.</summary>
        Failed,

        /// <summary>The attempt failed transiently and another one is scheduled.</summary>
        Retrying,

        /// <summary>The attempt budget is spent; the operation is <c>failed</c>.</summary>
        RetriesExhausted,
    }

    /// <summary>The result of running one delivery.</summary>
    /// <param name="Outcome">What happened.</param>
    /// <param name="Operation">The operation as it stands afterwards, when it was readable.</param>
    /// <param name="ErrorCode">The redacted code, when the attempt failed.</param>
    public sealed record AttemptResult(AttemptOutcome Outcome, Operation? Operation, string? ErrorCode);

    /// <summary>Runs one delivery of <paramref name="operationId"/>.</summary>
    public async Task<AttemptResult> RunAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        var leaseToken = Guid.NewGuid();

        var claimed = await store
            .TryClaimAsync(operationId, leaseToken, clock.GetUtcNow(), cancellationToken)
            .ConfigureAwait(false);

        // Not winning the claim is the normal outcome for a duplicate delivery, and it is a no-op by
        // construction: nothing below this point runs, so nothing below can write.
        if (claimed is null)
        {
            return new AttemptResult(AttemptOutcome.NotClaimed, null, null);
        }

        var handler = handlers.Find(claimed.Kind);
        if (handler is null)
        {
            // Nothing can be run, and nothing will change by trying again, so this fails at once.
            await store.TryMarkFailedAsync(
                    operationId, leaseToken, OperationErrorCodes.NoHandler, clock.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);

            return await SettledAsync(claimed, AttemptOutcome.NoHandler, OperationErrorCodes.NoHandler, cancellationToken)
                .ConfigureAwait(false);
        }

        var attempt = new OperationRef(
            claimed.Id,
            claimed.ScopeId,
            claimed.Kind,
            claimed.TargetId,
            claimed.InputRevision,
            claimed.Attempt,
            claimed.IdempotencyKey);

        var timeout = OperationPolicy.TimeoutFor(claimed.Kind);

        // The heartbeat is built first because the attempt's deadline is linked to its lease-lost
        // signal. A lease that has been taken away has to stop the handler, not only stop the beats:
        // the handler's token hangs off a source the heartbeat owns, because a source the heartbeat
        // was only handed cannot be cancelled by it.
        using var heartbeat = new LeaseHeartbeat(store, operationId, leaseToken, clock);
        var leaseLost = heartbeat.LeaseLost;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, leaseLost);
        deadline.CancelAfter(timeout);

        OperationResult result;
        try
        {
            await heartbeat.StartAsync(deadline.Token).ConfigureAwait(false);
            result = await handler.HandleAsync(attempt, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationFailedException failure)
        {
            heartbeat.Stop();
            return await RecordFailureAsync(claimed, leaseToken, failure, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (leaseLost.IsCancellationRequested)
        {
            // The lease was lost: it expired, or another worker took the run over. There is nothing
            // left for this attempt to do and nothing for it to record - every statement below is
            // conditional on the lease token this worker no longer holds, so all of them would match
            // no row. The row itself is the answer, and it says who has the run now.
            return new AttemptResult(AttemptOutcome.NotClaimed, await ReloadAsync(claimed, cancellationToken), null);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            // The attempt ran out of time, or the owner asked it to stop. Both are the same kind of
            // event from here, and neither produces a result.
            heartbeat.Stop();
            return await RecordFailureAsync(
                claimed,
                leaseToken,
                new OperationTransientException(OperationErrorCodes.Timeout),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // An unclassified failure is still bounded: the exception is deliberately not stored, and
            // the attempt budget applies to it exactly as it does to a classified one.
            heartbeat.Stop();
            return await RecordFailureAsync(
                claimed,
                leaseToken,
                new OperationTransientException(OperationErrorCodes.Unhandled),
                cancellationToken).ConfigureAwait(false);
        }

        heartbeat.Stop();
        return await CommitAsync(claimed, leaseToken, result, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Commits the result, or decides that it must be discarded.
    /// </summary>
    /// <remarks>
    /// The target's revision is read first and handed to the commit, which re-checks it in its WHERE
    /// clause. Reading it separately would leave a window in which an edit could land between the read
    /// and the write; making the statement itself refuse a moved revision closes that window, so a
    /// stale result cannot attach even if the two statements interleave with a concurrent edit.
    /// </remarks>
    private async Task<AttemptResult> CommitAsync(
        Operation claimed,
        Guid leaseToken,
        OperationResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);

        var observed = await store
            .ReadTargetRevisionAsync(claimed.ScopeId, claimed.Kind, claimed.TargetId, cancellationToken)
            .ConfigureAwait(false);

        var committed = await store.TryCommitResultAsync(
                claimed.Id,
                leaseToken,
                result.ResultRef,
                observed ?? claimed.InputRevision,
                clock.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);

        if (committed is { } status)
        {
            return await SettledAsync(claimed, OutcomeFor(status), null, cancellationToken).ConfigureAwait(false);
        }

        // The commit matched no row, so this worker did not commit a result. The only two cases worth
        // reporting as something other than "I did not commit" are the two an external decision
        // produced while this attempt was running: a cancellation and a supersede. Everything else -
        // including a row another worker has already moved to `succeeded` - means this attempt lost,
        // and saying so is the only attribution that is true.
        var latest = await ReloadAsync(claimed, cancellationToken).ConfigureAwait(false);

        var outcome = latest?.Status switch
        {
            OperationStatus.Cancelled => AttemptOutcome.Cancelled,
            OperationStatus.Superseded => AttemptOutcome.Superseded,
            OperationStatus.Failed => AttemptOutcome.Failed,
            _ => AttemptOutcome.NotClaimed,
        };

        return new AttemptResult(outcome, latest, latest?.ErrorCode);
    }

    private async Task<AttemptResult> RecordFailureAsync(
        Operation claimed,
        Guid leaseToken,
        OperationFailedException failure,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        // A permanent failure gets no second attempt whatever the attempt count says, because
        // repeating a refused request is the one thing that provably cannot change the answer.
        if (!failure.IsTransient)
        {
            await store.TryMarkFailedAsync(claimed.Id, leaseToken, failure.ErrorCode, now, cancellationToken)
                .ConfigureAwait(false);

            return await SettledAsync(claimed, AttemptOutcome.Failed, failure.ErrorCode, cancellationToken)
                .ConfigureAwait(false);
        }

        // The claim already counted this attempt, so the budget is spent when the count has reached
        // the maximum. That check is on the claimed row, not on a local tally, so a replayed delivery
        // cannot talk the operation into a fourth attempt.
        if (claimed.Attempt >= OperationPolicy.MaxAttempts)
        {
            await store.TryMarkFailedAsync(claimed.Id, leaseToken, failure.ErrorCode, now, cancellationToken)
                .ConfigureAwait(false);

            return await SettledAsync(claimed, AttemptOutcome.RetriesExhausted, failure.ErrorCode, cancellationToken)
                .ConfigureAwait(false);
        }

        var completedAttempts = Math.Max(1, claimed.Attempt);
        var delay = OperationPolicy.DelayFor(completedAttempts, failure.RetryAfter);
        var nextAttemptAt = now.Add(delay);

        var scheduled = await store
            .TryScheduleRetryAsync(
                claimed.Id, leaseToken, nextAttemptAt, failure.ErrorCode, now, cancellationToken)
            .ConfigureAwait(false);

        if (!scheduled)
        {
            // The conditional write changed no row, so this worker scheduled nothing and has no claim
            // to make about what happens next. Reporting "retrying" would credit this attempt with a
            // schedule a recovery, a cancellation or a terminal status wrote instead. The stored row
            // is the only honest answer, and it says this attempt did not schedule anything.
            var latest = await ReloadAsync(claimed, cancellationToken).ConfigureAwait(false);

            return new AttemptResult(
                latest?.Status switch
                {
                    OperationStatus.Cancelled => AttemptOutcome.Cancelled,
                    OperationStatus.Superseded => AttemptOutcome.Superseded,
                    OperationStatus.Failed => AttemptOutcome.Failed,
                    _ => AttemptOutcome.NotClaimed,
                },
                latest,
                failure.ErrorCode);
        }

        // The schedule is recorded before the job is queued, so the recorded due time is the truth
        // even if the enqueue is lost, and the claim - not the enqueue - is what bounds the count.
        await retryScheduler.ScheduleAsync(claimed.Id, delay, cancellationToken).ConfigureAwait(false);

        return new AttemptResult(AttemptOutcome.Retrying, await ReloadAsync(claimed, cancellationToken), failure.ErrorCode);
    }

    private async Task<AttemptResult> SettledAsync(
        Operation claimed,
        AttemptOutcome outcome,
        string? errorCode,
        CancellationToken cancellationToken) =>
        new(outcome, await ReloadAsync(claimed, cancellationToken).ConfigureAwait(false), errorCode);

    private Task<Operation?> ReloadAsync(Operation claimed, CancellationToken cancellationToken) =>
        query.FindAsync(claimed.ScopeId, claimed.Id, cancellationToken);

    private static AttemptOutcome OutcomeFor(OperationStatus status) => status switch
    {
        OperationStatus.Succeeded => AttemptOutcome.Succeeded,
        OperationStatus.Superseded => AttemptOutcome.Superseded,
        OperationStatus.Cancelled => AttemptOutcome.Cancelled,
        OperationStatus.Failed => AttemptOutcome.Failed,
        OperationStatus.Queued => AttemptOutcome.Retrying,
        _ => AttemptOutcome.NotClaimed,
    };
}
