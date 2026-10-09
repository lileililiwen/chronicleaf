using Chronicleaf.Application.Access;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Operations;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Infrastructure.Operations;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// Hosts the real application with the queue running, against a real disposable PostgreSQL and with
/// a test handler in place of any provider.
/// </summary>
/// <remarks>
/// <para>
/// The host is the delivered <c>WebApplicationFactory</c> pattern: the connection string arrives
/// through the same configuration key production reads, the access configuration through its own
/// keys, and the only substituted services are the clock and the operation handlers. The Hangfire
/// storage, the server, the workers, the outbox dispatcher, the stores and the routes are all the
/// production ones - there is no in-memory queue anywhere in this class.
/// </para>
/// <para>
/// The automated dispatch loop is switched off, and that is a deliberate test affordance rather than
/// a weakening. The loop is the seam between "the request committed" and "the queue knows", and every
/// restart scenario has to place the dispatch on a chosen side of that seam. The loop's own code is
/// exercised by running a pass by hand, through the same class the hosted service runs.
/// </para>
/// </remarks>
public sealed class BackgroundJobsApiHost : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly TimeProvider _clock;
    private readonly OperationScript _script;

    private BackgroundJobsApiHost(string connectionString, TimeProvider clock, OperationScript script)
    {
        _connectionString = connectionString;
        _clock = clock;
        _script = script;
    }

    /// <summary>The connection string this host was started with, for the assertions that read the database.</summary>
    public string Database => _connectionString;

    public static BackgroundJobsApiHost Start(
        string connectionString,
        TimeProvider clock,
        OperationScript script) =>
        new(connectionString, clock, script);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(ChronicleafConnection.SettingName, _connectionString);
        builder.UseSetting(AccessSettings.AccessTokenHashSettingName, OwnerClient.AccessTokenHash);
        builder.UseSetting(AccessSettings.PublicOriginSettingName, OwnerClient.PublicOrigin);
        builder.UseEnvironment("Production");

        // The dispatch loop runs only when a test asks it to, so a restart can be staged exactly.
        builder.UseSetting($"{OperationQueueOptions.SectionName}:Enabled", "false");

        // The engine polls on its own schedule; a test is not going to wait fifteen seconds for a job
        // it deliberately queued. This is a poll interval, not a behaviour: the queue, the workers and
        // the claiming are all the production ones.
        builder.UseSetting($"{OperationQueueOptions.SectionName}:SchedulePollingInterval", "00:00:00.200");
        builder.UseSetting($"{OperationQueueOptions.SectionName}:HeartbeatInterval", "00:00:05");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton(_clock);

            foreach (var kind in OperationScript.Kinds)
            {
                services.AddSingleton<IOperationHandler>(
                    new ScriptedOperationHandler(kind, _script));
            }
        });
    }

    /// <summary>
    /// Runs one real outbox dispatch pass and reports what it did.
    /// </summary>
    /// <remarks>
    /// This is the loop's own body, called by hand: the delivered dispatcher hands each pending event
    /// to the registered handler, which enqueues a real job through the real <c>IJobDispatcher</c>.
    /// </remarks>
    public async Task<IReadOnlyList<OutboxDispatchResult>> DispatchAsync(
        bool stampDispatched = true,
        CancellationToken cancellationToken = default)
    {
        using var scope = Services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>();
        var stamper = scope.ServiceProvider.GetRequiredService<IOutboxDispatchStamper>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        var results = await dispatcher.DispatchPendingAsync(100, cancellationToken);

        if (!stampDispatched)
        {
            return results;
        }

        foreach (var result in results)
        {
            if (result.Outcome == OutboxDispatchOutcome.Handled)
            {
                await stamper.TryMarkDispatchedAsync(result.EventId, clock.GetUtcNow(), cancellationToken);
            }
        }

        return results;
    }

    /// <summary>
    /// Opens a scope whose <see cref="OperationWorker"/> is bound to its own <c>DbContext</c>, and
    /// keeps that scope alive until the returned handle is disposed.
    /// </summary>
    /// <remarks>
    /// Two of these running at once are two real workers: two containers' worth of scope, two
    /// contexts, two sets of connections. That is what makes the two-worker boundary a concurrency
    /// rather than two calls into one object.
    /// </remarks>
    public WorkerHandle OpenWorker() => new(Services.CreateScope());

    /// <summary>A live scope and the worker it resolved.</summary>
    public sealed class WorkerHandle : IDisposable
    {
        private readonly IServiceScope _scope;

        internal WorkerHandle(IServiceScope scope)
        {
            _scope = scope;
            Worker = scope.ServiceProvider.GetRequiredService<OperationWorker>();
        }

        public OperationWorker Worker { get; }

        public void Dispose() => _scope.Dispose();
    }
}

/// <summary>
/// What the test handlers do, decided by the test rather than by a provider.
/// </summary>
/// <remarks>
/// There is no provider anywhere in this assembly's background-jobs scenarios: nothing reaches the
/// network, no token is presented, and no external write is attempted. A handler here either produces
/// a result reference, raises a classified failure, or waits for the test to let it finish - which is
/// how the cancellation and supersede boundaries are staged at a chosen instant.
/// </remarks>
public sealed class OperationScript
{
    /// <summary>
    /// The kinds this fixture registers a handler for, one per scenario behaviour.
    /// </summary>
    /// <remarks>
    /// <c>project.export</c> is here for the supersede scenario and is the only kind whose target is a
    /// real delivered table, because only a real revision can genuinely move under a run.
    /// </remarks>
    public static readonly IReadOnlyList<string> Kinds =
    [
        "test.success",
        "test.permanent",
        "test.transient",
        "test.slow",
        "project.export",
    ];

    private int _successCalls;
    private int _permanentCalls;
    private int _transientCalls;
    private int _slowCalls;

    private readonly TaskCompletionSource _slowStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _slowRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int SuccessHandlerCalls => Volatile.Read(ref _successCalls);
    public int PermanentHandlerCalls => Volatile.Read(ref _permanentCalls);
    public int TransientHandlerCalls => Volatile.Read(ref _transientCalls);
    public int SlowHandlerCalls => Volatile.Read(ref _slowCalls);

    /// <summary>Completes once a slow handler is actually running, so a race is never a guess.</summary>
    public Task WaitForSlowHandlerAsync() => _slowStarted.Task;

    /// <summary>Lets a slow handler finish and return its result, which is then discarded or refused.</summary>
    public void ReleaseSlowHandler() => _slowRelease.TrySetResult();

    internal async Task<OperationResult> RunAsync(string kind, CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case "test.success":
                Interlocked.Increment(ref _successCalls);
                return new OperationResult("test-result:success");

            case "test.permanent":
                Interlocked.Increment(ref _permanentCalls);

                // The message is deliberately unlike anything stored, so the redaction assertion below
                // can prove the handler's own text never reaches the operation row.
                throw new OperationPermanentException(OperationErrorCodes.Permanent);

            case "test.transient":
                Interlocked.Increment(ref _transientCalls);
                throw new OperationTransientException(OperationErrorCodes.Transient);

            case "test.slow":
            case "project.export":
                Interlocked.Increment(ref _slowCalls);
                _slowStarted.TrySetResult();

                // Waits for the test, so a cancellation or a moved revision can be staged while the
                // attempt is genuinely in flight and holding its lease.
                await _slowRelease.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

                return new OperationResult("test-result:late");

            default:
                throw new InvalidOperationException($"No test handler is scripted for '{kind}'.");
        }
    }
}

/// <summary>
/// The one handler class, bound to a kind per registration.
/// </summary>
/// <remarks>
/// It holds a reference to the script rather than to any store, so it has no way to write an
/// operation's status, result or error code even if a test wanted it to. That is the same property
/// the production handler contract has.
/// </remarks>
public sealed class ScriptedOperationHandler(string kind, OperationScript script) : IOperationHandler
{
    public string Kind => kind;

    public Task<OperationResult> HandleAsync(OperationRef operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        return script.RunAsync(kind, cancellationToken);
    }
}

/// <summary>Reads a single operation row straight out of PostgreSQL, as the test sees it.</summary>
public sealed record StoredOperation(
    Guid Id,
    string Status,
    int Attempt,
    string? ResultRef,
    string? ErrorCode,
    int LeaseRecoveries,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset? LeaseExpiresAt,
    bool CancelRequested,
    DateTimeOffset? UpdatedAt)
{
    /// <summary>
    /// The wait the operation recorded for its next attempt, measured from the write that scheduled
    /// it. With an injected clock this is the backoff the policy fixed, exactly, rather than an
    /// approximation of it.
    /// </summary>
    public TimeSpan? RecordedBackoff =>
        NextAttemptAt is { } next && UpdatedAt is { } updated ? next - updated : null;
}

/// <summary>Raw reads against the real database, for the assertions about stored state.</summary>
public static class OperationRows
{
    public static async Task<StoredOperation?> ReadAsync(string connectionString, Guid operationId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, status, attempt, result_ref, error_code, lease_recoveries,
                   next_attempt_at, lease_expires_at, cancel_requested_at IS NOT NULL, updated_at
            FROM operations
            WHERE id = @id
            """;

        command.Parameters.AddWithValue("id", operationId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new StoredOperation(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTime>(6),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTime>(7),
            reader.GetBoolean(8),
            reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTime>(9));
    }

    /// <summary>Waits for a status, then returns the row. A timeout is a failure, not a pass.</summary>
    public static async Task<StoredOperation> AwaitAsync(
        string connectionString,
        Guid operationId,
        string expectedStatus,
        TimeSpan? within = null)
    {
        var budget = within ?? TimeSpan.FromSeconds(90);
        var deadline = DateTime.UtcNow + budget;

        while (DateTime.UtcNow < deadline)
        {
            var row = await ReadAsync(connectionString, operationId);
            if (row is not null && string.Equals(row.Status, expectedStatus, StringComparison.Ordinal))
            {
                return row;
            }

            await Task.Delay(10);
        }

        var last = await ReadAsync(connectionString, operationId);
        throw new TimeoutException(
            $"Operation {operationId} is '{last?.Status ?? "missing"}', not '{expectedStatus}', after {budget}.");
    }

    /// <summary>Counts rows a statement matches, over a raw connection.</summary>
    public static async Task<long> CountAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();

        return value is null or DBNull ? 0 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Counts the rows a statement about one operation matches, binding the id as a parameter.
    /// </summary>
    /// <remarks>
    /// The id is always bound, never spliced into the statement. These are the engine's and the
    /// application's own tables, so a statement that reads them has to say what it means as a query
    /// rather than as text.
    /// </remarks>
    public static Task<long> CountForOperationAsync(string connectionString, Guid operationId, string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        return CountBoundAsync(connectionString, sql, command => command.Parameters.AddWithValue("id", operationId));
    }

    private static async Task<long> CountBoundAsync(
        string connectionString,
        string sql,
        Action<NpgsqlCommand> bind)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);

        var value = await command.ExecuteScalarAsync();

        return value is null or DBNull ? 0 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Counts rows matching a statement bound to one scope, for the boundary assertions.</summary>
    public static Task<long> CountForScopeAsync(string connectionString, Guid scopeId) =>
        CountBoundAsync(
            connectionString,
            "SELECT count(*) FROM operations WHERE scope_id = @scope",
            command => command.Parameters.AddWithValue("scope", scopeId));

    /// <summary>Reads one id a statement selects, so a test can name a row it did not keep a handle on.</summary>
    public static async Task<Guid> ReadIdByKeyAsync(
        string connectionString,
        string sql,
        string idempotencyKey)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("key", idempotencyKey);

        var value = await command.ExecuteScalarAsync();
        if (value is null or DBNull)
        {
            throw new InvalidOperationException("No operation matched the statement.");
        }

        return (Guid)value;
    }

    /// <summary>The instants the real queue enqueued a job for, oldest first.</summary>
    /// <remarks>
    /// These are the engine's own rows. A scheduled job is stored with the instant it is due, so the
    /// gaps between consecutive entries are the delays the worker actually asked the queue for - the
    /// backoff, measured by the engine that honoured it, rather than by a test's stopwatch.
    /// </remarks>
    public static async Task<List<DateTimeOffset>> ReadJobEnqueueInstantsAsync(
        string connectionString,
        Guid operationId)
    {
        var instants = new List<DateTimeOffset>();

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT enqueuedat
            FROM hangfire.job
            WHERE arguments::text ILIKE '%' || @id::text || '%'
            ORDER BY enqueuedat
            """;
        command.Parameters.AddWithValue("id", operationId);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            instants.Add(new DateTimeOffset(reader.GetFieldValue<DateTime>(0), TimeSpan.Zero));
        }

        return instants;
    }
}
