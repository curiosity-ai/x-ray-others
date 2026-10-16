// A baseline TIFF decoder (strips; none, LZW, PackBits and Deflate compression; horizontal
// predictor; gray, palette, RGB(A) and CMYK) for XPS image parts, which the original decoded
// through its imaging library.

using System.Buffers.Binary;
using XRay.PS.Internal;

namespace XRay.PS.Imaging;

internal static class TiffDecoder
{
    private sealed class FormatError(string message) : Exception(message);

    public static DecodedImage? Decode(byte[] data)
    {
        try
        {
            return DecodeCore(data);
        }
        catch (Exception ex) when (ex is FormatError or IndexOutOfRangeException or ArgumentException or OverflowException or InvalidDataException or HostErrorException)
        {
            return null;
        }
    }

    private static DecodedImage DecodeCore(byte[] data)
    {
        if (data.Length < 8)
            throw new FormatError("short TIFF");
        bool little = data[0] == 'I' && data[1] == 'I';
        if (!little && !(data[0] == 'M' && data[1] == 'M'))
            throw new FormatError("not a TIFF");
        int U16(long o) => o + 2 > data.Length ? throw new FormatError("truncated") : little ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)o)) : BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan((int)o));
        long U32(long o) => o + 4 > data.Length ? throw new FormatError("truncated") : little ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)o)) : BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)o));
        if (U16(2) != 42)
            throw new FormatError("bad TIFF magic");
        long ifd = U32(4);
        int count = U16(ifd);
        var tags = new Dictionary<int, long[]>();
        var rationals = new Dictionary<int, double>();
        for (int i = 0; i < count; i++)
        {
            long entry = ifd + 2 + i * 12L;
            int tag = U16(entry);
            int type = U16(entry + 2);
            long n = U32(entry + 4);
            int size = type switch
            {
                1 or 2 or 6 or 7 => 1,
                3 or 8 => 2,
                4 or 9 or 11 => 4,
                5 or 10 or 12 => 8,
                _ => 0,
            };
            if (size == 0 || n <= 0 || n > 1 << 24)
                continue;
            long total = size * n;
            long valueOffset = total <= 4 ? entry + 8 : U32(entry + 8);
            if (type is 5 or 10)
            {
                long num = U32(valueOffset);
                long den = U32(valueOffset + 4);
                rationals[tag] = den == 0 ? 0 : (double)num / den;
                continue;
            }
            var values = new long[n];
            for (long k = 0; k < n; k++)
            {
                long at = valueOffset + k * size;
                values[k] = size switch
                {
                    1 => at < data.Length ? data[at] : throw new FormatError("truncated"),
                    2 => U16(at),
                    _ => U32(at),
                };
            }
            tags[tag] = values;
        }
        long Tag(int t, long fallback) => tags.TryGetValue(t, out var v) && v.Length > 0 ? v[0] : fallback;
        int width = checked((int)Tag(256, 0));
        int height = checked((int)Tag(257, 0));
        if (width <= 0 || height <= 0 || (long)width * height > 400_000_000)
            throw new FormatError("bad size");
        int compression = (int)Tag(259, 1);
        int photometric = (int)Tag(262, 1);
        int samplesPerPixel = (int)Tag(277, 1);
        int bits = tags.TryGetValue(258, out var bps) && bps.Length > 0 ? (int)bps[0] : 1;
        int planar = (int)Tag(284, 1);
        int predictor = (int)Tag(317, 1);
        long rowsPerStrip = Tag(278, height);
        if (rowsPerStrip <= 0 || rowsPerStrip > height)
            rowsPerStrip = height;
        if (!tags.TryGetValue(273, out var offsets) || !tags.TryGetValue(279, out var byteCounts))
            throw new FormatError("tiled or missing strips");
        if (bits is not (1 or 2 or 4 or 8))
            throw new FormatError("unsupported bit depth");
        if (samplesPerPixel is < 1 or > 5)
            throw new FormatError("unsupported samples");
        int planes = planar == 2 ? samplesPerPixel : 1;
        int samplesPerPlanePixel = planar == 2 ? 1 : samplesPerPixel;
        int rowBytes = checked((width * samplesPerPlanePixel * bits + 7) / 8);
        var planeData = new byte[planes][];
        for (int p = 0; p < planes; p++)
            planeData[p] = new byte[checked(rowBytes * height)];
        int stripsPerPlane = (int)((height + rowsPerStrip - 1) / rowsPerStrip);
        for (int s = 0; s < offsets.Length && s < byteCounts.Length; s++)
        {
            int plane = s / stripsPerPlane;
            int strip = s % stripsPerPlane;
            if (plane >= planes)
                break;
            long start = offsets[s];
            long length = byteCounts[s];
            if (start < 0 || start >= data.Length)
                throw new FormatError("bad strip offset");
            length = Math.Min(length, data.Length - start);
            var encoded = data.AsSpan((int)start, (int)length);
            int stripRows = (int)Math.Min(rowsPerStrip, height - strip * rowsPerStrip);
            int expected = rowBytes * stripRows;
            byte[] decoded = compression switch
            {
                1 => encoded.ToArray(),
                5 => Lzw(encoded, expected),
                8 or 32946 => Zlib.DecompressLenient(encoded),
                32773 => PackBits(encoded, expected),
                _ => throw new FormatError("unsupported compression"),
            };
            if (predictor == 2)
                UndoPredictor(decoded, rowBytes, samplesPerPlanePixel, bits);
            int dest = (int)(strip * rowsPerStrip * rowBytes);
            Array.Copy(decoded, 0, planeData[plane], dest, Math.Min(decoded.Length, Math.Min(expected, planeData[plane].Length - dest)));
        }
        // Interleave to one byte per sample.
        int spp = samplesPerPixel;
        var samples = new byte[checked(width * height * spp)];
        int maxValue = (1 << bits) - 1;
        for (int y = 0; y < height; y++)
        {
            Internal.Guard.Check();
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < spp; c++)
                {
                    int plane = planar == 2 ? c : 0;
                    int sampleIndex = planar == 2 ? x : x * spp + c;
                    int value;
                    if (bits == 8)
                    {
                        value = planeData[plane][y * rowBytes + sampleIndex];
                    }
                    else
                    {
                        int bitIndex = sampleIndex * bits;
                        value = (planeData[plane][y * rowBytes + (bitIndex >> 3)] >> (8 - bits - (bitIndex & 7))) & maxValue;
                    }
                    samples[(y * width + x) * spp + c] = (byte)value;
                }
            }
        }
        var (xDpi, yDpi) = Resolution(rationals, tags);
        switch (photometric)
        {
            case 0:
            case 1:
            {
                var l = new byte[width * height];
                for (int i = 0; i < l.Length; i++)
                {
                    int v = samples[i * spp];
                    if (bits < 8)
                        v = v * 255 / maxValue;
                    l[i] = (byte)(photometric == 0 ? 255 - v : v);
                }
                return new DecodedImage(bits == 1 ? "1" : "L", width, height, l) { XDpi = xDpi, YDpi = yDpi };
            }
            case 2:
            {
                if (spp < 3)
                    throw new FormatError("bad RGB samples");
                bool alpha = spp >= 4;
                int outChannels = alpha ? 4 : 3;
                var px = new byte[width * height * outChannels];
                for (int i = 0; i < width * height; i++)
                {
                    for (int c = 0; c < outChannels; c++)
                        px[i * outChannels + c] = samples[i * spp + c];
                }
                return new DecodedImage(alpha ? "RGBA" : "RGB", width, height, px) { XDpi = xDpi, YDpi = yDpi };
            }
            case 3:
            {
                if (!tags.TryGetValue(320, out var colorMap) || colorMap.Length < 3 * (1 << bits))
                    throw new FormatError("missing color map");
                int entries = 1 << bits;
                var palette = new byte[entries * 3];
                for (int i = 0; i < entries; i++)
                {
                    palette[i * 3] = (byte)(colorMap[i] >> 8);
                    palette[i * 3 + 1] = (byte)(colorMap[entries + i] >> 8);
                    palette[i * 3 + 2] = (byte)(colorMap[2 * entries + i] >> 8);
                }
                var idx = new byte[width * height];
                for (int i = 0; i < idx.Length; i++)
                    idx[i] = samples[i * spp];
                return new DecodedImage("P", width, height, idx, palette) { XDpi = xDpi, YDpi = yDpi };
            }
            case 5:
            {
                if (spp < 4)
                    throw new FormatError("bad CMYK samples");
                var px = new byte[width * height * 4];
                for (int i = 0; i < width * height; i++)
                {
                    for (int c = 0; c < 4; c++)
                        px[i * 4 + c] = samples[i * spp + c];
                }
                return new DecodedImage("CMYK", width, height, px) { XDpi = xDpi, YDpi = yDpi };
            }
            default:
                throw new FormatError("unsupported photometric interpretation");
        }
    }

    // The imaging library's rule: resolution defaults to 1, unit 2 (or none) is inches, 3 is cm.
    private static (double?, double?) Resolution(Dictionary<int, double> rationals, Dictionary<int, long[]> tags)
    {
        double xres = rationals.TryGetValue(282, out double xr) ? xr : 1;
        double yres = rationals.TryGetValue(283, out double yr) ? yr : 1;
        if (xres == 0 || yres == 0)
            return (null, null);
        long? unit = tags.TryGetValue(296, out var u) && u.Length > 0 ? u[0] : null;
        if (unit is null or 2)
            return (xres, yres);
        if (unit == 3)
            return (xres * 2.54, yres * 2.54);
        return (null, null);
    }

    private static void UndoPredictor(byte[] data, int rowBytes, int samplesPerPixel, int bits)
    {
        if (bits != 8)
            return;
        for (int row = 0; row + rowBytes <= data.Length; row += rowBytes)
        {
            Internal.Guard.Check();
            for (int i = samplesPerPixel; i < rowBytes; i++)
                data[row + i] = (byte)(data[row + i] + data[row + i - samplesPerPixel]);
        }
    }

    private static byte[] PackBits(ReadOnlySpan<byte> input, int expected)
    {
        var output = new List<byte>(expected);
        int i = 0;
        while (i < input.Length && output.Count < expected)
        {
            int n = (sbyte)input[i++];
            if (n >= 0)
            {
                for (int k = 0; k <= n && i < input.Length; k++)
                    output.Add(input[i++]);
            }
            else if (n != -128 && i < input.Length)
            {
                byte b = input[i++];
                for (int k = 0; k < 1 - n; k++)
                    output.Add(b);
            }
        }
        return [.. output];
    }

    // TIFF LZW: MSB-first codes, 9 to 12 bits, early code-width change.
    private static byte[] Lzw(ReadOnlySpan<byte> input, int expected)
    {
        var output = new List<byte>(expected);
        var table = new List<byte[]>(4096);
        void Reset()
        {
            table.Clear();
            for (int i = 0; i < 256; i++)
                table.Add([(byte)i]);
            table.Add([]);
            table.Add([]);
        }
        Reset();
        int bitPos = 0;
        int codeWidth = 9;
        byte[]? previous = null;
        int totalBits = input.Length * 8;
        while (bitPos + codeWidth <= totalBits)
        {
            int code = 0;
            for (int k = 0; k < codeWidth; k++)
            {
                int bit = (input[(bitPos + k) >> 3] >> (7 - ((bitPos + k) & 7))) & 1;
                code = (code << 1) | bit;
            }
            bitPos += codeWidth;
            if (code == 256)
            {
                Reset();
                codeWidth = 9;
                previous = null;
                continue;
            }
            if (code == 257)
                break;
            byte[] entry;
            if (code < table.Count)
            {
                entry = table[code];
                if (previous is not null)
                {
                    var add = new byte[previous.Length + 1];
                    previous.CopyTo(add, 0);
                    add[^1] = entry[0];
                    table.Add(add);
                }
            }
            else if (previous is not null && code == table.Count)
            {
                entry = new byte[previous.Length + 1];
                previous.CopyTo(entry, 0);
                entry[^1] = previous[0];
                table.Add(entry);
            }
            else
            {
                break;
            }
            output.AddRange(entry);
            previous = entry;
            if (table.Count + 1 >= (1 << codeWidth) && codeWidth < 12)
                codeWidth++;
            if (output.Count >= expected)
                break;
        }
        return [.. output];
    }
}
