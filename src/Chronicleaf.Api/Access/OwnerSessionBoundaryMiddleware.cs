using Chronicleaf.Application.Access;

namespace Chronicleaf.Api.Access;

/// <summary>
/// The single-user access boundary (COMMUNITY-ACCESS-001, COMMUNITY-ACCESS-002).
/// </summary>
/// <remarks>
/// The rule is default-deny for <c>/api</c> endpoints: a matched <c>/api</c> route requires an
/// authenticated owner session unless it carries <see cref="AllowPublicApiAttribute"/>, and the
/// design fixes exactly three such routes. Everything else under <c>/api</c> - including routes
/// added by later packages - is private until its owner says otherwise.
/// <para>
/// For a private mutation there are two further requirements, checked in this order and both
/// before the endpoint runs, so a rejected request has not written anything: the request
/// <c>Origin</c> must be the configured public origin, and it must carry the antiforgery value
/// issued for its own session. A same-origin cookie alone is therefore not enough to mutate
/// anything, which is what a cross-site request cannot forge.
/// </para>
/// <para>
/// Failures are raised as the access package's exceptions so the shipped error middleware is the
/// only place an envelope is written. Nothing here logs a token, a cookie value or an antiforgery
/// value: the only detail a log line carries is the path that was refused.
/// </para>
/// </remarks>
public sealed class OwnerSessionBoundaryMiddleware(RequestDelegate next)
{
    /// <summary>
    /// Where the validated session is kept for the endpoint. The value is a validated server-side
    /// session, never anything a client supplied beyond the cookie that was verified.
    /// </summary>
    public const string SessionItemKey = "chronicleaf.owner-session";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var endpoint = context.GetEndpoint();
        if (endpoint is null || !IsApiPath(context.Request.Path))
        {
            // Static assets, client routes and any non-API surface are not private API routes.
            await next(context).ConfigureAwait(false);
            return;
        }

        if (endpoint.Metadata.GetMetadata<AllowPublicApiAttribute>() is not null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var access = context.RequestServices.GetRequiredService<IOwnerAccessService>();
        var settings = context.RequestServices.GetRequiredService<AccessSettings>();

        var session = await access
            .ValidateAsync(PresentedSessionToken(context), context.RequestAborted)
            .ConfigureAwait(false);

        if (session is null)
        {
            // One answer for a missing cookie, an unknown session, a revoked one and an expired
            // one. A caller cannot learn which, and no data is read on the way to the refusal.
            throw new UnauthenticatedException();
        }

        if (IsMutation(context.Request.Method))
        {
            if (!IsTrustedOrigin(settings, context.Request.Headers.Origin.ToString()))
            {
                throw new OriginMismatchException();
            }

            if (!access.VerifyAntiforgery(session, context.Request.Headers[AccessBoundaryContract.AntiforgeryHeaderName]))
            {
                throw new AntiforgeryFailedException();
            }
        }

        context.Items[SessionItemKey] = session;
        await next(context).ConfigureAwait(false);
    }

    /// <summary>The session cookie the browser presented, or <c>null</c> when it presented none.</summary>
    internal static string? PresentedSessionToken(HttpContext context) =>
        context.Request.Cookies.TryGetValue(AccessBoundaryContract.SessionCookieName, out var token)
            ? token
            : null;

    /// <summary>
    /// True when the request's <c>Origin</c> is the configured public origin. A mutation without
    /// an <c>Origin</c>, and a mutation from any other origin, are both refused: the header is
    /// required rather than optional, so a non-browser client has to name the origin explicitly.
    /// </summary>
    internal static bool IsTrustedOrigin(AccessSettings settings, string? origin)
    {
        if (settings.PublicOriginUri is not { } configured || string.IsNullOrEmpty(origin))
        {
            return false;
        }

        return string.Equals(
            origin,
            configured.GetLeftPart(UriPartial.Authority),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsMutation(string method) =>
        !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method);

    private static bool IsApiPath(PathString path) => path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase);
}
