using Chronicleaf.Application.Operations;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;

namespace Chronicleaf.Infrastructure.Operations;

/// <summary>
/// The Hangfire-backed <see cref="IJobDispatcher"/> (BACKGROUND-JOBS-001).
/// </summary>
/// <remarks>
/// <para>
/// The queue's storage is the <em>same</em> PostgreSQL database the rest of the application uses, in
/// its own <c>hangfire</c> schema. That is what makes the durability claim real rather than
/// decorative: a job is a row in the same database that holds the operation, so a process that dies
/// between the enqueue and the run has left the work somewhere the next process will find it, and
/// there is no second datastore to keep in step or to lose.
/// </para>
/// <para>
/// The enqueue is a write, not a call. A successful return means the job row exists; it does not mean
/// a worker has taken it, and it deliberately does not wait for one, because a request that blocks on
/// background work is a request that can time out while that work is still queued.
/// </para>
/// <para>
/// Only <see cref="JobPayload.ForOperation"/> builds a payload anywhere in this repository, and the
/// dispatcher reads nothing but the operation id out of it. A serialized job is therefore a job name
/// and a single GUID, so no credential and no evidence can travel in one - which is also why the
/// worker has to read the operation itself rather than being handed a prepared task.
/// </para>
/// </remarks>
public sealed class HangfireJobDispatcher(IBackgroundJobClient client) : IJobDispatcher
{
    public Task EnqueueAsync(JobPayload payload, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operationId = ReadOperationId(payload);

        client.Create<ExecuteOperationJob>(
            job => job.RunAsync(operationId, JobCancellationToken.Null),
            new EnqueuedState());

        return Task.CompletedTask;
    }

    public Task EnqueueAsync(
        JobPayload payload,
        TimeSpan delay,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var operationId = ReadOperationId(payload);

        if (delay <= TimeSpan.Zero)
        {
            client.Create<ExecuteOperationJob>(
                job => job.RunAsync(operationId, JobCancellationToken.Null),
                new EnqueuedState());

            return Task.CompletedTask;
        }

        // The engine owns the delay, so a process that stops during a backoff still runs the attempt
        // when it comes back. The attempt bound itself is enforced by the claim, not by this enqueue.
        client.Schedule<ExecuteOperationJob>(
            job => job.RunAsync(operationId, JobCancellationToken.Null),
            delay);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Reads the operation id out of a payload, refusing anything that is not one of ours. The
    /// dispatcher never enqueues a job whose payload it could not fully account for.
    /// </summary>
    private static Guid ReadOperationId(JobPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return payload.ReadOperationId();
    }
}

/// <summary>
/// The <see cref="IOperationDispatcher"/> the worker uses to queue a retry.
/// </summary>
/// <remarks>
/// It goes through the same <see cref="IJobDispatcher"/> and the same payload as the outbox, so there
/// is exactly one place a job is created and exactly one payload shape. A retry is an operation id and
/// a delay; it is not a second delivery mechanism with its own rules.
/// </remarks>
public sealed class HangfireOperationDispatcher(IJobDispatcher dispatcher) : IOperationDispatcher
{
    public Task ScheduleAsync(Guid operationId, TimeSpan delay, CancellationToken cancellationToken = default) =>
        dispatcher.EnqueueAsync(JobPayload.ForOperation(operationId), delay, cancellationToken);
}

/// <summary>
/// The dispatcher a deployment without a configured database gets.
/// </summary>
/// <remarks>
/// There is deliberately no fallback. A process with no database cannot run background work, and the
/// two honest answers to that are "readiness reports the missing configuration" and "nothing is
/// silently discarded". This type refuses the second: it throws with the reason rather than accepting
/// a job it has nowhere to put. It exists so the container stays valid and every other registration
/// can still be checked at start-up.
/// </remarks>
public sealed class UnavailableJobDispatcher : IJobDispatcher
{
    private const string Reason =
        "No database is configured, so background work cannot be queued. " +
        "Set the connection string and restart; the operation stays queued until then.";

    public Task EnqueueAsync(JobPayload payload, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(Reason);

    public Task EnqueueAsync(
        JobPayload payload,
        TimeSpan delay,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(Reason);
}
