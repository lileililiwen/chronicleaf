using System.Data.Common;
using Chronicleaf.Application.Content;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.Content;

/// <summary>
/// The approvals table in PostgreSQL (MARKDOWN-REVIEW-EXPORT-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Every statement is parameterized and every one carries the scope in its <c>WHERE</c> clause, so a
/// forged parameter cannot read or write across installations. A read for an id that exists in
/// another scope returns nothing and a write for one changes nothing, which is what makes a
/// foreign-scoped asset a 404 rather than a leak.
/// </para>
/// <para>
/// <b>There is no DELETE against <c>content_approvals</c> in this file.</b> Withdrawing an
/// approval marks it invalidated with a reason; the row stays as the history of what was once
/// exportable. The insert is guarded by the unique index on (scope, asset, revision), so two
/// approvals of one revision produce one row and one refusal rather than two decisions.
/// </para>
/// </remarks>
public sealed class PostgresContentReviewStore(ChronicleafDbContext dbContext) : IContentReviewStore
{
    private const string ApprovalColumns =
        "id, scope_id, asset_id, content_revision, actor_id, payload_kind, body_hash, "
        + "evidence_revision, voice_snapshot_hash, media_selection_hash, artifact_hash, "
        + "approved_at, invalidated_at, reason";

    /// <inheritdoc />
    public async Task<ContentApproval?> FindLiveAsync(
        Guid scopeId,
        Guid assetId,
        long contentRevision,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync<ContentApproval>(
            dbContext,
            $"SELECT {ApprovalColumns} FROM content_approvals "
                + "WHERE scope_id = @p0 AND asset_id = @p1 AND content_revision = @p2 "
                + "AND invalidated_at IS NULL",
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, assetId),
                new("@p2", NpgsqlDbType.Bigint, contentRevision),
            ],
            ReadApproval,
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ContentApproval>> ListForAssetAsync(
        Guid scopeId,
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync<ContentApproval>(
            dbContext,
            $"SELECT {ApprovalColumns} FROM content_approvals "
                + "WHERE scope_id = @p0 AND asset_id = @p1 "
                + "ORDER BY content_revision DESC",
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, assetId),
            ],
            ReadApproval,
            cancellationToken).ConfigureAwait(false);

        return rows;
    }

    /// <inheritdoc />
    public async Task<bool> TryInsertAsync(ContentApproval approval, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(approval);

        // ON CONFLICT DO NOTHING RETURNING is how the unique (scope, asset, revision) index decides
        // the race: a returned id means this call recorded the approval.
        var id = await ChronicleafSql.ScalarAsync<Guid?>(
            dbContext,
            """
            INSERT INTO content_approvals
                (id, scope_id, asset_id, content_revision, actor_id, payload_kind, body_hash,
                 evidence_revision, voice_snapshot_hash, media_selection_hash, artifact_hash,
                 approved_at, invalidated_at, reason)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12, @p13)
            ON CONFLICT (scope_id, asset_id, content_revision) DO NOTHING
            RETURNING id
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, approval.Id),
                new("@p1", NpgsqlDbType.Uuid, approval.ScopeId),
                new("@p2", NpgsqlDbType.Uuid, approval.AssetId),
                new("@p3", NpgsqlDbType.Bigint, approval.ContentRevision),
                new("@p4", NpgsqlDbType.Uuid, (object?)approval.ActorId ?? DBNull.Value),
                new("@p5", NpgsqlDbType.Varchar, approval.PayloadKind),
                new("@p6", NpgsqlDbType.Varchar, approval.BodyHash),
                new("@p7", NpgsqlDbType.Bigint, approval.EvidenceRevision),
                new("@p8", NpgsqlDbType.Varchar, approval.VoiceSnapshotHash),
                new("@p9", NpgsqlDbType.Varchar, approval.MediaSelectionHash),
                new("@p10", NpgsqlDbType.Varchar, (object?)approval.ArtifactHash ?? DBNull.Value),
                new("@p11", NpgsqlDbType.TimestampTz, approval.ApprovedAt),
                new("@p12", NpgsqlDbType.TimestampTz, (object?)approval.InvalidatedAt ?? DBNull.Value),
                new("@p13", NpgsqlDbType.Text, (object?)approval.Reason ?? DBNull.Value),
            ],
            cancellationToken).ConfigureAwait(false);

        return id is not null;
    }

    /// <inheritdoc />
    public async Task<int> InvalidateLiveAsync(
        Guid scopeId,
        Guid assetId,
        string reason,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        return await ChronicleafSql.ExecuteAsync(
            dbContext,
            "UPDATE content_approvals SET invalidated_at = @p3, reason = @p4 "
                + "WHERE scope_id = @p0 AND asset_id = @p1 AND invalidated_at IS NULL",
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, assetId),
                new("@p3", NpgsqlDbType.TimestampTz, now),
                new("@p4", NpgsqlDbType.Text, reason),
            ],
            cancellationToken).ConfigureAwait(false);
    }

    private static ContentApproval ReadApproval(DbDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetInt64(3),
            reader.IsDBNull(4) ? null : reader.GetGuid(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetInt64(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.GetFieldValue<DateTimeOffset>(11),
            reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
            reader.IsDBNull(13) ? null : reader.GetString(13));
}
