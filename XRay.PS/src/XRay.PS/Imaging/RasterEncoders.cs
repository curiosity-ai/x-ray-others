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
        using var ms = new MemoryStream();
        WritePng(surface, ms, dpi);
        return ms.ToArray();
    }

    /// <summary>Encodes the surface as PNG straight into <paramref name="output"/>: rows are
    /// filtered and deflated as they are produced and written in IDAT chunks of up to
    /// 8 MiB, so neither the raw scanlines nor the compressed image exist as one array.</summary>
    public static void WritePng(RasterSurface surface, Stream output, int? dpi = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
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
        var ihdr = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
        ihdr[8] = 8;
        ihdr[9] = (byte)(hasAlpha ? 6 : 2);
        output.Write(PngSignature);
        WritePngChunk(output, "IHDR"u8, ihdr);
        if (dpi is int d && d > 0)
        {
            uint ppm = (uint)Py.Round(d / 0.0254);
            var phys = new byte[9];
            BinaryPrimitives.WriteUInt32BigEndian(phys, ppm);
            BinaryPrimitives.WriteUInt32BigEndian(phys.AsSpan(4), ppm);
            phys[8] = 1;
            WritePngChunk(output, "pHYs"u8, phys);
        }
        using (var idat = new IdatStream(output))
        {
            using var z = new System.IO.Compression.ZLibStream(idat, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true);
            var row = new byte[1 + width * channels];
            for (int y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int src = y * width * 4;
                row[0] = 0;
                if (hasAlpha)
                {
                    Buffer.BlockCopy(pixels, src, row, 1, width * 4);
                }
                else
                {
                    int o = 1;
                    for (int x = 0; x < width; x++)
                    {
                        int idx = src + x * 4;
                        row[o++] = pixels[idx];
                        row[o++] = pixels[idx + 1];
                        row[o++] = pixels[idx + 2];
                    }
                }
                z.Write(row);
            }
        }
        WritePngChunk(output, "IEND"u8, []);
        output.Flush();
    }

    // Collects deflate output and emits it as IDAT chunks.
    private sealed class IdatStream(Stream output) : Stream
    {
        private const int ChunkSize = 8 * 1024 * 1024;
        private readonly byte[] _buffer = new byte[ChunkSize];
        private int _count;
        private bool _any;

        public override void Write(ReadOnlySpan<byte> data)
        {
            while (!data.IsEmpty)
            {
                int n = Math.Min(data.Length, ChunkSize - _count);
                data[..n].CopyTo(_buffer.AsSpan(_count));
                _count += n;
                data = data[n..];
                if (_count == ChunkSize)
                    Emit();
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        private void Emit()
        {
            WritePngChunk(output, "IDAT"u8, _buffer.AsSpan(0, _count));
            _count = 0;
            _any = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && (_count > 0 || !_any))
                Emit();
            base.Dispose(disposing);
        }

        public override void Flush()
        {
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>Inserts or replaces the pHYs chunk with DPI metadata.</summary>
    public static byte[] AddPngDpi(byte[] data, int dpi)
    {
        if (dpi <= 0 || !data.AsSpan().StartsWith(PngSignature))
            return data;
        using var output = new MemoryStream();
        AddPngDpi(data, dpi, output);
        return output.ToArray();
    }

    /// <summary>Writes the PNG in <paramref name="data"/> to <paramref name="output"/> with its
    /// pHYs chunk set to <paramref name="dpi"/>; data that is not a PNG is copied unchanged.</summary>
    public static void AddPngDpi(ReadOnlySpan<byte> data, int dpi, Stream output)
    {
        if (dpi <= 0 || !data.StartsWith(PngSignature))
        {
            output.Write(data);
            return;
        }
        uint ppm = (uint)Py.Round(dpi / 0.0254);
        var phys = new byte[9];
        BinaryPrimitives.WriteUInt32BigEndian(phys, ppm);
        BinaryPrimitives.WriteUInt32BigEndian(phys.AsSpan(4), ppm);
        phys[8] = 1;
        output.Write(PngSignature);
        int idx = PngSignature.Length;
        Span<byte> len = stackalloc byte[4];
        bool inserted = false;
        while (idx + 8 <= data.Length)
        {
            long length = BinaryPrimitives.ReadUInt32BigEndian(data[idx..]);
            idx += 4;
            var ctype = data.Slice(idx, 4);
            idx += 4;
            int chunkLen = (int)Math.Min(length, Math.Max(0, data.Length - idx));
            var chunk = data.Slice(idx, chunkLen);
            idx = (int)Math.Min((long)idx + length, data.Length);
            int crcLen = Math.Min(4, data.Length - idx);
            var crc = data.Slice(idx, crcLen);
            idx += 4;
            if (ctype.SequenceEqual("pHYs"u8))
            {
                if (!inserted)
                {
                    WritePngChunk(output, "pHYs"u8, phys);
                    inserted = true;
                }
                continue;
            }
            if (ctype.SequenceEqual("IDAT"u8) && !inserted)
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
