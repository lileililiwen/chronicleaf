using System.Buffers.Binary;
using Chronicleaf.Core;

namespace Chronicleaf.Application.Media;

/// <summary>
/// The decoded facts of one stored image (LOCAL-MEDIA-STORAGE-001).
/// </summary>
/// <param name="MediaType">The type the bytes actually are; never a declared one.</param>
/// <param name="Width">The width from the image's own header.</param>
/// <param name="Height">The height from the image's own header.</param>
public sealed record DecodedImage(string MediaType, int Width, int Height)
{
    /// <summary>How many pixels the image claims to have, as a 64-bit product so it cannot overflow.</summary>
    public long PixelCount => (long)Width * Height;
}

/// <summary>
/// Decides whether bytes are an image this package stores, from the bytes alone
/// (LOCAL-MEDIA-STORAGE-001).
/// </summary>
/// <remarks>
/// <para>
/// The type is decided by structure, never by a <c>Content-Type</c> header and never by a filename.
/// A request that says <c>image/png</c> and sends something else is refused by exactly the same check
/// that accepts an honest one, which is why this type takes a <c>ReadOnlySpan</c> of the body and has
/// no access to the request at all.
/// </para>
/// <para>
/// The check is structural rather than a four-byte prefix. A PNG begins with a fixed signature, its
/// first chunk is always <c>IHDR</c> carrying the real dimensions, and it ends with an empty
/// <c>IEND</c>. Requiring all three means a file that merely starts with the signature - a truncated
/// upload, a text file someone prefixed, an HTML error page stored as an image - is refused, and it
/// means the pixel bound is applied to the dimensions the image really has rather than to anything a
/// caller could state. Every read below is bounds-checked against the buffer, so a malformed chunk
/// length ends the scan instead of reading past the data.
/// </para>
/// <para>
/// This is a validator, not a decoder: it reads the header and the terminator and nothing else. It
/// does not decompress pixels, so it cannot be made to allocate by a hostile image, and it makes no
/// claim about a file's visual content - only that it is shaped like a complete PNG of a stated size.
/// </para>
/// </remarks>
public static class MediaImage
{
    /// <summary>The eight bytes every PNG begins with.</summary>
    public static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private const int SignatureLength = 8;
    private const int ChunkHeaderLength = 8;
    private const int HeaderChunkLength = 13;
    private const int MaxChunksScanned = 4096;

    /// <summary>
    /// Reads the decoded facts of an image, or reports that these bytes are not one.
    /// </summary>
    /// <remarks>
    /// The whole body has to be present, which is why the caller reads it under a bound first: this
    /// method takes bytes, not a stream, so it never partially consumes a request it is going to
    /// refuse.
    /// </remarks>
    public static bool TryDecode(ReadOnlySpan<byte> content, out DecodedImage image)
    {
        image = null!;

        if (content.Length < SignatureLength || !content[..SignatureLength].SequenceEqual(PngSignature))
        {
            return false;
        }

        // The first chunk is IHDR by specification; anything else means these are not PNG bytes.
        if (!TryReadChunk(content, SignatureLength, out var chunkType, out var chunkData)
            || !chunkType.SequenceEqual("IHDR"u8)
            || chunkData.Length != HeaderChunkLength)
        {
            return false;
        }

        var width = BinaryPrimitives.ReadUInt32BigEndian(chunkData[..4]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(chunkData[4..8]);

        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
        {
            return false;
        }

        // A PNG is not complete without its terminator, and this is what separates a real image from
        // a body that merely begins like one.
        if (!HasTerminator(content, SignatureLength))
        {
            return false;
        }

        image = new DecodedImage(MediaTypes.Png, (int)width, (int)height);

        return true;
    }

    /// <summary>
    /// Decodes or throws the delivered 415, with the message this package authors. No caller-supplied
    /// text is interpolated into it, because the middleware writes it verbatim.
    /// </summary>
    public static DecodedImage Decode(ReadOnlySpan<byte> content) =>
        TryDecode(content, out var image) ? image : throw new UnsupportedMediaTypeException();

    private static bool TryReadChunk(
        ReadOnlySpan<byte> content,
        int offset,
        out ReadOnlySpan<byte> type,
        out ReadOnlySpan<byte> data)
    {
        type = default;
        data = default;

        if (offset < 0 || offset + ChunkHeaderLength > content.Length)
        {
            return false;
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(content[offset..]);
        var body = offset + ChunkHeaderLength;

        // A chunk longer than the data that is left is a truncated or hostile file, not a chunk.
        if (length > int.MaxValue || body + length > content.Length)
        {
            return false;
        }

        type = content.Slice(offset + 4, 4);
        data = content.Slice(body, (int)length);

        return true;
    }

    private static bool HasTerminator(ReadOnlySpan<byte> content, int offset)
    {
        for (var scanned = 0; scanned < MaxChunksScanned; scanned++)
        {
            if (!TryReadChunk(content, offset, out var chunkType, out var chunkData))
            {
                return false;
            }

            if (chunkType.SequenceEqual("IEND"u8))
            {
                // IEND is specified as an empty chunk; a non-empty one is not a terminator.
                return chunkData.Length == 0;
            }

            offset += ChunkHeaderLength + chunkData.Length + 4;
        }

        // A file with thousands of chunks before its terminator is refused rather than scanned
        // without end, so the check stays bounded work.
        return false;
    }
}
