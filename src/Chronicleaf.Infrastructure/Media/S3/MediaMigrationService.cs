using System.Security.Cryptography;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Media;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Core;

// Inside the Chronicleaf.Infrastructure.Media namespace the bare identifier `Media` would resolve to
// the namespace, so the Core record is addressed through an alias throughout.
using MediaRecord = Chronicleaf.Core.Media;
using Microsoft.Extensions.Logging;
using Platform.Storage.Contracts;
using Platform.Storage.Keys;

namespace Chronicleaf.Infrastructure.Media.S3;

/// <summary>
/// Copies one scope's verified media between backends without ever unpublishing the source
/// (CLOUD-OBJECT-STORAGE-002).
/// </summary>
/// <remarks>
/// <para>
/// One record at a time, in oldest-first order, and each record in the same five steps: read the
/// row, skip it when the ledger already verified its target, read the source bytes and refuse them
/// when they do not hash to the row's digest, write them under the target key, and read the target
/// back and require its digest too. Only then is the row flipped to the target provider - by
/// compare-and-set, so two runs cannot both claim the same record - and the ledger entry is marked
/// verified. A failure at any earlier step records the ledger entry and leaves the source row
/// exactly as it was: the source reference remains valid and a later run resumes safely.
/// </para>
/// <para>
/// The two backends arrive as factories rather than as instances because the bytes' owners differ:
/// the source backend is the installation's own Local adapter, which needs no credential, while the
/// target backend is leased per run from the factory. The source is never deleted, by this run or
/// by any other code it calls: retention stays an explicit operator decision after a verified
/// migration.
/// </para>
/// </remarks>
public sealed class MediaMigrationService(
    IObjectStorage source,
    IS3BackendFactory targets,
    IMediaRepository media,
    IMediaMigrationRepository ledger,
    TimeProvider clock,
    ILogger<MediaMigrationService> logger) : IMediaMigrationService
{
    private const int ReadBufferSize = 81920;

    /// <summary>A copy the source could not serve. Kept distinct from a verification failure.</summary>
    private const string SourceUnreadableReason = "source_unavailable";

    /// <summary>A copy whose bytes did not hash to the row's digest, on either side.</summary>
    private const string ChecksumMismatchReason = "checksum_mismatch";

    /// <summary>A copy the target could not accept.</summary>
    private const string TargetUnavailableReason = "target_unavailable";

    /// <summary>A copy the row no longer describes: moved, retired or rewritten mid-run.</summary>
    private const string SourceMovedReason = "source_moved";

    public async Task<MediaMigrationResult> MigrateAsync(
        OwnerScope owner,
        string sourceProvider,
        string targetProvider,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetProvider);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        if (!StorageModes.IsSupported(sourceProvider) || !StorageModes.IsSupported(targetProvider))
        {
            throw new ValidationFailedException(
                "provider",
                "The migration source and target must each name a supported storage backend.");
        }

        if (string.Equals(sourceProvider, targetProvider, StringComparison.Ordinal))
        {
            throw new ValidationFailedException("provider", "The migration source and target differ.");
        }

        // The target backend is opened once for the run, not once per record: one leased key pair
        // serves every copy, and it is returned when the run ends however the run ends.
        await using var target = await targets.CreateAsync(cancellationToken).ConfigureAwait(false);

        var candidates = await media.ListReadyByProviderAsync(
            owner.ScopeId,
            sourceProvider,
            limit,
            cancellationToken).ConfigureAwait(false);

        var copied = 0;
        var alreadyVerified = 0;
        var failures = new List<MediaCopyFailure>();

        foreach (var record in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var outcome = await MigrateOneAsync(
                owner,
                record,
                sourceProvider,
                targetProvider,
                target,
                cancellationToken).ConfigureAwait(false);

            switch (outcome)
            {
                case CopyOutcome.Copied:
                    copied++;
                    break;

                case CopyOutcome.AlreadyVerified:
                    alreadyVerified++;
                    break;

                case CopyOutcome.Failed failed:
                    failures.Add(new MediaCopyFailure(record.Id, failed.Reason));
                    break;
            }
        }

        return new MediaMigrationResult(copied, alreadyVerified, failures);
    }

    private async Task<CopyOutcome> MigrateOneAsync(
        OwnerScope owner,
        MediaRecord record,
        string sourceProvider,
        string targetProvider,
        IObjectStorage target,
        CancellationToken cancellationToken)
    {
        StorageObjectKey sourceKey;
        try
        {
            sourceKey = new StorageObjectKey(record.StorageKey);
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
        {
            // A stored key is data. One that is not a valid key addresses nothing, and the ledger
            // records that rather than copying it.
            await RecordAsync(record, sourceProvider, targetProvider, record.StorageKey, record.StorageKey, null, MediaCopyStates.Failed, cancellationToken)
                .ConfigureAwait(false);
            logger.LogWarning("A media record names an invalid storage key; it was left on its source provider.");

            return new CopyOutcome.Failed(SourceMovedReason);
        }

        if (!IsScopedKey(record.ScopeId, sourceKey))
        {
            await RecordAsync(record, sourceProvider, targetProvider, record.StorageKey, record.StorageKey, null, MediaCopyStates.Failed, cancellationToken)
                .ConfigureAwait(false);

            return new CopyOutcome.Failed(SourceMovedReason);
        }

        // The target key is the same scoped string on the other backend: the key shape is portable
        // by design, and recording both sides anyway is what keeps a future backend honest.
        var targetKey = new StorageObjectKey(record.StorageKey);

        // A verified ledger entry short-circuits the copy, but never the verification: the target
        // is read back and required to hash to the row's digest before the row flips, so the ledger
        // can resume work but can never declare bytes good that are not.
        var entry = await ledger.FindAsync(owner.ScopeId, record.Id, cancellationToken).ConfigureAwait(false);
        if (entry is not null
            && string.Equals(entry.State, MediaCopyStates.Verified, StringComparison.Ordinal)
            && string.Equals(entry.CopiedSha256, record.Sha256, StringComparison.Ordinal))
        {
            if (await TargetVerifiesAsync(target, targetKey, record.Sha256, cancellationToken).ConfigureAwait(false)
                && await media.UpdateProviderAsync(owner.ScopeId, record.Id, sourceProvider, targetProvider, cancellationToken)
                    .ConfigureAwait(false) == 1)
            {
                return CopyOutcome.AlreadyVerified.Instance;
            }

            // The target stopped verifying, or the row moved on without this run: fall through and
            // copy again rather than trusting the ledger.
        }

        await RecordAsync(record, sourceProvider, targetProvider, sourceKey.Value, targetKey.Value, null, MediaCopyStates.Started, cancellationToken)
            .ConfigureAwait(false);

        // The source bytes, hashed on receipt. A source that cannot be read is a failure of this
        // record, not of the run: the row stays authoritative and the run moves on.
        byte[] content;
        try
        {
            content = await ReadAllAsync(source, sourceKey, cancellationToken).ConfigureAwait(false);
        }
        catch (MediaStorageUnavailableException)
        {
            await RecordAsync(record, sourceProvider, targetProvider, sourceKey.Value, targetKey.Value, null, MediaCopyStates.Failed, cancellationToken)
                .ConfigureAwait(false);

            return new CopyOutcome.Failed(SourceUnreadableReason);
        }

        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(content)), record.Sha256, StringComparison.Ordinal))
        {
            await RecordAsync(record, sourceProvider, targetProvider, sourceKey.Value, targetKey.Value, null, MediaCopyStates.Failed, cancellationToken)
                .ConfigureAwait(false);
            logger.LogWarning("A media object's bytes did not hash to its record's digest; it was left on its source provider.");

            return new CopyOutcome.Failed(ChecksumMismatchReason);
        }

        var upload = await target.UploadAsync(
            new StorageUploadRequest(targetKey, new MemoryStream(content, writable: false), record.MediaType, content.LongLength),
            cancellationToken).ConfigureAwait(false);

        if (upload.Status != StorageOutcomeStatus.Succeeded)
        {
            await RecordAsync(record, sourceProvider, targetProvider, sourceKey.Value, targetKey.Value, null, MediaCopyStates.Failed, cancellationToken)
                .ConfigureAwait(false);

            return new CopyOutcome.Failed(TargetUnavailableReason);
        }

        // The target proves itself the same way an upload does: the provider's own metadata is read
        // back and the bytes behind it are hashed, and only a digest that matches the row flips it.
        // A mismatch keeps the old provider reference, deletes nothing, and records the failure.
        if (!await TargetVerifiesAsync(target, targetKey, record.Sha256, cancellationToken).ConfigureAwait(false))
        {
            await ReleaseQuietlyAsync(target, targetKey, cancellationToken).ConfigureAwait(false);
            await RecordAsync(record, sourceProvider, targetProvider, sourceKey.Value, targetKey.Value, null, MediaCopyStates.Failed, cancellationToken)
                .ConfigureAwait(false);
            logger.LogWarning("A migrated copy did not verify against its record's digest; the source reference was kept.");

            return new CopyOutcome.Failed(ChecksumMismatchReason);
        }

        await RecordAsync(record, sourceProvider, targetProvider, sourceKey.Value, targetKey.Value, record.Sha256, MediaCopyStates.Verified, cancellationToken)
            .ConfigureAwait(false);

        var flipped = await media.UpdateProviderAsync(
            owner.ScopeId,
            record.Id,
            sourceProvider,
            targetProvider,
            cancellationToken).ConfigureAwait(false);

        if (flipped == 0)
        {
            // The row moved on without this run - retired, or claimed by a concurrent run. The bytes
            // on the target are verified and recorded but unreferenced, which is exactly the state an
            // interrupted upload leaves behind and which the purge reconciles.
            logger.LogWarning("A verified copy's record moved before the provider flip; the copy was left unreferenced.");

            return new CopyOutcome.Failed(SourceMovedReason);
        }

        return CopyOutcome.Copied.Instance;
    }

    private async Task<bool> TargetVerifiesAsync(
        IObjectStorage target,
        StorageObjectKey targetKey,
        string expectedDigest,
        CancellationToken cancellationToken)
    {
        var metadata = await target.GetMetadataAsync(targetKey, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            return false;
        }

        byte[] content;
        try
        {
            content = await ReadAllAsync(target, targetKey, cancellationToken).ConfigureAwait(false);
        }
        catch (MediaStorageUnavailableException)
        {
            return false;
        }

        return metadata.LengthBytes == content.LongLength
            && string.Equals(Convert.ToHexStringLower(SHA256.HashData(content)), expectedDigest, StringComparison.Ordinal);
    }

    private static async Task<byte[]> ReadAllAsync(
        IObjectStorage backend,
        StorageObjectKey key,
        CancellationToken cancellationToken)
    {
        var download = await backend.DownloadAsync(key, cancellationToken).ConfigureAwait(false);
        if (download.Status != StorageOutcomeStatus.Succeeded || download.Value is null)
        {
            throw new MediaStorageUnavailableException(ProviderReason(download.Failure));
        }

        await using var content = download.Value;

        var buffer = new MemoryStream();
        await using (buffer)
        {
            await content.Content.CopyToAsync(buffer, ReadBufferSize, cancellationToken).ConfigureAwait(false);

            return buffer.ToArray();
        }
    }

    private static async Task ReleaseQuietlyAsync(
        IObjectStorage backend,
        StorageObjectKey key,
        CancellationToken cancellationToken)
    {
        try
        {
            await backend.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Best-effort: see below.
        }
        catch (MediaStorageUnavailableException)
        {
            // The copy failed verification and its removal is best-effort: the bytes are
            // unreferenced by construction - no row names the target provider yet - and a provider
            // that cannot delete right now leaves an object the purge reconciles.
        }
    }

    private async Task RecordAsync(
        MediaRecord record,
        string sourceProvider,
        string targetProvider,
        string sourceKey,
        string targetKey,
        string? copiedDigest,
        string state,
        CancellationToken cancellationToken) =>
        await ledger.UpsertAsync(
            new MediaMigrationCopy(
                record.Id,
                record.ScopeId,
                sourceProvider,
                targetProvider,
                sourceKey,
                targetKey,
                record.Sha256,
                copiedDigest,
                state,
                clock.GetUtcNow().ToUniversalTime()),
            cancellationToken).ConfigureAwait(false);

    private static bool IsScopedKey(Guid scopeId, StorageObjectKey key) =>
        key.Value.StartsWith($"scopes/{scopeId:D}/", StringComparison.Ordinal);

    private static string ProviderReason(StorageFailure? failure) =>
        failure is null ? string.Empty : failure.Message;

    private abstract record CopyOutcome
    {
        public sealed record Copied : CopyOutcome
        {
            public static readonly Copied Instance = new();

            private Copied()
            {
            }
        }

        public sealed record AlreadyVerified : CopyOutcome
        {
            public static readonly AlreadyVerified Instance = new();

            private AlreadyVerified()
            {
            }
        }

        public sealed record Failed(string Reason) : CopyOutcome;
    }
}
