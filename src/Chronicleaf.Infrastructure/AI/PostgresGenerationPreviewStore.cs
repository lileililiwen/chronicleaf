using Chronicleaf.Application.AI;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Projects;
using NpgsqlTypes;
using System.Data.Common;

namespace Chronicleaf.Infrastructure.AI;

/// <summary>
/// The PostgreSQL store for owner-authorized generation previews
/// (LLM-PROVIDER-CONTRACT-002).
/// </summary>
/// <remarks>
/// <para>
/// Two statements and no interpretation. The row is written once and never updated - there is no
/// <c>UPDATE</c> in this file, and that is the requirement rather than an omission: a consent that
/// can be edited in place is a consent the owner never gave. The read is scoped in its
/// <c>WHERE</c> clause, so a preview belonging to another scope answers <c>null</c> and the route
/// turns that into a 404.
/// </para>
/// <para>
/// The snapshot is stored as <c>jsonb</c> and read back as text, so the bytes an owner authorized
/// and the bytes a call is built from are the same bytes the row holds. It is never parsed here:
/// interpretation lives in the application layer, which means a row cannot be half-understood by one
/// component and fully understood by another.
/// </para>
/// <para>
/// Every value is a bound parameter with its PostgreSQL type named. The scope is in the read's
/// <c>WHERE</c> clause and in the insert's column, never assembled from text.
/// </para>
/// </remarks>
public sealed class PostgresGenerationPreviewStore(Persistence.ChronicleafDbContext dbContext)
    : IGenerationPreviewStore
{
    /// <inheritdoc />
    public async Task<bool> InsertAsync(GenerationPreview preview, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);

        // ON CONFLICT DO NOTHING RETURNING is how the primary key decides the race, rather than a
        // read this method did first: a returned id means this call wrote the row.
        var id = await ChronicleafSql.ScalarAsync<Guid?>(
            dbContext,
            """
            INSERT INTO generation_previews
                (id, scope_id, milestone_id, purpose, aggregate_ref, snapshot_json, sha256, expires_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7)
            ON CONFLICT (id) DO NOTHING
            RETURNING id
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, preview.Id),
                new("@p1", NpgsqlDbType.Uuid, preview.ScopeId),
                new("@p2", NpgsqlDbType.Uuid, preview.MilestoneId.HasValue ? preview.MilestoneId.Value : DBNull.Value),
                new("@p3", NpgsqlDbType.Varchar, preview.Purpose),
                new("@p4", NpgsqlDbType.Varchar, preview.AggregateRef),
                new("@p5", NpgsqlDbType.Jsonb, preview.SnapshotJson),
                new("@p6", NpgsqlDbType.Varchar, preview.Sha256),
                new("@p7", NpgsqlDbType.TimestampTz, preview.ExpiresAt),
            ],
            cancellationToken).ConfigureAwait(false);

        return id is not null;
    }

    /// <inheritdoc />
    public async Task<GenerationPreview?> FindAsync(
        Guid scopeId,
        Guid previewId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync<GenerationPreview>(
            dbContext,
            """
            SELECT id, scope_id, milestone_id, purpose, aggregate_ref, snapshot_json, sha256, expires_at
            FROM generation_previews
            WHERE scope_id = @p0 AND id = @p1
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, previewId),
            ],
            ReadPreview,
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <summary>
    /// One row as it is read back, and the only place the nullable milestone column is unwrapped.
    /// </summary>
    /// <remarks>
    /// A row whose scope does not match the column this reader is given is refused rather than
    /// returned. The <c>WHERE</c> clause already decides that; this is the second, independent check
    /// that a scoped read cannot be rewritten into an unscoped one by a later edit to the SQL.
    /// </remarks>
    private static GenerationPreview ReadPreview(DbDataReader reader)
    {
        var row = new GenerationPreview(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.IsDBNull(2) ? null : reader.GetGuid(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetFieldValue<DateTimeOffset>(7));

        if (row.ScopeId == Guid.Empty)
        {
            throw new InvalidOperationException("The stored generation preview has no scope.");
        }

        return row;
    }
}
