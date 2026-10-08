using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Operations;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Operations;
using Chronicleaf.Infrastructure.Persistence;
using Hangfire;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// BACKGROUND-JOBS-001 and BACKGROUND-JOBS-002 against a real disposable PostgreSQL started from a
/// pinned Docker image, with a real Hangfire server draining a real <c>hangfire</c> schema in that
/// same database.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here is simulated. Operations, leases, attempt counts, error codes, result references,
/// outbox rows and queue jobs are all produced by a real PostgreSQL server, and the queue is a real
/// Hangfire server holding real connections to it. There is no EF in-memory provider, no in-memory
/// job store, no fake queue and no stubbed store: an unavailable container is a failure, never a
/// passing result. The handler that does the work is a test handler, which is the point - no provider
/// is contacted from this file at all, and no external write is attempted.
/// </para>
/// <para>
/// The clock is injected, so every stored instant, lease deadline and backoff due time is
/// deterministic and the schedule is asserted from stored rows rather than from elapsed wall time.
/// The automated dispatch loop is switched off so a test can place the dispatch on the side of the
/// restart it needs to reproduce; the loop's own body is then run by hand, through the production
/// dispatcher and the production handler.
/// </para>
/// <para>
/// Every assertion is about something a client could observe - an operation row, a status, an attempt
/// count, an error code, a response body - rather than about a private field, so the scenarios stay
/// meaningful if the implementation changes shape.
/// </para>
/// </remarks>
[Collection(ChronicleafPostgresCollection.Name)]
public sealed class BackgroundJobsTests(ChronicleafPostgresFixture postgres)
{
    private static readonly DateTimeOffset Clock = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The installation, and therefore the only scope, these scenarios run in.</summary>
    private static readonly Guid InstallationId = new("6e1c8b3a-2f47-4d5e-9a10-2b3c4d5e6f71");

    /// <summary>A second scope, used to prove an operation outside the caller's scope is not found.</summary>
    private static readonly Guid ForeignScopeId = new("7f2d9c4b-3058-4e6f-8b21-3c4d5e6f7082");

    // =============================================================================================
    // BACKGROUND-JOBS-001 Success: a committed operation survives a process that stopped between the
    // enqueue and its acknowledgement, and the restart leaves exactly one committed result.
    // =============================================================================================

    [Fact]
    public async Task A_restart_between_enqueue_and_acknowledgement_leaves_exactly_one_committed_result()
    {
        Assert.True(postgres.IsRealPostgres, "the background job scenarios require a real PostgreSQL container");

        var database = await ProvisionedDatabaseAsync("restart");
        var clock = new AdjustableTimeProvider(Clock);
        var script = new OperationScript();

        // The first process: an operation is committed and the process is thrown away before the
        // outbox row is ever dispatched. That is the state a crash in that gap leaves behind.
        Guid operationId;
        await using (var first = BackgroundJobsApiHost.Start(database, clock, script))
        {
            using var owner = await OwnerClient.CreateAsync(first);
            using var created = await Enqueue(owner, "test.success", Guid.NewGuid(), 1, "restart-1");
            operationId = await ReadIdAsync(created);
        }

        var afterStop = await OperationRows.ReadAsync(database, operationId);
        Assert.NotNull(afterStop);
        Assert.Equal("queued", afterStop.Status);
        Assert.Null(afterStop.ResultRef);

        var undispatched = await OperationRows.CountForOperationAsync(
            database,
            operationId,
            "SELECT count(*) FROM outbox WHERE aggregate_id = @id AND dispatched_at IS NULL");
        Assert.Equal(1, undispatched);

        // The restart. The same database, a second real process, the real Hangfire server.
        await using var restarted = BackgroundJobsApiHost.Start(database, clock, script);

        // A pass that enqueues and then does not stamp the acknowledgement: the crash window,
        // reproduced on purpose, which leaves the row pending and therefore replayable.
        var interrupted = await restarted.DispatchAsync(stampDispatched: false);
        Assert.Contains(interrupted, result => result.Outcome == OutboxDispatchOutcome.Handled);
        Assert.Equal(
            1,
            await OperationRows.CountForOperationAsync(
                database,
                operationId,
                "SELECT count(*) FROM outbox WHERE aggregate_id = @id AND dispatched_at IS NULL"));

        // The replay, from the restarted process, which enqueues the same operation a second time and
        // this time writes the acknowledgement.
        var replayed = await restarted.DispatchAsync();
        Assert.Contains(replayed, result => result.Outcome == OutboxDispatchOutcome.Handled);
        Assert.Equal(
            0,
            await OperationRows.CountForOperationAsync(
                database,
                operationId,
                "SELECT count(*) FROM outbox WHERE aggregate_id = @id AND dispatched_at IS NULL"));

        // Both deliveries are handed to the real queue, and the real queue runs them. The proof that
        // the engine - not this test - did the running is the handler count: the only path to a handler
        // call is the real Hangfire server invoking the real job against this database.
        var operation = await OperationRows.AwaitAsync(database, operationId, "succeeded");
        Assert.Equal(1, script.SuccessHandlerCalls);

        // And the claim is the single point of entry, so two deliveries still produce one attempt and
        // one result.
        Assert.Equal(1, operation.Attempt);
        Assert.Equal("test-result:success", operation.ResultRef);

        // One operation, one committed result - a success is never a row without its reference.
        Assert.Equal(
            1,
            await OperationRows.CountForOperationAsync(
                database,
                operationId,
                "SELECT count(*) FROM operations WHERE id = @id AND status = 'succeeded' AND result_ref IS NOT NULL"));

        // The queue's stored payload is the operation id and nothing else. This is the property that
        // keeps a credential out of a job: whatever the engine serialized, there is nothing in it but
        // a GUID and a token marker.
        var arguments = await ChronicleafPostgresFixture.ReadTextAsync(
            database,
            "SELECT string_agg(arguments::text, ' ') FROM hangfire.job");
        Assert.NotNull(arguments);
        Assert.Contains(operationId.ToString(), arguments, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(OwnerClient.AccessToken, arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", arguments, StringComparison.OrdinalIgnoreCase);
    }

    // =============================================================================================
    // BACKGROUND-JOBS-001 Failure: a permanently failing handler records `failed` with a redacted
    // code after exactly one attempt.
    // =============================================================================================

    [Fact]
    public async Task A_permanent_failure_is_recorded_after_one_attempt_with_a_redacted_code()
    {
        var database = await ProvisionedDatabaseAsync("permanent");
        var clock = new AdjustableTimeProvider(Clock);
        var script = new OperationScript();

        await using var host = BackgroundJobsApiHost.Start(database, clock, script);
        using var owner = await OwnerClient.CreateAsync(host);

        using var created = await Enqueue(owner, "test.permanent", Guid.NewGuid(), 1, "permanent-1");
        var operationId = await ReadIdAsync(created);

        await host.DispatchAsync();

        var operation = await OperationRows.AwaitAsync(database, operationId, "failed");

        // One attempt, and one handler call. A refusal is not retried, and the row says so rather
        // than the fact having to be inferred from an empty queue.
        Assert.Equal(1, operation.Attempt);
        Assert.Equal(1, script.PermanentHandlerCalls);

        // The stored code is one of this build's own fixed words, and the handler's own text is
        // nowhere on the row. That is the redaction, asserted rather than assumed.
        Assert.Equal(OperationErrorCodes.Permanent, operation.ErrorCode);
        Assert.Contains(operation.ErrorCode, OperationErrorCodes.All, StringComparer.Ordinal);

        // Nothing was rescheduled: a permanent failure does not become a delayed job.
        Assert.Equal(0, await ScheduledJobsAsync(database, operationId));
        Assert.Equal(1, await QueuedJobsAsync(database, operationId));

        // The same answer over the wire, so the owner sees the code and the single attempt.
        using var read = await owner.GetAsync($"/api/operations/{operationId}");
        read.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.Equal("failed", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("attempt").GetInt32());
        Assert.Equal(3, document.RootElement.GetProperty("maxAttempts").GetInt32());
        Assert.Equal(
            OperationErrorCodes.Permanent,
            document.RootElement.GetProperty("errorCode").GetString());
    }

    // =============================================================================================
    // BACKGROUND-JOBS-001 Boundary: two workers receive the same operation and only one commits.
    // =============================================================================================

    [Fact]
    public async Task Two_workers_handed_the_same_operation_commit_exactly_one_result()
    {
        var database = await ProvisionedDatabaseAsync("two_workers");
        var clock = new AdjustableTimeProvider(Clock);
        var script = new OperationScript();

        await using var hostA = BackgroundJobsApiHost.Start(database, clock, script);
        await using var hostB = BackgroundJobsApiHost.Start(database, clock, script);

        Guid operationId;
        using (var owner = await OwnerClient.CreateAsync(hostA))
        {
            using var created = await Enqueue(owner, "test.slow", Guid.NewGuid(), 1, "two-workers");
            operationId = await ReadIdAsync(created);
        }

        // Two processes, two scopes, two contexts, told to run the same operation at the same moment.
        using var workerA = hostA.OpenWorker();
        using var workerB = hostB.OpenWorker();

        var first = workerA.Worker.RunAsync(operationId);
        var second = workerB.Worker.RunAsync(operationId);

        await script.WaitForSlowHandlerAsync();
        script.ReleaseSlowHandler();

        var results = await Task.WhenAll(first, second);

        // Exactly one worker won the claim. The other wrote nothing at all, which is what the
        // conditional claim means rather than what a lock or a check-then-act would mean.
        Assert.Equal(1, results.Count(result => result.Outcome == OperationWorker.AttemptOutcome.Succeeded));
        Assert.Equal(1, results.Count(result => result.Outcome == OperationWorker.AttemptOutcome.NotClaimed));

        var operation = await OperationRows.AwaitAsync(database, operationId, "succeeded");
        Assert.Equal(1, operation.Attempt);
        Assert.Equal("test-result:late", operation.ResultRef);
        Assert.Equal(1, script.SlowHandlerCalls);

        // One committed result across both workers, read back as a count rather than assumed.
        Assert.Equal(
            1,
            await OperationRows.CountForOperationAsync(
                database,
                operationId,
                "SELECT count(*) FROM operations WHERE id = @id AND result_ref IS NOT NULL"));
    }

    // =============================================================================================
    // BACKGROUND-JOBS-002 Success: an explicitly transient failure obeys the three-attempt bound and
    // the fixed backoff, through the real queue's own scheduler.
    // =============================================================================================

    [Fact]
    public async Task An_explicitly_transient_failure_reaches_exactly_three_attempts_on_the_fixed_backoff()
    {
        var database = await ProvisionedDatabaseAsync("retry_bound");
        var clock = new AdjustableTimeProvider(Clock);
        var script = new OperationScript();

        await using var host = BackgroundJobsApiHost.Start(database, clock, script);
        using var owner = await OwnerClient.CreateAsync(host);

        using var created = await Enqueue(owner, "test.transient", Guid.NewGuid(), 1, "retry-bound");
        var operationId = await ReadIdAsync(created);

        // The retries are queued by the real engine and run by the real workers, on the engine's own
        // real-time schedule. Nothing here moves a clock past a backoff, so the elapsed time below is
        // the delays actually having been waited out.
        var started = DateTimeOffset.UtcNow;
        await host.DispatchAsync();

        var operation = await OperationRows.AwaitAsync(
            database, operationId, "failed", within: TimeSpan.FromMinutes(3));
        var elapsed = DateTimeOffset.UtcNow - started;

        // The bound, stated as the policy states it and confirmed by the row and by the handler.
        Assert.Equal(3, OperationPolicy.MaxAttempts);
        Assert.Equal(3, operation.Attempt);
        Assert.Equal(3, script.TransientHandlerCalls);
        Assert.Equal(OperationErrorCodes.Transient, operation.ErrorCode);

        // The backoff schedule, as implemented.
        Assert.Equal(TimeSpan.FromSeconds(5), OperationPolicy.FirstBackoff);
        Assert.Equal(TimeSpan.FromSeconds(30), OperationPolicy.SecondBackoff);
        Assert.Equal(TimeSpan.FromSeconds(300), OperationPolicy.MaxRetryAfter);

        // The engine really was asked for two delayed retries, and never a fourth attempt.
        Assert.Equal(2, await ScheduledJobsAsync(database, operationId));
        Assert.Equal(3, await QueuedJobsAsync(database, operationId));

        // And the delays were genuinely waited: 5s before the second attempt and 30s before the third
        // is 35s of real time, which this run cannot have taken by accident. A bound of three attempts
        // reached instantly would mean the backoff was not applied at all.
        // The floor allows two seconds for the time between the stopwatch starting and the first
        // attempt being claimed; the wait the policy fixes is 35s, and a run that took less than 33s
        // cannot have waited it.
        var floor = OperationPolicy.FirstBackoff + OperationPolicy.SecondBackoff - TimeSpan.FromSeconds(2);

        Assert.True(
            elapsed >= floor,
            $"three attempts took {elapsed.TotalSeconds:0.#}s, which is less than the {floor.TotalSeconds:0}s floor the policy's 35s of backoff implies");

        // The exact numbers are asserted where they can be exact, against the operation's own record
        // with the clock injected: the same policy, the same store, no polling to race.
        await RecordedBackoffsAreExactlyFiveAndThirtySecondsAsync(host, clock, script);
    }

    /// <summary>
    /// Drives three attempts directly and reads the wait each one recorded, so the two backoffs are
    /// compared with the policy's own values rather than with a stopwatch.
    /// </summary>
    /// <remarks>
    /// The clock is injected and frozen, so <c>next_attempt_at - updated_at</c> <em>is</em> the backoff
    /// the worker computed. This is the same worker, the same store and the same conditional
    /// statements the queue invokes; only the trigger is a direct call instead of a queued job, which
    /// is what makes the comparison exact. The end-to-end run above is what proves the queue honours
    /// the same numbers.
    /// </remarks>
    private static async Task RecordedBackoffsAreExactlyFiveAndThirtySecondsAsync(
        BackgroundJobsApiHost host,
        AdjustableTimeProvider clock,
        OperationScript script)
    {
        using var owner = await OwnerClient.CreateAsync(host);
        using var created = await Enqueue(owner, "test.transient", Guid.NewGuid(), 1, "retry-recorded");
        var operationId = await ReadIdAsync(created);

        var before = script.TransientHandlerCalls;

        // Attempt one: a transient failure, so the operation is handed back to `queued` with a
        // recorded due time.
        using (var worker = host.OpenWorker())
        {
            var attempt = await worker.Worker.RunAsync(operationId);
            Assert.Equal(OperationWorker.AttemptOutcome.Retrying, attempt.Outcome);
        }

        var afterFirst = await OperationRows.ReadAsync(host.Database, operationId);
        Assert.NotNull(afterFirst);
        Assert.Equal("queued", afterFirst.Status);
        Assert.Equal(1, afterFirst.Attempt);
        Assert.Equal(OperationPolicy.FirstBackoff, afterFirst.RecordedBackoff);
        Assert.Equal(5, afterFirst.RecordedBackoff!.Value.TotalSeconds);

        // Attempt two: the same, and this time the longer wait.
        using (var worker = host.OpenWorker())
        {
            var attempt = await worker.Worker.RunAsync(operationId);
            Assert.Equal(OperationWorker.AttemptOutcome.Retrying, attempt.Outcome);
        }

        var afterSecond = await OperationRows.ReadAsync(host.Database, operationId);
        Assert.NotNull(afterSecond);
        Assert.Equal("queued", afterSecond.Status);
        Assert.Equal(2, afterSecond.Attempt);
        Assert.Equal(OperationPolicy.SecondBackoff, afterSecond.RecordedBackoff);
        Assert.Equal(30, afterSecond.RecordedBackoff!.Value.TotalSeconds);

        // Attempt three spends the budget: the operation fails and schedules nothing further.
        using (var worker = host.OpenWorker())
        {
            var attempt = await worker.Worker.RunAsync(operationId);
            Assert.Equal(OperationWorker.AttemptOutcome.RetriesExhausted, attempt.Outcome);
        }

        var afterThird = await OperationRows.ReadAsync(host.Database, operationId);
        Assert.NotNull(afterThird);
        Assert.Equal("failed", afterThird.Status);
        Assert.Equal(3, afterThird.Attempt);
        Assert.Null(afterThird.NextAttemptAt);

        Assert.Equal(before + 3, script.TransientHandlerCalls);
        _ = clock;
    }

    // =============================================================================================
    // BACKGROUND-JOBS-002 Failure: the input revision moves before the result commit, so the
    // operation is superseded and no stale result attaches.
    // =============================================================================================

    [Fact]
    public async Task A_moved_input_revision_supersedes_the_operation_and_attaches_no_result()
    {
        var database = await ProvisionedDatabaseAsync("superseded");
        var clock = new AdjustableTimeProvider(Clock);
        var script = new OperationScript();

        await using var host = BackgroundJobsApiHost.Start(database, clock, script);
        using var owner = await OwnerClient.CreateAsync(host);

        // A real project, so the target's revision is a real column a real edit can move.
        var projectId = await CreateProjectAsync(owner, "Ledger");

        using var created = await Enqueue(owner, "project.export", projectId, 1, "supersede-1");
        var operationId = await ReadIdAsync(created);

        using var worker = host.OpenWorker();
        var running = worker.Worker.RunAsync(operationId);

        // The attempt is genuinely in flight and holding its lease.
        await script.WaitForSlowHandlerAsync();
        await UpdateProjectAsync(owner, projectId);

        // The input has moved, and only now is the handler allowed to return - so the commit is
        // genuinely the step that meets a revision it did not read.
        script.ReleaseSlowHandler();

        var result = await running;

        Assert.Equal(OperationWorker.AttemptOutcome.Superseded, result.Outcome);

        var operation = await OperationRows.AwaitAsync(database, operationId, "superseded");

        // The status is the designed one, the attempt counted, and nothing attached: the reference the
        // handler produced is not on the row, which is the whole point of the check.
        Assert.Equal("superseded", operation.Status);
        Assert.Null(operation.ResultRef);
        Assert.Null(operation.ErrorCode);
        Assert.Equal(1, operation.Attempt);
        Assert.Equal(1, script.SlowHandlerCalls);

        Assert.Equal(
            0,
            await OperationRows.CountForOperationAsync(
                database,
                operationId,
                "SELECT count(*) FROM operations WHERE id = @id AND result_ref IS NOT NULL"));

        // The owner is told the same thing over the wire.
        using var read = await owner.GetAsync($"/api/operations/{operationId}");
        read.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        Assert.Equal("superseded", document.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("resultRef").ValueKind);
    }

    // =============================================================================================
    // BACKGROUND-JOBS-002 Boundary: an owner cancels an in-flight operation, the terminal state is
    // `cancelled`, and a late result is discarded.
    // =============================================================================================

    [Fact]
    public async Task Cancelling_an_in_flight_operation_is_terminal_and_discards_a_late_result()
    {
        var database = await ProvisionedDatabaseAsync("cancelled");
        var clock = new AdjustableTimeProvider(Clock);
        var script = new OperationScript();

        await using var host = BackgroundJobsApiHost.Start(database, clock, script);
        using var owner = await OwnerClient.CreateAsync(host);

        using var created = await Enqueue(owner, "test.slow", Guid.NewGuid(), 1, "cancel-1");
        var operationId = await ReadIdAsync(created);

        using var worker = host.OpenWorker();
        var running = worker.Worker.RunAsync(operationId);
        await script.WaitForSlowHandlerAsync();

        // The owner's stop, over the wire, in the delivered envelope's own shape.
        using (var stop = await PostWithoutBodyAsync(owner, $"/api/operations/{operationId}/cancel"))
        {
            stop.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await stop.Content.ReadAsStringAsync());

            // Terminal immediately, and already not cancellable again.
            Assert.Equal("cancelled", document.RootElement.GetProperty("status").GetString());
            Assert.False(document.RootElement.GetProperty("cancellable").GetBoolean());
            Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("resultRef").ValueKind);
        }

        // The handler is released anyway and produces a result. The commit is refused, so the result
        // is discarded rather than attached - the row is the reason, and it is already terminal.
        script.ReleaseSlowHandler();
        await running;

        var operation = await OperationRows.AwaitAsync(database, operationId, "cancelled");
        Assert.Null(operation.ResultRef);
        Assert.True(operation.CancelRequested);
        Assert.Equal(1, script.SlowHandlerCalls);

        Assert.Equal(
            0,
            await OperationRows.CountForOperationAsync(
                database,
                operationId,
                "SELECT count(*) FROM operations WHERE id = @id AND result_ref IS NOT NULL"));

        // Stopping again is a conflict rather than a quiet no-op, so a caller is never told a stop
        // happened when it did not.
        using (var again = await PostWithoutBodyAsync(owner, $"/api/operations/{operationId}/cancel"))
        {
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            using var document = JsonDocument.Parse(await again.Content.ReadAsStringAsync());
            Assert.Equal("operation_not_cancellable", document.RootElement.GetProperty("code").GetString());
            Assert.Equal(409, Chronicleaf.Application.Common.ApiErrorStatus.ForCode("operation_not_cancellable"));
        }

        // And nothing was rescheduled for a cancelled operation.
        Assert.Equal(0, await ScheduledJobsAsync(database, operationId));
    }

    // =============================================================================================
    // Idempotency: the same key with the same payload returns the original operation; a different
    // payload is a conflict and never a second row.
    // =============================================================================================

    [Fact]
    public async Task The_same_idempotency_key_returns_the_original_and_a_different_payload_conflicts()
    {
        var database = await ProvisionedDatabaseAsync("idempotency");
        var clock = new AdjustableTimeProvider(Clock);
        var script = new OperationScript();

        await using var host = BackgroundJobsApiHost.Start(database, clock, script);
        using var owner = await OwnerClient.CreateAsync(host);

        var targetId = Guid.NewGuid();

        using var first = await Enqueue(owner, "test.success", targetId, 1, "idem-1");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.NotNull(first.Headers.Location);
        var originalId = await ReadIdAsync(first);

        // The identical request again: the original operation, a 200 rather than a 201, and no Location
        // header. A caller can tell a replay from a creation without parsing anything.
        using var replay = await Enqueue(owner, "test.success", targetId, 1, "idem-1");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Null(replay.Headers.Location);
        Assert.Equal(originalId, await ReadIdAsync(replay));

        // The same key bound to different work: a conflict, and still no second row.
        using var conflicting = await Enqueue(owner, "test.success", targetId, 2, "idem-1");
        Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);
        using (var document = JsonDocument.Parse(await conflicting.Content.ReadAsStringAsync()))
        {
            Assert.Equal("operation_idempotency_conflict", document.RootElement.GetProperty("code").GetString());
        }

        // A different kind under the same key is a different operation, because the key is scoped to
        // (scope, kind) rather than to the scope alone.
        using var otherKind = await Enqueue(owner, "test.slow", targetId, 1, "idem-1");
        Assert.Equal(HttpStatusCode.Created, otherKind.StatusCode);
        Assert.NotEqual(originalId, await ReadIdAsync(otherKind));

        // Exactly one operation per (scope, kind, key), and the original is untouched: the conflict
        // changed nothing about the work the key was already bound to.
        Assert.Equal(
            1,
            await OperationRows.CountAsync(
                database,
                "SELECT count(*) FROM operations WHERE kind = 'test.success' AND idempotency_key = 'idem-1'"));

        var original = await OperationRows.ReadAsync(database, originalId);
        Assert.NotNull(original);
        Assert.Equal("queued", original.Status);

        // The key is also scoped to the installation, so the same key elsewhere is unrelated.
        Assert.Equal(
            0,
            await OperationRows.CountForScopeAsync(database, ForeignScopeId));
    }

    // =============================================================================================
    // Lease recovery: an expired lease is recovered exactly once, and never twice.
    // =============================================================================================

    [Fact]
    public async Task An_expired_lease_is_recovered_exactly_once_and_never_twice()
    {
        var database = await ProvisionedDatabaseAsync("lease_recovery");
        var clock = new AdjustableTimeProvider(Clock);
        var script = new OperationScript();

        await using var host = BackgroundJobsApiHost.Start(database, clock, script);
        using var owner = await OwnerClient.CreateAsync(host);

        using var created = await Enqueue(owner, "test.slow", Guid.NewGuid(), 1, "lease-1");
        var operationId = await ReadIdAsync(created);

        // The lease is a real deadline, and the heartbeat that maintains it is a real interval. These
        // are the numbers as implemented, asserted so a change to either is a failing test.
        Assert.Equal(TimeSpan.FromSeconds(180), OperationPolicy.LeaseDuration);
        Assert.Equal(TimeSpan.FromSeconds(15), OperationPolicy.HeartbeatInterval);
        Assert.Equal(1, OperationPolicy.MaxLeaseRecoveries);

        // A worker claims the operation and then stops without committing: the exact state a process
        // killed mid-attempt leaves behind. Its scope stays open - the run is still real, it simply
        // never comes back.
        using var abandoned = host.OpenWorker();
        var abandonedRun = abandoned.Worker.RunAsync(operationId);
        await script.WaitForSlowHandlerAsync();

        var claimed = await OperationRows.ReadAsync(database, operationId);
        Assert.NotNull(claimed);
        Assert.Equal("running", claimed.Status);
        Assert.Equal(1, claimed.Attempt);
        Assert.Equal(0, claimed.LeaseRecoveries);
        Assert.NotNull(claimed.LeaseExpiresAt);

        // The clock moves past the lease, so the claim that took it has provably expired.
        clock.Advance(OperationPolicy.LeaseDuration + TimeSpan.FromSeconds(5));

        // Two workers now try to take the abandoned run over at the same moment. The recovery counter
        // is the arbiter, in the database, not in either worker.
        using var recovererA = host.OpenWorker();
        using var recovererB = host.OpenWorker();

        var recoveryA = recovererA.Worker.RunAsync(operationId);
        var recoveryB = recovererB.Worker.RunAsync(operationId);

        // Every handler is released at once, so the abandoned run and the two takeover attempts all
        // reach their commit together. This is the race the lease exists for, run all at once.
        script.ReleaseSlowHandler();

        var outcomes = await Task.WhenAll(abandonedRun, recoveryA, recoveryB);

        // Exactly one of the three committed. The abandoned run could not, because its lease token is
        // no longer the row's; the second recoverer could not, because the counter has already moved.
        // Which of the three runs wins the takeover is a genuine race, and this scenario does not
        // pretend otherwise: what it asserts is the property the race cannot get wrong. At least one
        // run was refused outright at the claim, which is the recovery bound doing its work; and
        // exactly one result exists afterwards, which is the claim's exclusivity doing its work.
        Assert.Contains(outcomes, result => result.Outcome == OperationWorker.AttemptOutcome.NotClaimed);
        Assert.True(
            outcomes.Count(result => result.Outcome == OperationWorker.AttemptOutcome.Succeeded) <= 1,
            "more than one worker reported committing a result");

        var operation = await OperationRows.AwaitAsync(database, operationId, "succeeded");
        Assert.Equal(1, operation.LeaseRecoveries);
        Assert.Equal("test-result:late", operation.ResultRef);

        // A third takeover attempt on a finished operation changes nothing, and the counter is still
        // one: recovery happened once, and the bound is a stored fact rather than a convention.
        using (var late = host.OpenWorker())
        {
            var third = await late.Worker.RunAsync(operationId);
            Assert.Equal(OperationWorker.AttemptOutcome.NotClaimed, third.Outcome);
        }

        var recoveries = await OperationRows.ReadAsync(database, operationId);
        Assert.NotNull(recoveries);
        Assert.Equal(1, recoveries.LeaseRecoveries);

        // Exactly one result across the abandoned run and its recovery.
        Assert.Equal(
            1,
            await OperationRows.CountForOperationAsync(
                database,
                operationId,
                "SELECT count(*) FROM operations WHERE id = @id AND status = 'succeeded' AND result_ref IS NOT NULL"));
    }

    // =============================================================================================
    // The queue's own placement, and the dashboard that is not there.
    // =============================================================================================

    [Fact]
    public async Task The_queue_shares_the_application_database_and_no_dashboard_is_served()
    {
        var database = await ProvisionedDatabaseAsync("dashboard");
        var clock = new AdjustableTimeProvider(Clock);
        var script = new OperationScript();

        await using var host = BackgroundJobsApiHost.Start(database, clock, script);

        // The queue's schema was created by the committed migration, in the same database as the
        // application's own tables, and the engine is running against it with real workers.
        Assert.Equal(
            1,
            await OperationRows.CountAsync(
                database,
                "SELECT count(*) FROM information_schema.schemata WHERE schema_name = 'hangfire'"));
        Assert.Equal(
            1,
            await OperationRows.CountAsync(
                database,
                "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'hangfire' AND table_name = 'job'"));

        Assert.True(host.Services.GetRequiredService<OperationQueueAvailability>().Configured);
        Assert.NotNull(host.Services.GetRequiredService<JobStorage>());

        // The dashboard is not a route this build has. Every path Hangfire would map is either the
        // shell's client-route fallback or the shell's own 404, and never a dashboard page.
        using var anonymous = host.CreateClient();

        foreach (var path in new[] { "/hangfire", "/hangfire/recurring", "/hangfire/dashboard", "/hangfire/queues" })
        {
            using var response = await anonymous.GetAsync(path);
            var body = await response.Content.ReadAsStringAsync();

            Assert.DoesNotContain("Hangfire", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("RecurringJobs", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("hangfire.job", body, StringComparison.OrdinalIgnoreCase);
        }

        // And the integration that could map one is not a dependency of this build at all, so nothing
        // added later can turn the dashboard on by importing it.
        var referenced = typeof(OperationQueueServiceCollectionExtensions).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .ToArray();

        Assert.Contains("Hangfire.Core", referenced, StringComparer.Ordinal);
        Assert.Contains("Hangfire.PostgreSql", referenced, StringComparer.Ordinal);
        Assert.DoesNotContain("Hangfire.AspNetCore", referenced, StringComparer.Ordinal);
        Assert.DoesNotContain("Hangfire.NetCore", referenced, StringComparer.Ordinal);
    }

    [Fact]
    public void Asking_for_the_dashboard_is_refused_at_start_up()
    {
        // A configuration that asked for the dashboard must not quietly start without it, and must
        // certainly not start with one. The refusal is the testable version of "disabled by default".
        var settings = new Dictionary<string, string?>
        {
            [ChronicleafConnection.SettingName] = "Host=127.0.0.1;Database=none;Username=none",
            [$"{OperationQueueOptions.SectionName}:DashboardEnabled"] = "true",
        };

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();

        var failure = Assert.Throws<InvalidOperationException>(
            () => services.AddChronicleafOperationQueue(configuration));

        Assert.Contains("DashboardEnabled", failure.Message, StringComparison.Ordinal);

        // And the refusal happened before anything was registered, so a refused start-up leaves no
        // half-built container behind.
        Assert.Empty(services);
    }

    // =============================================================================================
    // The access boundary: private routes, an unchanged public surface, and a foreign scope.
    // =============================================================================================

    [Fact]
    public async Task The_operation_routes_are_private_and_a_foreign_scope_is_not_found()
    {
        var database = await ProvisionedDatabaseAsync("boundary");
        var clock = new AdjustableTimeProvider(Clock);
        var script = new OperationScript();

        await using var host = BackgroundJobsApiHost.Start(database, clock, script);
        using var anonymous = host.CreateClient();

        // The delivered public routes are unchanged.
        using (var health = await anonymous.GetAsync("/api/health"))
        {
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }

        using (var ready = await anonymous.GetAsync("/api/ready"))
        {
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }

        // Every operation route is private. A matched /api endpoint without the public marker has
        // already been refused by the access package, which is why the status is 401 and not 404.
        foreach (var (method, path) in new (HttpMethod, string)[]
        {
            (HttpMethod.Get, "/api/operations"),
            (HttpMethod.Post, "/api/operations"),
            (HttpMethod.Get, $"/api/operations/{Guid.NewGuid()}"),
            (HttpMethod.Post, $"/api/operations/{Guid.NewGuid()}/cancel"),
        })
        {
            using var request = new HttpRequestMessage(method, path);
            request.Content = JsonContent.Create(new { });

            using var refused = await anonymous.SendAsync(request);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        // An operation that exists in another scope is not found for the owner, and the answer is
        // the same as for one that never existed.
        using var owner = await OwnerClient.CreateAsync(host);
        using (var foreign = await owner.GetAsync($"/api/operations/{ForeignScopeId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        }

        // The readiness contract names this build's migration, so a database stopped before it is a
        // mismatch rather than an empty installation.
        Assert.Contains(
            ChronicleafSchema.BackgroundOperationsMigrationId,
            ChronicleafSchema.RequiredMigrationIds,
            StringComparer.Ordinal);
    }

    // =============================================================================================
    // The operation list, which is what the panel reads.
    // =============================================================================================

    [Fact]
    public async Task The_operation_list_reports_each_stored_status_and_nothing_else()
    {
        var database = await ProvisionedDatabaseAsync("listing");
        var clock = new AdjustableTimeProvider(Clock);
        var script = new OperationScript();

        await using var host = BackgroundJobsApiHost.Start(database, clock, script);
        using var owner = await OwnerClient.CreateAsync(host);

        // The installation starts empty. The panel has to be able to say so, and the list has to be
        // empty rather than showing something invented.
        using (var empty = await owner.GetAsync("/api/operations"))
        {
            empty.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await empty.Content.ReadAsStringAsync());
            Assert.Empty(document.RootElement.GetProperty("items").EnumerateArray());
        }

        // One of each kind, so every status the panel can show is backed by a real row.
        foreach (var kind in new[] { "test.success", "test.permanent", "test.transient", "test.slow" })
        {
            using var created = await Enqueue(owner, kind, Guid.NewGuid(), 1, $"list-{kind}");
            created.EnsureSuccessStatusCode();
        }

        // A queued operation, and one that is cancelled, are produced through the real routes.
        using var cancelled = await Enqueue(owner, "test.slow", Guid.NewGuid(), 1, "list-cancelled");
        var cancelledId = await ReadIdAsync(cancelled);
        using (var stop = await PostWithoutBodyAsync(owner, $"/api/operations/{cancelledId}/cancel"))
        {
            stop.EnsureSuccessStatusCode();
        }

        await host.DispatchAsync();

        // The permanent failure is the one that settles first and alone, so waiting for it is enough
        // to know the queue has really drained this scope's work.
        var permanentId = await ReadIdByKeyAsync(database, "list-test.permanent");
        await OperationRows.AwaitAsync(database, permanentId, "failed", within: TimeSpan.FromSeconds(90));

        // The slow handler is still holding its lease; releasing it lets the host shut down cleanly.
        script.ReleaseSlowHandler();

        using var list = await owner.GetAsync("/api/operations?limit=100");
        list.EnsureSuccessStatusCode();
        using var listed = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var items = listed.RootElement.GetProperty("items").EnumerateArray().ToArray();

        // Every stored row is reported, each with the status the database holds, and none carries a
        // result it did not commit.
        Assert.Equal(5, items.Length);
        Assert.Contains(items, item => item.GetProperty("status").GetString() == "cancelled");
        Assert.Contains(items, item => item.GetProperty("status").GetString() == "failed");
        Assert.Contains(items, item => item.GetProperty("status").GetString() == "queued");

        foreach (var item in items)
        {
            var status = item.GetProperty("status").GetString();
            Assert.Contains(status, OperationStatuses.All, StringComparer.Ordinal);
            Assert.Equal(3, item.GetProperty("maxAttempts").GetInt32());
            Assert.False(item.TryGetProperty("leaseToken", out _));
            Assert.False(item.TryGetProperty("fingerprint", out _));
            Assert.False(item.TryGetProperty("leaseRecoveries", out _));

            var resultRef = item.GetProperty("resultRef").ValueKind;
            if (status == "succeeded")
            {
                Assert.Equal(JsonValueKind.String, resultRef);
            }
            else
            {
                Assert.Equal(JsonValueKind.Null, resultRef);
            }
        }
    }

    // =============================================================================================
    // Helpers.
    // =============================================================================================

    /// <summary>
    /// A migrated database with the singleton installation provisioned through the data foundation's
    /// own contract, which is the order a real deployment follows.
    /// </summary>
    private async Task<string> ProvisionedDatabaseAsync(string purpose)
    {
        var database = await postgres.CreateDatabaseAsync(purpose);
        await ChronicleafPostgresFixture.MigrateAsync(database);

        await using (var host = BackgroundJobsApiHost.Start(database, new AdjustableTimeProvider(Clock), new OperationScript()))
        {
            using var scope = host.Services.CreateScope();
            var provider = scope.ServiceProvider;

            await provider.GetRequiredService<IUnitOfWork>().ExecuteAsync(
                token => provider.GetRequiredService<IInstallationStore>().ProvisionAsync(
                    InstallationId,
                    ChronicleafDbContext.RequiredSchemaVersion,
                    InstallationStore.ProvisionedAction,
                    actorId: null,
                    new OutboxPayload
                    {
                        Kind = InstallationStore.ProvisionedAction,
                        ScopeId = InstallationId,
                        AggregateId = InstallationId,
                        Revision = 1,
                    },
                    token));
        }

        return database;
    }

    private static async Task<HttpResponseMessage> Enqueue(
        OwnerClient owner,
        string kind,
        Guid targetId,
        long inputRevision,
        string idempotencyKey)
    {
        ArgumentNullException.ThrowIfNull(owner);

        using var request = await owner.Post(
            "/api/operations",
            new
            {
                kind,
                targetId = targetId.ToString(),
                inputRevision,
                idempotencyKey,
            });

        return await owner.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> PostWithoutBodyAsync(OwnerClient owner, string path)
    {
        ArgumentNullException.ThrowIfNull(owner);

        using var request = await owner.PostWithoutBody(path);

        return await owner.SendAsync(request);
    }

    private static async Task<Guid> ReadIdAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return Guid.Parse(document.RootElement.GetProperty("id").GetString()!);
    }

    private static async Task<Guid> CreateProjectAsync(OwnerClient owner, string name)
    {
        using var created = await owner.Post("/api/projects", new { name, description = "A project." });
        using var response = await owner.SendAsync(created);
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return Guid.Parse(document.RootElement.GetProperty("id").GetString()!);
    }

    private static async Task UpdateProjectAsync(OwnerClient owner, Guid projectId)
    {
        using var read = await owner.GetAsync($"/api/projects/{projectId}");
        read.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        var revision = document.RootElement.GetProperty("revision").GetInt64();

        using var edited = await owner.Patch(
            $"/api/projects/{projectId}",
            revision,
            new { name = "Ledger, revised", description = "A project." });
        using var response = await owner.SendAsync(edited);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>How many jobs the real queue holds for an operation, whether waiting or done.</summary>
    private static Task<long> QueuedJobsAsync(string database, Guid operationId) =>
        OperationRows.CountForOperationAsync(
            database,
            operationId,
            "SELECT count(*) FROM hangfire.job WHERE arguments::text ILIKE '%' || @id::text || '%'");

    /// <summary>
    /// How many of those the real queue put into its delayed schedule.
    /// </summary>
    /// <remarks>
    /// The engine's own schema is read here, not a table this repository owns: <c>hangfire.job</c> and
    /// <c>hangfire.state</c> are written by Hangfire.PostgreSql in the application database, and
    /// asking them is what makes "the queue really scheduled a retry" a fact rather than a claim.
    /// </remarks>
    private static Task<long> ScheduledJobsAsync(string database, Guid operationId) =>
        OperationRows.CountForOperationAsync(
            database,
            operationId,
            """
            SELECT count(DISTINCT j.id)
            FROM hangfire.job j
            JOIN hangfire.state s ON s.jobid = j.id
            WHERE j.arguments::text ILIKE '%' || @id::text || '%' AND s.name = 'Scheduled'
            """);

    /// <summary>Reads one operation's id by the idempotency key it was created under.</summary>
    private static Task<Guid> ReadIdByKeyAsync(string database, string idempotencyKey) =>
        OperationRows.ReadIdByKeyAsync(
            database,
            "SELECT id FROM operations WHERE kind = 'test.permanent' AND idempotency_key = @key",
            idempotencyKey);
}
