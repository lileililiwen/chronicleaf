using Chronicleaf.AI.Contracts;
using Chronicleaf.Api.Access;
using Chronicleaf.Application.AI;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Core;

namespace Chronicleaf.Api.AI;

/// <summary>
/// The private generation routes (LLM-PROVIDER-CONTRACT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Five routes, all private: the public allow-list is untouched and every route carries
/// <c>RequireOwnerSession()</c>, so <c>/api/health</c> and <c>/api/ready</c> remain the only public
/// surface. The delivered origin check and antiforgery requirement apply to every mutation unchanged.
/// </para>
/// <para>
/// <b>The milestone route is an alias, not a second implementation.</b>
/// <c>POST /api/milestones/{id}/generation-preview</c> is exactly
/// <c>purpose: 'milestone-story'</c> with <c>aggregateRef: {id}</c> on the generic
/// <c>POST /api/generation-previews</c>, and both handlers build the same input and call the same
/// use case. The milestone route exists because an owner thinks in milestones and a client should
/// not have to know that a preview is also addressable generically - but there is one code path, so
/// the two cannot drift into disagreeing about what a preview is.
/// </para>
/// <para>
/// The generate route takes the consent hash in its body, and that is the whole of its
/// authentication story for the authorization: it does not authenticate anything by itself. The
/// owner's session is the authentication; the hash is a binding the server recomputes from the
/// stored snapshot and compares, so presenting somebody else's hash finds no row in this scope and
/// answers 404.
/// </para>
/// </remarks>
public static class AIGenerationEndpoints
{
    /// <summary>
    /// The request body a milestone preview gets when the caller sent none.
    /// </summary>
    /// <remarks>
    /// Every field empty rather than a set of guesses. <c>EvidenceRevision</c> is -1 so a body that
    /// did arrive without it is refused the same way, instead of an absent number being read as
    /// revision zero and matching a milestone that happens to have no evidence yet.
    /// </remarks>
    private static readonly GenerationPreviewRequest EmptyPreviewRequest = new(
        Purpose: null,
        AggregateRef: null,
        MilestoneId: null,
        ProviderId: null,
        Model: null,
        CredentialRef: null,
        EndpointRef: null,
        EvidenceRevision: -1,
        SelectedEvidenceIds: null,
        NarrativeSelections: null);

    public static IEndpointRouteBuilder MapAIGenerationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapGet("/api/ai/providers", ListProvidersAsync)
            .RequireOwnerSession();

        endpoints
            .MapPost("/api/settings/ai", ResolveSettingsAsync)
            .RequireOwnerSession();

        endpoints
            .MapPost("/api/generation-previews", CreatePreviewAsync)
            .RequireOwnerSession();

        endpoints
            .MapPost("/api/milestones/{milestoneId:guid}/generation-preview", CreateMilestonePreviewAsync)
            .RequireOwnerSession();

        endpoints
            .MapPost("/api/generation-previews/{previewId:guid}/generate", GenerateAsync)
            .RequireOwnerSession();

        return endpoints;
    }

    private static async Task<IResult> ListProvidersAsync(
        HttpContext context,
        IGenerationPreviewService generation,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);
        var catalogue = await generation
            .ReadProvidersAsync(owner.ScopeId, cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(AiProviderCatalogueResponse.From(catalogue));
    }

    private static async Task<IResult> ResolveSettingsAsync(
        HttpContext context,
        AiSettingsRequest? request,
        IGenerationPreviewService generation,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var resolution = await generation
            .ResolveSettingsAsync(
                owner,
                new AiSettingsRequest(
                    request?.ProviderId,
                    request?.Model,
                    request?.CredentialRef,
                    request?.EndpointRef),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(AiSettingsResponse.From(resolution));
    }

    private static async Task<IResult> CreatePreviewAsync(
        HttpContext context,
        GenerationPreviewRequest? request,
        IGenerationPreviewService generation,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        return await CreateAsync(owner, request, generation, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> CreateMilestonePreviewAsync(
        Guid milestoneId,
        HttpContext context,
        GenerationPreviewRequest? request,
        IGenerationPreviewService generation,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        // The alias, written as the alias: the same purpose and the same aggregate the generic route
        // would have been given. An absent body is the same request with every field empty, which the
        // use case then refuses by name rather than being interpreted as a wildcard.
        return await CreateAsync(
            owner,
            (request ?? EmptyPreviewRequest).ForMilestone(milestoneId),
            generation,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> GenerateAsync(
        Guid previewId,
        HttpContext context,
        GenerationRunRequest? request,
        IGenerationPreviewService generation,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var run = await generation
            .GenerateAsync(owner, previewId, request?.ConsentHash, cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(GenerationRunResponse.From(run));
    }

    private static async Task<IResult> CreateAsync(
        OwnerScope owner,
        GenerationPreviewRequest? request,
        IGenerationPreviewService generation,
        CancellationToken cancellationToken)
    {
        var view = await generation
            .CreatePreviewAsync(
                owner,
                new GenerationPreviewInput(
                    request?.Purpose,
                    request?.AggregateRef,
                    request?.MilestoneId,
                    request?.ProviderId,
                    request?.Model,
                    request?.CredentialRef,
                    request?.EndpointRef,
                    request?.EvidenceRevision ?? -1,
                    request?.SelectedEvidenceIds,
                    request?.NarrativeSelections),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(GenerationPreviewResponse.From(view));
    }

    /// <summary>
    /// The scope and the identity this request acts under. Both come from the access package, so a
    /// request body cannot influence either.
    /// </summary>
    private static async Task<OwnerScope> OwnerScopeAsync(
        HttpContext context,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var session = AccessEndpoints.OwnerSession(context);
        var identity = await access.EnsureOwnerAsync(cancellationToken).ConfigureAwait(false);

        return new OwnerScope(identity.InstallationId, session.ActorId);
    }
}

/// <summary>One provider slot as the catalogue publishes it.</summary>
internal sealed record AiProviderResponse(
    string Id,
    bool AdapterInstalled,
    ProviderCapabilityResponse? Capability,
    bool Configured,
    long? CredentialRevision)
{
    public static AiProviderResponse From(AiProviderView view) => new(
        view.Id,
        view.AdapterInstalled,
        view.Capability is null ? null : ProviderCapabilityResponse.From(view.Capability),
        view.Configured,
        view.CredentialRevision);
}

/// <summary>
/// One adapter's own capability descriptor.
/// </summary>
/// <param name="Id">The slot the descriptor names.</param>
/// <param name="StructuredOutputSupport">The provider can return structure-constrained output.</param>
/// <param name="NativeJsonSchema">
/// The provider enforces the schema itself. False - and the absent descriptor, which is also false by
/// omission - is what selects prompt plus strict local validation.
/// </param>
/// <param name="LocalEndpoint">The provider runs on an operator-named endpoint.</param>
/// <param name="RequiresCredential">The provider cannot be called without a stored credential.</param>
internal sealed record ProviderCapabilityResponse(
    string Id,
    bool StructuredOutputSupport,
    bool NativeJsonSchema,
    bool LocalEndpoint,
    bool RequiresCredential)
{
    public static ProviderCapabilityResponse From(ProviderCapability capability) => new(
        capability.Id,
        capability.StructuredOutputSupport,
        capability.NativeJsonSchema,
        capability.LocalEndpoint,
        capability.RequiresCredential);
}

/// <summary>The catalogue, with configured and supported kept apart and the bounds published.</summary>
internal sealed record AiProviderCatalogueResponse(
    IReadOnlyList<AiProviderResponse> Supported,
    IReadOnlyList<AiProviderResponse> Configured,
    IReadOnlyList<AiProviderResponse> Usable,
    GenerationLimitsResponse Limits)
{
    public static AiProviderCatalogueResponse From(AiProviderCatalogue catalogue) => new(
        [.. catalogue.Supported.Select(AiProviderResponse.From)],
        [.. catalogue.Configured.Select(AiProviderResponse.From)],
        [.. catalogue.Usable.Select(AiProviderResponse.From)],
        GenerationLimitsResponse.From(catalogue.Limits));
}

/// <summary>The bounds every call runs inside, published so a surface can show them rather than guess.</summary>
internal sealed record GenerationLimitsResponse(
    int MaxOutputTokens,
    int TimeoutSeconds,
    int MaxInputBytes,
    int ConsentTtlSeconds,
    int MaxTransientAttempts,
    IReadOnlyList<string> EnabledPurposes)
{
    public static GenerationLimitsResponse From(GenerationLimitsView limits) => new(
        limits.MaxOutputTokens,
        limits.TimeoutSeconds,
        limits.MaxInputBytes,
        limits.ConsentTtlSeconds,
        limits.MaxTransientAttempts,
        limits.EnabledPurposes);
}

/// <summary>An owner's resolved provider selection.</summary>
internal sealed record AiSettingsResponse(
    string ProviderId,
    string Model,
    bool AdapterInstalled,
    ProviderCapabilityResponse? Capability,
    bool? NativeJsonSchema,
    Guid? CredentialId,
    long? CredentialRevision,
    string? EndpointRef,
    GenerationLimitsResponse Limits)
{
    public static AiSettingsResponse From(AiSettingsResolution resolution) => new(
        resolution.ProviderId,
        resolution.Model,
        resolution.AdapterInstalled,
        resolution.Capability is null ? null : ProviderCapabilityResponse.From(resolution.Capability),
        resolution.NativeJsonSchema,
        resolution.CredentialId,
        resolution.CredentialRevision,
        resolution.EndpointRef,
        GenerationLimitsResponse.From(resolution.Limits));
}

/// <summary>What an owner asked to preview.</summary>
/// <remarks>
/// One constructor, deliberately. A second convenience constructor here would make this record
/// ambiguous to the JSON deserializer - which refuses a type with more than one constructor that is
/// not annotated - so the milestone route's defaults are applied in the handler instead, where they
/// are visible.
/// </remarks>
internal sealed record GenerationPreviewRequest(
    string? Purpose,
    string? AggregateRef,
    Guid? MilestoneId,
    string? ProviderId,
    string? Model,
    Guid? CredentialRef,
    string? EndpointRef,
    long EvidenceRevision,
    IReadOnlyList<Guid>? SelectedEvidenceIds,
    IReadOnlyList<Guid>? NarrativeSelections)
{
    /// <summary>
    /// The same body, bound to the milestone the route addressed.
    /// </summary>
    /// <remarks>
    /// The purpose and the aggregate are filled in only when the body left them out. A body that
    /// named either is passed through as it was, so a client that says "milestone story for
    /// something else" is refused by the use case rather than having its request rewritten into
    /// agreeing with the route.
    /// </remarks>
    public GenerationPreviewRequest ForMilestone(Guid milestoneId) => this with
    {
        Purpose = string.IsNullOrWhiteSpace(Purpose)
            ? GenerationPreviewPurposes.MilestoneStory
            : Purpose,
        AggregateRef = string.IsNullOrWhiteSpace(AggregateRef)
            ? milestoneId.ToString("d")
            : AggregateRef,
    };
}

/// <summary>
/// The owner-authorized preview, exactly the ten fields the design names.
/// </summary>
internal sealed record GenerationPreviewResponse(
    Guid PreviewId,
    string ConsentHash,
    IReadOnlyList<Guid> InputEvidenceIds,
    IReadOnlyList<string> Redactions,
    string ProviderId,
    string Model,
    long? CredentialRevision,
    long EvidenceRevision,
    long VoiceRevision,
    DateTimeOffset ExpiresAt)
{
    public static GenerationPreviewResponse From(GenerationPreviewView view) => new(
        view.PreviewId,
        view.ConsentHash,
        view.InputEvidenceIds,
        view.Redactions,
        view.ProviderId,
        view.Model,
        view.CredentialRevision,
        view.EvidenceRevision,
        view.VoiceRevision,
        view.ExpiresAt);
}

/// <summary>The body of the generate call. The consent hash is a binding, not a credential.</summary>
internal sealed record GenerationRunRequest(string? ConsentHash);

/// <summary>The result of one bounded call. A draft, and only a draft.</summary>
internal sealed record GenerationRunResponse(
    string Status,
    string? Text,
    string? ProviderRequestId,
    int? InputTokens,
    int? OutputTokens,
    string FinishReason,
    string? ErrorCode,
    bool NativeJsonSchema,
    bool LocalValidationApplied,
    int Attempts)
{
    public static GenerationRunResponse From(GenerationRunView run) => new(
        run.Status,
        run.Text,
        run.ProviderRequestId,
        run.InputTokens,
        run.OutputTokens,
        run.FinishReason,
        run.ErrorCode,
        run.NativeJsonSchema,
        run.LocalValidationApplied,
        run.Attempts);
}
