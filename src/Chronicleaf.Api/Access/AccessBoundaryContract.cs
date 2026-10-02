using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Chronicleaf.Api.Access;

/// <summary>
/// Marks an <c>/api</c> endpoint as reachable without an owner session.
/// </summary>
/// <remarks>
/// The boundary is default-deny: a matched <c>/api</c> endpoint that does not carry this marker is
/// private and requires an authenticated owner session. The design fixes exactly three public API
/// routes - <c>GET /api/health</c>, <c>GET /api/ready</c> and <c>POST /api/session</c> - and this
/// marker is the only way to say so, which is what stops a newly added private route from being
/// public because nobody remembered to protect it.
/// <para>
/// The client's SPA fallback also carries it. That endpoint is the shell's 404 for an unknown
/// <c>/api</c> path and the entry point for a client route: it exists, exposes no stored data and
/// is the runtime shell's shipped contract, so it is not a private route. An unknown path answers
/// 404 rather than 401, which discloses nothing and keeps the shell contract intact.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AllowPublicApiAttribute : Attribute;

/// <summary>Route mapping helpers for the access boundary.</summary>
public static class AccessEndpointExtensions
{
    /// <summary>Declares this <c>/api</c> endpoint public, which is the narrow exception above.</summary>
    public static TBuilder AllowPublicApi<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(new AllowPublicApiAttribute());
    }

    /// <summary>
    /// Declares this <c>/api</c> endpoint private. The boundary is default-deny, so this is
    /// documentation of intent rather than the mechanism that protects the route.
    /// </summary>
    public static TBuilder RequireOwnerSession<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(new RequireOwnerSessionAttribute());
    }
}

/// <summary>
/// Marks an <c>/api</c> endpoint as requiring an authenticated owner session explicitly. It
/// documents the same decision the default-deny rule already enforces, so a reviewer reading one
/// route sees whether it is public or private without following the middleware.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireOwnerSessionAttribute : Attribute;

/// <summary>The exact access boundary this build enforces, exposed for the browser client.</summary>
public static class AccessBoundaryContract
{
    /// <summary>The session cookie's name, shared by the API and the React client.</summary>
    public const string SessionCookieName = "chronicleaf_session";

    /// <summary>
    /// The header a mutation carries its antiforgery value in. It is reported by the owner-only
    /// metadata response, so the client never has to hard-code it.
    /// </summary>
    public const string AntiforgeryHeaderName = "X-Chronicleaf-Antiforgery";
}

/// <summary>The <c>GET /api/session</c> body, pinned so the contract cannot drift.</summary>
public sealed record SessionResponse(
    [property: JsonPropertyName("authenticated")] bool Authenticated,
    [property: JsonPropertyName("actorId")] string ActorId)
{
    public static SessionResponse For(Guid actorId) => new(true, actorId.ToString("D"));
}

/// <summary>
/// The owner-only antiforgery metadata response. It names the header a mutation uses and carries a
/// value scoped to the session that requested it: separate from the login token, and not accepted
/// by any other session.
/// </summary>
public sealed record AntiforgeryResponse(
    [property: JsonPropertyName("headerName")] string HeaderName,
    [property: JsonPropertyName("token")] string Token);

/// <summary>The <c>POST /api/session</c> request body.</summary>
public sealed record SignInRequest([property: JsonPropertyName("accessToken")] string? AccessToken);
