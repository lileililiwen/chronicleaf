namespace Chronicleaf.AI.Ollama;

/// <summary>
/// What this deployment will let an owner point the Ollama adapter at
/// (LLM-OLLAMA-ADAPTER-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Both lists are configuration, and both are empty unless an operator filled them in.</b> An
/// empty model list means <see cref="Contracts.ILLMProvider.SupportsModel"/> answers false for
/// every model, and an empty endpoint list means no origin is accepted - so the product refuses
/// the selection with <c>model_not_supported</c>, or the call with an unusable result, before a
/// credential is leased and before a socket is opened. That is the honest state of a transport
/// nobody has pointed at anything, not a missing feature.
/// </para>
/// <para>
/// The product never chooses a model or an endpoint. The owner names both, the adapter refuses
/// anything outside these lists rather than substituting a near miss, and a near miss would
/// silently bill the owner - in time, if not in money - for a different generation than the one
/// they authorized and read. There is no fallback routing either: an unusable answer is never
/// retried against a different model, a different endpoint or a different vendor, because the
/// consent named this one.
/// </para>
/// <para>
/// <b>Every entry is an origin an operator reviewed.</b> A local loopback or private endpoint is
/// permitted only by being named here - there is no implicit allowance for <c>localhost</c>, because
/// an implicit allowance is how a "local" provider becomes a request forwarder to whatever a name
/// happens to resolve to. Combined with the constant <see cref="OllamaEndpoint.ChatPath"/>, the
/// allowlist is the complete set of URIs this deployment may address, and a redirect that arrives
/// anyway is a refusal rather than a second hop.
/// </para>
/// </remarks>
public sealed class OllamaProviderSettings
{
    /// <summary>The configuration section these settings bind to.</summary>
    public const string SectionName = "Chronicleaf:Ollama";

    /// <summary>The widest model allowlist, so a misconfigured section cannot become a lookup table.</summary>
    public const int MaxAllowedModels = 64;

    /// <summary>The widest endpoint allowlist. An operator names one Ollama, not a fleet of them.</summary>
    public const int MaxAllowedEndpoints = 16;

    /// <summary>
    /// The longest model name an entry may have, 128 characters.
    /// </summary>
    /// <remarks>
    /// The same width the contract's own caller validates a model string to, restated here because
    /// this project references the contract and not the service that enforces that width - and because
    /// a configuration file is still an input. It is a validation bound, not a second source of truth:
    /// a name this accepts is still refused by the product if the request-side validation rejects it.
    /// </remarks>
    public const int MaxModelNameLength = 128;

    /// <summary>
    /// The longest endpoint entry, 200 characters: the same width the contract's own caller
    /// validates an endpoint reference to.
    /// </summary>
    public const int MaxEndpointLength = 200;

    /// <summary>
    /// The models this installation will accept, exactly as Ollama names them.
    /// </summary>
    /// <remarks>
    /// A constructor parameter rather than a settable property, and that is load-bearing: the
    /// normalised set below is built once here, and an <c>init</c> property assigned by an object
    /// initializer runs <i>after</i> the constructor - so a settable property would build the set
    /// from the default empty list and then appear to accept nothing, which is a silent, confusing
    /// way to fail a safety property.
    /// </remarks>
    public IReadOnlyList<string> AllowedModels { get; }

    /// <summary>The accepted models, normalised and validated once at construction.</summary>
    /// <remarks>
    /// Compared ordinally and case-sensitively, because a model name is an identifier rather than a
    /// label. A blank name, an over-long name, a duplicate and an over-long list are all refused: each
    /// is a configuration that would otherwise behave in a way its author did not intend.
    /// </remarks>
    public ISet<string> ModelAllowlist { get; }

    /// <summary>
    /// The origins this installation will address, as the operator wrote them.
    /// </summary>
    public IReadOnlyList<string> AllowedEndpoints { get; }

    /// <summary>
    /// The accepted origins, normalised once at construction for the adapter's lookup.
    /// </summary>
    /// <remarks>
    /// Each entry is the scheme and authority of an absolute HTTP(S) URI, lowercased and without a
    /// trailing slash, so <c>HTTP://127.0.0.1:11434/</c> and <c>http://127.0.0.1:11434</c> name the
    /// same origin rather than one of them slipping past the list. Only <c>http</c> and
    /// <c>https</c> are accepted: anything else is not an Ollama to address.
    /// </remarks>
    public ISet<string> EndpointAllowlist { get; }

    /// <param name="allowedModels">
    /// The models this deployment accepts. Empty or omitted refuses every model, which is the default
    /// and the honest state of a transport nobody has pointed at anything.
    /// </param>
    /// <param name="allowedEndpoints">
    /// The origins this deployment may address, e.g. <c>http://127.0.0.1:11434</c>. Empty or omitted
    /// refuses every endpoint. Entries name origins; the chat path is the adapter's own constant.
    /// </param>
    public OllamaProviderSettings(
        IReadOnlyList<string>? allowedModels = null,
        IReadOnlyList<string>? allowedEndpoints = null)
    {
        var models = allowedModels ?? [];
        ArgumentOutOfRangeException.ThrowIfGreaterThan(models.Count, MaxAllowedModels, nameof(allowedModels));

        var acceptedModels = new HashSet<string>(StringComparer.Ordinal);

        foreach (var model in models)
        {
            if (string.IsNullOrWhiteSpace(model))
            {
                throw new ArgumentException("An allowed model cannot be blank.", nameof(allowedModels));
            }

            if (model.Length > MaxModelNameLength)
            {
                throw new ArgumentException(
                    "An allowed model is longer than the contract's model ceiling.",
                    nameof(allowedModels));
            }

            if (!acceptedModels.Add(model))
            {
                throw new ArgumentException(
                    "The same model is allowed twice; a duplicated entry hides which spelling was meant.",
                    nameof(allowedModels));
            }
        }

        var endpoints = allowedEndpoints ?? [];
        ArgumentOutOfRangeException.ThrowIfGreaterThan(endpoints.Count, MaxAllowedEndpoints, nameof(allowedEndpoints));

        var acceptedEndpoints = new HashSet<string>(StringComparer.Ordinal);

        foreach (var endpoint in endpoints)
        {
            var origin = NormalizeOrigin(endpoint);

            if (!acceptedEndpoints.Add(origin))
            {
                throw new ArgumentException(
                    "The same endpoint is allowed twice; a duplicated entry hides which spelling was meant.",
                    nameof(allowedEndpoints));
            }
        }

        AllowedModels = [.. acceptedModels];
        ModelAllowlist = acceptedModels;
        AllowedEndpoints = [.. acceptedEndpoints];
        EndpointAllowlist = acceptedEndpoints;
    }

    /// <summary>
    /// True when <paramref name="endpointRef"/> names an allowlisted origin.
    /// </summary>
    /// <remarks>
    /// The reference is normalised the same way the list was, so spelling is not a way around it -
    /// and a reference carrying a path, a query or credentials is still decided by its origin,
    /// because the adapter addresses the origin's chat resource whatever else the string says.
    /// A reference that is not an absolute HTTP(S) URI is not an endpoint at all and never matches.
    /// </remarks>
    public bool IsEndpointAllowed(string? endpointRef)
    {
        if (string.IsNullOrWhiteSpace(endpointRef))
        {
            return false;
        }

        if (!TryNormalizeOrigin(endpointRef, out var origin) || origin is null)
        {
            return false;
        }

        return EndpointAllowlist.Contains(origin);
    }

    /// <summary>
    /// The origin of <paramref name="endpointRef"/>, normalised for comparison.
    /// </summary>
    public static string NormalizeOrigin(string endpoint)
    {
        if (!TryNormalizeOrigin(endpoint, out var origin) || origin is null)
        {
            throw new ArgumentException(
                "An allowed endpoint must be an absolute http or https URI.",
                nameof(endpoint));
        }

        return origin;
    }

    private static bool TryNormalizeOrigin(string endpoint, out string? origin)
    {
        origin = null;

        if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Length > MaxEndpointLength)
        {
            return false;
        }

        if (!Uri.TryCreate(endpoint.Trim(), UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        origin = uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.Unescaped).TrimEnd('/');

        return true;
    }
}
