using System.Globalization;
using Chronicleaf.Api.Access;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Xiaohongshu;

namespace Chronicleaf.Api.Content;

/// <summary>
/// The private Xiaohongshu pack routes (XIAOHONGSHU-CONTENT-PACKAGE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Two routes, all private: the public allow-list is untouched and every route carries
/// <c>RequireOwnerSession()</c>, so <c>/api/health</c> and <c>/api/ready</c> remain the only public
/// surface. A pack is readable only by the owner it belongs to and by nobody else, and an export
/// is an attachment served to the owner that approved it - never a public page and never an
/// upstream call, because there is no upstream: this package holds no account, no login, no
/// cookie, no browser automation, no token and no transport.
/// </para>
/// <para>
/// <b>The adaptation answers 201 with the new revision, not a job.</b> Adapting is deterministic
/// - no provider, no repair call, no queue - so there is nothing to wait for and no operation to
/// watch. The revisions the owner was looking at travel in the body, and both must still be
/// current: a body that names a moved evidence or voice head is refused rather than rendered
/// against stale provenance, because a pack of stale content is stale.
/// </para>
/// <para>
/// <b>Preview, edit and approval are the review package's own routes.</b> Nothing here approves
/// anything: the derivative starts life as a draft with no approval row, and the owner reviews
/// it through the delivered edit, approve, unapprove and review routes before the export serves
/// a byte. Publication is a refusal, not a transport: the publish route exists so that a native
/// publish request gets the honest 422 answer instead of a 404 that would send it looking for
/// another route.
/// </para>
/// </remarks>
public static class XiaohongshuPackEndpoints
{
    public static IEndpointRouteBuilder MapXiaohongshuPackEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapPost("/api/assets/{assetId:guid}/adaptations/xiaohongshu", AdaptAsync)
            .RequireOwnerSession();

        endpoints
            .MapPost("/api/assets/{assetId:guid}/adaptations/xiaohongshu/publish", PublishAsync)
            .RequireOwnerSession();

        return endpoints;
    }

    private static async Task<IResult> AdaptAsync(
        Guid assetId,
        HttpContext context,
        AdaptXiaohongshuPackWireRequest? request,
        IXiaohongshuPackService packs,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        if (request is null)
        {
            throw new ValidationFailedException(
                "evidenceRevision",
                "An adaptation names the evidence head it was derived against, so name one.");
        }

        var adapted = await packs
            .AdaptAsync(
                owner,
                assetId,
                new AdaptXiaohongshuPackRequest(
                    request.EvidenceRevisionValue,
                    request.VoiceRevisionValue,
                    request.Title ?? string.Empty,
                    request.Tags ?? [],
                    request.CoverCopy,
                    request.CallToAction,
                    [.. (request.Pictures ?? []).Select(Picture)]),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(
            new AdaptedXiaohongshuPackResponse(
                adapted.AssetId,
                adapted.Revision,
                adapted.BodyHash,
                [.. adapted.TitleCandidates],
                adapted.SelectedTitle,
                adapted.CharCount,
                adapted.LengthWarning,
                adapted.SourceAssetId,
                adapted.SourceRevision,
                adapted.EvidenceRevision,
                adapted.VoiceRevision,
                [.. adapted.ImageOrder.Select(id => id.ToString("D"))],
                adapted.HasCallToAction),
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> PublishAsync(
        Guid assetId,
        HttpContext context,
        IXiaohongshuPackService packs,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        // Always refuses with publisher_unsupported: the service resolves the asset first so a
        // foreign id is still the delivered 404, then answers 422 with no account action.
        await packs.PublishAsync(owner, assetId, cancellationToken).ConfigureAwait(false);

        // Unreachable: publication is a refusal, so success has no representation.
        throw new InvalidOperationException("Xiaohongshu publication cannot succeed.");
    }

    private static XiaohongshuMediaSelection Picture(XiaohongshuPictureWireRequest picture)
    {
        if (picture is null)
        {
            throw new ValidationFailedException(
                "pictures",
                "A selected picture names its media id and its accessibility words.");
        }

        if (!Guid.TryParse((picture.MediaId ?? string.Empty).Trim(), out var mediaId) || mediaId == Guid.Empty)
        {
            throw new ValidationFailedException(
                "selectedScreenshotIds",
                "A selected picture is named by its media id.");
        }

        return new XiaohongshuMediaSelection(mediaId, picture.AltText ?? string.Empty);
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

/// <summary>
/// What an owner sends when they adapt an approved story into a Xiaohongshu pack.
/// </summary>
/// <remarks>
/// Strings and nullables rather than parsed values, because the route parses and refuses them by
/// name. A missing revision is a 422 naming the field an owner can act on rather than a
/// deserializer's default that would read as a request against revision zero.
/// </remarks>
internal sealed record AdaptXiaohongshuPackWireRequest(
    long? EvidenceRevision,
    long? VoiceRevision,
    string? Title,
    string[]? Tags,
    string? CoverCopy,
    string? CallToAction,
    XiaohongshuPictureWireRequest[]? Pictures)
{
    /// <summary>The evidence head, refused by name when absent rather than defaulted.</summary>
    public long EvidenceRevisionValue => EvidenceRevision
        ?? throw new ValidationFailedException(
            "evidenceRevision",
            "An adaptation names the evidence head it was derived against, so name one.");

    /// <summary>The voice revision, refused by name when absent rather than defaulted.</summary>
    public long VoiceRevisionValue => VoiceRevision
        ?? throw new ValidationFailedException(
            "voiceRevision",
            "An adaptation names the voice revision it was derived against, so name one.");
}

/// <summary>One selected picture with its accessibility words, as the wire carries it.</summary>
internal sealed record XiaohongshuPictureWireRequest(string? MediaId, string? AltText);

/// <summary>One adapted Xiaohongshu pack, as the adapt route answers it.</summary>
internal sealed record AdaptedXiaohongshuPackResponse(
    Guid AssetId,
    long Revision,
    string BodyHash,
    IReadOnlyList<string> TitleCandidates,
    string SelectedTitle,
    int CharCount,
    bool LengthWarning,
    Guid SourceAssetId,
    long SourceRevision,
    long EvidenceRevision,
    long VoiceRevision,
    IReadOnlyList<string> ImageOrder,
    bool HasCallToAction);
