using Chronicleaf.Application.GitHubEnrichment;
using Chronicleaf.Application.Operations;
using Chronicleaf.GitHub.GraphQL;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Infrastructure.GitHubEnrichment;

/// <summary>
/// Registers the GitHub relationship-enrichment stores, service and job handler
/// (GITHUB-GRAPHQL-ENRICHMENT-001/002).
/// </summary>
/// <remarks>
/// The same shape as every other package's: one scoped store over the delivered
/// <c>ChronicleafDbContext</c> and unit of work, one service for the request, and one handler
/// registered by its kind so the delivered registry finds it. Nothing here migrates and nothing
/// here substitutes a store, so a database without the github-graphql-enrichment migration reports
/// <c>schema_mismatch</c> at readiness and every write fails, which is the honest outcome.
/// </remarks>
/// <remarks>
/// <b>The transport is the delivered GitHub client's, not a second one.</b> The relationship
/// reader is a singleton built over the same pinned <see cref="HttpClient"/> the github-evidence
/// package registered, because an <see cref="HttpClient"/> holds a connection pool rather than a
/// scope and the host, the redirects and the timeout must be the same ones. This registration
/// therefore requires <c>AddGitHubEvidence</c> to have run first, and registers no
/// <see cref="HttpClient"/> of its own: a second client registration would shadow the first and
/// move every GitHub collection onto whatever the second one was built with. Plain
/// <c>AddSingleton</c> is used throughout, because <c>TryAddSingleton</c> keys on the service type
/// and would silently install nothing once claimed.
/// </remarks>
public static class GitHubEnrichmentServiceCollectionExtensions
{
    public static IServiceCollection AddGitHubGraphqlEnrichment(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IEnrichmentRunStore, PostgresEnrichmentRunStore>();
        services.AddScoped<IGitHubEnrichmentService, GitHubEnrichmentService>();
        services.AddScoped<IOperationHandler, GitHubEnrichmentHandler>();

        services.AddSingleton<IGitHubRelationshipReader>(provider => new GitHubRelationshipReader(
            provider.GetRequiredService<HttpClient>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System));

        return services;
    }
}
