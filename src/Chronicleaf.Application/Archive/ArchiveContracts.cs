using Chronicleaf.Application.Milestones;
using Chronicleaf.Core;

namespace Chronicleaf.Application.Archive;

/// <summary>
/// The operator's backup policy as this deployment runs it (HOSTED-ARCHIVE-OPERATIONS-001).
/// </summary>
/// <remarks>
/// The current encryption key version is configuration rather than discovery: the rehearsal
/// compares the manifest's recorded version against this value, so rotating the operator key
/// without a fresh backup fails the next rehearsal loudly instead of restoring secrets nobody
/// can open. The backup-copy expiry documents the operator retention policy; enforcement lives
/// in the operator's inventory, not in this row.
/// </remarks>
public sealed class ArchiveBackupOptions
{
    public const string SectionName = "Archive";

    /// <summary>The key version the deployment verifies backups against. One or greater.</summary>
    public int CurrentEncryptionKeyVersion { get; set; } = 1;
}

/// <summary>What an operator records: the backup's own measurements, never live state.</summary>
public sealed record ArchiveManifestRequest(
    int SchemaVersion,
    string DbBackupRef,
    string MediaInventoryHash,
    string ContentDigest,
    int EncryptionKeyVersion);

/// <summary>The live scope's own measurements, for the operator's backup inventory.</summary>
public sealed record ArchiveMeasurements(string MediaInventoryHash, string ContentDigest);

/// <summary>One manifest as an operator reads it: versions, hashes and verification, never a location.</summary>
public sealed record ArchiveManifestView(
    Guid Id,
    Guid ScopeId,
    int SchemaVersion,
    string DbBackupRef,
    string MediaInventoryHash,
    string ContentDigest,
    int EncryptionKeyVersion,
    bool Verified,
    DateTimeOffset? RestoreVerifiedAt,
    long Revision,
    DateTimeOffset CreatedAt)
{
    public static ArchiveManifestView From(ArchiveBackupManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        return new ArchiveManifestView(
            manifest.Id,
            manifest.ScopeId,
            manifest.SchemaVersion,
            manifest.DbBackupRef,
            manifest.MediaInventoryHash,
            manifest.ContentDigest,
            manifest.EncryptionKeyVersion,
            manifest.RestoreVerifiedAt is not null,
            manifest.RestoreVerifiedAt,
            manifest.Revision,
            manifest.CreatedAt);
    }
}

/// <summary>One tombstone as served: the window, the erase intent, the hold and the outcome.</summary>
public sealed record ArchiveTombstoneView(
    Guid ProjectId,
    string ProjectName,
    DateTimeOffset DeletedAt,
    DateTimeOffset RecoveryUntil,
    bool EraseRequested,
    string? HoldReason,
    DateTimeOffset? RestoredAt,
    DateTimeOffset? PurgedAt,
    bool Live,
    long Revision)
{
    public static ArchiveTombstoneView From(ArchiveTombstone tombstone)
    {
        ArgumentNullException.ThrowIfNull(tombstone);

        return new ArchiveTombstoneView(
            tombstone.ProjectId,
            tombstone.ProjectName,
            tombstone.DeletedAt,
            tombstone.RecoveryUntil,
            tombstone.EraseRequested,
            tombstone.HoldReason,
            tombstone.RestoredAt,
            tombstone.PurgedAt,
            tombstone.IsLive,
            tombstone.Revision);
    }
}

/// <summary>What the operator purge removed and what it kept, per tombstone.</summary>
public sealed record ArchivePurgeReceipt(
    Guid TombstoneId,
    Guid ProjectId,
    DateTimeOffset PurgedAt,
    IReadOnlyList<Guid> RetiredMedia,
    IReadOnlyList<Guid> RetainedMediaInUse,
    long RetainedEvidenceBundles,
    long RetainedContentAssets,
    bool Skipped,
    string? SkipReason);

/// <summary>
/// The Chronicleaf-owned side of the tombstone table (HOSTED-ARCHIVE-OPERATIONS-002).
/// </summary>
/// <remarks>
/// Every method is scoped, and the scope is in the WHERE clause rather than in a filter applied
/// afterwards, so a lookup by project can only ever return a tombstone inside the caller's own
/// installation. The purge pass is the one exception: it reads across scopes, because it is the
/// operator's pass rather than an owner's request.
/// </remarks>
public interface IArchiveTombstoneStore
{
    /// <summary>Reads one scope's live tombstone for a project, or <c>null</c>.</summary>
    Task<ArchiveTombstone?> FindLiveAsync(
        Guid scopeId,
        Guid projectId,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one scope's tombstone by id, live or not, or <c>null</c>.</summary>
    Task<ArchiveTombstone?> FindAsync(
        Guid scopeId,
        Guid tombstoneId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts one tombstone. Returns false when the project already holds a live one, which is
    /// the partial unique index refusing a second window rather than a merged one.
    /// </summary>
    Task<bool> TryInsertAsync(ArchiveTombstone tombstone, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the tombstone only when its stored revision is still
    /// <paramref name="expectedRevision"/>. Returns false when another writer moved it first.
    /// </summary>
    Task<bool> TryUpdateAsync(
        ArchiveTombstone tombstone,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    /// <summary>Reads every purge candidate: erase requested, never restored, never purged.</summary>
    Task<IReadOnlyList<ArchiveTombstone>> ListPurgeCandidatesAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The Chronicleaf-owned side of the backup-manifest table (HOSTED-ARCHIVE-OPERATIONS-001).
/// </summary>
/// <remarks>
/// Manifests are operator rows, not owner content: there is no route that serves them, so this
/// store is read by the rehearsal and by operator tooling through the service, never by a
/// request. Every method is scoped for the same reason the tombstone store is.
/// </remarks>
public interface IArchiveBackupManifestStore
{
    /// <summary>Inserts one manifest. Ids are operator-generated and unique by primary key.</summary>
    Task InsertAsync(ArchiveBackupManifest manifest, CancellationToken cancellationToken = default);

    /// <summary>Reads one scope's manifest, or <c>null</c> when it is absent or out of scope.</summary>
    Task<ArchiveBackupManifest?> FindAsync(
        Guid scopeId,
        Guid manifestId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the manifest only when its stored revision is still
    /// <paramref name="expectedRevision"/>. Returns false when another writer moved it first.
    /// </summary>
    Task<bool> TryUpdateAsync(
        ArchiveBackupManifest manifest,
        long expectedRevision,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Recomputes a scope's archive hashes from the live rows (HOSTED-ARCHIVE-OPERATIONS-001).
/// </summary>
/// <remarks>
/// The digest is what makes "byte-identical or fail loudly" a property of stored rows rather
/// than of code paths: the same canonical lines are hashed at record time by the operator's
/// inventory and at rehearsal time here, so any added, removed or changed row fails the
/// comparison. Every line carries its table, so a row in another scope can never satisfy this
/// scope's digest.
/// </remarks>
public interface IArchiveDigestReader
{
    /// <summary>The schema version the live rows below were read under.</summary>
    int LiveSchemaVersion { get; }

    /// <summary>
    /// The canonical media lines of one scope: <c>media:{id}:{sha256}:{status}</c>, one per row.
    /// </summary>
    Task<IReadOnlyList<string>> ReadMediaLinesAsync(Guid scopeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// The canonical content lines of one scope: milestones, evidence bundles and items, and
    /// content assets, each as <c>{table}:{id}:{token}</c> where the token is the row's own hash
    /// or revision. Joins run through the project so a row in another scope is never listed.
    /// </summary>
    Task<IReadOnlyList<string>> ReadContentLinesAsync(Guid scopeId, CancellationToken cancellationToken = default);

    /// <summary>Counts one project's evidence bundles, for the purge receipt's retained column.</summary>
    Task<long> CountProjectBundlesAsync(Guid scopeId, Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>Counts one project's content assets, for the purge receipt's retained column.</summary>
    Task<long> CountProjectAssetsAsync(Guid scopeId, Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>
    /// True while at least one live (never invalidated) approval binds one of the project's
    /// assets. Evidence under an active approval is never purge-eligible.
    /// </summary>
    Task<bool> HasLiveApprovalAsync(Guid scopeId, Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>Reads one scope's servable media ids for a project, oldest first, bounded.</summary>
    Task<IReadOnlyList<Guid>> ListProjectMediaAsync(
        Guid scopeId,
        Guid projectId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>True while the project's journey row is still a live public projection.</summary>
    Task<bool> HasLiveJourneyAsync(Guid scopeId, Guid projectId, CancellationToken cancellationToken = default);
}

/// <summary>
/// The operator-run backup use cases over the delivered stores (HOSTED-ARCHIVE-OPERATIONS-001).
/// </summary>
public interface IArchiveBackupService
{
    /// <summary>
    /// Measures the live scope's own hashes, for the operator's backup inventory. Measuring
    /// proves nothing: the values only become a verified backup when they are recorded and then
    /// rehearsed against rows that have not moved since.
    /// </summary>
    Task<ArchiveMeasurements> MeasureAsync(
        OwnerScope owner,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records the operator's backup measurements as one manifest. Recording proves nothing:
    /// the manifest reads unverified until a rehearsal stamps it.
    /// </summary>
    Task<ArchiveManifestView> RecordAsync(
        OwnerScope owner,
        ArchiveManifestRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Reads one scope's manifest, or <c>null</c> when it is absent or out of scope.</summary>
    Task<ArchiveManifestView?> ReadAsync(
        OwnerScope owner,
        Guid manifestId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Rehearses one manifest against the live scope: the schema version, the key version, the
    /// media inventory and the content digest must all match, or nothing is promoted and the
    /// manifest keeps its old stamp. A rehearsal that passes stamps this manifest and only this
    /// manifest: verification supersedes rather than merges.
    /// </summary>
    Task<ArchiveManifestView> RehearseAsync(
        OwnerScope owner,
        Guid manifestId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The owner-run retention use cases over the delivered stores (HOSTED-ARCHIVE-OPERATIONS-002).
/// </summary>
public interface IArchiveRetentionService
{
    /// <summary>Reads one project's archive state, or <c>null</c> when it was never deleted.</summary>
    Task<ArchiveTombstoneView?> ReadAsync(
        OwnerScope owner,
        Guid projectId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks one project deleted: the name must confirm, the revision must be current, and the
    /// public projection and the recurring poll stop in the same unit of work as the tombstone.
    /// Scope credentials are untouched - they belong to the scope, not the project - and no
    /// evidence, milestone, asset or media row is deleted.
    /// </summary>
    Task<ArchiveTombstoneView> DeleteAsync(
        OwnerScope owner,
        Guid projectId,
        string confirmProjectName,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Restores inside the window at exactly the tombstone's revision. A restore past the window
    /// is expired rather than merged, and restoring what is not live is not found.
    /// </summary>
    Task<ArchiveTombstoneView> RestoreAsync(
        OwnerScope owner,
        Guid projectId,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    /// <summary>Sets an operator hold, so the purge skips this tombstone until the hold clears.</summary>
    Task<ArchiveTombstoneView> HoldAsync(
        OwnerScope owner,
        Guid tombstoneId,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>Clears an operator hold.</summary>
    Task<ArchiveTombstoneView> ClearHoldAsync(
        OwnerScope owner,
        Guid tombstoneId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The operator's purge pass: every erase-requested tombstone whose window has lapsed and
    /// which holds no live approval, no live projection and no hold loses its eligible
    /// unreferenced media and is stamped. Referenced media, evidence, milestones, assets and
    /// approvals are retained and itemized in the receipt, never deleted.
    /// </summary>
    Task<IReadOnlyList<ArchivePurgeReceipt>> PurgeExpiredAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
