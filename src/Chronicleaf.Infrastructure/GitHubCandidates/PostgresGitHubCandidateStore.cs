using System.Data.Common;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.GitHubCandidates;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.GitHubCandidates;

/// <summary>
/// The PostgreSQL store for tag- and event-derived Milestone candidates
/// (GITHUB-RELEASE-CANDIDATES-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>"Exactly one candidate per source key" is a fact about the database.</b> The insert is
/// <c>ON CONFLICT (scope_id, source_key) DO NOTHING RETURNING</c> on the unique index, and the caller
/// counts the difference between a returned id and no row as "already proposed". That is the same
/// shape the delivered run log uses, and it is the reason a tag and a release naming one commit are
/// two separately attributed candidates rather than one collapsed row: their keys differ, so each is
/// recorded once and the database decides which arrived second.
/// </para>
/// <para>
/// <b>Nothing here updates a candidate.</b> There is no update statement against this table anywhere in
/// the repository, because a candidate is a proposal somebody has not acted on: a row that could be
/// edited in place could stop being a record of what the provider said, and the unique key that makes
/// deduplication work would then be describing a value that had been rewritten under it.
/// </para>
/// <para>
/// <b>There is no milestone column and no bundle column, and there never will be.</b> Confirming a
/// candidate is a different package's decision made by a human; a candidate row that carried a
/// milestone id would be a claim this package cannot make.
/// </para>
/// </remarks>
public sealed class PostgresGitHubCandidateStore(ChronicleafDbContext dbContext) : IGitHubCandidateStore
{
    private const string Columns =
        """
        id, scope_id, connection_id, project_id, source_key, kind, title, revision, source,
        delivery_id, received_at
        """;

    /// <inheritdoc />
    public async Task<(GitHubCandidate Candidate, bool Created)> TryRecordAsync(
        GitHubCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var inserted = await ChronicleafSql.ScalarAsync<Guid?>(
            dbContext,
            """
            INSERT INTO github_candidates
                (id, scope_id, connection_id, project_id, source_key, kind, title, revision, source,
                 delivery_id, received_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10)
            ON CONFLICT (scope_id, source_key) DO NOTHING
            RETURNING id
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, candidate.Id),
                new("@p1", NpgsqlDbType.Uuid, candidate.ScopeId),
                new("@p2", NpgsqlDbType.Uuid, candidate.ConnectionId),
                new("@p3", NpgsqlDbType.Uuid, candidate.ProjectId),
                new("@p4", NpgsqlDbType.Varchar, candidate.SourceKey),
                new("@p5", NpgsqlDbType.Varchar, candidate.Kind),
                new("@p6", NpgsqlDbType.Varchar, candidate.Title),
                new("@p7", NpgsqlDbType.Varchar, candidate.Revision is { } revision ? revision : DBNull.Value),
                new("@p8", NpgsqlDbType.Varchar, candidate.Source),
                new("@p9", NpgsqlDbType.Varchar, candidate.DeliveryId is { } delivery ? delivery : DBNull.Value),
                new("@p10", NpgsqlDbType.TimestampTz, candidate.ReceivedAt.UtcDateTime),
            ],
            cancellationToken).ConfigureAwait(false);

        if (inserted is not null)
        {
            return (candidate, Created: true);
        }

        // The refusal is not an error. It is the answer "this is already proposed", and the caller
        // counts it as a duplicate rather than raising, because a repository's newest fifty releases
        // being read twice is the normal case rather than a fault.
        var rows = await ChronicleafSql.QueryAsync(
            dbContext,
            $"""
             SELECT {Columns} FROM github_candidates
             WHERE scope_id = @p0 AND source_key = @p1
             """,
            [new("@p0", NpgsqlDbType.Uuid, candidate.ScopeId), new("@p1", NpgsqlDbType.Varchar, candidate.SourceKey)],
            Read,
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0
            ? throw new InvalidOperationException(
                "A candidate conflicted with a row this store could not read back.")
            : (rows[0], Created: false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GitHubCandidate>> ListForProjectAsync(
        Guid scopeId,
        Guid projectId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        // Newest first, on the order the index already carries, and bounded by the delivered list policy
        // rather than by a number this package chose: a candidate list is an owner's review queue and an
        // unbounded one is a way to read a whole repository's history in one request.
        return await ChronicleafSql.QueryAsync(
            dbContext,
            $"""
             SELECT {Columns} FROM github_candidates
             WHERE scope_id = @p0 AND project_id = @p1
             ORDER BY received_at DESC, id DESC
             LIMIT @p2
             """,
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, projectId),
                new("@p2", NpgsqlDbType.Integer, ListQueryPolicy.Clamp(limit)),
            ],
            Read,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<long> CountForProjectAsync(
        Guid scopeId,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        // The count the empty state and the stored state differ by, and it is a `count(*)` in
        // PostgreSQL rather than a materialised list: a project with four thousand candidates must not
        // have four thousand rows built in memory to answer "how many are there".
        var count = await ChronicleafSql.ScalarAsync<long>(
            dbContext,
            """
            SELECT count(*) FROM github_candidates WHERE scope_id = @p0 AND project_id = @p1
            """,
            [new("@p0", NpgsqlDbType.Uuid, scopeId), new("@p1", NpgsqlDbType.Uuid, projectId)],
            cancellationToken).ConfigureAwait(false);

        return count;
    }

    private static GitHubCandidate Read(DbDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.GetGuid(2),
        reader.GetGuid(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.GetFieldValue<DateTimeOffset>(10));
}
