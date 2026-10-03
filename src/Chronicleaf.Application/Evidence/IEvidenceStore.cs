using Chronicleaf.Core;

namespace Chronicleaf.Application.Evidence;

/// <summary>
/// What a reference check found about one media id (MILESTONE-EVIDENCE-002).
/// </summary>
/// <param name="MediaId">The media id that was asked about.</param>
/// <param name="IsReferenceable">
/// True when the media is in the caller's scope, belongs to the milestone's project and still has
/// bytes. False means absent, foreign, in another project, or without bytes - and all four are one
/// answer, so an evidence append can never be used to probe outside its own installation.
/// </param>
public sealed record EvidenceMediaReference(Guid MediaId, bool IsReferenceable);

/// <summary>
/// The result of asking the database to commit a new bundle at a given revision.
/// </summary>
public enum EvidenceCommitStatus
{
    /// <summary>The bundle row was inserted and its items are stored.</summary>
    Committed,

    /// <summary>
    /// No row was inserted: the revision was already taken. The caller re-reads the head and decides
    /// between "these facts are already recorded" and "somebody else advanced the revision".
    /// </summary>
    RevisionTaken,
}

/// <summary>
/// The storage contract for milestone evidence (MILESTONE-EVIDENCE-001/002).
/// </summary>
/// <remarks>
/// Every method is scoped: a lookup for a milestone in another installation answers <c>null</c>
/// rather than finding it, and every statement carries the same scope in its WHERE clause. The
/// three invariants this package needs are left to the database rather than to a caller being
/// careful - the store inserts a bundle at exactly one revision and lets a unique index decide,
/// refuses nothing that a later trigger will refuse again, and never issues an UPDATE or a DELETE
/// against either evidence table.
/// </remarks>
public interface IEvidenceStore
{
    /// <summary>
    /// The milestone's newest bundle and its whole item set, or <c>null</c> when the milestone has
    /// no evidence yet or is out of scope.
    /// </summary>
    Task<EvidenceBundle?> FindHeadAsync(
        Guid scopeId,
        Guid milestoneId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Bundles newest revision first, starting after the cursor position and returning at most
    /// <paramref name="limit"/> rows. Passing no position starts at the newest.
    /// </summary>
    Task<IReadOnlyList<EvidenceBundle>> ListAsync(
        Guid scopeId,
        Guid milestoneId,
        long? beforeRevision,
        Guid? beforeId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>Answers which of the given media ids may be referenced by this milestone's evidence.</summary>
    Task<IReadOnlyList<EvidenceMediaReference>> ResolveMediaAsync(
        Guid scopeId,
        Guid projectId,
        IReadOnlyList<Guid> mediaIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits the bundle row at <c>expectedRevision + 1</c>. The insert is a compare-and-set: the
    /// unique index on (milestone, revision) is what makes two appends that present the same
    /// revision resolve to one committed bundle.
    /// </summary>
    Task<EvidenceCommitStatus> TryInsertBundleAsync(
        Guid scopeId,
        Guid milestoneId,
        Guid bundleId,
        long expectedRevision,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores one bundle's complete item set. Called only after the bundle row was inserted, in the
    /// same transaction.
    /// </summary>
    Task InsertItemsAsync(
        Guid scopeId,
        Guid bundleId,
        IReadOnlyList<EvidenceItem> items,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the <c>evidence.changed</c> outbox row. The payload carries the milestone id and the
    /// revision and nothing else - no item text, no media, no source payload - so a consumer has to
    /// read the snapshot to learn what happened.
    /// </summary>
    Task TryAppendOutboxAsync(
        Guid scopeId,
        Guid milestoneId,
        long revision,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default);
}
