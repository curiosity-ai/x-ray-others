using System.Globalization;
using System.Numerics;
using System.Xml;
using System.Xml.Linq;

namespace XRay.ThreeD.Formats.Mesh;

/// <summary>
/// COLLADA 1.4/1.5 (.dae): the visual scene as a node tree with transforms (matrix, translate, rotate, scale), instances of
/// geometry (triangles, polylist, polygons, fans and strips with normals), materials resolved through bind_material to
/// effect colours, node instances, unit and up axis. Skinning, animation, cameras and lights are ignored.
/// </summary>
internal static class ColladaReader
{
    private const int MaxDepth = 64;

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        XDocument document;
        using (var stream = new MemoryStream(data.ToArray(), writable: false))
        using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
        {
            document = XDocument.Load(reader);
        }

        var root = document.Root;
        if (root is null || root.Name.LocalName != "COLLADA")
        {
            throw new ModelFormatException("Not a COLLADA document.");
        }

        return new Builder(root, options).Build();
    }

    private sealed class Builder
    {
        private readonly XElement _root;
        private readonly LoadOptions _options;
        private readonly Model3D _model = new();
        private readonly Dictionary<string, XElement> _byId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, MeshData> _meshes = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _materialByEffect = new(StringComparer.Ordinal);
        private long _triangles;

        public Builder(XElement root, LoadOptions options)
        {
            _root = root;
            _options = options;
            foreach (var e in root.Descendants())
            {
                if ((string?)e.Attribute("id") is { } id && !_byId.ContainsKey(id))
                {
                    _byId[id] = e;
                }
            }
        }

        public Model3D Build()
        {
            var meta = _model.Metadata;
            meta.Format = ModelFormats.Collada;
            meta.DocumentKind = "scene";
            meta.FormatVersion = (string?)_root.Attribute("version");
            var asset = _root.Elements().FirstOrDefault(e => e.Name.LocalName == "asset");
            var unitScale = 1.0;
            var up = "Y_UP";
            if (asset is not null)
            {
                if (asset.Elements().FirstOrDefault(e => e.Name.LocalName == "unit") is { } unit && double.TryParse((string?)unit.Attribute("meter"), NumberStyles.Float, CultureInfo.InvariantCulture, out var meter) && meter > 0)
                {
                    unitScale = meter;
                }

                up = asset.Elements().FirstOrDefault(e => e.Name.LocalName == "up_axis")?.Value.Trim() ?? up;
                if (asset.Descendants().FirstOrDefault(e => e.Name.LocalName == "authoring_tool")?.Value is { Length: > 0 } tool)
                {
                    meta.Application = tool.Trim();
                }

                if (asset.Descendants().FirstOrDefault(e => e.Name.LocalName == "author")?.Value is { Length: > 0 } author)
                {
                    meta.Author = author.Trim();
                }

                if (DateTimeOffset.TryParse(asset.Elements().FirstOrDefault(e => e.Name.LocalName == "created")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created))
                {
                    meta.Created = created;
                }

                if (DateTimeOffset.TryParse(asset.Elements().FirstOrDefault(e => e.Name.LocalName == "modified")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var modified))
                {
                    meta.Modified = modified;
                }
            }

            meta.UpAxis = up == "Z_UP" ? UpAxis.Z : UpAxis.Y;
            meta.Units = unitScale switch
            {
                1.0 => LengthUnit.Meter,
                0.001 => LengthUnit.Millimeter,
                0.01 => LengthUnit.Centimeter,
                0.0254 => LengthUnit.Inch,
                0.3048 => LengthUnit.Foot,
                _ => LengthUnit.Meter,
            };

            var scene = _root.Elements().FirstOrDefault(e => e.Name.LocalName == "scene")?.Elements().FirstOrDefault(e => e.Name.LocalName == "instance_visual_scene");
            var visual = scene is not null ? Resolve(scene) : _root.Descendants().FirstOrDefault(e => e.Name.LocalName == "visual_scene");
            var top = new List<SceneNode>();
            if (visual is not null)
            {
                foreach (var node in visual.Elements().Where(e => e.Name.LocalName == "node"))
                {
                    if (BuildNode(node, 0) is { } built)
                    {
                        top.Add(built);
                    }
                }
            }
            else
            {
                // No scene: instantiate every geometry once.
                foreach (var geometry in _root.Descendants().Where(e => e.Name.LocalName == "geometry"))
                {
                    if (BuildMesh(geometry, null) is { } mesh)
                    {
                        top.Add(new SceneNode((string?)geometry.Attribute("name") ?? (string?)geometry.Attribute("id")) { Mesh = mesh });
                    }
                }
            }

            if (Math.Abs(unitScale - (meta.Units?.ToMeters() ?? 1)) > 1e-12)
            {
                // A unit that is not one of the named ones: scale the scene to meters.
                var wrapper = new SceneNode(null) { Transform = Matrix4x4.CreateScale((float)unitScale) };
                wrapper.Children.AddRange(top);
                top = [wrapper];
            }

            foreach (var node in top)
            {
                _model.Roots.Add(node);
            }

            if (_model.Meshes.Count == 0)
            {
                throw new ModelFormatException("The COLLADA file holds no geometry.");
            }

            meta.PartNames.AddRange(_model.Nodes().Where(n => n.Mesh is not null && !string.IsNullOrEmpty(n.Name)).Select(n => n.Name!));
            meta.MaterialNames.AddRange(_model.Materials.Select(m => m.Name!).Where(n => !string.IsNullOrEmpty(n)));
            meta.HasGeometry = true;
            meta.Statistics = _model.ComputeStatistics();
            meta.Bounds = _model.ComputeBounds();
            return _model;
        }

        private XElement? Resolve(XElement instance)
        {
            var url = (string?)instance.Attribute("url") ?? (string?)instance.Attribute("source") ?? (string?)instance.Attribute("target") ?? "";
            return url.StartsWith('#') && _byId.TryGetValue(url[1..], out var target) ? target : null;
        }

        private SceneNode? BuildNode(XElement element, int depth)
        {
            if (depth > MaxDepth)
            {
                _model.Warnings.Add("Nodes are nested too deeply; the rest was skipped.");
                return null;
            }

            _options.Cancellation.ThrowIfCancellationRequested();
            var node = new SceneNode((string?)element.Attribute("name") ?? (string?)element.Attribute("id"));
            if (((string?)element.Attribute("type")) == "JOINT")
            {
                // Skeleton joints carry no geometry of their own; keep the structure only when something hangs below.
            }

            var transform = Matrix4x4.Identity;
            foreach (var child in element.Elements())
            {
                var values = () => child.Value.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                switch (child.Name.LocalName)
                {
                    case "matrix":
                        {
                            var v = values();
                            if (v.Length == 16)
                            {
                                // Column-vector matrix in row-major text; the row-vector form is its transpose.
                                transform = new Matrix4x4(v[0], v[4], v[8], v[12], v[1], v[5], v[9], v[13], v[2], v[6], v[10], v[14], v[3], v[7], v[11], v[15]) * transform;
                            }

                            break;
                        }

                    case "translate":
                        {
                            var v = values();
                            if (v.Length >= 3)
                            {
                                transform = Matrix4x4.CreateTranslation(v[0], v[1], v[2]) * transform;
                            }

                            break;
                        }

                    case "rotate":
                        {
                            var v = values();
                            if (v.Length >= 4 && new Vector3(v[0], v[1], v[2]).LengthSquared() > 0)
                            {
                                transform = Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(new Vector3(v[0], v[1], v[2])), v[3] * MathF.PI / 180f) * transform;
                            }

                            break;
                        }

                    case "scale":
                        {
                            var v = values();
                            if (v.Length >= 3)
                            {
                                transform = Matrix4x4.CreateScale(v[0], v[1], v[2]) * transform;
                            }

                            break;
                        }
                }
            }

            node.Transform = transform;

            foreach (var child in element.Elements())
            {
                switch (child.Name.LocalName)
                {
                    case "node":
                        if (BuildNode(child, depth + 1) is { } sub)
                        {
                            node.Children.Add(sub);
                        }

                        break;
                    case "instance_node":
                        if (Resolve(child) is { } target && BuildNode(target, depth + 1) is { } instanced)
                        {
                            node.Children.Add(instanced);
                        }

                        break;
                    case "instance_geometry":
                    case "instance_controller":
                        {
                            var geometry = Resolve(child);
                            if (geometry is not null && geometry.Name.LocalName == "controller")
                            {
                                var skin = geometry.Elements().FirstOrDefault(e => e.Name.LocalName == "skin");
                                geometry = skin is null ? null : Resolve(skin);
                            }

                            if (geometry is not null && BuildMesh(geometry, child) is { } mesh)
                            {
                                if (node.Mesh is null)
                                {
                                    node.Mesh = mesh;
                                }
                                else
                                {
                                    node.Children.Add(new SceneNode(mesh.Name) { Mesh = mesh });
                                }
                            }

                            break;
                        }
                }
            }

            return node;
        }

        private MeshData? BuildMesh(XElement geometry, XElement? instance)
        {
            var id = (string?)geometry.Attribute("id") ?? "";
            if (id.Length > 0 && _meshes.TryGetValue(id, out var cached))
            {
                return cached;
            }

            var meshElement = geometry.Elements().FirstOrDefault(e => e.Name.LocalName == "mesh");
            if (meshElement is null)
            {
                return null;
            }

            var sources = new Dictionary<string, (float[] Data, int Stride)>(StringComparer.Ordinal);
            foreach (var source in meshElement.Elements().Where(e => e.Name.LocalName == "source"))
            {
                var array = source.Elements().FirstOrDefault(e => e.Name.LocalName == "float_array");
                if (array is null)
                {
                    continue;
                }

                var values = array.Value.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                var accessor = source.Descendants().FirstOrDefault(e => e.Name.LocalName == "accessor");
                var stride = accessor is not null && int.TryParse((string?)accessor.Attribute("stride"), out var st) ? st : 3;
                sources[(string?)source.Attribute("id") ?? ""] = (values, Math.Max(1, stride));
            }

            var vertexInputs = meshElement.Elements().FirstOrDefault(e => e.Name.LocalName == "vertices");
            var vertexId = (string?)vertexInputs?.Attribute("id") ?? "";
            var positionSource = vertexInputs?.Elements().FirstOrDefault(e => e.Name.LocalName == "input" && (string?)e.Attribute("semantic") == "POSITION");
            if (positionSource is null || !sources.TryGetValue(((string?)positionSource.Attribute("source") ?? "").TrimStart('#'), out var positions) || positions.Stride < 3)
            {
                return null;
            }

            var vertexNormal = vertexInputs?.Elements().FirstOrDefault(e => e.Name.LocalName == "input" && (string?)e.Attribute("semantic") == "NORMAL");

            var materialSymbols = ResolveMaterials(instance);
            var lookup = new Dictionary<(int, int), uint>();
            var outPositions = new List<float>();
            var outNormals = new List<float>();
            var normalsComplete = true;
            var byMaterial = new Dictionary<int, List<uint>>();
            var order = new List<int>();

            foreach (var primitive in meshElement.Elements().Where(e => e.Name.LocalName is "triangles" or "polylist" or "polygons" or "trifans" or "tristrips"))
            {
                _options.Cancellation.ThrowIfCancellationRequested();
                var inputs = primitive.Elements().Where(e => e.Name.LocalName == "input").ToList();
                var stride = inputs.Count == 0 ? 1 : inputs.Max(i => int.TryParse((string?)i.Attribute("offset"), out var o) ? o : 0) + 1;
                var vertexInput = inputs.FirstOrDefault(i => (string?)i.Attribute("semantic") == "VERTEX");
                var normalInput = inputs.FirstOrDefault(i => (string?)i.Attribute("semantic") == "NORMAL");
                if (vertexInput is null)
                {
                    continue;
                }

                var vertexOffset = int.Parse((string?)vertexInput.Attribute("offset") ?? "0", CultureInfo.InvariantCulture);
                var normalOffset = normalInput is null ? -1 : int.Parse((string?)normalInput.Attribute("offset") ?? "0", CultureInfo.InvariantCulture);
                (float[] Data, int Stride) normals = ([], 3);
                var hasNormals = false;
                if (normalInput is not null && sources.TryGetValue(((string?)normalInput.Attribute("source") ?? "").TrimStart('#'), out normals))
                {
                    hasNormals = normals.Stride >= 3;
                }
                else if (vertexNormal is not null && sources.TryGetValue(((string?)vertexNormal.Attribute("source") ?? "").TrimStart('#'), out normals) && normals.Stride >= 3)
                {
                    hasNormals = true;
                    normalOffset = -2; // normals indexed by the vertex index
                }

                var materialSymbol = (string?)primitive.Attribute("material") ?? "";
                var material = materialSymbols.GetValueOrDefault(materialSymbol, -1);
                if (!byMaterial.TryGetValue(material, out var target))
                {
                    byMaterial[material] = target = new List<uint>();
                    order.Add(material);
                }

                uint Corner(int[] p, int corner)
                {
                    var vi = p[corner * stride + vertexOffset];
                    var ni = normalOffset switch { -1 => -1, -2 => vi, _ => p[corner * stride + normalOffset] };
                    if (vi < 0 || vi * positions.Stride + 2 >= positions.Data.Length)
                    {
                        throw new ModelFormatException("A COLLADA primitive refers to a vertex that does not exist.");
                    }

                    if (!lookup.TryGetValue((vi, ni), out var index))
                    {
                        index = (uint)(outPositions.Count / 3);
                        lookup[(vi, ni)] = index;
                        outPositions.Add(positions.Data[vi * positions.Stride]);
                        outPositions.Add(positions.Data[vi * positions.Stride + 1]);
                        outPositions.Add(positions.Data[vi * positions.Stride + 2]);
                        if (hasNormals && ni >= 0 && ni * normals.Stride + 2 < normals.Data.Length)
                        {
                            outNormals.Add(normals.Data[ni * normals.Stride]);
                            outNormals.Add(normals.Data[ni * normals.Stride + 1]);
                            outNormals.Add(normals.Data[ni * normals.Stride + 2]);
                        }
                        else
                        {
                            normalsComplete = false;
                            outNormals.AddRange([0f, 0f, 0f]);
                        }
                    }

                    return index;
                }

                int[] Ints(XElement element) => element.Value.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();

                void Fan(int[] p, int start, int count)
                {
                    for (var k = 1; k + 1 < count; k++)
                    {
                        target.Add(Corner(p, start));
                        target.Add(Corner(p, start + k));
                        target.Add(Corner(p, start + k + 1));
                        if (++_triangles > _options.MaxTriangles)
                        {
                            throw new ModelFormatException("The COLLADA has more triangles than the configured limit.");
                        }
                    }
                }

                switch (primitive.Name.LocalName)
                {
                    case "triangles":
                        {
                            var p = Ints(primitive.Elements().First(e => e.Name.LocalName == "p"));
                            var corners = p.Length / stride;
                            for (var c = 0; c + 2 < corners; c += 3)
                            {
                                Fan(p, c, 3);
                            }

                            break;
                        }

                    case "polylist":
                        {
                            var counts = Ints(primitive.Elements().First(e => e.Name.LocalName == "vcount"));
                            var p = Ints(primitive.Elements().First(e => e.Name.LocalName == "p"));
                            var cursor = 0;
                            foreach (var n in counts)
                            {
                                if (n < 3 || (cursor + n) * stride > p.Length)
                                {
                                    throw new ModelFormatException("A COLLADA polylist entry is malformed.");
                                }

                                Fan(p, cursor, n);
                                cursor += n;
                            }

                            break;
                        }

                    case "polygons":
                        foreach (var ph in primitive.Elements().Where(e => e.Name.LocalName == "p"))
                        {
                            var p = Ints(ph);
                            Fan(p, 0, p.Length / stride);
                        }

                        break;
                    case "trifans":
                        foreach (var ph in primitive.Elements().Where(e => e.Name.LocalName == "p"))
                        {
                            var p = Ints(ph);
                            Fan(p, 0, p.Length / stride);
                        }

                        break;
                    case "tristrips":
                        foreach (var ph in primitive.Elements().Where(e => e.Name.LocalName == "p"))
                        {
                            var p = Ints(ph);
                            var n = p.Length / stride;
                            for (var k = 0; k + 2 < n; k++)
                            {
                                var a = Corner(p, k);
                                var b = Corner(p, k + 1);
                                var c = Corner(p, k + 2);
                                if ((k & 1) == 0)
                                {
                                    target.AddRange([a, b, c]);
                                }
                                else
                                {
                                    target.AddRange([b, a, c]);
                                }
                            }
                        }

                        break;
                }
            }

            _ = vertexId;
            if (outPositions.Count == 0)
            {
                return null;
            }

            var mesh = new MeshData { Name = (string?)geometry.Attribute("name") ?? id, Positions = outPositions.ToArray() };
            if (normalsComplete)
            {
                mesh.Normals = outNormals.ToArray();
            }

            var indices = new List<uint>();
            foreach (var m in order)
            {
                var list = byMaterial[m];
                if (list.Count == 0)
                {
                    continue;
                }

                mesh.Groups.Add(new MeshGroup(indices.Count, list.Count, m));
                indices.AddRange(list);
            }

            mesh.Indices = indices.ToArray();
            if (mesh.Groups.Count == 1 && mesh.Groups[0].Material < 0)
            {
                mesh.Groups.Clear();
            }

            _model.Meshes.Add(mesh);
            if (id.Length > 0)
            {
                _meshes[id] = mesh;
            }

            return mesh;
        }

        /// <summary>bind_material: primitive symbol to the model's material index (created from the effect's diffuse colour).</summary>
        private Dictionary<string, int> ResolveMaterials(XElement? instance)
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            if (instance is null)
            {
                return result;
            }

            foreach (var im in instance.Descendants().Where(e => e.Name.LocalName == "instance_material"))
            {
                var symbol = (string?)im.Attribute("symbol") ?? "";
                var material = Resolve(im);
                if (material is null)
                {
                    continue;
                }

                var key = (string?)material.Attribute("id") ?? symbol;
                if (!_materialByEffect.TryGetValue(key, out var index))
                {
                    index = _model.Materials.Count;
                    _materialByEffect[key] = index;
                    _model.Materials.Add(ToMaterial(material));
                }

                result[symbol] = index;
            }

            return result;
        }

        private ModelMaterial ToMaterial(XElement material)
        {
            var result = new ModelMaterial { Name = (string?)material.Attribute("name") ?? (string?)material.Attribute("id") };
            var effect = material.Elements().FirstOrDefault(e => e.Name.LocalName == "instance_effect") is { } ie ? Resolve(ie) : null;
            var technique = effect?.Descendants().FirstOrDefault(e => e.Name.LocalName is "phong" or "blinn" or "lambert" or "constant");
            if (technique?.Elements().FirstOrDefault(e => e.Name.LocalName == "diffuse")?.Elements().FirstOrDefault(e => e.Name.LocalName == "color") is { } color)
            {
                var v = color.Value.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                if (v.Length >= 3)
                {
                    result.BaseColor = new Vector4(v[0], v[1], v[2], v.Length > 3 ? v[3] : 1f);
                }
            }

            if (technique?.Elements().FirstOrDefault(e => e.Name.LocalName == "transparency")?.Elements().FirstOrDefault(e => e.Name.LocalName == "float") is { } transparency
                && float.TryParse(transparency.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var opacity) && opacity < 1f)
            {
                result.BaseColor = new Vector4(result.BaseColor.X, result.BaseColor.Y, result.BaseColor.Z, opacity);
            }

            return result;
        }
    }
}
