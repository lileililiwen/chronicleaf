using System.Security.Cryptography;
using Chronicleaf.Application.Common;
using Chronicleaf.Application.Media;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;

// Inside the Chronicleaf.Infrastructure.Media namespace the bare identifier `Media` would resolve to
// the namespace, so the Core record is addressed through an alias throughout.
using MediaRecord = Chronicleaf.Core.Media;
using Microsoft.Extensions.Logging;
using Platform.Storage.Contracts;
using Platform.Storage.Keys;

namespace Chronicleaf.Infrastructure.Media.S3;

/// <summary>
/// The S3 media store: the design's <c>IMediaStore</c> over the adopted shared S3-compatible
/// storage (CLOUD-OBJECT-STORAGE-001).
/// </summary>
/// <remarks>
/// <para>
/// The sibling of <see cref="LocalMediaStore"/>, and deliberately the same sequence: stage under a
/// server-generated pending key, verify against the provider's own metadata, promote to the ready
/// key, verify again, release the pending object and only then register the row. The row lands with
/// <c>s3</c> as its provider, so serving can trust the value rather than re-proving it. A failure at
/// any step releases whatever this call wrote and registers nothing, for the same reason as the
/// Local store: no committed row may name bytes that were not verified.
/// </para>
/// <para><b>What is different, and why.</b> There is no <c>MediaRoot</c> here, because an object key
/// is not a path: traversal containment is the adopted platform's own <see cref="StorageObjectKey"/>
/// validation plus the scope-prefix check below, which every read, retire and release applies to a
/// stored key as well as to a generated one. A row written by hand, or by a backend whose prefix
/// differs, still cannot make this process touch another scope's objects. There is no presign on
/// the write path and no listing anywhere: the purge sweeps recorded pending rows rather than
/// enumerating a bucket, because listing a bucket is the one operation the design forbids.
/// </para>
/// <para><b>Where the backend comes from.</b> Each public method opens exactly one backend from the
/// factory and disposes it before returning, so the leased key pair lives for the length of the
/// call and no longer. A backend that cannot be built is 503 before a single byte is read.
/// </para>
/// </remarks>
public sealed class S3MediaStore(
    IS3BackendFactory backends,
    IMediaRepository media,
    IMediaReferenceChecker references,
    IMediaIdSource ids,
    IProjectStore projects,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    MediaSettings settings,
    TimeProvider clock,
    ILogger<S3MediaStore> logger) : IMediaStore
{
    private const int ReadBufferSize = 81920;

    public async Task<MediaRef> PutAsync(
        OwnerScope owner,
        PutMediaInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(input);

        // The project has to exist in this caller's own scope, before a backend is even opened: a
        // media record that named a project in another scope would be unreachable by its owner and
        // reachable by nobody, and opening a leased backend to learn that would spend a secret for
        // nothing.
        if (await projects.FindAsync(owner.ScopeId, input.ProjectId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new MediaNotFoundException("project");
        }

        // Read once, under the bound. Nothing is written before this returns, so an oversized body is
        // refused with no object created rather than half-uploaded into one.
        var bytes = await ReadBoundedAsync(input.Content, settings.MaximumByteLength, cancellationToken)
            .ConfigureAwait(false);

        // Decode, then bound the decoded image. The declared content type is never consulted, so a
        // request that lies about itself is refused by the same check that accepts an honest one.
        var image = MediaImage.Decode(bytes);
        if (image.PixelCount > settings.MaximumPixelCount)
        {
            throw new MediaImageTooLargeException(image.Width, image.Height, settings.MaximumPixelCount);
        }

        var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!MediaValues.IsSha256Hex(digest))
        {
            throw new InternalFailureException("The image digest could not be computed.");
        }

        var mediaId = ids.NextMediaId();
        var pendingKey = new StorageObjectKey(
            MediaValues.PendingStorageKey(owner.ScopeId, input.ProjectId, mediaId));
        var readyKey = new StorageObjectKey(
            MediaValues.ReadyStorageKey(owner.ScopeId, input.ProjectId, mediaId));

        // The guard is asked about both keys before the first write. The keys are built from UUIDs
        // here and cannot traverse, so this is the check that keeps that true if the shape above ever
        // changes - and it is the same check the read and retire paths use on a stored key.
        RequireScopedKey(owner.ScopeId, pendingKey);
        RequireScopedKey(owner.ScopeId, readyKey);

        await using var backend = await backends.CreateAsync(cancellationToken).ConfigureAwait(false);

        await RequireAsync(
            backend.UploadAsync(
                new StorageUploadRequest(pendingKey, new MemoryStream(bytes, writable: false), MediaTypes.Png, bytes.LongLength),
                cancellationToken),
            "the image could not be staged").ConfigureAwait(false);

        try
        {
            await RequireVerifiedAsync(backend, pendingKey, bytes.LongLength, cancellationToken).ConfigureAwait(false);

            await PromoteAsync(backend, pendingKey, readyKey, bytes.LongLength, cancellationToken).ConfigureAwait(false);
            await ReleaseAsync(owner.ScopeId, backend, pendingKey, cancellationToken).ConfigureAwait(false);

            var record = new MediaRecord(
                mediaId,
                owner.ScopeId,
                input.ProjectId,
                readyKey.Value,
                image.MediaType,
                bytes.LongLength,
                digest,
                MediaStatus.Ready,
                clock.GetUtcNow().ToUniversalTime(),
                input.OwnerOperationId,
                MediaValues.InitialRevision,
                StorageModes.S3);

            await RegisterAsync(owner, record, cancellationToken).ConfigureAwait(false);

            return ToReference(record);
        }
        catch (Exception exception) when (exception is not MediaOutsideRootException)
        {
            // Compensation. The ready object is released first because it is the only one that could
            // ever be served, and both are released without failing the request: this path is already
            // reporting a failure, and a provider that cannot delete right now leaves an object that
            // is unservable because no row names it.
            await TryReleaseAsync(owner.ScopeId, backend, readyKey, cancellationToken).ConfigureAwait(false);
            await TryReleaseAsync(owner.ScopeId, backend, pendingKey, cancellationToken).ConfigureAwait(false);

            logger.LogWarning(
                exception,
                "An S3 media upload failed after staging and was released; nothing was registered.");

            throw;
        }
    }

    /// <summary>
    /// Commits the record and its audit row, and reports a database failure as such.
    /// </summary>
    /// <remarks>
    /// The last step of the sequence and the only one that can leave bytes with no row, so it is
    /// also the only one whose failure has to be compensated by the caller's catch. Anything that
    /// came out of the database is 503 rather than a 500 that would suggest the image itself was at
    /// fault. Either way the caller releases the bytes, so no committed row and no orphan survive
    /// together.
    /// </remarks>
    private async Task RegisterAsync(OwnerScope owner, MediaRecord record, CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.ExecuteAsync(
                async token =>
                {
                    if (!await media.InsertAsync(record, token).ConfigureAwait(false))
                    {
                        // A fresh id colliding is another writer holding the same one, which is an
                        // internal failure rather than something to retry into a second record.
                        throw new InternalFailureException("The new media record could not be stored.");
                    }

                    audit.Record(new DomainAudit(
                        owner.ActorId,
                        LocalMediaStore.MediaCreatedAction,
                        record.Id,
                        record.Revision,
                        LocalMediaStore.RegisteredDetail));

                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not ApiException)
        {
            logger.LogError(exception, "The S3 media record could not be committed.");

            throw new DependencyUnavailableException("media registration");
        }
    }

    public async Task<MediaContent> OpenAsync(
        OwnerScope owner,
        Guid mediaId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        // Scope first, provider second, status third. All three produce the same not-found, so a
        // caller cannot tell a record in another installation, a record on the other backend, or a
        // record that is not servable apart.
        var record = await media.FindAsync(owner.ScopeId, mediaId, cancellationToken).ConfigureAwait(false);
        if (record is null
            || !string.Equals(record.ProviderId, StorageModes.S3, StringComparison.Ordinal)
            || !record.IsServable)
        {
            throw new MediaNotFoundException();
        }

        var key = ResolveScopedKey(owner.ScopeId, record.StorageKey);

        await using var backend = await backends.CreateAsync(cancellationToken).ConfigureAwait(false);

        var download = await backend.DownloadAsync(key, cancellationToken).ConfigureAwait(false);
        if (download.Status == StorageOutcomeStatus.NotFound || download.Value is null)
        {
            // A ready row whose object is gone is a fault, not a 404 the caller can act on, and it is
            // still a 404 outward: the record is not servable and that is all a caller may learn.
            logger.LogError("A ready S3 media record named an object the provider does not have.");
            throw new MediaNotFoundException();
        }

        if (download.Status != StorageOutcomeStatus.Succeeded)
        {
            throw new MediaStorageUnavailableException(ProviderReason(download.Failure));
        }

        return new MediaContent(ToReference(record), download.Value.Content);
    }

    public async Task<RetireMediaResult> RetireAsync(
        OwnerScope owner,
        Guid mediaId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var record = await media.FindAsync(owner.ScopeId, mediaId, cancellationToken).ConfigureAwait(false);
        if (record is null
            || !string.Equals(record.ProviderId, StorageModes.S3, StringComparison.Ordinal)
            || !record.IsServable)
        {
            throw new MediaNotFoundException();
        }

        // The key is resolved before the reference check, so a stored key that does not belong to
        // this scope is refused before this call has written anything at all.
        var key = ResolveScopedKey(owner.ScopeId, record.StorageKey);

        // The reference check runs before anything is written, so a refused retire has changed
        // nothing at all: the record is still ready and the object is still there. This is the same
        // checker the Local store asks, so evidence that references an image holds it on either
        // backend.
        if (await references.IsReferencedAsync(record.Id, cancellationToken).ConfigureAwait(false))
        {
            throw new MediaInUseException();
        }

        var updated = await unitOfWork.ExecuteAsync(
            async token =>
            {
                var changed = await media.TransitionStatusAsync(
                    owner.ScopeId,
                    record.Id,
                    MediaStatus.Ready,
                    MediaStatus.Deleted,
                    clock.GetUtcNow().ToUniversalTime(),
                    token).ConfigureAwait(false);

                if (changed == 0)
                {
                    // Another writer retired it first, or the record is gone. The caller is told
                    // there is nothing here rather than being told it won a race it did not win.
                    throw new MediaNotFoundException();
                }

                audit.Record(new DomainAudit(
                    owner.ActorId,
                    LocalMediaStore.MediaRetiredAction,
                    record.Id,
                    record.Revision + 1,
                    LocalMediaStore.RetiredDetail));

                // A new record rather than a copy: the domain type is immutable, so a transition
                // produces the next version of the row instead of editing the one that was read.
                return new MediaRecord(
                    record.Id,
                    record.ScopeId,
                    record.ProjectId,
                    record.StorageKey,
                    record.MediaType,
                    record.ByteLength,
                    record.Sha256,
                    MediaStatus.Deleted,
                    record.CreatedAt,
                    record.OwnerOperationId,
                    record.Revision + 1,
                    record.ProviderId);
            },
            cancellationToken).ConfigureAwait(false);

        // The record is no longer servable before this runs, so a failure to release the object
        // cannot leave something readable, and a missing object is success rather than a failure.
        await using var backend = await backends.CreateAsync(cancellationToken).ConfigureAwait(false);
        var released = await TryReleaseAsync(owner.ScopeId, backend, key, cancellationToken).ConfigureAwait(false);

        return new RetireMediaResult(ToReference(updated), released);
    }

    public async Task<int> PurgeUnreferencedAsync(
        DateTimeOffset stagedBefore,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        // The rows first, because they decide what counts as an orphan. Only this backend's staged
        // records are considered: the Local store sweeps its own, and neither may move the other's.
        // A staged record older than the cutoff is unreferenced by definition - nothing can reference
        // a record that was never published - so no reference check is needed to justify sweeping it.
        var stale = await media.ListStalePendingAsync(stagedBefore, limit, cancellationToken)
            .ConfigureAwait(false);

        var own = stale
            .Where(record => string.Equals(record.ProviderId, StorageModes.S3, StringComparison.Ordinal))
            .ToList();

        if (own.Count == 0)
        {
            return 0;
        }

        await using var backend = await backends.CreateAsync(cancellationToken).ConfigureAwait(false);

        // Then the objects. There is deliberately no enumeration of the bucket here: listing is the
        // one operation the design forbids, so an object no record names cannot be found by this
        // sweep. What it sweeps is what a failed upload leaves behind while still recorded - the
        // staged object a pending row names - and the row is moved to a terminal status once its
        // object is gone, so it stops being a candidate for every future pass.
        var released = 0;

        foreach (var record in own)
        {
            cancellationToken.ThrowIfCancellationRequested();

            StorageObjectKey key;
            try
            {
                key = new StorageObjectKey(record.StorageKey);
                RequireScopedKey(record.ScopeId, key);
            }
            catch (Exception exception) when (exception is MediaOutsideRootException
                or ArgumentException
                or ArgumentOutOfRangeException)
            {
                continue;
            }

            if (await TryReleaseAsync(record.ScopeId, backend, key, cancellationToken).ConfigureAwait(false))
            {
                released++;
            }

            if (await backend.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false) is not null)
            {
                continue;
            }

            await media.TransitionStatusAsync(
                record.ScopeId,
                record.Id,
                MediaStatus.Pending,
                MediaStatus.Deleted,
                clock.GetUtcNow().ToUniversalTime(),
                cancellationToken).ConfigureAwait(false);
        }

        return released;
    }

    /// <summary>
    /// Copies the staged object to the key that can be served, through the provider.
    /// </summary>
    /// <remarks>
    /// The adopted interface has no rename, so promotion is a read of the staged object followed by a
    /// write to the ready one. The staged object's bytes are re-read rather than reused from the
    /// buffer that was sent, so what is promoted is what the provider actually holds; if the
    /// read-back length is not the length that was staged, the promotion is refused instead of
    /// writing a short object under a servable key.
    /// </remarks>
    private static async Task PromoteAsync(
        IObjectStorage backend,
        StorageObjectKey pendingKey,
        StorageObjectKey readyKey,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        var download = await backend.DownloadAsync(pendingKey, cancellationToken).ConfigureAwait(false);
        if (download.Status != StorageOutcomeStatus.Succeeded || download.Value is null)
        {
            throw new MediaStorageUnavailableException("the staged image could not be read back for promotion.");
        }

        await using var staged = download.Value;

        var promoted = new MemoryStream();
        await using (promoted)
        {
            await staged.Content.CopyToAsync(promoted, ReadBufferSize, cancellationToken).ConfigureAwait(false);

            if (promoted.Length != expectedLength)
            {
                throw new MediaStorageUnavailableException("the staged image did not read back whole.");
            }

            var bytes = promoted.ToArray();
            await RequireAsync(
                backend.UploadAsync(
                    new StorageUploadRequest(readyKey, new MemoryStream(bytes, writable: false), MediaTypes.Png, bytes.LongLength),
                    cancellationToken),
                "the image could not be promoted").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads back the provider's own metadata and requires the length that was sent.
    /// </summary>
    /// <remarks>
    /// This is the verification step, and it is a read of the object rather than a re-read of the
    /// buffer: a provider that wrote less than it was given is caught here, before a row can name
    /// it. It is checked for the staged object and again for the promoted one, because promotion is
    /// a second write performed by the provider and is therefore a second chance to go wrong.
    /// </remarks>
    private static async Task RequireVerifiedAsync(
        IObjectStorage backend,
        StorageObjectKey key,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        var metadata = await backend.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
        if (metadata is null || metadata.LengthBytes != expectedLength)
        {
            throw new MediaStorageUnavailableException("the written image did not verify.");
        }
    }

    private static async Task RequireAsync(Task<StorageOutcome> operation, string reason)
    {
        var outcome = await operation.ConfigureAwait(false);

        switch (outcome.Status)
        {
            case StorageOutcomeStatus.Succeeded:
                return;

            // A not-found after a write means the provider did not create the object, which is the
            // same fact as an unavailable provider from the caller's point of view.
            case StorageOutcomeStatus.Rejected:
            case StorageOutcomeStatus.NotFound:
            case StorageOutcomeStatus.Unavailable:
            default:
                throw new MediaStorageUnavailableException(reason + " " + ProviderReason(outcome.Failure));
        }
    }

    /// <summary>
    /// Releases an object, treating an object that is already gone as success - the design's rule, and
    /// the reason a second call to this after a successful one is not an error.
    /// </summary>
    private static async Task ReleaseAsync(
        Guid scopeId,
        IObjectStorage backend,
        StorageObjectKey key,
        CancellationToken cancellationToken)
    {
        RequireScopedKey(scopeId, key);

        var outcome = await backend.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
        if (outcome.Status is StorageOutcomeStatus.Unavailable or StorageOutcomeStatus.Rejected)
        {
            throw new MediaStorageUnavailableException("a staged image could not be released.");
        }
    }

    private static async Task<bool> TryReleaseAsync(
        Guid scopeId,
        IObjectStorage backend,
        StorageObjectKey key,
        CancellationToken cancellationToken)
    {
        RequireScopedKey(scopeId, key);

        var outcome = await backend.DeleteAsync(key, cancellationToken).ConfigureAwait(false);

        return outcome.Status != StorageOutcomeStatus.Unavailable;
    }

    /// <summary>
    /// The S3 half of traversal containment: a valid key that does not live under this scope's
    /// prefix addresses nothing this caller may touch.
    /// </summary>
    /// <remarks>
    /// <see cref="StorageObjectKey"/> already refuses traversal, absolute paths and hostile
    /// spellings before this runs; what it cannot know is whose objects a key names. The prefix is
    /// the scope segment the key shape fixes - <c>scopes/{scope}/</c> - so a stored key naming
    /// another scope is refused exactly like a key that never validated, and the refusal names no
    /// key and no scope.
    /// </remarks>
    /// <exception cref="MediaOutsideRootException">The key is not this scope's (404).</exception>
    private static void RequireScopedKey(Guid scopeId, StorageObjectKey key)
    {
        if (!IsScopedKey(scopeId, key))
        {
            throw new MediaOutsideRootException();
        }
    }

    private static StorageObjectKey ResolveScopedKey(Guid scopeId, string storageKey)
    {
        StorageObjectKey key;

        try
        {
            key = new StorageObjectKey(storageKey);
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
        {
            // A stored key is data. One that is not a valid key addresses nothing, and saying so as a
            // not-found keeps the refusal indistinguishable from an absent record.
            throw new MediaNotFoundException();
        }

        RequireScopedKey(scopeId, key);

        return key;
    }

    private static bool IsScopedKey(Guid scopeId, StorageObjectKey key) =>
        key.Value.StartsWith(
            $"scopes/{scopeId:D}/",
            StringComparison.Ordinal);

    private static string ProviderReason(StorageFailure? failure) =>
        failure is null ? string.Empty : failure.Message;

    private static MediaRef ToReference(MediaRecord record) =>
        new(
            record.Id,
            record.ProjectId,
            record.StorageKey,
            record.MediaType,
            record.ByteLength,
            record.Sha256,
            record.Status,
            record.CreatedAt,
            record.Revision);

    /// <summary>
    /// Reads a body once, under the bound, refusing anything longer without buffering the rest.
    /// </summary>
    /// <remarks>
    /// The bound is enforced by reading at most one byte more than is allowed and refusing when that
    /// byte exists, so an oversized body costs the limit and not the body. The returned array is
    /// exactly the accepted bytes, which is what lets the digest, the decoded type and the length sent
    /// to the provider all describe the same buffer.
    /// </remarks>
    private static async Task<byte[]> ReadBoundedAsync(Stream content, long limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        if (!content.CanRead)
        {
            throw new UnsupportedMediaTypeException("The request body is not readable.");
        }

        var buffer = new MemoryStream();
        var chunk = new byte[ReadBufferSize];

        while (true)
        {
            var read = await content.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > limit)
            {
                throw new MediaTooLargeException(limit);
            }

            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        if (buffer.Length == 0)
        {
            throw new UnsupportedMediaTypeException("The request body is empty.");
        }

        return buffer.ToArray();
    }
}
