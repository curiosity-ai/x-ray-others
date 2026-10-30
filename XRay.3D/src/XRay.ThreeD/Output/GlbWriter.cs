using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD;

public enum GlbInstancing
{
    Nodes,
    Gpu,
}

public enum GlbCompression
{
    None,
    Quantize,
    Meshopt,
}

/// <summary>What the writer does for meshes that carry no normals.</summary>
public enum GlbNormals
{
    /// <summary>Write none; the viewer computes its own (flat, usually).</summary>
    Keep,

    /// <summary>Area-weighted vertex normals over the welded vertices.</summary>
    Smooth,

    /// <summary>One normal per triangle; vertices are unwelded.</summary>
    Flat,
}

public enum EdgeMode
{
    None,
    Auto,
    All,
}

/// <summary>What <see cref="GlbWriter"/> does to the model on the way out.</summary>
public sealed class GlbWriteOptions
{
    /// <summary>Scale the scene to meters (glTF's unit) with a root transform when the model's units are known and are not meters.</summary>
    public bool ConvertToMeters { get; set; } = true;

    /// <summary>Rotate Z-up models to glTF's Y-up with a root transform.</summary>
    public bool ConvertToYUp { get; set; } = true;

    /// <summary>Write model metadata (<c>asset.extras</c>) and per-node properties (<c>extras</c>).</summary>
    public bool IncludeMetadata { get; set; } = true;

    /// <summary>
    /// <see cref="GlbCompression.Quantize"/> stores positions as 16-bit integers (with the dequantisation folded into the node
    /// matrices) and normals and colours as 8-bit integers (KHR_mesh_quantization); <see cref="GlbCompression.Meshopt"/> also
    /// compresses vertices and indices (EXT_meshopt_compression, required by the file). Viewers need the extensions: three.js loads both.
    /// </summary>
    public GlbCompression Compression { get; set; } = GlbCompression.None;

    /// <summary>
    /// <see cref="GlbInstancing.Gpu"/> merges sibling nodes that share a mesh into one node with EXT_mesh_gpu_instancing (one draw call
    /// for all of them); the instance names and properties move to the node's <c>extras</c>. Only for uncompressed output, and the
    /// file requires the extension. <see cref="GlbInstancing.Nodes"/> (the default) keeps one named node per instance.
    /// </summary>
    public GlbInstancing Instancing { get; set; } = GlbInstancing.Nodes;

    /// <summary>Smallest group of sibling instances that <see cref="GlbInstancing.Gpu"/> merges.</summary>
    public int MinGpuInstances { get; set; } = 4;

    /// <summary>Feature edges as a separate line primitive per mesh. <see cref="EdgeMode.Auto"/> draws them for meshes that have source faces (CAD).</summary>
    public EdgeMode Edges { get; set; } = EdgeMode.Auto;

    /// <summary>Crease angle in degrees for edges between triangles of one face (and of meshes without faces in <see cref="EdgeMode.All"/>).</summary>
    public float EdgeCreaseAngle { get; set; } = 30f;

    /// <summary>Embed the file's preview image as a texture-less extra (base64) in <c>asset.extras</c>. Off by default: it can be large.</summary>
    public bool IncludePreview { get; set; }

    /// <summary>Normals for triangle meshes that have none (CAD meshes with source faces are left alone). Default: none are written.</summary>
    public GlbNormals Normals { get; set; } = GlbNormals.Keep;

    /// <summary>
    /// A mesh whose vertices lie far from the origin compared with its size (a building at survey coordinates) loses float32 precision in
    /// a viewer. Such a mesh is written around its own bounds centre, the centre goes into the nodes that use it, and into
    /// <c>mesh.extras.origin</c> (source units). On by default; not combined with <see cref="GlbInstancing.Gpu"/> (whose instance
    /// transforms are written from the nodes).
    /// </summary>
    public bool RecentreFarModels { get; set; } = true;

    /// <summary>Checked between meshes, per node and every few thousand vertices; a cancelled write throws <see cref="OperationCanceledException"/>.</summary>
    public CancellationToken Cancellation { get; set; }

    internal GlbWriteOptions Clone() => (GlbWriteOptions)MemberwiseClone();
}

/// <summary>
/// Writes a <see cref="Model3D"/> as binary glTF 2.0: the node tree with names, shared meshes for instanced parts,
/// one primitive per material group, node properties and model metadata in <c>extras</c>.
/// </summary>
public static class GlbWriter
{
    private const uint Magic = 0x46546C67;
    private const uint JsonChunk = 0x4E4F534A;
    private const uint BinChunk = 0x004E4942;

    public static byte[] ToArray(Model3D model, GlbWriteOptions? options = null)
    {
        using var stream = new MemoryStream();
        Write(model, stream, options);
        return stream.ToArray();
    }

    public static void Write(Model3D model, Stream output, GlbWriteOptions? options = null)
    {
        options ??= new GlbWriteOptions();
        var bin = new MemoryStream();
        var json = new MemoryStream();
        using (var w = new Utf8JsonWriter(json))
        {
            WriteJson(model, options, w, bin);
        }

        var jsonBytes = Pad(json.ToArray(), (byte)' ');
        var binBytes = Pad(bin.ToArray(), 0);
        var total = 12 + 8 + jsonBytes.Length + (binBytes.Length > 0 ? 8 + binBytes.Length : 0);
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], (uint)total);
        output.Write(header);
        WriteChunk(output, JsonChunk, jsonBytes);
        if (binBytes.Length > 0)
        {
            WriteChunk(output, BinChunk, binBytes);
        }
    }

    private static void WriteChunk(Stream output, uint type, byte[] data)
    {
        Span<byte> header = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], type);
        output.Write(header);
        output.Write(data);
    }

    private static byte[] Pad(byte[] data, byte fill)
    {
        var padded = (data.Length + 3) & ~3;
        if (padded == data.Length)
        {
            return data;
        }

        var result = new byte[padded];
        data.CopyTo(result, 0);
        Array.Fill(result, fill, data.Length, padded - data.Length);
        return result;
    }

    private sealed record Compressed(int Offset, int Length, int Count, string Mode, string? Filter, int Stride);

    private sealed class View
    {
        public int Offset;
        public int Length;
        public int Target;
        public int Stride;
        public Compressed? Compression;
    }

    private sealed class Accessor
    {
        public int View;
        public int ComponentType;
        public int Count;
        public string Type = "";
        public bool Normalized;
        public float[]? Min;
        public float[]? Max;
    }

    private sealed class Accessors
    {
        public readonly List<Accessor> List = new();
        public CancellationToken Cancellation;
        public readonly List<View> Views = new();
        public long FallbackSize;
        public bool AnyCompressed;
        public bool AnyQuantized;
        public readonly Dictionary<MeshData, Matrix4x4> Dequantise = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<MeshData, Vector3> Origin = new(ReferenceEqualityComparer.Instance);
    }

    private sealed record SyntheticNode(int Translation, int Rotation, int Scale, List<SceneNode> Sources);

    private sealed class InstancePlan
    {
        public readonly Dictionary<SceneNode, List<SceneNode>> Children = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<SceneNode, SyntheticNode> Synthetic = new(ReferenceEqualityComparer.Instance);
        public List<SceneNode> Roots = new();
    }

    private static InstancePlan PlanInstancing(Model3D model, Accessors accessors, MemoryStream bin, GlbWriteOptions options)
    {
        var plan = new InstancePlan();

        List<SceneNode> Process(IReadOnlyList<SceneNode> siblings)
        {
            var result = new List<SceneNode>();
            var byMesh = new Dictionary<MeshData, List<SceneNode>>(ReferenceEqualityComparer.Instance);
            foreach (var node in siblings)
            {
                if (node.Mesh is not null && node.Children.Count == 0)
                {
                    if (!byMesh.TryGetValue(node.Mesh, out var list))
                    {
                        byMesh[node.Mesh] = list = new List<SceneNode>();
                    }

                    list.Add(node);
                }
            }

            var merged = new Dictionary<MeshData, SceneNode>(ReferenceEqualityComparer.Instance);
            foreach (var node in siblings)
            {
                if (node.Mesh is not null && node.Children.Count == 0 && byMesh[node.Mesh] is { } members
                    && members.Count >= Math.Max(2, options.MinGpuInstances) && TryDecompose(members, out var trs))
                {
                    if (merged.ContainsKey(node.Mesh))
                    {
                        continue;
                    }

                    var synthetic = new SceneNode((node.Mesh.Name ?? node.Name) + " (instances)") { Mesh = node.Mesh };
                    var t = new float[members.Count * 3];
                    var r = new float[members.Count * 4];
                    var sc = new float[members.Count * 3];
                    for (var i = 0; i < members.Count; i++)
                    {
                        trs[i].Translation.CopyTo(t.AsSpan(3 * i));
                        r[4 * i] = trs[i].Rotation.X;
                        r[4 * i + 1] = trs[i].Rotation.Y;
                        r[4 * i + 2] = trs[i].Rotation.Z;
                        r[4 * i + 3] = trs[i].Rotation.W;
                        trs[i].Scale.CopyTo(sc.AsSpan(3 * i));
                    }

                    var ta = AddAccessor(accessors, bin, MemoryMarshal.AsBytes(t.AsSpan()), 5126, members.Count, "VEC3", 0, null, null);
                    var ra = AddAccessor(accessors, bin, MemoryMarshal.AsBytes(r.AsSpan()), 5126, members.Count, "VEC4", 0, null, null);
                    var sa = AddAccessor(accessors, bin, MemoryMarshal.AsBytes(sc.AsSpan()), 5126, members.Count, "VEC3", 0, null, null);
                    plan.Synthetic[synthetic] = new SyntheticNode(ta, ra, sa, members);
                    merged[node.Mesh] = synthetic;
                    result.Add(synthetic);
                }
                else
                {
                    result.Add(node);
                    plan.Children[node] = Process(node.Children);
                }
            }

            return result;
        }

        plan.Roots = Process(model.Roots);
        return plan;
    }

    private static bool TryDecompose(List<SceneNode> members, out (Vector3 Translation, Quaternion Rotation, Vector3 Scale)[] result)
    {
        result = new (Vector3, Quaternion, Vector3)[members.Count];
        for (var i = 0; i < members.Count; i++)
        {
            var m = members[i].Transform;
            if (!Matrix4x4.Decompose(m, out var scale, out var rotation, out var translation))
            {
                return false;
            }

            var back = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
            var limit = 1e-4f * Math.Max(1f, Math.Max(MathF.Abs(m.M41), Math.Max(MathF.Abs(m.M42), MathF.Abs(m.M43))));
            if (!(Matrix4x4.Equals(back, m) || MaxDifference(back, m) <= limit))
            {
                return false;
            }

            result[i] = (translation, rotation, scale);
        }

        return true;
    }

    private static float MaxDifference(Matrix4x4 a, Matrix4x4 b) =>
        new[] { a.M11 - b.M11, a.M12 - b.M12, a.M13 - b.M13, a.M21 - b.M21, a.M22 - b.M22, a.M23 - b.M23, a.M31 - b.M31, a.M32 - b.M32, a.M33 - b.M33, a.M41 - b.M41, a.M42 - b.M42, a.M43 - b.M43 }
            .Max(MathF.Abs);

    private static void WriteJson(Model3D model, GlbWriteOptions options, Utf8JsonWriter w, MemoryStream bin)
    {
        var accessors = new Accessors { Cancellation = options.Cancellation };
        var meshPrimitives = new List<List<(int Position, int Normal, int Color, int TexCoord, int Indices, int Material, bool IsEdges)>>();

        foreach (var mesh in model.Meshes)
        {
            options.Cancellation.ThrowIfCancellationRequested();
            meshPrimitives.Add(AddMesh(mesh, accessors, bin, model.Materials.Count, options));
        }

        var textures = AddImages(model, accessors, bin);
        var plan = options.Instancing == GlbInstancing.Gpu && options.Compression == GlbCompression.None ? PlanInstancing(model, accessors, bin, options) : null;
        var anyDefaultMaterial = model.Meshes.Any(m => m.Groups.Count == 0 || m.Groups.Any(g => g.Material < 0));
        var materialCount = model.Materials.Count + (anyDefaultMaterial ? 1 : 0);

        w.WriteStartObject();
        w.WriteStartObject("asset");
        w.WriteString("version", "2.0");
        w.WriteString("generator", "X-Ray.3D");
        if (options.IncludeMetadata)
        {
            WriteModelExtras(w, model, options);
        }

        w.WriteEndObject();

        var extensionsUsed = new List<string>();
        var extensionsRequired = new List<string>();
        if (plan is { Synthetic.Count: > 0 })
        {
            extensionsUsed.Add("EXT_mesh_gpu_instancing");
            extensionsRequired.Add("EXT_mesh_gpu_instancing");
        }

        if (accessors.AnyQuantized)
        {
            extensionsUsed.Add("KHR_mesh_quantization");
            extensionsRequired.Add("KHR_mesh_quantization");
            if (accessors.AnyCompressed)
            {
                extensionsUsed.Add("EXT_meshopt_compression");
                extensionsRequired.Add("EXT_meshopt_compression");
            }
        }

        if (textures.AnyTransform)
        {
            extensionsUsed.Add("KHR_texture_transform");
        }

        if (extensionsUsed.Count > 0)
        {
            w.WriteStartArray("extensionsUsed");
            extensionsUsed.ForEach(w.WriteStringValue);
            w.WriteEndArray();
        }

        if (extensionsRequired.Count > 0)
        {
            w.WriteStartArray("extensionsRequired");
            extensionsRequired.ForEach(w.WriteStringValue);
            w.WriteEndArray();
        }

        var (rootScale, rootRotation, wrap) = RootTransform(model, options);
        var flat = new List<SceneNode>();
        var indexOf = new Dictionary<SceneNode, int>(ReferenceEqualityComparer.Instance);
        void Collect(SceneNode node)
        {
            indexOf[node] = flat.Count;
            flat.Add(node);
            foreach (var child in ChildrenOf(node))
            {
                Collect(child);
            }
        }

        IReadOnlyList<SceneNode> ChildrenOf(SceneNode node) => plan is not null && plan.Children.TryGetValue(node, out var c) ? c : node.Children;
        IReadOnlyList<SceneNode> roots = plan?.Roots ?? model.Roots;
        foreach (var root in roots)
        {
            Collect(root);
        }

        // A node whose mesh is stored in its own frame (dequantised, or recentred) and that also has children cannot carry that frame in its own
        // matrix: the children would inherit it. Its mesh moves to an extra child node holding the frame; the extra nodes follow the root wrapper.
        var meshChild = new Dictionary<SceneNode, int>(ReferenceEqualityComparer.Instance);
        var meshChildOrder = new List<SceneNode>();
        foreach (var node in flat)
        {
            if (node.Mesh is { } own && ChildrenOf(node).Count > 0 && (accessors.Dequantise.ContainsKey(own) || accessors.Origin.ContainsKey(own)) && !(plan is not null && plan.Synthetic.ContainsKey(node)))
            {
                meshChild[node] = flat.Count + (wrap ? 1 : 0) + meshChildOrder.Count;
                meshChildOrder.Add(node);
            }
        }

        w.WriteNumber("scene", 0);
        w.WriteStartArray("scenes");
        w.WriteStartObject();
        w.WriteStartArray("nodes");
        if (wrap)
        {
            w.WriteNumberValue(flat.Count);
        }
        else
        {
            foreach (var root in roots)
            {
                w.WriteNumberValue(indexOf[root]);
            }
        }

        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndArray();

        var nodeTick = 0L;
        w.WriteStartArray("nodes");
        foreach (var node in flat)
        {
            Cancel.Every(options.Cancellation, nodeTick++, 255);
            WriteNode(w, node, indexOf, model, options, accessors, plan, meshChild.TryGetValue(node, out var holder) ? holder : -1);
        }

        if (wrap)
        {
            w.WriteStartObject();
            w.WriteString("name", "root");
            var scale = (float)rootScale;
            if (rootScale != 1)
            {
                w.WriteStartArray("scale");
                w.WriteNumberValue(scale);
                w.WriteNumberValue(scale);
                w.WriteNumberValue(scale);
                w.WriteEndArray();
            }

            if (rootRotation != Quaternion.Identity)
            {
                w.WriteStartArray("rotation");
                w.WriteNumberValue(rootRotation.X);
                w.WriteNumberValue(rootRotation.Y);
                w.WriteNumberValue(rootRotation.Z);
                w.WriteNumberValue(rootRotation.W);
                w.WriteEndArray();
            }

            w.WriteStartArray("children");
            foreach (var root in roots)
            {
                w.WriteNumberValue(indexOf[root]);
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        foreach (var node in meshChildOrder)
        {
            WriteMeshHolder(w, node, model, options, accessors);
        }

        w.WriteEndArray();

        if (model.Meshes.Count > 0)
        {
            w.WriteStartArray("meshes");
            for (var i = 0; i < model.Meshes.Count; i++)
            {
                w.WriteStartObject();
                if (model.Meshes[i].Name is { } name)
                {
                    w.WriteString("name", name);
                }

                var hasOrigin = accessors.Origin.TryGetValue(model.Meshes[i], out var meshOrigin);
                var topology = options.IncludeMetadata && model.Meshes[i].Indices.Length > 0 ? MeshTopology.Analyze(model.Meshes[i], MeshTopology.DefaultWeldTolerance, options.Cancellation) : default;
                if (options.IncludeMetadata && (model.Meshes[i].Faces.Length > 0 || hasOrigin || topology.Triangles > 0))
                {
                    w.WriteStartObject("extras");
                    if (topology.Triangles > 0)
                    {
                        // The library's verdict on the whole mesh (all its primitives together): a closed solid with two materials is two
                        // primitives, each open on its own. Edges are welded by position at 1e-5 of the bounding box diagonal.
                        w.WriteBoolean("closed", topology.IsClosed);
                        w.WriteNumber("openEdges", topology.OpenEdges);
                        w.WriteNumber("nonManifoldEdges", topology.NonManifoldEdges);
                        w.WriteNumber("inconsistentEdges", topology.InconsistentEdges);
                        w.WriteBoolean("insideOut", topology.IsInsideOut);
                    }

                    if (hasOrigin)
                    {
                        // The vertices are stored around this point (source units); the nodes that use the mesh carry it as a translation.
                        w.WriteStartArray("origin");
                        w.WriteNumberValue(meshOrigin.X);
                        w.WriteNumberValue(meshOrigin.Y);
                        w.WriteNumberValue(meshOrigin.Z);
                        w.WriteEndArray();
                    }

                    if (model.Meshes[i].Faces.Length > 0)
                    {
                        // Source faces as (first index, index count) pairs over the mesh's triangle primitives, in order.
                        w.WriteNumber("faceCount", model.Meshes[i].Faces.Length);
                        w.WriteStartArray("faceRanges");
                        foreach (var face in model.Meshes[i].Faces)
                        {
                            w.WriteNumberValue(face.FirstIndex);
                            w.WriteNumberValue(face.IndexCount);
                        }

                        w.WriteEndArray();
                    }

                    w.WriteEndObject();
                }

                w.WriteStartArray("primitives");
                foreach (var (position, normal, color, texCoord, indices, material, isEdges) in meshPrimitives[i])
                {
                    w.WriteStartObject();
                    w.WriteStartObject("attributes");
                    w.WriteNumber("POSITION", position);
                    if (normal >= 0)
                    {
                        w.WriteNumber("NORMAL", normal);
                    }

                    if (color >= 0)
                    {
                        w.WriteNumber("COLOR_0", color);
                    }

                    if (texCoord >= 0)
                    {
                        w.WriteNumber("TEXCOORD_0", texCoord);
                    }

                    w.WriteEndObject();
                    if (indices >= 0)
                    {
                        w.WriteNumber("indices", indices);
                    }
                    else if (!isEdges)
                    {
                        w.WriteNumber("mode", 0);
                    }

                    if (isEdges)
                    {
                        w.WriteNumber("mode", 1);
                        if (material >= 0)
                        {
                            w.WriteNumber("material", material);
                        }

                        w.WriteStartObject("extras");
                        w.WriteString("role", model.Meshes[i].Indices.Length == 0 ? "lines" : "edges");
                        w.WriteEndObject();
                    }
                    else
                    {
                        w.WriteNumber("material", material < 0 ? model.Materials.Count : material);
                    }

                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        if (materialCount > 0)
        {
            w.WriteStartArray("materials");
            foreach (var m in model.Materials)
            {
                WriteMaterial(w, m, textures);
            }

            if (anyDefaultMaterial)
            {
                WriteMaterial(w, new ModelMaterial { Name = "default" }, textures);
            }

            w.WriteEndArray();
        }

        WriteImages(w, textures);

        if (accessors.Views.Count > 0)
        {
            if (accessors.List.Count > 0)
            {
                w.WriteStartArray("accessors");
                foreach (var a in accessors.List)
                {
                    w.WriteStartObject();
                    w.WriteNumber("bufferView", a.View);
                    w.WriteNumber("componentType", a.ComponentType);
                    if (a.Normalized)
                    {
                        w.WriteBoolean("normalized", true);
                    }

                    w.WriteNumber("count", a.Count);
                    w.WriteString("type", a.Type);
                    if (a.Min is { } min && a.Max is { } max)
                    {
                        w.WriteStartArray("min");
                        foreach (var v in min)
                        {
                            w.WriteNumberValue(v);
                        }

                        w.WriteEndArray();
                        w.WriteStartArray("max");
                        foreach (var v in max)
                        {
                            w.WriteNumberValue(v);
                        }

                        w.WriteEndArray();
                    }

                    w.WriteEndObject();
                }

                w.WriteEndArray();
            }

            w.WriteStartArray("bufferViews");
            foreach (var v in accessors.Views)
            {
                w.WriteStartObject();
                w.WriteNumber("buffer", v.Compression is null ? 0 : 1);
                w.WriteNumber("byteOffset", v.Offset);
                w.WriteNumber("byteLength", v.Length);
                if (v.Stride > 0)
                {
                    w.WriteNumber("byteStride", v.Stride);
                }

                if (v.Target != 0)
                {
                    w.WriteNumber("target", v.Target);
                }

                if (v.Compression is { } c)
                {
                    w.WriteStartObject("extensions");
                    w.WriteStartObject("EXT_meshopt_compression");
                    w.WriteNumber("buffer", 0);
                    w.WriteNumber("byteOffset", c.Offset);
                    w.WriteNumber("byteLength", c.Length);
                    w.WriteNumber("byteStride", c.Stride);
                    w.WriteNumber("count", c.Count);
                    w.WriteString("mode", c.Mode);
                    if (c.Filter is not null)
                    {
                        w.WriteString("filter", c.Filter);
                    }

                    w.WriteEndObject();
                    w.WriteEndObject();
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("buffers");
            w.WriteStartObject();
            w.WriteNumber("byteLength", (bin.Length + 3) & ~3L);
            w.WriteEndObject();
            if (accessors.AnyCompressed)
            {
                // No uncompressed copy is stored; this placeholder only gives the compressed views a valid parent buffer.
                w.WriteStartObject();
                w.WriteNumber("byteLength", accessors.FallbackSize);
                w.WriteStartObject("extensions");
                w.WriteStartObject("EXT_meshopt_compression");
                w.WriteBoolean("fallback", true);
                w.WriteEndObject();
                w.WriteEndObject();
                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        w.WriteEndObject();
    }

    internal static (double Scale, Quaternion Rotation, bool Wrap) RootTransform(Model3D model, GlbWriteOptions options)
    {
        var scale = 1.0;
        if (options.ConvertToMeters && model.Metadata.Units is { } unit)
        {
            scale = unit.ToMeters();
        }

        var rotation = Quaternion.Identity;
        if (options.ConvertToYUp && model.Metadata.UpAxis == UpAxis.Z)
        {
            rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2);
        }

        return (scale, rotation, scale != 1 || rotation != Quaternion.Identity);
    }

    private sealed class TextureTable
    {
        public readonly Dictionary<int, int> Map = new();
        public readonly List<(ModelTexture Texture, int View)> Images = new();
        public bool AnyTransform;
    }

    /// <summary>Stores the images that materials use (each once) as buffer views and numbers them for the <c>textures</c> array.</summary>
    private static TextureTable AddImages(Model3D model, Accessors accessors, MemoryStream bin)
    {
        var table = new TextureTable();
        foreach (var material in model.Materials)
        {
            if (material.BaseColorTexture is not { } reference || reference.Texture < 0 || reference.Texture >= model.Textures.Count)
            {
                continue;
            }

            var texture = model.Textures[reference.Texture];
            if (texture.Data.Length == 0 || texture.MediaType is not ("image/png" or "image/jpeg" or "image/webp"))
            {
                continue;
            }

            if (!table.Map.ContainsKey(reference.Texture))
            {
                table.Map[reference.Texture] = table.Images.Count;
                table.Images.Add((texture, AddPlainView(accessors, bin, texture.Data, 0, 0)));
            }

            table.AnyTransform |= reference.HasTransform;
        }

        return table;
    }

    private static int WrapCode(TextureWrap wrap) => wrap switch
    {
        TextureWrap.ClampToEdge => 33071,
        TextureWrap.MirroredRepeat => 33648,
        _ => 10497,
    };

    private static void WriteImages(Utf8JsonWriter w, TextureTable table)
    {
        if (table.Images.Count == 0)
        {
            return;
        }

        var samplers = new List<(TextureWrap U, TextureWrap V, bool Nearest)>();
        w.WriteStartArray("images");
        foreach (var (texture, view) in table.Images)
        {
            w.WriteStartObject();
            if (!string.IsNullOrEmpty(texture.Name))
            {
                w.WriteString("name", texture.Name);
            }

            w.WriteNumber("bufferView", view);
            w.WriteString("mimeType", texture.MediaType);
            w.WriteEndObject();
            var key = (texture.WrapU, texture.WrapV, texture.Nearest);
            if (!samplers.Contains(key))
            {
                samplers.Add(key);
            }
        }

        w.WriteEndArray();
        w.WriteStartArray("samplers");
        foreach (var (u, v, nearest) in samplers)
        {
            w.WriteStartObject();
            w.WriteNumber("magFilter", nearest ? 9728 : 9729);
            w.WriteNumber("minFilter", nearest ? 9984 : 9987);
            w.WriteNumber("wrapS", WrapCode(u));
            w.WriteNumber("wrapT", WrapCode(v));
            w.WriteEndObject();
        }

        w.WriteEndArray();
        w.WriteStartArray("textures");
        for (var i = 0; i < table.Images.Count; i++)
        {
            var t = table.Images[i].Texture;
            w.WriteStartObject();
            w.WriteNumber("sampler", samplers.IndexOf((t.WrapU, t.WrapV, t.Nearest)));
            w.WriteNumber("source", i);
            w.WriteEndObject();
        }

        w.WriteEndArray();
    }

    private static void WriteMaterial(Utf8JsonWriter w, ModelMaterial m, TextureTable textures)
    {
        w.WriteStartObject();
        if (m.Name is not null)
        {
            w.WriteString("name", m.Name);
        }

        w.WriteStartObject("pbrMetallicRoughness");
        w.WriteStartArray("baseColorFactor");
        w.WriteNumberValue(Srgb(Unit(m.BaseColor.X)));
        w.WriteNumberValue(Srgb(Unit(m.BaseColor.Y)));
        w.WriteNumberValue(Srgb(Unit(m.BaseColor.Z)));
        w.WriteNumberValue(Unit(m.BaseColor.W));
        w.WriteEndArray();
        w.WriteNumber("metallicFactor", Unit(m.Metallic));
        w.WriteNumber("roughnessFactor", Unit(m.Roughness));
        if (m.BaseColorTexture is { } reference && textures.Map.TryGetValue(reference.Texture, out var textureIndex))
        {
            w.WriteStartObject("baseColorTexture");
            w.WriteNumber("index", textureIndex);
            if (reference.HasTransform)
            {
                w.WriteStartObject("extensions");
                w.WriteStartObject("KHR_texture_transform");
                if (reference.Offset != Vector2.Zero)
                {
                    w.WriteStartArray("offset");
                    w.WriteNumberValue(reference.Offset.X);
                    w.WriteNumberValue(reference.Offset.Y);
                    w.WriteEndArray();
                }

                if (reference.Rotation != 0)
                {
                    w.WriteNumber("rotation", reference.Rotation);
                }

                if (reference.Scale != Vector2.One)
                {
                    w.WriteStartArray("scale");
                    w.WriteNumberValue(reference.Scale.X);
                    w.WriteNumberValue(reference.Scale.Y);
                    w.WriteEndArray();
                }

                w.WriteEndObject();
                w.WriteEndObject();
            }

            w.WriteEndObject();
        }

        w.WriteEndObject();
        w.WriteBoolean("doubleSided", true);
        if (m.BaseColor.W < 1)
        {
            w.WriteString("alphaMode", "BLEND");
        }

        w.WriteEndObject();
    }

    /// <summary>glTF base colours are linear; model colours are sRGB.</summary>
    /// <summary>A factor glTF accepts: finite and within 0..1.</summary>
    private static float Unit(float v) => float.IsFinite(v) ? Math.Clamp(v, 0f, 1f) : 0f;

    private static float Srgb(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    private static void WriteNode(Utf8JsonWriter w, SceneNode node, Dictionary<SceneNode, int> indexOf, Model3D model, GlbWriteOptions options, Accessors accessors, InstancePlan? plan, int meshChild)
    {
        w.WriteStartObject();
        if (!string.IsNullOrEmpty(node.Name))
        {
            w.WriteString("name", node.Name);
        }

        if (node.Mesh is { } mesh && meshChild < 0)
        {
            w.WriteNumber("mesh", model.Meshes.IndexOf(mesh));
        }

        var children = plan is not null && plan.Children.TryGetValue(node, out var planned) ? planned : node.Children;
        if (children.Count > 0 || meshChild >= 0)
        {
            w.WriteStartArray("children");
            foreach (var child in children)
            {
                w.WriteNumberValue(indexOf[child]);
            }

            if (meshChild >= 0)
            {
                w.WriteNumberValue(meshChild);
            }

            w.WriteEndArray();
        }

        // With the mesh on its own child node, this node keeps only its own transform: the children must not inherit the mesh's frame.
        var transform = meshChild >= 0 ? node.Transform : MeshFrame(node, accessors) * node.Transform;
        if (transform != Matrix4x4.Identity)
        {
            WriteMatrix(w, transform);
        }

        if (plan is not null && plan.Synthetic.TryGetValue(node, out var synthetic))
        {
            w.WriteStartObject("extensions");
            w.WriteStartObject("EXT_mesh_gpu_instancing");
            w.WriteStartObject("attributes");
            w.WriteNumber("TRANSLATION", synthetic.Translation);
            w.WriteNumber("ROTATION", synthetic.Rotation);
            w.WriteNumber("SCALE", synthetic.Scale);
            w.WriteEndObject();
            w.WriteEndObject();
            w.WriteEndObject();
            if (options.IncludeMetadata)
            {
                // What the merged nodes carried: instance i of the extension is entry i here.
                w.WriteStartObject("extras");
                w.WriteStartArray("instances");
                foreach (var source in synthetic.Sources)
                {
                    w.WriteStartObject();
                    if (!string.IsNullOrEmpty(source.Name))
                    {
                        w.WriteString("name", source.Name);
                    }

                    foreach (var (key, value) in source.Properties)
                    {
                        w.WriteString(key, value);
                    }

                    w.WriteEndObject();
                }

                w.WriteEndArray();
                w.WriteEndObject();
            }
        }
        else
        {
            WriteNodeExtras(w, node, model, options);
        }

        w.WriteEndObject();
    }

    /// <summary>The node's mesh sits in its own frame (dequantisation, then recentring); this maps it back to the model's.</summary>
    private static Matrix4x4 MeshFrame(SceneNode node, Accessors accessors)
    {
        var frame = Matrix4x4.Identity;
        if (node.Mesh is null)
        {
            return frame;
        }

        // Quantised positions are integers in [0, 65535]: the dequantisation is applied first, then the recentring shift.
        if (accessors.Dequantise.TryGetValue(node.Mesh, out var dequantise))
        {
            frame = dequantise;
        }

        if (accessors.Origin.TryGetValue(node.Mesh, out var origin))
        {
            frame *= Matrix4x4.CreateTranslation(origin);
        }

        return frame;
    }

    private static void WriteMatrix(Utf8JsonWriter w, Matrix4x4 m)
    {
        w.WriteStartArray("matrix");
        Span<float> values = [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];
        foreach (var v in values)
        {
            w.WriteNumberValue(v);
        }

        w.WriteEndArray();
    }

    private static void WriteNodeExtras(Utf8JsonWriter w, SceneNode node, Model3D model, GlbWriteOptions options)
    {
        if (options.IncludeMetadata && (node.Properties.Count > 0 || node.Layer >= 0))
        {
            w.WriteStartObject("extras");
            foreach (var (key, value) in node.Properties)
            {
                w.WriteString(key, value);
            }

            if (node.Layer >= 0 && node.Layer < model.Layers.Count)
            {
                w.WriteString("layer", model.Layers[node.Layer].Name);
            }

            w.WriteEndObject();
        }
    }

    /// <summary>The extra child of a node that has both a mesh in its own frame and children: it carries the mesh and the frame, and repeats the node's name and extras so a pick on it finds the same part.</summary>
    private static void WriteMeshHolder(Utf8JsonWriter w, SceneNode node, Model3D model, GlbWriteOptions options, Accessors accessors)
    {
        w.WriteStartObject();
        if (!string.IsNullOrEmpty(node.Name))
        {
            w.WriteString("name", node.Name);
        }

        w.WriteNumber("mesh", model.Meshes.IndexOf(node.Mesh!));
        var frame = MeshFrame(node, accessors);
        if (frame != Matrix4x4.Identity)
        {
            WriteMatrix(w, frame);
        }

        WriteNodeExtras(w, node, model, options);
        w.WriteEndObject();
    }

    internal static void WriteModelExtras(Utf8JsonWriter w, Model3D model, GlbWriteOptions options, string propertyName = "extras")
    {
        var meta = model.Metadata;
        w.WriteStartObject(propertyName);
        w.WriteString("format", meta.Format.Id);
        WriteIfSet(w, "formatName", meta.Format.Name);
        WriteIfSet(w, "documentKind", meta.DocumentKind);
        WriteIfSet(w, "formatVersion", meta.FormatVersion);
        WriteIfSet(w, "application", meta.Application);
        WriteIfSet(w, "title", meta.Title);
        WriteIfSet(w, "author", meta.Author);
        WriteIfSet(w, "company", meta.Company);
        WriteIfSet(w, "created", meta.Created?.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        WriteIfSet(w, "modified", meta.Modified?.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        WriteIfSet(w, "lastModifiedBy", meta.LastModifiedBy);
        if (meta.Origin is { Length: 3 } origin)
        {
            w.WriteStartArray("origin");
            foreach (var v in origin)
            {
                w.WriteNumberValue(v);
            }

            w.WriteEndArray();
        }

        if (meta.Units is { } unit)
        {
            w.WriteString("sourceUnits", unit.ToString().ToLowerInvariant());
        }

        if (meta.UpAxis is { } up)
        {
            w.WriteString("sourceUpAxis", up.ToString());
        }

        WriteList(w, "partNames", meta.PartNames);
        WriteList(w, "layers", model.Layers.Select(l => l.Name).Concat(meta.LayerNames).Distinct().ToList());
        WriteList(w, "configurations", meta.Configurations);
        WriteList(w, "materials", meta.MaterialNames);
        WriteList(w, "componentFiles", meta.ComponentFiles);
        if (meta.Properties.Count > 0)
        {
            w.WriteStartArray("properties");
            foreach (var p in meta.Properties)
            {
                w.WriteStartObject();
                w.WriteString("name", p.Name);
                w.WriteString("value", p.Value);
                if (p.Group is not null)
                {
                    w.WriteString("group", p.Group);
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
        }

        if (options.IncludePreview && model.Preview is { } preview)
        {
            w.WriteString("previewMediaType", preview.MediaType);
            w.WriteString("previewBase64", Convert.ToBase64String(preview.Data));
        }

        w.WriteEndObject();
    }

    private static void WriteIfSet(Utf8JsonWriter w, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            w.WriteString(name, value);
        }
    }

    private static void WriteList(Utf8JsonWriter w, string name, IReadOnlyCollection<string> values)
    {
        if (values.Count == 0)
        {
            return;
        }

        w.WriteStartArray(name);
        foreach (var v in values)
        {
            w.WriteStringValue(v);
        }

        w.WriteEndArray();
    }

    private static List<(int Position, int Normal, int Color, int TexCoord, int Indices, int Material, bool IsEdges)> AddMesh(MeshData source, Accessors accessors, MemoryStream bin, int materialCount, GlbWriteOptions options)
    {
        var mesh = MeshNormals.Apply(source, options.Normals, options.Cancellation);
        var primitives = new List<(int, int, int, int, int, int, bool)>();
        if (options.RecentreFarModels && !(options.Instancing == GlbInstancing.Gpu && options.Compression == GlbCompression.None) && mesh.VertexCount > 0)
        {
            var box = mesh.ComputeBounds();
            var size = Math.Max(Math.Max(box.Size.X, box.Size.Y), box.Size.Z);
            var centre = box.Center;
            var far = Math.Max(Math.Abs(centre.X), Math.Max(Math.Abs(centre.Y), Math.Abs(centre.Z)));
            if (float.IsFinite(far) && size > 0 && far > 1000f * size)
            {
                // Positions in float64 minus the centre, back to float32: the shift is exact enough and the residual is small.
                var shifted = new float[mesh.Positions.Length];
                for (var i = 0; i + 2 < shifted.Length; i += 3)
                {
                    shifted[i] = (float)((double)mesh.Positions[i] - centre.X);
                    shifted[i + 1] = (float)((double)mesh.Positions[i + 1] - centre.Y);
                    shifted[i + 2] = (float)((double)mesh.Positions[i + 2] - centre.Z);
                }

                mesh = ShiftedCopy(mesh, shifted);
                accessors.Origin[source] = centre;
            }
        }

        if (mesh.VertexCount == 0)
        {
            return primitives;
        }

        var compact = options.Compression != GlbCompression.None;
        var compress = options.Compression == GlbCompression.Meshopt;
        var positions = mesh.Positions;
        var normals = mesh.Normals.Length == mesh.Positions.Length ? mesh.Normals : [];
        var colors = mesh.Colors.Length == mesh.VertexCount * 4 ? mesh.Colors : [];
        var texCoords = mesh.TexCoords.Length == mesh.VertexCount * 2 ? mesh.TexCoords : [];
        var indices = mesh.Indices;
        var edges = EdgesFor(mesh, options);
        var vertexCount = mesh.VertexCount;

        if (compact && indices.Length > 0)
        {
            // Number the vertices in order of first use: the index codec relies on it, and unused vertices are dropped.
            (positions, normals, colors, texCoords, indices, edges) = ReorderByFirstUse(mesh, normals, colors, texCoords, edges);
            vertexCount = positions.Length / 3;
        }

        int position;
        int normal = -1;
        int color = -1;
        int texCoord = -1;
        if (compact)
        {
            var box = BoundsOf(positions);
            var scale = new Vector3(
                box.Size.X > 0 ? box.Size.X / 65535f : 1f,
                box.Size.Y > 0 ? box.Size.Y / 65535f : 1f,
                box.Size.Z > 0 ? box.Size.Z / 65535f : 1f);
            var packed = new byte[vertexCount * 8];
            var qmin = new float[] { 65535, 65535, 65535 };
            var qmax = new float[] { 0, 0, 0 };
            for (var v = 0; v < vertexCount; v++)
            {
                Cancel.Every(options.Cancellation, v, 4095);
                for (var axis = 0; axis < 3; axis++)
                {
                    var origin = axis == 0 ? box.Min.X : axis == 1 ? box.Min.Y : box.Min.Z;
                    var step = axis == 0 ? scale.X : axis == 1 ? scale.Y : scale.Z;
                    var q = (ushort)Math.Clamp((int)MathF.Round((positions[3 * v + axis] - origin) / step), 0, 65535);
                    BinaryPrimitives.WriteUInt16LittleEndian(packed.AsSpan(8 * v + 2 * axis), q);
                    qmin[axis] = Math.Min(qmin[axis], q);
                    qmax[axis] = Math.Max(qmax[axis], q);
                }
            }

            accessors.Dequantise[source] = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateTranslation(box.Min);
            accessors.AnyQuantized = true;
            position = AddAttribute(accessors, bin, packed, vertexCount, 8, compress, 5123, false, "VEC3", qmin, qmax, null);

            if (normals.Length > 0)
            {
                normal = compress
                    ? AddAttribute(accessors, bin, MeshoptEncoder.EncodeOctahedral8(normals), vertexCount, 4, true, 5120, true, "VEC3", null, null, "OCTAHEDRAL")
                    : AddAttribute(accessors, bin, PlainNormals(normals), vertexCount, 4, false, 5120, true, "VEC3", null, null, null);
            }

            if (colors.Length > 0)
            {
                var bytes = new byte[vertexCount * 4];
                for (var i = 0; i < bytes.Length; i++)
                {
                    bytes[i] = (byte)Math.Clamp((int)MathF.Round((i % 4 == 3 ? colors[i] : Srgb(colors[i])) * 255f), 0, 255);
                }

                color = AddAttribute(accessors, bin, bytes, vertexCount, 4, compress, 5121, true, "VEC4", null, null, null);
            }

            if (texCoords.Length > 0)
            {
                texCoord = AddAttribute(accessors, bin, MemoryMarshal.AsBytes(texCoords.AsSpan()).ToArray(), vertexCount, 8, compress, 5126, false, "VEC2", null, null, null);
            }
        }
        else
        {
            var bounds = mesh.ComputeBounds();
            if (!IsFinite(bounds.Min) || !IsFinite(bounds.Max))
            {
                bounds = new BoundingBox(Vector3.Zero, Vector3.Zero);
            }

            position = AddAccessor(accessors, bin, MemoryMarshal.AsBytes(positions.AsSpan()), 5126, vertexCount, "VEC3", 34962,
                [bounds.Min.X, bounds.Min.Y, bounds.Min.Z], [bounds.Max.X, bounds.Max.Y, bounds.Max.Z]);
            if (normals.Length > 0)
            {
                normal = AddAccessor(accessors, bin, MemoryMarshal.AsBytes(normals.AsSpan()), 5126, vertexCount, "VEC3", 34962, null, null);
            }

            if (colors.Length > 0)
            {
                var linear = new float[colors.Length];
                for (var i = 0; i < linear.Length; i++)
                {
                    linear[i] = i % 4 == 3 ? colors[i] : Srgb(colors[i]);
                }

                color = AddAccessor(accessors, bin, MemoryMarshal.AsBytes(linear.AsSpan()), 5126, vertexCount, "VEC4", 34962, null, null);
            }

            if (texCoords.Length > 0)
            {
                texCoord = AddAccessor(accessors, bin, MemoryMarshal.AsBytes(texCoords.AsSpan()), 5126, vertexCount, "VEC2", 34962, null, null);
            }
        }

        if (indices.Length == 0)
        {
            if (edges.Length > 0 && mesh.EdgeIndices.Length > 0)
            {
                // A line-only mesh (a 2D drawing, a toolpath): its edges are the content.
                var lines = AddIndices(accessors, bin, edges, vertexCount, compress, triangles: false);
                primitives.Add((position, -1, color, -1, lines, mesh.EdgeMaterial >= 0 && mesh.EdgeMaterial < materialCount ? mesh.EdgeMaterial : -1, true));
            }
            else
            {
                primitives.Add((position, normal, color, texCoord, -1, -1, false));
            }

            return primitives;
        }

        var groups = mesh.Groups.Count > 0 ? mesh.Groups : [new MeshGroup(0, mesh.Indices.Length, -1)];
        foreach (var group in groups)
        {
            if (group.Count <= 0)
            {
                continue;
            }

            var accessor = AddIndices(accessors, bin, indices.AsSpan(group.Start, group.Count), vertexCount, compress, triangles: true);
            primitives.Add((position, normal, color, texCoord, accessor, group.Material >= 0 && group.Material < materialCount ? group.Material : -1, false));
        }

        if (edges.Length > 0)
        {
            var edgeAccessor = AddIndices(accessors, bin, edges, vertexCount, compress, triangles: false);
            primitives.Add((position, -1, -1, -1, edgeAccessor, -1, true));
        }

        return primitives;
    }

    private static MeshData ShiftedCopy(MeshData mesh, float[] positions)
    {
        var copy = new MeshData
        {
            Name = mesh.Name,
            Positions = positions,
            Normals = mesh.Normals,
            Colors = mesh.Colors,
            TexCoords = mesh.TexCoords,
            Indices = mesh.Indices,
            Faces = mesh.Faces,
            EdgeIndices = mesh.EdgeIndices,
            EdgeMaterial = mesh.EdgeMaterial,
        };
        foreach (var g in mesh.Groups)
        {
            copy.Groups.Add(g);
        }

        return copy;
    }

    private static uint[] EdgesFor(MeshData mesh, GlbWriteOptions options)
    {
        if (mesh.Indices.Length == 0 && mesh.EdgeIndices.Length > 0)
        {
            return mesh.EdgeIndices;
        }

        if (options.Edges == EdgeMode.All || (options.Edges == EdgeMode.Auto && mesh.Faces.Length > 0))
        {
            return mesh.EdgeIndices.Length > 0 ? mesh.EdgeIndices : MeshEdges.Extract(mesh, options.EdgeCreaseAngle, cancellation: options.Cancellation);
        }

        return [];
    }

    private static BoundingBox BoundsOf(float[] positions)
    {
        var box = BoundingBox.Empty;
        for (var i = 0; i + 2 < positions.Length; i += 3)
        {
            box = box.Include(new Vector3(positions[i], positions[i + 1], positions[i + 2]));
        }

        return box;
    }

    private static (float[] Positions, float[] Normals, float[] Colors, float[] TexCoords, uint[] Indices, uint[] Edges) ReorderByFirstUse(MeshData mesh, float[] normals, float[] colors, float[] texCoords, uint[] edges)
    {
        var map = new int[mesh.VertexCount];
        Array.Fill(map, -1);
        var used = 0;
        var indices = new uint[mesh.Indices.Length];
        for (var i = 0; i < indices.Length; i++)
        {
            var v = (int)mesh.Indices[i];
            if (map[v] < 0)
            {
                map[v] = used++;
            }

            indices[i] = (uint)map[v];
        }

        var positions = new float[used * 3];
        var newNormals = normals.Length > 0 ? new float[used * 3] : [];
        var newColors = colors.Length > 0 ? new float[used * 4] : [];
        var newTexCoords = texCoords.Length > 0 ? new float[used * 2] : [];
        for (var v = 0; v < map.Length; v++)
        {
            var n = map[v];
            if (n < 0)
            {
                continue;
            }

            Array.Copy(mesh.Positions, 3 * v, positions, 3 * n, 3);
            if (normals.Length > 0)
            {
                Array.Copy(normals, 3 * v, newNormals, 3 * n, 3);
            }

            if (colors.Length > 0)
            {
                Array.Copy(colors, 4 * v, newColors, 4 * n, 4);
            }

            if (texCoords.Length > 0)
            {
                Array.Copy(texCoords, 2 * v, newTexCoords, 2 * n, 2);
            }
        }

        var newEdges = edges.Where(e => map[e] >= 0).Select(e => (uint)map[e]).ToArray();
        if (newEdges.Length != edges.Length)
        {
            newEdges = [];
        }

        return (positions, newNormals, newColors, newTexCoords, indices, newEdges);
    }

    private static byte[] PlainNormals(float[] normals)
    {
        var bytes = new byte[normals.Length / 3 * 4];
        for (var i = 0; i < normals.Length / 3; i++)
        {
            for (var axis = 0; axis < 3; axis++)
            {
                bytes[4 * i + axis] = (byte)(sbyte)Math.Clamp((int)MathF.Round(normals[3 * i + axis] * 127f), -127, 127);
            }
        }

        return bytes;
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    /// <summary>An interleaved-free attribute: <paramref name="raw"/> holds <paramref name="count"/> elements of <paramref name="stride"/> bytes.</summary>
    private static int AddAttribute(Accessors accessors, MemoryStream bin, byte[] raw, int count, int stride, bool compress, int componentType, bool normalized, string type, float[]? min, float[]? max, string? filter)
    {
        var view = NewView(accessors, bin, raw, count, stride, compress, "ATTRIBUTES", filter, 34962, stride);
        accessors.List.Add(new Accessor { View = view, ComponentType = componentType, Count = count, Type = type, Normalized = normalized, Min = min, Max = max });
        return accessors.List.Count - 1;
    }

    private static int AddIndices(Accessors accessors, MemoryStream bin, ReadOnlySpan<uint> indices, int vertexCount, bool compress, bool triangles)
    {
        var wide = vertexCount > ushort.MaxValue;
        var raw = new byte[indices.Length * (wide ? 4 : 2)];
        for (var i = 0; i < indices.Length; i++)
        {
            if (wide)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(4 * i), indices[i]);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan(2 * i), (ushort)indices[i]);
            }
        }

        int view;
        if (compress)
        {
            var encoded = triangles ? MeshoptEncoder.EncodeTriangles(indices, accessors.Cancellation) : MeshoptEncoder.EncodeIndexSequence(indices, accessors.Cancellation);
            view = AddCompressedView(accessors, bin, encoded, raw.Length, indices.Length, wide ? 4 : 2, triangles ? "TRIANGLES" : "INDICES", null, 34963, 0);
        }
        else
        {
            view = AddPlainView(accessors, bin, raw, 34963, 0);
        }

        accessors.List.Add(new Accessor { View = view, ComponentType = wide ? 5125 : 5123, Count = indices.Length, Type = "SCALAR" });
        return accessors.List.Count - 1;
    }

    private static int NewView(Accessors accessors, MemoryStream bin, byte[] raw, int count, int stride, bool compress, string mode, string? filter, int target, int parentStride)
    {
        if (!compress)
        {
            return AddPlainView(accessors, bin, raw, target, parentStride);
        }

        var encoded = MeshoptEncoder.EncodeVertices(raw, count, stride, accessors.Cancellation);
        return AddCompressedView(accessors, bin, encoded, raw.Length, count, stride, mode, filter, target, parentStride);
    }

    private static int AddPlainView(Accessors accessors, MemoryStream bin, ReadOnlySpan<byte> raw, int target, int stride)
    {
        Align(bin);
        accessors.Views.Add(new View { Offset = (int)bin.Length, Length = raw.Length, Target = target, Stride = stride });
        bin.Write(raw);
        return accessors.Views.Count - 1;
    }

    private static int AddCompressedView(Accessors accessors, MemoryStream bin, byte[] encoded, int rawLength, int count, int elementStride, string mode, string? filter, int target, int parentStride)
    {
        Align(bin);
        var offset = (int)bin.Length;
        bin.Write(encoded);
        accessors.FallbackSize = (accessors.FallbackSize + 3) & ~3L;
        accessors.Views.Add(new View
        {
            Offset = (int)accessors.FallbackSize,
            Length = rawLength,
            Target = target,
            Stride = parentStride,
            Compression = new Compressed(offset, encoded.Length, count, mode, filter, elementStride),
        });
        accessors.FallbackSize += rawLength;
        accessors.AnyCompressed = true;
        return accessors.Views.Count - 1;
    }

    private static void Align(MemoryStream bin)
    {
        while (bin.Length % 4 != 0)
        {
            bin.WriteByte(0);
        }
    }

    private static int AddAccessor(Accessors accessors, MemoryStream bin, ReadOnlySpan<byte> bytes, int componentType, int count, string type, int target, float[]? min, float[]? max)
    {
        var view = AddPlainView(accessors, bin, bytes, target, 0);
        accessors.List.Add(new Accessor { View = view, ComponentType = componentType, Count = count, Type = type, Min = min, Max = max });
        return accessors.List.Count - 1;
    }
}
