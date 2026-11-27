using System.Buffers.Binary;

namespace Aether;

internal sealed class GifAnimation : IDisposable
{
    private readonly GifFrameDescriptor[] _frames;
    private readonly byte[]? _globalPalette;

    private GifAnimation(int width, int height, byte[]? globalPalette,
        GifFrameDescriptor[] frames, int repeatCount)
    {
        Width = width;
        Height = height;
        _globalPalette = globalPalette;
        _frames = frames;
        RepeatCount = repeatCount;
    }

    public int Width { get; }
    public int Height { get; }
    public int FrameCount => _frames.Length;

    /// <summary>Number of complete plays, or -1 for an infinite animation.</summary>
    public int RepeatCount { get; }

    public static GifAnimation Load(string path)
    {
        if (new FileInfo(path).Length > 64L * 1024 * 1024)
            throw new InvalidDataException("GIF files larger than 64 MiB are not supported by the native renderer.");
        byte[] data = File.ReadAllBytes(path);
        bool gif87a = data.Length >= 6 && System.Text.Encoding.ASCII.GetString(data, 0, 6) == "GIF87a";
        bool gif89a = data.Length >= 6 && System.Text.Encoding.ASCII.GetString(data, 0, 6) == "GIF89a";
        if (data.Length < 13 || (!gif87a && !gif89a))
            throw new InvalidDataException("The image is not a supported GIF file.");

        int width = ReadUInt16(data, 6);
        int height = ReadUInt16(data, 8);
        if (width <= 0 || height <= 0 || checked((long)width * height) > 16_777_216)
            throw new InvalidDataException("GIF dimensions exceed the native renderer limit.");

        byte packed = data[10];
        int offset = 13;
        byte[]? globalPalette = null;
        if ((packed & 0x80) != 0)
        {
            int entries = 1 << ((packed & 0x07) + 1);
            globalPalette = ReadPalette(data, ref offset, entries);
        }

        var frames = new List<GifFrameDescriptor>();
        int repeatCount = 1;
        GifGraphicControl graphicControl = default;
        while (offset < data.Length)
        {
            byte block = data[offset++];
            switch (block)
            {
                case 0x3B:
                    offset = data.Length;
                    break;
                case 0x21:
                    ParseExtension(data, ref offset, ref graphicControl, ref repeatCount);
                    break;
                case 0x2C:
                    frames.Add(ParseFrame(data, ref offset, graphicControl));
                    graphicControl = default;
                    if (frames.Count > 1024)
                        throw new InvalidDataException("GIF contains too many frames.");
                    break;
                case 0x00:
                    break;
                default:
                    throw new InvalidDataException($"GIF contains an invalid block 0x{block:X2}.");
            }
        }

        if (frames.Count == 0)
            throw new InvalidDataException("GIF does not contain an image frame.");
        return new GifAnimation(width, height, globalPalette, frames.ToArray(), repeatCount);
    }

    public GifFrame DecodeFrame(int index)
    {
        if ((uint)index >= (uint)_frames.Length)
            throw new ArgumentOutOfRangeException(nameof(index));

        GifFrameDescriptor descriptor = _frames[index];
        byte[] indices = LzwDecode(descriptor.Compressed,
            descriptor.MinimumCodeSize, checked(descriptor.Width * descriptor.Height));
        byte[] palette = descriptor.LocalPalette ?? _globalPalette
            ?? throw new InvalidDataException("GIF frame has no color table.");
        byte[] pixels = new byte[checked(descriptor.Width * descriptor.Height * 4)];
        for (int sourceY = 0; sourceY < descriptor.Height; sourceY++)
        {
            int destinationY = descriptor.Interlaced ? GetInterlacedRow(sourceY, descriptor.Height) : sourceY;
            for (int x = 0; x < descriptor.Width; x++)
            {
                byte paletteIndex = indices[sourceY * descriptor.Width + x];
                int destination = (destinationY * descriptor.Width + x) * 4;
                if (descriptor.Transparent && paletteIndex == descriptor.TransparentIndex)
                    continue;

                int paletteOffset = paletteIndex * 3;
                if (paletteOffset + 2 >= palette.Length)
                    throw new InvalidDataException("GIF frame references an invalid palette entry.");
                pixels[destination] = palette[paletteOffset + 2];
                pixels[destination + 1] = palette[paletteOffset + 1];
                pixels[destination + 2] = palette[paletteOffset];
                pixels[destination + 3] = 255;
            }
        }

        return new GifFrame(
            new PixelRect(descriptor.Left, descriptor.Top, descriptor.Width, descriptor.Height),
            pixels,
            checked(descriptor.Width * 4),
            descriptor.Disposal,
            descriptor.DelayMilliseconds);
    }

    public void Dispose()
    {
        // The byte array is owned by the animation and becomes collectible with it.
    }

    private static GifFrameDescriptor ParseFrame(byte[] data, ref int offset, GifGraphicControl control)
    {
        Require(data, offset, 9);
        int left = ReadUInt16(data, offset);
        int top = ReadUInt16(data, offset + 2);
        int width = ReadUInt16(data, offset + 4);
        int height = ReadUInt16(data, offset + 6);
        byte packed = data[offset + 8];
        offset += 9;
        if (width <= 0 || height <= 0 || checked((long)width * height) > 16_777_216)
            throw new InvalidDataException("GIF frame dimensions exceed the native renderer limit.");

        byte[]? localPalette = null;
        if ((packed & 0x80) != 0)
        {
            int entries = 1 << ((packed & 0x07) + 1);
            localPalette = ReadPalette(data, ref offset, entries);
        }

        Require(data, offset, 1);
        byte minimumCodeSize = data[offset++];
        if (minimumCodeSize is < 2 or > 8)
            throw new InvalidDataException("GIF LZW minimum code size is invalid.");

        byte[] compressed = ReadSubBlocks(data, ref offset);

        return new GifFrameDescriptor(
            left,
            top,
            width,
            height,
            (packed & 0x40) != 0,
            localPalette,
            minimumCodeSize,
            compressed,
            control.Disposal,
            control.DelayMilliseconds > 0 ? control.DelayMilliseconds : 100,
            control.Transparent,
            control.TransparentIndex);
    }

    private static void ParseExtension(byte[] data, ref int offset, ref GifGraphicControl control, ref int repeatCount)
    {
        Require(data, offset, 1);
        byte label = data[offset++];
        switch (label)
        {
            case 0xF9:
                Require(data, offset, 5);
                if (data[offset++] != 4)
                    throw new InvalidDataException("GIF graphic control extension has an invalid size.");
                byte packed = data[offset++];
                int delay = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
                offset += 2;
                byte transparentIndex = data[offset++];
                Require(data, offset, 1);
                if (data[offset++] != 0)
                    throw new InvalidDataException("GIF graphic control extension is malformed.");
                control = new GifGraphicControl(
                    (GifDisposalMethod)((packed >> 2) & 0x07),
                    Math.Max(10, delay * 10L),
                    (packed & 1) != 0,
                    transparentIndex);
                break;
            case 0xFF:
                Require(data, offset, 1);
                int applicationLength = data[offset++];
                Require(data, offset, applicationLength);
                string application = System.Text.Encoding.ASCII.GetString(data, offset, applicationLength);
                offset += applicationLength;
                byte[] extensionData = ReadSubBlocks(data, ref offset);
                if (application.StartsWith("NETSCAPE", StringComparison.OrdinalIgnoreCase) && extensionData.Length >= 3 && extensionData[0] == 1)
                {
                    int loop = BinaryPrimitives.ReadUInt16LittleEndian(extensionData.AsSpan(1, 2));
                    repeatCount = loop == 0 ? -1 : checked(loop + 1);
                }
                break;
            default:
                _ = ReadSubBlocks(data, ref offset);
                break;
        }
    }

    private static byte[] ReadSubBlocks(byte[] data, ref int offset)
    {
        using var stream = new MemoryStream();
        while (true)
        {
            Require(data, offset, 1);
            int length = data[offset++];
            if (length == 0)
                break;
            Require(data, offset, length);
            stream.Write(data, offset, length);
            offset += length;
        }
        return stream.ToArray();
    }

    private static byte[] ReadPalette(byte[] data, ref int offset, int entries)
    {
        int length = checked(entries * 3);
        Require(data, offset, length);
        byte[] palette = data.AsSpan(offset, length).ToArray();
        offset += length;
        return palette;
    }

    private static byte[] LzwDecode(ReadOnlySpan<byte> compressed, int minimumCodeSize, int expectedPixels)
    {
        int clearCode = 1 << minimumCodeSize;
        int endCode = clearCode + 1;
        int codeSize = minimumCodeSize + 1;
        int nextCode = endCode + 1;
        int bitPosition = 0;
        int oldCode = -1;
        byte first = 0;
        int outputCount = 0;
        var output = new byte[expectedPixels];
        var prefix = new short[4096];
        var suffix = new byte[4096];
        var pixelStack = new byte[4097];
        int stackSize = 0;

        while (outputCount < expectedPixels && bitPosition + codeSize <= compressed.Length * 8)
        {
            int code = 0;
            for (int bit = 0; bit < codeSize; bit++)
                code |= ((compressed[bitPosition >> 3] >> (bitPosition & 7)) & 1) << bit;
            bitPosition += codeSize;

            if (code == clearCode)
            {
                codeSize = minimumCodeSize + 1;
                nextCode = endCode + 1;
                oldCode = -1;
                continue;
            }
            if (code == endCode)
                break;
            if (code > 4095)
                throw new InvalidDataException("GIF LZW code is invalid.");

            int currentCode = code;
            if (oldCode == -1)
            {
                if (code >= clearCode)
                    throw new InvalidDataException("GIF LZW stream starts with an invalid code.");
                first = (byte)code;
                output[outputCount++] = first;
                oldCode = code;
                continue;
            }

            if (code >= nextCode)
            {
                if (code != nextCode)
                    throw new InvalidDataException("GIF LZW code references an invalid dictionary entry.");
                pixelStack[stackSize++] = first;
                currentCode = oldCode;
            }
            while (currentCode >= clearCode)
            {
                if (currentCode >= 4096 || stackSize >= pixelStack.Length)
                    throw new InvalidDataException("GIF LZW dictionary is invalid.");
                pixelStack[stackSize++] = suffix[currentCode];
                currentCode = prefix[currentCode];
            }
            first = (byte)currentCode;
            pixelStack[stackSize++] = first;
            while (stackSize > 0 && outputCount < expectedPixels)
                output[outputCount++] = pixelStack[--stackSize];

            if (nextCode < 4096)
            {
                prefix[nextCode] = (short)oldCode;
                suffix[nextCode] = first;
                nextCode++;
                if (nextCode == (1 << codeSize) && codeSize < 12)
                    codeSize++;
            }
            oldCode = code;
        }

        if (outputCount != expectedPixels)
            throw new InvalidDataException("GIF LZW stream ended before the frame was decoded.");
        return output;
    }

    private static int GetInterlacedRow(int sourceRow, int height)
    {
        int row = sourceRow;
        if (row < (height + 7) / 8)
            return row * 8;
        row -= (height + 7) / 8;
        int secondPass = (height + 3) / 4;
        if (row < secondPass)
            return row * 4 + 4;
        row -= secondPass;
        int thirdPass = (height + 1) / 2;
        if (row < thirdPass)
            return row * 2 + 2;
        row -= thirdPass;
        return row * 2 + 1;
    }

    private static int ReadUInt16(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));

    private static void Require(byte[] data, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > data.Length - length)
            throw new InvalidDataException("GIF data is truncated.");
    }

    private readonly record struct GifGraphicControl(
        GifDisposalMethod Disposal,
        long DelayMilliseconds,
        bool Transparent,
        byte TransparentIndex);

    private sealed record GifFrameDescriptor(
        int Left,
        int Top,
        int Width,
        int Height,
        bool Interlaced,
        byte[]? LocalPalette,
        int MinimumCodeSize,
        byte[] Compressed,
        GifDisposalMethod Disposal,
        long DelayMilliseconds,
        bool Transparent,
        byte TransparentIndex);
}
