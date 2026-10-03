using Chronicleaf.AI.Contracts;
using Chronicleaf.Core;

namespace Chronicleaf.Application.AI;

/// <summary>
/// The bounds every generation call runs inside (LLM-PROVIDER-CONTRACT-002).
/// </summary>
/// <remarks>
/// <para>
/// These are constants rather than settings because each one is a safety property, not a preference.
/// An owner who could raise the output ceiling could ask for a hundred thousand tokens of their
/// family's milestone history in one call; an owner who could raise the timeout could leave a
/// request billing against an abandoned browser tab. So the numbers live here, they are one
/// published set, and the settings endpoint reports them rather than accepting them.
/// </para>
/// <para>
/// <see cref="MaxOutputTokens"/> is both the default and the ceiling. A request may ask for less -
/// a short answer is a legitimate thing to want - and asking for more is refused. That is one
/// number with two names rather than two numbers, and it means there is no way to reach an
/// unbounded generation no matter what is configured anywhere.
/// </para>
/// </remarks>
public static class GenerationLimits
{
    /// <summary>
    /// Output tokens per call: 4096. Also the hard ceiling, so a caller cannot raise it.
    /// </summary>
    public const int MaxOutputTokens = 4096;

    /// <summary>The per-attempt wall clock budget in seconds.</summary>
    public const int TimeoutSeconds = 120;

    /// <summary>
    /// The UTF-8 byte ceiling on the untrusted evidence payload, 256 KiB.
    /// </summary>
    /// <remarks>
    /// Measured in bytes and not characters because the transport is bytes: a cap counted in
    /// characters would be up to four times larger in UTF-8 and would let a preview past the size the
    /// limit exists to keep. The evidence the payload is built from is already bounded by its own
    /// package, so this is a second, independent ceiling rather than the only one.
    /// </remarks>
    public const int MaxInputBytes = 256 * 1024;

    /// <summary>
    /// How long an owner authorization holds, 15 minutes.
    /// </summary>
    /// <remarks>
    /// Long enough to read a redacted snapshot, choose evidence and write a model, and short enough
    /// that a preview left open on a shared machine is not a standing permission. The row is not
    /// deleted at expiry - it becomes stale and answers 409 - because an expired consent that
    /// returned 404 would tell a caller to go and find another preview, which is the opposite of
    /// "make a new one".
    /// </remarks>
    public static readonly TimeSpan ConsentTtl = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How many times one explicit, bounded, pre-response transient failure is retried.
    /// </summary>
    /// <remarks>
    /// One retry, so two attempts in total, and only for a failure the adapter could prove happened
    /// before the provider accepted the request. A timeout is excluded, a throttle is excluded and a
    /// rejected credential is excluded: each of those is either ambiguous about billing or has a
    /// known fix, and a retry would spend the owner's money or their quota to learn nothing.
    /// </remarks>
    public const int MaxTransientAttempts = 2;

    /// <summary>The model string's ceiling, bounded to the width the adapter's own field holds.</summary>
    public const int MaxModelLength = 128;

    /// <summary>
    /// An operator-named endpoint's ceiling. Long enough for a scheme, host and port, and nothing
    /// else: a settings field that accepts a 4000-character "endpoint" is a place for something
    /// other than an endpoint to live.
    /// </summary>
    public const int MaxEndpointRefLength = 200;
}

/// <summary>
/// The bounds as a settings surface reads them.
/// </summary>
/// <param name="MaxOutputTokens">The output-token default and ceiling.</param>
/// <param name="TimeoutSeconds">The per-attempt wall clock budget.</param>
/// <param name="MaxInputBytes">The UTF-8 ceiling on the untrusted evidence payload.</param>
/// <param name="ConsentTtlSeconds">How long an owner authorization holds.</param>
/// <param name="MaxTransientAttempts">Attempts per call, including the first. One means no retry.</param>
/// <param name="EnabledPurposes">The generation purposes that can be previewed right now.</param>
public sealed record GenerationLimitsView(
    int MaxOutputTokens,
    int TimeoutSeconds,
    int MaxInputBytes,
    int ConsentTtlSeconds,
    int MaxTransientAttempts,
    IReadOnlyList<string> EnabledPurposes);

/// <summary>
/// One provider slot, with what is actually known about it
/// (LLM-PROVIDER-CONTRACT-001, boundary scenario).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Capability"/> is <c>null</c> when no adapter is installed, and that is the whole point
/// of the type. The alternative - a default descriptor of all-<c>false</c> - would be
/// indistinguishable from a real provider that genuinely cannot enforce a schema, and a client
/// could not tell "this system has no adapter" from "this provider has no native schema support".
/// The first is a fact about the build and the second is a fact about a provider, and they lead to
/// different things a person does.
/// </para>
/// <para>
/// A non-null <see cref="Capability"/> is always the adapter's own published descriptor, copied
/// without adjustment. Nothing here may promote a capability, and nothing may add one to a
/// descriptor the adapter reported.
/// </para>
/// </remarks>
/// <param name="Id">The provider slot name; one of <c>GenerationProviderIds</c>.</param>
/// <param name="AdapterInstalled">Whether an adapter implementation is present in this deployment.</param>
/// <param name="Capability">The adapter's own descriptor, or <c>null</c> when no adapter is installed.</param>
/// <param name="Configured">Whether this scope has a stored credential for the slot.</param>
/// <param name="CredentialRevision">The metadata revision of that credential, so a change is visible.</param>
public sealed record AiProviderView(
    string Id,
    bool AdapterInstalled,
    ProviderCapability? Capability,
    bool Configured,
    long? CredentialRevision);

/// <summary>
/// The provider catalogue, with the two lists kept apart
/// (LLM-PROVIDER-CONTRACT-001).
/// </summary>
/// <remarks>
/// "Configured" and "supported" are different questions and answering them as one list is how a
/// settings page ends up implying that a stored credential means a working provider. Configured is
/// about this scope's own vault - is there a credential here, and at which metadata revision.
/// Supported is about the build - which adapter slots exist. In this build the second list is
/// non-empty and every entry reports no installed adapter, and that combination is the honest
/// description of a contract with no transports yet.
/// </remarks>
/// <param name="Supported">Every provider slot the build has a contract for.</param>
/// <param name="Configured">The slots this scope has a usable credential for.</param>
/// <param name="Limits">The bounds every call runs inside, published so a surface can show them.</param>
public sealed record AiProviderCatalogue(
    IReadOnlyList<AiProviderView> Supported,
    IReadOnlyList<AiProviderView> Configured,
    GenerationLimitsView Limits)
{
    /// <summary>
    /// The same catalogue with only the slots that have both a credential and an installed adapter -
    /// the ones a generation call could actually be made to. A settings surface shows the full
    /// lists and leaves this as the answer to "what can I use right now".
    /// </summary>
    public IReadOnlyList<AiProviderView> Usable =>
        [.. Supported.Where(view => view.AdapterInstalled && view.Configured)];
}

/// <summary>
/// An owner's provider selection, as submitted to <c>POST /api/settings/ai</c>.
/// </summary>
/// <param name="ProviderId">The slot to use; one of <c>GenerationProviderIds</c>.</param>
/// <param name="Model">The model, exactly as named. Validated against the adapter, never substituted.</param>
/// <param name="CredentialRef">The stored credential to use, or <c>null</c> to use the scope's only one for the slot.</param>
/// <param name="EndpointRef">The operator's endpoint, for a local-endpoint provider only.</param>
public sealed record AiSettingsRequest(
    string? ProviderId,
    string? Model,
    Guid? CredentialRef,
    string? EndpointRef);

/// <summary>
/// The selection, resolved against this scope and this build.
/// </summary>
/// <remarks>
/// <see cref="NativeJsonSchema"/> is <c>null</c> when no adapter is installed, and is otherwise the
/// adapter's own answer. The resolution endpoint reports it so an owner learns before consenting
/// whether the schema will be enforced by the provider or by a local validator, which is the one
/// thing about a provider that changes what they should check in the result.
/// </remarks>
public sealed record AiSettingsResolution(
    string ProviderId,
    string Model,
    bool AdapterInstalled,
    ProviderCapability? Capability,
    bool? NativeJsonSchema,
    Guid? CredentialId,
    long? CredentialRevision,
    string? EndpointRef,
    GenerationLimitsView Limits);

/// <summary>
/// The owner-authorized preview, exactly as the design's response names it.
/// </summary>
/// <remarks>
/// Ten fields and no more. There is no draft text here, no provider response and no token: this is
/// the input side of the handshake, and the owner reads <c>InputEvidenceIds</c> and
/// <c>Redactions</c> against the app's own redacted view to decide whether to authorize it. The
/// <c>ConsentHash</c> that comes back is a binding over that decision, not a credential - see
/// <see cref="ConsentBinding"/> for why it cannot be used on its own.
/// </remarks>
/// <param name="PreviewId">This preview's id; addressed by the generate call.</param>
/// <param name="ConsentHash">The SHA-256 binding over the authorized selection.</param>
/// <param name="InputEvidenceIds">The evidence item ids included in the snapshot, in snapshot order.</param>
/// <param name="Redactions">The redaction labels the included items carry, distinct and ordered.</param>
/// <param name="ProviderId">The provider the snapshot is bound to.</param>
/// <param name="Model">The model the snapshot is bound to.</param>
/// <param name="CredentialRevision">The credential's metadata revision at authorization, or null when none.</param>
/// <param name="EvidenceRevision">The evidence head revision at authorization.</param>
/// <param name="VoiceRevision">The project voice profile revision at authorization.</param>
/// <param name="ExpiresAt">When this authorization stops being usable.</param>
public sealed record GenerationPreviewView(
    Guid PreviewId,
    string ConsentHash,
    IReadOnlyList<Guid> InputEvidenceIds,
    IReadOnlyList<string> Redactions,
    string ProviderId,
    string Model,
    long? CredentialRevision,
    long EvidenceRevision,
    long VoiceRevision,
    DateTimeOffset ExpiresAt);

/// <summary>
/// The result of one bounded call, as the generate route returns it.
/// </summary>
/// <remarks>
/// <para>
/// This is a draft and only a draft. Nothing in it approves a milestone, closes one, changes a
/// project or records anything the owner did not do, and the type has no field that could.
/// </para>
/// <para>
/// <see cref="NativeJsonSchema"/> and <see cref="LocalValidationApplied"/> are published together
/// because they answer one question honestly: was the schema enforced by the provider, or by this
/// system after the fact. A caller that only sees <see cref="Status"/> completed cannot tell those
/// apart, and that difference is what a reviewer needs to know about a text they are about to
/// publish.
/// </para>
/// <para>
/// <see cref="Attempts"/> is how many times the transport was invoked, so a caller can see that a
/// bounded transient retry happened rather than discovering it in a provider's invoice.
/// </para>
/// </remarks>
/// <param name="Status">One of <c>GenerationStatuses</c>; only <c>completed</c> ever carries text.</param>
/// <param name="Text">The generated text when completed; otherwise null.</param>
/// <param name="ProviderRequestId">The provider's own request id, when it returned one.</param>
/// <param name="InputTokens">Input tokens as reported by the provider.</param>
/// <param name="OutputTokens">Output tokens as reported by the provider.</param>
/// <param name="FinishReason">One of <c>GenerationFinishReasons</c>.</param>
/// <param name="ErrorCode">One of <c>GenerationErrorCodes</c> when the status is not completed.</param>
/// <param name="NativeJsonSchema">Whether the provider enforced the schema itself.</param>
/// <param name="LocalValidationApplied">Whether this system validated the text against the schema locally.</param>
/// <param name="Attempts">How many times the transport was invoked.</param>
public sealed record GenerationRunView(
    string Status,
    string? Text,
    string? ProviderRequestId,
    int? InputTokens,
    int? OutputTokens,
    string FinishReason,
    string? ErrorCode,
    bool NativeJsonSchema,
    bool LocalValidationApplied,
    int Attempts);
