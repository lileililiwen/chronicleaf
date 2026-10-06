using Chronicleaf.Application.Common;
using Chronicleaf.Application.Media;
using Chronicleaf.Application.Milestones;
using Chronicleaf.Core;
using Microsoft.Extensions.Logging;
using Platform.Storage.Contracts;
using Platform.Storage.Keys;

namespace Chronicleaf.Infrastructure.Media.S3;

/// <summary>
/// Mints bounded, scope-checked presigned reads over the S3 backend
/// (CLOUD-OBJECT-STORAGE-001).
/// </summary>
/// <remarks>
/// <para>
/// The checks run in the order the requirement names: the record must be this scope's, it must be
/// ready, and the lifetime must be positive and at most five minutes - and all of that is decided
/// before the provider is touched, so a foreign id is a 404 with no presign minted and no byte
/// transferred. The minted URL carries the bound the provider enforces: expiry is what the
/// signature says, not what this service remembers, so an expired URL authorizes nothing because
/// the provider refuses it.
/// </para>
/// <para>
/// Each call opens exactly one backend from the factory and disposes it before returning, so the
/// leased key pair lives for the length of the mint and no longer. Nothing minted here is ever
/// logged: the URL is returned to the owner that asked and to nobody else.
/// </para>
/// </remarks>
public sealed class S3MediaLinkService(
    IS3BackendFactory backends,
    IMediaRepository media,
    ILogger<S3MediaLinkService> logger) : IS3MediaLinkService
{
    public async Task<PresignedMediaLink> PresignGetAsync(
        OwnerScope owner,
        Guid mediaId,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (lifetime <= TimeSpan.Zero || lifetime > S3MediaOptions.MaxPresignLifetime)
        {
            throw new ValidationFailedException(
                "lifetime",
                $"The presigned URL lifetime must be positive and at most {S3MediaOptions.MaxPresignLifetime.TotalMinutes} minutes.");
        }

        // Scope first, provider second, status third. All three produce the same not-found, so a
        // caller cannot tell a record in another scope, a record on the other backend, or a record
        // that is not servable apart - and none of them reaches the provider.
        var record = await media.FindAsync(owner.ScopeId, mediaId, cancellationToken).ConfigureAwait(false);
        if (record is null
            || !string.Equals(record.ProviderId, StorageModes.S3, StringComparison.Ordinal)
            || !record.IsServable)
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
            logger.LogWarning("A stored S3 media key is not a valid object key and was refused.");
            throw new MediaNotFoundException();
        }

        if (!key.Value.StartsWith($"scopes/{owner.ScopeId:D}/", StringComparison.Ordinal))
        {
            throw new MediaNotFoundException();
        }

        await using var backend = await backends.CreateAsync(cancellationToken).ConfigureAwait(false);

        var (outcome, operation) = await backend.PresignAsync(
            new PresignRequest(key, StorageOperation.Download, lifetime),
            cancellationToken).ConfigureAwait(false);

        if (outcome.Status != StorageOutcomeStatus.Succeeded || operation is null)
        {
            throw new MediaStorageUnavailableException(ProviderReason(outcome.Failure));
        }

        return new PresignedMediaLink(mediaId, operation.Url, operation.ExpiresAt);
    }

    private static string ProviderReason(StorageFailure? failure) =>
        failure is null ? string.Empty : failure.Message;
}
