using Chronicleaf.AI.Contracts;
using Chronicleaf.Application.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Infrastructure.AI;

/// <summary>
/// Registers the generation preview store and use cases (LLM-PROVIDER-CONTRACT-001/002).
/// </summary>
/// <remarks>
/// The same shape as every other package's: a scoped store over the delivered
/// <c>ChronicleafDbContext</c>, the delivered unit of work and audit writer, and one service instance
/// for the request. Nothing here migrates and nothing substitutes a store, so a database that has not
/// been migrated reports <c>schema_mismatch</c> at readiness and every write fails, which is the
/// honest outcome.
/// </remarks>
/// <remarks>
/// <b>No provider adapter is registered here, and that is load-bearing.</b> The five adapters are
/// separate roadmap packages, each of which will add its own <c>ILLMProvider</c> to the composition
/// root. Until one does, the resolver below is built over an empty set and every provider answers
/// <c>provider_not_installed</c> before a credential, a socket or a token is involved. The resolver is
/// a singleton over whatever adapters the composition root registered, so a test double is reachable
/// only where it was actually added - and the production registration adds none.
/// </remarks>
public static class AIGenerationServiceCollectionExtensions
{
    public static IServiceCollection AddAIGeneration(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IGenerationPreviewStore, PostgresGenerationPreviewStore>();
        services.AddScoped<IGenerationPreviewService, GenerationPreviewService>();

        // The call budget, as a value rather than a setting. No configuration key binds it and no
        // environment variable names it, so the only way to move it is to hand the service a
        // different instance - which is a composition root's decision, and a test's. Production always
        // gets the published default.
        services.AddSingleton(GenerationCallOptions.Default);

        // A transport, not a per-request object: it holds a client, not a scope, and it is required
        // to be thread-safe. Built once from whatever ILLMProvider registrations the composition root
        // supplied, which in production is none.
        services.AddSingleton<ILLMProviderResolver>(provider =>
            new LLMProviderResolver(provider.GetServices<ILLMProvider>()));

        return services;
    }
}
