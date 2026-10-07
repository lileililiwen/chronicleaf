using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace Chronicleaf.Publishers.CustomBlog;

/// <summary>
/// The operator's explicit custom-blog target (PUBLISHER-CUSTOM-BLOG-ADAPTER-001).
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
/// A custom-blog target is one fixed HTTPS origin speaking the optional Chronicleaf Publisher
/// HTTP v1 contract the transport declares: <c>GET /chronicleaf/v1/capabilities</c> must
/// advertise <c>schemaVersion</c> 1, <c>idempotentWrites</c> true and a content format this
/// adapter sends, and every publish is one <c>POST /chronicleaf/v1/posts</c> against that
/// same origin carrying the approved title and Markdown with <c>mode</c> <c>draft</c> or
/// <c>publish</c> by the authorized mode. The origin is fixed at configuration time - never
/// a per-request host, never a URL template - so the leased credential is addressed to
/// exactly one remote side. The credential itself is never configured here: it arrives per
/// call as a leased vault secret, so no configuration file ever names one.
/// </para>
/// <para>
/// The formats stay a flat comma-separated allowlist - rather than a bound list - so the
/// composition root's <c>Configure</c> binds the key operators actually set. Parsing and
/// validation live in <see cref="Create"/>, which the adapter re-runs on every call: a
/// reloaded section that no longer validates is a refusal, never a send under a half-read
/// origin. The allowlist must name <c>markdown</c> - the only format this adapter sends -
/// so an allowlist that cannot authorize any send fails startup instead of failing every
/// publish. Whether the server advertises a format on the allowlist is decided at call time
/// against its capabilities document instead: an incompatible server answers the explicit
/// <c>customblog_incompatible_remote_contract</c> refusal rather than a guessed post.
/// </para>
/// </remarks>
/// <param name="Origin">The server's raw HTTPS origin.</param>
/// <param name="Formats">The raw comma-separated content-format allowlist, or null for the default.</param>
public sealed partial record CustomBlogOptions(
    string? Origin,
    string? Formats)
{
    /// <summary>
    /// The binder's construction: the composition root binds the configured section through
    /// <c>Configure</c>, which builds options through a parameterless constructor. Without
    /// this, a configured section registers the target and then answers every preview and
    /// publish against it with a 500, because the options factory cannot build the record.
    /// Values still validate on every call through <c>Create</c>, so a bound-but-invalid
    /// section is refused rather than served.
    /// </summary>
    public CustomBlogOptions()
        : this(Origin: null, Formats: null)
    {
    }

    /// <summary>The configuration section this target binds from.</summary>
    public const string SectionName = "Chronicleaf:CustomBlog";

    /// <summary>The content format this adapter sends, and the default allowlist.</summary>
    public const string MarkdownFormat = "markdown";

    /// <summary>How many formats the allowlist may carry, as an internal validated bound.</summary>
    public const int MaxFormats = 10;

    /// <summary>How long one format token may be, as an internal validated bound.</summary>
    public const int MaxFormatLength = 32;

    /// <summary>
    /// The validated server origin posts are created under. Read only after
    /// <see cref="Create"/> accepted the raw values; constructing this record directly with
    /// unvalidated values and reading here is a programming error, not a publish.
    /// </summary>
    public Uri ValidatedOrigin
    {
        get
        {
            var origin = Create(Origin, Formats);
            return new Uri(origin.Origin!.Trim(), UriKind.Absolute);
        }
    }

    /// <summary>
    /// The validated content-format allowlist, normalized to lowercase without duplicates.
    /// Read only after <see cref="Create"/> accepted the raw values.
    /// </summary>
    public IReadOnlyList<string> ParsedFormats => ParseFormats(Formats);

    /// <summary>
    /// Reads the section, or null when no value names a target - the demand gate, not an error.
    /// </summary>
    public static CustomBlogOptions? Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);

        // The indexer, not the binder: two flat keys, no object graph, and no Binder package
        // enters a transport for them. A section that names nothing at all is the demand gate
        // answering "not configured"; anything present must validate below.
        var origin = section[nameof(Origin)];
        var formats = section[nameof(Formats)];

        if (origin is null && formats is null && !section.GetChildren().Any())
        {
            return null;
        }

        return Create(origin, formats);
    }

    /// <summary>
    /// Validates one raw target into options, throwing on the first refusal. Shape only: a
    /// well-formed origin whose server speaks another contract is accepted here and refused
    /// at call time with <c>customblog_incompatible_remote_contract</c>, so the receipt names
    /// the outcome instead of startup hiding which target an owner already previewed against.
    /// </summary>
    /// <exception cref="InvalidOperationException">A value is not one this target accepts.</exception>
    public static CustomBlogOptions Create(string? origin, string? formats)
    {
        var trimmedOrigin = origin?.Trim();

        if (string.IsNullOrEmpty(trimmedOrigin))
        {
            throw new InvalidOperationException(
                "The custom-blog origin is required: the https origin of the server posts are "
                + "created under, instead of publishing without a target.");
        }

        if (!Uri.TryCreate(trimmedOrigin, UriKind.Absolute, out var parsed) ||
            !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(parsed.UserInfo))
        {
            throw new InvalidOperationException(
                "A custom-blog origin is the server's absolute https URL with no credentials in it.");
        }

        var parsedFormats = ParseFormats(formats, throwOnInvalid: true);

        if (!parsedFormats.Contains(MarkdownFormat, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                "The custom-blog formats allowlist must name markdown: it is the only content "
                + "format this adapter sends, so an allowlist without it could never authorize a send.");
        }

        return new CustomBlogOptions(
            trimmedOrigin,
            parsedFormats.Count == 0 ? null : string.Join(",", parsedFormats));
    }

    private static IReadOnlyList<string> ParseFormats(string? raw, bool throwOnInvalid = false)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [MarkdownFormat];
        }

        var segments = raw.Split(',').Select(token => token.Trim().ToLowerInvariant()).ToList();

        if (segments.Any(token => token.Length == 0))
        {
            throw new InvalidOperationException(
                "The custom-blog formats must be comma-separated tokens without empties: no "
                + "leading, trailing or doubled separator and no blank entry.");
        }

        if (segments.Count > MaxFormats)
        {
            throw new InvalidOperationException(
                $"The custom-blog formats carry at most {MaxFormats} entries; name fewer instead.");
        }

        foreach (var token in segments)
        {
            if (token.Length > MaxFormatLength || !FormatPattern().IsMatch(token))
            {
                throw new InvalidOperationException(
                    "A custom-blog format is a lowercase content-type token such as markdown: "
                    + "letters, digits and interior dots, pluses and hyphens.");
            }
        }

        var distinct = segments.Distinct(StringComparer.Ordinal).ToList();

        if (throwOnInvalid)
        {
            return distinct.AsReadOnly();
        }

        return distinct.AsReadOnly();
    }

    /// <summary>
    /// The single display line the preview binds: the target id, its origin, its format
    /// allowlist, and the exact byte count of the Markdown the send would carry. A template
    /// change that alters the sent bytes alters this line, so a preview confirmed before it
    /// cannot authorize the new bytes.
    /// </summary>
    public string TargetLine(long markdownBytes) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"custom-blog origin:{Origin?.Trim()} formats:[{string.Join(",", ParseFormats(Formats))}] markdown:{markdownBytes} bytes");

    // A content-format token: the shape a capabilities document advertises formats in.
    [GeneratedRegex("^[a-z0-9]+(?:[+.-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex FormatPattern();
}
