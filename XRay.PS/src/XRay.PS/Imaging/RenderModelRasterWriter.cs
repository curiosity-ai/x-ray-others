// Ported from image/raster_writer.py: renders the first page with the managed rasterizer and
// encodes it.

using XRay.PS.Fonts;
using XRay.PS.Rendering;

namespace XRay.PS.Imaging;

/// <summary>The built-in raster writer: the managed <see cref="RasterRenderer"/> and encoders.</summary>
public sealed class RenderModelRasterWriter : IRasterWriter
{
    /// <inheritdoc />
    public byte[] Write(RenderDocument document, ImageSaveOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Dpi <= 0)
            throw new ArgumentException("dpi must be positive", nameof(options));
        var fontResolver = options.FontResolver ?? new FontResolver(options.AdditionalFontsFolder);
        bool transparent = options.Format == ImageFormat.Png && !options.OpaqueBackground;
        (byte, byte, byte, byte) background = transparent ? ((byte)0, (byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255, (byte)255);
        var renderer = new RasterRenderer(options.Dpi, fontResolver, background, options.PreserveFractionalPageSize);
        var surface = renderer.Render(document, 0);
        return Encode(surface, options);
    }

    /// <summary>Encodes a surface in the requested format.</summary>
    public static byte[] Encode(RasterSurface surface, ImageSaveOptions options) => options.Format switch
    {
        ImageFormat.Png => RasterEncoders.EncodePng(surface, options.Dpi),
        ImageFormat.Jpeg => RasterEncoders.EncodeJpeg(surface, options.JpegQuality ?? 75),
        ImageFormat.Tiff => RasterEncoders.EncodeTiff(surface),
        ImageFormat.Bmp => RasterEncoders.EncodeBmp(surface),
        _ => throw new ArgumentException($"unsupported format {options.Format}", nameof(options)),
    };
}
