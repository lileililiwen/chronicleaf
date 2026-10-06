using Chronicleaf.Application.Archive;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.Archive;

/// <summary>
/// The PostgreSQL store for recoverable archive-deletion markers (HOSTED-ARCHIVE-OPERATIONS-002).
/// </summary>
/// <remarks>
/// <para>
/// <b>"One live tombstone per project" is a fact about the database.</b> The insert takes the
/// partial unique index on <c>(scope_id, project_id)</c> over live rows in the same statement, so
/// two concurrent deletes cannot both open a window. Every later write is a compare-and-set on
/// the revision, so a restore that raced a purge is a conflict rather than an overwrite.
/// </para>
/// <para>
/// <b>Nothing here deletes.</b> This store reads and writes tombstone rows; the milestones,
/// evidence, assets and media behind a project are never touched here, and the purge's media
/// retirements go through the delivered media store rather than through SQL.
/// </para>
/// </remarks>
public sealed class PostgresArchiveTombstoneStore(ChronicleafDbContext dbContext) : IArchiveTombstoneStore
{
    private const string Columns =
        """
        id, scope_id, project_id, project_name, deleted_at, recovery_until, erase_requested,
        hold_reason, restored_at, purged_at, revision, created_at, updated_at
        """;

    /// <inheritdoc />
    public async Task<ArchiveTombstone?> FindLiveAsync(
        Guid scopeId,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync(
            dbContext,
            $"SELECT {Columns} FROM archive_tombstones"
            + " WHERE scope_id = @p0 AND project_id = @p1 AND restored_at IS NULL AND purged_at IS NULL",
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, projectId),
            ],
            Read,
            cancellationToken).ConfigureAwait(false);

        return rows.SingleOrDefault();
    }

    /// <inheritdoc />
    public async Task<ArchiveTombstone?> FindAsync(
        Guid scopeId,
        Guid tombstoneId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync(
            dbContext,
            $"SELECT {Columns} FROM archive_tombstones WHERE scope_id = @p0 AND id = @p1",
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, tombstoneId),
            ],
            Read,
            cancellationToken).ConfigureAwait(false);

        return rows.SingleOrDefault();
    }

    /// <inheritdoc />
    public async Task<bool> TryInsertAsync(ArchiveTombstone tombstone, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tombstone);

        var inserted = await ChronicleafSql.ScalarAsync<Guid?>(
            dbContext,
            """
            INSERT INTO archive_tombstones
                (id, scope_id, project_id, project_name, deleted_at, recovery_until,
                 erase_requested, hold_reason, restored_at, purged_at, revision, created_at, updated_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12)
            ON CONFLICT (scope_id, project_id) WHERE restored_at IS NULL AND purged_at IS NULL DO NOTHING
            RETURNING id
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, tombstone.Id),
                new("@p1", NpgsqlDbType.Uuid, tombstone.ScopeId),
                new("@p2", NpgsqlDbType.Uuid, tombstone.ProjectId),
                new("@p3", NpgsqlDbType.Text, tombstone.ProjectName),
                new("@p4", NpgsqlDbType.TimestampTz, tombstone.DeletedAt.UtcDateTime),
                new("@p5", NpgsqlDbType.TimestampTz, tombstone.RecoveryUntil.UtcDateTime),
                new("@p6", NpgsqlDbType.Boolean, tombstone.EraseRequested),
                new("@p7", NpgsqlDbType.Text, (object?)tombstone.HoldReason ?? DBNull.Value),
                new("@p8", NpgsqlDbType.TimestampTz, (object?)tombstone.RestoredAt?.UtcDateTime ?? DBNull.Value),
                new("@p9", NpgsqlDbType.TimestampTz, (object?)tombstone.PurgedAt?.UtcDateTime ?? DBNull.Value),
                new("@p10", NpgsqlDbType.Bigint, tombstone.Revision),
                new("@p11", NpgsqlDbType.TimestampTz, tombstone.CreatedAt.UtcDateTime),
                new("@p12", NpgsqlDbType.TimestampTz, tombstone.UpdatedAt.UtcDateTime),
            ],
            cancellationToken).ConfigureAwait(false);

        return inserted is not null;
    }

    /// <inheritdoc />
    public async Task<bool> TryUpdateAsync(
        ArchiveTombstone tombstone,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tombstone);

        var affected = await ChronicleafSql.ExecuteAsync(
            dbContext,
            """
            UPDATE archive_tombstones
            SET project_name = @p3, deleted_at = @p4, recovery_until = @p5,
                erase_requested = @p6, hold_reason = @p7, restored_at = @p8, purged_at = @p9,
                revision = @p10, updated_at = @p11
            WHERE id = @p0 AND scope_id = @p1 AND revision = @p2
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, tombstone.Id),
                new("@p1", NpgsqlDbType.Uuid, tombstone.ScopeId),
                new("@p2", NpgsqlDbType.Bigint, expectedRevision),
                new("@p3", NpgsqlDbType.Text, tombstone.ProjectName),
                new("@p4", NpgsqlDbType.TimestampTz, tombstone.DeletedAt.UtcDateTime),
                new("@p5", NpgsqlDbType.TimestampTz, tombstone.RecoveryUntil.UtcDateTime),
                new("@p6", NpgsqlDbType.Boolean, tombstone.EraseRequested),
                new("@p7", NpgsqlDbType.Text, (object?)tombstone.HoldReason ?? DBNull.Value),
                new("@p8", NpgsqlDbType.TimestampTz, (object?)tombstone.RestoredAt?.UtcDateTime ?? DBNull.Value),
                new("@p9", NpgsqlDbType.TimestampTz, (object?)tombstone.PurgedAt?.UtcDateTime ?? DBNull.Value),
                new("@p10", NpgsqlDbType.Bigint, tombstone.Revision),
                new("@p11", NpgsqlDbType.TimestampTz, tombstone.UpdatedAt.UtcDateTime),
            ],
            cancellationToken).ConfigureAwait(false);

        return affected == 1;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ArchiveTombstone>> ListPurgeCandidatesAsync(
        CancellationToken cancellationToken = default) =>
        await ChronicleafSql.QueryAsync(
            dbContext,
            $"SELECT {Columns} FROM archive_tombstones"
            + " WHERE erase_requested AND restored_at IS NULL AND purged_at IS NULL"
            + " ORDER BY recovery_until ASC",
            [],
            Read,
            cancellationToken).ConfigureAwait(false);

    private static ArchiveTombstone Read(System.Data.Common.DbDataReader reader) =>
        ArchiveTombstone.Rehydrate(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetString(3),
            reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.GetBoolean(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
            reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
            reader.GetInt64(10),
            reader.GetFieldValue<DateTimeOffset>(11),
            reader.GetFieldValue<DateTimeOffset>(12));
}
