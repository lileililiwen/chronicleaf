using System.Globalization;
using System.Text.Json;
using Chronicleaf.Api.Access;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Evolution;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Chronicleaf.Api.Evolution;

/// <summary>
/// The private evolution routes (SCREENSHOT-EVOLUTION-IMAGES-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Five routes, all private: the public allow-list is untouched and every route carries
/// <c>RequireOwnerSession()</c>. A comparison depicts only selected screenshots - candidates
/// are never compared and no route here promotes one - and the manifest cites the media and
/// caption revisions every cell was built from, so the artifact is re-derivable.
/// </para>
/// <para>
/// <b>The render answers 201 with the artifact, not a job.</b> Rendering is deterministic - no
/// provider, no browser, no queue - so there is nothing to wait for. The selection hash the
/// owner reviewed travels in the body when there is one, and rendering is conditional on the
/// selection still being what was reviewed.
/// </para>
/// <para>
/// <b>Preview is reviewable, download is gated.</b> The draft bytes are viewable behind the
/// preview route, because an owner cannot approve what they cannot see. The download serves the
/// exact approved bytes only while a live approval still describes the stored selection and
/// artifact - never approved is <c>approval_required</c>, a moved selection is
/// <c>approval_stale</c>, under the same codes the review package answers.
/// </para>
/// </remarks>
public static class EvolutionEndpoints
{
    public static IEndpointRouteBuilder MapEvolutionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapPost("/api/projects/{projectId:guid}/evolution/images", RenderEvolutionAsync)
            .RequireOwnerSession();

        endpoints
            .MapGet("/api/projects/{projectId:guid}/evolution/images", ListEvolutionsAsync)
            .RequireOwnerSession();

        endpoints
            .MapGet("/api/evolution/images/{evolutionId:guid}", ReadEvolutionAsync)
            .RequireOwnerSession();

        endpoints
            .MapPost("/api/evolution/images/{evolutionId:guid}/approve", ApproveEvolutionAsync)
            .RequireOwnerSession();

        endpoints
            .MapGet("/api/evolution/images/{evolutionId:guid}/preview", PreviewEvolutionAsync)
            .RequireOwnerSession();

        endpoints
            .MapGet("/api/evolution/images/{evolutionId:guid}/download", DownloadEvolutionAsync)
            .RequireOwnerSession();

        return endpoints;
    }

    private static async Task<IResult> RenderEvolutionAsync(
        Guid projectId,
        HttpContext context,
        RenderEvolutionBody? request,
        IEvolutionImageService evolutions,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        if (request is null)
        {
            throw new ValidationFailedException(
                "milestoneIds",
                "An evolution compares named milestones.");
        }

        var outcome = await evolutions
            .RenderAsync(owner, projectId, ToRender(request), cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(
            await ToResponseAsync(evolutions, owner, outcome.Artifact, outcome.Created, cancellationToken).ConfigureAwait(false),
            statusCode: outcome.Created ? StatusCodes.Status201Created : StatusCodes.Status200OK);
    }

    private static async Task<IResult> ListEvolutionsAsync(
        Guid projectId,
        HttpContext context,
        IEvolutionImageService evolutions,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);
        var limit = ListLimit(context);

        var listed = await evolutions.ListAsync(owner, projectId, limit, cancellationToken).ConfigureAwait(false);
        var responses = new List<EvolutionImageResponse>(listed.Count);

        foreach (var artifact in listed)
        {
            var (_, current) = await evolutions.ReadAsync(owner, artifact.Id, cancellationToken).ConfigureAwait(false);
            responses.Add(ToResponse(artifact, current, created: false));
        }

        return Results.Json(new { items = responses });
    }

    private static async Task<IResult> ReadEvolutionAsync(
        Guid evolutionId,
        HttpContext context,
        IEvolutionImageService evolutions,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var (artifact, current) = await evolutions.ReadAsync(owner, evolutionId, cancellationToken).ConfigureAwait(false);

        return Results.Json(ToResponse(artifact, current, created: false));
    }

    private static async Task<IResult> ApproveEvolutionAsync(
        Guid evolutionId,
        HttpContext context,
        ApproveEvolutionBody? request,
        IEvolutionImageService evolutions,
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

        var outcome = await evolutions.ApproveAsync(owner, evolutionId, expected, cancellationToken).ConfigureAwait(false);

        return Results.Json(ToResponse(outcome.Artifact, selectionCurrent: true, created: outcome.AlreadyApproved));
    }

    private static async Task<IResult> PreviewEvolutionAsync(
        Guid evolutionId,
        HttpContext context,
        IEvolutionImageService evolutions,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var preview = await evolutions.PreviewAsync(owner, evolutionId, cancellationToken).ConfigureAwait(false);

        context.Response.Headers.ETag = $"\"{preview.Sha256}\"";
        context.Response.Headers["X-Evolution-Approval-Live"] = preview.ApprovalLive ? "true" : "false";

        return Results.File(preview.Png, "image/png");
    }

    private static async Task<IResult> DownloadEvolutionAsync(
        Guid evolutionId,
        HttpContext context,
        IEvolutionImageService evolutions,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var export = await evolutions.ExportAsync(owner, evolutionId, cancellationToken).ConfigureAwait(false);

        // The exact approved bytes, as an attachment the owner chose to download. The ETag is
        // the artifact hash the approval bound, so a client can verify what it received.
        context.Response.Headers.ETag = $"\"{export.Sha256}\"";

        return Results.File(
            export.Png,
            "image/png",
            $"evolution-{evolutionId:N}.png");
    }

    private static RenderEvolutionRequest ToRender(RenderEvolutionBody request) =>
        new(
            ParseIds(request.MilestoneIds, "milestoneIds"),
            request.Layout ?? string.Empty,
            request.SelectedScreenshotIds is null ? null : ParseIds(request.SelectedScreenshotIds, "selectedScreenshotIds"),
            request.ManualOrder is null ? null : ParseIds(request.ManualOrder, "manualOrder"),
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

    private static EvolutionImageResponse ToResponse(EvolutionImage artifact, bool selectionCurrent, bool created) =>
        new(
            artifact.Id.ToString("D"),
            artifact.ProjectId.ToString("D"),
            artifact.Layout,
            JsonDocument.Parse(artifact.ManifestJson),
            artifact.SelectionHash,
            artifact.ArtifactSha256,
            artifact.Width,
            artifact.Height,
            artifact.Status,
            selectionCurrent,
            created);

    private static async Task<EvolutionImageResponse> ToResponseAsync(
        IEvolutionImageService evolutions,
        OwnerScope owner,
        EvolutionImage artifact,
        bool created,
        CancellationToken cancellationToken)
    {
        var (_, current) = await evolutions.ReadAsync(owner, artifact.Id, cancellationToken).ConfigureAwait(false);

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
