using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace Chronicleaf.Publishers.DevTo;

/// <summary>
/// The operator's explicit Dev.to target (PUBLISHER-DEVTO-ADAPTER-001).
/// </summary>
/// <remarks>
/// <para>
/// <b>Demand-gated by construction.</b> The section is read once at registration: when it is
/// absent this package registers nothing and the target answers
/// <c>publisher_unknown_target</c>, exactly as the contract says an unclaimed target does.
/// When it is present it must validate, and an invalid value fails startup rather than a
/// publish.
/// </para>
/// <para>
/// The only target configuration Dev.to needs beyond the leased credential is the default tag
/// set: the approved snapshot carries no tags of its own, and the Forem article API accepts at
/// most four. The bound below is internal and validated until the provider reports support for
/// more, so a fifth tag is a startup refusal rather than a refused publish.
/// </para>
/// <para>
/// The value stays a flat comma-separated string - rather than a bound list - so the
/// composition root's <c>Configure</c> binds the one key operators actually set. Parsing and
/// validation live in <see cref="Create"/>, which the adapter re-runs on every call: a
/// reloaded section that no longer validates is a refusal, never a send under half-read tags.
/// </para>
/// </remarks>
/// <param name="Tags">The raw comma-separated default tags, or null for none.</param>
public sealed partial record DevToOptions(string? Tags)
{
    /// <summary>The configuration section this target binds from.</summary>
    public const string SectionName = "Chronicleaf:DevTo";

    /// <summary>How many tags one article may carry, per the provider's bound.</summary>
    public const int MaxTags = 4;

    /// <summary>How long one tag may be, as an internal validated bound.</summary>
    public const int MaxTagLength = 30;

    /// <summary>
    /// The validated default tags sent with every article. Empty for none. Read only after
    /// <see cref="Create"/> accepted the raw value; constructing this record directly with an
    /// unvalidated value and reading here is a programming error, not a publish.
    /// </summary>
    public IReadOnlyList<string> ParsedTags =>
        string.IsNullOrWhiteSpace(Tags)
            ? []
            : Tags.Split(',').Select(tag => tag.Trim()).ToList().AsReadOnly();

    /// <summary>
    /// Reads the section, or null when no value names a target - the demand gate, not an error.
    /// </summary>
    public static DevToOptions? Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);

        // The indexer, not the binder: one flat key, no object graph, and no Binder package
        // enters a transport for it. A section that names nothing at all is the demand gate
        // answering "not configured"; anything present must validate below.
        var raw = section[nameof(Tags)];

        if (raw is null && !section.GetChildren().Any())
        {
            return null;
        }

        return Create(raw);
    }

    /// <summary>
    /// Validates one raw comma-separated tag value into options, throwing on the first refusal.
    /// </summary>
    /// <exception cref="InvalidOperationException">A value is not one this target accepts.</exception>
    public static DevToOptions Create(string? tags)
    {
        var parsed = string.IsNullOrWhiteSpace(tags)
            ? []
            : tags.Split(',').Select(tag => tag.Trim()).ToList();

        if (parsed.Any(tag => tag.Length == 0))
        {
            throw new InvalidOperationException(
                "The Dev.to tags must be comma-separated without empties: no leading, trailing or "
                + "doubled separator and no blank entry.");
        }

        if (parsed.Count > MaxTags)
        {
            throw new InvalidOperationException(
                $"The Dev.to target carries at most {MaxTags} tags; name fewer instead.");
        }

        foreach (var tag in parsed)
        {
            if (tag.Length > MaxTagLength || !TagPattern().IsMatch(tag))
            {
                throw new InvalidOperationException(
                    "A Dev.to tag is lowercase letters and digits, starting with a letter, at most "
                    + $"{MaxTagLength} characters.");
            }
        }

        return new DevToOptions(parsed.Count == 0 ? null : string.Join(",", parsed));
    }

    /// <summary>The single display line the preview binds: the target id and its tag set.</summary>
    public string TargetLine() =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"devto tags:[{string.Join(",", ParsedTags)}] ({ParsedTags.Count} tags)");

    // A Forem tag: the characters the provider accepts in the common case, without the
    // constructs that change meaning - no spaces, no separators, no mixed case.
    [GeneratedRegex("^[a-z][a-z0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();
}
