using Chronicleaf.Application.Operations;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.Operations;

/// <summary>
/// The scoped read side of a durable operation.
/// </summary>
/// <remarks>
/// Every query carries the scope, so an id belonging to another installation answers nothing rather
/// than answering wrongly. The list is a page of the scope's newest operations and is bounded by the
/// shared list policy before it reaches the database, so no caller can turn it into a full table scan
/// by asking for a large page.
/// </remarks>
public sealed class PostgresOperationQuery(ChronicleafDbContext dbContext) : IOperationQuery
{
    private const string Columns = """
        id, scope_id, kind, target_id, input_revision, idempotency_key, fingerprint, status, attempt,
        heartbeat_at, error_code, result_ref, lease_expires_at, lease_token, next_attempt_at,
        completed_at, cancel_requested_at, lease_recoveries, created_at, updated_at, revision
        """;

    public async Task<Operation?> FindAsync(
        Guid scopeId,
        Guid operationId,
        CancellationToken cancellationToken = default) =>
        (await ChronicleafSql.QueryAsync(
            dbContext,
            $"SELECT {Columns} FROM operations WHERE scope_id = @p0 AND id = @p1",
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, operationId),
            ],
            OperationRowReader.Read,
            cancellationToken).ConfigureAwait(false)).FirstOrDefault();

    public async Task<IReadOnlyList<Operation>> ListAsync(
        Guid scopeId,
        int? limit = null,
        CancellationToken cancellationToken = default) =>
        await ChronicleafSql.QueryAsync(
            dbContext,
            $"""
            SELECT {Columns}
            FROM operations
            WHERE scope_id = @p0
            ORDER BY created_at DESC, id DESC
            LIMIT @p1
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Integer, Application.Common.ListQueryPolicy.Clamp(limit)),
            ],
            OperationRowReader.Read,
            cancellationToken).ConfigureAwait(false);
}
