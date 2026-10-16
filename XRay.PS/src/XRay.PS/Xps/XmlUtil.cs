// ElementTree-style helpers over System.Xml.Linq. The original walked ElementTree elements; these
// mirror the queries it used: get() reads an attribute in no namespace, ".//{*}Name" finds
// descendants (not the element itself) by local name in any namespace, list(element) is the child
// elements, and attrib excludes namespace declarations.

using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace XRay.PS.Xps;

internal static class XmlUtil
{
    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Parse,
        XmlResolver = null,
        MaxCharactersFromEntities = 10_000_000,
        IgnoreProcessingInstructions = true,
        IgnoreComments = true,
    };

    /// <summary>Parses XML bytes to the root element.</summary>
    public static XElement Parse(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        using var reader = XmlReader.Create(stream, Settings);
        return XDocument.Load(reader).Root ?? throw new XmlException("no root element");
    }

    /// <summary>Parses an XML string to the root element.</summary>
    public static XElement Parse(string text)
    {
        // A decoded byte order mark is tolerated, as the original's parser did.
        if (text.StartsWith('\uFEFF'))
            text = text[1..];
        using var reader = XmlReader.Create(new StringReader(text), Settings);
        return XDocument.Load(reader).Root ?? throw new XmlException("no root element");
    }

    public static string LocalName(XElement element) => element.Name.LocalName;

    /// <summary>The value of an attribute in no namespace, or null.</summary>
    public static string? Get(XElement element, string name) => element.Attribute(name)?.Value;

    /// <summary>Descendants (excluding the element) with this local name, in document order.</summary>
    public static IEnumerable<XElement> Descendants(XElement element, string localName) =>
        element.Descendants().Where(d => d.Name.LocalName == localName);

    /// <summary>The first descendant with this local name, or null.</summary>
    public static XElement? FindDescendant(XElement element, string localName) =>
        element.Descendants().FirstOrDefault(d => d.Name.LocalName == localName);

    /// <summary>The first direct child with this local name, or null.</summary>
    public static XElement? FindChild(XElement element, string localName) =>
        element.Elements().FirstOrDefault(d => d.Name.LocalName == localName);

    public static List<XElement> Children(XElement element) => element.Elements().ToList();

    /// <summary>Attributes as ElementTree keys ("{ns}local" or "local") and values, without
    /// namespace declarations.</summary>
    public static IEnumerable<(string Key, string Value)> Attributes(XElement element)
    {
        foreach (var attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration)
                continue;
            yield return (attribute.Name.ToString(), attribute.Value);
        }
    }

    /// <summary>A detached deep copy (the original re-parsed serialized elements).</summary>
    public static XElement Clone(XElement element) => new(element);

    /// <summary>Serializes with an XML declaration in UTF-8.</summary>
    public static byte[] ToBytes(XElement root)
    {
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), OmitXmlDeclaration = false, Indent = false };
        using var ms = new MemoryStream();
        using (var writer = XmlWriter.Create(ms, settings))
            new XDocument(root).Save(writer);
        return ms.ToArray();
    }
}
