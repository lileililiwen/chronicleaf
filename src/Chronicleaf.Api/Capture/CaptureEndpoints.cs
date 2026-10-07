using System.Globalization;
using Chronicleaf.Api.Access;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Capture;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Curation;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Operations;
using Chronicleaf.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Chronicleaf.Api.Capture;

/// <summary>
/// The private capture routes (SCREENSHOT-CAPTURE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Two routes, both private: the public allow-list is untouched and both carry
/// <c>RequireOwnerSession()</c>, so <c>/api/health</c> and <c>/api/ready</c> remain the only public
/// surface. A capture is made for one installation and its candidates are readable by the owner they
/// belong to and by nobody else.
/// </para>
/// <para>
/// <b>The request route answers 202, not a candidate.</b> A capture starts a browser, waits for a page
/// and returns PNG bytes, with a 45-second limit attached, so it runs in the delivered queue and this
/// route answers with the operation id plus the delivered operation route to read it on. A client that
/// wanted the picture immediately would have to hold a request open across a browser launch.
/// </para>
/// <para>
/// <b>The read route is read-only and publishes no key.</b> It returns candidate ids, the media ids
/// that address the pictures through the delivered media route, and the provenance the design names -
/// the URL asked for, the URL the page ended at, the viewport and the capture time. It does not
/// select, reorder, promote or delete anything, because this package does not do those things and a
/// read route that could would mean the boundary had been crossed on the way in.
/// </para>
/// <para>
/// <b>The refusals are the four the design fixes</b>, plus the delivered ones. A request naming a
/// destination the policy refuses is a 403 and no browser is started; a malformed URL or an unknown
/// viewport is a 422; a request over the page, viewport or wait bound is a 413; a host that cannot
/// confine a browser at all answers 422 here and 503 from the job, and in both cases no page is
/// visited.
/// </para>
/// </remarks>
public static class CaptureEndpoints
{
    public static IEndpointRouteBuilder MapCaptureEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapPost("/api/milestones/{milestoneId:guid}/captures", RequestCaptureAsync)
            .RequireOwnerSession();

        endpoints
            .MapGet("/api/milestones/{milestoneId:guid}/screenshots", ListScreenshotsAsync)
            .RequireOwnerSession();

        return endpoints;
    }

    private static async Task<IResult> RequestCaptureAsync(
        Guid milestoneId,
        HttpContext context,
        CaptureRequestBody? request,
        IScreenshotCaptureService capture,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);
        var body = request ?? CaptureRequestBody.Empty;

        // The revision the owner was looking at travels twice. A body that disagrees with the header is
        // refused rather than resolved in favour of one of them, because picking one silently is how a
        // capture lands against a bundle somebody else has moved.
        var expected = RequireExpectedRevision(context, body.ExpectedBundleRevision);

        var accepted = await capture
            .RequestCaptureAsync(
                owner,
                milestoneId,
                new ScreenshotCaptureRequest(
                    ParseUrls(body),
                    ParseViewports(body.Viewports),
                    body.WaitSelector,
                    body.WaitMs ?? 0,
                    body.FullPage ?? false,
                    expected,
                    body.IdempotencyKey ?? string.Empty),
                cancellationToken)
            .ConfigureAwait(false);

        var payload = new CaptureAcceptedResponse(
            accepted.Operation.Id.ToString("D"),
            $"/api/operations/{accepted.Operation.Id:D}",
            OperationStatuses.ToWire(accepted.Operation.Status),
            accepted.Created);

        // 202 with the operation's own location: the work has been accepted and durably recorded, and
        // this is where its progress is read. A replay under the same idempotency key answers with the
        // same job and `created: false`.
        return Results.Json(payload, statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> ListScreenshotsAsync(
        Guid milestoneId,
        HttpContext context,
        IScreenshotCurationService curation,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        // The curation read, not the capture one. One collection and one route: the columns this
        // route publishes are moved by screenshot-curation now, so reading them through the
        // capture service's constants would report candidate/false/empty for a picture its owner
        // has already selected. Capture still owns the rows; curation owns what this route says
        // about the choice.
        var candidates = await curation
            .ListAsync(owner, milestoneId, cancellationToken)
            .ConfigureAwait(false);

        // No ETag. The collection is append-only and this route changes nothing, so there is no
        // revision an owner could be stale against - a header here would invite a client to send
        // If-Match to a read that has nothing to conflict with.
        return Results.Json(new ScreenshotCandidateListResponse(
            [.. candidates.Select(ScreenshotCandidateResponse.From)],
            ScreenshotCandidateListResponse.MediaRoute));
    }

    /// <summary>
    /// The pages a capture is asked for (WORKFLOW-WAYFINDING-004).
    /// </summary>
    /// <remarks>
    /// Exactly one of the legacy single <c>url</c> and the additive <c>urls</c> must name a page:
    /// naming both is refused rather than resolved in favour of one, because picking one silently
    /// is how a page the owner asked for never gets photographed. An absent or all-blank
    /// <c>urls</c> falls back to the single-<c>url</c> path untouched, so older callers behave
    /// byte-identically. How many pages is too many stays the service's own decision (413); this
    /// method only decides which field named them.
    /// </remarks>
    private static IReadOnlyList<Uri> ParseUrls(CaptureRequestBody body)
    {
        var named = body.Urls?.Where(url => !string.IsNullOrWhiteSpace(url)).ToList();

        if (named is not { Count: > 0 })
        {
            return [ParseUrl(body.Url)];
        }

        if (!string.IsNullOrWhiteSpace(body.Url))
        {
            throw new ValidationFailedException(
                "urls",
                "Name one page in url or up to three pages in urls, not both.");
        }

        var parsed = new List<Uri>(named.Count);

        for (var index = 0; index < named.Count; index++)
        {
            try
            {
                parsed.Add(ParseUrl(named[index]));
            }
            catch (ValidationFailedException)
            {
                throw new ValidationFailedException(
                    "urls",
                    $"urls[{index}]: name an absolute URL to capture.");
            }
        }

        return parsed;
    }

    /// <summary>
    /// The single URL a capture is asked for.
    /// </summary>
    /// <remarks>
    /// Parsed here and refused here for a value that is not a URI at all, so the service receives a
    /// <see cref="Uri"/> or nothing. A host that is a valid URI but a forbidden destination is a
    /// different refusal and belongs to the policy, which is why this does not judge the scheme: a
    /// <c>file:</c> URL and a <c>https:</c> URL that resolves to a private address are both refused,
    /// and they are refused with different codes so an owner can tell a typo from a block.
    /// </remarks>
    private static Uri ParseUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) ||
            !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var url))
        {
            throw new ValidationFailedException("url", "Name one absolute URL to capture.");
        }

        return url;
    }

    private static IReadOnlyList<CaptureViewport> ParseViewports(IReadOnlyList<string>? names)
    {
        if (names is null || names.Count == 0)
        {
            // Defaulting to desktop rather than refusing: the design fixes it as the default, and an
            // owner who asked for a capture and named no viewport has asked for the ordinary one.
            return [CaptureViewport.Desktop];
        }

        // An unknown name becomes a null element on purpose and is carried to the service rather than
        // dropped here: the service answers 422 naming the field, and a default substituted for it
        // would photograph the desktop viewport for a name the owner never asked for.
        return [.. names.Select(name => CaptureViewport.FromName(name)!)];
    }

    /// <summary>
    /// The revision a capture is based on, from the header and the body together.
    /// </summary>
    /// <remarks>
    /// A missing header is 428, because the delivered routes all require one and a capture that
    /// succeeded against an unnamed revision could not be told what it was for. A body naming a
    /// different revision is 422, because that is a malformed request rather than a missing
    /// precondition.
    /// </remarks>
    private static long RequireExpectedRevision(HttpContext context, long? bodyRevision)
    {
        var header = context.Request.Headers.IfMatch.ToString();

        if (string.IsNullOrWhiteSpace(header))
        {
            throw new IfMatchRequiredException("milestone's evidence");
        }

        var raw = header.Trim().Trim('"');

        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fromHeader))
        {
            throw new ValidationFailedException("If-Match", "An If-Match revision is a whole number.");
        }

        if (bodyRevision is { } fromBody && fromBody != fromHeader)
        {
            throw new ValidationFailedException(
                "expectedBundleRevision",
                "The expected revision in the body and the If-Match header must be the same.");
        }

        if (fromHeader < 0)
        {
            throw new ValidationFailedException("If-Match", "An evidence revision is zero or greater.");
        }

        return fromHeader;
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
