using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace XRay.ThreeD.Formats.Fusion;

using XRay.ThreeD.Internal;

/// <summary>
/// Autodesk Fusion design files: <c>.f3d</c> (a ZIP package whose entries are stored, deflated or Zstandard-compressed, method 93) and <c>.f3z</c> (a ZIP of
/// <c>Manifest.json</c>, <c>DesignDescription.json</c> and one <c>.f3d</c> or <c>.f2d</c> member per document). Read from the format's public clean-room
/// description (see THIRD_PARTY_NOTICES): the top-level <c>Manifest.dat</c> (length-prefixed strings: version, document type, display name, description, document
/// and asset GUIDs, a capability registry, the asset folders and the lineage), the per-asset manifest (asset type, revision, capabilities), <c>Properties.dat</c>
/// (the <c>docstruct</c> declaring a part or an assembly), configuration tables and the list of B-rep streams. The B-rep (ASM binary streams), the design records
/// and the display buffers are not decoded; <see cref="Read"/> says so.
/// </summary>
internal static class FusionReader
{
    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var meta = ReadMetadata(data, options);
        throw new UnsupportedModelFormatException(ModelFormats.Fusion, "Fusion geometry (ASM B-rep streams and display buffers) is not decoded; the manifest, document type, configurations and stream inventory are available through ReadMetadata" + (meta.Notes.Count > 0 ? " (" + meta.Notes[0] + ")" : "") + ".");
    }

    public static PreviewImage? ReadPreview(ReadOnlyMemory<byte> data, LoadOptions options) => ReadPreview(data, options, 0);

    private static PreviewImage? ReadPreview(ReadOnlyMemory<byte> data, LoadOptions options, int depth)
    {
        if (ZipPackage.TryOpen(data, options.Cancellation) is not { } zip)
        {
            return null;
        }

        foreach (var e in zip.Entries.Where(e => e.Size > 0 && (e.Name.Contains("/Previews/", StringComparison.OrdinalIgnoreCase) || e.Name.Contains("/Images.BlobParts/", StringComparison.OrdinalIgnoreCase) || e.Name.StartsWith("Previews/", StringComparison.OrdinalIgnoreCase))))
        {
            var type = e.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : e.Name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ? "image/jpeg" : null;
            if (type is not null && zip.Read(e, 1 << 24, options.Cancellation) is { Length: > 8 } bytes)
            {
                return new PreviewImage(type, bytes);
            }
        }

        if (depth == 0 && zip.Find("Manifest.json") is { } manifest && zip.Read(manifest, 1 << 20, options.Cancellation) is { } json && RootMember(json) is { } root
            && zip.Find(root) is { } member && zip.Read(member, 1L << 28, options.Cancellation) is { } inner)
        {
            return ReadPreview(inner, options, 1);
        }

        return null;
    }

    public static ModelMetadata ReadMetadata(ReadOnlyMemory<byte> data, LoadOptions options) => ReadMetadata(data, options, 0);

    private static ModelMetadata ReadMetadata(ReadOnlyMemory<byte> data, LoadOptions options, int depth)
    {
        var token = options.Cancellation;
        var zip = ZipPackage.TryOpen(data, token) ?? throw new ModelFormatException("Not a Fusion package: the ZIP directory is damaged.");
        var meta = new ModelMetadata { Format = ModelFormats.Fusion, DocumentKind = "design", Units = LengthUnit.Centimeter, HasGeometry = false };

        if (zip.Find("Manifest.json") is { } manifestJson && zip.Find("DesignDescription.json") is not null)
        {
            ReadArchive(zip, meta, options, depth, manifestJson);
            return meta;
        }

        if (zip.Find("Manifest.dat") is not { } manifest || zip.Read(manifest, 1 << 20, token) is not { } bytes)
        {
            throw new ModelFormatException("Not a Fusion package: Manifest.dat is missing.");
        }

        ReadManifest(bytes, meta);
        meta.Application = "Autodesk Fusion";

        if (zip.Find("Properties.dat") is { } props && zip.Read(props, 1 << 20, token) is { Length: > 4 } p)
        {
            var length = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(p), (uint)(p.Length - 4));
            if (length > 0)
            {
                ReadDocStruct(p.AsSpan(4, length), meta);
            }
        }

        var assetFolder = zip.Entries.Select(e => e.Name).Where(n => n.EndsWith("/Manifest.dat", StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal).FirstOrDefault();
        if (assetFolder is not null && zip.Find(assetFolder) is { } assetEntry && zip.Read(assetEntry, 1 << 20, token) is { } asset)
        {
            ReadAssetManifest(asset, meta);
        }

        long brepBytes = 0;
        var breps = 0;
        var tick = 0L;
        foreach (var e in zip.Entries)
        {
            Cancel.Every(token, tick++);
            if (e.Name.Contains("/Breps.BlobParts/", StringComparison.Ordinal) && (e.Name.EndsWith(".smb", StringComparison.Ordinal) || e.Name.EndsWith(".smbh", StringComparison.Ordinal) || e.Name.EndsWith(".sat", StringComparison.Ordinal) || e.Name.EndsWith(".smt", StringComparison.Ordinal)))
            {
                breps++;
                brepBytes += e.Size;
            }
            else if (e.Name.EndsWith(".dsgcfg", StringComparison.Ordinal) && e.Size is > 2 and < (1 << 20) && zip.Read(e, 1 << 20, token) is { } table)
            {
                ReadConfigurations(table, meta);
            }
            else if (e.Name.Contains("/ParaMeshGeometry.BlobParts/", StringComparison.Ordinal) && !e.Name.EndsWith('/'))
            {
                meta.Notes.Add("A mesh body container is present (" + e.Name[(e.Name.LastIndexOf('/') + 1)..] + ").");
            }
        }

        meta.HasPreview = zip.Entries.Any(e => e.Size > 0 && (e.Name.Contains("/Previews/", StringComparison.OrdinalIgnoreCase) || e.Name.Contains("/Images.BlobParts/", StringComparison.OrdinalIgnoreCase)) && (e.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)));
        meta.Properties.Add(new ModelProperty("B-rep streams", breps.ToString(CultureInfo.InvariantCulture) + " (" + brepBytes.ToString(CultureInfo.InvariantCulture) + " bytes)", "package"));
        meta.Notes.Add(breps > 0
            ? "The geometry is stored as " + breps.ToString(CultureInfo.InvariantCulture) + " ASM B-rep stream(s) and display buffers, which are not decoded."
            : "The package holds no B-rep stream (an assembly of external references, or mesh bodies only).");
        return meta;
    }

    private static string? RootMember(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("root", out var root) && root.ValueKind == JsonValueKind.String ? root.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void ReadArchive(ZipPackage zip, ModelMetadata meta, LoadOptions options, int depth, ZipPackage.Entry manifestJson)
    {
        var token = options.Cancellation;
        meta.DocumentKind = "assembly";
        meta.Notes.Add("A multi-document archive (.f3z).");
        var members = zip.Entries.Where(e => e.Name.EndsWith(".f3d", StringComparison.OrdinalIgnoreCase) || e.Name.EndsWith(".f2d", StringComparison.OrdinalIgnoreCase)).Select(e => e.Name).ToList();
        meta.ComponentFiles.AddRange(members);
        var root = zip.Read(manifestJson, 1 << 20, token) is { } json ? RootMember(json) : null;
        if (root is not null)
        {
            meta.Properties.Add(new ModelProperty("Root document", root, "archive"));
        }

        if (zip.Find("DesignDescription.json") is { } description && zip.Read(description, 1 << 24, token) is { } bytes)
        {
            try
            {
                using var doc = JsonDocument.Parse(bytes);
                if (doc.RootElement.TryGetProperty("designDescription", out var dd))
                {
                    if (dd.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                    {
                        meta.Title = name.GetString();
                    }

                    if (dd.TryGetProperty("designGraphs", out var graphs) && graphs.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var graph in graphs.EnumerateArray().Take(16))
                        {
                            if (graph.TryGetProperty("creationDate", out var date) && date.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(date.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when))
                            {
                                meta.Created ??= when;
                            }

                            if (graph.TryGetProperty("creatingService", out var service) && service.ValueKind == JsonValueKind.String)
                            {
                                meta.Properties.Add(new ModelProperty("Creating service", service.GetString()!, "archive"));
                            }

                            if (graph.TryGetProperty("designObjects", out var objects) && objects.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var o in objects.EnumerateArray().Take(512))
                                {
                                    if (o.TryGetProperty("friendlyName", out var friendly) && friendly.ValueKind == JsonValueKind.String && friendly.GetString() is { Length: > 0 } n && !meta.PartNames.Contains(n))
                                    {
                                        meta.PartNames.Add(n);
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (JsonException)
            {
            }
        }

        if (depth == 0 && root is not null && zip.Find(root) is { } member && root.EndsWith(".f3d", StringComparison.OrdinalIgnoreCase)
            && zip.Read(member, 1L << 28, token) is { } inner)
        {
            var rootMeta = ReadMetadata(inner, options, 1);
            meta.Application = rootMeta.Application;
            meta.HasPreview = rootMeta.HasPreview;
            foreach (var p in rootMeta.Properties)
            {
                meta.Properties.Add(p with { Group = "root: " + p.Group });
            }

            meta.Configurations.AddRange(rootMeta.Configurations);
            meta.Notes.AddRange(rootMeta.Notes);
            meta.Properties.Add(new ModelProperty("Root document type", rootMeta.DocumentKind ?? "design", "archive"));
        }
    }

    // ---- Manifest.dat ----

    private ref struct Cursor
    {
        private readonly ReadOnlySpan<byte> _d;
        public int P;

        public Cursor(ReadOnlySpan<byte> d)
        {
            _d = d;
            P = 0;
        }

        public readonly bool Has(int n) => P + n <= _d.Length;

        public uint U32()
        {
            if (!Has(4))
            {
                throw new FormatException();
            }

            var v = BinaryPrimitives.ReadUInt32LittleEndian(_d[P..]);
            P += 4;
            return v;
        }

        public string Ascii()
        {
            var n = U32();
            if (n > 4096 || !Has((int)n))
            {
                throw new FormatException();
            }

            var s = Encoding.ASCII.GetString(_d.Slice(P, (int)n));
            P += (int)n;
            return s;
        }

        public string Utf16()
        {
            var n = U32();
            if (n > 4096 || !Has((int)n * 2))
            {
                throw new FormatException();
            }

            var s = Encoding.Unicode.GetString(_d.Slice(P, (int)n * 2));
            P += (int)n * 2;
            return s;
        }
    }

    private static void ReadManifest(byte[] bytes, ModelMetadata meta)
    {
        try
        {
            var c = new Cursor(bytes);
            var version = c.Ascii();
            var type = c.Ascii();
            var extension = c.Utf16();
            var name = c.Utf16();
            var description = c.Utf16();
            var documentGuid = c.Utf16();
            var assetGuid = c.Utf16();
            meta.FormatVersion = version;
            meta.Title = string.IsNullOrWhiteSpace(name) ? null : name;
            Add(meta, "Document type", type + " " + extension, "manifest");
            Add(meta, "Description", description, "manifest");
            Add(meta, "Document GUID", documentGuid, "manifest");
            Add(meta, "Document asset GUID", assetGuid, "manifest");

            // The generation header: 1234 and three values, or one other value and one more; then the capability registry.
            var first = c.U32();
            c.U32();
            if (first == 1234)
            {
                c.U32();
                c.U32();
            }
            else
            {
                c.U32();
            }

            // The tail is found as the suffix that starts at the active asset GUID (a 36-character UTF-16 string followed by a folder count).
            var start = c.P;
            var span = bytes.AsSpan();
            for (var p = start; p + 4 + 72 + 4 <= span.Length; p++)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(span[p..]) != 36 || !LooksLikeGuid(span.Slice(p + 4, 72)))
                {
                    continue;
                }

                try
                {
                    var tail = new Cursor(span) { P = p };
                    var active = tail.Utf16();
                    var folders = new List<string>();
                    var count = tail.U32();
                    if (count is 0 or > 64)
                    {
                        continue;
                    }

                    for (var i = 0; i < count; i++)
                    {
                        folders.Add(tail.Utf16());
                    }

                    var secondary = tail.U32();
                    if (secondary > 64)
                    {
                        continue;
                    }

                    for (var i = 0; i < secondary; i++)
                    {
                        folders.Add(tail.Utf16());
                    }

                    Add(meta, "Active asset GUID", active, "manifest");
                    meta.Properties.Add(new ModelProperty("Asset folders", string.Join(", ", folders), "manifest"));
                    if (tail.Has(1))
                    {
                        var marker = span[tail.P++];
                        if (marker == 0 && tail.Has(4))
                        {
                            var shown = tail.Utf16();
                            Add(meta, "Display name", shown, "manifest");
                            if (tail.Has(4) && tail.Utf16() is { } urn && urn.StartsWith("urn:", StringComparison.Ordinal))
                            {
                                Add(meta, "Lineage URN", urn, "manifest");
                            }
                        }
                        else if (marker == 1 && tail.Has(4))
                        {
                            var text = tail.Utf16();
                            if (text.StartsWith("urn:", StringComparison.Ordinal))
                            {
                                Add(meta, "Lineage URN", text, "manifest");
                            }
                            else
                            {
                                Add(meta, "Origin", text, "manifest");
                            }
                        }
                    }

                    break;
                }
                catch (FormatException)
                {
                }
            }
        }
        catch (FormatException)
        {
            meta.Notes.Add("The manifest is shorter than its grammar says; only part of it was read.");
        }
    }

    private static bool LooksLikeGuid(ReadOnlySpan<byte> utf16)
    {
        for (var i = 0; i < 36; i++)
        {
            var c = utf16[2 * i];
            if (utf16[2 * i + 1] != 0 || !(i is 8 or 13 or 18 or 23 ? c == '-' : char.IsAsciiHexDigit((char)c)))
            {
                return false;
            }
        }

        return true;
    }

    private static void ReadAssetManifest(byte[] bytes, ModelMetadata meta)
    {
        try
        {
            var c = new Cursor(bytes);
            var baseName = c.Utf16();
            c.Utf16();
            c.Utf16();
            var assetType = c.Ascii();
            Add(meta, "Asset type", assetType, "asset");
            if (assetType == "FusionAssetType")
            {
                var revision = c.U32();
                Add(meta, "Asset revision", revision.ToString(CultureInfo.InvariantCulture), "asset");
                if (revision is 11 or 12 or 13 or 14 or 15 or 19 or 20)
                {
                    var count = c.U32();
                    if (count <= 64)
                    {
                        var names = new List<string>();
                        for (var i = 0; i < count; i++)
                        {
                            names.Add(c.Ascii());
                            c.U32();
                        }

                        if (names.Count > 0)
                        {
                            Add(meta, "Capabilities", string.Join(", ", names), "asset");
                        }
                    }
                }
            }

            _ = baseName;
        }
        catch (FormatException)
        {
        }
    }

    private static void ReadDocStruct(ReadOnlySpan<byte> json, ModelMetadata meta)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            if (doc.RootElement.TryGetProperty("docstruct", out var ds) && ds.ValueKind == JsonValueKind.Object)
            {
                var type = ds.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                var subtype = ds.TryGetProperty("subtype", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                if (type is not null)
                {
                    meta.DocumentKind = type.StartsWith("assembly", StringComparison.Ordinal) ? "assembly" : type.StartsWith("part", StringComparison.Ordinal) ? "part" : type;
                    Add(meta, "Document structure", type + (subtype is null ? "" : " / " + subtype), "properties");
                }
            }
        }
        catch (JsonException)
        {
        }
    }

    private static void ReadConfigurations(byte[] json, ModelMetadata meta)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("configurations", out var configs) && configs.ValueKind == JsonValueKind.Object)
            {
                foreach (var c in configs.EnumerateObject().Take(256))
                {
                    if (!meta.Configurations.Contains(c.Name))
                    {
                        meta.Configurations.Add(c.Name);
                    }
                }
            }
        }
        catch (JsonException)
        {
        }
    }

    private static void Add(ModelMetadata meta, string name, string? value, string group)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            meta.Properties.Add(new ModelProperty(name, value.Trim(), group));
        }
    }
}
