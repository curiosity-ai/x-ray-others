using System.Buffers.Binary;
using System.IO.Compression;

namespace XRay.OneNote.Internal.Pdf;

/// <summary>
/// An image prepared for a PDF image XObject: either JPEG data passed through as DCTDecode, or
/// 8-bit gray/RGB samples with an optional 8-bit alpha channel (written as an SMask).
/// </summary>
internal sealed class DecodedImage
{
    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>Samples per pixel of <see cref="Pixels"/> or of the JPEG data: 1 (gray), 3 (RGB) or 4 (CMYK, JPEG only).</summary>
    public required int Components { get; init; }

    /// <summary>JPEG data to embed unchanged, when the source was a JPEG.</summary>
    public byte[]? Jpeg { get; init; }

    /// <summary>True for an Adobe CMYK JPEG, whose samples are stored inverted.</summary>
    public bool InvertedCmyk { get; init; }

    /// <summary>Interleaved 8-bit samples, row by row, when the source was not a JPEG.</summary>
    public byte[]? Pixels { get; init; }

    /// <summary>8-bit alpha, one byte per pixel, when the image has transparency.</summary>
    public byte[]? Alpha { get; init; }
}

/// <summary>
/// Pure managed decoders for the picture formats OneNote embeds: PNG, JPEG (metadata only; the data
/// is embedded as is), GIF (first frame) and BMP (uncompressed). Returns null for anything else or
/// for damaged data.
/// </summary>
internal static class ImageDecoder
{
    // Refuse images whose decoded size would be unreasonable for a page.
    private const long MaxPixels = 40_000_000;

    public static DecodedImage? Decode(byte[] data)
    {
        try
        {
            if (data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            {
                return DecodePng(data);
            }

            if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            {
                return DecodeJpeg(data);
            }

            if (data.Length >= 6 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F')
            {
                return DecodeGif(data);
            }

            if (data.Length >= 26 && data[0] == 'B' && data[1] == 'M')
            {
                return DecodeBmp(data);
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IndexOutOfRangeException or ArgumentException
                                              or OverflowException or EndOfStreamException)
        {
            return null;
        }

        return null;
    }

    private static void CheckSize(long width, long height)
    {
        if (width <= 0 || height <= 0 || width * height > MaxPixels)
        {
            throw new InvalidDataException("Unsupported image size");
        }
    }

    // ------------------------------------------------------------------ JPEG

    private static DecodedImage DecodeJpeg(byte[] data)
    {
        var position = 2;
        var adobe = false;
        while (position + 4 <= data.Length)
        {
            if (data[position] != 0xFF)
            {
                position++;
                continue;
            }

            var marker = data[position + 1];
            if (marker is 0xFF)
            {
                position++;
                continue;
            }

            if (marker is 0xD8 or 0x01 or (>= 0xD0 and <= 0xD7))
            {
                position += 2;
                continue;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position + 2));
            if (length < 2 || position + 2 + length > data.Length)
            {
                break;
            }

            if (marker == 0xEE && length >= 14 && data.AsSpan(position + 4, 5).SequenceEqual("Adobe"u8))
            {
                adobe = true;
            }

            // SOF0-SOF15 except DHT (C4), JPG (C8) and DAC (CC).
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                var height = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position + 5));
                var width = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position + 7));
                var components = data[position + 9];
                CheckSize(width, height);
                if (components is not (1 or 3 or 4))
                {
                    throw new InvalidDataException("Unsupported JPEG component count");
                }

                return new DecodedImage
                {
                    Width = width,
                    Height = height,
                    Components = components,
                    Jpeg = data,
                    InvertedCmyk = components == 4 && adobe,
                };
            }

            position += 2 + length;
        }

        throw new InvalidDataException("JPEG without a frame header");
    }

    // ------------------------------------------------------------------ PNG

    private static DecodedImage DecodePng(byte[] data)
    {
        var position = 8;
        int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        byte[]? palette = null;
        byte[]? transparency = null;
        using var idat = new MemoryStream();
        while (position + 8 <= data.Length)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(position));
            var type = System.Text.Encoding.ASCII.GetString(data, position + 4, 4);
            if (length < 0 || position + 12L + length > data.Length)
            {
                throw new InvalidDataException("Truncated PNG chunk");
            }

            var chunk = data.AsSpan(position + 8, length);
            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(chunk);
                    height = BinaryPrimitives.ReadInt32BigEndian(chunk[4..]);
                    bitDepth = chunk[8];
                    colorType = chunk[9];
                    interlace = chunk[12];
                    break;
                case "PLTE":
                    palette = chunk.ToArray();
                    break;
                case "tRNS":
                    transparency = chunk.ToArray();
                    break;
                case "IDAT":
                    idat.Write(chunk);
                    break;
            }

            position += 12 + length;
            if (type == "IEND")
            {
                break;
            }
        }

        CheckSize(width, height);
        var channels = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => throw new InvalidDataException("Unsupported PNG color type"),
        };
        if (bitDepth is not (1 or 2 or 4 or 8 or 16) || (colorType is 2 or 4 or 6 && bitDepth < 8) || (colorType == 3 && bitDepth == 16))
        {
            throw new InvalidDataException("Unsupported PNG bit depth");
        }

        // Unfiltered samples for the whole image, one row of 'stride' bytes per scanline.
        var bitsPerPixel = channels * bitDepth;
        var stride = (int)(((long)width * bitsPerPixel + 7) / 8);

        // Inflate no more than the image can use, so a small file cannot expand without bound.
        var expected = interlace == 1 ? InterlacedLength(width, height, bitsPerPixel) : (long)height * (stride + 1);
        idat.Position = 0;
        var raw = new byte[checked((int)expected)];
        var filled = 0;
        using (var inflater = new ZLibStream(idat, CompressionMode.Decompress))
        {
            int read;
            while (filled < raw.Length && (read = inflater.Read(raw, filled, raw.Length - filled)) > 0)
            {
                filled += read;
            }
        }

        if (filled < raw.Length)
        {
            Array.Resize(ref raw, filled);
        }

        var samples = new byte[checked(stride * height)];
        if (interlace == 1)
        {
            DeinterlaceAdam7(raw, samples, width, height, bitsPerPixel, stride);
        }
        else
        {
            Unfilter(raw, 0, samples, 0, stride, height, Math.Max(1, bitsPerPixel / 8), stride);
        }

        return ExpandPng(samples, width, height, bitDepth, colorType, stride, palette, transparency);
    }

    private static long InterlacedLength(int width, int height, int bitsPerPixel)
    {
        int[] startX = [0, 4, 0, 2, 0, 1, 0];
        int[] startY = [0, 0, 4, 0, 2, 0, 1];
        int[] stepX = [8, 8, 4, 4, 2, 2, 1];
        int[] stepY = [8, 8, 8, 4, 4, 2, 2];
        long total = 0;
        for (var pass = 0; pass < 7; pass++)
        {
            long passWidth = (width - startX[pass] + stepX[pass] - 1) / stepX[pass];
            long passHeight = (height - startY[pass] + stepY[pass] - 1) / stepY[pass];
            if (passWidth > 0 && passHeight > 0)
            {
                total += passHeight * (1 + ((passWidth * bitsPerPixel) + 7) / 8);
            }
        }

        return total;
    }

    private static int Unfilter(byte[] raw, int rawOffset, byte[] target, int targetOffset, int stride, int rows, int bytesPerPixel, int targetStride)
    {
        var previous = new byte[stride];
        var current = new byte[stride];
        for (var row = 0; row < rows; row++)
        {
            if (rawOffset + 1 + stride > raw.Length)
            {
                throw new InvalidDataException("Truncated PNG image data");
            }

            var filter = raw[rawOffset];
            Array.Copy(raw, rawOffset + 1, current, 0, stride);
            rawOffset += 1 + stride;
            for (var i = 0; i < stride; i++)
            {
                int left = i >= bytesPerPixel ? current[i - bytesPerPixel] : 0;
                int up = previous[i];
                int upLeft = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;
                current[i] = filter switch
                {
                    0 => current[i],
                    1 => (byte)(current[i] + left),
                    2 => (byte)(current[i] + up),
                    3 => (byte)(current[i] + ((left + up) >> 1)),
                    4 => (byte)(current[i] + Paeth(left, up, upLeft)),
                    _ => throw new InvalidDataException("Invalid PNG filter"),
                };
            }

            Array.Copy(current, 0, target, targetOffset + (row * targetStride), stride);
            (previous, current) = (current, previous);
        }

        return rawOffset;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : (pb <= pc ? b : c);
    }

    private static void DeinterlaceAdam7(byte[] raw, byte[] samples, int width, int height, int bitsPerPixel, int stride)
    {
        int[] startX = [0, 4, 0, 2, 0, 1, 0];
        int[] startY = [0, 0, 4, 0, 2, 0, 1];
        int[] stepX = [8, 8, 4, 4, 2, 2, 1];
        int[] stepY = [8, 8, 8, 4, 4, 2, 2];
        var offset = 0;
        for (var pass = 0; pass < 7; pass++)
        {
            var passWidth = (width - startX[pass] + stepX[pass] - 1) / stepX[pass];
            var passHeight = (height - startY[pass] + stepY[pass] - 1) / stepY[pass];
            if (passWidth <= 0 || passHeight <= 0)
            {
                continue;
            }

            var passStride = (int)(((long)passWidth * bitsPerPixel + 7) / 8);
            var passSamples = new byte[passStride * passHeight];
            offset = Unfilter(raw, offset, passSamples, 0, passStride, passHeight, Math.Max(1, bitsPerPixel / 8), passStride);
            for (var y = 0; y < passHeight; y++)
            {
                for (var x = 0; x < passWidth; x++)
                {
                    CopyPixelBits(passSamples, y * passStride, x, samples, ((startY[pass] + (y * stepY[pass])) * stride), startX[pass] + (x * stepX[pass]), bitsPerPixel);
                }
            }
        }
    }

    private static void CopyPixelBits(byte[] source, int sourceRow, int sourceX, byte[] target, int targetRow, int targetX, int bitsPerPixel)
    {
        if (bitsPerPixel >= 8)
        {
            var bytes = bitsPerPixel / 8;
            Array.Copy(source, sourceRow + (sourceX * bytes), target, targetRow + (targetX * bytes), bytes);
            return;
        }

        var sourceBit = sourceX * bitsPerPixel;
        var targetBit = targetX * bitsPerPixel;
        var mask = (1 << bitsPerPixel) - 1;
        var value = (source[sourceRow + (sourceBit >> 3)] >> (8 - bitsPerPixel - (sourceBit & 7))) & mask;
        var shift = 8 - bitsPerPixel - (targetBit & 7);
        ref var cell = ref target[targetRow + (targetBit >> 3)];
        cell = (byte)((cell & ~(mask << shift)) | (value << shift));
    }

    private static DecodedImage ExpandPng(byte[] samples, int width, int height, int bitDepth, int colorType, int stride, byte[]? palette, byte[]? transparency)
    {
        var colorChannels = colorType is 2 or 3 or 6 ? 3 : 1;
        var pixels = new byte[(long)width * height * colorChannels];
        byte[]? alpha = colorType is 4 or 6 || transparency is not null ? new byte[(long)width * height] : null;
        var sampleMax = (1 << Math.Min(bitDepth, 8)) - 1;

        int Sample(int row, int index)
        {
            if (bitDepth == 8)
            {
                return samples[(row * stride) + index];
            }

            if (bitDepth == 16)
            {
                return samples[(row * stride) + (index * 2)];
            }

            var bit = index * bitDepth;
            return (samples[(row * stride) + (bit >> 3)] >> (8 - bitDepth - (bit & 7))) & sampleMax;
        }

        int Raw16(int row, int index) => bitDepth == 16
            ? BinaryPrimitives.ReadUInt16BigEndian(samples.AsSpan((row * stride) + (index * 2)))
            : Sample(row, index);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var pixel = (y * width) + x;
                switch (colorType)
                {
                    case 0:
                    {
                        var gray = Sample(y, x);
                        pixels[pixel] = (byte)(bitDepth < 8 ? gray * 255 / sampleMax : gray);
                        if (alpha is not null)
                        {
                            var key = transparency!.Length >= 2 ? BinaryPrimitives.ReadUInt16BigEndian(transparency) : -1;
                            alpha[pixel] = (byte)(Raw16(y, x) == key ? 0 : 255);
                        }

                        break;
                    }

                    case 2:
                        for (var c = 0; c < 3; c++)
                        {
                            pixels[(pixel * 3) + c] = (byte)Sample(y, (x * 3) + c);
                        }

                        if (alpha is not null)
                        {
                            var matches = transparency!.Length >= 6;
                            for (var c = 0; c < 3 && matches; c++)
                            {
                                matches = Raw16(y, (x * 3) + c) == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(c * 2));
                            }

                            alpha[pixel] = (byte)(matches ? 0 : 255);
                        }

                        break;
                    case 3:
                    {
                        var index = Sample(y, x);
                        if (palette is null || (index * 3) + 2 >= palette.Length)
                        {
                            throw new InvalidDataException("PNG palette index out of range");
                        }

                        pixels[pixel * 3] = palette[index * 3];
                        pixels[(pixel * 3) + 1] = palette[(index * 3) + 1];
                        pixels[(pixel * 3) + 2] = palette[(index * 3) + 2];
                        alpha?[pixel] = index < transparency!.Length ? transparency[index] : (byte)255;

                        break;
                    }

                    case 4:
                        pixels[pixel] = (byte)Sample(y, x * 2);
                        alpha![pixel] = (byte)Sample(y, (x * 2) + 1);
                        break;
                    case 6:
                        for (var c = 0; c < 3; c++)
                        {
                            pixels[(pixel * 3) + c] = (byte)Sample(y, (x * 4) + c);
                        }

                        alpha![pixel] = (byte)Sample(y, (x * 4) + 3);
                        break;
                }
            }
        }

        return new DecodedImage { Width = width, Height = height, Components = colorChannels, Pixels = pixels, Alpha = alpha };
    }

    // ------------------------------------------------------------------ GIF

    private static DecodedImage DecodeGif(byte[] data)
    {
        var width = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(6));
        var height = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(8));
        CheckSize(width, height);
        var flags = data[10];
        var position = 13;
        byte[]? globalPalette = null;
        if ((flags & 0x80) != 0)
        {
            var size = 3 * (1 << ((flags & 7) + 1));
            globalPalette = data.AsSpan(position, size).ToArray();
            position += size;
        }

        var transparentIndex = -1;
        while (position < data.Length)
        {
            var block = data[position++];
            if (block == 0x21)
            {
                var label = data[position++];
                if (label == 0xF9 && data[position] >= 4)
                {
                    if ((data[position + 1] & 1) != 0)
                    {
                        transparentIndex = data[position + 4];
                    }
                }

                while (data[position] != 0)
                {
                    position += data[position] + 1;
                }

                position++;
                continue;
            }

            if (block != 0x2C)
            {
                break;
            }

            var left = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position));
            var top = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position + 2));
            var frameWidth = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position + 4));
            var frameHeight = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position + 6));
            var frameFlags = data[position + 8];
            position += 9;
            var palette = globalPalette;
            if ((frameFlags & 0x80) != 0)
            {
                var size = 3 * (1 << ((frameFlags & 7) + 1));
                palette = data.AsSpan(position, size).ToArray();
                position += size;
            }

            if (palette is null)
            {
                throw new InvalidDataException("GIF without a palette");
            }

            var minCodeSize = data[position++];
            using var compressed = new MemoryStream();
            while (data[position] != 0)
            {
                compressed.Write(data, position + 1, data[position]);
                position += data[position] + 1;
            }

            var indices = LzwDecode(compressed.ToArray(), minCodeSize, frameWidth * frameHeight);
            var pixels = new byte[width * height * 3];
            byte[]? alpha = transparentIndex >= 0 ? new byte[width * height] : null;
            var interlaced = (frameFlags & 0x40) != 0;
            var rowOrder = interlaced ? InterlacedRows(frameHeight) : Enumerable.Range(0, frameHeight).ToArray();
            for (var row = 0; row < frameHeight; row++)
            {
                var y = top + rowOrder[row];
                for (var x = 0; x < frameWidth; x++)
                {
                    var source = (row * frameWidth) + x;
                    if (y >= height || left + x >= width || source >= indices.Length)
                    {
                        continue;
                    }

                    var index = indices[source];
                    var pixel = (y * width) + left + x;
                    if ((index * 3) + 2 < palette.Length)
                    {
                        pixels[pixel * 3] = palette[index * 3];
                        pixels[(pixel * 3) + 1] = palette[(index * 3) + 1];
                        pixels[(pixel * 3) + 2] = palette[(index * 3) + 2];
                    }

                    alpha?[pixel] = index == transparentIndex ? (byte)0 : (byte)255;
                }
            }

            return new DecodedImage { Width = width, Height = height, Components = 3, Pixels = pixels, Alpha = alpha };
        }

        throw new InvalidDataException("GIF without an image");
    }

    private static int[] InterlacedRows(int height)
    {
        var order = new List<int>(height);
        foreach (var (start, step) in (ReadOnlySpan<(int, int)>)[(0, 8), (4, 8), (2, 4), (1, 2)])
        {
            for (var y = start; y < height; y += step)
            {
                order.Add(y);
            }
        }

        return [.. order];
    }

    private static byte[] LzwDecode(byte[] input, int minCodeSize, int pixelCount)
    {
        if (minCodeSize is < 2 or > 11)
        {
            throw new InvalidDataException("Invalid GIF code size");
        }

        var output = new byte[pixelCount];
        var written = 0;
        var clear = 1 << minCodeSize;
        var end = clear + 1;
        var prefix = new int[4096];
        var suffix = new byte[4096];
        var lengths = new int[4096];
        for (var i = 0; i < clear; i++)
        {
            suffix[i] = (byte)i;
            lengths[i] = 1;
        }

        var codeSize = minCodeSize + 1;
        var next = end + 1;
        var previous = -1;
        var bitBuffer = 0;
        var bitCount = 0;
        var stack = new byte[4097];
        foreach (var b in input)
        {
            bitBuffer |= b << bitCount;
            bitCount += 8;
            while (bitCount >= codeSize)
            {
                var code = bitBuffer & ((1 << codeSize) - 1);
                bitBuffer >>= codeSize;
                bitCount -= codeSize;
                if (code == clear)
                {
                    codeSize = minCodeSize + 1;
                    next = end + 1;
                    previous = -1;
                    continue;
                }

                if (code == end)
                {
                    return output;
                }

                int entry;
                byte first;
                if (code < next)
                {
                    entry = code;
                }
                else if (code == next && previous >= 0)
                {
                    entry = previous;
                }
                else
                {
                    throw new InvalidDataException("Invalid GIF code");
                }

                var depth = 0;
                for (var walk = entry; ; walk = prefix[walk])
                {
                    stack[depth++] = suffix[walk];
                    if (lengths[walk] <= 1 || depth >= stack.Length)
                    {
                        break;
                    }
                }

                first = stack[depth - 1];
                for (var i = depth - 1; i >= 0 && written < output.Length; i--)
                {
                    output[written++] = stack[i];
                }

                if (code == next && written < output.Length)
                {
                    output[written++] = first;
                }

                if (previous >= 0 && next < 4096)
                {
                    prefix[next] = previous;
                    suffix[next] = first;
                    lengths[next] = lengths[previous] + 1;
                    next++;
                    if (next == (1 << codeSize) && codeSize < 12)
                    {
                        codeSize++;
                    }
                }

                previous = code;
                if (written >= output.Length)
                {
                    return output;
                }
            }
        }

        return output;
    }

    // ------------------------------------------------------------------ BMP

    private static DecodedImage DecodeBmp(byte[] data)
    {
        var dataOffset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(10));
        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(14));
        if (headerSize < 40)
        {
            throw new InvalidDataException("Unsupported BMP header");
        }

        var width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(18));
        var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(22));
        var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(28));
        var compression = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(30));
        var topDown = rawHeight < 0;
        var height = Math.Abs(rawHeight);
        CheckSize(width, height);
        if (compression is not (0 or 3) || bitCount is not (1 or 4 or 8 or 24 or 32))
        {
            throw new InvalidDataException("Unsupported BMP encoding");
        }

        var paletteCount = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(46));
        if (paletteCount == 0 && bitCount <= 8)
        {
            paletteCount = 1 << bitCount;
        }

        var paletteOffset = 14 + headerSize;
        var stride = (((width * bitCount) + 31) / 32) * 4;
        var pixels = new byte[width * height * 3];
        for (var y = 0; y < height; y++)
        {
            var sourceRow = dataOffset + ((topDown ? y : height - 1 - y) * stride);
            if (sourceRow + stride > data.Length)
            {
                throw new InvalidDataException("Truncated BMP");
            }

            for (var x = 0; x < width; x++)
            {
                byte r, g, b;
                if (bitCount >= 24)
                {
                    var at = sourceRow + (x * (bitCount / 8));
                    (b, g, r) = (data[at], data[at + 1], data[at + 2]);
                }
                else
                {
                    var bit = x * bitCount;
                    var index = (data[sourceRow + (bit >> 3)] >> (8 - bitCount - (bit & 7))) & ((1 << bitCount) - 1);
                    if (index >= paletteCount)
                    {
                        index = 0;
                    }

                    var entry = paletteOffset + (index * 4);
                    (b, g, r) = (data[entry], data[entry + 1], data[entry + 2]);
                }

                var pixel = ((y * width) + x) * 3;
                (pixels[pixel], pixels[pixel + 1], pixels[pixel + 2]) = (r, g, b);
            }
        }

        return new DecodedImage { Width = width, Height = height, Components = 3, Pixels = pixels };
    }
}
