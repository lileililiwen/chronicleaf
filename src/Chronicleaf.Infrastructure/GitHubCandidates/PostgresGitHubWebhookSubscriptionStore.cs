using System.Data.Common;
using Chronicleaf.Application.GitHubCandidates;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.GitHubCandidates;

/// <summary>
/// The PostgreSQL store for webhook subscriptions (GITHUB-RELEASE-CANDIDATES-001).
/// </summary>
/// <remarks>
/// <para>
/// <b>One subscription per connection, and the index is what decides it.</b> A connection is what a
/// delivery is addressed to, so two rows for one connection would make "is this connection
/// subscribed" a question with two possible answers. The unique index on
/// <c>(connection_id)</c> means the second save replaces the first rather than accumulating rows.
/// </para>
/// <para>
/// <b>The upsert keeps the row's id and its creation instant.</b> Past deliveries name this
/// connection, and their receipts are read back through the subscription, so replacing the row with a
/// new id would orphan history the owner may still read. It also means an archived subscription is
/// reactivated by the next save rather than being replaced by a row nothing points at any more.
/// </para>
/// <para>
/// <b>Archiving is an update, never a delete.</b> A subscription is how a connection stops accepting
/// candidates, not how its history is thrown away: the receipts already stored name this connection
/// and are the only record that a delivery ever arrived.
/// </para>
/// </remarks>
public sealed class PostgresGitHubWebhookSubscriptionStore(ChronicleafDbContext dbContext)
    : IGitHubWebhookSubscriptionStore
{
    private const string Columns =
        """
        id, scope_id, connection_id, project_id, credential_ref, archived_at, created_at, updated_at
        """;

    /// <inheritdoc />
    public async Task<GitHubWebhookSubscription?> FindByConnectionAsync(
        Guid connectionId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync(
            dbContext,
            $"SELECT {Columns} FROM github_webhook_subscriptions WHERE connection_id = @p0",
            [new("@p0", NpgsqlDbType.Uuid, connectionId)],
            Read,
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async Task<GitHubWebhookSubscription?> FindForProjectAsync(
        Guid scopeId,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync(
            dbContext,
            $"""
             SELECT {Columns} FROM github_webhook_subscriptions
             WHERE scope_id = @p0 AND project_id = @p1
             """,
            [new("@p0", NpgsqlDbType.Uuid, scopeId), new("@p1", NpgsqlDbType.Uuid, projectId)],
            Read,
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async Task<GitHubWebhookSubscription> UpsertAsync(
        GitHubWebhookSubscription subscription,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        // `ON CONFLICT (connection_id) DO UPDATE` rather than insert-then-read: the row's id and
        // created_at are the ones the caller already had, so a save that re-subscribes an archived
        // connection is a reactivation of the same row rather than a replacement a past delivery could
        // no longer find.
        await ChronicleafSql.ExecuteAsync(
            dbContext,
            """
            INSERT INTO github_webhook_subscriptions
                (id, scope_id, connection_id, project_id, credential_ref, archived_at, created_at, updated_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7)
            ON CONFLICT (connection_id) DO UPDATE
            SET scope_id = EXCLUDED.scope_id,
                project_id = EXCLUDED.project_id,
                credential_ref = EXCLUDED.credential_ref,
                archived_at = EXCLUDED.archived_at,
                updated_at = EXCLUDED.updated_at
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, subscription.Id),
                new("@p1", NpgsqlDbType.Uuid, subscription.ScopeId),
                new("@p2", NpgsqlDbType.Uuid, subscription.ConnectionId),
                new("@p3", NpgsqlDbType.Uuid, subscription.ProjectId),
                new("@p4", NpgsqlDbType.Uuid, subscription.CredentialRef),
                new("@p5", NpgsqlDbType.TimestampTz,
                    subscription.ArchivedAt is { } archived ? archived.UtcDateTime : DBNull.Value),
                new("@p6", NpgsqlDbType.TimestampTz, subscription.CreatedAt.UtcDateTime),
                new("@p7", NpgsqlDbType.TimestampTz, subscription.UpdatedAt.UtcDateTime),
            ],
            cancellationToken).ConfigureAwait(false);

        return await FindByConnectionAsync(subscription.ConnectionId, cancellationToken).ConfigureAwait(false)
            ?? subscription;
    }

    /// <inheritdoc />
    public async Task<bool> TryArchiveAsync(
        Guid scopeId,
        Guid projectId,
        DateTimeOffset archivedAt,
        CancellationToken cancellationToken = default)
    {
        // `archived_at IS NULL` is the whole condition. Unsubscribing something that is already
        // unsubscribed changes no row and is told so, so a panel that clicks twice does not record two
        // different changes of mind.
        var affected = await ChronicleafSql.ExecuteAsync(
            dbContext,
            """
            UPDATE github_webhook_subscriptions
            SET archived_at = @p0, updated_at = @p0
            WHERE scope_id = @p1 AND project_id = @p2 AND archived_at IS NULL
            """,
            [
                new("@p0", NpgsqlDbType.TimestampTz, archivedAt.UtcDateTime),
                new("@p1", NpgsqlDbType.Uuid, scopeId),
                new("@p2", NpgsqlDbType.Uuid, projectId),
            ],
            cancellationToken).ConfigureAwait(false);

        return affected > 0;
    }

    private static GitHubWebhookSubscription Read(DbDataReader reader) => new(
        reader.GetGuid(0),
        reader.GetGuid(1),
        reader.GetGuid(2),
        reader.GetGuid(3),
        reader.GetGuid(4),
        reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
        reader.GetFieldValue<DateTimeOffset>(6),
        reader.GetFieldValue<DateTimeOffset>(7));
}
