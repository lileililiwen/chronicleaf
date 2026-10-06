using System.Text.Json.Serialization;
using Chronicleaf.Api.Access;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Tenancy;

namespace Chronicleaf.Api.Tenancy;

/// <summary>
/// The Hosted tenant-boundary routes (CLOUD-TENANT-BOUNDARY-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The two login routes are the only public ones here, and they authenticate nobody: starting
/// a login stores a challenge and answers where to go, and the callback exchanges an
/// authorization code the issuer minted. Everything else is private by the access package's
/// default, and in Hosted mode the decorator behind <see cref="IOwnerAccessService"/> has
/// already refused anything without a usable Hosted session before these handlers run.
/// </para>
/// <para>
/// No route here accepts a scope, a tenant id from anywhere but the path or a vetted body, or
/// an actor id: the user comes from the validated session and the scope from that session's
/// active tenant, both resolved server-side.
/// </para>
/// </remarks>
public static class TenancyEndpoints
{
    public static IEndpointRouteBuilder MapTenancyEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/api/hosted/login/start", StartLoginAsync).AllowPublicApi();
        endpoints.MapPost("/api/hosted/login/callback", CompleteLoginAsync).AllowPublicApi();

        endpoints.MapGet("/api/hosted/tenants", ListTenantsAsync).RequireOwnerSession();
        endpoints.MapPost("/api/hosted/tenants", CreateTenantAsync).RequireOwnerSession();
        endpoints.MapPost("/api/hosted/tenants/select", SelectTenantAsync).RequireOwnerSession();
        endpoints.MapDelete(
            "/api/hosted/tenants/{tenantId:guid}/members/{userId:guid}",
            RevokeMembershipAsync).RequireOwnerSession();
        endpoints.MapPost("/api/hosted/import", RecordImportAsync).RequireOwnerSession();

        return endpoints;
    }

    /// <summary>
    /// Begins a verified login. Public: it authenticates nobody and stores only hashes, so the
    /// response carries the authorization URL and the state to bring back, and nothing secret.
    /// </summary>
    private static async Task<IResult> StartLoginAsync(
        ITenancyService tenancy,
        CancellationToken cancellationToken)
    {
        var started = await tenancy.StartLoginAsync(cancellationToken).ConfigureAwait(false);

        return Results.Json(new LoginStartResponse(started.AuthorizationUrl, started.State));
    }

    /// <summary>
    /// Completes a login: validates the state, exchanges the code, validates the ID token and
    /// issues the local session cookie. The response is 204 with no body, like the owner
    /// sign-in: nothing about the code, the state or the session travels in a document.
    /// </summary>
    private static async Task<IResult> CompleteLoginAsync(
        HttpContext context,
        LoginCallbackRequest? request,
        ITenancyService tenancy,
        AccessSettings settings,
        CancellationToken cancellationToken)
    {
        var created = await tenancy.CompleteLoginAsync(
            request?.Code,
            request?.State,
            cancellationToken).ConfigureAwait(false);

        context.Response.Cookies.Append(
            AccessBoundaryContract.SessionCookieName,
            created.SessionToken,
            new CookieOptions
            {
                // The same flags as the owner session: unreadable from script, never sent by a
                // cross-site request, and over TLS outside an explicit loopback Development run.
                // The value itself is an opaque random token; only its hash is stored.
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = !settings.AllowInsecureSessionCookie,
                IsEssential = true,
                Path = "/",
                Expires = created.ExpiresAt,
            });

        return Results.NoContent();
    }

    /// <summary>Lists the caller's live tenants. Needs no active tenant to answer.</summary>
    private static async Task<IResult> ListTenantsAsync(
        HttpContext context,
        ITenancyService tenancy,
        CancellationToken cancellationToken)
    {
        var memberships = await tenancy.ListTenantsAsync(
            HostedSession(context),
            cancellationToken).ConfigureAwait(false);

        return Results.Json(new TenantListResponse(
            memberships.Select(membership => new TenantMembershipResponse(
                membership.TenantId,
                membership.Name,
                membership.Role,
                membership.ScopeId)).ToArray()));
    }

    /// <summary>
    /// Creates a tenant the caller owns and selects it onto the session, so the response's
    /// tenant is immediately usable.
    /// </summary>
    private static async Task<IResult> CreateTenantAsync(
        HttpContext context,
        CreateTenantRequest? request,
        ITenancyService tenancy,
        CancellationToken cancellationToken)
    {
        var tenant = await tenancy.CreateTenantAsync(
            HostedSession(context),
            request?.Name,
            cancellationToken).ConfigureAwait(false);

        return Results.Created(
            $"/api/hosted/tenants/{tenant.Id}",
            new TenantResponse(tenant.Id, tenant.Name, tenant.Revision, tenant.Role));
    }

    /// <summary>Selects one of the caller's live tenants onto the session.</summary>
    private static async Task<IResult> SelectTenantAsync(
        HttpContext context,
        SelectTenantRequest? request,
        ITenancyService tenancy,
        CancellationToken cancellationToken)
    {
        var tenant = await tenancy.SelectTenantAsync(
            HostedSession(context),
            TenancyMapping.TenantId(request?.TenantId),
            cancellationToken).ConfigureAwait(false);

        return Results.Json(new TenantResponse(tenant.Id, tenant.Name, tenant.Revision, tenant.Role));
    }

    /// <summary>
    /// Revokes one user's membership in a tenant the caller owns, and the sessions that acted
    /// under it.
    /// </summary>
    private static async Task<IResult> RevokeMembershipAsync(
        Guid tenantId,
        Guid userId,
        HttpContext context,
        ITenancyService tenancy,
        CancellationToken cancellationToken)
    {
        await tenancy.RevokeMembershipAsync(
            HostedSession(context),
            tenantId,
            userId,
            cancellationToken).ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <summary>
    /// Records the explicit acknowledgment of a Community volume. It moves no row.
    /// </summary>
    private static async Task<IResult> RecordImportAsync(
        HttpContext context,
        RecordImportRequest? request,
        ITenancyService tenancy,
        CancellationToken cancellationToken)
    {
        await tenancy.RecordImportAsync(
            HostedSession(context),
            TenancyMapping.TenantId(request?.TenantId),
            cancellationToken).ConfigureAwait(false);

        return Results.NoContent();
    }

    /// <summary>
    /// The session the boundary validated. The boundary has already refused anything that had
    /// none, so this never has a fallback - in Hosted it is a Hosted session, in Community the
    /// owner one, and the service tells them apart by stored row rather than by claim.
    /// </summary>
    private static OwnerSessionContext HostedSession(HttpContext context) =>
        context.Items[OwnerSessionBoundaryMiddleware.SessionItemKey] as OwnerSessionContext
        ?? throw new UnauthenticatedException();
}

/// <summary>The <c>POST /api/hosted/login/start</c> body: where to go and what to bring back.</summary>
public sealed record LoginStartResponse(
    [property: JsonPropertyName("authorizationUrl")] string AuthorizationUrl,
    [property: JsonPropertyName("state")] string State);

/// <summary>The <c>POST /api/hosted/login/callback</c> body: the issuer's answer.</summary>
public sealed record LoginCallbackRequest(
    [property: JsonPropertyName("code")] string? Code,
    [property: JsonPropertyName("state")] string? State);

/// <summary>The <c>POST /api/hosted/tenants</c> body: the tenant's name.</summary>
public sealed record CreateTenantRequest(
    [property: JsonPropertyName("name")] string? Name);

/// <summary>The <c>POST /api/hosted/tenants/select</c> and <c>POST /api/hosted/import</c> bodies.</summary>
public sealed record SelectTenantRequest(
    [property: JsonPropertyName("tenantId")] string? TenantId);

/// <summary>The <c>POST /api/hosted/import</c> body.</summary>
public sealed record RecordImportRequest(
    [property: JsonPropertyName("tenantId")] string? TenantId);

/// <summary>One tenant as served: ids, name, role and revision, never a secret.</summary>
public sealed record TenantResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("role")] string Role);

/// <summary>One membership as served: the tenant, the caller's role and the scope id.</summary>
public sealed record TenantMembershipResponse(
    [property: JsonPropertyName("tenantId")] string TenantId,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("scopeId")] string ScopeId);

/// <summary>The <c>GET /api/hosted/tenants</c> body.</summary>
public sealed record TenantListResponse(
    [property: JsonPropertyName("tenants")] TenantMembershipResponse[] Tenants);

/// <summary>Request mapping for the tenancy routes: parsing without ever inventing an id.</summary>
internal static class TenancyMapping
{
    /// <summary>
    /// Parses a tenant id from the body. A missing or malformed value is a validation failure
    /// naming its field, never a lookup for whatever the caller almost meant.
    /// </summary>
    internal static Guid TenantId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Guid.TryParse(value.Trim(), out var parsed))
        {
            throw new Application.Common.ValidationFailedException(
                "tenantId",
                "The tenant id must be a valid identifier.");
        }

        return parsed;
    }
}
