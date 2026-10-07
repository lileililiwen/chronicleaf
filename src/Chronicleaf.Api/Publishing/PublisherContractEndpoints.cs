using Chronicleaf.Api.Access;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Publishing;
using Chronicleaf.Application.Teams;
using Chronicleaf.Application.Tenancy;

namespace Chronicleaf.Api.Publishing;

/// <summary>
/// The private reviewed-publication routes (PUBLISHER-CONTRACT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Five routes, all private: the public allow-list is untouched and every route carries
/// <c>RequireOwnerSession()</c>, so <c>/api/health</c> and <c>/api/ready</c> remain the only public
/// surface. A receipt is readable only by the owner it belongs to and by nobody else, and no
/// route here serves a body, a picture or a credential: the receipt names what was sent where
/// and what the remote side answered, the target list names what each target id means, and the
/// content itself stays behind the review package's own export.
/// </para>
/// <para>
/// <b>A preview authorizes nothing; a publish sends only what the stored rows still
/// describe.</b> The preview answers what a send would do without sending anything; the publish
/// validates the live approval, the confirmed preview and the credential metadata before a job
/// exists and answers 202 with the operation the owner watches - or 200 with the original
/// receipt when an identical request is resubmitted. The reconcile route asks the target to
/// resolve an ambiguous receipt where the target permits it, and refuses where it does not.
/// </para>
/// <para>
/// <b>No route here generates, approves or exports anything.</b> Approval stays the review
/// package's table, generation stays the drafts package's, and no byte here reaches a remote
/// side except through the one adapter call the queued run makes: the routes authorize and
/// report, and the run sends.
/// </para>
/// <para>
/// <b>Hosted authorization is per project, not per scope.</b> In Hosted mode the preview and
/// receipt reads are granted reads, while publishing and reconciling are granted publishes -
/// and a publish additionally requires the approved head the contract itself re-checks, on the
/// same terms the teams package holds its publisher role to. The Community path takes no
/// branch on team state: a foreign asset is the delivered 404 through scope isolation either
/// way.
/// </para>
/// </remarks>
public static class PublisherContractEndpoints
{
    public static IEndpointRouteBuilder MapPublisherContractEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapGet("/api/publisher-targets", ListTargetsAsync)
            .RequireOwnerSession();

        endpoints
            .MapPost("/api/assets/{assetId:guid}/publish-preview", PreviewAsync)
            .RequireOwnerSession();

        endpoints
            .MapPost("/api/assets/{assetId:guid}/publish", PublishAsync)
            .RequireOwnerSession();

        endpoints
            .MapGet("/api/publications/{attemptId:guid}", ReadReceiptAsync)
            .RequireOwnerSession();

        endpoints
            .MapPost("/api/publications/{attemptId:guid}/reconcile", ReconcileAsync)
            .RequireOwnerSession();

        return endpoints;
    }

    private static Task<IResult> ListTargetsAsync(IPublisherRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        // Classification, not transport: what each target id means, in registration order. It
        // serves the registry field for field - no credential, no adapter, no content - so
        // reading it can never send anything, and an installation with nothing stored and no
        // credential saved still reads the full classification.
        return Task.FromResult<IResult>(Results.Json(new PublisherTargetListResponse(
            [.. registry.Targets.Select(descriptor => new PublisherTargetResponse(
                descriptor.TargetId,
                descriptor.SupportsDraft,
                descriptor.SupportsPublish,
                descriptor.SupportsReconcile,
                descriptor.ExportOnly))])));
    }

    private static async Task<IResult> PreviewAsync(
        Guid assetId,
        PublishPreviewWireRequest? request,
        HttpContext context,
        IPublisherService publications,
        IOwnerAccessService access,
        ITeamPermissionService teams,
        TenancySettings tenancy,
        CancellationToken cancellationToken)
    {
        var session = AccessEndpoints.OwnerSession(context);
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        if (request is null)
        {
            // An absent body is the same request with every field empty, which the use case then
            // refuses by name rather than being interpreted as a preview of a default target. A
            // preview nobody aimed is a preview nobody asked for.
            throw new ValidationFailedException(
                "targetId",
                "A preview names the publisher target it is aimed at, so name one.");
        }

        if (tenancy.IsHosted)
        {
            var projectId = await publications
                .ResolveProjectAsync(owner, assetId, cancellationToken)
                .ConfigureAwait(false);

            await teams.RequireProjectPermissionAsync(
                session,
                projectId,
                "read",
                false,
                cancellationToken).ConfigureAwait(false);
        }

        var preview = await publications
            .PreviewAsync(
                owner,
                assetId,
                new PublishPreviewRequest(request.TargetId ?? string.Empty, request.Mode ?? string.Empty),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(PublishPreviewResponse.From(preview));
    }

    private static async Task<IResult> PublishAsync(
        Guid assetId,
        PublishWireRequest? request,
        HttpContext context,
        IPublisherService publications,
        IOwnerAccessService access,
        ITeamPermissionService teams,
        TenancySettings tenancy,
        CancellationToken cancellationToken)
    {
        var session = AccessEndpoints.OwnerSession(context);
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        if (request is null)
        {
            // An absent body is the same request with every field empty, which the use case then
            // refuses by name rather than being interpreted as consent to a default send. A
            // publish nobody confirmed is a publish nobody asked for.
            throw new PublisherConfirmationRequiredException();
        }

        if (tenancy.IsHosted)
        {
            var projectId = await publications
                .ResolveProjectAsync(owner, assetId, cancellationToken)
                .ConfigureAwait(false);

            var approved = await publications
                .IsApprovedHeadAsync(owner, assetId, cancellationToken)
                .ConfigureAwait(false);

            // A publisher publishes only an approved head: the grant authorizes the act, and the
            // approval state authorizes the object. The contract re-checks every binding itself
            // before a job exists; this check keeps a grant from authorizing what the approval
            // does not.
            await teams.RequireProjectPermissionAsync(
                session,
                projectId,
                "publish",
                approved,
                cancellationToken).ConfigureAwait(false);
        }

        var accepted = await publications
            .PublishAsync(
                owner,
                assetId,
                new PublishRequest(
                    request.TargetId ?? string.Empty,
                    request.Mode ?? string.Empty,
                    request.PreviewHash ?? string.Empty,
                    request.ApprovalId,
                    request.IdempotencyKey ?? string.Empty,
                    request.Confirm),
                cancellationToken)
            .ConfigureAwait(false);

        if (!accepted.Created)
        {
            var receipt = await publications
                .ReadReceiptAsync(owner, accepted.AttemptId, cancellationToken)
                .ConfigureAwait(false);

            return Results.Json(PublicationReceiptResponse.From(receipt, Created: false));
        }

        return Results.Json(
            new PublishAcceptedResponse(accepted.OperationId, accepted.AttemptId, accepted.Created),
            statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> ReadReceiptAsync(
        Guid attemptId,
        HttpContext context,
        IPublisherService publications,
        IOwnerAccessService access,
        ITeamPermissionService teams,
        TenancySettings tenancy,
        CancellationToken cancellationToken)
    {
        var session = AccessEndpoints.OwnerSession(context);
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        if (tenancy.IsHosted)
        {
            var projectId = await publications
                .ResolveReceiptProjectAsync(owner, attemptId, cancellationToken)
                .ConfigureAwait(false);

            await teams.RequireProjectPermissionAsync(
                session,
                projectId,
                "read",
                false,
                cancellationToken).ConfigureAwait(false);
        }

        var receipt = await publications
            .ReadReceiptAsync(owner, attemptId, cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(PublicationReceiptResponse.From(receipt, Created: false));
    }

    private static async Task<IResult> ReconcileAsync(
        Guid attemptId,
        HttpContext context,
        IPublisherService publications,
        IOwnerAccessService access,
        ITeamPermissionService teams,
        TenancySettings tenancy,
        CancellationToken cancellationToken)
    {
        var session = AccessEndpoints.OwnerSession(context);
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        if (tenancy.IsHosted)
        {
            var projectId = await publications
                .ResolveReceiptProjectAsync(owner, attemptId, cancellationToken)
                .ConfigureAwait(false);

            var assetId = await publications
                .ResolveReceiptAssetAsync(owner, attemptId, cancellationToken)
                .ConfigureAwait(false);

            var approved = await publications
                .IsApprovedHeadAsync(owner, assetId, cancellationToken)
                .ConfigureAwait(false);

            await teams.RequireProjectPermissionAsync(
                session,
                projectId,
                "publish",
                approved,
                cancellationToken).ConfigureAwait(false);
        }

        var receipt = await publications
            .ReconcileAsync(owner, attemptId, cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(PublicationReceiptResponse.From(receipt, Created: false));
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

    /// <summary>Every classified publisher target, in registration order.</summary>
    /// <param name="Targets">One entry per descriptor the registry holds.</param>
    public sealed record PublisherTargetListResponse(IReadOnlyList<PublisherTargetResponse> Targets);

    /// <summary>One classified target, as the target list answers it.</summary>
    /// <param name="TargetId">The id publishes name.</param>
    /// <param name="SupportsDraft">Whether the target can hold the material as a remote draft.</param>
    /// <param name="SupportsPublish">Whether the target can publish the material.</param>
    /// <param name="SupportsReconcile">Whether an ambiguous write can be resolved by remote id.</param>
    /// <param name="ExportOnly">True when material leaves through the target's export package, carried by hand.</param>
    public sealed record PublisherTargetResponse(
        string TargetId,
        bool SupportsDraft,
        bool SupportsPublish,
        bool SupportsReconcile,
        bool ExportOnly);

    /// <summary>What the preview route reads: the target and the mode, nothing else.</summary>
    /// <param name="TargetId">The registry id the preview is for.</param>
    /// <param name="Mode">One of <c>draft</c> or <c>publish</c>.</param>
    public sealed record PublishPreviewWireRequest(string? TargetId, string? Mode);

    /// <summary>
    /// What the publish route reads: the target, the mode, the confirmed preview, the approval
    /// the request was read against, the idempotency key and the separate, explicit confirmation.
    /// </summary>
    /// <param name="TargetId">The registry id to send to.</param>
    /// <param name="Mode">One of <c>draft</c> or <c>publish</c>.</param>
    /// <param name="PreviewHash">The confirmed preview's digest.</param>
    /// <param name="ApprovalId">The live approval the request was read against.</param>
    /// <param name="IdempotencyKey">The caller's key, unique within the scope for this contract.</param>
    /// <param name="Confirm">Must be true: the explicit publication consent.</param>
    public sealed record PublishWireRequest(
        string? TargetId,
        string? Mode,
        string? PreviewHash,
        Guid ApprovalId,
        string? IdempotencyKey,
        bool Confirm);

    /// <summary>One accepted publish, as the publish route answers it.</summary>
    /// <param name="OperationId">The durable operation the owner watches.</param>
    /// <param name="AttemptId">The attempt that carries the receipt.</param>
    /// <param name="Created">False when an identical request had already created this publish.</param>
    public sealed record PublishAcceptedResponse(Guid OperationId, Guid AttemptId, bool Created);

    /// <summary>One target preview, as the preview route answers it.</summary>
    /// <param name="TargetId">The registry id this preview is for.</param>
    /// <param name="Mode">The mode the preview was computed for.</param>
    /// <param name="AssetId">The asset the preview describes.</param>
    /// <param name="ContentRevision">The head revision the preview describes.</param>
    /// <param name="BodyHash">The digest of exactly the bytes the preview describes.</param>
    /// <param name="MediaPlan">The pictures a send would carry, by media id, ordered.</param>
    /// <param name="Warnings">Human-readable cautions, never content.</param>
    /// <param name="PreviewHash">The digest the publish request must confirm.</param>
    public sealed record PublishPreviewResponse(
        string TargetId,
        string Mode,
        Guid AssetId,
        long ContentRevision,
        string BodyHash,
        IReadOnlyList<Guid> MediaPlan,
        IReadOnlyList<string> Warnings,
        string PreviewHash)
    {
        public static PublishPreviewResponse From(Application.Publishing.PublishPreviewResponse preview) => new(
            preview.TargetId,
            preview.Mode,
            preview.AssetId,
            preview.ContentRevision,
            preview.BodyHash,
            preview.MediaPlan,
            preview.Warnings,
            preview.PreviewHash);
    }

    /// <summary>One stored receipt, as the receipt and reconcile routes answer it.</summary>
    /// <param name="AttemptId">This attempt's identifier.</param>
    /// <param name="OperationId">The durable operation that ran it.</param>
    /// <param name="TargetId">The publisher target it was sent to.</param>
    /// <param name="AssetId">The asset the sent revision belongs to.</param>
    /// <param name="ContentRevision">The sent revision number.</param>
    /// <param name="BodyHash">The digest of exactly the bytes that were sent.</param>
    /// <param name="Mode">The mode that was authorized.</param>
    /// <param name="State">One of prepared, sending, published, remote-draft, unknown or failed.</param>
    /// <param name="RemoteId">The remote side's identifier, when it named one.</param>
    /// <param name="RemoteUrl">The remote side's address, when it named one.</param>
    /// <param name="ErrorCode">The redacted failure code, when the attempt ended without publishing.</param>
    /// <param name="ReconcileHint">Present and manual when the attempt is unknown and unreconcilable.</param>
    /// <param name="Created">Always false on reads: the receipt already existed.</param>
    public sealed record PublicationReceiptResponse(
        Guid AttemptId,
        Guid OperationId,
        string TargetId,
        Guid AssetId,
        long ContentRevision,
        string BodyHash,
        string Mode,
        string State,
        string? RemoteId,
        string? RemoteUrl,
        string? ErrorCode,
        string? ReconcileHint,
        bool Created)
    {
        public static PublicationReceiptResponse From(PublicationReceipt receipt, bool Created) => new(
            receipt.AttemptId,
            receipt.OperationId,
            receipt.TargetId,
            receipt.AssetId,
            receipt.ContentRevision,
            receipt.BodyHash,
            receipt.Mode,
            receipt.State,
            receipt.RemoteId,
            receipt.RemoteUrl,
            receipt.ErrorCode,
            receipt.ReconcileHint,
            Created);
    }
}
