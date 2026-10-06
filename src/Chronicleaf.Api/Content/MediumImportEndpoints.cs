using Chronicleaf.Api.Access;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Medium;
using Chronicleaf.Application.Milestones;

namespace Chronicleaf.Api.Content;

/// <summary>
/// The private Medium publish refusal route (MEDIUM-IMPORT-PACKAGE-001).
/// </summary>
/// <remarks>
/// <para>
/// One route, private: the public allow-list is untouched and the route carries
/// <c>RequireOwnerSession()</c>, so <c>/api/health</c> and <c>/api/ready</c> remain the only public
/// surface. The export shape lives on the review package's export route as
/// <c>format=medium</c>; this route exists so that a native publish request gets the honest
/// 422 answer instead of a 404 that would send it looking for another route.
/// </para>
/// <para>
/// <b>Publication is a refusal, not a transport.</b> The service resolves the asset first so a
/// foreign id is still the delivered 404, then answers 422 with no account action: no
/// credential is leased, no job is enqueued, no audit row is written and no socket is opened.
/// </para>
/// </remarks>
public static class MediumImportEndpoints
{
    public static IEndpointRouteBuilder MapMediumImportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapPost("/api/assets/{assetId:guid}/adaptations/medium/publish", PublishAsync)
            .RequireOwnerSession();

        return endpoints;
    }

    private static async Task<IResult> PublishAsync(
        Guid assetId,
        HttpContext context,
        IMediumImportService imports,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        // Always refuses with publisher_unsupported: the service resolves the asset first so a
        // foreign id is still the delivered 404, then answers 422 with no account action.
        await imports.PublishAsync(owner, assetId, cancellationToken).ConfigureAwait(false);

        // Unreachable: publication is a refusal, so success has no representation.
        throw new InvalidOperationException("Medium publication cannot succeed.");
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
