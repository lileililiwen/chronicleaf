using System.Globalization;
using Chronicleaf.Api.Access;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Voice;
using Chronicleaf.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Chronicleaf.Api.Voice;

/// <summary>
/// The private voice routes (VOICE-PROFILES-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Nine routes, all private: the public allow-list is untouched and every route carries
/// <c>RequireOwnerSession()</c>, so <c>/api/health</c> and <c>/api/ready</c> remain the only public
/// surface. The origin check and the antiforgery requirement of the delivered boundary apply to
/// every mutation unchanged.
/// </para>
/// <para>
/// The three reads of a profile all publish the current revision as an <c>ETag</c>, including the
/// read of a project that has never had one - as 0. That is what lets a client present a correct
/// <c>If-Match</c> on its first save: the token that says "nothing is stored yet" has to be a token
/// the client can actually read, and inventing a placeholder at save time is what strands a write.
/// </para>
/// <para>
/// The check route is a route that writes nothing. It is a <c>POST</c> because it judges text the
/// caller holds, not text this system stored, and it is behind the same origin and antiforgery
/// checks as any other mutation - but there is no revision to present and no statement it can run.
/// </para>
/// </remarks>
public static class VoiceEndpoints
{
    public static IEndpointRouteBuilder MapVoiceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapGet("/api/voice-presets", ListPresets)
            .RequireOwnerSession();

        endpoints
            .MapGet("/api/projects/{projectId:guid}/voice-profile", GetProfileAsync)
            .RequireOwnerSession();

        endpoints
            .MapPut("/api/projects/{projectId:guid}/voice-profile", SaveProfileAsync)
            .RequireOwnerSession();

        endpoints
            .MapPost("/api/projects/{projectId:guid}/voice-check", CheckTextAsync)
            .RequireOwnerSession();

        endpoints
            .MapGet("/api/projects/{projectId:guid}/voice-drafts", ListDraftsAsync)
            .RequireOwnerSession();

        endpoints
            .MapPost("/api/projects/{projectId:guid}/voice-drafts", CreateDraftAsync)
            .RequireOwnerSession();

        endpoints
            .MapGet("/api/voice-drafts/{draftId:guid}", GetDraftAsync)
            .RequireOwnerSession();

        endpoints
            .MapPut("/api/voice-drafts/{draftId:guid}", EditDraftAsync)
            .RequireOwnerSession();

        endpoints
            .MapPost("/api/voice-drafts/{draftId:guid}/approve", ApproveDraftAsync)
            .RequireOwnerSession();

        return endpoints;
    }

    private static IResult ListPresets(IVoiceProfileService voice) =>
        Results.Json(new VoicePresetListResponse(
            [.. voice.Presets().Select(VoicePresetResponse.From)],
            VoiceValuesResponse.From(VoiceValues.Defaults),
            VoiceVocabularies.Languages,
            VoiceVocabularies.Tones,
            VoiceVocabularies.TechnicalDetails,
            VoiceVocabularies.MarketingLevels,
            VoiceFindingRules.All,
            VoiceLimitsResponse.Instance));

    private static async Task<IResult> GetProfileAsync(
        Guid projectId,
        HttpContext context,
        IVoiceProfileService voice,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);
        var view = await voice.GetAsync(owner.ScopeId, projectId, cancellationToken).ConfigureAwait(false);

        // Published even for a project with no stored profile, as 0, so the first save has a token to
        // present rather than a value the client has to guess.
        context.Response.Headers.ETag = VoiceMapping.EntityTag(view.Revision);

        return Results.Json(VoiceProfileResponse.From(view));
    }

    private static async Task<IResult> SaveProfileAsync(
        Guid projectId,
        HttpContext context,
        VoiceProfileWriteRequest? request,
        IVoiceProfileService voice,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        // 428 when absent, 409 when stale - both raised before the handler body runs, so a refused
        // save has written nothing.
        var expectedRevision = VoiceMapping.RequiredRevision(context, "voice profile");

        var view = await voice.SaveAsync(
            owner.ScopeId,
            projectId,
            new VoiceProfileWrite(
                request?.PresetId ?? string.Empty,
                (request?.Overrides ?? new VoiceOverridesRequest()).ToOverrides(),
                expectedRevision),
            cancellationToken).ConfigureAwait(false);

        context.Response.Headers.ETag = VoiceMapping.EntityTag(view.Revision);

        return Results.Json(VoiceProfileResponse.From(view));
    }

    private static async Task<IResult> CheckTextAsync(
        Guid projectId,
        HttpContext context,
        VoiceCheckRequest? request,
        IVoiceProfileService voice,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);
        var result = await voice
            .CheckAsync(owner.ScopeId, projectId, request?.Title ?? string.Empty, request?.Body ?? string.Empty, cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(VoiceCheckResponse.From(result));
    }

    private static async Task<IResult> ListDraftsAsync(
        Guid projectId,
        HttpContext context,
        IVoiceProfileService voice,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);
        var rawCursor = context.Request.Query["cursor"].ToString();

        var page = await voice
            .ListDraftsAsync(
                owner.ScopeId,
                projectId,
                new VoiceDraftPageRequest(
                    ReadLimit(context),
                    // An absent query parameter and an empty one both mean "start at the beginning".
                    // Treating "" as a cursor value would make the first page of every list a 422.
                    string.IsNullOrEmpty(rawCursor) ? null : rawCursor),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(VoiceDraftPageResponse.From(page));
    }

    private static async Task<IResult> CreateDraftAsync(
        Guid projectId,
        HttpContext context,
        VoiceDraftCreateRequest? request,
        IVoiceProfileService voice,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);
        var view = await voice
            .CreateDraftAsync(
                owner.ScopeId,
                projectId,
                new VoiceDraftCreate(request?.Title ?? string.Empty, request?.Body ?? string.Empty),
                cancellationToken)
            .ConfigureAwait(false);

        context.Response.Headers.ETag = VoiceMapping.EntityTag(view.Draft.Revision);

        return Results.Created($"/api/voice-drafts/{view.Draft.Id}", VoiceDraftResponse.From(view));
    }

    private static async Task<IResult> GetDraftAsync(
        Guid draftId,
        HttpContext context,
        IVoiceProfileService voice,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);
        var view = await voice.GetDraftAsync(owner.ScopeId, draftId, cancellationToken).ConfigureAwait(false);

        context.Response.Headers.ETag = VoiceMapping.EntityTag(view.Draft.Revision);

        return Results.Json(VoiceDraftResponse.From(view));
    }

    private static async Task<IResult> EditDraftAsync(
        Guid draftId,
        HttpContext context,
        VoiceDraftEditRequest? request,
        IVoiceProfileService voice,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);
        var expectedRevision = VoiceMapping.RequiredRevision(context, "voice draft");

        var view = await voice.EditDraftAsync(
            owner.ScopeId,
            draftId,
            new VoiceDraftEdit(request?.Title ?? string.Empty, request?.Body ?? string.Empty, expectedRevision),
            cancellationToken).ConfigureAwait(false);

        context.Response.Headers.ETag = VoiceMapping.EntityTag(view.Draft.Revision);

        return Results.Json(VoiceDraftResponse.From(view));
    }

    private static async Task<IResult> ApproveDraftAsync(
        Guid draftId,
        HttpContext context,
        IVoiceProfileService voice,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);
        var expectedRevision = VoiceMapping.RequiredRevision(context, "voice draft");

        // Refused with 422 while any finding remains, and the stored title and body are not written to
        // in either branch. The 422 carries one field error per flagged field.
        var view = await voice
            .ApproveDraftAsync(owner.ScopeId, draftId, expectedRevision, cancellationToken)
            .ConfigureAwait(false);

        context.Response.Headers.ETag = VoiceMapping.EntityTag(view.Draft.Revision);

        return Results.Json(VoiceDraftResponse.From(view));
    }

    /// <summary>
    /// The scope and the identity this request acts under. Both come from the access package: the
    /// actor is the validated session's and the scope is the installation that session belongs to, so
    /// a request body cannot influence either.
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

    private static int? ReadLimit(HttpContext context)
    {
        var raw = context.Request.Query["limit"].ToString();

        return int.TryParse(raw, out var limit) ? limit : null;
    }
}

/// <summary>The <c>If-Match</c> and <c>ETag</c> handling this package's mutations share.</summary>
internal static class VoiceMapping
{
    /// <summary>
    /// The revision a mutation must present. A missing <c>If-Match</c> is 428 and an unparseable or
    /// negative one is 422; neither ever reaches a store statement.
    /// </summary>
    public static long RequiredRevision(HttpContext context, string resource)
    {
        ArgumentNullException.ThrowIfNull(context);

        var presented = context.Request.Headers.IfMatch.ToString().Trim();

        if (presented.Length == 0)
        {
            throw new IfMatchRequiredException(resource);
        }

        if (presented.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            presented = presented[2..];
        }

        presented = presented.Trim().Trim('"');

        if (!long.TryParse(presented, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) ||
            revision < 0)
        {
            throw new ValidationFailedException("If-Match", "The If-Match revision is not a revision this resource has.");
        }

        return revision;
    }

    /// <summary>The revision as a strong entity tag, the same shape every other package publishes.</summary>
    public static string EntityTag(long revision) => $"\"{revision.ToString(CultureInfo.InvariantCulture)}\"";
}
