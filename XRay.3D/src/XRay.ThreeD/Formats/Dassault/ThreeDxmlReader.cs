using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Xml;
using System.Xml.Linq;

namespace XRay.ThreeD.Formats.Dassault;

using XRay.ThreeD.Internal;

/// <summary>
/// 3DXML (<c>.3dxml</c>), the neutral exchange format of the CATIA family. Two encodings exist: a ZIP package (<c>Manifest.xml</c> naming the root
/// document, the product-structure document, one <c>.3DRep</c> XML document per representation, images) and a single XML document holding the product
/// structure alone (representations then come through <see cref="LoadOptions.ResolveCompanion"/>).
/// The product structure is <c>Reference3D</c> (a part or assembly), <c>Instance3D</c> (a placement of a reference inside another, with a
/// <c>RelativeMatrix</c> of twelve numbers: three rows of the 3x3 part, then the translation, so row vectors as in System.Numerics),
/// <c>ReferenceRep</c> (a representation file, <c>associatedFile="urn:3DXML:Name.3DRep"</c>) and <c>InstanceRep</c> (a reference using a representation).
/// A tessellated representation is <c>PolygonalRepType</c>: a <c>VertexBuffer</c> (<c>Positions</c>, <c>Normals</c>, as "x y z,x y z") and
/// <c>Faces/Face</c> with index lists <c>triangles</c>, <c>strips</c> and <c>fans</c> and an RGBA colour in <c>SurfaceAttributes</c>; the
/// <c>PolygonalLOD</c> blocks are coarser copies of the same faces and are skipped. Lengths are millimetres. Shared references share their mesh.
/// Not read: the binary CGR representation, exact (B-rep) representations, material domains, edges and textures.
/// </summary>
internal static class ThreeDxmlReader
{
    private const long MaxDocumentBytes = 1L << 29;
    private const int MaxDepth = 64;
    private const int MaxNodes = 2_000_000;

    public static bool SniffXml(ReadOnlySpan<byte> head) =>
        head.Length > 0 && head[0] is (byte)'<' or 0xEF && head.IndexOf("<Model_3dxml"u8) >= 0;

    public static bool IsPackage(IEnumerable<string> names)
    {
        var any3DRep = false;
        var manifest = false;
        var doc = false;
        foreach (var n in names)
        {
            manifest |= string.Equals(n, "Manifest.xml", StringComparison.OrdinalIgnoreCase);
            any3DRep |= n.EndsWith(".3DRep", StringComparison.OrdinalIgnoreCase);
            doc |= n.EndsWith(".3dxml", StringComparison.OrdinalIgnoreCase);
        }

        return manifest && (any3DRep || doc);
    }

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var token = options.Cancellation;
        ZipArchive? zip = null;
        try
        {
            Func<string, byte[]?> files;
            string rootName;
            byte[]? rootBytes = null;
            if (data.Length >= 2 && data.Span[0] == 'P' && data.Span[1] == 'K')
            {
                zip = ZipEntries.Open(data);
                var open = zip;
                files = name =>
                {
                    var entry = ZipEntries.Find(open, name) ?? open.Entries.FirstOrDefault(e => string.Equals(Path.GetFileName(e.FullName), name, StringComparison.OrdinalIgnoreCase));
                    return entry is null ? null : ZipEntries.Read(entry, MaxDocumentBytes, token);
                };
                rootName = ManifestRoot(files("Manifest.xml")) ?? zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".3dxml", StringComparison.OrdinalIgnoreCase))?.FullName
                    ?? throw new ModelFormatException("The 3DXML package names no root document.");
            }
            else
            {
                rootName = "";
                rootBytes = data.ToArray();
                var resolve = options.ResolveCompanion;
                files = name => resolve?.Invoke(name);
            }

            var reader = new Builder(files, options);
            return reader.Build(rootName, rootBytes);
        }
        finally
        {
            zip?.Dispose();
        }
    }

    private static string? ManifestRoot(byte[]? manifest)
    {
        if (manifest is null)
        {
            return null;
        }

        try
        {
            return Load(manifest).Descendants().FirstOrDefault(e => e.Name.LocalName == "Root")?.Value.Trim();
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static XDocument Load(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var xml = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxDocumentBytes });
        return XDocument.Load(xml);
    }

    private sealed class Doc
    {
        public string Name = "";
        public XElement? Structure;
        public readonly Dictionary<string, XElement> References = new(StringComparer.Ordinal);
        public readonly Dictionary<string, XElement> ReferenceReps = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<XElement>> Instances = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<XElement>> RepInstances = new(StringComparer.Ordinal);
    }

    private sealed class Builder
    {
        private readonly Func<string, byte[]?> _files;
        private readonly LoadOptions _options;
        private readonly CancellationToken _token;
        private readonly Model3D _model = new();
        private readonly Dictionary<string, Doc> _docs = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, MeshData?> _meshes = new(StringComparer.Ordinal);
        private readonly Dictionary<uint, int> _materials = new();
        private readonly HashSet<string> _active = new(StringComparer.Ordinal);
        private int _nodes;
        private long _triangles;
        private long _tick;

        public Builder(Func<string, byte[]?> files, LoadOptions options)
        {
            _files = files;
            _options = options;
            _token = options.Cancellation;
        }

        public Model3D Build(string rootName, byte[]? rootBytes)
        {
            var meta = _model.Metadata;
            meta.Format = ModelFormats.ThreeDxml;
            meta.Units = LengthUnit.Millimeter;
            meta.UpAxis = UpAxis.Z;

            var root = LoadDoc(rootName, rootBytes) ?? throw new ModelFormatException("The 3DXML root document is missing or is not XML.");
            var xml = root.Structure?.Document?.Root;
            if (xml is not null)
            {
                foreach (var h in xml.Elements().FirstOrDefault(e => e.Name.LocalName == "Header")?.Elements() ?? [])
                {
                    var value = h.Value.Trim();
                    if (value.Length == 0)
                    {
                        continue;
                    }

                    switch (h.Name.LocalName)
                    {
                        case "SchemaVersion": meta.FormatVersion = value; break;
                        case "Title": meta.Title = value; break;
                        case "Author": meta.Author = value; break;
                        case "Generator": meta.Application = value; break;
                        case "Created": meta.Created = DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var c) ? c : null; break;
                    }

                    meta.Properties.Add(new ModelProperty(h.Name.LocalName, value, "header"));
                }
            }

            var rootId = (string?)root.Structure?.Attribute("root");
            if (rootId is null || !root.References.ContainsKey(rootId))
            {
                rootId = root.References.Keys.FirstOrDefault() ?? throw new ModelFormatException("The 3DXML product structure has no Reference3D.");
            }

            var node = ExpandReference(root, rootId, 0);
            _model.Roots.Add(node);
            meta.DocumentKind = root.Instances.TryGetValue(rootId, out var any) && any.Count > 0 ? "assembly" : "part";
            meta.HasGeometry = _model.Meshes.Count > 0;
            if (!meta.HasGeometry)
            {
                _model.Warnings.Add("No tessellated representation could be read (binary CGR and exact representations are not decoded, or the representation files are missing).");
            }

            meta.Statistics = _model.ComputeStatistics();
            meta.Bounds = _model.ComputeBounds();
            return _model;
        }

        private Doc? LoadDoc(string name, byte[]? bytes)
        {
            if (_docs.TryGetValue(name, out var cached))
            {
                return cached;
            }

            bytes ??= _files(name);
            if (bytes is null)
            {
                return null;
            }

            XDocument document;
            try
            {
                document = Load(bytes);
            }
            catch (XmlException)
            {
                return null;
            }

            var doc = new Doc { Name = name };
            doc.Structure = document.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "ProductStructure");
            _docs[name] = doc;
            if (doc.Structure is null)
            {
                return doc;
            }

            foreach (var e in doc.Structure.Elements())
            {
                Cancel.Every(_token, _tick++);
                var id = (string?)e.Attribute("id");
                switch (e.Name.LocalName)
                {
                    case "Reference3D" when id is not null: doc.References[id] = e; break;
                    case "ReferenceRep" when id is not null: doc.ReferenceReps[id] = e; break;
                    case "Instance3D":
                        Add(doc.Instances, Child(e, "IsAggregatedBy"), e);
                        break;
                    case "InstanceRep":
                        Add(doc.RepInstances, Child(e, "IsAggregatedBy"), e);
                        break;
                }
            }

            return doc;
        }

        private static void Add(Dictionary<string, List<XElement>> map, string? key, XElement value)
        {
            if (key is null)
            {
                return;
            }

            if (!map.TryGetValue(key, out var list))
            {
                map[key] = list = new List<XElement>();
            }

            list.Add(value);
        }

        private static string? Child(XElement e, string name) => e.Elements().FirstOrDefault(c => c.Name.LocalName == name)?.Value.Trim();

        /// <summary>Splits "urn:3DXML:Other.3dxml#7" or "7" into the document (the current one when none is named) and the id.</summary>
        private (Doc? Doc, string Id) Resolve(Doc current, string reference)
        {
            var text = reference.Trim();
            if (text.StartsWith("urn:3DXML:", StringComparison.OrdinalIgnoreCase))
            {
                text = text["urn:3DXML:".Length..];
            }

            var hash = text.IndexOf('#');
            if (hash < 0)
            {
                return (current, text);
            }

            var file = text[..hash];
            var id = text[(hash + 1)..];
            return (file.Length == 0 ? current : LoadDoc(file, null), id);
        }

        private SceneNode ExpandReference(Doc doc, string id, int depth)
        {
            Cancel.Every(_token, _tick++, 63);
            if (depth > MaxDepth || ++_nodes > MaxNodes)
            {
                throw new ModelFormatException("The 3DXML product structure is too deep or too large.");
            }

            var key = doc.Name + "#" + id;
            if (!_active.Add(key))
            {
                throw new ModelFormatException("The 3DXML product structure contains a cycle.");
            }

            try
            {
                var reference = doc.References.GetValueOrDefault(id);
                var node = new SceneNode((string?)reference?.Attribute("name") ?? id);
                if (reference is not null)
                {
                    CopyProperties(reference, node);
                    if (depth == 0 || reference.Attribute("name") is not null)
                    {
                        _model.Metadata.PartNames.Add(node.Name!);
                    }
                }

                if (doc.Instances.TryGetValue(id, out var instances))
                {
                    foreach (var instance in instances)
                    {
                        Cancel.Every(_token, _tick++, 63);
                        var target = Child(instance, "IsInstanceOf");
                        if (target is null)
                        {
                            continue;
                        }

                        var (targetDoc, targetId) = Resolve(doc, target);
                        if (targetDoc is null || !targetDoc.References.ContainsKey(targetId))
                        {
                            _model.Warnings.Add("An instance refers to a reference that is not in the package: " + target);
                            continue;
                        }

                        var child = ExpandReference(targetDoc, targetId, depth + 1);
                        child.Transform = ParseMatrix(Child(instance, "RelativeMatrix"));
                        var instanceName = (string?)instance.Attribute("name");
                        if (!string.IsNullOrEmpty(instanceName))
                        {
                            child.Properties["Reference"] = child.Name ?? "";
                            child.Name = instanceName;
                        }

                        CopyProperties(instance, child);
                        node.Children.Add(child);
                    }
                }

                if (doc.RepInstances.TryGetValue(id, out var repInstances))
                {
                    foreach (var repInstance in repInstances)
                    {
                        var target = Child(repInstance, "IsInstanceOf");
                        if (target is null)
                        {
                            continue;
                        }

                        var (repDoc, repId) = Resolve(doc, target);
                        if (repDoc is null || !repDoc.ReferenceReps.TryGetValue(repId, out var rep))
                        {
                            continue;
                        }

                        var mesh = MeshOf(repDoc, repId, rep);
                        if (mesh is null)
                        {
                            continue;
                        }

                        var repNode = new SceneNode((string?)repInstance.Attribute("name") ?? mesh.Name) { Mesh = mesh };
                        if (Child(repInstance, "RelativeMatrix") is { } matrix)
                        {
                            repNode.Transform = ParseMatrix(matrix);
                        }

                        CopyProperties(rep, repNode);
                        node.Children.Add(repNode);
                        _nodes++;
                    }
                }

                return node;
            }
            finally
            {
                _active.Remove(key);
            }
        }

        private static readonly HashSet<string> Structural = new(StringComparer.Ordinal) { "IsAggregatedBy", "IsInstanceOf", "RelativeMatrix" };

        private static void CopyProperties(XElement e, SceneNode node)
        {
            var count = 0;
            foreach (var p in e.Elements())
            {
                if (count >= 64)
                {
                    break;
                }

                if (Structural.Contains(p.Name.LocalName) || p.HasElements)
                {
                    continue;
                }

                var value = p.Value.Trim();
                if (value.Length > 0 && value.Length < 1024)
                {
                    node.Properties[p.Name.LocalName] = value;
                    count++;
                }
            }
        }

        private MeshData? MeshOf(Doc doc, string id, XElement rep)
        {
            var key = doc.Name + "#" + id;
            if (_meshes.TryGetValue(key, out var cached))
            {
                return cached;
            }

            MeshData? mesh = null;
            var file = (string?)rep.Attribute("associatedFile");
            if (!string.IsNullOrEmpty(file))
            {
                var name = file.StartsWith("urn:3DXML:", StringComparison.OrdinalIgnoreCase) ? file["urn:3DXML:".Length..] : file;
                var hash = name.IndexOf('#');
                if (hash >= 0)
                {
                    name = name[..hash];
                }

                var bytes = _files(name);
                if (bytes is null)
                {
                    _model.Warnings.Add("The representation file " + name + " is missing.");
                }
                else if (bytes.Length > 0 && bytes.AsSpan().IndexOf((byte)'<') is >= 0 and < 64)
                {
                    mesh = ReadRepresentation(bytes, (string?)rep.Attribute("name") ?? name);
                    if (mesh is null)
                    {
                        _model.Warnings.Add("The representation " + name + " has no tessellated faces (format " + ((string?)rep.Attribute("format") ?? "unknown") + ").");
                    }
                }
                else
                {
                    _model.Warnings.Add("The representation " + name + " is binary (CGR) and is not decoded.");
                }
            }

            if (mesh is not null)
            {
                _model.Meshes.Add(mesh);
            }

            _meshes[key] = mesh;
            return mesh;
        }

        private int MaterialFor(float r, float g, float b, float a)
        {
            static uint Q(float v) => (uint)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
            var key = (Q(r) << 24) | (Q(g) << 16) | (Q(b) << 8) | Q(a);
            if (!_materials.TryGetValue(key, out var index))
            {
                index = _model.Materials.Count;
                _model.Materials.Add(new ModelMaterial { Name = "Colour " + (index + 1).ToString(CultureInfo.InvariantCulture), BaseColor = new Vector4(Math.Clamp(r, 0f, 1f), Math.Clamp(g, 0f, 1f), Math.Clamp(b, 0f, 1f), Math.Clamp(a, 0f, 1f)), Roughness = 0.6f });
                _materials[key] = index;
            }

            return index;
        }

        private sealed class Part
        {
            public float[] Positions = [];
            public float[] Normals = [];
            public readonly List<(int Material, List<uint> Triangles)> Faces = new();
        }

        private MeshData? ReadRepresentation(byte[] bytes, string name)
        {
            var parts = new List<Part>();
            using var stream = new MemoryStream(bytes, writable: false);
            using var xml = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxDocumentBytes });
            Part? current = null;
            var polygonalDepth = -1;
            while (xml.Read())
            {
                Cancel.Every(_token, _tick++);
                if (xml.NodeType == XmlNodeType.EndElement)
                {
                    if (current is not null && xml.Depth == polygonalDepth)
                    {
                        parts.Add(current);
                        current = null;
                        polygonalDepth = -1;
                    }

                    continue;
                }

                if (xml.NodeType != XmlNodeType.Element)
                {
                    continue;
                }

                switch (xml.LocalName)
                {
                    case "Rep" when current is null && (xml.GetAttribute("type", "http://www.w3.org/2001/XMLSchema-instance") ?? xml.GetAttribute("xsi:type"))?.EndsWith("PolygonalRepType", StringComparison.Ordinal) == true:
                        if (xml.IsEmptyElement)
                        {
                            break;
                        }

                        current = new Part();
                        polygonalDepth = xml.Depth;
                        break;
                    case "PolygonalLOD" when current is not null:
                        xml.Skip();
                        // Skip leaves the reader on the next node, which must still be looked at.
                        if (xml.NodeType == XmlNodeType.EndElement && current is not null && xml.Depth == polygonalDepth)
                        {
                            parts.Add(current);
                            current = null;
                            polygonalDepth = -1;
                        }

                        break;
                    case "Positions" when current is not null:
                        current.Positions = ParseFloats(xml.ReadElementContentAsString());
                        break;
                    case "Normals" when current is not null:
                        current.Normals = ParseFloats(xml.ReadElementContentAsString());
                        break;
                    case "Edges" when current is not null:
                        xml.Skip();
                        break;
                    case "Face" when current is not null:
                        ReadFace(xml, current);
                        break;
                }
            }

            return Merge(parts, name);
        }

        private void ReadFace(XmlReader xml, Part part)
        {
            var triangles = xml.GetAttribute("triangles");
            var strips = xml.GetAttribute("strips");
            var fans = xml.GetAttribute("fans");
            var material = -1;
            if (!xml.IsEmptyElement)
            {
                using var sub = xml.ReadSubtree();
                while (sub.Read())
                {
                    if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "Color")
                    {
                        material = MaterialFor(Number(sub.GetAttribute("red"), 0.8f), Number(sub.GetAttribute("green"), 0.8f), Number(sub.GetAttribute("blue"), 0.8f), Number(sub.GetAttribute("alpha"), 1f));
                        break;
                    }
                }
            }

            var list = new List<uint>();
            if (!string.IsNullOrEmpty(triangles))
            {
                var ids = ParseInts(triangles);
                for (var i = 0; i + 2 < ids.Count; i += 3)
                {
                    Cancel.Every(_token, i);
                    list.Add(ids[i]);
                    list.Add(ids[i + 1]);
                    list.Add(ids[i + 2]);
                }
            }

            if (!string.IsNullOrEmpty(strips))
            {
                foreach (var run in strips.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var ids = ParseInts(run);
                    for (var i = 0; i + 2 < ids.Count; i++)
                    {
                        Cancel.Every(_token, i);
                        if ((i & 1) == 0)
                        {
                            list.Add(ids[i]);
                            list.Add(ids[i + 1]);
                        }
                        else
                        {
                            list.Add(ids[i + 1]);
                            list.Add(ids[i]);
                        }

                        list.Add(ids[i + 2]);
                    }
                }
            }

            if (!string.IsNullOrEmpty(fans))
            {
                foreach (var run in fans.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var ids = ParseInts(run);
                    for (var i = 1; i + 1 < ids.Count; i++)
                    {
                        Cancel.Every(_token, i);
                        list.Add(ids[0]);
                        list.Add(ids[i]);
                        list.Add(ids[i + 1]);
                    }
                }
            }

            _triangles += list.Count / 3;
            if (_triangles > _options.MaxTriangles)
            {
                throw new ModelFormatException("The 3DXML model exceeds the triangle limit.");
            }

            part.Faces.Add((material, list));
        }

        private MeshData? Merge(List<Part> parts, string name)
        {
            var vertexCount = 0L;
            foreach (var p in parts)
            {
                vertexCount += p.Positions.Length / 3;
            }

            if (vertexCount == 0 || vertexCount > _options.MaxPoints)
            {
                return null;
            }

            var positions = new float[vertexCount * 3];
            var normals = parts.All(p => p.Normals.Length == p.Positions.Length) ? new float[vertexCount * 3] : [];
            var byMaterial = new SortedDictionary<int, List<uint>>();
            uint baseVertex = 0;
            foreach (var p in parts)
            {
                var n = (uint)(p.Positions.Length / 3);
                Array.Copy(p.Positions, 0, positions, baseVertex * 3L, p.Positions.Length);
                if (normals.Length > 0)
                {
                    Array.Copy(p.Normals, 0, normals, baseVertex * 3L, p.Normals.Length);
                }

                foreach (var (material, list) in p.Faces)
                {
                    if (!byMaterial.TryGetValue(material, out var target))
                    {
                        byMaterial[material] = target = new List<uint>(list.Count);
                    }

                    for (var i = 0; i + 2 < list.Count; i += 3)
                    {
                        Cancel.Every(_token, i);
                        uint a = list[i], b = list[i + 1], c = list[i + 2];
                        if (a >= n || b >= n || c >= n || a == b || b == c || a == c)
                        {
                            continue;
                        }

                        target.Add(a + baseVertex);
                        target.Add(b + baseVertex);
                        target.Add(c + baseVertex);
                    }
                }

                baseVertex += n;
            }

            var mesh = new MeshData { Name = name, Positions = positions, Normals = normals };
            var indices = new List<uint>();
            foreach (var (material, list) in byMaterial)
            {
                if (list.Count == 0)
                {
                    continue;
                }

                mesh.Groups.Add(new MeshGroup(indices.Count, list.Count, material));
                indices.AddRange(list);
            }

            if (indices.Count == 0)
            {
                return null;
            }

            mesh.Indices = indices.ToArray();
            return mesh;
        }

        private static float Number(string? text, float fallback) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && float.IsFinite(v) ? v : fallback;

        private float[] ParseFloats(string text)
        {
            var span = text.AsSpan();
            var result = new List<float>(Math.Min(span.Length / 6 + 4, 1 << 20));
            var i = 0;
            while (i < span.Length)
            {
                Cancel.Every(_token, i);
                while (i < span.Length && (span[i] == ' ' || span[i] == ',' || span[i] == '\n' || span[i] == '\r' || span[i] == '\t'))
                {
                    i++;
                }

                var start = i;
                while (i < span.Length && span[i] is not (' ' or ',' or '\n' or '\r' or '\t'))
                {
                    i++;
                }

                if (i > start)
                {
                    result.Add(float.TryParse(span[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && float.IsFinite(v) ? v : 0f);
                }
            }

            // Whole vertices only.
            var whole = result.Count / 3 * 3;
            if (whole != result.Count)
            {
                result.RemoveRange(whole, result.Count - whole);
            }

            return result.ToArray();
        }

        private List<uint> ParseInts(string text)
        {
            var span = text.AsSpan();
            var result = new List<uint>(Math.Min(span.Length / 3 + 2, 1 << 20));
            var i = 0;
            while (i < span.Length)
            {
                Cancel.Every(_token, i);
                while (i < span.Length && !char.IsAsciiDigit(span[i]))
                {
                    i++;
                }

                var start = i;
                while (i < span.Length && char.IsAsciiDigit(span[i]))
                {
                    i++;
                }

                if (i > start)
                {
                    result.Add(uint.TryParse(span[start..i], NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : uint.MaxValue);
                }
            }

            return result;
        }

        private static Matrix4x4 ParseMatrix(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return Matrix4x4.Identity;
            }

            Span<float> v = stackalloc float[12];
            var count = 0;
            foreach (var part in text.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries))
            {
                if (count == 12)
                {
                    break;
                }

                if (!float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) || !float.IsFinite(f))
                {
                    return Matrix4x4.Identity;
                }

                v[count++] = f;
            }

            return count < 12
                ? Matrix4x4.Identity
                : new Matrix4x4(v[0], v[1], v[2], 0, v[3], v[4], v[5], 0, v[6], v[7], v[8], 0, v[9], v[10], v[11], 1);
        }
    }
}
