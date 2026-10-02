using Chronicleaf.Application.Operations;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Readiness;
using Chronicleaf.Infrastructure.Persistence;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Chronicleaf.Infrastructure.Operations;

/// <summary>
/// Registers durable operation execution (BACKGROUND-JOBS-001, BACKGROUND-JOBS-002).
/// </summary>
/// <remarks>
/// <para>
/// This is the only place Hangfire is configured, and it configures it against the same PostgreSQL
/// connection string the application already uses. There is no second connection string, no second
/// database to provision and no in-memory mode: an unconfigured deployment does not get a working
/// queue, it gets a refused start-up, because a queue that silently works in memory would make every
/// durability claim in this package untrue.
/// </para>
/// <para>
/// The Hangfire server is registered as an <see cref="IHostedService"/>, so it starts and stops with
/// the application. That is what "durable" means here: the workers are part of the process, and the
/// work they were given outlives them in the database.
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
        // such surface rather than discover later that nothing was listening.
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

        if (queueConfigured)
        {
            AddHangfire(services, connectionString, options);

            services.AddScoped<IJobDispatcher, HangfireJobDispatcher>();
            services.AddScoped<IOperationDispatcher, HangfireOperationDispatcher>();
            services.AddScoped<IOutboxEventHandler, OperationOutboxHandler>();
            services.AddHostedService<OperationQueueServer>();
        }
        else
        {
            // The dispatcher's contract stays resolvable even with no engine behind it, so the
            // container is valid and every other registration can still be validated. Asking it to
            // schedule is a configuration error, and it says so rather than dropping the work.
            services.AddScoped<IJobDispatcher, UnavailableJobDispatcher>();
            services.AddScoped<IOperationDispatcher, HangfireOperationDispatcher>();
        }

        services.AddSingleton(new OperationQueueAvailability(queueConfigured));

        return services;
    }

    /// <summary>
    /// Wires the engine itself: one storage, one client, one job activator, one server.
    /// </summary>
    /// <remarks>
    /// These are constructed explicitly rather than through a hosting extension so that the only
    /// engine packages this project takes are the engine and its PostgreSQL storage. The ASP.NET Core
    /// integration - which is where the dashboard's route mapping lives - is not a dependency of this
    /// build at all, so the dashboard cannot be served by accident, by a setting, or by a later
    /// package's import.
    /// </remarks>
    private static void AddHangfire(IServiceCollection services, string connectionString, OperationQueueOptions options)
    {
        // The factory is registered first and as a singleton of its own type, so the container
        // disposes it - and with it the pool - when the host is torn down. Registering it only as a
        // constructor argument would hide it from disposal and leak the queue's connections.
        services.AddSingleton(_ => new ChronicleafConnectionFactory(connectionString, MaxPoolSize));

        var storageOptions = new PostgreSqlStorageOptions
        {
            SchemaName = options.SchemaName,

            // The queue's own tables are created by the engine on first use. The forward migration
            // creates the schema; the engine creates what goes inside it, so a Hangfire upgrade brings
            // its own DDL with it instead of inheriting a snapshot this repository would then have to
            // keep correct by hand.
            PrepareSchemaIfNecessary = true,
        };

        services.AddSingleton<JobStorage>(provider => new PostgreSqlStorage(
            provider.GetRequiredService<ChronicleafConnectionFactory>(),
            storageOptions));

        services.AddSingleton<IBackgroundJobClient>(provider =>
            new BackgroundJobClient(provider.GetRequiredService<JobStorage>()));

        services.AddSingleton<JobActivator, ChronicleafJobActivator>();
        services.AddSingleton(provider => new ExecuteOperationJob(
            provider.GetRequiredService<IServiceScopeFactory>()));
    }

    /// <summary>The most connections the queue's own pool will ever hold open.</summary>
    private const int MaxPoolSize = 20;
}

/// <summary>The one schema name the queue is allowed to use.</summary>
public static class HangfireSchema
{
    public const string Default = "hangfire";
}
