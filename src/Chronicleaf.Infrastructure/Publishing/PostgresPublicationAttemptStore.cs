using System.Data.Common;
using Chronicleaf.Application.Publishing;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.Publishing;

/// <summary>
/// The publication attempts table in PostgreSQL (PUBLISHER-CONTRACT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Every statement is parameterized and every one carries the scope in its <c>WHERE</c> clause, so a
/// forged parameter cannot read or write across installations. A read for an id that exists in another
/// scope returns nothing and a write for one changes nothing, which is what makes a foreign-scoped
/// attempt a 404 rather than a leak.
/// </para>
/// <para>
/// <b>The unique tuple <c>(scope_id, idempotency_key)</c> is enforced by the database, so a retried
/// publish cannot record two attempts for one key.</b> A repeated key whose frozen binding matches
/// returns the stored receipt; a repeated key over different work is a conflict that writes nothing.
/// The state moves forward by compare-and-set - <c>prepared</c> to <c>sending</c>, then to one
/// terminal state - and there is no statement that moves a terminal state anywhere: a receipt that
/// was read once reads the same the next time.
/// </para>
/// </remarks>
public sealed class PostgresPublicationAttemptStore(ChronicleafDbContext dbContext) : IPublicationAttemptStore
{
    private const string AttemptColumns =
        "id, scope_id, operation_id, target_id, asset_id, content_revision, body_hash, "
        + "evidence_revision, preview_hash, mode, approval_id, idempotency_key, state, remote_id, "
        + "remote_url, error_code, dispatched_at, completed_at, created_at, updated_at";

    /// <inheritdoc />
    public async Task<bool> TryInsertAsync(
        PublicationAttempt attempt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(attempt);

        var id = await ChronicleafSql.ScalarAsync<Guid>(
            dbContext,
            "INSERT INTO publication_attempts ("
            + "id, scope_id, operation_id, target_id, asset_id, content_revision, body_hash, "
            + "evidence_revision, preview_hash, mode, approval_id, idempotency_key, state, "
            + "created_at, updated_at) "
            + "VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12, @p13, @p14) "
            + "ON CONFLICT (scope_id, idempotency_key) DO NOTHING "
            + "RETURNING id",
            [
                new("@p0", NpgsqlDbType.Uuid, attempt.Id),
                new("@p1", NpgsqlDbType.Uuid, attempt.ScopeId),
                new("@p2", NpgsqlDbType.Uuid, attempt.OperationId),
                new("@p3", NpgsqlDbType.Varchar, attempt.TargetId),
                new("@p4", NpgsqlDbType.Uuid, attempt.AssetId),
                new("@p5", NpgsqlDbType.Bigint, attempt.ContentRevision),
                new("@p6", NpgsqlDbType.Varchar, attempt.BodyHash),
                new("@p7", NpgsqlDbType.Bigint, attempt.EvidenceRevision),
                new("@p8", NpgsqlDbType.Varchar, attempt.PreviewHash),
                new("@p9", NpgsqlDbType.Varchar, attempt.Mode),
                new("@p10", NpgsqlDbType.Uuid, attempt.ApprovalId),
                new("@p11", NpgsqlDbType.Varchar, attempt.IdempotencyKey),
                new("@p12", NpgsqlDbType.Varchar, attempt.State),
                new("@p13", NpgsqlDbType.TimestampTz, attempt.CreatedAt),
                new("@p14", NpgsqlDbType.TimestampTz, attempt.UpdatedAt),
            ],
            cancellationToken).ConfigureAwait(false);

        return id != Guid.Empty;
    }

    /// <inheritdoc />
    public async Task<PublicationAttempt?> FindAsync(
        Guid scopeId,
        Guid attemptId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync<PublicationAttempt>(
            dbContext,
            $"SELECT {AttemptColumns} FROM publication_attempts WHERE scope_id = @p0 AND id = @p1",
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, attemptId),
            ],
            ReadAttempt,
            cancellationToken).ConfigureAwait(false);

        return rows.SingleOrDefault();
    }

    /// <inheritdoc />
    public async Task<PublicationAttempt?> FindByIdempotencyKeyAsync(
        Guid scopeId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        var rows = await ChronicleafSql.QueryAsync<PublicationAttempt>(
            dbContext,
            $"SELECT {AttemptColumns} FROM publication_attempts "
            + "WHERE scope_id = @p0 AND idempotency_key = @p1",
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Varchar, idempotencyKey),
            ],
            ReadAttempt,
            cancellationToken).ConfigureAwait(false);

        return rows.SingleOrDefault();
    }

    /// <inheritdoc />
    public async Task<PublicationAttempt?> FindByOperationAsync(
        Guid scopeId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync<PublicationAttempt>(
            dbContext,
            $"SELECT {AttemptColumns} FROM publication_attempts WHERE scope_id = @p0 AND operation_id = @p1",
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, operationId),
            ],
            ReadAttempt,
            cancellationToken).ConfigureAwait(false);

        return rows.SingleOrDefault();
    }

    /// <inheritdoc />
    public async Task<bool> TryMarkSendingAsync(
        Guid scopeId,
        Guid attemptId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var changed = await ChronicleafSql.ExecuteAsync(
            dbContext,
            "UPDATE publication_attempts SET state = @p0, dispatched_at = @p1, updated_at = @p2 "
            + "WHERE scope_id = @p3 AND id = @p4 AND state = @p5",
            [
                new("@p0", NpgsqlDbType.Varchar, PublishStates.Sending),
                new("@p1", NpgsqlDbType.TimestampTz, now),
                new("@p2", NpgsqlDbType.TimestampTz, now),
                new("@p3", NpgsqlDbType.Uuid, scopeId),
                new("@p4", NpgsqlDbType.Uuid, attemptId),
                new("@p5", NpgsqlDbType.Varchar, PublishStates.Prepared),
            ],
            cancellationToken).ConfigureAwait(false);

        return changed == 1;
    }

    /// <inheritdoc />
    public async Task<bool> TryCompleteAsync(
        Guid scopeId,
        Guid attemptId,
        string state,
        string? remoteId,
        string? remoteUrl,
        string? errorCode,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);

        if (!PublishStates.IsKnown(state) || PublishStates.IsOpen(state))
        {
            throw new ArgumentException(
                "A completion moves an open attempt to a terminal state.", nameof(state));
        }

        var changed = await ChronicleafSql.ExecuteAsync(
            dbContext,
            "UPDATE publication_attempts SET state = @p0, remote_id = @p1, remote_url = @p2, "
            + "error_code = @p3, completed_at = @p4, updated_at = @p5 "
            + "WHERE scope_id = @p6 AND id = @p7 AND state IN (@p8, @p9)",
            [
                new("@p0", NpgsqlDbType.Varchar, state),
                new("@p1", NpgsqlDbType.Varchar, (object?)remoteId ?? DBNull.Value),
                new("@p2", NpgsqlDbType.Varchar, (object?)remoteUrl ?? DBNull.Value),
                new("@p3", NpgsqlDbType.Varchar, (object?)errorCode ?? DBNull.Value),
                new("@p4", NpgsqlDbType.TimestampTz, now),
                new("@p5", NpgsqlDbType.TimestampTz, now),
                new("@p6", NpgsqlDbType.Uuid, scopeId),
                new("@p7", NpgsqlDbType.Uuid, attemptId),
                new("@p8", NpgsqlDbType.Varchar, PublishStates.Prepared),
                new("@p9", NpgsqlDbType.Varchar, PublishStates.Sending),
            ],
            cancellationToken).ConfigureAwait(false);

        return changed == 1;
    }

    /// <inheritdoc />
    public async Task<bool> TryReconcileAsync(
        Guid scopeId,
        Guid attemptId,
        string state,
        string? remoteId,
        string? remoteUrl,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(state);

        if (!string.Equals(state, PublishStates.Published, StringComparison.Ordinal) &&
            !string.Equals(state, PublishStates.RemoteDraft, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A reconcile resolves an unknown attempt to a held state.", nameof(state));
        }

        var changed = await ChronicleafSql.ExecuteAsync(
            dbContext,
            "UPDATE publication_attempts SET state = @p0, remote_id = @p1, remote_url = @p2, "
            + "error_code = NULL, updated_at = @p3 "
            + "WHERE scope_id = @p4 AND id = @p5 AND state = @p6",
            [
                new("@p0", NpgsqlDbType.Varchar, state),
                new("@p1", NpgsqlDbType.Varchar, (object?)remoteId ?? DBNull.Value),
                new("@p2", NpgsqlDbType.Varchar, (object?)remoteUrl ?? DBNull.Value),
                new("@p3", NpgsqlDbType.TimestampTz, now),
                new("@p4", NpgsqlDbType.Uuid, scopeId),
                new("@p5", NpgsqlDbType.Uuid, attemptId),
                new("@p6", NpgsqlDbType.Varchar, PublishStates.Unknown),
            ],
            cancellationToken).ConfigureAwait(false);

        return changed == 1;
    }

    private static PublicationAttempt ReadAttempt(DbDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetString(3),
            reader.GetGuid(4),
            reader.GetInt64(5),
            reader.GetString(6),
            reader.GetInt64(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetGuid(10),
            reader.GetString(11),
            reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.IsDBNull(14) ? null : reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetFieldValue<DateTimeOffset>(16),
            reader.IsDBNull(17) ? null : reader.GetFieldValue<DateTimeOffset>(17),
            reader.GetFieldValue<DateTimeOffset>(18),
            reader.GetFieldValue<DateTimeOffset>(19));
}
