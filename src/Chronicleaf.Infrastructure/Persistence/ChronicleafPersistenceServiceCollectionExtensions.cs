using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Readiness;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Infrastructure.Persistence;

/// <summary>
/// Registers the PostgreSQL implementation of the Community data foundation.
/// </summary>
/// <remarks>
/// Registration never migrates and never substitutes a store: the schema is applied only by the
/// explicit operator command, and a missing or unusable database shows up at
/// <c>GET /api/ready</c> rather than as a silent fallback. When no connection string is
/// configured the context is still registered against a deliberately unreachable sentinel so
/// that any accidental write fails loudly instead of succeeding in memory.
/// </remarks>
public static class ChronicleafPersistenceServiceCollectionExtensions
{
    /// <summary>
    /// A syntactically valid connection string that points at a reserved, non-resolvable host.
    /// It exists only so an unconfigured runtime fails on use instead of degrading.
    /// </summary>
    internal const string UnconfiguredSentinel =
        "Host=chronicleaf-unconfigured.invalid;Database=chronicleaf;Username=chronicleaf-unconfigured";

    public static IServiceCollection AddChronicleafPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = ReadConnectionString(configuration);
        PostgresConnectionValidator.EnsureUsable(connectionString);

        services.AddDbContext<ChronicleafDbContext>(options =>
        {
            options.UseNpgsql(connectionString ?? UnconfiguredSentinel, npgsql =>
            {
                // Readiness must answer quickly; it applies its own short probe budget on top.
                npgsql.CommandTimeout((int)PostgresReadinessProbe.ProbeTimeout.TotalSeconds);
            });
        });

        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IInstallationStore, InstallationStore>();
        services.AddScoped<IAuditLog, AuditLog>();
        // The factory rather than the open generic, so a staged outbox row is stamped by the same
        // TimeProvider the delivered rows are. The store's own parameter is optional, which is what
        // keeps the delivered persistence scenarios' direct construction compiling unchanged.
        services.AddScoped<IOutboxStore>(provider => new OutboxStore(
            provider.GetRequiredService<ChronicleafDbContext>(),
            provider.GetRequiredService<TimeProvider>()));
        services.AddScoped<IOutboxDispatcher, OutboxDispatcher>();

        services.AddSingleton(new ChronicleafDatabaseOptions(connectionString));

        // The probe reads the connection state captured at start-up, so readiness reflects what
        // this process was actually configured with rather than what a request happened to pass.
        services.AddScoped<IReadinessProbe>(provider => new PostgresReadinessProbe(
            provider.GetRequiredService<ChronicleafDbContext>(),
            provider.GetRequiredService<ChronicleafDatabaseOptions>().ConnectionString));

        return services;
    }

    private static string? ReadConnectionString(IConfiguration configuration)
    {
        var value = configuration[ChronicleafConnection.SettingName];
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

/// <summary>The connection state of the host, captured once at start-up.</summary>
/// <param name="ConnectionString">The configured value, or <c>null</c> when none is configured.</param>
public sealed record ChronicleafDatabaseOptions(string? ConnectionString)
{
    /// <summary>True when a usable connection string was configured for this process.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ConnectionString);
}
