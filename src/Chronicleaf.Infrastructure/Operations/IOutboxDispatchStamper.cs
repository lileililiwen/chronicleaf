using Chronicleaf.Application.Outbox;

namespace Chronicleaf.Infrastructure.Operations;

/// <summary>
/// Stamps an outbox row dispatched once its handler has run.
/// </summary>
/// <remarks>
/// <para>
/// The delivered dispatcher reports what it did with each event but does not write the
/// <c>dispatched_at</c> stamp, so the operation loop does it. The stamp is written only for an event
/// the consumer actually handled: an event with no handler, or one whose handler threw, is left
/// pending so a later pass picks it up.
/// </para>
/// <para>
/// That is what makes the crash window real rather than theoretical. If the process stops after the
/// job row is enqueued but before this stamp, the row stays pending and the next process enqueues the
/// same operation again - and the worker's claim is what makes the second enqueue harmless.
/// </para>
/// </remarks>
public interface IOutboxDispatchStamper
{
    /// <summary>Stamps one event dispatched, if it is still undispatched.</summary>
    /// <returns>False when another process stamped it first, or the row is gone.</returns>
    Task<bool> TryMarkDispatchedAsync(Guid eventId, DateTimeOffset now, CancellationToken cancellationToken = default);
}
