using Chronicleaf.Application.GitHub;
using Chronicleaf.GitHub.App;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicleaf.Infrastructure.GitHub;

/// <summary>
/// Registers the GitHub App connection stores, service and token provider
/// (GITHUB-APP-CONNECTION-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here migrates and nothing here substitutes a store.</b> A database without the
/// github-app-connection migration reports <c>schema_mismatch</c> at readiness and every write
/// fails, which is the honest outcome rather than a silently unconnected project.
/// </para>
/// <para>
/// <b>No name here is taken.</b> The installation store, the service and the provider are new
/// registrations under names of their own: the evidence collection stays the delivered
/// <c>milestone.github-sync</c> job, and this package adds no second collection.
/// </para>
/// <para>
/// <b>Rollback is this registration.</b> Removing it un-installs the App connection surface and
/// leaves stored installations, stored evidence and the delivered token path where they are.
/// </para>
/// </remarks>
public static class GitHubAppServiceCollectionExtensions
{
    public static IServiceCollection AddGitHubAppConnection(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton(
            configuration.GetSection(GitHubAppOptions.SectionName).Get<GitHubAppOptions>()
                ?? new GitHubAppOptions());

        services.AddScoped<IGitHubAppInstallationStore, PostgresGitHubAppInstallationStore>();
        services.AddScoped<IGitHubAppService, GitHubAppService>();

        // The setup states and the token entries are process-wide by design: states are nonces
        // and tokens are minted once no matter how many requests arrive together. Plain
        // AddSingleton, so a second registration can never silently install nothing.
        services.AddSingleton<GitHubAppSetupStates>();
        services.AddSingleton<GitHubAppTokenCache>();

        // The provider is scoped over the vault it leases the PEM from; the concrete registration
        // underneath is what the App service mints its verification token through, so the two
        // never disagree about what a token is.
        services.AddScoped<GitHubAppInstallationTokenProvider>();
        services.AddScoped<IGitHubInstallationTokenProvider>(
            provider => provider.GetRequiredService<GitHubAppInstallationTokenProvider>());

        // The App transport shares the delivered GitHub HttpClient - one host, one redirect
        // policy, one timeout - so there is no second host, redirect behaviour or ceiling to
        // disagree about. A test replaces that client and nothing else.
        services.AddSingleton<GitHubAppClient>(provider => new GitHubAppClient(
            provider.GetRequiredService<HttpClient>(),
            provider.GetService<TimeProvider>() ?? TimeProvider.System));

        return services;
    }
}
