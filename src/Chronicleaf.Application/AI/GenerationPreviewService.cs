using Chronicleaf.AI.Contracts;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Evidence;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Application.Secrets;
using Chronicleaf.Application.Voice;
using Chronicleaf.Core;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Application.AI;

/// <summary>
/// The use cases for a bounded BYOK generation: what a provider can do, what an owner selected, what
/// they authorized, and the one call that sends it (LLM-PROVIDER-CONTRACT-001/002).
/// </summary>
public interface IGenerationPreviewService
{
    /// <summary>
    /// The provider catalogue for a scope, with configured and supported kept apart
    /// (LLM-PROVIDER-CONTRACT-001).
    /// </summary>
    Task<AiProviderCatalogue> ReadProvidersAsync(
        Guid scopeId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves an owner's provider selection against this scope and this build
    /// (LLM-PROVIDER-CONTRACT-001).
    /// </summary>
    /// <remarks>
    /// The refusal order is the point of this method. An unknown provider slot, an unknown model, a
    /// credential from another scope and an endpoint for a public provider are all decided before a
    /// preview exists, so an owner finds out that their selection will not work before they have
    /// read a redacted snapshot and are about to press the button that spends money.
    /// </remarks>
    Task<AiSettingsResolution> ResolveSettingsAsync(
        OwnerScope owner,
        AiSettingsRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Freezes the redacted input for a purpose and returns the owner's authorization to it
    /// (LLM-PROVIDER-CONTRACT-002).
    /// </summary>
    Task<GenerationPreviewView> CreatePreviewAsync(
        OwnerScope owner,
        GenerationPreviewInput input,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Performs the one bounded call the owner authorized (LLM-PROVIDER-CONTRACT-001/002).
    /// </summary>
    /// <remarks>
    /// The build order is the requirement: load the preview, check the consent, re-resolve the
    /// revisions it was bound to, and only then resolve the adapter. A stale consent therefore
    /// cannot reach a transport, rather than reaching one and having its result discarded.
    /// </remarks>
    Task<GenerationRunView> GenerateAsync(
        OwnerScope owner,
        Guid previewId,
        string? consentHash,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One bounded call's outcome, with the number of transport invocations it took.
/// </summary>
/// <remarks>
/// A named type rather than a tuple because the attempt count is load-bearing in the published
/// result: it is how an owner learns that a transient retry happened, instead of finding out from a
/// provider's invoice.
/// </remarks>
/// <param name="Result">The last result the transport returned.</param>
/// <param name="Attempts">How many times the transport was invoked, including the first.</param>
public sealed record GenerationCall(GenerationResult Result, int Attempts);

/// <summary>What an owner asked to preview.</summary>
/// <param name="Purpose">One of <see cref="GenerationPreviewPurposes.Enabled"/>; others are 422.</param>
/// <param name="AggregateRef">The id the purpose applies to. The milestone route supplies it.</param>
/// <param name="MilestoneId">The milestone, when the preview is milestone-scoped.</param>
/// <param name="ProviderId">The provider slot to bind the snapshot to.</param>
/// <param name="Model">The model to bind the snapshot to.</param>
/// <param name="CredentialRef">The stored credential, or null to use the scope's own for the slot.</param>
/// <param name="EndpointRef">The operator's endpoint, for a local-endpoint provider only.</param>
/// <param name="EvidenceRevision">
/// The evidence head revision the owner was shown, as <c>If-Match</c> would carry it. A value that
/// is not the current head is refused, so a stale browser tab cannot preview against a bundle the
/// owner has not seen.
/// </param>
/// <param name="SelectedEvidenceIds">The evidence items to include; must all be in that head.</param>
/// <param name="NarrativeSelections">The narrative milestone ids to draw narrative continuity from.</param>
public sealed record GenerationPreviewInput(
    string? Purpose,
    string? AggregateRef,
    Guid? MilestoneId,
    string? ProviderId,
    string? Model,
    Guid? CredentialRef,
    string? EndpointRef,
    long EvidenceRevision,
    IReadOnlyList<Guid>? SelectedEvidenceIds,
    IReadOnlyList<Guid>? NarrativeSelections);

/// <summary>
/// The service behind the four use cases.
/// </summary>
/// <remarks>
/// <para>
/// It owns the consent, and every interesting behaviour in this package is a consequence of that one
/// fact. The preview write freezes a document; the call re-reads that document and re-derives the
/// revisions it claims, and refuses if they have moved. There is no path that regenerates a snapshot
/// and then sends it, because a regenerated snapshot is one the owner did not read.
/// </para>
/// <para>
/// <b>Nothing here writes project, milestone or draft state.</b> A generation produces text and an
/// audit row and nothing else; it does not confirm a milestone, does not close one, does not
/// approve a draft and does not create one. The type it returns has no field that could, and the
/// only repository it touches is the preview store.
/// </para>
/// <para>
/// <b>Nothing here logs evidence or system text.</b> The logger receives identifiers, codes and
/// durations. The snapshot is read from the store, handed to an adapter and dropped; the only place
/// it exists outside a request is the row the owner already authorized.
/// </para>
/// </remarks>
public sealed class GenerationPreviewService : IGenerationPreviewService
{
    /// <summary>The audit action recorded when an owner's authorization is frozen into a row.</summary>
    public const string PreviewCreatedAction = "ai.generation_preview.created";

    /// <summary>The audit action recorded when a bounded call returned a usable draft.</summary>
    public const string GenerationCompletedAction = "ai.generation.completed";

    /// <summary>The audit action recorded when a bounded call did not return a usable draft.</summary>
    public const string GenerationFailedAction = "ai.generation.failed";

    /// <summary>The response schema the milestone story purpose uses, in its first version.</summary>
    public const string MilestoneStorySchemaVersion = "milestone-story/1";

    /// <summary>
    /// The milestone story schema. Small on purpose: a title, a summary, a limitations list and a next
    /// steps list. It is what the local validator can check completely, and a schema larger than that
    /// would be a schema whose validation is partly ceremonial.
    /// </summary>
    private static readonly Lazy<GenerationResponseSchema> MilestoneStorySchema = new(
        () => GenerationResponseSchema.Parse(
            MilestoneStorySchemaVersion,
            """
            {
              "type": "object",
              "required": ["title", "summary"],
              "additionalProperties": false,
              "properties": {
                "title": { "type": "string", "maxLength": 160 },
                "summary": { "type": "string" },
                "knownLimitations": { "type": "string" },
                "nextSteps": { "type": "string" }
              }
            }
            """));

    private readonly IGenerationPreviewStore _previews;
    private readonly ICredentialStore _credentials;
    private readonly IProjectMilestoneService _milestones;
    private readonly IEvidenceStore _evidence;
    private readonly IVoiceProfileService _voice;
    private readonly ILLMProviderResolver _providers;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IAuditWriter _audit;
    private readonly TimeProvider _clock;
    private readonly GenerationCallOptions _call;
    private readonly ILogger<GenerationPreviewService> _logger;

    public GenerationPreviewService(
        IGenerationPreviewStore previews,
        ICredentialStore credentials,
        IProjectMilestoneService milestones,
        IEvidenceStore evidence,
        IVoiceProfileService voice,
        ILLMProviderResolver providers,
        IUnitOfWork unitOfWork,
        IAuditWriter audit,
        TimeProvider clock,
        GenerationCallOptions call,
        ILogger<GenerationPreviewService> logger)
    {
        ArgumentNullException.ThrowIfNull(previews);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(milestones);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(voice);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(unitOfWork);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(logger);

        _previews = previews;
        _credentials = credentials;
        _milestones = milestones;
        _evidence = evidence;
        _voice = voice;
        _providers = providers;
        _unitOfWork = unitOfWork;
        _audit = audit;
        _clock = clock;
        _call = call;
        _logger = logger;
    }

    public async Task<AiProviderCatalogue> ReadProvidersAsync(
        Guid scopeId,
        CancellationToken cancellationToken = default)
    {
        if (scopeId == Guid.Empty)
        {
            throw new ValidationFailedException("scopeId", "A scope is required.");
        }

        var credentials = await _credentials.ListAsync(scopeId, afterProvider: null, limit: 200, cancellationToken)
            .ConfigureAwait(false);

        var byProvider = credentials
            .GroupBy(metadata => metadata.Provider, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Max(metadata => metadata.Revision), StringComparer.Ordinal);

        var supported = new List<AiProviderView>(GenerationProviderIds.All.Count);
        foreach (var providerId in GenerationProviderIds.All)
        {
            var adapter = _providers.Find(providerId);
            var configured = byProvider.TryGetValue(providerId, out var revision);

            supported.Add(new AiProviderView(
                providerId,
                adapter is not null,
                // The adapter's own descriptor, or null. Never a synthesised one: an installed
                // adapter and a provider that happens to lack a capability have to be tellable apart.
                adapter?.Capability,
                configured,
                configured ? revision : null));
        }

        return new AiProviderCatalogue(
            supported,
            [.. supported.Where(view => view.Configured)],
            CurrentLimits());
    }

    public async Task<AiSettingsResolution> ResolveSettingsAsync(
        OwnerScope owner,
        AiSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        var providerId = RequireProviderId(request.ProviderId);
        var model = RequireModel(request.Model);

        var adapter = _providers.Find(providerId)
            ?? throw new ProviderNotInstalledException(providerId);

        if (!adapter.SupportsModel(model))
        {
            // Refused, never substituted. A near-miss model would bill the owner for a generation
            // other than the one they authorized, and the consent hash would still name this one.
            throw new ModelNotSupportedException(providerId);
        }

        var credential = await ResolveCredentialAsync(owner, providerId, request.CredentialRef, cancellationToken)
            .ConfigureAwait(false);

        if (adapter.Capability.RequiresCredential && credential is null)
        {
            throw new ValidationFailedException(
                "credentialRef",
                $"No credential is stored for {providerId} in this installation, so it cannot be called.");
        }

        var endpointRef = NormalizeEndpoint(request.EndpointRef);
        if (endpointRef is not null && !adapter.Capability.LocalEndpoint)
        {
            throw new ValidationFailedException(
                "endpointRef",
                "This provider uses its own published endpoint, so an endpoint cannot be set for it.");
        }

        return new AiSettingsResolution(
            providerId,
            model,
            AdapterInstalled: true,
            adapter.Capability,
            adapter.Capability.NativeJsonSchema,
            credential?.Id,
            credential?.Revision,
            endpointRef,
            CurrentLimits());
    }

    public async Task<GenerationPreviewView> CreatePreviewAsync(
        OwnerScope owner,
        GenerationPreviewInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(input);

        var purpose = RequirePurpose(input.Purpose);
        var aggregateRef = RequireAggregateRef(input.AggregateRef, purpose);
        var providerId = RequireProviderId(input.ProviderId);
        var model = RequireModel(input.Model);

        // The milestone route is exactly this purpose with the aggregate set to the milestone, and
        // the two must not be able to disagree. A generic request that claims purpose
        // 'milestone-story' and names a different aggregate is refused rather than being treated as
        // a second spelling of the same thing.
        var milestoneId = input.MilestoneId;
        if (purpose == GenerationPreviewPurposes.MilestoneStory)
        {
            if (milestoneId is null)
            {
                milestoneId = Guid.Parse(aggregateRef);
            }
            else if (!string.Equals(milestoneId.Value.ToString("d"), aggregateRef, StringComparison.Ordinal))
            {
                throw new ValidationFailedException(
                    "aggregateRef",
                    "The aggregate and the milestone must name the same thing.");
            }

            aggregateRef = milestoneId.Value.ToString("d");
        }

        var milestone = await _milestones
            .GetMilestoneAsync(owner, milestoneId!.Value, cancellationToken)
            .ConfigureAwait(false);

        var adapter = _providers.Find(providerId)
            ?? throw new ProviderNotInstalledException(providerId);

        if (!adapter.SupportsModel(model))
        {
            throw new ModelNotSupportedException(providerId);
        }

        var credential = await ResolveCredentialAsync(owner, providerId, input.CredentialRef, cancellationToken)
            .ConfigureAwait(false);

        if (adapter.Capability.RequiresCredential && credential is null)
        {
            throw new ValidationFailedException(
                "credentialRef",
                $"No credential is stored for {providerId} in this installation, so it cannot be called.");
        }

        var endpointRef = NormalizeEndpoint(input.EndpointRef);
        if (endpointRef is not null && !adapter.Capability.LocalEndpoint)
        {
            throw new ValidationFailedException(
                "endpointRef",
                "This provider uses its own published endpoint, so an endpoint cannot be set for it.");
        }

        var bundle = await _evidence.FindHeadAsync(owner.ScopeId, milestone.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new ValidationFailedException(
                "selectedEvidenceIds",
                "This milestone has no evidence yet, so there is nothing to draft from. Record evidence first.");

        if (bundle.Revision != input.EvidenceRevision)
        {
            throw new ValidationFailedException(
                "evidenceRevision",
                "The evidence changed since it was shown. Read the new evidence and preview again.");
        }

        var selected = SelectItems(bundle, input.SelectedEvidenceIds);
        var narrativeSelections = RequireNarrativeSelections(input.NarrativeSelections);

        var voice = await _voice.GetAsync(owner.ScopeId, milestone.ProjectId, cancellationToken).ConfigureAwait(false);

        var snapshot = GenerationSnapshot.Create(
            purpose,
            aggregateRef,
            providerId,
            model,
            endpointRef,
            credential?.Id,
            credential?.Revision,
            bundle,
            selected,
            voice,
            narrativeSelections,
            MilestoneStorySchema.Value,
            adapter.Capability.NativeJsonSchema);

        var inputBytes = System.Text.Encoding.UTF8.GetByteCount(snapshot.UntrustedEvidenceJson);
        if (inputBytes > GenerationLimits.MaxInputBytes)
        {
            throw new ValidationFailedException(
                "selectedEvidenceIds",
                "The selected evidence is larger than one call accepts. Select fewer items.");
        }

        var now = _clock.GetUtcNow();

        // The digest covers the canonical form of the snapshot, not the exact text this process
        // happened to write. The column is jsonb and PostgreSQL re-serialises it on the way back, so
        // a digest over the written bytes could never be verified against the stored bytes - and a
        // verification that always fails is worse than none, because it makes an untouched row look
        // altered. See ConsentBinding.CanonicalJson.
        var sha256 = ConsentBinding.Sha256Hex(ConsentBinding.CanonicalJson(snapshot.Json));
        var consentHash = ConsentBinding.Compute(
            owner.ScopeId,
            purpose,
            aggregateRef,
            providerId,
            model,
            credential?.Revision,
            bundle.Revision,
            voice.Revision,
            snapshot.InputEvidenceIds,
            narrativeSelections,
            sha256);

        var preview = new GenerationPreview(
            Guid.NewGuid(),
            owner.ScopeId,
            milestone.Id,
            purpose,
            aggregateRef,
            snapshot.Json,
            sha256,
            now.Add(GenerationLimits.ConsentTtl));

        // The insert and its audit row commit together or not at all, so there is no window in which
        // a preview exists with nothing recording that an owner was asked to authorize it.
        await _unitOfWork.ExecuteAsync(
            async token =>
            {
                if (!await _previews.InsertAsync(preview, token).ConfigureAwait(false))
                {
                    throw new GenerationPreviewConflictException();
                }

                _audit.Record(new DomainAudit(
                    // The actor is left to the delivered audit writer, which already knows the
                    // session. Naming it here as well would be a second source for it.
                    ActorId: null,
                    Action: PreviewCreatedAction,
                    TargetId: preview.Id,
                    Revision: bundle.Revision,
                    // Identifiers and revisions only. Not the snapshot, not the system instruction,
                    // not a single byte of the evidence text: this row is readable by anyone with
                    // audit access and it outlives the request.
                    Detail: $"purpose={purpose};provider={providerId};evidenceRevision={bundle.Revision};"
                        + $"voiceRevision={voice.Revision};credentialRevision={credential?.Revision.ToString() ?? "none"};"));

                return Task.FromResult(true);
            },
            cancellationToken).ConfigureAwait(false);

        return new GenerationPreviewView(
            preview.Id,
            consentHash,
            snapshot.InputEvidenceIds,
            snapshot.Redactions,
            providerId,
            model,
            credential?.Revision,
            bundle.Revision,
            voice.Revision,
            preview.ExpiresAt);
    }

    public async Task<GenerationRunView> GenerateAsync(
        OwnerScope owner,
        Guid previewId,
        string? consentHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (previewId == Guid.Empty)
        {
            throw new ValidationFailedException("previewId", "A preview id is required.");
        }

        // Scoped read. Another scope's preview is absent here, and absent is a 404 - never a 409,
        // which would confirm that the id exists somewhere.
        var preview = await _previews.FindAsync(owner.ScopeId, previewId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("generation preview");

        var now = _clock.GetUtcNow();
        if (preview.IsExpiredAt(now))
        {
            throw new ConsentStaleException(ConsentStaleReasons.Expired);
        }

        if (!ConsentBinding.Matches(consentHash, StoredConsentHash(preview, owner.ScopeId)))
        {
            throw new ConsentStaleException(ConsentStaleReasons.HashMismatch);
        }

        // Every check from here re-derives a fact and compares it against what the owner authorized.
        // These are fresh reads on purpose - of the revisions, never of the bytes that will be sent.
        var snapshot = GenerationSnapshot.Deserialize(preview.SnapshotJson);
        await RevalidateAsync(owner, preview, snapshot, cancellationToken).ConfigureAwait(false);

        // Only now is a transport resolved. Everything above is answerable with no adapter, no
        // credential and no network, so a stale consent costs the owner nothing.
        var adapter = _providers.Find(snapshot.ProviderId)
            ?? throw new ProviderNotInstalledException(snapshot.ProviderId);

        if (!adapter.SupportsModel(snapshot.Model))
        {
            throw new ModelNotSupportedException(snapshot.ProviderId);
        }

        var useNativeSchema = adapter.Capability.NativeJsonSchema;
        var schema = GenerationResponseSchema.Parse(snapshot.ResponseSchemaVersion, snapshot.ResponseSchema);
        var request = new GenerationRequest
        {
            RequestId = Guid.NewGuid(),
            ScopeId = owner.ScopeId,
            ProviderId = snapshot.ProviderId,
            Model = snapshot.Model,
            CredentialRef = snapshot.CredentialId,
            EndpointRef = snapshot.EndpointRef,
            SystemInstruction = snapshot.SystemInstruction,
            UntrustedEvidenceJson = snapshot.UntrustedEvidenceJson,
            ResponseSchema = snapshot.ResponseSchema,
            MaxOutputTokens = GenerationLimits.MaxOutputTokens,
            TimeoutSeconds = _call.TimeoutSeconds,
        };

        var call = await CallWithBoundedRetryAsync(adapter, request, cancellationToken).ConfigureAwait(false);
        var result = call.Result;

        // The fallback is a validator, not a hope. A provider without native schema enforcement gets
        // its text checked here before the status is allowed to be 'completed'.
        var validated = result;
        if (result.Status == GenerationStatuses.Completed && !useNativeSchema)
        {
            var failure = schema.Validate(result.Text);
            if (failure is not null)
            {
                validated = GenerationResult.Invalid(GenerationFinishReasons.Error, result.ProviderRequestId);
                _logger.LogWarning(
                    "Event=ai.generation_local_validation_failed Provider={Provider} Reason={Reason}",
                    snapshot.ProviderId,
                    failure);
            }
        }

        // The audit row is committed even when the call failed, because "this owner spent a provider
        // call and it did not work" is exactly the fact a later billing question needs. The preview
        // row is untouched either way: a failed call does not consume the consent, so the owner may
        // try again under the same authorization.
        await _unitOfWork.ExecuteAsync(
            token =>
            {
                _audit.Record(new DomainAudit(
                    ActorId: null,
                    Action: validated.Status == GenerationStatuses.Completed
                        ? GenerationCompletedAction
                        : GenerationFailedAction,
                    TargetId: preview.Id,
                    Revision: snapshot.EvidenceRevision,
                    // The draft text is not here and neither is the evidence. What is here is enough
                    // to reconcile the call against a provider's own invoice: which provider, which
                    // model, how many tokens, whether the schema was enforced remotely or locally, and
                    // how many times the transport was invoked.
                    Detail: $"provider={snapshot.ProviderId};model={snapshot.Model};status={validated.Status};"
                        + $"finishReason={validated.FinishReason};code={validated.ErrorCode ?? "none"};"
                        + $"inputTokens={validated.InputTokens?.ToString() ?? "none"};"
                        + $"outputTokens={validated.OutputTokens?.ToString() ?? "none"};"
                        + $"nativeJsonSchema={useNativeSchema};attempts={call.Attempts};"));
                return Task.FromResult(true);
            },
            cancellationToken).ConfigureAwait(false);

        if (validated.Status != GenerationStatuses.Completed)
        {
            throw ToEnvelope(validated);
        }

        return new GenerationRunView(
            validated.Status,
            validated.Text,
            validated.ProviderRequestId,
            validated.InputTokens,
            validated.OutputTokens,
            validated.FinishReason,
            validated.ErrorCode,
            useNativeSchema,
            LocalValidationApplied: !useNativeSchema,
            Attempts: call.Attempts);
    }

    /// <summary>
    /// Re-derives every fact the consent was bound to and refuses if any of them moved.
    /// </summary>
    private async Task RevalidateAsync(
        OwnerScope owner,
        GenerationPreview preview,
        GenerationSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var providerChanged =
            !string.Equals(preview.Purpose, snapshot.Purpose, StringComparison.Ordinal) ||
            !string.Equals(preview.AggregateRef, snapshot.AggregateRef, StringComparison.Ordinal) ||
            !ConsentBinding.Sha256Hex(ConsentBinding.CanonicalJson(snapshot.Json))
                .Equals(preview.Sha256, StringComparison.Ordinal);

        if (providerChanged)
        {
            // The stored bytes no longer hash to the digest they were stored with. Either the row or
            // the snapshot was altered, and in both cases the owner is no longer looking at what
            // would be sent.
            throw new ConsentStaleException(ConsentStaleReasons.SnapshotAltered);
        }

        var bundle = await _evidence.FindHeadAsync(owner.ScopeId, preview.MilestoneId!.Value, cancellationToken)
            .ConfigureAwait(false);

        if (bundle is null ||
            bundle.Id != snapshot.EvidenceBundleId ||
            bundle.Revision != snapshot.EvidenceRevision ||
            !ContainsAll(bundle, snapshot.InputEvidenceIds))
        {
            throw new ConsentStaleException(ConsentStaleReasons.EvidenceChanged);
        }

        var milestone = await _milestones
            .GetMilestoneAsync(owner, preview.MilestoneId.Value, cancellationToken)
            .ConfigureAwait(false);

        var voice = await _voice.GetAsync(owner.ScopeId, milestone.ProjectId, cancellationToken).ConfigureAwait(false);
        if (voice.Revision != snapshot.VoiceRevision)
        {
            throw new ConsentStaleException(ConsentStaleReasons.VoiceChanged);
        }

        if (snapshot.CredentialId.HasValue)
        {
            var credential = await _credentials
                .FindByIdAsync(owner.ScopeId, snapshot.CredentialId.Value, cancellationToken)
                .ConfigureAwait(false);

            // Metadata only. The sealed bytes are never read here, so a generation call does not
            // open the vault at all - only the adapter that is about to use the secret does.
            if (credential is null ||
                credential.Revision != snapshot.CredentialRevision ||
                !string.Equals(credential.Provider, snapshot.ProviderId, StringComparison.Ordinal))
            {
                throw new ConsentStaleException(ConsentStaleReasons.ProviderConfigurationChanged);
            }
        }
    }

    /// <summary>
    /// Makes at most <see cref="GenerationLimits.MaxTransientAttempts"/> calls, and only for a
    /// failure that provably happened before the provider accepted the request.
    /// </summary>
    private async Task<GenerationCall> CallWithBoundedRetryAsync(
        ILLMProvider adapter,
        GenerationRequest request,
        CancellationToken cancellationToken)
    {
        // The budget covers the whole call, not each attempt, so a slow failure cannot buy extra
        // attempts by taking longer.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(_call.TimeoutSeconds));

        var attempts = 0;
        GenerationResult result = GenerationResult.Failed(GenerationErrorCodes.ProviderUnavailable);

        while (attempts < GenerationLimits.MaxTransientAttempts)
        {
            attempts++;

            // Cancellation is the caller's and never converted into a result. Throwing here is what
            // suppresses it: a cancelled generation leaves no result for anyone to find.
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                result = await adapter.GenerateAsync(request, budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                // The budget, not the caller. A timeout is ambiguous - the provider may have accepted
                // and may be billing - so it is reported and never retried.
                _logger.LogWarning(
                    "Event=ai.generation_timeout Provider={Provider} TimeoutSeconds={TimeoutSeconds}",
                    request.ProviderId,
                    _call.TimeoutSeconds);
                throw new GenerationTimeoutException(_call.TimeoutSeconds);
            }

            if (!IsRetryablePreResponseFailure(result) || attempts >= GenerationLimits.MaxTransientAttempts)
            {
                return new GenerationCall(result, attempts);
            }

            _logger.LogWarning(
                "Event=ai.generation_transient_retry Provider={Provider} Attempt={Attempt} Code={Code}",
                request.ProviderId,
                attempts,
                result.ErrorCode);
        }

        return new GenerationCall(result, attempts);
    }

    /// <summary>
    /// True only for a failure the adapter could prove happened before the provider accepted the
    /// request.
    /// </summary>
    /// <remarks>
    /// The test is deliberately narrow: an explicit pre-response unavailability, with no text, no
    /// provider request id and no usage reported. Anything that suggests the request reached the
    /// provider - a throttling answer, a rejected credential, a malformed body, a timeout - is
    /// excluded, because for all of those a second attempt is a second thing the owner may be
    /// billed for and a second quota unit, and none of them would succeed on a retry anyway.
    /// </remarks>
    private static bool IsRetryablePreResponseFailure(GenerationResult result) =>
        result.Status == GenerationStatuses.Failed &&
        string.Equals(result.ErrorCode, GenerationErrorCodes.ProviderUnavailable, StringComparison.Ordinal) &&
        result.Text is null &&
        result.ProviderRequestId is null &&
        result.InputTokens is null &&
        result.OutputTokens is null;

    /// <summary>
    /// Turns a non-completed result into the envelope the caller sees.
    /// </summary>
    /// <remarks>
    /// Written as an explicit chain rather than a nested switch so that each mapping is a line an
    /// auditor can read against the package's stated failure policy, and so that a new
    /// <see cref="GenerationErrorCodes"/> value has to be given a decision here rather than falling
    /// through a default. The two terminal branches carry fixed text; nothing a provider returned is
    /// ever interpolated.
    /// </remarks>
    private static ApiException ToEnvelope(GenerationResult result)
    {
        if (string.Equals(result.Status, GenerationStatuses.Refused, StringComparison.Ordinal))
        {
            return new GenerationFailedException(
                AiError.ProviderRefused,
                "The provider declined to generate this draft. Nothing was changed.");
        }

        if (string.Equals(result.Status, GenerationStatuses.Invalid, StringComparison.Ordinal))
        {
            return new GenerationFailedException(
                AiError.ProviderInvalidResponse,
                "The provider's answer did not satisfy the response schema, so it was not presented as a draft.");
        }

        if (string.Equals(result.ErrorCode, GenerationErrorCodes.CredentialRejected, StringComparison.Ordinal))
        {
            return new GenerationFailedException(
                AiError.CredentialRejected,
                "The provider rejected the stored credential. Replace it in the credential vault.");
        }

        if (string.Equals(result.ErrorCode, GenerationErrorCodes.RateLimited, StringComparison.Ordinal))
        {
            return new GenerationFailedException(
                AiError.RateLimited,
                "The provider is throttling this account. Nothing was retried automatically.");
        }

        if (string.Equals(result.ErrorCode, GenerationErrorCodes.ProviderUnavailable, StringComparison.Ordinal))
        {
            return new GenerationFailedException(
                AiError.ProviderUnavailable,
                "The provider was unavailable before it accepted the request, and the bounded retry was spent.");
        }

        return new GenerationFailedException(
            AiError.ProviderInvalidResponse,
            "The provider did not return a usable answer, so it was not presented as a draft.");
    }

    private async Task<CredentialMetadata?> ResolveCredentialAsync(
        OwnerScope owner,
        string providerId,
        Guid? credentialRef,
        CancellationToken cancellationToken)
    {
        if (credentialRef.HasValue)
        {
            // Scoped, so a credential from another scope is absent rather than refused in a way that
            // confirms it exists.
            var byId = await _credentials
                .FindByIdAsync(owner.ScopeId, credentialRef.Value, cancellationToken)
                .ConfigureAwait(false);

            if (byId is null || !string.Equals(byId.Provider, providerId, StringComparison.Ordinal))
            {
                throw new ResourceNotFoundException("credential");
            }

            return byId;
        }

        return await _credentials.FindAsync(owner.ScopeId, providerId, cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<EvidenceItem> SelectItems(EvidenceBundle bundle, IReadOnlyList<Guid>? requested)
    {
        if (requested is null || requested.Count == 0)
        {
            throw new ValidationFailedException(
                "selectedEvidenceIds",
                "Select at least one evidence item to include.");
        }

        if (requested.Count > EvidenceLimits.MaxItemsPerBundle)
        {
            throw new ValidationFailedException(
                "selectedEvidenceIds",
                "More evidence items were selected than one bundle holds.");
        }

        var byId = bundle.Items.ToDictionary(item => item.Id);
        var selected = new List<EvidenceItem>(requested.Count);
        var seen = new HashSet<Guid>();

        foreach (var id in requested)
        {
            if (!seen.Add(id))
            {
                throw new ValidationFailedException("selectedEvidenceIds", "An evidence item was selected twice.");
            }

            // An id outside this bundle is refused rather than silently dropped. Dropping it would
            // produce a preview whose authorized item list is shorter than the one the owner chose,
            // and the consent hash would be over the shorter one.
            if (!byId.TryGetValue(id, out var item))
            {
                throw new ValidationFailedException(
                    "selectedEvidenceIds",
                    "A selected evidence item is not in this milestone's current evidence.");
            }

            selected.Add(item);
        }

        return selected;
    }

    private static IReadOnlyList<Guid> RequireNarrativeSelections(IReadOnlyList<Guid>? requested)
    {
        if (requested is null || requested.Count == 0)
        {
            return [];
        }

        if (requested.Count > 20)
        {
            throw new ValidationFailedException("narrativeSelections", "Too many narrative selections were named.");
        }

        var distinct = requested.Distinct().Order().ToArray();
        if (distinct.Length != requested.Count)
        {
            throw new ValidationFailedException("narrativeSelections", "A narrative selection was named twice.");
        }

        return distinct;
    }

    private static bool ContainsAll(EvidenceBundle bundle, IReadOnlyList<Guid> ids)
    {
        var present = new HashSet<Guid>(bundle.Items.Select(item => item.Id));
        return ids.All(present.Contains);
    }

    private static string RequirePurpose(string? purpose)
    {
        if (string.IsNullOrWhiteSpace(purpose))
        {
            throw new ValidationFailedException("purpose", "A generation purpose is required.");
        }

        if (purpose.Length > GenerationPreviewPurposes.MaxLength)
        {
            throw new ValidationFailedException("purpose", "The generation purpose is not one this build accepts.");
        }

        if (!GenerationPreviewPurposes.IsEnabled(purpose))
        {
            // Named plainly rather than as a validation error with no explanation: the honest answer
            // is that the extension which owns this purpose has not been built, and an owner who
            // guessed the name deserves to be told that instead of a bare 422.
            throw new ValidationFailedException(
                "purpose",
                $"'{purpose}' is not an enabled generation purpose. Only the milestone story purpose is available.");
        }

        return purpose;
    }

    private static string RequireAggregateRef(string? aggregateRef, string purpose)
    {
        if (string.IsNullOrWhiteSpace(aggregateRef))
        {
            throw new ValidationFailedException(
                "aggregateRef",
                $"The aggregate the '{purpose}' purpose applies to is required.");
        }

        if (aggregateRef.Length > GenerationPreviewPurposes.MaxLength)
        {
            throw new ValidationFailedException("aggregateRef", "The aggregate reference is too long.");
        }

        return aggregateRef;
    }

    private static string RequireProviderId(string? providerId)
    {
        if (string.IsNullOrWhiteSpace(providerId) || providerId.Length > GenerationProviderIds.MaxLength)
        {
            throw new ValidationFailedException(
                "providerId",
                "The provider is not one this build has a contract for.");
        }

        if (!GenerationProviderIds.IsSupported(providerId))
        {
            // The five names are public in the catalogue, so echoing the list helps and leaks
            // nothing. The submitted value is not echoed, in case a client filled the field with
            // something sensitive.
            throw new ValidationFailedException(
                "providerId",
                "The provider is not one this build has a contract for. Choose one of: "
                    + string.Join(", ", GenerationProviderIds.All) + ".");
        }

        return providerId;
    }

    private static string RequireModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ValidationFailedException("model", "A model is required.");
        }

        if (model.Length > GenerationLimits.MaxModelLength)
        {
            throw new ValidationFailedException("model", "The model name is too long.");
        }

        return model;
    }

    private static string? NormalizeEndpoint(string? endpointRef)
    {
        if (string.IsNullOrWhiteSpace(endpointRef))
        {
            return null;
        }

        var trimmed = endpointRef.Trim();
        if (trimmed.Length > GenerationLimits.MaxEndpointRefLength)
        {
            throw new ValidationFailedException("endpointRef", "The endpoint reference is too long.");
        }

        return trimmed;
    }

    private static GenerationLimitsView CurrentLimits() => new(
        GenerationLimits.MaxOutputTokens,
        GenerationLimits.TimeoutSeconds,
        GenerationLimits.MaxInputBytes,
        (int)GenerationLimits.ConsentTtl.TotalSeconds,
        GenerationLimits.MaxTransientAttempts,
        GenerationPreviewPurposes.Enabled);

    /// <summary>
    /// Recomputes the consent binding from the stored row.
    /// </summary>
    /// <remarks>
    /// The preview is stored without its consent hash, and the hash is recomputed from the stored
    /// snapshot on every call rather than kept in a second column. One source of truth means a hash
    /// cannot disagree with the bytes it covers: if the two ever differ, the recomputation is what
    /// the generate call compares against, so an edited row is stale by construction instead of
    /// because a second field was missed.
    /// </remarks>
    private static string StoredConsentHash(GenerationPreview preview, Guid scopeId)
    {
        var snapshot = GenerationSnapshot.Deserialize(preview.SnapshotJson);
        return ConsentBinding.Compute(
            scopeId,
            snapshot.Purpose,
            snapshot.AggregateRef,
            snapshot.ProviderId,
            snapshot.Model,
            snapshot.CredentialRevision,
            snapshot.EvidenceRevision,
            snapshot.VoiceRevision,
            snapshot.InputEvidenceIds,
            snapshot.NarrativeSelections,
            preview.Sha256);
    }
}
