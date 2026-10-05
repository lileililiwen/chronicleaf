using Chronicleaf.Application.Access;
using Chronicleaf.Application.Narrative;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Infrastructure.Operations;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// Hosts the real <c>Chronicleaf.Api</c> application with the real queue running, against a real
/// disposable PostgreSQL, with an optional scripted <see cref="INarrativeSummaryBuilder"/> in
/// place of the deterministic one (NARRATIVE-MEMORY-001/002).
/// </summary>
/// <remarks>
/// Everything except the summary builder is the production path: the real stores, the real
/// service, the real refresh handler on the real Hangfire server, the real routes, the real
/// access boundary and the real evidence, milestone and review contracts. The builder is the one
/// substitution, and it is the only seam the failure scenario needs: a builder that throws is how
/// "regeneration fails" is produced honestly, and the scenario then asserts what Chronicleaf does
/// <em>with</em> that failure - the prior summary kept and marked stale.
/// <para>
/// The automated dispatch loop is off and the polling is fast, on the same terms as the delivered
/// background-jobs host: the loop is the seam between "the request committed" and "the queue
/// knows", and a scenario has to be able to place a refresh on a chosen side of it.
/// </para>
/// </remarks>
public sealed class NarrativeMemoryApiHost : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly TimeProvider _clock;
    private readonly INarrativeSummaryBuilder? _builder;

    private NarrativeMemoryApiHost(string connectionString, TimeProvider clock, INarrativeSummaryBuilder? builder)
    {
        _connectionString = connectionString;
        _clock = clock;
        _builder = builder;
    }

    /// <summary>The connection string this host was started with, for the assertions that read the database.</summary>
    public string Database => _connectionString;

    /// <summary>Starts a host with the production deterministic summary builder.</summary>
    public static NarrativeMemoryApiHost Start(string connectionString, TimeProvider clock) =>
        new(connectionString, clock, builder: null);

    /// <summary>Starts a host with a scripted summary builder in place of the deterministic one.</summary>
    public static NarrativeMemoryApiHost StartWithBuilder(
        string connectionString,
        TimeProvider clock,
        INarrativeSummaryBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return new(connectionString, clock, builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting(ChronicleafConnection.SettingName, _connectionString);
        builder.UseSetting(AccessSettings.AccessTokenHashSettingName, OwnerClient.AccessTokenHash);
        builder.UseSetting(AccessSettings.PublicOriginSettingName, OwnerClient.PublicOrigin);
        builder.UseEnvironment("Production");

        // The dispatch loop runs only when a scenario asks it to, so a refresh can be staged either
        // side of the seam between "the request committed" and "the queue knows".
        builder.UseSetting($"{OperationQueueOptions.SectionName}:Enabled", "false");

        // A poll interval, not a behaviour: the queue, the workers, the claiming, the lease and
        // the conditional commit are all production's.
        builder.UseSetting($"{OperationQueueOptions.SectionName}:SchedulePollingInterval", "00:00:00.200");
        builder.UseSetting($"{OperationQueueOptions.SectionName}:HeartbeatInterval", "00:00:05");

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton(_clock);

            if (_builder is not null)
            {
                // The one substitution, through the same registration the production builder uses.
                // Removed first rather than appended, so the double is the one the service resolves
                // rather than a second registration the container never returns.
                services.RemoveAll<INarrativeSummaryBuilder>();
                services.AddSingleton(_builder);
            }
        });
    }

    /// <summary>
    /// Runs one real outbox dispatch pass, the loop's own body called by hand.
    /// </summary>
    public async Task<IReadOnlyList<OutboxDispatchResult>> DispatchAsync(
        CancellationToken cancellationToken = default)
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
/// A summary builder that always fails, so the refresh failure path is exercised honestly
/// (NARRATIVE-MEMORY-002 Failure).
/// </summary>
public sealed class FailingNarrativeSummaryBuilder : INarrativeSummaryBuilder
{
    public string PromptVersion => NarrativeMemoryPolicy.PromptVersion;

    public Task<string> BuildAsync(
        NarrativeContextView context,
        IReadOnlyList<MilestoneNarrativeSource> milestones,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The scripted summary build failed.");
}
