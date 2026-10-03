using Chronicleaf.Application.Operations;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Readiness;
using Hangfire.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Platform.Core.Time;
using Platform.Jobs;
using Platform.Jobs.Hangfire;
using Platform.Jobs.Hangfire.DependencyInjection;

namespace Chronicleaf.Infrastructure.Operations;

/// <summary>
/// Registers durable operation execution (BACKGROUND-JOBS-001, BACKGROUND-JOBS-002) on the platform's
/// scheduling boundary (PLATFORM-JOBS-ADOPTION-001/002).
/// </summary>
/// <remarks>
/// <para>
/// This is the only place the queue is configured. The engine, its server, its job activator, its
/// dispatcher and its executor are the shared platform's: one call to <c>AddPlatformHangfireJobs</c>
/// wires all of them. What Chronicleaf supplies is what the platform deliberately leaves to the
/// application - the same PostgreSQL connection string the rest of the application uses, the schema
/// its committed migration created, a bounded and disposable connection pool, and the payload handler
/// the platform's executor routes into.
/// </para>
/// <para>
/// There is no second connection string, no second database to provision and no in-memory mode: an
/// unconfigured deployment does not get a working queue, it gets a refused start-up, because a queue
/// that silently works in memory would make every durability claim in this package untrue.
/// </para>
/// <para>
/// The server is a hosted service, so it starts and stops with the application. That is what
/// "durable" means here: the workers are part of the process, and the work they were given outlives
/// them in the database.
/// </para>
/// </remarks>
public static class OperationQueueServiceCollectionExtensions
{
    public static IServiceCollection AddChronicleafOperationQueue(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new OperationQueueOptions();
        configuration.GetSection(OperationQueueOptions.SectionName).Bind(options);

        // The dashboard is a decision, not a default. Refusing to start is deliberately louder than
        // starting without it: an operator who asked for it should find out that this build has no
        // such surface rather than discover later that nothing was listening. This check stays ahead
        // of every registration, so a refused start-up leaves no half-built container behind.
        if (options.DashboardEnabled)
        {
            throw new InvalidOperationException(
                $"{OperationQueueOptions.SectionName}:DashboardEnabled cannot be turned on. " +
                "This build does not serve a Hangfire dashboard. Operation state is available through " +
                "the owner-scoped /api/operations routes.");
        }

        if (string.IsNullOrWhiteSpace(options.SchemaName) ||
            !string.Equals(options.SchemaName, HangfireSchema.Default, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{OperationQueueOptions.SectionName}:SchemaName must be '{HangfireSchema.Default}'. " +
                "The queue shares the application database, and its tables are expected in that schema.");
        }

        // An unconfigured deployment is given no queue at all, rather than one that works in memory.
        // The persistence package's sentinel is the reason that is safe to do: the context is already
        // pointed at an unreachable host, so an operation read or write fails loudly exactly as every
        // other store fails, and readiness reports the missing connection string. What is refused is a
        // queue that would appear to work and then lose everything on restart.
        var configured = configuration[ChronicleafConnection.SettingName];
        var connectionString = configured?.Trim() ?? string.Empty;
        var queueConfigured = connectionString.Length > 0;

        services.AddSingleton(options);
        services.Configure<OperationDispatchOptions>(
            configuration.GetSection(OperationDispatchOptions.SectionName));

        // The operation surface itself is always registered, so the routes exist and answer honestly
        // - a read or an enqueue against an unconfigured database fails the same loud way every other
        // store fails, rather than a route that mysteriously does not exist.
        services.AddScoped<IOperationStore, PostgresOperationStore>();
        services.AddScoped<IOperationQuery, PostgresOperationQuery>();
        services.AddScoped<IOperationService, OperationService>();
        services.AddScoped<IOperationHandlerRegistry, OperationHandlerRegistry>();
        services.AddScoped<OperationWorker>();
        services.AddScoped<IOutboxDispatchStamper, PostgresOutboxDispatchStamper>();
        services.AddHostedService<OperationDispatchLoop>();

        // The platform clock is bound to the application's own injected TimeProvider, so the
        // dispatcher, the executor and the operation layer all read one instant. In production that
        // provider is TimeProvider.System, which is exactly what SystemClock - the implementation the
        // platform would register by default - reads; under a test it is the test's clock, which is
        // what makes a dispatched payload's telemetry deterministic. Registered before the adapter,
        // whose TryAdd would not have replaced it anyway but whose ordering is worth being explicit
        // about.
        services.AddSingleton<IClock>(provider =>
            new FixedClock(() => provider.GetRequiredService<TimeProvider>().GetUtcNow()));

        if (queueConfigured)
        {
            AddPlatformJobs(services, connectionString, options);

            // The outbox's enqueue is the platform's own dispatcher, registered by the adapter above.
            // The retry path is the one job row the platform contract cannot express on its own, and
            // it goes through the same payload, the same executor and the same engine.
            services.AddScoped<IOperationRetryScheduler, DelayedOperationRetryScheduler>();
            services.AddScoped<IJobPayloadHandler, OperationJobPayloadHandler>();
            services.AddScoped<IOutboxEventHandler, OperationOutboxHandler>();
        }
        else
        {
            // The dispatcher's contract stays resolvable even with no engine behind it, so the
            // container is valid and every other registration can still be validated. Asking it to
            // schedule is a configuration error, and it says so rather than dropping the work.
            services.AddScoped<IJobDispatcher, UnavailableJobDispatcher>();
            services.AddScoped<IOperationRetryScheduler, UnavailableOperationRetryScheduler>();
        }

        services.AddSingleton(new OperationQueueAvailability(queueConfigured));

        return services;
    }

    /// <summary>
    /// Hands the queue to the platform adapter: the engine, the storage, the server and the dispatcher
    /// are all the adapter's, and Chronicleaf only chooses the backend and answers its questions.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The storage is PostgreSQL in the committed <c>hangfire</c> schema, reached with the connection
    /// string the rest of the application uses - one string, one database, no new configuration key and
    /// no secret in configuration - plus this build's own bound on how many connections the queue's own
    /// pool may hold. The adapter's own PostgreSQL options already default to that schema and to
    /// creating the engine's tables on first use, which is exactly what the committed migration
    /// prepares, so the engine's DDL still arrives with the engine rather than as a hand-copied
    /// snapshot. The storage is built inside the adapter's configuration callback, so an unreachable
    /// database is reported by readiness instead of by a start-up crash.
    /// </para>
    /// <para>
    /// The storage is built here rather than inside the adapter, so the queue's tables are created by
    /// the engine and not copied into a migration, and so the pool is bounded and disposable. Building
    /// it here also means the engine's own first connection is attempted while the container is being
    /// configured rather than while the host is starting; when the database is unreachable the engine
    /// degrades rather than throws, and that wait is the same one this build already paid when the
    /// workers were constructed at host start.
    /// </para>
    /// <para>
    /// The intervals come from Chronicleaf's own section and are clamped to the bounds the adapter
    /// validates at registration (one second to ten minutes). A request below the floor is honoured as
    /// far as the adapter allows rather than refused: the floor is the adapter's, not this build's, and
    /// the queue's durability does not depend on which side of it a polling interval lands.
    /// </para>
    /// <para>
    /// Two of this build's former server settings are the engine's now: its identity in its own tables
    /// and how long it waits for in-flight attempts while stopping. The adapter's registration exposes
    /// the worker count, the queues and the two intervals, and gives the application no way to reach
    /// the rest of the server's options - registering them afterwards was measured to have no effect on
    /// the server it starts. Neither value is ever shown to a client, and the second one is a courtesy
    /// rather than a guarantee: an attempt that does not finish in time is recovered by its lease
    /// expiry, exactly as before.
    /// </para>
    /// </remarks>
    private static void AddPlatformJobs(IServiceCollection services, string connectionString, OperationQueueOptions options)
    {
        // The storage is Chronicleaf's and the adapter is handed it. Two things are being kept here
        // rather than delegated, and both are things the adapter's own construction cannot express:
        // the schema this build's committed migration prepared, and the engine's tables inside it -
        // the forward migration creates the schema and the engine creates what goes in it, so a
        // Hangfire upgrade brings its own DDL with it instead of inheriting a snapshot this repository
        // would have to keep correct by hand. The connection pool is the third, and it is not a
        // preference: the engine's own connection source is never disposed, so a container that is
        // rebuilt - by a test host, by a deployment restart - would leave its connections open for the
        // life of the process. ChronicleafConnectionFactory exists to close that, and was measured
        // doing so against a shared server's own connection budget.
        var connectionFactory = new ChronicleafConnectionFactory(connectionString, MaxPoolSize);
        var storage = new PostgreSqlStorage(connectionFactory, new PostgreSqlStorageOptions
        {
            SchemaName = options.SchemaName,
            PrepareSchemaIfNecessary = true,
        });

        // Registered as a service in its own right, so the container owns the pool's lifetime: a host
        // that goes away takes its connections with it. Registering it only as a constructor argument
        // would hide it from disposal and leak the queue's connections.
        services.AddSingleton(connectionFactory);

        // ...and resolved once, so it is genuinely container-owned. Nothing in the engine's own wiring
        // asks for the connection source by name, so a registration nothing resolves is a registration
        // nothing disposes, and the pool would outlive the host that opened it. The closing service is
        // registered before the adapter's server, so it is also the last to stop: the workers are down
        // before their connections go. Disposing a data source twice is a no-op, so the container
        // disposing it afterwards as well is harmless.
        services.AddHostedService(provider =>
            new OperationQueueConnectionLifetime(provider.GetRequiredService<ChronicleafConnectionFactory>()));

        services.AddPlatformHangfireJobs(
            adapter =>
            {
                adapter.WorkerCount = Math.Max(1, options.WorkerCount);
                adapter.SchedulePollingInterval = Clamp(options.SchedulePollingInterval);
                adapter.HeartbeatInterval = Clamp(options.HeartbeatInterval);

                // The dashboard is left disabled and is never mapped. The adapter maps nothing unless
                // this is true, and nothing in this build sets it: a dashboard is a full application
                // over the queue's own rows, and the access boundary's public allow-list is the wrong
                // place to secure it, because a dashboard is not an API route.
            },
            storage);
    }

    /// <summary>The interval bounds the adapter validates, and this build honours them rather than refusing.</summary>
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumInterval = TimeSpan.FromMinutes(10);

    /// <summary>The most connections the queue's own pool will ever hold open.</summary>
    private const int MaxPoolSize = 20;

    private static TimeSpan Clamp(TimeSpan interval) =>
        interval < MinimumInterval ? MinimumInterval
        : interval > MaximumInterval ? MaximumInterval
        : interval;

    /// <summary>
    /// Closes the queue's connection pool when the host stops, after the workers that used it.
    /// </summary>
    /// <remarks>
    /// This exists because nothing else can. The engine's storage is not disposable and its own
    /// connection source is not either, so a host that goes away would leave the queue's pooled
    /// connections open for the life of the process - which, against one shared PostgreSQL server,
    /// spends a connection budget that belongs to everybody. It starts nothing; it only owns an end.
    /// </remarks>
    private sealed class OperationQueueConnectionLifetime(ChronicleafConnectionFactory factory) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            factory.Dispose();
            return Task.CompletedTask;
        }
    }
}

/// <summary>The one schema name the queue is allowed to use.</summary>
public static class HangfireSchema
{
    public const string Default = "hangfire";
}
