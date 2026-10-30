using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace XRay.ThreeD.Formats.Mesh;

/// <summary>
/// X3D (XML encoding, .x3d) and VRML97 (.wrl, .vrml, .x3dv-like text): both are parsed to one node tree and built by the same code.
/// Group, Transform (with centre and scale orientation), Switch, LOD, Shape with Material and the geometries IndexedFaceSet
/// (normals, colours, winding), the triangle/quad sets of X3D, line and point sets, ElevationGrid, Box, Sphere, Cylinder and Cone.
/// DEF/USE is honoured, so a shared Shape becomes a shared mesh. Textures, lights, animation and scripts are ignored.
/// </summary>
internal static class X3dReader
{
    private const int MaxDepth = 64;

    // ---- node tree -----------------------------------------------------------------------------------------------

    internal sealed class WNode
    {
        public string Type = "";
        public string? Def;
        public readonly Dictionary<string, object> Fields = new(StringComparer.Ordinal);
        public MeshData? CachedMesh;

        public WNode? Node(string field) => Fields.TryGetValue(field, out var v) ? v as WNode : null;

        public List<WNode> Nodes(string field) => Fields.TryGetValue(field, out var v) ? v switch
        {
            List<WNode> list => list,
            WNode single => [single],
            _ => [],
        } : [];

        public double[] Numbers(string field)
        {
            if (!Fields.TryGetValue(field, out var v))
            {
                return [];
            }

            return v switch
            {
                double[] d => d,
                string s => Parse(s),
                _ => [],
            };
        }

        public bool Bool(string field, bool fallback)
        {
            if (!Fields.TryGetValue(field, out var v))
            {
                return fallback;
            }

            return v switch
            {
                string s => s.Equals("true", StringComparison.OrdinalIgnoreCase),
                double[] d when d.Length > 0 => d[0] != 0,
                _ => fallback,
            };
        }

        public string? Text(string field) => Fields.TryGetValue(field, out var v) && v is string s ? s : null;

        private static double[] Parse(string s) => s.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(t => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0).ToArray();
    }

    public static Model3D ReadXml(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        XDocument document;
        using (var stream = new MemoryStream(data.ToArray(), writable: false))
        using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }))
        {
            document = XDocument.Load(reader);
        }

        var root = document.Root;
        if (root is null || root.Name.LocalName != "X3D")
        {
            throw new ModelFormatException("Not an X3D document.");
        }

        var defs = new Dictionary<string, WNode>(StringComparer.Ordinal);
        var scene = root.Elements().FirstOrDefault(e => e.Name.LocalName == "Scene") ?? throw new ModelFormatException("The X3D file has no Scene.");
        var top = new WNode { Type = "Group" };
        var children = new List<WNode>();
        foreach (var element in scene.Elements())
        {
            if (FromXml(element, defs, 0) is { } n)
            {
                children.Add(n);
            }
        }

        top.Fields["children"] = children;
        var model = Build(top, ModelFormats.X3d, options);
        model.Metadata.FormatVersion = (string?)root.Attribute("version");
        foreach (var meta in root.Descendants().Where(e => e.Name.LocalName == "meta"))
        {
            var name = (string?)meta.Attribute("name");
            var content = (string?)meta.Attribute("content");
            if (name is not null && content is not null)
            {
                model.Metadata.Properties.Add(new ModelProperty(name, content, "x3d"));
                if (name == "title")
                {
                    model.Metadata.Title = content;
                }
            }
        }

        return model;
    }

    private static WNode? FromXml(XElement element, Dictionary<string, WNode> defs, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new ModelFormatException("X3D nodes are nested too deeply.");
        }

        var use = (string?)element.Attribute("USE");
        if (use is not null)
        {
            return defs.TryGetValue(use, out var shared) ? shared : null;
        }

        var node = new WNode { Type = element.Name.LocalName, Def = (string?)element.Attribute("DEF") };
        if (node.Def is not null)
        {
            defs[node.Def] = node;
        }

        foreach (var attribute in element.Attributes())
        {
            if (attribute.Name.LocalName is not ("DEF" or "USE"))
            {
                node.Fields[attribute.Name.LocalName] = attribute.Value;
            }
        }

        foreach (var child in element.Elements())
        {
            var built = FromXml(child, defs, depth + 1);
            if (built is null)
            {
                continue;
            }

            var container = (string?)child.Attribute("containerField") ?? DefaultContainer(built.Type);
            if (container == "children")
            {
                if (!node.Fields.TryGetValue("children", out var list) || list is not List<WNode> l)
                {
                    node.Fields["children"] = l = new List<WNode>();
                }

                l.Add(built);
            }
            else
            {
                node.Fields[container] = built;
            }
        }

        return node;
    }

    private static string DefaultContainer(string type) => type switch
    {
        "Coordinate" => "coord",
        "Normal" => "normal",
        "Color" or "ColorRGBA" => "color",
        "TextureCoordinate" => "texCoord",
        "Appearance" => "appearance",
        "Material" => "material",
        "ImageTexture" or "PixelTexture" or "MovieTexture" => "texture",
        "Box" or "Sphere" or "Cylinder" or "Cone" or "IndexedFaceSet" or "IndexedTriangleSet" or "TriangleSet" or "IndexedTriangleFanSet" or "TriangleFanSet"
            or "IndexedTriangleStripSet" or "TriangleStripSet" or "IndexedQuadSet" or "QuadSet" or "IndexedLineSet" or "LineSet" or "PointSet" or "ElevationGrid" => "geometry",
        _ => "children",
    };

    // ---- VRML97 ----------------------------------------------------------------------------------------------------

    public static Model3D ReadVrml(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var text = Encoding.UTF8.GetString(data.Span);
        if (!text.StartsWith("#VRML V2.0", StringComparison.Ordinal))
        {
            throw new UnsupportedModelFormatException(ModelFormats.Vrml, "Only VRML 2.0 (VRML97) files are supported; VRML 1.0 uses a different node model.");
        }

        var parser = new VrmlParser(text);
        var top = new WNode { Type = "Group" };
        var children = new List<WNode>();
        parser.ParseTopLevel(children);
        top.Fields["children"] = children;
        return Build(top, ModelFormats.Vrml, options);
    }

    private sealed class VrmlParser
    {
        private readonly List<string> _tokens = new();
        private int _p;
        private readonly Dictionary<string, WNode> _defs = new(StringComparer.Ordinal);

        public VrmlParser(string text)
        {
            var sb = new StringBuilder();
            var i = 0;
            void Flush()
            {
                if (sb.Length > 0)
                {
                    _tokens.Add(sb.ToString());
                    sb.Clear();
                }
            }

            while (i < text.Length)
            {
                var c = text[i];
                if (c == '#')
                {
                    Flush();
                    while (i < text.Length && text[i] != '\n')
                    {
                        i++;
                    }
                }
                else if (c == '"')
                {
                    Flush();
                    var start = ++i;
                    while (i < text.Length && text[i] != '"')
                    {
                        if (text[i] == '\\')
                        {
                            i++;
                        }

                        i++;
                    }

                    _tokens.Add("\"" + text[start..Math.Min(i, text.Length)]);
                    i++;
                }
                else if (c is '{' or '}' or '[' or ']')
                {
                    Flush();
                    _tokens.Add(c.ToString());
                    i++;
                }
                else if (char.IsWhiteSpace(c) || c == ',')
                {
                    Flush();
                    i++;
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }

            Flush();
        }

        public void ParseTopLevel(List<WNode> into)
        {
            while (_p < _tokens.Count)
            {
                if (ParseNode(0) is { } node)
                {
                    into.Add(node);
                }
            }
        }

        private string Peek() => _p < _tokens.Count ? _tokens[_p] : "";

        private WNode? ParseNode(int depth)
        {
            if (depth > MaxDepth)
            {
                throw new ModelFormatException("VRML nodes are nested too deeply.");
            }

            var token = _tokens[_p++];
            if (token == "DEF")
            {
                var name = Next();
                var node = ParseNode(depth);
                if (node is not null)
                {
                    node.Def = name;
                    _defs[name] = node;
                }

                return node;
            }

            if (token == "USE")
            {
                return _defs.GetValueOrDefault(Next());
            }

            if (token == "PROTO" || token == "EXTERNPROTO")
            {
                SkipProto();
                return null;
            }

            if (token is "ROUTE")
            {
                _p += 3; // from.event TO to.event
                if (_p <= _tokens.Count && Peek() == "TO")
                {
                    _p++;
                }

                _p++;
                return null;
            }

            if (Peek() != "{")
            {
                throw new ModelFormatException($"VRML: '{{' expected after '{token}'.");
            }

            _p++;
            var result = new WNode { Type = token };
            while (_p < _tokens.Count && Peek() != "}")
            {
                var field = _tokens[_p++];
                result.Fields[field] = ParseValue(depth);
            }

            if (_p >= _tokens.Count)
            {
                throw new ModelFormatException("VRML: unterminated node.");
            }

            _p++; // }
            return result;
        }

        private void SkipProto()
        {
            // PROTO name [ interface ] { body }: skip the bracket and the brace group.
            Next();
            var depth = 0;
            while (_p < _tokens.Count)
            {
                var t = _tokens[_p++];
                if (t is "[" or "{")
                {
                    depth++;
                }
                else if (t is "]" or "}")
                {
                    depth--;
                    if (depth == 0 && t == "}")
                    {
                        return;
                    }
                }
            }
        }

        private string Next() => _p < _tokens.Count ? _tokens[_p++] : throw new ModelFormatException("VRML: unexpected end of file.");

        private bool IsNodeStart()
        {
            var t = Peek();
            return t is "DEF" or "USE" || (_p + 1 < _tokens.Count && _tokens[_p + 1] == "{" && t.Length > 0 && !IsNumber(t));
        }

        private static bool IsNumber(string t) => double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

        private object ParseValue(int depth)
        {
            var t = Peek();
            if (t == "[")
            {
                _p++;
                if (IsNodeStart())
                {
                    var nodes = new List<WNode>();
                    while (_p < _tokens.Count && Peek() != "]")
                    {
                        if (ParseNode(depth + 1) is { } n)
                        {
                            nodes.Add(n);
                        }
                    }

                    _p++;
                    return nodes;
                }

                var numbers = new List<double>();
                var strings = new List<string>();
                while (_p < _tokens.Count && Peek() != "]")
                {
                    var item = _tokens[_p++];
                    if (item.StartsWith('"'))
                    {
                        strings.Add(item[1..]);
                    }
                    else if (double.TryParse(item, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    {
                        numbers.Add(d);
                    }
                    else if (item.Equals("TRUE", StringComparison.Ordinal))
                    {
                        numbers.Add(1);
                    }
                    else if (item.Equals("FALSE", StringComparison.Ordinal))
                    {
                        numbers.Add(0);
                    }
                }

                _p++;
                return strings.Count > 0 ? string.Join(' ', strings) : numbers.ToArray();
            }

            if (IsNodeStart())
            {
                return ParseNode(depth + 1) ?? (object)"";
            }

            if (t == "NULL")
            {
                _p++;
                return "";
            }

            if (t.StartsWith('"'))
            {
                _p++;
                return t[1..];
            }

            if (t is "TRUE" or "FALSE")
            {
                _p++;
                return new[] { t == "TRUE" ? 1.0 : 0.0 };
            }

            var values = new List<double>();
            while (_p < _tokens.Count && double.TryParse(Peek(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                values.Add(v);
                _p++;
            }

            if (values.Count == 0)
            {
                // An enumeration or other bare word.
                return Next();
            }

            return values.ToArray();
        }
    }

    // ---- building --------------------------------------------------------------------------------------------------

    private static Model3D Build(WNode top, FormatInfo format, LoadOptions options)
    {
        var model = new Model3D();
        model.Metadata.Format = format;
        model.Metadata.DocumentKind = "scene";
        model.Metadata.Units = LengthUnit.Meter;
        model.Metadata.UpAxis = UpAxis.Y;
        var builder = new SceneBuilder(model, options);
        foreach (var child in top.Nodes("children"))
        {
            if (builder.Convert(child, 0) is { } node)
            {
                model.Roots.Add(node);
            }
        }

        if (model.Meshes.Count == 0)
        {
            throw new ModelFormatException("The file holds no geometry this reader understands.");
        }

        model.Metadata.PartNames.AddRange(model.Nodes().Where(n => n.Mesh is not null && !string.IsNullOrEmpty(n.Name)).Select(n => n.Name!));
        model.Metadata.MaterialNames.AddRange(model.Materials.Select(m => m.Name!).Where(n => !string.IsNullOrEmpty(n)));
        model.Metadata.HasGeometry = true;
        model.Metadata.Statistics = model.ComputeStatistics();
        model.Metadata.Bounds = model.ComputeBounds();
        return model;
    }

    private sealed class SceneBuilder
    {
        private readonly Model3D _model;
        private readonly LoadOptions _options;
        private readonly Dictionary<WNode, int> _materials = new(ReferenceEqualityComparer.Instance);
        private long _triangles;

        public SceneBuilder(Model3D model, LoadOptions options)
        {
            _model = model;
            _options = options;
        }

        public SceneNode? Convert(WNode node, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new ModelFormatException("Nodes are nested too deeply.");
            }

            _options.Cancellation.ThrowIfCancellationRequested();
            switch (node.Type)
            {
                case "Transform":
                case "Group":
                case "Collision":
                case "Anchor":
                case "Billboard":
                case "Switch":
                case "LOD":
                case "StaticGroup":
                    {
                        var result = new SceneNode(node.Def) { Transform = node.Type == "Transform" ? TransformOf(node) : Matrix4x4.Identity };
                        var children = node.Nodes("children");
                        if (node.Type == "Switch")
                        {
                            children = node.Nodes("choice").Count > 0 ? node.Nodes("choice") : children;
                            var which = node.Numbers("whichChoice");
                            var index = which.Length > 0 ? (int)which[0] : -1;
                            children = index >= 0 && index < children.Count ? [children[index]] : [];
                        }
                        else if (node.Type == "LOD")
                        {
                            children = node.Nodes("level").Count > 0 ? node.Nodes("level").Take(1).ToList() : children.Take(1).ToList();
                        }

                        foreach (var child in children)
                        {
                            if (Convert(child, depth + 1) is { } sub)
                            {
                                result.Children.Add(sub);
                            }
                        }

                        return result.Children.Count == 0 ? null : result;
                    }

                case "Shape":
                    {
                        var geometry = node.Node("geometry");
                        if (geometry is null)
                        {
                            return null;
                        }

                        var mesh = geometry.CachedMesh ??= BuildGeometry(geometry, ReadMaterial(node.Node("appearance")?.Node("material")));
                        if (mesh is null)
                        {
                            return null;
                        }

                        if (!_model.Meshes.Contains(mesh))
                        {
                            _model.Meshes.Add(mesh);
                        }

                        return new SceneNode(node.Def ?? geometry.Def) { Mesh = mesh };
                    }

                default:
                    return null;
            }
        }

        private static Matrix4x4 TransformOf(WNode n)
        {
            Vector3 V(string f, Vector3 d) => n.Numbers(f) is { Length: >= 3 } v ? new Vector3((float)v[0], (float)v[1], (float)v[2]) : d;
            Quaternion R(string f)
            {
                var v = n.Numbers(f);
                if (v.Length < 4)
                {
                    return Quaternion.Identity;
                }

                var axis = new Vector3((float)v[0], (float)v[1], (float)v[2]);
                return axis.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), (float)v[3]);
            }

            var translation = V("translation", Vector3.Zero);
            var center = V("center", Vector3.Zero);
            var scale = V("scale", Vector3.One);
            var rotation = R("rotation");
            var scaleOrientation = R("scaleOrientation");
            // P' = T * C * R * SR * S * SR^-1 * C^-1 * P; the row-vector product runs in the opposite order.
            return Matrix4x4.CreateTranslation(-center)
                * Matrix4x4.CreateFromQuaternion(Quaternion.Inverse(scaleOrientation))
                * Matrix4x4.CreateScale(scale)
                * Matrix4x4.CreateFromQuaternion(scaleOrientation)
                * Matrix4x4.CreateFromQuaternion(rotation)
                * Matrix4x4.CreateTranslation(center)
                * Matrix4x4.CreateTranslation(translation);
        }

        private int ReadMaterial(WNode? material)
        {
            if (material is null)
            {
                return -1;
            }

            if (_materials.TryGetValue(material, out var cached))
            {
                return cached;
            }

            var diffuse = material.Numbers("diffuseColor");
            var emissive = material.Numbers("emissiveColor");
            var color = diffuse.Length >= 3 ? diffuse : emissive.Length >= 3 && emissive.Any(v => v > 0) ? emissive : [0.8, 0.8, 0.8];
            var transparency = material.Numbers("transparency") is { Length: > 0 } t ? (float)t[0] : 0f;
            var m = new ModelMaterial { Name = material.Def, BaseColor = new Vector4((float)color[0], (float)color[1], (float)color[2], 1f - transparency) };
            _materials[material] = _model.Materials.Count;
            _model.Materials.Add(m);
            return _model.Materials.Count - 1;
        }

        private MeshData? BuildGeometry(WNode g, int material)
        {
            var mesh = g.Type switch
            {
                "IndexedFaceSet" => IndexedFaceSet(g),
                "IndexedTriangleSet" or "TriangleSet" or "IndexedTriangleFanSet" or "TriangleFanSet" or "IndexedTriangleStripSet" or "TriangleStripSet" or "IndexedQuadSet" or "QuadSet" => TriangleSets(g),
                "IndexedLineSet" or "LineSet" => LineSet(g),
                "PointSet" => PointSet(g),
                "ElevationGrid" => ElevationGrid(g),
                "Box" => Primitive.Box(g.Numbers("size") is { Length: >= 3 } s ? new Vector3((float)s[0], (float)s[1], (float)s[2]) : new Vector3(2, 2, 2)),
                "Sphere" => Primitive.Sphere(g.Numbers("radius") is { Length: > 0 } r ? (float)r[0] : 1f),
                "Cylinder" => Primitive.Cylinder(g.Numbers("radius") is { Length: > 0 } cr ? (float)cr[0] : 1f, g.Numbers("height") is { Length: > 0 } ch ? (float)ch[0] : 2f, g.Bool("top", true), g.Bool("bottom", true), g.Bool("side", true)),
                "Cone" => Primitive.Cone(g.Numbers("bottomRadius") is { Length: > 0 } br ? (float)br[0] : 1f, g.Numbers("height") is { Length: > 0 } h ? (float)h[0] : 2f, g.Bool("bottom", true), g.Bool("side", true)),
                _ => null,
            };
            if (mesh is null || mesh.VertexCount == 0)
            {
                return null;
            }

            mesh.Name = g.Def;
            if (material >= 0)
            {
                if (mesh.Indices.Length > 0)
                {
                    mesh.Groups.Add(new MeshGroup(0, mesh.Indices.Length, material));
                }
                else if (mesh.EdgeIndices.Length > 0)
                {
                    mesh.EdgeMaterial = material;
                }
            }

            _triangles += mesh.TriangleCount;
            if (_triangles > _options.MaxTriangles)
            {
                throw new ModelFormatException("The scene has more triangles than the configured limit.");
            }

            return mesh;
        }

        private static float[] Points(WNode? coordinate) => coordinate is null ? [] : coordinate.Numbers("point").Select(v => (float)v).ToArray();

        private static MeshData? IndexedFaceSet(WNode g)
        {
            var points = Points(g.Node("coord"));
            var coordIndex = g.Numbers("coordIndex").Select(v => (int)v).ToArray();
            if (points.Length < 9 || coordIndex.Length < 3)
            {
                return null;
            }

            var normals = g.Node("normal")?.Numbers("vector").Select(v => (float)v).ToArray() ?? [];
            var colors = g.Node("color") is { } cn ? cn.Numbers("color").Select(v => (float)v).ToArray() : [];
            var colorWidth = g.Node("color")?.Type == "ColorRGBA" ? 4 : 3;
            var normalIndex = g.Numbers("normalIndex").Select(v => (int)v).ToArray();
            var colorIndex = g.Numbers("colorIndex").Select(v => (int)v).ToArray();
            var normalPerVertex = g.Bool("normalPerVertex", true);
            var colorPerVertex = g.Bool("colorPerVertex", true);
            var ccw = g.Bool("ccw", true);
            var count = points.Length / 3;

            var positions = new List<float>();
            var outNormals = new List<float>();
            var outColors = new List<float>();
            var lookup = new Dictionary<(int, int, int), uint>();
            var indices = new List<uint>();
            var corner = 0;
            var face = 0;
            var polygon = new List<uint>();
            foreach (var ci in coordIndex)
            {
                if (ci >= 0)
                {
                    if (ci >= count)
                    {
                        throw new ModelFormatException("A face refers to a coordinate that does not exist.");
                    }

                    var ni = -1;
                    if (normals.Length > 0)
                    {
                        ni = normalPerVertex ? (normalIndex.Length > corner ? normalIndex[corner] : ci) : (normalIndex.Length > face ? normalIndex[face] : face);
                    }

                    var ki = -1;
                    if (colors.Length > 0)
                    {
                        ki = colorPerVertex ? (colorIndex.Length > corner ? colorIndex[corner] : ci) : (colorIndex.Length > face ? colorIndex[face] : face);
                    }

                    if (!lookup.TryGetValue((ci, ni, ki), out var index))
                    {
                        index = (uint)(positions.Count / 3);
                        lookup[(ci, ni, ki)] = index;
                        positions.AddRange([points[3 * ci], points[3 * ci + 1], points[3 * ci + 2]]);
                        if (normals.Length > 0)
                        {
                            if (ni < 0 || 3 * ni + 2 >= normals.Length)
                            {
                                outNormals.AddRange([0f, 0f, 0f]);
                            }
                            else
                            {
                                outNormals.AddRange([normals[3 * ni], normals[3 * ni + 1], normals[3 * ni + 2]]);
                            }
                        }

                        if (colors.Length > 0)
                        {
                            if (ki < 0 || colorWidth * ki + 2 >= colors.Length)
                            {
                                outColors.AddRange([0.8f, 0.8f, 0.8f, 1f]);
                            }
                            else
                            {
                                outColors.AddRange([colors[colorWidth * ki], colors[colorWidth * ki + 1], colors[colorWidth * ki + 2], colorWidth == 4 ? colors[colorWidth * ki + 3] : 1f]);
                            }
                        }
                    }

                    polygon.Add(index);
                    corner++;
                }
                else
                {
                    corner++;
                    face++;
                    for (var k = 1; k + 1 < polygon.Count; k++)
                    {
                        if (ccw)
                        {
                            indices.AddRange([polygon[0], polygon[k], polygon[k + 1]]);
                        }
                        else
                        {
                            indices.AddRange([polygon[0], polygon[k + 1], polygon[k]]);
                        }
                    }

                    polygon.Clear();
                }
            }

            for (var k = 1; k + 1 < polygon.Count; k++)
            {
                indices.AddRange(ccw ? [polygon[0], polygon[k], polygon[k + 1]] : [polygon[0], polygon[k + 1], polygon[k]]);
            }

            var mesh = new MeshData { Positions = positions.ToArray(), Indices = indices.ToArray() };
            if (outNormals.Count == positions.Count)
            {
                mesh.Normals = outNormals.ToArray();
            }

            if (outColors.Count == positions.Count / 3 * 4)
            {
                mesh.Colors = outColors.ToArray();
            }

            return mesh;
        }

        private static MeshData? TriangleSets(WNode g)
        {
            var points = Points(g.Node("coord"));
            var count = points.Length / 3;
            var index = g.Numbers("index").Select(v => (int)v).ToArray();
            var indices = new List<uint>();
            void Add(int a, int b, int c)
            {
                if (a < 0 || b < 0 || c < 0 || a >= count || b >= count || c >= count)
                {
                    throw new ModelFormatException("A triangle refers to a coordinate that does not exist.");
                }

                indices.AddRange([(uint)a, (uint)b, (uint)c]);
            }

            switch (g.Type)
            {
                case "TriangleSet":
                    for (var i = 0; i + 2 < count; i += 3)
                    {
                        Add(i, i + 1, i + 2);
                    }

                    break;
                case "IndexedTriangleSet":
                    for (var i = 0; i + 2 < index.Length; i += 3)
                    {
                        Add(index[i], index[i + 1], index[i + 2]);
                    }

                    break;
                case "QuadSet":
                    for (var i = 0; i + 3 < count; i += 4)
                    {
                        Add(i, i + 1, i + 2);
                        Add(i, i + 2, i + 3);
                    }

                    break;
                case "IndexedQuadSet":
                    for (var i = 0; i + 3 < index.Length; i += 4)
                    {
                        Add(index[i], index[i + 1], index[i + 2]);
                        Add(index[i], index[i + 2], index[i + 3]);
                    }

                    break;
                case "IndexedTriangleFanSet":
                case "IndexedTriangleStripSet":
                    {
                        var run = new List<int>();
                        foreach (var v in index.Append(-1))
                        {
                            if (v >= 0)
                            {
                                run.Add(v);
                                continue;
                            }

                            for (var k = 0; k + 2 < run.Count; k++)
                            {
                                if (g.Type == "IndexedTriangleFanSet")
                                {
                                    Add(run[0], run[k + 1], run[k + 2]);
                                }
                                else if ((k & 1) == 0)
                                {
                                    Add(run[k], run[k + 1], run[k + 2]);
                                }
                                else
                                {
                                    Add(run[k + 1], run[k], run[k + 2]);
                                }
                            }

                            run.Clear();
                        }

                        break;
                    }

                case "TriangleFanSet":
                case "TriangleStripSet":
                    {
                        var start = 0;
                        var counts = g.Numbers(g.Type == "TriangleFanSet" ? "fanCount" : "stripCount").Select(v => (int)v).ToArray();
                        foreach (var n in counts)
                        {
                            for (var k = 0; k + 2 < n && start + k + 2 < count; k++)
                            {
                                if (g.Type == "TriangleFanSet")
                                {
                                    Add(start, start + k + 1, start + k + 2);
                                }
                                else if ((k & 1) == 0)
                                {
                                    Add(start + k, start + k + 1, start + k + 2);
                                }
                                else
                                {
                                    Add(start + k + 1, start + k, start + k + 2);
                                }
                            }

                            start += n;
                        }

                        break;
                    }
            }

            var mesh = new MeshData { Positions = points, Indices = indices.ToArray() };
            if (g.Node("normal")?.Numbers("vector") is { } nv && nv.Length == points.Length)
            {
                mesh.Normals = nv.Select(v => (float)v).ToArray();
            }

            return mesh;
        }

        private static MeshData? LineSet(WNode g)
        {
            var points = Points(g.Node("coord"));
            var count = points.Length / 3;
            var lines = new List<uint>();
            if (g.Type == "IndexedLineSet")
            {
                var run = new List<int>();
                foreach (var v in g.Numbers("coordIndex").Select(v => (int)v).Append(-1))
                {
                    if (v >= 0 && v < count)
                    {
                        run.Add(v);
                        continue;
                    }

                    for (var k = 0; k + 1 < run.Count; k++)
                    {
                        lines.AddRange([(uint)run[k], (uint)run[k + 1]]);
                    }

                    run.Clear();
                }
            }
            else
            {
                var start = 0;
                foreach (var n in g.Numbers("vertexCount").Select(v => (int)v))
                {
                    for (var k = 0; k + 1 < n && start + k + 1 < count; k++)
                    {
                        lines.AddRange([(uint)(start + k), (uint)(start + k + 1)]);
                    }

                    start += n;
                }
            }

            return new MeshData { Positions = points, EdgeIndices = lines.ToArray() };
        }

        private static MeshData? PointSet(WNode g)
        {
            var points = Points(g.Node("coord"));
            var mesh = new MeshData { Positions = points };
            if (g.Node("color")?.Numbers("color") is { } colors && g.Node("color")!.Type == "Color" && colors.Length == points.Length)
            {
                mesh.Colors = Enumerable.Range(0, points.Length / 3).SelectMany(i => new[] { (float)colors[3 * i], (float)colors[3 * i + 1], (float)colors[3 * i + 2], 1f }).ToArray();
            }

            return mesh;
        }

        private static MeshData? ElevationGrid(WNode g)
        {
            var nx = (int)(g.Numbers("xDimension") is { Length: > 0 } a ? a[0] : 0);
            var nz = (int)(g.Numbers("zDimension") is { Length: > 0 } b ? b[0] : 0);
            var dx = g.Numbers("xSpacing") is { Length: > 0 } c ? (float)c[0] : 1f;
            var dz = g.Numbers("zSpacing") is { Length: > 0 } d ? (float)d[0] : 1f;
            var heights = g.Numbers("height");
            if (nx < 2 || nz < 2 || heights.Length < nx * nz || (long)nx * nz > 50_000_000)
            {
                return null;
            }

            var positions = new float[nx * nz * 3];
            for (var z = 0; z < nz; z++)
            {
                for (var x = 0; x < nx; x++)
                {
                    var i = z * nx + x;
                    positions[3 * i] = x * dx;
                    positions[3 * i + 1] = (float)heights[i];
                    positions[3 * i + 2] = z * dz;
                }
            }

            var indices = new List<uint>();
            for (var z = 0; z + 1 < nz; z++)
            {
                for (var x = 0; x + 1 < nx; x++)
                {
                    uint i0 = (uint)(z * nx + x), i1 = i0 + 1, i2 = (uint)((z + 1) * nx + x), i3 = i2 + 1;
                    indices.AddRange([i0, i2, i1, i1, i2, i3]);
                }
            }

            return new MeshData { Positions = positions, Indices = indices.ToArray() };
        }
    }

    /// <summary>The X3D/VRML primitives, tessellated with enough segments to look round.</summary>
    internal static class Primitive
    {
        private const int Segments = 32;

        public static MeshData Box(Vector3 size)
        {
            var h = size / 2;
            var p = new List<float>();
            var n = new List<float>();
            var i = new List<uint>();
            void Face(Vector3 normal, Vector3 u, Vector3 v)
            {
                var start = (uint)(p.Count / 3);
                foreach (var (a, b) in new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) })
                {
                    var q = normal * 1f + u * a + v * b;
                    p.AddRange([q.X * h.X, q.Y * h.Y, q.Z * h.Z]);
                    n.AddRange([normal.X, normal.Y, normal.Z]);
                }

                i.AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
            }

            Face(Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY);
            Face(-Vector3.UnitZ, -Vector3.UnitX, Vector3.UnitY);
            Face(Vector3.UnitX, -Vector3.UnitZ, Vector3.UnitY);
            Face(-Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY);
            Face(Vector3.UnitY, Vector3.UnitX, -Vector3.UnitZ);
            Face(-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ);
            return new MeshData { Positions = p.ToArray(), Normals = n.ToArray(), Indices = i.ToArray() };
        }

        public static MeshData Sphere(float radius)
        {
            var rings = Segments / 2;
            var p = new List<float>();
            var n = new List<float>();
            var i = new List<uint>();
            for (var r = 0; r <= rings; r++)
            {
                var phi = MathF.PI * r / rings;
                for (var s = 0; s <= Segments; s++)
                {
                    var theta = 2 * MathF.PI * s / Segments;
                    var normal = new Vector3(MathF.Sin(phi) * MathF.Sin(theta), MathF.Cos(phi), MathF.Sin(phi) * MathF.Cos(theta));
                    p.AddRange([normal.X * radius, normal.Y * radius, normal.Z * radius]);
                    n.AddRange([normal.X, normal.Y, normal.Z]);
                }
            }

            for (var r = 0; r < rings; r++)
            {
                for (var s = 0; s < Segments; s++)
                {
                    var a = (uint)(r * (Segments + 1) + s);
                    var b = a + Segments + 1;
                    i.AddRange([a, a + 1, b, a + 1, b + 1, b]);
                }
            }

            return new MeshData { Positions = p.ToArray(), Normals = n.ToArray(), Indices = i.ToArray() };
        }

        public static MeshData Cylinder(float radius, float height, bool top, bool bottom, bool side)
        {
            var p = new List<float>();
            var n = new List<float>();
            var i = new List<uint>();
            var h = height / 2;
            if (side)
            {
                for (var s = 0; s <= Segments; s++)
                {
                    var t = 2 * MathF.PI * s / Segments;
                    float x = MathF.Sin(t), z = MathF.Cos(t);
                    p.AddRange([x * radius, h, z * radius, x * radius, -h, z * radius]);
                    n.AddRange([x, 0, z, x, 0, z]);
                }

                for (var s = 0; s < Segments; s++)
                {
                    var a = (uint)(2 * s);
                    i.AddRange([a, a + 1, a + 2, a + 1, a + 3, a + 2]);
                }
            }

            void Cap(float y, float ny)
            {
                var center = (uint)(p.Count / 3);
                p.AddRange([0, y, 0]);
                n.AddRange([0, ny, 0]);
                for (var s = 0; s <= Segments; s++)
                {
                    var t = 2 * MathF.PI * s / Segments;
                    p.AddRange([MathF.Sin(t) * radius, y, MathF.Cos(t) * radius]);
                    n.AddRange([0, ny, 0]);
                }

                for (var s = 0; s < Segments; s++)
                {
                    var a = center + 1 + (uint)s;
                    i.AddRange(ny > 0 ? [center, a + 1, a] : [center, a, a + 1]);
                }
            }

            if (top)
            {
                Cap(h, 1);
            }

            if (bottom)
            {
                Cap(-h, -1);
            }

            return new MeshData { Positions = p.ToArray(), Normals = n.ToArray(), Indices = i.ToArray() };
        }

        public static MeshData Cone(float radius, float height, bool bottom, bool side)
        {
            var p = new List<float>();
            var n = new List<float>();
            var i = new List<uint>();
            var h = height / 2;
            var slope = radius / height;
            if (side)
            {
                for (var s = 0; s <= Segments; s++)
                {
                    var t = 2 * MathF.PI * s / Segments;
                    float x = MathF.Sin(t), z = MathF.Cos(t);
                    var normal = Vector3.Normalize(new Vector3(x, slope, z));
                    p.AddRange([0, h, 0, x * radius, -h, z * radius]);
                    n.AddRange([normal.X, normal.Y, normal.Z, normal.X, normal.Y, normal.Z]);
                }

                for (var s = 0; s < Segments; s++)
                {
                    var a = (uint)(2 * s);
                    i.AddRange([a, a + 1, a + 3]);
                }
            }

            if (bottom)
            {
                var center = (uint)(p.Count / 3);
                p.AddRange([0, -h, 0]);
                n.AddRange([0, -1, 0]);
                for (var s = 0; s <= Segments; s++)
                {
                    var t = 2 * MathF.PI * s / Segments;
                    p.AddRange([MathF.Sin(t) * radius, -h, MathF.Cos(t) * radius]);
                    n.AddRange([0, -1, 0]);
                }

                for (var s = 0; s < Segments; s++)
                {
                    var a = center + 1 + (uint)s;
                    i.AddRange([center, a, a + 1]);
                }
            }

            return new MeshData { Positions = p.ToArray(), Normals = n.ToArray(), Indices = i.ToArray() };
        }
    }
}
