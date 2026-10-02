using Chronicleaf.Application.Operations;
using Chronicleaf.Application.Outbox;

namespace Chronicleaf.Infrastructure.Operations;

/// <summary>
/// Turns one pending <c>operation.queued</c> outbox row into a queued job.
/// </summary>
/// <remarks>
/// <para>
/// This is the bridge between the delivered outbox and the scheduling engine, and it is the reason
/// the enqueue path is safe. The outbox row is written in the same transaction as the operation, so a
/// request that commits is a request that will be dispatched even if the process dies on the very next
/// line. The dispatcher may then replay a row that was already enqueued - the window between "the job
/// row exists" and "the outbox row is stamped dispatched" is exactly the window a crash leaves open -
/// so the same operation can be enqueued twice.
/// </para>
/// <para>
/// That is accepted, not defended against, because the queue's job is not to run the work once. Its
/// job is to make the work available; the worker's claim is what runs it once, and the claim is
/// decided by a conditional statement. A duplicate enqueue costs one no-op claim and nothing else.
/// </para>
/// <para>
/// The payload is read for one field. An event with no operation id, or with a schema version this
/// build does not implement, is refused rather than executed.
/// </para>
/// </remarks>
public sealed class OperationOutboxHandler(IJobDispatcher dispatcher) : IOutboxEventHandler
{
    public string Kind => OperationService.OperationQueuedKind;

    public Task HandleAsync(OutboxPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var operationId = payload.OperationId
            ?? throw new InvalidOperationException($"An {Kind} event must carry an operation id.");

        return dispatcher.EnqueueAsync(JobPayload.ForOperation(operationId), cancellationToken);
    }
}
