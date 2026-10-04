using System.Data.Common;
using Chronicleaf.Application.Curation;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.Curation;

/// <summary>
/// The PostgreSQL store for the curation columns of the candidate table
/// (SCREENSHOT-CURATION-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The table is capture's; these statements move only the columns curation owns - status, caption,
/// the primary flag, the display order and the revision. Capture's own columns are never written
/// here, so a curation can no more re-point a picture at other bytes than a capture can select one.
/// </para>
/// <para>
/// <b>Every statement carries the scope in its WHERE clause</b>, so a lookup by id can only ever
/// return a row inside the caller's own installation. The milestone lock is a row lock on the
/// milestone, taken before any curation write, so two curations of one milestone serialize on the
/// row instead of racing on the one-primary index.
/// </para>
/// </remarks>
public sealed class PostgresScreenshotCurationStore(ChronicleafDbContext dbContext) : IScreenshotCurationStore
{
    private const string CurationColumns =
        """
        id, scope_id, project_id, milestone_id, media_id, operation_id, source_url, final_url,
        viewport, captured_at, status, caption, is_primary, display_order, revision, created_at
        """;

    /// <inheritdoc />
    public async Task<bool> LockMilestoneAsync(
        Guid scopeId,
        Guid milestoneId,
        CancellationToken cancellationToken = default)
    {
        // Milestones carry no scope of their own: the scope arrives through the project, so the
        // lock joins it. The milestone row is the one locked, and it serializes every curation of
        // that milestone inside the caller's transaction.
        var id = await ChronicleafSql.ScalarAsync<Guid?>(
            dbContext,
            """
            SELECT m.id FROM milestones AS m
            INNER JOIN projects AS p ON p.id = m.project_id
            WHERE p.scope_id = @p0 AND m.id = @p1
            FOR UPDATE OF m
            """,
            [new("@p0", NpgsqlDbType.Uuid, scopeId), new("@p1", NpgsqlDbType.Uuid, milestoneId)],
            cancellationToken).ConfigureAwait(false);

        return id is not null;
    }

    /// <inheritdoc />
    public async Task<CuratedScreenshot?> FindAsync(
        Guid scopeId,
        Guid screenshotId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync(
            dbContext,
            $"SELECT {CurationColumns} FROM screenshot_candidates WHERE scope_id = @p0 AND id = @p1",
            [new("@p0", NpgsqlDbType.Uuid, scopeId), new("@p1", NpgsqlDbType.Uuid, screenshotId)],
            ReadCurated,
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CuratedScreenshot>> ListAsync(
        Guid scopeId,
        Guid milestoneId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        // Newest first, with the id as the tie-break - the same order the capture list publishes,
        // so the two readers never disagree about what "the pictures" are. The curated sequence
        // among the selected pictures is display_order, which the picker applies itself.
        return await ChronicleafSql.QueryAsync(
            dbContext,
            $"""
             SELECT {CurationColumns} FROM screenshot_candidates
             WHERE scope_id = @p0 AND milestone_id = @p1
             ORDER BY created_at DESC, id DESC
             LIMIT @p2
             """,
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, milestoneId),
                new("@p2", NpgsqlDbType.Integer, limit),
            ],
            ReadCurated,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> TryUpdateAsync(
        Guid scopeId,
        Guid screenshotId,
        long expectedRevision,
        string status,
        string caption,
        bool isPrimary,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        ArgumentNullException.ThrowIfNull(caption);

        var changed = await ChronicleafSql.ExecuteAsync(
            dbContext,
            """
            UPDATE screenshot_candidates
            SET status = @p2, caption = @p3, is_primary = @p4, revision = revision + 1
            WHERE scope_id = @p0 AND id = @p1 AND revision = @p5
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, screenshotId),
                new("@p2", NpgsqlDbType.Varchar, status),
                new("@p3", NpgsqlDbType.Varchar, caption),
                new("@p4", NpgsqlDbType.Boolean, isPrimary),
                new("@p5", NpgsqlDbType.Bigint, expectedRevision),
            ],
            cancellationToken).ConfigureAwait(false);

        return changed == 1;
    }

    /// <inheritdoc />
    public async Task ClearOtherPrimariesAsync(
        Guid scopeId,
        Guid milestoneId,
        Guid exceptId,
        CancellationToken cancellationToken = default)
    {
        // The milestone row is already locked by this transaction, so no concurrent curation can
        // set a primary between this clear and the update that named the new one. The partial
        // unique index behind this is the backstop, not the mechanism.
        await ChronicleafSql.ExecuteAsync(
            dbContext,
            """
            UPDATE screenshot_candidates
            SET is_primary = false, revision = revision + 1
            WHERE scope_id = @p0 AND milestone_id = @p1 AND is_primary AND id <> @p2
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, milestoneId),
                new("@p2", NpgsqlDbType.Uuid, exceptId),
            ],
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> TryOrderAsync(
        Guid scopeId,
        Guid screenshotId,
        long expectedRevision,
        int displayOrder,
        CancellationToken cancellationToken = default)
    {
        var changed = await ChronicleafSql.ExecuteAsync(
            dbContext,
            """
            UPDATE screenshot_candidates
            SET display_order = @p2, revision = revision + 1
            WHERE scope_id = @p0 AND id = @p1 AND revision = @p3
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, screenshotId),
                new("@p2", NpgsqlDbType.Integer, displayOrder),
                new("@p3", NpgsqlDbType.Bigint, expectedRevision),
            ],
            cancellationToken).ConfigureAwait(false);

        return changed == 1;
    }

    private static CuratedScreenshot ReadCurated(DbDataReader reader) =>
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
            reader.GetFieldValue<DateTimeOffset>(15),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetBoolean(12),
            reader.GetInt32(13),
            reader.GetInt64(14));
}
