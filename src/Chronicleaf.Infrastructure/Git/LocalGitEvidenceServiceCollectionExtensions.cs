using Chronicleaf.Application.Git;
using Chronicleaf.Application.Operations;
using Chronicleaf.GitHub.Evidence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Infrastructure.Git;

/// <summary>
/// How the local Git transport is built in production (LOCAL-GIT-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>The registered roots are configuration, read once, and never a request.</b> There is no route that
/// adds one and no body that could widen the table, so the set of readable directories is a deployment fact.
/// An operator configures <c>Chronicleaf:LocalRepositoryRoots</c> with a deployment mode and a list of
/// alias/path pairs; an owner chooses between the aliases.
/// </para>
/// <para>
/// <b>The runner is a singleton and the source is too</b>, because a process runner holds no state and a
/// transport is required to be safe to call concurrently. A test replaces the whole runner - never a
/// timeout, a cap or an argument inside it - so the only thing a double can stand in for is Git itself.
/// </para>
/// <para>
/// <b>Nothing here writes to a repository.</b> The registered runner refuses every subcommand outside its
/// four reads, and the registration is what makes that check a run-time fact rather than a convention: a
/// composition root that forgot to register the runner would have no way to run Git at all, rather than one
/// that quietly could.
/// </para>
/// </remarks>
public static class LocalGitEvidenceServiceCollectionExtensions
{
    /// <summary>The configuration section the registered roots and the deployment mode are read from.</summary>
    /// <remarks>
    /// <b>The environment-variable spelling is the section name with <c>:</c> doubled</b>, which is what an
    /// operator writes in a compose file or a unit: <c>Chronicleaf__LocalRepositoryRoots__DeploymentMode</c>
    /// and <c>Chronicleaf__LocalRepositoryRoots__Roots__0__Alias</c> /
    /// <c>Chronicleaf__LocalRepositoryRoots__Roots__0__Path</c>. That is the standard .NET configuration
    /// convention rather than a second naming scheme invented here, and it means one section name describes
    /// both the appsettings form and the container form.
    /// </remarks>
    public const string CatalogSectionName = "Chronicleaf:LocalRepositoryRoots";

    public static IServiceCollection AddLocalGitEvidence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddScoped<ILocalRepositoryStore, PostgresLocalRepositoryStore>();
        services.AddScoped<ILocalSyncSourceStore, PostgresLocalSyncSourceStore>();
        services.AddScoped<ILocalGitEvidenceService, LocalGitEvidenceService>();
        services.AddScoped<IOperationHandler, LocalGitEvidenceSyncHandler>();

        // The catalogue is built once from configuration and read thereafter, so the set of readable
        // directories cannot change while a collection is running. A test replaces the whole thing to
        // register a temporary root; it does not append to a live one.
        services.TryAddSingleton<ILocalRepositoryCatalog>(provider => ConfigurationLocalRepositoryCatalog
            .FromConfiguration(
                configuration,
                CatalogSectionName,
                provider.GetRequiredService<ILogger<ConfigurationLocalRepositoryCatalog>>()));

        // The real runner. `TryAdd` rather than `Add`, so a test host that has already registered its own
        // runner keeps it - which is how a scenario can decide what Git answers without reconfiguring the
        // production one.
        services.TryAddSingleton<IGitCommandRunner, GitCommandRunner>();

        // The local transport satisfies the delivered `IGitEvidenceSource` contract as well as its own
        // `ILocalGitSource`, which is what makes the two interchangeable at the level of a collected result.
        // It is registered under the local seam only: `IGitEvidenceSource` is the hosted transport's, and a
        // second registration under that name would shadow it and move every GitHub collection onto a
        // directory.
        services.TryAddSingleton<ILocalGitSource, LocalGitEvidenceSource>();

        return services;
    }
}
