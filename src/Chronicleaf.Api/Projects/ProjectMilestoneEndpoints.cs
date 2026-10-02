using Chronicleaf.Api.Access;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Core;

namespace Chronicleaf.Api.Projects;

/// <summary>
/// The private Project and Milestone routes (PROJECTS-AND-MILESTONES-001/002).
/// </summary>
/// <remarks>
/// Every route here is private, and that is the access package's default rather than something these
/// handlers re-check: a matched <c>/api</c> endpoint without the public marker has already been
/// given an authenticated owner session, the trusted origin and the antiforgery value, and anything
/// else was refused before this code ran. The public allow-list is therefore unchanged, and a
/// handler that did not exist would still be private.
/// <para>
/// Two rules are visible in the routes themselves. Every mutation that changes a stored record takes
/// its revision from <c>If-Match</c>, so a client states which version it is editing rather than
/// overwriting whatever is there now. And no request shape carries a status, so confirmation and
/// dismissal are the only two actions that can move a milestone, and only the owner may call them.
/// </para>
/// <para>
/// A project is never erased: <c>DELETE</c> archives it, which is why it is a mutation with an
/// <c>If-Match</c> and not a route with a body of its own.
/// </para>
/// </remarks>
public static class ProjectMilestoneEndpoints
{
    public static IEndpointRouteBuilder MapProjectMilestoneEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/projects", ListProjectsAsync).RequireOwnerSession();
        endpoints.MapPost("/api/projects", CreateProjectAsync).RequireOwnerSession();
        endpoints.MapGet("/api/projects/{projectId:guid}", GetProjectAsync).RequireOwnerSession();
        endpoints.MapPatch("/api/projects/{projectId:guid}", UpdateProjectAsync).RequireOwnerSession();
        endpoints.MapDelete("/api/projects/{projectId:guid}", ArchiveProjectAsync).RequireOwnerSession();

        endpoints.MapGet("/api/projects/{projectId:guid}/milestones", ListMilestonesAsync).RequireOwnerSession();
        endpoints.MapPost("/api/projects/{projectId:guid}/milestones", CreateMilestoneAsync).RequireOwnerSession();
        endpoints.MapPost("/api/projects/{projectId:guid}/milestones/import", ImportCandidatesAsync).RequireOwnerSession();

        endpoints.MapGet("/api/milestones/{milestoneId:guid}", GetMilestoneAsync).RequireOwnerSession();
        endpoints.MapPatch("/api/milestones/{milestoneId:guid}", UpdateMilestoneAsync).RequireOwnerSession();
        endpoints.MapPost("/api/milestones/{milestoneId:guid}/confirm", ConfirmMilestoneAsync).RequireOwnerSession();
        endpoints.MapPost("/api/milestones/{milestoneId:guid}/dismiss", DismissMilestoneAsync).RequireOwnerSession();

        return endpoints;
    }

    private static async Task<IResult> ListProjectsAsync(
        HttpContext context,
        IProjectMilestoneService projects,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var page = await projects.ListProjectsAsync(
            owner,
            new PageRequest(ReadLimit(context), context.Request.Query["cursor"]),
            cancellationToken).ConfigureAwait(false);

        return Results.Json(ProjectListResponse.From(page));
    }

    private static async Task<IResult> CreateProjectAsync(
        HttpContext context,
        CreateProjectRequest? request,
        IProjectMilestoneService projects,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var project = await projects.CreateProjectAsync(
            owner,
            ProjectMilestoneMapping.ToInput(request ?? new CreateProjectRequest(null, null, null, null)),
            cancellationToken).ConfigureAwait(false);

        return Created(context, ProjectResponse.From(project), $"/api/projects/{project.Id.ToString("D")}");
    }

    private static async Task<IResult> GetProjectAsync(
        Guid projectId,
        HttpContext context,
        IProjectMilestoneService projects,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);
        var project = await projects.GetProjectAsync(owner, projectId, cancellationToken).ConfigureAwait(false);

        ProjectMilestoneMapping.PublishRevision(context, project.Revision);

        return Results.Json(ProjectResponse.From(project));
    }

    private static async Task<IResult> UpdateProjectAsync(
        Guid projectId,
        HttpContext context,
        UpdateProjectRequest? request,
        IProjectMilestoneService projects,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var project = await projects.UpdateProjectAsync(
            owner,
            projectId,
            ProjectMilestoneMapping.RequiredRevision(context, "project"),
            ProjectMilestoneMapping.ToPatch(request ?? new UpdateProjectRequest(null, null, null, null)),
            cancellationToken).ConfigureAwait(false);

        ProjectMilestoneMapping.PublishRevision(context, project.Revision);

        return Results.Json(ProjectResponse.From(project));
    }

    private static async Task<IResult> ArchiveProjectAsync(
        Guid projectId,
        HttpContext context,
        IProjectMilestoneService projects,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var project = await projects.ArchiveProjectAsync(
            owner,
            projectId,
            ProjectMilestoneMapping.RequiredRevision(context, "project"),
            cancellationToken).ConfigureAwait(false);

        ProjectMilestoneMapping.PublishRevision(context, project.Revision);

        // The project is still there and still readable; nothing under it was deleted.
        return Results.Json(ProjectResponse.From(project));
    }

    private static async Task<IResult> ListMilestonesAsync(
        Guid projectId,
        HttpContext context,
        IProjectMilestoneService milestones,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var page = await milestones.ListMilestonesAsync(
            owner,
            projectId,
            MilestoneListRequest.From(
                new PageRequest(ReadLimit(context), context.Request.Query["cursor"]),
                context.Request.Query["status"]),
            cancellationToken).ConfigureAwait(false);

        return Results.Json(MilestoneListResponse.From(page));
    }

    private static async Task<IResult> CreateMilestoneAsync(
        Guid projectId,
        HttpContext context,
        CreateMilestoneRequest? request,
        IProjectMilestoneService milestones,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var milestone = await milestones.CreateManualMilestoneAsync(
            owner,
            projectId,
            ProjectMilestoneMapping.ToDraft(request ?? EmptyMilestone()),
            cancellationToken).ConfigureAwait(false);

        return Created(
            context,
            MilestoneResponse.From(milestone),
            $"/api/milestones/{milestone.Id.ToString("D")}");
    }

    private static async Task<IResult> ImportCandidatesAsync(
        Guid projectId,
        HttpContext context,
        ImportCandidatesRequest? request,
        IProjectMilestoneService milestones,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var imported = await milestones.ImportCandidatesAsync(
            owner,
            projectId,
            ProjectMilestoneMapping.ToInput(request ?? new ImportCandidatesRequest(null, null)),
            cancellationToken).ConfigureAwait(false);

        var response = ImportedCandidatesResponse.From(imported);

        // 201 with a Location only when this call really did create a record. An import whose keys
        // are all known is a read of what is already stored, and reporting it as a creation would
        // tell the caller something did not happen.
        return imported.CreatedAny
            ? Created(context, response, $"/api/milestones/{imported.Items.First(item => item.Created).MilestoneId.ToString("D")}")
            : Results.Json(response);
    }

    private static async Task<IResult> GetMilestoneAsync(
        Guid milestoneId,
        HttpContext context,
        IProjectMilestoneService milestones,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);
        var milestone = await milestones.GetMilestoneAsync(owner, milestoneId, cancellationToken).ConfigureAwait(false);

        ProjectMilestoneMapping.PublishRevision(context, milestone.Revision);

        return Results.Json(MilestoneResponse.From(milestone));
    }

    private static async Task<IResult> UpdateMilestoneAsync(
        Guid milestoneId,
        HttpContext context,
        UpdateMilestoneRequest? request,
        IProjectMilestoneService milestones,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var milestone = await milestones.UpdateMilestoneAsync(
            owner,
            milestoneId,
            ProjectMilestoneMapping.RequiredRevision(context, "milestone"),
            ProjectMilestoneMapping.ToPatch(request ?? new UpdateMilestoneRequest(null, null, null, null, null, null, null)),
            cancellationToken).ConfigureAwait(false);

        ProjectMilestoneMapping.PublishRevision(context, milestone.Revision);

        return Results.Json(MilestoneResponse.From(milestone));
    }

    private static async Task<IResult> ConfirmMilestoneAsync(
        Guid milestoneId,
        HttpContext context,
        IProjectMilestoneService milestones,
        IOwnerAccessService access,
        CancellationToken cancellationToken) =>
        await ChangeStatusAsync(
            milestoneId,
            context,
            milestones,
            access,
            (service, owner, revision, token) => service.ConfirmMilestoneAsync(owner, milestoneId, revision, token),
            cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> DismissMilestoneAsync(
        Guid milestoneId,
        HttpContext context,
        IProjectMilestoneService milestones,
        IOwnerAccessService access,
        CancellationToken cancellationToken) =>
        await ChangeStatusAsync(
            milestoneId,
            context,
            milestones,
            access,
            (service, owner, revision, token) => service.DismissMilestoneAsync(owner, milestoneId, revision, token),
            cancellationToken).ConfigureAwait(false);

    private static async Task<IResult> ChangeStatusAsync(
        Guid milestoneId,
        HttpContext context,
        IProjectMilestoneService milestones,
        IOwnerAccessService access,
        Func<IProjectMilestoneService, OwnerScope, long, CancellationToken, Task<Milestone>> change,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var milestone = await change(
            milestones,
            owner,
            ProjectMilestoneMapping.RequiredRevision(context, "milestone"),
            cancellationToken).ConfigureAwait(false);

        ProjectMilestoneMapping.PublishRevision(context, milestone.Revision);

        return Results.Json(MilestoneResponse.From(milestone));
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

    private static IResult Created(HttpContext context, object body, string location)
    {
        // The revision is published as the entity-tag for a single record's creation. A batch import
        // that created several candidates has no one revision to publish, so it is left off rather
        // than reported as zero.
        if (ReadRevision(body) > 0)
        {
            ProjectMilestoneMapping.PublishRevision(context, ReadRevision(body));
        }

        return Results.Created(location, body);
    }

    private static long ReadRevision(object body) => body switch
    {
        ProjectResponse project => project.Revision,
        MilestoneResponse milestone => milestone.Revision,
        _ => 0,
    };

    private static int? ReadLimit(HttpContext context)
    {
        var raw = context.Request.Query["limit"].ToString();

        return int.TryParse(raw, out var limit) ? limit : null;
    }

    private static CreateMilestoneRequest EmptyMilestone() => new(null, null, null, null, null, null, null);
}
