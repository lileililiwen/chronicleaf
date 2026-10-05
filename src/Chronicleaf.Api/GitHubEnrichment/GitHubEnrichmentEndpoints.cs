using System.Globalization;
using Chronicleaf.Api.Access;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.GitHubEnrichment;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Operations;
using Chronicleaf.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Chronicleaf.Api.GitHubEnrichment;

/// <summary>
/// The private GitHub relationship-enrichment route (GITHUB-GRAPHQL-ENRICHMENT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// One route, private: the public allow-list is untouched and it carries
/// <c>RequireOwnerSession()</c>, so <c>/api/health</c> and <c>/api/ready</c> remain the only public
/// surface. An enrichment is readable by the owner it belongs to and by nobody else.
/// </para>
/// <para>
/// <b>No route here accepts or returns a URL, a ref or a SHA.</b> The SHAs an enrichment queries
/// are the ones the milestone's own bundle already carries; the body carries only the evidence
/// revision the owner was looking at and the caller's idempotency key. That is why the body has
/// no repository field and why the response has no URL: there is nothing in this surface a caller
/// could point somewhere else.
/// </para>
/// <para>
/// <b>The route answers 202, not a bundle.</b> An enrichment is a bounded set of network reads
/// with a rate limit and a timeout attached, so it runs in the delivered queue and this route
/// answers with the operation id plus the delivered operation route to read it on.
/// </para>
/// </remarks>
public static class GitHubEnrichmentEndpoints
{
    public static IEndpointRouteBuilder MapGitHubEnrichmentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapPost("/api/milestones/{milestoneId:guid}/github-enrichment", RequestEnrichmentAsync)
            .RequireOwnerSession();

        return endpoints;
    }

    private static async Task<IResult> RequestEnrichmentAsync(
        Guid milestoneId,
        HttpContext context,
        GitHubEnrichmentWireRequest? request,
        IGitHubEnrichmentService enrichment,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);
        var body = request ?? GitHubEnrichmentWireRequest.Empty;

        // The revision the owner was looking at travels twice. A body that disagrees with the header
        // is refused rather than resolved in favour of one of them, because picking one silently is
        // how a stale enrichment gets written onto a bundle somebody else has moved.
        var expected = RequireExpectedRevision(context, body.ExpectedBundleRevision);

        var accepted = await enrichment
            .RequestEnrichmentAsync(
                owner,
                milestoneId,
                new GitHubEnrichmentRequest(expected, body.IdempotencyKey ?? string.Empty),
                cancellationToken)
            .ConfigureAwait(false);

        var operationUrl = $"/api/operations/{accepted.Operation.Id:D}";
        var payload = new GitHubEnrichmentAcceptedResponse(
            accepted.Operation.Id.ToString("D"),
            operationUrl,
            OperationStatuses.ToWire(accepted.Operation.Status),
            accepted.Created);

        // 202 with the operation's own location: the work has been accepted and durably recorded,
        // and this is where its progress is read. A replay under the same idempotency key answers
        // with the same job and `created: false`, which is a different answer to a different question
        // and the same answer to this one.
        return Results.Json(payload, statusCode: StatusCodes.Status202Accepted);
    }

    /// <summary>
    /// The revision an enrichment is based on, from the header and the body together. A missing
    /// header is 428, because the delivered routes all require one; a body that names a different
    /// revision is 422, because that is a malformed request rather than a missing precondition.
    /// </summary>
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
