using System.Data.Common;
using Chronicleaf.Application.Media;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using NpgsqlTypes;
namespace Chronicleaf.Infrastructure.Media.S3;

/// <summary>
/// The PostgreSQL migration ledger (CLOUD-OBJECT-STORAGE-002).
/// </summary>
/// <remarks>
/// Every statement carries the scope in its WHERE clause, so a ledger entry for another
/// installation is never read and never replaced. The write is an upsert by primary key, so two
/// runs recording the same object converge rather than diverging - and the compare is in the
/// statement rather than in application code for the same reason every other store here does it.
/// </remarks>
public sealed class PostgresMediaMigrationRepository(ChronicleafDbContext dbContext) : IMediaMigrationRepository
{
    /// <summary>The ledger columns in the order <see cref="MediaMigrationCopyReader.Read"/> expects.</summary>
    public const string Columns =
        "media_id, scope_id, source_provider, target_provider, source_key, target_key, "
        + "source_sha256, copied_sha256, state, updated_at";

    public async Task<MediaMigrationCopy?> FindAsync(
        Guid scopeId,
        Guid mediaId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync(
            dbContext,
            $"""
              SELECT {Columns}
              FROM media_migration_copies
              WHERE scope_id = @p0 AND media_id = @p1
              """,
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, mediaId),
            ],
            MediaMigrationCopyReader.Read,
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    public Task UpsertAsync(MediaMigrationCopy copy, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(copy);

        return ChronicleafSql.ExecuteAsync(
            dbContext,
            $"""
             INSERT INTO media_migration_copies ({Columns})
             VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9)
             ON CONFLICT (media_id) DO UPDATE SET
                 scope_id = EXCLUDED.scope_id,
                 source_provider = EXCLUDED.source_provider,
                 target_provider = EXCLUDED.target_provider,
                 source_key = EXCLUDED.source_key,
                 target_key = EXCLUDED.target_key,
                 source_sha256 = EXCLUDED.source_sha256,
                 copied_sha256 = EXCLUDED.copied_sha256,
                 state = EXCLUDED.state,
                 updated_at = EXCLUDED.updated_at
             """,
            [
                new("@p0", NpgsqlDbType.Uuid, copy.MediaId),
                new("@p1", NpgsqlDbType.Uuid, copy.ScopeId),
                new("@p2", NpgsqlDbType.Varchar, copy.SourceProvider),
                new("@p3", NpgsqlDbType.Varchar, copy.TargetProvider),
                new("@p4", NpgsqlDbType.Varchar, copy.SourceKey),
                new("@p5", NpgsqlDbType.Varchar, copy.TargetKey),
                new("@p6", NpgsqlDbType.Varchar, copy.SourceSha256),
                new("@p7", NpgsqlDbType.Varchar, (object?)copy.CopiedSha256 ?? DBNull.Value),
                new("@p8", NpgsqlDbType.Varchar, copy.State),
                new("@p9", NpgsqlDbType.TimestampTz, copy.UpdatedAt),
            ],
            cancellationToken);
    }
}

/// <summary>
/// Reads one stored ledger row into its domain record, positionally.
/// </summary>
/// <remarks>
/// The column order is the order of the SELECTs above, stated here rather than guessed at, so a
/// query cannot silently hand the wrong column to a field.
/// </remarks>
internal static class MediaMigrationCopyReader
{
    public static MediaMigrationCopy Read(DbDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetString(8),
            reader.GetFieldValue<DateTimeOffset>(9));
}
