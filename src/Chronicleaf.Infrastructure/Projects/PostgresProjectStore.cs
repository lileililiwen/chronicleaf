using Chronicleaf.Application.Milestones;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.Projects;

/// <summary>
/// The PostgreSQL Project store (PROJECTS-AND-MILESTONES-002).
/// </summary>
/// <remarks>
/// Reads are ordinary queries and every one of them carries the scope, so a lookup by id can only
/// ever return a record inside the caller's own installation. Writes are explicit statements whose
/// WHERE clause is the whole safety property: <c>revision = @expected AND archived_at IS NULL</c>.
/// That is what makes two edits of the same revision decide between themselves instead of both
/// landing, and it is what makes an archived project refuse an edit in the database rather than only
/// in the service.
/// <para>
/// Delete is not offered and cannot be offered: archiving stamps a column and advances the revision,
/// which leaves the project, its milestones and their notes exactly as they were.
/// </para>
/// </remarks>
public sealed class PostgresProjectStore(ChronicleafDbContext dbContext) : IProjectStore
{
    public Task<Project?> FindAsync(Guid scopeId, Guid projectId, CancellationToken cancellationToken = default) =>
        dbContext.Projects
            .AsNoTracking()
            .Where(project => project.Id == projectId && project.ScopeId == scopeId)
            .SingleOrDefaultAsync(cancellationToken);

    public Task<IReadOnlyList<Project>> ListAsync(
        Guid scopeId,
        string? afterName,
        Guid? afterId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        // The page boundary is a row comparison in PostgreSQL, which orders by the same key the
        // ORDER BY uses. That is what makes a cursor a position rather than an offset: a project
        // created while the owner is paging does not shift a later page, and a name that is a prefix
        // of another is neither skipped nor repeated.
        var positioned = afterName is not null && afterId is not null;

        var sql = $"""
            SELECT {ProjectRowReader.Columns}
            FROM projects
            WHERE scope_id = @p0
              {(positioned ? "AND (name, id) > (@p1, @p2)" : string.Empty)}
            ORDER BY name, id
            LIMIT @p3
            """;

        var parameters = new List<SqlParameter>(4)
        {
            new("@p0", NpgsqlDbType.Uuid, scopeId),
            new("@p3", NpgsqlDbType.Integer, limit),
        };

        if (positioned)
        {
            parameters.Add(new("@p1", NpgsqlDbType.Varchar, afterName));
            parameters.Add(new("@p2", NpgsqlDbType.Uuid, afterId));
        }

        return ChronicleafSql.QueryAsync(dbContext, sql, parameters, ProjectRowReader.Read, cancellationToken);
    }

    public async Task<bool> InsertAsync(Project project, CancellationToken cancellationToken = default)
    {
        var inserted = await ChronicleafSql.ScalarAsync<Guid>(
            dbContext,
            """
            INSERT INTO projects (id, scope_id, name, description, repo_connection_id, project_url, revision, archived_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, NULL)
            ON CONFLICT (id) DO NOTHING
            RETURNING id
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, project.Id),
                new("@p1", NpgsqlDbType.Uuid, project.ScopeId),
                new("@p2", NpgsqlDbType.Varchar, project.Name),
                new("@p3", NpgsqlDbType.Varchar, project.Description),
                new("@p4", NpgsqlDbType.Uuid, project.RepoConnectionId),
                new("@p5", NpgsqlDbType.Varchar, project.ProjectUrl),
                new("@p6", NpgsqlDbType.Bigint, project.Revision),
            ],
            cancellationToken);

        return inserted != Guid.Empty;
    }

    public Task<int> UpdateAsync(
        Guid scopeId,
        Guid projectId,
        long expectedRevision,
        string name,
        string description,
        string? projectUrl,
        Guid? repoConnectionId,
        CancellationToken cancellationToken = default) =>
        ChronicleafSql.ExecuteAsync(
            dbContext,
            """
            UPDATE projects
            SET name = @p2, description = @p3, project_url = @p4, repo_connection_id = @p5, revision = revision + 1
            WHERE id = @p0 AND scope_id = @p1 AND revision = @p6 AND archived_at IS NULL
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, projectId),
                new("@p1", NpgsqlDbType.Uuid, scopeId),
                new("@p2", NpgsqlDbType.Varchar, name),
                new("@p3", NpgsqlDbType.Varchar, description),
                new("@p4", NpgsqlDbType.Varchar, projectUrl),
                new("@p5", NpgsqlDbType.Uuid, repoConnectionId),
                new("@p6", NpgsqlDbType.Bigint, expectedRevision),
            ],
            cancellationToken);

    public Task<int> ArchiveAsync(
        Guid scopeId,
        Guid projectId,
        long expectedRevision,
        DateTimeOffset archivedAt,
        CancellationToken cancellationToken = default) =>
        ChronicleafSql.ExecuteAsync(
            dbContext,
            // Nothing is deleted. The stamp makes the project read-only and the revision bump tells a
            // client that still holds the old revision that its view is stale.
            """
            UPDATE projects
            SET archived_at = @p2, revision = revision + 1
            WHERE id = @p0 AND scope_id = @p1 AND revision = @p3 AND archived_at IS NULL
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, projectId),
                new("@p1", NpgsqlDbType.Uuid, scopeId),
                new("@p2", NpgsqlDbType.TimestampTz, archivedAt.ToUniversalTime()),
                new("@p3", NpgsqlDbType.Bigint, expectedRevision),
            ],
            cancellationToken);
}
