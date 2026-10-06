using Platform.Storage.Contracts;
using Platform.Storage.Keys;

namespace Chronicleaf.Infrastructure.Media.S3;

/// <summary>
/// One leased S3 backend: the adopted platform adapter over a caller-owned client, disposed with it
/// (CLOUD-OBJECT-STORAGE-001).
/// </summary>
/// <remarks>
/// A delegating wrapper rather than a second implementation: every call reaches the platform's own
/// <see cref="IObjectStorage"/>, so conformance stays the platform's property rather than a copy of
/// it. The release runs once, on dispose, and a backend that was never disposed is a caller bug the
/// factory's triple-use cannot hide - the credential lease it releases is returned here, which is
/// what keeps a plaintext pair from outliving the call that needed it.
/// </remarks>
public sealed class OwnedS3Backend : IObjectStorage, IAsyncDisposable
{
    private readonly IObjectStorage _inner;
    private readonly Func<ValueTask> _release;
    private int _released;

    public OwnedS3Backend(IObjectStorage inner, Func<ValueTask> release)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _release = release ?? throw new ArgumentNullException(nameof(release));
    }

    public Task<StorageOutcome> UploadAsync(StorageUploadRequest request, CancellationToken cancellationToken = default) =>
        _inner.UploadAsync(request, cancellationToken);

    public Task<StorageDownloadResult> DownloadAsync(StorageObjectKey key, CancellationToken cancellationToken = default) =>
        _inner.DownloadAsync(key, cancellationToken);

    public Task<StorageObjectMetadata?> GetMetadataAsync(StorageObjectKey key, CancellationToken cancellationToken = default) =>
        _inner.GetMetadataAsync(key, cancellationToken);

    public Task<StorageOutcome> DeleteAsync(StorageObjectKey key, CancellationToken cancellationToken = default) =>
        _inner.DeleteAsync(key, cancellationToken);

    public Task<(StorageOutcome Outcome, PresignedOperation? Operation)> PresignAsync(
        PresignRequest request,
        CancellationToken cancellationToken = default) =>
        _inner.PresignAsync(request, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) == 1)
        {
            return;
        }

        await _release().ConfigureAwait(false);
    }
}
