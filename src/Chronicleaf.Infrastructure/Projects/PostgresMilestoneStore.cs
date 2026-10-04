using Chronicleaf.Application.Milestones;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.Projects;

/// <summary>
/// The PostgreSQL Milestone store (PROJECTS-AND-MILESTONES-001).
/// </summary>
/// <remarks>
/// Every read joins the owning project and requires the caller's scope, so a milestone id can never
/// be resolved without its project's scope being checked, and a record that belongs to another
/// installation is simply not found. Every write carries the same join plus two guards:
/// <c>revision = @expected</c> and <c>projects.archived_at IS NULL</c>. Together they are the
/// concurrency rule and the read-only rule, enforced by the database rather than by a check that a
/// second writer could slip past.
/// <para>
/// The insert asks PostgreSQL to decide whether a key is new. That is the idempotence of an import:
/// it is one atomic statement, so two imports of the same key that race each other still leave one
/// row, and both callers are handed the same id. A record with no source key is never subject to the
/// partial index, which is why the owner's own records can never be treated as duplicates.
/// </para>
/// </remarks>
public sealed class PostgresMilestoneStore(ChronicleafDbContext dbContext) : IMilestoneStore
{
    public Task<Milestone?> FindAsync(Guid scopeId, Guid milestoneId, CancellationToken cancellationToken = default)
    {
        // The scope is checked through the owning project in the same query, so a milestone outside
        // the caller's installation is not found rather than found and then refused.
        var scopedProjects = dbContext.Projects
            .Where(project => project.ScopeId == scopeId)
            .Select(project => project.Id);

        return dbContext.Milestones
            .AsNoTracking()
            .Where(milestone => milestone.Id == milestoneId && scopedProjects.Contains(milestone.ProjectId))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public Task<Milestone?> FindBySourceAsync(
        Guid scopeId,
        Guid projectId,
        string source,
        string sourceKey,
        CancellationToken cancellationToken = default)
    {
        var scopedProjects = dbContext.Projects
            .Where(project => project.ScopeId == scopeId)
            .Select(project => project.Id);

        return dbContext.Milestones
            .AsNoTracking()
            .Where(
                milestone =>
                    milestone.ProjectId == projectId
                    && milestone.Source == source
                    && milestone.SourceKey == sourceKey
                    && scopedProjects.Contains(milestone.ProjectId))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public Task<IReadOnlyList<Milestone>> ListAsync(
        Guid scopeId,
        Guid projectId,
        string? status,
        DateTimeOffset? occurredBefore,
        Guid? idBefore,
        int limit,
        CancellationToken cancellationToken = default)
    {
        // occurredAt desc, id desc, and the cursor is the same ordering read backwards: the next page
        // is the rows strictly before the last one this caller saw.
        var positioned = occurredBefore is not null && idBefore is not null;
        var filtered = status is not null;

        var sql = $"""
            SELECT {MilestoneRowReader.QualifiedColumns("m")}
            FROM milestones AS m
            INNER JOIN projects AS p ON p.id = m.project_id
            WHERE p.scope_id = @p0 AND m.project_id = @p1
              {(filtered ? "AND m.status = @p2" : string.Empty)}
              {(positioned ? "AND (m.occurred_at, m.id) < (@p3, @p4)" : string.Empty)}
            ORDER BY m.occurred_at DESC, m.id DESC
            LIMIT @p5
            """;

        var parameters = new List<SqlParameter>(6)
        {
            new("@p0", NpgsqlDbType.Uuid, scopeId),
            new("@p1", NpgsqlDbType.Uuid, projectId),
            new("@p5", NpgsqlDbType.Integer, limit),
        };

        if (filtered)
        {
            parameters.Add(new("@p2", NpgsqlDbType.Varchar, status));
        }

        if (positioned)
        {
            parameters.Add(new("@p3", NpgsqlDbType.TimestampTz, occurredBefore!.Value.ToUniversalTime()));
            parameters.Add(new("@p4", NpgsqlDbType.Uuid, idBefore));
        }

        return ChronicleafSql.QueryAsync(dbContext, sql, parameters, MilestoneRowReader.Read, cancellationToken);
    }

    public Task<IReadOnlyList<Milestone>> ListChronologicalAsync(
        Guid scopeId,
        Guid projectId,
        bool includeCandidates,
        DateTimeOffset? occurredAfter,
        Guid? idAfter,
        int limit,
        CancellationToken cancellationToken = default)
    {
        // occurredAt asc, id asc, and the cursor is the same ordering: the next page is the rows
        // strictly after the last one this caller saw. Dismissed records are never part of the
        // timeline; candidates join the confirmed history only when the owner asked for them.
        var positioned = occurredAfter is not null && idAfter is not null;

        var sql = $"""
            SELECT {MilestoneRowReader.QualifiedColumns("m")}
            FROM milestones AS m
            INNER JOIN projects AS p ON p.id = m.project_id
            WHERE p.scope_id = @p0 AND m.project_id = @p1
              {(includeCandidates ? "AND m.status IN (@p2, @p6)" : "AND m.status = @p2")}
              {(positioned ? "AND (m.occurred_at, m.id) > (@p3, @p4)" : string.Empty)}
            ORDER BY m.occurred_at ASC, m.id ASC
            LIMIT @p5
            """;

        var parameters = new List<SqlParameter>(7)
        {
            new("@p0", NpgsqlDbType.Uuid, scopeId),
            new("@p1", NpgsqlDbType.Uuid, projectId),
            new("@p2", NpgsqlDbType.Varchar, MilestoneStatus.Confirmed),
            new("@p5", NpgsqlDbType.Integer, limit),
        };

        if (includeCandidates)
        {
            parameters.Add(new("@p6", NpgsqlDbType.Varchar, MilestoneStatus.Candidate));
        }

        if (positioned)
        {
            parameters.Add(new("@p3", NpgsqlDbType.TimestampTz, occurredAfter!.Value.ToUniversalTime()));
            parameters.Add(new("@p4", NpgsqlDbType.Uuid, idAfter));
        }

        return ChronicleafSql.QueryAsync(dbContext, sql, parameters, MilestoneRowReader.Read, cancellationToken);
    }

    public async Task<bool> InsertAsync(Milestone milestone, CancellationToken cancellationToken = default)
    {
        var inserted = await ChronicleafSql.ScalarAsync<Guid>(
            dbContext,
            """
            INSERT INTO milestones
                (id, project_id, title, version, occurred_at, type, source, source_key, status,
                 revision, notes, known_limitations, next_steps)
            VALUES
                (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12)
            ON CONFLICT (project_id, source, source_key) WHERE source_key IS NOT NULL DO NOTHING
            RETURNING id
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, milestone.Id),
                new("@p1", NpgsqlDbType.Uuid, milestone.ProjectId),
                new("@p2", NpgsqlDbType.Varchar, milestone.Title),
                new("@p3", NpgsqlDbType.Varchar, milestone.Version),
                new("@p4", NpgsqlDbType.TimestampTz, milestone.OccurredAt.ToUniversalTime()),
                new("@p5", NpgsqlDbType.Varchar, milestone.Type),
                new("@p6", NpgsqlDbType.Varchar, milestone.Source),
                new("@p7", NpgsqlDbType.Varchar, milestone.SourceKey),
                new("@p8", NpgsqlDbType.Varchar, milestone.Status),
                new("@p9", NpgsqlDbType.Bigint, milestone.Revision),
                new("@p10", NpgsqlDbType.Varchar, milestone.Notes),
                new("@p11", NpgsqlDbType.Array | NpgsqlDbType.Text, milestone.KnownLimitations.ToArray()),
                new("@p12", NpgsqlDbType.Array | NpgsqlDbType.Text, milestone.NextSteps.ToArray()),
            ],
            cancellationToken);

        return inserted != Guid.Empty;
    }

    public Task<int> UpdateAsync(
        Guid scopeId,
        Guid milestoneId,
        long expectedRevision,
        string title,
        string? version,
        DateTimeOffset occurredAt,
        string type,
        string notes,
        IReadOnlyList<string> knownLimitations,
        IReadOnlyList<string> nextSteps,
        CancellationToken cancellationToken = default) =>
        ChronicleafSql.ExecuteAsync(
            dbContext,
            // The status is absent from the SET list on purpose. No field edit can set it, which is
            // what stops an edit from turning a candidate into a confirmed record.
            """
            UPDATE milestones AS m
            SET title = @p2,
                version = @p3,
                occurred_at = @p4,
                type = @p5,
                notes = @p6,
                known_limitations = @p7,
                next_steps = @p8,
                revision = m.revision + 1
            FROM projects AS p
            WHERE m.id = @p0
              AND p.id = m.project_id
              AND p.scope_id = @p1
              AND m.revision = @p9
              AND p.archived_at IS NULL
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, milestoneId),
                new("@p1", NpgsqlDbType.Uuid, scopeId),
                new("@p2", NpgsqlDbType.Varchar, title),
                new("@p3", NpgsqlDbType.Varchar, version),
                new("@p4", NpgsqlDbType.TimestampTz, occurredAt.ToUniversalTime()),
                new("@p5", NpgsqlDbType.Varchar, type),
                new("@p6", NpgsqlDbType.Varchar, notes),
                new("@p7", NpgsqlDbType.Array | NpgsqlDbType.Text, knownLimitations.ToArray()),
                new("@p8", NpgsqlDbType.Array | NpgsqlDbType.Text, nextSteps.ToArray()),
                new("@p9", NpgsqlDbType.Bigint, expectedRevision),
            ],
            cancellationToken);

    public Task<int> SetStatusAsync(
        Guid scopeId,
        Guid milestoneId,
        long expectedRevision,
        string status,
        CancellationToken cancellationToken = default) =>
        ChronicleafSql.ExecuteAsync(
            dbContext,
            """
            UPDATE milestones AS m
            SET status = @p2, revision = m.revision + 1
            FROM projects AS p
            WHERE m.id = @p0
              AND p.id = m.project_id
              AND p.scope_id = @p1
              AND m.revision = @p3
              AND p.archived_at IS NULL
              AND m.status <> @p2
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, milestoneId),
                new("@p1", NpgsqlDbType.Uuid, scopeId),
                new("@p2", NpgsqlDbType.Varchar, status),
                new("@p3", NpgsqlDbType.Bigint, expectedRevision),
            ],
            cancellationToken);
}
