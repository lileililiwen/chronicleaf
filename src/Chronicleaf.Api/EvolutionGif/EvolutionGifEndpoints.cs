using System.Globalization;
using System.Text.Json;
using Chronicleaf.Api.Access;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.EvolutionGifs;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Chronicleaf.Api.EvolutionGifs;

/// <summary>
/// The private evolution GIF routes (EVOLUTION-GIF-EXPORT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Six routes, all private: the public allow-list is untouched and every route carries
/// <c>RequireOwnerSession()</c>. An animation depicts only selected screenshots - candidates
/// are never depicted and no route here promotes one - and the manifest cites the media and
/// caption revisions every frame was built from, in canvas order with the playback the review
/// authorized, so the artifact is re-derivable.
/// </para>
/// <para>
/// <b>The render answers 201 with the artifact, not a job.</b> Rendering is deterministic - no
/// provider, no browser, no queue - so there is nothing to wait for. The selection hash the
/// owner reviewed travels in the body when there is one, and rendering is conditional on the
/// selection still being what was reviewed.
/// </para>
/// <para>
/// <b>Preview is reviewable, download is gated.</b> The draft bytes are viewable behind the
/// preview route - as the animation or, with <c>?mode=static</c>, as the exact first frame
/// playback starts with, which is what a reduced-motion owner reviews - because an owner cannot
/// approve what they cannot see. The download serves the exact approved bytes only while a live
/// approval still describes the stored selection and artifact - never approved is
/// <c>approval_required</c>, a moved selection is <c>approval_stale</c>, under the same codes
/// the review package answers.
/// </para>
/// </remarks>
public static class EvolutionGifEndpoints
{
    public static IEndpointRouteBuilder MapEvolutionGifEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapPost("/api/projects/{projectId:guid}/evolution/gifs", RenderGifAsync)
            .RequireOwnerSession();

        endpoints
            .MapGet("/api/projects/{projectId:guid}/evolution/gifs", ListGifsAsync)
            .RequireOwnerSession();

        endpoints
            .MapGet("/api/evolution/gifs/{gifId:guid}", ReadGifAsync)
            .RequireOwnerSession();

        endpoints
            .MapPost("/api/evolution/gifs/{gifId:guid}/approve", ApproveGifAsync)
            .RequireOwnerSession();

        endpoints
            .MapGet("/api/evolution/gifs/{gifId:guid}/preview", PreviewGifAsync)
            .RequireOwnerSession();

        endpoints
            .MapGet("/api/evolution/gifs/{gifId:guid}/download", DownloadGifAsync)
            .RequireOwnerSession();

        return endpoints;
    }

    private static async Task<IResult> RenderGifAsync(
        Guid projectId,
        HttpContext context,
        RenderEvolutionGifBody? request,
        IEvolutionGifService gifs,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        if (request is null)
        {
            throw new ValidationFailedException(
                "milestoneIds",
                "An evolution GIF animates named milestones.");
        }

        var outcome = await gifs
            .RenderAsync(owner, projectId, ToRender(request), cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(
            await ToResponseAsync(gifs, owner, outcome.Artifact, outcome.Created, cancellationToken).ConfigureAwait(false),
            statusCode: outcome.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
    }

    private static async Task<IResult> ListGifsAsync(
        Guid projectId,
        HttpContext context,
        IEvolutionGifService gifs,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);
        var limit = ListLimit(context);

        var listed = await gifs.ListAsync(owner, projectId, limit, cancellationToken).ConfigureAwait(false);
        var responses = new List<EvolutionGifResponse>(listed.Count);

        foreach (var artifact in listed)
        {
            var (_, current) = await gifs.ReadAsync(owner, artifact.Id, cancellationToken).ConfigureAwait(false);
            responses.Add(ToResponse(artifact, current, created: false));
        }

        return Results.Json(new { items = responses });
    }

    private static async Task<IResult> ReadGifAsync(
        Guid gifId,
        HttpContext context,
        IEvolutionGifService gifs,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var (artifact, current) = await gifs.ReadAsync(owner, gifId, cancellationToken).ConfigureAwait(false);

        return Results.Json(ToResponse(artifact, current, created: false));
    }

    private static async Task<IResult> ApproveGifAsync(
        Guid gifId,
        HttpContext context,
        ApproveEvolutionGifBody? request,
        IEvolutionGifService gifs,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        if (request?.ExpectedSelectionHash is not { Length: > 0 } expected)
        {
            throw new ValidationFailedException(
                "expectedSelectionHash",
                "An approval names the manifest selection hash it reviewed.");
        }

        var outcome = await gifs.ApproveAsync(owner, gifId, expected, cancellationToken).ConfigureAwait(false);

        return Results.Json(ToResponse(outcome.Artifact, selectionCurrent: true, created: outcome.AlreadyApproved));
    }

    private static async Task<IResult> PreviewGifAsync(
        Guid gifId,
        HttpContext context,
        IEvolutionGifService gifs,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);
        var mode = context.Request.Query["mode"].FirstOrDefault();

        if (string.Equals(mode, "static", StringComparison.OrdinalIgnoreCase))
        {
            var (png, width, height) = await gifs.PreviewStaticAsync(owner, gifId, cancellationToken).ConfigureAwait(false);

            context.Response.Headers["X-Evolution-Gif-Static-Width"] = width.ToString(CultureInfo.InvariantCulture);
            context.Response.Headers["X-Evolution-Gif-Static-Height"] = height.ToString(CultureInfo.InvariantCulture);

            return Results.File(png, "image/png");
        }

        if (mode is not null)
        {
            throw new ValidationFailedException(
                "mode",
                "A preview is the animation, or the static first frame with mode=static.");
        }

        var preview = await gifs.PreviewAsync(owner, gifId, cancellationToken).ConfigureAwait(false);

        context.Response.Headers.ETag = $"\"{preview.Sha256}\"";
        context.Response.Headers["X-Evolution-Gif-Approval-Live"] = preview.ApprovalLive ? "true" : "false";

        return Results.File(preview.Gif, "image/gif");
    }

    private static async Task<IResult> DownloadGifAsync(
        Guid gifId,
        HttpContext context,
        IEvolutionGifService gifs,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var export = await gifs.ExportAsync(owner, gifId, cancellationToken).ConfigureAwait(false);

        // The exact approved bytes, as an attachment the owner chose to download. The ETag is
        // the artifact hash the approval bound, so a client can verify what it received.
        context.Response.Headers.ETag = $"\"{export.Sha256}\"";

        return Results.File(
            export.Gif,
            "image/gif",
            $"evolution-{gifId:N}.gif");
    }

    private static RenderEvolutionGifRequest ToRender(RenderEvolutionGifBody request) =>
        new(
            ParseIds(request.MilestoneIds, "milestoneIds"),
            request.SelectedScreenshotIds is null ? null : ParseIds(request.SelectedScreenshotIds, "selectedScreenshotIds"),
            request.ManualOrder is null ? null : ParseIds(request.ManualOrder, "manualOrder"),
            request.DurationsMs,
            request.Loop ?? false,
            request.ExpectedSelectionHash,
            request.IdempotencyKey);

    private static IReadOnlyList<Guid> ParseIds(List<string>? raw, string field)
    {
        if (raw is null)
        {
            return [];
        }

        var ids = new List<Guid>(raw.Count);

        foreach (var entry in raw)
        {
            if (!Guid.TryParse(entry, out var id) || id == Guid.Empty)
            {
                throw new ValidationFailedException(field, "Every id is a non-empty UUID.");
            }

            ids.Add(id);
        }

        return ids;
    }

    private static int ListLimit(HttpContext context)
    {
        var raw = context.Request.Query["limit"].FirstOrDefault();

        if (raw is null)
        {
            return 25;
        }

        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var limit))
        {
            throw new ValidationFailedException("limit", "A limit is a positive number.");
        }

        return Math.Clamp(limit, 1, 100);
    }

    private static EvolutionGifResponse ToResponse(EvolutionGif artifact, bool selectionCurrent, bool created) =>
        new(
            artifact.Id.ToString("D"),
            artifact.ProjectId.ToString("D"),
            JsonDocument.Parse(artifact.ManifestJson),
            artifact.SelectionHash,
            artifact.ArtifactSha256,
            artifact.Width,
            artifact.Height,
            artifact.FrameCount,
            artifact.TotalDurationMs,
            artifact.Loop,
            artifact.Status,
            selectionCurrent,
            created);

    private static async Task<EvolutionGifResponse> ToResponseAsync(
        IEvolutionGifService gifs,
        OwnerScope owner,
        EvolutionGif artifact,
        bool created,
        CancellationToken cancellationToken)
    {
        var (_, current) = await gifs.ReadAsync(owner, artifact.Id, cancellationToken).ConfigureAwait(false);

        return ToResponse(artifact, current, created);
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
