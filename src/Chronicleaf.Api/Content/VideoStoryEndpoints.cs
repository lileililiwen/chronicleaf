using System.Globalization;
using Chronicleaf.Api.Access;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.VideoStory;

namespace Chronicleaf.Api.Content;

/// <summary>
/// The private video-story routes (VIDEO-STORY-SCRIPTS-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Two routes, all private: the public allow-list is untouched and every route carries
/// <c>RequireOwnerSession()</c>, so <c>/api/health</c> and <c>/api/ready</c> remain the only public
/// surface. A script is readable only by the owner it belongs to and by nobody else, and an export
/// is an attachment served to the owner that approved it - never a public page and never an
/// upstream call, because there is no upstream: this package holds no renderer, no encoder, no
/// account and no transport.
/// </para>
/// <para>
/// <b>Generation answers 201 with the new revision, not a job.</b> Scripting is deterministic -
/// no provider, no repair call, no queue - so there is nothing to wait for and no operation to
/// watch. The revisions the owner was looking at travel in the body, and both must still be
/// current: a body that names a moved evidence or voice head is refused rather than scripted
/// against stale provenance, because a script of stale content is stale.
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
public static class VideoStoryEndpoints
{
    public static IEndpointRouteBuilder MapVideoStoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapPost("/api/milestones/{milestoneId:guid}/video-stories", GenerateAsync)
            .RequireOwnerSession();

        endpoints
            .MapPost("/api/assets/{assetId:guid}/video-story/publish", PublishAsync)
            .RequireOwnerSession();

        return endpoints;
    }

    private static async Task<IResult> GenerateAsync(
        Guid milestoneId,
        HttpContext context,
        GenerateVideoStoryWireRequest? request,
        IVideoStoryService stories,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        if (request is null)
        {
            throw new ValidationFailedException(
                "durationSeconds",
                "A video story covers 15, 30 or 60 seconds exactly, so name the runtime.");
        }

        var generated = await stories
            .GenerateAsync(
                owner,
                milestoneId,
                new GenerateVideoStoryRequest(
                    request.DurationSecondsValue,
                    request.SourceAssetIdValue,
                    request.EvidenceRevisionValue,
                    request.VoiceRevisionValue,
                    [.. (request.Shots ?? []).Select((shot, index) => Shot(index, shot))]),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(
            new GeneratedVideoStoryResponse(
                generated.AssetId,
                generated.Revision,
                generated.BodyHash,
                generated.DurationSeconds,
                generated.ShotCount,
                generated.TotalMs,
                generated.SourceAssetId,
                generated.SourceRevision,
                generated.EvidenceRevision,
                generated.VoiceRevision,
                [.. generated.ImageOrder.Select(id => id.ToString("D"))]),
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> PublishAsync(
        Guid assetId,
        HttpContext context,
        IVideoStoryService stories,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        // Always refuses with publisher_unsupported: the service resolves the asset first so a
        // foreign id is still the delivered 404, then answers 422 with no account action.
        await stories.PublishAsync(owner, assetId, cancellationToken).ConfigureAwait(false);

        // Unreachable: publication is a refusal, so success has no representation.
        throw new InvalidOperationException("Video publication cannot succeed.");
    }

    private static VideoStoryShotRequest Shot(int index, VideoStoryShotWireRequest? shot)
    {
        string Field(string member) => string.Create(
            CultureInfo.InvariantCulture, $"shots[{index}].{member}");

        if (shot is null)
        {
            throw new ValidationFailedException(
                "shots",
                "A video story holds at least one shot. Select material or omit the script.");
        }

        if (!Guid.TryParse((shot.ScreenshotMediaId ?? string.Empty).Trim(), out var mediaId)
            || mediaId == Guid.Empty)
        {
            throw new ValidationFailedException(
                Field("screenshotMediaId"),
                "A shot depicts a selected screenshot, so name its media id.");
        }

        if (shot.StartMs is null || shot.StartMs < 0)
        {
            throw new ValidationFailedException(
                Field("startMs"),
                "A shot's timings are milliseconds from the start, never negative.");
        }

        if (shot.EndMs is null || shot.EndMs < 0)
        {
            throw new ValidationFailedException(
                Field("endMs"),
                "A shot's timings are milliseconds from the start, never negative.");
        }

        var evidenceIds = new List<Guid>();

        foreach (var raw in shot.SourceEvidenceIds ?? [])
        {
            if (!Guid.TryParse((raw ?? string.Empty).Trim(), out var evidenceId) || evidenceId == Guid.Empty)
            {
                throw new ValidationFailedException(
                    Field("sourceEvidenceIds"),
                    "A shot cites the evidence it shows: every citation names an id.");
            }

            evidenceIds.Add(evidenceId);
        }

        return new VideoStoryShotRequest(
            mediaId,
            shot.Caption ?? string.Empty,
            shot.Narration ?? string.Empty,
            (shot.Transition ?? string.Empty).Trim(),
            shot.StartMs.Value,
            shot.EndMs.Value,
            evidenceIds);
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
/// What an owner sends when they script an approved story into a video storyboard.
/// </summary>
/// <remarks>
/// Strings and nullables rather than parsed values, because the route parses and refuses them by
/// name. A missing runtime, source or revision is a 422 naming the field an owner can act on
/// rather than a deserializer's default that would read as a request against zero.
/// </remarks>
internal sealed record GenerateVideoStoryWireRequest(
    int? DurationSeconds,
    string? SourceAssetId,
    long? EvidenceRevision,
    long? VoiceRevision,
    VideoStoryShotWireRequest[]? Shots)
{
    /// <summary>The runtime, refused by name when absent rather than defaulted.</summary>
    public int DurationSecondsValue => DurationSeconds
        ?? throw new ValidationFailedException(
            "durationSeconds",
            "A video story covers 15, 30 or 60 seconds exactly, so name the runtime.");

    /// <summary>The approved story, refused by name when absent or unparsable.</summary>
    public Guid SourceAssetIdValue
    {
        get
        {
            if (!Guid.TryParse((SourceAssetId ?? string.Empty).Trim(), out var assetId) || assetId == Guid.Empty)
            {
                throw new ValidationFailedException(
                    "sourceAssetId",
                    "A video story is derived from the milestone's approved Markdown story, so name it.");
            }

            return assetId;
        }
    }

    /// <summary>The evidence head, refused by name when absent rather than defaulted.</summary>
    public long EvidenceRevisionValue => EvidenceRevision
        ?? throw new ValidationFailedException(
            "evidenceRevision",
            "A script names the evidence head it was derived against, so name one.");

    /// <summary>The voice revision, refused by name when absent rather than defaulted.</summary>
    public long VoiceRevisionValue => VoiceRevision
        ?? throw new ValidationFailedException(
            "voiceRevision",
            "A script names the voice revision it was derived against, so name one.");
}

/// <summary>One scripted shot, as the wire carries it.</summary>
internal sealed record VideoStoryShotWireRequest(
    string? ScreenshotMediaId,
    string? Caption,
    string? Narration,
    string? Transition,
    long? StartMs,
    long? EndMs,
    string[]? SourceEvidenceIds);

/// <summary>One scripted video story, as the generate route answers it.</summary>
internal sealed record GeneratedVideoStoryResponse(
    Guid AssetId,
    long Revision,
    string BodyHash,
    int DurationSeconds,
    int ShotCount,
    long TotalMs,
    Guid SourceAssetId,
    long SourceRevision,
    long EvidenceRevision,
    long VoiceRevision,
    IReadOnlyList<string> ImageOrder);
