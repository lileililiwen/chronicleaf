using System.Data.Common;
using Chronicleaf.Application.GitHub;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Projects;
using Chronicleaf.Infrastructure.Persistence;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.GitHub;

/// <summary>
/// The PostgreSQL store for the collection run log (GITHUB-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The insert is <c>ON CONFLICT DO NOTHING RETURNING</c> on the operation's unique index, so a
/// replayed request - the same idempotency key, the same job - adds no second row for one
/// collection. That is the same shape the delivered preview store uses to let the database decide a
/// race rather than a read this method did first.
/// </para>
/// <para>
/// <b>The completion is a single conditional update.</b> It moves a run from requested to completed
/// and changes no row when one already was, so a second attempt on the same operation cannot
/// overwrite what the first attempt recorded about what it read. The limitation columns are written
/// by the database from the same statement, which is what makes a truncated run recognisable as
/// truncated without reading the bundle it appended to.
/// </para>
/// </remarks>
public sealed class PostgresSyncSourceStore(ChronicleafDbContext dbContext) : ISyncSourceStore
{
    private const string Columns =
        """
        id, scope_id, operation_id, milestone_id, expected_bundle_revision, owner, repo,
        head_ref, base_ref, release_tag, issue_number, pull_request_number,
        base_sha, head_sha, endpoints, fetched_at, commit_count, changed_file_count,
        truncated, excluded_file_count, completed_at, created_at
        """;

    /// <inheritdoc />
    public async Task<bool> TryInsertAsync(SyncSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var id = await ChronicleafSql.ScalarAsync<Guid?>(
            dbContext,
            """
            INSERT INTO sync_sources
                (id, scope_id, operation_id, milestone_id, expected_bundle_revision, owner, repo,
                 head_ref, base_ref, release_tag, issue_number, pull_request_number, created_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12)
            ON CONFLICT (scope_id, operation_id) DO NOTHING
            RETURNING id
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, source.Id),
                new("@p1", NpgsqlDbType.Uuid, source.ScopeId),
                new("@p2", NpgsqlDbType.Uuid, source.OperationId),
                new("@p3", NpgsqlDbType.Uuid, source.MilestoneId),
                new("@p4", NpgsqlDbType.Bigint, source.ExpectedBundleRevision),
                new("@p5", NpgsqlDbType.Varchar, source.Owner),
                new("@p6", NpgsqlDbType.Varchar, source.Repo),
                new("@p7", NpgsqlDbType.Varchar, source.HeadRef),
                new("@p8", NpgsqlDbType.Varchar, source.BaseRef is { } baseRef ? baseRef : DBNull.Value),
                new("@p9", NpgsqlDbType.Varchar, source.ReleaseTag is { } tag ? tag : DBNull.Value),
                new("@p10", NpgsqlDbType.Integer, source.IssueNumber is { } issue ? issue : DBNull.Value),
                new("@p11", NpgsqlDbType.Integer, source.PullRequestNumber is { } pull ? pull : DBNull.Value),
                new("@p12", NpgsqlDbType.TimestampTz, source.CreatedAt.UtcDateTime),
            ],
            cancellationToken).ConfigureAwait(false);

        return id is not null;
    }

    /// <inheritdoc />
    public async Task<SyncSource?> FindByOperationAsync(
        Guid scopeId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync(
            dbContext,
            $"SELECT {Columns} FROM sync_sources WHERE scope_id = @p0 AND operation_id = @p1",
            [new("@p0", NpgsqlDbType.Uuid, scopeId), new("@p1", NpgsqlDbType.Uuid, operationId)],
            Read,
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async Task<bool> TryCompleteAsync(SyncSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        // `completed_at IS NULL` is the whole condition. One run, one completion, and a second
        // attempt on the same operation changes no row and is told so.
        var affected = await ChronicleafSql.ExecuteAsync(
            dbContext,
            """
            UPDATE sync_sources
            SET base_sha = @p0,
                head_sha = @p1,
                endpoints = @p2,
                fetched_at = @p3,
                commit_count = @p4,
                changed_file_count = @p5,
                truncated = @p6,
                excluded_file_count = @p7,
                completed_at = @p3
            WHERE scope_id = @p8 AND operation_id = @p9 AND completed_at IS NULL
            """,
            [
                new("@p0", NpgsqlDbType.Varchar, source.BaseSha is { } baseSha ? baseSha : DBNull.Value),
                new("@p1", NpgsqlDbType.Varchar, source.HeadSha!),
                new("@p2", NpgsqlDbType.Varchar, source.Endpoints!),
                new("@p3", NpgsqlDbType.TimestampTz, source.FetchedAt!.Value.UtcDateTime),
                new("@p4", NpgsqlDbType.Integer, source.CommitCount),
                new("@p5", NpgsqlDbType.Integer, source.ChangedFileCount),
                new("@p6", NpgsqlDbType.Boolean, source.Truncated),
                new("@p7", NpgsqlDbType.Integer, source.ExcludedFileCount),
                new("@p8", NpgsqlDbType.Uuid, source.ScopeId),
                new("@p9", NpgsqlDbType.Uuid, source.OperationId),
            ],
            cancellationToken).ConfigureAwait(false);

        return affected == 1;
    }

    private static SyncSource Read(DbDataReader reader)
    {
        var source = new SyncSource(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetGuid(3),
            reader.GetInt64(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetInt32(10),
            reader.IsDBNull(11) ? null : reader.GetInt32(11),
            reader.GetFieldValue<DateTimeOffset>(21));

        if (reader.IsDBNull(13))
        {
            return source;
        }

        source.Complete(
            reader.IsDBNull(12) ? null : reader.GetString(12),
            reader.GetString(13),
            reader.GetString(14),
            reader.GetFieldValue<DateTimeOffset>(15),
            reader.GetInt32(16),
            reader.GetInt32(17),
            reader.GetBoolean(18),
            reader.GetInt32(19));

        return source;
    }
}
