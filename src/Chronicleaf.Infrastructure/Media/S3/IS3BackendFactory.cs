using Chronicleaf.Application.Media;

namespace Chronicleaf.Infrastructure.Media.S3;

/// <summary>
/// Builds one S3-backed object-storage backend per call (CLOUD-OBJECT-STORAGE-001).
/// </summary>
/// <remarks>
/// A factory rather than a registered backend because the keys are leased: a backend built once at
/// start-up would hold a plaintext pair for the life of the process, while a backend built here
/// holds one for the length of the returned <see cref="OwnedS3Backend"/>, which the caller
/// disposes. In Local mode this service is never registered at all, which is what keeps a
/// self-hosted deployment free of S3 clients, S3 credentials and S3 network calls. The interface
/// lives here rather than in Application because a backend is the adopted platform's own
/// <c>IObjectStorage</c>, and only Infrastructure may name it.
/// </remarks>
public interface IS3BackendFactory
{
    /// <summary>
    /// Builds a backend bound to one leased key pair. Dispose it when the call is over.
    /// </summary>
    /// <exception cref="MediaStorageUnavailableException">
    /// The S3 backend is not configured or its credential cannot be opened (503).
    /// </exception>
    Task<OwnedS3Backend> CreateAsync(CancellationToken cancellationToken = default);
}
