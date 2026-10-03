namespace Chronicleaf.AI.Contracts;

/// <summary>
/// The provider slots this contract has adapters for (LLM-PROVIDER-CONTRACT-001).
/// </summary>
/// <remarks>
/// <para>
/// This is a list of adapter package names, not a catalogue of models, endpoints, prices or
/// availability. A model list here would be a claim this contract cannot keep: a provider adds and
/// retires models continuously, and a hard-coded list is how a system ends up offering a model that
/// was withdrawn last week. So there is none, and a model a provider does not accept is an error
/// the adapter reports rather than a value this file guesses at.
/// </para>
/// <para>
/// The five identifiers are the same strings the credential vault already accepts for
/// <c>openai</c>, <c>anthropic</c>, <c>gemini</c>, <c>openrouter</c> and <c>ollama</c>, so a
/// credential the owner stored for a provider is a reference this contract can carry without a
/// translation table. The vault also accepts <c>github</c> and <c>publisher</c>; those are not
/// generation providers and are deliberately absent here.
/// </para>
/// </remarks>
public static class GenerationProviderIds
{
    public const string OpenAI = "openai";
    public const string Anthropic = "anthropic";
    public const string Gemini = "gemini";
    public const string OpenRouter = "openrouter";
    public const string Ollama = "ollama";

    /// <summary>The adapter slots, in the order a settings surface lists them.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        OpenAI,
        Anthropic,
        Gemini,
        OpenRouter,
        Ollama,
    ];

    /// <summary>
    /// The widest stored value, so a provider column, a request field and this list are bounded by
    /// one number. It matches the vault's own provider column width.
    /// </summary>
    public const int MaxLength = 32;

    /// <summary>
    /// True when the identifier names one of <see cref="All"/>. Compared ordinally: provider names
    /// are not case-insensitive, so a folded spelling would select an adapter the owner did not
    /// name.
    /// </summary>
    public static bool IsSupported(string? providerId) =>
        providerId is not null && All.Contains(providerId, StringComparer.Ordinal);
}

/// <summary>
/// What one adapter says it can do (LLM-PROVIDER-CONTRACT-001, boundary scenario).
/// </summary>
/// <remarks>
/// <para>
/// <b>A capability is the adapter's own claim about itself, and nothing may promote it.</b>
/// <see cref="NativeJsonSchema"/> in particular is the honest answer to "can this provider enforce
/// my schema itself", and a provider that cannot says <c>false</c>. The system then falls back to
/// putting the schema in the prompt and validating the returned text locally. What it must never do
/// is report a native capability the adapter did not claim, because the whole value of this
/// descriptor is that a caller can trust it - and a false <c>true</c> would mean silently shipping
/// unvalidated model output.
/// </para>
/// <para>
/// There is deliberately no field for a model list, an endpoint, a price or a rate limit. Those are
/// transport facts that change under any adapter's feet, and a contract that carried them would be
/// wrong within weeks while still compiling.
/// </para>
/// </remarks>
/// <param name="Id">The provider slot this descriptor belongs to; one of <see cref="GenerationProviderIds"/>.</param>
/// <param name="StructuredOutputSupport">The provider can return output constrained to a structure.</param>
/// <param name="NativeJsonSchema">
/// The provider enforces a JSON schema itself, so no local re-validation is needed. False means the
/// system prompts for the schema and validates strictly and locally.
/// </param>
/// <param name="LocalEndpoint">The provider runs on an endpoint the operator names, not a public one.</param>
/// <param name="RequiresCredential">The provider cannot be called without a stored credential.</param>
public sealed record ProviderCapability(
    string Id,
    bool StructuredOutputSupport,
    bool NativeJsonSchema,
    bool LocalEndpoint,
    bool RequiresCredential)
{
    /// <summary>
    /// The descriptor of a slot no adapter has claimed yet.
    /// </summary>
    /// <remarks>
    /// This is not a default capability and must never be published as one. It exists so a caller can
    /// be told "this slot has no adapter" in the same vocabulary as one that does, and every
    /// capability reads <c>false</c> - which is exactly what the fallback path needs to be chosen.
    /// A system that treated this as a real descriptor would be claiming a provider supports nothing
    /// it was asked about rather than admitting it cannot ask.
    /// </remarks>
    public static ProviderCapability Unclaimed(string id) =>
        new(id, StructuredOutputSupport: false, NativeJsonSchema: false, LocalEndpoint: false, RequiresCredential: false);

    /// <summary>
    /// The name of the field the fallback turns on. Published so a client can render the reason
    /// rather than re-deriving it.
    /// </summary>
    public const string NativeSchemaField = "nativeJsonSchema";
}
