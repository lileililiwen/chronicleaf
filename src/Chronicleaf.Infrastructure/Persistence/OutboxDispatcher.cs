using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;

namespace Chronicleaf.Infrastructure.Persistence;

/// <summary>
/// Hands each pending outbox event to the consumer registered for its kind.
/// </summary>
/// <remarks>
/// A stored payload whose <c>schemaVersion</c> this build does not implement is reported as
/// <see cref="OutboxDispatchOutcome.UnsupportedEvent"/> and is not handed to any consumer: an
/// unknown envelope is never guessed at, and the row stays pending and undispatched so a build
/// that does understand it can still process it.
/// </remarks>
public sealed class OutboxDispatcher(
    IOutboxStore store,
    IEnumerable<IOutboxEventHandler> handlers) : IOutboxDispatcher
{
    public async Task<IReadOnlyList<OutboxDispatchResult>> DispatchPendingAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        var pending = await store.ListPendingAsync(limit, cancellationToken).ConfigureAwait(false);
        var results = new List<OutboxDispatchResult>(pending.Count);

        foreach (var message in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!OutboxPayload.TryParse(message.PayloadJson, out var payload, out var schemaVersion))
            {
                results.Add(new OutboxDispatchResult(
                    message.Id,
                    message.Kind,
                    message.Revision,
                    OutboxDispatchOutcome.UnsupportedEvent,
                    schemaVersion));
                continue;
            }

            var handler = handlers.FirstOrDefault(candidate =>
                string.Equals(candidate.Kind, message.Kind, StringComparison.Ordinal));

            if (handler is null)
            {
                results.Add(new OutboxDispatchResult(
                    message.Id,
                    message.Kind,
                    message.Revision,
                    OutboxDispatchOutcome.NoHandler,
                    schemaVersion));
                continue;
            }

            try
            {
                await handler.HandleAsync(payload!, cancellationToken).ConfigureAwait(false);
                results.Add(new OutboxDispatchResult(
                    message.Id,
                    message.Kind,
                    message.Revision,
                    OutboxDispatchOutcome.Handled,
                    schemaVersion));
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A failed consumer leaves the row pending for redelivery; it is never reported
                // as success and never marked dispatched.
                results.Add(new OutboxDispatchResult(
                    message.Id,
                    message.Kind,
                    message.Revision,
                    OutboxDispatchOutcome.Failed,
                    schemaVersion));
            }
        }

        return results;
    }
}
