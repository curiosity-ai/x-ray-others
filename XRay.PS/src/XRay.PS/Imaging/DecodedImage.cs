// A decoded raster in one of the imaging-library modes the original's code paths observed
// ("1", "L", "LA", "P", "RGB", "RGBA", "CMYK"), with that library's mode conversions.

namespace XRay.PS.Imaging;

/// <summary>Decoded pixels in a named mode, interleaved, top row first.</summary>
internal sealed class DecodedImage
{
    public DecodedImage(string mode, int width, int height, byte[] pixels, byte[]? palette = null)
    {
        Mode = mode;
        Width = width;
        Height = height;
        Pixels = pixels;
        Palette = palette;
    }

    /// <summary>"1" (stored as 0/255 bytes), "L", "LA", "P", "RGB", "RGBA" or "CMYK".</summary>
    public string Mode { get; }
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    /// <summary>RGB triplets for mode "P".</summary>
    public byte[]? Palette { get; }

    /// <summary>Palette alpha for mode "P" (from tRNS), when present.</summary>
    public byte[]? PaletteAlpha { get; init; }

    /// <summary>A transparent gray or RGB color (tRNS) for modes "L" and "RGB".</summary>
    public int[]? TransparentColor { get; init; }

    public double? XDpi { get; init; }
    public double? YDpi { get; init; }

    public int Bands => Mode switch
    {
        "LA" => 2,
        "RGB" => 3,
        "RGBA" or "CMYK" => 4,
        _ => 1,
    };

    /// <summary>Whether the mode has an alpha band.</summary>
    public bool HasAlphaBand => Mode is "LA" or "RGBA";

    /// <summary>Converts to RGB bytes.</summary>
    public byte[] ToRgb()
    {
        int n = Width * Height;
        var rgb = new byte[n * 3];
        switch (Mode)
        {
            case "RGB":
                Buffer.BlockCopy(Pixels, 0, rgb, 0, Math.Min(Pixels.Length, rgb.Length));
                break;
            case "RGBA":
                for (int i = 0; i < n; i++)
                {
                    rgb[i * 3] = Pixels[i * 4];
                    rgb[i * 3 + 1] = Pixels[i * 4 + 1];
                    rgb[i * 3 + 2] = Pixels[i * 4 + 2];
                }
                break;
            case "L":
            case "1":
                for (int i = 0; i < n; i++)
                    rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = Pixels[i];
                break;
            case "LA":
                for (int i = 0; i < n; i++)
                    rgb[i * 3] = rgb[i * 3 + 1] = rgb[i * 3 + 2] = Pixels[i * 2];
                break;
            case "P":
                for (int i = 0; i < n; i++)
                {
                    int p = Pixels[i] * 3;
                    if (Palette is not null && p + 2 < Palette.Length)
                    {
                        rgb[i * 3] = Palette[p];
                        rgb[i * 3 + 1] = Palette[p + 1];
                        rgb[i * 3 + 2] = Palette[p + 2];
                    }
                }
                break;
            case "CMYK":
                for (int i = 0; i < n; i++)
                {
                    int nk = 255 - Pixels[i * 4 + 3];
                    rgb[i * 3] = Clip8(nk - MulDiv255(Pixels[i * 4], nk));
                    rgb[i * 3 + 1] = Clip8(nk - MulDiv255(Pixels[i * 4 + 1], nk));
                    rgb[i * 3 + 2] = Clip8(nk - MulDiv255(Pixels[i * 4 + 2], nk));
                }
                break;
            default:
                throw new InvalidOperationException("unsupported mode " + Mode);
        }
        return rgb;
    }

    /// <summary>Converts to RGBA bytes (transparency information becomes alpha).</summary>
    public byte[] ToRgba()
    {
        int n = Width * Height;
        var rgba = new byte[n * 4];
        if (Mode == "RGBA")
        {
            Buffer.BlockCopy(Pixels, 0, rgba, 0, Math.Min(Pixels.Length, rgba.Length));
            return rgba;
        }
        if (Mode == "LA")
        {
            for (int i = 0; i < n; i++)
            {
                rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = Pixels[i * 2];
                rgba[i * 4 + 3] = Pixels[i * 2 + 1];
            }
            return rgba;
        }
        var rgb = ToRgb();
        for (int i = 0; i < n; i++)
        {
            rgba[i * 4] = rgb[i * 3];
            rgba[i * 4 + 1] = rgb[i * 3 + 1];
            rgba[i * 4 + 2] = rgb[i * 3 + 2];
            byte a = 255;
            if (Mode == "P" && PaletteAlpha is not null && Pixels[i] < PaletteAlpha.Length)
                a = PaletteAlpha[Pixels[i]];
            else if (Mode == "L" && TransparentColor is { Length: 1 } t && Pixels[i] == t[0])
                a = 0;
            else if (Mode == "RGB" && TransparentColor is { Length: 3 } c && rgb[i * 3] == c[0] && rgb[i * 3 + 1] == c[1] && rgb[i * 3 + 2] == c[2])
                a = 0;
            rgba[i * 4 + 3] = a;
        }
        return rgba;
    }

    /// <summary>Converts to 8-bit luminance with the ITU-R 601-2 weights in 16-bit fixed point.</summary>
    public byte[] ToL()
    {
        int n = Width * Height;
        var l = new byte[n];
        switch (Mode)
        {
            case "L":
            case "1":
                Buffer.BlockCopy(Pixels, 0, l, 0, Math.Min(Pixels.Length, n));
                return l;
            case "LA":
                for (int i = 0; i < n; i++)
                    l[i] = Pixels[i * 2];
                return l;
            default:
            {
                var rgb = ToRgb();
                for (int i = 0; i < n; i++)
                    l[i] = (byte)((rgb[i * 3] * 19595 + rgb[i * 3 + 1] * 38470 + rgb[i * 3 + 2] * 7471 + 0x8000) >> 16);
                return l;
            }
        }
    }

    /// <summary>The alpha channel of the RGBA conversion.</summary>
    public byte[] AlphaChannel()
    {
        var rgba = ToRgba();
        var alpha = new byte[Width * Height];
        for (int i = 0; i < alpha.Length; i++)
            alpha[i] = rgba[i * 4 + 3];
        return alpha;
    }

    private static int MulDiv255(int a, int b)
    {
        int tmp = a * b + 128;
        return ((tmp >> 8) + tmp) >> 8;
    }

    private static byte Clip8(int v) => (byte)Math.Clamp(v, 0, 255);

    /// <summary>Decodes PNG, JPEG or TIFF bytes as the imaging library's <c>Image.open</c> would.</summary>
    public static DecodedImage? Open(byte[] data)
    {
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == (byte)'P' && data[2] == (byte)'N' && data[3] == (byte)'G')
            return PngDecoder.Decode(data);
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xD8)
            return JpegDecoder.DecodeImage(data);
        if (data.Length >= 4 && ((data[0] == (byte)'I' && data[1] == (byte)'I') || (data[0] == (byte)'M' && data[1] == (byte)'M')))
            return TiffDecoder.Decode(data);
        return null;
    }
}
