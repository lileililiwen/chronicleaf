using Chronicleaf.Application.Common;
using Chronicleaf.Application.Media;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Application.Persistence;
using Chronicleaf.Core;
using Microsoft.Extensions.Logging;

namespace Chronicleaf.Infrastructure.Media.S3;

/// <summary>
/// The mode-aware media store: one <c>IMediaStore</c> over two backends, chosen per record
/// (CLOUD-OBJECT-STORAGE-001/002).
/// </summary>
/// <remarks>
/// <para>
/// Writes go to the installation's configured mode; reads, retires and purges go to whichever
/// backend the record's own provider names. That split is the whole of the migration story: rows
/// move one at a time from Local to S3, and every row keeps pointing at bytes that exist however
/// far the migration has got. A record that names a provider this deployment never registered is a
/// 404 rather than a crash, because a row moved between installations is exactly what must not
/// leak.
/// </para>
/// <para>
/// In Local mode this type is never registered at all - the Local store answers directly, with
/// byte-identical behavior to before this package existed. Registration, not a runtime flag, is the
/// mode switch, for the same reason a provider adapter is: a backend that silently stops being
/// reachable in the middle of an owner's workflow is a worse failure than one that was never there.
/// </para>
/// </remarks>
public sealed class ProviderRoutingMediaStore(
    LocalMediaStore local,
    S3MediaStore s3,
    IMediaRepository media,
    MediaSettings settings,
    ILogger<ProviderRoutingMediaStore> logger) : IMediaStore
{
    public Task<MediaRef> PutAsync(
        OwnerScope owner,
        PutMediaInput input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(input);

        // New writes follow the installation's mode. The row records which backend it landed on, so
        // a mode change later moves new writes without stranding the rows that already exist.
        return settings.IsS3Mode
            ? s3.PutAsync(owner, input, cancellationToken)
            : local.PutAsync(owner, input, cancellationToken);
    }

    public async Task<MediaContent> OpenAsync(
        OwnerScope owner,
        Guid mediaId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        // The row decides, not the mode: a Local row in an S3-mode installation is still served from
        // the filesystem until the migration moves it, and an S3 row is never served from disk.
        // The lookup is scope-bound, so a foreign id is a 404 before any backend is chosen.
        var record = await media.FindAsync(owner.ScopeId, mediaId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            throw new MediaNotFoundException();
        }

        return string.Equals(record.ProviderId, StorageModes.S3, StringComparison.Ordinal)
            ? await s3.OpenAsync(owner, mediaId, cancellationToken).ConfigureAwait(false)
            : string.Equals(record.ProviderId, StorageModes.Local, StringComparison.Ordinal)
                ? await local.OpenAsync(owner, mediaId, cancellationToken).ConfigureAwait(false)
                : throw UnknownProvider();
    }

    public async Task<RetireMediaResult> RetireAsync(
        OwnerScope owner,
        Guid mediaId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        var record = await media.FindAsync(owner.ScopeId, mediaId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            throw new MediaNotFoundException();
        }

        return string.Equals(record.ProviderId, StorageModes.S3, StringComparison.Ordinal)
            ? await s3.RetireAsync(owner, mediaId, cancellationToken).ConfigureAwait(false)
            : string.Equals(record.ProviderId, StorageModes.Local, StringComparison.Ordinal)
                ? await local.RetireAsync(owner, mediaId, cancellationToken).ConfigureAwait(false)
                : throw UnknownProvider();
    }

    public async Task<int> PurgeUnreferencedAsync(
        DateTimeOffset stagedBefore,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        // Each backend sweeps its own staged rows. The bound is split rather than shared so one
        // backend's orphans cannot starve the other's sweep, and each sweep is idempotent so running
        // both is safe however many times it happens.
        var half = Math.Max(1, limit / 2);

        var localReleased = await local.PurgeUnreferencedAsync(stagedBefore, half, cancellationToken)
            .ConfigureAwait(false);
        var s3Released = await s3.PurgeUnreferencedAsync(stagedBefore, half, cancellationToken)
            .ConfigureAwait(false);

        return localReleased + s3Released;
    }

    private MediaNotFoundException UnknownProvider()
    {
        // The record names a backend this deployment never registered. Nothing about the stored
        // value is answered or logged, because a stored provider is data and data can be hostile -
        // and what the caller learns is only that there is nothing here it may read.
        logger.LogWarning("A media record names an unregistered storage provider and was refused.");

        return new MediaNotFoundException();
    }
}
