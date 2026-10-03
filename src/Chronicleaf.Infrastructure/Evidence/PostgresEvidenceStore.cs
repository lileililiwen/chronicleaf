using System.Data.Common;
using Chronicleaf.Application.Evidence;
using Chronicleaf.Application.Outbox;
using Chronicleaf.Core;
using Chronicleaf.Infrastructure.Persistence;
using Chronicleaf.Infrastructure.Projects;
using NpgsqlTypes;

namespace Chronicleaf.Infrastructure.Evidence;

/// <summary>Reads one stored evidence row, positionally against the column order below.</summary>
/// <remarks>
/// Stated rather than guessed at: a row is read positionally so a query cannot quietly hand the
/// wrong column to a field. Nothing here interprets a value or hides a null.
/// </remarks>
internal static class EvidenceRowReader
{
    /// <summary>The item columns, in the order <see cref="ReadItem"/> expects.</summary>
    public const string ItemColumns =
        "id, bundle_id, milestone_id, kind, source_ref, source_revision, captured_at, payload_sha256, " +
        "text, media_id, redactions, availability, supersedes_id";

    public static EvidenceItem ReadItem(DbDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetFieldValue<DateTimeOffset>(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetGuid(9),
            reader.GetFieldValue<string[]>(10),
            reader.GetString(11),
            reader.IsDBNull(12) ? null : reader.GetGuid(12));
}

/// <summary>
/// The PostgreSQL store for milestone evidence (MILESTONE-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// <para><b>Where the invariants live.</b> As much of the evidence argument as PostgreSQL can hold is
/// held here, rather than by the service agreeing to do the right thing:</para>
/// <list type="bullet">
/// <item><description><b>Append-only</b> - the two triggers installed by this package's migration refuse
/// an UPDATE and a DELETE on either table. This class issues neither, and the database refuses both
/// for anybody.</description></item>
/// <item><description><b>One bundle per revision</b> - the insert is
/// <c>ON CONFLICT (milestone_id, revision) DO NOTHING RETURNING id</c>. A returned id means this call
/// wrote the row; no row means the revision was already taken, and the service decides what to say
/// about it. Nothing computes a revision from a value it read earlier.</description></item>
/// <item><description><b>Scope</b> - every statement joins <c>milestones</c> to <c>projects</c> and
/// filters on <c>projects.scope_id</c>, including the bundle insert. A forged scope parameter finds
/// no milestone and commits nothing.</description></item>
/// <item><description><b>Archived projects are read-only</b> - the insert's WHERE clause names
/// <c>projects.archived_at IS NULL</c>, so an archived project's evidence cannot move even from a
/// caller that skipped the service.</description></item>
/// <item><description><b>Honesty</b> - the availability check constraints refuse a gap that carries a
/// payload, and refuse a partial source that names no exclusion.</description></item>
/// </list>
/// <para>Every value is a bound parameter, never text spliced into SQL, and every command joins the
/// unit of work's transaction so a bundle, its items, its audit row and its outbox row commit or
/// roll back together.</para>
/// </remarks>
internal sealed class PostgresEvidenceStore(ChronicleafDbContext dbContext) : IEvidenceStore
{
    public async Task<EvidenceBundle?> FindHeadAsync(
        Guid scopeId,
        Guid milestoneId,
        CancellationToken cancellationToken = default)
    {
        var head = await ChronicleafSql
            .QueryAsync(
                dbContext,
                """
                SELECT b.id, b.revision, b.created_at
                FROM evidence_bundles b
                INNER JOIN milestones m ON m.id = b.milestone_id
                INNER JOIN projects p ON p.id = m.project_id
                WHERE p.scope_id = @p0 AND b.milestone_id = @p1
                ORDER BY b.revision DESC
                LIMIT 1
                """,
                [new("@p0", NpgsqlDbType.Uuid, scopeId), new("@p1", NpgsqlDbType.Uuid, milestoneId)],
                reader => (Id: reader.GetGuid(0), Revision: reader.GetInt64(1), CreatedAt: reader.GetFieldValue<DateTimeOffset>(2)),
                cancellationToken)
            .ConfigureAwait(false);

        if (head.Count == 0)
        {
            return null;
        }

        return await ReadBundleAsync(scopeId, head[0].Id, head[0].Revision, head[0].CreatedAt, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<EvidenceBundle>> ListAsync(
        Guid scopeId,
        Guid milestoneId,
        long? beforeRevision,
        Guid? beforeId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        // Ordered revision descending, id descending, and paged by the same pair the cursor carries.
        // `id::text` is what makes the in-memory order of an appended bundle and the stored order
        // agree: both compare the id's text, so a bundle read straight after an append and the same
        // bundle read later are identical.
        var rows = await ChronicleafSql
            .QueryAsync(
                dbContext,
                $"""
                 SELECT b.id, b.revision, b.created_at
                 FROM evidence_bundles b
                 INNER JOIN milestones m ON m.id = b.milestone_id
                 INNER JOIN projects p ON p.id = m.project_id
                 WHERE p.scope_id = @p0
                   AND b.milestone_id = @p1
                   AND (@p2::bigint IS NULL OR (b.revision, b.id::text) < (@p2::bigint, @p3::text))
                 ORDER BY b.revision DESC, b.id::text DESC
                 LIMIT @p4
                 """,
                [
                    new("@p0", NpgsqlDbType.Uuid, scopeId),
                    new("@p1", NpgsqlDbType.Uuid, milestoneId),
                    new("@p2", NpgsqlDbType.Bigint, beforeRevision),
                    new("@p3", NpgsqlDbType.Text, beforeId?.ToString("D")),
                    new("@p4", NpgsqlDbType.Integer, limit),
                ],
                reader => (Id: reader.GetGuid(0), Revision: reader.GetInt64(1), CreatedAt: reader.GetFieldValue<DateTimeOffset>(2)),
                cancellationToken)
            .ConfigureAwait(false);

        var bundles = new List<EvidenceBundle>(rows.Count);
        foreach (var row in rows)
        {
            bundles.Add(await ReadBundleAsync(scopeId, row.Id, row.Revision, row.CreatedAt, cancellationToken)
                .ConfigureAwait(false));
        }

        return bundles;
    }

    public async Task<IReadOnlyList<EvidenceMediaReference>> ResolveMediaAsync(
        Guid scopeId,
        Guid projectId,
        IReadOnlyList<Guid> mediaIds,
        CancellationToken cancellationToken = default)
    {
        if (mediaIds.Count == 0)
        {
            return [];
        }

        // Scope, project and a status that still has bytes, all in one statement. A pending or deleted
        // record is not referenceable: an item that points at it would claim a payload that is not
        // there, which is the same lie as an unavailable source carrying text.
        var referenceable = await ChronicleafSql
            .QueryAsync(
                dbContext,
                """
                SELECT id
                FROM media
                WHERE scope_id = @p0
                  AND project_id = @p1
                  AND id = ANY(@p2)
                  AND status IN ('ready', 'retained')
                """,
                [
                    new("@p0", NpgsqlDbType.Uuid, scopeId),
                    new("@p1", NpgsqlDbType.Uuid, projectId),
                    new("@p2", NpgsqlDbType.Array | NpgsqlDbType.Uuid, mediaIds.ToArray()),
                ],
                reader => reader.GetGuid(0),
                cancellationToken)
            .ConfigureAwait(false);

        var allowed = referenceable.ToHashSet();

        // Answered for every id asked about, not only the ones found: the caller learns that an id is
        // not referenceable, and never which of the three reasons it was not.
        return [.. mediaIds.Select(mediaId => new EvidenceMediaReference(mediaId, allowed.Contains(mediaId)))];
    }

    public async Task<EvidenceCommitStatus> TryInsertBundleAsync(
        Guid scopeId,
        Guid milestoneId,
        Guid bundleId,
        long expectedRevision,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        var inserted = await ChronicleafSql
            .ScalarAsync<Guid>(
                dbContext,
                """
                INSERT INTO evidence_bundles (id, milestone_id, revision, created_at)
                SELECT @p0, m.id, @p1 + 1, @p2
                FROM milestones m
                INNER JOIN projects p ON p.id = m.project_id
                WHERE m.id = @p3 AND p.scope_id = @p4 AND p.archived_at IS NULL
                ON CONFLICT (milestone_id, revision) DO NOTHING
                RETURNING id
                """,
                [
                    new("@p0", NpgsqlDbType.Uuid, bundleId),
                    new("@p1", NpgsqlDbType.Bigint, expectedRevision),
                    new("@p2", NpgsqlDbType.TimestampTz, createdAt.UtcDateTime),
                    new("@p3", NpgsqlDbType.Uuid, milestoneId),
                    new("@p4", NpgsqlDbType.Uuid, scopeId),
                ],
                cancellationToken)
            .ConfigureAwait(false);

        // A returned id means this statement wrote the row. No row means the revision was taken by a
        // concurrent append, or the milestone is out of scope or archived - and the service re-reads
        // to say which, rather than this statement guessing.
        return inserted == Guid.Empty ? EvidenceCommitStatus.RevisionTaken : EvidenceCommitStatus.Committed;
    }

    public async Task InsertItemsAsync(
        Guid scopeId,
        Guid bundleId,
        IReadOnlyList<EvidenceItem> items,
        CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
        {
            return;
        }

        // One statement per item, in the caller's transaction, with the same fixed parameter names the
        // delivered stores use. A statement assembled from a variable number of placeholders would be
        // a different statement per bundle, and the one thing this table must never do is have its
        // shape decided by a value. The bundle bound is 500 items, so the loop is bounded too.
        foreach (var item in items)
        {
            await ChronicleafSql
                .ExecuteAsync(
                    dbContext,
                    """
                    INSERT INTO evidence_items
                        (id, bundle_id, milestone_id, kind, source_ref, source_revision, captured_at,
                         payload_sha256, text, media_id, redactions, availability, supersedes_id)
                    SELECT @p0, @p1, m.id, @p3, @p4, @p5, @p6, @p7, @p8, @p9, @p10, @p11, @p12
                    FROM milestones m
                    INNER JOIN projects pr ON pr.id = m.project_id
                    WHERE m.id = @p2 AND pr.scope_id = @p13
                    """,
                    [
                        new("@p0", NpgsqlDbType.Uuid, item.Id),
                        new("@p1", NpgsqlDbType.Uuid, bundleId),
                        new("@p2", NpgsqlDbType.Uuid, item.MilestoneId),
                        new("@p3", NpgsqlDbType.Varchar, item.Kind),
                        new("@p4", NpgsqlDbType.Varchar, item.SourceRef),
                        new("@p5", NpgsqlDbType.Varchar, item.SourceRevision),
                        new("@p6", NpgsqlDbType.TimestampTz, item.CapturedAt.UtcDateTime),
                        new("@p7", NpgsqlDbType.Varchar, item.PayloadSha256),
                        new("@p8", NpgsqlDbType.Text, item.Text),
                        new("@p9", NpgsqlDbType.Uuid, item.MediaId),
                        new("@p10", NpgsqlDbType.Array | NpgsqlDbType.Text, item.Redactions.ToArray()),
                        new("@p11", NpgsqlDbType.Varchar, item.Availability),
                        new("@p12", NpgsqlDbType.Uuid, item.SupersedesId),
                        new("@p13", NpgsqlDbType.Uuid, scopeId),
                    ],
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    public async Task TryAppendOutboxAsync(
        Guid scopeId,
        Guid milestoneId,
        long revision,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default)
    {
        // The delivered envelope with the milestone and the revision and nothing else. A consumer
        // learns what changed and then reads the snapshot from PostgreSQL; no collected text, no
        // source payload and no media id travels in the event itself.
        var payload = new OutboxPayload
        {
            Kind = MilestoneEvidenceService.EvidenceChangedKind,
            ScopeId = scopeId,
            AggregateId = milestoneId,
            Revision = revision,
        };

        // The unique index on (kind, aggregate_id, revision) makes a duplicate impossible: bundle
        // revisions are unique per milestone, so a committed bundle produces exactly one event.
        await ChronicleafSql
            .ExecuteAsync(
                dbContext,
                """
                INSERT INTO outbox (id, kind, aggregate_id, revision, payload, created_at, dispatched_at)
                SELECT @p0, @p1, @p2, @p3, @p4, @p5, NULL
                ON CONFLICT (kind, aggregate_id, revision) DO NOTHING
                """,
                [
                    new("@p0", NpgsqlDbType.Uuid, Guid.NewGuid()),
                    new("@p1", NpgsqlDbType.Varchar, MilestoneEvidenceService.EvidenceChangedKind),
                    new("@p2", NpgsqlDbType.Uuid, milestoneId),
                    new("@p3", NpgsqlDbType.Bigint, revision),
                    new("@p4", NpgsqlDbType.Jsonb, payload.ToJson()),
                    new("@p5", NpgsqlDbType.TimestampTz, createdAt.UtcDateTime),
                ],
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads a bundle's items in the same order the service assembled them. Two statements rather than
    /// one join so the page of bundles and the item sets stay independent: a page of 25 bundles
    /// returns 25 item queries, not a cartesian product the database has to sort.
    /// </summary>
    private async Task<EvidenceBundle> ReadBundleAsync(
        Guid scopeId,
        Guid bundleId,
        long revision,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        var milestoneId = await ChronicleafSql
            .ScalarAsync<Guid>(
                dbContext,
                """
                SELECT b.milestone_id
                FROM evidence_bundles b
                INNER JOIN milestones m ON m.id = b.milestone_id
                INNER JOIN projects p ON p.id = m.project_id
                WHERE p.scope_id = @p0 AND b.id = @p1
                """,
                [new("@p0", NpgsqlDbType.Uuid, scopeId), new("@p1", NpgsqlDbType.Uuid, bundleId)],
                cancellationToken)
            .ConfigureAwait(false);

        var items = await ChronicleafSql
            .QueryAsync(
                dbContext,
                """
                SELECT id, bundle_id, milestone_id, kind, source_ref, source_revision, captured_at,
                       payload_sha256, text, media_id, redactions, availability, supersedes_id
                FROM evidence_items
                WHERE bundle_id = @p0
                ORDER BY id::text
                """,
                [new("@p0", NpgsqlDbType.Uuid, bundleId)],
                EvidenceRowReader.ReadItem,
                cancellationToken)
            .ConfigureAwait(false);

        return new EvidenceBundle(bundleId, milestoneId, revision, createdAt, items);
    }
}
