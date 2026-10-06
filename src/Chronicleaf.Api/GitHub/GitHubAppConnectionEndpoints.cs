using Chronicleaf.Api.Access;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.GitHub;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Chronicleaf.Api.GitHub;

/// <summary>
/// The GitHub App connection routes (GITHUB-APP-CONNECTION-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Three private routes and one public one. The private ones require an owner session like every
/// other private route, so <c>/api/health</c> and <c>/api/ready</c> stay the only public surfaces
/// an owner's browser can see. The fourth, <c>POST /api/webhooks/github-app</c>, is GitHub calling
/// in, and it carries its own authentication: the HMAC signature over its raw body.
/// </para>
/// <para>
/// <b>No route here accepts or returns a token.</b> The connect body carries two numbers and a
/// setup state; the server reads everything else itself, through the App, before it stores
/// anything. A token is minted when a collection needs one and held only in memory.
/// </para>
/// </remarks>
public static class GitHubAppConnectionEndpoints
{
    /// <summary>The delivery id header GitHub stamps on every request it sends.</summary>
    public const string DeliveryIdHeader = "X-GitHub-Delivery";

    /// <summary>The event name header GitHub stamps on every request it sends.</summary>
    public const string EventHeader = "X-GitHub-Event";

    /// <summary>The signature header this route authenticates against. SHA-1 is refused, not accepted.</summary>
    public const string SignatureHeader = "X-Hub-Signature-256";

    public static IEndpointRouteBuilder MapGitHubAppConnectionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapGet("/api/projects/{projectId:guid}/github-app/setup", BeginSetupAsync)
            .RequireOwnerSession();

        endpoints
            .MapPost("/api/projects/{projectId:guid}/github-app/connect", ConnectAsync)
            .RequireOwnerSession();

        endpoints
            .MapGet("/api/projects/{projectId:guid}/github-app", ReadConnectionAsync)
            .RequireOwnerSession();

        endpoints
            .MapDelete("/api/projects/{projectId:guid}/github-app", DisconnectAsync)
            .RequireOwnerSession();

        // The one public route in this package, for the same reason the candidate package has
        // one: the caller is GitHub's own delivery, it has no owner session, and the HMAC
        // signature over its own bytes is what authenticates it.
        endpoints
            .MapPost("/api/webhooks/github-app", ReceiveWebhookAsync)
            .AllowPublicApi();

        return endpoints;
    }

    private static async Task<IResult> BeginSetupAsync(
        Guid projectId,
        HttpContext context,
        IGitHubAppService apps,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);
        var setup = await apps.BeginSetupAsync(owner, projectId, cancellationToken).ConfigureAwait(false);

        return Results.Json(new GitHubAppSetupResponse(
            setup.State,
            GitHubAppConnectionResponse.Stamp(setup.ExpiresAt)));
    }

    private static async Task<IResult> ConnectAsync(
        Guid projectId,
        HttpContext context,
        GitHubAppConnectRequest? request,
        IGitHubAppService apps,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        if (request?.InstallationId is null or < 1 || request?.RepositoryId is null or < 1)
        {
            throw new ValidationFailedException(new Dictionary<string, string[]>(StringComparer.Ordinal)
            {
                ["installationId"] = ["An installation id is one or greater."],
                ["repositoryId"] = ["A repository id is one or greater."],
            });
        }

        var connection = await apps
            .ConnectAsync(
                owner,
                projectId,
                new Application.GitHub.GitHubAppConnectRequest(
                    request.InstallationId.Value,
                    request.RepositoryId.Value,
                    request.State),
                cancellationToken)
            .ConfigureAwait(false);

        var installation = await apps
            .ReadInstallationAsync(owner, projectId, cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(
            new GitHubAppConnectionResponse(
                connection.Id.ToString("D"),
                connection.ProjectId.ToString("D"),
                connection.Owner,
                connection.Repo,
                connection.DefaultBranch,
                connection.RepositoryId,
                connection.InstallationId ?? 0,
                installation?.AccountLogin ?? string.Empty,
                GitHubAppConnectionResponse.Stamp(connection.CreatedAt),
                GitHubAppConnectionResponse.Stamp(connection.UpdatedAt)),
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> ReadConnectionAsync(
        Guid projectId,
        HttpContext context,
        IGitHubAppService apps,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);
        var connection = await apps
            .ReadConnectionAsync(owner, projectId, cancellationToken)
            .ConfigureAwait(false);

        if (connection?.InstallationId is null)
        {
            throw new ResourceNotFoundException("github app connection");
        }

        var installation = await apps
            .ReadInstallationAsync(owner, projectId, cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(new GitHubAppConnectionResponse(
            connection.Id.ToString("D"),
            connection.ProjectId.ToString("D"),
            connection.Owner,
            connection.Repo,
            connection.DefaultBranch,
            connection.RepositoryId,
            connection.InstallationId.Value,
            installation?.AccountLogin ?? string.Empty,
            GitHubAppConnectionResponse.Stamp(connection.CreatedAt),
            GitHubAppConnectionResponse.Stamp(connection.UpdatedAt)));
    }

    private static async Task<IResult> DisconnectAsync(
        Guid projectId,
        HttpContext context,
        IGitHubAppService apps,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);
        await apps.DisconnectAsync(owner, projectId, cancellationToken).ConfigureAwait(false);

        return Results.NoContent();
    }

    private static async Task<IResult> ReceiveWebhookAsync(
        HttpContext context,
        IGitHubAppService apps,
        CancellationToken cancellationToken)
    {
        if (context.Request.ContentLength is { } declared &&
            declared > GitHubAppService.WebhookMaxBytes)
        {
            throw new PayloadTooLargeException(GitHubAppService.WebhookMaxBytes);
        }

        var body = await ReadBoundedBodyAsync(context, cancellationToken).ConfigureAwait(false);

        await apps.ReceiveWebhookAsync(
            context.Request.Headers[EventHeader].ToString(),
            context.Request.Headers[SignatureHeader].ToString(),
            body,
            cancellationToken).ConfigureAwait(false);

        // 200, and always 200 for a verified delivery. An unverified one never reaches here: it
        // is a 401 with no receipt and no row.
        return Results.Json(new GitHubAppWebhookReceipt(Received: true));
    }

    private static async Task<byte[]> ReadBoundedBodyAsync(HttpContext context, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();

        var chunk = new byte[8192];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);

            if (buffer.Length > GitHubAppService.WebhookMaxBytes)
            {
                throw new PayloadTooLargeException(GitHubAppService.WebhookMaxBytes);
            }
        }

        return buffer.ToArray();
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
