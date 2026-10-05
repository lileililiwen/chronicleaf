using System.Data.Common;
using Chronicleaf.Application.Evolution;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using Npgsql;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.Evolution;

/// <summary>
/// The PostgreSQL store for the evolution table (SCREENSHOT-EVOLUTION-IMAGES-001/002).
/// </summary>
/// <remarks>
/// <para>
/// This table is this package's own: one row per rendered comparison, holding the derived PNG's
/// media reference, the re-derivable manifest and the approval. The selected pictures it cites
/// stay the curation package's rows and the evidence they publish stays the evidence package's -
/// this store reads neither table and writes neither one.
/// </para>
/// <para>
/// <b>Every statement carries the scope in its WHERE clause</b>, so a lookup by id can only ever
/// return a row inside the caller's own installation. A duplicate idempotency key reports
/// <c>false</c> rather than throwing, so the service can replay the bound row or refuse the
/// conflict instead of answering a constraint.
/// </para>
/// </remarks>
public sealed class PostgresEvolutionImageStore(ChronicleafDbContext dbContext) : IEvolutionImageStore
{
    private const string EvolutionColumns =
        """
        id, scope_id, project_id, layout, manifest_json, selection_hash, artifact_media_id,
        artifact_sha256, width, height, status, approved_by, approved_at, idempotency_key, created_at
        """;

    /// <inheritdoc />
    public async Task<bool> InsertAsync(EvolutionImage artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        try
        {
            var inserted = await ChronicleafSql.ExecuteAsync(
                dbContext,
                $"""
                 INSERT INTO evolution_images ({EvolutionColumns})
                 VALUES (@p0, @p1, @p2, @p3, @p4::jsonb, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12, @p13, @p14)
                 """,
                [
                    new("@p0", NpgsqlDbType.Uuid, artifact.Id),
                    new("@p1", NpgsqlDbType.Uuid, artifact.ScopeId),
                    new("@p2", NpgsqlDbType.Uuid, artifact.ProjectId),
                    new("@p3", NpgsqlDbType.Varchar, artifact.Layout),
                    new("@p4", NpgsqlDbType.Jsonb, artifact.ManifestJson),
                    new("@p5", NpgsqlDbType.Varchar, artifact.SelectionHash),
                    new("@p6", NpgsqlDbType.Uuid, artifact.ArtifactMediaId),
                    new("@p7", NpgsqlDbType.Varchar, artifact.ArtifactSha256),
                    new("@p8", NpgsqlDbType.Integer, artifact.Width),
                    new("@p9", NpgsqlDbType.Integer, artifact.Height),
                    new("@p10", NpgsqlDbType.Varchar, artifact.Status),
                    new("@p11", NpgsqlDbType.Uuid, artifact.ApprovedBy is { } by ? (object)by : DBNull.Value),
                    new("@p12", NpgsqlDbType.TimestampTz, artifact.ApprovedAt is { } at ? (object)at : DBNull.Value),
                    new("@p13", NpgsqlDbType.Varchar, artifact.IdempotencyKey is { } key ? (object)key : DBNull.Value),
                    new("@p14", NpgsqlDbType.TimestampTz, artifact.CreatedAt),
                ],
                cancellationToken).ConfigureAwait(false);

            return inserted == 1;
        }
        catch (PostgresException failure) when (failure.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // The id or the idempotency key is already bound. The service re-reads the bound row
            // and either replays it or refuses the conflict, so a retried render never leaves a
            // second row behind.
            return false;
        }
    }

    /// <inheritdoc />
    public async Task<EvolutionImage?> FindAsync(
        Guid scopeId,
        Guid evolutionId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync(
            dbContext,
            $"SELECT {EvolutionColumns} FROM evolution_images WHERE scope_id = @p0 AND id = @p1",
            [new("@p0", NpgsqlDbType.Uuid, scopeId), new("@p1", NpgsqlDbType.Uuid, evolutionId)],
            ReadEvolution,
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EvolutionImage>> ListAsync(
        Guid scopeId,
        Guid projectId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return await ChronicleafSql.QueryAsync(
            dbContext,
            $"""
             SELECT {EvolutionColumns} FROM evolution_images
             WHERE scope_id = @p0 AND project_id = @p1
             ORDER BY created_at DESC, id DESC
             LIMIT @p2
             """,
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, projectId),
                new("@p2", NpgsqlDbType.Integer, limit),
            ],
            ReadEvolution,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<EvolutionImage?> FindByIdempotencyKeyAsync(
        Guid scopeId,
        Guid projectId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        var rows = await ChronicleafSql.QueryAsync(
            dbContext,
            $"""
             SELECT {EvolutionColumns} FROM evolution_images
             WHERE scope_id = @p0 AND project_id = @p1 AND idempotency_key = @p2
             """,
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, projectId),
                new("@p2", NpgsqlDbType.Varchar, idempotencyKey),
            ],
            ReadEvolution,
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async Task<bool> TryApproveAsync(
        Guid scopeId,
        Guid evolutionId,
        Guid actorId,
        DateTimeOffset approvedAt,
        CancellationToken cancellationToken = default)
    {
        var changed = await ChronicleafSql.ExecuteAsync(
            dbContext,
            """
            UPDATE evolution_images
            SET status = 'approved', approved_by = @p2, approved_at = @p3
            WHERE scope_id = @p0 AND id = @p1 AND status = 'draft'
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, evolutionId),
                new("@p2", NpgsqlDbType.Uuid, actorId),
                new("@p3", NpgsqlDbType.TimestampTz, approvedAt),
            ],
            cancellationToken).ConfigureAwait(false);

        return changed == 1;
    }

    private static EvolutionImage ReadEvolution(DbDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetGuid(6),
            reader.GetString(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetString(10),
            reader.IsDBNull(11) ? null : reader.GetGuid(11),
            reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
            reader.IsDBNull(13) ? null : reader.GetString(13),
            reader.GetFieldValue<DateTimeOffset>(14));
}
