using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace XRay.ThreeD.Formats.Mesh;

/// <summary>
/// glTF 2.0 (.glb, and .gltf with embedded data URIs): scene graph, names, materials, extras and triangle
/// meshes. Compressed geometry (Draco, meshopt), sparse accessors and external buffers are reported as warnings
/// and the node tree is still returned.
/// </summary>
internal static class GltfReader
{
    private const int MaxDepth = 256;

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var span = data.Span;
        byte[]? glbBin = null;
        ReadOnlyMemory<byte> json;
        if (span.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(span) == 0x46546C67)
        {
            json = default;
            var offset = 12;
            while (offset + 8 <= span.Length)
            {
                var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset, 4));
                var type = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset + 4, 4));
                if (length < 0 || offset + 8L + length > span.Length)
                {
                    throw new ModelFormatException("A GLB chunk runs past the end of the file.");
                }

                if (type == 0x4E4F534A && json.IsEmpty)
                {
                    json = data.Slice(offset + 8, length);
                }
                else if (type == 0x004E4942 && glbBin is null)
                {
                    glbBin = span.Slice(offset + 8, length).ToArray();
                }

                offset += 8 + ((length + 3) & ~3);
            }

            if (json.IsEmpty)
            {
                throw new ModelFormatException("The GLB has no JSON chunk.");
            }
        }
        else
        {
            json = data;
        }

        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64, AllowTrailingCommas = true });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new ModelFormatException("The glTF JSON is not an object.");
        }

        return new Builder(root, glbBin, options).Build();
    }

    private sealed class Builder
    {
        private readonly JsonElement _root;
        private readonly byte[]? _glbBin;
        private readonly LoadOptions _options;
        private readonly Model3D _model = new();
        private readonly Dictionary<int, byte[]?> _buffers = new();
        private readonly Dictionary<int, MeshData?> _meshCache = new();
        private bool _geometryBlocked;

        public Builder(JsonElement root, byte[]? glbBin, LoadOptions options)
        {
            _root = root;
            _glbBin = glbBin;
            _options = options;
        }

        public Model3D Build()
        {
            var meta = _model.Metadata;
            meta.Format = ModelFormats.Gltf;
            meta.DocumentKind = "scene";
            meta.Units = LengthUnit.Meter;
            meta.UpAxis = UpAxis.Y;

            if (_root.TryGetProperty("asset", out var asset) && asset.ValueKind == JsonValueKind.Object)
            {
                meta.FormatVersion = Str(asset, "version");
                meta.Application = Str(asset, "generator");
                meta.Author = Str(asset, "copyright");
            }

            foreach (var required in Strings(_root, "extensionsRequired"))
            {
                if (required is "KHR_draco_mesh_compression" or "EXT_meshopt_compression" or "KHR_meshopt_compression")
                {
                    _geometryBlocked = true;
                    _model.Warnings.Add($"Geometry uses {required}, which this library does not decode; only the node tree is read.");
                }
            }

            ReadMaterials();
            var sceneIndex = _root.TryGetProperty("scene", out var sceneValue) && sceneValue.TryGetInt32(out var s) ? s : 0;
            var nodes = Array(_root, "nodes");
            var scenes = Array(_root, "scenes");
            var roots = new List<int>();
            if (scenes.Count > sceneIndex && sceneIndex >= 0)
            {
                roots.AddRange(Ints(scenes[sceneIndex], "nodes"));
                if (Str(scenes[sceneIndex], "name") is { } sceneName)
                {
                    meta.Title = sceneName;
                }
            }
            else if (scenes.Count == 0)
            {
                // No scene: take the nodes nobody else claims.
                var children = new HashSet<int>(nodes.SelectMany(n => Ints(n, "children")));
                roots.AddRange(Enumerable.Range(0, nodes.Count).Where(i => !children.Contains(i)));
            }

            foreach (var index in roots)
            {
                if (BuildNode(nodes, index, 0) is { } node)
                {
                    _model.Roots.Add(node);
                }
            }

            foreach (var node in _model.Nodes())
            {
                if (node.Mesh is not null && !string.IsNullOrEmpty(node.Name))
                {
                    meta.PartNames.Add(node.Name);
                }
            }

            meta.MaterialNames.AddRange(_model.Materials.Select(m => m.Name).Where(n => !string.IsNullOrEmpty(n))!);
            meta.HasGeometry = _model.Meshes.Count > 0;
            meta.Statistics = _model.ComputeStatistics();
            meta.Bounds = _model.ComputeBounds();
            _model.Warnings.AddRange(meta.Notes);
            return _model;
        }

        private void ReadMaterials()
        {
            foreach (var m in Array(_root, "materials"))
            {
                var material = new ModelMaterial { Name = Str(m, "name") };
                if (m.ValueKind == JsonValueKind.Object && m.TryGetProperty("pbrMetallicRoughness", out var pbr) && pbr.ValueKind == JsonValueKind.Object)
                {
                    if (pbr.TryGetProperty("baseColorFactor", out var factor) && factor.ValueKind == JsonValueKind.Array && factor.GetArrayLength() == 4)
                    {
                        var c = factor.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Number ? e.GetSingle() : 1f).ToArray();
                        material.BaseColor = new Vector4(ToSrgb(c[0]), ToSrgb(c[1]), ToSrgb(c[2]), c[3]);
                    }

                    if (pbr.TryGetProperty("metallicFactor", out var metallic) && metallic.ValueKind == JsonValueKind.Number)
                    {
                        material.Metallic = metallic.GetSingle();
                    }

                    if (pbr.TryGetProperty("roughnessFactor", out var roughness) && roughness.ValueKind == JsonValueKind.Number)
                    {
                        material.Roughness = roughness.GetSingle();
                    }
                }

                _model.Materials.Add(material);
            }
        }

        private static float ToSrgb(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * MathF.Pow(c, 1 / 2.4f) - 0.055f;

        private SceneNode? BuildNode(List<JsonElement> nodes, int index, int depth)
        {
            if (index < 0 || index >= nodes.Count || depth > MaxDepth)
            {
                _model.Warnings.Add("A node reference is out of range or the hierarchy is cyclic; it was skipped.");
                return null;
            }

            var element = nodes[index];
            var node = new SceneNode(Str(element, "name")) { Transform = ReadTransform(element) };
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("mesh", out var meshIndex) && meshIndex.TryGetInt32(out var mi))
            {
                node.Mesh = GetMesh(mi);
            }

            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("extras", out var extras) && extras.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in extras.EnumerateObject())
                {
                    node.Properties[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : property.Value.GetRawText();
                }
            }

            foreach (var child in Ints(element, "children"))
            {
                if (BuildNode(nodes, child, depth + 1) is { } built)
                {
                    node.Children.Add(built);
                }
            }

            return node;
        }

        private static Matrix4x4 ReadTransform(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return Matrix4x4.Identity;
            }

            if (element.TryGetProperty("matrix", out var matrix) && matrix.ValueKind == JsonValueKind.Array && matrix.GetArrayLength() == 16)
            {
                var a = matrix.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Number ? e.GetSingle() : 0f).ToArray();
                return new Matrix4x4(a[0], a[1], a[2], a[3], a[4], a[5], a[6], a[7], a[8], a[9], a[10], a[11], a[12], a[13], a[14], a[15]);
            }

            var scale = Vec(element, "scale", 3) is { } sv ? new Vector3(sv[0], sv[1], sv[2]) : Vector3.One;
            var rotation = Vec(element, "rotation", 4) is { } rv ? Quaternion.Normalize(new Quaternion(rv[0], rv[1], rv[2], rv[3])) : Quaternion.Identity;
            var translation = Vec(element, "translation", 3) is { } tv ? new Vector3(tv[0], tv[1], tv[2]) : Vector3.Zero;
            return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
        }

        private static float[]? Vec(JsonElement element, string name, int length)
        {
            if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != length)
            {
                return null;
            }

            return value.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.Number ? e.GetSingle() : 0f).ToArray();
        }

        private MeshData? GetMesh(int index)
        {
            if (_meshCache.TryGetValue(index, out var cached))
            {
                return cached;
            }

            MeshData? mesh = null;
            var meshes = Array(_root, "meshes");
            if (!_geometryBlocked && index >= 0 && index < meshes.Count)
            {
                mesh = BuildMesh(meshes[index]);
                if (mesh is not null)
                {
                    _model.Meshes.Add(mesh);
                }
            }

            _meshCache[index] = mesh;
            return mesh;
        }

        private MeshData? BuildMesh(JsonElement element)
        {
            var positions = new List<float>();
            var normals = new List<float>();
            var indices = new List<uint>();
            var groups = new List<MeshGroup>();
            var allNormals = true;
            foreach (var primitive in Array(element, "primitives"))
            {
                _options.Cancellation.ThrowIfCancellationRequested();
                var mode = primitive.TryGetProperty("mode", out var m) && m.TryGetInt32(out var mm) ? mm : 4;
                if (mode is not (4 or 5 or 6) || !primitive.TryGetProperty("attributes", out var attributes) || !attributes.TryGetProperty("POSITION", out var pa) || !pa.TryGetInt32(out var positionAccessor))
                {
                    _model.Warnings.Add("A primitive that is not a triangle list was skipped.");
                    continue;
                }

                var p = ReadAccessor(positionAccessor, 3);
                if (p is null)
                {
                    continue;
                }

                var vertexCount = p.Length / 3;
                var baseVertex = (uint)(positions.Count / 3);
                float[]? n = attributes.TryGetProperty("NORMAL", out var na) && na.TryGetInt32(out var nai) ? ReadAccessor(nai, 3) : null;
                if (n is null || n.Length != p.Length)
                {
                    allNormals = false;
                }

                uint[] local;
                if (primitive.TryGetProperty("indices", out var ia) && ia.TryGetInt32(out var iai))
                {
                    var read = ReadIndices(iai);
                    if (read is null)
                    {
                        continue;
                    }

                    local = read;
                }
                else
                {
                    local = Enumerable.Range(0, vertexCount).Select(i => (uint)i).ToArray();
                }

                if (local.Any(i => i >= vertexCount))
                {
                    _model.Warnings.Add("A primitive has indices outside its vertex range and was skipped.");
                    continue;
                }

                var triangles = mode switch
                {
                    5 => Strip(local),
                    6 => Fan(local),
                    _ => local,
                };
                var start = indices.Count;
                foreach (var i in triangles.Take(triangles.Length / 3 * 3))
                {
                    indices.Add(baseVertex + i);
                }

                var material = primitive.TryGetProperty("material", out var mat) && mat.TryGetInt32(out var mi) && mi >= 0 && mi < _model.Materials.Count ? mi : -1;
                groups.Add(new MeshGroup(start, indices.Count - start, material));
                positions.AddRange(p);
                if (n is not null && n.Length == p.Length)
                {
                    normals.AddRange(n);
                }
                else
                {
                    normals.AddRange(new float[p.Length]);
                }
            }

            if (positions.Count == 0)
            {
                return null;
            }

            var mesh = new MeshData { Name = Str(element, "name"), Positions = positions.ToArray(), Indices = indices.ToArray() };
            if (allNormals)
            {
                mesh.Normals = normals.ToArray();
            }

            mesh.Groups.AddRange(groups);
            return mesh;
        }

        private static uint[] Strip(uint[] s)
        {
            var o = new List<uint>();
            for (var i = 0; i + 2 < s.Length; i++)
            {
                if ((i & 1) == 0)
                {
                    o.AddRange([s[i], s[i + 1], s[i + 2]]);
                }
                else
                {
                    o.AddRange([s[i + 1], s[i], s[i + 2]]);
                }
            }

            return o.ToArray();
        }

        private static uint[] Fan(uint[] s)
        {
            var o = new List<uint>();
            for (var i = 1; i + 1 < s.Length; i++)
            {
                o.AddRange([s[0], s[i], s[i + 1]]);
            }

            return o.ToArray();
        }

        private byte[]? Buffer(int index)
        {
            if (_buffers.TryGetValue(index, out var cached))
            {
                return cached;
            }

            byte[]? result = null;
            var buffers = Array(_root, "buffers");
            if (index >= 0 && index < buffers.Count)
            {
                var uri = Str(buffers[index], "uri");
                if (uri is null)
                {
                    result = index == 0 ? _glbBin : null;
                }
                else if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    var comma = uri.IndexOf(',');
                    result = comma > 0 ? Convert.FromBase64String(uri[(comma + 1)..]) : null;
                }
                else
                {
                    _model.Warnings.Add($"Buffer '{uri}' is an external file and was not loaded.");
                }
            }

            _buffers[index] = result;
            return result;
        }

        private (byte[] Data, int Offset, int Stride, int Count, int ComponentType, int Components, bool Normalized)? Locate(int accessorIndex)
        {
            var accessors = Array(_root, "accessors");
            if (accessorIndex < 0 || accessorIndex >= accessors.Count)
            {
                return null;
            }

            var a = accessors[accessorIndex];
            if (a.ValueKind != JsonValueKind.Object || a.TryGetProperty("sparse", out _))
            {
                _model.Warnings.Add("A sparse accessor was skipped.");
                return null;
            }

            if (!a.TryGetProperty("bufferView", out var bv) || !bv.TryGetInt32(out var viewIndex))
            {
                return null;
            }

            var views = Array(_root, "bufferViews");
            if (viewIndex < 0 || viewIndex >= views.Count)
            {
                return null;
            }

            var v = views[viewIndex];
            var data = v.TryGetProperty("buffer", out var b) && b.TryGetInt32(out var bi) ? Buffer(bi) : null;
            if (data is null)
            {
                return null;
            }

            var viewOffset = Int(v, "byteOffset");
            var accessorOffset = Int(a, "byteOffset");
            var count = Int(a, "count");
            var componentType = Int(a, "componentType");
            var components = Str(a, "type") switch
            {
                "SCALAR" => 1,
                "VEC2" => 2,
                "VEC3" => 3,
                "VEC4" => 4,
                _ => 0,
            };
            if (components == 0 || count < 0)
            {
                return null;
            }

            var size = ComponentSize(componentType);
            if (size == 0)
            {
                return null;
            }

            var stride = Int(v, "byteStride");
            if (stride == 0)
            {
                stride = size * components;
            }

            var offset = (long)viewOffset + accessorOffset;
            if (offset < 0 || count > 0 && offset + (long)(count - 1) * stride + size * components > data.Length)
            {
                throw new ModelFormatException("A glTF accessor reads past the end of its buffer.");
            }

            return (data, (int)offset, stride, count, componentType, components, a.TryGetProperty("normalized", out var nz) && nz.ValueKind == JsonValueKind.True);
        }

        private float[]? ReadAccessor(int index, int expectedComponents)
        {
            if (Locate(index) is not { } loc || loc.Components != expectedComponents)
            {
                return null;
            }

            var result = new float[loc.Count * loc.Components];
            var size = ComponentSize(loc.ComponentType);
            for (var i = 0; i < loc.Count; i++)
            {
                for (var c = 0; c < loc.Components; c++)
                {
                    result[i * loc.Components + c] = (float)ReadComponent(loc.Data, loc.Offset + i * loc.Stride + c * size, loc.ComponentType, loc.Normalized);
                }
            }

            return result;
        }

        private uint[]? ReadIndices(int index)
        {
            if (Locate(index) is not { } loc || loc.Components != 1)
            {
                return null;
            }

            var size = ComponentSize(loc.ComponentType);
            var result = new uint[loc.Count];
            for (var i = 0; i < loc.Count; i++)
            {
                result[i] = (uint)ReadComponent(loc.Data, loc.Offset + i * loc.Stride, loc.ComponentType, false);
            }

            _ = size;
            return result;
        }

        private static int ComponentSize(int type) => type switch
        {
            5120 or 5121 => 1,
            5122 or 5123 => 2,
            5125 or 5126 => 4,
            _ => 0,
        };

        private static double ReadComponent(byte[] d, int o, int type, bool normalized)
        {
            var s = new ReadOnlySpan<byte>(d, o, ComponentSize(type));
            return type switch
            {
                5120 => normalized ? Math.Max((sbyte)s[0] / 127.0, -1) : (sbyte)s[0],
                5121 => normalized ? s[0] / 255.0 : s[0],
                5122 => normalized ? Math.Max(BinaryPrimitives.ReadInt16LittleEndian(s) / 32767.0, -1) : BinaryPrimitives.ReadInt16LittleEndian(s),
                5123 => normalized ? BinaryPrimitives.ReadUInt16LittleEndian(s) / 65535.0 : BinaryPrimitives.ReadUInt16LittleEndian(s),
                5125 => BinaryPrimitives.ReadUInt32LittleEndian(s),
                _ => BinaryPrimitives.ReadSingleLittleEndian(s),
            };
        }

        private static List<JsonElement> Array(JsonElement parent, string name) =>
            parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToList() : [];

        private static IEnumerable<int> Ints(JsonElement parent, string name) =>
            Array(parent, name).Where(e => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out _)).Select(e => e.GetInt32());

        private static IEnumerable<string> Strings(JsonElement parent, string name) =>
            Array(parent, name).Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!);

        private static string? Str(JsonElement parent, string name) =>
            parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static int Int(JsonElement parent, string name) =>
            parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;
    }
}
