using System.Globalization;
using Chronicleaf.Api.Access;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.GitHub;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Operations;
using Chronicleaf.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Chronicleaf.Api.GitHub;

/// <summary>
/// The private GitHub evidence routes (GITHUB-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Three routes, all private: the public allow-list is untouched and every route carries
/// <c>RequireOwnerSession()</c>, so <c>/api/health</c> and <c>/api/ready</c> remain the only public
/// surface. A repository is readable by the owner it belongs to and by nobody else.
/// </para>
/// <para>
/// <b>No route here accepts or returns a URL.</b> A connection is an owner and a name; the host is a
/// constant of the transport. That is why the connection body has no endpoint field and why the
/// response has no URL: there is nothing in this surface a caller could point somewhere else.
/// </para>
/// <para>
/// <b>The collection route answers 202, not a bundle.</b> A collection is a bounded set of network
/// reads with a rate limit and a timeout attached, so it runs in the delivered queue and this route
/// answers with the operation id plus the delivered operation route to read it on. A client that
/// wanted the evidence immediately would have to hold a request open across GitHub, which is what
/// the queue exists to avoid.
/// </para>
/// <para>
/// The connection and the collection are both conditional on the revision they are based on: the
/// project revision for a connection, the evidence revision for a collection. A body that disagrees
/// with <c>If-Match</c> is refused rather than resolved in favour of one of them.
/// </para>
/// </remarks>
public static class GitHubEvidenceEndpoints
{
    public static IEndpointRouteBuilder MapGitHubEvidenceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapGet("/api/projects/{projectId:guid}/github-connection", ReadConnectionAsync)
            .RequireOwnerSession();

        endpoints
            .MapPost("/api/projects/{projectId:guid}/github-connection", ConnectAsync)
            .RequireOwnerSession();

        endpoints
            .MapPost("/api/milestones/{milestoneId:guid}/github-sync", RequestSyncAsync)
            .RequireOwnerSession();

        return endpoints;
    }

    private static async Task<IResult> ReadConnectionAsync(
        Guid projectId,
        HttpContext context,
        IGitHubEvidenceService evidence,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);
        var connection = await evidence
            .ReadConnectionAsync(owner, projectId, cancellationToken)
            .ConfigureAwait(false);

        if (connection is null)
        {
            // A project with no connection is answered with this package's own code rather than the
            // delivered not-found one: the project is real and reachable, and what is missing is
            // something the owner has to supply. Only a project this owner may not reach is a 404.
            throw new GitHubConnectionMissingException();
        }

        context.Response.Headers.ETag =
            $"\"{connection.UpdatedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture)}\"";

        return Results.Json(GitHubConnectionResponse.From(connection));
    }

    private static async Task<IResult> ConnectAsync(
        Guid projectId,
        HttpContext context,
        GitHubConnectionRequest? request,
        IGitHubEvidenceService evidence,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var connection = await evidence
            .ConnectAsync(
                owner,
                projectId,
                new Application.GitHub.GitHubConnectionRequest(
                    request?.Owner,
                    request?.Repository,
                    ParseCredentialRef(request?.CredentialRef)),
                cancellationToken)
            .ConfigureAwait(false);

        context.Response.Headers.ETag =
            $"\"{GitHubConnectionResponse.Stamp(connection.UpdatedAt)}\"";

        // 201 whether the connection was created or replaced: the project now has exactly one
        // connection and this request is what put it there, so the two are the same outcome from a
        // client's point of view and the body carries the id either way.
        return Results.Json(
            GitHubConnectionResponse.From(connection),
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> RequestSyncAsync(
        Guid milestoneId,
        HttpContext context,
        GitHubSyncWireRequest? request,
        IGitHubEvidenceService evidence,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);
        var body = request ?? GitHubSyncWireRequest.Empty;

        // The revision the owner was looking at travels twice. A body that disagrees with the header
        // is refused rather than resolved in favour of one of them, because picking one silently is
        // how a stale collection gets written onto a bundle somebody else has moved.
        var expected = RequireExpectedRevision(context, body.ExpectedBundleRevision);

        var accepted = await evidence
            .RequestSyncAsync(
                owner,
                milestoneId,
                new GitHubSyncRequest(
                    body.HeadRef,
                    body.BaseRef,
                    body.ReleaseTag,
                    body.IssueNumber,
                    body.PullRequestNumber,
                    expected,
                    body.IdempotencyKey ?? string.Empty),
                cancellationToken)
            .ConfigureAwait(false);

        var operationUrl = $"/api/operations/{accepted.Operation.Id:D}";
        var payload = new GitHubSyncAcceptedResponse(
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
    /// The revision a collection is based on, from the header and the body together. A missing
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

    private static Guid? ParseCredentialRef(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!Guid.TryParse(raw.Trim(), out var parsed))
        {
            // A reference is an identifier, so a value that is not one is a malformed field rather
            // than a missing credential. Nothing about the value is echoed back.
            throw new ValidationFailedException("credentialRef", "A credential reference is an identifier.");
        }

        return parsed;
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
