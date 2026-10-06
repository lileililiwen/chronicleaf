using Chronicleaf.Application.Journey;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Infrastructure.Journey;

/// <summary>
/// Registers the public-project-journey store and service (PUBLIC-PROJECT-JOURNEY-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The same shape as every other package's: one journey store over the delivered
/// <c>ChronicleafDbContext</c> and unit of work, and one service for the preview, publication
/// and public reads. Nothing here migrates and nothing here substitutes a store, so a database
/// without the public-project-journey migration reports <c>schema_mismatch</c> at readiness and
/// every write fails, which is the honest outcome.
/// </para>
/// <para>
/// <b>Nothing here can generate, approve, export or publish to another host.</b> There is no
/// provider, no credential and no publisher behind this registration: the projection re-resolves
/// approved bytes through the delivered stores, approval stays the review package's table, and
/// the public surface is this application's own anonymous routes. Registration is the whole of
/// this package's switch: a build that includes it serves journeys, and a build that does not
/// answers 404.
/// </para>
/// </remarks>
public static class JourneyServiceCollectionExtensions
{
    public static IServiceCollection AddProjectJourney(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IProjectJourneyStore, PostgresProjectJourneyStore>();
        services.AddScoped<IProjectJourneyService, ProjectJourneyService>();

        return services;
    }
}
