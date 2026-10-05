using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Chronicleaf.Application.Evolution;

/// <summary>
/// One decoded source picture as eight-bit grey pixels (SCREENSHOT-EVOLUTION-IMAGES-001).
/// </summary>
public sealed class GreyImage
{
    public GreyImage(int width, int height, byte[] pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(pixels);

        if (pixels.Length != width * height)
        {
            throw new ArgumentException("The pixels do not fill the stated dimensions.", nameof(pixels));
        }

        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }
}

/// <summary>
/// One chronological slot of an evolution canvas (SCREENSHOT-EVOLUTION-IMAGES-001).
/// </summary>
/// <remarks>
/// A slot is either a depicted source picture or a gap. A gap is a milestone in range with no
/// selected screenshot: it occupies a neutral band of standard size and is labelled unavailable
/// in the manifest, never interpolated from its neighbours.
/// </remarks>
public sealed class EvolutionSlot
{
    private EvolutionSlot(GreyImage? image) => Image = image;

    /// <summary>A depicted source picture, placed at its native size.</summary>
    public static EvolutionSlot Depicted(GreyImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return new EvolutionSlot(image);
    }

    /// <summary>A milestone with no selected screenshot: a neutral band, not a picture.</summary>
    public static EvolutionSlot Gap() => new(null);

    public GreyImage? Image { get; }

    public bool IsGap => Image is null;
}

/// <summary>
/// The rendered canvas: exact bytes, dimensions and digest (SCREENSHOT-EVOLUTION-IMAGES-001/002).
/// </summary>
public sealed record RenderedEvolution(byte[] Png, int Width, int Height, string Sha256);

/// <summary>
/// Decodes source PNGs and composes the comparison canvas with BCL code only
/// (SCREENSHOT-EVOLUTION-IMAGES-001).
/// </summary>
/// <remarks>
/// <para>
/// <b>No image library, no resampling, no invention.</b> Sources are decoded to grey pixels -
/// colour types 0, 2 and 6 at eight bits, which is what the capture package's PNG fixture and
/// any conformant browser capture produce - and placed at their native size centered on neutral
/// padding. A canvas that would exceed the pixel or byte ceiling is refused before anything is
/// stored, rather than rendered as a usable partial image.
/// </para>
/// <para>
/// The encoder writes the same structural shape the media package validates: signature, an
/// <c>IHDR</c> carrying real dimensions, a zlib <c>IDAT</c> and an empty <c>IEND</c>, with
/// computed CRCs - so the artifact is a genuine PNG the delivered store accepts, not a body that
/// merely begins like one.
/// </para>
/// </remarks>
public static class EvolutionPng
{
    /// <summary>The neutral padding around a picture smaller than its cell.</summary>
    public const byte PaddingGrey = 0x80;

    /// <summary>The fill of a gap band: distinct from padding, so unavailable is visible.</summary>
    public const byte GapGrey = 0x40;

    private static ReadOnlySpan<byte> Signature =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// Decodes PNG bytes to grey pixels. Returns false for bytes that are not a supported PNG.
    /// </summary>
    public static bool TryDecodeToGrey(ReadOnlySpan<byte> png, out GreyImage? image)
    {
        image = null;

        if (png.Length < 8 || !png[..8].SequenceEqual(Signature))
        {
            return false;
        }

        var offset = 8;
        var width = 0;
        var height = 0;
        var bitDepth = 0;
        var colorType = -1;
        var interlace = -1;
        var haveHeader = false;
        using var idat = new MemoryStream();

        var chunks = 0;
        while (true)
        {
            if (chunks++ > 4096)
            {
                return false;
            }

            if (offset + 8 > png.Length)
            {
                return false;
            }

            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png[offset..]);
            if (length < 0 || offset + 12 + length > png.Length)
            {
                return false;
            }

            var type = png.Slice(offset + 4, 4);
            var data = png.Slice(offset + 8, length);

            if (type.SequenceEqual("IHDR"u8))
            {
                if (haveHeader || data.Length != 13)
                {
                    return false;
                }

                haveHeader = true;
                var w = BinaryPrimitives.ReadUInt32BigEndian(data[..4]);
                var h = BinaryPrimitives.ReadUInt32BigEndian(data[4..8]);
                bitDepth = data[8];
                colorType = data[9];
                interlace = data[12];

                if (w == 0 || h == 0 || w > int.MaxValue || h > int.MaxValue)
                {
                    return false;
                }

                width = (int)w;
                height = (int)h;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                if (!haveHeader)
                {
                    return false;
                }

                idat.Write(data);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                if (data.Length != 0)
                {
                    return false;
                }

                break;
            }

            offset += 12 + length;
        }

        if (!haveHeader || bitDepth != 8 || interlace != 0)
        {
            return false;
        }

        var channels = colorType switch
        {
            0 => 1,
            2 => 3,
            6 => 4,
            _ => 0,
        };

        if (channels == 0)
        {
            return false;
        }

        byte[] raw;
        try
        {
            raw = InflateBounded(idat.ToArray(), width, height, channels);
        }
        catch (InvalidDataException)
        {
            return false;
        }

        var stride = width * channels;
        if (raw.Length != height * (stride + 1))
        {
            return false;
        }

        var grey = new byte[width * height];
        var previous = new byte[stride];
        var current = new byte[stride];

        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            Buffer.BlockCopy(raw, y * (stride + 1) + 1, current, 0, stride);

            if (filter > 4)
            {
                return false;
            }

            Unfilter(filter, current, previous, channels);

            for (var x = 0; x < width; x++)
            {
                grey[y * width + x] = colorType switch
                {
                    0 => current[x],
                    2 => (byte)((current[3 * x] + current[3 * x + 1] + current[3 * x + 2]) / 3),
                    _ => (byte)((current[4 * x] + current[4 * x + 1] + current[4 * x + 2]) / 3),
                };
            }

            (previous, current) = (current, previous);
        }

        image = new GreyImage(width, height, grey);
        return true;
    }

    /// <summary>
    /// Composes the ordered slots into one canvas for the named layout.
    /// </summary>
    /// <exception cref="EvolutionTooLargeException">
    /// The canvas exceeds the pixel or byte ceiling (413). Nothing is stored.
    /// </exception>
    public static RenderedEvolution Compose(IReadOnlyList<EvolutionSlot> slots, string layout)
    {
        ArgumentNullException.ThrowIfNull(slots);

        if (slots.Count == 0)
        {
            throw new ArgumentException("An evolution compares at least one slot.", nameof(slots));
        }

        var depicted = slots.Where(slot => !slot.IsGap).Select(slot => slot.Image!).ToList();
        var cellWidth = depicted.Max(image => image.Width);
        var cellHeight = depicted.Max(image => image.Height);

        int columns;
        int rows;
        if (string.Equals(layout, Core.EvolutionLayouts.VerticalLong, StringComparison.Ordinal))
        {
            columns = 1;
            rows = slots.Count;
        }
        else if (string.Equals(layout, Core.EvolutionLayouts.Gallery, StringComparison.Ordinal))
        {
            columns = 2;
            rows = (slots.Count + 1) / 2;
        }
        else
        {
            columns = slots.Count;
            rows = 1;
        }

        var canvasWidth = columns * cellWidth;
        var canvasHeight = rows * cellHeight;
        var pixels = (long)canvasWidth * canvasHeight;

        if (pixels > Core.EvolutionImageLimits.MaxPixelCount)
        {
            throw new EvolutionTooLargeException();
        }

        var canvas = new byte[canvasWidth * canvasHeight];
        Array.Fill(canvas, PaddingGrey);

        for (var index = 0; index < slots.Count; index++)
        {
            var column = layout is var l && string.Equals(l, Core.EvolutionLayouts.VerticalLong, StringComparison.Ordinal)
                ? 0
                : string.Equals(l, Core.EvolutionLayouts.Gallery, StringComparison.Ordinal)
                    ? index % 2
                    : index;
            var row = string.Equals(layout, Core.EvolutionLayouts.VerticalLong, StringComparison.Ordinal)
                ? index
                : string.Equals(layout, Core.EvolutionLayouts.Gallery, StringComparison.Ordinal)
                    ? index / 2
                    : 0;

            var originX = column * cellWidth;
            var originY = row * cellHeight;
            var slot = slots[index];

            if (slot.IsGap)
            {
                Fill(canvas, canvasWidth, originX, originY, cellWidth, cellHeight, GapGrey);
                continue;
            }

            var picture = slot.Image!;
            var offsetX = originX + ((cellWidth - picture.Width) / 2);
            var offsetY = originY + ((cellHeight - picture.Height) / 2);

            for (var y = 0; y < picture.Height; y++)
            {
                Buffer.BlockCopy(
                    picture.Pixels, y * picture.Width,
                    canvas, (offsetY + y) * canvasWidth + offsetX,
                    picture.Width);
            }
        }

        var png = EncodeGrey(canvasWidth, canvasHeight, canvas);

        if (png.LongLength > Core.EvolutionImageLimits.MaxByteLength)
        {
            throw new EvolutionTooLargeException();
        }

        return new RenderedEvolution(
            png,
            canvasWidth,
            canvasHeight,
            Convert.ToHexString(SHA256.HashData(png)).ToLowerInvariant());
    }

    private static void Fill(byte[] canvas, int canvasWidth, int x, int y, int w, int h, byte value)
    {
        for (var row = 0; row < h; row++)
        {
            Array.Fill(canvas, value, (y + row) * canvasWidth + x, w);
        }
    }

    private static byte[] InflateBounded(byte[] deflated, int width, int height, int channels)
    {
        // The bound is exact for a well-formed file: one filter byte per row plus the samples.
        // Anything larger is not this picture, and allocating it would let a hostile length do
        // the allocating.
        var expected = (long)height * (width * channels + 1);
        if (expected > 256L * 1024 * 1024)
        {
            throw new InvalidDataException("The decoded image is larger than the decode bound.");
        }

        using var input = new MemoryStream(deflated, writable: false);
        using var inflate = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();

        inflate.CopyTo(output);

        var raw = output.ToArray();
        if (raw.LongLength != expected)
        {
            throw new InvalidDataException("The decoded samples do not fill the stated dimensions.");
        }

        return raw;
    }

    private static void Unfilter(int filter, byte[] current, byte[] previous, int channels)
    {
        switch (filter)
        {
            case 0:
                break;
            case 1:
                for (var i = channels; i < current.Length; i++)
                {
                    current[i] = (byte)(current[i] + current[i - channels]);
                }

                break;
            case 2:
                for (var i = 0; i < current.Length; i++)
                {
                    current[i] = (byte)(current[i] + previous[i]);
                }

                break;
            case 3:
                for (var i = 0; i < current.Length; i++)
                {
                    var left = i >= channels ? current[i - channels] : 0;
                    current[i] = (byte)(current[i] + ((left + previous[i]) / 2));
                }

                break;
            case 4:
                for (var i = 0; i < current.Length; i++)
                {
                    var left = i >= channels ? current[i - channels] : 0;
                    var up = previous[i];
                    var upperLeft = i >= channels ? previous[i - channels] : 0;
                    current[i] = (byte)(current[i] + Paeth(left, up, upperLeft));
                }

                break;
        }
    }

    private static int Paeth(int left, int up, int upperLeft)
    {
        var estimate = left + up - upperLeft;
        var distanceLeft = Math.Abs(estimate - left);
        var distanceUp = Math.Abs(estimate - up);
        var distanceUpperLeft = Math.Abs(estimate - upperLeft);

        if (distanceLeft <= distanceUp && distanceLeft <= distanceUpperLeft)
        {
            return left;
        }

        return distanceUp <= distanceUpperLeft ? up : upperLeft;
    }

    private static byte[] EncodeGrey(int width, int height, byte[] grey)
    {
        using var file = new MemoryStream();
        file.Write(Signature);

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = 8;
        header[9] = 0;
        file.Write(Chunk("IHDR"u8, header));

        byte[] framed;
        using (var rows = new MemoryStream(height * (width + 1)))
        {
            for (var y = 0; y < height; y++)
            {
                rows.WriteByte(0);
                rows.Write(grey.AsSpan(y * width, width));
            }

            framed = rows.ToArray();
        }

        using (var compressed = new MemoryStream())
        {
            using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(framed, 0, framed.Length);
            }

            file.Write(Chunk("IDAT"u8, compressed.ToArray()));
        }

        file.Write(Chunk("IEND"u8, []));
        return file.ToArray();
    }

    private static byte[] Chunk(ReadOnlySpan<byte> type, byte[] data)
    {
        var chunk = new byte[12 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(chunk, (uint)data.Length);
        type.CopyTo(chunk.AsSpan(4));
        data.CopyTo(chunk, 8);

        var crcInput = new byte[4 + data.Length];
        type.CopyTo(crcInput);
        data.CopyTo(crcInput, 4);
        BinaryPrimitives.WriteUInt32BigEndian(chunk.AsSpan(8 + data.Length), Crc32(crcInput));

        return chunk;
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
