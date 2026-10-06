using Chronicleaf.Application.Operations;

namespace Chronicleaf.Application.Schedules;

/// <summary>
/// The job that performs one scheduled candidate poll (SCHEDULED-PROJECT-SYNC-001).
/// </summary>
/// <remarks>
/// <para>
/// This handler is almost empty on purpose, for the same reason the delivered candidate poll handler
/// is: it is handed an operation id, resolves the scope the operation was created in, and asks the
/// service to do the work. It holds no store, so it cannot write a status, a result reference or an
/// error code - the only way anything reaches the operation row is the worker's own conditional
/// commit, which this class never sees.
/// </para>
/// <para>
/// <b>The work is the delivered poll.</b> A scheduled sync runs the candidate package's own poll
/// against the run row the tick wrote, so the attempt budget, the backoff schedule, the
/// rate-limit wait and the supersede check are the delivered ones rather than a second policy
/// written here. A scheduled sync that finds its input moved therefore ends superseded, exactly as
/// a manual poll does.
/// </para>
/// <para>
/// <b>The scope is the operation's, never a request's.</b> A job runs long after the tick that
/// created it is gone, so the identity it acts under comes from the operation row the worker read.
/// </para>
/// </remarks>
public sealed class ProjectSyncJobHandler(IProjectSyncScheduleService schedules) : IOperationHandler
{
    /// <inheritdoc />
    public string Kind => IProjectSyncScheduleService.ScheduledSyncOperationKind;

    /// <inheritdoc />
    public async Task<OperationResult> HandleAsync(
        OperationRef operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (!string.Equals(operation.Kind, Kind, StringComparison.Ordinal))
        {
            // A handler only runs for its own kind. Being handed another one is a wiring mistake, and
            // refusing it here is what stops this class polling anything under a kind it does not own.
            throw new InvalidOperationException(
                $"This handler serves {Kind} and was handed {operation.Kind}.");
        }

        var owner = new Milestones.OwnerScope(operation.ScopeId, Guid.Empty);
        var result = await schedules
            .RunScheduledSyncAsync(owner, operation.Id, cancellationToken)
            .ConfigureAwait(false);

        return new OperationResult(result);
    }
}
