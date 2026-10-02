using Chronicleaf.Application.Operations;
using Hangfire;
using Hangfire.Common;
using Hangfire.Server;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Chronicleaf.Infrastructure.Operations;

/// <summary>
/// The job the queue runs (BACKGROUND-JOBS-001).
/// </summary>
/// <remarks>
/// <para>
/// The whole of a job's payload is the operation id in the method's arguments. The worker is resolved
/// from a scope this job opens itself, so the operation store's <c>DbContext</c> is scoped to exactly
/// one attempt and is disposed with it - two workers are two scopes and two contexts, which is what
/// makes the two-worker boundary a real concurrency rather than two calls on one context.
/// </para>
/// <para>
/// The job returns whatever the worker returns and does not translate an outcome into a queue state.
/// That is deliberate: the operation's own row is the authority on what happened, and a queue that
/// recorded "success" for work that was superseded or cancelled would be a second, disagreeing
/// source of truth. The worker swallows the failures it has already recorded, so a failed attempt does
/// not also become an engine-level exception.
/// </para>
/// </remarks>
public sealed class ExecuteOperationJob(IServiceScopeFactory scopeFactory)
{
    /// <summary>Runs one delivery of one operation.</summary>
    /// <param name="operationId">
    /// The operation to run. The worker reads everything else - scope, kind, target, revision,
    /// attempt count - from the row itself, so none of it has to survive in a queue payload.
    /// </param>
    /// <param name="cancellationToken">
    /// The engine's cancellation token, so that stopping the storage can interrupt a running attempt.
    /// Owner-requested cancellation does not travel this way: it is written to the operation row, which
    /// is what makes it survive a restart and what the worker's commit refuses.
    /// </param>
    public async Task RunAsync(Guid operationId, IJobCancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cancellationToken);

        using var scope = scopeFactory.CreateScope();

        var worker = scope.ServiceProvider.GetRequiredService<OperationWorker>();
        await worker.RunAsync(operationId, cancellationToken.ShutdownToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Resolves jobs from the application's container instead of from a parameterless constructor.
/// </summary>
/// <remarks>
/// Hangfire's own activator can only build a type whose constructor it can call with no arguments,
/// which is no use for a job that needs a scope. This activator resolves the job from the root
/// provider, and the job opens the scope it actually works in. The list of types resolved this way is
/// short and explicit - a job type is a code path into the application, not a place for a container to
/// choose what runs.
/// </remarks>
public sealed class ChronicleafJobActivator(IServiceProvider services) : JobActivator
{
    public override object ActivateJob(Type jobType)
    {
        ArgumentNullException.ThrowIfNull(jobType);
        return services.GetRequiredService(jobType);
    }

    public override JobActivatorScope BeginScope(JobActivatorContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new ContainerScope(services);
    }

    /// <summary>
    /// Hands the container's services to the engine for the length of one job's execution. The scope
    /// the job itself opens is a child of nothing here: the job owns it and disposes it, so a job
    /// that throws still releases its context.
    /// </summary>
    private sealed class ContainerScope(IServiceProvider services) : JobActivatorScope
    {
        public override object Resolve(Type type)
        {
            ArgumentNullException.ThrowIfNull(type);
            return services.GetRequiredService(type);
        }
    }
}

/// <summary>
/// Runs the queue's workers for the lifetime of the application.
/// </summary>
/// <remarks>
/// <para>
/// The server is constructed here rather than through a package extension so that this project
/// depends on the engine and its PostgreSQL storage only. The usual hosting extension lives in the
/// ASP.NET Core integration, and taking that dependency purely to start a worker would have pulled in
/// the very dashboard integration this build refuses to serve.
/// </para>
/// <para>
/// The job activator is supplied through the options object, which is the engine's supported
/// composition point, so the custom activator costs no deprecated constructor.
/// </para>
/// </remarks>
public sealed class OperationQueueServer(
    JobStorage storage,
    JobActivator activator,
    OperationQueueOptions options) : IHostedService, IDisposable
{
    private readonly BackgroundJobServer _server = new(
        new BackgroundJobServerOptions
        {
            WorkerCount = Math.Max(1, options.WorkerCount),
            SchedulePollingInterval = options.SchedulePollingInterval,
            HeartbeatInterval = options.HeartbeatInterval,
            ShutdownTimeout = TimeSpan.FromSeconds(30),
            ServerName = options.ServerName,
            Activator = activator,
        },
        storage);

    /// <summary>
    /// Starts the workers. The engine's own <c>Start</c> is a documented no-op stub - constructing
    /// the server begins processing - so this exists only to satisfy the hosted-service contract and
    /// to return a completed task.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Stops the workers and waits, briefly, for the in-flight attempts to finish, so a process that
    /// is going away does not abandon a claimed operation mid-attempt.
    /// </summary>
    /// <remarks>
    /// The wait is bounded and deliberately swallows a timeout. A stop that hung would keep a
    /// deployment from ever shutting down cleanly; an attempt that did not finish in time is not lost
    /// either, because the lease it holds expires and the next process recovers the operation - once,
    /// exactly as the policy allows.
    /// </remarks>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _server.SendStop();

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(ShutdownGracePeriod);

        try
        {
            await _server.WaitForShutdownAsync(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The grace period elapsed. The lease recovery path is what resolves the abandoned work.
        }
    }

    private static readonly TimeSpan ShutdownGracePeriod = TimeSpan.FromSeconds(20);

    public void Dispose() => _server.Dispose();
}
