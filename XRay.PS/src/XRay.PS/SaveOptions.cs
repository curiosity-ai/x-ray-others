// Ported from the PdfSaveOptions / ImageSaveOptions types of ps/output.py, shared by the PS/EPS
// and XPS converters.

using XRay.PS.Fonts;
using XRay.PS.Imaging;
using XRay.PS.PostScript;

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

    /// <summary>Re-encode embedded raw 8-bit gray and RGB images as JPEG at this quality
    /// (1-100), when that is smaller than the deflated pixels. Null (the default) passes images
    /// through unchanged. Images that are already compressed, masks, and other color spaces are
    /// never touched.</summary>
    public int? JpegQuality { get; set; }

    /// <summary>Recover from errors instead of throwing. PostScript: on a fatal error the page
    /// being drawn is abandoned but kept as drawn so far, interpretation resumes after the next
    /// <c>showpage</c>, and a warning is recorded; a runaway job (an exceeded
    /// <see cref="Limits"/> ceiling) ends interpretation and keeps the pages so far. XPS: an
    /// element that cannot be rendered is skipped and a warning is recorded. Cancellation is
    /// never suppressed. Default false: the first error throws.</summary>
    public bool SuppressErrors { get; set; }

    /// <summary>Receives a <see cref="ConversionWarning"/> for every error suppressed by
    /// <see cref="SuppressErrors"/>, and (with or without it) for an XPS colour profile the
    /// conversion did not apply (code <c>color-profile</c>). Entries are appended, never cleared,
    /// so use a fresh options object (or clear the list) per document.</summary>
    public List<ConversionWarning> Warnings { get; set; } = [];

    /// <summary>Ceilings on the work and memory of a PostScript job (not used for XPS). The
    /// defaults apply when null.</summary>
    public PsInterpreterLimits? Limits { get; set; }
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

    /// <summary>PostScript: recover from errors instead of throwing, as
    /// <see cref="PdfSaveOptions.SuppressErrors"/>. XPS: skip unrenderable elements.</summary>
    public bool SuppressErrors { get; set; }

    /// <summary>Receives a warning for every error suppressed by <see cref="SuppressErrors"/>, and
    /// for an XPS colour profile the conversion did not apply (code <c>color-profile</c>).</summary>
    public List<ConversionWarning> Warnings { get; set; } = [];

    /// <summary>Ceilings on the work and memory of a PostScript job; the defaults when null. For
    /// the page raster itself (PostScript and XPS), <see cref="PsInterpreterLimits.MaxMemoryBytes"/>
    /// caps the pixel buffer; without <see cref="Limits"/> the cap is 4 GiB.</summary>
    public PsInterpreterLimits? Limits { get; set; }

    /// <summary>The cap on one page's pixel buffer when <see cref="Limits"/> is not set.</summary>
    internal const long DefaultMaxRasterBytes = 4L << 30;

    /// <summary>The cap on one page's pixel buffer in effect.</summary>
    internal long? MaxRasterBytes => Limits is { } limits ? limits.MaxMemoryBytes : DefaultMaxRasterBytes;

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
