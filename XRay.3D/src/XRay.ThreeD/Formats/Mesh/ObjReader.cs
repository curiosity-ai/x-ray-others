using System.Globalization;
using System.Text;

namespace XRay.ThreeD.Formats.Mesh;

/// <summary>
/// Wavefront OBJ: every <c>o</c>/<c>g</c> object becomes a node with its own mesh, <c>usemtl</c> splits it into
/// material groups. The .mtl file is a separate file the loader does not see, so materials get names and a
/// neutral colour; the library file name is kept in the metadata.
/// </summary>
internal static class ObjReader
{
    private sealed class Group
    {
        public string? Name;
        public readonly Dictionary<(int, int), uint> Lookup = new();
        public readonly List<float> Positions = new();
        public readonly List<float> Normals = new();
        public readonly Dictionary<int, List<uint>> ByMaterial = new();
        public readonly List<int> MaterialOrder = new();
        public bool HasNormals;
    }

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var model = new Model3D();
        model.Metadata.Format = ModelFormats.Obj;
        model.Metadata.DocumentKind = "mesh";
        model.Metadata.UpAxis = UpAxis.Y;
        model.Metadata.Notes.Add("OBJ stores no units; Y-up is assumed.");

        var positions = new List<float>();
        var normals = new List<float>();
        var materialIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var groups = new List<Group>();
        var current = new Group();
        groups.Add(current);
        var material = -1;
        long triangles = 0;

        string text = Encoding.UTF8.GetString(data.Span);
        foreach (var raw in text.AsSpan().EnumerateLines())
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var space = line.IndexOfAny(' ', '\t');
            var keyword = space < 0 ? line : line[..space];
            var rest = space < 0 ? [] : line[(space + 1)..].Trim();
            switch (keyword)
            {
                case "v":
                    AddFloats(rest, 3, positions);
                    break;
                case "vn":
                    AddFloats(rest, 3, normals);
                    break;
                case "o":
                case "g":
                    {
                        var name = rest.ToString();
                        if (current.Positions.Count == 0 && current.ByMaterial.Count == 0)
                        {
                            current.Name = name.Length > 0 ? name : null;
                        }
                        else
                        {
                            current = new Group { Name = name.Length > 0 ? name : null };
                            groups.Add(current);
                        }

                        break;
                    }

                case "usemtl":
                    {
                        var name = rest.ToString();
                        if (!materialIndex.TryGetValue(name, out material))
                        {
                            material = model.Materials.Count;
                            materialIndex[name] = material;
                            model.Materials.Add(new ModelMaterial { Name = name });
                        }

                        break;
                    }

                case "mtllib":
                    model.Metadata.Properties.Add(new ModelProperty("mtllib", rest.ToString(), "obj"));
                    break;
                case "f":
                    triangles += AddFace(rest, current, material, positions, normals);
                    if (triangles > options.MaxTriangles)
                    {
                        throw new ModelFormatException("The OBJ has more triangles than the configured limit.");
                    }

                    break;
            }
        }

        foreach (var g in groups.Where(g => g.Positions.Count > 0))
        {
            options.Cancellation.ThrowIfCancellationRequested();
            var mesh = new MeshData { Name = g.Name, Positions = g.Positions.ToArray() };
            if (g.HasNormals)
            {
                mesh.Normals = g.Normals.ToArray();
            }

            var indices = new List<uint>();
            foreach (var m in g.MaterialOrder)
            {
                var list = g.ByMaterial[m];
                mesh.Groups.Add(new MeshGroup(indices.Count, list.Count, m));
                indices.AddRange(list);
            }

            mesh.Indices = indices.ToArray();
            model.Meshes.Add(mesh);
        }

        if (model.Meshes.Count == 0)
        {
            throw new ModelFormatException("The OBJ file holds no faces.");
        }

        if (model.Meshes.Count == 1)
        {
            model.Roots.Add(new SceneNode(model.Meshes[0].Name) { Mesh = model.Meshes[0] });
        }
        else
        {
            var root = new SceneNode(null);
            foreach (var mesh in model.Meshes)
            {
                root.Children.Add(new SceneNode(mesh.Name) { Mesh = mesh });
            }

            model.Roots.Add(root);
        }

        model.Metadata.PartNames.AddRange(model.Meshes.Select(m => m.Name).Where(n => !string.IsNullOrEmpty(n))!);
        model.Metadata.MaterialNames.AddRange(model.Materials.Select(m => m.Name!));
        model.Metadata.HasGeometry = true;
        model.Metadata.Statistics = model.ComputeStatistics();
        model.Metadata.Bounds = model.ComputeBounds();
        return model;
    }

    private static void AddFloats(ReadOnlySpan<char> text, int count, List<float> into)
    {
        var added = 0;
        foreach (var range in text.Split(' '))
        {
            var token = text[range].Trim();
            if (token.Length == 0)
            {
                continue;
            }

            if (added < count)
            {
                into.Add(float.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture));
                added++;
            }
        }

        if (added < count)
        {
            throw new ModelFormatException("An OBJ vertex has fewer than three coordinates.");
        }
    }

    private static int AddFace(ReadOnlySpan<char> text, Group group, int material, List<float> positions, List<float> normals)
    {
        var corners = new List<uint>(4);
        foreach (var range in text.Split(' '))
        {
            var token = text[range].Trim();
            if (token.Length == 0)
            {
                continue;
            }

            var slash = token.IndexOf('/');
            var vertexToken = slash < 0 ? token : token[..slash];
            var normalToken = default(ReadOnlySpan<char>);
            if (slash >= 0)
            {
                var second = token[(slash + 1)..].IndexOf('/');
                if (second >= 0)
                {
                    normalToken = token[(slash + 2 + second)..];
                }
            }

            var vertex = Resolve(int.Parse(vertexToken, CultureInfo.InvariantCulture), positions.Count / 3);
            var normal = normalToken.Length > 0 ? Resolve(int.Parse(normalToken, CultureInfo.InvariantCulture), normals.Count / 3) : -1;
            if (!group.Lookup.TryGetValue((vertex, normal), out var index))
            {
                index = (uint)(group.Positions.Count / 3);
                group.Lookup[(vertex, normal)] = index;
                group.Positions.Add(positions[3 * vertex]);
                group.Positions.Add(positions[3 * vertex + 1]);
                group.Positions.Add(positions[3 * vertex + 2]);
                if (normal >= 0)
                {
                    group.HasNormals = true;
                    group.Normals.Add(normals[3 * normal]);
                    group.Normals.Add(normals[3 * normal + 1]);
                    group.Normals.Add(normals[3 * normal + 2]);
                }
                else
                {
                    group.Normals.Add(0);
                    group.Normals.Add(0);
                    group.Normals.Add(0);
                }
            }

            corners.Add(index);
        }

        if (corners.Count < 3)
        {
            return 0;
        }

        if (!group.ByMaterial.TryGetValue(material, out var list))
        {
            list = new List<uint>();
            group.ByMaterial[material] = list;
            group.MaterialOrder.Add(material);
        }

        for (var i = 1; i + 1 < corners.Count; i++)
        {
            list.Add(corners[0]);
            list.Add(corners[i]);
            list.Add(corners[i + 1]);
        }

        return corners.Count - 2;
    }

    /// <summary>OBJ indices are 1-based, and negative ones count back from the end.</summary>
    private static int Resolve(int index, int count)
    {
        var resolved = index > 0 ? index - 1 : count + index;
        if (resolved < 0 || resolved >= count)
        {
            throw new ModelFormatException("An OBJ face refers to a vertex that does not exist.");
        }

        return resolved;
    }
}
