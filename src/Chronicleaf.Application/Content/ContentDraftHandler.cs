using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Operations;

namespace Chronicleaf.Application.Content;

/// <summary>
/// The job that drafts one grounded Markdown story (GROUNDED-MARKDOWN-DRAFTS-001/002).
/// </summary>
/// <remarks>
/// <para>
/// This handler is almost empty on purpose, on exactly the same terms the delivered GitHub and capture
/// handlers are. It is handed an operation id, resolves the owner scope the operation was created in,
/// and asks the service to do the work. It holds no store, so it cannot write a status, a result
/// reference or an error code: the only way anything reaches the operation row is the worker's own
/// conditional commit, which this class never sees.
/// </para>
/// <para>
/// <b>Every refusal is a classified kind.</b> The service raises
/// <see cref="OperationPermanentException"/> for anything a second attempt cannot change - a grounding
/// refusal, an unsupported reference, a body that dropped a limitation, a consent that went stale - and
/// <see cref="OperationTransientException"/> or <see cref="OperationRateLimitedException"/> for a
/// dependency that may come back. The attempt budget, the backoff schedule and the terminal statuses
/// are therefore the delivered ones rather than a second policy written here.
/// </para>
/// <para>
/// <b>The scope is the operation's, never a request's.</b> A job runs long after the request that
/// created it is gone, so the identity it acts under comes from the operation row the worker read. The
/// actor id is deliberately the empty one: a background generation is not an owner clicking a button,
/// and the delivered audit writer records the operation's own actor for it.
/// </para>
/// </remarks>
public sealed class ContentDraftHandler(IContentDraftService drafts) : IOperationHandler
{
    /// <inheritdoc />
    public string Kind => ContentDraftService.DraftOperationKind;

    /// <inheritdoc />
    public async Task<OperationResult> HandleAsync(
        OperationRef operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (!string.Equals(operation.Kind, Kind, StringComparison.Ordinal))
        {
            // A handler only runs for its own kind. Being handed another one is a wiring mistake, and
            // refusing it here is what stops this class from drafting anything under a kind it does not
            // own.
            throw new InvalidOperationException(
                $"This handler serves {Kind} and was handed {operation.Kind}.");
        }

        var owner = new OwnerScope(operation.ScopeId, Guid.Empty);
        var outcome = await drafts.RunAsync(owner, operation.Id, cancellationToken).ConfigureAwait(false);

        // The result reference names the asset and the revision that were committed, and the two
        // provenance revisions the text is bound to. It carries no draft prose, no evidence and no
        // claim text: a reference is a bounded column on the operation row, and a draft's body read
        // through a queue payload would put unapproved text somewhere it does not belong. A reader
        // fetches the asset through the read route, which is scoped and which re-derives the findings.
        return new OperationResult(
            $"{Kind}:{outcome.AssetId:D}:{outcome.AssetRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + $":evidence={outcome.EvidenceRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + $":voice={outcome.VoiceRevision.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + $":citations={outcome.CitationCount.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            + $":repairCalls={outcome.RepairCalls.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
    }
}
