namespace Chronicleaf.Application.Operations;

/// <summary>
/// Schedules the next attempt of an operation.
/// </summary>
/// <remarks>
/// This is the worker's own enqueue path, kept separate from the outbox dispatcher on purpose. The
/// outbox is how work is <em>requested</em> and is written once, in the creating transaction, so a
/// request cannot be lost. A retry is a consequence of an attempt that already happened, so it is
/// queued at the moment the attempt is recorded, against the same engine and the same payload shape -
/// an operation id and nothing else. Keeping them apart means the retry bound is enforced by the
/// claim rather than by the number of enqueues, so a replayed enqueue can never produce a fourth
/// attempt.
/// </remarks>
public interface IOperationDispatcher
{
    /// <summary>Queues a run no earlier than <paramref name="delay"/> from now.</summary>
    Task ScheduleAsync(Guid operationId, TimeSpan delay, CancellationToken cancellationToken = default);
}

/// <summary>
/// Keeps a claimed operation's lease alive while its handler runs.
/// </summary>
/// <remarks>
/// <para>
/// The heartbeat is what turns "this worker is still working on it" into stored state, on the
/// interval the policy fixes. Every beat is a conditional update, so a beat that matches no row is
/// information rather than an error: the lease expired, or another worker took the run over. The
/// handler's token is cancelled in that case, which is what makes a lost lease a cooperative stop
/// instead of two live attempts writing to one row.
/// </para>
/// <para>
/// It also polls the operation's own status for a cancellation. Cancellation is written by the
/// request that asked for it, so the row is already terminal before any worker notices; the
/// heartbeat's job is only to tell the running handler to stop, and the worker's commit is what
/// discards whatever the handler then produces.
/// </para>
/// <para><b>Who owns a cancellation source.</b> One rule, and it is the rule that keeps this class
/// from disposing a source another path is about to use: a source is owned by the thing whose
/// lifetime it expresses, and it is disposed only on the path that ends that lifetime. The source
/// this object hands out (<see cref="LeaseLost"/>) is created here and released by <see cref="Stop"/>
/// alone. The merged source the loop reads is created by <see cref="StartAsync"/> but outlives that
/// call - the loop is still running after the method has returned - so the <em>loop</em> disposes it,
/// on the way out. Disposing it there is also what unlinks it from both of its inputs, so a
/// cancellation arriving afterwards finds a loop that has already finished rather than a source that
/// has already been thrown away.</para>
/// </remarks>
public sealed class LeaseHeartbeat : IDisposable
{
    private readonly IOperationStore _store;
    private readonly TimeProvider _clock;
    private readonly Guid _operationId;
    private readonly Guid _leaseToken;

    /// <summary>
    /// The heartbeat's own source, and the only one it ever hands out. It is created here rather than
    /// in <see cref="StartAsync"/> so that <see cref="LeaseLost"/> can be read before any of the
    /// lifetime has started moving.
    /// </summary>
    private readonly CancellationTokenSource _stop = new();

    /// <summary>
    /// Captured once, because a source's own <c>Token</c> property is not readable after that source
    /// has been disposed, while a token copied out of it stays safe to read for as long as its holder
    /// lives. That is what lets the worker ask "was the lease lost?" on a path that has already called
    /// <see cref="Stop"/>.
    /// </summary>
    private readonly CancellationToken _leaseLost;

    /// <summary>
    /// Guards the hand-off of <see cref="_stop"/>, and the hand-off is guarded rather than lock-free
    /// on purpose. <see cref="Stop"/> cancels and then disposes, and the loop's lease-lost path
    /// cancels the same source, so a lock-free exchange would still leave a window in which
    /// <c>Cancel</c> ran on a source another path had already disposed - which is precisely the failure
    /// this class exists to prevent. Under the gate exactly one caller ever takes the source, and only
    /// that caller can dispose it.
    /// </summary>
    private readonly object _gate = new();

    private bool _started;
    private bool _stopped;

    public LeaseHeartbeat(
        IOperationStore store,
        Guid operationId,
        Guid leaseToken,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(clock);

        _store = store;
        _operationId = operationId;
        _leaseToken = leaseToken;
        _clock = clock;
        _leaseLost = _stop.Token;
    }

    /// <summary>
    /// The token a lost lease cancels, and the only one the worker is meant to link its handler
    /// under.
    /// </summary>
    /// <remarks>
    /// The heartbeat cannot cancel a token it was only handed, which is why this is a token the
    /// heartbeat <em>owns</em> and gives out rather than one the worker owns and passes in: the
    /// lease-lost signal has to travel towards the handler, and only the source on this side of the
    /// link can carry it that way.
    /// </remarks>
    public CancellationToken LeaseLost => _leaseLost;

    /// <summary>Starts beating until the token is cancelled or <see cref="Stop"/> is called.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_started)
            {
                throw new InvalidOperationException("This heartbeat has already been started.");
            }

            _started = true;

            if (_stopped)
            {
                // Stopped before it started is legal and beats nothing, rather than resurrecting a
                // source that has already been disposed.
                return Task.CompletedTask;
            }
        }

        // The merged source belongs to the loop, not to this method. This call returns while the loop
        // is still running, so nothing here may dispose anything the loop is reading; the loop
        // disposes the source in its own finally.
        var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);

        // The task is not passed the loop's own token: a source that is about to be cancelled by
        // `Stop` would otherwise be able to cancel the scheduling of the very loop that has to
        // observe that cancellation.
        _ = Task.Run(() => BeatAsync(linked), CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <summary>Stops beating. Safe to call more than once, and from more than one thread.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
        }

        // Cancelled before disposed, and outside the gate: cancelling runs the loop's registration
        // and the handler's, and that is code this class does not own.
        _stop.Cancel();
        _stop.Dispose();
    }

    /// <inheritdoc />
    public void Dispose() => Stop();

    /// <summary>
    /// The beating loop.
    /// </summary>
    /// <remarks>
    /// The loop cannot fault, which is what keeps its exceptions observed rather than abandoned. A
    /// cancellation is the ordinary exit; any other failure stops the loop and is caught here, and the
    /// only statement outside the <c>try</c> is a <c>Dispose</c> that does not throw - so the task
    /// always completes and there is never an unobserved exception left behind.
    /// </remarks>
    private async Task BeatAsync(CancellationTokenSource linked)
    {
        try
        {
            // The first beat waits out the interval, so a fast handler never pays for a write it does
            // not need; the lease was set to a full duration at claim time and covers the gap.
            while (await DelayAsync(linked.Token).ConfigureAwait(false))
            {
                var kept = await _store
                    .TryHeartbeatAsync(_operationId, _leaseToken, _clock.GetUtcNow(), linked.Token)
                    .ConfigureAwait(false);

                if (!kept)
                {
                    // The lease is gone, which is information rather than an error. Stopping is the
                    // whole point: the handler's result can no longer be committed, so there is
                    // nothing left for it to do, and the token it runs under is cancelled with it.
                    Stop();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal: the attempt finished, or the handler's deadline passed.
        }
        catch (Exception)
        {
            // The beat could not be written. That is not a lost lease - nothing has said the lease is
            // gone - so the handler is deliberately left alone, and the loop simply stops proving a
            // liveness it can no longer prove. The attempt's own deadline still bounds the handler,
            // and the lease this loop stopped renewing expires on its own, which is what the recovery
            // path already exists for. Swallowed deliberately: letting it escape would fault an
            // unobserved task and the loop owns no other reporting channel.
        }
        finally
        {
            linked.Dispose();
        }
    }

    private static async Task<bool> DelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(OperationPolicy.HeartbeatInterval, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
