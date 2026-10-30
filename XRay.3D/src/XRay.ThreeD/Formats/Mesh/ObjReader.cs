using System.Globalization;
using System.Text;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.Mesh;

/// <summary>
/// Wavefront OBJ: every <c>o</c>/<c>g</c> object becomes a node with its own mesh, <c>usemtl</c> splits it into
/// material groups. The .mtl file is a separate file: when the caller supplies companions
/// (<see cref="LoadOptions.ResolveCompanion"/>, as ZIP archives do) its diffuse colour, opacity and shininess fill the materials;
/// otherwise materials keep their names and a neutral colour. The library file name is kept in the metadata.
/// </summary>
internal static class ObjReader
{
    private sealed class Group
    {
        public string? Name;
        public readonly Dictionary<(int, int, int), uint> Lookup = new();
        public readonly List<float> Positions = new();
        public readonly List<float> Normals = new();
        public readonly List<float> TexCoords = new();
        public bool HasTexCoords;
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
        var texCoords = new List<float>();
        var materialIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var groups = new List<Group>();
        var current = new Group();
        groups.Add(current);
        var material = -1;
        long triangles = 0;
        var libraries = new List<string>();

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
                case "vt":
                    AddTexCoord(rest, texCoords);
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
                    libraries.Add(rest.ToString());
                    break;
                case "f":
                    triangles += AddFace(rest, current, material, positions, normals, texCoords);
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

            if (g.HasTexCoords)
            {
                mesh.TexCoords = g.TexCoords.ToArray();
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

        if (options.ResolveCompanion is { } resolve)
        {
            ApplyLibraries(model, libraries, resolve, materialIndex);
        }

        model.Metadata.PartNames.AddRange(model.Meshes.Select(m => m.Name).Where(n => !string.IsNullOrEmpty(n))!);
        model.Metadata.MaterialNames.AddRange(model.Materials.Select(m => m.Name!));
        model.Metadata.HasGeometry = true;
        model.Metadata.Statistics = model.ComputeStatistics();
        model.Metadata.Bounds = model.ComputeBounds();
        return model;
    }

    /// <summary>Reads the .mtl files named by <c>mtllib</c>: newmtl, Kd (diffuse), d or Tr (opacity), Ns (shininess), Pm and Pr (PBR extension).</summary>
    private static void ApplyLibraries(Model3D model, List<string> libraries, Func<string, byte[]?> resolve, Dictionary<string, int> materialIndex)
    {
        var textures = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in libraries.SelectMany(l => l.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.Ordinal).Take(16))
        {
            var bytes = resolve(entry);
            if (bytes is null)
            {
                model.Warnings.Add($"The material library '{entry}' was not found; materials keep neutral colours.");
                continue;
            }

            ModelMaterial? current = null;
            var textureCache = textures;
            foreach (var raw in Encoding.UTF8.GetString(bytes).AsSpan().EnumerateLines())
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                var space = line.IndexOfAny(' ', '\t');
                var keyword = space < 0 ? line : line[..space];
                var rest = space < 0 ? default : line[(space + 1)..].Trim();
                if (keyword.Equals("newmtl", StringComparison.OrdinalIgnoreCase))
                {
                    current = materialIndex.TryGetValue(rest.ToString(), out var index) ? model.Materials[index] : null;
                    continue;
                }

                if (current is null)
                {
                    continue;
                }

                if (keyword.Equals("map_Kd", StringComparison.OrdinalIgnoreCase))
                {
                    ApplyDiffuseMap(model, current, rest.ToString(), resolve, textureCache);
                    continue;
                }

                var values = new List<float>(3);
                foreach (var range in rest.Split(' '))
                {
                    if (float.TryParse(rest[range], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && float.IsFinite(v))
                    {
                        values.Add(v);
                    }
                }

                if (values.Count == 0)
                {
                    continue;
                }

                var colour = current.BaseColor;
                switch (keyword.ToString().ToLowerInvariant())
                {
                    case "kd" when values.Count >= 3:
                        current.BaseColor = new System.Numerics.Vector4(Math.Clamp(values[0], 0, 1), Math.Clamp(values[1], 0, 1), Math.Clamp(values[2], 0, 1), colour.W);
                        break;
                    case "d":
                        current.BaseColor = colour with { W = Math.Clamp(values[0], 0, 1) };
                        break;
                    case "tr":
                        current.BaseColor = colour with { W = Math.Clamp(1 - values[0], 0, 1) };
                        break;
                    case "ns":
                        current.Roughness = (float)(1 - Math.Sqrt(Math.Clamp(values[0], 0, 1000) / 1000));
                        break;
                    case "pr":
                        current.Roughness = Math.Clamp(values[0], 0, 1);
                        break;
                    case "pm":
                        current.Metallic = Math.Clamp(values[0], 0, 1);
                        break;
                }
            }
        }
    }

    /// <summary>
    /// <c>map_Kd [options] file</c>: the diffuse image. Options that matter are <c>-s u v w</c> (scale), <c>-o u v w</c> (offset) and
    /// <c>-clamp on</c>; the others take their arguments and are skipped. OBJ's v axis points up and glTF's down, and the mesh's v is flipped
    /// to match, so the transform is conjugated by the flip: offset v becomes <c>1 - scale v - offset v</c>.
    /// </summary>
    private static void ApplyDiffuseMap(Model3D model, ModelMaterial material, string text, Func<string, byte[]?> resolve, Dictionary<string, int> cache)
    {
        var tokens = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        var offset = System.Numerics.Vector2.Zero;
        var scale = System.Numerics.Vector2.One;
        var clamp = false;
        var i = 0;
        float Number(int at) => at < tokens.Length && float.TryParse(tokens[at], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && float.IsFinite(v) ? v : 0;
        bool IsNumber(int at) => at < tokens.Length && float.TryParse(tokens[at], NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        int Numbers(int from, int max)
        {
            var n = 0;
            while (n < max && IsNumber(from + n))
            {
                n++;
            }

            return n;
        }

        while (i < tokens.Length && tokens[i].StartsWith('-') && tokens[i].Length > 1 && !char.IsDigit(tokens[i][1]))
        {
            var option = tokens[i].ToLowerInvariant();
            switch (option)
            {
                case "-o":
                    offset = new System.Numerics.Vector2(Number(i + 1), Numbers(i + 1, 3) >= 2 ? Number(i + 2) : 0);
                    i += 1 + Numbers(i + 1, 3);
                    break;
                case "-s":
                    scale = new System.Numerics.Vector2(Number(i + 1) is var su and not 0 ? su : 1, Numbers(i + 1, 3) >= 2 && Number(i + 2) is var sv and not 0 ? sv : 1);
                    i += 1 + Numbers(i + 1, 3);
                    break;
                case "-t":
                    i += 1 + Numbers(i + 1, 3);
                    break;
                case "-mm":
                    i += 1 + Numbers(i + 1, 2);
                    break;
                case "-clamp":
                    clamp = i + 1 < tokens.Length && tokens[i + 1].Equals("on", StringComparison.OrdinalIgnoreCase);
                    i += 2;
                    break;
                default:
                    i += 2; // -blendu, -blendv, -cc, -bm, -imfchan, -texres: one argument
                    break;
            }
        }

        if (i >= tokens.Length)
        {
            return;
        }

        // The file name is the rest of the line (it may hold spaces).
        var name = string.Join(' ', tokens.Skip(i)).Trim('"');
        var key = name + (clamp ? "|clamp" : "");
        if (!cache.TryGetValue(key, out var index))
        {
            index = -1;
            var bytes = resolve(name);
            if (bytes is null)
            {
                model.Warnings.Add($"The texture '{name}' was not found; the material keeps its colour.");
            }
            else if (TextureImages.Create(name, bytes, out var problem) is { } texture)
            {
                if (clamp)
                {
                    texture.WrapU = texture.WrapV = TextureWrap.ClampToEdge;
                }

                index = model.Textures.Count;
                model.Textures.Add(texture);
            }
            else if (problem is not null)
            {
                model.Warnings.Add(problem);
            }

            cache[key] = index;
        }

        if (index >= 0)
        {
            material.BaseColorTexture = new ModelTextureRef(index)
            {
                Scale = scale,
                Offset = new System.Numerics.Vector2(offset.X, 1 - scale.Y - offset.Y),
            };
            if (!material.BaseColorTexture.HasTransform)
            {
                material.BaseColorTexture.Offset = System.Numerics.Vector2.Zero;
            }
        }
    }

    private static void AddTexCoord(ReadOnlySpan<char> text, List<float> into)
    {
        var added = 0;
        foreach (var range in text.Split(' '))
        {
            var token = text[range].Trim();
            if (token.Length == 0)
            {
                continue;
            }

            if (added < 2)
            {
                into.Add(float.Parse(token, NumberStyles.Float, CultureInfo.InvariantCulture));
                added++;
            }
        }

        if (added == 0)
        {
            throw new ModelFormatException("An OBJ texture coordinate has no values.");
        }

        if (added == 1)
        {
            into.Add(0);
        }
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

    private static int AddFace(ReadOnlySpan<char> text, Group group, int material, List<float> positions, List<float> normals, List<float> texCoords)
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
            var texToken = default(ReadOnlySpan<char>);
            if (slash >= 0)
            {
                var after = token[(slash + 1)..];
                var second = after.IndexOf('/');
                texToken = second >= 0 ? after[..second] : after;
                if (second >= 0)
                {
                    normalToken = token[(slash + 2 + second)..];
                }
            }

            var vertex = Resolve(int.Parse(vertexToken, CultureInfo.InvariantCulture), positions.Count / 3);
            var normal = normalToken.Length > 0 ? Resolve(int.Parse(normalToken, CultureInfo.InvariantCulture), normals.Count / 3) : -1;
            // A texture index that points nowhere (files with "f v/1/n" and no vt lines exist) is ignored, not an error.
            var tex = -1;
            if (texToken.Length > 0 && int.TryParse(texToken, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var texIndex))
            {
                var resolved = texIndex > 0 ? texIndex - 1 : texCoords.Count / 2 + texIndex;
                tex = resolved >= 0 && resolved < texCoords.Count / 2 ? resolved : -1;
            }
            if (!group.Lookup.TryGetValue((vertex, tex, normal), out var index))
            {
                index = (uint)(group.Positions.Count / 3);
                group.Lookup[(vertex, tex, normal)] = index;
                if (tex >= 0)
                {
                    group.HasTexCoords = true;
                    group.TexCoords.Add(texCoords[2 * tex]);
                    group.TexCoords.Add(1 - texCoords[2 * tex + 1]);
                }
                else
                {
                    group.TexCoords.Add(0);
                    group.TexCoords.Add(0);
                }

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
