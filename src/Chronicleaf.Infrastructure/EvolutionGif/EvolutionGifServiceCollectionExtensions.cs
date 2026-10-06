using Chronicleaf.Application.EvolutionGifs;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Infrastructure.EvolutionGifs;

/// <summary>
/// Registers the evolution GIF store and service (EVOLUTION-GIF-EXPORT-001/002).
/// </summary>
/// <remarks>
/// The same shape as every other package's: one store over the delivered
/// <c>ChronicleafDbContext</c> and unit of work, and one service for the animation. Nothing here
/// migrates and nothing here substitutes a store, so a database without the evolution GIF
/// migration reports <c>schema_mismatch</c> at readiness and every write fails, which is the
/// honest outcome. Selection stays the curation package's and approval of prose stays the review
/// package's: this registration renders, binds and serves animations and nothing else.
/// </remarks>
public static class EvolutionGifServiceCollectionExtensions
{
    public static IServiceCollection AddEvolutionGifExport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IEvolutionGifStore, PostgresEvolutionGifStore>();
        services.AddScoped<IEvolutionGifService, EvolutionGifService>();

        return services;
    }
}
