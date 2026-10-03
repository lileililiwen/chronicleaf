using Chronicleaf.Api.Access;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Secrets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Chronicleaf.Api.Secrets;

/// <summary>
/// The private settings routes for provider credentials (CREDENTIAL-VAULT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Four routes, all private: the public allow-list is untouched and every route here carries
/// <c>RequireOwnerSession()</c>, so a browser with no session never reaches a handler and
/// <c>/api/health</c> and <c>/api/ready</c> remain the only public surface. The origin check and the
/// antiforgery requirement of the delivered boundary apply to the two mutations unchanged.
/// </para>
/// <para>
/// <b>No route returns a secret, and no route resolves one.</b> The read routes return four metadata
/// fields and the <c>ETag</c> the current revision is published as; the mutation routes return the
/// metadata of what they stored. There is no "reveal", no "verify" and no export, and the vault's
/// resolve has no HTTP shape at all: a secret is not something an endpoint can return, so there is no
/// error path here that could return one either.
/// </para>
/// <para>
/// The save is conditional on <c>If-Match</c> like every other mutation, publishes the new revision as
/// its <c>ETag</c>, and answers 201 for a first save and 200 for a replacement, because those are
/// different outcomes. A delete is conditional the same way and answers 200 with the metadata of what
/// was removed, which carries no value.
/// </para>
/// </remarks>
public static class CredentialEndpoints
{
    public static IEndpointRouteBuilder MapCredentialEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapGet("/api/settings/credentials", ListCredentialsAsync)
            .RequireOwnerSession();

        endpoints
            .MapGet("/api/settings/credentials/{provider}", DescribeCredentialAsync)
            .RequireOwnerSession();

        endpoints
            .MapPut("/api/settings/credentials/{provider}", SaveCredentialAsync)
            .RequireOwnerSession();

        endpoints
            .MapDelete("/api/settings/credentials/{id:guid}", DeleteCredentialAsync)
            .RequireOwnerSession();

        return endpoints;
    }

    private static async Task<IResult> ListCredentialsAsync(
        HttpContext context,
        ICredentialVaultService credentials,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var page = await credentials
            .ListAsync(owner.ScopeId, context.Request.Query["cursor"], ReadLimit(context), cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(CredentialListResponse.From(page));
    }

    private static async Task<IResult> DescribeCredentialAsync(
        string provider,
        HttpContext context,
        ICredentialVaultService credentials,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);
        var status = await credentials
            .DescribeAsync(owner.ScopeId, provider, cancellationToken)
            .ConfigureAwait(false);

        // The current revision is published even when nothing is configured, as 0: the token a client
        // presents for a first save is the one that says "nothing is stored yet".
        context.Response.Headers.ETag = CredentialMapping.EntityTag(status.Revision ?? 0);

        // A provider with no credential is 200 with configured false, not 404. The caller asked whether
        // this installation has one, and the answer is no - which is a fact about the installation and
        // not a missing resource.
        return Results.Json(CredentialMetadataResponse.From(status));
    }

    private static async Task<IResult> SaveCredentialAsync(
        string provider,
        HttpContext context,
        CredentialSaveRequestBody? request,
        ICredentialVaultService credentials,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);
        var expectedRevision = CredentialMapping.RequiredRevision(context, request?.ExpectedRevision);

        var stored = await credentials
            .SaveAsync(
                CredentialMapping.ToSave(owner, provider, request, expectedRevision),
                cancellationToken)
            .ConfigureAwait(false);

        context.Response.Headers.ETag = CredentialMapping.EntityTag(stored.Revision);

        var body = CredentialMetadataResponse.From(
            new CredentialStatus(stored.Provider, Configured: true, stored.Id, stored.UpdatedAt, stored.Revision));

        // A first save created the credential and answers 201; a replacement did not and answers 200.
        // A client that retries a replacement should not be told it created something.
        return expectedRevision == 0
            ? Results.Created($"/api/settings/credentials/{stored.Provider}", body)
            : Results.Json(body);
    }

    private static async Task<IResult> DeleteCredentialAsync(
        Guid id,
        HttpContext context,
        ICredentialVaultService credentials,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        // Conditional, like a save: the caller presents the revision it read, and a credential that
        // was rotated since is left in place rather than deleted by a caller that never saw it.
        var expectedRevision = CredentialMapping.RequiredRevision(context, bodyRevision: null);

        var removed = await credentials
            .DeleteAsync(new CredentialDeleteRequest(owner, id, expectedRevision), cancellationToken)
            .ConfigureAwait(false);

        // The metadata of what was removed, with configured false. It carries no value, and there is
        // nothing about the deleted secret to report.
        return Results.Json(CredentialMetadataResponse.From(
            new CredentialStatus(removed.Provider, Configured: false, removed.Id, removed.UpdatedAt, removed.Revision)));
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

    private static int? ReadLimit(HttpContext context)
    {
        var raw = context.Request.Query["limit"].ToString();

        return int.TryParse(raw, out var limit) ? limit : null;
    }
}
