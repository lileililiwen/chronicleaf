using Chronicleaf.Application.Common;

namespace Chronicleaf.Application.AgencyReports;

/// <summary>The failure codes this package adds. They extend the envelope; they do not change it.</summary>
public static class WeeklyReportError
{
    /// <summary>
    /// A named project is not currently grouped under this client. Nothing was rendered and no
    /// byte reached any model, because there is no model behind a report to redact after.
    /// </summary>
    public const string ProjectOutsideClient = "weekly_report_project_outside_client";

    /// <summary>
    /// The idempotency key already names a report built from different inputs. Replaying the
    /// stored report would answer a different question, and building a second row would fork
    /// one key into two reports.
    /// </summary>
    public const string IdempotencyConflict = "weekly_report_idempotency_conflict";
}

/// <summary>
/// A named project is not currently grouped under this client (422). The request changed
/// nothing; the other client's projects are unnamed by the refusal.
/// </summary>
public sealed class WeeklyReportProjectOutsideClientException()
    : ApiException(
        WeeklyReportError.ProjectOutsideClient,
        "Every reported project must currently belong to this client.")
{
}

/// <summary>
/// The idempotency key already names a report built from different inputs (409). The request
/// changed nothing; the stored report is untouched.
/// </summary>
public sealed class WeeklyReportIdempotencyConflictException()
    : ApiException(
        WeeklyReportError.IdempotencyConflict,
        "The idempotency key already names a weekly report built from different inputs.")
{
}
