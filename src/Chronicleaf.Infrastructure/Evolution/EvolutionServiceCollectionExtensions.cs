using Chronicleaf.Application.Evolution;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Infrastructure.Evolution;

/// <summary>
/// Registers the evolution store and service (SCREENSHOT-EVOLUTION-IMAGES-001/002).
/// </summary>
/// <remarks>
/// The same shape as every other package's: one store over the delivered
/// <c>ChronicleafDbContext</c> and unit of work, and one service for the comparison. Nothing here
/// migrates and nothing here substitutes a store, so a database without the evolution migration
/// reports <c>schema_mismatch</c> at readiness and every write fails, which is the honest
/// outcome. Selection stays the curation package's and approval of prose stays the review
/// package's: this registration renders, binds and serves comparisons and nothing else.
/// </remarks>
public static class EvolutionServiceCollectionExtensions
{
    public static IServiceCollection AddScreenshotEvolutionImages(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IEvolutionImageStore, PostgresEvolutionImageStore>();
        services.AddScoped<IEvolutionImageService, EvolutionImageService>();

        return services;
    }
}
