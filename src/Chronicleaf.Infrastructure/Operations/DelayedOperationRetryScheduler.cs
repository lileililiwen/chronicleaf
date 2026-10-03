using System.Reflection;
using System.Text.Json;
using Chronicleaf.Application.Operations;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Platform.Jobs;
using Platform.Jobs.Hangfire;

namespace Chronicleaf.Infrastructure.Operations;

/// <summary>
/// Runs the bounded retry backoff through the engine's own scheduled state, on top of the platform's
/// dispatcher and executor.
/// </summary>
/// <remarks>
/// <para>
/// The enqueue contract is the platform's: the payload is a <c>JobPayload</c>, the job the engine
/// invokes is the platform's <see cref="HangfireJobExecutor"/>, and a run with no delay is handed to
/// the platform's own <see cref="IJobDispatcher"/>. This type exists for the one case that contract
/// cannot express - a run that may not start until a backoff has elapsed - and it does the smallest
/// thing that can express it: the same payload, serialized the platform's way, in a job aimed at the
/// platform's entry point, in the engine's <see cref="ScheduledState"/>.
/// </para>
/// <para>
/// The engine owns the wait, so a process that stops during a backoff still runs the attempt when it
/// comes back. The attempt bound itself is not here and never was: it is enforced by the claim, which
/// is why a replayed enqueue cannot produce a fourth attempt.
/// </para>
/// <para>
/// The platform exposes the executor and its JSON options, but not the factory that builds its job
/// (<c>HangfireJobExecutor.CreateExecuteJob</c> is internal to the platform assembly). The three lines
/// below therefore aim a <see cref="Job"/> at the platform's own public entry point, which is the
/// documented way an application schedules work for an executor it does not own.
/// </para>
/// </remarks>
public sealed class DelayedOperationRetryScheduler(
    IBackgroundJobClient client,
    IJobDispatcher dispatcher) : IOperationRetryScheduler
{
    private static readonly MethodInfo ExecuteMethod = typeof(HangfireJobExecutor)
        .GetMethod(
            nameof(HangfireJobExecutor.Execute),
            BindingFlags.Public | BindingFlags.Instance,
            [typeof(string), typeof(IJobCancellationToken)])
        ?? throw new InvalidOperationException("The platform job executor's entry point could not be resolved.");

    /// <inheritdoc />
    public Task ScheduleAsync(Guid operationId, TimeSpan delay, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (delay <= TimeSpan.Zero)
        {
            // Nothing to wait for, so this is an ordinary enqueue and the platform's dispatcher owns it.
            return dispatcher.EnqueueAsync(OperationJobPayload.ForOperation(operationId), cancellationToken);
        }

        var payloadJson = JsonSerializer.Serialize(
            OperationJobPayload.ForOperation(operationId),
            HangfireJobExecutor.PayloadJsonOptions);

        client.Create(
            new Job(typeof(HangfireJobExecutor), ExecuteMethod, new object?[] { payloadJson, null }),
            new ScheduledState(delay));

        return Task.CompletedTask;
    }
}
