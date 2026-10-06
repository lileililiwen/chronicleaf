using Chronicleaf.Application.Schedules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Infrastructure.Schedules;

/// <summary>
/// The configuration of the scheduled-project-sync tick
/// (SCHEDULED-PROJECT-SYNC-001).
/// </summary>
public sealed class ProjectSyncTickerOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "Chronicleaf:Schedules";

    /// <summary>The delay between tick passes. One minute: the smallest storable cadence is fifteen.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Whether the loop runs at all. It defaults to true and is only switched off by a test that
    /// wants to drive a tick by hand; a production deployment needs it, because the loop is what
    /// turns a due schedule row into a queued poll.
    /// </summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// Fires due sync schedules once per <see cref="ProjectSyncTickerOptions.Interval"/>
/// (SCHEDULED-PROJECT-SYNC-001).
/// </summary>
/// <remarks>
/// <para>
/// The loop holds no schedule state of its own: every pass asks the service which rows are due as
/// of this instant, so a restart resumes from the stored <c>next_run_at</c> values rather than from
/// anything this process remembered. That is what "shutdown retains nextDue state" means here - the
/// state was never anywhere but the database.
/// </para>
/// <para>
/// A pass that throws is logged and forgotten rather than retried inline: the next pass is one
/// interval away, and a tick that crashed on a poisoned row must not wedge the loop behind it.
/// </para>
/// </remarks>
public sealed class ProjectSyncTicker(
    IServiceScopeFactory scopeFactory,
    Microsoft.Extensions.Options.IOptions<ProjectSyncTickerOptions> options,
    TimeProvider clock,
    ILogger<ProjectSyncTicker> logger) : BackgroundService
{
    private readonly ProjectSyncTickerOptions _options = options.Value;

    /// <summary>
    /// Runs one tick pass by hand. The tests use it the way they use the dispatch loop's own pass:
    /// to place a firing on a chosen side of a restart without waiting a minute for the loop.
    /// </summary>
    public async Task<ProjectSyncTickResult> RunOnceAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        using var scope = scopeFactory.CreateScope();
        var schedules = scope.ServiceProvider.GetRequiredService<IProjectSyncScheduleService>();

        return await schedules.TickAsync(now, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await RunOnceAsync(clock.GetUtcNow(), stoppingToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "A scheduled sync tick failed; the next pass retries it.");
            }
        }
    }
}
