using Chronicleaf.AI.Contracts;

namespace Chronicleaf.AI.Ollama;

/// <summary>
/// The fixed transport facts of the Ollama provider, and the only place they are written down
/// (LLM-OLLAMA-ADAPTER-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The path is constant but the origin is not, which is what makes this adapter different from the
/// four public-provider transports. Ollama runs on hardware the operator names - almost always the
/// same machine - so there is no published origin to pin. What is pinned instead is the resource
/// (<see cref="ChatPath"/>) and the rule that the origin comes from the operator's own allowlist
/// (see <see cref="OllamaProviderSettings"/>): the adapter can only ever address
/// <c>{allowlisted-origin}/api/chat</c>, never a host a caller, a setting or a request invented.
/// </para>
/// <para>
/// <b>What is deliberately not here: a default origin, a model list, a price, a context window and
/// an availability claim.</b> A compiled-in origin (even <c>localhost:11434</c>) would be a claim
/// that an untouched deployment can call something, and it cannot - the allowlist is empty unless
/// an operator filled it in, and an empty allowlist refuses every endpoint before a socket is
/// opened. The accepted models and endpoints are deployment configuration, so an operator asserts
/// which models and which local origins this installation may be pointed at, and the product never
/// chooses either on an owner's behalf.
/// </para>
/// </remarks>
public static class OllamaEndpoint
{
    /// <summary>The provider slot this adapter serves.</summary>
    public const string ProviderId = GenerationProviderIds.Ollama;

    /// <summary>
    /// The one resource this adapter will ever post to, under an allowlisted origin.
    /// </summary>
    /// <remarks>
    /// A constant path rather than a caller-supplied one, so nothing in this project can build a
    /// request against another resource: no model pulling, no tag listing, no metadata endpoint.
    /// Combined with the origin allowlist, the only URI this project can reach the network with is
    /// <c>{allowlisted-origin}/api/chat</c>.
    /// </remarks>
    public const string ChatPath = "/api/chat";

    /// <summary>
    /// Builds the chat URI under an allowlisted base.
    /// </summary>
    /// <param name="baseUri">An origin the operator allowlisted, already validated.</param>
    public static Uri ChatUri(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);

        return new Uri(baseUri, ChatPath);
    }

    /// <summary>
    /// The wire format label recorded with every call.
    /// </summary>
    /// <remarks>
    /// The contract's <see cref="GenerationResult"/> has no member for a request version, and that is
    /// correct rather than an oversight: it is a provider-neutral type and a provider's own version
    /// label does not belong in it. So the version is recorded where a transport's version can be
    /// recorded honestly - this adapter's log line, next to the model and the capability it used - and
    /// Ollama supplies no request id of its own, so the result carries none rather than an invented
    /// one (see <see cref="OllamaProvider"/>).
    /// </remarks>
    public const string RequestVersion = "ollama-chat-2026-09-30";

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
}
