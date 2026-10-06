using Chronicleaf.Application.GitHub;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.GitHub;

/// <summary>
/// The PostgreSQL store for scoped GitHub App installation bindings
/// (GITHUB-APP-CONNECTION-001).
/// </summary>
/// <remarks>
/// <para>
/// Every statement is scoped in its <c>WHERE</c> clause, except the webhook's lookup by
/// installation id, which is how a server-to-server delivery finds the scope it belongs to. That
/// lookup returns the binding and nothing secret: the row holds no token and no secret of any
/// kind.
/// </para>
/// </remarks>
public sealed class PostgresGitHubAppInstallationStore(ChronicleafDbContext dbContext) : IGitHubAppInstallationStore
{
    private const string Columns =
        "id, scope_id, installation_id, account_login, repository_selection, status, created_at, updated_at";

    /// <inheritdoc />
    public async Task<GitHubAppInstallation?> FindAsync(
        Guid scopeId,
        long installationId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync(
            dbContext,
            $"SELECT {Columns} FROM github_app_installations WHERE scope_id = @p0 AND installation_id = @p1",
            [new("@p0", NpgsqlDbType.Uuid, scopeId), new("@p1", NpgsqlDbType.Bigint, installationId)],
            Read,
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async Task<GitHubAppInstallation?> FindByInstallationIdAsync(
        long installationId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync(
            dbContext,
            $"SELECT {Columns} FROM github_app_installations WHERE installation_id = @p0",
            [new("@p0", NpgsqlDbType.Bigint, installationId)],
            Read,
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GitHubAppInstallation>> ListAsync(
        Guid scopeId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync(
            dbContext,
            $"SELECT {Columns} FROM github_app_installations WHERE scope_id = @p0 ORDER BY installation_id",
            [new("@p0", NpgsqlDbType.Uuid, scopeId)],
            Read,
            cancellationToken).ConfigureAwait(false);

        return rows;
    }

    /// <inheritdoc />
    public async Task<GitHubAppInstallation> UpsertAsync(
        GitHubAppInstallation installation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installation);

        // The unique index on installation_id is what refuses a second scope's attach of the same
        // installation: the first binding wins and the second conflicts rather than shares.
        await ChronicleafSql.ExecuteAsync(
            dbContext,
            """
            INSERT INTO github_app_installations
                (id, scope_id, installation_id, account_login, repository_selection, status, created_at, updated_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7)
            ON CONFLICT (installation_id) DO UPDATE
            SET scope_id = EXCLUDED.scope_id,
                account_login = EXCLUDED.account_login,
                repository_selection = EXCLUDED.repository_selection,
                status = EXCLUDED.status,
                updated_at = EXCLUDED.updated_at
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, installation.Id),
                new("@p1", NpgsqlDbType.Uuid, installation.ScopeId),
                new("@p2", NpgsqlDbType.Bigint, installation.InstallationId),
                new("@p3", NpgsqlDbType.Varchar, installation.AccountLogin),
                new("@p4", NpgsqlDbType.Varchar, installation.RepositorySelection),
                new("@p5", NpgsqlDbType.Varchar, installation.Status),
                new("@p6", NpgsqlDbType.TimestampTz, installation.CreatedAt.UtcDateTime),
                new("@p7", NpgsqlDbType.TimestampTz, installation.UpdatedAt.UtcDateTime),
            ],
            cancellationToken).ConfigureAwait(false);

        return await FindAsync(installation.ScopeId, installation.InstallationId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The stored installation could not be read back.");
    }

    private static GitHubAppInstallation Read(System.Data.Common.DbDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetInt64(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetFieldValue<DateTimeOffset>(7));
}
