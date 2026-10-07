using System.Data.Common;
using Chronicleaf.Application.Xiaohongshu;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.Xiaohongshu;

/// <summary>
/// The Xiaohongshu pack selection rows in PostgreSQL (XIAOHONGSHU-CONTENT-PACKAGE-001).
/// </summary>
/// <remarks>
/// <para>
/// Every statement is parameterized and every one carries the scope in its <c>WHERE</c> clause, so
/// a forged parameter cannot read across installations. Rows are inserted beside the revision
/// they belong to in the owner's image order and there is no UPDATE and no DELETE in this file:
/// an edited pack is a new revision with its own rows.
/// </para>
/// <para>
/// The unique tuple <c>(asset_id, revision, media_id)</c> is enforced by the database, so a
/// retried adaptation cannot record one picture twice for one revision. The position column
/// carries the owner's image order, so the export serves the arrangement the approval covers.
/// </para>
/// </remarks>
public sealed class PostgresXiaohongshuPackMediaStore(ChronicleafDbContext dbContext)
    : IXiaohongshuPackMediaStore
{
    private const string SelectionColumns = "scope_id, asset_id, revision, position, media_id, alt_text";

    /// <inheritdoc />
    public async Task AddAsync(
        Guid scopeId,
        Guid assetId,
        long revision,
        IReadOnlyList<XiaohongshuMediaSelection> selections,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selections);

        for (var position = 0; position < selections.Count; position++)
        {
            await ChronicleafSql.ExecuteAsync(
                dbContext,
                $"INSERT INTO xiaohongshu_pack_media ({SelectionColumns}) VALUES (@p0, @p1, @p2, @p3, @p4, @p5) "
                + "ON CONFLICT (asset_id, revision, media_id) DO NOTHING",
                [
                    new("@p0", NpgsqlDbType.Uuid, scopeId),
                    new("@p1", NpgsqlDbType.Uuid, assetId),
                    new("@p2", NpgsqlDbType.Bigint, revision),
                    new("@p3", NpgsqlDbType.Integer, position),
                    new("@p4", NpgsqlDbType.Uuid, selections[position].MediaId),
                    new("@p5", NpgsqlDbType.Varchar, selections[position].AltText),
                ],
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<XiaohongshuMediaSelection>> ListAsync(
        Guid scopeId,
        Guid assetId,
        long revision,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync<XiaohongshuMediaSelection>(
            dbContext,
            "SELECT media_id, alt_text FROM xiaohongshu_pack_media "
            + "WHERE scope_id = @p0 AND asset_id = @p1 AND revision = @p2 "
            + "ORDER BY position",
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, assetId),
                new("@p2", NpgsqlDbType.Bigint, revision),
            ],
            ReadSelection,
            cancellationToken).ConfigureAwait(false);

        return rows;
    }

    private static XiaohongshuMediaSelection ReadSelection(DbDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetString(1));
}
