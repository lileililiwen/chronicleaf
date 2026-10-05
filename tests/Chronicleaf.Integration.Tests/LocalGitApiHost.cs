using Chronicleaf.Application.Access;
using Chronicleaf.Application.Git;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Operations;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Secrets;
using Chronicleaf.Infrastructure.Git;
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
/// Hosts the real application with the real queue running, against a real disposable PostgreSQL, over
/// <b>real temporary Git repositories on a real filesystem</b> and a real <c>git</c> process.
/// </summary>
/// <remarks>
/// <para>
/// The host is the delivered <c>WebApplicationFactory</c> pattern: the connection string arrives through
/// the same configuration key production reads, the registered local roots through the section the operator
/// configures. The Hangfire storage, the server, the workers, the outbox dispatcher, the stores, the
/// evidence append, the routes and <b>the Git command runner</b> are all the production ones.
/// </para>
/// <para>
/// <b>Nothing is substituted at all, which is the point of this package.</b> The claim under test is that
/// Chronicleaf reads committed revisions off an operator's disk through Git and changes nothing while doing
/// it, and that claim is about Git, the filesystem and this server's own handling of them. A scripted
/// transport would leave every one of those three out of the picture, and a test that proved a stub reads
/// only the arguments it was given proves nothing about a process.
/// </para>
/// <para>
/// The one thing a scenario may still replace is the command runner, and only so it can move a repository's
/// head <em>during</em> a collection - the race the boundary scenario is about, which cannot be scheduled
/// from outside otherwise. That seam is the runner, which is this package's own declared boundary for the
/// process, and everything above it - the argument vector, the caps, the exclusions, the final head
/// re-check and the supersede classification - is production code running a real Git.
/// </para>
/// <para>
/// The automated dispatch loop is off and the polling is fast, exactly as the GitHub host does it, so a
/// scenario can place the collection on a chosen side of the seam between "the request committed" and "the
/// queue knows".
/// </para>
/// </remarks>
public sealed class LocalGitApiHost : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly TimeProvider _clock;
    private readonly LogCapture _logs = new();
    private readonly IReadOnlyList<LocalRepositoryRoot> _roots;
    private readonly LocalGitDeploymentMode _mode;
    private readonly IGitCommandRunner? _runner;

    private LocalGitApiHost(
        string connectionString,
        TimeProvider clock,
        IReadOnlyList<LocalRepositoryRoot> roots,
        LocalGitDeploymentMode mode,
        IGitCommandRunner? runner)
    {
        _connectionString = connectionString;
        _clock = clock;
        _roots = roots;
        _mode = mode;
        _runner = runner;
    }

    /// <summary>The connection string this host was started with, for the assertions that read the database.</summary>
    public string Database => _connectionString;

    /// <summary>What the server logged, for the assertions that a refusal said why.</summary>
    public LogCapture Logs => _logs;

    public static LocalGitApiHost Start(
        string connectionString,
        TimeProvider clock,
        IReadOnlyList<LocalRepositoryRoot> roots,
        LocalGitDeploymentMode mode = LocalGitDeploymentMode.Community,
        IGitCommandRunner? runner = null) =>
        new(connectionString, clock, roots, mode, runner);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(ChronicleafConnection.SettingName, _connectionString);
        builder.UseSetting(AccessSettings.AccessTokenHashSettingName, OwnerClient.AccessTokenHash);
        builder.UseSetting(AccessSettings.PublicOriginSettingName, OwnerClient.PublicOrigin);
        builder.UseEnvironment("Production");

        // The dispatch loop runs only when a scenario asks it to, so a collection can be staged either side of
        // the seam between "the request committed" and "the queue knows".
        builder.UseSetting($"{OperationQueueOptions.SectionName}:Enabled", "false");
        builder.UseSetting($"{OperationQueueOptions.SectionName}:SchedulePollingInterval", "00:00:00.200");
        builder.UseSetting($"{OperationQueueOptions.SectionName}:HeartbeatInterval", "00:00:05");

        // The registered roots, through the section an operator configures. This is the only way a directory
        // enters the table, and a scenario registering a temporary one here is the same mechanism an operator
        // uses - not a test-only back door.
        builder.UseSetting(
            $"{LocalGitEvidenceServiceCollectionExtensions.CatalogSectionName}"
            + $":{ConfigurationLocalRepositoryCatalog.DeploymentModeKey}",
            _mode.ToString());

        for (var index = 0; index < _roots.Count; index++)
        {
            builder.UseSetting(
                $"{LocalGitEvidenceServiceCollectionExtensions.CatalogSectionName}:Roots:{index}"
                + $":{ConfigurationLocalRepositoryCatalog.RootAliasKey}",
                _roots[index].Alias);
            builder.UseSetting(
                $"{LocalGitEvidenceServiceCollectionExtensions.CatalogSectionName}:Roots:{index}"
                + $":{ConfigurationLocalRepositoryCatalog.RootPathKey}",
                _roots[index].Path);
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

            if (_runner is not null)
            {
                // The one substitution this host allows, and only for a scenario that has to move a
                // repository under a running collection. Everything above the runner is production's.
                services.RemoveAll<IGitCommandRunner>();
                services.AddSingleton(_runner);
            }
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
/// A real runner that delegates to the production one and can move a repository's head between two of its
/// commands.
/// </summary>
/// <remarks>
/// <para>
/// <b>It delegates rather than replaces.</b> Every command it is given runs as the production runner would
/// run it - same argument vector, same sanitised environment, same caps - and this class only watches for
/// the moment a scenario named. So a scenario that uses it is still reading a real repository with a real
/// <c>git</c>, and the only thing it fabricates is the timing of a commit, which is the one thing a test
/// cannot otherwise control.
/// </para>
/// <para>
/// The trigger is a subcommand and an occurrence count rather than a callback the scenario supplies, so the
/// seam cannot be used to alter what a command is. A scenario that wanted to make Git say something else
/// would have to replace the runner outright, and that would show up as a different type in the host.
/// </para>
/// </remarks>
public sealed class HeadMovingGitRunner(IGitCommandRunner inner) : IGitCommandRunner
{
    private int _seen;

    /// <summary>How many commands ran through this runner, whatever they were.</summary>
    public int CommandsRun => Volatile.Read(ref _seen);

    /// <summary>The subcommand whose run triggers the move, and how many of them to let through first.</summary>
    public string? TriggerOnSubcommand { get; init; }

    /// <summary>How many calls of the trigger to allow before the move happens. Zero is the first one.</summary>
    public int TriggerAfterCalls { get; init; }

    /// <summary>What to do at the trigger. The only way a scenario can affect this class.</summary>
    public Func<Task>? OnTrigger { get; init; }

    public async Task<GitCommandResult> RunAsync(
        GitCommandRequest request,
        TimeSpan timeout,
        int maxOutputBytes,
        CancellationToken cancellationToken = default)
    {
        var count = Interlocked.Increment(ref _seen);

        if (OnTrigger is not null &&
            TriggerOnSubcommand is not null &&
            string.Equals(request.Subcommand, TriggerOnSubcommand, StringComparison.Ordinal) &&
            count > TriggerAfterCalls)
        {
            await OnTrigger().ConfigureAwait(false);
        }

        return await inner.RunAsync(request, timeout, maxOutputBytes, cancellationToken).ConfigureAwait(false);
    }
}
