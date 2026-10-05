using System.Data.Common;
using Chronicleaf.Application.GitHubCandidates;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.GitHubCandidates;

/// <summary>
/// The PostgreSQL store for the candidate poll run log (GITHUB-RELEASE-CANDIDATES-001).
/// </summary>
/// <remarks>
/// <para>
/// <b>The row exists because a job payload carries an operation id and nothing else.</b> A worker is
/// handed the operation and has to find the window the owner typed, the project revision it was asked
/// against and the repository names it was to read, so those words have to be durable somewhere the
/// worker can read them without trusting a queue message. The insert is
/// <c>ON CONFLICT (scope_id, operation_id) DO NOTHING RETURNING</c>, so a replay under the same
/// idempotency key changes no row and is told it did not create one.
/// </para>
/// <para>
/// <b>The completion is one conditional update.</b> <c>WHERE completed_at IS NULL</c> is the whole
/// condition: a poll is a statement about one collection, and a second attempt on the same operation
/// must not overwrite what the first recorded about what it read.
/// </para>
/// <para>
/// <b>A row that has completed has its counts, and one that has not has none of them.</b> The four
/// count columns default to zero in the database as well as in the record, so "asked for and not yet
/// polled" is a state the table can hold without this store inventing numbers it has not read. The
/// check constraint is on the completion pair rather than on any one column, so half a completion
/// cannot be stored at all.
/// </para>
/// </remarks>
public sealed class PostgresGitHubCandidateRunStore(ChronicleafDbContext dbContext) : IGitHubCandidateRunStore
{
    private const string Columns =
        """
        id, scope_id, operation_id, project_id, expected_project_revision, owner, repo, since,
        head_sha, endpoints, fetched_at, release_count, tag_count, candidate_count, recorded_count,
        completed_at, created_at
        """;

    /// <inheritdoc />
    public async Task<bool> TryInsertAsync(GitHubCandidateRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        var inserted = await ChronicleafSql.ScalarAsync<Guid?>(
            dbContext,
            """
            INSERT INTO github_candidate_runs
                (id, scope_id, operation_id, project_id, expected_project_revision, owner, repo,
                 since, created_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8)
            ON CONFLICT (scope_id, operation_id) DO NOTHING
            RETURNING id
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, run.Id),
                new("@p1", NpgsqlDbType.Uuid, run.ScopeId),
                new("@p2", NpgsqlDbType.Uuid, run.OperationId),
                new("@p3", NpgsqlDbType.Uuid, run.ProjectId),
                new("@p4", NpgsqlDbType.Bigint, run.ExpectedProjectRevision),
                new("@p5", NpgsqlDbType.Varchar, run.Owner),
                new("@p6", NpgsqlDbType.Varchar, run.Repo),
                new("@p7", NpgsqlDbType.TimestampTz, run.Since is { } since ? since.UtcDateTime : DBNull.Value),
                new("@p8", NpgsqlDbType.TimestampTz, run.CreatedAt.UtcDateTime),
            ],
            cancellationToken).ConfigureAwait(false);

        return inserted is not null;
    }

    /// <inheritdoc />
    public async Task<GitHubCandidateRun?> FindByOperationAsync(
        Guid scopeId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync(
            dbContext,
            $"""
             SELECT {Columns} FROM github_candidate_runs
             WHERE scope_id = @p0 AND operation_id = @p1
             """,
            [new("@p0", NpgsqlDbType.Uuid, scopeId), new("@p1", NpgsqlDbType.Uuid, operationId)],
            Read,
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async Task<bool> TryCompleteAsync(GitHubCandidateRun run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(run);

        var affected = await ChronicleafSql.ExecuteAsync(
            dbContext,
            """
            UPDATE github_candidate_runs
            SET head_sha = @p0,
                endpoints = @p1,
                fetched_at = @p2,
                release_count = @p3,
                tag_count = @p4,
                candidate_count = @p5,
                recorded_count = @p6,
                completed_at = @p2
            WHERE scope_id = @p7 AND operation_id = @p8 AND completed_at IS NULL
            """,
            [
                new("@p0", NpgsqlDbType.Varchar, run.HeadSha is { } head ? head : DBNull.Value),
                new("@p1", NpgsqlDbType.Varchar, run.Endpoints!),
                new("@p2", NpgsqlDbType.TimestampTz, run.FetchedAt!.Value.UtcDateTime),
                new("@p3", NpgsqlDbType.Integer, run.ReleaseCount),
                new("@p4", NpgsqlDbType.Integer, run.TagCount),
                new("@p5", NpgsqlDbType.Integer, run.CandidateCount),
                new("@p6", NpgsqlDbType.Integer, run.RecordedCount),
                new("@p7", NpgsqlDbType.Uuid, run.ScopeId),
                new("@p8", NpgsqlDbType.Uuid, run.OperationId),
            ],
            cancellationToken).ConfigureAwait(false);

        return affected == 1;
    }

    private static GitHubCandidateRun Read(DbDataReader reader)
    {
        var run = new GitHubCandidateRun(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetGuid(3),
            reader.GetInt64(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
            reader.GetFieldValue<DateTimeOffset>(16));

        if (reader.IsDBNull(9))
        {
            return run;
        }

        run.Complete(
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetString(9),
            reader.GetFieldValue<DateTimeOffset>(10),
            reader.GetInt32(11),
            reader.GetInt32(12),
            reader.GetInt32(13),
            reader.GetInt32(14));

        return run;
    }
}
