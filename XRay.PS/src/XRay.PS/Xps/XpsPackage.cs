// Ported from xps/package.py, xps/parser.py, xps/relationships.py and xps/resources.py.

using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace XRay.PS.Xps;

/// <summary>The parts of an XPS (OPC zip) package, keyed by normalized part name.</summary>
public sealed partial class XpsPackage
{
    /// <summary>Creates a package over existing parts.</summary>
    public XpsPackage(Dictionary<string, byte[]>? parts = null) => Parts = parts ?? new Dictionary<string, byte[]>(StringComparer.Ordinal);

    /// <summary>Part bytes by normalized name (<c>/Documents/1/Pages/1.fpage</c>).</summary>
    public Dictionary<string, byte[]> Parts { get; }

    /// <summary>Reads a package from zip bytes, joining interleaved <c>[n].piece</c> parts.</summary>
    public static XpsPackage FromBytes(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var parts = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var pieceParts = new Dictionary<string, Dictionary<int, byte[]>>(StringComparer.Ordinal);
        var pieceLastIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        try
        {
            using var archive = new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                string name = entry.FullName;
                byte[] bytes = ReadEntry(entry);
                var piece = ParsePieceName(name);
                if (piece is var (baseName, index, isLast))
                {
                    if (!pieceParts.TryGetValue(baseName, out var bucket))
                        pieceParts[baseName] = bucket = [];
                    bucket[index] = bytes;
                    if (isLast)
                        pieceLastIndex[baseName] = index;
                    continue;
                }
                parts[NormalizePart(name)] = bytes;
            }
        }
        catch (InvalidDataException ex)
        {
            throw new XpsFormatException("BadZipFile: " + ex.Message, ex);
        }
        foreach (var (baseName, chunks) in pieceParts)
        {
            if (chunks.Count == 0)
                continue;
            int maxIndex = pieceLastIndex.TryGetValue(baseName, out int last) ? last : chunks.Keys.Max();
            using var joined = new MemoryStream();
            for (int i = 0; i <= maxIndex; i++)
            {
                if (chunks.TryGetValue(i, out var chunk))
                    joined.Write(chunk);
            }
            parts[NormalizePart(baseName)] = joined.ToArray();
        }
        return new XpsPackage(parts);
    }

    private static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        // Guard against zip bombs: parts are read whole, as the original did, but bounded.
        const long limit = 1L << 31;
        using var stream = entry.Open();
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > limit)
                throw new XpsFormatException("package part too large");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    /// <summary>Reads a package from a file.</summary>
    public static XpsPackage FromFile(string path) => FromBytes(File.ReadAllBytes(path));

    /// <summary>Reads a part by name.</summary>
    public byte[] Read(string partName)
    {
        string key = NormalizePart(partName);
        if (!Parts.TryGetValue(key, out var data))
            throw new XpsFormatException($"missing part {key}");
        return data;
    }

    /// <summary>Whether a part exists.</summary>
    public bool HasPart(string partName) => Parts.ContainsKey(NormalizePart(partName));

    /// <summary>Normalizes a part name: forward slashes, a leading slash, dot segments resolved.</summary>
    public static string NormalizePart(string name)
    {
        string value = name.Replace('\\', '/');
        if (!value.StartsWith('/'))
            value = "/" + value;
        var segments = new List<string>();
        foreach (string segment in value.Split('/'))
        {
            if (segment is "" or ".")
                continue;
            if (segment == "..")
            {
                if (segments.Count > 0)
                    segments.RemoveAt(segments.Count - 1);
                continue;
            }
            segments.Add(segment);
        }
        return "/" + string.Join("/", segments);
    }

    private static (string Base, int Index, bool IsLast)? ParsePieceName(string name)
    {
        var match = PieceRegex().Match(name);
        if (!match.Success)
            return null;
        if (!int.TryParse(match.Groups["idx"].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int idx))
            return null;
        return (match.Groups["base"].Value, idx, match.Groups["last"].Success);
    }

    [GeneratedRegex(@"^(?<base>.+)/\[(?<idx>[0-9]+)\](?<last>\.last)?\.piece$", RegexOptions.Singleline)]
    private static partial Regex PieceRegex();

    /// <summary>Resolves a relative target against a part (the base part's directory).</summary>
    internal static string ResolvePart(string basePart, string target)
    {
        if (target.StartsWith('/'))
            return NormalizePart(target);
        int slash = basePart.LastIndexOf('/');
        string baseDir = slash >= 0 ? basePart[..slash] : "";
        if (baseDir.Length == 0)
            return NormalizePart(target);
        return NormalizePart(baseDir + "/" + target);
    }
}

/// <summary>Thrown for malformed XPS packages.</summary>
public sealed class XpsFormatException : Exception
{
    public XpsFormatException(string message) : base(message)
    {
    }

    public XpsFormatException(string message, Exception inner) : base(message, inner)
    {
    }

    public XpsFormatException()
    {
    }
}

/// <summary>Locates fixed pages through the document sequence.</summary>
public sealed class XpsParser(XpsPackage package)
{
    /// <summary>Fixed page part names in document order.</summary>
    public List<string> FixedPageParts()
    {
        string? fdseq = FindFdseq();
        if (fdseq is null)
            return [];
        var fdseqXml = XmlUtil.Parse(package.Read(fdseq));
        var documentParts = new List<string>();
        foreach (var docRef in XmlUtil.Descendants(fdseqXml, "DocumentReference"))
        {
            string? source = XmlUtil.Get(docRef, "Source");
            if (!string.IsNullOrEmpty(source))
                documentParts.Add(XpsPackage.ResolvePart(fdseq, source));
        }
        var pageParts = new List<string>();
        foreach (string document in documentParts)
        {
            var docXml = XmlUtil.Parse(package.Read(document));
            foreach (var pageContent in XmlUtil.Descendants(docXml, "PageContent"))
            {
                string? source = XmlUtil.Get(pageContent, "Source");
                if (!string.IsNullOrEmpty(source))
                    pageParts.Add(XpsPackage.ResolvePart(document, source));
            }
        }
        return pageParts;
    }

    private string? FindFdseq()
    {
        foreach (string part in package.Parts.Keys)
        {
            if (part.EndsWith(".fdseq", StringComparison.OrdinalIgnoreCase))
                return part;
        }
        const string relsPart = "/_rels/.rels";
        if (package.HasPart(relsPart))
        {
            var root = XmlUtil.Parse(package.Read(relsPart));
            foreach (var rel in XmlUtil.Descendants(root, "Relationship"))
            {
                string? target = XmlUtil.Get(rel, "Target");
                if (!string.IsNullOrEmpty(target) && target.EndsWith(".fdseq", StringComparison.OrdinalIgnoreCase))
                    return XpsPackage.ResolvePart(relsPart, target);
            }
        }
        return null;
    }
}

/// <summary>A package relationship.</summary>
public sealed record XpsRelationship(string Id, string Type, string Target);

/// <summary>Reads and writes part relationships.</summary>
public static class XpsRelationships
{
    private const string RelsNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>Reads the relationships of a part.</summary>
    public static List<XpsRelationship> Read(XpsPackage package, string partName)
    {
        string relsPart = RelsPartName(partName);
        if (!package.HasPart(relsPart))
            return [];
        var root = XmlUtil.Parse(package.Read(relsPart));
        var rels = new List<XpsRelationship>();
        foreach (var rel in XmlUtil.Descendants(root, "Relationship"))
            rels.Add(new XpsRelationship(XmlUtil.Get(rel, "Id") ?? "", XmlUtil.Get(rel, "Type") ?? "", XmlUtil.Get(rel, "Target") ?? ""));
        return rels;
    }

    /// <summary>Writes (or removes, when empty) the relationships of a part.</summary>
    public static void Write(XpsPackage package, string partName, List<XpsRelationship> rels)
    {
        string relsPart = RelsPartName(partName);
        if (rels.Count == 0)
        {
            package.Parts.Remove(relsPart);
            return;
        }
        XNamespace ns = RelsNs;
        var root = new XElement(ns + "Relationships");
        foreach (var rel in rels)
            root.Add(new XElement(ns + "Relationship", new XAttribute("Id", rel.Id), new XAttribute("Type", rel.Type), new XAttribute("Target", rel.Target)));
        package.Parts[relsPart] = XmlUtil.ToBytes(root);
    }

    internal static string RelsPartName(string partName)
    {
        if (partName is "" or "/")
            return "/_rels/.rels";
        string normalized = partName.TrimStart('/');
        int slash = normalized.LastIndexOf('/');
        if (slash >= 0)
            return $"/{normalized[..slash]}/_rels/{normalized[(slash + 1)..]}.rels";
        return $"/_rels/{normalized}.rels";
    }
}

/// <summary>A resource dictionary with parent lookup.</summary>
public sealed class XpsResourceDictionary(Dictionary<string, XElement> items, XpsResourceDictionary? parent = null)
{
    public Dictionary<string, XElement> Items { get; } = items;
    public XpsResourceDictionary? Parent { get; } = parent;

    /// <summary>Resolves a key here or in a parent dictionary.</summary>
    public XElement? Resolve(string key)
    {
        if (Items.TryGetValue(key, out var value))
            return value;
        return Parent?.Resolve(key);
    }
}
