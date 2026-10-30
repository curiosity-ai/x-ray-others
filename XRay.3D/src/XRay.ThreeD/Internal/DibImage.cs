using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

namespace XRay.ThreeD.Internal;

/// <summary>Decodes a device-independent bitmap (8, 24 and 32 bits per pixel, uncompressed or RLE8) and writes pixels as PNG.</summary>
internal static class DibImage
{
    private const long MaxPixels = 64_000_000;

    /// <summary>RGB pixels (3 bytes each, top row first) of a DIB with or without a file header or a 4-byte length prefix, or null when it cannot be decoded.</summary>
    public static byte[]? DecodeRgb(ReadOnlySpan<byte> data, out int width, out int height, CancellationToken cancel = default)
    {
        width = height = 0;
        if (data.Length > 18 && data[0] == (byte)'B' && data[1] == (byte)'M')
        {
            data = data[14..];
        }
        else if (data.Length > 44 && BinaryPrimitives.ReadUInt32LittleEndian(data) == (uint)(data.Length - 4) && BinaryPrimitives.ReadUInt32LittleEndian(data[4..]) == 40)
        {
            data = data[4..];
        }

        if (data.Length < 40 || BinaryPrimitives.ReadUInt32LittleEndian(data) < 40)
        {
            return null;
        }

        var headerSize = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data), 4096);
        var w = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        var h = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]);
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
        var colours = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(data[32..]), 256);
        var topDown = h < 0;
        h = Math.Abs(h);
        if (w <= 0 || h <= 0 || (long)w * h > MaxPixels || bits is not (8 or 24 or 32) || compression is not (0 or 1) || (compression == 1 && bits != 8))
        {
            return null;
        }

        var palette = ReadOnlySpan<byte>.Empty;
        var pixelStart = headerSize;
        if (bits == 8)
        {
            var count = colours == 0 ? 256 : colours;
            if (data.Length < headerSize + 4 * count)
            {
                return null;
            }

            palette = data.Slice(headerSize, 4 * count);
            pixelStart += 4 * count;
        }

        var pixels = data[pixelStart..];
        var indices = new byte[(long)w * h * (bits == 8 ? 1 : 3)];
        var rgb = indices;
        if (bits == 8)
        {
            if (compression == 0)
            {
                var stride = (w + 3) & ~3;
                for (var row = 0; row < h; row++)
                {
                    Cancel.Every(cancel, row, 255);
                    var source = pixels.Slice((int)Math.Min((long)row * stride, pixels.Length), (int)Math.Min(w, Math.Max(0, pixels.Length - (long)row * stride)));
                    source.CopyTo(indices.AsSpan(Dest(row, h, w, topDown)));
                }
            }
            else if (!Rle8(pixels, indices, w, h, topDown, cancel))
            {
                return null;
            }

            rgb = new byte[(long)w * h * 3];
            for (var i = 0; i < indices.Length; i++)
            {
                Cancel.Every(cancel, i, 1 << 20);
                var p = indices[i] * 4;
                if (p + 2 < palette.Length)
                {
                    rgb[3 * i] = palette[p + 2];
                    rgb[3 * i + 1] = palette[p + 1];
                    rgb[3 * i + 2] = palette[p];
                }
            }
        }
        else
        {
            var bytes = bits / 8;
            var stride = (w * bytes + 3) & ~3;
            if (pixels.Length < (long)stride * h - (stride - w * bytes))
            {
                return null;
            }

            for (var row = 0; row < h; row++)
            {
                Cancel.Every(cancel, row, 255);
                var source = pixels.Slice(row * stride, w * bytes);
                var o = (topDown ? row : h - 1 - row) * w * 3;
                for (var x = 0; x < w; x++)
                {
                    rgb[o + 3 * x] = source[bytes * x + 2];
                    rgb[o + 3 * x + 1] = source[bytes * x + 1];
                    rgb[o + 3 * x + 2] = source[bytes * x];
                }
            }
        }

        width = w;
        height = h;
        return rgb;
    }

    private static int Dest(int row, int h, int w, bool topDown) => (topDown ? row : h - 1 - row) * w;

    private static bool Rle8(ReadOnlySpan<byte> input, byte[] output, int w, int h, bool topDown, CancellationToken cancel)
    {
        int x = 0, y = 0, i = 0;
        void Put(byte v)
        {
            if (x < w && y < h)
            {
                output[Dest(y, h, w, topDown) + x] = v;
            }

            x++;
        }

        while (i + 1 < input.Length && y < h)
        {
            Cancel.Every(cancel, i, 1 << 16);
            int count = input[i], value = input[i + 1];
            i += 2;
            if (count > 0)
            {
                for (var k = 0; k < count; k++)
                {
                    Put((byte)value);
                }

                continue;
            }

            switch (value)
            {
                case 0:
                    x = 0;
                    y++;
                    break;
                case 1:
                    return true;
                case 2:
                    if (i + 1 >= input.Length)
                    {
                        return true;
                    }

                    x += input[i];
                    y += input[i + 1];
                    i += 2;
                    break;
                default:
                    if (i + value > input.Length)
                    {
                        return false;
                    }

                    for (var k = 0; k < value; k++)
                    {
                        Put(input[i + k]);
                    }

                    i += (value + 1) & ~1;
                    break;
            }
        }

        return true;
    }

    /// <summary>Removes the bars of one flat colour (the colour of the top-left pixel) along the left, right, top and bottom edges.</summary>
    public static byte[] Trim(byte[] rgb, ref int width, ref int height)
    {
        if (rgb.Length < 3 || width < 3 || height < 3)
        {
            return rgb;
        }

        byte r = rgb[0], g = rgb[1], b = rgb[2];
        int width0 = width, height0 = height;
        bool Flat(int index) => rgb[index] == r && rgb[index + 1] == g && rgb[index + 2] == b;
        bool Column(int x) { for (var y = 0; y < height0; y++) { if (!Flat(3 * (y * width0 + x))) { return false; } } return true; }
        bool Row(int y) { for (var x = 0; x < width0; x++) { if (!Flat(3 * (y * width0 + x))) { return false; } } return true; }
        int left = 0, right = width, top = 0, bottom = height;
        while (left < right - 1 && Column(left)) { left++; }
        while (right - 1 > left && Column(right - 1)) { right--; }
        while (top < bottom - 1 && Row(top)) { top++; }
        while (bottom - 1 > top && Row(bottom - 1)) { bottom--; }
        if (left == 0 && top == 0 && right == width && bottom == height)
        {
            return rgb;
        }

        var w = right - left;
        var h = bottom - top;
        var trimmed = new byte[w * h * 3];
        for (var y = 0; y < h; y++)
        {
            Buffer.BlockCopy(rgb, 3 * ((top + y) * width + left), trimmed, 3 * y * w, 3 * w);
        }

        width = w;
        height = h;
        return trimmed;
    }

    /// <summary>An 8-bit RGB PNG.</summary>
    public static byte[] EncodePng(byte[] rgb, int width, int height)
    {
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Fastest, leaveOpen: true))
        {
            var zero = new byte[1];
            for (var y = 0; y < height; y++)
            {
                z.Write(zero);
                z.Write(rgb, y * width * 3, width * 3);
            }
        }

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], height);
        header[8] = 8;
        header[9] = 2;
        Chunk(png, "IHDR"u8, header);
        Chunk(png, "IDAT"u8, raw.GetBuffer().AsSpan(0, (int)raw.Length));
        Chunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void Chunk(MemoryStream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        output.Write(type);
        output.Write(data);
        var crc = Crc32.Update(Crc32.Update(0, type), data);
        BinaryPrimitives.WriteUInt32BigEndian(length, crc);
        output.Write(length);
    }
}
