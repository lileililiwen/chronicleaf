using Chronicleaf.Application.Git;
using Chronicleaf.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Infrastructure.Git;

/// <summary>
/// The registered roots and the deployment they may be read in, read once at startup
/// (LOCAL-GIT-EVIDENCE-002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Configuration, not a registry, and never a request.</b> The only way a directory enters this table is
/// an operator's own configuration. There is no route that adds one, no "register a root" body, and no
/// request field that could widen the table - so the set of readable directories is a deployment fact and
/// not something an owner can change about their own installation.
/// </para>
/// <para>
/// <b>A root that does not exist is dropped, and an alias that is not a name is dropped.</b> A catalogue
/// holding a path this process cannot read would offer an owner a choice that always fails, and an alias
/// that cannot be typed is a key nobody could send. Both are therefore refused at startup with a log line
/// that names the alias and never the path, so a misconfiguration is visible in the operator's own logs
/// without this package putting their filesystem into them.
/// </para>
/// <para>
/// <b>Deployment is read before anything else, and the roots are held regardless.</b> A Hosted deployment
/// refuses local collection, so its roots are never consulted - but they are still parsed, so an operator
/// moving a Community install to Hosted sees the misconfiguration in the log rather than discovering it
/// later as a puzzling absence of aliases.
/// </para>
/// </remarks>
/// <param name="roots">The registered roots, in configuration order.</param>
/// <param name="deploymentMode">Which deployment this process is serving.</param>
public sealed class ConfigurationLocalRepositoryCatalog(
    IReadOnlyList<LocalRepositoryRoot> roots,
    LocalGitDeploymentMode deploymentMode) : ILocalRepositoryCatalog
{
    /// <inheritdoc />
    public LocalGitDeploymentMode DeploymentMode { get; } = deploymentMode;

    /// <inheritdoc />
    public IReadOnlyList<string> Aliases { get; } = [.. roots.Select(root => root.Alias)];

    private readonly IReadOnlyDictionary<string, LocalRepositoryRoot> _byAlias = roots
        .GroupBy(root => root.Alias, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public LocalRepositoryRoot? Find(string? alias) =>
        string.IsNullOrWhiteSpace(alias) ? null : _byAlias.GetValueOrDefault(alias.Trim());

    /// <summary>
    /// Builds the catalogue from configuration, dropping every entry this process cannot use.
    /// </summary>
    /// <remarks>
    /// <b>Four refusals, four different repairs, so each is named separately.</b> A blank alias and a
    /// non-absolute path are the operator's own typos; a directory that does not exist is a deployment that
    /// has not been mounted yet, which is a different thing from a path that is wrong. The alias is logged
    /// because it is the key the operator will use; the path is not, because it is the operator's own
    /// filesystem and this product does not publish paths.
    /// </remarks>
    public static ConfigurationLocalRepositoryCatalog FromConfiguration(
        IConfiguration configuration,
        string sectionName,
        ILogger<ConfigurationLocalRepositoryCatalog> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        var section = configuration.GetSection(sectionName);

        // The key is spelled out rather than produced by `nameof` on the type: `nameof` on a type yields the
        // type's own name, not a property's, and asking for `nameof(LocalGitDeploymentMode)` here would read
        // a key no operator ever writes and would leave this deployment permanently Community without any
        // failure to notice it by.
        var mode = ReadMode(section[DeploymentModeKey]);
        var accepted = new List<LocalRepositoryRoot>();

        foreach (var entry in section.GetSection("Roots").GetChildren())
        {
            var alias = entry[RootAliasKey]?.Trim();
            var path = entry[RootPathKey]?.Trim();

            if (string.IsNullOrWhiteSpace(alias))
            {
                logger.LogWarning("A local repository root was configured with no alias and was ignored.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(path))
            {
                logger.LogWarning(
                    "The local repository root alias {Alias} has no path and was ignored.",
                    alias);
                continue;
            }

            if (!Path.IsPathRooted(path))
            {
                logger.LogWarning(
                    "The local repository root alias {Alias} does not name an absolute directory and was ignored.",
                    alias);
                continue;
            }

            if (!Directory.Exists(path))
            {
                logger.LogWarning(
                    "The directory for local repository root alias {Alias} is not present, so it will refuse every "
                    + "collection until it is. The path itself is not logged.",
                    alias);
                continue;
            }

            accepted.Add(new LocalRepositoryRoot(alias, path));
        }

        return new ConfigurationLocalRepositoryCatalog(accepted, mode);
    }

    /// <summary>The configuration key a root's name is read from, inside each entry of <c>Roots</c>.</summary>
    public const string RootAliasKey = "Alias";

    /// <summary>The configuration key a root's directory is read from, inside each entry of <c>Roots</c>.</summary>
    public const string RootPathKey = "Path";

    /// <summary>
    /// The configuration key the deployment mode is read from, inside the section this catalogue is bound to.
    /// </summary>
    /// <remarks>
    /// Public because a test asserting the container's own reading of it should not have to hard-code the
    /// same string twice, and because a key that is written in two places will eventually be written
    /// differently in one of them.
    /// </remarks>
    public const string DeploymentModeKey = "DeploymentMode";

    private static LocalGitDeploymentMode ReadMode(string? value)
    {
        // An unrecognised value is Community, and that is the safe direction: the mode's whole purpose is to
        // withhold local reads on a shared machine, so a typo must not silently turn it on and a typo must
        // not turn a single-tenant install into a tenant-less one either. Anything but an explicit Hosted is
        // Community, and the log line below is how an operator learns their setting did not take.
        if (string.IsNullOrWhiteSpace(value))
        {
            return LocalGitDeploymentMode.Community;
        }

        if (Enum.TryParse<LocalGitDeploymentMode>(value.Trim(), ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        return LocalGitDeploymentMode.Community;
    }
}
