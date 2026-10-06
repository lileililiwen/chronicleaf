using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace Chronicleaf.Publishers.Hashnode;

/// <summary>
/// The operator's explicit Hashnode target (PUBLISHER-HASHNODE-ADAPTER-001).
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
/// The only target configuration Hashnode needs beyond the leased personal access token is
/// the publication the drafts are created under: the approved snapshot carries no
/// publication of its own, and both GraphQL mutations address one explicit publication id.
/// The bound below is internal and validated until the provider reports support for more
/// shapes, so an unparseable id is a startup refusal rather than a refused publish.
/// </para>
/// <para>
/// The value stays a flat string - rather than a bound object - so the composition root's
/// <c>Configure</c> binds the one key operators actually set. Validation lives in
/// <see cref="Create"/>, which the adapter re-runs on every call: a reloaded section that
/// no longer validates is a refusal, never a send under a half-read publication.
/// </para>
/// </remarks>
/// <param name="PublicationId">The Hashnode publication the drafts are created under.</param>
public sealed partial record HashnodeOptions(string? PublicationId)
{
    /// <summary>The configuration section this target binds from.</summary>
    public const string SectionName = "Chronicleaf:Hashnode";

    /// <summary>How long a publication id may be, as an internal validated bound.</summary>
    public const int MaxPublicationIdLength = 64;

    /// <summary>
    /// The validated publication id drafts are created under. Read only after
    /// <see cref="Create"/> accepted the raw value; constructing this record directly with an
    /// unvalidated value and reading here is a programming error, not a publish.
    /// </summary>
    public string ValidatedPublicationId =>
        string.IsNullOrWhiteSpace(PublicationId)
            ? throw new InvalidOperationException("The Hashnode publication id is not configured.")
            : PublicationId.Trim();

    /// <summary>
    /// Reads the section, or null when no value names a target - the demand gate, not an error.
    /// </summary>
    public static HashnodeOptions? Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var section = configuration.GetSection(SectionName);

        // The indexer, not the binder: one flat key, no object graph, and no Binder package
        // enters a transport for it. A section that names nothing at all is the demand gate
        // answering "not configured"; anything present must validate below.
        var raw = section[nameof(PublicationId)];

        if (raw is null && !section.GetChildren().Any())
        {
            return null;
        }

        return Create(raw);
    }

    /// <summary>
    /// Validates one raw publication id into options, throwing on the first refusal.
    /// </summary>
    /// <exception cref="InvalidOperationException">A value is not one this target accepts.</exception>
    public static HashnodeOptions Create(string? publicationId)
    {
        var trimmed = publicationId?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            throw new InvalidOperationException(
                "The Hashnode publication id is required: copy the publication id from the "
                + "Hashnode dashboard instead of publishing without a target.");
        }

        if (trimmed.Length > MaxPublicationIdLength || !PublicationIdPattern().IsMatch(trimmed))
        {
            throw new InvalidOperationException(
                "A Hashnode publication id is letters, digits, dashes and underscores, at most "
                + $"{MaxPublicationIdLength} characters.");
        }

        return new HashnodeOptions(trimmed);
    }

    /// <summary>The single display line the preview binds: the target id and its publication.</summary>
    public string TargetLine() =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"hashnode publication:{ValidatedPublicationId}");

    // A Hashnode publication id as operators copy it: the characters an id carries, without
    // the constructs that change meaning - no spaces, no separators, no URLs.
    [GeneratedRegex("^[A-Za-z0-9_-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PublicationIdPattern();
}
