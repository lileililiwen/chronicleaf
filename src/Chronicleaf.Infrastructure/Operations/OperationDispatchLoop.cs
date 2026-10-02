using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Chronicleaf.Infrastructure.Operations;

/// <summary>How often the outbox is drained, and how much of it is read each pass.</summary>
public sealed class OperationDispatchOptions
{
    /// <summary>The configuration section these options bind from, shared with the queue options.</summary>
    public const string SectionName = OperationQueueOptions.SectionName;

    /// <summary>The delay between passes. Short enough to be responsive, long enough not to spin.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How many pending events one pass reads. Bounded by the shared list policy.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>
    /// Whether the loop runs at all. It defaults to true and is only switched off by a test that
    /// wants to drive a pass by hand; a production deployment needs it, because the loop is what turns
    /// a committed operation row into a queued job.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Drains the delivered outbox into the scheduling engine (BACKGROUND-JOBS-001).
/// </summary>
/// <remarks>
/// <para>
/// The loop exists because the outbox row and the job row cannot be written in one transaction: they
/// live in the same database but in different schemas and different tables, written by different
/// components. So the loop is the seam, and its job is to make the gap small and safe in a specific
/// way - dispatch, then stamp. A crash in the gap costs a duplicate enqueue and nothing else, because
/// the worker's claim, not the enqueue, is what guarantees one result.
/// </para>
/// <para>
/// It is a background service rather than a middleware so that a request never waits on it and a
/// request failing cannot stop it. Failures are logged and the next pass retries: a dispatch error
/// leaves the row pending, which is the only correct outcome for work that has not been queued.
/// </para>
/// <para>
/// The loop is a singleton but the work it does is scoped, so it opens a scope per pass. Sharing one
/// long-lived <c>DbContext</c> across every pass for the life of the process would be both a
/// correctness problem - a context is not thread-safe and two passes can overlap - and a leak.
/// </para>
/// </remarks>
public sealed class OperationDispatchLoop(
    IServiceScopeFactory scopeFactory,
    IOptions<OperationDispatchOptions> options,
    TimeProvider clock,
    ILogger<OperationDispatchLoop> logger) : BackgroundService
{
    private readonly OperationDispatchOptions _options = options.Value;

    /// <summary>
    /// Runs one pass by hand. The tests use it to place a dispatch on a chosen side of a simulated
    /// restart, which is the only way to reproduce the gap between "queued" and "stamped dispatched"
    /// without a real crash.
    /// </summary>
    public async Task<IReadOnlyList<OutboxDispatchResult>> RunOnceAsync(
        CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();

        var dispatcher = scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>();
        var stamper = scope.ServiceProvider.GetRequiredService<IOutboxDispatchStamper>();

        var results = await dispatcher.DispatchPendingAsync(_options.BatchSize, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();

        foreach (var result in results)
        {
            if (result.Outcome != OutboxDispatchOutcome.Handled)
            {
                // Not handled is not stamped. The row stays pending so a later pass - or a later
                // process - tries again, which is the whole point of writing the intent durably.
                continue;
            }

            try
            {
                await stamper.TryMarkDispatchedAsync(result.EventId, now, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // The job is queued but the stamp is not written. The next pass enqueues the same
                // operation again and the worker's claim absorbs it; that is the designed outcome, not
                // a failure to retry indefinitely.
                logger.LogWarning(
                    exception,
                    "An outbox event was handled but not stamped dispatched; it will be replayed.");
            }
        }

        return results;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // The database may be unreachable at this moment. The rows are durable and the next
                // pass will read them, so a failure here is logged and retried rather than fatal.
                logger.LogWarning(exception, "An outbox dispatch pass failed and will be retried.");
            }

            try
            {
                await Task.Delay(_options.Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
