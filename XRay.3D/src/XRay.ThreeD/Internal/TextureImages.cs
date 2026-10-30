using System.Buffers.Binary;
using System.IO.Compression;

namespace XRay.ThreeD.Internal;

/// <summary>
/// Turns image files a model refers to into something glTF can carry: PNG, JPEG and WebP pass through (identified by their magic bytes, not
/// their names); BMP and TGA, which OBJ libraries often name, are decoded and re-encoded as PNG. Everything else is refused with a reason.
/// </summary>
internal static class TextureImages
{
    private const long MaxPixels = 64L * 1024 * 1024;

    /// <summary>The texture for <paramref name="data"/>, or null (with <paramref name="problem"/> saying why) when it is not an image this library can pass on.</summary>
    public static ModelTexture? Create(string name, byte[] data, out string? problem, CancellationToken cancel = default)
    {
        problem = null;
        var type = Sniff(data);
        if (type is not null)
        {
            return new ModelTexture { Name = name, MediaType = type, Data = data };
        }

        try
        {
            var png = data.Length >= 2 && data[0] == (byte)'B' && data[1] == (byte)'M' ? FromBmp(data, cancel)
                : LooksLikeTga(data, name) ? FromTga(data, cancel)
                : null;
            if (png is not null)
            {
                return new ModelTexture { Name = name, MediaType = "image/png", Data = png };
            }
        }
        catch (Exception ex) when (ex is ModelFormatException or IndexOutOfRangeException or ArgumentException or OverflowException or InvalidDataException)
        {
            problem = $"The texture '{name}' could not be decoded: {ex.Message}";
            return null;
        }

        problem = $"The texture '{name}' is in an image format glTF cannot carry and this library does not convert (PNG, JPEG, WebP, BMP and TGA are supported).";
        return null;
    }

    /// <summary>The media type a glTF viewer can load, from the leading bytes; null otherwise.</summary>
    public static string? Sniff(ReadOnlySpan<byte> d)
    {
        if (d.Length >= 8 && d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G' && d[4] == 0x0D && d[5] == 0x0A && d[6] == 0x1A && d[7] == 0x0A)
        {
            return "image/png";
        }

        if (d.Length >= 3 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (d.Length >= 12 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P')
        {
            return "image/webp";
        }

        return null;
    }

    private static byte[] FromBmp(byte[] d, CancellationToken cancel)
    {
        if (d.Length < 54)
        {
            throw new ModelFormatException("The BMP is truncated.");
        }

        var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(10));
        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(14));
        if (headerSize < 40)
        {
            throw new ModelFormatException("Only BITMAPINFOHEADER BMPs are supported.");
        }

        var width = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(18));
        var rawHeight = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(22));
        var planes = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(26));
        var bits = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(28));
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(30));
        var colours = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(46));
        var topDown = rawHeight < 0;
        var height = Math.Abs((long)rawHeight);
        CheckSize(width, height);
        if (planes != 1 || (compression != 0 && !(compression == 3 && bits == 32)) || bits is not (8 or 24 or 32))
        {
            throw new ModelFormatException("Only uncompressed 8, 24 and 32 bit BMPs are supported.");
        }

        var palette = Array.Empty<byte>();
        if (bits == 8)
        {
            var entries = (int)Math.Min(colours == 0 ? 256u : colours, 256u);
            var start = 14 + (long)headerSize;
            if (start + entries * 4L > d.Length)
            {
                throw new ModelFormatException("The BMP palette is truncated.");
            }

            palette = d.AsSpan((int)start, entries * 4).ToArray();
        }

        var stride = ((long)width * bits + 31) / 32 * 4;
        if (dataOffset + stride * height > d.Length)
        {
            throw new ModelFormatException("The BMP pixel data is truncated.");
        }

        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            Cancel.Every(cancel, y, 255);
            var row = (int)(dataOffset + stride * (topDown ? y : height - 1 - y));
            for (var x = 0; x < width; x++)
            {
                var o = (int)((y * (long)width + x) * 4);
                switch (bits)
                {
                    case 8:
                        {
                            var index = d[row + x] * 4;
                            if (index + 3 < palette.Length)
                            {
                                rgba[o] = palette[index + 2];
                                rgba[o + 1] = palette[index + 1];
                                rgba[o + 2] = palette[index];
                            }

                            rgba[o + 3] = 255;
                            break;
                        }

                    case 24:
                        rgba[o] = d[row + 3 * x + 2];
                        rgba[o + 1] = d[row + 3 * x + 1];
                        rgba[o + 2] = d[row + 3 * x];
                        rgba[o + 3] = 255;
                        break;
                    default:
                        rgba[o] = d[row + 4 * x + 2];
                        rgba[o + 1] = d[row + 4 * x + 1];
                        rgba[o + 2] = d[row + 4 * x];
                        rgba[o + 3] = d[row + 4 * x + 3];
                        break;
                }
            }
        }

        // A 32-bit BMP without an alpha channel stores zero there; treat an all-zero channel as opaque.
        if (bits == 32 && !rgba.Where((_, i) => i % 4 == 3).Any(a => a != 0))
        {
            for (var i = 3; i < rgba.Length; i += 4)
            {
                rgba[i] = 255;
            }
        }

        return EncodePng(rgba, width, (int)height, cancel);
    }

    private static bool LooksLikeTga(byte[] d, string name)
    {
        if (d.Length < 18 || d[1] > 1)
        {
            return false;
        }

        var type = d[2];
        if (type is not (2 or 3 or 10 or 11))
        {
            return false;
        }

        return name.EndsWith(".tga", StringComparison.OrdinalIgnoreCase) || d[16] is 8 or 24 or 32;
    }

    private static byte[] FromTga(byte[] d, CancellationToken cancel)
    {
        var idLength = d[0];
        var mapLength = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(5));
        var mapBits = d[7];
        var width = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(12));
        var height = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(14));
        var bits = d[16];
        var descriptor = d[17];
        var type = d[2];
        CheckSize(width, height);
        if (bits is not (8 or 24 or 32) || (type is 3 or 11) != (bits == 8))
        {
            throw new ModelFormatException("Only 8-bit grey and 24 or 32-bit true-colour TGAs are supported.");
        }

        var bytesPerPixel = bits / 8;
        var position = 18L + idLength + (d[1] == 1 ? mapLength * ((mapBits + 7) / 8L) : 0);
        var pixels = new byte[(long)width * height * bytesPerPixel];
        if (type is 2 or 3)
        {
            if (position + pixels.Length > d.Length)
            {
                throw new ModelFormatException("The TGA pixel data is truncated.");
            }

            Array.Copy(d, position, pixels, 0, pixels.Length);
        }
        else
        {
            var written = 0;
            while (written < pixels.Length)
            {
                cancel.ThrowIfCancellationRequested();
                if (position >= d.Length)
                {
                    throw new ModelFormatException("The TGA pixel data is truncated.");
                }

                var header = d[position++];
                var count = (header & 0x7F) + 1;
                if ((header & 0x80) != 0)
                {
                    if (position + bytesPerPixel > d.Length)
                    {
                        throw new ModelFormatException("The TGA pixel data is truncated.");
                    }

                    for (var i = 0; i < count && written < pixels.Length; i++)
                    {
                        for (var b = 0; b < bytesPerPixel && written < pixels.Length; b++)
                        {
                            pixels[written++] = d[position + b];
                        }
                    }

                    position += bytesPerPixel;
                }
                else
                {
                    var length = Math.Min((long)count * bytesPerPixel, pixels.Length - written);
                    if (position + length > d.Length)
                    {
                        throw new ModelFormatException("The TGA pixel data is truncated.");
                    }

                    Array.Copy(d, position, pixels, written, length);
                    written += (int)length;
                    position += (long)count * bytesPerPixel;
                }
            }
        }

        var topDown = (descriptor & 0x20) != 0;
        var rightToLeft = (descriptor & 0x10) != 0;
        var rgba = new byte[(long)width * height * 4];
        for (var y = 0; y < height; y++)
        {
            Cancel.Every(cancel, y, 255);
            var sourceRow = (topDown ? y : height - 1 - y) * width;
            for (var x = 0; x < width; x++)
            {
                var s = (sourceRow + (rightToLeft ? width - 1 - x : x)) * bytesPerPixel;
                var o = (y * width + x) * 4;
                if (bytesPerPixel == 1)
                {
                    rgba[o] = rgba[o + 1] = rgba[o + 2] = pixels[s];
                    rgba[o + 3] = 255;
                }
                else
                {
                    rgba[o] = pixels[s + 2];
                    rgba[o + 1] = pixels[s + 1];
                    rgba[o + 2] = pixels[s];
                    rgba[o + 3] = bytesPerPixel == 4 ? pixels[s + 3] : (byte)255;
                }
            }
        }

        return EncodePng(rgba, width, height, cancel);
    }

    private static void CheckSize(long width, long height)
    {
        if (width <= 0 || height <= 0 || width > 32768 || height > 32768 || width * height > MaxPixels)
        {
            throw new ModelFormatException("The image has unreasonable dimensions.");
        }
    }

    /// <summary>An 8-bit RGBA PNG (no filtering: the codec only has to be valid, the viewer decodes it once).</summary>
    internal static byte[] EncodePng(byte[] rgba, int width, int height, CancellationToken cancel = default)
    {
        using var output = new MemoryStream();
        output.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(output, "IHDR", header);

        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (var y = 0; y < height; y++)
            {
                Cancel.Every(cancel, y, 255);
                z.WriteByte(0);
                z.Write(rgba, y * width * 4, width * 4);
            }
        }

        WriteChunk(output, "IDAT", raw.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    private static void WriteChunk(Stream output, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        output.Write(length);
        var typed = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++)
        {
            typed[i] = (byte)type[i];
        }

        data.CopyTo(typed.AsSpan(4));
        output.Write(typed);
        BinaryPrimitives.WriteUInt32BigEndian(length, Crc32.Update(0, typed));
        output.Write(length);
    }
}
