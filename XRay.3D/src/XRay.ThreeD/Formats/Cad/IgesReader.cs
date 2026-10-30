using System.Globalization;
using System.Numerics;
using System.Text;
using XRay.ThreeD.Formats.Step;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.Cad;

/// <summary>
/// IGES (fixed 80-column records: start, global, directory, parameter and terminate sections). Metadata from the start and global
/// sections (names, units, author, dates, version) and entity counts; geometry for lines (110), circular arcs (100), copious data
/// paths (106), rational B-spline curves (126) and the transformation matrices (124) that place them, drawn as line meshes grouped
/// by level and colour. Surfaces and B-rep topology (128, 143, 144, 186, 510, 514) are counted and reported, not tessellated.
/// </summary>
internal static partial class IgesReader
{
    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options) => Parse(data, options).Build(geometry: true);

    public static ModelMetadata ReadMetadata(ReadOnlyMemory<byte> data, LoadOptions options) => Parse(data, options).Build(geometry: false).Metadata;

    /// <summary>The first record of an IGES file: 80 columns, section letter S in column 73 and sequence number 1.</summary>
    public static bool Sniff(ReadOnlySpan<byte> head)
    {
        if (head.Length < 80 || head[72] != (byte)'S')
        {
            return false;
        }

        var sequence = Encoding.ASCII.GetString(head.Slice(73, 7)).Trim();
        return sequence == "1" && head[..72].IndexOfAnyExceptInRange((byte)32, (byte)126) < 0;
    }

    private static readonly Dictionary<int, string> EntityNames = new()
    {
        [100] = "Circular arc", [102] = "Composite curve", [104] = "Conic arc", [106] = "Copious data", [108] = "Plane", [110] = "Line",
        [112] = "Parametric spline curve", [114] = "Parametric spline surface", [116] = "Point", [118] = "Ruled surface",
        [120] = "Surface of revolution", [122] = "Tabulated cylinder", [124] = "Transformation matrix", [126] = "Rational B-spline curve",
        [128] = "Rational B-spline surface", [130] = "Offset curve", [141] = "Boundary", [142] = "Curve on a parametric surface",
        [143] = "Bounded surface", [144] = "Trimmed surface", [186] = "Manifold solid B-rep object", [202] = "Angular dimension",
        [212] = "General note", [214] = "Leader arrow", [308] = "Subfigure definition", [314] = "Color definition", [402] = "Associativity instance",
        [406] = "Property", [408] = "Singular subfigure instance", [502] = "Vertex list", [504] = "Edge list", [508] = "Loop", [510] = "Face",
        [514] = "Shell",
    };

    private sealed record DirectoryEntry(int Type, int Pointer, int Count, int Transform, int Level, int Color, int Form, int Use, int Blank = 0, int Subordinate = 0, string Label = "");

    private static Builder Parse(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var text = Encoding.Latin1.GetString(data.Span);
        var start = new StringBuilder();
        var global = new StringBuilder();
        var directoryLines = new List<string>();
        var parameterLines = new List<string>();
        var length = text.Length;
        var position = 0;
        var any = false;
        while (position < length)
        {
            var end = text.IndexOf('\n', position);
            if (end < 0)
            {
                end = length;
            }

            var line = text.AsSpan(position, end - position).TrimEnd('\r');
            position = end + 1;
            if (line.Length < 73)
            {
                continue;
            }

            var section = line[72];
            var body = line[..72];
            switch (section)
            {
                case 'S':
                    any = true;
                    start.Append(body.TrimEnd()).Append('\n');
                    break;
                case 'G':
                    global.Append(body);
                    break;
                case 'D':
                    directoryLines.Add(line[..72].ToString());
                    break;
                case 'P':
                    parameterLines.Add(line[..64].ToString());
                    break;
            }

            if (parameterLines.Count + directoryLines.Count > 4_000_000)
            {
                throw new ModelFormatException("The IGES file has too many records.");
            }
        }

        if (!any || global.Length == 0)
        {
            throw new ModelFormatException("Not an IGES file: the start or global section is missing.");
        }

        return new Builder(start.ToString().TrimEnd(), global.ToString(), directoryLines, parameterLines, options);
    }

    private sealed partial class Builder
    {
        private readonly string _start;
        private readonly string _global;
        private readonly List<string> _directoryLines;
        private readonly List<string> _parameterLines;
        private readonly LoadOptions _options;
        private readonly Model3D _model = new();
        private readonly List<DirectoryEntry> _entries = new();
        private readonly Dictionary<int, DirectoryEntry> _byIndex = new();
        private readonly Dictionary<int, Matrix4x4> _matrices = new();
        private char _delimiter = ',';
        private char _terminator = ';';

        public Builder(string start, string global, List<string> directoryLines, List<string> parameterLines, LoadOptions options)
        {
            _start = start;
            _global = global;
            _directoryLines = directoryLines;
            _parameterLines = parameterLines;
            _options = options;
        }

        public Model3D Build(bool geometry)
        {
            var meta = _model.Metadata;
            meta.Format = ModelFormats.Iges;
            meta.DocumentKind = "drawing";
            ReadGlobal(meta);
            ReadDirectory();
            CountEntities(meta);
            if (_start.Length > 0)
            {
                meta.Properties.Add(new ModelProperty("description", _start, "iges"));
            }

            var surfaceEntities = _entries.Count(e => e.Type is 108 or 114 or 118 or 120 or 122 or 128 or 143 or 144 or 186 or 514);
            var curveEntities = _entries.Count(e => e.Type is 100 or 110 or 106 or 126 or 112);
            meta.HasGeometry = curveEntities > 0 || surfaceEntities > 0;

            if (geometry)
            {
                BuildGeometry();
                meta.HasGeometry = _model.Meshes.Count > 0;
                meta.Statistics = _model.ComputeStatistics();
                meta.Bounds = _model.ComputeBounds();
                if (_model.Meshes.Any(m => m.Indices.Length > 0))
                {
                    meta.DocumentKind = "scene"; // faces were tessellated: a model, not a drawing
                }

                if (_model.Meshes.All(m => m.Indices.Length == 0))
                {
                    _model.DetectIs2D(); // curves only, all in one plane: a drawing
                }
            }

            return _model;
        }

        // ---- global section --------------------------------------------------------------------------------------

        private void ReadGlobal(ModelMetadata meta)
        {
            // The first two parameters may redefine the delimiters: ",," keeps the defaults, otherwise "1H;,1H;" style Hollerith.
            var g = _global;
            var p = 0;
            if (g.Length > 0 && g[0] != _delimiter)
            {
                _delimiter = ReadHollerith(g, ref p) is { Length: 1 } d ? d[0] : ',';
                p++;
            }
            else
            {
                p = 0;
            }

            if (p < g.Length && g[p] == _delimiter)
            {
                p++;
            }

            if (p < g.Length && g[p] != _delimiter)
            {
                _terminator = ReadHollerith(g, ref p) is { Length: 1 } t ? t[0] : ';';
                p++;
            }
            else if (p < g.Length)
            {
                p++;
            }

            var fields = new List<string> { _delimiter.ToString(), _terminator.ToString() };
            fields.AddRange(Fields(g, p, _delimiter, _terminator));
            string F(int index) => index <= fields.Count ? fields[index - 1].Trim() : "";

            if (F(3).Length > 0)
            {
                meta.Title = F(3);
            }
            else if (F(4).Length > 0)
            {
                meta.Title = F(4);
            }

            var application = string.Join(" / ", new[] { F(5), F(6) }.Where(s => s.Length > 0 && !s.Equals("unknown", StringComparison.OrdinalIgnoreCase)));
            if (application.Length > 0)
            {
                meta.Application = application;
            }

            if (F(21).Length > 0 && !F(21).Equals("unknown", StringComparison.OrdinalIgnoreCase))
            {
                meta.Author = F(21);
            }

            if (F(22).Length > 0 && !F(22).Equals("unknown", StringComparison.OrdinalIgnoreCase))
            {
                meta.Company = F(22);
            }

            meta.Created = IgesDate(F(18));
            meta.Modified = IgesDate(F(25));
            if (int.TryParse(F(23), NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            {
                meta.FormatVersion = version switch
                {
                    1 => "IGES 1.0",
                    2 => "ANSI Y14.26M-1981",
                    3 => "IGES 2.0",
                    4 => "IGES 3.0",
                    5 => "ASME/ANSI Y14.26M-1987",
                    6 => "IGES 4.0",
                    7 => "ASME Y14.26M-1989",
                    8 => "IGES 5.0",
                    9 => "IGES 5.1",
                    10 => "IGES 5.2",
                    11 => "IGES 5.3",
                    _ => "version code " + version.ToString(CultureInfo.InvariantCulture),
                };
            }

            if (F(4).Length > 0 && F(4) != meta.Title)
            {
                meta.Properties.Add(new ModelProperty("file name", F(4), "iges"));
            }

            if (F(12).Length > 0 && F(12) != meta.Title)
            {
                meta.Properties.Add(new ModelProperty("receiving product", F(12), "iges"));
            }

            if (F(26).Length > 0)
            {
                meta.Properties.Add(new ModelProperty("application protocol", F(26), "iges"));
            }

            if (F(24).Length > 0 && F(24) != "0")
            {
                meta.Properties.Add(new ModelProperty("drafting standard", F(24), "iges"));
            }

            if (F(13).Length > 0)
            {
                meta.Properties.Add(new ModelProperty("model space scale", F(13), "iges"));
            }

            var flag = int.TryParse(F(14), NumberStyles.None, CultureInfo.InvariantCulture, out var f) ? f : 0;
            meta.Units = flag switch
            {
                1 => LengthUnit.Inch,
                2 => LengthUnit.Millimeter,
                4 => LengthUnit.Foot,
                5 => LengthUnit.Mile,
                6 => LengthUnit.Meter,
                7 => LengthUnit.Kilometer,
                8 => LengthUnit.Mil,
                9 => LengthUnit.Micrometer,
                10 => LengthUnit.Centimeter,
                11 => LengthUnit.Microinch,
                3 => F(15).ToUpperInvariant() switch
                {
                    "IN" or "INCH" => LengthUnit.Inch,
                    "MM" => LengthUnit.Millimeter,
                    "FT" or "FEET" or "FOOT" => LengthUnit.Foot,
                    "M" or "METER" or "METRE" => LengthUnit.Meter,
                    "CM" => LengthUnit.Centimeter,
                    "KM" => LengthUnit.Kilometer,
                    _ => null,
                },
                _ => null,
            };
            if (meta.Units is null)
            {
                meta.Notes.Add("The file states no length unit.");
            }

            meta.UpAxis = UpAxis.Z;
        }

        private static DateTimeOffset? IgesDate(string text)
        {
            // YYYYMMDD.HHNNSS, or the old YYMMDD.HHNNSS.
            var dot = text.IndexOf('.', StringComparison.Ordinal);
            if (dot != 6 && dot != 8 || text.Length < dot + 7)
            {
                return null;
            }

            var date = dot == 6 ? "20" + text[..6] : text[..8];
            return DateTimeOffset.TryParseExact(date + text.Substring(dot + 1, 6), "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
        }

        /// <summary>Reads "nHtext" at <paramref name="p"/>, leaving <paramref name="p"/> after the text.</summary>
        private static string? ReadHollerith(string s, ref int p)
        {
            var start = p;
            while (p < s.Length && char.IsDigit(s[p]))
            {
                p++;
            }

            if (p == start || p >= s.Length || s[p] != 'H' || !int.TryParse(s.AsSpan(start, p - start), NumberStyles.None, CultureInfo.InvariantCulture, out var n) || p + 1 + n > s.Length)
            {
                p = start;
                return null;
            }

            var result = s.Substring(p + 1, n);
            p += 1 + n;
            return result;
        }

        /// <summary>Splits delimiter-separated fields up to the record terminator, honouring Hollerith strings.</summary>
        private static List<string> Fields(string s, int p, char delimiter, char terminator)
        {
            var fields = new List<string>();
            while (p <= s.Length)
            {
                while (p < s.Length && s[p] == ' ')
                {
                    p++; // a record may be padded with blanks after a delimiter
                }

                if (p >= s.Length)
                {
                    break;
                }

                var save = p;
                var hollerith = ReadHollerith(s, ref p);
                if (hollerith is not null && (p >= s.Length || s[p] == delimiter || s[p] == terminator))
                {
                    fields.Add(hollerith);
                }
                else
                {
                    p = save;
                    var q = p;
                    while (q < s.Length && s[q] != delimiter && s[q] != terminator)
                    {
                        q++;
                    }

                    fields.Add(s[p..q].Trim());
                    p = q;
                }

                if (p >= s.Length || s[p] == terminator)
                {
                    break;
                }

                p++; // the delimiter
                if (p >= s.Length)
                {
                    break;
                }
            }

            return fields;
        }

        // ---- directory -------------------------------------------------------------------------------------------

        private static int Int(string line, int from, int length)
        {
            var span = line.AsSpan(Math.Min(from, line.Length), Math.Max(0, Math.Min(length, line.Length - from))).Trim();
            return int.TryParse(span, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v) ? v : 0;
        }

        private void ReadDirectory()
        {
            for (var i = 0; i + 1 < _directoryLines.Count; i += 2)
            {
                var a = _directoryLines[i];
                var b = _directoryLines[i + 1];
                var status = a.Length >= 72 ? a.Substring(64, 8) : "";
                var use = status.Length == 8 && int.TryParse(status.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var u) ? u : 0;
                // Status: blank (hidden) and subordinate switches, entity use flag, hierarchy.
                var blank = status.Length == 8 && int.TryParse(status.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var bl) ? bl : 0;
                var subordinate = status.Length == 8 && int.TryParse(status.AsSpan(2, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var sub) ? sub : 0;
                var label = b.Length >= 64 ? b.Substring(56, 8).Trim() : "";
                var entry = new DirectoryEntry(Int(a, 0, 8), Int(a, 8, 8), Int(b, 24, 8), Int(a, 48, 8), Int(a, 32, 8), Int(b, 16, 8), Int(b, 32, 8), use, blank, subordinate, label);
                _entries.Add(entry);
                _byIndex[i + 1] = entry;
            }
        }

        private void CountEntities(ModelMetadata meta)
        {
            foreach (var group in _entries.GroupBy(e => e.Type).OrderByDescending(g => g.Count()).ThenBy(g => g.Key))
            {
                var name = EntityNames.TryGetValue(group.Key, out var n) ? n : "Entity " + group.Key.ToString(CultureInfo.InvariantCulture);
                meta.Properties.Add(new ModelProperty($"{group.Key} {name}", group.Count().ToString(CultureInfo.InvariantCulture), "entities"));
            }
        }

        // ---- parameters ------------------------------------------------------------------------------------------

        private List<string>? Parameters(DirectoryEntry entry)
        {
            var first = entry.Pointer - 1;
            if (first < 0 || first >= _parameterLines.Count)
            {
                return null;
            }

            var sb = new StringBuilder();
            for (var i = first; i < _parameterLines.Count && i < first + Math.Max(entry.Count, 1) && sb.Length < 1 << 24; i++)
            {
                sb.Append(_parameterLines[i]);
            }

            var fields = Fields(sb.ToString(), 0, _delimiter, _terminator);
            return fields.Count > 0 ? fields : null;
        }

        private static double Real(List<string> f, int index)
        {
            if (index >= f.Count || f[index].Length == 0)
            {
                return 0;
            }

            var text = f[index].Replace('D', 'E').Replace('d', 'e');
            return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : 0;
        }

        private static int Count(List<string> f, int index) => (int)Math.Clamp(Real(f, index), 0, 10_000_000);

        private Matrix4x4 TransformOf(DirectoryEntry entry, int depth = 0)
        {
            if (entry.Transform <= 0 || depth > 8 || !_byIndex.TryGetValue(entry.Transform, out var t) || t.Type != 124)
            {
                return Matrix4x4.Identity;
            }

            if (!_matrices.TryGetValue(entry.Transform, out var m))
            {
                var f = Parameters(t);
                if (f is null)
                {
                    m = Matrix4x4.Identity;
                }
                else
                {
                    var r = new Func<int, float>(i => (float)Real(f, i));
                    m = new Matrix4x4(r(1), r(5), r(9), 0, r(2), r(6), r(10), 0, r(3), r(7), r(11), 0, r(4), r(8), r(12), 1);
                    m *= TransformOf(t, depth + 1);
                }

                _matrices[entry.Transform] = m;
            }

            return m;
        }

        // ---- geometry --------------------------------------------------------------------------------------------

        private SceneNode? _root;
        private readonly Dictionary<int, SceneNode> _levelNodes = new();
        private readonly Dictionary<int, int> _layerIndex = new();
        private readonly Dictionary<(float, float, float), int> _materialIndex = new();

        private static string LayerName(int level) => level == 0 ? "0" : level.ToString(CultureInfo.InvariantCulture);

        private int LayerOf(int level)
        {
            if (!_layerIndex.TryGetValue(level, out var layer))
            {
                layer = _model.Layers.Count;
                _layerIndex[level] = layer;
                _model.Layers.Add(new ModelLayer(LayerName(level)));
                _model.Metadata.LayerNames.Add(LayerName(level));
            }

            return layer;
        }

        /// <summary>The node under the title root that holds everything drawn on one level.</summary>
        private SceneNode LevelNode(int level)
        {
            _root ??= new SceneNode(_model.Metadata.Title);
            if (!_levelNodes.TryGetValue(level, out var node))
            {
                _levelNodes[level] = node = new SceneNode("level " + LayerName(level)) { Layer = LayerOf(level) };
                _root.Children.Add(node);
            }

            return node;
        }

        private int MaterialFor((float R, float G, float B) color)
        {
            if (!_materialIndex.TryGetValue(color, out var material))
            {
                material = _model.Materials.Count;
                _materialIndex[color] = material;
                _model.Materials.Add(new ModelMaterial { Name = $"#{(int)(color.R * 255):X2}{(int)(color.G * 255):X2}{(int)(color.B * 255):X2}", BaseColor = new Vector4(color.R, color.G, color.B, 1) });
            }

            return material;
        }

        private (float R, float G, float B) ColorOf(DirectoryEntry entry)
        {
            if (entry.Color > 0)
            {
                return entry.Color switch
                {
                    1 => (0, 0, 0),
                    2 => (1, 0, 0),
                    3 => (0, 1, 0),
                    4 => (0, 0, 1),
                    5 => (1, 1, 0),
                    6 => (1, 0, 1),
                    7 => (0, 1, 1),
                    8 => (1, 1, 1),
                    _ => (0.2f, 0.2f, 0.2f),
                };
            }

            if (entry.Color < 0 && _byIndex.TryGetValue(-entry.Color, out var definition) && definition.Type == 314 && Parameters(definition) is { } f)
            {
                return ((float)Math.Clamp(Real(f, 1) / 100, 0, 1), (float)Math.Clamp(Real(f, 2) / 100, 0, 1), (float)Math.Clamp(Real(f, 3) / 100, 0, 1));
            }

            return (0.15f, 0.15f, 0.15f);
        }

        private void BuildGeometry()
        {
            var surfaceTriangles = BuildSurfaces();
            // 2D parameter-space curves used by the loops of a B-rep are not part of the 3D drawing.
            var parameterSpace = new HashSet<int>();
            foreach (var entry in _entries.Where(e => e.Type == 508))
            {
                var f = Parameters(entry);
                if (f is null)
                {
                    continue;
                }

                var n = Count(f, 1);
                var p = 2;
                var tick515 = 0L;
                for (var i = 0; i < n && p + 4 < f.Count; i++)
                {
                    Cancel.Every(_options.Cancellation, tick515++);
                    var k = Count(f, p + 4);
                    for (var j = 0; j < k && p + 6 + 2 * j < f.Count; j++)
                    {
                        parameterSpace.Add((int)Real(f, p + 6 + 2 * j));
                    }

                    p += 5 + 2 * k;
                }
            }

            if (surfaceTriangles > 0)
            {
                MarkBoundaryCurvesConsumed();
            }

            var sinks = new Dictionary<(int Level, (float, float, float) Color), MeshAccumulator>();
            var skippedCurves = 0;
            var drawn = 0;
            foreach (var (index, entry) in _byIndex.OrderBy(p => p.Key))
            {
                if (entry.Type is not (100 or 110 or 106 or 126 or 112))
                {
                    continue;
                }

                if (entry.Use == 5 || parameterSpace.Contains(entry.Pointer) || parameterSpace.Contains(index) || surfaceTriangles > 0 && _consumedCurves.Contains(index))
                {
                    skippedCurves++;
                    continue;
                }

                if (index % 64 == 1)
                {
                    _options.Cancellation.ThrowIfCancellationRequested();
                }

                var parameters = Parameters(entry);
                if (parameters is null)
                {
                    continue;
                }

                var lines = entry.Type switch
                {
                    110 => Line(parameters),
                    100 => Arc(parameters),
                    106 => Copious(entry, parameters),
                    112 => ParametricSpline(parameters),
                    _ => Nurbs(parameters),
                };
                if (lines is null || lines.Count < 2)
                {
                    continue;
                }

                var matrix = TransformOf(entry);
                var key = (entry.Level, ColorOf(entry));
                if (!sinks.TryGetValue(key, out var sink))
                {
                    sinks[key] = sink = new MeshAccumulator(_options.MaxTriangles) { Cancel = _options.Cancellation };
                }

                uint previous = 0;
                for (var i = 0; i < lines.Count; i++)
                {
                    var v = sink.AddVertex(Vector3.Transform(new Vector3((float)lines[i].X, (float)lines[i].Y, (float)lines[i].Z), matrix));
                    if (i > 0)
                    {
                        sink.AddLine(previous, v);
                    }

                    previous = v;
                }

                drawn++;
            }

            foreach (var ((level, color), sink) in sinks.OrderBy(p => p.Key.Level).ThenBy(p => p.Key.Color))
            {
                var material = MaterialFor(color);
                var layer = LayerOf(level);
                var mesh = sink.ToMesh("level " + LayerName(level), material);
                if (mesh is null)
                {
                    continue;
                }

                _model.Meshes.Add(mesh);
                LevelNode(level).Children.Add(new SceneNode(null) { Mesh = mesh, Layer = layer });
            }

            if (_root is { Children.Count: > 0 })
            {
                _model.Roots.Add(_root);
            }

            if (_unsupportedSurfaces > 0)
            {
                _model.Warnings.Add($"{_unsupportedSurfaces} surface entities (parametric splines, ruled surfaces, faces on them) are not tessellated.");
            }

            if (_skippedFaces > 0)
            {
                _model.Warnings.Add($"{_skippedFaces} faces could not be tessellated and were skipped.");
            }

            foreach (var warning in _surfaceWarnings.Distinct().Take(20))
            {
                _model.Warnings.Add(warning);
            }

            if (drawn == 0 && surfaceTriangles == 0)
            {
                _model.Metadata.Notes.Add("The file holds no curve entities this reader can draw.");
            }

            _ = skippedCurves;
        }

        private static List<V3>? Line(List<string> f) =>
            f.Count >= 7 ? [new V3(Real(f, 1), Real(f, 2), Real(f, 3)), new V3(Real(f, 4), Real(f, 5), Real(f, 6))] : null;

        private List<V3>? Arc(List<string> f)
        {
            if (f.Count < 8)
            {
                return null;
            }

            double z = Real(f, 1), cx = Real(f, 2), cy = Real(f, 3), sx = Real(f, 4), sy = Real(f, 5), ex = Real(f, 6), ey = Real(f, 7);
            var radius = Math.Sqrt((sx - cx) * (sx - cx) + (sy - cy) * (sy - cy));
            var a0 = Math.Atan2(sy - cy, sx - cx);
            var a1 = Math.Atan2(ey - cy, ex - cx);
            if (a1 <= a0 + 1e-12)
            {
                a1 += 2 * Math.PI;
            }

            var steps = (int)Math.Clamp(Math.Ceiling((a1 - a0) / Math.Min(Math.Max(_options.AngularDeflection, 0.02), 0.15)), 4, 720);
            var points = new List<V3>(steps + 1);
            for (var i = 0; i <= steps; i++)
            {
                var a = a0 + (a1 - a0) * i / steps;
                points.Add(new V3(cx + radius * Math.Cos(a), cy + radius * Math.Sin(a), z));
            }

            return points;
        }

        private static List<V3>? Copious(DirectoryEntry entry, List<string> f)
        {
            // Forms 11, 12, 13 and 63 are linear paths (a polyline); the other forms are point sets.
            if (entry.Form is not (11 or 12 or 13 or 63))
            {
                return null;
            }

            var ip = Count(f, 1);
            var n = Count(f, 2);
            var stride = ip switch { 1 => 2, 2 => 3, 3 => 6, _ => 0 };
            if (stride == 0 || n < 2)
            {
                return null;
            }

            var first = ip == 1 ? 4 : 3;
            if (first + (long)n * stride > f.Count + 1)
            {
                n = Math.Max(0, (f.Count - first) / stride);
            }

            var points = new List<V3>(n);
            var z = ip == 1 ? Real(f, 3) : 0;
            for (var i = 0; i < n; i++)
            {
                var o = first + i * stride;
                points.Add(new V3(Real(f, o), Real(f, o + 1), ip == 1 ? z : Real(f, o + 2)));
            }

            return points;
        }

        private static List<V3>? Nurbs(List<string> f)
        {
            if (f.Count < 8)
            {
                return null;
            }

            var k = Count(f, 1);
            var m = Count(f, 2);
            if (m < 1 || m > 64 || k < m || k > 5_000_000)
            {
                return null;
            }

            var n = k + 1;
            var knotCount = n + m + 1;
            var o = 7;
            if ((long)o + knotCount + n + 3L * n + 2 > f.Count + 3)
            {
                return null;
            }

            var knots = new double[knotCount];
            for (var i = 0; i < knotCount; i++)
            {
                knots[i] = Real(f, o + i);
                if (i > 0 && knots[i] < knots[i - 1])
                {
                    return null; // knots must not decrease
                }
            }

            o += knotCount;
            var weights = new double[n];
            for (var i = 0; i < n; i++)
            {
                weights[i] = Real(f, o + i);
                if (!(weights[i] > 0))
                {
                    weights[i] = 1;
                }
            }

            o += n;
            var control = new V3[n];
            for (var i = 0; i < n; i++)
            {
                control[i] = new V3(Real(f, o + 3 * i), Real(f, o + 3 * i + 1), Real(f, o + 3 * i + 2));
            }

            o += 3 * n;
            double t0 = Real(f, o), t1 = Real(f, o + 1);
            t0 = Math.Max(t0, knots[m]);
            t1 = Math.Min(t1, knots[n]);
            if (!(t1 > t0))
            {
                t0 = knots[m];
                t1 = knots[n];
            }

            if (m == 1)
            {
                return [.. control];
            }

            var curve = new SplineCurve { Degree = m, Control = control, Weights = weights, Knots = knots };
            var samples = Math.Clamp(n * 4, 16, 256);
            var points = new List<V3>(samples + 1);
            for (var s = 0; s <= samples; s++)
            {
                points.Add(curve.At(t0 + (t1 - t0) * s / samples));
            }

            return points;
        }
    }
}
