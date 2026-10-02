using Chronicleaf.Application.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// Hosts the real <c>Chronicleaf.Api</c> application against a real disposable PostgreSQL.
/// </summary>
/// <remarks>
/// The connection string arrives through the same configuration key production reads, so the
/// readiness and persistence behaviour under test is the production code path. The clock is
/// replaced with a fixed UTC instant so stored timestamps are deterministic; nothing else about
/// the host is substituted.
/// </remarks>
public sealed class ChronicleafApiHost : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly FixedTimeProvider _clock;

    private ChronicleafApiHost(string connectionString, DateTimeOffset clock)
    {
        _connectionString = connectionString;
        _clock = new FixedTimeProvider(clock);
    }

    public static ChronicleafApiHost Start(string connectionString, DateTimeOffset clock) =>
        new(connectionString, clock);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(ChronicleafConnection.SettingName, _connectionString);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(_clock);
        });
    }
}

/// <summary>A UTC clock frozen at one instant, so every stored timestamp is deterministic.</summary>
public sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public DateTimeOffset UtcNow { get; } = utcNow;

    public override DateTimeOffset GetUtcNow() => UtcNow;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}
