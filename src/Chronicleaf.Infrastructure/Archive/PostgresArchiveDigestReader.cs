using Chronicleaf.Application.Archive;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.Archive;

/// <summary>
/// Recomputes a scope's archive hashes from the live rows (HOSTED-ARCHIVE-OPERATIONS-001).
/// </summary>
/// <remarks>
/// <para>
/// <b>Every line carries its table and every join runs through the project.</b> Milestones,
/// bundles and items name no scope of their own, so the scope predicate joins through
/// <c>projects.scope_id</c>: a row in another scope is never listed, and a digest over this
/// scope's rows cannot be satisfied by another scope's bytes. The lines are canonical -
/// lowercase ids, fixed prefixes, the row's own hash or revision - so the same rows always hash
/// the same way, and any added, removed or changed row fails the rehearsal's comparison.
/// </para>
/// <para>
/// <b>Reads only.</b> Nothing here writes, retires or deletes; the purge's retirements go
/// through the delivered media store, and the eligibility answers below only say what the purge
/// may consider.
/// </para>
/// </remarks>
public sealed class PostgresArchiveDigestReader(ChronicleafDbContext dbContext) : IArchiveDigestReader
{
    /// <inheritdoc />
    public int LiveSchemaVersion => ChronicleafDbContext.RequiredSchemaVersion;

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ReadMediaLinesAsync(
        Guid scopeId,
        CancellationToken cancellationToken = default) =>
        await ChronicleafSql.QueryAsync(
            dbContext,
            "SELECT id, sha256, status FROM media WHERE scope_id = @p0 ORDER BY id ASC",
            [new("@p0", NpgsqlDbType.Uuid, scopeId)],
            reader => $"media:{reader.GetGuid(0):D}:{reader.GetString(1)}:{reader.GetString(2)}",
            cancellationToken).ConfigureAwait(false);

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> ReadContentLinesAsync(
        Guid scopeId,
        CancellationToken cancellationToken = default)
    {
        var lines = new List<string>();

        lines.AddRange(await ChronicleafSql.QueryAsync(
            dbContext,
            """
            SELECT m.id, m.revision FROM milestones m
            JOIN projects p ON p.id = m.project_id
            WHERE p.scope_id = @p0 ORDER BY m.id ASC
            """,
            [new("@p0", NpgsqlDbType.Uuid, scopeId)],
            reader => $"milestone:{reader.GetGuid(0):D}:{reader.GetInt64(1)}",
            cancellationToken).ConfigureAwait(false));

        lines.AddRange(await ChronicleafSql.QueryAsync(
            dbContext,
            """
            SELECT b.id, b.revision FROM evidence_bundles b
            JOIN milestones m ON m.id = b.milestone_id
            JOIN projects p ON p.id = m.project_id
            WHERE p.scope_id = @p0 ORDER BY b.id ASC
            """,
            [new("@p0", NpgsqlDbType.Uuid, scopeId)],
            reader => $"bundle:{reader.GetGuid(0):D}:{reader.GetInt64(1)}",
            cancellationToken).ConfigureAwait(false));

        lines.AddRange(await ChronicleafSql.QueryAsync(
            dbContext,
            """
            SELECT i.id, COALESCE(i.payload_sha256, '') FROM evidence_items i
            JOIN evidence_bundles b ON b.id = i.bundle_id
            JOIN milestones m ON m.id = b.milestone_id
            JOIN projects p ON p.id = m.project_id
            WHERE p.scope_id = @p0 ORDER BY i.id ASC
            """,
            [new("@p0", NpgsqlDbType.Uuid, scopeId)],
            reader => $"item:{reader.GetGuid(0):D}:{reader.GetString(1)}",
            cancellationToken).ConfigureAwait(false));

        lines.AddRange(await ChronicleafSql.QueryAsync(
            dbContext,
            "SELECT id, current_revision FROM content_assets WHERE scope_id = @p0 ORDER BY id ASC",
            [new("@p0", NpgsqlDbType.Uuid, scopeId)],
            reader => $"asset:{reader.GetGuid(0):D}:{reader.GetInt64(1)}",
            cancellationToken).ConfigureAwait(false));

        lines.AddRange(await ChronicleafSql.QueryAsync(
            dbContext,
            "SELECT id, revision FROM projects WHERE scope_id = @p0 ORDER BY id ASC",
            [new("@p0", NpgsqlDbType.Uuid, scopeId)],
            reader => $"project:{reader.GetGuid(0):D}:{reader.GetInt64(1)}",
            cancellationToken).ConfigureAwait(false));

        return lines;
    }

    /// <inheritdoc />
    public async Task<long> CountProjectBundlesAsync(
        Guid scopeId,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var count = await ChronicleafSql.ScalarAsync<long?>(
            dbContext,
            """
            SELECT count(*) FROM evidence_bundles b
            JOIN milestones m ON m.id = b.milestone_id
            JOIN projects p ON p.id = m.project_id
            WHERE p.scope_id = @p0 AND p.id = @p1
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, projectId),
            ],
            cancellationToken).ConfigureAwait(false);

        return count ?? 0;
    }

    /// <inheritdoc />
    public async Task<long> CountProjectAssetsAsync(
        Guid scopeId,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var count = await ChronicleafSql.ScalarAsync<long?>(
            dbContext,
            "SELECT count(*) FROM content_assets WHERE scope_id = @p0 AND project_id = @p1",
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, projectId),
            ],
            cancellationToken).ConfigureAwait(false);

        return count ?? 0;
    }

    /// <inheritdoc />
    public async Task<bool> HasLiveApprovalAsync(
        Guid scopeId,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var count = await ChronicleafSql.ScalarAsync<long?>(
            dbContext,
            """
            SELECT count(*) FROM content_approvals a
            JOIN content_assets s ON s.id = a.asset_id
            WHERE a.scope_id = @p0 AND s.project_id = @p1 AND a.invalidated_at IS NULL
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, projectId),
            ],
            cancellationToken).ConfigureAwait(false);

        return (count ?? 0) > 0;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Guid>> ListProjectMediaAsync(
        Guid scopeId,
        Guid projectId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        return await ChronicleafSql.QueryAsync(
            dbContext,
            "SELECT id FROM media WHERE scope_id = @p0 AND project_id = @p1 AND status = 'ready'"
            + " ORDER BY created_at ASC LIMIT @p2",
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, projectId),
                new("@p2", NpgsqlDbType.Integer, limit),
            ],
            reader => reader.GetGuid(0),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> HasLiveJourneyAsync(
        Guid scopeId,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        var count = await ChronicleafSql.ScalarAsync<long?>(
            dbContext,
            "SELECT count(*) FROM project_journeys"
            + " WHERE scope_id = @p0 AND project_id = @p1 AND enabled AND revoked_at IS NULL",
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, projectId),
            ],
            cancellationToken).ConfigureAwait(false);

        return (count ?? 0) > 0;
    }
}
