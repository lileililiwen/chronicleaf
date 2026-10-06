using Chronicleaf.Application.Content;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.Content;

/// <summary>
/// The static-blog-export request table in PostgreSQL (STATIC-BLOG-EXPORT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Two statements and no interpretation. The row exists because a queued job payload carries an
/// operation id and nothing else, so the selection the owner authorized has to be durable
/// somewhere the worker can read it from; the row is written once and never updated, because a
/// request that can be edited after it was accepted is a request the worker might act on a second
/// time with different content.
/// </para>
/// <para>
/// <b>No approved text is stored here.</b> The canonical asset list and the frozen approval
/// bindings are references the run re-resolves against the asset, evidence and media stores. An
/// export's prose is approved content, and a queue row is the wrong place for it: it is written
/// by a background path and retained by a retention policy nobody has audited.
/// </para>
/// <para>
/// The read is scoped in its <c>WHERE</c> clause, so a request belonging to another scope answers
/// <c>null</c> and the handler turns that into a permanent failure rather than reaching for it anyway.
/// </para>
/// </remarks>
public sealed class PostgresStaticBlogExportRequestStore(ChronicleafDbContext dbContext)
    : IStaticBlogExportRequestStore
{
    private const string Columns =
        "id, scope_id, operation_id, project_id, base_path, asset_ids_json, "
        + "selection_hash, frozen_json, created_at";

    /// <inheritdoc />
    public async Task<StaticBlogExportRequestRow?> FindByOperationAsync(
        Guid scopeId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync<StaticBlogExportRequestRow>(
            dbContext,
            $"SELECT {Columns} FROM static_blog_export_requests "
                + "WHERE scope_id = @p0 AND operation_id = @p1",
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, operationId),
            ],
            Read,
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async Task<bool> TryInsertAsync(
        StaticBlogExportRequestRow row,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);

        // The unique (scope, operation) index decides the race, not a read this method did first: a
        // returned id means this call wrote the row, and no row means a re-delivered enqueue already
        // left one behind for this job.
        var id = await ChronicleafSql.ScalarAsync<Guid?>(
            dbContext,
            """
            INSERT INTO static_blog_export_requests
                (id, scope_id, operation_id, project_id, base_path, asset_ids_json,
                 selection_hash, frozen_json, created_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8)
            ON CONFLICT (scope_id, operation_id) DO NOTHING
            RETURNING id
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, row.Id),
                new("@p1", NpgsqlDbType.Uuid, row.ScopeId),
                new("@p2", NpgsqlDbType.Uuid, row.OperationId),
                new("@p3", NpgsqlDbType.Uuid, row.ProjectId),
                new("@p4", NpgsqlDbType.Varchar, row.BasePath),
                new("@p5", NpgsqlDbType.Jsonb, row.AssetIdsJson),
                new("@p6", NpgsqlDbType.Varchar, row.SelectionHash),
                new("@p7", NpgsqlDbType.Jsonb, row.FrozenJson),
                new("@p8", NpgsqlDbType.TimestampTz, row.CreatedAt),
            ],
            cancellationToken).ConfigureAwait(false);

        return id is not null;
    }

    private static StaticBlogExportRequestRow Read(System.Data.Common.DbDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetGuid(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetFieldValue<DateTimeOffset>(8));
}
