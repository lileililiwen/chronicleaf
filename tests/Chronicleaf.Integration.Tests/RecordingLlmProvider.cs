using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chronicleaf.AI.Contracts;

namespace Chronicleaf.Integration.Tests;

/// <summary>
/// The one <see cref="ILLMProvider"/> implementation in this repository, and it is a test double
/// (LLM-PROVIDER-CONTRACT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>It is not a provider and it proves nothing about one.</b> It opens no socket, sends nothing,
/// and its answers are whatever a scenario queued. The five real adapters are separate roadmap
/// packages, so no claim in this assembly may rest on this type: the scenarios that use it assert
/// what Chronicleaf does with a request and a result - the consent, the bounds, the error mapping,
/// the local validation - and each of those is a property of the system, not of any transport. The
/// scenario that matters most in that direction is
/// <c>Production_registration_cannot_reach_a_test_double</c>, which starts a host with the production
/// composition root and proves the same provider id answers 422 there.
/// </para>
/// <para>
/// It is registered only in <see cref="LlmProviderApiHost"/>. Production DI adds no
/// <see cref="ILLMProvider"/> at all, so a test double cannot be reached by a configuration value, an
/// environment variable or a settings row.
/// </para>
/// <para>
/// What it does do is record. Every request it receives is captured together with the outbound
/// payload an adapter would have serialised, so a scenario can assert on the bytes that would have
/// left the process - that there is no tools member, that the evidence is exactly the authorized
/// snapshot and nothing else - rather than trusting that the system built the request correctly.
/// </para>
/// </remarks>
public sealed class RecordingLlmProvider : ILLMProvider
{
    private readonly ConcurrentQueue<GenerationRequest> _requests = new();
    private readonly ConcurrentQueue<OutboundPayload> _outbound = new();
    private readonly Func<GenerationRequest, int, CancellationToken, Task<GenerationResult>> _behaviour;
    private readonly Func<string, bool> _supportsModel;

    private RecordingLlmProvider(
        string providerId,
        ProviderCapability capability,
        Func<bool> supportsModel,
        Func<GenerationRequest, int, CancellationToken, Task<GenerationResult>> behaviour,
        Func<string, bool>? supportsModelName = null)
    {
        ProviderId = providerId;
        Capability = capability;
        _behaviour = behaviour;
        _supportsModel = supportsModelName ?? (_ => supportsModel());
    }

    public string ProviderId { get; }

    public ProviderCapability Capability { get; }

    /// <summary>Every request this double received, in order.</summary>
    public IReadOnlyList<GenerationRequest> Requests => [.. _requests];

    /// <summary>
    /// The payload an adapter would have serialised for each request, in order.
    /// </summary>
    public IReadOnlyList<OutboundPayload> Outbound => [.. _outbound];

    /// <summary>How many times the transport was invoked. Zero means the call never reached a provider.</summary>
    public int CallCount => _requests.Count;

    /// <summary>True once a call was cancelled through the token rather than answering.</summary>
    public bool ObservedCancellation { get; private set; }

    /// <summary>Builds a double that answers with each queued result in turn.</summary>
    /// <param name="providerId">The slot this double serves.</param>
    /// <param name="capability">The descriptor it publishes; the system must use it verbatim.</param>
    /// <param name="supportedModels">The models it accepts. Anything else is refused before a call.</param>
    /// <param name="results">
    /// The answers, consumed in order. When the queue runs dry the last answer repeats, so a
    /// scenario that scripts a single retry does not have to predict the attempt count to script the
    /// second answer.
    /// </param>
    public static RecordingLlmProvider Scripted(
        string providerId,
        ProviderCapability capability,
        IReadOnlyList<string> supportedModels,
        IReadOnlyList<GenerationResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(results.Count, nameof(results));
        ArgumentNullException.ThrowIfNull(supportedModels);

        // The supported-model list is honoured, not decorative. A double that answered true for
        // everything would make the unknown-model scenario untestable, because the refusal the system
        // makes has to come from a real adapter saying "no" - the contract deliberately keeps no
        // model list of its own.
        var accepted = new HashSet<string>(supportedModels, StringComparer.Ordinal);

        return new RecordingLlmProvider(
            providerId,
            capability,
            () => true,
            (_, attempt, _) => Task.FromResult(results[Math.Min(attempt - 1, results.Count - 1)]),
            model => accepted.Contains(model));
    }

    /// <summary>
    /// Builds a double whose behaviour is decided by a scenario, for the cases a queue cannot express:
    /// a call that blocks until its token is cancelled, or one that counts its own attempts.
    /// </summary>
    public static RecordingLlmProvider Custom(
        string providerId,
        ProviderCapability capability,
        Func<bool>? supportsModel,
        Func<GenerationRequest, int, CancellationToken, Task<GenerationResult>> behaviour) =>
        new(
            providerId,
            capability,
            supportsModel ?? (() => true),
            behaviour ?? throw new ArgumentNullException(nameof(behaviour)));

    /// <summary>True for a model this double's provider is scripted to accept, and nothing else.</summary>
    public bool SupportsModel(string model) => _supportsModel(model);

    public async Task<GenerationResult> GenerateAsync(
        GenerationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var attempt = _requests.Count + 1;
        _requests.Enqueue(request);
        _outbound.Enqueue(OutboundPayload.From(request));

        try
        {
            return await _behaviour(request, attempt, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Recorded before rethrowing, so a scenario can prove the cancellation reached the
            // transport rather than inferring it from an aborted HTTP call.
            ObservedCancellation = true;
            throw;
        }
    }

    /// <summary>
    /// The JSON an adapter would put on the wire for one request, plus the request it came from.
    /// </summary>
    /// <remarks>
    /// Built by this double rather than by the system, which is exactly why the scenario that asserts
    /// on it is meaningful: the members are chosen here, so a member named <c>tools</c> could only
    /// appear if the <see cref="GenerationRequest"/> it was built from had one. The double is an
    /// honest stand-in for an adapter's serialisation step and nothing more - it is not evidence that
    /// any provider would accept this payload.
    /// </remarks>
    public sealed record OutboundPayload(string Json, string Model, int MaxOutputTokens, int TimeoutSeconds)
    {
        /// <summary>The member names this payload carries, so "no tools" can be asserted by name.</summary>
        public IReadOnlyList<string> Members =>
            JsonDocument.Parse(Json).RootElement.EnumerateObject().Select(property => property.Name).ToArray();

        public JsonElement Parse() => JsonDocument.Parse(Json).RootElement.Clone();

        public static OutboundPayload From(GenerationRequest request)
        {
            var evidence = JsonNode.Parse(request.UntrustedEvidenceJson) ?? new JsonArray();

            var document = new JsonObject
            {
                ["evidence"] = evidence.DeepClone(),
                ["maxOutputTokens"] = request.MaxOutputTokens,
                ["model"] = request.Model,
                ["systemInstruction"] = request.SystemInstruction,
                ["timeoutSeconds"] = request.TimeoutSeconds,
            };

            if (request.ResponseSchema.Length > 0)
            {
                document["responseSchema"] = request.ResponseSchema;
            }

            return new OutboundPayload(
                document.ToJsonString(new JsonSerializerOptions { WriteIndented = false }),
                request.Model,
                request.MaxOutputTokens,
                request.TimeoutSeconds);
        }
    }
}
