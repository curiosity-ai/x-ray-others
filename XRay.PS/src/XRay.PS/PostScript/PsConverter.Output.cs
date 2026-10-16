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
    public static byte[] ToPdf(PsDocument document, PdfSaveOptions? options = null)
    {
        var opts = options ?? new PdfSaveOptions();
        var result = BuildRenderModel(document, opts.AdditionalFontsFolder);
        return WritePdf(result, BuildPdfMetadata(document.Dsc), opts.NoCompression);
    }

    /// <summary>Writes an interpreted job's render model as PDF.</summary>
    internal static byte[] WritePdf(PsRenderResult result, PdfMetadata metadata, bool noCompression)
    {
        var store = result.Images;
        RenderImageResource ImageProvider(string imageId) => ToRenderImage(store.Get(imageId));
        PdfEmbeddedFont? FontProvider(string fontName, ISet<int> usedCodes) =>
            PdfFontEmbedder.BuildEmbeddedFont(fontName, usedCodes, result.Fonts);
        var writer = new PdfWriter(metadata, noCompression, ImageProvider, FontProvider);
        return writer.Write(result.Document);
    }

    /// <summary>Converts the first page of a PS/EPS document to an image.</summary>
    public static byte[] ToImage(PsDocument document, ImageSaveOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var result = BuildRenderModel(document, options.AdditionalFontsFolder);
        AttachImages(result);
        var opts = options.Clone();
        opts.FontResolver = result.Fonts;
        var writer = opts.RasterWriter ?? new RenderModelRasterWriter();
        return writer.Write(result.Document, opts);
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
