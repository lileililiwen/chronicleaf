using Chronicleaf.Application.Milestones;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Infrastructure.Projects;

/// <summary>
/// Registers the Project and Milestone stores, the audit writer and the use cases
/// (PROJECTS-AND-MILESTONES-001/002).
/// </summary>
/// <remarks>
/// The registrations are the same shape as every other package's: scoped stores over the delivered
/// <c>ChronicleafDbContext</c>, the delivered unit of work, and one service instance for the request.
/// Nothing here migrates, nothing here substitutes a store, and nothing here installs an in-memory
/// provider: a database that has not been migrated reports <c>schema_mismatch</c> at readiness and
/// every write fails, which is the honest outcome.
/// </remarks>
public static class ProjectMilestoneServiceCollectionExtensions
{
    public static IServiceCollection AddProjectMilestones(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IProjectStore, PostgresProjectStore>();
        services.AddScoped<IMilestoneStore, PostgresMilestoneStore>();
        services.AddScoped<IAuditWriter, PostgresAuditWriter>();
        services.AddScoped<IProjectMilestoneService, ProjectMilestoneService>();

        return services;
    }
}
