using Chronicleaf.Application.Curation;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Infrastructure.Curation;

/// <summary>
/// Registers the curation store and service (SCREENSHOT-CURATION-001/002).
/// </summary>
/// <remarks>
/// The same shape as every other package's: one store over the delivered
/// <c>ChronicleafDbContext</c> and unit of work, and one service for the curation. Nothing here
/// migrates and nothing here substitutes a store, so a database without the screenshot-curation
/// migration reports <c>schema_mismatch</c> at readiness and every write fails, which is the
/// honest outcome. The candidate table stays capture's: this registration moves its curation
/// columns and nothing else.
/// </remarks>
public static class CurationServiceCollectionExtensions
{
    public static IServiceCollection AddScreenshotCuration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IScreenshotCurationStore, PostgresScreenshotCurationStore>();
        services.AddScoped<IScreenshotCurationService, ScreenshotCurationService>();

        return services;
    }
}
