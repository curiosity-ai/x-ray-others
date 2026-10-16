// Ported from xps/images.py: XPS image decoding and storage.

using System.Buffers.Binary;
using XRay.PS.Imaging;
using XRay.PS.Internal;

namespace XRay.PS.Xps;

/// <summary>A decoded (or passed-through JPEG) image resource.</summary>
public sealed record XpsImageResource(
    string ImageId,
    byte[] Data,
    int Width,
    int Height,
    int BitsPerComponent,
    string ColorSpace,
    string? Filter,
    double XDpi = 96.0,
    double YDpi = 96.0,
    byte[]? SoftMask = null,
    string? SourceFormat = null);

/// <summary>Stores image resources by id (<c>ximg1</c>, <c>ximg2</c>, ...).</summary>
public sealed class XpsImageStore
{
    private readonly Dictionary<string, XpsImageResource> _images = new(StringComparer.Ordinal);
    private int _counter = 1;

    /// <summary>Registers an image and returns its id.</summary>
    public string Register(XpsImageResource resource)
    {
        string imageId = string.IsNullOrEmpty(resource.ImageId) ? $"ximg{_counter}" : resource.ImageId;
        _counter++;
        if (imageId != resource.ImageId)
            resource = resource with { ImageId = imageId };
        _images[imageId] = resource;
        return imageId;
    }

    /// <summary>Returns a registered image.</summary>
    public XpsImageResource Get(string imageId) =>
        _images.TryGetValue(imageId, out var image) ? image : throw new KeyNotFoundException($"unknown image resource {imageId}");

    public bool TryGet(string imageId, out XpsImageResource image) => _images.TryGetValue(imageId, out image!);

    public IEnumerable<KeyValuePair<string, XpsImageResource>> Items => _images;
}

/// <summary>Image decoders for XPS image parts.</summary>
public static class XpsImageDecoders
{
    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Decodes a PNG into RGB with an optional soft mask (8-bit gray, palette, RGB and RGBA).</summary>
    public static XpsImageResource DecodePng(byte[] data)
    {
        if (!data.AsSpan().StartsWith(PngSignature))
            throw Py.ValueError("invalid PNG signature");
        int width = 0, height = 0;
        int? bitDepth = null, colorType = null;
        using var idat = new MemoryStream();
        byte[]? palette = null;
        byte[]? transparency = null;
        int offset = 8;
        while (offset + 8 <= data.Length)
        {
            long length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
            string chunkType = System.Text.Encoding.Latin1.GetString(data, offset + 4, 4);
            int start = offset + 8;
            int available = (int)Math.Max(0, Math.Min(length, data.Length - start));
            var chunk = data.AsSpan(start, Math.Max(0, available));
            offset = (int)Math.Min((long)offset + 12 + length, int.MaxValue);
            if (chunkType == "IHDR")
            {
                if (chunk.Length < 10)
                    throw new HostErrorException("struct.error", "unpack requires a buffer of 10 bytes");
                width = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(chunk), int.MaxValue);
                height = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(chunk[4..]), int.MaxValue);
                bitDepth = chunk[8];
                colorType = chunk[9];
            }
            else if (chunkType == "IDAT")
            {
                idat.Write(chunk);
            }
            else if (chunkType == "PLTE")
            {
                palette = chunk.ToArray();
            }
            else if (chunkType == "tRNS")
            {
                transparency = chunk.ToArray();
            }
            else if (chunkType == "IEND")
            {
                break;
            }
        }
        if (colorType is not (0 or 2 or 3 or 6))
            throw Py.ValueError("unsupported PNG format");
        if (colorType == 0 && bitDepth is not (1 or 2 or 4 or 8))
            throw Py.ValueError("unsupported PNG format");
        if (colorType is 2 or 6 && bitDepth != 8)
            throw Py.ValueError("unsupported PNG format");
        if (colorType == 3 && bitDepth is not (1 or 2 or 4 or 8))
            throw Py.ValueError("unsupported PNG format");
        int ct = colorType!.Value;
        int bd = bitDepth!.Value;
        byte[] decompressed = Zlib.Decompress(idat.ToArray());
        int bytesPerPixel = ct == 2 ? 3 : ct == 6 ? 4 : 1;
        long strideLong = ((long)width * (ct is 0 or 3 ? 1 : ct == 2 ? 3 : 4) * bd + 7) / 8;
        if (strideLong > int.MaxValue / 2 || (long)width * height > 400_000_000)
            throw Py.ValueError("PNG too large");
        int stride = (int)strideLong;
        using var raw = new MemoryStream();
        MemoryStream? alpha = (ct is 0 or 3 or 6 && transparency is not null) || ct == 6 ? new MemoryStream() : null;
        int idx = 0;
        var prev = new byte[stride];
        int? transparentSample = ct == 0 && transparency is { Length: >= 2 } ? BinaryPrimitives.ReadUInt16BigEndian(transparency) : null;
        for (int row = 0; row < height; row++)
        {
            if (idx >= decompressed.Length)
                throw new HostErrorException("IndexError", "index out of range");
            int filterType = decompressed[idx];
            idx++;
            int lineLength = Math.Max(0, Math.Min(stride, decompressed.Length - idx));
            var line = new byte[lineLength];
            Array.Copy(decompressed, idx, line, 0, lineLength);
            idx += stride;
            ApplyPngFilter(filterType, line, prev, bytesPerPixel);
            if (ct == 6)
            {
                for (int i = 0; i < line.Length; i += 4)
                {
                    raw.Write(line, i, Math.Min(3, line.Length - i));
                    if (i + 3 < line.Length)
                        alpha!.WriteByte(line[i + 3]);
                }
            }
            else if (ct == 2)
            {
                raw.Write(line);
            }
            else if (ct == 0)
            {
                foreach (int sample in UnpackSamples(line, width, bd))
                {
                    byte gray = ScaleSample(sample, bd);
                    raw.WriteByte(gray);
                    raw.WriteByte(gray);
                    raw.WriteByte(gray);
                    if (alpha is not null)
                        alpha.WriteByte(transparentSample is int ts && sample == ts ? (byte)0 : (byte)255);
                }
            }
            else
            {
                if (palette is null)
                    throw Py.ValueError("palette PNG missing PLTE chunk");
                foreach (int sample in UnpackSamples(line, width, bd))
                {
                    int b = sample * 3;
                    if (b + 3 > palette.Length)
                        throw Py.ValueError("palette PNG index out of range");
                    raw.Write(palette, b, 3);
                    byte alphaValue = transparency is not null && sample < transparency.Length ? transparency[sample] : (byte)255;
                    alpha?.WriteByte(alphaValue);
                }
            }
            prev = line;
        }
        return new XpsImageResource("", raw.ToArray(), width, height, 8, "DeviceRGB", null,
            PngDpi(data, false) ?? 96.0, PngDpi(data, true) ?? 96.0, alpha?.ToArray());
    }

    private static List<int> UnpackSamples(byte[] line, int width, int bitDepth)
    {
        var samples = new List<int>(Math.Min(width, line.Length * 8));
        if (bitDepth == 8)
        {
            for (int i = 0; i < Math.Min(width, line.Length); i++)
                samples.Add(line[i]);
            return samples;
        }
        int mask = (1 << bitDepth) - 1;
        foreach (byte b in line)
        {
            int bitsRemaining = 8;
            while (bitsRemaining >= bitDepth && samples.Count < width)
            {
                bitsRemaining -= bitDepth;
                samples.Add((b >> bitsRemaining) & mask);
            }
            if (samples.Count >= width)
                break;
        }
        return samples;
    }

    private static byte ScaleSample(int sample, int bitDepth)
    {
        if (bitDepth >= 8)
            return (byte)Math.Clamp(sample, 0, 255);
        int maxSample = (1 << bitDepth) - 1;
        if (maxSample <= 0)
            return 0;
        return (byte)Math.Clamp(Py.Round(sample * 255.0 / maxSample), 0, 255);
    }

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
                {
                    int left = i >= bpp ? line[i - bpp] : 0;
                    line[i] = (byte)(line[i] + ((left + Prev(prev, i)) >> 1));
                }
                return;
            case 4:
                for (int i = 0; i < line.Length; i++)
                {
                    int left = i >= bpp ? line[i - bpp] : 0;
                    int up = Prev(prev, i);
                    int upLeft = i >= bpp ? Prev(prev, i - bpp) : 0;
                    line[i] = (byte)(line[i] + Paeth(left, up, upLeft));
                }
                return;
            default:
                throw Py.ValueError("unsupported PNG filter");
        }
    }

    // A short previous line raised IndexError in the original.
    private static int Prev(byte[] prev, int i) => i < prev.Length ? prev[i] : throw new HostErrorException("IndexError", "bytearray index out of range");

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc)
            return a;
        if (pb <= pc)
            return b;
        return c;
    }

    private static double? PngDpi(byte[] data, bool vertical)
    {
        int offset = 8;
        while (offset + 8 <= data.Length)
        {
            long length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
            string chunkType = System.Text.Encoding.Latin1.GetString(data, offset + 4, 4);
            int start = offset + 8;
            int available = (int)Math.Max(0, Math.Min(length, data.Length - start));
            var chunk = data.AsSpan(start, available);
            offset = (int)Math.Min((long)offset + 12 + length, int.MaxValue);
            if (chunkType == "pHYs" && chunk.Length >= 9)
            {
                uint xPpm = BinaryPrimitives.ReadUInt32BigEndian(chunk);
                uint yPpm = BinaryPrimitives.ReadUInt32BigEndian(chunk[4..]);
                if (chunk[8] == 1)
                {
                    uint ppm = vertical ? yPpm : xPpm;
                    if (ppm > 0)
                        return ppm * 0.0254;
                }
                return null;
            }
            if (chunkType == "IEND")
                break;
        }
        return null;
    }

    /// <summary>Reads a JPEG's size and resolution; the data stays DCT-encoded.</summary>
    public static XpsImageResource DecodeJpeg(byte[] data)
    {
        if (data.Length < 2 || data[0] != 0xFF || data[1] != 0xD8)
            throw Py.ValueError("invalid JPEG");
        int offset = 2;
        int? width = null, height = null, components = null;
        while (offset + 4 <= data.Length)
        {
            if (data[offset] != 0xFF)
                break;
            int marker = data[offset + 1];
            offset += 2;
            if (marker is 0xD8 or 0xD9)
                continue;
            int length = U16(data, offset);
            if (marker is 0xC0 or 0xC2)
            {
                height = U16(data, offset + 3);
                width = U16(data, offset + 5);
                components = offset + 7 < data.Length ? data[offset + 7] : throw new HostErrorException("IndexError", "index out of range");
                break;
            }
            offset += length;
        }
        if (width is null || height is null)
            throw Py.ValueError("JPEG size not found");
        var (xDpi, yDpi) = JpegDpi(data);
        return new XpsImageResource("", data, width.Value, height.Value, 8, components == 1 ? "DeviceGray" : "DeviceRGB", "DCTDecode", xDpi, yDpi);
    }

    private static int U16(byte[] data, int offset)
    {
        if (offset + 2 > data.Length)
            throw new HostErrorException("struct.error", "unpack requires a buffer of 2 bytes");
        return (data[offset] << 8) | data[offset + 1];
    }

    private static (double, double) JpegDpi(byte[] data)
    {
        int offset = 2;
        while (offset + 4 <= data.Length)
        {
            if (data[offset] != 0xFF)
                break;
            int marker = data[offset + 1];
            offset += 2;
            if (marker is 0xD8 or 0xD9)
                continue;
            if (offset + 2 > data.Length)
                break;
            int length = (data[offset] << 8) | data[offset + 1];
            if (length < 2 || offset + length > data.Length)
                break;
            if (marker == 0xE0 && length >= 14)
            {
                var payload = data.AsSpan(offset + 2, length - 2);
                if (payload.StartsWith("JFIF\0"u8) && payload.Length >= 12)
                {
                    int units = payload[7];
                    int xDensity = (payload[8] << 8) | payload[9];
                    int yDensity = (payload[10] << 8) | payload[11];
                    if (xDensity > 0 && yDensity > 0)
                    {
                        if (units == 1)
                            return (xDensity, yDensity);
                        if (units == 2)
                            return (xDensity * 2.54, yDensity * 2.54);
                    }
                }
            }
            offset += length;
        }
        return (96.0, 96.0);
    }

    /// <summary>Decodes a TIFF into RGB.</summary>
    public static XpsImageResource DecodeTiff(byte[] data)
    {
        var decoded = TiffDecoder.Decode(data) ?? throw Py.ValueError("invalid TIFF");
        var rgb = decoded.ToRgb();
        return new XpsImageResource("", rgb, decoded.Width, decoded.Height, 8, "DeviceRGB", null, decoded.XDpi ?? 96.0, decoded.YDpi ?? 96.0);
    }

    /// <summary>JPEG XR is not supported (the original needed an optional native codec for it).</summary>
    public static XpsImageResource DecodeJpegXr(byte[] data)
    {
        _ = data;
        throw Py.ValueError("JPEG-XR decoding is not supported");
    }
}
