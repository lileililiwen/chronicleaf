using System.Data.Common;
using Chronicleaf.Application.AgencyReports;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.AgencyReports;

/// <summary>
/// The weekly report rows in PostgreSQL (AGENCY-WEEKLY-REPORTS-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Every statement is parameterized and every one carries the scope in its <c>WHERE</c> clause, so
/// a forged parameter cannot read across installations. Rows are inserted and never updated except
/// by the approval stamp, which moves only an unapproved row: a regeneration is a new row at the
/// next revision, so history is never rewritten.
/// </para>
/// <para>
/// The per-client-week revision and the idempotency key are enforced by the database, so two
/// generations that race each other still produce ordered revisions and one key still names one
/// report.
/// </para>
/// </remarks>
public sealed class PostgresWeeklyReportStore(ChronicleafDbContext dbContext) : IWeeklyReportStore
{
    private const string Columns =
        "id, scope_id, client_id, week_start, time_zone, window_start_utc, window_end_utc, revision, "
        + "project_ids, attribution_hash, source_refs, inputs_hash, idempotency_key, body_markdown, "
        + "body_hash, in_progress_notes, next_week_notes, approved_revision, approved_body_hash, "
        + "approved_attribution_hash, approved_source_refs, approved_at, created_at";

    /// <inheritdoc />
    public async Task<WeeklyReport?> FindAsync(
        Guid scopeId,
        Guid reportId,
        CancellationToken cancellationToken = default) =>
        (await ChronicleafSql.QueryAsync(
            dbContext,
            $"SELECT {Columns} FROM weekly_reports WHERE scope_id = @p0 AND id = @p1",
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, reportId),
            ],
            ReadReport,
            cancellationToken).ConfigureAwait(false)).SingleOrDefault();

    /// <inheritdoc />
    public async Task<WeeklyReport?> FindLatestAsync(
        Guid scopeId,
        Guid clientId,
        string weekStart,
        CancellationToken cancellationToken = default) =>
        (await ChronicleafSql.QueryAsync(
            dbContext,
            $"SELECT {Columns} FROM weekly_reports "
            + "WHERE scope_id = @p0 AND client_id = @p1 AND week_start = @p2 "
            + "ORDER BY revision DESC LIMIT 1",
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, clientId),
                new("@p2", NpgsqlDbType.Varchar, weekStart),
            ],
            ReadReport,
            cancellationToken).ConfigureAwait(false)).SingleOrDefault();

    /// <inheritdoc />
    public async Task<WeeklyReport?> FindByIdempotencyKeyAsync(
        Guid scopeId,
        string idempotencyKey,
        CancellationToken cancellationToken = default) =>
        (await ChronicleafSql.QueryAsync(
            dbContext,
            $"SELECT {Columns} FROM weekly_reports WHERE scope_id = @p0 AND idempotency_key = @p1",
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Varchar, idempotencyKey),
            ],
            ReadReport,
            cancellationToken).ConfigureAwait(false)).SingleOrDefault();

    /// <inheritdoc />
    public async Task<bool> TryInsertAsync(WeeklyReport report, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        try
        {
            await ChronicleafSql.ExecuteAsync(
                dbContext,
                "INSERT INTO weekly_reports "
                + "(id, scope_id, client_id, week_start, time_zone, window_start_utc, window_end_utc, "
                + "revision, project_ids, attribution_hash, source_refs, inputs_hash, idempotency_key, "
                + "body_markdown, body_hash, in_progress_notes, next_week_notes, "
                + "approved_revision, approved_body_hash, approved_attribution_hash, "
                + "approved_source_refs, approved_at, created_at) "
                + "VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12, "
                + "@p13, @p14, @p15, @p16, NULL, NULL, NULL, NULL, NULL, @p17)",
                [
                    new("@p0", NpgsqlDbType.Uuid, report.Id),
                    new("@p1", NpgsqlDbType.Uuid, report.ScopeId),
                    new("@p2", NpgsqlDbType.Uuid, report.ClientId),
                    new("@p3", NpgsqlDbType.Varchar, report.WeekStart),
                    new("@p4", NpgsqlDbType.Varchar, report.TimeZone),
                    new("@p5", NpgsqlDbType.TimestampTz, report.WindowStartUtc),
                    new("@p6", NpgsqlDbType.TimestampTz, report.WindowEndUtc),
                    new("@p7", NpgsqlDbType.Bigint, report.Revision),
                    new("@p8", NpgsqlDbType.Array | NpgsqlDbType.Uuid, report.ProjectIds.ToArray()),
                    new("@p9", NpgsqlDbType.Varchar, report.AttributionHash),
                    new("@p10", NpgsqlDbType.Text, report.SourceRefsJson),
                    new("@p11", NpgsqlDbType.Varchar, report.InputsHash),
                    new("@p12", NpgsqlDbType.Varchar, (object?)report.IdempotencyKey ?? DBNull.Value),
                    new("@p13", NpgsqlDbType.Text, report.BodyMarkdown),
                    new("@p14", NpgsqlDbType.Varchar, report.BodyHash),
                    new("@p15", NpgsqlDbType.Text, report.InProgressNotes),
                    new("@p16", NpgsqlDbType.Text, report.NextWeekNotes),
                    new("@p17", NpgsqlDbType.TimestampTz, report.CreatedAt),
                ],
                cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (DbException ex) when (IsUniqueViolation(ex))
        {
            return false;
        }
    }

    /// <inheritdoc />
    public Task<int> TryApproveAsync(
        Guid scopeId,
        Guid reportId,
        long revision,
        string bodyHash,
        string attributionHash,
        string sourceRefsJson,
        DateTimeOffset approvedAt,
        CancellationToken cancellationToken = default) =>
        ChronicleafSql.ExecuteAsync(
            dbContext,
            "UPDATE weekly_reports SET approved_revision = @p3, approved_body_hash = @p4, "
            + "approved_attribution_hash = @p5, approved_source_refs = @p6, approved_at = @p7 "
            + "WHERE scope_id = @p0 AND id = @p1 AND revision = @p2 AND approved_revision IS NULL",
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, reportId),
                new("@p2", NpgsqlDbType.Bigint, revision),
                new("@p3", NpgsqlDbType.Bigint, revision),
                new("@p4", NpgsqlDbType.Varchar, bodyHash),
                new("@p5", NpgsqlDbType.Varchar, attributionHash),
                new("@p6", NpgsqlDbType.Text, sourceRefsJson),
                new("@p7", NpgsqlDbType.TimestampTz, approvedAt),
            ],
            cancellationToken);

    private static WeeklyReport ReadReport(DbDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetInt64(7),
            (Guid[])reader.GetValue(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.GetString(13),
            reader.GetString(14),
            reader.GetString(15),
            reader.GetString(16),
            reader.IsDBNull(17) ? null : reader.GetInt64(17),
            reader.IsDBNull(18) ? null : reader.GetString(18),
            reader.IsDBNull(19) ? null : reader.GetString(19),
            reader.IsDBNull(20) ? null : reader.GetString(20),
            reader.IsDBNull(21) ? null : reader.GetFieldValue<DateTimeOffset>(21),
            reader.GetFieldValue<DateTimeOffset>(22));

    private static bool IsUniqueViolation(DbException exception) =>
        exception is Npgsql.NpgsqlException { SqlState: "23505" };
}
