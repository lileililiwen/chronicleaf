using Chronicleaf.Application.Access;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Secrets;
using Chronicleaf.Infrastructure.GitHub;
using Chronicleaf.Infrastructure.Operations;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Schedules;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// Hosts the real application with the real queue, the real outbox dispatcher and the real schedule
/// tick, against a real disposable PostgreSQL, with a fake GitHub in place of GitHub and nothing else
/// faked.
/// </summary>
/// <remarks>
/// <para>
/// The host is the delivered <c>WebApplicationFactory</c> pattern used by every other package here:
/// the connection string arrives through the same configuration key production reads, the access
/// configuration through its own keys. The Hangfire storage, the server, the workers, the outbox
/// dispatcher, the schedule stores, the vault, the operation queue and the routes are all the
/// production ones, and the committed migrations are what created the tables.
/// </para>
/// <para>
/// <b>The only substitution is the transport, and it is the same seam every other package here
/// substitutes.</b> The production registration builds one <see cref="HttpClient"/> over the pinned
/// handler; this host removes that client and adds one over the shared
/// <see cref="FakeGitHubHandler"/>. It does not substitute a store, a service or a clock, and does
/// not touch a host, a header, a timeout or a cap - so a scenario exercises this package's real tick,
/// real revision guard, real quota count and real status mapping, and only the bytes on the wire
/// belong to the test.
/// </para>
/// <para>
/// The automated dispatch loop and the minute tick are both off, exactly as the delivered candidate
/// host does it. A scenario that needs them runs <see cref="DispatchAsync"/> and
/// <see cref="TickAsync"/>, which are the loops' own bodies called by hand, so a firing can be placed
/// on a chosen side of the seam between "the tick ran" and "the poll finished".
/// </para>
/// </remarks>
public sealed class ScheduledProjectSyncApiHost : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly TimeProvider _clock;
    private readonly FakeGitHubHandler _github;
    private readonly LogCapture _logs = new();

    private ScheduledProjectSyncApiHost(
        string connectionString,
        TimeProvider clock,
        FakeGitHubHandler github,
        string keyFile)
    {
        _connectionString = connectionString;
        _clock = clock;
        _github = github;
        KeyFile = keyFile;
    }

    /// <summary>The connection string this host was started with, for the assertions that read the database.</summary>
    public string Database => _connectionString;

    /// <summary>The operator key file this run created, over which secrets are really sealed.</summary>
    public string KeyFile { get; }

    /// <summary>The transport the application is really using, for the assertions about what it read.</summary>
    public FakeGitHubHandler GitHub => _github;

    /// <summary>What the server logged, for the assertions that a refusal said why and a secret did not leak.</summary>
    public LogCapture Logs => _logs;

    public static ScheduledProjectSyncApiHost Start(
        string connectionString,
        TimeProvider clock,
        FakeGitHubHandler github,
        string keyFile) =>
        new(connectionString, clock, github, keyFile);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(ChronicleafConnection.SettingName, _connectionString);
        builder.UseSetting(AccessSettings.AccessTokenHashSettingName, OwnerClient.AccessTokenHash);
        builder.UseSetting(AccessSettings.PublicOriginSettingName, OwnerClient.PublicOrigin);
        builder.UseEnvironment("Production");

        // The dispatch loop runs only when a scenario asks it to, so work can be staged either side
        // of the seam between "the tick fired" and "the poll finished".
        builder.UseSetting($"{OperationQueueOptions.SectionName}:Enabled", "false");
        builder.UseSetting($"{OperationQueueOptions.SectionName}:SchedulePollingInterval", "00:00:00.200");
        builder.UseSetting($"{OperationQueueOptions.SectionName}:HeartbeatInterval", "00:00:05");

        // The minute tick runs only when a scenario asks it to, so a firing lands on the instant the
        // scenario names rather than on the loop's own minute.
        builder.UseSetting($"{ProjectSyncTickerOptions.SectionName}:Enabled", "false");

        // The real operator key file, so a secret is really sealed and really opened. A test that
        // simulated the vault would prove nothing about the claim that no plaintext is stored.
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

            // The one substitution: the transport, and nothing else. The base address is still the
            // pinned host, so a scenario asserting where a request went is reading the real routing.
            services.RemoveAll<HttpClient>();
            services.AddSingleton(_ => new HttpClient(_github, disposeHandler: false)
            {
                BaseAddress = new Uri(GitHubTransport.BaseAddress),
                Timeout = GitHubTransport.Timeout,
            });
        });
    }

    /// <summary>
    /// Runs one real tick pass at <paramref name="now"/>: the delivered ticker's own body called by
    /// hand, so a firing lands on the instant the scenario names.
    /// </summary>
    public async Task<Application.Schedules.ProjectSyncTickResult> TickAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        using var scope = Services.CreateScope();
        var ticker = scope.ServiceProvider.GetRequiredService<ProjectSyncTicker>();

        return await ticker.RunOnceAsync(now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one real outbox dispatch pass: the delivered dispatcher hands each pending event to the
    /// registered handler, and the scheduled poll is one of the jobs it can start.
    /// </summary>
    public async Task<IReadOnlyList<OutboxDispatchResult>> DispatchAsync(
        CancellationToken cancellationToken = default)
    {
        using var scope = Services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<IOutboxDispatcher>();
        var stamper = scope.ServiceProvider.GetRequiredService<IOutboxDispatchStamper>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        var results = await dispatcher.DispatchPendingAsync(100, cancellationToken).ConfigureAwait(false);

        foreach (var result in results)
        {
            if (result.Outcome == OutboxDispatchOutcome.Handled)
            {
                await stamper.TryMarkDispatchedAsync(result.EventId, clock.GetUtcNow(), cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return results;
    }
}
