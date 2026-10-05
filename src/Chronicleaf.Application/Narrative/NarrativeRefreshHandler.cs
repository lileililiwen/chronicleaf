using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Operations;

namespace Chronicleaf.Application.Narrative;

/// <summary>
/// Runs one narrative refresh from the durable operation queue (NARRATIVE-MEMORY-002).
/// </summary>
/// <remarks>
/// The refresh parameters are read back from the request row the enqueue wrote, not from the job
/// payload: the queue carries the operation id and the row carries the cutoff and the consented
/// private-entry selection, which is what lets the run survive a restart between request and work.
/// A job whose row is gone cannot be refreshed honestly and fails permanently rather than
/// guessing a cutoff.
/// </remarks>
public sealed class NarrativeRefreshHandler(INarrativeService narrative) : IOperationHandler
{
    /// <inheritdoc />
    public string Kind => NarrativeService.RefreshOperationKind;

    /// <inheritdoc />
    public async Task<OperationResult> HandleAsync(
        OperationRef operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (!string.Equals(operation.Kind, Kind, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"This handler serves {Kind} and was handed {operation.Kind}.");
        }

        var owner = new OwnerScope(operation.ScopeId, Guid.Empty);

        var outcome = await narrative.RunRefreshAsync(owner, operation.Id, cancellationToken).ConfigureAwait(false);

        // The result reference names the summary and the revision that were committed. It carries
        // no history prose and no entry text: a reference is a bounded column on the operation row,
        // and a summary's body read through a queue payload would put history somewhere it does not
        // belong. A reader fetches the summary through the read route, which is scoped.
        return new OperationResult(
            $"{Kind}:{outcome.SummaryId:D}:revision={outcome.Revision}"
            + $":regenerated={(outcome.Regenerated ? "true" : "false")}"
            + $":stale={(outcome.Stale ? "true" : "false")}");
    }
}
