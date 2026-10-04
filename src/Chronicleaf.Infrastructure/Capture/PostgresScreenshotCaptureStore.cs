using System.Data.Common;
using Chronicleaf.Application.Capture;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.Capture;

/// <summary>
/// The PostgreSQL store for the two tables this package owns (SCREENSHOT-CAPTURE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The insert for a candidate is <c>ON CONFLICT DO NOTHING RETURNING</c> on the design's unique tuple
/// <c>(operation_id, source_url, viewport)</c>, so the duplicate decision is the database's and is made
/// atomically. A job that is retried, resumed or delivered twice therefore cannot write a second copy
/// of the same picture, and the second writer is told the row already exists rather than being told it
/// succeeded - which is what lets the service retire the media it had already stored instead of leaving
/// an orphan for the reconciler.
/// </para>
/// <para>
/// <b>Every statement carries the scope in its WHERE clause</b>, so a lookup by id can only ever return
/// a row inside the caller's own installation. Nothing here resolves a storage key: a candidate is
/// addressed by its media id and nothing more, and the delivered media store is the only thing in this
/// repository that turns an id into bytes.
/// </para>
/// </remarks>
public sealed class PostgresScreenshotCaptureStore(ChronicleafDbContext dbContext) :
    IScreenshotCandidateStore,
    ICaptureRequestStore
{
    private const string RequestColumns =
        """
        id, scope_id, operation_id, project_id, milestone_id, urls, viewports,
        wait_selector, wait_milliseconds, full_page, expected_bundle_revision, created_at
        """;

    private const string CandidateColumns =
        """
        id, scope_id, project_id, milestone_id, media_id, operation_id, source_url, final_url,
        viewport, captured_at, status, caption, is_primary, revision, created_at
        """;

    // -----------------------------------------------------------------------------------------
    // The request a job reads its work back from.
    // -----------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<bool> TryInsertAsync(Core.CaptureRequestRow row, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);

        var id = await ChronicleafSql.ScalarAsync<Guid?>(
            dbContext,
            """
            INSERT INTO capture_requests
                (id, scope_id, operation_id, project_id, milestone_id, urls, viewports,
                 wait_selector, wait_milliseconds, full_page, expected_bundle_revision, created_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11)
            ON CONFLICT (scope_id, operation_id) DO NOTHING
            RETURNING id
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, row.Id),
                new("@p1", NpgsqlDbType.Uuid, row.ScopeId),
                new("@p2", NpgsqlDbType.Uuid, row.OperationId),
                new("@p3", NpgsqlDbType.Uuid, row.ProjectId),
                new("@p4", NpgsqlDbType.Uuid, row.MilestoneId),
                new("@p5", NpgsqlDbType.Jsonb, CaptureColumnCodec.SerializeUrls(row.Urls)),
                new("@p6", NpgsqlDbType.Jsonb, CaptureColumnCodec.SerializeViewports(row.Viewports)),
                new("@p7", NpgsqlDbType.Varchar, row.WaitSelector is { } selector ? selector : DBNull.Value),
                new("@p8", NpgsqlDbType.Integer, row.WaitMilliseconds),
                new("@p9", NpgsqlDbType.Boolean, row.FullPage),
                new("@p10", NpgsqlDbType.Bigint, row.ExpectedBundleRevision),
                new("@p11", NpgsqlDbType.TimestampTz, row.CreatedAt.UtcDateTime),
            ],
            cancellationToken).ConfigureAwait(false);

        return id is not null;
    }

    /// <inheritdoc />
    public async Task<Core.CaptureRequestRow?> FindByOperationAsync(
        Guid scopeId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync(
            dbContext,
            $"SELECT {RequestColumns} FROM capture_requests WHERE scope_id = @p0 AND operation_id = @p1",
            [new("@p0", NpgsqlDbType.Uuid, scopeId), new("@p1", NpgsqlDbType.Uuid, operationId)],
            ReadRequest,
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    // -----------------------------------------------------------------------------------------
    // The candidates a job produces.
    // -----------------------------------------------------------------------------------------

    /// <inheritdoc />
    public async Task<bool> TryInsertAsync(ScreenshotCandidate candidate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        // The tuple conflict is the design's: one candidate per (operation, source url, viewport). The
        // four curation columns are written with the values the record holds, which are the constants
        // this package inserts - the columns exist for screenshot-curation to move, not for this
        // package to have opinions about.
        var id = await ChronicleafSql.ScalarAsync<Guid?>(
            dbContext,
            """
            INSERT INTO screenshot_candidates
                (id, scope_id, project_id, milestone_id, media_id, operation_id, source_url, final_url,
                 viewport, captured_at, status, caption, is_primary, revision, created_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12, @p13, @p14)
            ON CONFLICT (operation_id, source_url, viewport) DO NOTHING
            RETURNING id
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, candidate.Id),
                new("@p1", NpgsqlDbType.Uuid, candidate.ScopeId),
                new("@p2", NpgsqlDbType.Uuid, candidate.ProjectId),
                new("@p3", NpgsqlDbType.Uuid, candidate.MilestoneId),
                new("@p4", NpgsqlDbType.Uuid, candidate.MediaId),
                new("@p5", NpgsqlDbType.Uuid, candidate.OperationId),
                new("@p6", NpgsqlDbType.Varchar, candidate.SourceUrl),
                new("@p7", NpgsqlDbType.Varchar, candidate.FinalUrl),
                new("@p8", NpgsqlDbType.Jsonb, CaptureColumnCodec.SerializeViewport(candidate.Viewport)),
                new("@p9", NpgsqlDbType.TimestampTz, candidate.CapturedAt.UtcDateTime),
                new("@p10", NpgsqlDbType.Varchar, candidate.Status),
                new("@p11", NpgsqlDbType.Varchar, candidate.Caption),
                new("@p12", NpgsqlDbType.Boolean, candidate.IsPrimary),
                new("@p13", NpgsqlDbType.Bigint, candidate.Revision),
                new("@p14", NpgsqlDbType.TimestampTz, candidate.CreatedAt.UtcDateTime),
            ],
            cancellationToken).ConfigureAwait(false);

        return id is not null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ScreenshotCandidate>> ListAsync(
        Guid scopeId,
        Guid milestoneId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        // Newest first, with the id as the tie-break so two rows captured in the same millisecond still
        // come back in a stable order and a caller paging through them never sees one twice.
        return await ChronicleafSql.QueryAsync(
            dbContext,
            $"""
             SELECT {CandidateColumns} FROM screenshot_candidates
             WHERE scope_id = @p0 AND milestone_id = @p1
             ORDER BY created_at DESC, id DESC
             LIMIT @p2
             """,
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, milestoneId),
                new("@p2", NpgsqlDbType.Integer, limit),
            ],
            ReadCandidate,
            cancellationToken).ConfigureAwait(false);
    }

    private static Core.CaptureRequestRow ReadRequest(DbDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetGuid(3),
            reader.GetGuid(4),
            CaptureColumnCodec.DeserializeUrls(reader.GetString(5)),
            CaptureColumnCodec.DeserializeViewports(reader.GetString(6)),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetInt32(8),
            reader.GetBoolean(9),
            reader.GetInt64(10),
            reader.GetFieldValue<DateTimeOffset>(11));

    private static ScreenshotCandidate ReadCandidate(DbDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetGuid(3),
            reader.GetGuid(4),
            reader.GetGuid(5),
            reader.GetString(6),
            reader.GetString(7),
            CaptureColumnCodec.DeserializeViewport(reader.GetString(8)) ?? CaptureViewport.Desktop,
            reader.GetFieldValue<DateTimeOffset>(9),
            reader.GetFieldValue<DateTimeOffset>(14));
}
