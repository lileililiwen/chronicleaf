using System.Text.RegularExpressions;
using Chronicleaf.GitHub.Evidence;
using Microsoft.Extensions.Configuration;

namespace Chronicleaf.Publishers.GitHubPages;

/// <summary>
/// The operator's explicit GitHub Pages target
/// (PUBLISHER-GITHUB-PAGES-ADAPTER-001).
/// </summary>
/// <remarks>
/// <para>
/// <b>Demand-gated by construction.</b> The section is read once at registration: when it is
/// absent this package registers nothing and the target answers <c>publisher_unknown_target</c>,
/// exactly as the contract says an unclaimed target does. When it is present it must validate,
/// and an invalid value fails startup rather than a publish.
/// </para>
/// <para>
/// The repository is two validated name segments - owner and name, never a URL - through the
/// delivered <see cref="RepoRef"/>, so no value here can address another host, carry a port, a
/// path, a query or a fragment. The branch and the subdirectory are validated against the same
/// refusals: no traversal, no empty segment, no scheme.
/// </para>
/// </remarks>
/// <param name="RepositoryOwner">The GitHub account or organisation that owns the repository.</param>
/// <param name="RepositoryName">The repository's own name, without the owner.</param>
/// <param name="Branch">The Pages branch the push moves, for example <c>gh-pages</c>.</param>
/// <param name="Subdirectory">
/// Where under the branch the site is written. Empty means the branch root.
/// </param>
public sealed partial record GitHubPagesOptions(
    string RepositoryOwner,
    string RepositoryName,
    string Branch,
    string Subdirectory)
{
    /// <summary>The configuration section this target binds from.</summary>
    public const string SectionName = "Chronicleaf:GitHubPages";

    /// <summary>The longest branch name this target accepts.</summary>
    public const int BranchMaxLength = 255;

    /// <summary>The longest subdirectory value this target accepts.</summary>
    public const int SubdirectoryMaxLength = 128;

    /// <summary>The display form of an empty subdirectory: the branch root.</summary>
    public const string RootDisplay = "/";

    /// <summary>The validated repository reference: owner and name, never a URL.</summary>
    public RepoRef Repository => RepoRef.Parse(RepositoryOwner, RepositoryName)
        ?? throw new InvalidOperationException(
            "The GitHub Pages repository did not validate, although the options did.");

    /// <summary>The subdirectory segments, or empty for the branch root.</summary>
    public IReadOnlyList<string> SubdirectorySegments => Subdirectory.Length == 0
        ? []
        : Subdirectory.Split('/').ToList().AsReadOnly();

    /// <summary>
    /// Reads the section, or null when no value names a target - the demand gate, not an error.
    /// </summary>
    public static GitHubPagesOptions? Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);

        // The indexer, not the binder: four flat keys, no object graph, and no Binder
        // package enters a transport for it.
        var owner = section[nameof(RepositoryOwner)];
        var name = section[nameof(RepositoryName)];
        var branch = section[nameof(Branch)];
        var subdirectory = section[nameof(Subdirectory)] ?? string.Empty;

        if (string.IsNullOrWhiteSpace(owner)
            && string.IsNullOrWhiteSpace(name)
            && string.IsNullOrWhiteSpace(branch)
            && string.IsNullOrWhiteSpace(subdirectory))
        {
            return null;
        }

        return Create(owner, name, branch, subdirectory);
    }

    /// <summary>
    /// Validates four raw values into options, throwing on the first refusal.
    /// </summary>
    /// <exception cref="InvalidOperationException">A value is not one this target accepts.</exception>
    public static GitHubPagesOptions Create(
        string? owner,
        string? name,
        string? branch,
        string? subdirectory)
    {
        if (RepoRef.Parse(owner, name) is null)
        {
            throw new InvalidOperationException(
                "The GitHub Pages repository must be a GitHub owner and repository name: two name "
                + "segments, never a URL.");
        }

        var parsedBranch = (branch ?? string.Empty).Trim();

        if (parsedBranch.Length == 0
            || parsedBranch.Length > BranchMaxLength
            || !BranchPattern().IsMatch(parsedBranch)
            || parsedBranch.StartsWith("/", StringComparison.Ordinal)
            || parsedBranch.EndsWith("/", StringComparison.Ordinal)
            || parsedBranch.StartsWith(".", StringComparison.Ordinal)
            || parsedBranch.EndsWith(".", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The GitHub Pages branch must name a branch: letters, digits, dots, dashes, "
                + "underscores and single slashes, never traversal and never a leading or trailing "
                + "separator.");
        }

        var parsedSubdirectory = (subdirectory ?? string.Empty).Trim();

        if (parsedSubdirectory.Length > SubdirectoryMaxLength
            || parsedSubdirectory.Contains('\\')
            || parsedSubdirectory.Contains(':')
            || (parsedSubdirectory.Length > 0
                && (parsedSubdirectory.StartsWith("/", StringComparison.Ordinal)
                    || parsedSubdirectory.EndsWith("/", StringComparison.Ordinal))))
        {
            throw new InvalidOperationException(
                "The GitHub Pages subdirectory must be relative URL segments, never a filesystem "
                + "path: no leading or trailing slash, no backslash and no drive letter.");
        }

        if (parsedSubdirectory.Length > 0)
        {
            foreach (var segment in parsedSubdirectory.Split('/'))
            {
                if (segment.Length == 0
                    || segment is "." or ".."
                    || !SubdirectorySegmentPattern().IsMatch(segment))
                {
                    throw new InvalidOperationException(
                        "The GitHub Pages subdirectory must be relative URL segments, never the "
                        + "current or parent directory.");
                }
            }
        }

        return new GitHubPagesOptions(
            owner!.Trim(), name!.Trim(), parsedBranch, parsedSubdirectory);
    }

    /// <summary>The single display line the preview binds: owner, name, branch and subdirectory.</summary>
    public string TargetLine(
        int fileCount,
        long totalBytes) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"github-pages {RepositoryOwner}/{RepositoryName}@{Branch}:{(Subdirectory.Length == 0 ? RootDisplay : Subdirectory)} - {fileCount} files, {totalBytes} bytes");

    // A branch name: the characters GitHub accepts in the common case, without the constructs
    // that change meaning - no traversal, no doubled separator, no ref suffix.
    [GeneratedRegex("^(?!.*\\.\\.)(?!.*//)(?!.*@\\{)[A-Za-z0-9._/-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex BranchPattern();

    // One subdirectory segment: URL characters an operator means, and nothing a filesystem
    // would resolve away from the branch.
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._~-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SubdirectorySegmentPattern();
}
