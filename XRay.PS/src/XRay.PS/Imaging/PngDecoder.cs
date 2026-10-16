// A general PNG decoder (all color types, bit depths and Adam7 interlacing) producing the modes
// the original's imaging library reports, for the code paths that opened images generically.

using System.Buffers.Binary;
using XRay.PS.Internal;

namespace XRay.PS.Imaging;

internal static class PngDecoder
{
    public static DecodedImage? Decode(byte[] data)
    {
        try
        {
            return DecodeCore(data);
        }
        catch (Exception ex) when (ex is HostErrorException or IndexOutOfRangeException or ArgumentException or OverflowException or InvalidDataException)
        {
            return null;
        }
    }

    private static DecodedImage? DecodeCore(byte[] data)
    {
        int offset = 8;
        int width = 0, height = 0, bitDepth = 0, colorType = -1, interlace = 0;
        using var idat = new MemoryStream();
        byte[]? palette = null;
        byte[]? trns = null;
        double? xDpi = null, yDpi = null;
        while (offset + 8 <= data.Length)
        {
            int length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset)));
            string type = System.Text.Encoding.Latin1.GetString(data, offset + 4, 4);
            if (offset + 8 + length > data.Length)
                break;
            var chunk = data.AsSpan(offset + 8, length);
            offset += 12 + length;
            switch (type)
            {
                case "IHDR":
                    width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(chunk));
                    height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(chunk[4..]));
                    bitDepth = chunk[8];
                    colorType = chunk[9];
                    interlace = chunk[12];
                    break;
                case "PLTE":
                    palette = chunk.ToArray();
                    break;
                case "tRNS":
                    trns = chunk.ToArray();
                    break;
                case "IDAT":
                    idat.Write(chunk);
                    break;
                case "pHYs" when chunk.Length >= 9 && chunk[8] == 1:
                    xDpi = BinaryPrimitives.ReadUInt32BigEndian(chunk) * 0.0254;
                    yDpi = BinaryPrimitives.ReadUInt32BigEndian(chunk[4..]) * 0.0254;
                    break;
            }
            if (type == "IEND")
                break;
        }
        if (width <= 0 || height <= 0 || (long)width * height > 400_000_000)
            return null;
        int channels = colorType switch
        {
            0 => 1,
            2 => 3,
            3 => 1,
            4 => 2,
            6 => 4,
            _ => -1,
        };
        if (channels < 0 || bitDepth is not (1 or 2 or 4 or 8 or 16))
            return null;
        byte[] raw = Zlib.DecompressLenient(idat.ToArray());
        int bitsPerPixel = channels * bitDepth;
        int bpp = Math.Max(1, bitsPerPixel / 8);
        // Samples scaled to 8 bits, one byte per channel.
        var samples = new byte[checked(width * height * channels)];
        int pos = 0;
        void DecodePass(int x0, int y0, int dx, int dy)
        {
            int passWidth = (width - x0 + dx - 1) / dx;
            int passHeight = (height - y0 + dy - 1) / dy;
            if (passWidth <= 0 || passHeight <= 0)
                return;
            int stride = (passWidth * bitsPerPixel + 7) / 8;
            var prev = new byte[stride];
            var line = new byte[stride];
            for (int r = 0; r < passHeight; r++)
            {
                Internal.Guard.Check();
                if (pos + 1 + stride > raw.Length)
                    throw new InvalidDataException("truncated PNG data");
                int filter = raw[pos];
                Array.Copy(raw, pos + 1, line, 0, stride);
                pos += 1 + stride;
                Unfilter(filter, line, prev, bpp);
                int y = y0 + r * dy;
                for (int c = 0; c < passWidth; c++)
                {
                    int x = x0 + c * dx;
                    for (int ch = 0; ch < channels; ch++)
                        samples[(y * width + x) * channels + ch] = Sample(line, c * channels + ch, bitDepth, colorType);
                }
                (prev, line) = (line, prev);
            }
        }
        if (interlace == 1)
        {
            DecodePass(0, 0, 8, 8);
            DecodePass(4, 0, 8, 8);
            DecodePass(0, 4, 4, 8);
            DecodePass(2, 0, 4, 4);
            DecodePass(0, 2, 2, 4);
            DecodePass(1, 0, 2, 2);
            DecodePass(0, 1, 1, 2);
        }
        else
        {
            DecodePass(0, 0, 1, 1);
        }
        switch (colorType)
        {
            case 0:
            {
                string mode = bitDepth == 1 ? "1" : "L";
                int[]? transparent = null;
                if (trns is { Length: >= 2 })
                {
                    int value = BinaryPrimitives.ReadUInt16BigEndian(trns);
                    transparent = [bitDepth == 16 ? Math.Min(255, value) : ScaleGray(value, bitDepth)];
                }
                return new DecodedImage(mode, width, height, samples) { XDpi = xDpi, YDpi = yDpi, TransparentColor = transparent };
            }
            case 2:
            {
                int[]? transparent = null;
                if (trns is { Length: >= 6 } && bitDepth == 8)
                    transparent = [BinaryPrimitives.ReadUInt16BigEndian(trns), BinaryPrimitives.ReadUInt16BigEndian(trns.AsSpan(2)), BinaryPrimitives.ReadUInt16BigEndian(trns.AsSpan(4))];
                return new DecodedImage("RGB", width, height, samples) { XDpi = xDpi, YDpi = yDpi, TransparentColor = transparent };
            }
            case 3:
                return new DecodedImage("P", width, height, samples, palette ?? []) { XDpi = xDpi, YDpi = yDpi, PaletteAlpha = trns };
            case 4:
                return new DecodedImage("LA", width, height, samples) { XDpi = xDpi, YDpi = yDpi };
            default:
                return new DecodedImage("RGBA", width, height, samples) { XDpi = xDpi, YDpi = yDpi };
        }
    }

    private static int ScaleGray(int value, int bitDepth) => bitDepth switch
    {
        1 => value != 0 ? 255 : 0,
        2 => value * 85,
        4 => value * 17,
        _ => value & 0xFF,
    };

    private static byte Sample(byte[] line, int index, int bitDepth, int colorType)
    {
        switch (bitDepth)
        {
            case 8:
                return line[index];
            case 16:
                // The high byte, as the 16-bit modes reduce to 8 bits.
                return line[index * 2];
            default:
            {
                int bitIndex = index * bitDepth;
                int value = (line[bitIndex >> 3] >> (8 - bitDepth - (bitIndex & 7))) & ((1 << bitDepth) - 1);
                // Palette indices stay raw; gray levels scale to 0..255.
                return colorType == 3 ? (byte)value : (byte)ScaleGray(value, bitDepth);
            }
        }
    }

    private static void Unfilter(int filter, byte[] line, byte[] prev, int bpp)
    {
        switch (filter)
        {
            case 0:
                break;
            case 1:
                for (int i = bpp; i < line.Length; i++)
                    line[i] = (byte)(line[i] + line[i - bpp]);
                break;
            case 2:
                for (int i = 0; i < line.Length; i++)
                    line[i] = (byte)(line[i] + prev[i]);
                break;
            case 3:
                for (int i = 0; i < line.Length; i++)
                    line[i] = (byte)(line[i] + (((i >= bpp ? line[i - bpp] : 0) + prev[i]) >> 1));
                break;
            case 4:
                for (int i = 0; i < line.Length; i++)
                {
                    int a = i >= bpp ? line[i - bpp] : 0;
                    int b = prev[i];
                    int c = i >= bpp ? prev[i - bpp] : 0;
                    int p = a + b - c;
                    int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                    line[i] = (byte)(line[i] + (pa <= pb && pa <= pc ? a : pb <= pc ? b : c));
                }
                break;
            default:
                throw new InvalidDataException("bad PNG filter");
        }
    }
}
