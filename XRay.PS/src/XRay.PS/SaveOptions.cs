// Ported from the PdfSaveOptions / ImageSaveOptions types of ps/output.py, shared by the PS/EPS
// and XPS converters.

using XRay.PS.Fonts;
using XRay.PS.Imaging;

namespace XRay.PS;

/// <summary>Options for PDF output.</summary>
public sealed class PdfSaveOptions
{
    /// <summary>Write content streams uncompressed (useful for inspection).</summary>
    public bool NoCompression { get; set; }

    /// <summary>A folder of extra TrueType/OpenType fonts consulted before system fonts.</summary>
    public string? AdditionalFontsFolder { get; set; }

    /// <summary>XPS only: renders opacity-masked content to images for the PDF. The built-in
    /// managed rasterizer when null; the optional Skia writer reproduces the original output.</summary>
    public IRasterWriter? MaskRasterizer { get; set; }
}

/// <summary>Raster output formats.</summary>
public enum ImageFormat
{
    Png,
    Jpeg,
    Bmp,
    Tiff,
}

/// <summary>Options for raster output.</summary>
public sealed class ImageSaveOptions
{
    public ImageSaveOptions()
    {
    }

    public ImageSaveOptions(ImageFormat format, int dpi = 96)
    {
        Format = format;
        Dpi = dpi;
    }

    public ImageFormat Format { get; set; } = ImageFormat.Png;

    /// <summary>Output resolution in dots per inch.</summary>
    public int Dpi { get; set; } = 96;

    /// <summary>A custom rasterizer; the built-in managed renderer when null.</summary>
    public IRasterWriter? RasterWriter { get; set; }

    /// <summary>A folder of extra TrueType/OpenType fonts consulted before system fonts.</summary>
    public string? AdditionalFontsFolder { get; set; }

    /// <summary>Paint an opaque white background (PNG output is otherwise transparent).</summary>
    public bool OpaqueBackground { get; set; }

    /// <summary>Keep fractional page sizes instead of snapping them to whole pixels.</summary>
    public bool PreserveFractionalPageSize { get; set; }

    /// <summary>JPEG quality (1-100); null uses the writer's default (75 for the managed
    /// writer, 80 for the Skia writer, as in the original).</summary>
    public int? JpegQuality { get; set; }

    /// <summary>Internal plumbing: the interpreter's font resolver (with embedded font
    /// registrations), reused during rasterization.</summary>
    public FontResolver? FontResolver { get; set; }

    /// <summary>Parses a format name (png, jpeg/jpg, bmp, tiff/tif).</summary>
    public static ImageFormat ParseFormat(string name) => name.Trim().ToLowerInvariant() switch
    {
        "png" => ImageFormat.Png,
        "jpeg" or "jpg" => ImageFormat.Jpeg,
        "bmp" => ImageFormat.Bmp,
        "tiff" or "tif" => ImageFormat.Tiff,
        _ => throw new ArgumentException($"unsupported format {name}"),
    };

    internal ImageSaveOptions Clone() => (ImageSaveOptions)MemberwiseClone();
}
