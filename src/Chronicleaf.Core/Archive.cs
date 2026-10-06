using System.Security.Cryptography;
using System.Text;

namespace Chronicleaf.Core;

/// <summary>
/// One project's recoverable archive-deletion marker (HOSTED-ARCHIVE-OPERATIONS-002).
/// </summary>
/// <remarks>
/// <para>
/// <b>A tombstone suspends, never erases.</b> Creating one stops the public projection and the
/// recurring poll for the project, and starts a thirty-day recovery window during which the owner
/// may restore. Nothing is deleted by this row: milestones, evidence bundles, content assets,
/// approvals and media stay exactly where they are, so a restore is a state change rather than a
/// reconstruction. Only the operator purge after the window removes anything, and only the
/// eligible unreferenced private data it names.
/// </para>
/// <para>
/// <b>Erase is explicit and holds are honored.</b> The delete route always records an erase
/// request, because a confirmed archive-data deletion is the owner's statement that the data may
/// go once the window lapses. An operator hold (<see cref="HoldReason"/>) suspends the purge
/// without touching the window: a held tombstone whose window has lapsed is skipped, not purged,
/// until the hold is cleared.
/// </para>
/// <para>
/// <b>One live tombstone per project.</b> A second delete while one is live is a conflict rather
/// than a merge, and a restore stamps <see cref="RestoredAt"/> instead of removing the row, so
/// the history of what was deleted and restored survives both. The database enforces the
/// single-live-row rule with a partial unique index; this record enforces the state transitions.
/// </para>
/// </remarks>
public sealed class ArchiveTombstone
{
    /// <summary>How long a deleted project stays recoverable before it becomes purge-eligible.</summary>
    public static readonly TimeSpan RecoveryWindow = TimeSpan.FromDays(30);

    /// <summary>How long an operator backup copy is kept before it expires, documented not enforced here.</summary>
    public static readonly TimeSpan BackupCopyExpiry = TimeSpan.FromDays(90);

    public ArchiveTombstone(
        Guid id,
        Guid scopeId,
        Guid projectId,
        string projectName,
        DateTimeOffset deletedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A tombstone needs an id.", nameof(id));
        }

        if (scopeId == Guid.Empty)
        {
            throw new ArgumentException("A tombstone needs a scope.", nameof(scopeId));
        }

        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("A tombstone needs a project.", nameof(projectId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);

        Id = id;
        ScopeId = scopeId;
        ProjectId = projectId;
        ProjectName = projectName;
        DeletedAt = deletedAt;
        RecoveryUntil = deletedAt.Add(RecoveryWindow);
        EraseRequested = true;
        Revision = 1;
        CreatedAt = deletedAt;
        UpdatedAt = deletedAt;
    }

    public Guid Id { get; }

    public Guid ScopeId { get; }

    public Guid ProjectId { get; }

    /// <summary>The project name the owner confirmed, copied so the tombstone stays readable.</summary>
    public string ProjectName { get; }

    public DateTimeOffset DeletedAt { get; }

    public DateTimeOffset RecoveryUntil { get; private set; }

    /// <summary>Whether the owner asked for erasure after the window. A delete always sets this.</summary>
    public bool EraseRequested { get; private set; }

    /// <summary>Why an operator is holding this tombstone out of the purge, or null when unheld.</summary>
    public string? HoldReason { get; private set; }

    /// <summary>When the owner restored inside the window, or null while deleted.</summary>
    public DateTimeOffset? RestoredAt { get; private set; }

    /// <summary>When the operator purge removed the eligible data, or null while retained.</summary>
    public DateTimeOffset? PurgedAt { get; private set; }

    /// <summary>The optimistic revision every tombstone change presents.</summary>
    public long Revision { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>True while the deletion is live: never restored, never purged.</summary>
    public bool IsLive => RestoredAt is null && PurgedAt is null;

    /// <summary>True when the recovery window has lapsed at <paramref name="now"/>.</summary>
    public bool IsWindowElapsed(DateTimeOffset now) => now >= RecoveryUntil;

    /// <summary>
    /// Rebuilds the stored record: the same checks as a new tombstone, with the stored state
    /// rather than fresh state. Only the store calls this; owners change a tombstone through
    /// <see cref="Restore"/>, <see cref="Hold"/>, <see cref="ClearHold"/> and
    /// <see cref="MarkPurged"/>.
    /// </summary>
    public static ArchiveTombstone Rehydrate(
        Guid id,
        Guid scopeId,
        Guid projectId,
        string projectName,
        DateTimeOffset deletedAt,
        DateTimeOffset recoveryUntil,
        bool eraseRequested,
        string? holdReason,
        DateTimeOffset? restoredAt,
        DateTimeOffset? purgedAt,
        long revision,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        var tombstone = new ArchiveTombstone(id, scopeId, projectId, projectName, deletedAt);

        if (recoveryUntil <= deletedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(recoveryUntil), recoveryUntil, "A recovery window ends after the deletion.");
        }

        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revision), revision, "A stored tombstone revision is one or greater.");
        }

        tombstone.RecoveryUntil = recoveryUntil;
        tombstone.EraseRequested = eraseRequested;
        tombstone.HoldReason = holdReason;
        tombstone.RestoredAt = restoredAt;
        tombstone.PurgedAt = purgedAt;
        tombstone.Revision = revision;
        tombstone.CreatedAt = createdAt;
        tombstone.UpdatedAt = updatedAt;
        return tombstone;
    }

    /// <summary>
    /// Restores inside the window: the deletion lifts and the erase request is withdrawn, because
    /// an owner who restores is no longer asking for erasure. A restore after the window, of a
    /// purged tombstone, or of a held one is refused by the caller, not merged here.
    /// </summary>
    public void Restore(DateTimeOffset now)
    {
        RestoredAt = now;
        EraseRequested = false;
        Revision += 1;
        UpdatedAt = now;
    }

    /// <summary>Holds this tombstone out of the operator purge with the operator's reason.</summary>
    public void Hold(string reason, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        HoldReason = reason;
        Revision += 1;
        UpdatedAt = now;
    }

    /// <summary>Clears an operator hold. Clearing what was never held still moves the revision.</summary>
    public void ClearHold(DateTimeOffset now)
    {
        HoldReason = null;
        Revision += 1;
        UpdatedAt = now;
    }

    /// <summary>Records that the operator purge removed the eligible data. Nothing else may follow.</summary>
    public void MarkPurged(DateTimeOffset now)
    {
        PurgedAt = now;
        Revision += 1;
        UpdatedAt = now;
    }
}

/// <summary>
/// One operator-recorded backup manifest over a tenant-scoped store
/// (HOSTED-ARCHIVE-OPERATIONS-001).
/// </summary>
/// <remarks>
/// <para>
/// <b>A manifest is a claim the rehearsal checks, never proof by itself.</b> The operator takes a
/// database snapshot and an object inventory, measures the two hashes off the backup, and records
/// them here with the opaque snapshot reference and the versions the backup was taken under. The
/// rehearsal recomputes both hashes from the live scope and compares byte for byte: any drift in
/// schema, key version or content fails loudly and <see cref="RestoreVerifiedAt"/> stays null.
/// Configuring a schedule alone records nothing and therefore verifies nothing.
/// </para>
/// <para>
/// <b>The reference is opaque, not a location.</b> <see cref="DbBackupRef"/> names the snapshot
/// in the operator's own inventory ("pg-snapshot-2026-10-06-001"), never a path, a URL or a
/// bucket: no route serves these rows at all, so a backup location cannot leak through the API.
/// </para>
/// <para>
/// <b>Verification supersedes rather than merges.</b> Each rehearsal recomputes from the live
/// scope and either stamps this manifest or refuses it; a manifest verified once and drifted
/// since fails the next rehearsal rather than keeping its old stamp.
/// </para>
/// </remarks>
public sealed class ArchiveBackupManifest
{
    /// <summary>Lowercase SHA-256 hex length, the only hash spelling a manifest accepts.</summary>
    public const int HashHexLength = 64;

    /// <summary>The longest backup reference an operator may record.</summary>
    public const int BackupRefMaxLength = 200;

    public ArchiveBackupManifest(
        Guid id,
        Guid scopeId,
        int schemaVersion,
        string dbBackupRef,
        string mediaInventoryHash,
        string contentDigest,
        int encryptionKeyVersion,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A manifest needs an id.", nameof(id));
        }

        if (scopeId == Guid.Empty)
        {
            throw new ArgumentException("A manifest needs a scope.", nameof(scopeId));
        }

        if (schemaVersion < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion), schemaVersion, "A backup schema version is one or greater.");
        }

        if (encryptionKeyVersion < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(encryptionKeyVersion), encryptionKeyVersion, "A backup key version is one or greater.");
        }

        if (!IsBackupRef(dbBackupRef))
        {
            throw new ArgumentException(
                "A backup reference is an operator inventory token, not a path or a URL.",
                nameof(dbBackupRef));
        }

        if (!IsHashHex(mediaInventoryHash))
        {
            throw new ArgumentException(
                "A media inventory hash is lowercase SHA-256 hex.", nameof(mediaInventoryHash));
        }

        if (!IsHashHex(contentDigest))
        {
            throw new ArgumentException(
                "A content digest is lowercase SHA-256 hex.", nameof(contentDigest));
        }

        Id = id;
        ScopeId = scopeId;
        SchemaVersion = schemaVersion;
        DbBackupRef = dbBackupRef;
        MediaInventoryHash = mediaInventoryHash;
        ContentDigest = contentDigest;
        EncryptionKeyVersion = encryptionKeyVersion;
        Revision = 1;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid Id { get; }

    public Guid ScopeId { get; }

    /// <summary>The schema version the backup was taken under, checked against the live one.</summary>
    public int SchemaVersion { get; }

    /// <summary>Opaque operator inventory token. Never a path, never served by a route.</summary>
    public string DbBackupRef { get; }

    /// <summary>Lowercase SHA-256 hex over the backup's media inventory, recomputed at rehearsal.</summary>
    public string MediaInventoryHash { get; }

    /// <summary>Lowercase SHA-256 hex over the backup's scoped content, recomputed at rehearsal.</summary>
    public string ContentDigest { get; }

    /// <summary>The encryption key version the backup was sealed under, checked at rehearsal.</summary>
    public int EncryptionKeyVersion { get; }

    /// <summary>When a rehearsal last proved this manifest against the live scope, if ever.</summary>
    public DateTimeOffset? RestoreVerifiedAt { get; private set; }

    /// <summary>The optimistic revision every manifest change presents.</summary>
    public long Revision { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// Rebuilds the stored record: the same checks as a new manifest, with the stored verification
    /// state rather than a fresh one. Only the store calls this.
    /// </summary>
    public static ArchiveBackupManifest Rehydrate(
        Guid id,
        Guid scopeId,
        int schemaVersion,
        string dbBackupRef,
        string mediaInventoryHash,
        string contentDigest,
        int encryptionKeyVersion,
        DateTimeOffset? restoreVerifiedAt,
        long revision,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        var manifest = new ArchiveBackupManifest(
            id, scopeId, schemaVersion, dbBackupRef, mediaInventoryHash, contentDigest,
            encryptionKeyVersion, createdAt);

        if (revision < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revision), revision, "A stored manifest revision is one or greater.");
        }

        manifest.RestoreVerifiedAt = restoreVerifiedAt;
        manifest.Revision = revision;
        manifest.UpdatedAt = updatedAt;
        return manifest;
    }

    /// <summary>
    /// Stamps a successful rehearsal. The stamp says the manifest described the live scope at
    /// <paramref name="now"/>; any later drift fails the next rehearsal rather than inheriting it.
    /// </summary>
    public void MarkVerified(DateTimeOffset now)
    {
        RestoreVerifiedAt = now;
        Revision += 1;
        UpdatedAt = now;
    }

    /// <summary>
    /// True only for the operator inventory token spelling: letters, digits, dot, dash and
    /// underscore. A slash, a backslash, a colon or whitespace is a path or a URL, not a token.
    /// </summary>
    public static bool IsBackupRef(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > BackupRefMaxLength)
        {
            return false;
        }

        foreach (var ch in value)
        {
            var ok = char.IsAsciiLetterOrDigit(ch) || ch is '.' or '-' or '_';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsHashHex(string? value)
    {
        if (value is null || value.Length != HashHexLength)
        {
            return false;
        }

        foreach (var ch in value)
        {
            var ok = char.IsAsciiDigit(ch) || (ch >= 'a' && ch <= 'f');
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>SHA-256 hex over the canonical lines, the digest spelling every hash here uses.</summary>
    public static string DigestLines(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var joined = string.Join("\n", lines.OrderBy(line => line, StringComparer.Ordinal));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined))).ToLowerInvariant();
    }
}
