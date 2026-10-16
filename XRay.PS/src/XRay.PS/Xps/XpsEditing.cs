// Ported from xps/editing.py and xps/serializer.py: editable XPS pages and their serialization.

using System.Globalization;
using System.Xml.Linq;

namespace XRay.PS.Xps;

/// <summary>An editable XPS fixed page. <see cref="Elements"/> holds parsed page XML
/// (<see cref="XElement"/>) and elements added through the editing types.</summary>
public sealed class XpsFixedPage(double width, double height)
{
    public double Width { get; set; } = width;
    public double Height { get; set; } = height;

    /// <summary><see cref="XElement"/>, <see cref="XpsCanvas"/>, <see cref="XpsPath"/>,
    /// <see cref="XpsGlyphs"/>, <see cref="XpsImage"/>, or an XML string.</summary>
    public List<object> Elements { get; } = [];
}

/// <summary>A canvas grouping child elements.</summary>
public sealed class XpsCanvas
{
    public List<object> Elements { get; } = [];
    public string? Transform { get; set; }
    public string? Clip { get; set; }
}

/// <summary>A path element.</summary>
public sealed class XpsPath(string data)
{
    public string Data { get; set; } = data;
    public string? Fill { get; set; }
    public string? Stroke { get; set; }
    public double? StrokeThickness { get; set; }
}

/// <summary>A glyphs (text) element.</summary>
public sealed class XpsGlyphs(string fontUri, double fontSize, double originX, double originY, string unicodeString)
{
    public string FontUri { get; set; } = fontUri;
    public double FontSize { get; set; } = fontSize;
    public double OriginX { get; set; } = originX;
    public double OriginY { get; set; } = originY;
    public string UnicodeString { get; set; } = unicodeString;
    public string? Fill { get; set; }
}

/// <summary>An image element referencing an image part.</summary>
public sealed class XpsImage(string source, double width, double height)
{
    public string Source { get; set; } = source;
    public double Width { get; set; } = width;
    public double Height { get; set; } = height;
    public string? Transform { get; set; }
}

/// <summary>Builds and edits the page list of an XPS document.</summary>
public sealed class XpsDocumentBuilder(string? title = null)
{
    public string? Title { get; set; } = title;
    public List<XpsFixedPage> Pages { get; } = [];

    public XpsFixedPage AddPage(double width, double height)
    {
        var page = new XpsFixedPage(width, height);
        Pages.Add(page);
        return page;
    }

    public void InsertPage(int index, XpsFixedPage page)
    {
        if (index < 0 || index > Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(index), "page index out of range");
        Pages.Insert(index, page);
    }

    public void RemovePage(int index)
    {
        if (index < 0 || index >= Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(index), "page index out of range");
        Pages.RemoveAt(index);
    }

    /// <summary>Serializes the pages into a new package.</summary>
    public XpsPackage ToPackage()
    {
        var parts = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var pageParts = new List<string>();
        for (int i = 0; i < Pages.Count; i++)
        {
            string partName = $"/Documents/1/Pages/{i + 1}.fpage";
            parts[partName] = XpsSerializer.SerializeFixedPage(Pages[i]);
            pageParts.Add(partName);
        }
        foreach (var (name, data) in XpsSerializer.SerializeDocumentSequence(pageParts))
            parts[name] = data;
        return new XpsPackage(parts);
    }
}

/// <summary>Serializes fixed pages and the document sequence.</summary>
public static class XpsSerializer
{
    internal static readonly XNamespace XpsNs = "http://schemas.microsoft.com/xps/2005/06";
    private static readonly XNamespace RelsNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>Serializes a fixed page to XML bytes.</summary>
    public static byte[] SerializeFixedPage(XpsFixedPage page)
    {
        var root = new XElement(XpsNs + "FixedPage", new XAttribute("Width", Fmt(page.Width)), new XAttribute("Height", Fmt(page.Height)));
        foreach (var element in page.Elements)
            AppendElement(root, element);
        return XmlUtil.ToBytes(root);
    }

    /// <summary>Serializes the document sequence, fixed document and package relationships.</summary>
    public static Dictionary<string, byte[]> SerializeDocumentSequence(IReadOnlyList<string> pages)
    {
        var fdseq = new XElement(XpsNs + "FixedDocumentSequence", new XElement(XpsNs + "DocumentReference", new XAttribute("Source", "Documents/1/FixedDoc.fdoc")));
        var fixedDoc = new XElement(XpsNs + "FixedDocument");
        foreach (string part in pages)
            fixedDoc.Add(new XElement(XpsNs + "PageContent", new XAttribute("Source", RelativePageSource(part))));
        var rels = new XElement(RelsNs + "Relationships",
            new XElement(RelsNs + "Relationship",
                new XAttribute("Id", "rId1"),
                new XAttribute("Type", "http://schemas.microsoft.com/xps/2005/06/fixedrepresentation"),
                new XAttribute("Target", "FixedDocSeq.fdseq")));
        return new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["/_rels/.rels"] = XmlUtil.ToBytes(rels),
            ["/FixedDocSeq.fdseq"] = XmlUtil.ToBytes(fdseq),
            ["/Documents/1/FixedDoc.fdoc"] = XmlUtil.ToBytes(fixedDoc),
        };
    }

    private static void AppendElement(XElement parent, object element)
    {
        switch (element)
        {
            case XpsPath path:
            {
                var e = new XElement(XpsNs + "Path", new XAttribute("Data", path.Data));
                if (path.Fill is not null)
                    e.Add(new XAttribute("Fill", path.Fill));
                if (path.Stroke is not null)
                    e.Add(new XAttribute("Stroke", path.Stroke));
                if (path.StrokeThickness is double t)
                    e.Add(new XAttribute("StrokeThickness", Fmt(t)));
                parent.Add(e);
                return;
            }
            case XpsGlyphs glyphs:
            {
                var e = new XElement(XpsNs + "Glyphs",
                    new XAttribute("FontUri", glyphs.FontUri),
                    new XAttribute("FontRenderingEmSize", Fmt(glyphs.FontSize)),
                    new XAttribute("OriginX", Fmt(glyphs.OriginX)),
                    new XAttribute("OriginY", Fmt(glyphs.OriginY)),
                    new XAttribute("UnicodeString", glyphs.UnicodeString));
                if (glyphs.Fill is not null)
                    e.Add(new XAttribute("Fill", glyphs.Fill));
                parent.Add(e);
                return;
            }
            case XpsImage image:
            {
                var e = new XElement(XpsNs + "Image",
                    new XAttribute("Source", image.Source),
                    new XAttribute("Width", Fmt(image.Width)),
                    new XAttribute("Height", Fmt(image.Height)));
                if (image.Transform is not null)
                    e.Add(new XAttribute("RenderTransform", image.Transform));
                parent.Add(e);
                return;
            }
            case XpsCanvas canvas:
            {
                var e = new XElement(XpsNs + "Canvas");
                if (canvas.Transform is not null)
                    e.Add(new XAttribute("RenderTransform", canvas.Transform));
                if (canvas.Clip is not null)
                    e.Add(new XAttribute("Clip", canvas.Clip));
                parent.Add(e);
                foreach (var child in canvas.Elements)
                    AppendElement(e, child);
                return;
            }
            case XElement xml:
                parent.Add(XmlUtil.Clone(xml));
                return;
            case string text:
                parent.Add(XmlUtil.Parse(text));
                return;
            case byte[] bytes:
                parent.Add(XmlUtil.Parse(bytes));
                return;
            default:
                throw new ArgumentException("unsupported XPS element type");
        }
    }

    private static string RelativePageSource(string partName)
    {
        if (partName.StartsWith("/Documents/1/", StringComparison.Ordinal))
            return partName["/Documents/1/".Length..];
        if (partName.StartsWith('/'))
            return partName[1..];
        return partName;
    }

    internal static string Fmt(double value)
    {
        if (Internal.Py.Int(value) == value)
            return Internal.Py.Int(value).ToString(CultureInfo.InvariantCulture);
        return Internal.Py.FormatF(value, 6).TrimEnd('0').TrimEnd('.');
    }
}
