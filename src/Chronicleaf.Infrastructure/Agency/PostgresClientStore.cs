using Chronicleaf.Application.Agency;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.Agency;

/// <summary>
/// The PostgreSQL Client store (AGENCY-CLIENT-PROJECTS-001).
/// </summary>
/// <remarks>
/// Reads are ordinary queries and every one of them carries the scope, so a lookup by id can only
/// ever return a record inside the caller's own installation. Writes are explicit statements whose
/// WHERE clause is the whole safety property: <c>revision = @expected AND archived_at IS NULL</c>.
/// That is what makes two edits of the same revision decide between themselves instead of both
/// landing, and it is what makes an archived client refuse an edit in the database rather than only
/// in the service.
/// <para>
/// Delete is not offered and cannot be offered: archiving stamps a column and advances the
/// revision, which leaves the client and every project grouped under it exactly as they were.
/// </para>
/// <para>
/// The context revision is the one counter this store moves without a caller's revision: a project
/// membership change advances it on both sides, so a report context pinned to the old generation
/// no longer matches while a client edit the owner holds stays current.
/// </para>
/// </remarks>
public sealed class PostgresClientStore(ChronicleafDbContext dbContext) : IClientStore
{
    public Task<Client?> FindAsync(Guid scopeId, Guid clientId, CancellationToken cancellationToken = default) =>
        dbContext.Clients
            .AsNoTracking()
            .Where(client => client.Id == clientId && client.ScopeId == scopeId)
            .SingleOrDefaultAsync(cancellationToken);

    public Task<IReadOnlyList<Client>> ListAsync(
        Guid scopeId,
        string? afterName,
        Guid? afterId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        // The page boundary is a row comparison in PostgreSQL, which orders by the same key the
        // ORDER BY uses. That is what makes a cursor a position rather than an offset: a client
        // created while the owner is paging does not shift a later page, and a name that is a prefix
        // of another is neither skipped nor repeated.
        var positioned = afterName is not null && afterId is not null;

        var sql = $"""
            SELECT {ClientRowReader.Columns}
            FROM clients
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

        return ChronicleafSql.QueryAsync(dbContext, sql, parameters, ClientRowReader.Read, cancellationToken);
    }

    public async Task<bool> InsertAsync(Client client, CancellationToken cancellationToken = default)
    {
        var inserted = await ChronicleafSql.ScalarAsync<Guid>(
            dbContext,
            """
            INSERT INTO clients (id, scope_id, name, notes, revision, context_revision, archived_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, NULL)
            ON CONFLICT (id) DO NOTHING
            RETURNING id
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, client.Id),
                new("@p1", NpgsqlDbType.Uuid, client.ScopeId),
                new("@p2", NpgsqlDbType.Varchar, client.Name),
                new("@p3", NpgsqlDbType.Varchar, client.Notes),
                new("@p4", NpgsqlDbType.Bigint, client.Revision),
                new("@p5", NpgsqlDbType.Bigint, client.ContextRevision),
            ],
            cancellationToken);

        return inserted != Guid.Empty;
    }

    public Task<int> UpdateAsync(
        Guid scopeId,
        Guid clientId,
        long expectedRevision,
        string name,
        string notes,
        CancellationToken cancellationToken = default) =>
        ChronicleafSql.ExecuteAsync(
            dbContext,
            """
            UPDATE clients
            SET name = @p2, notes = @p3, revision = revision + 1
            WHERE id = @p0 AND scope_id = @p1 AND revision = @p4 AND archived_at IS NULL
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, clientId),
                new("@p1", NpgsqlDbType.Uuid, scopeId),
                new("@p2", NpgsqlDbType.Varchar, name),
                new("@p3", NpgsqlDbType.Varchar, notes),
                new("@p4", NpgsqlDbType.Bigint, expectedRevision),
            ],
            cancellationToken);

    public Task<int> ArchiveAsync(
        Guid scopeId,
        Guid clientId,
        long expectedRevision,
        DateTimeOffset archivedAt,
        CancellationToken cancellationToken = default) =>
        ChronicleafSql.ExecuteAsync(
            dbContext,
            // Nothing is deleted. The stamp makes the client refuse new assignments and the revision
            // bump tells an owner that still holds the old revision that its view is stale. Every
            // project grouped under the client stays exactly where it is.
            """
            UPDATE clients
            SET archived_at = @p2, revision = revision + 1
            WHERE id = @p0 AND scope_id = @p1 AND revision = @p3 AND archived_at IS NULL
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, clientId),
                new("@p1", NpgsqlDbType.Uuid, scopeId),
                new("@p2", NpgsqlDbType.TimestampTz, archivedAt.ToUniversalTime()),
                new("@p3", NpgsqlDbType.Bigint, expectedRevision),
            ],
            cancellationToken);

    public Task<int> TouchContextAsync(
        Guid scopeId,
        Guid clientId,
        CancellationToken cancellationToken = default) =>
        ChronicleafSql.ExecuteAsync(
            dbContext,
            // The membership generation moves and nothing else does: a report context that pinned
            // the old generation is invalidated, while a client edit the owner holds stays current.
            """
            UPDATE clients
            SET context_revision = context_revision + 1
            WHERE id = @p0 AND scope_id = @p1
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, clientId),
                new("@p1", NpgsqlDbType.Uuid, scopeId),
            ],
            cancellationToken);
}
