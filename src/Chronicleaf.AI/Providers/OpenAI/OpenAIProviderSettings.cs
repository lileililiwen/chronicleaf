namespace Chronicleaf.AI.OpenAI;

/// <summary>
/// What this deployment will let an owner point the OpenAI adapter at
/// (LLM-OPENAI-ADAPTER-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>The accepted model list is configuration, and it is empty unless an operator filled it in.</b>
/// That is the whole answer to "which models does this build support", and it is deliberately not a
/// compiled-in list: OpenAI adds and retires models continuously, and a list in source is a claim
/// that goes stale without anybody noticing. An empty list means
/// <see cref="Contracts.ILLMProvider.SupportsModel"/> answers false for every model, so the product
/// refuses the selection with <c>model_not_supported</c> before a credential is leased and before a
/// socket is opened - the honest answer for a deployment that has not been told what it may use.
/// </para>
/// <para>
/// The product never chooses a model. The owner names it, the adapter refuses anything outside this
/// list rather than substituting a near miss, and a near miss would silently bill the owner for a
/// different generation than the one they authorized and read.
/// </para>
/// <para>
/// <b>Naming a model here is an assertion about that model.</b> The capability this adapter publishes
/// says the provider enforces the response schema itself, which is true of the Responses API's
/// structured output and not of every model that can be named. So the list is where an operator
/// records the models this installation has verified, and an operator who allows a model that cannot
/// enforce a schema is misreporting its own deployment - the adapter cannot detect that from a name,
/// and pretending otherwise would mean shipping the guess the contract forbids.
/// </para>
/// </remarks>
public sealed class OpenAIProviderSettings
{
    /// <summary>The configuration section these settings bind to.</summary>
    public const string SectionName = "Chronicleaf:OpenAI";

    /// <summary>The widest allowlist, so a misconfigured section cannot become a lookup table.</summary>
    public const int MaxAllowedModels = 64;

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
    /// The models this installation will accept, exactly as the provider names them.
    /// </summary>
    /// <remarks>
    /// A constructor parameter rather than a settable property, and that is load-bearing: the normalised
    /// set below is built once here, and an <c>init</c> property assigned by an object initializer runs
    /// <i>after</i> the constructor - so a settable property would build the set from the default empty
    /// list and then appear to accept nothing, which is a silent, confusing way to fail a safety
    /// property.
    /// </remarks>
    public IReadOnlyList<string> AllowedModels { get; }

    /// <summary>
    /// The accepted set, normalised and validated once at construction.
    /// </summary>
    /// <remarks>
    /// Compared ordinally and case-sensitively, because a model name is an identifier rather than a
    /// label: <c>gpt</c> and <c>GPT</c> are different strings to a provider, and folding them would
    /// let an owner name one and be billed for whichever the provider resolved. A blank name, an
    /// over-long name, a duplicate and an over-long list are all refused: each is a configuration that
    /// would otherwise behave in a way its author did not intend.
    /// </remarks>
    public ISet<string> ModelAllowlist { get; }

    /// <param name="allowedModels">
    /// The models this deployment accepts. Empty or omitted refuses every model, which is the default
    /// and the honest state of a transport nobody has pointed at anything.
    /// </param>
    public OpenAIProviderSettings(IReadOnlyList<string>? allowedModels = null)
    {
        var models = allowedModels ?? [];
        ArgumentOutOfRangeException.ThrowIfGreaterThan(models.Count, MaxAllowedModels, nameof(allowedModels));

        var accepted = new HashSet<string>(StringComparer.Ordinal);

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

            if (!accepted.Add(model))
            {
                throw new ArgumentException(
                    "The same model is allowed twice; a duplicated entry hides which spelling was meant.",
                    nameof(allowedModels));
            }
        }

        AllowedModels = [.. accepted];
        ModelAllowlist = accepted;
    }
}
