// Ported from xps/output.py: XPS to PDF and raster conversion.

using System.Xml;
using XRay.PS.Fonts;
using XRay.PS.Imaging;
using XRay.PS.Internal;
using XRay.PS.Pdf;
using XRay.PS.PostScript;
using XRay.PS.Rendering;

namespace XRay.PS.Xps;

/// <summary>Converts XPS documents to PDF and raster images.</summary>
public static class XpsConverter
{
    /// <summary>Converts an XPS document to PDF bytes.</summary>
    public static byte[] ToPdf(XpsDocument document, PdfSaveOptions? options = null, int? maxPages = null) =>
        ToPdf(document, options, maxPages, default);

    /// <summary>Converts an XPS document to PDF bytes, observing a cancellation token.</summary>
    public static byte[] ToPdf(XpsDocument document, PdfSaveOptions? options, int? maxPages, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        ToPdf(document, ms, options, maxPages, cancellationToken);
        return ms.ToArray();
    }

    /// <summary>Converts an XPS document to PDF and writes it to <paramref name="output"/>; the PDF
    /// objects are written one at a time and the stream need not be seekable.</summary>
    public static void ToPdf(XpsDocument document, Stream output, PdfSaveOptions? options = null, int? maxPages = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(output);
        var opts = options ?? new PdfSaveOptions();
        cancellationToken.ThrowIfCancellationRequested();
        using var guard = Guard.Begin(cancellationToken);
        var (renderDoc, imageStore) = BuildPdfRenderModel(document, maxPages, opts.MaskRasterizer, opts.SuppressErrors, opts.Warnings, cancellationToken);
        var fontResolver = BuildXpsFontResolver(document.Package, renderDoc, opts.AdditionalFontsFolder);
        string timestamp = PsConverter.PdfTimestamp();
        var metadata = new PdfMetadata("", "", PsConverter.Producer, timestamp, timestamp, false);
        RenderImageResource ImageProvider(string imageId)
        {
            var image = imageStore.Get(imageId);
            return new RenderImageResource(image.Data, image.Width, image.Height, image.ColorSpace, image.BitsPerComponent, image.Filter, SoftMask: image.SoftMask);
        }
        PdfEmbeddedFont? FontProvider(string fontRef, ISet<int> usedCodes) => PdfFontEmbedder.BuildEmbeddedFont(fontRef, usedCodes, fontResolver);
        new PdfWriter(metadata, opts.NoCompression, ImageProvider, FontProvider, opts.JpegQuality).Write(renderDoc, output, cancellationToken);
    }

    /// <summary>The render model exactly as the PDF writer receives it (page sizes quantized to
    /// half points, image payloads attached).</summary>
    public static (RenderDocument Document, XpsImageStore Images) BuildPdfRenderModel(XpsDocument document, int? maxPages = null, IRasterWriter? maskRasterizer = null) =>
        BuildPdfRenderModel(document, maxPages, maskRasterizer, false, null, default);

    /// <summary>As <see cref="BuildPdfRenderModel(XpsDocument, int?, IRasterWriter?)"/> with error
    /// suppression (an element that cannot be rendered is skipped, with a warning) and
    /// cancellation.</summary>
    public static (RenderDocument Document, XpsImageStore Images) BuildPdfRenderModel(XpsDocument document, int? maxPages,
        IRasterWriter? maskRasterizer, bool suppressErrors, List<ConversionWarning>? warnings, CancellationToken cancellationToken)
    {
        var (renderDoc, imageStore) = BuildRenderDocument(document, maxPages, forPdf: true, maskRasterizer, suppressErrors, warnings, cancellationToken);
        QuantizePdfPageSizes(renderDoc);
        AttachImageResources(renderDoc, imageStore);
        return (renderDoc, imageStore);
    }

    /// <summary>Converts the first page to an image.</summary>
    public static byte[] ToImage(XpsDocument document, ImageSaveOptions options, int? maxPages = null) =>
        ToImage(document, options, maxPages, default);

    /// <summary>Converts the first page to an image, observing a cancellation token. Only the
    /// first page is rendered.</summary>
    public static byte[] ToImage(XpsDocument document, ImageSaveOptions options, int? maxPages, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        ToImage(document, options, ms, maxPages, cancellationToken);
        return ms.ToArray();
    }

    /// <summary>Converts the first page to an image and writes it to <paramref name="output"/>.</summary>
    public static void ToImage(XpsDocument document, ImageSaveOptions options, Stream output, int? maxPages = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);
        bool any = false;
        int limit = maxPages is int m ? Math.Min(m, 1) : 1;
        ToImages(document, options, _ =>
        {
            any = true;
            return output;
        }, limit, cancellationToken);
        if (!any)
            throw new InvalidOperationException("document has no pages");
    }

    /// <summary>Converts each fixed page to an image.</summary>
    public static List<byte[]> ToImages(XpsDocument document, ImageSaveOptions options, int? maxPages = null) =>
        ToImages(document, options, maxPages, default);

    /// <summary>Converts each fixed page to an image, observing a cancellation token.</summary>
    public static List<byte[]> ToImages(XpsDocument document, ImageSaveOptions options, int? maxPages, CancellationToken cancellationToken)
    {
        var outputs = new List<MemoryStream>();
        ToImages(document, options, _ =>
        {
            var ms = new MemoryStream();
            outputs.Add(ms);
            return ms;
        }, maxPages, cancellationToken);
        return outputs.Select(m => m.ToArray()).ToList();
    }

    /// <summary>Converts each fixed page to an image, taking the stream of each page (zero-based
    /// index) from <paramref name="streamForPage"/> just before that page is written. The streams
    /// are not disposed.</summary>
    public static void ToImages(XpsDocument document, ImageSaveOptions options, Func<int, Stream> streamForPage, int? maxPages = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(streamForPage);
        cancellationToken.ThrowIfCancellationRequested();
        using var guard = Guard.Begin(cancellationToken);
        var writer = options.RasterWriter ?? new RenderModelRasterWriter();
        var (renderDoc, imageStore) = BuildRenderDocument(document, maxPages, forPdf: false, writer, options.SuppressErrors, options.Warnings, cancellationToken);
        AttachImageResources(renderDoc, imageStore);
        var opts = options.Clone();
        opts.OpaqueBackground = true;
        opts.PreserveFractionalPageSize = true;
        opts.FontResolver = BuildXpsFontResolver(document.Package, renderDoc, options.AdditionalFontsFolder);
        for (int i = 0; i < renderDoc.Pages.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageDoc = new RenderDocument { Pages = [renderDoc.Pages[i]], Resources = renderDoc.Resources };
            writer.Write(pageDoc, opts, streamForPage(i), cancellationToken);
        }
    }

    /// <summary>Builds the render model of the document's pages.</summary>
    /// <param name="document">The document.</param>
    /// <param name="maxPages">Render at most this many pages.</param>
    /// <param name="forPdf">Use the PDF settings (hairline solid strokes rasterized).</param>
    /// <param name="maskRasterizer">Renders opacity-masked content to images.</param>
    public static (RenderDocument Document, XpsImageStore Images) BuildRenderDocument(XpsDocument document, int? maxPages = null, bool forPdf = false, IRasterWriter? maskRasterizer = null) =>
        BuildRenderDocument(document, maxPages, forPdf, maskRasterizer, false, null, default);

    /// <summary>As <see cref="BuildRenderDocument(XpsDocument, int?, bool, IRasterWriter?)"/>, observing a
    /// cancellation token and optionally skipping elements that cannot be rendered.</summary>
    /// <param name="document">The document.</param>
    /// <param name="maxPages">Render at most this many pages.</param>
    /// <param name="forPdf">Use the PDF settings (hairline solid strokes rasterized).</param>
    /// <param name="maskRasterizer">Renders opacity-masked content to images.</param>
    /// <param name="suppressErrors">Skip an element (with everything it had drawn) that throws, and a
    /// page whose XML cannot be read, recording a warning, instead of failing the document.</param>
    /// <param name="warnings">Receives the warnings.</param>
    /// <param name="cancellationToken">Checked per page and per element; never suppressed.</param>
    public static (RenderDocument Document, XpsImageStore Images) BuildRenderDocument(XpsDocument document, int? maxPages, bool forPdf,
        IRasterWriter? maskRasterizer, bool suppressErrors, List<ConversionWarning>? warnings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var guard = Guard.Begin(cancellationToken);
        var builder = new RenderModelBuilder();
        var imageStore = new XpsImageStore();
        var renderer = new XpsRenderer(builder, imageStore, null, rasterizeSolidStrokes: forPdf, maskRasterizer: maskRasterizer)
        {
            CancellationToken = cancellationToken,
            SuppressErrors = suppressErrors,
            Warnings = warnings,
        };
        renderer.SetPackage(document.Package);
        var parser = new XpsParser(document.Package);
        var mediaFitSizes = MediaFitSizesByPart(document);
        var parts = parser.FixedPageParts();
        if (maxPages is int limit)
            parts = parts.Take(Math.Max(0, limit)).ToList();
        int pageNumber = 0;
        foreach (string part in parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            pageNumber++;
            renderer.SetCurrentPart(part);
            renderer.SetMediaFitSize(mediaFitSizes.GetValueOrDefault(part));
            renderer.PageNumber = pageNumber;
            if (!suppressErrors)
            {
                renderer.RenderFixedPage(document.Package.Read(part));
                continue;
            }
            try
            {
                renderer.RenderFixedPage(document.Package.Read(part));
            }
            catch (Exception ex) when (XpsRenderer.IsRecoverable(ex))
            {
                warnings?.Add(new ConversionWarning("xps", "page", ex.Message, pageNumber));
                // A page that failed after it began keeps what was drawn; one that never began is skipped.
                if (builder.ActivePage is not null)
                    builder.EndPage();
            }
        }
        return (builder.Document(), imageStore);
    }

    private static Dictionary<string, (double, double)?> MediaFitSizesByPart(XpsDocument document)
    {
        var tickets = XpsPrintTickets.Read(document.Package);
        (double, double)? defaultSize = null;
        var pageSizes = new Dictionary<string, (double, double)?>(StringComparer.Ordinal);
        foreach (var ticket in tickets)
        {
            var size = PrintTicketMediaSizePoints(ticket.Xml);
            if (size is null)
                continue;
            if (ticket.Scope == PrintTicketScope.Job)
                defaultSize = size;
        }
        var parts = new XpsParser(document.Package).FixedPageParts();
        foreach (string part in parts)
        {
            string? ticketXml = XpsPrintTickets.PagePrintTicketXml(document.Package, part);
            if (ticketXml is null)
                continue;
            var size = PrintTicketMediaSizePoints(ticketXml);
            if (size is not null)
                pageSizes[part] = size;
        }
        if (pageSizes.Count == 0 && defaultSize is null)
            return [];
        var result = new Dictionary<string, (double, double)?>(StringComparer.Ordinal);
        foreach (string part in new XpsParser(document.Package).FixedPageParts())
            result[part] = pageSizes.TryGetValue(part, out var s) ? s : defaultSize;
        return result;
    }

    internal static (double Width, double Height)? PrintTicketMediaSizePoints(string xml)
    {
        System.Xml.Linq.XElement root;
        try
        {
            root = XmlUtil.Parse(xml);
        }
        catch (XmlException)
        {
            return null;
        }
        var parameterValues = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var parameter in XmlUtil.Descendants(root, "ParameterInit"))
        {
            string name = XmlUtil.Get(parameter, "name") ?? "";
            var value = XmlUtil.FindChild(parameter, "Value");
            if (value is null || value.IsEmpty)
                continue;
            if (Py.TryFloat(TextOf(value), out double v))
                parameterValues[name] = v;
        }
        bool landscape = false;
        double? widthUm = null, heightUm = null;
        foreach (var feature in XmlUtil.Descendants(root, "Feature"))
        {
            string name = XmlUtil.Get(feature, "name") ?? "";
            if (name.EndsWith("PageOrientation", StringComparison.Ordinal))
            {
                var option = XmlUtil.FindChild(feature, "Option");
                if (option is not null)
                    landscape = (XmlUtil.Get(option, "name") ?? "").EndsWith("Landscape", StringComparison.Ordinal);
                continue;
            }
            if (!name.EndsWith("PageMediaSize", StringComparison.Ordinal))
                continue;
            var mediaOption = XmlUtil.FindChild(feature, "Option");
            if (mediaOption is null)
                continue;
            foreach (var scored in mediaOption.Elements().Where(e => e.Name.LocalName == "ScoredProperty"))
            {
                string scoredName = XmlUtil.Get(scored, "name") ?? "";
                double? parsed = null;
                var value = XmlUtil.FindChild(scored, "Value");
                if (value is not null && !value.IsEmpty && Py.TryFloat(TextOf(value), out double v))
                    parsed = v;
                if (parsed is null)
                {
                    var reference = XmlUtil.FindChild(scored, "ParameterRef");
                    if (reference is not null && parameterValues.TryGetValue(XmlUtil.Get(reference, "name") ?? "", out double pv))
                        parsed = pv;
                }
                if (parsed is null)
                    continue;
                if (scoredName.EndsWith("MediaSizeWidth", StringComparison.Ordinal))
                    widthUm = parsed;
                else if (scoredName.EndsWith("MediaSizeHeight", StringComparison.Ordinal))
                    heightUm = parsed;
            }
        }
        if (widthUm is double w && w != 0 && heightUm is double h && h != 0)
        {
            double widthPt = (w / 25400.0) * 72.0;
            double heightPt = (h / 25400.0) * 72.0;
            if (landscape && heightPt > widthPt)
                (widthPt, heightPt) = (heightPt, widthPt);
            return (widthPt, heightPt);
        }
        return null;
    }

    // ElementTree's .text: the text before the first child element.
    private static string TextOf(System.Xml.Linq.XElement element)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var node in element.Nodes())
        {
            if (node is System.Xml.Linq.XText text)
                sb.Append(text.Value);
            else if (node is System.Xml.Linq.XElement)
                break;
        }
        return sb.ToString();
    }

    /// <summary>A font resolver with the package's embedded fonts registered under their font
    /// references.</summary>
    internal static FontResolver BuildXpsFontResolver(XpsPackage package, RenderDocument renderDoc, string? additionalFontsFolder)
    {
        var resolver = new FontResolver(additionalFontsFolder);
        foreach (string fontRef in UsedFontRefs(renderDoc))
        {
            if (string.IsNullOrEmpty(fontRef))
                continue;
            string partName = NormalizePartName(fontRef);
            if (!package.HasPart(partName))
                continue;
            byte[] data = package.Read(partName);
            if (partName.EndsWith(".odttf", StringComparison.OrdinalIgnoreCase))
                data = DeobfuscateOdttf(partName, data);
            try
            {
                var (unitsPerEm, codeWidths) = TtfMetrics.Parse(data);
                resolver.RegisterEmbeddedType42(fontRef, data, unitsPerEm, codeWidths);
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
            {
                // Unparseable fonts are skipped, as in the original.
            }
        }
        return resolver;
    }

    internal static void AttachImageResources(RenderDocument renderDoc, XpsImageStore imageStore)
    {
        foreach (var (imageId, resource) in imageStore.Items)
        {
            renderDoc.Resources.Images[imageId] = new RenderImageResource(resource.Data, resource.Width, resource.Height, resource.ColorSpace,
                resource.BitsPerComponent, resource.Filter, SoftMask: resource.SoftMask);
        }
    }

    private static void QuantizePdfPageSizes(RenderDocument renderDoc)
    {
        foreach (var page in renderDoc.Pages)
        {
            page.Width = Math.Round(page.Width * 2.0, MidpointRounding.ToEven) / 2.0;
            page.Height = Math.Round(page.Height * 2.0, MidpointRounding.ToEven) / 2.0;
        }
    }

    private static HashSet<string> UsedFontRefs(RenderDocument renderDoc)
    {
        var refs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in renderDoc.Pages)
        {
            foreach (var command in page.Commands)
            {
                if (command is TextCommand text && !string.IsNullOrEmpty(text.FontRef))
                    refs.Add(text.FontRef);
            }
        }
        foreach (var pattern in renderDoc.Resources.Patterns.Values)
        {
            if (pattern is not TilingPattern tiling)
                continue;
            foreach (var command in tiling.Commands)
            {
                if (command is TextCommand text && !string.IsNullOrEmpty(text.FontRef))
                    refs.Add(text.FontRef);
            }
        }
        return refs;
    }

    private static string NormalizePartName(string value)
    {
        int hash = value.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
            value = value[..hash];
        int query = value.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
            value = value[..query];
        return value.StartsWith('/') ? value : "/" + value;
    }

    /// <summary>XPS obfuscated fonts XOR their first 32 bytes with the reversed GUID bytes of
    /// the part name (braces allowed).</summary>
    internal static byte[] DeobfuscateOdttf(string partName, byte[] data)
    {
        if (data.Length < 32)
            return data;
        var key = OdttfGuidKey(partName);
        if (key is null)
            return data;
        var result = (byte[])data.Clone();
        for (int i = 0; i < 32; i++)
            result[i] ^= key[i % 16];
        return result;
    }

    private static byte[]? OdttfGuidKey(string partName)
    {
        int slash = partName.LastIndexOf('/');
        string baseName = slash >= 0 ? partName[(slash + 1)..] : partName;
        int dot = baseName.LastIndexOf('.');
        string stem = dot >= 0 ? baseName[..dot] : baseName;
        string hex = stem.Replace("urn:", "", StringComparison.Ordinal).Replace("uuid:", "", StringComparison.Ordinal).Trim('{', '}').Replace("-", "", StringComparison.Ordinal);
        if (hex.Length != 32 || !hex.All(Uri.IsHexDigit))
            return null;
        var raw = Convert.FromHexString(hex);
        Array.Reverse(raw);
        return raw;
    }
}
