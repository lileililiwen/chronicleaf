using Chronicleaf.Api.Access;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Content;
using Chronicleaf.Application.Milestones;

namespace Chronicleaf.Api.Content;

/// <summary>
/// The private static blog export routes (STATIC-BLOG-EXPORT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Three routes, all private: the public allow-list is untouched and every route carries
/// <c>RequireOwnerSession()</c>, so <c>/api/health</c> and <c>/api/ready</c> remain the only public
/// surface. An archive is readable only by the owner it belongs to and by nobody else, and a
/// download is an attachment served to the owner whose approvals authorize it - never a public
/// page and never an upstream call.
/// </para>
/// <para>
/// <b>The request freezes; the job builds; the download re-checks.</b> The POST validates every
/// selected approval before a job exists - never approved is <c>approval_required</c>, once
/// approved but moved is <c>approval_stale</c>, a missing picture is <c>media_unavailable</c> -
/// and answers 202 with the operation the owner watches. The status route reports that job; the
/// download route serves the deterministic archive only while every frozen binding still
/// describes the stored state.
/// </para>
/// <para>
/// <b>No route here can approve, generate or publish anything.</b> Approval stays the review
/// package's table, generation stays the drafts package's, and no byte here reaches GitHub or
/// any other host: the archive is a private download until its owner deploys it somewhere.
/// </para>
/// </remarks>
public static class StaticBlogExportEndpoints
{
    public static IEndpointRouteBuilder MapStaticBlogExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapPost("/api/projects/{projectId:guid}/exports/static-blog", RequestExportAsync)
            .RequireOwnerSession();

        endpoints
            .MapGet("/api/projects/{projectId:guid}/exports/static-blog/{operationId:guid}", ReadExportAsync)
            .RequireOwnerSession();

        endpoints
            .MapGet(
                "/api/projects/{projectId:guid}/exports/static-blog/{operationId:guid}/download",
                DownloadExportAsync)
            .RequireOwnerSession();

        return endpoints;
    }

    private static async Task<IResult> RequestExportAsync(
        Guid projectId,
        HttpContext context,
        StaticBlogExportWireRequest? request,
        IStaticBlogExportService exports,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        if (request is null)
        {
            // An absent body is the same request with every field empty, which the use case then
            // refuses by name rather than being interpreted as a request for a default export. A
            // site with no named assets is a site nobody asked for.
            throw new ValidationFailedException(
                "approvedAssetIds",
                "A static site depicts at least one approved post, so name one.");
        }

        var accepted = await exports
            .RequestExportAsync(
                owner,
                projectId,
                new StaticBlogExportRequest(
                    request.ApprovedAssetIds ?? [],
                    request.BasePath,
                    request.ExpectedSelectionHash,
                    request.IdempotencyKey),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(
            new StaticBlogExportAcceptedResponse(accepted.OperationId, accepted.Created),
            statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> ReadExportAsync(
        Guid projectId,
        Guid operationId,
        HttpContext context,
        IStaticBlogExportService exports,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var view = await exports
            .ReadExportAsync(owner, projectId, operationId, cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(StaticBlogExportResponse.From(view));
    }

    private static async Task<IResult> DownloadExportAsync(
        Guid projectId,
        Guid operationId,
        HttpContext context,
        IStaticBlogExportService exports,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var download = await exports
            .RenderDownloadAsync(owner, projectId, operationId, cancellationToken)
            .ConfigureAwait(false);

        // The completed archive, as an attachment the owner chose to download: the deterministic
        // ZIP holding the index, the posts, the approval-covered pictures and their checksum
        // manifest. Never a public page and never an upstream call, under the same approvals the
        // single-file exports re-check.
        return Results.File(download.Bytes, "application/zip", download.FileName);
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

/// <summary>What an owner sends when they ask for a static blog export.</summary>
internal sealed record StaticBlogExportWireRequest(
    IReadOnlyList<Guid>? ApprovedAssetIds,
    string? BasePath,
    string? ExpectedSelectionHash,
    string? IdempotencyKey);

/// <summary>One accepted export, as the request route answers it.</summary>
internal sealed record StaticBlogExportAcceptedResponse(Guid OperationId, bool Created);

/// <summary>One export job, as the status route answers it.</summary>
internal sealed record StaticBlogExportResponse(
    Guid OperationId,
    string Status,
    string? ErrorCode,
    string? ContentSha256,
    int? PostCount,
    int? ImageCount)
{
    public static StaticBlogExportResponse From(StaticBlogExportView view) => new(
        view.OperationId,
        view.Status,
        view.ErrorCode,
        view.ContentSha256,
        view.PostCount,
        view.ImageCount);
}
