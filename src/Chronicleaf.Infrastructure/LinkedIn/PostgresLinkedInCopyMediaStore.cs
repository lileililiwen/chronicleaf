using System.Data.Common;
using Chronicleaf.Application.LinkedIn;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.LinkedIn;

/// <summary>
/// The LinkedIn copy selection rows in PostgreSQL (LINKEDIN-COPY-PACKAGE-001).
/// </summary>
/// <remarks>
/// <para>
/// Every statement is parameterized and every one carries the scope in its <c>WHERE</c> clause, so
/// a forged parameter cannot read across installations. Rows are inserted beside the revision
/// they belong to and there is no UPDATE and no DELETE in this file: an edited copy is a new
/// revision with its own rows.
/// </para>
/// <para>
/// The unique tuple <c>(asset_id, revision, media_id)</c> is enforced by the database, so a
/// retried adaptation cannot record one picture twice for one revision.
/// </para>
/// </remarks>
public sealed class PostgresLinkedInCopyMediaStore(ChronicleafDbContext dbContext) : ILinkedInCopyMediaStore
{
    private const string SelectionColumns = "scope_id, asset_id, revision, media_id, alt_text";

    /// <inheritdoc />
    public async Task AddAsync(
        Guid scopeId,
        Guid assetId,
        long revision,
        IReadOnlyList<LinkedInMediaSelection> selections,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selections);

        foreach (var selection in selections)
        {
            await ChronicleafSql.ExecuteAsync(
                dbContext,
                $"INSERT INTO linkedin_copy_media ({SelectionColumns}) VALUES (@p0, @p1, @p2, @p3, @p4) "
                + "ON CONFLICT (asset_id, revision, media_id) DO NOTHING",
                [
                    new("@p0", NpgsqlDbType.Uuid, scopeId),
                    new("@p1", NpgsqlDbType.Uuid, assetId),
                    new("@p2", NpgsqlDbType.Bigint, revision),
                    new("@p3", NpgsqlDbType.Uuid, selection.MediaId),
                    new("@p4", NpgsqlDbType.Varchar, selection.AltText),
                ],
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<LinkedInMediaSelection>> ListAsync(
        Guid scopeId,
        Guid assetId,
        long revision,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync<LinkedInMediaSelection>(
            dbContext,
            "SELECT media_id, alt_text FROM linkedin_copy_media "
            + "WHERE scope_id = @p0 AND asset_id = @p1 AND revision = @p2 "
            + "ORDER BY media_id",
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, assetId),
                new("@p2", NpgsqlDbType.Bigint, revision),
            ],
            ReadSelection,
            cancellationToken).ConfigureAwait(false);

        return rows;
    }

    private static LinkedInMediaSelection ReadSelection(DbDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetString(1));
}
