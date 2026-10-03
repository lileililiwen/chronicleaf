using Chronicleaf.Application.Voice;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Infrastructure.Voice;

/// <summary>
/// Registers the voice store and use cases (VOICE-PROFILES-001/002).
/// </summary>
/// <remarks>
/// The same shape as every other package's: a scoped store over the delivered
/// <c>ChronicleafDbContext</c>, the delivered unit of work and audit writer, and one service
/// instance for the request. Nothing here migrates, nothing substitutes a store, and nothing
/// installs an in-memory provider - a database that has not been migrated reports
/// <c>schema_mismatch</c> at readiness and every write fails, which is the honest outcome.
/// </remarks>
public static class VoiceServiceCollectionExtensions
{
    public static IServiceCollection AddVoiceProfiles(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IVoiceProfileStore, PostgresVoiceStore>();
        services.AddScoped<IVoiceProfileService, VoiceProfileService>();

        return services;
    }
}
