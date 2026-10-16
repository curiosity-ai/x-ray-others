// Ported from xps/print_tickets.py: reading and editing print tickets at job, document and page scope.

using System.Text;
using System.Xml;

namespace XRay.PS.Xps;

/// <summary>Print ticket scopes.</summary>
public enum PrintTicketScope
{
    Job,
    Document,
    Page,
}

/// <summary>A print ticket and the part it was read from.</summary>
public sealed record PrintTicket(PrintTicketScope Scope, string PartName, string Xml);

/// <summary>Print ticket helpers.</summary>
public static class XpsPrintTickets
{
    private const string PrintTicketType = "http://schemas.microsoft.com/xps/2005/06/printticket";

    /// <summary>Parses a scope name (<c>job</c>, <c>document</c>, <c>page</c>).</summary>
    public static PrintTicketScope ParseScope(string scope) => scope switch
    {
        "job" => PrintTicketScope.Job,
        "document" => PrintTicketScope.Document,
        "page" => PrintTicketScope.Page,
        _ => throw new ArgumentException($"'{scope}' is not a valid PrintTicketScope", nameof(scope)),
    };

    /// <summary>Reads every print ticket in the package.</summary>
    public static List<PrintTicket> Read(XpsPackage package)
    {
        var tickets = new List<PrintTicket>();
        string? fdseq = FindFdseq(package);
        if (fdseq is not null)
        {
            tickets.AddRange(ReadScopeTickets(package, PrintTicketScope.Job, fdseq));
            foreach (string document in DocumentParts(package, fdseq))
                tickets.AddRange(ReadScopeTickets(package, PrintTicketScope.Document, document));
        }
        foreach (string pagePart in new XpsParser(package).FixedPageParts())
            tickets.AddRange(ReadScopeTickets(package, PrintTicketScope.Page, pagePart));
        return tickets;
    }

    /// <summary>Adds or replaces the print ticket at a scope.</summary>
    public static void Write(XpsPackage package, PrintTicketScope scope, string xml, int? pageIndex = null)
    {
        try
        {
            XmlUtil.Parse(xml);
        }
        catch (XmlException ex)
        {
            throw new ArgumentException("invalid print ticket XML", nameof(xml), ex);
        }
        string basePart = ScopePart(package, scope, pageIndex);
        var rels = XpsRelationships.Read(package, basePart);
        var existing = rels.FirstOrDefault(r => r.Type == PrintTicketType);
        string partName = existing is not null ? ResolvePart(basePart, existing.Target) : PrintTicketPartName(basePart, package);
        package.Parts[partName] = Encoding.UTF8.GetBytes(xml);
        rels = rels.Where(r => r.Type != PrintTicketType).ToList();
        rels.Add(new XpsRelationship(NextRelId(rels), PrintTicketType, RelativeTarget(basePart, partName)));
        XpsRelationships.Write(package, basePart, rels);
    }

    /// <summary>Removes the print tickets at a scope.</summary>
    public static void Remove(XpsPackage package, PrintTicketScope scope, int? pageIndex = null)
    {
        string basePart = ScopePart(package, scope, pageIndex);
        var rels = XpsRelationships.Read(package, basePart);
        var remaining = new List<XpsRelationship>();
        foreach (var rel in rels)
        {
            if (rel.Type == PrintTicketType)
                package.Parts.Remove(ResolvePart(basePart, rel.Target));
            else
                remaining.Add(rel);
        }
        XpsRelationships.Write(package, basePart, remaining);
    }

    /// <summary>The print ticket XML related to a page part, if any.</summary>
    internal static string? PagePrintTicketXml(XpsPackage package, string pagePart)
    {
        foreach (var rel in XpsRelationships.Read(package, pagePart))
        {
            if (rel.Type != PrintTicketType)
                continue;
            string ticketPart = ResolvePart(pagePart, rel.Target);
            if (!package.HasPart(ticketPart))
                continue;
            return DecodeXml(package.Read(ticketPart));
        }
        return null;
    }

    private static List<PrintTicket> ReadScopeTickets(XpsPackage package, PrintTicketScope scope, string basePart)
    {
        var tickets = new List<PrintTicket>();
        foreach (var rel in XpsRelationships.Read(package, basePart))
        {
            if (rel.Type != PrintTicketType)
                continue;
            string partName = ResolvePart(basePart, rel.Target);
            if (!package.HasPart(partName))
                continue;
            tickets.Add(new PrintTicket(scope, partName, DecodeXml(package.Read(partName))));
        }
        return tickets;
    }

    private static string ScopePart(XpsPackage package, PrintTicketScope scope, int? pageIndex)
    {
        string? fdseq = FindFdseq(package);
        switch (scope)
        {
            case PrintTicketScope.Job:
                return fdseq ?? throw new InvalidOperationException("missing FixedDocumentSequence");
            case PrintTicketScope.Document:
            {
                if (fdseq is null)
                    throw new InvalidOperationException("missing FixedDocumentSequence");
                var documents = DocumentParts(package, fdseq);
                if (documents.Count == 0)
                    throw new InvalidOperationException("missing FixedDocument");
                return documents[0];
            }
            default:
            {
                if (pageIndex is not int index)
                    throw new ArgumentException("page_index required for page scope", nameof(pageIndex));
                var pages = new XpsParser(package).FixedPageParts();
                if (index < 0 || index >= pages.Count)
                    throw new ArgumentOutOfRangeException(nameof(pageIndex), "page_index out of range");
                return pages[index];
            }
        }
    }

    internal static string? FindFdseq(XpsPackage package)
    {
        foreach (string part in package.Parts.Keys)
        {
            if (part.EndsWith(".fdseq", StringComparison.OrdinalIgnoreCase))
                return part;
        }
        foreach (var rel in XpsRelationships.Read(package, "/"))
        {
            if (rel.Target.EndsWith(".fdseq", StringComparison.OrdinalIgnoreCase))
                return ResolvePart("/", rel.Target);
        }
        return null;
    }

    private static List<string> DocumentParts(XpsPackage package, string fdseqPart)
    {
        System.Xml.Linq.XElement root;
        try
        {
            root = XmlUtil.Parse(package.Read(fdseqPart));
        }
        catch (Exception ex) when (ex is XmlException or XpsFormatException)
        {
            return [];
        }
        var documents = new List<string>();
        foreach (var docRef in XmlUtil.Descendants(root, "DocumentReference"))
        {
            string? source = XmlUtil.Get(docRef, "Source");
            if (!string.IsNullOrEmpty(source))
                documents.Add(ResolvePart(fdseqPart, source));
        }
        return documents;
    }

    // Unlike the package resolver this one does not normalize, as in the original.
    internal static string ResolvePart(string basePart, string target)
    {
        if (target.StartsWith('/'))
            return target;
        int slash = basePart.LastIndexOf('/');
        string baseDir = slash >= 0 ? basePart[..slash] : basePart;
        if (baseDir.Length == 0)
            return "/" + target;
        return baseDir + "/" + target;
    }

    private static string RelativeTarget(string basePart, string partName)
    {
        int slash = basePart.LastIndexOf('/');
        string baseDir = slash >= 0 ? basePart[..slash] : basePart;
        if (baseDir.Length == 0)
            return partName.TrimStart('/');
        string prefix = baseDir + "/";
        if (partName.StartsWith(prefix, StringComparison.Ordinal))
            return partName[prefix.Length..];
        return partName.TrimStart('/');
    }

    private static string PrintTicketPartName(string basePart, XpsPackage package)
    {
        int slash = basePart.LastIndexOf('/');
        string baseDir = slash >= 0 ? basePart[..slash] : basePart;
        string candidate = baseDir.Length > 0 ? $"{baseDir}/PrintTicket.xml" : "/PrintTicket.xml";
        if (!package.Parts.ContainsKey(candidate))
            return candidate;
        for (int index = 1; ; index++)
        {
            candidate = baseDir.Length > 0 ? $"{baseDir}/PrintTicket{index}.xml" : $"/PrintTicket{index}.xml";
            if (!package.Parts.ContainsKey(candidate))
                return candidate;
        }
    }

    private static string NextRelId(List<XpsRelationship> rels)
    {
        var used = rels.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        for (int index = 1; ; index++)
        {
            string candidate = $"rIdPT{index}";
            if (!used.Contains(candidate))
                return candidate;
        }
    }

    internal static string DecodeXml(byte[] data)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(data);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(data);
        }
    }
}
