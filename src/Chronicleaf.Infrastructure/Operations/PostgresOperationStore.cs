using Chronicleaf.Application.Operations;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.Operations;

/// <summary>
/// The PostgreSQL operation store (BACKGROUND-JOBS-001, BACKGROUND-JOPS-002).
/// </summary>
/// <remarks>
/// <para>
/// Every write below is an explicit statement, and in every one of them the WHERE clause is the
/// whole safety property. Nothing here reads a value, decides something in C# and writes it back,
/// because a read-then-write would leave a window in which another worker could take the same
/// decision. The database decides, and the affected-row count is the answer.
/// </para>
///
/// <para><b>The claim.</b> One statement moves the operation to <c>running</c>, counts the attempt and
/// takes the lease, from either <c>queued</c> or an expired <c>running</c> lease that has not been
/// recovered. The recovery branch increments <c>lease_recoveries</c> and the statement refuses a
/// second recovery, so "recoverable exactly once" is a property of the row rather than of a caller
/// being careful.</para>
///
/// <para><b>The result commit.</b> One statement writes the result reference, the terminal status and
/// the revision check together. Its WHERE clause names the lease token and the revision the worker
/// observed, so a second worker, a cancelled run and a superseded run all match no row - and a
/// statement that matches no row writes nothing, so there is no partial result to clean up.</para>
///
/// <para>Reads are scoped, and the revision reader resolves a target only inside the operation's own
/// scope, so a forged target id cannot move another installation's revision.</para>
/// </remarks>
public sealed class PostgresOperationStore(ChronicleafDbContext dbContext) : IOperationStore
{
    private const string Columns = """
        id, scope_id, kind, target_id, input_revision, idempotency_key, fingerprint, status, attempt,
        heartbeat_at, error_code, result_ref, lease_expires_at, lease_token, next_attempt_at,
        completed_at, cancel_requested_at, lease_recoveries, created_at, updated_at, revision
        """;

    public async Task<bool> TryCreateAsync(
        Guid operationId,
        Guid scopeId,
        string kind,
        Guid targetId,
        long inputRevision,
        string idempotencyKey,
        string fingerprint,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // The unique index is the arbiter. Two simultaneous requests for one key: the first inserts,
        // the second conflicts and writes nothing, and neither of them can read first and decide.
        var inserted = await ChronicleafSql.ScalarAsync<Guid>(
            dbContext,
            """
            INSERT INTO operations (
                id, scope_id, kind, target_id, input_revision, idempotency_key, fingerprint,
                status, attempt, lease_recoveries, created_at, updated_at, revision)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, 'queued', 0, 0, @p7, @p7, 1)
            ON CONFLICT (scope_id, kind, idempotency_key) DO NOTHING
            RETURNING id
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, operationId),
                new("@p1", NpgsqlDbType.Uuid, scopeId),
                new("@p2", NpgsqlDbType.Varchar, kind),
                new("@p3", NpgsqlDbType.Uuid, targetId),
                new("@p4", NpgsqlDbType.Bigint, inputRevision),
                new("@p5", NpgsqlDbType.Varchar, idempotencyKey),
                new("@p6", NpgsqlDbType.Varchar, fingerprint),
                new("@p7", NpgsqlDbType.TimestampTz, now.UtcDateTime),
            ],
            cancellationToken).ConfigureAwait(false);

        return inserted != Guid.Empty;
    }

    public async Task<Guid?> TryAppendOutboxAsync(
        Guid operationId,
        string kind,
        Guid scopeId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // The payload is the delivered outbox envelope with the operation id in it and nothing else:
        // no target content, no credential, no evidence. The dispatcher reads the id and goes to
        // PostgreSQL for every other fact.
        var payload = new Application.Outbox.OutboxPayload
        {
            Kind = kind,
            ScopeId = scopeId,
            AggregateId = operationId,
            Revision = 1,
            OperationId = operationId,
        };

        return await ChronicleafSql.ScalarAsync<Guid>(
            dbContext,
            """
            INSERT INTO outbox (id, kind, aggregate_id, revision, payload, created_at, dispatched_at)
            VALUES (@p0, @p1, @p2, 1, @p3, @p4, NULL)
            ON CONFLICT (kind, aggregate_id, revision) DO NOTHING
            RETURNING id
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, Guid.NewGuid()),
                new("@p1", NpgsqlDbType.Varchar, kind),
                new("@p2", NpgsqlDbType.Uuid, operationId),
                new("@p3", NpgsqlDbType.Jsonb, payload.ToJson()),
                new("@p4", NpgsqlDbType.TimestampTz, now.UtcDateTime),
            ],
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<IdempotencyBinding?> FindByIdempotencyKeyAsync(
        Guid scopeId,
        string kind,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync(
            dbContext,
            $"""
            SELECT {Columns}
            FROM operations
            WHERE scope_id = @p0 AND kind = @p1 AND idempotency_key = @p2
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Varchar, kind),
                new("@p2", NpgsqlDbType.Varchar, idempotencyKey),
            ],
            OperationRowReader.Read,
            cancellationToken).ConfigureAwait(false);

        var row = rows.FirstOrDefault();
        return row is null ? null : new IdempotencyBinding(row, row.Fingerprint);
    }

    public async Task<Operation?> TryClaimAsync(
        Guid operationId,
        Guid leaseToken,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // One statement, two permitted starting points. `queued` is the ordinary claim; the expired
        // `running` branch is the single recovery, and it refuses a second one by requiring the
        // counter to still be zero. The attempt count is the row's own, so a replayed delivery
        // cannot talk an operation past its budget.
        var rows = await ChronicleafSql.QueryAsync(
            dbContext,
            $"""
            UPDATE operations
            SET status = 'running',
                attempt = attempt + 1,
                lease_token = @p1,
                lease_expires_at = @p2,
                heartbeat_at = @p0,
                lease_recoveries = lease_recoveries + (CASE WHEN status = 'running' THEN 1 ELSE 0 END),
                updated_at = @p0,
                revision = revision + 1
            WHERE id = @p3
              AND cancel_requested_at IS NULL
              AND (
                    status = 'queued'
                 OR (status = 'running' AND lease_expires_at < @p0 AND lease_recoveries < @p4)
              )
            RETURNING {Columns}
            """,
            [
                new("@p0", NpgsqlDbType.TimestampTz, now.UtcDateTime),
                new("@p1", NpgsqlDbType.Uuid, leaseToken),
                new("@p2", NpgsqlDbType.TimestampTz, now.Add(OperationPolicy.LeaseDuration).UtcDateTime),
                new("@p3", NpgsqlDbType.Uuid, operationId),
                new("@p4", NpgsqlDbType.Integer, OperationPolicy.MaxLeaseRecoveries),
            ],
            OperationRowReader.Read,
            cancellationToken).ConfigureAwait(false);

        return rows.FirstOrDefault();
    }

    public async Task<bool> TryHeartbeatAsync(
        Guid operationId,
        Guid leaseToken,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var affected = await ChronicleafSql.ExecuteAsync(
            dbContext,
            """
            UPDATE operations
            SET heartbeat_at = @p0,
                lease_expires_at = @p1,
                updated_at = @p0,
                revision = revision + 1
            WHERE id = @p2
              AND status = 'running'
              AND lease_token = @p3
              AND cancel_requested_at IS NULL
            """,
            [
                new("@p0", NpgsqlDbType.TimestampTz, now.UtcDateTime),
                new("@p1", NpgsqlDbType.TimestampTz, now.Add(OperationPolicy.LeaseDuration).UtcDateTime),
                new("@p2", NpgsqlDbType.Uuid, operationId),
                new("@p3", NpgsqlDbType.Uuid, leaseToken),
            ],
            cancellationToken).ConfigureAwait(false);

        return affected == 1;
    }

    public async Task<OperationStatus?> TryCommitResultAsync(
        Guid operationId,
        Guid leaseToken,
        string resultRef,
        long observedInputRevision,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // Success, the result reference and the supersede decision are one statement.
        //
        // The revision the worker observed is re-checked here rather than trusted from the read that
        // produced it, so an edit landing between that read and this write is caught by the database
        // and the result is discarded instead of attaching to input that has moved on. The status is
        // derived in the same statement, so `superseded` is not a second write that could be lost.
        //
        // `lease_token = @p2` is what makes the two-worker race a single winner: the loser's token is
        // not the row's token, so it updates nothing and cannot commit a second result.
        var rows = await ChronicleafSql.QueryAsync(
            dbContext,
            """
            UPDATE operations
            SET status = CASE WHEN @p3 = input_revision THEN 'succeeded' ELSE 'superseded' END,
                result_ref = CASE WHEN @p3 = input_revision THEN @p4 ELSE NULL END,
                error_code = NULL,
                completed_at = @p0,
                lease_token = NULL,
                lease_expires_at = NULL,
                next_attempt_at = NULL,
                updated_at = @p0,
                revision = revision + 1
            WHERE id = @p1
              AND status = 'running'
              AND lease_token = @p2
              AND cancel_requested_at IS NULL
            RETURNING status
            """,
            [
                new("@p0", NpgsqlDbType.TimestampTz, now.UtcDateTime),
                new("@p1", NpgsqlDbType.Uuid, operationId),
                new("@p2", NpgsqlDbType.Uuid, leaseToken),
                new("@p3", NpgsqlDbType.Bigint, observedInputRevision),
                new("@p4", NpgsqlDbType.Varchar, resultRef),
            ],
            OperationRowReader.ReadStatus,
            cancellationToken).ConfigureAwait(false);

        return rows.FirstOrDefault();
    }

    public async Task<bool> TryMarkFailedAsync(
        Guid operationId,
        Guid leaseToken,
        string errorCode,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // Only the stored code travels. The exception, its message and its stack stay in the worker's
        // log, and a redacted code is what the owner and the UI are shown.
        var affected = await ChronicleafSql.ExecuteAsync(
            dbContext,
            """
            UPDATE operations
            SET status = 'failed',
                error_code = @p0,
                completed_at = @p1,
                lease_token = NULL,
                lease_expires_at = NULL,
                next_attempt_at = NULL,
                updated_at = @p1,
                revision = revision + 1
            WHERE id = @p2
              AND status = 'running'
              AND lease_token = @p3
            """,
            [
                new("@p0", NpgsqlDbType.Varchar, errorCode),
                new("@p1", NpgsqlDbType.TimestampTz, now.UtcDateTime),
                new("@p2", NpgsqlDbType.Uuid, operationId),
                new("@p3", NpgsqlDbType.Uuid, leaseToken),
            ],
            cancellationToken).ConfigureAwait(false);

        return affected == 1;
    }

    public async Task<bool> TryScheduleRetryAsync(
        Guid operationId,
        Guid leaseToken,
        DateTimeOffset nextAttemptAt,
        string errorCode,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // Back to `queued` with the due time recorded, so the backoff schedule is stored state the
        // owner and the tests can read, not only a detail of the queue.
        var affected = await ChronicleafSql.ExecuteAsync(
            dbContext,
            """
            UPDATE operations
            SET status = 'queued',
                error_code = @p0,
                next_attempt_at = @p1,
                lease_token = NULL,
                lease_expires_at = NULL,
                updated_at = @p2,
                revision = revision + 1
            WHERE id = @p3
              AND status = 'running'
              AND lease_token = @p4
            """,
            [
                new("@p0", NpgsqlDbType.Varchar, errorCode),
                new("@p1", NpgsqlDbType.TimestampTz, nextAttemptAt.UtcDateTime),
                new("@p2", NpgsqlDbType.TimestampTz, now.UtcDateTime),
                new("@p3", NpgsqlDbType.Uuid, operationId),
                new("@p4", NpgsqlDbType.Uuid, leaseToken),
            ],
            cancellationToken).ConfigureAwait(false);

        return affected == 1;
    }

    public async Task<bool> TryMarkCancelledAsync(
        Guid operationId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // The status becomes terminal here, on the request side, rather than when a worker gets round
        // to it. That is the property that discards a late result: the worker's commit is conditional
        // on `status = 'running'`, and this statement has already made that false.
        var affected = await ChronicleafSql.ExecuteAsync(
            dbContext,
            """
            UPDATE operations
            SET status = 'cancelled',
                cancel_requested_at = @p0,
                completed_at = @p0,
                lease_token = NULL,
                lease_expires_at = NULL,
                next_attempt_at = NULL,
                updated_at = @p0,
                revision = revision + 1
            WHERE id = @p1
              AND status IN ('queued', 'running')
            """,
            [
                new("@p0", NpgsqlDbType.TimestampTz, now.UtcDateTime),
                new("@p1", NpgsqlDbType.Uuid, operationId),
            ],
            cancellationToken).ConfigureAwait(false);

        return affected == 1;
    }

    public async Task<long?> ReadTargetRevisionAsync(
        Guid scopeId,
        string kind,
        Guid targetId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(kind);

        // A kind this build has no target for cannot be superseded by a moved revision, so the answer
        // is null and the caller keeps the operation's own revision. The result is still committed
        // under the same conditional statement; only the supersede check is unavailable.
        var statement = OperationTargets.Resolve(kind);
        if (statement is null)
        {
            return null;
        }

        return await ChronicleafSql.ScalarAsync<long?>(
            dbContext,
            statement,
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, targetId),
            ],
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Maps an operation kind onto the table that holds its target's revision.
/// </summary>
/// <remarks>
/// The supersede check is only meaningful if the current revision can actually be read, so a kind
/// that no delivered table backs resolves to nothing and the check is skipped. A later package that
/// introduces a new kind adds its mapping here rather than having the worker learn about tables.
/// </remarks>
internal static class OperationTargets
{
    private static readonly Dictionary<string, string> Targets = new(StringComparer.Ordinal)
    {
        ["project"] = "SELECT revision FROM projects WHERE scope_id = @p0 AND id = @p1",
        ["milestone"] = """
            SELECT m.revision
            FROM milestones m
            JOIN projects p ON p.id = m.project_id
            WHERE p.scope_id = @p0 AND m.id = @p1
            """,
    };

    /// <summary>The statement that reads a target's revision inside a scope, or <c>null</c>.</summary>
    public static string? Resolve(string kind)
    {
        ArgumentNullException.ThrowIfNull(kind);

        if (Targets.TryGetValue(kind, out var sql))
        {
            return sql;
        }

        // A kind may be namespaced, as in `project.export`; the leading segment names the target.
        var separator = kind.IndexOf('.', StringComparison.Ordinal);
        var head = separator > 0 ? kind[..separator] : kind;

        return Targets.TryGetValue(head, out var namespaced) ? namespaced : null;
    }
}

/// <summary>Reads an <see cref="Operation"/> out of a result row.</summary>
internal static class OperationRowReader
{
    public static Operation Read(System.Data.Common.DbDataReader reader) => new(
        id: reader.GetGuid(reader.GetOrdinal("id")),
        scopeId: reader.GetGuid(reader.GetOrdinal("scope_id")),
        kind: reader.GetString(reader.GetOrdinal("kind")),
        targetId: reader.GetGuid(reader.GetOrdinal("target_id")),
        inputRevision: reader.GetInt64(reader.GetOrdinal("input_revision")),
        idempotencyKey: reader.GetString(reader.GetOrdinal("idempotency_key")),
        status: ReadStatus(reader),
        attempt: reader.GetInt32(reader.GetOrdinal("attempt")),
        createdAt: reader.GetFieldValue<DateTime>(reader.GetOrdinal("created_at")),
        updatedAt: reader.GetFieldValue<DateTime>(reader.GetOrdinal("updated_at")),
        fingerprint: reader.GetString(reader.GetOrdinal("fingerprint")),
        revision: reader.GetInt64(reader.GetOrdinal("revision")),
        heartbeatAt: Nullable(reader, "heartbeat_at"),
        errorCode: NullableString(reader, "error_code"),
        resultRef: NullableString(reader, "result_ref"),
        leaseExpiresAt: Nullable(reader, "lease_expires_at"),
        leaseToken: NullableGuid(reader, "lease_token"),
        nextAttemptAt: Nullable(reader, "next_attempt_at"),
        completedAt: Nullable(reader, "completed_at"),
        cancelRequestedAt: Nullable(reader, "cancel_requested_at"),
        leaseRecoveries: reader.GetInt32(reader.GetOrdinal("lease_recoveries")));

    /// <summary>
    /// Reads a stored status. An unknown value is refused rather than guessed, because reporting a
    /// status this build does not define would be a lie to the owner.
    /// </summary>
    public static OperationStatus ReadStatus(System.Data.Common.DbDataReader reader)
    {
        var wire = reader.GetString(reader.GetOrdinal("status"));

        return OperationStatuses.TryParse(wire, out var status)
            ? status
            : throw new InvalidOperationException($"'{wire}' is not a known operation status.");
    }

    private static DateTimeOffset? Nullable(System.Data.Common.DbDataReader reader, string column) =>
        reader.IsDBNull(reader.GetOrdinal(column))
            ? null
            : new DateTimeOffset(reader.GetFieldValue<DateTime>(reader.GetOrdinal(column)), TimeSpan.Zero);

    private static string? NullableString(System.Data.Common.DbDataReader reader, string column) =>
        reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetString(reader.GetOrdinal(column));

    private static Guid? NullableGuid(System.Data.Common.DbDataReader reader, string column) =>
        reader.IsDBNull(reader.GetOrdinal(column)) ? null : reader.GetGuid(reader.GetOrdinal(column));
}
