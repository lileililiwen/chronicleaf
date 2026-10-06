using System.Data.Common;
using Chronicleaf.Application.Schedules;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.Schedules;

/// <summary>
/// The PostgreSQL store for opt-in project sync schedules (SCHEDULED-PROJECT-SYNC-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>"One schedule per project" is a fact about the database.</b> The insert takes the unique index
/// on <c>(scope_id, project_id)</c> in the same statement, so two concurrent PUTs for one project
/// cannot both create. Every later write is a compare-and-set on the revision, so a tick that fires
/// while an owner edits is a conflict rather than an overwrite.
/// </para>
/// <para>
/// <b>Nothing here reaches a provider.</b> This store reads and writes one table and asks the
/// operations table which syncs are still active; the repository, the credential and the poll are
/// the delivered packages' rows and work, read back by the service rather than joined here.
/// </para>
/// </remarks>
public sealed class PostgresProjectSyncScheduleStore(ChronicleafDbContext dbContext)
    : IProjectSyncScheduleStore
{
    private const string Columns =
        """
        id, scope_id, project_id, enabled, interval_minutes, next_run_at, last_operation_id,
        revision, created_at, updated_at
        """;

    /// <inheritdoc />
    public async Task<ProjectSyncSchedule?> FindForProjectAsync(
        Guid scopeId,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync(
            dbContext,
            $"SELECT {Columns} FROM project_sync_schedules WHERE scope_id = @p0 AND project_id = @p1",
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, projectId),
            ],
            Read,
            cancellationToken).ConfigureAwait(false);

        return rows.SingleOrDefault();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProjectSyncSchedule>> ListDueAsync(
        DateTimeOffset now,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return await ChronicleafSql.QueryAsync(
            dbContext,
            $"SELECT {Columns} FROM project_sync_schedules"
            + " WHERE enabled AND next_run_at <= @p0 ORDER BY next_run_at ASC LIMIT @p1",
            [
                new("@p0", NpgsqlDbType.TimestampTz, now.UtcDateTime),
                new("@p1", NpgsqlDbType.Integer, limit),
            ],
            Read,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<long> CountEnabledAsync(Guid scopeId, CancellationToken cancellationToken = default)
    {
        var count = await ChronicleafSql.ScalarAsync<long?>(
            dbContext,
            "SELECT count(*) FROM project_sync_schedules WHERE scope_id = @p0 AND enabled",
            [new("@p0", NpgsqlDbType.Uuid, scopeId)],
            cancellationToken).ConfigureAwait(false);

        return count ?? 0;
    }

    /// <inheritdoc />
    public async Task<bool> TryInsertAsync(
        ProjectSyncSchedule schedule,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        var inserted = await ChronicleafSql.ScalarAsync<Guid?>(
            dbContext,
            """
            INSERT INTO project_sync_schedules
                (id, scope_id, project_id, enabled, interval_minutes, next_run_at,
                 last_operation_id, revision, created_at, updated_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9)
            ON CONFLICT (scope_id, project_id) DO NOTHING
            RETURNING id
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, schedule.Id),
                new("@p1", NpgsqlDbType.Uuid, schedule.ScopeId),
                new("@p2", NpgsqlDbType.Uuid, schedule.ProjectId),
                new("@p3", NpgsqlDbType.Boolean, schedule.Enabled),
                new("@p4", NpgsqlDbType.Integer, schedule.IntervalMinutes),
                new("@p5", NpgsqlDbType.TimestampTz, schedule.NextRunAt.UtcDateTime),
                new("@p6", NpgsqlDbType.Uuid, schedule.LastOperationId is { } last ? last : DBNull.Value),
                new("@p7", NpgsqlDbType.Bigint, schedule.Revision),
                new("@p8", NpgsqlDbType.TimestampTz, schedule.CreatedAt.UtcDateTime),
                new("@p9", NpgsqlDbType.TimestampTz, schedule.UpdatedAt.UtcDateTime),
            ],
            cancellationToken).ConfigureAwait(false);

        return inserted is not null;
    }

    /// <inheritdoc />
    public async Task<bool> TryUpdateAsync(
        ProjectSyncSchedule schedule,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(schedule);

        var affected = await ChronicleafSql.ExecuteAsync(
            dbContext,
            """
            UPDATE project_sync_schedules
            SET enabled = @p3, interval_minutes = @p4, next_run_at = @p5,
                last_operation_id = @p6, revision = @p7, updated_at = @p8
            WHERE id = @p0 AND scope_id = @p1 AND revision = @p2
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, schedule.Id),
                new("@p1", NpgsqlDbType.Uuid, schedule.ScopeId),
                new("@p2", NpgsqlDbType.Bigint, expectedRevision),
                new("@p3", NpgsqlDbType.Boolean, schedule.Enabled),
                new("@p4", NpgsqlDbType.Integer, schedule.IntervalMinutes),
                new("@p5", NpgsqlDbType.TimestampTz, schedule.NextRunAt.UtcDateTime),
                new("@p6", NpgsqlDbType.Uuid, schedule.LastOperationId is { } last ? last : DBNull.Value),
                new("@p7", NpgsqlDbType.Bigint, schedule.Revision),
                new("@p8", NpgsqlDbType.TimestampTz, schedule.UpdatedAt.UtcDateTime),
            ],
            cancellationToken).ConfigureAwait(false);

        return affected == 1;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ActiveSyncOperation>> ListActiveOperationsAsync(
        Guid scopeId,
        Guid projectId,
        IReadOnlyCollection<string> kinds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kinds);

        if (kinds.Count == 0)
        {
            return [];
        }

        // The kinds are the service's own closed set, interpolated as literals rather than bound:
        // Npgsql cannot bind a list into an IN predicate without expanding it, and expanding it by
        // hand here is what would turn a closed set into string building over caller input.
        var kindList = string.Join(", ", kinds.Select(kind =>
        {
            if (kind.Any(character => !char.IsLetter(character) && character != '.' && character != '-'))
            {
                throw new ArgumentException("A sync operation kind is letters, dots and dashes.", nameof(kinds));
            }

            return $"'{kind}'";
        }));

        return await ChronicleafSql.QueryAsync(
            dbContext,
            "SELECT id, kind, status FROM operations"
            + $" WHERE scope_id = @p0 AND target_id = @p1 AND kind IN ({kindList})"
            + " AND status IN ('queued', 'running') ORDER BY created_at ASC",
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, projectId),
            ],
            reader => new ActiveSyncOperation(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2)),
            cancellationToken).ConfigureAwait(false);
    }

    private static ProjectSyncSchedule Read(DbDataReader reader) =>
        ProjectSyncSchedule.Rehydrate(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetBoolean(3),
            reader.GetInt32(4),
            reader.GetFieldValue<DateTimeOffset>(5),
            reader.IsDBNull(6) ? null : reader.GetGuid(6),
            reader.GetInt64(7),
            reader.GetFieldValue<DateTimeOffset>(8),
            reader.GetFieldValue<DateTimeOffset>(9));
}
