using System.Globalization;
using System.Text.Json;
using Chronicleaf.Api.Access;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Content;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Voice;
using Chronicleaf.Core;

namespace Chronicleaf.Api.Content;

/// <summary>
/// The private grounded-draft routes (GROUNDED-MARKDOWN-DRAFTS-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Three routes, all private: the public allow-list is untouched and every route carries
/// <c>RequireOwnerSession()</c>, so <c>/api/health</c> and <c>/api/ready</c> remain the only public
/// surface. A draft is text the owner has not approved, so it is readable only by the owner it belongs to
/// and by nobody else.
/// </para>
/// <para>
/// <b>The request route answers 202, not a draft.</b> A draft is two provider calls, a repair call
/// possibly, and two local validation passes; it runs in the delivered queue and this route answers with
/// the operation id plus the delivered operation route to read it on. A client that wanted the text
/// immediately would have to hold a request open across a provider, which is what the queue exists to
/// avoid.
/// </para>
/// <para>
/// <b>The revisions the owner is looking at travel in the body, and they are checked against the
/// authorized preview rather than trusted.</b> A body that disagrees with the preview is a 422, the same
/// rule the delivered GitHub route applies to a body that disagrees with <c>If-Match</c>, and it exists
/// for the same reason: silently picking one of two contradictory claims is how a stale client gets a
/// draft written against input it never described.
/// </para>
/// <para>
/// <b>No route here can approve anything.</b> There is no approve verb, and the read route's status is
/// <c>draft</c> because that is the only status this package writes. Human review is
/// <c>markdown-review-export</c>'s, and it will bind an approval to a revision and a body hash.
/// </para>
/// </remarks>
public static class ContentDraftEndpoints
{
    public static IEndpointRouteBuilder MapContentDraftEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapPost("/api/milestones/{milestoneId:guid}/drafts", RequestDraftAsync)
            .RequireOwnerSession();

        endpoints
            .MapGet("/api/milestones/{milestoneId:guid}/drafts", ReadMilestoneDraftAsync)
            .RequireOwnerSession();

        endpoints
            .MapGet("/api/assets/{assetId:guid}", ReadAssetAsync)
            .RequireOwnerSession();

        return endpoints;
    }

    private static async Task<IResult> RequestDraftAsync(
        Guid milestoneId,
        HttpContext context,
        ContentDraftWireRequest? request,
        IContentDraftService drafts,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        if (request is null)
        {
            // An absent body is the same request with every field empty, which the use case then refuses
            // by name rather than being interpreted as a wildcard. A draft with no preview is a draft
            // with no consent.
            throw new ValidationFailedException(
                "previewId",
                "A draft is built from an authorized generation preview, so name the preview.");
        }

        var accepted = await drafts
            .RequestDraftAsync(
                owner,
                milestoneId,
                new ContentDraftRequest(
                    ParseId(request.PreviewId, "previewId"),
                    request.ConsentHash,
                    request.ProviderId,
                    request.Model,
                    request.EvidenceRevision,
                    request.VoiceRevision,
                    request.IdempotencyKey),
                cancellationToken)
            .ConfigureAwait(false);

        // 202 with the operation's own location: the work has been accepted and durably recorded, and
        // this is where its progress is read. A replay under the same idempotency key answers with the
        // same job and `created: false`, which is the same answer to this question and a different answer
        // to "did my click do anything".
        return Results.Json(
            new ContentDraftAcceptedResponse(
                accepted.Operation.Id.ToString("D"),
                accepted.OperationUrl,
                OperationStatuses.ToWire(accepted.Operation.Status),
                accepted.Created),
            statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> ReadMilestoneDraftAsync(
        Guid milestoneId,
        HttpContext context,
        IContentDraftService drafts,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        // "This milestone has no draft yet" is a normal state on a fresh installation, not a missing
        // resource, so the route answers 200 with a null asset rather than a 404. The milestone itself
        // is still read and still 404s when it is absent or in another scope, so the empty answer cannot
        // be used to probe for another owner's milestones.
        var view = await drafts
            .ReadAssetForMilestoneAsync(owner, milestoneId, cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(ContentAssetResponse.From(view));
    }

    private static async Task<IResult> ReadAssetAsync(
        Guid assetId,
        HttpContext context,
        IContentDraftService drafts,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var view = await drafts.ReadAssetAsync(owner, assetId, cancellationToken).ConfigureAwait(false);

        return Results.Json(ContentAssetResponse.From(view));
    }

    private static Guid ParseId(string? raw, string field)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw new ValidationFailedException(field, "This field is required.");
        }

        if (!Guid.TryParse(raw.Trim(), out var parsed))
        {
            // An id is an identifier, so a value that is not one is a malformed field. Nothing about the
            // value is echoed back.
            throw new ValidationFailedException(field, "This field is an identifier.");
        }

        return parsed;
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

/// <summary>
/// What an owner asked for when they asked a milestone to draft itself.
/// </summary>
/// <remarks>
/// Strings rather than parsed identifiers, because the route parses and refuses them by name. A
/// <c>Guid</c> binding would have the deserializer answer 400 for a malformed id where the rest of this
/// repository answers 422 with a field name an owner can act on.
/// </remarks>
internal sealed record ContentDraftWireRequest(
    string? PreviewId,
    string? ConsentHash,
    string? ProviderId,
    string? Model,
    long EvidenceRevision,
    long VoiceRevision,
    string? IdempotencyKey);

/// <summary>The 202 a draft request answers with.</summary>
internal sealed record ContentDraftAcceptedResponse(
    string OperationId,
    string OperationUrl,
    string Status,
    bool Created);

/// <summary>
/// One asset with its head revision, its claims and both kinds of finding.
/// </summary>
/// <remarks>
/// The body is published as a string with no HTML interpretation anywhere on this route, and the panel
/// that renders it is a preview: a draft is unapproved text, so it is shown as source and not as a
/// document. The design's "raw HTML disabled in preview" is a rendering decision, and the route's part
/// in it is that it hands over exactly the stored bytes and nothing interpreted.
/// </remarks>
internal sealed record ContentAssetResponse(
    Guid? AssetId,
    string? Type,
    string? Status,
    long? CurrentRevision,
    DateTimeOffset? UpdatedAt,
    string? Title,
    string? Summary,
    string? BodyMarkdown,
    string? BodyHash,
    Guid? EvidenceBundleId,
    long? EvidenceRevision,
    long? VoiceRevision,
    string? VoiceFingerprint,
    string? ProviderId,
    string? Model,
    string? PromptVersion,
    string? SchemaVersion,
    DateTimeOffset? CreatedAt,
    IReadOnlyList<ContentClaimResponse> Claims,
    IReadOnlyList<GroundingFindingResponse> GroundingFindings,
    IReadOnlyList<VoiceFindingResponse> VoiceFindings,
    string? MediaBaseUrl)
{
    public static ContentAssetResponse From(ContentAssetView? view)
    {
        if (view is null)
        {
            return new ContentAssetResponse(
                null, null, null, null, null, null, null, null, null, null, null, null, null, null,
                null, null, null, null, [], [], [], null);
        }

        var revision = view.Current;
        var summary = revision is null ? null : GroundedSummary.FromCanonicalJson(revision.SummaryJson);
        var voiceSnapshot = revision is null
            ? null
            : VoiceSnapshotCodec.Deserialize(revision.VoiceSnapshotJson);

        return new ContentAssetResponse(
            view.Asset.Id,
            view.Asset.Type,
            // Always `draft`, and read from the row rather than written here so the route has no way to
            // say anything else.
            view.Asset.Status,
            view.Asset.CurrentRevision,
            view.Asset.UpdatedAt,
            summary?.MilestoneTitle,
            summary?.Summary,
            revision?.BodyMarkdown,
            revision?.BodyHash,
            revision?.EvidenceBundleId,
            revision?.EvidenceRevision,
            revision?.VoiceRevision,
            voiceSnapshot?.Fingerprint,
            revision?.ProviderId,
            revision?.Model,
            revision?.PromptVersion,
            revision?.SchemaVersion,
            revision?.CreatedAt,
            [.. view.ClaimMap.Select(ContentClaimResponse.From)],
            [.. view.Findings.Select(GroundingFindingResponse.From)],
            [.. view.VoiceFindings.Select(VoiceFindingResponse.From)],
            null);
    }
}

/// <summary>One claim of the stored summary, under the footnote id the body cites it by.</summary>
internal sealed record ContentClaimResponse(
    string Id,
    string Section,
    int Index,
    string Text,
    IReadOnlyList<string> EvidenceIds)
{
    public static ContentClaimResponse From(DraftClaim claim) => new(
        claim.Id,
        GroundedSummary.SectionKey(claim.Section),
        claim.Index,
        claim.Text,
        [.. claim.EvidenceIds.Select(id => id.ToString("d"))]);
}

/// <summary>
/// One grounding finding, as a panel needs it.
/// </summary>
/// <remarks>
/// The same shape the delivered voice findings use - a rule, a field, a span, an excerpt and a message -
/// so a client renders both lists with one component. <c>evidenceId</c> is null for a finding that is
/// about a claim rather than about one specific item.
/// </remarks>
internal sealed record GroundingFindingResponse(
    string Rule,
    string Section,
    int Index,
    string? EvidenceId,
    string Excerpt,
    string Message)
{
    public static GroundingFindingResponse From(GroundingFinding finding) => new(
        finding.Rule,
        finding.Section,
        finding.Index,
        finding.EvidenceId?.ToString("d"),
        finding.Excerpt,
        finding.Message);
}

/// <summary>
/// One delivered voice finding, under the voice the stored draft was written with.
/// </summary>
/// <remarks>
/// The offsets are into the stored <c>body_markdown</c> for a body finding and into the stored title for
/// a title finding, which is the same convention the delivered voice draft route uses for its own two
/// fields. A client selects the span rather than searching for the excerpt.
/// </remarks>
internal sealed record VoiceFindingResponse(
    string Rule,
    string Field,
    int Start,
    int Length,
    string Excerpt,
    string Message)
{
    public static VoiceFindingResponse From(VoiceFinding finding) => new(
        finding.Rule,
        finding.Field,
        finding.Start,
        finding.Length,
        finding.Excerpt,
        finding.Message);
}
