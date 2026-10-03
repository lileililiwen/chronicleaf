using System.Data.Common;
using Chronicleaf.Application.Media;
using Chronicleaf.Core;

// Inside the Chronicleaf.Infrastructure.Media namespace the bare identifier `Media` would resolve to
// the namespace, so the Core record is addressed through an alias throughout.
using MediaRecord = Chronicleaf.Core.Media;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.Media;

/// <summary>
/// The PostgreSQL media store (LOCAL-MEDIA-STORAGE-002).
/// </summary>
/// <remarks>
/// <para>
/// Every lookup carries the scope in its WHERE clause, so a read by id can only ever return a record
/// inside the caller's own installation, and the write that changes a status is a compare-and-set on
/// the status the caller was told about. The revision is advanced by the statement rather than
/// computed by the caller, so two writers that both believed a record was ready cannot both retire it.
/// </para>
/// <para>
/// A media record is immutable in the domain: there is no field edit, so a record is only ever
/// inserted or moved between statuses. That is why there is no update of content here, and it is why
/// the only statement that changes an existing row names the status it is moving from.
/// </para>
/// <para>
/// The table has no foreign key to a reference table, because references are owned by a later package
/// and inventing one here would guess at its schema. The referential rule is therefore enforced at
/// the decision point instead - by <see cref="IMediaReferenceChecker"/> before anything is written -
/// and the status change below is a compare-and-set, so even two callers that disagreed about the
/// reference could not both delete the bytes.
/// </para>
/// </remarks>
public sealed class PostgresMediaRepository(ChronicleafDbContext dbContext) : IMediaRepository
{
    /// <summary>The media columns in the order <see cref="MediaRowReader.Read"/> expects.</summary>
    public const string Columns =
        "id, scope_id, project_id, storage_key, media_type, byte_length, sha256, status, created_at, "
        + "owner_operation_id, revision";

    public Task<MediaRecord?> FindAsync(Guid scopeId, Guid mediaId, CancellationToken cancellationToken = default) =>
        dbContext.Set<MediaRecord>()
            .AsNoTracking()
            .Where(record => record.Id == mediaId && record.ScopeId == scopeId)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<bool> InsertAsync(MediaRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        // A statement rather than a tracked insert, so the row and the audit row in the same unit of
        // work commit together and a collision is the database's answer rather than an exception the
        // caller has to interpret.
        var inserted = await ChronicleafSql.ScalarAsync<Guid>(
            dbContext,
            $"""
             INSERT INTO media ({Columns})
             VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10)
             ON CONFLICT (id) DO NOTHING
             RETURNING id
             """,
            [
                new("@p0", NpgsqlDbType.Uuid, record.Id),
                new("@p1", NpgsqlDbType.Uuid, record.ScopeId),
                new("@p2", NpgsqlDbType.Uuid, record.ProjectId),
                new("@p3", NpgsqlDbType.Varchar, record.StorageKey),
                new("@p4", NpgsqlDbType.Varchar, record.MediaType),
                new("@p5", NpgsqlDbType.Bigint, record.ByteLength),
                new("@p6", NpgsqlDbType.Varchar, record.Sha256),
                new("@p7", NpgsqlDbType.Varchar, record.Status),
                new("@p8", NpgsqlDbType.TimestampTz, record.CreatedAt),
                new("@p9", NpgsqlDbType.Uuid, record.OwnerOperationId),
                new("@p10", NpgsqlDbType.Bigint, record.Revision),
            ],
            cancellationToken).ConfigureAwait(false);

        return inserted != Guid.Empty;
    }

    public Task<int> TransitionStatusAsync(
        Guid scopeId,
        Guid mediaId,
        string expectedStatus,
        string newStatus,
        DateTimeOffset changedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(changedAt.Ticks);

        return ChronicleafSql.ExecuteAsync(
            dbContext,
            """
            UPDATE media
            SET status = @p3, revision = revision + 1
            WHERE id = @p1 AND scope_id = @p0 AND status = @p2
            """,
            [
                // The scope is in the WHERE clause rather than in a filter applied afterwards, so a
                // transition can never be applied on behalf of an installation other than the
                // caller's own, and the expected status makes two writers that both believed the
                // record was live unable to both move it.
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, mediaId),
                new("@p2", NpgsqlDbType.Varchar, expectedStatus),
                new("@p3", NpgsqlDbType.Varchar, newStatus),
            ],
            cancellationToken);
    }

    public Task<IReadOnlyList<MediaRecord>> ListStalePendingAsync(
        DateTimeOffset stagedBefore,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        var sql = $"""
            SELECT {Columns}
            FROM media
            WHERE status = @p0 AND created_at <= @p1
            ORDER BY created_at, id
            LIMIT @p2
            """;

        var parameters = new List<SqlParameter>(3)
        {
            new("@p0", NpgsqlDbType.Varchar, MediaStatus.Pending),
            new("@p1", NpgsqlDbType.TimestampTz, stagedBefore),
            new("@p2", NpgsqlDbType.Integer, limit),
        };

        return ChronicleafSql.QueryAsync(dbContext, sql, parameters, MediaRowReader.Read, cancellationToken);
    }

    public async Task<MediaRecord?> FindByStorageKeyAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(storageKey);

        // Deliberately not scoped: the question a purge asks is "does anything at all name this key",
        // and answering it per scope would let an orphan in another installation look untracked.
        var rows = await ChronicleafSql.QueryAsync(
            dbContext,
            $"""
             SELECT {Columns}
             FROM media
             WHERE storage_key = @p0
             ORDER BY created_at
             LIMIT 1
             """,
            [new("@p0", NpgsqlDbType.Varchar, storageKey)],
            MediaRowReader.Read,
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }
}

/// <summary>
/// Reads one stored media row into its domain record, positionally.
/// </summary>
/// <remarks>
/// The column order is the order of the SELECTs above, stated here rather than guessed at, so a
/// query cannot silently hand the wrong column to a field. Nothing here interprets a value: a nullable
/// column stays null, and a timestamp is already UTC because it is stored as <c>timestamptz</c>.
/// </remarks>
internal static class MediaRowReader
{
    public static MediaRecord Read(DbDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetFieldValue<DateTimeOffset>(8),
            reader.IsDBNull(9) ? null : reader.GetGuid(9),
            reader.GetInt64(10));
}
