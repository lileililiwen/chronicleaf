using System.Data.Common;
using Chronicleaf.Application.Git;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Projects;
using Chronicleaf.Infrastructure.Persistence;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.Git;

/// <summary>
/// The PostgreSQL store for registered local repositories and for the local collection run log
/// (LOCAL-GIT-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Every statement is scoped in its <c>WHERE</c> clause, so a project or an operation belonging to another
/// installation answers <c>null</c> and the route turns that into a 404 rather than finding a row it should
/// not.
/// </para>
/// <para>
/// <b>There is no column an absolute path could be written into.</b> The two address columns are the
/// operator's alias and the relative path, both of which the owner chose and both of which are already
/// public to that owner. The registered directory lives in configuration and is never bound as a parameter,
/// so a read of either table learns nothing about the machine this process runs on.
/// </para>
/// <para>
/// <b>There is no credential column either</b>, and not because this store refuses one: a local collection
/// reads a directory with the operator's own Git and has no remote to authenticate to, so there is no value
/// this package could be given. The absence is a property of the feature rather than a guard on it.
/// </para>
/// </remarks>
public sealed class PostgresLocalRepositoryStore(ChronicleafDbContext dbContext) : ILocalRepositoryStore
{
    private const string Columns =
        "id, scope_id, project_id, root_alias, relative_path, default_branch, head_sha, created_at, updated_at";

    /// <inheritdoc />
    public async Task<LocalRepository?> FindAsync(
        Guid scopeId,
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        await ReadAsync(
            dbContext,
            $"SELECT {Columns} FROM local_repositories WHERE scope_id = @p0 AND project_id = @p1",
            [new("@p0", NpgsqlDbType.Uuid, scopeId), new("@p1", NpgsqlDbType.Uuid, projectId)],
            cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<LocalRepository?> FindForMilestoneAsync(
        Guid scopeId,
        Guid milestoneId,
        CancellationToken cancellationToken = default) =>
        await ReadAsync(
            dbContext,
            $"""
            SELECT r.id, r.scope_id, r.project_id, r.root_alias, r.relative_path, r.default_branch,
                   r.head_sha, r.created_at, r.updated_at
            FROM local_repositories r
            JOIN milestones m ON m.project_id = r.project_id
            JOIN projects p ON p.id = r.project_id
            WHERE p.scope_id = @p0 AND m.id = @p1 AND r.scope_id = @p0
            """,
            [new("@p0", NpgsqlDbType.Uuid, scopeId), new("@p1", NpgsqlDbType.Uuid, milestoneId)],
            cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<LocalRepository> UpsertAsync(
        LocalRepository repository,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(repository);

        // The unique index on (scope, project) is what makes a second POST replace the first registration
        // rather than accumulate repositories, and the update keeps the row's id and created_at: a client that
        // has been holding a repository id keeps holding a valid one.
        var id = await ChronicleafSql.ScalarAsync<Guid>(
            dbContext,
            """
            INSERT INTO local_repositories
                (id, scope_id, project_id, root_alias, relative_path, default_branch, head_sha, created_at, updated_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8)
            ON CONFLICT (scope_id, project_id) DO UPDATE
            SET root_alias = EXCLUDED.root_alias,
                relative_path = EXCLUDED.relative_path,
                default_branch = EXCLUDED.default_branch,
                head_sha = EXCLUDED.head_sha,
                updated_at = EXCLUDED.updated_at
            RETURNING id
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, repository.Id),
                new("@p1", NpgsqlDbType.Uuid, repository.ScopeId),
                new("@p2", NpgsqlDbType.Uuid, repository.ProjectId),
                new("@p3", NpgsqlDbType.Varchar, repository.RootAlias),
                new("@p4", NpgsqlDbType.Varchar, repository.RelativePath),
                new("@p5", NpgsqlDbType.Varchar, repository.DefaultBranch is { } branch ? branch : DBNull.Value),
                new("@p6", NpgsqlDbType.Varchar, repository.HeadSha is { } head ? head : DBNull.Value),
                new("@p7", NpgsqlDbType.TimestampTz, repository.CreatedAt.UtcDateTime),
                new("@p8", NpgsqlDbType.TimestampTz, repository.UpdatedAt.UtcDateTime),
            ],
            cancellationToken).ConfigureAwait(false);

        _ = id;

        return await FindAsync(repository.ScopeId, repository.ProjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The stored local repository could not be read back.");
    }

    private static async Task<LocalRepository?> ReadAsync(
        ChronicleafDbContext dbContext,
        string sql,
        IReadOnlyList<SqlParameter> parameters,
        CancellationToken cancellationToken)
    {
        var rows = await ChronicleafSql
            .QueryAsync(dbContext, sql, parameters, Read, cancellationToken)
            .ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    private static LocalRepository Read(DbDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.GetFieldValue<DateTimeOffset>(8));
}

/// <summary>
/// The PostgreSQL store for the local collection run log (LOCAL-GIT-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The run row exists because a job payload carries an operation id and nothing else. The words the owner
/// typed - which root, which refs - therefore have to be durable somewhere the worker can read them without
/// trusting a queue message, and a run that was asked for and never collected is a true statement about a
/// row rather than a placeholder standing in for a fact.
/// </para>
/// <para>
/// <b>Completion is conditional, and the condition is in the SQL.</b> <c>completed_at IS NULL</c> means a
/// second attempt on the same operation cannot overwrite the first run's record of what it read, which is a
/// claim about one collection and not a number to be revised.
/// </para>
/// <para>
/// <b>The command log holds verbs, never operands.</b> The column is the list of Git subcommands that ran,
/// which is what a reader needs in order to know what kind of reads this run made. Their arguments - the
/// operator's root, a ref an owner typed, a path inside a shared machine - are not written anywhere, so
/// this table cannot become a map of a filesystem.
/// </para>
/// </remarks>
public sealed class PostgresLocalSyncSourceStore(ChronicleafDbContext dbContext) : ILocalSyncSourceStore
{
    /// <inheritdoc />
    public async Task<bool> TryInsertAsync(LocalSyncSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var inserted = await ChronicleafSql.ScalarAsync<Guid>(
            dbContext,
            """
            INSERT INTO local_sync_sources
                (id, scope_id, operation_id, milestone_id, expected_bundle_revision, root_alias, relative_path,
                 head_ref, base_ref, created_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9)
            ON CONFLICT (scope_id, operation_id) DO NOTHING
            RETURNING id
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, source.Id),
                new("@p1", NpgsqlDbType.Uuid, source.ScopeId),
                new("@p2", NpgsqlDbType.Uuid, source.OperationId),
                new("@p3", NpgsqlDbType.Uuid, source.MilestoneId),
                new("@p4", NpgsqlDbType.Bigint, source.ExpectedBundleRevision),
                new("@p5", NpgsqlDbType.Varchar, source.RootAlias),
                new("@p6", NpgsqlDbType.Varchar, source.RelativePath),
                new("@p7", NpgsqlDbType.Varchar, source.HeadRef),
                new("@p8", NpgsqlDbType.Varchar, source.BaseRef is { } baseRef ? baseRef : DBNull.Value),
                new("@p9", NpgsqlDbType.TimestampTz, source.CreatedAt.UtcDateTime),
            ],
            cancellationToken).ConfigureAwait(false);

        return inserted != Guid.Empty;
    }

    /// <inheritdoc />
    public async Task<LocalSyncSource?> FindByOperationAsync(
        Guid scopeId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql
            .QueryAsync(
                dbContext,
                """
                SELECT id, scope_id, operation_id, milestone_id, expected_bundle_revision, root_alias, relative_path,
                       head_ref, base_ref, base_sha, head_sha, command_log, fetched_at, commit_count,
                       changed_file_count, truncated, excluded_file_count, uncommitted_count, completed_at, created_at
                FROM local_sync_sources
                WHERE scope_id = @p0 AND operation_id = @p1
                """,
                [new("@p0", NpgsqlDbType.Uuid, scopeId), new("@p1", NpgsqlDbType.Uuid, operationId)],
                Read,
                cancellationToken)
            .ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async Task<bool> TryCompleteAsync(LocalSyncSource source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var updated = await ChronicleafSql.ScalarAsync<Guid>(
            dbContext,
            """
            UPDATE local_sync_sources
            SET base_sha = @p2,
                head_sha = @p3,
                command_log = @p4,
                fetched_at = @p5,
                commit_count = @p6,
                changed_file_count = @p7,
                truncated = @p8,
                excluded_file_count = @p9,
                uncommitted_count = @p10,
                completed_at = @p5
            WHERE id = @p0 AND scope_id = @p1 AND completed_at IS NULL
            RETURNING id
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, source.Id),
                new("@p1", NpgsqlDbType.Uuid, source.ScopeId),
                new("@p2", NpgsqlDbType.Varchar, source.BaseSha is { } baseSha ? baseSha : DBNull.Value),
                new("@p3", NpgsqlDbType.Varchar, source.HeadSha is { } headSha ? headSha : DBNull.Value),
                new("@p4", NpgsqlDbType.Varchar, source.CommandLog is { } log ? log : DBNull.Value),
                new("@p5", NpgsqlDbType.TimestampTz, source.FetchedAt is { } at ? at.UtcDateTime : DBNull.Value),
                new("@p6", NpgsqlDbType.Integer, source.CommitCount),
                new("@p7", NpgsqlDbType.Integer, source.ChangedFileCount),
                new("@p8", NpgsqlDbType.Boolean, source.Truncated),
                new("@p9", NpgsqlDbType.Integer, source.ExcludedFileCount),
                new("@p10", NpgsqlDbType.Integer, source.UncommittedCount),
            ],
            cancellationToken).ConfigureAwait(false);

        return updated != Guid.Empty;
    }

    private static LocalSyncSource Read(DbDataReader reader)
    {
        var source = new LocalSyncSource(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetGuid(3),
            reader.GetInt64(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetFieldValue<DateTimeOffset>(19));

        // The completion columns are applied through the record's own transition rather than by constructing a
        // second shape, so a row that is already complete is a state the reader of this table can see and this
        // code cannot produce by accident.
        var completedAt = reader.IsDBNull(18) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(18);
        var baseSha = reader.IsDBNull(9) ? null : reader.GetString(9);
        var headSha = reader.IsDBNull(10) ? null : reader.GetString(10);
        var commandLog = reader.IsDBNull(11) ? null : reader.GetString(11);
        var fetchedAt = reader.IsDBNull(12) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(12);

        if (completedAt is { } finished && headSha is { } resolved && commandLog is { } commands)
        {
            source.Complete(
                baseSha,
                resolved,
                commands,
                fetchedAt ?? finished,
                reader.GetInt32(13),
                reader.GetInt32(14),
                reader.GetBoolean(15),
                reader.GetInt32(16),
                reader.GetInt32(17));
        }

        return source;
    }
}
