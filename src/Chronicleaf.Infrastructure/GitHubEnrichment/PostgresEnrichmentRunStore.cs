using System.Data.Common;
using Chronicleaf.Application.GitHubEnrichment;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.GitHubEnrichment;

/// <summary>
/// The PostgreSQL store for the enrichment run log (GITHUB-GRAPHQL-ENRICHMENT-001/002).
/// </summary>
/// <remarks>
/// The insert is <c>ON CONFLICT DO NOTHING RETURNING</c> on the operation's unique index, so a
/// replayed request - the same idempotency key, the same job - adds no second row for one
/// enrichment. That is the same shape the delivered preview store uses to let the database decide
/// a race rather than a read this method did first. The row is never updated afterwards: what the
/// run observed is recorded on the bundle it appended, so a second attempt on the same operation
/// reads the same request and appends the same facts, which the delivered idempotent append
/// recognises as the same statement.
/// </remarks>
public sealed class PostgresEnrichmentRunStore(ChronicleafDbContext dbContext) : IEnrichmentRunStore
{
    /// <inheritdoc />
    public async Task<bool> TryInsertAsync(EnrichmentRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        var id = await ChronicleafSql.ScalarAsync<Guid?>(
            dbContext,
            """
            INSERT INTO github_enrichment_runs
                (id, scope_id, operation_id, milestone_id, expected_bundle_revision, owner, repo, created_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7)
            ON CONFLICT (scope_id, operation_id) DO NOTHING
            RETURNING id
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, run.Id),
                new("@p1", NpgsqlDbType.Uuid, run.ScopeId),
                new("@p2", NpgsqlDbType.Uuid, run.OperationId),
                new("@p3", NpgsqlDbType.Uuid, run.MilestoneId),
                new("@p4", NpgsqlDbType.Bigint, run.ExpectedBundleRevision),
                new("@p5", NpgsqlDbType.Varchar, run.Owner),
                new("@p6", NpgsqlDbType.Varchar, run.Repo),
                new("@p7", NpgsqlDbType.TimestampTz, run.CreatedAt.UtcDateTime),
            ],
            cancellationToken).ConfigureAwait(false);

        return id is not null;
    }

    /// <inheritdoc />
    public async Task<EnrichmentRun?> FindByOperationAsync(
        Guid scopeId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync(
            dbContext,
            """
            SELECT id, scope_id, operation_id, milestone_id, expected_bundle_revision, owner, repo, created_at
            FROM github_enrichment_runs WHERE scope_id = @p0 AND operation_id = @p1
            """,
            [new("@p0", NpgsqlDbType.Uuid, scopeId), new("@p1", NpgsqlDbType.Uuid, operationId)],
            Read,
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    private static EnrichmentRun Read(DbDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetGuid(3),
            reader.GetInt64(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetFieldValue<DateTimeOffset>(7));
}
