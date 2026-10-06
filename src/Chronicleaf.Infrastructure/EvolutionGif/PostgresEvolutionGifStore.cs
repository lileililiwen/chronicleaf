using System.Data.Common;
using Chronicleaf.Application.EvolutionGifs;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using Npgsql;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.EvolutionGifs;

/// <summary>
/// The PostgreSQL store for the evolution GIF table (EVOLUTION-GIF-EXPORT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// This table is this package's own: one row per rendered animation, holding the derived GIF
/// bytes beside the re-derivable manifest and the approval. The selected pictures it cites
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
public sealed class PostgresEvolutionGifStore(ChronicleafDbContext dbContext) : IEvolutionGifStore
{
    private const string GifColumns =
        """
        id, scope_id, project_id, manifest_json, selection_hash, artifact_sha256, artifact_bytes,
        width, height, frame_count, total_duration_ms, loop, status, approved_by, approved_at,
        idempotency_key, created_at
        """;

    /// <inheritdoc />
    public async Task<bool> InsertAsync(EvolutionGif artifact, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(artifact);

        try
        {
            var inserted = await ChronicleafSql.ExecuteAsync(
                dbContext,
                $"""
                 INSERT INTO evolution_gifs ({GifColumns})
                 VALUES (@p0, @p1, @p2, @p3::jsonb, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12, @p13, @p14, @p15, @p16)
                 """,
                [
                    new("@p0", NpgsqlDbType.Uuid, artifact.Id),
                    new("@p1", NpgsqlDbType.Uuid, artifact.ScopeId),
                    new("@p2", NpgsqlDbType.Uuid, artifact.ProjectId),
                    new("@p3", NpgsqlDbType.Jsonb, artifact.ManifestJson),
                    new("@p4", NpgsqlDbType.Varchar, artifact.SelectionHash),
                    new("@p5", NpgsqlDbType.Varchar, artifact.ArtifactSha256),
                    new("@p6", NpgsqlDbType.Bytea, artifact.ArtifactBytes),
                    new("@p7", NpgsqlDbType.Integer, artifact.Width),
                    new("@p8", NpgsqlDbType.Integer, artifact.Height),
                    new("@p9", NpgsqlDbType.Integer, artifact.FrameCount),
                    new("@p10", NpgsqlDbType.Integer, artifact.TotalDurationMs),
                    new("@p11", NpgsqlDbType.Boolean, artifact.Loop),
                    new("@p12", NpgsqlDbType.Varchar, artifact.Status),
                    new("@p13", NpgsqlDbType.Uuid, artifact.ApprovedBy is { } by ? (object)by : DBNull.Value),
                    new("@p14", NpgsqlDbType.TimestampTz, artifact.ApprovedAt is { } at ? (object)at : DBNull.Value),
                    new("@p15", NpgsqlDbType.Varchar, artifact.IdempotencyKey is { } key ? (object)key : DBNull.Value),
                    new("@p16", NpgsqlDbType.TimestampTz, artifact.CreatedAt),
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
    public async Task<EvolutionGif?> FindAsync(
        Guid scopeId,
        Guid gifId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync(
            dbContext,
            $"SELECT {GifColumns} FROM evolution_gifs WHERE scope_id = @p0 AND id = @p1",
            [new("@p0", NpgsqlDbType.Uuid, scopeId), new("@p1", NpgsqlDbType.Uuid, gifId)],
            ReadGif,
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EvolutionGif>> ListAsync(
        Guid scopeId,
        Guid projectId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        return await ChronicleafSql.QueryAsync(
            dbContext,
            $"""
             SELECT {GifColumns} FROM evolution_gifs
             WHERE scope_id = @p0 AND project_id = @p1
             ORDER BY created_at DESC, id DESC
             LIMIT @p2
             """,
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, projectId),
                new("@p2", NpgsqlDbType.Integer, limit),
            ],
            ReadGif,
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<EvolutionGif?> FindByIdempotencyKeyAsync(
        Guid scopeId,
        Guid projectId,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);

        var rows = await ChronicleafSql.QueryAsync(
            dbContext,
            $"""
             SELECT {GifColumns} FROM evolution_gifs
             WHERE scope_id = @p0 AND project_id = @p1 AND idempotency_key = @p2
             """,
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, projectId),
                new("@p2", NpgsqlDbType.Varchar, idempotencyKey),
            ],
            ReadGif,
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async Task<bool> TryApproveAsync(
        Guid scopeId,
        Guid gifId,
        Guid actorId,
        DateTimeOffset approvedAt,
        CancellationToken cancellationToken = default)
    {
        var changed = await ChronicleafSql.ExecuteAsync(
            dbContext,
            """
            UPDATE evolution_gifs
            SET status = 'approved', approved_by = @p2, approved_at = @p3
            WHERE scope_id = @p0 AND id = @p1 AND status = 'draft'
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, gifId),
                new("@p2", NpgsqlDbType.Uuid, actorId),
                new("@p3", NpgsqlDbType.TimestampTz, approvedAt),
            ],
            cancellationToken).ConfigureAwait(false);

        return changed == 1;
    }

    private static EvolutionGif ReadGif(DbDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            (byte[])reader.GetValue(6),
            reader.GetInt32(7),
            reader.GetInt32(8),
            reader.GetInt32(9),
            reader.GetInt32(10),
            reader.GetBoolean(11),
            reader.GetString(12),
            reader.IsDBNull(13) ? null : reader.GetGuid(13),
            reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            reader.GetFieldValue<DateTimeOffset>(16));
}
