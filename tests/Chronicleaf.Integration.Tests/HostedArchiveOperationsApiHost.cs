using Chronicleaf.Application.Access;
using Chronicleaf.Application.Media;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Secrets;
using Chronicleaf.Infrastructure.Operations;
using Chronicleaf.Infrastructure.Schedules;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// Hosts the real application with the real archive, journey, schedule and media services
/// against a real disposable PostgreSQL, with nothing faked.
/// </summary>
/// <remarks>
/// <para>
/// The host is the delivered <c>WebApplicationFactory</c> pattern used by every other package
/// here: the connection string arrives through the same configuration key production reads, the
/// access configuration through its own keys, the media bytes land in a real temporary
/// directory, and secrets are really sealed over a real operator key file. The committed
/// migrations created the tables. There is no substituted store, no substituted service and no
/// substituted clock: scenarios that need time to pass move the clock this host was started
/// with.
/// </para>
/// <para>
/// The automated dispatch loop and the minute tick are both off. Nothing this package does
/// enqueues work, so a scenario never waits on a worker; the settings are off so a test that
/// arms a schedule for the suspension proof does not race a background tick for the same row.
/// </para>
/// </remarks>
public sealed class HostedArchiveOperationsApiHost : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly TimeProvider _clock;
    private readonly string _mediaRoot;
    private readonly LogCapture _logs = new();

    private HostedArchiveOperationsApiHost(
        string connectionString,
        TimeProvider clock,
        string mediaRoot,
        string keyFile)
    {
        _connectionString = connectionString;
        _clock = clock;
        _mediaRoot = mediaRoot;
        KeyFile = keyFile;
    }

    /// <summary>The connection string this host was started with, for the assertions that read the database.</summary>
    public string Database => _connectionString;

    /// <summary>The operator key file this run created, over which secrets are really sealed.</summary>
    public string KeyFile { get; }

    /// <summary>The temporary media root this host writes, for the assertions that check bytes.</summary>
    public string MediaRootPath => _mediaRoot;

    /// <summary>What the server logged, for the assertions that a refusal said why.</summary>
    public LogCapture Logs => _logs;

    public static HostedArchiveOperationsApiHost Start(
        string connectionString,
        TimeProvider clock,
        string mediaRoot,
        string keyFile) =>
        new(connectionString, clock, mediaRoot, keyFile);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(ChronicleafConnection.SettingName, _connectionString);
        builder.UseSetting(AccessSettings.AccessTokenHashSettingName, OwnerClient.AccessTokenHash);
        builder.UseSetting(AccessSettings.PublicOriginSettingName, OwnerClient.PublicOrigin);
        builder.UseSetting(MediaSettings.MediaRootSettingName, _mediaRoot);
        builder.UseEnvironment("Production");

        // Nothing this package does enqueues work, and the scenarios drive the operator purge by
        // hand, so both loops stay off rather than racing a scenario for its own rows.
        builder.UseSetting($"{OperationQueueOptions.SectionName}:Enabled", "false");
        builder.UseSetting($"{ProjectSyncTickerOptions.SectionName}:Enabled", "false");

        // The real operator key file, so a stored credential is really sealed and the
        // archive-delete proof that credentials survive is about real ciphertext.
        builder.UseSetting(SecretVaultSettings.EncryptionKeyFileSettingName, KeyFile);

        builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(_logs);
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton(_clock);
        });
    }
}
