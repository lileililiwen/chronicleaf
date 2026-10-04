using Chronicleaf.Application.Content;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.Content;

/// <summary>
/// The draft-request table in PostgreSQL (GROUNDED-MARKDOWN-DRAFTS-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Two statements and no interpretation. The row exists because a queued job payload carries an
/// operation id and nothing else, so the words the owner typed have to be durable somewhere the worker
/// can read them from; the row is written once and never updated, because a request that can be edited
/// after it was accepted is a request the worker might act on a second time with different content.
/// </para>
/// <para>
/// <b>No secret and no evidence text is stored here.</b> The consent binding is a SHA-256 over a
/// document this package cannot reverse, and the input itself is read back from the frozen preview the
/// request names. A queue row is the wrong place for an owner's evidence: it is written by a
/// background path, carried by a dispatcher, and retained by a retention policy nobody has audited.
/// </para>
/// <para>
/// The read is scoped in its <c>WHERE</c> clause, so a request belonging to another scope answers
/// <c>null</c> and the handler turns that into a permanent failure rather than reaching for it anyway.
/// </para>
/// </remarks>
public sealed class PostgresDraftRequestStore(ChronicleafDbContext dbContext) : IDraftRequestStore
{
    private const string Columns =
        "id, scope_id, operation_id, project_id, milestone_id, preview_id, consent_hash, "
        + "evidence_revision, voice_revision, provider_id, model, created_at";

    /// <inheritdoc />
    public async Task<ContentDraftRequestRow?> FindByOperationAsync(
        Guid scopeId,
        Guid operationId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync<ContentDraftRequestRow>(
            dbContext,
            $"SELECT {Columns} FROM content_draft_requests "
                + "WHERE scope_id = @p0 AND operation_id = @p1",
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, operationId),
            ],
            Read,
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async Task<bool> TryInsertAsync(
        ContentDraftRequestRow row,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(row);

        // The unique (scope, operation) index decides the race, not a read this method did first: a
        // returned id means this call wrote the row, and no row means a re-delivered enqueue already
        // left one behind for this job.
        var id = await ChronicleafSql.ScalarAsync<Guid?>(
            dbContext,
            """
            INSERT INTO content_draft_requests
                (id, scope_id, operation_id, project_id, milestone_id, preview_id, consent_hash,
                 evidence_revision, voice_revision, provider_id, model, created_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11)
            ON CONFLICT (scope_id, operation_id) DO NOTHING
            RETURNING id
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, row.Id),
                new("@p1", NpgsqlDbType.Uuid, row.ScopeId),
                new("@p2", NpgsqlDbType.Uuid, row.OperationId),
                new("@p3", NpgsqlDbType.Uuid, row.ProjectId),
                new("@p4", NpgsqlDbType.Uuid, row.MilestoneId),
                new("@p5", NpgsqlDbType.Uuid, row.PreviewId),
                new("@p6", NpgsqlDbType.Varchar, row.ConsentHash),
                new("@p7", NpgsqlDbType.Bigint, row.EvidenceRevision),
                new("@p8", NpgsqlDbType.Bigint, row.VoiceRevision),
                new("@p9", NpgsqlDbType.Varchar, row.ProviderId),
                new("@p10", NpgsqlDbType.Varchar, row.Model),
                new("@p11", NpgsqlDbType.TimestampTz, row.CreatedAt),
            ],
            cancellationToken).ConfigureAwait(false);

        return id is not null;
    }

    private static ContentDraftRequestRow Read(System.Data.Common.DbDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetGuid(3),
            reader.GetGuid(4),
            reader.GetGuid(5),
            reader.GetString(6),
            reader.GetInt64(7),
            reader.GetInt64(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetFieldValue<DateTimeOffset>(11));
}
