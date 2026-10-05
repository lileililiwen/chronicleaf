using Chronicleaf.AI.Contracts;

namespace Chronicleaf.AI.Anthropic;

/// <summary>
/// The fixed transport facts of one provider, and the only place they are written down
/// (LLM-ANTHROPIC-ADAPTER-001/002).
/// </summary>
/// <remarks>
/// <para>
/// These are constants rather than settings because each one is a property of the transport rather
/// than a preference. The origin is not configurable because a configurable origin is how a "BYOK"
/// provider becomes a request forwarder to a host of somebody else's choosing, and the redirect
/// refusal below exists for the same reason: the bytes an owner authorized were addressed to
/// Anthropic, and a 302 is the one way they can end up somewhere else without anybody deciding to
/// send them there.
/// </para>
/// <para>
/// <b>What is deliberately not here: a model list, a price, a context window and an availability
/// claim.</b> Anthropic adds and retires models continuously, and a compiled-in list is a claim that
/// goes stale without anybody noticing. The accepted models are deployment configuration - see
/// <see cref="AnthropicProviderSettings"/> - so an operator asserts which models this installation
/// may be pointed at, and the product never chooses one on an owner's behalf.
/// </para>
/// </remarks>
public static class AnthropicEndpoint
{
    /// <summary>The provider slot this adapter serves.</summary>
    public const string ProviderId = GenerationProviderIds.Anthropic;

    /// <summary>
    /// The one URI this adapter will ever connect to.
    /// </summary>
    /// <remarks>
    /// A constant absolute URI rather than a base plus a caller-supplied path, so nothing in this
    /// project can build a request against another host or another resource: the only way to reach
    /// the network from here is this value, and <see cref="AnthropicProvider"/> never accepts a base
    /// URL from a caller, a setting or a request. The <c>EndpointRef</c> the contract carries is
    /// ignored rather than honoured - it exists for providers that run on an operator's own
    /// endpoint, and Anthropic is not one.
    /// </remarks>
    public static readonly Uri MessagesUri = new("https://api.anthropic.com/v1/messages");

    /// <summary>
    /// The wire format label recorded with every call.
    /// </summary>
    /// <remarks>
    /// The contract's <see cref="GenerationResult"/> has no member for a request version, and that is
    /// correct rather than an oversight: it is a provider-neutral type and a provider's own version
    /// label does not belong in it. So the version is recorded where a transport's version can be
    /// recorded honestly - this adapter's log line, next to the model and the capability it used - and
    /// the correlation handle a caller gets is the provider's own message id, which the contract does
    /// carry as <see cref="GenerationResult.ProviderRequestId"/>.
    /// </remarks>
    public const string RequestVersion = "anthropic-messages-2023-06-01-2026-09-30";

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
    /// Anthropic's API key travels in <c>x-api-key</c>, not in an <c>Authorization</c> header.
    /// A header name built anywhere else would be a second spelling of the secret's location, and a
    /// second spelling is how a redaction misses one.
    /// </remarks>
    public const string ApiKeyHeaderName = "x-api-key";

    /// <summary>The API version header, written once so no call site assembles it.</summary>
    /// <remarks>
    /// The messages API versions by this header rather than by the path. Pinning it here rather than
    /// reading it from configuration keeps the dialect this adapter was verified against from drifting
    /// under a deployment: a new version is a new verification, not a new setting value.
    /// </remarks>
    public const string VersionHeaderName = "anthropic-version";

    /// <summary>The pinned API version this adapter speaks.</summary>
    public const string Version = "2023-06-01";
}
