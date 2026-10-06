using Chronicleaf.Application.Media;
using Chronicleaf.Application.Milestones;

namespace Chronicleaf.Infrastructure.Media.S3;

/// <summary>
/// The presign answer of a deployment with no S3 backend (CLOUD-OBJECT-STORAGE-001).
/// </summary>
/// <remarks>
/// Registered in Local mode so the presign route resolves in every deployment. It answers 503
/// before reading anything: there is no S3 backend here, so no record - whatever its provider -
/// can have a presigned URL minted for it. This is not a permissive stub that swallows the
/// question: it is the honest answer for an installation whose media lives on the filesystem, and
/// a deployment that does have S3 gets a different implementation rather than a weakened one.
/// </remarks>
public sealed class UnconfiguredS3MediaLinkService : IS3MediaLinkService
{
    public Task<PresignedMediaLink> PresignGetAsync(
        OwnerScope owner,
        Guid mediaId,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        throw new MediaStorageUnavailableException(
            "Presigned media URLs are not available: this installation stores media locally.");
    }
}
