using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.Operations;

/// <summary>PostgreSQL implementation of <see cref="IOutboxDispatchStamper"/>.</summary>
public sealed class PostgresOutboxDispatchStamper(ChronicleafDbContext dbContext) : IOutboxDispatchStamper
{
    public async Task<bool> TryMarkDispatchedAsync(
        Guid eventId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // `dispatched_at IS NULL` is the whole guard: two processes finishing the same event race
        // here, and the loser's update matches no row instead of moving the stamp backwards.
        var affected = await ChronicleafSql.ExecuteAsync(
            dbContext,
            """
            UPDATE outbox
            SET dispatched_at = @p0
            WHERE id = @p1 AND dispatched_at IS NULL
            """,
            [
                new("@p0", NpgsqlDbType.TimestampTz, now.UtcDateTime),
                new("@p1", NpgsqlDbType.Uuid, eventId),
            ],
            cancellationToken).ConfigureAwait(false);

        return affected == 1;
    }
}
