namespace Chronicleaf.AI.Contracts;

/// <summary>
/// One LLM adapter: the only way anything in this product reaches a provider
/// (LLM-PROVIDER-CONTRACT-001).
/// </summary>
/// <remarks>
/// <para>
/// An adapter translates one provider's dialect into the neutral contract and nothing else. It does
/// not decide what to generate, it does not read the database, it does not choose a model and it
/// does not know what a milestone is. Everything above it is server-resolved before the call, so
/// the adapter's code is the only place a provider-specific secret, endpoint or header exists, and
/// it is the only place that has to change when a provider changes.
/// </para>
/// <para>
/// <b>No implementation of this interface ships in this package.</b> The five adapters are separate
/// roadmap units, so until one of them is installed the product can list the provider slots, record
/// an owner's authorized snapshot and describe the absence - but it cannot reach a provider, and it
/// must not pretend to. A test double may implement this interface in test DI only; the production
/// composition root must never contain one, and that is asserted rather than assumed.
/// </para>
/// <para>
/// An adapter must be safe to call concurrently and must treat the token as authoritative: a
/// cancelled call must stop, and must not leave a half-written result for a caller to find.
/// </para>
/// </remarks>
public interface ILLMProvider
{
    /// <summary>
    /// The provider slot this adapter serves; one of <see cref="GenerationProviderIds"/>. This is
    /// the value callers select, so it must be exactly the slot name and not a display name.
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// What this adapter's provider can actually do, reported by the adapter. The system reads
    /// <see cref="ProviderCapability.NativeJsonSchema"/> to decide between the provider's own schema
    /// enforcement and prompt-plus-local-validation, and never assumes an answer this property does
    /// not give.
    /// </summary>
    ProviderCapability Capability { get; }

    /// <summary>
    /// Whether this adapter's provider accepts the named model.
    /// </summary>
    /// <remarks>
    /// The adapter knows its provider's models and this contract deliberately does not, so that the
    /// list cannot go stale here. An unknown model is reported by this method as unsupported before
    /// any call is made, which is why it must not answer <c>true</c> optimistically: substituting a
    /// near-miss model would silently bill the owner for a different generation than they authorized.
    /// </remarks>
    bool SupportsModel(string model);

    /// <summary>
    /// Performs one bounded, text-only generation.
    /// </summary>
    /// <remarks>
    /// The implementation must honour <see cref="GenerationRequest.MaxOutputTokens"/> and
    /// <see cref="GenerationRequest.TimeoutSeconds"/>, must send no tools, and must return a
    /// <see cref="GenerationResult"/> rather than throwing for an ordinary provider outcome. It may
    /// throw <see cref="OperationCanceledException"/> when the token is cancelled; every other
    /// failure is a result.
    /// </remarks>
    Task<GenerationResult> GenerateAsync(GenerationRequest request, CancellationToken cancellationToken);
}
