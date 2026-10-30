using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Xml;
using System.Xml.Linq;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.Mesh;

/// <summary>3D Manufacturing Format: objects with meshes or components, build items with transforms, metadata, base-material colours and units.</summary>
internal static class ThreeMfReader
{
    private const long MaxEntryBytes = 1L << 30;

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        using var zip = new ZipArchive(new MemoryStream(data.ToArray(), writable: false), ZipArchiveMode.Read);
        var modelEntry = FindModelPath(zip) is { } path
            ? zip.GetEntry(path)
            : zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".model", StringComparison.OrdinalIgnoreCase));
        if (modelEntry is null)
        {
            throw new ModelFormatException("The 3MF package has no 3D model part.");
        }

        if (modelEntry.Length > MaxEntryBytes)
        {
            throw new ModelFormatException("The 3MF model part is too large.");
        }

        XDocument document;
        using (var stream = modelEntry.Open())
        using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
        {
            document = XDocument.Load(reader);
        }

        var root = document.Root ?? throw new ModelFormatException("The 3MF model part is empty.");
        var model = new Model3D();
        var meta = model.Metadata;
        meta.Format = ModelFormats.ThreeMf;
        meta.DocumentKind = "mesh";
        meta.UpAxis = UpAxis.Z;
        meta.Units = ((string?)root.Attribute("unit")) switch
        {
            "micron" => LengthUnit.Micrometer,
            "centimeter" => LengthUnit.Centimeter,
            "inch" => LengthUnit.Inch,
            "foot" => LengthUnit.Foot,
            "meter" => LengthUnit.Meter,
            _ => LengthUnit.Millimeter,
        };

        foreach (var m in root.Elements().Where(e => e.Name.LocalName == "metadata"))
        {
            var name = (string?)m.Attribute("name");
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var value = m.Value;
            switch (name)
            {
                case "Title": meta.Title = value; break;
                case "Designer": meta.Author = value; break;
                case "Application": meta.Application = value; break;
                case "CreationDate": meta.Created = DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var c) ? c : null; break;
                case "ModificationDate": meta.Modified = DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var mo) ? mo : null; break;
            }

            meta.Properties.Add(new ModelProperty(name, value, "3mf"));
        }

        var resources = root.Elements().FirstOrDefault(e => e.Name.LocalName == "resources");
        var build = root.Elements().FirstOrDefault(e => e.Name.LocalName == "build");
        if (resources is null || build is null)
        {
            throw new ModelFormatException("The 3MF model has no resources or build section.");
        }

        var colorGroups = new Dictionary<string, List<Vector4>>(StringComparer.Ordinal);
        foreach (var group in resources.Elements().Where(e => e.Name.LocalName == "basematerials"))
        {
            var list = new List<Vector4>();
            foreach (var b in group.Elements().Where(e => e.Name.LocalName == "base"))
            {
                list.Add(ParseColor((string?)b.Attribute("displaycolor")));
                var materialName = (string?)b.Attribute("name");
                if (!string.IsNullOrEmpty(materialName) && !meta.MaterialNames.Contains(materialName))
                {
                    meta.MaterialNames.Add(materialName);
                }
            }

            colorGroups[(string?)group.Attribute("id") ?? ""] = list;
        }

        // Materials extension: texture2d (an image part), texture2dgroup (UV coordinates over one image; triangles name three per corner).
        var texturePaths = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var texture in resources.Elements().Where(e => e.Name.LocalName == "texture2d"))
        {
            texturePaths[(string?)texture.Attribute("id") ?? ""] = texture;
        }

        var textureGroups = new Dictionary<string, TextureGroup>(StringComparer.Ordinal);
        var textureIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var group in resources.Elements().Where(e => e.Name.LocalName == "texture2dgroup"))
        {
            var coords = new List<Vector2>();
            foreach (var c in group.Elements().Where(e => e.Name.LocalName == "tex2coord"))
            {
                coords.Add(new Vector2(
                    float.Parse((string?)c.Attribute("u") ?? "0", CultureInfo.InvariantCulture),
                    float.Parse((string?)c.Attribute("v") ?? "0", CultureInfo.InvariantCulture)));
            }

            textureGroups[(string?)group.Attribute("id") ?? ""] = new TextureGroup((string?)group.Attribute("texid") ?? "", coords);
        }

        int LoadTexture(string id)
        {
            if (textureIndex.TryGetValue(id, out var existing))
            {
                return existing;
            }

            var result = -1;
            if (texturePaths.TryGetValue(id, out var element))
            {
                var path = ((string?)element.Attribute("path") ?? "").TrimStart('/');
                var entry = ZipEntries.Find(zip, path);
                if (entry is null)
                {
                    model.Warnings.Add($"The texture part '{path}' is missing from the package.");
                }
                else if (TextureImages.Create(path, ZipEntries.Read(entry, options.MaxInputBytes, options.Cancellation), out var problem, options.Cancellation) is { } created)
                {
                    created.WrapU = Tile((string?)element.Attribute("tilestyleu"));
                    created.WrapV = Tile((string?)element.Attribute("tilestylev"));
                    created.Nearest = string.Equals((string?)element.Attribute("filter"), "nearest", StringComparison.OrdinalIgnoreCase);
                    result = model.Textures.Count;
                    model.Textures.Add(created);
                }
                else if (problem is not null)
                {
                    model.Warnings.Add(problem);
                }
            }

            textureIndex[id] = result;
            return result;
        }

        var objects = resources.Elements().Where(e => e.Name.LocalName == "object").ToDictionary(e => (string?)e.Attribute("id") ?? "", e => e, StringComparer.Ordinal);
        var materialIds = new Dictionary<(string, int), int>();
        var built = new Dictionary<string, (SceneNode? Template, MeshData? Mesh)>(StringComparer.Ordinal);
        long triangles = 0;

        SceneNode BuildObject(string id, int depth)
        {
            if (depth > 32 || !objects.TryGetValue(id, out var obj))
            {
                throw new ModelFormatException("A 3MF object reference is missing or cyclic.");
            }

            var node = new SceneNode((string?)obj.Attribute("name") ?? (string?)obj.Attribute("partnumber"));
            if (obj.Attribute("partnumber") is { } pn)
            {
                node.Properties["partNumber"] = pn.Value;
            }

            if (obj.Attribute("type") is { } type)
            {
                node.Properties["type"] = type.Value;
            }

            var meshElement = obj.Elements().FirstOrDefault(e => e.Name.LocalName == "mesh");
            if (meshElement is not null)
            {
                options.Cancellation.ThrowIfCancellationRequested();
                var mesh = BuildMesh(meshElement, (string?)obj.Attribute("pid"), (string?)obj.Attribute("pindex"), colorGroups, textureGroups, LoadTexture, materialIds, model, options, ref triangles);
                mesh.Name = node.Name;
                model.Meshes.Add(mesh);
                node.Mesh = mesh;
            }

            var components = obj.Elements().FirstOrDefault(e => e.Name.LocalName == "components");
            if (components is not null)
            {
                foreach (var component in components.Elements().Where(e => e.Name.LocalName == "component"))
                {
                    var child = BuildObject((string?)component.Attribute("objectid") ?? "", depth + 1);
                    child.Transform = ParseTransform((string?)component.Attribute("transform"));
                    node.Children.Add(child);
                }
            }

            return node;
        }

        foreach (var item in build.Elements().Where(e => e.Name.LocalName == "item"))
        {
            var node = BuildObject((string?)item.Attribute("objectid") ?? "", 0);
            node.Transform = ParseTransform((string?)item.Attribute("transform"));
            if (item.Attribute("partnumber") is { } part)
            {
                node.Properties["partNumber"] = part.Value;
            }

            model.Roots.Add(node);
        }

        _ = built;
        if (model.Meshes.Count == 0)
        {
            throw new ModelFormatException("The 3MF file holds no meshes.");
        }

        meta.PartNames.AddRange(model.Nodes().Where(n => n.Mesh is not null && !string.IsNullOrEmpty(n.Name)).Select(n => n.Name!));
        meta.HasGeometry = true;
        meta.Statistics = model.ComputeStatistics();
        meta.Bounds = model.ComputeBounds();
        return model;
    }

    private sealed record TextureGroup(string TextureId, List<Vector2> Coordinates);

    private static TextureWrap Tile(string? style) => style?.ToLowerInvariant() switch
    {
        "clamp" => TextureWrap.ClampToEdge,
        "mirror" => TextureWrap.MirroredRepeat,
        _ => TextureWrap.Repeat,
    };

    private static string? FindModelPath(ZipArchive zip)
    {
        var rels = zip.GetEntry("_rels/.rels");
        if (rels is null)
        {
            return zip.GetEntry("3D/3dmodel.model") is not null ? "3D/3dmodel.model" : null;
        }

        using var reader = XmlReader.Create(rels.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = XDocument.Load(reader);
        var target = document.Descendants()
            .Where(e => e.Name.LocalName == "Relationship" && ((string?)e.Attribute("Type"))?.EndsWith("3dmodel", StringComparison.OrdinalIgnoreCase) == true)
            .Select(e => (string?)e.Attribute("Target"))
            .FirstOrDefault();
        return target?.TrimStart('/');
    }

    private static MeshData BuildMesh(
        XElement mesh,
        string? pid,
        string? pindex,
        Dictionary<string, List<Vector4>> colorGroups,
        Dictionary<string, TextureGroup> textureGroups,
        Func<string, int> loadTexture,
        Dictionary<(string, int), int> materialIds,
        Model3D model,
        LoadOptions options,
        ref long triangleTotal)
    {
        var vertices = mesh.Elements().FirstOrDefault(e => e.Name.LocalName == "vertices")?.Elements().ToList() ?? [];
        var positions = new List<float>(vertices.Count * 3);
        for (var i = 0; i < vertices.Count; i++)
        {
            positions.Add(float.Parse((string?)vertices[i].Attribute("x") ?? "0", CultureInfo.InvariantCulture));
            positions.Add(float.Parse((string?)vertices[i].Attribute("y") ?? "0", CultureInfo.InvariantCulture));
            positions.Add(float.Parse((string?)vertices[i].Attribute("z") ?? "0", CultureInfo.InvariantCulture));
        }

        // A textured corner needs its own vertex per (vertex, coordinate); those are appended after the shared ones.
        var texCoords = new List<float>(new float[vertices.Count * 2]);
        var textured = new Dictionary<(int Vertex, string Group, int Coordinate), uint>();
        var anyTextured = false;

        var tris = mesh.Elements().FirstOrDefault(e => e.Name.LocalName == "triangles")?.Elements().ToList() ?? [];
        triangleTotal += tris.Count;
        if (triangleTotal > options.MaxTriangles)
        {
            throw new ModelFormatException("The 3MF has more triangles than the configured limit.");
        }

        var byMaterial = new Dictionary<int, List<uint>>();
        var order = new List<int>();
        foreach (var t in tris)
        {
            var v1 = int.Parse((string?)t.Attribute("v1") ?? "-1", CultureInfo.InvariantCulture);
            var v2 = int.Parse((string?)t.Attribute("v2") ?? "-1", CultureInfo.InvariantCulture);
            var v3 = int.Parse((string?)t.Attribute("v3") ?? "-1", CultureInfo.InvariantCulture);
            if (v1 < 0 || v2 < 0 || v3 < 0 || v1 >= vertices.Count || v2 >= vertices.Count || v3 >= vertices.Count)
            {
                throw new ModelFormatException("A 3MF triangle refers to a vertex that does not exist.");
            }

            var material = -1;
            var group = (string?)t.Attribute("pid") ?? pid;
            var index = (string?)t.Attribute("p1") ?? pindex;
            var corners = new[] { (uint)v1, (uint)v2, (uint)v3 };
            if (group is not null && textureGroups.TryGetValue(group, out var uvGroup) && loadTexture(uvGroup.TextureId) is var texture and >= 0)
            {
                var vs = new[] { v1, v2, v3 };
                var ps = new[] { index, (string?)t.Attribute("p2") ?? index, (string?)t.Attribute("p3") ?? index };
                var ok = true;
                for (var k = 0; k < 3 && ok; k++)
                {
                    if (!int.TryParse(ps[k], NumberStyles.None, CultureInfo.InvariantCulture, out var ci) || ci >= uvGroup.Coordinates.Count)
                    {
                        ok = false;
                        break;
                    }

                    if (!textured.TryGetValue((vs[k], group, ci), out var copy))
                    {
                        copy = (uint)(positions.Count / 3);
                        textured[(vs[k], group, ci)] = copy;
                        positions.Add(positions[3 * vs[k]]);
                        positions.Add(positions[3 * vs[k] + 1]);
                        positions.Add(positions[3 * vs[k] + 2]);
                        texCoords.Add(uvGroup.Coordinates[ci].X);
                        texCoords.Add(1 - uvGroup.Coordinates[ci].Y);
                    }

                    corners[k] = copy;
                }

                if (ok)
                {
                    anyTextured = true;
                    if (!materialIds.TryGetValue((group, -1), out material))
                    {
                        material = model.Materials.Count;
                        materialIds[(group, -1)] = material;
                        model.Materials.Add(new ModelMaterial { Name = $"texture {uvGroup.TextureId}", BaseColor = Vector4.One, BaseColorTexture = new ModelTextureRef(texture) });
                    }
                }
                else
                {
                    corners = new[] { (uint)v1, (uint)v2, (uint)v3 };
                }
            }
            else if (group is not null && index is not null && colorGroups.TryGetValue(group, out var colors)
                && int.TryParse(index, NumberStyles.None, CultureInfo.InvariantCulture, out var ci) && ci < colors.Count)
            {
                if (!materialIds.TryGetValue((group, ci), out material))
                {
                    material = model.Materials.Count;
                    materialIds[(group, ci)] = material;
                    model.Materials.Add(new ModelMaterial { Name = $"material {group}.{ci}", BaseColor = colors[ci] });
                }
            }

            if (!byMaterial.TryGetValue(material, out var list))
            {
                list = new List<uint>();
                byMaterial[material] = list;
                order.Add(material);
            }

            list.Add(corners[0]);
            list.Add(corners[1]);
            list.Add(corners[2]);
        }

        var result = new MeshData { Positions = positions.ToArray() };
        if (anyTextured)
        {
            result.TexCoords = texCoords.ToArray();
        }

        var indices = new List<uint>();
        foreach (var m in order)
        {
            result.Groups.Add(new MeshGroup(indices.Count, byMaterial[m].Count, m));
            indices.AddRange(byMaterial[m]);
        }

        result.Indices = indices.ToArray();
        return result;
    }

    private static Matrix4x4 ParseTransform(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Matrix4x4.Identity;
        }

        var v = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => float.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        if (v.Length != 12)
        {
            throw new ModelFormatException("A 3MF transform does not have twelve values.");
        }

        return new Matrix4x4(v[0], v[1], v[2], 0, v[3], v[4], v[5], 0, v[6], v[7], v[8], 0, v[9], v[10], v[11], 1);
    }

    private static Vector4 ParseColor(string? hex)
    {
        if (hex is { Length: >= 7 } && hex[0] == '#')
        {
            var r = Convert.ToInt32(hex.Substring(1, 2), 16) / 255f;
            var g = Convert.ToInt32(hex.Substring(3, 2), 16) / 255f;
            var b = Convert.ToInt32(hex.Substring(5, 2), 16) / 255f;
            var a = hex.Length >= 9 ? Convert.ToInt32(hex.Substring(7, 2), 16) / 255f : 1f;
            return new Vector4(r, g, b, a);
        }

        return new Vector4(0.8f, 0.8f, 0.8f, 1f);
    }
}
