using System.Globalization;
using Chronicleaf.Api.Access;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Git;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Chronicleaf.Api.Git;

/// <summary>
/// The private local Git evidence routes (LOCAL-GIT-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Four routes, all private: the public allow-list is untouched and every route carries
/// <c>RequireOwnerSession()</c>, so <c>/api/health</c> and <c>/api/ready</c> remain the only public
/// surface. A registered directory is readable by the owner whose project registered it and by nobody else.
/// </para>
/// <para>
/// <b>No route here accepts a path, and only one accepts a path at all.</b> The registration route takes a
/// registered alias and a path relative to it; nothing takes an absolute directory, and no response returns
/// one. The registered roots themselves come from the operator's configuration and are published as a list
/// of aliases, which is what the panel's picker needs and is the most a caller can learn about the machine.
/// </para>
/// <para>
/// <b>The catalogue route exists so a picker can be a picker.</b> Without it, a panel would have to ask the
/// owner to type an alias from memory and would be unable to say "this installation has none registered",
/// which is the answer a fresh Community install actually has.
/// </para>
/// <para>
/// <b>The collection route answers 202, not a bundle.</b> A collection is a bounded set of process reads
/// with a timeout attached, so it runs in the delivered queue and this route answers with the operation id
/// plus the delivered operation route to read it on. A client that wanted the evidence immediately would
/// have to hold a request open across Git, which is what the queue exists to avoid.
/// </para>
/// <para>
/// The registration and the collection are both conditional on the revision they are based on: the project
/// revision for a registration, the evidence revision for a collection. A body that disagrees with
/// <c>If-Match</c> is refused rather than resolved in favour of one of them.
/// </para>
/// </remarks>
public static class LocalGitEvidenceEndpoints
{
    public static IEndpointRouteBuilder MapLocalGitEvidenceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapGet("/api/local-repository-roots", ReadCatalogAsync)
            .RequireOwnerSession();

        endpoints
            .MapGet("/api/projects/{projectId:guid}/local-repository", ReadRepositoryAsync)
            .RequireOwnerSession();

        endpoints
            .MapPost("/api/projects/{projectId:guid}/local-repository", RegisterAsync)
            .RequireOwnerSession();

        endpoints
            .MapPost("/api/milestones/{milestoneId:guid}/local-sync", RequestSyncAsync)
            .RequireOwnerSession();

        return endpoints;
    }

    /// <summary>
    /// The aliases this deployment offers, and whether it reads local directories at all.
    /// </summary>
    /// <remarks>
    /// <b>Answered even in a deployment that refuses local collection</b>, because "this deployment does not
    /// read local directories" is exactly what a panel needs in order to say so instead of offering a form
    /// that will be refused. The route does not call the service for that reason: the service's whole first
    /// act is to refuse in Hosted, and a route whose only job is to report the refusal must be able to
    /// report it.
    /// </remarks>
    private static IResult ReadCatalogAsync(ILocalRepositoryCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        var available = catalog.DeploymentMode is not LocalGitDeploymentMode.Hosted;

        // In a deployment that may not read local directories the alias list is empty even though the
        // catalogue holds one. A tenant that could read the names would learn the shape of a machine it does
        // not share, and the names are the only thing about a registered root that is not already public.
        return Results.Json(new LocalRepositoryCatalogResponse(
            catalog.DeploymentMode.ToString().ToLowerInvariant(),
            available,
            available ? catalog.Aliases : []));
    }

    private static async Task<IResult> ReadRepositoryAsync(
        Guid projectId,
        HttpContext context,
        ILocalGitEvidenceService evidence,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);
        var repository = await evidence
            .ReadAsync(owner, projectId, cancellationToken)
            .ConfigureAwait(false);

        if (repository is null)
        {
            // A project with no registration is answered with this package's own code rather than the
            // delivered not-found one: the project is real and reachable, and what is missing is something
            // the owner has to supply. Only a project this owner may not reach is a 404.
            throw new LocalGitRepositoryMissingException();
        }

        context.Response.Headers.ETag =
            $"\"{LocalRepositoryResponse.Stamp(repository.UpdatedAt)}\"";

        return Results.Json(LocalRepositoryResponse.From(repository));
    }

    private static async Task<IResult> RegisterAsync(
        Guid projectId,
        HttpContext context,
        LocalRepositoryWireRequest? request,
        ILocalGitEvidenceService evidence,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var repository = await evidence
            .RegisterAsync(
                owner,
                projectId,
                new LocalRepositoryRequest(request?.RootAlias, request?.RelativePath),
                cancellationToken)
            .ConfigureAwait(false);

        context.Response.Headers.ETag =
            $"\"{LocalRepositoryResponse.Stamp(repository.UpdatedAt)}\"";

        // 201 whether the registration was created or replaced: the project now has exactly one registered
        // local repository and this request is what put it there, so the two are the same outcome from a
        // client's point of view and the body carries the id either way.
        return Results.Json(
            LocalRepositoryResponse.From(repository),
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> RequestSyncAsync(
        Guid milestoneId,
        HttpContext context,
        LocalGitSyncWireRequest? request,
        ILocalGitEvidenceService evidence,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);
        var body = request ?? LocalGitSyncWireRequest.Empty;

        // The revision the owner was looking at travels twice. A body that disagrees with the header is
        // refused rather than resolved in favour of one of them, because picking one silently is how a stale
        // collection gets written onto a bundle somebody else has moved.
        var expected = RequireExpectedRevision(context, body.ExpectedBundleRevision);

        var accepted = await evidence
            .RequestSyncAsync(
                owner,
                milestoneId,
                new LocalGitSyncRequest(
                    body.HeadRef,
                    body.BaseRef,
                    expected,
                    body.IdempotencyKey ?? string.Empty),
                cancellationToken)
            .ConfigureAwait(false);

        var payload = new LocalGitSyncAcceptedResponse(
            accepted.Operation.Id.ToString("D"),
            $"/api/operations/{accepted.Operation.Id:D}",
            OperationStatuses.ToWire(accepted.Operation.Status),
            accepted.Created);

        // 202 with the operation's own location: the work has been accepted and durably recorded, and this is
        // where its progress is read. A replay under the same idempotency key answers with the same job and
        // `created: false`, which is a different answer to a different question and the same answer to this
        // one.
        return Results.Json(payload, statusCode: StatusCodes.Status202Accepted);
    }

    /// <summary>
    /// The revision a collection is based on, from the header and the body together. A missing header is 428,
    /// because the delivered routes all require one; a body that names a different revision is 422, because
    /// that is a malformed request rather than a missing precondition.
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
