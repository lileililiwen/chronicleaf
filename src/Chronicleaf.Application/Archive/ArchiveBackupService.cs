using Chronicleaf.Application.Common;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Application.Archive;

/// <summary>
/// The operator-run backup use cases over the delivered stores (HOSTED-ARCHIVE-OPERATIONS-001).
/// </summary>
/// <remarks>
/// <para>
/// <b>Recording and verifying are two different acts by design.</b> The operator measures the
/// backup off the backup - the schema and key versions it was taken under, the media inventory
/// hash and the content digest - and records them here. The rehearsal recomputes the two hashes
/// from the live scope through <see cref="IArchiveDigestReader"/> and compares all four values
/// byte for byte. A manifest that was recorded an hour ago against rows that moved since fails
/// loudly and keeps its old stamp: verification supersedes rather than merges.
/// </para>
/// <para>
/// <b>There is deliberately no route that serves these rows.</b> The backup reference names the
/// operator's snapshot inventory, and key versions name the operator's key history; neither
/// belongs in an owner-facing response, so this service is resolved by operator tooling and by
/// tests, never by an endpoint module.
/// </para>
/// </remarks>
public sealed class ArchiveBackupService(
    IArchiveBackupManifestStore manifests,
    IArchiveDigestReader digests,
    IAuditWriter audit,
    IUnitOfWork unitOfWork,
    ArchiveBackupOptions options,
    TimeProvider clock,
    ILogger<ArchiveBackupService> logger) : IArchiveBackupService
{
    /// <summary>The audit action recorded when an operator records a backup manifest.</summary>
    public const string ManifestRecordedAction = "archive.backup.recorded";

    /// <summary>The audit action recorded when a rehearsal proves a manifest against the live scope.</summary>
    public const string ManifestVerifiedAction = "archive.backup.verified";

    private readonly ArchiveBackupOptions _options = options;

    /// <inheritdoc />
    public async Task<ArchiveMeasurements> MeasureAsync(
        OwnerScope owner,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var mediaLines = await digests.ReadMediaLinesAsync(owner.ScopeId, cancellationToken).ConfigureAwait(false);
        var contentLines = await digests.ReadContentLinesAsync(owner.ScopeId, cancellationToken).ConfigureAwait(false);

        return new ArchiveMeasurements(
            ArchiveBackupManifest.DigestLines(mediaLines),
            ArchiveBackupManifest.DigestLines(contentLines));
    }

    /// <inheritdoc />
    public async Task<ArchiveManifestView> RecordAsync(
        OwnerScope owner,
        ArchiveManifestRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);

        if (request.SchemaVersion < 1)
        {
            throw new ValidationFailedException("schemaVersion", "A backup schema version is one or greater.");
        }

        if (request.EncryptionKeyVersion < 1)
        {
            throw new ValidationFailedException(
                "encryptionKeyVersion", "A backup key version is one or greater.");
        }

        if (!ArchiveBackupManifest.IsBackupRef(request.DbBackupRef))
        {
            throw new ValidationFailedException(
                "dbBackupRef",
                "A backup reference is an operator inventory token of letters, digits, dot, dash and underscore.");
        }

        if (!ArchiveBackupManifest.IsHashHex(request.MediaInventoryHash))
        {
            throw new ValidationFailedException(
                "mediaInventoryHash", "A media inventory hash is lowercase SHA-256 hex.");
        }

        if (!ArchiveBackupManifest.IsHashHex(request.ContentDigest))
        {
            throw new ValidationFailedException(
                "contentDigest", "A content digest is lowercase SHA-256 hex.");
        }

        var now = clock.GetUtcNow();

        var manifest = new ArchiveBackupManifest(
            Guid.NewGuid(),
            owner.ScopeId,
            request.SchemaVersion,
            request.DbBackupRef,
            request.MediaInventoryHash,
            request.ContentDigest,
            request.EncryptionKeyVersion,
            now);

        await unitOfWork.ExecuteAsync(
            async token =>
            {
                await manifests.InsertAsync(manifest, token).ConfigureAwait(false);

                audit.Record(new DomainAudit(
                    owner.ActorId,
                    ManifestRecordedAction,
                    manifest.Id,
                    manifest.Revision,
                    "archive backup recorded"));
                return true;
            },
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Archive backup {ManifestId} recorded for scope {ScopeId}.",
            manifest.Id,
            owner.ScopeId);

        return ArchiveManifestView.From(manifest);
    }

    /// <inheritdoc />
    public async Task<ArchiveManifestView?> ReadAsync(
        OwnerScope owner,
        Guid manifestId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var stored = await manifests.FindAsync(owner.ScopeId, manifestId, cancellationToken).ConfigureAwait(false);

        return stored is null ? null : ArchiveManifestView.From(stored);
    }

    /// <inheritdoc />
    public async Task<ArchiveManifestView> RehearseAsync(
        OwnerScope owner,
        Guid manifestId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var stored = await manifests.FindAsync(owner.ScopeId, manifestId, cancellationToken).ConfigureAwait(false)
            ?? throw new ResourceNotFoundException("archive backup");

        // The schema the backup was taken under must be the schema this deployment verifies:
        // restoring rows this code cannot read is not a recovery.
        if (stored.SchemaVersion != digests.LiveSchemaVersion)
        {
            throw new ArchiveSchemaMismatchException(
                digests.LiveSchemaVersion,
                stored.SchemaVersion);
        }

        // The key the backup was sealed under must be the key this deployment verifies: a
        // restore that cannot open its own secrets is not a restore.
        if (stored.EncryptionKeyVersion != _options.CurrentEncryptionKeyVersion)
        {
            throw new ArchiveKeyMismatchException(
                _options.CurrentEncryptionKeyVersion,
                stored.EncryptionKeyVersion);
        }

        // Both hashes are recomputed from the live scope and compared byte for byte. Any drift -
        // a missing object, an added row, a changed record - fails loudly and the manifest keeps
        // its old stamp: recovery is byte-identical or it is not promoted.
        var mediaLines = await digests.ReadMediaLinesAsync(owner.ScopeId, cancellationToken).ConfigureAwait(false);
        var liveMedia = ArchiveBackupManifest.DigestLines(mediaLines);
        if (!CryptographicEquals(liveMedia, stored.MediaInventoryHash))
        {
            throw new ArchiveMediaMismatchException("media");
        }

        var contentLines = await digests.ReadContentLinesAsync(owner.ScopeId, cancellationToken).ConfigureAwait(false);
        var liveContent = ArchiveBackupManifest.DigestLines(contentLines);
        if (!CryptographicEquals(liveContent, stored.ContentDigest))
        {
            throw new ArchiveMediaMismatchException("content");
        }

        var now = clock.GetUtcNow();
        var expected = stored.Revision;

        var verified = await unitOfWork.ExecuteAsync(
            async token =>
            {
                var current = await manifests.FindAsync(owner.ScopeId, manifestId, token).ConfigureAwait(false)
                    ?? throw new ResourceNotFoundException("archive backup");

                if (current.RestoreVerifiedAt != stored.RestoreVerifiedAt)
                {
                    throw new RevisionConflictException(current.Revision);
                }

                current.MarkVerified(now);

                if (!await manifests.TryUpdateAsync(current, expected, token).ConfigureAwait(false))
                {
                    throw new RevisionConflictException(expected + 1);
                }

                audit.Record(new DomainAudit(
                    owner.ActorId,
                    ManifestVerifiedAction,
                    current.Id,
                    current.Revision,
                    "archive backup verified"));

                return current;
            },
            cancellationToken).ConfigureAwait(false);

        logger.LogInformation(
            "Archive backup {ManifestId} verified for scope {ScopeId}.",
            verified.Id,
            owner.ScopeId);

        return ArchiveManifestView.From(verified);
    }

    private static bool CryptographicEquals(string left, string right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        var difference = 0;
        for (var index = 0; index < left.Length; index++)
        {
            difference |= left[index] ^ right[index];
        }

        return difference == 0;
    }
}
