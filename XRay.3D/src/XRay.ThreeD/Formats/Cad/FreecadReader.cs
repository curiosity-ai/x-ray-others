using System.Globalization;
using System.Numerics;
using System.Xml;
using System.Xml.Linq;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.Cad;

/// <summary>
/// FreeCAD documents (.FCStd, a ZIP with Document.xml): document properties, every object with its label, type and properties, the
/// tree made by Group, Links and the origin, placements, colours and the preview thumbnail. B-rep shapes (*.brp) are listed, not
/// tessellated. Meshes embedded as STL, OBJ, PLY, OFF or 3MF files (a Mesh property naming such an entry) are loaded under their object.
/// </summary>
internal static class FreecadReader
{
    private static readonly string[] ChildLinks = ["Group", "Links", "OriginFeatures", "Shapes", "Objects"];
    private static readonly string[] EmbeddedMeshFormats = ["stl", "obj", "ply", "off", "3mf"];

    private sealed class FcObject
    {
        public string Name = "";
        public string Type = "";
        public string Label = "";
        public readonly Dictionary<string, string> Values = new(StringComparer.Ordinal);
        public readonly List<string> Children = new();
        public readonly List<string> Files = new();
        public Matrix4x4 Placement = Matrix4x4.Identity;
        public bool HasShape;
        public string? Color;
    }

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        using var zip = ZipEntries.Open(data);
        var entry = ZipEntries.Find(zip, "Document.xml") ?? throw new ModelFormatException("The FreeCAD file has no Document.xml.");
        var limit = Math.Min(options.MaxInputBytes, 1L << 28);
        var document = Load(ZipEntries.Read(entry, limit));
        var root = document.Root;
        if (root is null || root.Name.LocalName != "Document")
        {
            throw new ModelFormatException("Not a FreeCAD document.");
        }

        var model = new Model3D();
        var meta = model.Metadata;
        meta.Format = ModelFormats.FreeCad;
        meta.DocumentKind = "part";
        meta.Units = LengthUnit.Millimeter;
        meta.UpAxis = UpAxis.Z;
        meta.FormatVersion = (string?)root.Attribute("SchemaVersion");
        if ((string?)root.Attribute("ProgramVersion") is { Length: > 0 } version)
        {
            meta.Application = "FreeCAD " + version;
        }

        foreach (var p in root.Element("Properties")?.Elements("Property") ?? [])
        {
            var name = (string?)p.Attribute("name") ?? "";
            var value = ValueOf(p);
            if (value is not { Length: > 0 })
            {
                continue;
            }

            switch (name)
            {
                case "Label":
                    meta.Title = value;
                    break;
                case "CreatedBy":
                    meta.Author = value;
                    break;
                case "Company":
                    meta.Company = value;
                    break;
                case "CreationDate":
                    meta.Created = Date(value);
                    break;
                case "LastModifiedDate":
                    meta.Modified = Date(value);
                    break;
                case "LastModifiedBy":
                    meta.LastModifiedBy = value;
                    break;
                default:
                    if (name is not ("Id" or "ExpressionEngine" or "FileName"))
                    {
                        meta.Properties.Add(new ModelProperty(name, value, "document"));
                    }

                    break;
            }
        }

        var objects = new Dictionary<string, FcObject>(StringComparer.Ordinal);
        var order = new List<FcObject>();
        foreach (var o in root.Element("Objects")?.Elements("Object") ?? [])
        {
            var name = (string?)o.Attribute("name") ?? (string?)o.Attribute("Name") ?? "";
            if (name.Length == 0 || objects.ContainsKey(name))
            {
                continue;
            }

            var fc = new FcObject { Name = name, Type = (string?)o.Attribute("type") ?? "" };
            objects[name] = fc;
            order.Add(fc);
        }

        foreach (var o in root.Element("ObjectData")?.Elements("Object") ?? [])
        {
            var name = (string?)o.Attribute("name") ?? "";
            if (!objects.TryGetValue(name, out var fc))
            {
                fc = new FcObject { Name = name, Type = "" };
                if (name.Length == 0 || objects.ContainsKey(name))
                {
                    continue;
                }

                objects[name] = fc;
                order.Add(fc);
            }

            ReadObject(o, fc);
        }

        var colours = ReadColours(zip, limit);
        foreach (var fc in order)
        {
            if (colours.TryGetValue(fc.Name, out var colour))
            {
                fc.Color = colour;
            }
        }

        BuildTree(model, order, objects, zip, options, limit);
        if (ZipEntries.Find(zip, "thumbnails/Thumbnail.png") is { } thumbnail && thumbnail.Length < 16 << 20)
        {
            model.Preview = new PreviewImage("image/png", ZipEntries.Read(thumbnail, 16 << 20));
            meta.HasPreview = true;
        }

        var brep = order.Count(o => o.Files.Any(f => f.EndsWith(".brp", StringComparison.OrdinalIgnoreCase)));
        if (brep > 0)
        {
            model.Warnings.Add($"{brep} object(s) store B-rep shapes (.brp), which are listed but not tessellated.");
        }

        meta.HasGeometry = model.Meshes.Count > 0;
        if (model.Meshes.Count == 0 && brep > 0)
        {
            meta.Notes.Add("The geometry is stored as B-rep shapes; there is no mesh to draw.");
        }

        foreach (var fc in order.Where(o => !IsHelper(o.Type)))
        {
            meta.Features.Add(new ModelFeature(fc.Label.Length > 0 ? fc.Label : fc.Name, fc.Type));
            if (fc.HasShape && !meta.PartNames.Contains(fc.Label.Length > 0 ? fc.Label : fc.Name))
            {
                meta.PartNames.Add(fc.Label.Length > 0 ? fc.Label : fc.Name);
            }
        }

        if (model.Meshes.Count > 0)
        {
            meta.Statistics = model.ComputeStatistics();
            meta.Bounds = model.ComputeBounds();
        }

        return model;
    }

    private static bool IsHelper(string type) => type is "App::Origin" or "App::Line" or "App::Plane";

    private static XDocument Load(byte[] xml)
    {
        using var stream = new MemoryStream(xml, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader);
    }

    private static DateTimeOffset? Date(string value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date : null;

    /// <summary>The value of a property when it is one scalar or a list of links; null for structured values.</summary>
    private static string? ValueOf(XElement property)
    {
        var first = property.Elements().FirstOrDefault();
        if (first is null)
        {
            return null;
        }

        if (first.Name.LocalName is "LinkList" or "LinkSubList")
        {
            return string.Join(", ", first.Elements().Select(l => (string?)l.Attribute("value")).Where(v => !string.IsNullOrEmpty(v)));
        }

        if (first.Name.LocalName == "PropertyVector")
        {
            return $"{(string?)first.Attribute("valueX")}, {(string?)first.Attribute("valueY")}, {(string?)first.Attribute("valueZ")}";
        }

        var value = (string?)first.Attribute("value");
        return value is null ? null : value.Length > 4096 ? value[..4096] : value;
    }

    private static void ReadObject(XElement element, FcObject fc)
    {
        foreach (var p in element.Element("Properties")?.Elements("Property") ?? [])
        {
            var name = (string?)p.Attribute("name") ?? "";
            var type = (string?)p.Attribute("type") ?? "";
            var content = p.Elements().FirstOrDefault();
            if (content is null || name is "ExpressionEngine")
            {
                continue;
            }

            if (name == "Placement" && content.Name.LocalName == "PropertyPlacement")
            {
                fc.Placement = PlacementOf(content);
                continue;
            }

            if (content.Name.LocalName is "Part" or "Mesh" && (string?)content.Attribute("file") is { Length: > 0 } file)
            {
                fc.Files.Add(file);
                if (content.Name.LocalName == "Part")
                {
                    fc.HasShape = true;
                }

                continue;
            }

            if (type.EndsWith("LinkList", StringComparison.Ordinal) && ChildLinks.Contains(name, StringComparer.Ordinal))
            {
                foreach (var link in content.Elements().Select(l => (string?)l.Attribute("value")).Where(v => !string.IsNullOrEmpty(v)))
                {
                    fc.Children.Add(link!);
                }
            }
            else if (name == "Origin" && type.EndsWith("PropertyLink", StringComparison.Ordinal) && (string?)content.Attribute("value") is { Length: > 0 } origin)
            {
                fc.Children.Add(origin);
            }

            var value = ValueOf(p);
            if (value is null)
            {
                continue;
            }

            if (name == "Label")
            {
                fc.Label = value;
            }
            else if (value.Length > 0 && !(name is "Label2" or "Group" or "Links" or "OriginFeatures" or "Origin"))
            {
                fc.Values[name] = value;
            }
        }

    }

    private static Matrix4x4 PlacementOf(XElement e)
    {
        double D(string name) => double.TryParse((string?)e.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : 0;
        var q = new Quaternion((float)D("Q0"), (float)D("Q1"), (float)D("Q2"), (float)D("Q3"));
        var length = q.Length();
        q = length > 1e-6f ? Quaternion.Normalize(q) : Quaternion.Identity;
        var m = Matrix4x4.CreateFromQuaternion(q);
        m.Translation = new Vector3((float)D("Px"), (float)D("Py"), (float)D("Pz"));
        return m;
    }

    /// <summary>The first colour of each object's DiffuseColor file (a count, then packed r,g,b,a values), from GuiDocument.xml.</summary>
    private static Dictionary<string, string> ReadColours(System.IO.Compression.ZipArchive zip, long limit)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (ZipEntries.Find(zip, "GuiDocument.xml") is not { } gui)
            {
                return result;
            }

            var doc = Load(ZipEntries.Read(gui, limit));
            foreach (var provider in doc.Root?.Element("ViewProviderData")?.Elements("ViewProvider") ?? [])
            {
                var name = (string?)provider.Attribute("name") ?? "";
                foreach (var p in provider.Element("Properties")?.Elements("Property") ?? [])
                {
                    var content = p.Elements().FirstOrDefault();
                    if ((string?)p.Attribute("name") == "DiffuseColor" && content is { } && (string?)content.Attribute("file") is { Length: > 0 } file
                        && ZipEntries.Find(zip, file) is { } entry && entry.Length is >= 8 and < 1 << 24)
                    {
                        var bytes = ZipEntries.Read(entry, 1 << 24);
                        var count = BitConverter.ToUInt32(bytes, 0);
                        if (count > 0)
                        {
                            var packed = BitConverter.ToUInt32(bytes, 4);
                            result[name] = $"#{(packed >> 24) & 255:x2}{(packed >> 16) & 255:x2}{(packed >> 8) & 255:x2}";
                        }
                    }
                    else if ((string?)p.Attribute("name") == "ShapeColor" && content is { } && uint.TryParse((string?)content.Attribute("value"), NumberStyles.None, CultureInfo.InvariantCulture, out var shape) && !result.ContainsKey(name))
                    {
                        result[name] = $"#{(shape >> 24) & 255:x2}{(shape >> 16) & 255:x2}{(shape >> 8) & 255:x2}";
                    }
                }
            }
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or ArgumentException)
        {
            // colours are optional
        }

        return result;
    }

    private static void BuildTree(Model3D model, List<FcObject> order, Dictionary<string, FcObject> objects, System.IO.Compression.ZipArchive zip, LoadOptions options, long limit)
    {
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var kids = new Dictionary<string, List<FcObject>>(StringComparer.Ordinal);
        foreach (var fc in order)
        {
            var list = new List<FcObject>();
            foreach (var child in fc.Children.Distinct(StringComparer.Ordinal))
            {
                if (child != fc.Name && objects.TryGetValue(child, out var c) && claimed.Add(child))
                {
                    list.Add(c);
                }
            }

            kids[fc.Name] = list;
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<(FcObject Object, SceneNode? Parent)>();
        void PushRoots()
        {
            foreach (var fc in Enumerable.Reverse(order.Where(o => !claimed.Contains(o.Name))))
            {
                stack.Push((fc, null));
            }
        }

        PushRoots();
        while (true)
        {
            if (stack.Count == 0)
            {
                // Objects only reachable through a cycle: start from them so none is lost.
                var missing = order.FirstOrDefault(o => !visited.Contains(o.Name));
                if (missing is null)
                {
                    break;
                }

                stack.Push((missing, null));
                continue;
            }

            var (fc, parent) = stack.Pop();
            if (!visited.Add(fc.Name))
            {
                continue;
            }

            options.Cancellation.ThrowIfCancellationRequested();
            var node = new SceneNode(fc.Label.Length > 0 ? fc.Label : fc.Name) { Transform = fc.Placement };
            node.Properties["Name"] = fc.Name;
            node.Properties["Type"] = fc.Type;
            foreach (var (key, value) in fc.Values.Take(500))
            {
                node.Properties[key] = value;
            }

            if (fc.Color is not null)
            {
                node.Properties["Color"] = fc.Color;
            }

            foreach (var file in fc.Files)
            {
                if (file.EndsWith(".brp", StringComparison.OrdinalIgnoreCase))
                {
                    node.Properties["Geometry"] = "B-rep shape (" + file + "), not tessellated";
                }
                else if (ZipEntries.Find(zip, file) is { } entry)
                {
                    AttachMesh(model, node, entry, options, limit);
                }
            }

            if (parent is null)
            {
                model.Roots.Add(node);
            }
            else
            {
                parent.Children.Add(node);
            }

            var children = kids[fc.Name];
            for (var i = children.Count - 1; i >= 0; i--)
            {
                stack.Push((children[i], node));
            }
        }
    }

    private static void AttachMesh(Model3D model, SceneNode node, System.IO.Compression.ZipArchiveEntry entry, LoadOptions options, long limit)
    {
        try
        {
            var bytes = ZipEntries.Read(entry, limit);
            var format = ModelFormats.Detect(bytes, entry.Name, options.Cancellation);
            if (!EmbeddedMeshFormats.Contains(format.Id))
            {
                node.Properties["Geometry"] = $"{entry.FullName} is not a mesh format this reader knows";
                return;
            }

            var inner = ModelLoader.Load(bytes, entry.Name, options);
            ModelMerge.Append(model, node, inner, entry.Name + ": ");
        }
        catch (ModelFormatException ex)
        {
            model.Warnings.Add($"{entry.FullName}: {ex.Message}");
        }
    }
}
