using Chronicleaf.Application.Outbox;

namespace Chronicleaf.Application.GitHubCandidates;

/// <summary>
/// Turns one accepted delivery into candidates (GITHUB-RELEASE-CANDIDATES-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the other half of the delivery route, and it is a separate class so the route can answer.</b>
/// The route authenticates over raw bytes, decides what the payload claims, records a receipt and
/// answers 202. The work - resolving the commit a created tag names, deduplicating each source key
/// against a unique index, and acknowledging an archived connection as ignored - happens here, on the
/// consumer, after the sender has gone.
/// </para>
/// <para>
/// <b>The envelope carries nothing private, and nothing it needs twice.</b> The aggregate id is this
/// package's own receipt row id rather than GitHub's delivery id, because the envelope's aggregate is a
/// GUID and GitHub's id is a string of no guaranteed shape. Addressing the receipt by a row id it
/// generated is what lets the connection, the delivery id and the scope all be read back from one place
/// instead of being smuggled through a queue envelope that has room for none of them.
/// </para>
/// <para>
/// <b>It is idempotent by construction, not by convention.</b> The receipt's own status is the guard and
/// the unique index on each source key is the second one, so an event handed over twice proposes
/// nothing the first pass already recorded.
/// </para>
/// </remarks>
public sealed class GitHubCandidateInboxHandler(IGitHubCandidateService candidates) : IOutboxEventHandler
{
    /// <inheritdoc />
    public string Kind => GitHubCandidateService.InboxOutboxKind;

    /// <inheritdoc />
    public Task HandleAsync(OutboxPayload payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (!string.Equals(payload.Kind, Kind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"This handler serves {Kind} and was handed {payload.Kind}.");
        }

        if (payload.AggregateId == Guid.Empty)
        {
            throw new InvalidOperationException($"An {Kind} event must name the receipt it is about.");
        }

        return candidates.ProcessInboxAsync(payload.ScopeId, payload.AggregateId, cancellationToken);
    }
}
