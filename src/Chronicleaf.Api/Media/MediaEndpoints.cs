using Chronicleaf.Api.Access;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Media;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Core;
using Microsoft.Net.Http.Headers;

namespace Chronicleaf.Api.Media;

/// <summary>
/// The private media routes (LOCAL-MEDIA-STORAGE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// All three routes are private, and that is the access package's default rather than something these
/// handlers re-check: a matched <c>/api</c> endpoint without the public marker has already been given
/// an authenticated owner session, the trusted origin and the antiforgery value, and anything else
/// was refused before this code ran. The public allow-list is therefore unchanged - there is no public
/// media route, and there is no directory listing anywhere in this file.
/// </para>
/// <para>
/// The upload takes its bytes as the request body and its project from the query string, rather than
/// as a multipart form. That is a deliberate choice about what a client gets to say: a form would
/// carry a filename and a declared content type, and this package accepts neither. With a raw body the
/// only thing a caller supplies is the bytes, and the store decides the type by decoding them. The
/// declared content type is read once, for the store's diagnostics, and never trusted.
/// </para>
/// <para>
/// Nothing in a response is a filesystem path, and the read sets <c>X-Content-Type-Options: nosniff</c>
/// with the decoded type, so a browser cannot be talked into treating a stored image as anything
/// else.
/// </para>
/// </remarks>
public static class MediaEndpoints
{
    public static IEndpointRouteBuilder MapMediaEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/api/media", UploadMediaAsync).RequireOwnerSession();
        endpoints.MapGet("/api/media/{mediaId:guid}", GetMediaAsync).RequireOwnerSession();
        endpoints.MapDelete("/api/media/{mediaId:guid}", RetireMediaAsync).RequireOwnerSession();

        return endpoints;
    }

    private static async Task<IResult> UploadMediaAsync(
        HttpContext context,
        IMediaStore media,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        if (!Guid.TryParse(context.Request.Query["projectId"].ToString(), out var projectId))
        {
            // A missing or unparseable project id is a semantic validation failure, not a not-found:
            // there is no id here to look up, and answering 404 would imply there was.
            throw new ValidationFailedException("projectId", "A project id is required.");
        }

        var uploaded = await media.PutAsync(
            owner,
            new PutMediaInput(
                projectId,
                context.Request.Body,
                DeclaredContentType(context.Request.ContentType)),
            cancellationToken).ConfigureAwait(false);

        // The revision is published as the entity-tag, as it is for every other created record.
        context.Response.Headers.ETag = $"\"{uploaded.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"";

        return Results.Created(
            $"/api/media/{uploaded.Id.ToString("D")}",
            MediaResponse.From(uploaded));
    }

    private static async Task<IResult> GetMediaAsync(
        Guid mediaId,
        HttpContext context,
        IMediaStore media,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        await using var content = await media.OpenAsync(owner, mediaId, cancellationToken).ConfigureAwait(false);

        // The stored length is the number of bytes the store verified before it registered the record.
        // Comparing it with what actually comes back is the read-side half of "no ready row points at
        // missing bytes": a row whose object has been truncated or replaced answers 404 rather than
        // serving something that is not the image the record describes.
        var buffer = new MemoryStream((int)content.Ref.ByteLength);
        await using (buffer)
        {
            await content.Content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (buffer.Length != content.Ref.ByteLength)
            {
                throw new MediaNotFoundException();
            }

            // The stored type, never the caller's or the provider's guess, and never sniffable: a
            // stored image is served as the image it decoded to or not at all.
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers.CacheControl = "private, no-store";

            // Buffered rather than piped, because the package's own bound already caps one image at
            // 20 MiB and a buffer is what lets this handler own the provider stream's lifetime
            // deterministically instead of leaving it to a result that may outlive the request.
            return Results.File(buffer.ToArray(), content.Ref.MediaType, enableRangeProcessing: false);
        }
    }

    private static async Task<IResult> RetireMediaAsync(
        Guid mediaId,
        HttpContext context,
        IMediaStore media,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);
        var retired = await media.RetireAsync(owner, mediaId, cancellationToken).ConfigureAwait(false);

        return Results.Json(RetiredMediaResponse.From(retired));
    }

    /// <summary>
    /// The scope and the identity this request acts under. Both come from the access package: the
    /// actor is the validated session's, and the scope is the installation that session belongs to. A
    /// request body and a query string cannot influence either.
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

    /// <summary>
    /// The type the caller declared, or <c>null</c> when it declared none it can be parsed into.
    /// </summary>
    /// <remarks>
    /// Passed to the store as a hint and nothing more. The stored type is the decoded one, so a
    /// caller that sends valid PNG bytes labelled <c>text/plain</c> still gets a correctly typed
    /// image, and a caller that sends non-image bytes labelled <c>image/png</c> is still refused.
    /// </remarks>
    private static string? DeclaredContentType(string? header) =>
        header is { Length: > 0 } declared && MediaTypeHeaderValue.TryParse(declared, out var parsed)
            ? parsed.MediaType.Value
            : null;
}
