using System.Text.Json.Serialization;
using Chronicleaf.Application.Media;

namespace Chronicleaf.Api.Media;

/// <summary>
/// The <c>POST /api/media</c> response body, pinned field by field.
/// </summary>
/// <remarks>
/// It carries the decoded type, the verified length, the digest and the state, because those are the
/// facts a client has to store to refer to the image later. It carries the server-generated
/// <c>storageKey</c> because the design's reference shape names it and because it is a portable opaque
/// key rather than a path - holding it discloses nothing about the operator's disk. There is no field
/// for a filename, a directory or a content type the caller declared: none of those is accepted, so
/// none of them is echoed back as if it were.
/// </remarks>
public sealed record MediaResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("storageKey")] string StorageKey,
    [property: JsonPropertyName("mediaType")] string MediaType,
    [property: JsonPropertyName("byteLength")] long ByteLength,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("createdAt")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("revision")] long Revision)
{
    public static MediaResponse From(MediaRef media) =>
        new(
            media.Id.ToString("D"),
            media.ProjectId.ToString("D"),
            media.StorageKey,
            media.MediaType,
            media.ByteLength,
            media.Sha256,
            media.Status,
            media.CreatedAt,
            media.Revision);
}

/// <summary>
/// The <c>DELETE /api/media/{id}</c> response body.
/// </summary>
/// <remarks>
/// The record is reported rather than replaced with 204, because a retire is not an erasure: the row
/// stays as the record that the bytes existed, and a client that asked to delete is told what state
/// the record is now in rather than being told nothing. <c>bytesReleased</c> distinguishes "the bytes
/// are gone" from "the bytes were already gone, which is success", so neither case has to be
/// inferred.
/// </remarks>
public sealed record RetiredMediaResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("revision")] long Revision,
    [property: JsonPropertyName("bytesReleased")] bool BytesReleased)
{
    public static RetiredMediaResponse From(RetireMediaResult result) =>
        new(
            result.Ref.Id.ToString("D"),
            result.Ref.Status,
            result.Ref.Revision,
            result.BytesReleased);
}

/// <summary>
/// The <c>POST /api/media/{id}/presign</c> response body (CLOUD-OBJECT-STORAGE-001).
/// </summary>
/// <remarks>
/// It carries the URL and the instant the provider stops authorizing it, because a bounded read is
/// only honest when the bound travels with it. The URL is opaque - a signature over the bucket, the
/// scoped key and the expiry - and discloses no key material, no bucket layout beyond the one
/// object, and no credential. There is no field for a lifetime the caller asked for: the expiry is
/// the provider's answer, not an echo.
/// </remarks>
public sealed record PresignedMediaUrlResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt)
{
    public static PresignedMediaUrlResponse From(PresignedMediaLink link) =>
        new(
            link.MediaId.ToString("D"),
            link.Url.ToString(),
            link.ExpiresAt);
}
