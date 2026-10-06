using Chronicleaf.Application.Journey;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using Npgsql;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.Journey;

/// <summary>
/// The public-project-journey table in PostgreSQL (PUBLIC-PROJECT-JOURNEY-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Four statements and no interpretation. The row exists because a journey is an explicit
/// publication decision per project: one row per project, one global slug per row, and an
/// optimistic revision every mutation compares against. The snapshot it carries is a frozen
/// projection of approved text and picture references - never private notes, never candidates,
/// never unselected pictures - so the public routes serve a decision rather than a live read.
/// </para>
/// <para>
/// <b>No approved text is interpreted here.</b> The snapshot JSON is bytes the service built and
/// the public routes serve; this store compares revisions and slugs, and nothing else. The
/// unique indexes decide the races, not a read this method did first.
/// </para>
/// <para>
/// Every scoped read carries its scope in its <c>WHERE</c> clause, so a journey belonging to
/// another scope answers <c>null</c>. The slug lookup is deliberately global: the slug is the
/// public address, and the service turns a miss into the same 404 as a revoked journey.
/// </para>
/// </remarks>
public sealed class PostgresProjectJourneyStore(ChronicleafDbContext dbContext) : IProjectJourneyStore
{
    private const string Columns =
        "id, scope_id, project_id, slug, enabled, revision, selection_hash, snapshot_ref, "
        + "snapshot_json, revoked_at, created_at, updated_at";

    /// <inheritdoc />
    public async Task<ProjectJourneyRow?> FindByProjectAsync(
        Guid scopeId,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync<ProjectJourneyRow>(
            dbContext,
            $"SELECT {Columns} FROM project_journeys WHERE scope_id = @p0 AND project_id = @p1",
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, projectId),
            ],
            Read,
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async Task<ProjectJourneyRow?> FindBySlugAsync(
        string slug,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync<ProjectJourneyRow>(
            dbContext,
            $"SELECT {Columns} FROM project_journeys WHERE slug = @p0",
            [new("@p0", NpgsqlDbType.Varchar, slug)],
            Read,
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async Task<bool> TryInsertAsync(ProjectJourneyRow row, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);

        try
        {
            // Either unique index decides the race, not a read this method did first: a returned
            // id means this call wrote the row, and no row means another writer bound the project
            // or the slug first.
            var id = await ChronicleafSql.ScalarAsync<Guid?>(
                dbContext,
                """
                INSERT INTO project_journeys
                    (id, scope_id, project_id, slug, enabled, revision, selection_hash, snapshot_ref,
                     snapshot_json, revoked_at, created_at, updated_at)
                VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11)
                ON CONFLICT DO NOTHING
                RETURNING id
                """,
                [
                    new("@p0", NpgsqlDbType.Uuid, row.Id),
                    new("@p1", NpgsqlDbType.Uuid, row.ScopeId),
                    new("@p2", NpgsqlDbType.Uuid, row.ProjectId),
                    new("@p3", NpgsqlDbType.Varchar, row.Slug),
                    new("@p4", NpgsqlDbType.Boolean, row.Enabled),
                    new("@p5", NpgsqlDbType.Bigint, row.Revision),
                    new("@p6", NpgsqlDbType.Varchar, row.SelectionHash),
                    new("@p7", NpgsqlDbType.Varchar, row.SnapshotRef),
                    new("@p8", NpgsqlDbType.Jsonb, row.SnapshotJson),
                    new("@p9", NpgsqlDbType.TimestampTz, (object?)row.RevokedAt ?? DBNull.Value),
                    new("@p10", NpgsqlDbType.TimestampTz, row.CreatedAt),
                    new("@p11", NpgsqlDbType.TimestampTz, row.UpdatedAt),
                ],
                cancellationToken).ConfigureAwait(false);

            return id is not null;
        }
        catch (PostgresException failure) when (failure.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // A unique index this insert did not name refused it: another writer bound the project
            // or the slug first, and the service re-reads to say which.
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<bool> TryUpdateAsync(
        ProjectJourneyRow row,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);

        try
        {
            // The stored revision decides the race, not a read this method did first: zero rows
            // means another writer moved it, and the service re-reads to say where it stands.
            var changed = await ChronicleafSql.ExecuteAsync(
                dbContext,
                """
                UPDATE project_journeys
                SET slug = @p1, enabled = @p2, revision = @p3, selection_hash = @p4,
                    snapshot_ref = @p5, snapshot_json = @p6, revoked_at = @p7, updated_at = @p8
                WHERE id = @p0 AND scope_id = @p9 AND revision = @p10
                """,
                [
                    new("@p0", NpgsqlDbType.Uuid, row.Id),
                    new("@p1", NpgsqlDbType.Varchar, row.Slug),
                    new("@p2", NpgsqlDbType.Boolean, row.Enabled),
                    new("@p3", NpgsqlDbType.Bigint, row.Revision),
                    new("@p4", NpgsqlDbType.Varchar, row.SelectionHash),
                    new("@p5", NpgsqlDbType.Varchar, row.SnapshotRef),
                    new("@p6", NpgsqlDbType.Jsonb, row.SnapshotJson),
                    new("@p7", NpgsqlDbType.TimestampTz, (object?)row.RevokedAt ?? DBNull.Value),
                    new("@p8", NpgsqlDbType.TimestampTz, row.UpdatedAt),
                    new("@p9", NpgsqlDbType.Uuid, row.ScopeId),
                    new("@p10", NpgsqlDbType.Bigint, expectedRevision),
                ],
                cancellationToken).ConfigureAwait(false);

            return changed == 1;
        }
        catch (PostgresException failure) when (failure.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // The slug this update carries is bound elsewhere: another writer took it first, and
            // the service re-reads to say so.
            return false;
        }
    }

    private static ProjectJourneyRow Read(System.Data.Common.DbDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetString(3),
            reader.GetBoolean(4),
            reader.GetInt64(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9),
            reader.GetFieldValue<DateTimeOffset>(10),
            reader.GetFieldValue<DateTimeOffset>(11));
}
