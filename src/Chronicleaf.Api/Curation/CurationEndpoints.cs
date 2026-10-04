using System.Globalization;
using Chronicleaf.Api.Access;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Curation;
using Chronicleaf.Application.Milestones;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Chronicleaf.Api.Curation;

/// <summary>
/// The private curation routes (SCREENSHOT-CURATION-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Two routes, both private: the public allow-list is untouched and both carry
/// <c>RequireOwnerSession()</c>, so <c>/api/health</c> and <c>/api/ready</c> remain the only public
/// surface. A curation is made for one installation and its pictures are readable by the owner they
/// belong to and by nobody else.
/// </para>
/// <para>
/// <b>The PATCH is conditional twice.</b> If-Match carries the screenshot revision the owner read
/// and the body carries the evidence revision, and both must be current: the candidate row and the
/// evidence bundle are written in one transaction, so a change conditional on only one of them
/// could publish a selection against evidence its owner never saw. A missing If-Match is 428, a
/// stale revision of either kind is 409, and a body that disagrees with the header is 422.
/// </para>
/// <para>
/// <b>The order POST names the whole selected set.</b> It must contain exactly the currently
/// selected pictures, once each: an order that drops one would silently unselect it, and one that
/// adds an unselected picture would silently select it, so both are 422 rather than resolved in
/// favour of the stored set.
/// </para>
/// </remarks>
public static class CurationEndpoints
{
    public static IEndpointRouteBuilder MapCurationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapPatch("/api/screenshots/{screenshotId:guid}", CurateScreenshotAsync)
            .RequireOwnerSession();

        endpoints
            .MapPost("/api/milestones/{milestoneId:guid}/screenshots/order", OrderScreenshotsAsync)
            .RequireOwnerSession();

        return endpoints;
    }

    private static async Task<IResult> CurateScreenshotAsync(
        Guid screenshotId,
        HttpContext context,
        CurateScreenshotBody? request,
        IScreenshotCurationService curation,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);
        var expectedRevision = RequiredScreenshotRevision(context, request?.ExpectedRevision);

        var outcome = await curation
            .CurateAsync(owner, screenshotId, CurationMapping.ToCurate(request, expectedRevision), cancellationToken)
            .ConfigureAwait(false);

        context.Response.Headers.ETag = $"\"{outcome.Screenshot.Revision.ToString(CultureInfo.InvariantCulture)}\"";

        return Results.Json(CuratedScreenshotResponse.From(outcome.Screenshot, outcome.BundleRevision));
    }

    private static async Task<IResult> OrderScreenshotsAsync(
        Guid milestoneId,
        HttpContext context,
        OrderScreenshotsBody? request,
        IScreenshotCurationService curation,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var outcome = await curation
            .OrderAsync(owner, milestoneId, CurationMapping.ToOrder(milestoneId, request), cancellationToken)
            .ConfigureAwait(false);

        context.Response.Headers.ETag = $"\"{outcome.BundleRevision.ToString(CultureInfo.InvariantCulture)}\"";

        return Results.Json(new ScreenshotOrderResponse(
            milestoneId.ToString("D"),
            [.. outcome.Ordered.Select(picture => picture.Id.ToString("D"))],
            outcome.BundleRevision));
    }

    /// <summary>
    /// Reads the screenshot revision a curation is conditional on. The header is the precondition,
    /// and the body's <c>expectedRevision</c> has to agree with it - the same rule the delivered
    /// evidence routes enforce, so two answers to one question never reach the service.
    /// </summary>
    private static long RequiredScreenshotRevision(HttpContext context, long? bodyRevision)
    {
        var presented = context.Request.Headers.IfMatch.ToString().Trim();

        if (presented.Length == 0)
        {
            throw new IfMatchRequiredException("screenshot");
        }

        if (presented.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            presented = presented[2..];
        }

        presented = presented.Trim().Trim('"');

        if (!long.TryParse(presented, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 0)
        {
            throw new ValidationFailedException(
                "If-Match",
                "If-Match must be the screenshot revision you read, as a decimal number, optionally quoted or weakly marked.");
        }

        if (bodyRevision is not null && bodyRevision.Value != revision)
        {
            throw new ValidationFailedException(
                "expectedRevision",
                "The expected revision must be the one presented in If-Match.");
        }

        return revision;
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
