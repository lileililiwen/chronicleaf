using System.Globalization;
using Chronicleaf.Api.Access;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.GitHubCandidates;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Core;
// The application contracts are reached through an explicit alias because two of them share a name
// with the wire record this package publishes for the same thing, and a same-named pair resolved by
// namespace rather than by hand is how a request body quietly stops being what the service accepts.
using Application = Chronicleaf.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Chronicleaf.Api.Candidates;

/// <summary>
/// The GitHub candidate routes (GITHUB-RELEASE-CANDIDATES-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>Five private routes and one public one, and the public one is a different kind of thing.</b> The
/// five require an owner session like every other private route, so <c>/api/health</c> and
/// <c>/api/ready</c> stay the only public surfaces an owner's browser can see. The sixth,
/// <c>POST /api/webhooks/github/{connectionId}</c>, is GitHub calling in, and it carries its own
/// authentication: the HMAC signature over its raw body. That is the design's rule and the reason it
/// is a public route at all - a server-to-server caller has no session and cannot be made to have one -
/// and it is why the route is useless to anybody who cannot produce that signature.
/// </para>
/// <para>
/// <b>A connection id in a URL is an address, never an authentication.</b> Every way of failing to
/// authenticate a delivery is the same 401, so a caller cannot learn which connection ids exist, and a
/// guessed id is refused exactly as a wrong signature is. The signature is checked over the bytes the
/// server read, before the body is parsed and before any row exists.
/// </para>
/// <para>
/// <b>Nothing here confirms anything.</b> There is no approve verb, no milestone write and no evidence
/// append anywhere in this file. The most a route can do is propose, and the most a response can carry
/// is a candidate and the receipt that produced it.
/// </para>
/// </remarks>
public static class GitHubCandidateEndpoints
{
    /// <summary>The delivery id header GitHub stamps on every request it sends.</summary>
    public const string DeliveryIdHeader = "X-GitHub-Delivery";

    /// <summary>The event name header GitHub stamps on every request it sends.</summary>
    public const string EventHeader = "X-GitHub-Event";

    /// <summary>The signature header this route authenticates against. SHA-1 is refused, not accepted.</summary>
    public const string SignatureHeader = "X-Hub-Signature-256";

    public static IEndpointRouteBuilder MapGitHubCandidateEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapGet("/api/projects/{projectId:guid}/github-candidates", ListCandidatesAsync)
            .RequireOwnerSession();

        endpoints
            .MapPost("/api/projects/{projectId:guid}/github-candidates/sync", RequestSyncAsync)
            .RequireOwnerSession();

        endpoints
            .MapGet("/api/projects/{projectId:guid}/github-webhook", ReadSubscriptionAsync)
            .RequireOwnerSession();

        endpoints
            .MapPut("/api/projects/{projectId:guid}/github-webhook", SubscribeAsync)
            .RequireOwnerSession();

        endpoints
            .MapDelete("/api/projects/{projectId:guid}/github-webhook", UnsubscribeAsync)
            .RequireOwnerSession();

        // The one public route in this package, and the reason is a protocol rather than a policy: the
        // caller is GitHub's own delivery, it has no owner session, and the HMAC signature over its own
        // bytes is what authenticates it. The marker is placed deliberately and visibly here so a
        // reader can see the one route in this repository that a browser session does not protect.
        endpoints
            .MapPost("/api/webhooks/github/{connectionId:guid}", ReceiveDeliveryAsync)
            .AllowPublicApi();

        return endpoints;
    }

    private static async Task<IResult> ListCandidatesAsync(
        Guid projectId,
        int? limit,
        HttpContext context,
        IGitHubCandidateService candidates,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        // The stored count and the bounded page are read together so a panel can tell an empty project
        // from a project with more proposals than one page holds.
        var items = await candidates
            .ListCandidatesAsync(owner, projectId, limit, cancellationToken)
            .ConfigureAwait(false);

        var count = await candidates
            .CountCandidatesAsync(owner, projectId, cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(GitHubCandidateListResponse.From(items, count));
    }

    private static async Task<IResult> RequestSyncAsync(
        Guid projectId,
        HttpContext context,
        GitHubCandidateSyncRequest? request,
        IGitHubCandidateService candidates,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);
        var body = request ?? GitHubCandidateSyncRequest.Empty;

        // Refused rather than defaulted. A window that silently became "everything one bounded page
        // holds" after an owner asked for one afternoon would look like a correct answer to a question
        // nobody asked.
        if (!GitHubCandidatePolicy.TryParseSince(body.Since, out var since, out var sinceError))
        {
            throw new ValidationFailedException("since", sinceError!);
        }

        var expected = RequireExpectedProjectRevision(context);

        var accepted = await candidates
            .RequestSyncAsync(
                owner,
                projectId,
                new Application.GitHubCandidates.GitHubCandidateSyncRequest(since, body.IdempotencyKey ?? string.Empty),
                expected,
                cancellationToken)
            .ConfigureAwait(false);

        // 202 with the operation's own location, for the same reason the delivered collection route
        // answers 202: the work is durable and the owner watches it on the delivered operation route.
        return Results.Json(
            new GitHubCandidateSyncAcceptedResponse(
                accepted.Operation.Id.ToString("D"),
                $"/api/operations/{accepted.Operation.Id:D}",
                OperationStatuses.ToWire(accepted.Operation.Status),
                accepted.Created),
            statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> ReadSubscriptionAsync(
        Guid projectId,
        HttpContext context,
        IGitHubCandidateService candidates,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);
        var stored = await candidates
            .ReadSubscriptionAsync(owner, projectId, cancellationToken)
            .ConfigureAwait(false);

        if (stored is null)
        {
            throw new ResourceNotFoundException("github webhook subscription");
        }

        context.Response.Headers.ETag = $"\"{GitHubCandidateResponse.Stamp(stored.UpdatedAt)}\"";

        return Results.Json(GitHubWebhookSubscriptionResponse.From(stored));
    }

    private static async Task<IResult> SubscribeAsync(
        Guid projectId,
        HttpContext context,
        GitHubWebhookSubscribeRequest? request,
        IGitHubCandidateService candidates,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);
        var expected = RequireExpectedProjectRevision(context);

        var saved = await candidates
            .SubscribeAsync(
                owner,
                projectId,
                new Application.GitHubCandidates.GitHubWebhookSubscribeRequest(
                    ParseCredentialRef(request?.CredentialRef)),
                expected,
                cancellationToken)
            .ConfigureAwait(false);

        context.Response.Headers.ETag = $"\"{GitHubCandidateResponse.Stamp(saved.UpdatedAt)}\"";

        // 200 rather than 201: this is a PUT onto the project's single subscription, so subscribing,
        // replacing the secret and reactivating an archived row are the same outcome from a client's
        // point of view, and the body carries the connection id either way.
        return Results.Json(GitHubWebhookSubscriptionResponse.From(saved));
    }

    private static async Task<IResult> UnsubscribeAsync(
        Guid projectId,
        HttpContext context,
        IGitHubCandidateService candidates,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);
        var expected = RequireExpectedProjectRevision(context);

        var stored = await candidates
            .UnsubscribeAsync(owner, projectId, expected, cancellationToken)
            .ConfigureAwait(false);

        if (stored is null)
        {
            // Nothing was subscribed, which is the state the owner asked for. A 404 here would send a
            // client looking for a subscription that is exactly what is absent.
            return Results.NoContent();
        }

        context.Response.Headers.ETag = $"\"{GitHubCandidateResponse.Stamp(stored.UpdatedAt)}\"";

        return Results.Json(GitHubWebhookSubscriptionResponse.From(stored));
    }

    private static async Task<IResult> ReceiveDeliveryAsync(
        Guid connectionId,
        HttpContext context,
        IGitHubCandidateService candidates,
        CancellationToken cancellationToken)
    {
        // The declared length is refused before a byte is read, so an oversized delivery costs the
        // sender a 413 and this server nothing. A body with no declared length is still bounded below,
        // because Content-Length is a claim the sender makes and this one is not obliged to believe.
        if (context.Request.ContentLength is { } declared &&
            declared > GitHubCandidatePolicy.DeliveryMaxBytes)
        {
            throw new GitHubCandidateDeliveryTooLargeException(GitHubCandidatePolicy.DeliveryMaxBytes);
        }

        var body = await ReadBoundedBodyAsync(context, cancellationToken).ConfigureAwait(false);

        var receipt = await candidates
            .ReceiveDeliveryAsync(
                connectionId,
                context.Request.Headers[EventHeader].ToString(),
                context.Request.Headers[DeliveryIdHeader].ToString(),
                context.Request.Headers[SignatureHeader].ToString(),
                body,
                cancellationToken)
            .ConfigureAwait(false);

        // 202, and always 202. A verified delivery is durably recorded whether or not this build routed
        // it, so a sender that is told "ignored" as a failure would retry an event that will never
        // produce anything - the receipt says which it was, and the status code does not change.
        return Results.Json(
            GitHubWebhookReceiptResponse.From(receipt),
            statusCode: StatusCodes.Status202Accepted);
    }

    /// <summary>
    /// Reads at most one mebibyte of raw body, and refuses rather than truncating.
    /// </summary>
    /// <remarks>
    /// The signature is computed by GitHub over the exact bytes it sent, so this buffer is the only
    /// thing that may be authenticated: re-encoding, reordering a member or trimming whitespace would
    /// produce a different digest and turn a correct delivery into a 401. Reading one byte past the
    /// ceiling and refusing is what makes the limit real for a sender that did not declare a length -
    /// a truncated body would parse as something else and be recorded as a fact about a document
    /// nobody sent.
    /// </remarks>
    private static async Task<ReadOnlyMemory<byte>> ReadBoundedBodyAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var limit = GitHubCandidatePolicy.DeliveryMaxBytes;
        var buffer = new byte[limit + 1];

        var read = 0;
        while (read < buffer.Length)
        {
            var taken = await context.Request.Body
                .ReadAsync(buffer.AsMemory(read, buffer.Length - read), cancellationToken)
                .ConfigureAwait(false);

            if (taken == 0)
            {
                break;
            }

            read += taken;
        }

        if (read > limit)
        {
            throw new GitHubCandidateDeliveryTooLargeException(limit);
        }

        return read == buffer.Length ? buffer : buffer[..read];
    }

    /// <summary>
    /// The project revision a mutation is conditional on, from the header alone.
    /// </summary>
    /// <remarks>
    /// A missing header is 428, as every other conditional route in this repository answers, and a
    /// header that is not a whole number or is negative is a 422 naming <c>If-Match</c>. The revision
    /// is not repeated in the body: it is a precondition on the project rather than a value the client
    /// chooses, and two copies of it would be two places the two could disagree.
    /// </remarks>
    private static long RequireExpectedProjectRevision(HttpContext context)
    {
        var header = context.Request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            throw new IfMatchRequiredException("project's GitHub candidate subscription");
        }

        var raw = header.Trim().Trim('"');
        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new ValidationFailedException("If-Match", "An If-Match revision is a whole number.");
        }

        if (parsed < 0)
        {
            throw new ValidationFailedException("If-Match", "A project revision is zero or greater.");
        }

        return parsed;
    }

    private static Guid? ParseCredentialRef(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!Guid.TryParse(raw.Trim(), out var parsed))
        {
            // A reference is an identifier, so a value that is not one is a malformed field rather than
            // a missing credential. Nothing about the value is echoed back.
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
