// Ported from xps/document.py: the XPS document entry point (load, edit, save, convert).

using System.IO.Compression;
using XRay.PS.Rendering;

namespace XRay.PS.Xps;

/// <summary>A loaded or editable XPS document.</summary>
public sealed class XpsDocument
{
    private List<string?> _pageParts;

    private XpsDocument(XpsPackage package, XpsDocumentBuilder builder, List<string?> pageParts)
    {
        Package = package;
        Builder = builder;
        _pageParts = pageParts;
    }

    /// <summary>The package parts.</summary>
    public XpsPackage Package { get; private set; }

    /// <summary>The editable page list.</summary>
    public XpsDocumentBuilder Builder { get; }

    /// <summary>The fixed pages.</summary>
    public List<XpsFixedPage> Pages => Builder.Pages;

    /// <summary>Creates an empty document.</summary>
    public static XpsDocument Create(string? title = null) => new(new XpsPackage(), new XpsDocumentBuilder(title), []);

    /// <summary>Reads a document from bytes.</summary>
    public static XpsDocument FromBytes(byte[] data) => FromBytes(data, default);

    /// <summary>Reads a document from bytes, observing a cancellation token (checked per package entry and per page).</summary>
    public static XpsDocument FromBytes(byte[] data, CancellationToken cancellationToken)
    {
        var package = XpsPackage.FromBytes(data, cancellationToken);
        var builder = new XpsDocumentBuilder();
        var pageParts = new XpsParser(package).FixedPageParts();
        foreach (string part in pageParts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Pages.Add(ParseFixedPage(package.Read(part)));
        }
        return new XpsDocument(package, builder, pageParts.Cast<string?>().ToList());
    }

    /// <summary>Reads a document from a file.</summary>
    public static XpsDocument FromFile(string path) => FromBytes(File.ReadAllBytes(path));

    /// <summary>Reads a document from a file, observing a cancellation token.</summary>
    public static XpsDocument FromFile(string path, CancellationToken cancellationToken) => FromBytes(File.ReadAllBytes(path), cancellationToken);

    /// <summary>Reads a document from a stream.</summary>
    public static XpsDocument FromStream(Stream stream) => FromStream(stream, default);

    /// <summary>Reads a document from a stream, observing a cancellation token.</summary>
    public static XpsDocument FromStream(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var ms = new MemoryStream();
        stream.CopyToAsync(ms, cancellationToken).GetAwaiter().GetResult();
        return FromBytes(ms.ToArray(), cancellationToken);
    }

    public XpsFixedPage AddPage(double width, double height)
    {
        var page = Builder.AddPage(width, height);
        _pageParts.Add(null);
        return page;
    }

    public void InsertPage(int index, XpsFixedPage page)
    {
        Builder.InsertPage(index, page);
        _pageParts.Insert(index, null);
    }

    public void RemovePage(int index)
    {
        Builder.RemovePage(index);
        _pageParts.RemoveAt(index);
    }

    /// <summary>Serializes the document (and writes it to <paramref name="path"/> when given).</summary>
    public byte[] Save(string? path = null)
    {
        if (_pageParts.Count != Builder.Pages.Count)
        {
            int missing = Builder.Pages.Count - _pageParts.Count;
            if (missing > 0)
                _pageParts.AddRange(Enumerable.Repeat<string?>(null, missing));
            else
                _pageParts = _pageParts.Take(Builder.Pages.Count).ToList();
        }
        var parts = new Dictionary<string, byte[]>(Package.Parts, StringComparer.Ordinal);
        var pageParts = AssignPageParts(_pageParts, parts);
        foreach (string partName in parts.Keys.ToList())
        {
            if (IsPagePart(partName) && !pageParts.Contains(partName))
                parts.Remove(partName);
        }
        for (int i = 0; i < Builder.Pages.Count; i++)
        {
            ValidatePageImages(Builder.Pages[i], parts, pageParts[i]);
            parts[pageParts[i]] = XpsSerializer.SerializeFixedPage(Builder.Pages[i]);
        }
        foreach (var (name, data) in XpsSerializer.SerializeDocumentSequence(pageParts))
            parts[name] = data;
        byte[] bytes = WritePackage(parts);
        Package = new XpsPackage(parts);
        _pageParts = pageParts.Cast<string?>().ToList();
        if (!string.IsNullOrEmpty(path))
            File.WriteAllBytes(path, bytes);
        return bytes;
    }

    public List<PrintTicket> GetPrintTickets() => XpsPrintTickets.Read(Package);

    public void SetPrintTicket(PrintTicketScope scope, string xml, int? pageIndex = null) => XpsPrintTickets.Write(Package, scope, xml, pageIndex);

    public void RemovePrintTicket(PrintTicketScope scope, int? pageIndex = null) => XpsPrintTickets.Remove(Package, scope, pageIndex);

    /// <summary>Converts to PDF.</summary>
    public byte[] ToPdf(PdfSaveOptions? options = null, int? maxPages = null) => XpsConverter.ToPdf(this, options, maxPages);

    /// <summary>Converts to PDF, observing a cancellation token.</summary>
    public byte[] ToPdf(PdfSaveOptions? options, int? maxPages, CancellationToken cancellationToken) =>
        XpsConverter.ToPdf(this, options, maxPages, cancellationToken);

    /// <summary>Converts to PDF and writes it to <paramref name="output"/> without building the
    /// whole PDF as one array. The stream need not be seekable.</summary>
    public void ToPdf(Stream output, PdfSaveOptions? options = null, int? maxPages = null, CancellationToken cancellationToken = default) =>
        XpsConverter.ToPdf(this, output, options, maxPages, cancellationToken);

    /// <summary>Converts the first page to an image.</summary>
    public byte[] ToImage(ImageSaveOptions options, int? maxPages = null) => XpsConverter.ToImage(this, options, maxPages);

    /// <summary>Converts the first page to an image, observing a cancellation token.</summary>
    public byte[] ToImage(ImageSaveOptions options, int? maxPages, CancellationToken cancellationToken) =>
        XpsConverter.ToImage(this, options, maxPages, cancellationToken);

    /// <summary>Converts the first page to an image and writes it to <paramref name="output"/>.</summary>
    public void ToImage(ImageSaveOptions options, Stream output, int? maxPages = null, CancellationToken cancellationToken = default) =>
        XpsConverter.ToImage(this, options, output, maxPages, cancellationToken);

    /// <summary>Converts each page to an image.</summary>
    public List<byte[]> ToImages(ImageSaveOptions options, int? maxPages = null) => XpsConverter.ToImages(this, options, maxPages);

    /// <summary>Converts each page to an image, observing a cancellation token.</summary>
    public List<byte[]> ToImages(ImageSaveOptions options, int? maxPages, CancellationToken cancellationToken) =>
        XpsConverter.ToImages(this, options, maxPages, cancellationToken);

    /// <summary>Converts each page to an image, taking each page's stream (zero-based index) from
    /// <paramref name="streamForPage"/>.</summary>
    public void ToImages(ImageSaveOptions options, Func<int, Stream> streamForPage, int? maxPages = null, CancellationToken cancellationToken = default) =>
        XpsConverter.ToImages(this, options, streamForPage, maxPages, cancellationToken);

    /// <summary>Builds the render model (with the PDF settings when <paramref name="forPdf"/>).</summary>
    public RenderDocument BuildRenderModel(bool forPdf = true, int? maxPages = null) =>
        XpsConverter.BuildRenderDocument(this, maxPages, forPdf).Document;

    private static XpsFixedPage ParseFixedPage(byte[] xml)
    {
        var root = XmlUtil.Parse(xml);
        double width = XpsRenderer.ParseFloat(XmlUtil.Get(root, "Width")) is double w && w != 0 ? w : 0.0;
        double height = XpsRenderer.ParseFloat(XmlUtil.Get(root, "Height")) is double h && h != 0 ? h : 0.0;
        var page = new XpsFixedPage(width, height);
        foreach (var child in root.Elements())
            page.Elements.Add(XmlUtil.Clone(child));
        return page;
    }

    private static List<string> AssignPageParts(List<string?> existing, Dictionary<string, byte[]> parts)
    {
        var used = existing.Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToHashSet(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (string? name in existing)
        {
            if (!string.IsNullOrEmpty(name))
            {
                result.Add(name);
                continue;
            }
            for (int index = 1; ; index++)
            {
                string candidate = $"/Documents/1/Pages/{index}.fpage";
                if (!used.Contains(candidate) && !parts.ContainsKey(candidate))
                {
                    used.Add(candidate);
                    result.Add(candidate);
                    break;
                }
            }
        }
        return result;
    }

    private static bool IsPagePart(string partName) =>
        partName.StartsWith("/Documents/1/Pages/", StringComparison.Ordinal) && partName.EndsWith(".fpage", StringComparison.Ordinal);

    private static void ValidatePageImages(XpsFixedPage page, Dictionary<string, byte[]> parts, string pagePart)
    {
        foreach (var element in WalkElements(page.Elements))
        {
            if (element is XpsImage image)
            {
                string partName = XpsPackage.ResolvePart(pagePart, image.Source);
                if (!parts.ContainsKey(partName))
                    throw new InvalidOperationException($"missing image part {partName}");
            }
        }
    }

    private static IEnumerable<object> WalkElements(List<object> elements)
    {
        foreach (var element in elements)
        {
            yield return element;
            if (element is XpsCanvas canvas)
            {
                foreach (var child in WalkElements(canvas.Elements))
                    yield return child;
            }
            else if (element is XpsFixedPage nested)
            {
                foreach (var child in WalkElements(nested.Elements))
                    yield return child;
            }
        }
    }

    private static byte[] WritePackage(Dictionary<string, byte[]> parts)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in parts)
            {
                var entry = archive.CreateEntry(name.TrimStart('/'), CompressionLevel.NoCompression);
                using var stream = entry.Open();
                stream.Write(data);
            }
        }
        return ms.ToArray();
    }
}
