using Chronicleaf.Application.Access;
using Chronicleaf.Application.GitHub;
using Chronicleaf.Application.Operations;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Secrets;
using Chronicleaf.GitHub.App;
using Chronicleaf.Infrastructure.GitHub;
using Chronicleaf.Infrastructure.Operations;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// Hosts the real application with the real queue running, against a real disposable PostgreSQL,
/// with a fake GitHub in place of GitHub and nothing else faked, for the App connection package.
/// </summary>
/// <remarks>
/// <para>
/// The same shape as <see cref="GithubEvidenceApiHost"/>: the connection string arrives through
/// the same configuration key production reads, the Hangfire storage, the server, the workers,
/// the outbox dispatcher, the stores, the vault, the evidence append and the routes are all the
/// production ones, and the only substitution is the transport.
/// </para>
/// <para>
/// The App options travel as a mutable holder rather than configuration, because the credential
/// references they name are decided by the vault after the host starts. The holder is the same
/// <see cref="GitHubAppOptions"/> the production service reads, so a scenario exercises the real
/// option checks - an empty holder is the unconfigured server - and only the source of the values
/// differs.
/// </para>
/// </remarks>
public sealed class GithubAppConnectionApiHost : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly TimeProvider _clock;
    private readonly FakeGitHubHandler _github;
    private readonly TestGitHubAppOptions _appOptions;
    private readonly LogCapture _logs = new();

    private GithubAppConnectionApiHost(
        string connectionString,
        TimeProvider clock,
        FakeGitHubHandler github,
        TestGitHubAppOptions appOptions,
        string? keyFile)
    {
        _connectionString = connectionString;
        _clock = clock;
        _github = github;
        _appOptions = appOptions;
        KeyFile = keyFile;
    }

    /// <summary>The connection string this host was started with, for the assertions that read the database.</summary>
    public string Database => _connectionString;

    /// <summary>The operator key file this run created, or <c>null</c> when this host has none.</summary>
    public string? KeyFile { get; }

    /// <summary>The transport the application is really using, for the assertions about what it read.</summary>
    public FakeGitHubHandler GitHub => _github;

    /// <summary>What the server logged, for the assertions that a refusal said why.</summary>
    public LogCapture Logs => _logs;

    public static GithubAppConnectionApiHost Start(
        string connectionString,
        TimeProvider clock,
        FakeGitHubHandler github,
        TestGitHubAppOptions appOptions,
        string? keyFile = null) =>
        new(connectionString, clock, github, appOptions, keyFile);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(ChronicleafConnection.SettingName, _connectionString);
        builder.UseSetting(AccessSettings.AccessTokenHashSettingName, OwnerClient.AccessTokenHash);
        builder.UseSetting(AccessSettings.PublicOriginSettingName, OwnerClient.PublicOrigin);
        builder.UseEnvironment("Production");

        // The dispatch loop runs only when a scenario asks it to, exactly as the evidence host
        // does it: the loop is the seam between "the request committed" and "the queue knows".
        builder.UseSetting($"{OperationQueueOptions.SectionName}:Enabled", "false");

        // A poll interval, not a behaviour: the queue, the workers and the claiming are production's.
        builder.UseSetting($"{OperationQueueOptions.SectionName}:SchedulePollingInterval", "00:00:00.200");
        builder.UseSetting($"{OperationQueueOptions.SectionName}:HeartbeatInterval", "00:00:05");

        if (KeyFile is not null)
        {
            builder.UseSetting(SecretVaultSettings.EncryptionKeyFileSettingName, KeyFile);
        }

        builder.ConfigureLogging(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddProvider(_logs);
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton(_clock);

            // The one substitution: the transport, and nothing else. The base address is still the
            // pinned host, so a scenario asserting where a request went is reading the real routing
            // rather than a host a test chose.
            services.RemoveAll<HttpClient>();
            services.AddSingleton(_ => new HttpClient(_github, disposeHandler: false)
            {
                BaseAddress = new Uri(GitHubTransport.BaseAddress),
                Timeout = GitHubTransport.Timeout,
            });

            // The App options as the holder the scenario fills in after the vault decides the
            // credential ids. The production registration is removed the same way the production
            // transport above is: what runs is the production service reading this object.
            services.RemoveAll<GitHubAppOptions>();
            services.AddSingleton<GitHubAppOptions>(_appOptions);
        });
    }

    /// <summary>
    /// Runs one real outbox dispatch pass: the delivered dispatcher hands each pending event to the
    /// registered handler, which enqueues a real job through the real <c>IJobDispatcher</c>.
    /// </summary>
    public async Task<IReadOnlyList<OutboxDispatchResult>> DispatchAsync(CancellationToken cancellationToken = default)
    {
        using var scope = Services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>();
        var stamper = scope.ServiceProvider.GetRequiredService<IOutboxDispatchStamper>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        var results = await dispatcher.DispatchPendingAsync(100, cancellationToken);

        foreach (var result in results)
        {
            if (result.Outcome == OutboxDispatchOutcome.Handled)
            {
                await stamper.TryMarkDispatchedAsync(result.EventId, clock.GetUtcNow(), cancellationToken);
            }
        }

        return results;
    }
}

/// <summary>
/// The App options a scenario fills in after the vault decides the credential ids.
/// </summary>
/// <remarks>
/// A mutable holder rather than configuration for one reason: the PEM and webhook references it
/// names do not exist until the scenario stores them through the host's own vault. An empty
/// holder is the unconfigured server, which the not-configured scenario asserts on purpose.
/// </remarks>
public sealed class TestGitHubAppOptions : GitHubAppOptions
{
}
