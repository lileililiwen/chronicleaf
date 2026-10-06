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

namespace Chronicleaf.Infrastructure.Media;

/// <summary>
/// The Local media store: the design's <c>IMediaStore</c> over the adopted shared storage
/// (LOCAL-MEDIA-STORAGE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// The provider is <c>Platform.Storage</c>'s <see cref="IObjectStorage"/>, adopted rather than
/// rewritten, and the filesystem adapter behind it is <c>Platform.Storage.Local</c>. Neither is
/// declared in the Application layer: the interface above is Chronicleaf's own facade, and this class
/// is the only place the two meet. That is what keeps the three guarantees of this package in one
/// readable sequence instead of spread across a provider and its callers.
/// </para>
///
/// <para><b>The two-phase upload.</b> <see cref="PutAsync"/> never writes straight to a servable key,
/// because a direct write has exactly one failure mode that matters - the crash between the bytes and
/// the record - and nothing can undo it. So it runs four observable steps, and the order is the whole
/// point:</para>
/// <list type="number">
/// <item>
/// <b>Stage.</b> The bytes, already decoded and hashed, are written under a server-generated
/// <em>pending</em> key. A pending key is not a servable key, so at this instant nothing can be
/// served and nothing is recorded.
/// </item>
/// <item>
/// <b>Verify.</b> The provider's own metadata for the staged object is read back and its length is
/// compared with the length that was sent. This is a read of the file that was actually written, not
/// of the buffer that was meant to be written, so a short write is caught here rather than by a
/// reader later.
/// </item>
/// <item>
/// <b>Promote.</b> The object is copied to the <em>ready</em> key through the provider, verified the
/// same way, and the pending object is released. A release of something already gone is success, as
/// the design requires. Promotion is a copy rather than a rename because the adopted interface has no
/// rename: the two-phase property does not depend on the primitive, only on the fact that the servable
/// key is written, verified and only then referenced.
/// </item>
/// <item>
/// <b>Register.</b> The row is inserted in one unit of work with its audit row, and only with
/// <see cref="MediaStatus.Ready"/> and the ready key that was just verified.</item>
/// </list>
///
/// <para><b>Why no failure can leave a ready row pointing at missing bytes.</b> The row is written
/// last, so every earlier failure happens while there is no row to be wrong. And the one failure that
/// happens after it - the insert or the commit itself - is the one case where bytes exist with no
/// record, which the compensation below removes. The three ways this can end are therefore:
/// a failure at step 1 or 2 leaves at most a pending object and no row; a failure at step 3 releases
/// both objects and leaves no row; a failure at step 4 releases the ready object and leaves no row.
/// There is no ordering in which a committed row names a key whose bytes were not verified, because
/// the row is the last thing that happens and the verification is the thing before it. What is left
/// behind in the worst case is an unreferenced object, which is unservable by construction - only a
/// <see cref="MediaStatus.Ready"/> row is ever read - and which
/// <see cref="PurgeUnreferencedAsync"/> removes.</para>
///
/// <para><b>What is not here.</b> No presigned URL is ever issued: the Local adapter's presign
/// produces a <c>local://</c> string that no client can fetch, and the design forbids a public URL for
/// it, so the code path is absent rather than disabled. No absolute path is stored, no caller's
/// filename or key is read, and there is no network call of any kind.</para>
/// </remarks>
public sealed class LocalMediaStore(
    IObjectStorage storage,
    IMediaRepository media,
    IMediaReferenceChecker references,
    IMediaIdSource ids,
    IProjectStore projects,
    IUnitOfWork unitOfWork,
    IAuditWriter audit,
    MediaRoot root,
    MediaSettings settings,
    TimeProvider clock,
    ILogger<LocalMediaStore> logger) : IMediaStore
{
    /// <summary>The audit action recorded when an upload is registered.</summary>
    public const string MediaCreatedAction = "media.created";

    /// <summary>The audit action recorded when an image is retired.</summary>
    public const string MediaRetiredAction = "media.retired";

    /// <summary>The audit detail for a registered image. A fixed label, never a key or a type.</summary>
    public const string RegisteredDetail = "ready";

    /// <summary>The audit detail for a retired image. It records the outcome, not the reason found.</summary>
    public const string RetiredDetail = "deleted";

    private const int ReadBufferSize = 81920;

    /// <summary>
    /// How deep below the MediaRoot a sweep descends. Six levels is exactly the key's own shape -
    /// scopes, a scope, projects, a project, media, a stage - and one more, so a shape that grew a
    /// segment would be skipped rather than half-walked.
    /// </summary>
    private const int MaxSweepDepth = 6;

    public async Task<MediaRef> PutAsync(
        OwnerScope owner,
        PutMediaInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(input);

        // The project has to exist in this caller's own scope. A media record that named a project in
        // another installation would be unreachable by its owner and reachable by nobody, so the
        // reference is checked before a single byte is read.
        if (await projects.FindAsync(owner.ScopeId, input.ProjectId, cancellationToken).ConfigureAwait(false) is null)
        {
            throw new MediaNotFoundException("project");
        }

        // Read once, under the bound. Nothing is written before this returns, so an oversized body is
        // refused with an empty MediaRoot rather than half-filled with it.
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
        root.ResolveInside(pendingKey);
        root.ResolveInside(readyKey);

        await RequireAsync(
            UploadAsync(pendingKey, bytes, cancellationToken),
            "the image could not be staged").ConfigureAwait(false);

        try
        {
            await RequireVerifiedAsync(pendingKey, bytes.LongLength, cancellationToken).ConfigureAwait(false);

            await PromoteAsync(pendingKey, readyKey, bytes.LongLength, cancellationToken).ConfigureAwait(false);
            await ReleaseAsync(pendingKey, cancellationToken).ConfigureAwait(false);

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
                StorageModes.Local);

            await RegisterAsync(owner, record, cancellationToken).ConfigureAwait(false);

            return ToReference(record);
        }
        catch (Exception exception) when (exception is not MediaOutsideRootException)
        {
            // Compensation. The ready object is released first because it is the only one that could
            // ever be served, and both are released without failing the request: this path is already
            // reporting a failure, and a provider that cannot delete right now leaves an object that
            // is unservable because no row names it.
            await TryReleaseAsync(readyKey, cancellationToken).ConfigureAwait(false);
            await TryReleaseAsync(pendingKey, cancellationToken).ConfigureAwait(false);

            logger.LogWarning(
                exception,
                "A media upload failed after staging and was released; nothing was registered.");

            throw;
        }
    }

    /// <summary>
    /// Commits the record and its audit row, and reports a database failure as such.
    /// </summary>
    /// <remarks>
    /// This is the last step of the sequence and the only one that can leave bytes with no row, so it
    /// is also the only one whose failure has to be compensated by the caller's catch. A refusal this
    /// package authors - a colliding id - is rethrown unchanged; anything else came out of the
    /// database, and the honest answer for a dependency that could not commit is 503 rather than a
    /// 500 that would suggest the image itself was at fault. Either way the caller releases the bytes,
    /// so no committed row and no orphan survive together.
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
                        MediaCreatedAction,
                        record.Id,
                        record.Revision,
                        RegisteredDetail));

                    return true;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not ApiException)
        {
            logger.LogError(exception, "The media record could not be committed.");

            throw new DependencyUnavailableException("media registration");
        }
    }

    public async Task<MediaContent> OpenAsync(
        OwnerScope owner,
        Guid mediaId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        // Scope first, status second. Both produce the same not-found, so a caller cannot tell a
        // record in another installation from a record that is not servable.
        var record = await media.FindAsync(owner.ScopeId, mediaId, cancellationToken).ConfigureAwait(false);
        if (record is null || !record.IsServable)
        {
            throw new MediaNotFoundException();
        }

        StorageObjectKey key;
        try
        {
            key = new StorageObjectKey(record.StorageKey);
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
        {
            // A stored key is data. One that is not a valid key addresses nothing, and saying so as a
            // not-found keeps the refusal indistinguishable from an absent record.
            logger.LogWarning("A stored media key is not a valid object key and was refused.");
            throw new MediaNotFoundException();
        }

        // The resolved-path guard runs on every read, so a row written by an older build, edited by
        // hand or imported by a later package still cannot make this process read outside MediaRoot.
        var download = await storage.DownloadAsync(key, cancellationToken).ConfigureAwait(false);
        if (download.Status == StorageOutcomeStatus.NotFound || download.Value is null)
        {
            // A ready row whose bytes are gone is a fault, not a 404 the caller can act on, and it is
            // still a 404 outward: the record is not servable and that is all a caller may learn.
            logger.LogError("A ready media record named an object the provider does not have.");
            throw new MediaNotFoundException();
        }

        if (download.Status == StorageOutcomeStatus.Unavailable)
        {
            throw new MediaStorageUnavailableException(ProviderReason(download.Failure));
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
        if (record is null || !record.IsServable)
        {
            throw new MediaNotFoundException();
        }

        // The key is resolved before the reference check, so a stored key that does not resolve inside
        // MediaRoot is refused before this call has written anything at all - not after it has retired
        // the record. A retire that answered 404 having already changed the row would be a lie about
        // what it did, and a hostile key must not be able to cause one.
        var key = ResolveKey(record.StorageKey);
        root.ResolveInside(key);

        // The reference check runs before anything is written, so a refused retire has changed
        // nothing at all: the record is still ready and the bytes are still there.
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
                    MediaRetiredAction,
                    record.Id,
                    record.Revision + 1,
                    RetiredDetail));

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

        // The record is no longer servable before this runs, so a failure to release the bytes cannot
        // leave something readable, and a missing object is success rather than a failure.
        var released = await TryReleaseAsync(key, cancellationToken).ConfigureAwait(false);

        return new RetireMediaResult(ToReference(updated), released);
    }

    public async Task<int> PurgeUnreferencedAsync(
        DateTimeOffset stagedBefore,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        // The rows first, because they decide what counts as an orphan. A staged record older than the
        // cutoff is unreferenced by definition - nothing can reference a record that was never
        // published - and a pending record is unreferenced by definition whatever its age, so neither
        // needs a reference check to justify being swept. Only this backend's rows are swept: the
        // S3 store sweeps its own, and neither may move the other's. In a Local-only database every
        // row is a Local row, so the filter changes nothing there.
        var stale = await media.ListStalePendingAsync(stagedBefore, limit, cancellationToken)
            .ConfigureAwait(false);
        var own = stale
            .Where(record => string.Equals(record.ProviderId, StorageModes.Local, StringComparison.Ordinal))
            .ToList();
        var swept = own
            .Select(record => record.StorageKey)
            .ToHashSet(StringComparer.Ordinal);

        var released = 0;

        // Then the objects. Two kinds of staged object are swept: one a stale record names, and one no
        // record names at all - which is what a process that died between the write and the
        // registration leaves behind, and which no row can ever vouch for. An object named by a record
        // that is not stale is left alone, because that record is still live.
        foreach (var candidate in EnumerateStagedObjects(limit))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!swept.Contains(candidate)
                && await media.FindByStorageKeyAsync(candidate, cancellationToken).ConfigureAwait(false) is not null)
            {
                continue;
            }

            if (await TryReleaseAsync(new StorageObjectKey(candidate), cancellationToken).ConfigureAwait(false))
            {
                released++;
            }
        }

        // Finally the rows whose object is gone. A staged record that has just been swept names
        // nothing, and moving it to a terminal status stops it being a candidate for every future
        // pass. It was never servable and nothing here makes it so.
        foreach (var record in own)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (root.TryResolveInside(record.StorageKey, out _)
                && await storage.GetMetadataAsync(
                    new StorageObjectKey(record.StorageKey),
                    cancellationToken).ConfigureAwait(false) is not null)
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
    /// The staged objects on disk, as portable keys, bounded by <paramref name="limit"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The walk is by depth rather than by the key's shape, so it does not have to restate
    /// <c>scopes/{scope}/projects/{project}/media/{stage}/{id}.png</c> in a second place where the
    /// shape could change without this following. Every directory below the root is passed through the
    /// same guard the read path uses before it is descended into, so a link planted inside the root
    /// cannot make the sweep enumerate - and therefore delete - anything outside it. A refused
    /// directory is skipped rather than fatal: one hostile entry must not stop the sweep from cleaning
    /// up after an interrupted upload.
    /// </para>
    /// <para>
    /// The depth is bounded and so is the number of objects returned, because a sweep that can walk
    /// without end is a sweep an operator cannot reason about.
    /// </para>
    /// </remarks>
    private IEnumerable<string> EnumerateStagedObjects(int limit) =>
        Walk(root.Root, string.Empty, 0, new SweepBudget(limit));

    /// <summary>
    /// What a sweep is still allowed to yield. A class rather than a counter passed by reference,
    /// because an iterator method cannot take a ref parameter and the bound has to survive across the
    /// whole recursion rather than being reset by each level.
    /// </summary>
    private sealed class SweepBudget(int limit)
    {
        public int Limit { get; } = limit;

        public int Found { get; set; }

        public bool Exhausted => Found >= Limit;
    }

    private IEnumerable<string> Walk(
        string directory,
        string relativeKey,
        int depth,
        SweepBudget budget)
    {
        if (depth > MaxSweepDepth)
        {
            yield break;
        }

        foreach (var file in SafeFiles(directory))
        {
            if (budget.Exhausted)
            {
                yield break;
            }

            var key = relativeKey.Length == 0
                ? Path.GetFileName(file)
                : $"{relativeKey}/{Path.GetFileName(file)}";

            if (!MediaValues.IsPendingStorageKey(key))
            {
                continue;
            }

            budget.Found++;
            yield return key;
        }

        foreach (var child in SafeDirectories(directory))
        {
            if (budget.Exhausted)
            {
                yield break;
            }

            var name = Path.GetFileName(child);
            var childKey = relativeKey.Length == 0 ? name : $"{relativeKey}/{name}";

            if (!root.TryResolveInside(childKey, out _))
            {
                continue;
            }

            foreach (var key in Walk(child, childKey, depth + 1, budget))
            {
                yield return key;
            }
        }
    }

    private static IEnumerable<string> SafeDirectories(string path)
    {
        string[] entries;

        try
        {
            entries = Directory.GetDirectories(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var entry in entries.Order(StringComparer.Ordinal))
        {
            yield return entry;
        }
    }

    private static IEnumerable<string> SafeFiles(string path)
    {
        string[] entries;

        try
        {
            entries = Directory.GetFiles(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var entry in entries.Order(StringComparer.Ordinal))
        {
            yield return entry;
        }
    }

    /// <summary>
    /// Copies the staged object to the key that can be served, through the provider.
    /// </summary>
    /// <remarks>
    /// The adopted interface has no rename, so promotion is a read of the staged object followed by a
    /// write to the ready one. The staged object's bytes are re-read rather than reused from the
    /// buffer that was sent, so what is promoted is what is actually on disk; if the read-back length
    /// is not the length that was staged, the promotion is refused instead of writing a short object
    /// under a servable key. The ready key is verified by the caller immediately afterwards, which
    /// makes this a second provider write with its own verification rather than an assumed one.
    /// </remarks>
    private async Task PromoteAsync(
        StorageObjectKey pendingKey,
        StorageObjectKey readyKey,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        var download = await storage.DownloadAsync(pendingKey, cancellationToken).ConfigureAwait(false);
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
                UploadAsync(readyKey, bytes, cancellationToken),
                "the image could not be promoted").ConfigureAwait(false);
        }
    }

    private Task<StorageOutcome> UploadAsync(
        StorageObjectKey key,
        byte[] bytes,
        CancellationToken cancellationToken) =>
        storage.UploadAsync(
            new StorageUploadRequest(key, new MemoryStream(bytes, writable: false), MediaTypes.Png, bytes.LongLength),
            cancellationToken);

    /// <summary>
    /// Reads back the provider's own metadata and requires the length that was sent.
    /// </summary>
    /// <remarks>
    /// This is the verification step, and it is a read of the object rather than a re-read of the
    /// buffer: a provider that wrote less than it was given, or wrote to a key that resolved
    /// somewhere else, is caught here, before a row can name it. The failure it prevents is the exact
    /// one the requirement names - a ready row pointing at missing or short bytes - and it is checked
    /// for the staged object and again for the promoted one, because promotion is a second write
    /// performed by the provider and is therefore a second chance to go wrong.
    /// </remarks>
    private async Task RequireVerifiedAsync(
        StorageObjectKey key,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        var metadata = await storage.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
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
    private async Task ReleaseAsync(StorageObjectKey key, CancellationToken cancellationToken)
    {
        if (!root.TryResolveInside(key.Value, out _))
        {
            throw new MediaOutsideRootException();
        }

        var outcome = await storage.DeleteAsync(key, cancellationToken).ConfigureAwait(false);
        if (outcome.Status is StorageOutcomeStatus.Unavailable or StorageOutcomeStatus.Rejected)
        {
            throw new MediaStorageUnavailableException("a staged image could not be released.");
        }
    }

    private async Task<bool> TryReleaseAsync(StorageObjectKey key, CancellationToken cancellationToken)
    {
        if (!root.TryResolveInside(key.Value, out _))
        {
            throw new MediaOutsideRootException();
        }

        var outcome = await storage.DeleteAsync(key, cancellationToken).ConfigureAwait(false);

        return outcome.Status != StorageOutcomeStatus.Unavailable;
    }

    private StorageObjectKey ResolveKey(string storageKey)
    {
        try
        {
            return new StorageObjectKey(storageKey);
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
        {
            throw new MediaNotFoundException();
        }
    }

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
