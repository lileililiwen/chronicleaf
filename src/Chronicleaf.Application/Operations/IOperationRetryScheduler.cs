namespace Chronicleaf.Application.Operations;

/// <summary>
/// Schedules the next attempt of an operation, no earlier than a given instant.
/// </summary>
/// <remarks>
/// <para>
/// This is the worker's own scheduling seam, kept separate from the outbox dispatcher on purpose. The
/// outbox is how work is <em>requested</em> and is written once, in the creating transaction, so a
/// request cannot be lost. A retry is a consequence of an attempt that already happened, so it is
/// queued at the moment the attempt is recorded. Keeping them apart means the retry bound is enforced
/// by the claim rather than by the number of enqueues, so a replayed enqueue can never produce a
/// fourth attempt.
/// </para>
/// <para>
/// <b>Why this seam exists at all, when the enqueue path is the platform's.</b> Enqueueing a payload
/// is <c>Platform.Jobs.IJobDispatcher</c>'s job, and it is the only way anything is enqueued: the
/// payload is the platform's <c>JobPayload</c> and the dispatcher is the platform's
/// <c>HangfireJobDispatcher</c>, both reached from the infrastructure project. What that contract
/// cannot express is <em>when</em> a run may start: <c>EnqueueAsync(JobPayload, CancellationToken)</c>
/// has no delay, and Chronicleaf's bounded backoff is a real, engine-owned wait rather than a
/// timestamp the worker checks for itself. This type therefore carries the one thing the platform
/// contract has no room for - the wait - and it carries nothing else: no payload, no job name, no
/// engine type, and no credential. The implementation on the other side is still the platform's
/// dispatcher plus the engine's own scheduled state, so there is one payload shape, one job entry
/// point and one place a job row is created.
/// </para>
/// <para>
/// The wait is already stored before it is requested. The worker records the due time on the operation
/// row first, so the recorded schedule is the truth even if this enqueue is lost, and the engine's
/// delay is a second, independent copy of the same number rather than the only one.
/// </para>
/// </remarks>
public interface IOperationRetryScheduler
{
    /// <summary>Queues a run no earlier than <paramref name="delay"/> from now.</summary>
    /// <param name="operationId">The operation to run. The engine receives this and nothing else.</param>
    /// <param name="delay">How long the engine must wait before the attempt may run.</param>
    /// <param name="cancellationToken">Cancels the enqueue.</param>
    Task ScheduleAsync(Guid operationId, TimeSpan delay, CancellationToken cancellationToken = default);
}
