using Chronicleaf.Application.Milestones;
using Chronicleaf.Core;

namespace Chronicleaf.Application.AgencyReports;

/// <summary>
/// What the owner asks for when generating a weekly report (AGENCY-WEEKLY-REPORTS-001/002).
/// </summary>
/// <remarks>
/// There is no provider id, no model and no consent hash here on purpose: a report is a
/// deterministic aggregation of confirmed milestones, evidence heads and the owner's own
/// words, so there is nothing to consent to and no key that could be leased. The only
/// free text is the two explicit note fields, because incomplete work must be user-labeled -
/// nothing is ever inferred from commit counts, and this service never sees one.
/// </remarks>
/// <param name="ProjectIds">The client projects to cover, explicitly named.</param>
/// <param name="WeekStart">The first local calendar date of the window (YYYY-MM-DD).</param>
/// <param name="TimeZone">The IANA time zone those calendar dates are read in.</param>
/// <param name="InProgressNotes">The owner's explicit in-progress notes, or null.</param>
/// <param name="NextWeekNotes">The owner's explicit next-week notes, or null.</param>
/// <param name="IdempotencyKey">The caller's key, or null for no replay.</param>
public sealed record GenerateWeeklyReportRequest(
    IReadOnlyList<Guid> ProjectIds,
    string WeekStart,
    string TimeZone,
    string? InProgressNotes,
    string? NextWeekNotes,
    string? IdempotencyKey);

/// <summary>One report row as the routes answer it.</summary>
public sealed record WeeklyReportView(
    Guid ReportId,
    Guid ClientId,
    string WeekStart,
    string TimeZone,
    DateTimeOffset WindowStartUtc,
    DateTimeOffset WindowEndUtc,
    long Revision,
    IReadOnlyList<Guid> ProjectIds,
    string AttributionHash,
    string SourceRefsJson,
    string BodyMarkdown,
    string BodyHash,
    bool Approved,
    DateTimeOffset CreatedAt)
{
    public static WeeklyReportView From(WeeklyReport report) =>
        new(
            report.Id,
            report.ClientId,
            report.WeekStart,
            report.TimeZone,
            report.WindowStartUtc,
            report.WindowEndUtc,
            report.Revision,
            report.ProjectIds,
            report.AttributionHash,
            report.SourceRefsJson,
            report.BodyMarkdown,
            report.BodyHash,
            report.IsApproved,
            report.CreatedAt);
}

/// <summary>One recorded approval, as the approve route answers it.</summary>
public sealed record WeeklyReportApprovalView(
    Guid ReportId,
    long Revision,
    string BodyHash,
    DateTimeOffset ApprovedAt);

/// <summary>One export, as the export route serves it.</summary>
public sealed record WeeklyReportExport(
    string FileName,
    string MediaType,
    byte[] Bytes,
    string Sha256);

/// <summary>
/// The agency weekly-report use cases over the delivered agency, milestone, evidence, media
/// and export contracts (AGENCY-WEEKLY-REPORTS-001/002).
/// </summary>
public interface IWeeklyReportService
{
    /// <summary>
    /// Renders the window from confirmed milestones, evidence heads and explicit owner notes,
    /// and stores it as the next revision for this client week.
    /// </summary>
    /// <exception cref="Common.ResourceNotFoundException">The client, or a named project, is not in the owner's scope.</exception>
    /// <exception cref="Agency.ClientArchivedException">The client is archived.</exception>
    /// <exception cref="WeeklyReportProjectOutsideClientException">A named project is not currently grouped under this client.</exception>
    /// <exception cref="Common.ValidationFailedException">The window, notes or key is malformed.</exception>
    /// <exception cref="Common.RevisionConflictException">A membership moved between the read and the write.</exception>
    /// <exception cref="WeeklyReportIdempotencyConflictException">The key already names different inputs.</exception>
    Task<WeeklyReportView> GenerateAsync(
        OwnerScope owner,
        Guid clientId,
        GenerateWeeklyReportRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one stored report row inside the client's scope.</summary>
    /// <exception cref="Common.ResourceNotFoundException">The client or the report is not in the owner's scope.</exception>
    Task<WeeklyReportView> GetAsync(
        OwnerScope owner,
        Guid clientId,
        Guid reportId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Binds the owner's approval to one stored revision, its body hash, its attribution hash
    /// and its source refs. Anything that moved since - a regrouped project, an advanced
    /// evidence head - refuses the approval as stale rather than binding it.
    /// </summary>
    /// <exception cref="Common.ResourceNotFoundException">The client or the report is not in the owner's scope.</exception>
    /// <exception cref="Common.RevisionConflictException">The named revision is not the stored one.</exception>
    /// <exception cref="Content.ApprovalStaleException">The attribution or the sources moved.</exception>
    Task<WeeklyReportApprovalView> ApproveAsync(
        OwnerScope owner,
        Guid clientId,
        Guid reportId,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Serves the approved revision's exact bytes as Markdown, or the same words through the
    /// shared sanitizing HTML template with the approval-covered pictures.
    /// </summary>
    /// <exception cref="Common.ResourceNotFoundException">The client or the report is not in the owner's scope.</exception>
    /// <exception cref="Content.ApprovalRequiredException">The revision was never approved.</exception>
    /// <exception cref="Content.ApprovalStaleException">The revision, attribution or sources moved since the approval.</exception>
    /// <exception cref="Common.ValidationFailedException">The format is not one this route serves.</exception>
    Task<WeeklyReportExport> ExportAsync(
        OwnerScope owner,
        Guid clientId,
        Guid reportId,
        string format,
        long? revision,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Chronicleaf-owned side of the weekly-reports table. Every statement carries the scope,
/// so a forged parameter cannot read across installations.
/// </summary>
public interface IWeeklyReportStore
{
    /// <summary>Reads one report row inside a scope, or null when it is absent or out of scope.</summary>
    Task<WeeklyReport?> FindAsync(
        Guid scopeId,
        Guid reportId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the newest row for one client week inside a scope, or null when never generated.</summary>
    Task<WeeklyReport?> FindLatestAsync(
        Guid scopeId,
        Guid clientId,
        string weekStart,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the row one idempotency key names inside a scope, or null.</summary>
    Task<WeeklyReport?> FindByIdempotencyKeyAsync(
        Guid scopeId,
        string idempotencyKey,
        CancellationToken cancellationToken = default);

    /// <summary>Inserts a report row. Returns false when the revision or the key already exists.</summary>
    Task<bool> TryInsertAsync(WeeklyReport report, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stamps the approval on one unapproved row revision. Returns 1 on the first stamp and 0
    /// when the row is absent, out of scope, a different revision or already approved.
    /// </summary>
    Task<int> TryApproveAsync(
        Guid scopeId,
        Guid reportId,
        long revision,
        string bodyHash,
        string attributionHash,
        string sourceRefsJson,
        DateTimeOffset approvedAt,
        CancellationToken cancellationToken = default);
}
