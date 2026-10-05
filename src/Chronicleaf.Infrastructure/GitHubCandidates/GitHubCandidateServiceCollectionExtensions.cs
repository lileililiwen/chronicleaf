using Chronicleaf.Application.GitHubCandidates;
using Chronicleaf.Application.Operations;
using Chronicleaf.Application.Outbox;
using Chronicleaf.GitHub.Candidates;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Infrastructure.GitHubCandidates;

/// <summary>
/// Registers the GitHub candidate stores, service, job handler and outbox consumer
/// (GITHUB-RELEASE-CANDIDATES-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>The transport takes the delivered <see cref="HttpClient"/> and is a third registration name.</b>
/// <c>IGitEvidenceSource</c> is the hosted evidence transport's and <c>ILocalGitSource</c> is the local
/// one's; a candidate poll is a different question of the same host, and registering it under either of
/// those names would shadow the transport that owns it - a silent no-op that reads as a pass.
/// <c>IGitHubCandidateSource</c> is therefore its own name, and the suite that ships with this package
/// asserts from the other side that the two delivered names still resolve to the transports that own
/// them.
/// </para>
/// <para>
/// <b>Nothing here migrates and nothing here substitutes a store.</b> A database without the
/// github-release-candidates migration reports <c>schema_mismatch</c> at readiness and every write
/// fails, which is the honest outcome rather than a silently empty candidate list.
/// </para>
/// <para>
/// <b>There is no secret to configure.</b> The webhook secret is a delivered vault credential under the
/// <c>github-webhook</c> provider, stored by the owner through the delivered vault route; this package
/// only ever holds a reference to it and a lease for the length of one comparison.
/// </para>
/// </remarks>
public static class GitHubCandidateServiceCollectionExtensions
{
    public static IServiceCollection AddGitHubCandidates(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IGitHubWebhookSubscriptionStore, PostgresGitHubWebhookSubscriptionStore>();
        services.AddScoped<IGitHubWebhookDeliveryStore, PostgresGitHubWebhookDeliveryStore>();
        services.AddScoped<IGitHubCandidateStore, PostgresGitHubCandidateStore>();
        services.AddScoped<IGitHubCandidateRunStore, PostgresGitHubCandidateRunStore>();
        services.AddScoped<IGitHubCandidateService, GitHubCandidateService>();

        // Two handlers, registered the same way the delivered packages register theirs: by their own
        // kind, through the same interfaces, so the delivered registries find them. The job handler
        // performs a poll; the outbox handler turns one accepted delivery into candidates. Neither holds
        // a store, so neither can write an outcome behind the delivered worker's conditional commit.
        services.AddScoped<IOperationHandler, GitHubCandidateSyncHandler>();
        services.AddScoped<IOutboxEventHandler, GitHubCandidateInboxHandler>();

        // The read-only candidate transport over the delivered HttpClient, so a poll and a collection
        // share one connection pool, one pinned host and one set of handler settings. Plain
        // `AddSingleton`, so a second registration can never silently install nothing the way
        // `TryAddSingleton` does when the service type is already registered: a transport that is
        // silently absent reads as a pass while testing nothing.
        services.AddSingleton<IGitHubCandidateSource>(provider => new GitHubCandidateClient(
            provider.GetRequiredService<HttpClient>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System));

        return services;
    }
}
