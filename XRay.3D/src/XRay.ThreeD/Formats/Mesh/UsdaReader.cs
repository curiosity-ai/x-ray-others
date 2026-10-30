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
/// primitives, materials bound through material:binding (UsdPreviewSurface diffuse colour, metallic, roughness, opacity), references
/// and payloads (same file, other layers through <see cref="LoadOptions.ResolveCompanion"/>, other members of a USDZ), sublayers, authored variant
/// selections (text layers), the first time sample of an animated attribute (text layers), UsdUVTexture diffuse textures with <c>primvars:st</c>,
/// metersPerUnit and upAxis. The binary crate layer is read by <see cref="UsdcReader"/> into the same stage model.
/// </summary>
internal static class UsdaReader
{
    private const int MaxDepth = 64;

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var span = data.Span;
        Dictionary<string, byte[]>? members = null;
        var rootPath = "root.usd";
        if (span.Length > 4 && span[0] == 'P' && span[1] == 'K')
        {
            (data, members, rootPath) = ReadUsdz(data, options);
        }

        var layers = new LayerSet(options, members);
        var stage = layers.Parse(data, rootPath);
        return new Builder(stage, options, layers).Build();
    }

    /// <summary>The package's root layer (the first stored layer, text or crate) and every stored member by its normalised path.</summary>
    private static (ReadOnlyMemory<byte> Root, Dictionary<string, byte[]> Members, string RootPath) ReadUsdz(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        using var zip = new ZipArchive(new MemoryStream(data.ToArray(), writable: false), ZipArchiveMode.Read);
        var members = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        byte[]? root = null;
        var rootPath = "";
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            options.Cancellation.ThrowIfCancellationRequested();
            if (entry.Length == 0 || entry.Length > 1L << 28 || (total += entry.Length) > options.MaxInputBytes || entry.FullName.EndsWith('/'))
            {
                continue;
            }

            using var stream = entry.Open();
            using var buffer = new MemoryStream((int)Math.Min(entry.Length, int.MaxValue));
            stream.CopyTo(buffer);
            var bytes = buffer.ToArray();
            var name = LayerSet.Normalise(entry.FullName);
            members[name] = bytes;
            var isLayerName = name.EndsWith(".usda", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".usd", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".usdc", StringComparison.OrdinalIgnoreCase);
            if (root is null && isLayerName && (bytes.AsSpan().StartsWith("#usda"u8) || UsdcReader.IsCrate(bytes)))
            {
                root = bytes;
                rootPath = name;
            }
        }

        return root is null ? throw new UnsupportedModelFormatException(ModelFormats.Usd, "The USDZ holds no readable USD layer.") : (root, members, rootPath);
    }

    /// <summary>Loads layers by asset path (relative paths anchored at the referencing layer), from a USDZ's members or through the caller's companion resolver, each at most once.</summary>
    internal sealed class LayerSet
    {
        private const int MaxLayers = 256;
        private readonly LoadOptions _options;
        private readonly Dictionary<string, byte[]>? _members;
        private readonly Dictionary<string, Stage?> _cache = new(StringComparer.OrdinalIgnoreCase);
        private int _depth;

        public LayerSet(LoadOptions options, Dictionary<string, byte[]>? members)
        {
            _options = options;
            _members = members;
        }

        public List<string> Warnings { get; } = new();

        public static string Normalise(string path)
        {
            var parts = new List<string>();
            foreach (var part in path.Replace('\\', '/').Split('/'))
            {
                if (part.Length == 0 || part == ".")
                {
                    continue;
                }

                if (part == ".." && parts.Count > 0)
                {
                    parts.RemoveAt(parts.Count - 1);
                }
                else if (part != "..")
                {
                    parts.Add(part);
                }
            }

            return string.Join('/', parts);
        }

        public static string Anchor(string asset, string fromLayer)
        {
            if (asset.StartsWith('/'))
            {
                return Normalise(asset);
            }

            var slash = fromLayer.LastIndexOf('/');
            return Normalise(slash < 0 ? asset : fromLayer[..slash] + "/" + asset);
        }

        public byte[]? Bytes(string asset, string fromLayer)
        {
            var path = Anchor(asset, fromLayer);
            if (_members is not null && _members.TryGetValue(path, out var bytes))
            {
                return bytes;
            }

            if (_options.ResolveCompanion is { } resolve)
            {
                return resolve(path) ?? resolve(path[(path.LastIndexOf('/') + 1)..]);
            }

            return null;
        }

        public Stage Parse(ReadOnlyMemory<byte> data, string path)
        {
            var stage = ParseLayer(data);
            stage.LayerPath = path;
            if (stage.Metadata.TryGetValue("subLayers", out var sub) && sub is List<object> list && _depth < 16)
            {
                _depth++;
                try
                {
                    // Strongest first: each weaker layer fills in what the stronger ones left open.
                    foreach (var asset in list.OfType<string>())
                    {
                        if (Load(asset.Split('\u0001')[0], path) is { } layer)
                        {
                            MergeUnder(stage, layer);
                        }
                    }
                }
                finally
                {
                    _depth--;
                }
            }

            stage.Seal();
            return stage;
        }

        private Stage ParseLayer(ReadOnlyMemory<byte> data)
        {
            if (UsdcReader.IsCrate(data.Span))
            {
                return UsdcReader.ReadStage(data, _options);
            }

            var text = Encoding.UTF8.GetString(data.Span);
            if (!text.StartsWith("#usda", StringComparison.Ordinal))
            {
                throw new ModelFormatException("Not a USDA text file.");
            }

            return new Parser(text).ParseStage();
        }

        public Stage? Load(string asset, string fromLayer)
        {
            var path = Anchor(asset, fromLayer);
            if (_cache.TryGetValue(path, out var cached))
            {
                return cached;
            }

            _cache[path] = null; // guards against cycles
            if (_cache.Count > MaxLayers)
            {
                Warnings.Add("More than " + MaxLayers + " USD layers: the rest were not loaded.");
                return null;
            }

            var bytes = Bytes(asset, fromLayer);
            if (bytes is null)
            {
                Warnings.Add($"The USD layer '{asset}' was not found.");
                return null;
            }

            try
            {
                var stage = Parse(bytes, path);
                _cache[path] = stage;
                return stage;
            }
            catch (ModelFormatException e)
            {
                Warnings.Add($"The USD layer '{asset}' could not be read: {e.Message}");
                return null;
            }
        }

        private static void MergeUnder(Stage strong, Stage weak)
        {
            foreach (var (key, value) in weak.Metadata)
            {
                strong.Metadata.TryAdd(key, value);
            }

            foreach (var root in weak.Roots)
            {
                var existing = strong.Roots.FirstOrDefault(p => p.Name == root.Name);
                if (existing is null)
                {
                    root.Parent = null;
                    strong.Roots.Add(root);
                }
                else
                {
                    MergePrim(existing, root);
                }
            }
        }

        private static void MergePrim(Prim strong, Prim weak)
        {
            if (strong.Type.Length == 0)
            {
                strong.Type = weak.Type;
            }

            if (strong.Kind != "def" && weak.Kind == "def")
            {
                strong.Kind = "def";
            }

            foreach (var (k, v) in weak.Attributes)
            {
                strong.Attributes.TryAdd(k, v);
            }

            foreach (var (k, v) in weak.Arrays)
            {
                strong.Arrays.TryAdd(k, v);
            }

            foreach (var (k, v) in weak.Interpolation)
            {
                strong.Interpolation.TryAdd(k, v);
            }

            foreach (var (k, v) in weak.Metadata)
            {
                if (k is "references" or "payload" && strong.Metadata.TryGetValue(k, out var own))
                {
                    strong.Metadata[k] = Concat(own, v);
                }
                else
                {
                    strong.Metadata.TryAdd(k, v);
                }
            }

            foreach (var (set, choices) in weak.VariantSets)
            {
                if (!strong.VariantSets.TryGetValue(set, out var mine))
                {
                    strong.VariantSets[set] = choices;
                }
                else
                {
                    foreach (var (choice, content) in choices)
                    {
                        if (!mine.TryGetValue(choice, out var existing))
                        {
                            mine[choice] = content;
                        }
                        else
                        {
                            MergePrim(existing, content);
                        }
                    }
                }
            }

            foreach (var child in weak.Children)
            {
                var existing = strong.Children.FirstOrDefault(p => p.Name == child.Name);
                if (existing is null)
                {
                    child.Parent = strong;
                    strong.Children.Add(child);
                }
                else
                {
                    MergePrim(existing, child);
                }
            }
        }

        private static object Concat(object a, object b)
        {
            var list = new List<object>();
            foreach (var x in new[] { a, b })
            {
                if (x is List<object> l)
                {
                    list.AddRange(l);
                }
                else
                {
                    list.Add(x);
                }
            }

            return list;
        }
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

        /// <summary>The layer this prim's opinions come from: path-valued attributes (material bindings, connections, references) are in its namespace.</summary>
        public Stage? Layer;

        /// <summary>Variant sets authored on the prim: set name, then choice, then the opinions the choice adds.</summary>
        public readonly Dictionary<string, Dictionary<string, Prim>> VariantSets = new(StringComparer.Ordinal);
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

        /// <summary>Asset path of the layer (normalised), the anchor of its relative references.</summary>
        public string LayerPath = "";

        public readonly Dictionary<string, Prim> Index = new(StringComparer.Ordinal);

        /// <summary>Records which layer each prim came from and indexes them by path.</summary>
        public void Seal()
        {
            Index.Clear();
            void Visit(Prim prim, int depth)
            {
                if (depth > MaxDepth)
                {
                    throw new ModelFormatException("USD prims are nested too deeply.");
                }

                prim.Layer ??= this;
                Index[prim.Path] = prim;
                foreach (var child in prim.Children)
                {
                    Visit(child, depth + 1);
                }
            }

            foreach (var root in Roots)
            {
                Visit(root, 0);
            }
        }
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

            ParseBody(prim, depth);
            _p++; // }
            return prim;
        }

        private void ParseBody(Prim prim, int depth)
        {
            while (_p < _t.Count && Peek() != "}")
            {
                var token = Peek();
                if (token is "def" or "over" or "class")
                {
                    prim.Children.Add(ParsePrim(prim, depth + 1));
                }
                else if (token == "variantSet")
                {
                    ParseVariantSet(prim, depth);
                }
                else
                {
                    ParseAttribute(prim);
                }
            }
        }

        /// <summary>variantSet "name" = { "choice" { prim body } ... }: the body of each choice is kept as the opinions it adds when selected.</summary>
        private void ParseVariantSet(Prim prim, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new ModelFormatException("USDA variant sets are nested too deeply.");
            }

            _p++; // variantSet
            var name = Next().TrimStart('"');
            if (Peek() == "=")
            {
                _p++;
            }

            if (Next() != "{")
            {
                throw new ModelFormatException("USDA: '{' expected after a variantSet.");
            }

            var choices = new Dictionary<string, Prim>(StringComparer.Ordinal);
            while (_p < _t.Count && Peek() != "}")
            {
                var choice = Next().TrimStart('"');
                if (Peek() == "(")
                {
                    ParseMetadata(new Dictionary<string, object>(StringComparer.Ordinal));
                }

                if (Next() != "{")
                {
                    throw new ModelFormatException("USDA: '{' expected after a variant name.");
                }

                var content = new Prim { Kind = "over", Name = prim.Name, Parent = prim.Parent, Type = "" };
                ParseBody(content, depth + 1);
                _p++; // }
                choices[choice] = content;
            }

            _p++; // }
            prim.VariantSets[name] = choices;
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
                else if (words.Contains("payload") || name == "payload")
                {
                    prim.Metadata["payload"] = value;
                }
                else if (value is TimeSamples samples)
                {
                    // The first sample stands for the attribute unless a default value is (or gets) authored.
                    var attribute = name.EndsWith(".timeSamples", StringComparison.Ordinal) ? name[..^12] : name;
                    if (samples.First is { } first && !prim.Attributes.ContainsKey(attribute))
                    {
                        prim.Attributes[attribute] = first;
                    }

                    name = attribute;
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

        /// <summary>A brace value after its opening brace: a time-sample table (time: value, ...) or a dictionary (type key = value, ...); nested dictionaries are kept.</summary>
        private object ParseBraces(int depth)
        {
            if (depth > MaxDepth)
            {
                throw new ModelFormatException("USDA values are nested too deeply.");
            }

            var numeric = _p < _t.Count && double.TryParse(_t[_p].TrimEnd(':'), NumberStyles.Float, CultureInfo.InvariantCulture, out _);
            if (numeric)
            {
                var samples = new TimeSamples();
                while (_p < _t.Count && Peek() != "}")
                {
                    var key = Next();
                    if (Peek() == ":")
                    {
                        _p++;
                    }

                    double.TryParse(key.TrimEnd(':'), NumberStyles.Float, CultureInfo.InvariantCulture, out var time);
                    var value = ParseValue(depth + 1);
                    if (time < samples.FirstTime)
                    {
                        samples.FirstTime = time;
                        samples.First = value;
                    }
                }

                _p++; // }
                return samples;
            }

            var dictionary = new Dictionary<string, object>(StringComparer.Ordinal);
            while (_p < _t.Count && Peek() != "}")
            {
                var words = new List<string>();
                while (_p < _t.Count && Peek() is not ("=" or "}"))
                {
                    words.Add(Next());
                    if (words.Count > 6)
                    {
                        break;
                    }
                }

                if (Peek() == "=" && words.Count > 0)
                {
                    _p++;
                    dictionary[words[^1].TrimStart('"')] = ParseValue(depth + 1);
                }
                else if (words.Count == 0)
                {
                    _p++;
                }
            }

            _p++; // }
            return dictionary;
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

            if (t.StartsWith('@'))
            {
                // @asset@</prim/path>: kept as one string, asset and prim path separated by U+0001
                if (_p < _t.Count && _t[_p].StartsWith('<'))
                {
                    return t[1..] + "\u0001" + _t[_p++][1..];
                }

                return t[1..];
            }

            if (t.StartsWith('<'))
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
                return ParseBraces(depth);
            }

            return t; // a bare token (an enumeration, true/false)
        }
    }

    /// <summary>The first value of a <c>timeSamples</c> dictionary (smallest time).</summary>
    internal sealed class TimeSamples
    {
        public object? First;
        public double FirstTime = double.PositiveInfinity;
    }

    // ---- building --------------------------------------------------------------------------------------------------

    internal sealed class Builder
    {
        private readonly Stage _stage;
        private readonly LoadOptions _options;
        private readonly Model3D _model = new();
        private readonly LayerSet _layers;
        private readonly Dictionary<string, int> _textures = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _materials = new(StringComparer.Ordinal);
        private readonly Dictionary<Prim, MeshData> _meshes = new(ReferenceEqualityComparer.Instance);
        private long _triangles;

        public Builder(Stage stage, LoadOptions options, LayerSet layers)
        {
            _stage = stage;
            _options = options;
            _layers = layers;
        }

        private static Prim? Find(Prim from, string path) => from.Layer is { } layer && layer.Index.TryGetValue(path, out var found) ? found : null;

        public Model3D Build()
        {
            var meta = _model.Metadata;
            meta.Format = ModelFormats.Usd;
            meta.DocumentKind = "scene";
            meta.Units = LengthUnit.Meter;
            var up = _stage.Metadata.TryGetValue("upAxis", out var u) ? u as string : null;
            meta.UpAxis = up == "Z" ? UpAxis.Z : UpAxis.Y;
            var metersPerUnit = _stage.Metadata.TryGetValue("metersPerUnit", out var m) && m is double mpu && mpu > 0 ? mpu : 0.01; // the schema's fallback is centimetres
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

            _model.Warnings.AddRange(_layers.Warnings.Distinct(StringComparer.Ordinal));
            meta.PartNames.AddRange(_model.Nodes().Where(n => n.Mesh is not null && !string.IsNullOrEmpty(n.Name)).Select(n => n.Name!));
            meta.MaterialNames.AddRange(_model.Materials.Select(x => x.Name!).Where(n => !string.IsNullOrEmpty(n)));
            meta.HasGeometry = true;
            meta.Statistics = _model.ComputeStatistics();
            meta.Bounds = _model.ComputeBounds();
            return _model;
        }

        /// <summary>
        /// Applies references and payloads (to prims of the same layer, or of another layer: a loose file through the companion resolver, a member of
        /// the USDZ) and then the authored variant selections, each over what the weaker arcs gave: the composed prim takes the referenced prim's type
        /// (when it has none of its own), its attributes and children, then the selected variants' opinions, and its own opinions last.
        /// </summary>
        private Prim Compose(Prim prim, int depth)
        {
            if (depth > MaxDepth)
            {
                return prim;
            }

            var hasArcs = prim.Metadata.ContainsKey("references") || prim.Metadata.ContainsKey("payload");
            if (!hasArcs && prim.VariantSets.Count == 0)
            {
                return prim;
            }

            var composed = prim;
            foreach (var arc in new[] { "references", "payload" })
            {
                if (!prim.Metadata.TryGetValue(arc, out var references))
                {
                    continue;
                }

                var items = references is List<object> list ? list.OfType<string>().ToList() : references is string single ? [single] : [];
                foreach (var item in items)
                {
                    var split = item.Split('\u0001');
                    var asset = split.Length > 1 ? split[0] : item.StartsWith('/') ? "" : item;
                    var path = split.Length > 1 ? split[1] : item.StartsWith('/') ? item : "";
                    Stage? layer;
                    if (asset.Length == 0)
                    {
                        layer = prim.Layer;
                    }
                    else
                    {
                        layer = _layers.Load(asset, prim.Layer?.LayerPath ?? "");
                    }

                    if (layer is null)
                    {
                        continue;
                    }

                    if (path.Length == 0)
                    {
                        path = layer.Metadata.TryGetValue("defaultPrim", out var dp) && dp is string name ? "/" + name : layer.Roots.Count > 0 ? "/" + layer.Roots[0].Name : "";
                    }

                    if (!layer.Index.TryGetValue(path, out var target) || ReferenceEquals(target, prim))
                    {
                        _model.Warnings.Add($"The reference target '{path}' was not found in '{(asset.Length > 0 ? asset : "this layer")}'.");
                        continue;
                    }

                    var source = Compose(target, depth + 1);
                    var copy = new Prim { Kind = prim.Kind, Name = prim.Name, Parent = prim.Parent, Type = prim.Type.Length > 0 ? prim.Type : source.Type, Source = source.Source ?? source, Layer = source.Layer };
                    foreach (var (k, v) in source.Attributes)
                    {
                        copy.Attributes[k] = v;
                    }

                    foreach (var (k, v) in source.Arrays)
                    {
                        copy.Arrays[k] = v;
                    }

                    foreach (var (k, v) in source.Interpolation)
                    {
                        copy.Interpolation[k] = v;
                    }

                    foreach (var (k, v) in source.Metadata)
                    {
                        if (k is not ("references" or "payload"))
                        {
                            copy.Metadata.TryAdd(k, v);
                        }
                    }

                    foreach (var (k, v) in source.VariantSets)
                    {
                        copy.VariantSets.TryAdd(k, v);
                    }

                    foreach (var (k, v) in composed.Arrays)
                    {
                        copy.Arrays[k] = v;
                    }

                    foreach (var (k, v) in composed.Attributes)
                    {
                        copy.Attributes[k] = v;
                    }

                    foreach (var (k, v) in composed.Interpolation)
                    {
                        copy.Interpolation[k] = v;
                    }

                    foreach (var (k, v) in composed.Metadata)
                    {
                        copy.Metadata[k] = v;
                    }

                    foreach (var (k, v) in composed.VariantSets)
                    {
                        copy.VariantSets[k] = v;
                    }

                    copy.Children.AddRange(source.Children);
                    copy.Children.AddRange(composed.Children.Where(c => !copy.Children.Any(x => x.Name == c.Name)));
                    composed = copy;
                }
            }

            return ApplyVariants(composed, prim);
        }

        /// <summary>Authored selections only (<c>variants = { string set = "choice" }</c>): without one, a variant set contributes nothing, as in USD.</summary>
        private static Prim ApplyVariants(Prim composed, Prim original)
        {
            if (composed.VariantSets.Count == 0)
            {
                return composed;
            }

            var selections = composed.Metadata.TryGetValue("variants", out var chosen) && chosen is Dictionary<string, object> map ? map : null;
            if (selections is null && original.Metadata.TryGetValue("variants", out var own) && own is Dictionary<string, object> ownMap)
            {
                selections = ownMap;
            }

            if (selections is null)
            {
                return composed;
            }

            var result = new Prim { Kind = composed.Kind, Name = composed.Name, Parent = composed.Parent, Type = composed.Type, Source = composed.Source, Layer = composed.Layer };
            foreach (var (k, v) in composed.Attributes)
            {
                result.Attributes[k] = v;
            }

            foreach (var (k, v) in composed.Arrays)
            {
                result.Arrays[k] = v;
            }

            foreach (var (k, v) in composed.Interpolation)
            {
                result.Interpolation[k] = v;
            }

            foreach (var (k, v) in composed.Metadata)
            {
                result.Metadata[k] = v;
            }

            result.Children.AddRange(composed.Children);
            foreach (var (k, v) in composed.VariantSets)
            {
                result.VariantSets[k] = v;
            }

            foreach (var (set, choices) in composed.VariantSets)
            {
                if (!selections.TryGetValue(set, out var selected) || selected is not string name || !choices.TryGetValue(name, out var content))
                {
                    continue;
                }

                // The variant's opinions are stronger than the referenced ones but weaker than the prim's own: the prim's own attributes were copied
                // last by the caller, so only attributes the prim does not author itself are taken (a reference chain cannot tell them apart here).
                foreach (var (k, v) in content.Attributes)
                {
                    if (!original.Attributes.ContainsKey(k))
                    {
                        result.Attributes[k] = v;
                    }
                }

                foreach (var (k, v) in content.Arrays)
                {
                    if (!original.Arrays.ContainsKey(k))
                    {
                        result.Arrays[k] = v;
                    }
                }

                foreach (var (k, v) in content.Interpolation)
                {
                    result.Interpolation.TryAdd(k, v);
                }

                if (content.Type.Length > 0 && result.Type.Length == 0)
                {
                    result.Type = content.Type;
                }

                foreach (var child in content.Children)
                {
                    var existing = result.Children.FindIndex(c => c.Name == child.Name);
                    var adopted = child;
                    if (existing >= 0)
                    {
                        // an over of an existing child: layer its opinions on a copy
                        var under = result.Children[existing];
                        adopted = new Prim { Kind = under.Kind, Name = under.Name, Parent = under.Parent, Type = under.Type.Length > 0 ? under.Type : child.Type, Source = under.Source, Layer = under.Layer };
                        foreach (var (k, v) in under.Attributes) { adopted.Attributes[k] = v; }
                        foreach (var (k, v) in under.Arrays) { adopted.Arrays[k] = v; }
                        foreach (var (k, v) in under.Interpolation) { adopted.Interpolation[k] = v; }
                        foreach (var (k, v) in under.Metadata) { adopted.Metadata[k] = v; }
                        foreach (var (k, v) in child.Attributes) { adopted.Attributes[k] = v; }
                        foreach (var (k, v) in child.Arrays) { adopted.Arrays[k] = v; }
                        foreach (var (k, v) in child.Interpolation) { adopted.Interpolation[k] = v; }
                        foreach (var (k, v) in child.Metadata) { adopted.Metadata[k] = v; }
                        adopted.Children.AddRange(under.Children);
                        adopted.Children.AddRange(child.Children);
                        result.Children[existing] = adopted;
                    }
                    else
                    {
                        child.Layer ??= composed.Layer;
                        result.Children.Add(child);
                    }
                }
            }

            return result;
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
                if (p.Attributes.TryGetValue("material:binding", out var binding) && binding is string path && Find(p, path) is { } material)
                {
                    return MaterialIndex(material);
                }
            }

            return -1;
        }

        private int MaterialIndex(Prim material)
        {
            if (_materials.TryGetValue(material.Layer?.LayerPath + "|" + material.Path, out var index))
            {
                return index;
            }

            var result = new ModelMaterial { Name = material.Name };
            var shader = Descendants(material).FirstOrDefault(p => p.Type == "Shader" && (p.Attributes.ContainsKey("inputs:diffuseColor") || p.Attributes.ContainsKey("inputs:baseColor") || p.Attributes.ContainsKey("inputs:diffuseColor:connect")));
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

            if (shader is not null)
            {
                var colorInput = shader.Attributes.ContainsKey("inputs:diffuseColor:connect") ? "inputs:diffuseColor:connect" : "inputs:baseColor:connect";
                if (shader.Attributes.TryGetValue(colorInput, out var connection) && connection is string target && AddTexture(shader, target) is { } textureIndex)
                {
                    result.BaseColorTexture = new ModelTextureRef(textureIndex);
                    result.BaseColor = Vector4.One;
                }
            }

            _materials[material.Layer?.LayerPath + "|" + material.Path] = _model.Materials.Count;
            _model.Materials.Add(result);
            return _model.Materials.Count - 1;
        }

        /// <summary>The image behind a UsdUVTexture the colour input is connected to (<c>inputs:file</c>, read from a loose file or the package), or null.</summary>
        private int? AddTexture(Prim shader, string connection)
        {
            var dot = connection.IndexOf('.');
            var texture = Find(shader, dot < 0 ? connection : connection[..dot]);
            if (texture is null || !(texture.Text("info:id") == "UsdUVTexture") || texture.Text("inputs:file") is not { Length: > 0 } file)
            {
                return null;
            }

            var key = (texture.Layer?.LayerPath ?? "") + "|" + file;
            if (_textures.TryGetValue(key, out var known))
            {
                return known;
            }

            var bytes = _layers.Bytes(file.Split('\u0001')[0], texture.Layer?.LayerPath ?? "");
            string? type = bytes is null ? null : bytes.AsSpan().StartsWith(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }) ? "image/png" : bytes.Length > 2 && bytes[0] == 0xFF && bytes[1] == 0xD8 ? "image/jpeg" : null;
            if (bytes is null || type is null)
            {
                _model.Warnings.Add($"The texture '{file}' was not found or is not a PNG or JPEG; the material keeps its colour.");
                return null;
            }

            _model.Textures.Add(new ModelTexture { Name = Path.GetFileName(file), MediaType = type, Data = bytes, WrapU = TextureWrap.Repeat, WrapV = TextureWrap.Repeat });
            _textures[key] = _model.Textures.Count - 1;
            return _model.Textures.Count - 1;
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
            var lookup = new Dictionary<(int, int, int, int), uint>();
            var st = prim.Numbers("primvars:st");
            var stInterp = prim.Interpolation.TryGetValue("primvars:st", out var si) ? si : "vertex";
            var outSt = new List<float>();
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
                    var stIndex = stInterp switch { "faceVarying" => corner + k, "uniform" => face, "constant" => 0, _ => vi };
                    var lookupKey = (vi, normals.Length > 0 ? normalIndex : -1, colors.Length > 0 ? colorIndex : -1, st.Length > 0 ? stIndex : -1);
                    if (!lookup.TryGetValue(lookupKey, out var index))
                    {
                        index = (uint)(positions.Count / 3);
                        lookup[lookupKey] = index;
                        if (st.Length >= 2)
                        {
                            var at = 2 * stIndex + 1 < st.Length ? stIndex : 0;
                            outSt.AddRange([(float)st[2 * at], 1f - (float)st[2 * at + 1]]);
                        }

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

            if (outSt.Count == positions.Count / 3 * 2)
            {
                mesh.TexCoords = outSt.ToArray();
            }

            // Materials: GeomSubsets bind faces to materials; the mesh's own binding covers the rest.
            var defaultMaterial = MaterialOf(prim);
            var faceMaterial = new Dictionary<int, int>();
            foreach (var subset in prim.Children.Where(c => c.Type == "GeomSubset"))
            {
                var material = subset.Attributes.TryGetValue("material:binding", out var b) && b is string path && Find(subset, path) is { } mp ? MaterialIndex(mp) : defaultMaterial;
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
