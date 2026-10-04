using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Operations;

namespace Chronicleaf.Application.Capture;

/// <summary>
/// The job that performs one screenshot capture (SCREENSHOT-CAPTURE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// This handler is almost empty on purpose, for the same reason the delivered github-evidence handler
/// is. It is handed an operation id, resolves the owner scope the operation was created in, and asks
/// the service to do the work. It holds no store, so it cannot write a status, a result reference or
/// an error code: the only way anything reaches the operation row is the worker's own conditional
/// commit, which this class never sees.
/// </para>
/// <para>
/// <b>Every refusal is the delivered classified kind.</b> The service raises
/// <see cref="OperationPermanentException"/> for something a second attempt cannot change - a missing
/// request row, an absent sandbox, a destination the policy refuses - and the delivered queue's
/// attempt budget, backoff and terminal statuses are therefore the delivered ones rather than a second
/// policy written here. A capture that is refused is not retried into success, which is what stops a
/// blocked destination from being dialled repeatedly.
/// </para>
/// <para>
/// <b>The scope is the operation's, never a request's.</b> A job runs long after the request that
/// created it is gone, so the identity it acts under comes from the operation row the worker read.
/// </para>
/// </remarks>
public sealed class ScreenshotCaptureHandler(IScreenshotCaptureService capture) : IOperationHandler
{
    /// <inheritdoc />
    public string Kind => ScreenshotCaptureService.CaptureOperationKind;

    /// <inheritdoc />
    public async Task<OperationResult> HandleAsync(
        OperationRef operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (!string.Equals(operation.Kind, Kind, StringComparison.Ordinal))
        {
            // A handler only runs for its own kind. Being handed another one is a wiring mistake, and
            // refusing it here is what stops this class from capturing anything under a kind it does not
            // own.
            throw new InvalidOperationException(
                $"This handler serves {Kind} and was handed {operation.Kind}.");
        }

        var owner = new OwnerScope(operation.ScopeId, Guid.Empty);
        var outcome = await capture.RunAsync(owner, operation.Id, cancellationToken).ConfigureAwait(false);

        // The result reference counts, it does not describe. It names no URL, no media id and no
        // evidence text - a reader who needs the pictures asks the delivered read route, which is
        // scoped. A run that produced nothing never reaches here: the service fails it instead, so a
        // result reference always means at least one candidate exists.
        return new OperationResult(
            $"{Kind}:{outcome.Requested}:{outcome.Captured}:{outcome.Failed.Count}");    }
}
