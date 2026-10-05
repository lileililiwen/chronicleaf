using Chronicleaf.AI.Contracts;

namespace Chronicleaf.AI.Gemini;

/// <summary>
/// The fixed transport facts of one provider, and the only place they are written down
/// (LLM-GEMINI-ADAPTER-001/002).
/// </summary>
/// <remarks>
/// <para>
/// These are constants rather than settings because each one is a property of the transport rather
/// than a preference. The origin is not configurable because a configurable origin is how a "BYOK"
/// provider becomes a request forwarder to a host of somebody else's choosing, and the redirect
/// refusal below exists for the same reason: the bytes an owner authorized were addressed to
/// Google, and a 302 is the one way they can end up somewhere else without anybody deciding to send
/// them there.
/// </para>
/// <para>
/// <b>What is deliberately not here: a model list, a price, a context window and an availability
/// claim.</b> Google adds and retires models continuously, and a compiled-in list is a claim that
/// goes stale without anybody noticing. The accepted models are deployment configuration - see
/// <see cref="GeminiProviderSettings"/> - so an operator asserts which models this installation may
/// be pointed at, and the product never chooses one on an owner's behalf.
/// </para>
/// </remarks>
public static class GeminiEndpoint
{
    /// <summary>The provider slot this adapter serves.</summary>
    public const string ProviderId = GenerationProviderIds.Gemini;

    /// <summary>
    /// The one origin this adapter will ever connect to. The model is addressed as a path segment
    /// under it; see <see cref="GenerateContentUri"/>.
    /// </summary>
    /// <remarks>
    /// A constant base URI rather than a string, so nothing in this project can build a request
    /// against another host: the only way to reach the network from here is this value combined with
    /// an accepted model name, and <see cref="GeminiProvider"/> never accepts a base URL from a
    /// caller, a setting or a request. The <c>EndpointRef</c> the contract carries is ignored rather
    /// than honoured - it exists for providers that run on an operator's own endpoint, and Gemini is
    /// not one.
    /// </remarks>
    public static readonly Uri BaseUri = new("https://generativelanguage.googleapis.com/v1beta/");

    /// <summary>
    /// Builds the generateContent URI for one accepted model.
    /// </summary>
    /// <remarks>
    /// The model is escaped as a single path segment, so a configured name cannot smuggle a second
    /// path, a query or an authority into the request line: a name that is not one segment addresses
    /// nothing the provider serves, and the provider's answer to that is an unusable response rather
    /// than a call to somewhere else. The allowlist already bounds the name's length; this bounds its
    /// shape.
    /// </remarks>
    public static Uri GenerateContentUri(string model) =>
        new(BaseUri, $"models/{Uri.EscapeDataString(model)}:generateContent");

    /// <summary>
    /// The wire format label recorded with every call.
    /// </summary>
    /// <remarks>
    /// The contract's <see cref="GenerationResult"/> has no member for a request version, and that is
    /// correct rather than an oversight: it is a provider-neutral type and a provider's own version
    /// label does not belong in it. So the version is recorded where a transport's version can be
    /// recorded honestly - this adapter's log line, next to the model and the capability it used - and
    /// the correlation handle a caller gets would be the provider's own request id, which the contract
    /// does carry. Gemini's generateContent answer carries no request id, so none is recorded; a
    /// future contract revision could add a field, and this package does not get to add one to a type
    /// it depends on.
    /// </remarks>
    public const string RequestVersion = "gemini-generateContent-v1beta-2026-09-30";

    /// <summary>
    /// The ceiling on a provider response body: 2 MiB.
    /// </summary>
    /// <remarks>
    /// A cap rather than a read, so an oversized or hostile answer is refused while it is still being
    /// read rather than after it has been buffered. It is generous next to the largest legitimate
    /// answer this product can ask for - the output ceiling is 4096 tokens - so hitting it means
    /// something other than a model answered.
    /// </remarks>
    public const int MaxResponseBytes = 2 * 1024 * 1024;

    /// <summary>The credential header, written once so no call site assembles it.</summary>
    /// <remarks>
    /// Gemini's API key travels in <c>x-goog-api-key</c>, not in an <c>Authorization</c> header.
    /// A header name built anywhere else would be a second spelling of the secret's location, and a
    /// second spelling is how a redaction misses one.
    /// </remarks>
    public const string ApiKeyHeaderName = "x-goog-api-key";
}
