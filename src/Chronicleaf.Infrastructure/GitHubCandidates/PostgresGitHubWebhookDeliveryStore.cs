using System.Data.Common;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.GitHubCandidates;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.GitHubCandidates;

/// <summary>
/// The PostgreSQL store for signed deliveries and their receipts (GITHUB-RELEASE-CANDIDATES-001/002).
/// </summary>
/// <remarks>
/// <para>
/// <b>The receipt is decided by the database, not by a read this store did first.</b> Accepting is
/// <c>ON CONFLICT (connection_id, delivery_id) DO NOTHING RETURNING</c>, and a delivery whose
/// conflict produced no row is then read back. A sender that redelivers - which GitHub does, and does
/// not announce - therefore gets the original receipt rather than a second one, so the receipt a
/// reader sees and the rows a reader counts cannot disagree.
/// </para>
/// <para>
/// <b>Nothing about the payload is stored, and that is the design of the row rather than an
/// omission.</b> A delivery's body may name a private issue title, a branch name and an author's login,
/// and a table of receipts is a table a support export would carry. The <c>proposals</c> column holds
/// this package's own already-routed reading of the body - a kind, a provider id, a bounded label and
/// an instant - and not the document it was read from. It has exactly one reader, the outbox consumer
/// that turns a receipt into candidates, which is why it rides on the receipt rather than in a table
/// of its own.
/// </para>
/// <para>
/// <b>An outcome is written once.</b> <c>WHERE status = 'pending'</c> is the whole condition on the
/// completion, so a consumer handed the same event twice records the first outcome and leaves it.
/// </para>
/// </remarks>
public sealed class PostgresGitHubWebhookDeliveryStore(ChronicleafDbContext dbContext)
    : IGitHubWebhookDeliveryStore
{
    private const string Columns =
        """
        id, scope_id, connection_id, project_id, delivery_id, event, action, status, reason,
        candidate_count, received_at, proposals
        """;

    /// <inheritdoc />
    public async Task<(GitHubWebhookDelivery Delivery, bool Created)> AcceptAsync(
        GitHubWebhookDelivery delivery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        // The insert decides. A returned id means this call wrote the row; no row means the row was
        // already there, and the read below is the original receipt rather than this call's own new
        // object - which is the only answer that keeps a retry and a first delivery agreeing.
        var accepted = await ChronicleafSql.ScalarAsync<Guid?>(
            dbContext,
            """
            INSERT INTO github_webhook_deliveries
                (id, scope_id, connection_id, project_id, delivery_id, event, action, status, reason,
                 candidate_count, received_at, proposals)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11)
            ON CONFLICT (connection_id, delivery_id) DO NOTHING
            RETURNING id
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, delivery.Id),
                new("@p1", NpgsqlDbType.Uuid, delivery.ScopeId),
                new("@p2", NpgsqlDbType.Uuid, delivery.ConnectionId),
                new("@p3", NpgsqlDbType.Uuid, delivery.ProjectId),
                new("@p4", NpgsqlDbType.Varchar, delivery.DeliveryId),
                new("@p5", NpgsqlDbType.Varchar, delivery.Event),
                new("@p6", NpgsqlDbType.Varchar, delivery.Action is { } action ? action : DBNull.Value),
                new("@p7", NpgsqlDbType.Varchar, delivery.Status),
                new("@p8", NpgsqlDbType.Varchar, delivery.Reason is { } reason ? reason : DBNull.Value),
                new("@p9", NpgsqlDbType.Integer, delivery.CandidateCount),
                new("@p10", NpgsqlDbType.TimestampTz, delivery.ReceivedAt.UtcDateTime),
                new("@p11", NpgsqlDbType.Jsonb, delivery.ProposalsJson),
            ],
            cancellationToken).ConfigureAwait(false);

        if (accepted is not null)
        {
            return (delivery, Created: true);
        }

        var rows = await ChronicleafSql.QueryAsync(
            dbContext,
            $"""
             SELECT {Columns} FROM github_webhook_deliveries
             WHERE connection_id = @p0 AND delivery_id = @p1
             """,
            [
                new("@p0", NpgsqlDbType.Uuid, delivery.ConnectionId),
                new("@p1", NpgsqlDbType.Varchar, delivery.DeliveryId),
            ],
            Read,
            cancellationToken).ConfigureAwait(false);

        // A conflict with nothing readable behind it is a state the schema does not allow, and
        // reporting it as a created delivery would be reporting a second receipt where the database
        // holds exactly one. Failing loudly is the honest answer; a silent success here would be a
        // delivery that was accepted and then lost.
        return rows.Count == 0
            ? throw new InvalidOperationException(
                "A delivery receipt conflicted with a row this store could not read back.")
            : (rows[0], Created: false);
    }

    /// <inheritdoc />
    public async Task<GitHubWebhookDelivery?> FindByReceiptAsync(
        Guid scopeId,
        Guid receiptId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync(
            dbContext,
            $"""
             SELECT {Columns} FROM github_webhook_deliveries
             WHERE scope_id = @p0 AND id = @p1
             """,
            [new("@p0", NpgsqlDbType.Uuid, scopeId), new("@p1", NpgsqlDbType.Uuid, receiptId)],
            Read,
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async Task<bool> TryRecordOutcomeAsync(
        GitHubWebhookDelivery delivery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        // The pending guard is the idempotence. A consumer handed the same event twice finds no row to
        // move and is told so, rather than writing a second claim about what one delivery produced.
        var affected = await ChronicleafSql.ExecuteAsync(
            dbContext,
            """
            UPDATE github_webhook_deliveries
            SET status = @p0, reason = @p1, candidate_count = @p2
            WHERE id = @p3 AND status = 'pending'
            """,
            [
                new("@p0", NpgsqlDbType.Varchar, delivery.Status),
                new("@p1", NpgsqlDbType.Varchar, delivery.Reason is { } reason ? reason : DBNull.Value),
                new("@p2", NpgsqlDbType.Integer, delivery.CandidateCount),
                new("@p3", NpgsqlDbType.Uuid, delivery.Id),
            ],
            cancellationToken).ConfigureAwait(false);

        return affected == 1;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GitHubWebhookDelivery>> ListAsync(
        Guid scopeId,
        Guid connectionId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        // Newest first, and bounded by the delivered list policy rather than by a number this package
        // invented: a receipts list is a support surface, and an unbounded one is a way to read a whole
        // connection's history in one request.
        return await ChronicleafSql.QueryAsync(
            dbContext,
            $"""
             SELECT {Columns} FROM github_webhook_deliveries
             WHERE scope_id = @p0 AND connection_id = @p1
             ORDER BY received_at DESC, id DESC
             LIMIT @p2
             """,
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, connectionId),
                new("@p2", NpgsqlDbType.Integer, ListQueryPolicy.Clamp(limit)),
            ],
            Read,
            cancellationToken).ConfigureAwait(false);
    }

    private static GitHubWebhookDelivery Read(DbDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.GetGuid(2),
        reader.GetGuid(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.GetString(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.GetInt32(9),
        reader.GetFieldValue<DateTimeOffset>(10),
        reader.GetString(11));
}
