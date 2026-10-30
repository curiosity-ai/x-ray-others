using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD;

/// <summary>Options for <see cref="AssemblyPackage.Create"/>.</summary>
public sealed class AssemblyPackageOptions
{
    /// <summary>How each part's GLB is written (compression, edges...). The scene-level normalisation is done by the manifest, not the parts.</summary>
    public GlbWriteOptions Glb { get; set; } = new();

    /// <summary>Checked per part and per node; a cancelled call throws <see cref="OperationCanceledException"/>.</summary>
    public CancellationToken Cancellation { get; set; }
}

/// <summary>
/// A large assembly as one small manifest plus one GLB per <em>unique</em> part. The manifest holds the instance tree (names,
/// transforms, properties, which part each instance uses) and the model metadata; part geometry is fetched only when needed and
/// is shared by every instance, and by other assemblies that reuse the part. The parts are in the source's units and up axis;
/// the manifest's <c>root</c> transform converts the whole scene to meters and Y-up like <see cref="GlbWriter"/> does.
/// </summary>
/// <remarks>
/// Layout: <c>assembly.json</c> and <c>parts/p0.glb</c>, <c>parts/p1.glb</c>... Manifest schema <c>xray3d-assembly/1</c>:
/// <c>{ schema, root: {matrix?}, extras: {model metadata}, parts: [{id, name, file, vertices, triangles, bounds}],
/// nodes: [{name, part?, matrix?, properties?, children?}], roots: [node index] }</c>.
/// </remarks>
public sealed class AssemblyPackage
{
    public const string Schema = "xray3d-assembly/1";
    public const string ManifestName = "assembly.json";

    private AssemblyPackage(string manifest, Dictionary<string, byte[]> files)
    {
        Manifest = manifest;
        Files = files;
    }

    /// <summary>The manifest JSON.</summary>
    public string Manifest { get; }

    /// <summary>Every file of the package by relative path, including <see cref="ManifestName"/>.</summary>
    public IReadOnlyDictionary<string, byte[]> Files { get; }

    public static AssemblyPackage Create(Model3D model, AssemblyPackageOptions? options = null)
    {
        options ??= new AssemblyPackageOptions();
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var partIds = new Dictionary<MeshData, string>(ReferenceEqualityComparer.Instance);

        var partOptions = new GlbWriteOptions
        {
            ConvertToMeters = false,
            ConvertToYUp = false,
            IncludeMetadata = options.Glb.IncludeMetadata,
            Compression = options.Glb.Compression,
            Edges = options.Glb.Edges,
            EdgeCreaseAngle = options.Glb.EdgeCreaseAngle,
            Cancellation = options.Cancellation.CanBeCanceled ? options.Cancellation : options.Glb.Cancellation,
        };
        options.Cancellation.ThrowIfCancellationRequested();

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("schema", Schema);
            var (scale, rotation, wrap) = GlbWriter.RootTransform(model, options.Glb);
            if (wrap)
            {
                var m = Matrix4x4.CreateScale((float)scale) * Matrix4x4.CreateFromQuaternion(rotation);
                w.WriteStartObject("root");
                WriteMatrix(w, m);
                w.WriteEndObject();
            }

            GlbWriter.WriteModelExtras(w, model, options.Glb);

            w.WriteStartArray("parts");
            for (var i = 0; i < model.Meshes.Count; i++)
            {
                options.Cancellation.ThrowIfCancellationRequested();
                var mesh = model.Meshes[i];
                var id = "p" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                partIds[mesh] = id;
                var file = $"parts/{id}.glb";
                files[file] = GlbWriter.ToArray(SinglePart(model, mesh), partOptions);

                w.WriteStartObject();
                w.WriteString("id", id);
                if (!string.IsNullOrEmpty(mesh.Name))
                {
                    w.WriteString("name", mesh.Name);
                }

                w.WriteString("file", file);
                w.WriteNumber("vertices", mesh.VertexCount);
                w.WriteNumber("triangles", mesh.TriangleCount);
                if (mesh.VertexCount > 0)
                {
                    var box = mesh.ComputeBounds();
                    w.WriteStartObject("bounds");
                    w.WriteStartArray("min");
                    w.WriteNumberValue(box.Min.X);
                    w.WriteNumberValue(box.Min.Y);
                    w.WriteNumberValue(box.Min.Z);
                    w.WriteEndArray();
                    w.WriteStartArray("max");
                    w.WriteNumberValue(box.Max.X);
                    w.WriteNumberValue(box.Max.Y);
                    w.WriteNumberValue(box.Max.Z);
                    w.WriteEndArray();
                    w.WriteEndObject();
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();

            var flat = model.Nodes().ToList();
            var index = new Dictionary<SceneNode, int>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < flat.Count; i++)
            {
                index[flat[i]] = i;
            }

            var nodeTick = 0L;
            w.WriteStartArray("nodes");
            foreach (var node in flat)
            {
                Cancel.Every(options.Cancellation, nodeTick++, 255);
                w.WriteStartObject();
                if (!string.IsNullOrEmpty(node.Name))
                {
                    w.WriteString("name", node.Name);
                }

                if (node.Mesh is { } mesh && partIds.TryGetValue(mesh, out var part))
                {
                    w.WriteString("part", part);
                }

                if (node.Transform != Matrix4x4.Identity)
                {
                    WriteMatrix(w, node.Transform);
                }

                if (node.Layer >= 0 && node.Layer < model.Layers.Count)
                {
                    w.WriteString("layer", model.Layers[node.Layer].Name);
                }

                if (node.Properties.Count > 0)
                {
                    w.WriteStartObject("properties");
                    foreach (var (key, value) in node.Properties)
                    {
                        w.WriteString(key, value);
                    }

                    w.WriteEndObject();
                }

                if (node.Children.Count > 0)
                {
                    w.WriteStartArray("children");
                    foreach (var child in node.Children)
                    {
                        w.WriteNumberValue(index[child]);
                    }

                    w.WriteEndArray();
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("roots");
            foreach (var root in model.Roots)
            {
                w.WriteNumberValue(index[root]);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        var manifest = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        files[ManifestName] = stream.ToArray();
        return new AssemblyPackage(manifest, files);
    }

    private static void WriteMatrix(Utf8JsonWriter w, Matrix4x4 m)
    {
        w.WriteStartArray("matrix");
        foreach (var v in new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 })
        {
            w.WriteNumberValue(v);
        }

        w.WriteEndArray();
    }

    /// <summary>A model holding one mesh and only the materials it uses, so a part file stands alone.</summary>
    private static Model3D SinglePart(Model3D source, MeshData mesh)
    {
        var part = new Model3D();
        part.Metadata.Format = source.Metadata.Format;
        part.Metadata.Title = mesh.Name;
        var remap = new Dictionary<int, int>();
        var textures = new Dictionary<int, int>();
        var copy = new MeshData
        {
            Name = mesh.Name,
            Positions = mesh.Positions,
            Normals = mesh.Normals,
            Colors = mesh.Colors,
            TexCoords = mesh.TexCoords,
            Indices = mesh.Indices,
            Faces = mesh.Faces,
            EdgeIndices = mesh.EdgeIndices,
        };
        foreach (var group in mesh.Groups)
        {
            var material = -1;
            if (group.Material >= 0 && group.Material < source.Materials.Count)
            {
                if (!remap.TryGetValue(group.Material, out material))
                {
                    material = ModelMerge.CopyMaterial(part, source, group.Material, textures);
                    remap[group.Material] = material;
                }
            }

            copy.Groups.Add(new MeshGroup(group.Start, group.Count, material));
        }

        part.Meshes.Add(copy);
        part.Roots.Add(new SceneNode(mesh.Name) { Mesh = copy });
        return part;
    }

    /// <summary>Writes every file under <paramref name="directory"/>.</summary>
    public void WriteToDirectory(string directory)
    {
        foreach (var (name, data) in Files)
        {
            var path = Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, data);
        }
    }

    /// <summary>The package as a ZIP archive.</summary>
    public byte[] ToZip()
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in Files)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(data);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>Loads a package through <paramref name="read"/>, which returns the bytes of a relative path (or null if missing).</summary>
    public static Model3D Load(Func<string, byte[]?> read, LoadOptions? options = null)
    {
        options ??= new LoadOptions();
        try
        {
            var manifestBytes = read(ManifestName) ?? throw new ModelFormatException("The package has no assembly.json.");
            using var document = JsonDocument.Parse(manifestBytes, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            if (root.GetProperty("schema").GetString() != Schema)
            {
                throw new ModelFormatException("Unknown assembly package schema.");
            }

            var model = new Model3D();
            var partModels = new Dictionary<string, MeshData>(StringComparer.Ordinal);
            var materialMap = new Dictionary<(string, int), int>();
            var textureMap = new Dictionary<(string, int), int>();
            foreach (var part in root.GetProperty("parts").EnumerateArray())
            {
                options.Cancellation.ThrowIfCancellationRequested();
                var id = part.GetProperty("id").GetString()!;
                var file = part.GetProperty("file").GetString()!;
                if (file.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(file))
                {
                    throw new ModelFormatException("A part path leaves the package.");
                }

                var data = read(file) ?? throw new ModelFormatException($"The part file '{file}' is missing.");
                var loaded = ModelLoader.Load(data, file, options);
                var mesh = loaded.Meshes.FirstOrDefault() ?? throw new ModelFormatException($"The part file '{file}' holds no mesh.");
                // A quantised part file keeps its dequantisation in the node matrix; bake it into the vertices.
                if (loaded.Nodes().FirstOrDefault(n => ReferenceEquals(n.Mesh, mesh)) is { } holder)
                {
                    BakeNodeTransform(loaded, holder, mesh);
                }

                // Materials of the part file join the model's list.
                for (var g = 0; g < mesh.Groups.Count; g++)
                {
                    var group = mesh.Groups[g];
                    if (group.Material >= 0 && group.Material < loaded.Materials.Count)
                    {
                        if (!materialMap.TryGetValue((id, group.Material), out var mapped))
                        {
                            var partTextures = new Dictionary<int, int>();
                            foreach (var ((partId, texture), index) in textureMap.Where(t => t.Key.Item1 == id))
                            {
                                _ = partId;
                                partTextures[texture] = index;
                            }

                            mapped = ModelMerge.CopyMaterial(model, loaded, group.Material, partTextures);
                            foreach (var (texture, index) in partTextures)
                            {
                                textureMap[(id, texture)] = index;
                            }

                            materialMap[(id, group.Material)] = mapped;
                        }

                        mesh.Groups[g] = group with { Material = mapped };
                    }
                }

                mesh.Name = part.TryGetProperty("name", out var n) ? n.GetString() : mesh.Name;
                model.Meshes.Add(mesh);
                partModels[id] = mesh;
            }

            var nodes = new List<SceneNode>();
            var nodeElements = root.GetProperty("nodes").EnumerateArray().ToList();
            foreach (var element in nodeElements)
            {
                var node = new SceneNode(element.TryGetProperty("name", out var name) ? name.GetString() : null);
                if (element.TryGetProperty("part", out var partId) && partModels.TryGetValue(partId.GetString()!, out var mesh))
                {
                    node.Mesh = mesh;
                }

                if (element.TryGetProperty("matrix", out var matrix))
                {
                    var a = matrix.EnumerateArray().Select(e => e.GetSingle()).ToArray();
                    if (a.Length != 16)
                    {
                        throw new ModelFormatException("A node matrix does not have 16 values.");
                    }

                    node.Transform = new Matrix4x4(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8], a[9], a[10], a[11], a[12], a[13], a[14], a[15]);
                }

                if (element.TryGetProperty("properties", out var properties))
                {
                    foreach (var p in properties.EnumerateObject())
                    {
                        node.Properties[p.Name] = p.Value.GetString() ?? "";
                    }
                }

                nodes.Add(node);
            }

            for (var i = 0; i < nodeElements.Count; i++)
            {
                if (nodeElements[i].TryGetProperty("children", out var children))
                {
                    foreach (var child in children.EnumerateArray())
                    {
                        nodes[i].Children.Add(nodes[child.GetInt32()]);
                    }
                }
            }

            foreach (var r in root.GetProperty("roots").EnumerateArray())
            {
                model.Roots.Add(nodes[r.GetInt32()]);
            }

            ReadExtras(model, root);
            model.Metadata.HasGeometry = model.Meshes.Count > 0;
            model.Metadata.Statistics = model.ComputeStatistics();
            model.Metadata.Bounds = model.ComputeBounds();
            return model;
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException or JsonException or ArgumentException or IndexOutOfRangeException or InvalidDataException)
        {
            throw new ModelFormatException("The assembly package is damaged: " + ex.Message, ex);
        }
    }

    private static void BakeNodeTransform(Model3D loaded, SceneNode holder, MeshData mesh)
    {
        // World transform of the node that holds the mesh (parents first).
        Matrix4x4 Find(IEnumerable<SceneNode> nodes, Matrix4x4 parent, out bool found)
        {
            foreach (var n in nodes)
            {
                var world = n.Transform * parent;
                if (ReferenceEquals(n, holder))
                {
                    found = true;
                    return world;
                }

                var inner = Find(n.Children, world, out found);
                if (found)
                {
                    return inner;
                }
            }

            found = false;
            return Matrix4x4.Identity;
        }

        var m = Find(loaded.Roots, Matrix4x4.Identity, out _);
        if (m == Matrix4x4.Identity)
        {
            return;
        }

        for (var i = 0; i + 2 < mesh.Positions.Length; i += 3)
        {
            var p = Vector3.Transform(new Vector3(mesh.Positions[i], mesh.Positions[i + 1], mesh.Positions[i + 2]), m);
            mesh.Positions[i] = p.X;
            mesh.Positions[i + 1] = p.Y;
            mesh.Positions[i + 2] = p.Z;
        }

        if (mesh.Normals.Length == mesh.Positions.Length && Matrix4x4.Invert(m, out var inverse))
        {
            var normalMatrix = Matrix4x4.Transpose(inverse);
            for (var i = 0; i + 2 < mesh.Normals.Length; i += 3)
            {
                var n = Vector3.TransformNormal(new Vector3(mesh.Normals[i], mesh.Normals[i + 1], mesh.Normals[i + 2]), normalMatrix);
                n = n.LengthSquared() > 0 ? Vector3.Normalize(n) : n;
                mesh.Normals[i] = n.X;
                mesh.Normals[i + 1] = n.Y;
                mesh.Normals[i + 2] = n.Z;
            }
        }

        holder.Transform = Matrix4x4.Identity;
    }

    public static Model3D Load(string directory, LoadOptions? options = null) =>
        Load(path =>
        {
            var full = Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(full) ? File.ReadAllBytes(full) : null;
        }, options);

    public static Model3D LoadZip(ReadOnlyMemory<byte> zip, LoadOptions? options = null)
    {
        using var archive = new ZipArchive(new MemoryStream(zip.ToArray(), writable: false), ZipArchiveMode.Read);
        return Load(path =>
        {
            var entry = archive.GetEntry(path);
            if (entry is null)
            {
                return null;
            }

            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }, options);
    }

    private static void ReadExtras(Model3D model, JsonElement root)
    {
        if (!root.TryGetProperty("extras", out var extras))
        {
            return;
        }

        var meta = model.Metadata;
        meta.Format = ModelFormats.Gltf;
        string? Str(string name) => extras.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var formatId = Str("format");
        meta.Format = ModelFormats.All.FirstOrDefault(f => f.Id == formatId) ?? FormatInfo.Unknown;
        meta.DocumentKind = Str("documentKind");
        meta.Application = Str("application");
        meta.Title = Str("title");
        meta.Author = Str("author");
        meta.Company = Str("company");
        meta.LastModifiedBy = Str("lastModifiedBy");
        meta.Units = Enum.TryParse<LengthUnit>(Str("sourceUnits"), ignoreCase: true, out var unit) ? unit : null;
        meta.UpAxis = Enum.TryParse<UpAxis>(Str("sourceUpAxis"), ignoreCase: true, out var up) ? up : null;
        void List(string name, List<string> into)
        {
            if (extras.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array)
            {
                into.AddRange(a.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));
            }
        }

        List("partNames", meta.PartNames);
        List("configurations", meta.Configurations);
        List("materials", meta.MaterialNames);
        List("componentFiles", meta.ComponentFiles);
        if (extras.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in props.EnumerateArray())
            {
                meta.Properties.Add(new ModelProperty(p.GetProperty("name").GetString() ?? "", p.GetProperty("value").GetString() ?? "", p.TryGetProperty("group", out var g) ? g.GetString() : null));
            }
        }
    }
}
