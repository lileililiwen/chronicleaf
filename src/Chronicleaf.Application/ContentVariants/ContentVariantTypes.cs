using Chronicleaf.Core;

namespace Chronicleaf.Application.ContentVariants;

/// <summary>
/// The derived formats this package stores (CONTENT-ASSET-VARIANTS-001).
/// </summary>
/// <remarks>
/// <para>
/// Every member aliases a kind in <see cref="ContentAssetTypes"/>: the storage vocabulary stays in
/// the domain, and this file is the package's own reading order over it. A format is a rendering of
/// an approved story's accepted summary, never a new fact: sections with no source claims read the
/// renderer's unknown label rather than inventing one.
/// </para>
/// </remarks>
public static class ContentVariantTypes
{
    public const string Changelog = ContentAssetTypes.Changelog;

    public const string TechnicalBlog = ContentAssetTypes.TechnicalBlog;

    public const string SocialShort = ContentAssetTypes.SocialShort;

    public const string GithubRelease = ContentAssetTypes.GithubRelease;

    public const string ReadmeUpdate = ContentAssetTypes.ReadmeUpdate;

    public const string Portfolio = ContentAssetTypes.Portfolio;

    public const string WeeklyProgress = ContentAssetTypes.WeeklyProgress;

    /// <summary>Every derived format, in the order a picker lists them.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Changelog,
        TechnicalBlog,
        SocialShort,
        GithubRelease,
        ReadmeUpdate,
        Portfolio,
        WeeklyProgress,
    ];

    /// <summary>True for a derived format this package renders. Compared ordinally, like the domain.</summary>
    public static bool IsVariant(string? type) =>
        type is not null && All.Contains(type, StringComparer.Ordinal);
}
