using System.Globalization;
using System.Text;
using Chronicleaf.Api.Access;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Content;
using Chronicleaf.Application.LinkedIn;
using Chronicleaf.Application.Medium;
using Chronicleaf.Application.Milestones;

namespace Chronicleaf.Api.Content;

/// <summary>
/// The private review routes (MARKDOWN-REVIEW-EXPORT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Five routes, all private: the public allow-list is untouched and every route carries
/// <c>RequireOwnerSession()</c>, so <c>/api/health</c> and <c>/api/ready</c> remain the only public
/// surface. A review is readable only by the owner it belongs to and by nobody else, and an export
/// is an attachment served to the owner that approved it - never a public page and never an
/// upstream call.
/// </para>
/// <para>
/// <b>The edit is conditional.</b> If-Match carries the head revision the owner read and the body
/// carries the same value, and both must be current: an edit against a stale head would fork the
/// text the approval was bound to. A missing If-Match is 428, a stale revision of either kind is
/// 409, and a body that disagrees with the header is 422 - the same rule the delivered curation
/// route enforces, so two answers to one question never reach the service.
/// </para>
/// <para>
/// <b>The approve button is never triggered by Enter.</b> Approval is a POST with an explicit body
/// naming both revisions; there is no form whose default submission could approve, and the panel
/// renders its approve control as a button that only a press can reach.
/// </para>
/// <para>
/// <b>HTML is the same approval in a second shape.</b> <c>format=html</c> on the export route
/// re-checks every binding the Markdown export re-checks - never approved is
/// <c>approval_required</c>, once approved but moved is <c>approval_stale</c> - and then renders
/// the approved bytes through the fixed sanitizing template: a single HTML file, or a ZIP with
/// the approval-covered pictures and their checksum manifest. A picture whose bytes are gone is
/// <c>media_unavailable</c>, never a silently picture-less file.
/// </para>
/// </remarks>
public static class ContentReviewEndpoints
{
    public static IEndpointRouteBuilder MapContentReviewEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints
            .MapPatch("/api/assets/{assetId:guid}", EditAssetAsync)
            .RequireOwnerSession();

        endpoints
            .MapPost("/api/assets/{assetId:guid}/approve", ApproveAssetAsync)
            .RequireOwnerSession();

        endpoints
            .MapPost("/api/assets/{assetId:guid}/unapprove", UnapproveAssetAsync)
            .RequireOwnerSession();

        endpoints
            .MapGet("/api/assets/{assetId:guid}/review", ReadReviewAsync)
            .RequireOwnerSession();

        endpoints
            .MapGet("/api/assets/{assetId:guid}/export", ExportAssetAsync)
            .RequireOwnerSession();

        return endpoints;
    }

    private static async Task<IResult> EditAssetAsync(
        Guid assetId,
        HttpContext context,
        EditContentBody? request,
        IContentReviewService reviews,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        if (request is null)
        {
            throw new ValidationFailedException(
                "bodyMarkdown",
                "An edited body must carry text.");
        }

        var presented = RequiredContentRevision(context, request.ExpectedRevision);

        var edited = await reviews
            .EditAsync(
                owner,
                assetId,
                presented,
                new EditContentRequest(request.ExpectedRevision ?? presented, request.BodyMarkdown, request.Title),
                cancellationToken)
            .ConfigureAwait(false);

        context.Response.Headers.ETag = $"\"{edited.Revision.ToString(CultureInfo.InvariantCulture)}\"";

        return Results.Json(new EditedContentResponse(
            edited.AssetId,
            edited.Revision,
            edited.BodyHash));
    }

    private static async Task<IResult> ApproveAssetAsync(
        Guid assetId,
        HttpContext context,
        ApproveContentBody? request,
        IContentReviewService reviews,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        if (request is null)
        {
            throw new ValidationFailedException(
                "contentRevision",
                "An approval names a stored revision, so name one.");
        }

        var approval = await reviews
            .ApproveAsync(
                owner,
                assetId,
                new ApproveContentRequest(
                    request.ContentRevisionValue,
                    request.EvidenceRevisionValue,
                    request.VoiceFindingsAcknowledged ?? false),
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(ContentApprovalResponse.From(approval));
    }

    private static async Task<IResult> UnapproveAssetAsync(
        Guid assetId,
        HttpContext context,
        UnapproveContentBody? request,
        IContentReviewService reviews,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var approval = await reviews
            .UnapproveAsync(owner, assetId, request?.Reason, cancellationToken)
            .ConfigureAwait(false);

        return Results.Json(ContentApprovalResponse.From(approval));
    }

    private static async Task<IResult> ReadReviewAsync(
        Guid assetId,
        HttpContext context,
        IContentReviewService reviews,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var view = await reviews.ReadReviewAsync(owner, assetId, cancellationToken).ConfigureAwait(false);

        context.Response.Headers.ETag = $"\"{view.CurrentRevision.ToString(CultureInfo.InvariantCulture)}\"";

        return Results.Json(ContentReviewResponse.From(view));
    }

    private static async Task<IResult> ExportAssetAsync(
        Guid assetId,
        HttpContext context,
        IContentReviewService reviews,
        ILinkedInCopyService copies,
        IMediumImportService imports,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var format = context.Request.Query["format"].FirstOrDefault();
        var revisionText = context.Request.Query["revision"].FirstOrDefault();

        // HTML is the same approval in a second shape, served by its own use case: the Markdown
        // export below refuses any format but its own, so the branch happens here rather than
        // behind a parameter neither shape owns.
        if (string.Equals((format ?? "markdown").Trim(), "html", StringComparison.OrdinalIgnoreCase))
        {
            return await ExportHtmlAsync(assetId, context, reviews, access, cancellationToken)
                .ConfigureAwait(false);
        }

        // LINKEDIN-COPY-PACKAGE-002. The LinkedIn package is the same approval in a portable
        // shape: copy.txt, the selected pictures and their manifest, served by its own use case
        // under the same bindings the Markdown export re-checks. Preview and approval stay the
        // review package's own routes; this branch only serves what they authorized.
        if (string.Equals((format ?? "markdown").Trim(), "linkedin", StringComparison.OrdinalIgnoreCase))
        {
            return await ExportLinkedInAsync(assetId, revisionText, context, copies, access, cancellationToken)
                .ConfigureAwait(false);
        }

        // MEDIUM-IMPORT-PACKAGE-001/002. The Medium package is the same approval in a manual
        // import shape: article.html, article.md, the approval-covered pictures, their manifest
        // and the hand-carry instructions, served by its own use case under the same bindings
        // the Markdown export re-checks. Preview and approval stay the review package's own
        // routes; this branch only serves what they authorized.
        if (string.Equals((format ?? "markdown").Trim(), "medium", StringComparison.OrdinalIgnoreCase))
        {
            return await ExportMediumAsync(assetId, revisionText, context, imports, access, cancellationToken)
                .ConfigureAwait(false);
        }

        long? revision = null;
        if (revisionText is not null)
        {
            if (!long.TryParse(revisionText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
                parsed < 1)
            {
                throw new ValidationFailedException(
                    "revision",
                    "A revision is a stored version number, from 1.");
            }

            revision = parsed;
        }

        var export = await reviews
            .ExportAsync(owner, assetId, format, revision, cancellationToken)
            .ConfigureAwait(false);

        // The exact approved bytes, as an attachment the owner chose to download. The content
        // type is Markdown, never HTML: a body that carries a script tag or an unsafe link is
        // served as text the browser saves rather than as a document it runs, which is what
        // keeps unsafe markup inert on the way out as well as in the preview.
        return Results.File(
            Encoding.UTF8.GetBytes(export.BodyMarkdown),
            "text/markdown; charset=utf-8",
            export.FileName);
    }

    private static async Task<IResult> ExportHtmlAsync(
        Guid assetId,
        HttpContext context,
        IContentReviewService reviews,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var revisionText = context.Request.Query["revision"].FirstOrDefault();

        long? revision = null;
        if (revisionText is not null)
        {
            if (!long.TryParse(revisionText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
                parsed < 1)
            {
                throw new ValidationFailedException(
                    "revision",
                    "A revision is a stored version number, from 1.");
            }

            revision = parsed;
        }

        var export = await reviews
            .ExportHtmlAsync(owner, assetId, revision, cancellationToken)
            .ConfigureAwait(false);

        // The approved revision rendered through the fixed sanitizing template, as an attachment
        // the owner chose to download: a single HTML file, or a ZIP holding the HTML, the
        // approval-covered pictures and their checksum manifest. Never a public page and never an
        // upstream call, under the same approval the Markdown export re-checks.
        return Results.File(export.Bytes, export.ContentType, export.FileName);
    }

    private static async Task<IResult> ExportLinkedInAsync(
        Guid assetId,
        string? revisionText,
        HttpContext context,
        ILinkedInCopyService copies,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        long? revision = null;
        if (revisionText is not null)
        {
            if (!long.TryParse(revisionText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
                parsed < 1)
            {
                throw new ValidationFailedException(
                    "revision",
                    "A revision is a stored version number, from 1.");
            }

            revision = parsed;
        }

        var export = await copies
            .ExportAsync(owner, assetId, revision, cancellationToken)
            .ConfigureAwait(false);

        // The approved copy as a package the owner chose to download: copy.txt, the selected
        // pictures and their manifest. Never a public page and never an upstream call, under
        // the same approval the Markdown export re-checks.
        return Results.File(export.Bytes, export.ContentType, export.FileName);
    }

    private static async Task<IResult> ExportMediumAsync(
        Guid assetId,
        string? revisionText,
        HttpContext context,
        IMediumImportService imports,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        long? revision = null;
        if (revisionText is not null)
        {
            if (!long.TryParse(revisionText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
                parsed < 1)
            {
                throw new ValidationFailedException(
                    "revision",
                    "A revision is a stored version number, from 1.");
            }

            revision = parsed;
        }

        var export = await imports
            .ExportAsync(owner, assetId, revision, cancellationToken)
            .ConfigureAwait(false);

        // The approved article as a manual import package the owner chose to download:
        // article.html, article.md, the approval-covered pictures, their manifest and the
        // hand-carry instructions. Never a public page and never an upstream call, under
        // the same approval the Markdown export re-checks.
        return Results.File(export.Bytes, export.ContentType, export.FileName);
    }

    /// <summary>
    /// Reads the head revision an edit is conditional on. The header is the precondition, and the
    /// body's <c>expectedRevision</c> has to agree with it - the same rule the delivered evidence
    /// and curation routes enforce, so two answers to one question never reach the service.
    /// </summary>
    private static long RequiredContentRevision(HttpContext context, long? bodyRevision)
    {
        var presented = context.Request.Headers.IfMatch.ToString().Trim();

        if (presented.Length == 0)
        {
            throw new IfMatchRequiredException("content asset");
        }

        if (presented.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            presented = presented[2..];
        }

        presented = presented.Trim().Trim('"');

        if (!long.TryParse(presented, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) ||
            revision < 0)
        {
            throw new ValidationFailedException(
                "If-Match",
                "If-Match must be the revision you read, as a decimal number, optionally quoted or weakly marked.");
        }

        if (bodyRevision is not null && bodyRevision.Value != revision)
        {
            throw new ValidationFailedException(
                "expectedRevision",
                "The expected revision must be the one presented in If-Match.");
        }

        return revision;
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
/// What an owner sends when they edit a draft's text.
/// </summary>
/// <remarks>
/// Longs rather than parsed headers, because the route parses and refuses them by name. A missing
/// body is a 422 naming the field an owner can act on rather than a deserializer's 400.
/// </remarks>
internal sealed record EditContentBody(
    long? ExpectedRevision,
    string? BodyMarkdown,
    string? Title);

/// <summary>What an owner sends when they approve a revision.</summary>
internal sealed record ApproveContentBody(
    long? ContentRevision,
    long? EvidenceRevision,
    bool? VoiceFindingsAcknowledged)
{
    /// <summary>The content revision, refused by name when absent rather than defaulted.</summary>
    public long ContentRevisionValue => ContentRevision
        ?? throw new ValidationFailedException("contentRevision", "An approval names a stored revision, so name one.");

    /// <summary>The evidence revision, refused by name when absent rather than defaulted.</summary>
    public long EvidenceRevisionValue => EvidenceRevision
        ?? throw new ValidationFailedException(
            "evidenceRevision",
            "An approval names the evidence head it was bound to, so name one.");
}

/// <summary>What an owner sends when they withdraw an approval.</summary>
internal sealed record UnapproveContentBody(string? Reason);

/// <summary>One edited revision, as the edit route answers it.</summary>
internal sealed record EditedContentResponse(
    Guid AssetId,
    long Revision,
    string BodyHash);

/// <summary>One recorded approval.</summary>
/// <remarks>
/// The binding in full: who decided, what text, and which evidence, voice and pictures it was
/// bound to. A reader deciding whether an export is still authorized can see every value the
/// export re-checks.
/// </remarks>
internal sealed record ContentApprovalResponse(
    Guid ApprovalId,
    Guid AssetId,
    long ContentRevision,
    string PayloadKind,
    string BodyHash,
    long EvidenceRevision,
    DateTimeOffset ApprovedAt)
{
    public static ContentApprovalResponse From(ContentApprovalRecord approval) => new(
        approval.ApprovalId,
        approval.AssetId,
        approval.ContentRevision,
        approval.PayloadKind,
        approval.BodyHash,
        approval.EvidenceRevision,
        approval.ApprovedAt);
}

/// <summary>The review state of one asset.</summary>
internal sealed record ContentReviewResponse(
    Guid AssetId,
    long CurrentRevision,
    string ReviewStatus,
    ContentApprovalResponse? Approval)
{
    public static ContentReviewResponse From(ContentReviewView view) => new(
        view.AssetId,
        view.CurrentRevision,
        view.ReviewStatus,
        view.Approval is null ? null : ContentApprovalResponse.From(view.Approval));
}
