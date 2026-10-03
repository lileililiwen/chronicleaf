using Chronicleaf.Application.Operations;
using Platform.Jobs;

namespace Chronicleaf.Infrastructure.Operations;

/// <summary>
/// The platform dispatcher a deployment without a configured database gets.
/// </summary>
/// <remarks>
/// There is deliberately no fallback. A process with no database cannot run background work, and the
/// two honest answers to that are "readiness reports the missing configuration" and "nothing is
/// silently discarded". This type refuses the second: it throws with the reason rather than accepting
/// a job it has nowhere to put. It exists so the container stays valid and every other registration
/// can still be checked at start-up.
/// </remarks>
public sealed class UnavailableJobDispatcher : IJobDispatcher
{
    private const string Reason =
        "No database is configured, so background work cannot be queued. " +
        "Set the connection string and restart; the operation stays queued until then.";

    /// <inheritdoc />
    public Task EnqueueAsync(JobPayload payload, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(Reason);
}

/// <summary>
/// The retry seam a deployment without a configured database gets.
/// </summary>
/// <remarks>
/// It refuses by asking the dispatcher to do something it cannot, so the reason an operator sees is
/// written down once and cannot drift between the two paths. The alternative - taking the engine's
/// client as a dependency - would leave the container unbuildable in a deployment that has no engine,
/// which is the state this build starts in before a database is configured.
/// </remarks>
public sealed class UnavailableOperationRetryScheduler(IJobDispatcher dispatcher) : IOperationRetryScheduler
{
    /// <inheritdoc />
    public Task ScheduleAsync(Guid operationId, TimeSpan delay, CancellationToken cancellationToken = default) =>
        dispatcher.EnqueueAsync(OperationJobPayload.ForOperation(operationId), cancellationToken);
}
