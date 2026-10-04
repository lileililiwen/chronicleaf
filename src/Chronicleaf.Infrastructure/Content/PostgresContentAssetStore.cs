using System.Data.Common;
using Chronicleaf.Application.Content;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.Content;

/// <summary>
/// The content tables in PostgreSQL (GROUNDED-MARKDOWN-DRAFTS-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Every statement is parameterized and every one carries the scope in its <c>WHERE</c> clause, so a
/// forged parameter cannot read or write across installations. A read for an id that exists in another
/// scope returns nothing and a write for one changes nothing, which is what makes a foreign-scoped asset
/// a 404 rather than a leak.
/// </para>
/// <para>
/// <b>There is no UPDATE against <c>content_revisions</c> in this file, and that is the requirement
/// rather than an omission.</b> A revision is a provenance record; the review package will bind an
/// approval to a revision and a body hash, and a text that could be edited afterwards would make that
/// binding a signature over nothing. The one statement that moves anything is
/// <see cref="PostgresContentAssetStore.TryAppendRevisionAsync"/>, and what it moves is the asset's
/// head pointer in the same statement that inserts the new revision.
/// </para>
/// <para>
/// <b>The append is one statement, not two.</b> A revision whose head was not advanced would be
/// unreachable through its asset, and an advanced head with no revision would point at nothing - so the
/// insert and the compare-and-set share a statement and a transaction, and the caller learns which
/// happened from the row count.
/// </para>
/// <para>
/// The one exception to "no raw SQL interpretation" is
/// <see cref="PostgresContentAssetStore.ResolveAvailabilityAsync"/>, a bounded read over
/// <c>evidence_items</c> for the ids one frozen bundle holds. It is asked of Chronicleaf's own table by
/// name rather than added to the delivered evidence contract, because the alternative is changing a
/// package this one depends on for one question about one bundle.
/// </para>
/// </remarks>
public sealed class PostgresContentAssetStore(ChronicleafDbContext dbContext) : IContentAssetStore
{
    private const string AssetColumns =
        "id, scope_id, project_id, milestone_id, type, status, current_revision, created_at, updated_at";

    private const string RevisionColumns =
        "id, asset_id, scope_id, revision, evidence_bundle_id, evidence_revision, voice_revision, "
        + "voice_snapshot, summary, body_markdown, body_hash, provider_id, model, prompt_version, "
        + "schema_version, created_at";

    /// <inheritdoc />
    public async Task<ContentAsset?> FindAsync(
        Guid scopeId,
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync<ContentAsset>(
            dbContext,
            $"SELECT {AssetColumns} FROM content_assets WHERE scope_id = @p0 AND id = @p1",
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, assetId),
            ],
            ReadAsset,
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async Task<ContentAsset?> FindForMilestoneAsync(
        Guid scopeId,
        Guid milestoneId,
        string type,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync<ContentAsset>(
            dbContext,
            $"SELECT {AssetColumns} FROM content_assets "
                + "WHERE scope_id = @p0 AND milestone_id = @p1 AND type = @p2",
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, milestoneId),
                new("@p2", NpgsqlDbType.Varchar, type),
            ],
            ReadAsset,
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async Task<bool> TryCreateAsync(ContentAsset asset, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);

        // ON CONFLICT DO NOTHING RETURNING is how the unique (scope, milestone, type) index decides the
        // race, rather than a read this method did first: a returned id means this call wrote the row.
        var id = await ChronicleafSql.ScalarAsync<Guid?>(
            dbContext,
            """
            INSERT INTO content_assets
                (id, scope_id, project_id, milestone_id, type, status, current_revision, created_at, updated_at)
            VALUES (@p0, @p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8)
            ON CONFLICT (scope_id, milestone_id, type) DO NOTHING
            RETURNING id
            """,
            [
                new("@p0", NpgsqlDbType.Uuid, asset.Id),
                new("@p1", NpgsqlDbType.Uuid, asset.ScopeId),
                new("@p2", NpgsqlDbType.Uuid, asset.ProjectId),
                new("@p3", NpgsqlDbType.Uuid, asset.MilestoneId),
                new("@p4", NpgsqlDbType.Varchar, asset.Type),
                new("@p5", NpgsqlDbType.Varchar, asset.Status),
                new("@p6", NpgsqlDbType.Bigint, asset.CurrentRevision),
                new("@p7", NpgsqlDbType.TimestampTz, asset.CreatedAt),
                new("@p8", NpgsqlDbType.TimestampTz, asset.UpdatedAt),
            ],
            cancellationToken).ConfigureAwait(false);

        return id is not null;
    }

    /// <inheritdoc />
    public async Task<bool> TryAppendRevisionAsync(
        ContentRevision revision,
        long expectedRevision,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(revision);

        if (revision.Revision != expectedRevision + 1)
        {
            // The version and the compare-and-set cannot disagree. A caller that presented one head and
            // tried to write another is a bug, and catching it here means the store can never produce a
            // gap or a duplicate in the version sequence.
            throw new InvalidOperationException(
                "A content revision must be the head revision it advances from, plus one.");
        }

        // One statement, and the returned count is the decision. The insert and the head update are two
        // data-modifying CTEs, so they share a snapshot and commit together; the count is the number of
        // asset rows the compare-and-set actually moved, which is one when this revision became the head
        // and zero when the stored head was not the one the caller read.
        //
        // A zero does not leave an unreachable revision behind, because the caller owns the transaction
        // and rolls it back on false - and the rollback takes the inserted row with it. That is the whole
        // reason this is a statement the caller can refuse rather than two statements it would have to
        // undo itself.
        var moved = await ChronicleafSql.ScalarAsync<long>(
            dbContext,
            """
            WITH inserted AS (
                INSERT INTO content_revisions
                    (id, asset_id, scope_id, revision, evidence_bundle_id, evidence_revision,
                     voice_revision, voice_snapshot, summary, body_markdown, body_hash,
                     provider_id, model, prompt_version, schema_version, created_at)
                VALUES (@p1, @p2, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12, @p13, @p14, @p15, @p16)
                RETURNING asset_id
            ), advanced AS (
                UPDATE content_assets AS asset
                SET current_revision = @p4, updated_at = @p17
                FROM inserted
                WHERE asset.id = inserted.asset_id
                  AND asset.scope_id = @p3
                  AND asset.current_revision = @p0
                RETURNING asset.id
            )
            SELECT count(*) FROM advanced
            """,
            [
                new("@p0", NpgsqlDbType.Bigint, expectedRevision),
                new("@p1", NpgsqlDbType.Uuid, revision.Id),
                new("@p2", NpgsqlDbType.Uuid, revision.AssetId),
                new("@p3", NpgsqlDbType.Uuid, revision.ScopeId),
                new("@p4", NpgsqlDbType.Bigint, revision.Revision),
                new("@p5", NpgsqlDbType.Uuid, revision.EvidenceBundleId),
                new("@p6", NpgsqlDbType.Bigint, revision.EvidenceRevision),
                new("@p7", NpgsqlDbType.Bigint, revision.VoiceRevision),
                new("@p8", NpgsqlDbType.Jsonb, revision.VoiceSnapshotJson),
                new("@p9", NpgsqlDbType.Jsonb, revision.SummaryJson),
                new("@p10", NpgsqlDbType.Text, revision.BodyMarkdown),
                new("@p11", NpgsqlDbType.Varchar, revision.BodyHash),
                new("@p12", NpgsqlDbType.Varchar, revision.ProviderId),
                new("@p13", NpgsqlDbType.Varchar, revision.Model),
                new("@p14", NpgsqlDbType.Varchar, revision.PromptVersion),
                new("@p15", NpgsqlDbType.Varchar, revision.SchemaVersion),
                new("@p16", NpgsqlDbType.TimestampTz, revision.CreatedAt),
                new("@p17", NpgsqlDbType.TimestampTz, updatedAt),
            ],
            cancellationToken).ConfigureAwait(false);

        return moved == 1;
    }

    /// <inheritdoc />
    public async Task<ContentRevision?> FindRevisionAsync(
        Guid scopeId,
        Guid assetId,
        long revisionNumber,
        CancellationToken cancellationToken = default)
    {
        var rows = await ChronicleafSql.QueryAsync<ContentRevision>(
            dbContext,
            $"SELECT {RevisionColumns} FROM content_revisions "
                + "WHERE scope_id = @p0 AND asset_id = @p1 AND revision = @p2",
            [
                new("@p0", NpgsqlDbType.Uuid, scopeId),
                new("@p1", NpgsqlDbType.Uuid, assetId),
                new("@p2", NpgsqlDbType.Bigint, revisionNumber),
            ],
            ReadRevision,
            cancellationToken).ConfigureAwait(false);

        return rows.Count == 0 ? null : rows[0];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, string?>> ResolveAvailabilityAsync(
        Guid scopeId,
        Guid bundleId,
        IReadOnlyList<Guid> evidenceIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidenceIds);

        var availability = new Dictionary<Guid, string?>();

        if (bundleId == Guid.Empty || evidenceIds.Count == 0)
        {
            return availability;
        }

        // Every requested id is answered, whether or not the bundle holds it: an absent id is
        // information the read route reports as a finding rather than as a missing key, because
        // "this claim cites something the bundle does not contain" is a different statement from
        // "this claim cites nothing".
        var parameterNames = new List<string>(evidenceIds.Count);
        var parameters = new List<SqlParameter> { new("@p0", NpgsqlDbType.Uuid, bundleId) };

        for (var index = 0; index < evidenceIds.Count; index++)
        {
            var name = $"@e{index}";
            parameterNames.Add(name);
            parameters.Add(new(name, NpgsqlDbType.Uuid, evidenceIds[index]));
        }

        var rows = await ChronicleafSql.QueryAsync<(Guid Id, string Availability)>(
            dbContext,
            "SELECT id, availability FROM evidence_items "
                + $"WHERE bundle_id = @p0 AND id IN ({string.Join(", ", parameterNames)})",
            parameters,
            reader => (reader.GetGuid(0), reader.GetString(1)),
            cancellationToken).ConfigureAwait(false);

        foreach (var id in evidenceIds)
        {
            availability[id] = null;
        }

        foreach (var row in rows)
        {
            availability[row.Id] = row.Availability;
        }

        return availability;
    }

    private static ContentAsset ReadAsset(DbDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetGuid(3),
            reader.GetString(4),
            reader.GetInt64(6),
            reader.GetFieldValue<DateTimeOffset>(7),
            reader.GetFieldValue<DateTimeOffset>(8));

    private static ContentRevision ReadRevision(DbDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetInt64(3),
            reader.GetGuid(4),
            reader.GetInt64(5),
            reader.GetInt64(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.GetString(14),
            reader.GetFieldValue<DateTimeOffset>(15));
}
