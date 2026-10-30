using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using XRay.ThreeD.Internal;

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
                if (required is not ("KHR_draco_mesh_compression" or "EXT_meshopt_compression" or "KHR_mesh_quantization" or "KHR_texture_transform" or "EXT_mesh_gpu_instancing"
                    or "KHR_materials_unlit" or "KHR_materials_emissive_strength" or "KHR_materials_ior" or "KHR_materials_specular" or "KHR_materials_clearcoat"
                    or "KHR_materials_transmission" or "KHR_materials_volume" or "KHR_materials_sheen" or "KHR_texture_basisu" or "EXT_texture_webp"))
                {
                    _model.Warnings.Add($"The file requires {required}, which this library does not interpret; the result may look different.");
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

                    if (pbr.TryGetProperty("baseColorTexture", out var colorTexture))
                    {
                        material.BaseColorTexture = ReadTextureRef(colorTexture);
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

        private readonly Dictionary<int, int> _textures = new();

        /// <summary>A <c>textureInfo</c>: the texture, and <c>KHR_texture_transform</c> when present. Only the first UV set is kept by the model.</summary>
        private ModelTextureRef? ReadTextureRef(JsonElement info)
        {
            if (info.ValueKind != JsonValueKind.Object || !info.TryGetProperty("index", out var indexValue) || !indexValue.TryGetInt32(out var index))
            {
                return null;
            }

            var texture = LoadTexture(index);
            if (texture < 0)
            {
                return null;
            }

            var reference = new ModelTextureRef(texture);
            var set = Int(info, "texCoord");
            if (info.TryGetProperty("extensions", out var extensions) && extensions.ValueKind == JsonValueKind.Object
                && extensions.TryGetProperty("KHR_texture_transform", out var transform) && transform.ValueKind == JsonValueKind.Object)
            {
                if (Vec(transform, "offset", 2) is { } o)
                {
                    reference.Offset = new Vector2(o[0], o[1]);
                }

                if (Vec(transform, "scale", 2) is { } sc)
                {
                    reference.Scale = new Vector2(sc[0], sc[1]);
                }

                if (transform.TryGetProperty("rotation", out var r) && r.ValueKind == JsonValueKind.Number)
                {
                    reference.Rotation = r.GetSingle();
                }

                if (transform.TryGetProperty("texCoord", out var tc) && tc.TryGetInt32(out var tcv))
                {
                    set = tcv;
                }
            }

            if (set != 0)
            {
                _model.Warnings.Add($"A texture uses UV set {set}; only TEXCOORD_0 is read, so it may be mapped wrongly.");
            }

            return reference;
        }

        /// <summary>The model texture for glTF texture <paramref name="index"/> (image bytes from a buffer view, a data URI or a companion file), or -1.</summary>
        private int LoadTexture(int index)
        {
            if (_textures.TryGetValue(index, out var cached))
            {
                return cached;
            }

            var result = -1;
            var textures = Array(_root, "textures");
            if (index >= 0 && index < textures.Count)
            {
                var texture = textures[index];
                var source = Int(texture, "source") is var s0 && texture.TryGetProperty("source", out _) ? s0 : -1;
                if (source < 0 && texture.TryGetProperty("extensions", out var ext) && ext.ValueKind == JsonValueKind.Object)
                {
                    foreach (var name in new[] { "EXT_texture_webp", "KHR_texture_basisu" })
                    {
                        if (ext.TryGetProperty(name, out var alt) && alt.TryGetProperty("source", out var sv) && sv.TryGetInt32(out var si))
                        {
                            source = si;
                            break;
                        }
                    }
                }

                var images = Array(_root, "images");
                if (source >= 0 && source < images.Count)
                {
                    var image = images[source];
                    var imageName = Str(image, "name") ?? Str(image, "uri");
                    var bytes = ImageBytes(image);
                    if (bytes is null)
                    {
                        // already warned
                    }
                    else if (TextureImages.Create(imageName ?? $"image {source}", bytes, out var problem, _options.Cancellation) is { } created)
                    {
                        created.Name = Str(image, "name") ?? created.Name;
                        if (Array(_root, "samplers") is var samplers && texture.TryGetProperty("sampler", out var sampler) && sampler.TryGetInt32(out var si) && si >= 0 && si < samplers.Count)
                        {
                            created.WrapU = Wrap(Int(samplers[si], "wrapS"));
                            created.WrapV = Wrap(Int(samplers[si], "wrapT"));
                            created.Nearest = samplers[si].TryGetProperty("magFilter", out var mag) && mag.TryGetInt32(out var m) && m == 9728;
                        }

                        result = _model.Textures.Count;
                        _model.Textures.Add(created);
                    }
                    else if (problem is not null)
                    {
                        _model.Warnings.Add(problem);
                    }
                }
            }

            _textures[index] = result;
            return result;
        }

        private static TextureWrap Wrap(int code) => code switch
        {
            33071 => TextureWrap.ClampToEdge,
            33648 => TextureWrap.MirroredRepeat,
            _ => TextureWrap.Repeat,
        };

        private byte[]? ImageBytes(JsonElement image)
        {
            if (image.TryGetProperty("bufferView", out var bv) && bv.TryGetInt32(out var viewIndex))
            {
                var views = Array(_root, "bufferViews");
                if (viewIndex < 0 || viewIndex >= views.Count || !views[viewIndex].TryGetProperty("buffer", out var b) || !b.TryGetInt32(out var bi) || Buffer(bi) is not { } data)
                {
                    _model.Warnings.Add("An image refers to a buffer view that is missing.");
                    return null;
                }

                long offset = Int(views[viewIndex], "byteOffset");
                long length = Int(views[viewIndex], "byteLength");
                if (offset < 0 || length < 0 || offset + length > data.Length)
                {
                    throw new ModelFormatException("A glTF image lies outside its buffer.");
                }

                return data.AsSpan((int)offset, (int)length).ToArray();
            }

            var uri = Str(image, "uri");
            if (uri is null)
            {
                return null;
            }

            if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = uri.IndexOf(',');
                return comma > 0 && uri[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase) ? Convert.FromBase64String(uri[(comma + 1)..]) : null;
            }

            var decoded = Uri.UnescapeDataString(uri);
            if (_options.ResolveCompanion?.Invoke(decoded) is { } file)
            {
                return file;
            }

            _model.Warnings.Add($"The image '{uri}' is an external file and was not found among the companions.");
            return null;
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

            if (node.Mesh is not null && element.TryGetProperty("extensions", out var extensions)
                && extensions.TryGetProperty("EXT_mesh_gpu_instancing", out var instancing)
                && instancing.TryGetProperty("attributes", out var instanceAttributes))
            {
                ExpandInstances(node, element, instanceAttributes);
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

        /// <summary>
        /// EXT_mesh_gpu_instancing: the node becomes a group and each instance a child node with the mesh. Names and properties come
        /// from <c>extras.instances</c> when the writer (this library) stored them there.
        /// </summary>
        private void ExpandInstances(SceneNode node, JsonElement element, JsonElement attributes)
        {
            float[]? Attribute(string name, int width) =>
                attributes.TryGetProperty(name, out var a) && a.TryGetInt32(out var index) ? ReadAccessor(index, width) : null;

            var translation = Attribute("TRANSLATION", 3);
            var rotation = Attribute("ROTATION", 4);
            var scale = Attribute("SCALE", 3);
            var count = Math.Max(translation?.Length / 3 ?? 0, Math.Max(rotation?.Length / 4 ?? 0, scale?.Length / 3 ?? 0));
            if (count == 0 || count > 10_000_000)
            {
                return;
            }

            var names = new List<JsonElement>();
            if (element.TryGetProperty("extras", out var extras) && extras.ValueKind == JsonValueKind.Object)
            {
                names = Array(extras, "instances");
            }

            var mesh = node.Mesh!;
            node.Mesh = null;
            for (var i = 0; i < count; i++)
            {
                var t = translation is not null && translation.Length >= 3 * (i + 1) ? new Vector3(translation[3 * i], translation[3 * i + 1], translation[3 * i + 2]) : Vector3.Zero;
                var r = rotation is not null && rotation.Length >= 4 * (i + 1) ? Quaternion.Normalize(new Quaternion(rotation[4 * i], rotation[4 * i + 1], rotation[4 * i + 2], rotation[4 * i + 3])) : Quaternion.Identity;
                var s = scale is not null && scale.Length >= 3 * (i + 1) ? new Vector3(scale[3 * i], scale[3 * i + 1], scale[3 * i + 2]) : Vector3.One;
                var instance = new SceneNode(i < names.Count ? Str(names[i], "name") : null)
                {
                    Mesh = mesh,
                    Transform = Matrix4x4.CreateScale(s) * Matrix4x4.CreateFromQuaternion(r) * Matrix4x4.CreateTranslation(t),
                };
                if (i < names.Count && names[i].ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in names[i].EnumerateObject().Where(p => p.Name != "name" && p.Value.ValueKind == JsonValueKind.String))
                    {
                        instance.Properties[property.Name] = property.Value.GetString()!;
                    }
                }

                node.Children.Add(instance);
            }
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
            if (index >= 0 && index < meshes.Count)
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
            var texCoords = new List<float>();
            var anyTexCoords = false;
            var pointsOnly = false;
            var trianglesSeen = false;
            var linesSeen = false;
            var edgeIndices = new List<uint>();
            var edgeMaterial = -1;
            foreach (var primitive in Array(element, "primitives"))
            {
                _options.Cancellation.ThrowIfCancellationRequested();
                var mode = primitive.TryGetProperty("mode", out var m) && m.TryGetInt32(out var mm) ? mm : 4;
                if (mode == 1 && primitive.TryGetProperty("extras", out var primitiveExtras) && Str(primitiveExtras, "role") == "edges")
                {
                    continue; // feature edges written by this library: derived data, recomputed on demand
                }

                if (mode == 1)
                {
                    linesSeen = true;
                    AddLines(primitive, positions, edgeIndices, ref edgeMaterial);
                    continue;
                }

                if (mode == 0)
                {
                    pointsOnly = true;
                }
                else
                {
                    trianglesSeen = true;
                }

                if (mode is not (0 or 4 or 5 or 6) || !primitive.TryGetProperty("attributes", out var attributes) || !attributes.TryGetProperty("POSITION", out var pa) || !pa.TryGetInt32(out var positionAccessor))
                {
                    _model.Warnings.Add("A primitive that is not a triangle list was skipped.");
                    continue;
                }

                var draco = DracoGeometryFor(primitive);
                var p = draco is not null ? DracoValues(draco, primitive, "POSITION", 3) : ReadAccessor(positionAccessor, 3);
                if (p is null)
                {
                    continue;
                }

                var vertexCount = p.Length / 3;
                var baseVertex = (uint)(positions.Count / 3);
                float[]? n = draco is not null
                    ? DracoValues(draco, primitive, "NORMAL", 3)
                    : attributes.TryGetProperty("NORMAL", out var na) && na.TryGetInt32(out var nai) ? ReadAccessor(nai, 3) : null;
                if (n is null || n.Length != p.Length)
                {
                    allNormals = false;
                }

                uint[] local;
                if (draco is not null)
                {
                    local = draco.Indices.Length > 0 ? draco.Indices : Enumerable.Range(0, vertexCount).Select(i => (uint)i).ToArray();
                }
                else if (primitive.TryGetProperty("indices", out var ia) && ia.TryGetInt32(out var iai))
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

                float[]? uv = draco is not null
                    ? DracoValues(draco, primitive, "TEXCOORD_0", 2)
                    : attributes.TryGetProperty("TEXCOORD_0", out var ta) && ta.TryGetInt32(out var tai) ? ReadAccessor(tai, 2) : null;
                if (uv is not null && uv.Length == vertexCount * 2)
                {
                    anyTexCoords = true;
                }
                else
                {
                    uv = null;
                }

                if (mode == 0)
                {
                    positions.AddRange(p);
                    normals.AddRange(n is not null && n.Length == p.Length ? n : new float[p.Length]);
                    texCoords.AddRange(uv ?? new float[vertexCount * 2]);
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
                texCoords.AddRange(uv ?? new float[vertexCount * 2]);
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

            _ = linesSeen;
            var mesh = new MeshData { Name = Str(element, "name"), Positions = positions.ToArray(), Indices = indices.ToArray() };
            if (allNormals)
            {
                mesh.Normals = normals.ToArray();
            }

            if (anyTexCoords && texCoords.Count == positions.Count / 3 * 2)
            {
                mesh.TexCoords = texCoords.ToArray();
            }

            if (groups.Count > 1 || groups.Any(g => g.Material >= 0))
            {
                mesh.Groups.AddRange(groups);
            }

            if (edgeIndices.Count > 0)
            {
                // Vertices of line primitives join the array after the others, so the UVs no longer line up.
                if (texCoords.Count != positions.Count / 3 * 2)
                {
                    mesh.TexCoords = [];
                }

                // Line primitives (a 2D drawing, a toolpath) keep their lines as edges of the mesh; the vertices of every primitive share one array.
                mesh.EdgeIndices = edgeIndices.ToArray();
                mesh.EdgeMaterial = edgeMaterial;
                if (normals.Count != positions.Count)
                {
                    mesh.Normals = [];
                }
            }

            if (pointsOnly && trianglesSeen)
            {
                _model.Warnings.Add("A mesh mixes points and triangles; only the triangles are kept.");
            }

            return mesh;
        }

        /// <summary>A LINES primitive: its vertices join the mesh's array and its index pairs become edges.</summary>
        private void AddLines(JsonElement primitive, List<float> positions, List<uint> edgeIndices, ref int edgeMaterial)
        {
            if (!primitive.TryGetProperty("attributes", out var attributes) || !attributes.TryGetProperty("POSITION", out var pa) || !pa.TryGetInt32(out var positionAccessor))
            {
                return;
            }

            var p = ReadAccessor(positionAccessor, 3);
            if (p is null)
            {
                return;
            }

            var vertexCount = p.Length / 3;
            uint[] local = primitive.TryGetProperty("indices", out var ia) && ia.TryGetInt32(out var iai)
                ? ReadIndices(iai) ?? []
                : Enumerable.Range(0, vertexCount).Select(i => (uint)i).ToArray();
            if (local.Any(i => i >= vertexCount))
            {
                _model.Warnings.Add("A line primitive has indices outside its vertex range and was skipped.");
                return;
            }

            var baseVertex = (uint)(positions.Count / 3);
            positions.AddRange(p);
            for (var i = 0; i + 1 < local.Length; i += 2)
            {
                edgeIndices.Add(baseVertex + local[i]);
                edgeIndices.Add(baseVertex + local[i + 1]);
            }

            if (edgeMaterial < 0 && primitive.TryGetProperty("material", out var mat) && mat.TryGetInt32(out var mi) && mi >= 0 && mi < _model.Materials.Count)
            {
                edgeMaterial = mi;
            }
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

        private readonly Dictionary<int, Internal.DracoGeometry?> _draco = new();

        /// <summary>The decoded <c>KHR_draco_mesh_compression</c> geometry of a primitive (each buffer view is decoded once), or null for an ordinary primitive.</summary>
        private Internal.DracoGeometry? DracoGeometryFor(JsonElement primitive)
        {
            if (!primitive.TryGetProperty("extensions", out var extensions) || extensions.ValueKind != JsonValueKind.Object
                || !extensions.TryGetProperty("KHR_draco_mesh_compression", out var ext) || ext.ValueKind != JsonValueKind.Object
                || !ext.TryGetProperty("bufferView", out var bv) || !bv.TryGetInt32(out var viewIndex))
            {
                return null;
            }

            if (_draco.TryGetValue(viewIndex, out var cached))
            {
                return cached;
            }

            Internal.DracoGeometry? geometry = null;
            var views = Array(_root, "bufferViews");
            if (viewIndex >= 0 && viewIndex < views.Count && views[viewIndex].TryGetProperty("buffer", out var b) && b.TryGetInt32(out var bi) && Buffer(bi) is { } data)
            {
                long offset = Int(views[viewIndex], "byteOffset");
                long length = Int(views[viewIndex], "byteLength");
                if (offset < 0 || length < 0 || offset + length > data.Length)
                {
                    throw new ModelFormatException("A Draco buffer view lies outside its buffer.");
                }

                geometry = Internal.DracoDecoder.Decode(data.AsSpan((int)offset, (int)length).ToArray(), _options.MaxTriangles, _options.Cancellation);
            }
            else
            {
                _model.Warnings.Add("A Draco-compressed primitive refers to data that is missing.");
            }

            _draco[viewIndex] = geometry;
            return geometry;
        }

        /// <summary>One semantic of a Draco primitive as floats (the file's attribute id comes from the extension), scaled like the accessor says for normalised integers.</summary>
        private float[]? DracoValues(Internal.DracoGeometry geometry, JsonElement primitive, string semantic, int components)
        {
            var ext = primitive.GetProperty("extensions").GetProperty("KHR_draco_mesh_compression");
            if (!ext.TryGetProperty("attributes", out var map) || !map.TryGetProperty(semantic, out var idValue) || !idValue.TryGetInt32(out var id))
            {
                return null;
            }

            var attribute = geometry.Attributes.Find(a => a.UniqueId == id);
            if (attribute is null || attribute.NumComponents != components)
            {
                return null;
            }

            var scale = 1f;
            if (primitive.TryGetProperty("attributes", out var attributes) && attributes.TryGetProperty(semantic, out var accessorValue) && accessorValue.TryGetInt32(out var accessorIndex))
            {
                var accessors = Array(_root, "accessors");
                if (accessorIndex >= 0 && accessorIndex < accessors.Count && accessors[accessorIndex].TryGetProperty("normalized", out var nz) && nz.ValueKind == JsonValueKind.True)
                {
                    scale = Int(accessors[accessorIndex], "componentType") switch { 5120 => 1 / 127f, 5121 => 1 / 255f, 5122 => 1 / 32767f, 5123 => 1 / 65535f, _ => 1f };
                }
            }

            if (scale == 1f)
            {
                return attribute.Values;
            }

            var scaled = new float[attribute.Values.Length];
            for (var i = 0; i < scaled.Length; i++)
            {
                scaled[i] = attribute.Values[i] * scale;
            }

            return scaled;
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
                else if (_options.ResolveCompanion?.Invoke(Uri.UnescapeDataString(uri)) is { } file)
                {
                    result = file;
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
            byte[]? data;
            var viewOffset = Int(v, "byteOffset");
            var stride = Int(v, "byteStride");
            if (v.TryGetProperty("extensions", out var viewExtensions) && viewExtensions.TryGetProperty("EXT_meshopt_compression", out var meshopt))
            {
                data = DecodeMeshoptView(viewIndex, meshopt);
                viewOffset = 0;
                stride = Int(meshopt, "byteStride");
            }
            else
            {
                data = v.TryGetProperty("buffer", out var b) && b.TryGetInt32(out var bi) ? Buffer(bi) : null;
            }

            if (data is null)
            {
                return null;
            }

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

        private readonly Dictionary<int, byte[]?> _meshoptViews = new();

        /// <summary>Decompresses a buffer view stored with EXT_meshopt_compression (cached: several accessors can share it).</summary>
        private byte[]? DecodeMeshoptView(int viewIndex, JsonElement extension)
        {
            if (_meshoptViews.TryGetValue(viewIndex, out var cached))
            {
                return cached;
            }

            byte[]? decoded = null;
            var source = extension.TryGetProperty("buffer", out var bufferIndex) && bufferIndex.TryGetInt32(out var bi) ? Buffer(bi) : null;
            if (source is not null)
            {
                var offset = Int(extension, "byteOffset");
                var length = Int(extension, "byteLength");
                if (offset < 0 || length < 0 || (long)offset + length > source.Length)
                {
                    throw new ModelFormatException("A meshopt buffer view lies outside its buffer.");
                }

                _options.Cancellation.ThrowIfCancellationRequested();
                decoded = Internal.MeshoptDecoder.Decode(
                    Str(extension, "mode") ?? "",
                    Str(extension, "filter"),
                    source.AsSpan(offset, length),
                    Int(extension, "count"),
                    Int(extension, "byteStride"),
                    _options.Cancellation);
            }

            _meshoptViews[viewIndex] = decoded;
            return decoded;
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
