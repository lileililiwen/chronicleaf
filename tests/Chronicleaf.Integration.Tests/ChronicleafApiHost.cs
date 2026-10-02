using Chronicleaf.Application.Access;
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
/// The connection string arrives through the same configuration key production reads, and the
/// access token hash and public origin arrive through theirs, so the readiness, persistence and
/// access behaviour under test is the production code path. The clock is replaced with a fixed
/// UTC instant so stored timestamps and every session deadline are deterministic; nothing else
/// about the host is substituted.
/// </remarks>
public sealed class ChronicleafApiHost : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly TimeProvider _clock;
    private readonly string? _accessTokenHash;
    private readonly string? _publicOrigin;
    private readonly string? _environment;

    private ChronicleafApiHost(
        string connectionString,
        TimeProvider clock,
        string? accessTokenHash,
        string? publicOrigin,
        string? environment)
    {
        _connectionString = connectionString;
        _clock = clock;
        _accessTokenHash = accessTokenHash;
        _publicOrigin = publicOrigin;
        _environment = environment;
    }

    /// <summary>
    /// Starts a host with the persistence configuration only. The access boundary is left without
    /// an access token hash, which is the documented Development behaviour: the process starts and
    /// no login can ever succeed.
    /// </summary>
    public static ChronicleafApiHost Start(string connectionString, DateTimeOffset clock) =>
        new(connectionString, new FixedTimeProvider(clock), accessTokenHash: null, publicOrigin: null, environment: null);

    /// <summary>
    /// Starts a host that enforces the access boundary with a real operator access token hash and
    /// a real public origin, the way a configured deployment does.
    /// </summary>
    public static ChronicleafApiHost StartWithAccess(
        string connectionString,
        DateTimeOffset clock,
        string accessTokenHash,
        string publicOrigin) =>
        new(connectionString, new FixedTimeProvider(clock), accessTokenHash, publicOrigin, environment: null);

    /// <summary>
    /// Starts an access-enforcing host on a clock the test can move, so idle and absolute expiry
    /// are proven by advancing time rather than by waiting for it.
    /// </summary>
    public static ChronicleafApiHost StartWithAccess(
        string connectionString,
        TimeProvider clock,
        string accessTokenHash,
        string publicOrigin) =>
        new(connectionString, clock, accessTokenHash, publicOrigin, environment: null);

    /// <summary>Starts a host in an explicit environment, for the start-up configuration rules.</summary>
    public static ChronicleafApiHost StartInEnvironment(
        string connectionString,
        DateTimeOffset clock,
        string? accessTokenHash,
        string environment) =>
        new(connectionString, new FixedTimeProvider(clock), accessTokenHash, publicOrigin: null, environment);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(ChronicleafConnection.SettingName, _connectionString);

        if (_accessTokenHash is not null)
        {
            builder.UseSetting(AccessSettings.AccessTokenHashSettingName, _accessTokenHash);
        }

        if (_publicOrigin is not null)
        {
            builder.UseSetting(AccessSettings.PublicOriginSettingName, _publicOrigin);
        }

        if (_environment is not null)
        {
            builder.UseEnvironment(_environment);
        }

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton(_clock);
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
