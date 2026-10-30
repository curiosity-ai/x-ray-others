using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace XRay.ThreeD.Formats.Cad;

using XRay.ThreeD.Internal;

/// <summary>
/// Formats that keep their document in an OLE compound file and whose geometry is proprietary: Autodesk Inventor (.ipt, .iam, .idw) and
/// Revit (.rvt, .rfa, .rft). Only what the container documents is read. Property sets are found by their format id, not by stream name
/// (Inventor names its streams after hashes of the id): summary and document summary (title, author, dates, application, company, thumbnail),
/// Inventor's design tracking properties (part number, description, material, designer, subtype, version), its control set (last save) and the
/// user-defined properties with their names. For Revit: the text of BasicFileInfo (which Revit 2019 and later prefix with binary fields that
/// leave the text on an odd byte offset), the zipped Atom document of ProjectInformation, the linked files of TransmissionData, the Revit
/// versions that saved the file (Global/Latest, a gzip stream) and the PNG inside RevitPreview. Geometry is not decoded, and
/// <see cref="Read"/> says so.
/// </summary>
internal static partial class CompoundDocumentReader
{
    private static readonly Guid SummaryId = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9");
    private static readonly Guid InventorSummaryId = new("3D38DE39-0588-4C14-BB37-18F4D5DD31C7");
    private static readonly Guid DocumentSummaryId = new("D5CDD502-2E9C-101B-9397-08002B2CF9AE");
    private static readonly Guid InventorDocumentSummaryId = new("8CF58000-DA66-4AE6-8FF0-7B58406FB049");
    private static readonly Guid UserDefinedId = new("D5CDD505-2E9C-101B-9397-08002B2CF9AE");
    private static readonly Guid InventorUserDefinedId = new("9929ADB8-6407-413E-B3DC-CB9AD2F564B7");
    private static readonly Guid DesignTrackingId = new("32853F0F-3444-11D1-9E93-0060B03C1CA6");
    private static readonly Guid DesignControlId = new("D861FB30-3136-11D1-9E92-0060B03C1CA6");

    public static Model3D Read(FormatInfo format, ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var meta = ReadMetadata(format, data, options);
        throw new UnsupportedModelFormatException(format, $"{format.Name} geometry is proprietary and not decoded; the document properties ({meta.Properties.Count} found) and the preview are available through ReadMetadata and ReadPreview.");
    }

    /// <summary>The property sets of every stream named like a property set (leading U+0005, at any depth), by format id; the first of a kind wins.</summary>
    private static Dictionary<Guid, OlePropertySet> PropertySets(CompoundFile file, CancellationToken token)
    {
        var sets = new Dictionary<Guid, OlePropertySet>();
        var read = 0;
        foreach (var name in file.StreamNames.Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            var slash = name.LastIndexOf('/');
            if (name[(slash + 1)..].StartsWith('\u0005') && read++ < 64 && file.Read(name, token, 1 << 24) is { } bytes)
            {
                foreach (var set in OlePropertySet.ParseAll(bytes, token))
                {
                    sets.TryAdd(set.Fmtid, set);
                }
            }
        }

        return sets;
    }

    private static OlePropertySet? Pick(Dictionary<Guid, OlePropertySet> sets, Guid a, Guid b) =>
        sets.TryGetValue(a, out var x) ? x : sets.TryGetValue(b, out var y) ? y : null;

    public static ModelMetadata ReadMetadata(FormatInfo format, ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var token = options.Cancellation;
        var file = new CompoundFile(data, token);
        var meta = new ModelMetadata { Format = format, DocumentKind = format.Id == "revit" ? "bim" : "cad", HasGeometry = false };
        var sets = PropertySets(file, token);

        if (Pick(sets, SummaryId, InventorSummaryId) is { } summary)
        {
            meta.Title = Clean(summary.Text(2));
            Add(meta, "Subject", summary.Text(3));
            meta.Author = Clean(summary.Text(4));
            Add(meta, "Keywords", summary.Text(5));
            Add(meta, "Comments", summary.Text(6));
            meta.LastModifiedBy = Clean(summary.Text(8));
            meta.Created = summary.Time(12);
            meta.Modified = summary.Time(13);
            meta.Application = Clean(summary.Text(18)) ?? meta.Application;
            meta.HasPreview = summary.Blob(17) is { } blob && Sniff(blob, token) is not null;
        }

        if (Pick(sets, DocumentSummaryId, InventorDocumentSummaryId) is { } document)
        {
            meta.Company = Clean(document.Text(15));
            Add(meta, "Category", document.Text(2));
            Add(meta, "Manager", document.Text(14));
        }

        foreach (var id in new[] { UserDefinedId, InventorUserDefinedId })
        {
            if (sets.TryGetValue(id, out var custom))
            {
                foreach (var (pid, value) in custom.Values.OrderBy(v => v.Key))
                {
                    if (pid != 255 && custom.Names.TryGetValue(pid, out var name) && Format(value) is { } text)
                    {
                        meta.Properties.Add(new ModelProperty(name, text, "custom"));
                    }
                }
            }
        }

        if (format.Id == "inventor")
        {
            ReadInventorTracking(sets, meta);
            if (file.Read("UFRxDoc", token, 1 << 24) is { Length: > 16 } references)
            {
                ReadInventorReferences(references, meta, token);
            }
        }

        if (format.Id == "revit")
        {
            ReadRevitInfo(file, meta, token);
        }

        meta.Notes.Add($"{format.Name} geometry is proprietary and is not decoded; only the document properties and the preview are read.");
        return meta;
    }

    private static string? Format(object value) => value switch
    {
        string s => Clean(s),
        DateTimeOffset t => t.ToString("O", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>
    /// Inventor's "Design Tracking Properties". The ids are the ones checked against real files (part number 5, description 29, material 20,
    /// designer 41, appearance 72, creation time 4, document subtype 32, application build 67); the control set holds the last save (user 16, time 17).
    /// </summary>
    private static void ReadInventorTracking(Dictionary<Guid, OlePropertySet> sets, ModelMetadata meta)
    {
        if (sets.TryGetValue(DesignTrackingId, out var design))
        {
            Add(meta, "Part Number", design.Text(5), "design");
            Add(meta, "Description", design.Text(29), "design");
            Add(meta, "Material", design.Text(20), "design");
            Add(meta, "Appearance", design.Text(72), "design");
            Add(meta, "Designer", design.Text(41), "design");
            Add(meta, "Stock Number", design.Text(55), "design");
            Add(meta, "Revision Number", design.Text(7), "design");
            Add(meta, "Document Subtype", design.Text(32), "design");
            meta.Created ??= design.Time(4);
            if (Clean(design.Text(67)) is { } build)
            {
                meta.Application ??= "Autodesk Inventor " + build;
            }

            meta.Author ??= Clean(design.Text(41));
            meta.DocumentKind = design.Text(32) switch
            {
                { } kind when kind.Contains("Assembly", StringComparison.OrdinalIgnoreCase) => "assembly",
                { } kind when kind.Contains("Drawing", StringComparison.OrdinalIgnoreCase) => "drawing",
                _ => "part",
            };
        }

        if (sets.TryGetValue(DesignControlId, out var control))
        {
            meta.Modified ??= control.Time(17);
            meta.LastModifiedBy ??= Clean(control.Text(16));
        }
    }

    /// <summary>
    /// The UFRxDoc stream keeps the full names of the documents an assembly (or a part with derived or referenced documents) refers to as
    /// UTF-16 text; the file names of those that look like Inventor, drawing or exchange files go to <see cref="ModelMetadata.ComponentFiles"/>.
    /// </summary>
    private static void ReadInventorReferences(byte[] bytes, ModelMetadata meta, CancellationToken token)
    {
        var span = bytes.AsSpan();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var align = 0; align < 2; align++)
        {
            var i = align;
            while (i + 1 < span.Length && meta.ComponentFiles.Count < 2048)
            {
                Cancel.Every(token, i);
                var start = i;
                while (i + 1 < span.Length && span[i + 1] == 0 && span[i] >= 0x20 && span[i] < 0x7F)
                {
                    i += 2;
                }

                var length = i - start;
                if (length >= 12 && length <= 2 * 520)
                {
                    var text = Encoding.Unicode.GetString(span.Slice(start, length));
                    if (InventorFile().IsMatch(text))
                    {
                        var name = text.Replace('\\', '/');
                        name = name[(name.LastIndexOf('/') + 1)..];
                        if (seen.Add(name))
                        {
                            meta.ComponentFiles.Add(name);
                        }
                    }
                }

                i = Math.Max(i, start) + 2;
            }
        }
    }

    [GeneratedRegex(@"^(?:[A-Za-z]:\\|\\\\|\.{0,2}\\|/).*\.(?:ipt|iam|ipn|idw|dwg|stp|step|sat|jt|prt|sldprt|sldasm)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 500)]
    private static partial Regex InventorFile();

    public static PreviewImage? ReadPreview(FormatInfo format, ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var token = options.Cancellation;
        var file = new CompoundFile(data, token);
        if (format.Id == "revit")
        {
            foreach (var name in file.StreamNames.Where(n => n.StartsWith("RevitPreview", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                if (file.Read(name, token, 1 << 24) is { } bytes && FindPng(bytes) is { } png)
                {
                    return new PreviewImage("image/png", png);
                }
            }
        }

        var sets = PropertySets(file, token);
        return Pick(sets, SummaryId, InventorSummaryId)?.Blob(17) is { } blob ? Sniff(blob, token) : null;
    }

    private static void ReadRevitInfo(CompoundFile file, ModelMetadata meta, CancellationToken token)
    {
        if (file.Read("BasicFileInfo", token, 1 << 22) is { Length: > 8 } bytes)
        {
            ReadBasicFileInfo(bytes, meta, token);
        }

        foreach (var name in file.StreamNames.Where(n => n.StartsWith("RevitPreview", StringComparison.Ordinal)))
        {
            if (file.Read(name, token, 1 << 24) is { } preview && FindPng(preview) is not null)
            {
                meta.HasPreview = true;
                break;
            }
        }

        if (file.Read("ProjectInformation", token, 1 << 22) is { Length: > 30 } project)
        {
            ReadProjectInformation(project, meta, token);
        }

        if (file.Read("TransmissionData", token, 1 << 22) is { Length: > 8 } transmission)
        {
            ReadTransmissionData(transmission, meta, token);
        }

        if (file.Read("Global/Latest", token, 1 << 24) is { Length: > 18 } global)
        {
            ReadSaveHistory(global, meta, token);
        }
    }

    private static void ReadBasicFileInfo(byte[] bytes, ModelMetadata meta, CancellationToken token)
    {
        // UTF-16 "Key: value" lines after a binary header. Revit 2019 and later put length-prefixed strings (version, build, path) in that header,
        // which leaves the text on an odd byte offset, so both alignments are tried and the one with more lines wins. Control characters
        // become line breaks.
        var best = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var start = 0; start < 2; start++)
        {
            var chars = Encoding.Unicode.GetString(bytes, start, (bytes.Length - start) & ~1).Select(c => c < ' ' && c is not ('\r' or '\n') ? '\n' : c).ToArray();
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match m in RevitLine().Matches(new string(chars)))
            {
                token.ThrowIfCancellationRequested();
                found.TryAdd(m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim());
            }

            if (found.Count > best.Count)
            {
                best = found;
            }
        }

        if (best.TryGetValue("Revit Build", out var revitBuild) && revitBuild.Length > 0)
        {
            meta.Application = revitBuild;
        }
        else if (best.TryGetValue("Format", out var year) && year.Length > 0)
        {
            meta.Application = "Autodesk Revit " + year + (best.TryGetValue("Build", out var build) && build.Length > 0 ? " (Build: " + build + ")" : "");
        }

        if (best.TryGetValue("Format", out var format))
        {
            meta.FormatVersion = format;
        }

        foreach (var key in new[] { "Username", "Worksharing", "Central Model Path", "Last Save Path", "Locale when saved", "Locale", "Open Workset Default", "Unique Document GUID", "Author" })
        {
            if (best.TryGetValue(key, out var value))
            {
                Add(meta, key == "Locale when saved" ? "Locale" : key, value, "document");
            }
        }
    }

    [GeneratedRegex(@"(Revit Build|Build|Format|Username|Worksharing|Central Model Path|Last Save Path|Locale when saved|Locale|Open Workset Default|Unique Document GUID|Author):[ \t]*([^\r\n]*)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex RevitLine();

    private static XDocument? LoadXml(Stream stream)
    {
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1 << 22 };
            using var reader = XmlReader.Create(stream, settings);
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    /// <summary>ProjectInformation is a ZIP holding one Atom document (title, save time, product version, the project's identity fields).</summary>
    private static void ReadProjectInformation(byte[] bytes, ModelMetadata meta, CancellationToken token)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && e.Length is > 0 and < (1 << 22));
            if (entry is null)
            {
                return;
            }

            using var stream = entry.Open();
            if (LoadXml(stream)?.Root is not { } root)
            {
                return;
            }

            token.ThrowIfCancellationRequested();
            var title = root.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value;
            meta.Title ??= Clean(title);
            if (root.Elements().FirstOrDefault(e => e.Name.LocalName == "updated")?.Value is { } updated
                && DateTimeOffset.TryParse(updated, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when))
            {
                meta.Modified ??= when;
            }

            var version = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "product-version")?.Value;
            if (Clean(version) is { } v)
            {
                meta.FormatVersion ??= v;
            }

            foreach (var e in root.Descendants().Where(e => e.Attribute("displayName") is not null && !e.HasElements).Take(64))
            {
                Add(meta, e.Attribute("displayName")!.Value, e.Value, "project");
            }
        }
        catch (InvalidDataException)
        {
        }
    }

    /// <summary>TransmissionData is a length, then UTF-16 XML listing the files the model links (keynote and code tables, linked models, CAD links, images).</summary>
    private static void ReadTransmissionData(byte[] bytes, ModelMetadata meta, CancellationToken token)
    {
        var text = Encoding.Unicode.GetString(bytes, 4, (bytes.Length - 4) & ~1).TrimEnd('\0');
        var start = text.IndexOf('<');
        if (start < 0)
        {
            return;
        }

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text[start..].Replace("encoding=\"UTF-16\"", "", StringComparison.Ordinal)), writable: false);
        if (LoadXml(stream)?.Root is not { } root)
        {
            return;
        }

        foreach (var reference in root.Elements().Where(e => e.Name.LocalName == "ExternalFileReference").Take(512))
        {
            token.ThrowIfCancellationRequested();
            var path = reference.Elements().FirstOrDefault(e => e.Name.LocalName == "LastSavedPath")?.Value;
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            var name = path.Replace('\\', '/');
            name = name[(name.LastIndexOf('/') + 1)..];
            if (name.Length > 0 && !meta.ComponentFiles.Contains(name))
            {
                meta.ComponentFiles.Add(name);
                var kind = reference.Elements().FirstOrDefault(e => e.Name.LocalName == "ExternalFileReferenceType")?.Value;
                Add(meta, string.IsNullOrWhiteSpace(kind) ? "Linked file" : kind!, name, "links");
            }
        }
    }

    /// <summary>Global/Latest is an 8-byte prefix and a gzip stream; it lists, as UTF-16 text, the Revit builds that saved the file.</summary>
    private static void ReadSaveHistory(byte[] bytes, ModelMetadata meta, CancellationToken token)
    {
        var gzip = bytes.AsSpan().IndexOf(new byte[] { 0x1F, 0x8B, 0x08 });
        if (gzip < 0 || gzip > 64 || gzip + 10 > bytes.Length)
        {
            return;
        }

        var raw = new MemoryStream();
        try
        {
            // Revit's gzip trailer does not check out, so the deflate data is read directly after the 10-byte header (no name or extra fields).
            if ((bytes[gzip + 3] & 0x1E) != 0)
            {
                return;
            }

            using var deflate = new DeflateStream(new MemoryStream(bytes, gzip + 10, bytes.Length - gzip - 10, writable: false), CompressionMode.Decompress);
            var buffer = new byte[65536];
            int read;
            while (raw.Length < (1 << 24) && (read = deflate.Read(buffer, 0, buffer.Length)) > 0)
            {
                raw.Write(buffer, 0, read);
                token.ThrowIfCancellationRequested();
            }
        }
        catch (InvalidDataException)
        {
        }

        var span = raw.GetBuffer().AsSpan(0, (int)raw.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var align = 0; align < 2; align++)
        {
            var i = align;
            while (i + 1 < span.Length && seen.Count < 32)
            {
                var start = i;
                while (i + 1 < span.Length && span[i + 1] == 0 && span[i] >= 0x20 && span[i] < 0x7F)
                {
                    i += 2;
                }

                if (i - start >= 24)
                {
                    var text = Encoding.Unicode.GetString(span.Slice(start, i - start));
                    if (text.StartsWith("Revit 20", StringComparison.Ordinal) && seen.Add(text.TrimEnd('/')))
                    {
                        Add(meta, "Saved with", text.TrimEnd('/'), "history");
                    }
                }

                i = Math.Max(i, start) + 2;
            }
        }
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void Add(ModelMetadata meta, string name, string? value, string group = "document")
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            meta.Properties.Add(new ModelProperty(name, value.Trim(), group));
        }
    }

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The PNG that starts at the first signature in <paramref name="bytes"/>, ending after its IEND chunk.</summary>
    private static byte[]? FindPng(ReadOnlySpan<byte> bytes)
    {
        var start = bytes.IndexOf(PngSignature);
        if (start < 0)
        {
            return null;
        }

        var p = start + 8;
        for (var chunks = 0; chunks < 100_000 && p + 12 <= bytes.Length; chunks++)
        {
            var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes[p..]);
            if (length > bytes.Length - p - 12)
            {
                return null;
            }

            var isEnd = bytes.Slice(p + 4, 4).SequenceEqual("IEND"u8);
            p += 12 + (int)length;
            if (isEnd)
            {
                return bytes[start..p].ToArray();
            }
        }

        return null;
    }

    /// <summary>Clipboard thumbnail data as a PNG: stored PNG as is, a DIB (with or without a file header) converted.</summary>
    private static PreviewImage? Sniff(byte[] blob, CancellationToken token)
    {
        // Inventor's thumbnail is a tagged clipboard value with an 8-byte metafile header (mode and extents) in front of the PNG.
        if (blob.AsSpan().IndexOf(PngSignature) is >= 0 and <= 16 && FindPng(blob) is { } png)
        {
            return new PreviewImage("image/png", png);
        }

        if (blob.Length > 2 && blob[0] == 0xFF && blob[1] == 0xD8)
        {
            return new PreviewImage("image/jpeg", blob);
        }

        return DibImage.DecodeRgb(blob, out var w, out var h, token) is { } rgb ? new PreviewImage("image/png", DibImage.EncodePng(rgb, w, h)) : null;
    }
}
