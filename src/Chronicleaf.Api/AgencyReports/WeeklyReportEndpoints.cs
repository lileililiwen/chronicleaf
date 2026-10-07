using System.Globalization;
using System.Text.Json.Serialization;
using Chronicleaf.Api.Access;
using Chronicleaf.Api.Agency;
using Chronicleaf.Application.Access;
using Chronicleaf.Application.AgencyReports;
using Chronicleaf.Application.Milestones;

namespace Chronicleaf.Api.AgencyReports;

/// <summary>
/// The private agency weekly-report routes (AGENCY-WEEKLY-REPORTS-001/002).
/// </summary>
/// <remarks>
/// Every route here is private, and that is the access package's default rather than something
/// these handlers re-check. The report is an artifact the owner acts on: there is no client
/// portal, no invoicing and no email sending anywhere in this package, and no route here takes
/// a recipient, a price or a send flag. Response field names are pinned explicitly so the
/// contract cannot drift with serializer policy.
/// </remarks>
public static class WeeklyReportEndpoints
{
    public static IEndpointRouteBuilder MapAgencyWeeklyReportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/api/clients/{clientId:guid}/weekly-reports", GenerateAsync).RequireOwnerSession();
        endpoints.MapGet("/api/clients/{clientId:guid}/weekly-reports/{reportId:guid}", GetAsync).RequireOwnerSession();
        endpoints.MapPost("/api/clients/{clientId:guid}/weekly-reports/{reportId:guid}/approve", ApproveAsync).RequireOwnerSession();
        endpoints.MapGet("/api/clients/{clientId:guid}/weekly-reports/{reportId:guid}/export", ExportAsync).RequireOwnerSession();

        return endpoints;
    }

    private static async Task<IResult> GenerateAsync(
        Guid clientId,
        HttpContext context,
        GenerateWeeklyReportBody? body,
        IWeeklyReportService reports,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var request = body ?? new GenerateWeeklyReportBody(null, null, null, null, null, null);

        var report = await reports.GenerateAsync(
            owner,
            clientId,
            new GenerateWeeklyReportRequest(
                request.ProjectIds ?? [],
                request.WeekStart ?? string.Empty,
                request.TimeZone ?? string.Empty,
                request.InProgressNotes,
                request.NextWeekNotes,
                request.IdempotencyKey),
            cancellationToken).ConfigureAwait(false);

        AgencyClientMapping.PublishRevision(context, report.Revision);

        return Results.Created(
            $"/api/clients/{clientId:D}/weekly-reports/{report.ReportId:D}",
            WeeklyReportResponse.From(report));
    }

    private static async Task<IResult> GetAsync(
        Guid clientId,
        Guid reportId,
        HttpContext context,
        IWeeklyReportService reports,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var report = await reports.GetAsync(owner, clientId, reportId, cancellationToken).ConfigureAwait(false);

        AgencyClientMapping.PublishRevision(context, report.Revision);

        return Results.Json(WeeklyReportResponse.From(report));
    }

    private static async Task<IResult> ApproveAsync(
        Guid clientId,
        Guid reportId,
        HttpContext context,
        IWeeklyReportService reports,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var approval = await reports.ApproveAsync(
            owner,
            clientId,
            reportId,
            AgencyClientMapping.RequiredRevision(context, "weekly report"),
            cancellationToken).ConfigureAwait(false);

        return Results.Json(WeeklyReportApprovalResponse.From(approval));
    }

    private static async Task<IResult> ExportAsync(
        Guid clientId,
        Guid reportId,
        HttpContext context,
        IWeeklyReportService reports,
        IOwnerAccessService access,
        CancellationToken cancellationToken)
    {
        var owner = await OwnerScopeAsync(context, access, cancellationToken).ConfigureAwait(false);

        var format = context.Request.Query["format"].ToString();
        var revisionRaw = context.Request.Query["revision"].ToString();

        long? revision = null;

        if (revisionRaw.Length > 0)
        {
            if (!long.TryParse(revisionRaw, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
                || parsed < 1)
            {
                throw new Application.Common.ValidationFailedException(
                    "revision",
                    "The revision must be a positive integer.");
            }

            revision = parsed;
        }

        var export = await reports.ExportAsync(
            owner,
            clientId,
            reportId,
            format,
            revision,
            cancellationToken).ConfigureAwait(false);

        return Results.File(export.Bytes, export.MediaType, export.FileName);
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
/// The <c>POST /api/clients/{id}/weekly-reports</c> body: the explicit project set, the local
/// week, the owner's own notes and the replay key. No provider, no model, no recipient.
/// </summary>
public sealed record GenerateWeeklyReportBody(
    [property: JsonPropertyName("projectIds")] IReadOnlyList<Guid>? ProjectIds,
    [property: JsonPropertyName("weekStart")] string? WeekStart,
    [property: JsonPropertyName("timeZone")] string? TimeZone,
    [property: JsonPropertyName("inProgressNotes")] string? InProgressNotes,
    [property: JsonPropertyName("nextWeekNotes")] string? NextWeekNotes,
    [property: JsonPropertyName("idempotencyKey")] string? IdempotencyKey);

/// <summary>One report row as the routes answer it.</summary>
public sealed record WeeklyReportResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("clientId")] string ClientId,
    [property: JsonPropertyName("weekStart")] string WeekStart,
    [property: JsonPropertyName("timeZone")] string TimeZone,
    [property: JsonPropertyName("windowStartUtc")] string WindowStartUtc,
    [property: JsonPropertyName("windowEndUtc")] string WindowEndUtc,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("projectIds")] IReadOnlyList<string> ProjectIds,
    [property: JsonPropertyName("attributionHash")] string AttributionHash,
    [property: JsonPropertyName("sourceRefs")] string SourceRefs,
    [property: JsonPropertyName("bodyMarkdown")] string BodyMarkdown,
    [property: JsonPropertyName("bodyHash")] string BodyHash,
    [property: JsonPropertyName("approved")] bool Approved)
{
    public static WeeklyReportResponse From(WeeklyReportView report) =>
        new(
            report.ReportId.ToString("D"),
            report.ClientId.ToString("D"),
            report.WeekStart,
            report.TimeZone,
            report.WindowStartUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            report.WindowEndUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            report.Revision,
            [.. report.ProjectIds.Select(id => id.ToString("D"))],
            report.AttributionHash,
            report.SourceRefsJson,
            report.BodyMarkdown,
            report.BodyHash,
            report.Approved);
}

/// <summary>One recorded approval, as the approve route answers it.</summary>
public sealed record WeeklyReportApprovalResponse(
    [property: JsonPropertyName("reportId")] string ReportId,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("bodyHash")] string BodyHash,
    [property: JsonPropertyName("approvedAt")] string ApprovedAt)
{
    public static WeeklyReportApprovalResponse From(WeeklyReportApprovalView approval) =>
        new(
            approval.ReportId.ToString("D"),
            approval.Revision,
            approval.BodyHash,
            approval.ApprovedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
}
