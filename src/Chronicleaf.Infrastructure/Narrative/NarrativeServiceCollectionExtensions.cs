using Chronicleaf.Application.Narrative;
using Chronicleaf.Application.Operations;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Infrastructure.Narrative;

/// <summary>
/// Registers the narrative stores, service, summary builder and refresh job handler
/// (NARRATIVE-MEMORY-001/002).
/// </summary>
/// <remarks>
/// The same shape as every other package's: one store over the delivered
/// <c>ChronicleafDbContext</c> and unit of work, one service for the requests and the context
/// builder, one deterministic summary builder, and one handler registered by its kind so the
/// delivered registry finds it. Nothing here migrates and nothing here substitutes a store, so a
/// database without the narrative-memory migration reports <c>schema_mismatch</c> at readiness
/// and every write fails, which is the honest outcome.
/// <para>
/// <b>No provider is registered here, and no credential is resolved.</b> The summary is built
/// deterministically from attributed sources the owner already owns: there is no model call, no
/// secret and no socket behind a refresh. A build that includes this registration refreshes, and
/// a build that does not answers 404.
/// </para>
/// <para>
/// <b>Nothing here can approve anything.</b> There is no approval registration to remove and no
/// configuration that would install one, because approval is the review package's table bound to
/// a revision and a body hash. This registration reads live approvals to excerpt them and never
/// writes to them.
/// </para>
/// </remarks>
public static class NarrativeServiceCollectionExtensions
{
    public static IServiceCollection AddNarrativeMemory(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<INarrativeStore, PostgresNarrativeStore>();
        services.AddScoped<INarrativeSummaryBuilder, DeterministicNarrativeSummaryBuilder>();
        services.AddScoped<NarrativeService>();
        services.AddScoped<INarrativeService>(provider => provider.GetRequiredService<NarrativeService>());
        services.AddScoped<IProjectNarrativeContext>(provider => provider.GetRequiredService<NarrativeService>());
        services.AddScoped<IOperationHandler, NarrativeRefreshHandler>();

        return services;
    }
}
