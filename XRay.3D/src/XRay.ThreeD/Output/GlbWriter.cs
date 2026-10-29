using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace XRay.ThreeD;

/// <summary>What <see cref="GlbWriter"/> does to the model on the way out.</summary>
public sealed class GlbWriteOptions
{
    /// <summary>Scale the scene to meters (glTF's unit) with a root transform when the model's units are known and are not meters.</summary>
    public bool ConvertToMeters { get; set; } = true;

    /// <summary>Rotate Z-up models to glTF's Y-up with a root transform.</summary>
    public bool ConvertToYUp { get; set; } = true;

    /// <summary>Write model metadata (<c>asset.extras</c>) and per-node properties (<c>extras</c>).</summary>
    public bool IncludeMetadata { get; set; } = true;

    /// <summary>Embed the file's preview image as a texture-less extra (base64) in <c>asset.extras</c>. Off by default: it can be large.</summary>
    public bool IncludePreview { get; set; }
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

    private sealed class Accessors
    {
        public readonly List<(int View, int ComponentType, int Count, string Type, Vector3? Min, Vector3? Max)> List = new();
        public readonly List<(int Offset, int Length, int Target)> Views = new();
    }

    private static void WriteJson(Model3D model, GlbWriteOptions options, Utf8JsonWriter w, MemoryStream bin)
    {
        var accessors = new Accessors();
        var meshPrimitives = new List<List<(int Position, int Normal, int Color, int Indices, int Material)>>();

        foreach (var mesh in model.Meshes)
        {
            meshPrimitives.Add(AddMesh(mesh, accessors, bin, model.Materials.Count));
        }

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

        var (rootScale, rootRotation, wrap) = RootTransform(model, options);
        var flat = new List<SceneNode>();
        var indexOf = new Dictionary<SceneNode, int>(ReferenceEqualityComparer.Instance);
        void Collect(SceneNode node)
        {
            indexOf[node] = flat.Count;
            flat.Add(node);
            foreach (var child in node.Children)
            {
                Collect(child);
            }
        }

        foreach (var root in model.Roots)
        {
            Collect(root);
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
            foreach (var root in model.Roots)
            {
                w.WriteNumberValue(indexOf[root]);
            }
        }

        w.WriteEndArray();
        w.WriteEndObject();
        w.WriteEndArray();

        w.WriteStartArray("nodes");
        foreach (var node in flat)
        {
            WriteNode(w, node, indexOf, model, options);
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
            foreach (var root in model.Roots)
            {
                w.WriteNumberValue(indexOf[root]);
            }

            w.WriteEndArray();
            w.WriteEndObject();
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

                w.WriteStartArray("primitives");
                foreach (var (position, normal, color, indices, material) in meshPrimitives[i])
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

                    w.WriteEndObject();
                    if (indices >= 0)
                    {
                        w.WriteNumber("indices", indices);
                    }
                    else
                    {
                        w.WriteNumber("mode", 0);
                    }

                    w.WriteNumber("material", material < 0 ? model.Materials.Count : material);
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
                WriteMaterial(w, m);
            }

            if (anyDefaultMaterial)
            {
                WriteMaterial(w, new ModelMaterial { Name = "default" });
            }

            w.WriteEndArray();
        }

        if (accessors.List.Count > 0)
        {
            w.WriteStartArray("accessors");
            foreach (var a in accessors.List)
            {
                w.WriteStartObject();
                w.WriteNumber("bufferView", a.View);
                w.WriteNumber("componentType", a.ComponentType);
                w.WriteNumber("count", a.Count);
                w.WriteString("type", a.Type);
                if (a.Min is { } min && a.Max is { } max)
                {
                    w.WriteStartArray("min");
                    w.WriteNumberValue(min.X);
                    w.WriteNumberValue(min.Y);
                    w.WriteNumberValue(min.Z);
                    w.WriteEndArray();
                    w.WriteStartArray("max");
                    w.WriteNumberValue(max.X);
                    w.WriteNumberValue(max.Y);
                    w.WriteNumberValue(max.Z);
                    w.WriteEndArray();
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("bufferViews");
            foreach (var v in accessors.Views)
            {
                w.WriteStartObject();
                w.WriteNumber("buffer", 0);
                w.WriteNumber("byteOffset", v.Offset);
                w.WriteNumber("byteLength", v.Length);
                if (v.Target != 0)
                {
                    w.WriteNumber("target", v.Target);
                }

                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteStartArray("buffers");
            w.WriteStartObject();
            w.WriteNumber("byteLength", (bin.Length + 3) & ~3L);
            w.WriteEndObject();
            w.WriteEndArray();
        }

        w.WriteEndObject();
    }

    private static (double Scale, Quaternion Rotation, bool Wrap) RootTransform(Model3D model, GlbWriteOptions options)
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

    private static void WriteMaterial(Utf8JsonWriter w, ModelMaterial m)
    {
        w.WriteStartObject();
        if (m.Name is not null)
        {
            w.WriteString("name", m.Name);
        }

        w.WriteStartObject("pbrMetallicRoughness");
        w.WriteStartArray("baseColorFactor");
        w.WriteNumberValue(Srgb(m.BaseColor.X));
        w.WriteNumberValue(Srgb(m.BaseColor.Y));
        w.WriteNumberValue(Srgb(m.BaseColor.Z));
        w.WriteNumberValue(m.BaseColor.W);
        w.WriteEndArray();
        w.WriteNumber("metallicFactor", m.Metallic);
        w.WriteNumber("roughnessFactor", m.Roughness);
        w.WriteEndObject();
        w.WriteBoolean("doubleSided", true);
        if (m.BaseColor.W < 1)
        {
            w.WriteString("alphaMode", "BLEND");
        }

        w.WriteEndObject();
    }

    /// <summary>glTF base colours are linear; model colours are sRGB.</summary>
    private static float Srgb(float c) => c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);

    private static void WriteNode(Utf8JsonWriter w, SceneNode node, Dictionary<SceneNode, int> indexOf, Model3D model, GlbWriteOptions options)
    {
        w.WriteStartObject();
        if (!string.IsNullOrEmpty(node.Name))
        {
            w.WriteString("name", node.Name);
        }

        if (node.Mesh is { } mesh)
        {
            w.WriteNumber("mesh", model.Meshes.IndexOf(mesh));
        }

        if (node.Children.Count > 0)
        {
            w.WriteStartArray("children");
            foreach (var child in node.Children)
            {
                w.WriteNumberValue(indexOf[child]);
            }

            w.WriteEndArray();
        }

        if (node.Transform != Matrix4x4.Identity)
        {
            var m = node.Transform;
            w.WriteStartArray("matrix");
            foreach (var v in new[] { m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44 })
            {
                w.WriteNumberValue(v);
            }

            w.WriteEndArray();
        }

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

        w.WriteEndObject();
    }

    private static void WriteModelExtras(Utf8JsonWriter w, Model3D model, GlbWriteOptions options)
    {
        var meta = model.Metadata;
        w.WriteStartObject("extras");
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

    private static List<(int Position, int Normal, int Color, int Indices, int Material)> AddMesh(MeshData mesh, Accessors accessors, MemoryStream bin, int materialCount)
    {
        var primitives = new List<(int, int, int, int, int)>();
        if (mesh.VertexCount == 0)
        {
            return primitives;
        }

        var bounds = mesh.ComputeBounds();
        if (!IsFinite(bounds.Min) || !IsFinite(bounds.Max))
        {
            bounds = new BoundingBox(Vector3.Zero, Vector3.Zero);
        }

        var position = AddAccessor(accessors, bin, MemoryMarshal.AsBytes(mesh.Positions.AsSpan()), 5126, mesh.VertexCount, "VEC3", 34962, bounds.Min, bounds.Max);
        var normal = mesh.Normals.Length == mesh.Positions.Length
            ? AddAccessor(accessors, bin, MemoryMarshal.AsBytes(mesh.Normals.AsSpan()), 5126, mesh.VertexCount, "VEC3", 34962, null, null)
            : -1;

        var color = -1;
        if (mesh.Colors.Length == mesh.VertexCount * 4)
        {
            var linear = new float[mesh.Colors.Length];
            for (var i = 0; i < linear.Length; i++)
            {
                linear[i] = i % 4 == 3 ? mesh.Colors[i] : Srgb(mesh.Colors[i]);
            }

            color = AddAccessor(accessors, bin, MemoryMarshal.AsBytes(linear.AsSpan()), 5126, mesh.VertexCount, "VEC4", 34962, null, null);
        }

        var groups = mesh.Groups.Count > 0 ? mesh.Groups : [new MeshGroup(0, mesh.Indices.Length, -1)];
        if (mesh.Indices.Length == 0)
        {
            primitives.Add((position, normal, color, -1, -1));
            return primitives;
        }

        var small = mesh.VertexCount <= ushort.MaxValue;
        foreach (var group in groups)
        {
            if (group.Count <= 0)
            {
                continue;
            }

            var slice = mesh.Indices.AsSpan(group.Start, group.Count);
            int indices;
            if (small)
            {
                var narrow = new ushort[slice.Length];
                for (var i = 0; i < narrow.Length; i++)
                {
                    narrow[i] = (ushort)slice[i];
                }

                indices = AddAccessor(accessors, bin, MemoryMarshal.AsBytes(narrow.AsSpan()), 5123, narrow.Length, "SCALAR", 34963, null, null);
            }
            else
            {
                indices = AddAccessor(accessors, bin, MemoryMarshal.AsBytes(slice), 5125, slice.Length, "SCALAR", 34963, null, null);
            }

            primitives.Add((position, normal, color, indices, group.Material >= 0 && group.Material < materialCount ? group.Material : -1));
        }

        return primitives;
    }

    private static bool IsFinite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private static int AddAccessor(Accessors accessors, MemoryStream bin, ReadOnlySpan<byte> bytes, int componentType, int count, string type, int target, Vector3? min, Vector3? max)
    {
        while (bin.Length % 4 != 0)
        {
            bin.WriteByte(0);
        }

        var offset = (int)bin.Length;
        bin.Write(bytes);
        accessors.Views.Add((offset, bytes.Length, target));
        accessors.List.Add((accessors.Views.Count - 1, componentType, count, type, min, max));
        return accessors.List.Count - 1;
    }
}
