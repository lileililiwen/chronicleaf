using Chronicleaf.Api.Access;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Journey;
using Chronicleaf.Application.Milestones;

namespace Chronicleaf.Api.Journey;

/// <summary>
/// The project journey routes: three private owner routes and two anonymous public ones
/// (PUBLIC-PROJECT-JOURNEY-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The preview and publication routes are private: they carry
/// <c>RequireOwnerSession()</c>, so a selection nobody published can never be read from them.
/// The two <c>/api/journey/{slug}</c> routes are marked public deliberately and visibly, and
/// they are the only routes in this package an owner session does not protect: their caller is
/// an anonymous reader, and the published snapshot - frozen at publication time and re-checked
/// against the live approvals on every read - is what authorizes them. They serve no-store
/// responses carrying only explicitly published milestones and entries: no private note, no
/// candidate, no unselected picture and no credential ever reaches them.
/// </para>
/// <para>
/// <b>A preview computes; a publication freezes; a public read re-checks.</b> The POST reports
/// the hash publication must carry. The PUT re-resolves the same selection - never approved is
/// <c>approval_required</c>, once approved but moved is <c>approval_stale</c>, a preview hash
/// that no longer matches is <c>journey_selection_stale</c> - and freezes the snapshot only
/// after every approval passes. The anonymous routes serve that frozen snapshot only while
/// every binding still describes the stored state; a revocation or a lapsed approval suspends
/// it as the same 404 a missing slug answers.
/// </para>
/// <para>
/// <b>No route here can generate, approve or export anything.</b> Approval stays the review
/// package's table, generation stays the drafts package's, and no byte here reaches any host
/// but this one: the projection is served here, not sent anywhere.
/// </para>
/// </remarks>
public static class JourneyEndpoints
{
    public static IEndpointRouteBuilder MapJourneyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapPost("/api/projects/{projectId:guid}/journey/preview", PreviewJourneyAsync)
            .RequireOwnerSession();

        endpoints
            .MapPut("/api/projects/{projectId:guid}/journey", PublishJourneyAsync)
            .RequireOwnerSession();

        endpoints
            .MapGet("/api/projects/{projectId:guid}/journey", ReadJourneyAsync)
            .RequireOwnerSession();

        // The two public routes in this package. The markers are placed deliberately and visibly
        // here so a reader can see the only routes in this repository that serve stored content
        // to an anonymous caller - and what authorizes them is the frozen, explicitly published
        // snapshot, re-checked on every read, not a session.
        endpoints
            .MapGet("/api/journey/{slug}", ReadPublicJourneyAsync)
            .AllowPublicApi();

        endpoints
            .MapGet("/api/journey/{slug}/media/{mediaId:guid}", ReadPublicJourneyMediaAsync)
            .AllowPublicApi();

        return endpoints;
    }

    private static async Task<IResult> PreviewJourneyAsync(
        Guid projectId,
        HttpContext context,
        JourneyPreviewWireRequest? request,
        IProjectJourneyService journeys,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        if (request is null)
        {
            // An absent body is the same request with every field empty, which the use case then
            // refuses by name rather than being interpreted as a request to publish a default
            // selection. A journey with no named entries is a journey nobody asked for.
            throw new ValidationFailedException(
                "approvedAssetIds",
                "A journey depicts at least one approved entry, so name one.");
        }

        var preview = await journeys
            .PreviewAsync(
                owner,
                projectId,
                new JourneyPreviewRequest(request.ApprovedAssetIds ?? []),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(JourneyPreviewResponse.From(preview));
    }

    private static async Task<IResult> PublishJourneyAsync(
        Guid projectId,
        HttpContext context,
        JourneyPublishWireRequest? request,
        IProjectJourneyService journeys,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        if (request is null)
        {
            throw new ValidationFailedException(
                "enabled",
                "A journey publication says whether it enables or revokes, so say so.");
        }

        var view = await journeys
            .PublishAsync(
                owner,
                projectId,
                new JourneyPublishRequest(
                    request.Enabled,
                    request.Slug,
                    request.ExpectedRevision,
                    request.SelectionHash,
                    request.ApprovedAssetIds),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(JourneyResponse.From(view));
    }

    private static async Task<IResult> ReadJourneyAsync(
        Guid projectId,
        HttpContext context,
        IProjectJourneyService journeys,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var view = await journeys
            .ReadAsync(owner, projectId, cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(JourneyResponse.From(view));
    }

    private static async Task<IResult> ReadPublicJourneyAsync(
        string slug,
        HttpContext context,
        IProjectJourneyService journeys,
        CancellationToken cancellationToken)
    {
        var view = await journeys
            .ReadPublicAsync(slug, cancellationToken)
            .ConfigureAwait(false);

        // A cached copy would outlive the revocation that suspends this route, so the public
        // projection is never stored anywhere but the snapshot row: downloaded copies cannot be
        // recalled, and the panel says so.
        context.Response.Headers.CacheControl = "no-store";

        return Results.Json(JourneyPublicResponse.From(view));
    }

    private static async Task<IResult> ReadPublicJourneyMediaAsync(
        string slug,
        Guid mediaId,
        HttpContext context,
        IProjectJourneyService journeys,
        CancellationToken cancellationToken)
    {
        var picture = await journeys
            .ReadPublicMediaAsync(slug, mediaId, cancellationToken)
            .ConfigureAwait(false);

        // The published bytes, served to an anonymous reader that the frozen snapshot authorizes.
        // No-store, because a cached copy would outlive the revocation that suspends this route.
        context.Response.Headers.CacheControl = "no-store";

        return Results.Bytes(picture.Bytes, picture.MediaType, enableRangeProcessing: false);
    }

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

/// <summary>What an owner sends when they ask what a selection would publish.</summary>
internal sealed record JourneyPreviewWireRequest(IReadOnlyList<Guid>? ApprovedAssetIds);

/// <summary>What an owner sends when they publish, republish or revoke a journey.</summary>
internal sealed record JourneyPublishWireRequest(
    bool Enabled,
    string? Slug,
    long? ExpectedRevision,
    string? SelectionHash,
    IReadOnlyList<Guid>? ApprovedAssetIds);

/// <summary>One previewed selection, as the preview route answers it.</summary>
internal sealed record JourneyPreviewResponse(
    string SelectionHash,
    IReadOnlyList<JourneyPreviewEntryResponse> Entries)
{
    public static JourneyPreviewResponse From(JourneyPreview preview) => new(
        preview.SelectionHash,
        [.. preview.Entries.Select(entry => new JourneyPreviewEntryResponse(
            entry.AssetId,
            entry.MilestoneId,
            entry.MilestoneTitle,
            entry.OccurredAt,
            entry.ContentRevision,
            entry.BodyHash,
            entry.EvidenceRevision,
            [.. entry.Pictures.Select(picture => new JourneyPictureResponse(
                picture.MediaId,
                picture.Caption))]))]);
}

/// <summary>One previewed entry, as the preview route answers it.</summary>
internal sealed record JourneyPreviewEntryResponse(
    Guid AssetId,
    Guid MilestoneId,
    string MilestoneTitle,
    DateTimeOffset OccurredAt,
    long ContentRevision,
    string BodyHash,
    long EvidenceRevision,
    IReadOnlyList<JourneyPictureResponse> Pictures);

/// <summary>One previewed picture, as the preview route answers it.</summary>
internal sealed record JourneyPictureResponse(Guid MediaId, string Caption);

/// <summary>One journey, as the private routes answer it.</summary>
internal sealed record JourneyResponse(
    Guid ProjectId,
    string Slug,
    bool Enabled,
    long Revision,
    string SelectionHash,
    string SnapshotRef,
    DateTimeOffset? RevokedAt,
    int EntryCount)
{
    public static JourneyResponse From(JourneyView view) => new(
        view.ProjectId,
        view.Slug,
        view.Enabled,
        view.Revision,
        view.SelectionHash,
        view.SnapshotRef,
        view.RevokedAt,
        view.EntryCount);
}

/// <summary>One journey, as the anonymous public route serves it.</summary>
internal sealed record JourneyPublicResponse(
    string Slug,
    string ProjectName,
    long Revision,
    IReadOnlyList<JourneyPublicEntryResponse> Entries)
{
    public static JourneyPublicResponse From(JourneyPublicView view) => new(
        view.Slug,
        view.ProjectName,
        view.Revision,
        [.. view.Entries.Select(entry => new JourneyPublicEntryResponse(
            entry.MilestoneTitle,
            entry.OccurredAt,
            entry.BodyMarkdown,
            [.. entry.Pictures.Select(picture => new JourneyPublicPictureResponse(
                picture.Caption,
                picture.MediaUrl))]))]);
}

/// <summary>One published entry, as the anonymous public route serves it.</summary>
internal sealed record JourneyPublicEntryResponse(
    string MilestoneTitle,
    DateTimeOffset OccurredAt,
    string BodyMarkdown,
    IReadOnlyList<JourneyPublicPictureResponse> Pictures);

/// <summary>One published picture, as the anonymous public route serves it.</summary>
internal sealed record JourneyPublicPictureResponse(string Caption, string MediaUrl);
