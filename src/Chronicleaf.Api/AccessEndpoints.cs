using System.Data.Common;
using System.Net.Sockets;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Chronicleaf.Api.Access;

/// <summary>
/// The single-user owner session routes (COMMUNITY-ACCESS-001).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>
/// <c>POST /api/session</c> exchanges the operator's setup token for a bounded session. It is the
/// only private-facing route that is public, so it carries no session requirement; the token
/// itself is the whole authorization.
/// </description></item>
/// <item><description>
/// <c>GET /api/session</c> is the owner-only session and antiforgery metadata response: who the
/// owner is and, separately, the antiforgery value a mutation must carry.
/// </description></item>
/// <item><description>
/// <c>DELETE /api/session</c> revokes the session. It is a mutation, so the boundary has already
/// checked the origin and the antiforgery value before it runs, and a rejected request has revoked
/// nothing.
/// </description></item>
/// </list>
/// No route here returns a token, a cookie value or an antiforgery value it did not just create,
/// and none of them accepts a caller-supplied actor id: the actor comes from the validated session
/// or from the installation's stored owner identity, never from the request body.
/// </remarks>
public static class AccessEndpoints
{
    /// <summary>How long a dependency may take before a session operation reports a timeout.</summary>
    private static readonly TimeSpan DependencyBudget = TimeSpan.FromSeconds(5);

    public static IEndpointRouteBuilder MapAccessEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/api/session", SignInAsync).AllowPublicApi();
        endpoints.MapGet("/api/session", GetSessionAsync).RequireOwnerSession();
        endpoints.MapGet("/api/session/antiforgery", GetAntiforgeryAsync).RequireOwnerSession();
        endpoints.MapDelete("/api/session", SignOutAsync).RequireOwnerSession();

        return endpoints;
    }

    /// <summary>
    /// Verifies the presented setup token and issues the session cookie. The response is 204 with
    /// no body: nothing about the token, the session id or the owner travels back in a document
    /// that a proxy or a browser history could keep.
    /// </summary>
    private static async Task<IResult> SignInAsync(
        HttpContext context,
        SignInRequest? request,
        IOwnerAccessService access,
        AccessSettings settings)
    {
        var clientKey = ClientKeyOf(context);

        CreatedOwnerSession session = await GuardAsync(
            () => access.SignInAsync(
                request?.AccessToken,
                clientKey,
                PresentedSessionToken(context),
                context.RequestAborted),
            context.RequestAborted).ConfigureAwait(false);

        context.Response.Cookies.Append(
            AccessBoundaryContract.SessionCookieName,
            session.SessionToken,
            new CookieOptions
            {
                // The cookie is unreadable from script, is never sent by a cross-site request, and
                // travels only over TLS outside an explicit loopback Development run. The value
                // itself is an opaque random token; only its hash is stored.
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = !settings.AllowInsecureSessionCookie,
                IsEssential = true,
                Path = "/",
                Expires = session.ExpiresAt,
            });

        return Results.NoContent();
    }

    /// <summary>The owner-only session response. Anonymous callers never reach it: the boundary answers 401.</summary>
    private static IResult GetSessionAsync(HttpContext context) =>
        Results.Json(SessionResponse.For(OwnerSession(context).ActorId));

    /// <summary>
    /// Issues the antiforgery value for this session. The value is new on every call, so the one a
    /// previous response carried stops working as soon as the owner asks for a new one.
    /// </summary>
    private static async Task<IResult> GetAntiforgeryAsync(
        HttpContext context,
        IOwnerAccessService access)
    {
        var token = await GuardAsync(
            () => access.IssueAntiforgeryAsync(OwnerSession(context), context.RequestAborted),
            context.RequestAborted).ConfigureAwait(false);

        return Results.Json(new AntiforgeryResponse(AccessBoundaryContract.AntiforgeryHeaderName, token));
    }

    /// <summary>
    /// Revokes the session and deletes its cookie. The deletion repeats the cookie's own flags, so
    /// the browser removes it even though it no longer matches a stored session.
    /// </summary>
    private static async Task<IResult> SignOutAsync(
        HttpContext context,
        IOwnerAccessService access,
        AccessSettings settings)
    {
        await GuardAsync(
            () => access.RevokeAsync(PresentedSessionToken(context), context.RequestAborted),
            context.RequestAborted).ConfigureAwait(false);

        context.Response.Cookies.Delete(
            AccessBoundaryContract.SessionCookieName,
            new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = !settings.AllowInsecureSessionCookie,
                IsEssential = true,
                Path = "/",
            });

        return Results.NoContent();
    }

    /// <summary>
    /// The session the boundary validated. The boundary has already refused anything that had
    /// none, so this never has a fallback.
    /// </summary>
    internal static OwnerSessionContext OwnerSession(HttpContext context) =>
        context.Items[OwnerSessionBoundaryMiddleware.SessionItemKey] as OwnerSessionContext
        ?? throw new UnauthenticatedException();

    /// <summary>
    /// The client a failed login is counted against. Only the transport address is used, and only
    /// as a key the service hashes before it stores anything.
    /// </summary>
    internal static string ClientKeyOf(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown-client";

    private static string? PresentedSessionToken(HttpContext context) =>
        OwnerSessionBoundaryMiddleware.PresentedSessionToken(context);

    /// <summary>
    /// Turns a real database failure into the repository's own dependency envelope instead of an
    /// opaque 500. A refused connection, an unresolvable host, a schema that has not been migrated
    /// and a statement that times out are all dependency failures, so readiness stays the precise
    /// channel and the caller is told the truth. Anything else keeps its own exception and is
    /// reported as an internal error.
    /// </summary>
    private static async Task<T> GuardAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception failure) when (IsDependencyFailure(failure))
        {
            throw failure is TimeoutException
                ? new DependencyTimeoutException("PostgreSQL", DependencyBudget)
                : new DependencyUnavailableException("PostgreSQL");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    private static async Task GuardAsync(Func<Task> action, CancellationToken cancellationToken)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception failure) when (IsDependencyFailure(failure))
        {
            throw failure is TimeoutException
                ? new DependencyTimeoutException("PostgreSQL", DependencyBudget)
                : new DependencyUnavailableException("PostgreSQL");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
    }

    /// <summary>
    /// True when the innermost cause is a connection-level failure. Providers and EF wrap the same
    /// failure differently depending on where it happened - a refused connection can surface as a
    /// raw <see cref="SocketException"/> and a missing table as a provider error - so the
    /// classification always looks at the root cause, exactly as the readiness probe does.
    /// </summary>
    private static bool IsDependencyFailure(Exception exception)
    {
        var root = exception;
        while (root.InnerException is not null)
        {
            root = root.InnerException;
        }

        return root is DbException
            or DbUpdateException
            or SocketException
            or TimeoutException
            or IOException;
    }
}
