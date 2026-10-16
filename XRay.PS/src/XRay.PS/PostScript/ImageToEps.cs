// Ported from ps/image_to_eps.py: wraps a PNG, JPEG, BMP or TIFF image in a one-page EPS file.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using XRay.PS.Internal;

namespace XRay.PS.PostScript;

/// <summary>Decoded raster image data for EPS conversion.</summary>
/// <param name="Width">Width in pixels.</param>
/// <param name="Height">Height in pixels.</param>
/// <param name="BitsPerComponent">Bits per sample (8).</param>
/// <param name="ColorSpace"><c>DeviceRGB</c> or <c>DeviceGray</c>.</param>
/// <param name="Pixels">Samples, rows top to bottom, or the JPEG stream itself for a JPEG.</param>
public sealed record EpsImageInfo(int Width, int Height, int BitsPerComponent, string ColorSpace, byte[] Pixels);

/// <summary>Converts raster images to EPS.</summary>
public static class ImageToEps
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    // Bounds the output of a TIFF LZW strip; the format allows a huge expansion ratio.
    private const long MaxDecodedBytes = 1L << 30;

    /// <summary>Converts an image file (PNG, JPEG, BMP or TIFF) to EPS bytes.</summary>
    /// <param name="path">The image file.</param>
    /// <param name="bbox">The EPS bounding box (llx, lly, urx, ury); the image size when null.</param>
    public static byte[] ConvertFile(string path, (int Llx, int Lly, int Urx, int Ury)? bbox = null) =>
        Convert(File.ReadAllBytes(path), bbox);

    /// <summary>Converts an image file to EPS bytes, observing a cancellation token.</summary>
    public static byte[] ConvertFile(string path, (int Llx, int Lly, int Urx, int Ury)? bbox, CancellationToken cancellationToken) =>
        Convert(File.ReadAllBytes(path), bbox, cancellationToken);

    /// <summary>Converts an image file to EPS and writes it to <paramref name="output"/>.</summary>
    public static void ConvertFile(string path, Stream output, (int Llx, int Lly, int Urx, int Ury)? bbox = null, CancellationToken cancellationToken = default) =>
        Convert(File.ReadAllBytes(path), output, bbox, cancellationToken);

    /// <summary>Converts image bytes (PNG, JPEG, BMP or TIFF) to EPS bytes.</summary>
    public static byte[] Convert(byte[] data, (int Llx, int Lly, int Urx, int Ury)? bbox = null) => Convert(data, bbox, default);

    /// <summary>Converts image bytes to EPS bytes, observing a cancellation token.</summary>
    public static byte[] Convert(byte[] data, (int Llx, int Lly, int Urx, int Ury)? bbox, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        Convert(data, ms, bbox, cancellationToken);
        return ms.ToArray();
    }

    /// <summary>Converts image bytes to EPS and writes it to <paramref name="output"/>. The hex
    /// body is written line by line, so the (twice the size of the pixels) text is never built.</summary>
    public static void Convert(byte[] data, Stream output, (int Llx, int Lly, int Urx, int Ury)? bbox = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(output);
        cancellationToken.ThrowIfCancellationRequested();
        using var guard = Guard.Begin(cancellationToken);
        EpsImageInfo info;
        if (data.AsSpan().StartsWith(PngSignature))
            info = DecodePng(data);
        else if (data.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8]))
            info = DecodeJpeg(data);
        else if (data.AsSpan().StartsWith("BM"u8))
            info = DecodeBmp(data);
        else if (data.AsSpan().StartsWith("II"u8) || data.AsSpan().StartsWith("MM"u8))
            info = DecodeTiff(data);
        else
            throw Py.ValueError("unsupported image format");
        ToEps(info, output, bbox, cancellationToken);
    }

    /// <summary>Decodes an 8-bit RGB or RGBA PNG to RGB samples (alpha is dropped).</summary>
    public static EpsImageInfo DecodePng(byte[] data)
    {
        if (!data.AsSpan().StartsWith(PngSignature))
            throw Py.ValueError("invalid PNG signature");
        long width = 0, height = 0;
        int? bitDepth = null, colorType = null;
        using var idat = new MemoryStream();
        long offset = 8;
        while (offset + 8 <= data.Length)
        {
            long length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)offset, 4));
            var chunkType = data.AsSpan((int)offset + 4, 4);
            var chunkData = Slice(data, offset + 8, length);
            offset += 12 + length;
            if (chunkType.SequenceEqual("IHDR"u8))
            {
                if (chunkData.Length < 10)
                    throw StructError(10);
                width = BinaryPrimitives.ReadUInt32BigEndian(chunkData);
                height = BinaryPrimitives.ReadUInt32BigEndian(chunkData[4..]);
                bitDepth = chunkData[8];
                colorType = chunkData[9];
            }
            else if (chunkType.SequenceEqual("IDAT"u8))
            {
                idat.Write(chunkData);
            }
            else if (chunkType.SequenceEqual("IEND"u8))
            {
                break;
            }
        }
        if (bitDepth != 8 || colorType is not (2 or 6))
            throw Py.ValueError("unsupported PNG format");
        byte[] decompressed = Zlib.Decompress(idat.ToArray());
        int bytesPerPixel = colorType == 2 ? 3 : 4;
        long stride = width * bytesPerPixel;
        if (width > int.MaxValue || height > int.MaxValue)
            throw Py.ValueError("unsupported PNG format");
        using var raw = new MemoryStream();
        long idx = 0;
        // A row can be no longer than the data that remains, so the buffers stay bounded by it.
        byte[] prev = new byte[Math.Min(stride, decompressed.Length)];
        for (long row = 0; row < height; row++)
        {
            Guard.Check();
            if (idx >= decompressed.Length)
                throw Py.IndexError("index out of range");
            int filterType = decompressed[idx];
            idx++;
            byte[] line = Slice(decompressed, idx, stride).ToArray();
            idx += stride;
            ApplyPngFilter(filterType, line, prev, bytesPerPixel);
            if (colorType == 6)
            {
                for (int i = 0; i < line.Length; i += 4)
                    raw.Write(line, i, Math.Min(3, line.Length - i));
            }
            else
            {
                raw.Write(line);
            }
            prev = line;
        }
        return new EpsImageInfo((int)width, (int)height, 8, "DeviceRGB", raw.ToArray());
    }

    /// <summary>Reads a JPEG's size and component count; the stream is embedded as is.</summary>
    public static EpsImageInfo DecodeJpeg(byte[] data)
    {
        if (!data.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8]))
            throw Py.ValueError("invalid JPEG");
        long offset = 2;
        int? width = null, height = null, components = null;
        while (offset + 4 <= data.Length)
        {
            if (data[offset] != 0xFF)
                break;
            int marker = data[offset + 1];
            offset += 2;
            if (marker is 0xD8 or 0xD9)
                continue;
            int length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan((int)offset, 2));
            if (marker is 0xC0 or 0xC2)
            {
                var sof = Slice(data, offset + 3, 5);
                if (sof.Length < 4)
                    throw StructError(2);
                height = BinaryPrimitives.ReadUInt16BigEndian(sof);
                width = BinaryPrimitives.ReadUInt16BigEndian(sof[2..]);
                if (sof.Length < 5)
                    throw Py.IndexError("index out of range");
                components = sof[4];
                break;
            }
            offset += length;
        }
        if (width is null || height is null)
            throw Py.ValueError("JPEG size not found");
        string colorSpace = (components ?? 0) >= 3 ? "DeviceRGB" : "DeviceGray";
        return new EpsImageInfo(width.Value, height.Value, 8, colorSpace, data);
    }

    /// <summary>Decodes an uncompressed 24-bit or 1-bit BMP.</summary>
    public static EpsImageInfo DecodeBmp(byte[] data)
    {
        if (!data.AsSpan().StartsWith("BM"u8))
            throw Py.ValueError("invalid BMP");
        if (data.Length < 34)
            throw StructError(4);
        long offset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(10));
        long headerSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(14));
        if (headerSize < 40)
            throw Py.ValueError("unsupported BMP header");
        int width = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(18));
        int height = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(22));
        int planes = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(26));
        int bpp = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(28));
        long compression = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(30));
        if (planes != 1 || compression != 0)
            throw Py.ValueError("unsupported BMP format");
        long absHeight = Math.Abs((long)height);
        if (absHeight > int.MaxValue)
            throw Py.ValueError("invalid image dimensions");
        var rows = new List<byte[]>();
        string colorSpace;
        if (bpp == 1 && Slice(data, 14 + headerSize, 8).Length < 8)
            throw Py.ValueError("invalid BMP palette");
        if (bpp is 1 or 24 && width <= 0)
        {
            // Every row slice is empty, as in the original; no need to walk them.
            return new EpsImageInfo(width, (int)absHeight, 8, bpp == 24 ? "DeviceRGB" : "DeviceGray", []);
        }
        if (bpp == 24)
        {
            long rowSize = ((width * 3L + 3) / 4) * 4;
            for (long rowIndex = 0; rowIndex < absHeight; rowIndex++)
            {
                Guard.Check();
                long start = offset + rowIndex * rowSize;
                if (start >= data.Length)
                    break; // every remaining row is empty, as the original's slices were
                var row = Slice(data, start, width * 3L);
                if (row.Length % 3 != 0)
                    throw Py.ValueError("not enough values to unpack");
                var rgb = new byte[row.Length];
                for (int i = 0; i < row.Length; i += 3)
                {
                    rgb[i] = row[i + 2];
                    rgb[i + 1] = row[i + 1];
                    rgb[i + 2] = row[i];
                }
                rows.Add(rgb);
            }
            colorSpace = "DeviceRGB";
        }
        else if (bpp == 1)
        {
            var palette = Slice(data, 14 + headerSize, 8);
            if (palette.Length < 8)
                throw Py.ValueError("invalid BMP palette");
            int[] grayMap = [(palette[2] + palette[1] + palette[0]) / 3, (palette[6] + palette[5] + palette[4]) / 3];
            long rowSize = ((width * (long)bpp + 31) / 32) * 4;
            for (long rowIndex = 0; rowIndex < absHeight; rowIndex++)
            {
                Guard.Check();
                long start = offset + rowIndex * rowSize;
                var row = Slice(data, start, rowSize);
                var grayRow = new byte[Math.Max(0, width)];
                for (int col = 0; col < width; col++)
                {
                    if (col / 8 >= row.Length)
                        throw Py.IndexError("index out of range");
                    int bit = (row[col / 8] >> (7 - col % 8)) & 1;
                    grayRow[col] = (byte)grayMap[bit];
                }
                rows.Add(grayRow);
            }
            colorSpace = "DeviceGray";
        }
        else
        {
            throw Py.ValueError("unsupported BMP format");
        }
        if (height > 0)
            rows.Reverse();
        return new EpsImageInfo(width, (int)absHeight, 8, colorSpace, rows.SelectMany(r => r).ToArray());
    }

    /// <summary>Decodes an 8-bit uncompressed, PackBits or LZW TIFF.</summary>
    public static EpsImageInfo DecodeTiff(byte[] data)
    {
        bool little;
        if (data.AsSpan().StartsWith("II"u8))
            little = true;
        else if (data.AsSpan().StartsWith("MM"u8))
            little = false;
        else
            throw Py.ValueError("invalid TIFF header");
        if (data.Length < 8)
            throw StructError(2);
        if (U16(data, 2, little) != 42)
            throw Py.ValueError("invalid TIFF marker");
        long ifdOffset = U32(data, 4, little);
        if (ifdOffset >= data.Length)
            throw Py.ValueError("invalid TIFF offset");
        var tags = ReadIfd(data, ifdOffset, little);
        object? Tag(int id, object? fallback) => tags.TryGetValue(id, out var v) ? v : fallback;
        var width = Tag(256, null);
        var height = Tag(257, null);
        var bits = Tag(258, 8L);
        var compression = Tag(259, 1L);
        var photometric = Tag(262, 2L);
        var samplesPerPixel = Tag(277, 1L);
        var planar = Tag(284, 1L);
        var stripOffsets = Tag(273, null);
        var stripCounts = Tag(279, null);
        if (width is null || height is null || stripOffsets is null || stripCounts is null)
            throw Py.ValueError("missing TIFF fields");
        if (!NumberEquals(planar, 1))
            throw Py.ValueError("unsupported planar TIFF");
        object bitsPerComponent = bits is List<object> bitList ? (bitList.Count > 0 ? bitList[0] : throw Py.IndexError()) : bits!;
        if (!NumberEquals(bitsPerComponent, 8))
            throw Py.ValueError("unsupported TIFF bits per sample");
        var offsets = stripOffsets is List<object> ol ? ol : [stripOffsets];
        var counts = stripCounts is List<object> cl ? cl : [stripCounts];
        using var raw = new MemoryStream();
        for (int i = 0; i < Math.Min(offsets.Count, counts.Count); i++)
        {
            var chunk = Slice(data, ToLong(offsets[i]), ToLong(counts[i]));
            if (NumberEquals(compression, 1))
                raw.Write(chunk);
            else if (NumberEquals(compression, 32773))
                raw.Write(PackBitsDecode(chunk));
            else if (NumberEquals(compression, 5))
                raw.Write(TiffLzwDecode(chunk));
            else
                throw Py.ValueError("unsupported TIFF compression");
            if (raw.Length > MaxDecodedBytes)
                throw Py.ValueError("TIFF data too large");
        }
        long w = ToLong(width), h = ToLong(height), spp = ToLong(samplesPerPixel);
        double expected = (double)w * h * spp;
        if (raw.Length < expected)
            throw Py.ValueError("TIFF data length mismatch");
        if (w <= 0 || h <= 0 || spp <= 0 || w > int.MaxValue || h > int.MaxValue)
        {
            // The original sliced a negative or zero length to nothing; the EPS step rejects it.
            return new EpsImageInfo((int)Math.Clamp(w, int.MinValue, int.MaxValue), (int)Math.Clamp(h, int.MinValue, int.MaxValue), 8,
                spp >= 3 ? "DeviceRGB" : "DeviceGray", []);
        }
        byte[] pixels = raw.GetBuffer().AsSpan(0, (int)expected).ToArray();
        if (spp == 4)
        {
            var rgb = new byte[pixels.Length / 4 * 3 + Math.Min(3, pixels.Length % 4)];
            int dst = 0;
            for (int i = 0; i < pixels.Length; i += 4)
            {
                int n = Math.Min(3, pixels.Length - i);
                Array.Copy(pixels, i, rgb, dst, n);
                dst += n;
            }
            pixels = rgb;
            spp = 3;
        }
        if (NumberEquals(photometric, 0) && spp == 1)
        {
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = (byte)(255 - pixels[i]);
        }
        return new EpsImageInfo((int)w, (int)h, 8, spp >= 3 ? "DeviceRGB" : "DeviceGray", pixels);
    }

    /// <summary>Serializes decoded image data as EPS.</summary>
    public static byte[] ToEps(EpsImageInfo image, (int Llx, int Lly, int Urx, int Ury)? bbox = null)
    {
        using var ms = new MemoryStream();
        ToEps(image, ms, bbox);
        return ms.ToArray();
    }

    /// <summary>Writes an image as EPS to <paramref name="output"/>.</summary>
    public static void ToEps(EpsImageInfo image, Stream output, (int Llx, int Lly, int Urx, int Ury)? bbox = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(output);
        int imgW = image.Width, imgH = image.Height;
        if (imgW <= 0 || imgH <= 0)
            throw Py.ValueError("invalid image dimensions");
        var box = bbox ?? (0, 0, imgW, imgH);
        long bboxW = (long)box.Urx - box.Llx;
        long bboxH = (long)box.Ury - box.Lly;
        if (bboxW <= 0 || bboxH <= 0)
            throw Py.ValueError("invalid bounding box");
        int components = image.ColorSpace == "DeviceGray" ? 1 : 3;
        long expected = (long)imgW * imgH * components * (image.BitsPerComponent / 8);
        bool useDct = false;
        if (image.Pixels.Length != expected)
        {
            if (image.Pixels.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8]))
                useDct = true;
            else
                throw Py.ValueError("image data length mismatch");
        }
        double scaleX = (double)bboxW / imgW;
        double scaleY = (double)bboxH / imgH;
        var lines = new List<string>
        {
            "%!PS-Adobe-3.0 EPSF-3.0",
            $"%%BoundingBox: {box.Llx} {box.Lly} {box.Urx} {box.Ury}",
            "%%LanguageLevel: 3",
            "%%Pages: 1",
            "%%EndComments",
            "gsave",
            $"{Fmt(box.Llx)} {Fmt(box.Lly)} translate",
            $"{Fmt(scaleX)} {Fmt(scaleY)} scale",
            image.ColorSpace == "DeviceGray" ? "/DeviceGray setcolorspace" : "/DeviceRGB setcolorspace",
        };
        string matrix = $"[{imgW} 0 0 -{imgH} 0 {imgH}]";
        string source = useDct ? "{currentfile /ASCIIHexDecode filter /DCTDecode filter}" : "{currentfile /ASCIIHexDecode filter}";
        lines.Add(components == 1
            ? $"{imgW} {imgH} {image.BitsPerComponent} {matrix} {source} image"
            : $"{imgW} {imgH} {image.BitsPerComponent} {matrix} {source} false {components} colorimage");
        foreach (string line in lines)
            WriteLine(output, line);
        WriteHexBody(output, image.Pixels, cancellationToken);
        WriteLine(output, "grestore");
        WriteLine(output, "%%EOF");
        output.Flush();
    }

    private static void WriteLine(Stream output, string line)
    {
        output.Write(Encoding.Latin1.GetBytes(line));
        output.WriteByte((byte)'\n');
    }

    // The data as lowercase hex, 64 characters per line, closed by '>' (so a '>' after a full
    // last line starts a line of its own), written through a small buffer.
    private static void WriteHexBody(Stream output, byte[] data, CancellationToken cancellationToken)
    {
        const int bytesPerLine = 32;
        const int blockSize = 64 * 1024;
        var block = new byte[blockSize + 16];
        int used = 0;
        int lastCount = 0;
        for (int position = 0; position < data.Length; position += bytesPerLine)
        {
            if (used + bytesPerLine * 2 + 1 > blockSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Write(block, 0, used);
                used = 0;
            }
            int n = Math.Min(bytesPerLine, data.Length - position);
            for (int i = 0; i < n; i++)
            {
                byte b = data[position + i];
                block[used++] = (byte)"0123456789abcdef"[b >> 4];
                block[used++] = (byte)"0123456789abcdef"[b & 15];
            }
            lastCount = n;
            if (position + n < data.Length)
                block[used++] = (byte)'\n';
        }
        if (lastCount == bytesPerLine)
            block[used++] = (byte)'\n';
        block[used++] = (byte)'>';
        block[used++] = (byte)'\n';
        output.Write(block, 0, used);
    }

    private static string Fmt(double value)
    {
        double truncated = Math.Truncate(value);
        if (truncated == value)
            return ((long)truncated).ToString(CultureInfo.InvariantCulture);
        return value.ToString("F6", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
    }

    // ---- TIFF helpers ----

    private static int U16(byte[] data, long offset, bool little)
    {
        var span = Slice(data, offset, 2);
        if (span.Length < 2)
            throw StructError(2);
        return little ? BinaryPrimitives.ReadUInt16LittleEndian(span) : BinaryPrimitives.ReadUInt16BigEndian(span);
    }

    private static long U32(byte[] data, long offset, bool little)
    {
        var span = Slice(data, offset, 4);
        if (span.Length < 4)
            throw StructError(4);
        return little ? BinaryPrimitives.ReadUInt32LittleEndian(span) : BinaryPrimitives.ReadUInt32BigEndian(span);
    }

    private static Dictionary<int, object> ReadIfd(byte[] data, long offset, bool little)
    {
        var tags = new Dictionary<int, object>();
        int count = U16(data, offset, little);
        long cursor = offset + 2;
        for (int i = 0; i < count; i++)
        {
            if (cursor + 12 > data.Length)
                throw StructError(12);
            int tag = U16(data, cursor, little);
            int dtype = U16(data, cursor + 2, little);
            long items = U32(data, cursor + 4, little);
            long value = U32(data, cursor + 8, little);
            cursor += 12;
            tags[tag] = ReadIfdValue(data, little, dtype, items, value);
        }
        return tags;
    }

    private static object ReadIfdValue(byte[] data, bool little, int dtype, long count, long value)
    {
        int size = dtype switch
        {
            1 or 2 => 1,
            3 => 2,
            4 => 4,
            5 => 8,
            _ => throw Py.ValueError("unsupported TIFF field type"),
        };
        long total = size * count;
        ReadOnlySpan<byte> raw;
        if (total <= 4)
        {
            var inline = new byte[4];
            if (little)
                BinaryPrimitives.WriteUInt32LittleEndian(inline, (uint)value);
            else
                BinaryPrimitives.WriteUInt32BigEndian(inline, (uint)value);
            raw = inline.AsSpan(0, (int)total);
        }
        else
        {
            raw = Slice(data, value, total);
        }
        if (dtype == 2)
            return Encoding.Latin1.GetString(raw.TrimEnd((byte)0));
        if (raw.Length != total)
            throw StructError(total);
        var items = new List<object>((int)Math.Min(count, 1 << 16));
        for (long idx = 0; idx < count; idx++)
        {
            if ((idx & 0xFFFF) == 0)
                Guard.Check();
            var piece = raw.Slice((int)(idx * size), size);
            items.Add(dtype switch
            {
                1 => (long)piece[0],
                3 => (long)(little ? BinaryPrimitives.ReadUInt16LittleEndian(piece) : BinaryPrimitives.ReadUInt16BigEndian(piece)),
                5 => Rational(piece, little),
                _ => (long)(little ? BinaryPrimitives.ReadUInt32LittleEndian(piece) : BinaryPrimitives.ReadUInt32BigEndian(piece)),
            });
        }
        if (count == 1)
            return items[0];
        return items;
    }

    private static object Rational(ReadOnlySpan<byte> piece, bool little)
    {
        uint num = little ? BinaryPrimitives.ReadUInt32LittleEndian(piece) : BinaryPrimitives.ReadUInt32BigEndian(piece);
        uint den = little ? BinaryPrimitives.ReadUInt32LittleEndian(piece[4..]) : BinaryPrimitives.ReadUInt32BigEndian(piece[4..]);
        return den == 0 ? 0.0 : (double)num / den;
    }

    private static bool NumberEquals(object? value, long expected) => value switch
    {
        long l => l == expected,
        double d => d == expected,
        _ => false,
    };

    private static long ToLong(object? value) => value switch
    {
        long l => l,
        double d when double.IsFinite(d) && d == Math.Truncate(d) => (long)d,
        double => throw Py.TypeError("slice indices must be integers or None or have an __index__ method"),
        List<object> => throw Py.TypeError("unsupported operand type(s)"),
        string => throw Py.TypeError("unsupported operand type(s)"),
        _ => throw Py.TypeError("unsupported operand type(s)"),
    };

    private static byte[] PackBitsDecode(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length);
        int index = 0;
        int polls = 0;
        while (index < data.Length)
        {
            Guard.Every(ref polls, 0xFFF);
            int control = (sbyte)data[index];
            index++;
            if (control >= 0)
            {
                int count = control + 1;
                int n = Math.Min(count, data.Length - index);
                for (int i = 0; i < n; i++)
                    output.Add(data[index + i]);
                index += count;
            }
            else if (control >= -127)
            {
                int count = 1 - control;
                if (index >= data.Length)
                    break;
                for (int i = 0; i < count; i++)
                    output.Add(data[index]);
                index++;
            }
        }
        return [.. output];
    }

    private static byte[] TiffLzwDecode(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return [];
        var reader = new MsbBitReader(data.ToArray());
        const int clearCode = 256, endCode = 257;
        int codeSize = 9;
        var dictionary = NewDictionary();
        int nextCode = 258;
        using var output = new MemoryStream();
        byte[]? prev = null;
        int lzwPolls = 0;
        while (true)
        {
            Guard.Every(ref lzwPolls, 0xFFF);
            int? read = reader.Read(codeSize);
            if (read is not int code)
                break;
            if (code == clearCode)
            {
                dictionary = NewDictionary();
                nextCode = 258;
                codeSize = 9;
                prev = null;
                continue;
            }
            if (code == endCode)
                break;
            byte[]? entry = code is >= 0 and < 4096 ? dictionary[code] : null;
            if (entry is null)
            {
                if (prev is not null && code == nextCode)
                    entry = [.. prev, prev[0]];
                else
                    throw Py.ValueError("invalid TIFF LZW stream");
            }
            output.Write(entry);
            if (output.Length > MaxDecodedBytes)
                throw Py.ValueError("TIFF data too large");
            if (prev is not null && nextCode < 4096)
            {
                dictionary[nextCode] = [.. prev, entry[0]];
                nextCode++;
                if (codeSize < 12 && nextCode == (1 << codeSize) - 1)
                    codeSize++;
            }
            prev = entry;
        }
        return output.ToArray();
    }

    private static byte[]?[] NewDictionary()
    {
        var dictionary = new byte[]?[4096];
        for (int i = 0; i < 256; i++)
            dictionary[i] = [(byte)i];
        return dictionary;
    }

    private sealed class MsbBitReader(byte[] data)
    {
        private int _pos;
        private long _bitBuffer;
        private int _bitCount;

        public int? Read(int bits)
        {
            while (_bitCount < bits)
            {
                if (_pos >= data.Length)
                    return null;
                _bitBuffer = (_bitBuffer << 8) | data[_pos];
                _pos++;
                _bitCount += 8;
            }
            _bitCount -= bits;
            int value = (int)((_bitBuffer >> _bitCount) & ((1L << bits) - 1));
            _bitBuffer &= (1L << _bitCount) - 1;
            return value;
        }
    }

    // ---- PNG helpers ----

    private static void ApplyPngFilter(int filterType, byte[] line, byte[] prev, int bpp)
    {
        switch (filterType)
        {
            case 0:
                return;
            case 1:
                for (int i = 0; i < line.Length; i++)
                    line[i] = (byte)(line[i] + (i >= bpp ? line[i - bpp] : 0));
                return;
            case 2:
                for (int i = 0; i < line.Length; i++)
                    line[i] = (byte)(line[i] + Prev(prev, i));
                return;
            case 3:
                for (int i = 0; i < line.Length; i++)
                    line[i] = (byte)(line[i] + (((i >= bpp ? line[i - bpp] : 0) + Prev(prev, i)) >> 1));
                return;
            case 4:
                for (int i = 0; i < line.Length; i++)
                {
                    int left = i >= bpp ? line[i - bpp] : 0;
                    int upLeft = i >= bpp ? Prev(prev, i - bpp) : 0;
                    line[i] = (byte)(line[i] + Paeth(left, Prev(prev, i), upLeft));
                }
                return;
            default:
                throw Py.ValueError("unsupported PNG filter");
        }
    }

    private static int Prev(byte[] prev, int i) => i < prev.Length ? prev[i] : throw Py.IndexError("bytearray index out of range");

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc)
            return a;
        return pb <= pc ? b : c;
    }

    // ---- shared ----

    /// <summary>Python slicing: clamps to the data, never throws.</summary>
    private static ReadOnlySpan<byte> Slice(byte[] data, long start, long length)
    {
        if (start < 0 || length <= 0 || start >= data.Length)
            return [];
        long end = Math.Min(data.Length, start + length);
        return data.AsSpan((int)start, (int)(end - start));
    }

    private static HostErrorException StructError(long size) => new("error", $"unpack requires a buffer of {size} bytes");
}
