using System.Text.Json;
using Chronicleaf.Api.Access;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Agency;
using Chronicleaf.Application.Milestones;

namespace Chronicleaf.Api.Agency;

/// <summary>
/// The private agency client routes (AGENCY-CLIENT-PROJECTS-001/002).
/// </summary>
/// <remarks>
/// Every route here is private, and that is the access package's default rather than something
/// these handlers re-check: a matched <c>/api</c> endpoint without the public marker has already
/// been given an authenticated owner session, the trusted origin and the antiforgery value, and
/// anything else was refused before this code ran. The public allow-list is therefore unchanged.
/// <para>
/// Two rules are visible in the routes themselves. Every mutation that changes a stored record
/// takes its revision from <c>If-Match</c>, so a client states which version it is editing rather
/// than overwriting whatever is there now. And a client is never erased: <c>DELETE</c> archives
/// it, which is why it is a mutation with an <c>If-Match</c> and not a route with a body of its
/// own.
/// </para>
/// <para>
/// A project association is grouped through the dedicated <c>PATCH
/// /api/projects/{id}/client</c> route rather than through the project edit: grouping has its own
/// preconditions (a live, same-scope client) and its own invalidation (both clients' context
/// revisions move), and folding it into the project patch would let a partial save regroup a
/// project by accident.
/// </para>
/// </remarks>
public static class AgencyClientEndpoints
{
    public static IEndpointRouteBuilder MapAgencyClientEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/clients", ListClientsAsync).RequireOwnerSession();
        endpoints.MapPost("/api/clients", CreateClientAsync).RequireOwnerSession();
        endpoints.MapGet("/api/clients/{clientId:guid}", GetClientAsync).RequireOwnerSession();
        endpoints.MapPatch("/api/clients/{clientId:guid}", UpdateClientAsync).RequireOwnerSession();
        endpoints.MapDelete("/api/clients/{clientId:guid}", ArchiveClientAsync).RequireOwnerSession();

        endpoints.MapGet("/api/clients/{clientId:guid}/projects", ListClientProjectsAsync).RequireOwnerSession();
        endpoints.MapGet("/api/clients/{clientId:guid}/context", BuildReportContextAsync).RequireOwnerSession();

        endpoints.MapPatch("/api/projects/{projectId:guid}/client", SetProjectClientAsync).RequireOwnerSession();

        return endpoints;
    }

    private static async Task<IResult> ListClientsAsync(
        HttpContext context,
        IAgencyClientService agency,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var page = await agency.ListClientsAsync(
            owner,
            new PageRequest(ReadLimit(context), context.Request.Query["cursor"]),
            cancellationToken).ConfigureAwait(false);

        return Results.Json(ClientListResponse.From(page));
    }

    private static async Task<IResult> CreateClientAsync(
        HttpContext context,
        CreateClientRequest? request,
        IAgencyClientService agency,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var client = await agency.CreateClientAsync(
            owner,
            AgencyClientMapping.ToInput(request ?? new CreateClientRequest(null, null)),
            cancellationToken).ConfigureAwait(false);

        AgencyClientMapping.PublishRevision(context, client.Revision);

        return Results.Created($"/api/clients/{client.Id.ToString("D")}", ClientResponse.From(client));
    }

    private static async Task<IResult> GetClientAsync(
        Guid clientId,
        HttpContext context,
        IAgencyClientService agency,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);
        var client = await agency.GetClientAsync(owner, clientId, cancellationToken).ConfigureAwait(false);

        AgencyClientMapping.PublishRevision(context, client.Revision);

        return Results.Json(ClientResponse.From(client));
    }

    private static async Task<IResult> UpdateClientAsync(
        Guid clientId,
        HttpContext context,
        UpdateClientRequest? request,
        IAgencyClientService agency,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var client = await agency.UpdateClientAsync(
            owner,
            clientId,
            AgencyClientMapping.RequiredRevision(context, "client"),
            AgencyClientMapping.ToPatch(request ?? new UpdateClientRequest(null, null)),
            cancellationToken).ConfigureAwait(false);

        AgencyClientMapping.PublishRevision(context, client.Revision);

        return Results.Json(ClientResponse.From(client));
    }

    private static async Task<IResult> ArchiveClientAsync(
        Guid clientId,
        HttpContext context,
        IAgencyClientService agency,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var client = await agency.ArchiveClientAsync(
            owner,
            clientId,
            AgencyClientMapping.RequiredRevision(context, "client"),
            cancellationToken).ConfigureAwait(false);

        AgencyClientMapping.PublishRevision(context, client.Revision);

        // The client is still there and still readable, and so is every project grouped under it;
        // nothing grouped was moved or cleared by the archive.
        return Results.Json(ClientResponse.From(client));
    }

    private static async Task<IResult> ListClientProjectsAsync(
        Guid clientId,
        HttpContext context,
        IAgencyClientService agency,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var page = await agency.ListClientProjectsAsync(
            owner,
            clientId,
            new PageRequest(ReadLimit(context), context.Request.Query["cursor"]),
            cancellationToken).ConfigureAwait(false);

        return Results.Json(ClientProjectListResponse.From(page));
    }

    private static async Task<IResult> BuildReportContextAsync(
        Guid clientId,
        HttpContext context,
        IAgencyClientService agency,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var projectIds = ReadProjectIds(context);

        var report = await agency.BuildReportContextAsync(owner, clientId, projectIds, cancellationToken).ConfigureAwait(false);

        return Results.Json(ClientReportContextResponse.From(report));
    }

    private static async Task<IResult> SetProjectClientAsync(
        Guid projectId,
        HttpContext context,
        JsonDocument? body,
        IAgencyClientService agency,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var document = body?.RootElement ?? default;

        var revision = context.Request.Headers.IfMatch.Count > 0
            ? AgencyClientMapping.RequiredRevision(context, "project")
            : AgencyClientMapping.ToBodyRevision(document)
                ?? throw new Application.Common.IfMatchRequiredException("project");

        var project = await agency.SetProjectClientAsync(
            owner,
            projectId,
            revision,
            AgencyClientMapping.ToAssignment(document),
            cancellationToken).ConfigureAwait(false);

        AgencyClientMapping.PublishRevision(context, project.Revision);

        return Results.Json(new
        {
            id = project.Id.ToString("D"),
            revision = project.Revision,
            clientId = project.ClientId?.ToString("D"),
        });
    }

    /// <summary>
    /// The scope and the identity this request acts under. Both come from the access package: the
    /// actor is the validated session's, and the scope is the installation that session belongs to.
    /// A request body cannot influence either.
    /// </summary>
    private static async Task<OwnerScope> OwnerScopeAsync(
        HttpContext context,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var session = AccessEndpoints.OwnerSession(context);
        var identity = await access.EnsureOwnerAsync(cancellationToken).ConfigureAwait(false);

        return new OwnerScope(identity.InstallationId, session.ActorId);
    }

    /// <summary>
    /// Reads the explicit project ids a report context is built from. The query may repeat the key;
    /// anything that is not a well-formed id is a 422 naming <c>projectIds</c>, never a silent skip.
    /// </summary>
    private static IReadOnlyList<Guid> ReadProjectIds(HttpContext context)
    {
        var values = context.Request.Query["projectIds"];
        var ids = new List<Guid>(values.Count);

        foreach (var value in values)
        {
            foreach (var part in (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Guid.TryParse(part.Trim(), out var id) || id == Guid.Empty)
                {
                    throw new Application.Common.ValidationFailedException(
                        "projectIds",
                        "every projectIds entry must be a project id.");
                }

                ids.Add(id);
            }
        }

        return ids;
    }

    private static int? ReadLimit(HttpContext context)
    {
        var raw = context.Request.Query["limit"].ToString();

        return int.TryParse(raw, out var limit) ? limit : null;
    }
}
