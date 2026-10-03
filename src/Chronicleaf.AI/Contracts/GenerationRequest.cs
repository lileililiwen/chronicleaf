using System.Diagnostics.CodeAnalysis;

namespace Chronicleaf.AI.Contracts;

/// <summary>
/// One bounded, text-only generation request, already resolved by the server
/// (LLM-PROVIDER-CONTRACT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// An adapter receives exactly this and one cancellation token. Everything the request carries was
/// decided before the adapter was chosen: the provider, the model, the credential reference, the
/// bounded limits, the trusted instruction and the untrusted evidence payload. The adapter's only
/// remaining freedom is how it speaks its provider's dialect, so a transport cannot quietly widen a
/// call, re-read the database, or renegotiate what was authorized.
/// </para>
/// <para>
/// <b>There is no tools field, no function-calling field, no image or file field and no
/// conversation history, and their absence is the feature.</b> Generation in this product is
/// drafting text an owner reviews, not acting. A tool field would make every call a prompt
/// injection target with a side-effect channel; a history field would let a caller smuggle turns in
/// past the consent that authorized one snapshot. If a future capability needs either, it needs a
/// different contract and a new spec, not a nullable property here.
/// </para>
/// <para>
/// The type is deliberately free of any Chronicleaf dependency. The credential reference is the
/// vault's identifier rather than the vault's reference type so that an adapter can be written,
/// tested and shipped without the credential vault, and so that no secret - not even a sealed
/// handle to one - can travel inside a request that gets logged, queued or serialised.
/// </para>
/// </remarks>
public sealed record GenerationRequest
{
    /// <summary>
    /// This request's identifier, for audit correlation. Carries no meaning to a provider.
    /// </summary>
    /// <remarks>
    /// A bounded retry re-sends the same request, so the same id, rather than minting one per
    /// transport attempt. A retry is a second attempt at one authorized request and not a second
    /// request, and a stable identifier is what lets a caller reconcile both attempts against one
    /// line on a provider's invoice rather than against two unrelated ones.
    /// </remarks>
    public required Guid RequestId { get; init; }

    /// <summary>
    /// The Chronicleaf scope the call is made on behalf of. Carried so an adapter can tag its own
    /// provider-side request id against the scope that owns it; never sent to a provider.
    /// </summary>
    public required Guid ScopeId { get; init; }

    /// <summary>One of <see cref="GenerationProviderIds"/>, the slot this request is bound to.</summary>
    public required string ProviderId { get; init; }

    /// <summary>
    /// The model, exactly as the owner named it. The caller has already checked it against the
    /// adapter's own <see cref="ILLMProvider.SupportsModel"/>, so an adapter may treat an
    /// unrecognised model as an error to report rather than a name to substitute.
    /// </summary>
    public required string Model { get; init; }

    /// <summary>
    /// The identifier of the stored credential to use, or <c>null</c> when the provider needs none.
    /// A reference, never a secret: the adapter is responsible for leasing the secret from the
    /// vault at the moment of use and for disposing that lease before it returns.
    /// </summary>
    public Guid? CredentialRef { get; init; }

    /// <summary>
    /// The owner's endpoint for a <c>localEndpoint</c> provider, or <c>null</c>. Adapters for public
    /// providers must ignore a value here rather than redirect the call.
    /// </summary>
    public string? EndpointRef { get; init; }

    /// <summary>
    /// The trusted instruction. This is the only text in the request that is treated as
    /// instructions; everything else in the call is data to be handled as data.
    /// </summary>
    public required string SystemInstruction { get; init; }

    /// <summary>
    /// The untrusted evidence payload, already redacted by the owner-facing flow and frozen into
    /// the authorized snapshot. Canonical JSON. An adapter must embed it as quoted data and must not
    /// execute, resolve or fetch anything inside it.
    /// </summary>
    public required string UntrustedEvidenceJson { get; init; }

    /// <summary>
    /// The canonical JSON Schema the output must satisfy, as text. A
    /// <c>nativeJsonSchema</c> provider may hand it to the provider; one without must be told the
    /// schema in the prompt and must have its output validated locally. Passing the schema does not
    /// move responsibility for validation onto the provider.
    /// </summary>
    public required string ResponseSchema { get; init; }

    /// <summary>
    /// The hard ceiling on generated output, already at or below the system limit. An adapter must
    /// pass it to the provider rather than letting the provider's own default apply.
    /// </summary>
    public required int MaxOutputTokens { get; init; }

    /// <summary>
    /// The wall-clock budget for the attempt. The caller has already started it, so an adapter must
    /// bound its own call by <paramref name="CancellationToken"/> rather than inventing a longer
    /// timeout.
    /// </summary>
    public required int TimeoutSeconds { get; init; }
}

/// <summary>
/// The four outcomes a generation can have (LLM-PROVIDER-CONTRACT-001).
/// </summary>
/// <remarks>
/// They are distinct because collapsing them is how a draft system starts presenting a failure as a
/// draft: a refusal is the model declining, an invalid result is the model producing something
/// unusable, and a failure is the call not completing. Only <see cref="Completed"/> ever carries
/// text an owner can review.
/// </remarks>
public static class GenerationStatuses
{
    /// <summary>The provider returned text that passed validation. The only status that carries text.</summary>
    public const string Completed = "completed";

    /// <summary>The provider declined to generate. Not an error to retry; a different answer.</summary>
    public const string Refused = "refused";

    /// <summary>
    /// The provider answered, but the answer is not usable: malformed, empty, or a tool call in a
    /// text-only contract. The text, if any, is never presented as a draft.
    /// </summary>
    public const string Invalid = "invalid";

    /// <summary>The call did not complete. Nothing usable was produced.</summary>
    public const string Failed = "failed";

    public static readonly IReadOnlyList<string> All = [Completed, Refused, Invalid, Failed];

    public static bool IsKnown([NotNullWhen(true)] string? status) =>
        status is not null && All.Contains(status, StringComparer.Ordinal);
}

/// <summary>
/// Why the provider stopped, in vocabulary that survives five dialects.
/// </summary>
/// <remarks>
/// Every provider names its stop reasons differently; a contract that carried a provider's own
/// string would force an adapter to leak it upward. These are the distinctions the product actually
/// acts on.
/// </remarks>
public static class GenerationFinishReasons
{
    /// <summary>The model finished on its own terms.</summary>
    public const string Stop = "stop";

    /// <summary>
    /// The output ceiling was reached. The text is truncated and is reported as
    /// <see cref="GenerationStatuses.Completed"/> with this reason, so a caller can refuse a short
    /// draft rather than present half a story as if it were whole.
    /// </summary>
    public const string Length = "length";

    /// <summary>The provider stopped because of its own safety policy, not the caller's content.</summary>
    public const string ContentFilter = "content_filter";

    /// <summary>
    /// The provider asked to call a tool. In a text-only contract this is
    /// <see cref="GenerationStatuses.Invalid"/>: the name is kept so the refusal is legible instead
    /// of looking like a malformed response, and so an adapter cannot pass a tool request through by
    /// calling it something else.
    /// </summary>
    public const string ToolCall = "tool_call";

    /// <summary>The provider did not reach a stop condition of its own; the call failed underneath it.</summary>
    public const string Error = "error";

    public static readonly IReadOnlyList<string> All =
        [Stop, Length, ContentFilter, ToolCall, Error];

    public static bool IsKnown([NotNullWhen(true)] string? reason) =>
        reason is not null && All.Contains(reason, StringComparer.Ordinal);
}

/// <summary>
/// Why a non-completed generation failed, in the vocabulary the API envelope uses.
/// </summary>
/// <remarks>
/// These are codes, not sentences. An adapter maps its provider's own error shape onto one of them
/// and returns it; the raw provider body, its status text, its token and any absolute host path stay
/// inside the adapter, because a provider error body is where a key, a prompt or a customer's name
/// is most likely to be sitting in plain text.
/// </remarks>
public static class GenerationErrorCodes
{
    /// <summary>The provider rejected the stored credential (its 401 or 403). Retrying is pointless until it is replaced.</summary>
    public const string CredentialRejected = "credential_rejected";

    /// <summary>The provider is throttling (its 429). The caller's quota, not a defect.</summary>
    public const string RateLimited = "rate_limited";

    /// <summary>The provider was temporarily unreachable <b>before it accepted the request</b>.</summary>
    public const string ProviderUnavailable = "provider_unavailable";

    /// <summary>
    /// The provider accepted and answered, but the answer cannot be used: malformed JSON, a missing
    /// required key, an over-long field, or a tool call in a text-only contract.
    /// </summary>
    public const string ProviderInvalidResponse = "provider_invalid_response";

    /// <summary>The provider declined to generate. Recorded as a result, not as a fault of the caller.</summary>
    public const string ProviderRefused = "provider_refused";

    /// <summary>The attempt was cancelled. The result is suppressed; nothing is stored or returned.</summary>
    public const string GenerationCancelled = "generation_cancelled";

    public static bool IsKnown([NotNullWhen(true)] string? code) =>
        code is not null &&
        (
            code == CredentialRejected ||
            code == RateLimited ||
            code == ProviderUnavailable ||
            code == ProviderInvalidResponse ||
            code == ProviderRefused ||
            code == GenerationCancelled
        );
}
