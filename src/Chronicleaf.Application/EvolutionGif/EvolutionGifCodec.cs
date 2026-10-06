using System.Buffers.Binary;
using Chronicleaf.Application.Evolution;
using Chronicleaf.Core;

namespace Chronicleaf.Application.EvolutionGifs;

/// <summary>
/// The deterministic GIF89a encoder and first-frame reader for reviewed chronological GIFs
/// (EVOLUTION-GIF-EXPORT-001).
/// </summary>
/// <remarks>
/// <para>
/// <b>BCL only, byte-identical rebuilds.</b> The palette is the fixed 256-entry grey ramp, so a
/// pixel's palette index <i>is</i> its intensity and no quantisation decision can drift between
/// runs. The LZW table resets on the same full-table boundary every time, sub-blocks are packed
/// maximally, and no timestamp, comment or application extension beyond the loop switch is ever
/// written. The same frames, durations and loop flag therefore encode to the same bytes, which
/// is what lets the service promise that a re-render of an unchanged selection hashes
/// identically.
/// </para>
/// <para>
/// <b>One canvas for every frame.</b> The canvas is the maximum frame width by the maximum
/// frame height, and each frame sits centered on the same neutral padding the PNG comparison
/// uses - so a narrower source is visibly narrower rather than stretched, and the manifest's
/// dimensions describe every frame at once.
/// </para>
/// </remarks>
public static class EvolutionGifCodec
{
    private const int MinCodeSize = 8;
    private const int ClearCode = 1 << MinCodeSize;
    private const int EndCode = ClearCode + 1;
    private const int FirstCode = ClearCode + 2;
    private const int MaxTableSize = 4096;

    private static ReadOnlySpan<byte> Gif89a => "GIF89a"u8;
    private static ReadOnlySpan<byte> NetscapeLabel => "NETSCAPE2.0"u8;

    /// <summary>
    /// Encodes the frames as one deterministic GIF89a animation.
    /// </summary>
    /// <remarks>
    /// Every frame is depicted: there is no gap band in an animation, so a caller with a
    /// missing picture fails before calling. Durations are whole centiseconds - the service
    /// refuses millisecond remainders with a 422 before this method ever sees them.
    /// </remarks>
    /// <exception cref="EvolutionGifTooLargeException">The canvas or the encoded bytes exceed the ceiling.</exception>
    public static byte[] Encode(
        IReadOnlyList<GreyImage> frames,
        IReadOnlyList<int> durationsMs,
        bool loop,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(durationsMs);

        if (frames.Count < EvolutionGifLimits.MinFrames || frames.Count > EvolutionGifLimits.MaxFrames)
        {
            throw new ArgumentException(
                "An evolution GIF animates two to twelve frames.", nameof(frames));
        }

        if (durationsMs.Count != frames.Count)
        {
            throw new ArgumentException(
                "Every frame carries its own duration.", nameof(durationsMs));
        }

        var canvasWidth = 0;
        var canvasHeight = 0;

        for (var index = 0; index < frames.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var frame = frames[index];
            ArgumentNullException.ThrowIfNull(frame, nameof(frames));

            if (frame.Width <= 0 || frame.Height <= 0 ||
                frame.Width > EvolutionGifLimits.MaxDimension ||
                frame.Height > EvolutionGifLimits.MaxDimension)
            {
                throw new EvolutionGifTooLargeException();
            }

            if (frame.Pixels.Length != frame.Width * frame.Height)
            {
                throw new ArgumentException("A frame's pixels fill its dimensions.", nameof(frames));
            }

            var duration = durationsMs[index];
            if (duration < EvolutionGifLimits.MinFrameDurationMs ||
                duration > EvolutionGifLimits.MaxFrameDurationMs ||
                duration % 10 != 0)
            {
                throw new ArgumentException("Every duration is whole centiseconds within bounds.", nameof(durationsMs));
            }

            canvasWidth = Math.Max(canvasWidth, frame.Width);
            canvasHeight = Math.Max(canvasHeight, frame.Height);
        }

        var totalMs = durationsMs.Sum();
        if (totalMs > EvolutionGifLimits.MaxTotalDurationMs)
        {
            throw new ArgumentException("The total playback fits sixty seconds.", nameof(durationsMs));
        }

        using var gif = new MemoryStream();
        gif.Write(Gif89a);

        var screen = new byte[7];
        BinaryPrimitives.WriteUInt16LittleEndian(screen.AsSpan(0, 2), (ushort)canvasWidth);
        BinaryPrimitives.WriteUInt16LittleEndian(screen.AsSpan(2, 2), (ushort)canvasHeight);
        screen[4] = 0xF7;
        screen[5] = EvolutionPng.PaddingGrey;
        screen[6] = 0;
        gif.Write(screen);

        for (var shade = 0; shade < 256; shade++)
        {
            gif.WriteByte((byte)shade);
            gif.WriteByte((byte)shade);
            gif.WriteByte((byte)shade);
        }

        if (loop)
        {
            gif.WriteByte(0x21);
            gif.WriteByte(0xFF);
            gif.WriteByte(0x0B);
            gif.Write(NetscapeLabel);
            gif.WriteByte(0x03);
            gif.WriteByte(0x01);
            gif.Write([0x00, 0x00]);
            gif.WriteByte(0x00);
        }

        for (var index = 0; index < frames.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var delayCs = durationsMs[index] / 10;

            gif.WriteByte(0x21);
            gif.WriteByte(0xF9);
            gif.WriteByte(0x04);
            gif.WriteByte(0x04);
            gif.WriteByte((byte)(delayCs & 0xFF));
            gif.WriteByte((byte)((delayCs >> 8) & 0xFF));
            gif.WriteByte(0x00);
            gif.WriteByte(0x00);

            gif.WriteByte(0x2C);
            var descriptor = new byte[9];
            BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(0, 2), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(2, 2), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(4, 2), (ushort)canvasWidth);
            BinaryPrimitives.WriteUInt16LittleEndian(descriptor.AsSpan(6, 2), (ushort)canvasHeight);
            descriptor[8] = 0x00;
            gif.Write(descriptor);

            gif.WriteByte(MinCodeSize);
            WriteDataSubBlocks(gif, Compress(Center(frames[index], canvasWidth, canvasHeight)));

            if (gif.Length > EvolutionGifLimits.MaxByteLength)
            {
                throw new EvolutionGifTooLargeException();
            }
        }

        gif.WriteByte(0x3B);

        if (gif.Length > EvolutionGifLimits.MaxByteLength)
        {
            throw new EvolutionGifTooLargeException();
        }

        return gif.ToArray();
    }

    /// <summary>
    /// Reads the first frame of GIF bytes back to grey pixels, beside the canvas dimensions.
    /// </summary>
    /// <remarks>
    /// This is what the static preview is derived from: the exact first frame the stored
    /// animation starts with, rather than a second rendering that could drift from it. Returns
    /// false for bytes that are not a GIF this encoder's shape describes.
    /// </remarks>
    public static bool TryDecodeFirstFrame(
        ReadOnlySpan<byte> gif,
        out GreyImage? frame,
        out int width,
        out int height)
    {
        frame = null;
        width = 0;
        height = 0;

        if (gif.Length < 13 || !gif[..6].SequenceEqual(Gif89a))
        {
            return false;
        }

        var canvasWidth = BinaryPrimitives.ReadUInt16LittleEndian(gif.Slice(6, 2));
        var canvasHeight = BinaryPrimitives.ReadUInt16LittleEndian(gif.Slice(8, 2));
        var packed = gif[10];

        if (canvasWidth == 0 || canvasHeight == 0)
        {
            return false;
        }

        var offset = 13;
        if ((packed & 0x80) != 0)
        {
            offset += 3 * (1 << ((packed & 0x07) + 1));
        }

        while (true)
        {
            if (offset >= gif.Length)
            {
                return false;
            }

            var separator = gif[offset++];
            if (separator == 0x3B)
            {
                return frame is not null;
            }

            if (separator == 0x21)
            {
                if (!SkipExtension(gif, ref offset))
                {
                    return false;
                }

                continue;
            }

            if (separator != 0x2C)
            {
                return false;
            }

            if (offset + 9 > gif.Length)
            {
                return false;
            }

            var left = BinaryPrimitives.ReadUInt16LittleEndian(gif.Slice(offset, 2));
            var top = BinaryPrimitives.ReadUInt16LittleEndian(gif.Slice(offset + 2, 2));
            var frameWidth = BinaryPrimitives.ReadUInt16LittleEndian(gif.Slice(offset + 4, 2));
            var frameHeight = BinaryPrimitives.ReadUInt16LittleEndian(gif.Slice(offset + 6, 2));
            var framePacked = gif[offset + 8];
            offset += 9;

            if ((framePacked & 0x80) != 0)
            {
                offset += 3 * (1 << ((framePacked & 0x07) + 1));
            }

            if (offset >= gif.Length)
            {
                return false;
            }

            var minCodeSize = gif[offset++];
            if (minCodeSize > 8)
            {
                return false;
            }

            if (!ReadDataSubBlocks(gif, ref offset, out var compressed))
            {
                return false;
            }

            if (frame is not null)
            {
                continue;
            }

            if (!Decompress(compressed, minCodeSize, frameWidth * frameHeight, out var indices))
            {
                return false;
            }

            if (left + frameWidth > canvasWidth || top + frameHeight > canvasHeight)
            {
                return false;
            }

            var canvas = new byte[canvasWidth * canvasHeight];
            Array.Fill(canvas, EvolutionPng.PaddingGrey);
            for (var row = 0; row < frameHeight; row++)
            {
                Array.Copy(
                    indices, row * frameWidth,
                    canvas, (top + row) * canvasWidth + left,
                    frameWidth);
            }

            frame = new GreyImage(canvasWidth, canvasHeight, canvas);
            width = canvasWidth;
            height = canvasHeight;
        }
    }

    /// <summary>
    /// Encodes grey pixels as a greyscale PNG: the static preview's bytes.
    /// </summary>
    public static byte[] EncodeGreyPng(GreyImage frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        using var file = new MemoryStream();
        file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), (uint)frame.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), (uint)frame.Height);
        header[8] = 8;
        header[9] = 0;
        header[10] = 0;
        header[11] = 0;
        header[12] = 0;
        file.Write(Chunk("IHDR"u8, header));

        var raw = new byte[frame.Height * (frame.Width + 1)];
        for (var row = 0; row < frame.Height; row++)
        {
            raw[row * (frame.Width + 1)] = 0;
            Array.Copy(frame.Pixels, row * frame.Width, raw, row * (frame.Width + 1) + 1, frame.Width);
        }

        file.Write(Chunk("IDAT"u8, CompressZlib(raw)));
        file.Write(Chunk("IEND"u8, []));

        return file.ToArray();
    }

    private static byte[] Center(GreyImage frame, int canvasWidth, int canvasHeight)
    {
        var left = (canvasWidth - frame.Width) / 2;
        var top = (canvasHeight - frame.Height) / 2;

        var canvas = new byte[canvasWidth * canvasHeight];
        Array.Fill(canvas, EvolutionPng.PaddingGrey);

        for (var row = 0; row < frame.Height; row++)
        {
            Array.Copy(
                frame.Pixels, row * frame.Width,
                canvas, (top + row) * canvasWidth + left,
                frame.Width);
        }

        return canvas;
    }

    private static byte[] Compress(byte[] indices)
    {
        var table = new Dictionary<(int Prefix, int Suffix), int>(4096);
        var output = new List<byte>(indices.Length);
        var buffer = 0;
        var buffered = 0;

        void Emit(int code, int size)
        {
            buffer |= code << buffered;
            buffered += size;
            while (buffered >= 8)
            {
                output.Add((byte)(buffer & 0xFF));
                buffer >>= 8;
                buffered -= 8;
            }
        }

        var codeSize = MinCodeSize + 1;
        var next = FirstCode;
        Emit(ClearCode, codeSize);

        var prefix = (int)indices[0];
        for (var at = 1; at < indices.Length; at++)
        {
            var suffix = (int)indices[at];
            if (table.TryGetValue((prefix, suffix), out var extended))
            {
                prefix = extended;
                continue;
            }

            Emit(prefix, codeSize);

            if (next < MaxTableSize)
            {
                table[(prefix, suffix)] = next++;
                // The encoder widens its codes the moment an entry no longer fits the current
                // width: the decoder learns every entry a read later, so it widens one entry
                // earlier (at 2^size), and the two widths stay matched step for step.
                if (next == (1 << codeSize) + 1 && codeSize < 12)
                {
                    codeSize++;
                }
            }
            else
            {
                Emit(ClearCode, codeSize);
                table.Clear();
                codeSize = MinCodeSize + 1;
                next = FirstCode;
            }

            prefix = suffix;
        }

        Emit(prefix, codeSize);
        Emit(EndCode, codeSize);
        if (buffered > 0)
        {
            output.Add((byte)(buffer & 0xFF));
        }

        return [.. output];
    }

    private static bool Decompress(byte[] compressed, int minCodeSize, int expected, out byte[] indices)
    {
        indices = [];
        var prefixes = new int[MaxTableSize];
        var suffixes = new byte[MaxTableSize];
        for (var code = 0; code < ClearCodeValue(minCodeSize); code++)
        {
            suffixes[code] = (byte)code;
        }

        var clear = 1 << minCodeSize;
        var end = clear + 1;
        var next = end + 1;
        var codeSize = minCodeSize + 1;

        var result = new List<byte>(expected);
        var bit = 0;
        int ReadCode()
        {
            var value = 0;
            for (var shift = 0; shift < codeSize; shift++)
            {
                var byteIndex = bit >> 3;
                if (byteIndex >= compressed.Length)
                {
                    return -1;
                }

                value |= ((compressed[byteIndex] >> (bit & 7)) & 1) << shift;
                bit++;
            }

            return value;
        }

        var previous = -1;
        var first = (byte)0;
        var stack = new byte[MaxTableSize + 1];

        while (true)
        {
            var code = ReadCode();
            if (code < 0)
            {
                return false;
            }

            if (code == clear)
            {
                next = end + 1;
                codeSize = minCodeSize + 1;
                previous = -1;
                continue;
            }

            if (code == end)
            {
                break;
            }

            var current = code;
            var top = 0;
            if (code >= next)
            {
                if (previous < 0)
                {
                    return false;
                }

                stack[top++] = first;
                current = previous;
            }

            while (current >= clear)
            {
                if (current >= next || top >= stack.Length)
                {
                    return false;
                }

                stack[top++] = suffixes[current];
                current = prefixes[current];
            }

            first = suffixes[current];
            stack[top++] = first;

            while (top > 0)
            {
                result.Add(stack[--top]);
                if (result.Count > expected)
                {
                    return false;
                }
            }

            if (previous >= 0 && next < MaxTableSize)
            {
                prefixes[next] = previous;
                suffixes[next] = first;
                next++;
                // The decoder trails the encoder by one table entry: it learns each entry a
                // read later than the encoder stored it. It therefore widens its codes one
                // entry earlier - when next reaches 2^size rather than 2^size + 1 - so the
                // width of every read still matches the width of the corresponding emission.
                if (next == (1 << codeSize) && codeSize < 12)
                {
                    codeSize++;
                }
            }

            previous = code;
        }

        if (result.Count != expected)
        {
            return false;
        }

        indices = [.. result];
        return true;
    }

    private static int ClearCodeValue(int minCodeSize) => 1 << minCodeSize;

    private static bool SkipExtension(ReadOnlySpan<byte> gif, ref int offset)
    {
        if (offset >= gif.Length)
        {
            return false;
        }

        offset++;

        while (true)
        {
            if (offset >= gif.Length)
            {
                return false;
            }

            var size = gif[offset++];
            if (size == 0)
            {
                return true;
            }

            if (offset + size > gif.Length)
            {
                return false;
            }

            offset += size;
        }
    }

    private static bool ReadDataSubBlocks(ReadOnlySpan<byte> gif, ref int offset, out byte[] data)
    {
        data = [];
        using var blocks = new MemoryStream();

        while (true)
        {
            if (offset >= gif.Length)
            {
                return false;
            }

            var size = gif[offset++];
            if (size == 0)
            {
                data = blocks.ToArray();
                return true;
            }

            if (offset + size > gif.Length)
            {
                return false;
            }

            blocks.Write(gif.Slice(offset, size));
            offset += size;
        }
    }

    private static void WriteDataSubBlocks(Stream gif, byte[] data)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            var size = Math.Min(255, data.Length - offset);
            gif.WriteByte((byte)size);
            gif.Write(data, offset, size);
            offset += size;
        }

        gif.WriteByte(0x00);
    }

    private static byte[] CompressZlib(byte[] raw)
    {
        using var body = new MemoryStream();
        body.WriteByte(0x78);
        body.WriteByte(0x01);

        using (var deflate = new System.IO.Compression.DeflateStream(body, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw, 0, raw.Length);
        }

        var adler = 1u;
        const uint prime = 65521;
        var low = 1u;
        var high = 0u;
        foreach (var octet in raw)
        {
            low = (low + octet) % prime;
            high = (high + low) % prime;
        }

        adler = (high << 16) | low;
        var trailer = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(trailer, adler);
        body.Write(trailer);

        return body.ToArray();
    }

    private static byte[] Chunk(ReadOnlySpan<byte> type, byte[] data)
    {
        using var chunk = new MemoryStream();
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        chunk.Write(length);
        chunk.Write(type);
        chunk.Write(data);

        var crc = Crc32([.. type.ToArray(), .. data]);
        var digest = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(digest, crc);
        chunk.Write(digest);

        return chunk.ToArray();
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (var octet = 0; octet < 256; octet++)
        {
            var entry = (uint)octet;
            for (var bit = 0; bit < 8; bit++)
            {
                entry = (entry & 1) == 1 ? 0xEDB88320u ^ (entry >> 1) : entry >> 1;
            }

            table[octet] = entry;
        }

        return table;
    }

    private static uint Crc32(byte[] input)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var octet in input)
        {
            crc = CrcTable[(crc ^ octet) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }
}
