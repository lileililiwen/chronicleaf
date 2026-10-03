using Chronicleaf.Application.Common;
using Chronicleaf.Core;

namespace Chronicleaf.Application.Media;

/// <summary>
/// The media codes this package adds to the delivered envelope (LOCAL-MEDIA-STORAGE-001/002).
/// </summary>
/// <remarks>
/// They extend the repository's <c>{code,message,traceId,fieldErrors?}</c> shape without changing
/// it, and each one has exactly one meaning. <see cref="NotFound"/> deliberately reuses the
/// delivered 404 code rather than inventing a media-specific one, so "there is nothing here" is the
/// same answer for an absent id and for an id in another scope: a caller cannot probe for another
/// installation's media by comparing the codes.
/// </remarks>
public static class MediaError
{
    /// <summary>An id that does not exist, or exists in a scope this caller may not read (404).</summary>
    public const string NotFound = ApiError.NotFound;

    /// <summary>
    /// The bytes are not the one image type this package stores (415). Named separately from
    /// <see cref="ApiError.ValidationFailed"/> because the design fixes an unsupported type at 415
    /// while a merely invalid field is 422.
    /// </summary>
    public const string UnsupportedMediaType = "unsupported_media_type";

    /// <summary>
    /// The request body is larger than <see cref="MediaLimits.MaximumByteLength"/> (413). The
    /// delivered payload-too-large code, reused so an oversized body reads the same here as anywhere
    /// else.
    /// </summary>
    public const string PayloadTooLarge = ApiError.PayloadTooLarge;

    /// <summary>
    /// The image is still an accepted type but its decoded dimensions are over
    /// <see cref="MediaLimits.MaximumPixelCount"/> (422). A field error rather than a payload error:
    /// the request was small, and what it decoded to is what is wrong.
    /// </summary>
    public const string ImageTooLarge = "image_too_large";

    /// <summary>
    /// Something still references this media, so it cannot be retired (409). The name is fixed by the
    /// design and is what a client matches on.
    /// </summary>
    public const string MediaInUse = "media_in_use";

    /// <summary>
    /// The storage provider could not serve the request right now (503). Reported as one code for
    /// every provider failure so no provider detail, path or raw body reaches a caller.
    /// </summary>
    public const string StorageUnavailable = "storage_unavailable";

    /// <summary>
    /// The resolved path of an object key leaves the configured MediaRoot (404). It answers as
    /// "not found" rather than as a distinct error, because telling a caller that a key was refused
    /// would confirm the key addresses something.
    /// </summary>
    public const string OutsideMediaRoot = NotFound;
}

/// <summary>
/// A stored image, as the rest of the application sees it: what a reference needs to hold on to and
/// what a caller is told about its own upload.
/// </summary>
/// <remarks>
/// This is deliberately not the <see cref="Core.Media"/> record. A reference in a later package needs
/// an id, a hash, a length and a type, and handing it the stored record would let it reach the
/// storage key - the one field a consumer has no business resolving. The key is in
/// <see cref="StorageKey"/> because the design's reference shape names it, and it is a portable
/// object key rather than a path, so holding it discloses nothing about the operator's disk.
/// </remarks>
/// <param name="Id">This image's identity; what <c>GET /api/media/{id}</c> addresses.</param>
/// <param name="ProjectId">The project it belongs to.</param>
/// <param name="StorageKey">The server-generated object key. Portable and opaque, never a path.</param>
/// <param name="MediaType">The decoded type, which is also what is served.</param>
/// <param name="ByteLength">How many bytes were verified and stored.</param>
/// <param name="Sha256">Lower-case hex SHA-256 of exactly those bytes.</param>
/// <param name="Status">One of the fixed <see cref="MediaStatus"/> values.</param>
/// <param name="CreatedAt">When it was created, from the injected clock, always UTC.</param>
/// <param name="Revision">The optimistic revision of the media record.</param>
public sealed record MediaRef(
    Guid Id,
    Guid ProjectId,
    string StorageKey,
    string MediaType,
    long ByteLength,
    string Sha256,
    string Status,
    DateTimeOffset CreatedAt,
    long Revision);

/// <summary>
/// The bytes of one stored image, together with everything needed to serve them honestly.
/// </summary>
/// <remarks>
/// The stream is owned by the caller of <see cref="IMediaStore.OpenAsync"/> and must be disposed;
/// it is a provider-owned stream over a file inside MediaRoot and must not be assumed to be a seekable
/// buffer. <see cref="MediaType"/> is the decoded type from the row rather than whatever the storage
/// provider inferred from the file extension, and <see cref="Sha256"/> is the stored digest, so a
/// caller that wants to re-verify what it received has a fixed value to compare against.
/// </remarks>
/// <param name="Ref">The stored record, including its own id and status.</param>
/// <param name="Content">The readable bytes. Disposed by the caller.</param>
public sealed record MediaContent(MediaRef Ref, Stream Content) : IAsyncDisposable
{
    public async ValueTask DisposeAsync() => await Content.DisposeAsync().ConfigureAwait(false);
}

/// <summary>
/// One upload, as the store receives it: the owning project and the bytes, and nothing else.
/// </summary>
/// <remarks>
/// There is no key, no filename, no path and no status here, and that omission is the boundary. A
/// caller cannot choose where its bytes land, cannot name the object, and cannot declare the record
/// ready; the store generates the key, decodes the type and decides the state. The declared content
/// type is carried only as the hint it is, and it is never what the record ends up saying.
/// </remarks>
/// <param name="ProjectId">The project the image belongs to. Must exist in the caller's own scope.</param>
/// <param name="Content">The bytes, read once and bounded. The store takes ownership of reading it.</param>
/// <param name="DeclaredContentType">
/// The type the caller claimed, used for nothing but diagnostics. Never trusted and never stored.
/// </param>
/// <param name="OwnerOperationId">
/// The operation that produced these bytes, when one did; <c>null</c> for an upload the owner made.
/// </param>
public sealed record PutMediaInput(
    Guid ProjectId,
    Stream Content,
    string? DeclaredContentType,
    Guid? OwnerOperationId = null);

/// <summary>
/// What retiring an image did (LOCAL-MEDIA-STORAGE-002).
/// </summary>
/// <remarks>
/// A retire is two steps in a fixed order: the record stops being servable first, and the bytes are
/// only then released. A client therefore cannot observe a record that is gone while its bytes are
/// still being served, and a crash between the two steps leaves a record that is unreadable with
/// bytes that a later reconciliation can remove. The design calls the second step asynchronous; it is
/// performed in the same call here and a failure to remove the bytes does not undo the retirement,
/// because the record has already stopped being served and the leftover is unreferenced by
/// definition.
/// </remarks>
/// <param name="Ref">The record as it now stands, with the advanced revision.</param>
/// <param name="BytesReleased">Whether the physical bytes were removed.</param>
public sealed record RetireMediaResult(MediaRef Ref, bool BytesReleased);

/// <summary>
/// An id this owner may not reach (404). The message never distinguishes "absent" from "somebody
/// else's", because that difference is exactly what must not leak.
/// </summary>
public class MediaNotFoundException(string resource = "media")
    : ApiException(MediaError.NotFound, $"No such {resource}.");

/// <summary>
/// The bytes are not an accepted image type (415). Nothing was written and no record was created.
/// </summary>
public sealed class UnsupportedMediaTypeException(string message = "Only PNG images are accepted.")
    : ApiException(MediaError.UnsupportedMediaType, message);

/// <summary>
/// The request body is over the byte limit (413). Nothing was written, because the bound is applied
/// while reading rather than after it.
/// </summary>
public sealed class MediaTooLargeException(long limitBytes)
    : ApiException(MediaError.PayloadTooLarge, $"The request body exceeds the {limitBytes} byte limit.");

/// <summary>
/// The decoded image is over the pixel limit (422). A field error, so a client learns which part of
/// the upload was refused.
/// </summary>
public sealed class MediaImageTooLargeException(int width, int height, long limitPixels)
    : ApiException(
        MediaError.ImageTooLarge,
        $"The image is {width}x{height} pixels, over the {limitPixels} pixel limit.",
        new Dictionary<string, string[]>
        {
            ["content"] = [$"The decoded image is {width}x{height} pixels; the limit is {limitPixels}."],
        });

    /// <summary>
/// The media is still referenced, so it cannot be retired (409). The request changed nothing: the
/// bytes and the record are exactly as they were.
/// </summary>
public sealed class MediaInUseException()
    : ApiException(MediaError.MediaInUse, "This media is referenced and cannot be deleted.");

/// <summary>
/// The storage provider could not complete the operation (503). One message for every provider
/// failure: no provider body, no filesystem path and no key ever reaches a caller through it.
/// </summary>
public sealed class MediaStorageUnavailableException(string reason)
    : ApiException(MediaError.StorageUnavailable, $"The media storage provider is unavailable. {reason}".TrimEnd());

/// <summary>
/// An object key that resolves outside the configured MediaRoot (404).
/// </summary>
/// <remarks>
/// This is an ordinary not-found rather than a distinct code on purpose. A stored key is data, and
/// data can be hostile - a row written by an older build, a row edited by hand, a row imported by a
/// later package. Answering "that key is refused" would confirm that the key addresses something, so
/// the guard raises the same 404 as an absent record and the read never reaches the filesystem.
/// </remarks>
public sealed class MediaOutsideRootException()
    : MediaNotFoundException();
