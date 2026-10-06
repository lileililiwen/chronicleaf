using Chronicleaf.Application.Archive;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.Archive;

/// <summary>
/// The PostgreSQL store for operator-recorded backup manifests (HOSTED-ARCHIVE-OPERATIONS-001).
/// </summary>
/// <remarks>
/// <para>
/// <b>Manifests are written once and stamped by rehearsal.</b> The insert carries revision one
/// and no verification; the only later write is the rehearsal's compare-and-set stamp, so a
/// rehearsal that raced another rehearsal is a conflict rather than a merged verification.
/// </para>
/// <para>
/// <b>No read here leaves the scope.</b> Every lookup carries the caller's scope in the WHERE
/// clause, so a manifest id from another installation answers as absent - the same refusal shape
/// the tombstone store gives.
/// </para>
/// </remarks>
public sealed class PostgresArchiveBackupManifestStore(ChronicleafDbContext dbContext)
    : IArchiveBackupManifestStore
{
    private const string Columns =
        """
        id, scope_id, schema_version, created_at, db_backup_ref, media_inventory_hash,
        content_digest, encryption_key_version, restore_verified_at, revision, updated_at
        """;

    /// <inheritdoc />
    public async Task InsertAsync(ArchiveBackupManifest manifest, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        await ChronicleafSql.ExecuteAsync(
            dbContext,
            """
            INSERT INTO archive_backup_manifests
                (id, scope_id, schema_version, created_at, db_backup_ref, media_inventory_hash,
                 content_digest, encryption_key_version, restore_verified_at, revision, updated_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10)
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, manifest.Id),
                new("@p1", NpgsqlDbType.Uuid, manifest.ScopeId),
                new("@p2", NpgsqlDbType.Integer, manifest.SchemaVersion),
                new("@p3", NpgsqlDbType.TimestampTz, manifest.CreatedAt.UtcDateTime),
                new("@p4", NpgsqlDbType.Varchar, manifest.DbBackupRef),
                new("@p5", NpgsqlDbType.Char, manifest.MediaInventoryHash),
                new("@p6", NpgsqlDbType.Char, manifest.ContentDigest),
                new("@p7", NpgsqlDbType.Integer, manifest.EncryptionKeyVersion),
                new("@p8", NpgsqlDbType.TimestampTz, (object?)manifest.RestoreVerifiedAt?.UtcDateTime ?? DBNull.Value),
                new("@p9", NpgsqlDbType.Bigint, manifest.Revision),
                new("@p10", NpgsqlDbType.TimestampTz, manifest.UpdatedAt.UtcDateTime),
            ],
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ArchiveBackupManifest?> FindAsync(
        Guid scopeId,
        Guid manifestId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync(
            dbContext,
            $"SELECT {Columns} FROM archive_backup_manifests WHERE scope_id = @p0 AND id = @p1",
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, manifestId),
            ],
            Read,
            cancellationToken).ConfigureAwait(false);

        return rows.SingleOrDefault();
    }

    /// <inheritdoc />
    public async Task<bool> TryUpdateAsync(
        ArchiveBackupManifest manifest,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var affected = await ChronicleafSql.ExecuteAsync(
            dbContext,
            """
            UPDATE archive_backup_manifests
            SET restore_verified_at = @p3, revision = @p4, updated_at = @p5
            WHERE id = @p0 AND scope_id = @p1 AND revision = @p2
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, manifest.Id),
                new("@p1", NpgsqlDbType.Uuid, manifest.ScopeId),
                new("@p2", NpgsqlDbType.Bigint, expectedRevision),
                new("@p3", NpgsqlDbType.TimestampTz, (object?)manifest.RestoreVerifiedAt?.UtcDateTime ?? DBNull.Value),
                new("@p4", NpgsqlDbType.Bigint, manifest.Revision),
                new("@p5", NpgsqlDbType.TimestampTz, manifest.UpdatedAt.UtcDateTime),
            ],
            cancellationToken).ConfigureAwait(false);

        return affected == 1;
    }

    private static ArchiveBackupManifest Read(System.Data.Common.DbDataReader reader) =>
        ArchiveBackupManifest.Rehydrate(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetInt32(2),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
            reader.GetInt64(9),
            reader.GetFieldValue<DateTimeOffset>(3),
            reader.GetFieldValue<DateTimeOffset>(10));
}
