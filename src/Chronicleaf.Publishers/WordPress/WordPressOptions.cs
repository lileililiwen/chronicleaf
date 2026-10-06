using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace Chronicleaf.Publishers.WordPress;

/// <summary>
/// The operator's explicit WordPress target (PUBLISHER-WORDPRESS-ADAPTER-001).
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
/// A WordPress target is the self-hosted site's own HTTPS origin plus the user the leased
/// application password belongs to: every publish is one <c>POST /wp-json/wp/v2/posts</c>
/// against that origin under that user's Basic credential, carrying the approved title, the
/// shared HTML export's sanitized article body and <c>status</c> <c>draft</c> or
/// <c>publish</c> by the authorized mode. The origin is the site itself - never a
/// multi-tenant host shared with anyone else - so the leased credential is addressed to
/// exactly one remote side. The WordPress.com API is not implicitly interchangeable with
/// this endpoint: a .com site is a different remote side with a different authentication
/// scheme, and pointing this target at one is a misconfiguration the refused publish names.
/// </para>
/// <para>
/// The taxonomy stays two flat comma-separated id lists - rather than bound objects - so the
/// composition root's <c>Configure</c> binds the keys operators actually set. Parsing and
/// validation live in <see cref="Create"/>, which the adapter re-runs on every call: a
/// reloaded section that no longer validates is a refusal, never a send under a half-read
/// origin. The application password itself is never configured here: it arrives per call as
/// a leased vault secret, so no configuration file ever names one.
/// </para>
/// </remarks>
/// <param name="Origin">The site's raw HTTPS origin.</param>
/// <param name="Username">The raw WordPress user the application password belongs to.</param>
/// <param name="Slug">The raw default post slug, or null for the provider-generated slug.</param>
/// <param name="Categories">The raw comma-separated category ids, or null for none.</param>
/// <param name="Tags">The raw comma-separated tag ids, or null for none.</param>
public sealed partial record WordPressOptions(
    string? Origin,
    string? Username,
    string? Slug,
    string? Categories,
    string? Tags)
{
    /// <summary>The configuration section this target binds from.</summary>
    public const string SectionName = "Chronicleaf:WordPress";

    /// <summary>How long one username may be, as an internal validated bound.</summary>
    public const int MaxUsernameLength = 60;

    /// <summary>How long the default slug may be, as an internal validated bound.</summary>
    public const int MaxSlugLength = 200;

    /// <summary>How many terms one list may carry, as an internal validated bound.</summary>
    public const int MaxTerms = 50;

    /// <summary>
    /// The validated site origin posts are created under. Read only after
    /// <see cref="Create"/> accepted the raw values; constructing this record directly with
    /// unvalidated values and reading here is a programming error, not a publish.
    /// </summary>
    public Uri ValidatedOrigin
    {
        get
        {
            var origin = Create(Origin, Username, Slug, Categories, Tags);
            return new Uri(origin.Origin!.Trim(), UriKind.Absolute);
        }
    }

    /// <summary>
    /// The validated user the application password belongs to. Read only after
    /// <see cref="Create"/> accepted the raw values.
    /// </summary>
    public string ValidatedUsername => Create(Origin, Username, Slug, Categories, Tags).Username!.Trim();

    /// <summary>
    /// The validated default slug, or null for the provider-generated slug. Read only after
    /// <see cref="Create"/> accepted the raw values.
    /// </summary>
    public string? ValidatedSlug
    {
        get
        {
            var slug = Create(Origin, Username, Slug, Categories, Tags).Slug;
            return string.IsNullOrWhiteSpace(slug) ? null : slug.Trim();
        }
    }

    /// <summary>
    /// The validated default category ids. Empty for none. Read only after
    /// <see cref="Create"/> accepted the raw values.
    /// </summary>
    public IReadOnlyList<int> ParsedCategories => ParseTerms(Categories);

    /// <summary>
    /// The validated default tag ids. Empty for none. Read only after
    /// <see cref="Create"/> accepted the raw values.
    /// </summary>
    public IReadOnlyList<int> ParsedTags => ParseTerms(Tags);

    /// <summary>
    /// Reads the section, or null when no value names a target - the demand gate, not an error.
    /// </summary>
    public static WordPressOptions? Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);

        // The indexer, not the binder: five flat keys, no object graph, and no Binder package
        // enters a transport for them. A section that names nothing at all is the demand gate
        // answering "not configured"; anything present must validate below.
        var origin = section[nameof(Origin)];
        var username = section[nameof(Username)];
        var slug = section[nameof(Slug)];
        var categories = section[nameof(Categories)];
        var tags = section[nameof(Tags)];

        if (origin is null && username is null && slug is null &&
            categories is null && tags is null && !section.GetChildren().Any())
        {
            return null;
        }

        return Create(origin, username, slug, categories, tags);
    }

    /// <summary>
    /// Validates one raw target into options, throwing on the first refusal.
    /// </summary>
    /// <exception cref="InvalidOperationException">A value is not one this target accepts.</exception>
    public static WordPressOptions Create(
        string? origin,
        string? username,
        string? slug,
        string? categories,
        string? tags)
    {
        var trimmedOrigin = origin?.Trim();

        if (string.IsNullOrEmpty(trimmedOrigin))
        {
            throw new InvalidOperationException(
                "The WordPress origin is required: the https origin of the site posts are "
                + "created under, instead of publishing without a target.");
        }

        if (!Uri.TryCreate(trimmedOrigin, UriKind.Absolute, out var parsed) ||
            !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(parsed.UserInfo))
        {
            throw new InvalidOperationException(
                "A WordPress origin is the site's absolute https URL with no credentials in it.");
        }

        var trimmedUsername = username?.Trim();

        if (string.IsNullOrEmpty(trimmedUsername))
        {
            throw new InvalidOperationException(
                "The WordPress username is required: the user the leased application password "
                + "belongs to, instead of authenticating as nobody.");
        }

        if (trimmedUsername.Length > MaxUsernameLength ||
            trimmedUsername.Any(char.IsWhiteSpace) ||
            trimmedUsername.Contains(':'))
        {
            throw new InvalidOperationException(
                $"A WordPress username is at most {MaxUsernameLength} characters with no whitespace "
                + "and no colon: it travels inside one Basic credential pair.");
        }

        var trimmedSlug = slug?.Trim();

        if (!string.IsNullOrEmpty(trimmedSlug) &&
            (trimmedSlug.Length > MaxSlugLength || !SlugPattern().IsMatch(trimmedSlug)))
        {
            throw new InvalidOperationException(
                "A WordPress slug is lowercase letters, digits and single hyphens, starting and "
                + $"ending with a letter or digit, at most {MaxSlugLength} characters.");
        }

        var parsedCategories = ParseTerms(categories, nameof(Categories));
        var parsedTags = ParseTerms(tags, nameof(Tags));

        return new WordPressOptions(
            trimmedOrigin,
            trimmedUsername,
            string.IsNullOrEmpty(trimmedSlug) ? null : trimmedSlug,
            parsedCategories.Count == 0 ? null : string.Join(",", parsedCategories),
            parsedTags.Count == 0 ? null : string.Join(",", parsedTags));
    }

    private static IReadOnlyList<int> ParseTerms(string? raw, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var segments = raw.Split(',').Select(term => term.Trim()).ToList();

        if (segments.Any(term => term.Length == 0))
        {
            throw new InvalidOperationException(
                $"The WordPress {name ?? "terms"} must be comma-separated ids without empties: no "
                + "leading, trailing or doubled separator and no blank entry.");
        }

        if (segments.Count > MaxTerms)
        {
            throw new InvalidOperationException(
                $"The WordPress {name ?? "terms"} carries at most {MaxTerms} ids; name fewer instead.");
        }

        var parsed = new List<int>(segments.Count);

        foreach (var segment in segments)
        {
            if (!int.TryParse(
                    segment,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var id) ||
                id <= 0)
            {
                throw new InvalidOperationException(
                    $"A WordPress {name ?? "term"} is a positive taxonomy id the site already defines.");
            }

            parsed.Add(id);
        }

        return parsed.AsReadOnly();
    }

    /// <summary>
    /// The single display line the preview binds: the target id, its origin, its user, its
    /// slug and taxonomy, and the exact byte count of the HTML the send would carry. A
    /// template change that alters the rendered bytes alters this line, so a preview confirmed
    /// before it cannot authorize the new bytes.
    /// </summary>
    public string TargetLine(long htmlBytes) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"wordpress origin:{Origin?.Trim()} user:{Username?.Trim()} slug:{Slug?.Trim() ?? "-"} categories:[{string.Join(",", ParseTerms(Categories))}] tags:[{string.Join(",", ParseTerms(Tags))}] html:{htmlBytes} bytes");

    // A sanitized post slug: the shape the provider itself enforces on write.
    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();
}
