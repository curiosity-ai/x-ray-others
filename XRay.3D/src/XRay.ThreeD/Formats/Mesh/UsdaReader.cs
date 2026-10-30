using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace XRay.ThreeD.Formats.Mesh;

using XRay.ThreeD.Internal;

/// <summary>
/// Universal Scene Description in its text form (.usda) and USDZ packages (text or crate root layer): the prim hierarchy with transform ops
/// (translate, rotateXYZ and the other Euler orders, scale, orient, transform) in xformOpOrder, Mesh prims (points, face counts and
/// indices, normals, display colours with their interpolation, GeomSubsets), the cube, sphere, cylinder, cone and capsule
/// primitives, materials bound through material:binding (UsdPreviewSurface diffuse colour, metallic, roughness, opacity), same-file
/// references, metersPerUnit and upAxis. The binary crate layer is read by <see cref="UsdcReader"/> into the same stage model. References to other files are not read.
/// </summary>
internal static class UsdaReader
{
    private const int MaxDepth = 64;

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var span = data.Span;
        if (span.Length > 4 && span[0] == 'P' && span[1] == 'K')
        {
            data = ReadUsdz(data, options);
            span = data.Span;
        }

        if (UsdcReader.IsCrate(span))
        {
            return new Builder(UsdcReader.ReadStage(data, options), options).Build();
        }

        var text = Encoding.UTF8.GetString(span);
        if (!text.StartsWith("#usda", StringComparison.Ordinal))
        {
            throw new ModelFormatException("Not a USDA text file.");
        }

        var parser = new Parser(text);
        var stage = parser.ParseStage();
        return new Builder(stage, options).Build();
    }

    /// <summary>The package's root layer: the first stored layer (text or crate) in the archive.</summary>
    private static ReadOnlyMemory<byte> ReadUsdz(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        using var zip = new ZipArchive(new MemoryStream(data.ToArray(), writable: false), ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            options.Cancellation.ThrowIfCancellationRequested();
            var name = entry.FullName;
            if (!(name.EndsWith(".usda", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".usd", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".usdc", StringComparison.OrdinalIgnoreCase)) || entry.Length > 1L << 30)
            {
                continue;
            }

            using var stream = entry.Open();
            using var buffer = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
            stream.CopyTo(buffer);
            var bytes = buffer.ToArray();
            if (bytes.AsSpan().StartsWith("#usda"u8) || UsdcReader.IsCrate(bytes))
            {
                return bytes;
            }
        }

        throw new UnsupportedModelFormatException(ModelFormats.Usd, "The USDZ holds no readable USD layer.");
    }

    // ---- stage model -----------------------------------------------------------------------------------------------

    internal sealed class Prim
    {
        public string Kind = "";
        public string Type = "";
        public string Name = "";
        public Prim? Parent;

        /// <summary>For a prim composed from a same-file reference: the prim whose mesh data it shares.</summary>
        public Prim? Source;
        public readonly Dictionary<string, object> Attributes = new(StringComparer.Ordinal);

        /// <summary>Numeric values the binary reader decoded, flattened; an entry here wins over the boxed value in <see cref="Attributes"/>.</summary>
        public readonly Dictionary<string, double[]> Arrays = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Interpolation = new(StringComparer.Ordinal);
        public readonly List<Prim> Children = new();
        public readonly Dictionary<string, object> Metadata = new(StringComparer.Ordinal);

        public string Path => Parent is null ? "/" + Name : Parent.Path.TrimEnd('/') + "/" + Name;

        public double[] Numbers(string name)
        {
            if (Arrays.TryGetValue(name, out var direct))
            {
                return direct;
            }

            var result = new List<double>();
            if (Attributes.TryGetValue(name, out var value))
            {
                Flatten(value, result);
            }

            return result.ToArray();
        }

        public static void Flatten(object value, List<double> into)
        {
            switch (value)
            {
                case double d:
                    into.Add(d);
                    break;
                case List<object> list:
                    foreach (var item in list)
                    {
                        Flatten(item, into);
                    }

                    break;
            }
        }

        public string? Text(string name) => Attributes.TryGetValue(name, out var v) ? v as string : null;

        public List<string> Strings(string name) => Attributes.TryGetValue(name, out var v) && v is List<object> l ? l.OfType<string>().ToList() : [];
    }

    internal sealed class Stage
    {
        public readonly Dictionary<string, object> Metadata = new(StringComparer.Ordinal);
        public readonly List<Prim> Roots = new();
    }

    // ---- parser ----------------------------------------------------------------------------------------------------

    private sealed class Parser
    {
        private readonly List<string> _t = new();
        private int _p;

        public Parser(string text)
        {
            var i = 0;
            while (i < text.Length)
            {
                var c = text[i];
                if (c == '#')
                {
                    while (i < text.Length && text[i] != '\n')
                    {
                        i++;
                    }
                }
                else if (char.IsWhiteSpace(c) || c == ',' || c == ';')
                {
                    i++;
                }
                else if (c == '"')
                {
                    var triple = i + 2 < text.Length && text[i + 1] == '"' && text[i + 2] == '"';
                    var start = i + (triple ? 3 : 1);
                    i = start;
                    while (i < text.Length && !(triple ? text.AsSpan(i).StartsWith("\"\"\"") : text[i] == '"'))
                    {
                        if (text[i] == '\\')
                        {
                            i++;
                        }

                        i++;
                    }

                    _t.Add("\"" + text[start..Math.Min(i, text.Length)]);
                    i += triple ? 3 : 1;
                }
                else if (c == '@')
                {
                    var start = ++i;
                    while (i < text.Length && text[i] != '@')
                    {
                        i++;
                    }

                    _t.Add("@" + text[start..Math.Min(i, text.Length)]);
                    i++;
                }
                else if (c == '<')
                {
                    var start = ++i;
                    while (i < text.Length && text[i] != '>')
                    {
                        i++;
                    }

                    _t.Add("<" + text[start..Math.Min(i, text.Length)]);
                    i++;
                }
                else if (c is '(' or ')' or '[' or ']' or '{' or '}' or '=')
                {
                    _t.Add(c.ToString());
                    i++;
                }
                else
                {
                    var start = i;
                    while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not (',' or ';' or '(' or ')' or '[' or ']' or '{' or '}' or '=' or '"' or '@' or '<' or '#'))
                    {
                        i++;
                    }

                    if (i == start)
                    {
                        i++;
                    }
                    else
                    {
                        _t.Add(text[start..i]);
                    }
                }
            }

            _p = _t.Count > 0 && _t[0] == "usda" ? 0 : 0;
        }

        public Stage ParseStage()
        {
            var stage = new Stage();
            // The header "#usda 1.0" is a comment to the tokenizer; an optional (metadata) block follows.
            if (Peek() == "(")
            {
                ParseMetadata(stage.Metadata);
            }

            while (_p < _t.Count)
            {
                var token = _t[_p];
                if (token is "def" or "over" or "class")
                {
                    stage.Roots.Add(ParsePrim(null, 0));
                }
                else
                {
                    _p++;
                }
            }

            return stage;
        }

        private string Peek() => _p < _t.Count ? _t[_p] : "";

        private string Next() => _p < _t.Count ? _t[_p++] : throw new ModelFormatException("USDA: unexpected end of file.");

        private void ParseMetadata(Dictionary<string, object> into)
        {
            Next(); // (
            while (_p < _t.Count && Peek() != ")")
            {
                // key = value, or a bare string (documentation)
                var key = Next();
                if (Peek() == "=")
                {
                    _p++;
                    into[key] = ParseValue(0);
                }
                else if (key.StartsWith('"'))
                {
                    into["doc"] = key[1..];
                }
                else
                {
                    // list-edit keywords: prepend references = ...
                    var next = Next();
                    if (Peek() == "=")
                    {
                        _p++;
                        into[next] = ParseValue(0);
                    }
                }
            }

            _p++;
        }

        private Prim ParsePrim(Prim? parent, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new ModelFormatException("USDA prims are nested too deeply.");
            }

            var prim = new Prim { Kind = Next(), Parent = parent };
            if (Peek().StartsWith('"'))
            {
                prim.Name = Next()[1..];
            }
            else
            {
                prim.Type = Next();
                prim.Name = Next().TrimStart('"');
            }

            if (prim.Type.Length == 0 && Peek().StartsWith('"'))
            {
                prim.Name = Next()[1..];
            }

            if (Peek() == "(")
            {
                ParseMetadata(prim.Metadata);
            }

            if (Next() != "{")
            {
                throw new ModelFormatException("USDA: '{' expected after a prim header.");
            }

            while (_p < _t.Count && Peek() != "}")
            {
                var token = Peek();
                if (token is "def" or "over" or "class")
                {
                    prim.Children.Add(ParsePrim(prim, depth + 1));
                }
                else if (token == "variantSet")
                {
                    SkipBraces();
                }
                else if (token is "rel" or "custom" or "uniform" or "varying" or "prepend" or "append" or "delete" or "add" or "reorder" || true)
                {
                    ParseAttribute(prim);
                }
            }

            _p++; // }
            return prim;
        }

        private void SkipBraces()
        {
            while (_p < _t.Count && Peek() != "{")
            {
                _p++;
            }

            var depth = 0;
            while (_p < _t.Count)
            {
                var t = _t[_p++];
                if (t == "{")
                {
                    depth++;
                }
                else if (t == "}")
                {
                    depth--;
                    if (depth == 0)
                    {
                        return;
                    }
                }
            }
        }

        private void ParseAttribute(Prim prim)
        {
            // [qualifiers] type[] name [= value] [(metadata)]
            var words = new List<string>();
            while (_p < _t.Count && Peek() is not ("=" or "}" or "(" or "def" or "over" or "class"))
            {
                words.Add(Next());
                if (words.Count > 8)
                {
                    break;
                }
            }

            if (words.Count == 0)
            {
                _p++;
                return;
            }

            var name = words[^1];
            if (Peek() == "=")
            {
                _p++;
                var value = ParseValue(0);
                if (name.EndsWith(".connect", StringComparison.Ordinal))
                {
                    name = name[..^8] + ":connect";
                }

                if (words.Contains("references") || name == "references")
                {
                    prim.Metadata["references"] = value;
                }
                else
                {
                    prim.Attributes[name] = value;
                }
            }

            if (Peek() == "(")
            {
                var meta = new Dictionary<string, object>(StringComparer.Ordinal);
                ParseMetadata(meta);
                if (meta.TryGetValue("interpolation", out var interpolation) && interpolation is string s)
                {
                    prim.Interpolation[name] = s;
                }
            }
        }

        private object ParseValue(int depth)
        {
            if (depth > MaxDepth)
            {
                throw new ModelFormatException("USDA values are nested too deeply.");
            }

            var t = Next();
            if (t is "(" or "[")
            {
                var close = t == "(" ? ")" : "]";
                var list = new List<object>();
                while (_p < _t.Count && Peek() != close)
                {
                    list.Add(ParseValue(depth + 1));
                }

                _p++;
                return list;
            }

            if (t.StartsWith('"'))
            {
                return t[1..];
            }

            if (t.StartsWith('@') || t.StartsWith('<'))
            {
                return t[1..];
            }

            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            {
                return d;
            }

            if (t == "None")
            {
                return "";
            }

            if (t == "{")
            {
                // A dictionary value: skip it.
                var depthBraces = 1;
                while (_p < _t.Count && depthBraces > 0)
                {
                    var x = _t[_p++];
                    depthBraces += x == "{" ? 1 : x == "}" ? -1 : 0;
                }

                return "";
            }

            return t; // a bare token (an enumeration, true/false)
        }
    }

    // ---- building --------------------------------------------------------------------------------------------------

    internal sealed class Builder
    {
        private readonly Stage _stage;
        private readonly LoadOptions _options;
        private readonly Model3D _model = new();
        private readonly Dictionary<string, Prim> _byPath = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _materials = new(StringComparer.Ordinal);
        private readonly Dictionary<Prim, MeshData> _meshes = new(ReferenceEqualityComparer.Instance);
        private long _triangles;

        public Builder(Stage stage, LoadOptions options)
        {
            _stage = stage;
            _options = options;
            void Index(Prim prim)
            {
                _byPath[prim.Path] = prim;
                foreach (var child in prim.Children)
                {
                    Index(child);
                }
            }

            foreach (var root in stage.Roots)
            {
                Index(root);
            }
        }

        public Model3D Build()
        {
            var meta = _model.Metadata;
            meta.Format = ModelFormats.Usd;
            meta.DocumentKind = "scene";
            meta.Units = LengthUnit.Meter;
            var up = _stage.Metadata.TryGetValue("upAxis", out var u) ? u as string : null;
            meta.UpAxis = up == "Z" ? UpAxis.Z : UpAxis.Y;
            var metersPerUnit = _stage.Metadata.TryGetValue("metersPerUnit", out var m) && m is double mpu && mpu > 0 ? mpu : 1.0;
            meta.Units = metersPerUnit switch
            {
                1.0 => LengthUnit.Meter,
                0.01 => LengthUnit.Centimeter,
                0.001 => LengthUnit.Millimeter,
                0.0254 => LengthUnit.Inch,
                0.3048 => LengthUnit.Foot,
                _ => LengthUnit.Meter,
            };
            if (_stage.Metadata.TryGetValue("doc", out var doc) && doc is string docText && docText.Length > 0)
            {
                meta.Properties.Add(new ModelProperty("doc", docText, "usd"));
            }

            if (_stage.Metadata.TryGetValue("defaultPrim", out var dp) && dp is string defaultPrim)
            {
                meta.Title = defaultPrim;
            }

            var roots = new List<SceneNode>();
            foreach (var prim in _stage.Roots)
            {
                if (Convert(prim, 0) is { } node)
                {
                    roots.Add(node);
                }
            }

            var top = new List<SceneNode>(roots);
            if (Math.Abs(metersPerUnit - (meta.Units?.ToMeters() ?? 1)) > 1e-12)
            {
                var wrapper = new SceneNode(null) { Transform = Matrix4x4.CreateScale((float)metersPerUnit) };
                wrapper.Children.AddRange(top);
                top = [wrapper];
            }

            _model.Roots.AddRange(top);
            if (_model.Meshes.Count == 0)
            {
                throw new ModelFormatException("The USD stage holds no mesh geometry.");
            }

            meta.PartNames.AddRange(_model.Nodes().Where(n => n.Mesh is not null && !string.IsNullOrEmpty(n.Name)).Select(n => n.Name!));
            meta.MaterialNames.AddRange(_model.Materials.Select(x => x.Name!).Where(n => !string.IsNullOrEmpty(n)));
            meta.HasGeometry = true;
            meta.Statistics = _model.ComputeStatistics();
            meta.Bounds = _model.ComputeBounds();
            return _model;
        }

        /// <summary>
        /// Applies same-file references: the referencing prim takes the referenced prim's type (when it has none of its own),
        /// attributes it does not author itself, and children. A reference to another file is reported and left alone.
        /// </summary>
        private Prim Compose(Prim prim, int depth)
        {
            if (depth > MaxDepth || !prim.Metadata.TryGetValue("references", out var references))
            {
                return prim;
            }

            var paths = references is List<object> list ? list.OfType<string>().ToList() : references is string single ? [single] : [];
            var composed = prim;
            foreach (var path in paths)
            {
                if (path.StartsWith('/') && _byPath.TryGetValue(path, out var target) && !ReferenceEquals(target, prim))
                {
                    var source = Compose(target, depth + 1);
                    var copy = new Prim { Kind = prim.Kind, Name = prim.Name, Parent = prim.Parent, Type = prim.Type.Length > 0 ? prim.Type : source.Type, Source = source.Source ?? source };
                    foreach (var (k, v) in source.Attributes)
                    {
                        copy.Attributes[k] = v;
                    }

                    foreach (var (k, v) in source.Arrays)
                    {
                        copy.Arrays[k] = v;
                    }

                    foreach (var (k, v) in composed.Arrays)
                    {
                        copy.Arrays[k] = v;
                    }

                    foreach (var (k, v) in source.Interpolation)
                    {
                        copy.Interpolation[k] = v;
                    }

                    foreach (var (k, v) in composed.Attributes)
                    {
                        copy.Attributes[k] = v;
                    }

                    foreach (var (k, v) in composed.Interpolation)
                    {
                        copy.Interpolation[k] = v;
                    }

                    copy.Children.AddRange(source.Children);
                    copy.Children.AddRange(composed.Children);
                    composed = copy;
                }
                else if (!path.StartsWith('/'))
                {
                    _model.Warnings.Add($"A reference to another file ('{path}') was not followed.");
                }
            }

            return composed;
        }

        private SceneNode? Convert(Prim prim, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new ModelFormatException("USD prims are nested too deeply.");
            }

            _options.Cancellation.ThrowIfCancellationRequested();
            if (prim.Kind != "def" || prim.Type is "Material" or "Shader" or "NodeGraph" or "Scope" && prim.Children.All(c => c.Type is "Material" or "Shader" or "NodeGraph") && prim.Children.Count > 0 && prim.Children.Any(c => c.Type == "Material"))
            {
                return null;
            }

            prim = Compose(prim, 0);
            var node = new SceneNode(prim.Name) { Transform = TransformOf(prim) };
            if (MeshOf(prim) is { } mesh)
            {
                node.Mesh = mesh;
            }

            foreach (var child in prim.Children)
            {
                if (Convert(child, depth + 1) is { } sub)
                {
                    node.Children.Add(sub);
                }
            }

            return node.Mesh is null && node.Children.Count == 0 ? null : node;
        }

        private static Matrix4x4 Rotation(string op, double[] v)
        {
            if (v.Length < 3)
            {
                return Matrix4x4.Identity;
            }

            static Matrix4x4 R(char axis, double degrees) => axis switch
            {
                'X' => Matrix4x4.CreateRotationX((float)(degrees * Math.PI / 180)),
                'Y' => Matrix4x4.CreateRotationY((float)(degrees * Math.PI / 180)),
                _ => Matrix4x4.CreateRotationZ((float)(degrees * Math.PI / 180)),
            };

            var order = op[^3..]; // XYZ, XZY, ...
            var values = new Dictionary<char, double> { ['X'] = v[0], ['Y'] = v[1], ['Z'] = v[2] };
            var result = Matrix4x4.Identity;
            foreach (var axis in order)
            {
                result *= R(axis, values[axis]);
            }

            return result;
        }

        private static Matrix4x4 TransformOf(Prim prim)
        {
            var order = prim.Strings("xformOpOrder");
            if (order.Count == 0)
            {
                // Without an order, USD applies no ops; but a lone transform/translate attribute is common in hand-written files.
                order = prim.Attributes.Keys.Where(k => k.StartsWith("xformOp:", StringComparison.Ordinal) && !k.Contains(':', StringComparison.Ordinal) | k.IndexOf(':', 8) < 0).ToList();
            }

            var local = Matrix4x4.Identity;
            foreach (var op in order)
            {
                var invert = op.StartsWith("!invert!", StringComparison.Ordinal);
                var name = invert ? op["!invert!".Length..] : op;
                var v = prim.Numbers(name);
                Matrix4x4 m;
                if (name.StartsWith("xformOp:translate", StringComparison.Ordinal) && v.Length >= 3)
                {
                    m = Matrix4x4.CreateTranslation((float)v[0], (float)v[1], (float)v[2]);
                }
                else if (name.StartsWith("xformOp:scale", StringComparison.Ordinal) && v.Length >= 3)
                {
                    m = Matrix4x4.CreateScale((float)v[0], (float)v[1], (float)v[2]);
                }
                else if (name.StartsWith("xformOp:rotateXYZ", StringComparison.Ordinal) || name.StartsWith("xformOp:rotateXZY", StringComparison.Ordinal) || name.StartsWith("xformOp:rotateYXZ", StringComparison.Ordinal)
                    || name.StartsWith("xformOp:rotateYZX", StringComparison.Ordinal) || name.StartsWith("xformOp:rotateZXY", StringComparison.Ordinal) || name.StartsWith("xformOp:rotateZYX", StringComparison.Ordinal))
                {
                    var baseName = name.Contains(':', StringComparison.Ordinal) ? name.Split(':')[1] : name;
                    m = Rotation(baseName, v);
                }
                else if (name.StartsWith("xformOp:rotateX", StringComparison.Ordinal) && v.Length >= 1)
                {
                    m = Matrix4x4.CreateRotationX((float)(v[0] * Math.PI / 180));
                }
                else if (name.StartsWith("xformOp:rotateY", StringComparison.Ordinal) && v.Length >= 1)
                {
                    m = Matrix4x4.CreateRotationY((float)(v[0] * Math.PI / 180));
                }
                else if (name.StartsWith("xformOp:rotateZ", StringComparison.Ordinal) && v.Length >= 1)
                {
                    m = Matrix4x4.CreateRotationZ((float)(v[0] * Math.PI / 180));
                }
                else if (name.StartsWith("xformOp:orient", StringComparison.Ordinal) && v.Length >= 4)
                {
                    m = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(new Quaternion((float)v[1], (float)v[2], (float)v[3], (float)v[0])));
                }
                else if (name.StartsWith("xformOp:transform", StringComparison.Ordinal) && v.Length >= 16)
                {
                    m = new Matrix4x4((float)v[0], (float)v[1], (float)v[2], (float)v[3], (float)v[4], (float)v[5], (float)v[6], (float)v[7], (float)v[8], (float)v[9], (float)v[10], (float)v[11], (float)v[12], (float)v[13], (float)v[14], (float)v[15]);
                }
                else
                {
                    continue;
                }

                if (invert && Matrix4x4.Invert(m, out var inverse))
                {
                    m = inverse;
                }

                local = m * local;
            }

            return local;
        }

        private MeshData? MeshOf(Prim prim)
        {
            var key = prim.Source ?? prim;
            if (_meshes.TryGetValue(key, out var cached))
            {
                return cached;
            }

            MeshData? mesh = prim.Type switch
            {
                "Mesh" => BuildMesh(prim),
                "Cube" => Shape.Box((float)(prim.Numbers("size") is { Length: > 0 } s ? s[0] : 2)),
                "Sphere" => Shape.Sphere((float)(prim.Numbers("radius") is { Length: > 0 } r ? r[0] : 1)),
                "Cylinder" => Shape.Cylinder((float)(prim.Numbers("radius") is { Length: > 0 } cr ? cr[0] : 1), (float)(prim.Numbers("height") is { Length: > 0 } ch ? ch[0] : 2), prim.Text("axis") ?? "Z"),
                "Cone" => Shape.Cone((float)(prim.Numbers("radius") is { Length: > 0 } kr ? kr[0] : 1), (float)(prim.Numbers("height") is { Length: > 0 } kh ? kh[0] : 2), prim.Text("axis") ?? "Z"),
                "Capsule" => Shape.Cylinder((float)(prim.Numbers("radius") is { Length: > 0 } pr ? pr[0] : 0.5), (float)(prim.Numbers("height") is { Length: > 0 } ph ? ph[0] : 1) + 1, prim.Text("axis") ?? "Z"),
                _ => null,
            };
            if (mesh is null)
            {
                return null;
            }

            mesh.Name = prim.Name;
            if (prim.Type != "Mesh")
            {
                var material = MaterialOf(prim);
                if (material >= 0)
                {
                    mesh.Groups.Add(new MeshGroup(0, mesh.Indices.Length, material));
                }
            }

            _meshes[key] = mesh;
            _model.Meshes.Add(mesh);
            return mesh;
        }

        private int MaterialOf(Prim prim)
        {
            // material:binding on the prim or the nearest ancestor that has one.
            for (var p = prim; p is not null; p = p.Parent)
            {
                if (p.Attributes.TryGetValue("material:binding", out var binding) && binding is string path && _byPath.TryGetValue(path, out var material))
                {
                    return MaterialIndex(material);
                }
            }

            return -1;
        }

        private int MaterialIndex(Prim material)
        {
            if (_materials.TryGetValue(material.Path, out var index))
            {
                return index;
            }

            var result = new ModelMaterial { Name = material.Name };
            var shader = Descendants(material).FirstOrDefault(p => p.Type == "Shader" && (p.Attributes.ContainsKey("inputs:diffuseColor") || p.Attributes.ContainsKey("inputs:baseColor")));
            if (shader is not null)
            {
                var color = shader.Numbers(shader.Attributes.ContainsKey("inputs:diffuseColor") ? "inputs:diffuseColor" : "inputs:baseColor");
                if (color.Length >= 3)
                {
                    result.BaseColor = new Vector4((float)color[0], (float)color[1], (float)color[2], 1f);
                }

                if (shader.Numbers("inputs:metallic") is { Length: > 0 } metallic)
                {
                    result.Metallic = (float)metallic[0];
                }

                if (shader.Numbers("inputs:roughness") is { Length: > 0 } roughness)
                {
                    result.Roughness = (float)roughness[0];
                }

                if (shader.Numbers("inputs:opacity") is { Length: > 0 } opacity && opacity[0] < 1)
                {
                    result.BaseColor = new Vector4(result.BaseColor.X, result.BaseColor.Y, result.BaseColor.Z, (float)opacity[0]);
                }
            }

            _materials[material.Path] = _model.Materials.Count;
            _model.Materials.Add(result);
            return _model.Materials.Count - 1;
        }

        private static IEnumerable<Prim> Descendants(Prim prim)
        {
            foreach (var child in prim.Children)
            {
                yield return child;
                foreach (var d in Descendants(child))
                {
                    yield return d;
                }
            }
        }

        private MeshData? BuildMesh(Prim prim)
        {
            var points = prim.Numbers("points");
            var counts = prim.Numbers("faceVertexCounts").Select(v => (int)v).ToArray();
            var indices = prim.Numbers("faceVertexIndices").Select(v => (int)v).ToArray();
            if (points.Length < 9 || counts.Length == 0 || indices.Length < 3)
            {
                return null;
            }

            var vertexCount = points.Length / 3;
            var normalName = prim.Attributes.ContainsKey("normals") ? "normals" : prim.Attributes.ContainsKey("primvars:normals") ? "primvars:normals" : null;
            var normals = normalName is null ? [] : prim.Numbers(normalName);
            var normalInterp = normalName is not null && prim.Interpolation.TryGetValue(normalName, out var ni) ? ni : "vertex";
            var colors = prim.Numbers("primvars:displayColor");
            var colorInterp = prim.Interpolation.TryGetValue("primvars:displayColor", out var ci) ? ci : "constant";

            var positions = new List<float>();
            var outNormals = new List<float>();
            var outColors = new List<float>();
            var lookup = new Dictionary<(int, int, int), uint>();
            var triangles = new List<uint>();
            var faceTriangles = new List<(int Face, int Start, int Count)>();
            var corner = 0;
            var normalsComplete = normals.Length >= 3;
            for (var face = 0; face < counts.Length; face++)
            {
                var n = counts[face];
                if (n < 3 || corner + n > indices.Length)
                {
                    corner += Math.Max(n, 0);
                    continue;
                }

                var ids = new uint[n];
                for (var k = 0; k < n; k++)
                {
                    var vi = indices[corner + k];
                    if (vi < 0 || vi >= vertexCount)
                    {
                        throw new ModelFormatException("A USD face refers to a point that does not exist.");
                    }

                    var normalIndex = normalInterp switch { "faceVarying" => corner + k, "uniform" => face, "constant" => 0, _ => vi };
                    var colorIndex = colorInterp switch { "faceVarying" => corner + k, "uniform" => face, "constant" => 0, _ => vi };
                    if (!lookup.TryGetValue((vi, normals.Length > 0 ? normalIndex : -1, colors.Length > 0 ? colorIndex : -1), out var index))
                    {
                        index = (uint)(positions.Count / 3);
                        lookup[(vi, normals.Length > 0 ? normalIndex : -1, colors.Length > 0 ? colorIndex : -1)] = index;
                        positions.AddRange([(float)points[3 * vi], (float)points[3 * vi + 1], (float)points[3 * vi + 2]]);
                        if (normals.Length > 0 && 3 * normalIndex + 2 < normals.Length)
                        {
                            outNormals.AddRange([(float)normals[3 * normalIndex], (float)normals[3 * normalIndex + 1], (float)normals[3 * normalIndex + 2]]);
                        }
                        else
                        {
                            outNormals.AddRange([0f, 0f, 0f]);
                            normalsComplete = false;
                        }

                        if (colors.Length >= 3)
                        {
                            var at = 3 * colorIndex + 2 < colors.Length ? colorIndex : 0;
                            outColors.AddRange([(float)colors[3 * at], (float)colors[3 * at + 1], (float)colors[3 * at + 2], 1f]);
                        }
                    }

                    ids[k] = index;
                }

                var start = triangles.Count;
                for (var k = 1; k + 1 < n; k++)
                {
                    triangles.AddRange([ids[0], ids[k], ids[k + 1]]);
                    if (++_triangles > _options.MaxTriangles)
                    {
                        throw new ModelFormatException("The USD has more triangles than the configured limit.");
                    }
                }

                faceTriangles.Add((face, start, triangles.Count - start));
                corner += n;
            }

            var mesh = new MeshData { Positions = positions.ToArray() };
            if (normalsComplete)
            {
                mesh.Normals = outNormals.ToArray();
            }

            if (outColors.Count == positions.Count / 3 * 4)
            {
                mesh.Colors = outColors.ToArray();
            }

            // Materials: GeomSubsets bind faces to materials; the mesh's own binding covers the rest.
            var defaultMaterial = MaterialOf(prim);
            var faceMaterial = new Dictionary<int, int>();
            foreach (var subset in prim.Children.Where(c => c.Type == "GeomSubset"))
            {
                var material = subset.Attributes.TryGetValue("material:binding", out var b) && b is string path && _byPath.TryGetValue(path, out var mp) ? MaterialIndex(mp) : defaultMaterial;
                foreach (var face in subset.Numbers("indices"))
                {
                    faceMaterial[(int)face] = material;
                }
            }

            var order = faceTriangles.OrderBy(f => faceMaterial.GetValueOrDefault(f.Face, defaultMaterial)).ToList();
            var sorted = new List<uint>(triangles.Count);
            var groupStart = 0;
            for (var k = 0; k < order.Count; k++)
            {
                for (var t = 0; t < order[k].Count; t++)
                {
                    sorted.Add(triangles[order[k].Start + t]);
                }

                var material = faceMaterial.GetValueOrDefault(order[k].Face, defaultMaterial);
                if (k + 1 == order.Count || faceMaterial.GetValueOrDefault(order[k + 1].Face, defaultMaterial) != material)
                {
                    mesh.Groups.Add(new MeshGroup(groupStart, sorted.Count - groupStart, material));
                    groupStart = sorted.Count;
                }
            }

            mesh.Indices = sorted.ToArray();
            if (mesh.Groups.Count == 1 && mesh.Groups[0].Material < 0)
            {
                mesh.Groups.Clear();
            }

            return mesh;
        }
    }

    /// <summary>Tessellated UsdGeom primitives (axis Z unless stated).</summary>
    private static class Shape
    {
        public static MeshData Box(float size) => X3dReader.Primitive.Box(new Vector3(size));

        public static MeshData Sphere(float radius) => X3dReader.Primitive.Sphere(radius);

        public static MeshData Cylinder(float radius, float height, string axis) => Reorient(X3dReader.Primitive.Cylinder(radius, height, true, true, true), axis);

        public static MeshData Cone(float radius, float height, string axis) => Reorient(X3dReader.Primitive.Cone(radius, height, true, true), axis);

        /// <summary>The generators build Y-up shapes; USD's default axis is Z.</summary>
        private static MeshData Reorient(MeshData mesh, string axis)
        {
            if (axis == "Y")
            {
                return mesh;
            }

            for (var i = 0; i + 2 < mesh.Positions.Length; i += 3)
            {
                (mesh.Positions[i + 1], mesh.Positions[i + 2], mesh.Positions[i]) = axis == "Z"
                    ? (mesh.Positions[i + 2], -mesh.Positions[i + 1], mesh.Positions[i])
                    : (mesh.Positions[i], mesh.Positions[i + 2], mesh.Positions[i + 1]);
            }

            for (var i = 0; i + 2 < mesh.Normals.Length; i += 3)
            {
                (mesh.Normals[i + 1], mesh.Normals[i + 2], mesh.Normals[i]) = axis == "Z"
                    ? (mesh.Normals[i + 2], -mesh.Normals[i + 1], mesh.Normals[i])
                    : (mesh.Normals[i], mesh.Normals[i + 2], mesh.Normals[i + 1]);
            }

            return mesh;
        }
    }
}
