using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace Chronicleaf.Publishers.Ghost;

/// <summary>
/// The operator's explicit Ghost target (PUBLISHER-GHOST-ADAPTER-001).
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
/// A Ghost target is the publication's own origin plus the Admin API version the transport
/// speaks: every publish is one <c>POST /ghost/api/admin/posts/?source=html</c> against that
/// origin under that <c>Accept-Version</c>, carrying the approved title, the shared HTML
/// export's sanitized article body and <c>status</c> <c>draft</c> or <c>published</c> by the
/// authorized mode. The origin is the publication itself - never a multi-tenant host shared
/// with anyone else - so the leased integration key is addressed to exactly one remote side.
/// </para>
/// <para>
/// The version stays a flat <c>v{major}.{minor}</c> string - rather than a bound object - so
/// the composition root's <c>Configure</c> binds the keys operators actually set. Shape
/// validation lives in <see cref="Create"/>, which the adapter re-runs on every call: a
/// reloaded section that no longer validates is a refusal, never a send under a half-read
/// origin. Whether the named major is one this transport speaks is decided at call time by
/// <see cref="IsSupportedVersion"/> instead: an unsupported major answers the explicit
/// <c>ghost_incompatible_target</c> refusal rather than guessing an alternative endpoint.
/// </para>
/// </remarks>
/// <param name="Origin">The publication's raw HTTPS origin.</param>
/// <param name="ApiVersion">The raw Admin API version, <c>v{major}.{minor}</c>.</param>
/// <param name="Tags">The raw comma-separated default tags, or null for none.</param>
public sealed partial record GhostOptions(string? Origin, string? ApiVersion, string? Tags)
{
    /// <summary>The configuration section this target binds from.</summary>
    public const string SectionName = "Chronicleaf:Ghost";

    /// <summary>The one Admin API major this transport speaks.</summary>
    public const int SupportedApiMajor = 5;

    /// <summary>How many default tags one post may carry, as an internal validated bound.</summary>
    public const int MaxTags = 10;

    /// <summary>How long one tag name may be: the provider's own tag-name bound.</summary>
    public const int MaxTagLength = 191;

    /// <summary>
    /// The validated publication origin posts are created under. Read only after
    /// <see cref="Create"/> accepted the raw values; constructing this record directly with
    /// unvalidated values and reading here is a programming error, not a publish.
    /// </summary>
    public Uri ValidatedOrigin
    {
        get
        {
            var origin = Create(Origin, ApiVersion, Tags);
            return new Uri(origin.Origin!.Trim(), UriKind.Absolute);
        }
    }

    /// <summary>
    /// The validated Admin API version sent as <c>Accept-Version</c>. Read only after
    /// <see cref="Create"/> accepted the raw values.
    /// </summary>
    public string ValidatedApiVersion => Create(Origin, ApiVersion, Tags).ApiVersion!.Trim();

    /// <summary>
    /// The validated default tags sent with every post. Empty for none. Read only after
    /// <see cref="Create"/> accepted the raw values.
    /// </summary>
    public IReadOnlyList<string> ParsedTags =>
        string.IsNullOrWhiteSpace(Tags)
            ? []
            : Tags.Split(',').Select(tag => tag.Trim()).ToList().AsReadOnly();

    /// <summary>
    /// Reads the section, or null when no value names a target - the demand gate, not an error.
    /// </summary>
    public static GhostOptions? Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);

        // The indexer, not the binder: three flat keys, no object graph, and no Binder package
        // enters a transport for them. A section that names nothing at all is the demand gate
        // answering "not configured"; anything present must validate below.
        var origin = section[nameof(Origin)];
        var apiVersion = section[nameof(ApiVersion)];
        var tags = section[nameof(Tags)];

        if (origin is null && apiVersion is null && tags is null && !section.GetChildren().Any())
        {
            return null;
        }

        return Create(origin, apiVersion, tags);
    }

    /// <summary>
    /// Validates one raw target into options, throwing on the first refusal. Shape only: a
    /// well-formed but unsupported API major is accepted here and refused at call time with
    /// <c>ghost_incompatible_target</c>, so the receipt names the outcome instead of startup
    /// hiding which target an owner already previewed against.
    /// </summary>
    /// <exception cref="InvalidOperationException">A value is not one this target accepts.</exception>
    public static GhostOptions Create(string? origin, string? apiVersion, string? tags)
    {
        var trimmedOrigin = origin?.Trim();

        if (string.IsNullOrEmpty(trimmedOrigin))
        {
            throw new InvalidOperationException(
                "The Ghost origin is required: the https origin of the publication posts are "
                + "created under, instead of publishing without a target.");
        }

        if (!Uri.TryCreate(trimmedOrigin, UriKind.Absolute, out var parsed) ||
            !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(parsed.UserInfo))
        {
            throw new InvalidOperationException(
                "A Ghost origin is the publication's absolute https URL with no credentials in it.");
        }

        var trimmedVersion = apiVersion?.Trim();

        if (string.IsNullOrEmpty(trimmedVersion) || !ApiVersionPattern().IsMatch(trimmedVersion))
        {
            throw new InvalidOperationException(
                "A Ghost API version is v{major}.{minor}, for example v5.0: the Accept-Version "
                + "this transport speaks the provider's versioning scheme with.");
        }

        var parsedTags = string.IsNullOrWhiteSpace(tags)
            ? []
            : tags.Split(',').Select(tag => tag.Trim()).ToList();

        if (parsedTags.Any(tag => tag.Length == 0))
        {
            throw new InvalidOperationException(
                "The Ghost tags must be comma-separated without empties: no leading, trailing or "
                + "doubled separator and no blank entry.");
        }

        if (parsedTags.Count > MaxTags)
        {
            throw new InvalidOperationException(
                $"The Ghost target carries at most {MaxTags} default tags; name fewer instead.");
        }

        foreach (var tag in parsedTags)
        {
            if (tag.Length > MaxTagLength)
            {
                throw new InvalidOperationException(
                    $"A Ghost tag is at most {MaxTagLength} characters, the provider's own bound.");
            }
        }

        return new GhostOptions(
            trimmedOrigin,
            trimmedVersion,
            parsedTags.Count == 0 ? null : string.Join(",", parsedTags));
    }

    /// <summary>
    /// True for an API version this transport speaks: the supported major, any minor. The
    /// transport sends the configured minor verbatim and refuses anything else before a byte
    /// leaves, rather than guessing an alternative endpoint.
    /// </summary>
    public static bool IsSupportedVersion(string? apiVersion)
    {
        if (string.IsNullOrWhiteSpace(apiVersion))
        {
            return false;
        }

        var match = ApiVersionPattern().Match(apiVersion.Trim());

        return match.Success &&
            int.TryParse(
                match.Groups[1].Value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var major) &&
            major == SupportedApiMajor;
    }

    /// <summary>
    /// The single display line the preview binds: the target id, its origin, its version, its
    /// tags and the exact byte count of the HTML the send would carry. A template change that
    /// alters the rendered bytes alters this line, so a preview confirmed before it cannot
    /// authorize the new bytes.
    /// </summary>
    public string TargetLine(long htmlBytes) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"ghost origin:{Origin?.Trim()} version:{ApiVersion?.Trim()} tags:[{string.Join(",", ParsedTags)}] html:{htmlBytes} bytes");

    // v{major}.{minor}: the provider's Accept-Version scheme, two numeric components.
    [GeneratedRegex("^v([0-9]+)\\.([0-9]+)$", RegexOptions.CultureInvariant)]
    private static partial Regex ApiVersionPattern();
}
