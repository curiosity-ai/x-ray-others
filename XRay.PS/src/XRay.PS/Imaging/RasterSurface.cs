// Ported from the RasterSurface type of image/raster_renderer.py.

namespace XRay.PS.Imaging;

/// <summary>An in-memory RGBA pixel surface, top row first.</summary>
public sealed class RasterSurface
{
    /// <summary>Creates a surface over existing RGBA pixels.</summary>
    public RasterSurface(int width, int height, byte[] pixels)
    {
        if (width < 0 || height < 0 || pixels.Length != (long)width * height * 4)
            throw new ArgumentException("pixel buffer does not match the surface size");
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>RGBA bytes, four per pixel, rows top to bottom.</summary>
    public byte[] Pixels { get; }

    /// <summary>A per-pixel clip (non-zero paints), or null for no clip.</summary>
    internal byte[]? ClipMask { get; set; }

    /// <summary>Creates a surface filled with <paramref name="background"/>.</summary>
    public static RasterSurface Create(int width, int height, (byte R, byte G, byte B, byte A) background)
    {
        Internal.Guard.Charge((long)width * height * 4, "page raster");
        var pixels = new byte[checked((long)width * height * 4)];
        if (background != (0, 0, 0, 0))
        {
            uint value = (uint)(background.R | (background.G << 8) | (background.B << 16) | (background.A << 24));
            var span = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(pixels.AsSpan());
            span.Fill(BitConverter.IsLittleEndian ? value : System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(value));
        }
        return new RasterSurface(width, height, pixels);
    }

    internal void SetPixel(int x, int y, Rgba color)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
            return;
        int index = y * Width + x;
        if (ClipMask is not null && ClipMask[index] == 0)
            return;
        int idx = index * 4;
        Pixels[idx] = color.R;
        Pixels[idx + 1] = color.G;
        Pixels[idx + 2] = color.B;
        Pixels[idx + 3] = color.A;
    }

    internal Rgba GetPixel(int x, int y)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height)
            return default;
        int idx = (y * Width + x) * 4;
        return new Rgba(Pixels[idx], Pixels[idx + 1], Pixels[idx + 2], Pixels[idx + 3]);
    }
}

/// <summary>An 8-bit RGBA color.</summary>
internal readonly record struct Rgba(byte R, byte G, byte B, byte A)
{
    public static readonly Rgba Black = new(0, 0, 0, 255);

    public static Rgba FromInts(int r, int g, int b, int a) => new((byte)r, (byte)g, (byte)b, (byte)a);
}
