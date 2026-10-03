using System.Globalization;
using Chronicleaf.Api.Access;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Evidence;
using Chronicleaf.Application.Milestones;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Chronicleaf.Api.Evidence;

/// <summary>
/// The private evidence routes (MILESTONE-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Two routes, both private: the owner reads a milestone's evidence and the owner states something
/// by hand. The public allow-list is untouched - every route here carries
/// <c>RequireOwnerSession()</c>, so a browser with no session never reaches the handler, and
/// <c>/api/health</c> and <c>/api/ready</c> remain the only public surface.
/// </para>
/// <para>
/// Only the manual append is a route. Appending collected source material is the application
/// contract <c>IAppendEvidence.AppendAsync</c> and has no HTTP shape at all: a source's facts arrive
/// from a collector, and there is no request a client could send that would be entitled to claim
/// one. That is also why the manual body carries no kind, no availability and no source reference -
/// the service derives all three from what the owner wrote.
/// </para>
/// <para>
/// The manual append is conditional on <c>If-Match</c> like every other mutation here, publishes the
/// new revision as its <c>ETag</c>, and returns the whole new snapshot so the client can re-render
/// from the server's own answer rather than from a guess about what was added.
/// </para>
/// </remarks>
public static class MilestoneEvidenceEndpoints
{
    public static IEndpointRouteBuilder MapMilestoneEvidenceEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/api/milestones/{milestoneId:guid}/evidence", ListEvidenceAsync).RequireOwnerSession();
        endpoints.MapPost("/api/milestones/{milestoneId:guid}/evidence/manual", AppendManualEvidenceAsync).RequireOwnerSession();

        return endpoints;
    }

    private static async Task<IResult> ListEvidenceAsync(
        Guid milestoneId,
        HttpContext context,
        IReadEvidence evidence,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var page = await evidence
            .ListAsync(
                owner,
                milestoneId,
                new PageRequest(ReadLimit(context), context.Request.Query["cursor"]),
                cancellationToken)
            .ConfigureAwait(false);

        // The current revision is published as the entity-tag even for a milestone with no evidence,
        // where it is 0. A client that has read the empty state needs a token it can present, and 0
        // is the one that says "nothing has been recorded yet".
        context.Response.Headers.ETag = $"\"{RevisionOf(page).ToString(CultureInfo.InvariantCulture)}\"";

        return Results.Json(EvidenceListResponse.From(milestoneId, page));
    }

    private static async Task<IResult> AppendManualEvidenceAsync(
        Guid milestoneId,
        HttpContext context,
        ManualEvidenceRequestBody? request,
        IAppendEvidence evidence,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);
        var expectedRevision = EvidenceMapping.RequiredRevision(context, request?.ExpectedRevision);

        var bundle = await evidence
            .AppendManualAsync(
                owner,
                milestoneId,
                EvidenceMapping.ToManual(request, expectedRevision),
                cancellationToken)
            .ConfigureAwait(false);

        context.Response.Headers.ETag = $"\"{bundle.Revision.ToString(CultureInfo.InvariantCulture)}\"";

        // The whole snapshot, not just what this call added. A caller that rendered the difference
        // itself would be reconstructing state the server already has, and would have to guess what
        // the server decided to carry forward.
        var body = EvidenceBundleResponse.From(bundle);

        // A revision that advanced is a new snapshot and answers 201; a revision that did not advance
        // means the append was a no-op, because the facts were already recorded, and that answers 200.
        // The two are different outcomes and a caller that retries a no-op should not be told it
        // created something.
        return bundle.Revision > expectedRevision
            ? Results.Created($"/api/milestones/{milestoneId:D}/evidence", body)
            : Results.Json(body);
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

    private static long RevisionOf(EvidencePage page) => page.Bundles.Count == 0 ? 0 : page.Bundles[0].Revision;

    private static int? ReadLimit(HttpContext context)
    {
        var raw = context.Request.Query["limit"].ToString();

        return int.TryParse(raw, out var limit) ? limit : null;
    }
}
