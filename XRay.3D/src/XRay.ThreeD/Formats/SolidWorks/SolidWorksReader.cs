using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using XRay.ThreeD.Internal;

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
        SwContainer.TryOpen(data, cancellation, verify: false) is { } c && (c.Read("docProps/app.xml") is not null || c.Contains(DisplayLists) || c.Read("Header2") is not null);

    public static ModelMetadata ReadMetadata(ReadOnlyMemory<byte> data, CancellationToken cancellation)
    {
        // Only the entry headers are read and the small property streams inflated; the display lists (most of the file) are never touched,
        // so the geometry is reported as present when the stream is, without counts (load the model for those).
        var container = Open(data, cancellation, verify: false);
        var metadata = ReadMetadata(container, readDisplayLists: false, out _, cancellation);
        metadata.HasGeometry = container.Contains(DisplayLists) && metadata.DocumentKind != "drawing";
        FinishNotes(metadata);
        return metadata;
    }

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options) => Read(data, options, 0);

    private static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options, int depth)
    {
        var cancellation = options.Cancellation;
        var container = Open(data, cancellation);
        var model = new Model3D();
        var metadata = ReadMetadata(container, readDisplayLists: true, out var stream, cancellation);
        CopyInto(model.Metadata, metadata);
        if (container.Read("PreviewPNG") is { Length: > 8 } png && png[0] == 0x89 && png[1] == (byte)'P')
        {
            model.Preview = new PreviewImage("image/png", png);
            model.Metadata.HasPreview = true;
        }

        else if (container.Read("Preview") is { Length: > 44 } dib && DibImage.DecodeRgb(dib, out var pw, out var ph, cancellation) is { } pixels)
        {
            var trimmed = DibImage.Trim(pixels, ref pw, ref ph);
            model.Preview = new PreviewImage("image/png", DibImage.EncodePng(trimmed, pw, ph));
            model.Metadata.HasPreview = true;
        }

        if (stream is not null)
        {
            var decoded = SwDisplayLists.Decode(stream, metadata.DocumentKind == "assembly", cancellation);
            BuildScene(model, stream, decoded, metadata);
            if (decoded.Unplaced.Count > 0 && options.ResolveCompanion is not null)
            {
                MergeExternalComponents(model, decoded.Unplaced, metadata, options, depth);
            }

            model.Metadata.HasGeometry = model.Meshes.Count > 0;
            model.Metadata.Statistics = model.ComputeStatistics();
            model.Metadata.Bounds = model.ComputeBounds();
        }

        model.Is2D = model.Metadata.DocumentKind == "drawing";
        if (model.Is2D)
        {
            BuildDrawing(model, container, stream, cancellation);
        }

        FinishNotes(model.Metadata);
        model.Warnings.AddRange(model.Metadata.Notes);
        return model;
    }

    private static SwContainer Open(ReadOnlyMemory<byte> data, CancellationToken cancellation, bool verify = true) =>
        SwContainer.TryOpen(data, cancellation, verify)
        ?? throw new UnsupportedModelFormatException(
            ModelFormats.SolidWorks,
            "Not a SolidWorks 2015+ document. Older SolidWorks files (compound-file envelope) are not supported yet.");

    private static void FinishNotes(ModelMetadata metadata)
    {
        if (metadata.DocumentKind == "drawing")
        {
            metadata.Notes.Add(metadata.DrawingSheets.Count > 0 || metadata.HasPreview
                ? "A drawing holds sheets, not 3D geometry; each sheet is drawn as the raster image the file stores, its vector views and dimensions are not decoded."
                : "A drawing holds sheets, not 3D geometry.");
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

        var appearanceMaterial = new int[decoded.Appearances.Count];
        Array.Fill(appearanceMaterial, -1);
        var byKey = new Dictionary<(string, int), int>();
        int MaterialOf(int appearance)
        {
            if (appearance < 0)
            {
                return -1;
            }

            if (appearanceMaterial[appearance] < 0)
            {
                var a = decoded.Appearances[appearance];
                if (!byKey.TryGetValue((a.Name, a.Rgb), out var index))
                {
                    index = model.Materials.Count;
                    byKey[(a.Name, a.Rgb)] = index;
                    model.Materials.Add(ToMaterial(a));
                }

                appearanceMaterial[appearance] = index;
            }

            return appearanceMaterial[appearance];
        }

        var meshes = new MeshData[decoded.Geometries.Count];
        for (var i = 0; i < meshes.Length; i++)
        {
            var firstInstance = decoded.Instances.FirstOrDefault(x => x.Geometry == i);
            meshes[i] = SwDisplayLists.BuildMesh(stream, decoded.Geometries[i], PartName(firstInstance?.Name) ?? metadata.Title, MaterialOf);
            model.Meshes.Add(meshes[i]);
        }

        var named = decoded.Instances.Any(i => i.Name is not null);
        foreach (var materialName in model.Materials.Select(m => m.Name!).Where(n => !string.IsNullOrEmpty(n)))
        {
            if (!model.Metadata.MaterialNames.Contains(materialName))
            {
                model.Metadata.MaterialNames.Add(materialName);
            }
        }

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

    // ---- drawings ----------------------------------------------------------------------------------------------------

    /// <summary>
    /// A drawing's content is the raster of each sheet SolidWorks saved (<c>Images/Sheet_n</c>), placed in the plane at the paper size the file
    /// states (the sheet record holds the two sides as doubles before the sheet format's name; a sheet is taken as landscape when its image is),
    /// side by side with a gap. The first sheet uses the higher resolution <c>Preview</c> bitmap, with its letterbox bars trimmed, when it shows
    /// the same page. Each sheet is a layer.
    /// </summary>
    private static void BuildDrawing(Model3D model, SwContainer container, byte[]? displayLists, CancellationToken cancellation)
    {
        var meta = model.Metadata;
        var sizes = SheetSizes(container.Read("Contents/Definition")) is { Count: > 0 } fromDefinition ? fromDefinition : SheetSizes(displayLists);
        var x = 0.0;
        for (var index = 0; index < 64; index++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (container.Read($"Images/Sheet_{index}") is not { Length: > 24 } png || png[0] != 0x89 || png[1] != (byte)'P')
            {
                break;
            }

            var pixelWidth = (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19];
            var pixelHeight = (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23];
            if (pixelWidth <= 0 || pixelHeight <= 0)
            {
                break;
            }

            var name = index < meta.Sheets.Count ? meta.Sheets[index] : $"Sheet{index + 1}";
            var (sideA, sideB) = sizes.Count == 0 ? (0.0, 0.0) : sizes[Math.Min(index, sizes.Count - 1)];
            double width, height;
            if (sideA > 0)
            {
                (width, height) = pixelWidth >= pixelHeight ? (Math.Max(sideA, sideB), Math.Min(sideA, sideB)) : (Math.Min(sideA, sideB), Math.Max(sideA, sideB));
                meta.DrawingSheets.Add(new DrawingSheet(name, width, height));
                meta.Properties.Add(new ModelProperty($"sheet size: {name}", string.Create(CultureInfo.InvariantCulture, $"{width * 1000:0.#} x {height * 1000:0.#} mm"), "drawing"));
            }
            else
            {
                // No size stated: the image's shape on an A3-ish page.
                (width, height) = pixelWidth >= pixelHeight ? (0.42, 0.42 * pixelHeight / pixelWidth) : (0.42 * pixelWidth / pixelHeight, 0.42);
            }

            var data = png;
            if (index == 0 && container.Read("Preview") is { Length: > 44 } dib && DibImage.DecodeRgb(dib, out var w, out var h, cancellation) is { } rgb)
            {
                var trimmed = DibImage.Trim(rgb, ref w, ref h);
                if (Math.Abs((double)w / h / ((double)pixelWidth / pixelHeight) - 1) < 0.1 && w > pixelWidth)
                {
                    data = DibImage.EncodePng(trimmed, w, h);
                }
            }

            var layer = model.Layers.Count;
            model.Layers.Add(new ModelLayer(name));
            if (!meta.LayerNames.Contains(name))
            {
                meta.LayerNames.Add(name);
            }

            model.Images.Add(new ModelImage { Name = name, Data = data, Position = new Vector3((float)x, 0, 0), Width = (float)width, Height = (float)height, Layer = layer });
            x += width * 1.05;
        }

        if (model.Images.Count == 0 && model.Preview is { } preview)
        {
            model.Images.Add(new ModelImage { Name = "preview", MediaType = preview.MediaType, Data = preview.Data, Width = 0.42f, Height = 0.297f });
        }
    }

    /// <summary>The two sides of every sheet record: two doubles in a plausible paper range, directly before a serialised string.</summary>
    private static List<(double A, double B)> SheetSizes(byte[]? stream)
    {
        var result = new List<(double, double)>();
        if (stream is null)
        {
            return result;
        }

        var span = stream.AsSpan();
        for (var o = 0; o + 19 < span.Length; o++)
        {
            if (span[o + 16] != 0xFF || span[o + 17] != 0xFE || span[o + 18] != 0xFF)
            {
                continue;
            }

            var a = BitConverter.ToDouble(span.Slice(o, 8));
            var b = BitConverter.ToDouble(span.Slice(o + 8, 8));
            if (double.IsFinite(a) && double.IsFinite(b) && a is > 0.05 and < 5 && b is > 0.05 and < 5 && Math.Min(a, b) / Math.Max(a, b) is > 0.45 and < 1.0)
            {
                result.Add((a, b));
                if (result.Count >= 64)
                {
                    break;
                }
            }
        }

        return result;
    }

    // ---- external components -----------------------------------------------------------------------------------------

    /// <summary>
    /// Instances the display lists place but hold no geometry for take their geometry from the component file, found through
    /// <see cref="LoadOptions.ResolveCompanion"/> by the file name the assembly lists. Each distinct file is loaded once (assemblies nest, to three
    /// levels) and shared by every instance under that instance's placement.
    /// </summary>
    private static void MergeExternalComponents(Model3D model, List<SwDisplayLists.Instance> unplaced, ModelMetadata metadata, LoadOptions options, int depth)
    {
        var loaded = new Dictionary<string, Model3D?>(StringComparer.OrdinalIgnoreCase);
        var root = model.Roots.FirstOrDefault();
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tick = 0;
        foreach (var instance in unplaced)
        {
            Internal.Cancel.Every(options.Cancellation, tick++, 63);
            var part = PartName(instance.Name);
            var file = part is null ? null : metadata.ComponentFiles.FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), part, StringComparison.OrdinalIgnoreCase));
            if (file is null)
            {
                continue;
            }

            if (!loaded.TryGetValue(file, out var component))
            {
                component = null;
                if (depth < 3 && options.ResolveCompanion!(file) is { Length: > 0 } bytes)
                {
                    component = Read(bytes, options, depth + 1);
                    ModelMerge.AdoptResources(model, component);
                }
                else if (depth < 3)
                {
                    missing.Add(file);
                }

                loaded[file] = component;
            }

            if (component is null || component.Roots.Count == 0)
            {
                continue;
            }

            if (root is null)
            {
                root = new SceneNode(metadata.Title ?? "Assembly");
                root.Properties["kind"] = "assembly";
                model.Roots.Add(root);
            }

            var node = new SceneNode(instance.Name is null ? null : InstanceLabel(instance.Name)) { Transform = instance.Transform };
            if (instance.Name is not null)
            {
                node.Properties["instancePath"] = instance.Name;
                node.Properties["file"] = file;
            }

            foreach (var componentRoot in component.Roots)
            {
                node.Children.Add(ModelMerge.CloneTree(componentRoot));
            }

            root.Children.Add(node);
        }

        foreach (var file in missing.Take(20))
        {
            model.Warnings.Add($"Component file {file} was not supplied; its instances are not drawn.");
        }

        if (model.Meshes.Count > 0)
        {
            model.Metadata.PartNames.Clear();
            model.Metadata.PartNames.AddRange(metadata.PartNames);
            model.Metadata.MaterialNames.AddRange(model.Materials.Select(m => m.Name!).Where(n => !string.IsNullOrEmpty(n) && !model.Metadata.MaterialNames.Contains(n)));
        }
    }

    private static readonly string[] MetalWords = ["metal", "steel", "aluminum", "aluminium", "chrome", "brass", "copper", "iron", "titanium", "nickel", "zinc"];

    /// <summary>
    /// Appearance to PBR. SolidWorks stores a colour and a texture family, not PBR factors, so metallic and roughness are
    /// a heuristic from the family and name: metals are 0.9 metallic, 0.35 rough; glass and clear plastic are translucent.
    /// </summary>
    private static ModelMaterial ToMaterial(SwDisplayLists.Appearance a)
    {
        var text = (a.Category + " " + a.Name).ToLowerInvariant();
        var material = new ModelMaterial
        {
            Name = a.Name,
            BaseColor = new Vector4(((a.Rgb >> 16) & 255) / 255f, ((a.Rgb >> 8) & 255) / 255f, (a.Rgb & 255) / 255f, 1f),
        };
        if (MetalWords.Any(text.Contains) && !text.Contains("plastic", StringComparison.Ordinal))
        {
            material.Metallic = 0.9f;
            material.Roughness = text.Contains("polish", StringComparison.Ordinal) || text.Contains("chrome", StringComparison.Ordinal) ? 0.15f : 0.35f;
        }
        else if (text.Contains("glass", StringComparison.Ordinal) || text.Contains("transparent", StringComparison.Ordinal))
        {
            material.BaseColor = new Vector4(material.BaseColor.X, material.BaseColor.Y, material.BaseColor.Z, 0.35f);
            material.Roughness = 0.05f;
        }

        return material;
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

    private static ModelMetadata ReadMetadata(SwContainer c, bool readDisplayLists, out byte[]? displayLists, CancellationToken cancellation)
    {
        var metadata = new ModelMetadata
        {
            Format = ModelFormats.SolidWorks,
            Units = LengthUnit.Meter,
            UpAxis = UpAxis.Y,
        };
        displayLists = readDisplayLists ? c.Read(DisplayLists) : null;

        ReadCoreProperties(c, metadata);
        ReadApp(c, metadata);
        ReadCompoundProperties(c, metadata, cancellation);
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

    /// <summary>
    /// SolidWorks 2014 and earlier keep what the XML property streams hold in OLE property sets: summary information (title, author, dates),
    /// document summary (company), <c>ISolidWorksInformation</c> (the SW-* document properties, named in the set's dictionary as
    /// <c>SW-Author(Author)</c>) and <c>Config-0-Properties</c> (custom properties). The model-version marker (<c>_MO_VERSION_7000</c>) is the format version.
    /// </summary>
    private static void ReadCompoundProperties(SwContainer c, ModelMetadata metadata, CancellationToken cancellation)
    {
        if (c.Compound is not { } file)
        {
            return;
        }

        List<OlePropertySet> Sets(string stream) => file.Read(stream, cancellation, 1 << 22) is { } bytes ? OlePropertySet.ParseAll(bytes, cancellation) : [];

        if (Sets("\u0005SummaryInformation").FirstOrDefault() is { } summary)
        {
            metadata.Title ??= NonEmpty(summary.Text(2));
            metadata.Author ??= NonEmpty(summary.Text(4));
            metadata.LastModifiedBy ??= NonEmpty(summary.Text(8));
            metadata.Created ??= summary.Time(12);
            metadata.Modified ??= summary.Time(13);
        }

        if (Sets("\u0005DocumentSummaryInformation").FirstOrDefault() is { } document)
        {
            metadata.Company ??= NonEmpty(document.Text(15));
        }

        foreach (var (stream, group) in new[] { ("ISolidWorksInformation", "document"), ("Config-0-Properties", "custom") })
        {
            foreach (var set in Sets(stream))
            {
                foreach (var (id, value) in set.Values.OrderBy(v => v.Key))
                {
                    if (!set.Names.TryGetValue(id, out var name) || value is not (string or int or double or uint or short))
                    {
                        continue;
                    }

                    var open = name.IndexOf('(');
                    var shown = open > 0 && name.EndsWith(')') ? name[..open] : name;
                    var text = Convert.ToString(value, CultureInfo.InvariantCulture);
                    if (!string.IsNullOrWhiteSpace(text) && !metadata.Properties.Any(p => p.Name == shown))
                    {
                        metadata.Properties.Add(new ModelProperty(shown, text.Trim(), group));
                    }
                }
            }
        }

        if (metadata.Author is null && metadata.GetProperty("SW-Author") is { Length: > 0 } author)
        {
            metadata.Author = author;
        }

        if (c.CompoundVersion() is { } version)
        {
            metadata.FormatVersion ??= version;
            metadata.Application ??= "SolidWorks (2014 or earlier)";
        }
    }

    private static string? NonEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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
        target.DrawingSheets.AddRange(source.DrawingSheets);
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
