namespace Chronicleaf.Application.Outbox;

/// <summary>
/// The explicit result of handing one outbox event to its consumer. A version this build does
/// not know is reported as <see cref="UnsupportedEvent"/> and is never executed.
/// </summary>
public enum OutboxDispatchOutcome
{
    /// <summary>The consumer handled the event.</summary>
    Handled,

    /// <summary>The stored payload carries a schema version this build does not implement.</summary>
    UnsupportedEvent,

    /// <summary>No consumer is registered for the event kind.</summary>
    NoHandler,

    /// <summary>The consumer failed; the event stays pending for redelivery.</summary>
    Failed,
}

/// <summary>One dispatched outbox event together with the reason it was or was not executed.</summary>
/// <param name="EventId">Identity of the stored outbox row.</param>
/// <param name="Kind">The outbox kind column.</param>
/// <param name="Revision">The aggregate revision carried by the event.</param>
/// <param name="Outcome">What the dispatcher did with the event.</param>
/// <param name="SchemaVersion">The schema version found in the payload, when it could be read.</param>
public readonly record struct OutboxDispatchResult(
    Guid EventId,
    string Kind,
    long Revision,
    OutboxDispatchOutcome Outcome,
    int? SchemaVersion = null);

/// <summary>Handles exactly one outbox kind. Implementations live in the package that owns the kind.</summary>
public interface IOutboxEventHandler
{
    string Kind { get; }

    Task HandleAsync(OutboxPayload payload, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads pending outbox events and hands each to the consumer registered for its kind.
/// </summary>
public interface IOutboxDispatcher
{
    Task<IReadOnlyList<OutboxDispatchResult>> DispatchPendingAsync(
        int limit,
        CancellationToken cancellationToken = default);
}
