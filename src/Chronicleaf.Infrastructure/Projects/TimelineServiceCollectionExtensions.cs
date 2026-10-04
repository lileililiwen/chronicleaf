using Chronicleaf.Application.Timeline;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Infrastructure.Projects;

/// <summary>
/// Registers the private timeline read (MILESTONE-TIMELINE-001, MILESTONE-TIMELINE-002).
/// </summary>
/// <remarks>
/// The registration is the same shape as every other package's: a scoped service over the
/// delivered stores. Nothing here migrates and nothing here stores: the timeline is a projection
/// of the milestone records the projects-and-milestones migration already holds, so a database
/// without that migration reports <c>schema_mismatch</c> at readiness and this read fails with it.
/// </remarks>
public static class TimelineServiceCollectionExtensions
{
    public static IServiceCollection AddTimeline(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<ITimelineService, TimelineService>();

        return services;
    }
}
