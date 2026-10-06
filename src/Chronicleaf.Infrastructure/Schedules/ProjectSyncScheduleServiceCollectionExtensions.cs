using Chronicleaf.Application.Operations;
using Chronicleaf.Application.Schedules;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Infrastructure.Schedules;

/// <summary>
/// Registers the scheduled-project-sync stores, service, job handler and tick loop
/// (SCHEDULED-PROJECT-SYNC-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here migrates and nothing here substitutes a store.</b> A database without the
/// scheduled-project-sync migration reports <c>schema_mismatch</c> at readiness and every write
/// fails, which is the honest outcome rather than a silently unscheduled project.
/// </para>
/// <para>
/// <b>No name here is taken.</b> The service, the store, the handler and the ticker are all new
/// registrations under names of their own: the poll the tick triggers is the delivered candidate
/// poll under the delivered kind, and this package adds no transport and no second poll.
/// </para>
/// </remarks>
public static class ProjectSyncScheduleServiceCollectionExtensions
{
    public static IServiceCollection AddProjectSyncSchedules(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<ProjectSyncTickerOptions>(
            configuration.GetSection(ProjectSyncTickerOptions.SectionName));

        services.AddScoped<IProjectSyncScheduleStore, PostgresProjectSyncScheduleStore>();
        services.AddScoped<IProjectSyncScheduleService, ProjectSyncScheduleService>();

        // The handler performs a scheduled poll, registered the same way the delivered packages
        // register theirs: by its own kind, so the delivered registry finds it. It holds no store,
        // so it cannot write an outcome behind the delivered worker's conditional commit.
        services.AddScoped<IOperationHandler, ProjectSyncJobHandler>();

        // The minute tick that turns due rows into queued polls. Registered as its own
        // singleton first, so a test can drive one pass by hand through the same instance the loop
        // runs; then hosted, so a production deployment actually loops. Plain AddSingleton, so a
        // second registration can never silently install nothing.
        services.AddSingleton<ProjectSyncTicker>();
        services.AddHostedService(provider => provider.GetRequiredService<ProjectSyncTicker>());

        return services;
    }
}
