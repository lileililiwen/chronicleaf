using Chronicleaf.Application.Narrative;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.Narrative;

/// <summary>
/// The PostgreSQL narrative store (NARRATIVE-MEMORY-001/002).
/// </summary>
/// <remarks>
/// Every read joins the owning project and requires the caller's scope, so an entry id can never
/// be resolved without its project's scope being checked, and a record that belongs to another
/// installation is simply not found. Every write carries the same join plus the revision
/// compare-and-set, enforced by the database rather than by a check a second writer could slip
/// past: a stale writer loses rather than merging.
/// <para>
/// The citation check is one statement: it counts how many of the cited evidence ids belong to
/// this project inside this scope, through bundles and milestones, and the caller compares that
/// count to the distinct ids it asked about. A probe for another project's evidence learns
/// nothing but a count.
/// </para>
/// </remarks>
public sealed class PostgresNarrativeStore(ChronicleafDbContext dbContext) : INarrativeStore
{
    public Task<NarrativeEntry?> FindEntryAsync(Guid scopeId, Guid entryId, CancellationToken cancellationToken = default)
    {
        var scopedProjects = dbContext.Projects
            .Where(project => project.ScopeId == scopeId)
            .Select(project => project.Id);

        return dbContext.NarrativeEntries
            .AsNoTracking()
            .Where(entry => entry.Id == entryId && scopedProjects.Contains(entry.ProjectId))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<NarrativeEntry>> ListEntriesAsync(
        Guid scopeId,
        Guid projectId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var owned = await dbContext.Projects
            .AsNoTracking()
            .Where(project => project.ScopeId == scopeId && project.Id == projectId)
            .Select(project => project.Id)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (owned == Guid.Empty)
        {
            return [];
        }

        return await dbContext.NarrativeEntries
            .AsNoTracking()
            .Where(entry => entry.ProjectId == projectId)
            .OrderBy(entry => entry.OccurredAt)
            .ThenBy(entry => entry.Id)
            .Take(Math.Max(1, limit))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> CitationsBelongToProjectAsync(
        Guid scopeId,
        Guid projectId,
        IReadOnlyList<Guid> evidenceIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidenceIds);

        var distinct = evidenceIds.Distinct().ToArray();
        if (distinct.Length == 0)
        {
            return true;
        }

        var count = await ChronicleafSql.ScalarAsync<long>(
            dbContext,
            """
            SELECT COUNT(*)::bigint
            FROM evidence_items AS i
            JOIN evidence_bundles AS b ON b.id = i.bundle_id
            JOIN milestones AS m ON m.id = b.milestone_id
            JOIN projects AS p ON p.id = m.project_id
            WHERE p.scope_id = @p0
              AND m.project_id = @p1
              AND i.id = ANY(@p2)
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, projectId),
                new("@p2", NpgsqlDbType.Array | NpgsqlDbType.Uuid, distinct),
            ],
            cancellationToken).ConfigureAwait(false);

        return count == distinct.Length;
    }

    public async Task<bool> InsertEntryAsync(
        NarrativeEntry entry,
        Guid scopeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var inserted = await ChronicleafSql.ScalarAsync<Guid>(
            dbContext,
            """
            INSERT INTO narrative_entries
                (id, project_id, kind, occurred_at, text, source_evidence_ids, revision, visibility)
            SELECT @p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7
            WHERE EXISTS (SELECT 1 FROM projects AS p WHERE p.id = @p1 AND p.scope_id = @p8)
            ON CONFLICT (id) DO NOTHING
            RETURNING id
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, entry.Id),
                new("@p1", NpgsqlDbType.Uuid, entry.ProjectId),
                new("@p2", NpgsqlDbType.Varchar, entry.Kind),
                new("@p3", NpgsqlDbType.TimestampTz, entry.OccurredAt.ToUniversalTime()),
                new("@p4", NpgsqlDbType.Varchar, entry.Text),
                new("@p5", NpgsqlDbType.Array | NpgsqlDbType.Uuid, entry.SourceEvidenceIds.ToArray()),
                new("@p6", NpgsqlDbType.Bigint, entry.Revision),
                new("@p7", NpgsqlDbType.Varchar, entry.Visibility),
                new("@p8", NpgsqlDbType.Uuid, scopeId),
            ],
            cancellationToken).ConfigureAwait(false);

        return inserted != Guid.Empty;
    }

    public Task<int> UpdateEntryAsync(
        Guid scopeId,
        Guid entryId,
        long expectedRevision,
        string kind,
        DateTimeOffset occurredAt,
        string text,
        IReadOnlyList<Guid> sourceEvidenceIds,
        string visibility,
        CancellationToken cancellationToken = default) =>
        ChronicleafSql.ExecuteAsync(
            dbContext,
            // The revision is not a parameter: it is incremented by the database, so two edits that
            // present the same revision cannot both land. The scope rides the project join, so a
            // forged entry id in another installation matches no row.
            """
            UPDATE narrative_entries AS e
            SET kind = @p2,
                occurred_at = @p3,
                text = @p4,
                source_evidence_ids = @p5,
                visibility = @p6,
                revision = e.revision + 1
            FROM projects AS p
            WHERE e.id = @p0
              AND p.id = e.project_id
              AND p.scope_id = @p1
              AND e.revision = @p7
              AND p.archived_at IS NULL
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, entryId),
                new("@p1", NpgsqlDbType.Uuid, scopeId),
                new("@p2", NpgsqlDbType.Varchar, kind),
                new("@p3", NpgsqlDbType.TimestampTz, occurredAt.ToUniversalTime()),
                new("@p4", NpgsqlDbType.Varchar, text),
                new("@p5", NpgsqlDbType.Array | NpgsqlDbType.Uuid, sourceEvidenceIds.ToArray()),
                new("@p6", NpgsqlDbType.Varchar, visibility),
                new("@p7", NpgsqlDbType.Bigint, expectedRevision),
            ],
            cancellationToken);

    public Task<ProjectSummary?> FindSummaryAsync(
        Guid scopeId,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var scopedProjects = dbContext.Projects
            .Where(project => project.ScopeId == scopeId)
            .Select(project => project.Id);

        return dbContext.ProjectSummaries
            .AsNoTracking()
            .Where(summary => summary.ProjectId == projectId && scopedProjects.Contains(summary.ProjectId))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<bool> TryInsertSummaryAsync(
        ProjectSummary summary,
        Guid scopeId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(summary);

        var inserted = await ChronicleafSql.ScalarAsync<Guid>(
            dbContext,
            """
            INSERT INTO project_summaries
                (id, project_id, through_milestone_id, input_hash, body_json, revision, stale, updated_at)
            SELECT @p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7
            WHERE EXISTS (SELECT 1 FROM projects AS p WHERE p.id = @p1 AND p.scope_id = @p8)
            ON CONFLICT (project_id) DO NOTHING
            RETURNING id
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, summary.Id),
                new("@p1", NpgsqlDbType.Uuid, summary.ProjectId),
                new("@p2", NpgsqlDbType.Uuid, (object?)summary.ThroughMilestoneId ?? DBNull.Value),
                new("@p3", NpgsqlDbType.Varchar, summary.InputHash),
                new("@p4", NpgsqlDbType.Jsonb, summary.BodyJson),
                new("@p5", NpgsqlDbType.Bigint, summary.Revision),
                new("@p6", NpgsqlDbType.Boolean, summary.Stale),
                new("@p7", NpgsqlDbType.TimestampTz, summary.UpdatedAt.ToUniversalTime()),
                new("@p8", NpgsqlDbType.Uuid, scopeId),
            ],
            cancellationToken).ConfigureAwait(false);

        return inserted != Guid.Empty;
    }

    public Task<int> UpdateSummaryAsync(
        Guid scopeId,
        Guid projectId,
        long expectedRevision,
        Guid? throughMilestoneId,
        string inputHash,
        string bodyJson,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) =>
        ChronicleafSql.ExecuteAsync(
            dbContext,
            // A regeneration replaces the body wholesale at exactly the revision it read: two
            // refreshes that race cannot both commit, and the loser re-reads rather than merging.
            // The stale mark is cleared by the same statement, because a body that was just built
            // from the current sources is current by definition.
            """
            UPDATE project_summaries AS s
            SET through_milestone_id = @p2,
                input_hash = @p3,
                body_json = @p4::jsonb,
                revision = s.revision + 1,
                stale = FALSE,
                updated_at = @p5
            FROM projects AS p
            WHERE s.project_id = @p0
              AND p.id = s.project_id
              AND p.scope_id = @p1
              AND s.revision = @p6
              AND p.archived_at IS NULL
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, projectId),
                new("@p1", NpgsqlDbType.Uuid, scopeId),
                new("@p2", NpgsqlDbType.Uuid, (object?)throughMilestoneId ?? DBNull.Value),
                new("@p3", NpgsqlDbType.Varchar, inputHash),
                new("@p4", NpgsqlDbType.Json, bodyJson),
                new("@p5", NpgsqlDbType.TimestampTz, updatedAt.ToUniversalTime()),
                new("@p6", NpgsqlDbType.Bigint, expectedRevision),
            ],
            cancellationToken);

    public Task<int> MarkSummaryStaleAsync(
        Guid scopeId,
        Guid projectId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default) =>
        ChronicleafSql.ExecuteAsync(
            dbContext,
            // The body is untouched: staleness is a mark on the last good answer, never a deletion
            // of it. A project with no summary yet matches no row, which is a real answer too.
            """
            UPDATE project_summaries AS s
            SET stale = TRUE,
                updated_at = @p2
            FROM projects AS p
            WHERE s.project_id = @p0
              AND p.id = s.project_id
              AND p.scope_id = @p1
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, projectId),
                new("@p1", NpgsqlDbType.Uuid, scopeId),
                new("@p2", NpgsqlDbType.TimestampTz, updatedAt.ToUniversalTime()),
            ],
            cancellationToken);

    public Task InsertRefreshRequestAsync(
        NarrativeRefreshRequestRow row,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);

        return ChronicleafSql.ExecuteAsync(
            dbContext,
            """
            INSERT INTO narrative_refresh_requests
                (id, scope_id, operation_id, project_id, cutoff, selected_entry_ids, created_at)
            VALUES
                (@p0, @p1, @p2, @p3, @p4, @p5, @p6)
            ON CONFLICT (scope_id, operation_id) DO NOTHING
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, row.Id),
                new("@p1", NpgsqlDbType.Uuid, row.ScopeId),
                new("@p2", NpgsqlDbType.Uuid, row.OperationId),
                new("@p3", NpgsqlDbType.Uuid, row.ProjectId),
                new("@p4", NpgsqlDbType.TimestampTz, row.Cutoff.ToUniversalTime()),
                new("@p5", NpgsqlDbType.Array | NpgsqlDbType.Uuid, row.SelectedEntryIds.ToArray()),
                new("@p6", NpgsqlDbType.TimestampTz, row.CreatedAt.ToUniversalTime()),
            ],
            cancellationToken);
    }

    public Task<NarrativeRefreshRequestRow?> FindRefreshRequestByOperationAsync(
        Guid scopeId,
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        dbContext.NarrativeRefreshRequests
            .AsNoTracking()
            .Where(row => row.ScopeId == scopeId && row.OperationId == operationId)
            .SingleOrDefaultAsync(cancellationToken);
}
