// Ported from ps/output.py: to_pdf, to_image and the PDF metadata helper.

using System.Globalization;
using XRay.PS.Imaging;
using XRay.PS.Pdf;
using XRay.PS.Rendering;

namespace XRay.PS.PostScript;

public static partial class PsConverter
{
    /// <summary>The producer string written to PDF output.</summary>
    public const string Producer = "XRay.PS";

    /// <summary>Converts a PS/EPS document to PDF bytes.</summary>
    public static byte[] ToPdf(PsDocument document, PdfSaveOptions? options = null) => ToPdf(document, options, default);

    /// <summary>Converts a PS/EPS document to PDF bytes, observing a cancellation token.</summary>
    public static byte[] ToPdf(PsDocument document, PdfSaveOptions? options, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        ToPdf(document, ms, options, cancellationToken);
        return ms.ToArray();
    }

    /// <summary>Converts a PS/EPS document to PDF and writes it to <paramref name="output"/>.
    /// The PDF objects are written one at a time, so the document is never held a second time
    /// as one byte array; the stream need not be seekable.</summary>
    public static void ToPdf(PsDocument document, Stream output, PdfSaveOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(output);
        var opts = options ?? new PdfSaveOptions();
        var result = BuildRenderModel(document, opts.AdditionalFontsFolder, opts.Limits, opts.SuppressErrors, opts.Warnings, cancellationToken);
        WritePdf(result, BuildPdfMetadata(document.Dsc), opts.NoCompression, output, cancellationToken, opts.JpegQuality);
    }

    /// <summary>Writes an interpreted job's render model as PDF.</summary>
    internal static byte[] WritePdf(PsRenderResult result, PdfMetadata metadata, bool noCompression)
    {
        using var ms = new MemoryStream();
        WritePdf(result, metadata, noCompression, ms, default, null);
        return ms.ToArray();
    }

    internal static void WritePdf(PsRenderResult result, PdfMetadata metadata, bool noCompression, Stream output,
        CancellationToken cancellationToken, int? jpegQuality)
    {
        var store = result.Images;
        RenderImageResource ImageProvider(string imageId) => ToRenderImage(store.Get(imageId));
        PdfEmbeddedFont? FontProvider(string fontName, ISet<int> usedCodes) =>
            PdfFontEmbedder.BuildEmbeddedFont(fontName, usedCodes, result.Fonts);
        var writer = new PdfWriter(metadata, noCompression, ImageProvider, FontProvider, jpegQuality);
        writer.Write(result.Document, output, cancellationToken);
    }

    /// <summary>Converts the first page of a PS/EPS document to an image.</summary>
    public static byte[] ToImage(PsDocument document, ImageSaveOptions options) => ToImage(document, options, default);

    /// <summary>Converts the first page of a PS/EPS document to an image, observing a cancellation token.</summary>
    public static byte[] ToImage(PsDocument document, ImageSaveOptions options, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        ToImage(document, options, ms, cancellationToken);
        return ms.ToArray();
    }

    /// <summary>Converts the first page of a PS/EPS document to an image and writes it to
    /// <paramref name="output"/>. A PNG is encoded straight into the stream; the other formats
    /// are encoded in memory first (their encoders need the whole image).</summary>
    public static void ToImage(PsDocument document, ImageSaveOptions options, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        var result = BuildRenderModel(document, options.AdditionalFontsFolder, options.Limits, options.SuppressErrors, options.Warnings, cancellationToken);
        AttachImages(result);
        var opts = options.Clone();
        opts.FontResolver = result.Fonts;
        var writer = opts.RasterWriter ?? new RenderModelRasterWriter();
        writer.Write(result.Document, opts, output, cancellationToken);
    }

    /// <summary>Converts every page of a PS/EPS document to an image (the same rendering as
    /// <see cref="ToImage(PsDocument, ImageSaveOptions)"/> for each page).</summary>
    public static List<byte[]> ToImages(PsDocument document, ImageSaveOptions options, CancellationToken cancellationToken = default)
    {
        var outputs = new List<MemoryStream>();
        ToImages(document, options, _ =>
        {
            var ms = new MemoryStream();
            outputs.Add(ms);
            return ms;
        }, cancellationToken);
        return outputs.Select(m => m.ToArray()).ToList();
    }

    /// <summary>Converts every page to an image, asking <paramref name="streamForPage"/> for the
    /// stream of each page (zero-based page index) just before that page is written. The streams
    /// are not disposed.</summary>
    public static void ToImages(PsDocument document, ImageSaveOptions options, Func<int, Stream> streamForPage, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(streamForPage);
        var result = BuildRenderModel(document, options.AdditionalFontsFolder, options.Limits, options.SuppressErrors, options.Warnings, cancellationToken);
        AttachImages(result);
        var opts = options.Clone();
        opts.FontResolver = result.Fonts;
        var writer = opts.RasterWriter ?? new RenderModelRasterWriter();
        for (int i = 0; i < result.Document.Pages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageDoc = new RenderDocument { Pages = [result.Document.Pages[i]], Resources = result.Document.Resources };
            writer.Write(pageDoc, opts, streamForPage(i), cancellationToken);
        }
    }

    /// <summary>Copies the job's image payloads into the render document's resources.</summary>
    internal static void AttachImages(PsRenderResult result)
    {
        foreach (var (imageId, image) in result.Images.Items)
            result.Document.Resources.Images[imageId] = ToRenderImage(image);
    }

    private static RenderImageResource ToRenderImage(PsImageResource image) => new(
        image.Data, image.Width, image.Height, image.ColorSpace, image.BitsPerComponent, image.Filter,
        image.FilterParams, image.Decode, image.Mask, image.MaskPolarity);

    /// <summary>Builds the PDF information dictionary from DSC comments.</summary>
    public static PdfMetadata BuildPdfMetadata(DscMetadata? dsc)
    {
        string title = string.IsNullOrEmpty(dsc?.Title) ? "" : dsc.Title;
        string creator = string.IsNullOrEmpty(dsc?.Creator) ? "" : dsc.Creator;
        string timestamp = PdfTimestamp();
        return new PdfMetadata(title, creator, Producer, timestamp, timestamp, false);
    }

    internal static string PdfTimestamp() => "D:" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
}
