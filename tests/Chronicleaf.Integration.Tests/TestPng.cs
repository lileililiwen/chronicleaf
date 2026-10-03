namespace Chronicleaf.Integration.Tests;

/// <summary>
/// Builds real PNG bytes for the media scenarios.
/// </summary>
/// <remarks>
/// These are genuine files, not fixtures that merely begin like images: signature, an <c>IHDR</c>
/// carrying real dimensions, a zlib-compressed <c>IDAT</c> and an empty <c>IEND</c>, with every
/// chunk's CRC32 computed the way the format specifies. That matters because the package's type check
/// is structural - it reads the header and the terminator - and a scenario that fed it a hand-written
/// approximation would be testing the approximation rather than the decoder. The image data itself is
/// a flat grey ramp, which keeps the encoded file small and fully deterministic.
/// </remarks>
internal static class TestPng
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>A valid greyscale PNG of the stated size.</summary>
    public static byte[] Create(int width, int height) => Assemble(width, height, includeTerminator: true);

    /// <summary>
    /// Valid PNG bytes with no <c>IEND</c> chunk, which is what a truncated or interrupted upload
    /// looks like. It has the right signature and the right header and is still not an image.
    /// </summary>
    public static byte[] CreateWithoutTerminator(int width, int height) =>
        Assemble(width, height, includeTerminator: false);

    /// <summary>
    /// A valid PNG followed by trailing bytes, so the only thing wrong with the body is its length.
    /// The size check runs before the decode, so this is how a scenario proves the byte bound - and
    /// not the type check - is what refused it.
    /// </summary>
    public static byte[] CreateOversizedBy(int extraBytes) =>
        [.. Create(2, 2), .. new byte[extraBytes]];

    private static byte[] Assemble(int width, int height, bool includeTerminator)
    {
        using var file = new MemoryStream();

        file.Write(Signature);
        file.Write(Chunk("IHDR"u8, Header(width, height)));
        file.Write(Chunk("IDAT"u8, Compress(Pixels(width, height))));

        if (includeTerminator)
        {
            file.Write(Chunk("IEND"u8, []));
        }

        return file.ToArray();
    }

    private static byte[] Header(int width, int height)
    {
        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)width);
        WriteBigEndian(header, 4, (uint)height);

        // Bit depth 8, colour type 0 (greyscale), no interlace, and the three filter/compression
        // method bytes the format fixes at zero.
        header[8] = 8;
        header[9] = 0;
        header[10] = 0;
        header[11] = 0;
        header[12] = 0;

        return header;
    }

    /// <summary>One filter byte and one byte per pixel, for every row. A ramp, so it compresses.</summary>
    private static byte[] Pixels(int width, int height)
    {
        var raw = new byte[height * (width + 1)];
        var offset = 0;

        for (var y = 0; y < height; y++)
        {
            raw[offset++] = 0;

            for (var x = 0; x < width; x++)
            {
                raw[offset++] = (byte)((x * 7 + y * 13) % 256);
            }
        }

        return raw;
    }

    private static byte[] Compress(byte[] raw)
    {
        using var output = new MemoryStream();

        using (var deflate = new System.IO.Compression.ZLibStream(
            output,
            System.IO.Compression.CompressionLevel.Optimal,
            leaveOpen: true))
        {
            deflate.Write(raw, 0, raw.Length);
        }

        return output.ToArray();
    }

    private static byte[] Chunk(ReadOnlySpan<byte> type, byte[] data)
    {
        var chunk = new byte[12 + data.Length];

        WriteBigEndian(chunk, 0, (uint)data.Length);
        type.CopyTo(chunk.AsSpan(4));
        data.CopyTo(chunk, 8);

        // The CRC covers the type and the data, never the length.
        var crcInput = new byte[4 + data.Length];
        type.CopyTo(crcInput);
        data.CopyTo(crcInput, 4);
        WriteBigEndian(chunk, 8 + data.Length, Crc32(crcInput));

        return chunk;
    }

    private static void WriteBigEndian(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)(value >> 24);
        target[offset + 1] = (byte)(value >> 16);
        target[offset + 2] = (byte)(value >> 8);
        target[offset + 3] = (byte)value;
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];

        for (uint index = 0; index < 256; index++)
        {
            var value = index;

            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320u ^ (value >> 1) : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }

    private static uint Crc32(byte[] input)
    {
        var crc = 0xFFFFFFFFu;

        foreach (var value in input)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
