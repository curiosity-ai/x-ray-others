// Ported from image/encoders.py: PNG, BMP, TIFF and baseline JPEG encoders for raster surfaces.
// The JPEG encoder reproduces the original's tables and float DCT so output is byte-identical.

using System.Buffers.Binary;
using XRay.PS.Internal;

namespace XRay.PS.Imaging;

/// <summary>Encodes <see cref="RasterSurface"/> pixels as image files.</summary>
public static class RasterEncoders
{
    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Encodes 8-bit RGB (or RGBA when any pixel is not opaque) PNG bytes, with a
    /// pHYs chunk when <paramref name="dpi"/> is given.</summary>
    public static byte[] EncodePng(RasterSurface surface, int? dpi = null)
    {
        int width = surface.Width;
        int height = surface.Height;
        var pixels = surface.Pixels;
        bool hasAlpha = false;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 255)
            {
                hasAlpha = true;
                break;
            }
        }
        int channels = hasAlpha ? 4 : 3;
        var raw = new byte[(long)height * (1 + width * channels)];
        int o = 0;
        for (int y = 0; y < height; y++)
        {
            raw[o++] = 0;
            int row = y * width * 4;
            if (hasAlpha)
            {
                Buffer.BlockCopy(pixels, row, raw, o, width * 4);
                o += width * 4;
            }
            else
            {
                for (int x = 0; x < width; x++)
                {
                    int idx = row + x * 4;
                    raw[o++] = pixels[idx];
                    raw[o++] = pixels[idx + 1];
                    raw[o++] = pixels[idx + 2];
                }
            }
        }
        byte[] compressed = Zlib.Compress(raw);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8;
        ihdr[9] = (byte)(hasAlpha ? 6 : 2);
        using var ms = new MemoryStream();
        ms.Write(PngSignature);
        WritePngChunk(ms, "IHDR"u8, ihdr);
        WritePngChunk(ms, "IDAT"u8, compressed);
        WritePngChunk(ms, "IEND"u8, []);
        byte[] data = ms.ToArray();
        return dpi is int d ? AddPngDpi(data, d) : data;
    }

    /// <summary>Inserts or replaces the pHYs chunk with DPI metadata.</summary>
    public static byte[] AddPngDpi(byte[] data, int dpi)
    {
        if (dpi <= 0 || !data.AsSpan().StartsWith(PngSignature))
            return data;
        uint ppm = (uint)Py.Round(dpi / 0.0254);
        var phys = new byte[9];
        BinaryPrimitives.WriteUInt32BigEndian(phys, ppm);
        BinaryPrimitives.WriteUInt32BigEndian(phys.AsSpan(4), ppm);
        phys[8] = 1;
        using var output = new MemoryStream();
        output.Write(PngSignature);
        int idx = PngSignature.Length;
        Span<byte> len = stackalloc byte[4];
        bool inserted = false;
        while (idx + 8 <= data.Length)
        {
            long length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(idx));
            idx += 4;
            var ctype = data.AsSpan(idx, 4).ToArray();
            idx += 4;
            int chunkLen = (int)Math.Min(length, Math.Max(0, data.Length - idx));
            var chunk = data.AsSpan(idx, chunkLen).ToArray();
            idx = (int)Math.Min((long)idx + length, data.Length);
            int crcLen = Math.Min(4, data.Length - idx);
            var crc = data.AsSpan(idx, crcLen).ToArray();
            idx += 4;
            if (ctype.AsSpan().SequenceEqual("pHYs"u8))
            {
                if (!inserted)
                {
                    WritePngChunk(output, "pHYs"u8, phys);
                    inserted = true;
                }
                continue;
            }
            if (ctype.AsSpan().SequenceEqual("IDAT"u8) && !inserted)
            {
                WritePngChunk(output, "pHYs"u8, phys);
                inserted = true;
            }
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)length);
            output.Write(len);
            output.Write(ctype);
            output.Write(chunk);
            output.Write(crc);
        }
        return output.ToArray();
    }

    private static void WritePngChunk(Stream stream, ReadOnlySpan<byte> tag, ReadOnlySpan<byte> data)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(buf, (uint)data.Length);
        stream.Write(buf);
        stream.Write(tag);
        stream.Write(data);
        uint crc = Crc32.Update(Crc32.Update(0xFFFFFFFFu, tag), data) ^ 0xFFFFFFFFu;
        BinaryPrimitives.WriteUInt32BigEndian(buf, crc);
        stream.Write(buf);
    }

    /// <summary>Encodes a 24-bit bottom-up BMP.</summary>
    public static byte[] EncodeBmp(RasterSurface surface)
    {
        int width = surface.Width;
        int height = surface.Height;
        int rowStride = (width * 3 + 3) & ~3;
        int imageSize = rowStride * height;
        var output = new byte[54 + imageSize];
        output[0] = (byte)'B';
        output[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(2), (uint)(54 + imageSize));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(10), 54);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(14), 40);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(18), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(22), (uint)height);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(26), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(28), 24);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(34), (uint)imageSize);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(38), 2835);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(42), 2835);
        int o = 54;
        for (int y = height - 1; y >= 0; y--)
        {
            int row = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                int idx = row + x * 4;
                output[o + x * 3] = surface.Pixels[idx + 2];
                output[o + x * 3 + 1] = surface.Pixels[idx + 1];
                output[o + x * 3 + 2] = surface.Pixels[idx];
            }
            o += rowStride;
        }
        return output;
    }

    /// <summary>Encodes a baseline uncompressed RGB TIFF.</summary>
    public static byte[] EncodeTiff(RasterSurface surface)
    {
        int width = surface.Width;
        int height = surface.Height;
        int imageLength = width * height * 3;
        const int ifdOffset = 8;
        const int entryCount = 12;
        const int ifdSize = 2 + entryCount * 12 + 4;
        const int bitsOffset = ifdOffset + ifdSize;
        const int xresOffset = bitsOffset + 6;
        const int yresOffset = xresOffset + 8;
        const int imageOffset = yresOffset + 8;
        var output = new byte[imageOffset + imageLength];
        var span = output.AsSpan();
        span[0] = (byte)'I';
        span[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 42);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], ifdOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(span[8..], entryCount);
        int p = 10;
        void Entry(ushort tag, ushort type, uint count, uint value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(p), tag);
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(p + 2), type);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(p + 4), count);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(p + 8), value);
            p += 12;
        }
        Entry(256, 4, 1, (uint)width);
        Entry(257, 4, 1, (uint)height);
        Entry(258, 3, 3, bitsOffset);
        Entry(259, 3, 1, 1);
        Entry(262, 3, 1, 2);
        Entry(273, 4, 1, imageOffset);
        Entry(277, 3, 1, 3);
        Entry(278, 4, 1, (uint)height);
        Entry(279, 4, 1, (uint)imageLength);
        Entry(282, 5, 1, xresOffset);
        Entry(283, 5, 1, yresOffset);
        Entry(296, 3, 1, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(span[p..], 0);
        BinaryPrimitives.WriteUInt16LittleEndian(span[bitsOffset..], 8);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(bitsOffset + 2)..], 8);
        BinaryPrimitives.WriteUInt16LittleEndian(span[(bitsOffset + 4)..], 8);
        BinaryPrimitives.WriteUInt32LittleEndian(span[xresOffset..], 72);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(xresOffset + 4)..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(span[yresOffset..], 72);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(yresOffset + 4)..], 1);
        int o = imageOffset;
        var pixels = surface.Pixels;
        for (int i = 0; i < width * height; i++)
        {
            output[o++] = pixels[i * 4];
            output[o++] = pixels[i * 4 + 1];
            output[o++] = pixels[i * 4 + 2];
        }
        return output;
    }

    /// <summary>Encodes a baseline 4:4:4 color JPEG.</summary>
    public static byte[] EncodeJpeg(RasterSurface surface, int quality = 75) => JpegEncoder.Encode(surface, quality);
}

/// <summary>The CRC-32 used by PNG (polynomial 0xEDB88320).</summary>
internal static class Crc32
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[n] = c;
        }
        return table;
    }

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    public static uint Compute(ReadOnlySpan<byte> data) => Update(0xFFFFFFFFu, data) ^ 0xFFFFFFFFu;
}
