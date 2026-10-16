// Ported from image/raster_writer.py: renders the first page with the managed rasterizer and
// encodes it.

using XRay.PS.Fonts;
using XRay.PS.Internal;
using XRay.PS.PostScript;
using XRay.PS.Rendering;

namespace XRay.PS.Imaging;

/// <summary>The built-in raster writer: the managed <see cref="RasterRenderer"/> and encoders.</summary>
public sealed class RenderModelRasterWriter : IRasterWriter
{
    /// <inheritdoc />
    public byte[] Write(RenderDocument document, ImageSaveOptions options)
    {
        using var ms = new MemoryStream();
        Write(document, options, ms, default);
        return ms.ToArray();
    }

    /// <inheritdoc />
    public byte[] Write(RenderDocument document, ImageSaveOptions options, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        Write(document, options, ms, cancellationToken);
        return ms.ToArray();
    }

    /// <summary>Renders the first page and writes it to <paramref name="output"/>. PNG is encoded
    /// straight into the stream; the other formats are encoded in memory first.</summary>
    public void Write(RenderDocument document, ImageSaveOptions options, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        if (options.Dpi <= 0)
            throw new ArgumentException("dpi must be positive", nameof(options));
        cancellationToken.ThrowIfCancellationRequested();
        using var guard = Guard.Begin(cancellationToken, options.MaxRasterBytes);
        var fontResolver = options.FontResolver ?? new FontResolver(options.AdditionalFontsFolder);
        bool transparent = options.Format == ImageFormat.Png && !options.OpaqueBackground;
        (byte, byte, byte, byte) background = transparent ? ((byte)0, (byte)0, (byte)0, (byte)0) : ((byte)255, (byte)255, (byte)255, (byte)255);
        var renderer = new RasterRenderer(options.Dpi, fontResolver, background, options.PreserveFractionalPageSize);
        var surface = renderer.Render(document, 0);
        Encode(surface, options, output, cancellationToken);
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

    /// <summary>Encodes a surface into a stream (PNG is streamed, the others buffered).</summary>
    public static void Encode(RasterSurface surface, ImageSaveOptions options, Stream output, CancellationToken cancellationToken = default)
    {
        if (options.Format == ImageFormat.Png)
        {
            RasterEncoders.WritePng(surface, output, options.Dpi, cancellationToken);
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();
        output.Write(Encode(surface, options));
    }
}
