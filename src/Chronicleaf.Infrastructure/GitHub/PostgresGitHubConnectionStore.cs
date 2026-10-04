using System.Data.Common;
using Chronicleaf.Application.GitHub;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Projects;
using Chronicleaf.Infrastructure.Persistence;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.GitHub;

/// <summary>
/// The PostgreSQL store for project repository connections and for the collection run log
/// (GITHUB-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Every statement is scoped in its <c>WHERE</c> clause, so a project or an operation belonging to
/// another installation answers <c>null</c> and the route turns that into a 404 rather than finding
/// a row it should not.
/// </para>
/// <para>
/// <b>Nothing here can hold a token.</b> The connection's <c>credential_ref</c> is the vault's own
/// identifier and is written as a bound parameter of a UUID type; there is no column a secret could
/// be written into, and the reader below maps exactly the columns the domain record has.
/// </para>
/// <para>
/// <b>No URL is ever built or stored.</b> The two name columns are read and written as text and are
/// combined by the caller, which is the only place that decides what a request URL looks like.
/// </para>
/// </remarks>
public sealed class PostgresGitHubConnectionStore(ChronicleafDbContext dbContext) : IGitHubConnectionStore
{
    private const string Columns =
        "id, scope_id, project_id, owner, repo, credential_ref, default_branch, repository_id, created_at, updated_at";

    /// <inheritdoc />
    public async Task<GitHubConnection?> FindAsync(
        Guid scopeId,
        Guid projectId,
        CancellationToken cancellationToken = default) =>
        await ReadAsync(
            dbContext,
            $"SELECT {Columns} FROM github_connections WHERE scope_id = @p0 AND project_id = @p1",
            [new("@p0", NpgsqlDbType.Uuid, scopeId), new("@p1", NpgsqlDbType.Uuid, projectId)],
            cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<GitHubConnection?> FindForMilestoneAsync(
        Guid scopeId,
        Guid milestoneId,
        CancellationToken cancellationToken = default) =>
        await ReadAsync(
            dbContext,
            $"""
            SELECT c.id, c.scope_id, c.project_id, c.owner, c.repo, c.credential_ref,
                   c.default_branch, c.repository_id, c.created_at, c.updated_at
            FROM github_connections c
            JOIN milestones m ON m.project_id = c.project_id
            JOIN projects p ON p.id = c.project_id
            WHERE p.scope_id = @p0 AND m.id = @p1 AND c.scope_id = @p0
            """,
            [new("@p0", NpgsqlDbType.Uuid, scopeId), new("@p1", NpgsqlDbType.Uuid, milestoneId)],
            cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<GitHubConnection> UpsertAsync(
        GitHubConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // The unique index on (scope, project) is what makes a second POST replace the first
        // connection rather than accumulate repositories, and the update keeps the row's id and
        // created_at: a client that has been holding a connection id keeps holding a valid one.
        var id = await ChronicleafSql.ScalarAsync<Guid>(
            dbContext,
            """
            INSERT INTO github_connections
                (id, scope_id, project_id, owner, repo, credential_ref, default_branch, repository_id, created_at, updated_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9)
            ON CONFLICT (scope_id, project_id) DO UPDATE
            SET owner = EXCLUDED.owner,
                repo = EXCLUDED.repo,
                credential_ref = EXCLUDED.credential_ref,
                default_branch = EXCLUDED.default_branch,
                repository_id = EXCLUDED.repository_id,
                updated_at = EXCLUDED.updated_at
            RETURNING id
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, connection.Id),
                new("@p1", NpgsqlDbType.Uuid, connection.ScopeId),
                new("@p2", NpgsqlDbType.Uuid, connection.ProjectId),
                new("@p3", NpgsqlDbType.Varchar, connection.Owner),
                new("@p4", NpgsqlDbType.Varchar, connection.Repo),
                new("@p5", NpgsqlDbType.Uuid, connection.CredentialRef is { } reference ? reference : DBNull.Value),
                new("@p6", NpgsqlDbType.Varchar, connection.DefaultBranch is { } branch ? branch : DBNull.Value),
                new("@p7", NpgsqlDbType.Bigint, connection.RepositoryId is { } repositoryId ? repositoryId : DBNull.Value),
                new("@p8", NpgsqlDbType.TimestampTz, connection.CreatedAt.UtcDateTime),
                new("@p9", NpgsqlDbType.TimestampTz, connection.UpdatedAt.UtcDateTime),
            ],
            cancellationToken).ConfigureAwait(false);

        return await FindAsync(connection.ScopeId, connection.ProjectId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The stored connection could not be read back.");
    }

    private static async Task<GitHubConnection?> ReadAsync(
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

    private static GitHubConnection Read(DbDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetGuid(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetInt64(7),
            reader.GetFieldValue<DateTimeOffset>(8),
            reader.GetFieldValue<DateTimeOffset>(9));
}
