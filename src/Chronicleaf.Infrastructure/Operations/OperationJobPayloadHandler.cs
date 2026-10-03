using Chronicleaf.Application.Operations;
using Platform.Jobs;
using Platform.Jobs.Hangfire;

namespace Chronicleaf.Infrastructure.Operations;

/// <summary>
/// The application-owned <see cref="IJobPayloadHandler"/> the platform's executor routes every
/// dispatched payload to.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of the engine-to-application seam. The platform's <c>HangfireJobExecutor</c> is
/// the job the engine invokes, the platform's <c>ScopedJobActivator</c> opens a fresh DI scope for
/// each execution, and the executor resolves this handler from that scope; everything below it is the
/// same <see cref="OperationWorker"/> the two-worker, lease-recovery, cancellation and supersede
/// scenarios already run, unchanged.
/// </para>
/// <para>
/// A payload that cannot be read is a permanent failure and not a guess. There is no operation id, so
/// there is no row to record anything on, and the honest outcome is a job that fails loudly with a
/// redacted reason rather than a claim against a row nobody asked about. Nothing about the payload is
/// echoed into the message.
/// </para>
/// </remarks>
public sealed class OperationJobPayloadHandler(OperationWorker worker) : IJobPayloadHandler
{
    /// <inheritdoc />
    public Task HandleAsync(JobPayload payload, CancellationToken cancellationToken) =>
        worker.RunAsync(OperationJobPayload.ReadOperationId(payload), cancellationToken);
}
