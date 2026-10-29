using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace XRay.ThreeD.Formats.SolidWorks;

/// <summary>
/// Reads SolidWorks parts, assemblies and drawings (.sldprt, .sldasm, .slddrw) saved by SolidWorks 2015 or
/// later: the document properties, the component list and the display mesh SolidWorks itself draws.
/// Documents that embed no geometry (assemblies whose components are external files, by-reference saves)
/// load as metadata with a note.
/// </summary>
internal static class SolidWorksReader
{
    private const string DisplayLists = "Contents/DisplayLists";

    public static bool Sniff(ReadOnlyMemory<byte> data, CancellationToken cancellation) =>
        SwContainer.TryOpen(data, cancellation) is { } c && (c.Contains("docProps/app.xml") || c.Contains(DisplayLists) || c.Contains("Header2"));

    public static ModelMetadata ReadMetadata(ReadOnlyMemory<byte> data, CancellationToken cancellation)
    {
        var container = Open(data, cancellation);
        var metadata = ReadMetadata(container, out var stream, cancellation);
        if (stream is not null)
        {
            var decoded = SwDisplayLists.Decode(stream, metadata.DocumentKind == "assembly", cancellation);
            metadata.HasGeometry = decoded.Instances.Count > 0;
            metadata.Statistics = Count(decoded);
        }

        FinishNotes(metadata);
        return metadata;
    }

    public static Model3D Read(ReadOnlyMemory<byte> data, CancellationToken cancellation)
    {
        var container = Open(data, cancellation);
        var model = new Model3D();
        var metadata = ReadMetadata(container, out var stream, cancellation);
        CopyInto(model.Metadata, metadata);
        if (container.Read("PreviewPNG") is { Length: > 8 } png && png[0] == 0x89 && png[1] == (byte)'P')
        {
            model.Preview = new PreviewImage("image/png", png);
            model.Metadata.HasPreview = true;
        }

        if (stream is not null)
        {
            var decoded = SwDisplayLists.Decode(stream, metadata.DocumentKind == "assembly", cancellation);
            BuildScene(model, stream, decoded, metadata);
            model.Metadata.HasGeometry = model.Meshes.Count > 0;
            model.Metadata.Statistics = model.ComputeStatistics();
            model.Metadata.Bounds = model.ComputeBounds();
        }

        FinishNotes(model.Metadata);
        model.Warnings.AddRange(model.Metadata.Notes);
        return model;
    }

    private static SwContainer Open(ReadOnlyMemory<byte> data, CancellationToken cancellation) =>
        SwContainer.TryOpen(data, cancellation)
        ?? throw new UnsupportedModelFormatException(
            ModelFormats.SolidWorks,
            "Not a SolidWorks 2015+ document. Older SolidWorks files (compound-file envelope) are not supported yet.");

    private static void FinishNotes(ModelMetadata metadata)
    {
        if (metadata.DocumentKind == "drawing")
        {
            metadata.Notes.Add("A drawing holds sheets, not 3D geometry.");
        }
        else if (!metadata.HasGeometry)
        {
            metadata.Notes.Add(metadata.ComponentFiles.Count > 0
                ? "This assembly embeds no geometry; its components live in the files listed in ComponentFiles."
                : "The document embeds no display geometry (a by-reference save).");
        }
    }

    private static ModelStatistics Count(SwDisplayLists.Result decoded)
    {
        var perGeometry = decoded.Geometries
            .Select(g => (Vertices: (long)g.Faces.Sum(f => f.VertexCount), Triangles: (long)g.Faces.Sum(f => f.StripLengths.Sum(l => Math.Max(0, l - 2)))))
            .ToArray();
        long vertices = 0;
        long triangles = 0;
        foreach (var instance in decoded.Instances)
        {
            vertices += perGeometry[instance.Geometry].Vertices;
            triangles += perGeometry[instance.Geometry].Triangles;
        }

        return new ModelStatistics(decoded.Instances.Count, decoded.Geometries.Count, vertices, triangles, 0, perGeometry.Sum(g => g.Vertices), perGeometry.Sum(g => g.Triangles));
    }

    private static void BuildScene(Model3D model, byte[] stream, SwDisplayLists.Result decoded, ModelMetadata metadata)
    {
        if (decoded.Instances.Count == 0)
        {
            return;
        }

        var meshes = new MeshData[decoded.Geometries.Count];
        for (var i = 0; i < meshes.Length; i++)
        {
            var firstInstance = decoded.Instances.FirstOrDefault(x => x.Geometry == i);
            meshes[i] = SwDisplayLists.BuildMesh(stream, decoded.Geometries[i], PartName(firstInstance?.Name) ?? metadata.Title);
            model.Meshes.Add(meshes[i]);
        }

        var named = decoded.Instances.Any(i => i.Name is not null);
        if (!named)
        {
            model.Roots.Add(new SceneNode(metadata.Title) { Mesh = meshes[decoded.Instances[0].Geometry] });
            return;
        }

        var root = new SceneNode(metadata.Title ?? "Assembly");
        root.Properties["kind"] = "assembly";
        foreach (var instance in decoded.Instances)
        {
            var node = new SceneNode(instance.Name is null ? null : InstanceLabel(instance.Name))
            {
                Transform = instance.Transform,
                Mesh = meshes[instance.Geometry],
            };
            if (instance.Name is not null)
            {
                node.Properties["instancePath"] = instance.Name;
                var part = PartName(instance.Name)!;
                var file = metadata.ComponentFiles.FirstOrDefault(f =>
                    string.Equals(Path.GetFileNameWithoutExtension(f), part, StringComparison.OrdinalIgnoreCase));
                if (file is not null)
                {
                    node.Properties["file"] = file;
                }
            }

            root.Children.Add(node);
        }

        model.Roots.Add(root);
    }

    /// <summary>"Pin-1@Gear" is instance 1 of Pin inside Gear: the node is called "Pin-1".</summary>
    private static string InstanceLabel(string path)
    {
        var at = path.IndexOf('@');
        return at > 0 ? path[..at] : path;
    }

    /// <summary>The part file's name without the instance suffix: "Pin-1@Gear" gives "Pin".</summary>
    private static string? PartName(string? path)
    {
        if (path is null)
        {
            return null;
        }

        var label = InstanceLabel(path);
        var dash = label.LastIndexOf('-');
        return dash > 0 && int.TryParse(label.AsSpan(dash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out _) ? label[..dash] : label;
    }

    private static ModelMetadata ReadMetadata(SwContainer c, out byte[]? displayLists, CancellationToken cancellation)
    {
        var metadata = new ModelMetadata
        {
            Format = ModelFormats.SolidWorks,
            Units = LengthUnit.Meter,
            UpAxis = UpAxis.Y,
        };
        displayLists = c.Read(DisplayLists);

        ReadCoreProperties(c, metadata);
        ReadApp(c, metadata);
        ReadPropertySet(c, "docProps/custom.xml", "custom", metadata);
        ReadPropertySet(c, "docProps/ISolidWorksInformation.xml", "document", metadata);
        ReadPropertySet(c, "swXmlContents/CustomProperties", "custom", metadata, skipNamesAlreadyPresent: true);
        ReadKeywords(c, metadata);
        var kind = ReadComponentTree(c, metadata);
        metadata.DocumentKind = kind
            ?? (c.Contains("SheetPreviews/SheetNames") || c.Contains("DWG/DWG_HEADER") ? "drawing" : "part");
        if (metadata.DocumentKind == "drawing")
        {
            ReadSheets(c, metadata);
        }

        metadata.HasPreview = c.Contains("PreviewPNG");
        return metadata;
    }

    private static void CopyInto(ModelMetadata target, ModelMetadata source)
    {
        target.Format = source.Format;
        target.DocumentKind = source.DocumentKind;
        target.FormatVersion = source.FormatVersion;
        target.Application = source.Application;
        target.Title = source.Title;
        target.Author = source.Author;
        target.Company = source.Company;
        target.Created = source.Created;
        target.Modified = source.Modified;
        target.LastModifiedBy = source.LastModifiedBy;
        target.Units = source.Units;
        target.UpAxis = source.UpAxis;
        target.PartNames.AddRange(source.PartNames);
        target.LayerNames.AddRange(source.LayerNames);
        target.Sheets.AddRange(source.Sheets);
        target.Configurations.AddRange(source.Configurations);
        target.Features.AddRange(source.Features);
        target.MaterialNames.AddRange(source.MaterialNames);
        target.ComponentFiles.AddRange(source.ComponentFiles);
        target.Properties.AddRange(source.Properties);
        target.HasPreview = source.HasPreview;
        target.Notes.AddRange(source.Notes);
    }

    private static XDocument? ParseXml(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return null;
        }

        // Some streams carry a stray byte or two before the XML declaration.
        var start = Array.IndexOf(bytes, (byte)'<');
        if (start < 0)
        {
            return null;
        }

        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64_000_000 };
            using var reader = XmlReader.Create(new MemoryStream(bytes, start, bytes.Length - start), settings);
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static void ReadCoreProperties(SwContainer c, ModelMetadata metadata)
    {
        if (ParseXml(c.Read("docProps/core.xml"))?.Root is not { } root)
        {
            return;
        }

        // The file repeats the group once per save, newest first. Dates are taken as the earliest created and the
        // latest modified rather than trusting the order.
        string? Last(string local) => root.Descendants().LastOrDefault(e => e.Name.LocalName == local)?.Value;
        string? First(string local) => root.Descendants().FirstOrDefault(e => e.Name.LocalName == local)?.Value;
        IEnumerable<DateTimeOffset> Dates(string local) =>
            root.Descendants().Where(e => e.Name.LocalName == local).Select(e => ParseDate(e.Value)).Where(d => d is not null).Select(d => d!.Value);
        metadata.LastModifiedBy = First("lastModifiedBy");
        metadata.Created = Dates("created").Cast<DateTimeOffset?>().Min();
        metadata.Modified = Dates("modified").Cast<DateTimeOffset?>().Max();
        if (Last("creator") is { Length: > 0 } creator)
        {
            metadata.Author = creator;
        }

        if (Last("title") is { Length: > 0 } title)
        {
            metadata.Title = title;
        }
    }

    private static DateTimeOffset? ParseDate(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value : null;

    private static void ReadApp(SwContainer c, ModelMetadata metadata)
    {
        if (ParseXml(c.Read("docProps/app.xml"))?.Root is not { } root)
        {
            return;
        }

        string? Get(string local) => root.Elements().FirstOrDefault(e => e.Name.LocalName == local)?.Value;
        metadata.Company = Get("Company");
        var version = Get("AppVersion");
        var application = Get("Application") ?? "SOLIDWORKS";
        if (version is not null && double.TryParse(version, NumberStyles.Float, CultureInfo.InvariantCulture, out var major) && major >= 12)
        {
            // Release n of SolidWorks is year 1992 + n (23 is SolidWorks 2015).
            metadata.Application = string.Create(CultureInfo.InvariantCulture, $"{application} {1992 + (int)major} ({version})");
            metadata.FormatVersion = version;
        }
        else
        {
            metadata.Application = application;
        }
    }

    private static void ReadPropertySet(SwContainer c, string stream, string group, ModelMetadata metadata, bool skipNamesAlreadyPresent = false)
    {
        if (ParseXml(c.Read(stream))?.Root is not { } root)
        {
            return;
        }

        foreach (var property in root.Descendants().Where(e => e.Name.LocalName == "property"))
        {
            var name = (string?)property.Attribute("name");
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var value = property.Elements().FirstOrDefault()?.Value ?? property.Value;
            if (skipNamesAlreadyPresent && metadata.Properties.Any(p => p.Name == name))
            {
                continue;
            }

            metadata.Properties.Add(new ModelProperty(name, value, group));
        }

        if (group == "document" && metadata.Author is null)
        {
            metadata.Author = metadata.GetProperty("SW-Author") is { Length: > 0 } a ? a : null;
        }
    }

    private static void ReadKeywords(SwContainer c, ModelMetadata metadata)
    {
        if (ParseXml(c.Read("swXmlContents/KeyWords"))?.Root is not { } root)
        {
            return;
        }

        metadata.Title ??= (string?)root.Attribute("Name");
        foreach (var element in root.Elements())
        {
            var name = (string?)element.Attribute("Name");
            if (element.Name.LocalName == "Configuration")
            {
                if (!string.IsNullOrEmpty(name))
                {
                    metadata.Configurations.Add(name);
                }

                if ((string?)element.Attribute("Material") is { Length: > 0 } material
                    && !material.Contains("not specified", StringComparison.OrdinalIgnoreCase)
                    && !metadata.MaterialNames.Contains(material))
                {
                    metadata.MaterialNames.Add(material);
                }

                continue;
            }

            if (!string.IsNullOrEmpty(name))
            {
                metadata.Features.Add(new ModelFeature(name, (string?)element.Attribute("Type") ?? element.Name.LocalName));
            }
        }
    }

    /// <summary>Reads the referenced-file table; returns the document's own kind ("part", "assembly", "drawing") when it states one.</summary>
    private static string? ReadComponentTree(SwContainer c, ModelMetadata metadata)
    {
        if (ParseXml(c.Read("swXmlContents/COMPINSTANCETREE"))?.Root is not { } root)
        {
            return null;
        }

        var files = root.Descendants().Where(e => e.Name.LocalName == "swFile").ToList();
        if (files.Count == 0)
        {
            return null;
        }

        string? Kind(XElement f) => ((string?)f.Attribute("swDocType"))?.ToLowerInvariant() switch
        {
            "assembly" => "assembly",
            "part" => "part",
            "drawing" => "drawing",
            _ => null,
        };

        var self = files[0];
        var byId = files.Where(f => f.Attribute("id") is not null).ToDictionary(f => (string)f.Attribute("id")!, f => f, StringComparer.Ordinal);
        foreach (var file in files.Skip(1))
        {
            var path = (string?)file.Attribute("swPath");
            var name = path is null ? null : path[(Math.Max(path.LastIndexOf('\\'), path.LastIndexOf('/')) + 1)..];
            if (!string.IsNullOrEmpty(name) && !metadata.ComponentFiles.Contains(name))
            {
                metadata.ComponentFiles.Add(name);
            }
        }

        foreach (var model in root.Descendants().Where(e => e.Name.LocalName == "swModel"))
        {
            var name = (string?)model.Attribute("swName");
            var reference = (string?)model.Attribute("swFileRef");
            if (string.IsNullOrEmpty(name) || reference is null || !byId.TryGetValue(reference, out var file))
            {
                continue;
            }

            if (Kind(file) == "part" && !metadata.PartNames.Contains(name))
            {
                metadata.PartNames.Add(name);
            }
        }

        return Kind(self);
    }

    private static void ReadSheets(SwContainer c, ModelMetadata metadata)
    {
        if (c.Read("SheetPreviews/SheetNames") is not { Length: > 4 } bytes)
        {
            return;
        }

        // Sheet names are serialised strings: FF FE FF, a length byte, then that many UTF-16 characters.
        for (var i = 0; i + 4 < bytes.Length; i++)
        {
            if (bytes[i] != 0xFF || bytes[i + 1] != 0xFE || bytes[i + 2] != 0xFF)
            {
                continue;
            }

            int length = bytes[i + 3];
            if (length > 0 && length < 0xFF && i + 4 + 2 * length <= bytes.Length)
            {
                var name = Encoding.Unicode.GetString(bytes, i + 4, 2 * length);
                if (!name.Any(char.IsControl))
                {
                    metadata.Sheets.Add(name);
                }

                i += 3 + 2 * length;
            }
        }
    }
}
