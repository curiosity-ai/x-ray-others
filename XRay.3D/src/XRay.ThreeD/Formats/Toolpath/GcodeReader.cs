using System.Globalization;
using System.Numerics;
using System.Text;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.Toolpath;

/// <summary>
/// G-code toolpaths (3D printing and CNC): G0/G1 moves, G2/G3 arcs (centre or radius form, any of the three planes, helical), absolute and
/// relative positioning, G20/G21 units, G92 offsets, relative or absolute extrusion. Moves become line meshes: "extrusion" and "travel" for a
/// printer (an E word that increases), "cut" and "rapid" for a machine without one. Layer comments (Cura, PrusaSlicer, Simplify3D) and the
/// slicer's header values go to the metadata.
/// </summary>
internal static class GcodeReader
{
    private static readonly HashSet<string> HeaderKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "layer_height", "nozzle_diameter", "filament_type", "filament used [mm]", "filament used [g]", "estimated printing time (normal mode)",
        "total filament used [g]", "total filament cost", "temperature", "bed_temperature", "first_layer_temperature", "perimeters",
        "fill_density", "flavor", "time", "filament used", "layer height", "minx", "miny", "minz", "maxx", "maxy", "maxz", "target_machine.name",
        "printer_model", "gcode_flavor", "print_time", "material",
    };

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options) => new Parser(data, options).Run();

    /// <summary>True when the bytes are plain text: no control characters other than tab, line feed, form feed and carriage return.</summary>
    public static bool IsText(ReadOnlySpan<byte> head)
    {
        foreach (var b in head)
        {
            if (b is < 9 or (> 13 and < 32) or > 126)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True when the head of a file is text that has G-code in it: a G or M word starting a line, or a typical slicer comment.</summary>
    public static bool LooksLikeGcode(ReadOnlySpan<byte> head)
    {
        if (!IsText(head))
        {
            return false;
        }

        var text = Encoding.ASCII.GetString(head);
        var hits = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().ToUpperInvariant();
            if (line.Length >= 2 && (line[0] is 'G' or 'M') && char.IsDigit(line[1]) && (line.Length == 2 || !char.IsLetter(line[2]) || line[2] == ' '))
            {
                hits++;
            }
        }

        return hits >= 2 || text.Contains("FLAVOR:", StringComparison.Ordinal) || text.Contains(";LAYER_COUNT:", StringComparison.Ordinal);
    }

    private sealed class Sink
    {
        public readonly MeshAccumulator Acc;
        public uint Last = uint.MaxValue;
        public Vector3 LastPoint;

        public Sink(long limit)
        {
            Acc = new MeshAccumulator(limit);
        }
    }

    private sealed class Parser
    {
        private readonly ReadOnlyMemory<byte> _data;
        private readonly LoadOptions _options;
        private readonly Model3D _model = new();
        private readonly Sink _draw;
        private readonly Sink _move;
        private readonly Sink _plain;
        private readonly double[] _pos = new double[3];
        private readonly double[] _offset = new double[3];
        private readonly HashSet<int> _zLevels = new();
        private readonly SortedSet<double> _zValues = new();
        private readonly Dictionary<string, string> _header = new(StringComparer.OrdinalIgnoreCase);
        private bool _absolute = true;
        private bool _extruderAbsolute = true;
        private double _e;
        private bool _hasE;
        private bool _baseInch;
        private bool _currentInch;
        private bool _unitSet;
        private bool _mixedUnits;
        private int _motion = 1;
        private int _plane = 17;
        private int _layerComments;
        private int _declaredLayers;
        private long _moves;
        private long _arcs;
        private long _lines;
        private bool _expressions;
        private string? _application;
        private double? _layerHeightComment;

        public Parser(ReadOnlyMemory<byte> data, LoadOptions options)
        {
            _data = data;
            _options = options;
            _draw = new Sink(options.MaxTriangles);
            _move = new Sink(options.MaxTriangles);
            _plain = new Sink(options.MaxTriangles);
        }

        public Model3D Run()
        {
            var span = _data.Span;
            var start = 0;
            while (start < span.Length)
            {
                var end = span[start..].IndexOf((byte)'\n');
                end = end < 0 ? span.Length : start + end;
                var line = Encoding.Latin1.GetString(span[start..end]);
                start = end + 1;
                if (++_lines % 8192 == 0)
                {
                    _options.Cancellation.ThrowIfCancellationRequested();
                }

                Line(line);
            }

            return Finish();
        }

        private void Line(string raw)
        {
            var line = raw.TrimEnd('\r');
            var semicolon = line.IndexOf(';', StringComparison.Ordinal);
            if (semicolon >= 0)
            {
                Comment(line[(semicolon + 1)..]);
                line = line[..semicolon];
            }

            // Parenthesised comments (CNC) and block checksums.
            while (true)
            {
                var open = line.IndexOf('(', StringComparison.Ordinal);
                if (open < 0)
                {
                    break;
                }

                var close = line.IndexOf(')', open + 1);
                line = close < 0 ? line[..open] : line[..open] + " " + line[(close + 1)..];
            }

            var star = line.IndexOf('*', StringComparison.Ordinal);
            if (star >= 0)
            {
                line = line[..star];
            }

            if (line.Length == 0 || line.AsSpan().IsWhiteSpace())
            {
                return;
            }

            var words = Words(line);
            if (words.Count == 0)
            {
                return;
            }

            // Modal state first (G and M words), then the motion with the axis words.
            var motionSeen = -1;
            foreach (var (letter, value) in words)
            {
                if (letter == 'G')
                {
                    var g = (int)Math.Round(value);
                    var fractional = Math.Abs(value - g) > 1e-6;
                    switch (g)
                    {
                        case 0 or 1 or 2 or 3 when !fractional:
                            motionSeen = g;
                            break;
                        case 17 or 18 or 19 when !fractional:
                            _plane = g;
                            break;
                        case 20 when !fractional:
                            SetUnits(inch: true);
                            break;
                        case 21 when !fractional:
                            SetUnits(inch: false);
                            break;
                        case 90 when !fractional:
                            _absolute = true;
                            break;
                        case 91 when !fractional:
                            _absolute = false;
                            break;
                        case 28 when !fractional:
                            Home(words);
                            return;
                        case 92 when !fractional:
                            SetPosition(words);
                            return;
                    }
                }
                else if (letter == 'M')
                {
                    var m = (int)Math.Round(value);
                    if (m == 82)
                    {
                        _extruderAbsolute = true;
                    }
                    else if (m == 83)
                    {
                        _extruderAbsolute = false;
                    }
                }
            }

            if (motionSeen >= 0)
            {
                _motion = motionSeen;
            }

            var hasAxis = words.Any(w => w.Letter is 'X' or 'Y' or 'Z' or 'E');
            if (!hasAxis || _motion > 3)
            {
                return;
            }

            // Only move when the line has a motion word, or continues the modal one with axes.
            Move(words, _motion);
        }

        private void SetUnits(bool inch)
        {
            if (!_unitSet && _moves == 0)
            {
                _baseInch = inch;
            }

            _unitSet = true;
            _currentInch = inch;
            if (_currentInch != _baseInch)
            {
                _mixedUnits = true;
            }
        }

        private double Scale => _currentInch == _baseInch ? 1 : _currentInch ? 25.4 : 1 / 25.4;

        private static List<(char Letter, double Value)> Words(string line)
        {
            var words = new List<(char, double)>();
            var i = 0;
            while (i < line.Length)
            {
                var c = char.ToUpperInvariant(line[i]);
                if (!char.IsLetter(c))
                {
                    i++;
                    continue;
                }

                var j = i + 1;
                while (j < line.Length && line[j] == ' ')
                {
                    j++;
                }

                var numberStart = j;
                if (j < line.Length && (line[j] is '-' or '+'))
                {
                    j++;
                }

                while (j < line.Length && (char.IsDigit(line[j]) || line[j] == '.'))
                {
                    j++;
                }

                if (j > numberStart && double.TryParse(line.AsSpan(numberStart, j - numberStart), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
                {
                    words.Add((c, value));
                }
                else if (j < line.Length && line[j] is '#' or '[')
                {
                    words.Add((c, double.NaN)); // a parameter or expression: not evaluated
                }

                i = Math.Max(j, i + 1);
            }

            return words;
        }

        private double? Axis(List<(char Letter, double Value)> words, char letter)
        {
            foreach (var (l, v) in words)
            {
                if (l == letter)
                {
                    if (double.IsNaN(v))
                    {
                        _expressions = true;
                        return null;
                    }

                    return v * Scale;
                }
            }

            return null;
        }

        private void Home(List<(char Letter, double Value)> words)
        {
            var any = words.Any(w => w.Letter is 'X' or 'Y' or 'Z');
            for (var k = 0; k < 3; k++)
            {
                if (!any || words.Any(w => w.Letter == "XYZ"[k]))
                {
                    _pos[k] = 0;
                }
            }

            BreakPath();
        }

        private void SetPosition(List<(char Letter, double Value)> words)
        {
            var any = false;
            for (var k = 0; k < 3; k++)
            {
                if (Axis(words, "XYZ"[k]) is { } v)
                {
                    _offset[k] += _pos[k] - v;
                    _pos[k] = v;
                    any = true;
                }
            }

            if (Axis(words, 'E') is { } e)
            {
                _e = e;
                _hasE = true;
            }
            else if (!any && words.All(w => w.Letter is 'G'))
            {
                _e = 0;
            }

            BreakPath();
        }

        private void BreakPath()
        {
            _draw.Last = uint.MaxValue;
            _move.Last = uint.MaxValue;
        }

        private void Move(List<(char Letter, double Value)> words, int motion)
        {
            var target = (double[])_pos.Clone();
            for (var k = 0; k < 3; k++)
            {
                if (Axis(words, "XYZ"[k]) is { } v)
                {
                    target[k] = _absolute ? v : _pos[k] + v;
                }
            }

            var extrusion = 0.0;
            var eWord = false;
            if (Axis(words, 'E') is { } e)
            {
                eWord = true;
                _hasE = true;
                var next = _extruderAbsolute ? e : _e + e;
                extrusion = next - _e;
                _e = next;
            }

            var travel = motion == 0 || (eWord ? !(extrusion > 1e-9) : _hasE);
            var sink = motion == 0 ? _move : eWord ? (extrusion > 1e-9 ? _draw : _move) : _hasE ? _move : _plain;
            _moves++;
            var moved = target[0] != _pos[0] || target[1] != _pos[1] || target[2] != _pos[2];
            if (!moved)
            {
                return;
            }

            if (motion >= 2)
            {
                _arcs++;
                Arc(words, motion == 3, target, sink);
            }
            else
            {
                Segment(sink, new Vector3((float)_pos[0], (float)_pos[1], (float)_pos[2]), new Vector3((float)target[0], (float)target[1], (float)target[2]));
            }

            if (!travel && eWord)
            {
                var layer = (int)Math.Round(target[2] * 10000);
                if (_zLevels.Add(layer))
                {
                    _zValues.Add(Math.Round(target[2], 4));
                }
            }

            Array.Copy(target, _pos, 3);
        }

        private static void Segment(Sink sink, Vector3 a, Vector3 b)
        {
            if (sink.Last == uint.MaxValue || sink.LastPoint != a)
            {
                sink.Last = sink.Acc.AddVertex(a);
            }

            var end = sink.Acc.AddVertex(b);
            sink.Acc.AddLine(sink.Last, end);
            sink.Last = end;
            sink.LastPoint = b;
        }

        private void Arc(List<(char Letter, double Value)> words, bool counterClockwise, double[] target, Sink sink)
        {
            // Plane axes (a, b) turn about the axis c; the offsets are named I, J, K for X, Y, Z.
            int a, b, c;
            switch (_plane)
            {
                case 18:
                    (a, b, c) = (2, 0, 1);
                    break;
                case 19:
                    (a, b, c) = (1, 2, 0);
                    break;
                default:
                    (a, b, c) = (0, 1, 2);
                    break;
            }

            double sa = _pos[a], sb = _pos[b], ea = target[a], eb = target[b];
            double ca, cb;
            var radius = Axis(words, 'R');
            if (radius is { } r && r != 0)
            {
                var dx = ea - sa;
                var dy = eb - sb;
                var d = Math.Sqrt(dx * dx + dy * dy);
                if (d < 1e-12)
                {
                    Segment(sink, ToVector(_pos), ToVector(target));
                    return;
                }

                var h = Math.Sqrt(Math.Max(r * r - d * d / 4, 0));
                var sign = (counterClockwise ? 1 : -1) * (r > 0 ? 1 : -1);
                ca = (sa + ea) / 2 + sign * h * (-dy / d);
                cb = (sb + eb) / 2 + sign * h * (dx / d);
            }
            else
            {
                var offsetA = Axis(words, "IJK"[a]) ?? 0;
                var offsetB = Axis(words, "IJK"[b]) ?? 0;
                ca = sa + offsetA;
                cb = sb + offsetB;
            }

            var rs = Math.Sqrt((sa - ca) * (sa - ca) + (sb - cb) * (sb - cb));
            if (!(rs > 1e-12))
            {
                Segment(sink, ToVector(_pos), ToVector(target));
                return;
            }

            var a0 = Math.Atan2(sb - cb, sa - ca);
            var a1 = Math.Atan2(eb - cb, ea - ca);
            var sweep = a1 - a0;
            if (counterClockwise)
            {
                while (sweep <= 1e-9)
                {
                    sweep += 2 * Math.PI;
                }
            }
            else
            {
                while (sweep >= -1e-9)
                {
                    sweep -= 2 * Math.PI;
                }
            }

            var step = Math.Min(Math.Max(_options.AngularDeflection, 0.02), 0.1);
            var steps = (int)Math.Clamp(Math.Ceiling(Math.Abs(sweep) / step), 1, 720);
            var previous = ToVector(_pos);
            for (var i = 1; i <= steps; i++)
            {
                var p = new double[3];
                var angle = a0 + sweep * i / steps;
                // The end point is exact; radius error between start and end is spread over the arc.
                var rr = rs + (Math.Sqrt((ea - ca) * (ea - ca) + (eb - cb) * (eb - cb)) - rs) * i / steps;
                p[a] = ca + rr * Math.Cos(angle);
                p[b] = cb + rr * Math.Sin(angle);
                p[c] = _pos[c] + (target[c] - _pos[c]) * i / steps;
                if (i == steps)
                {
                    p[a] = ea;
                    p[b] = eb;
                }

                var next = ToVector(p);
                Segment(sink, previous, next);
                previous = next;
            }
        }

        private static Vector3 ToVector(double[] p) => new((float)p[0], (float)p[1], (float)p[2]);

        private void Comment(string text)
        {
            var t = text.Trim();
            if (t.Length == 0)
            {
                return;
            }

            if (t.StartsWith("LAYER_CHANGE", StringComparison.Ordinal))
            {
                _layerComments++;
            }
            else if (t.StartsWith("LAYER:", StringComparison.Ordinal) && int.TryParse(t.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out var layer))
            {
                _layerComments = Math.Max(_layerComments, layer + 1);
            }
            else if (t.StartsWith("LAYER_COUNT:", StringComparison.Ordinal) && int.TryParse(t.AsSpan(12), NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            {
                _declaredLayers = count;
            }
            else if (t.StartsWith("layer ", StringComparison.Ordinal) && t.Contains("Z =", StringComparison.Ordinal))
            {
                _layerComments++;
            }
            else if (t.StartsWith("HEIGHT:", StringComparison.Ordinal) && _layerHeightComment is null
                && double.TryParse(t.AsSpan(7), NumberStyles.Float, CultureInfo.InvariantCulture, out var height))
            {
                _layerHeightComment = height;
            }

            if (_application is null)
            {
                _application = Application(t);
            }

            var colon = t.IndexOf(':', StringComparison.Ordinal);
            var equals = t.IndexOf('=', StringComparison.Ordinal);
            var split = equals >= 0 && (colon < 0 || equals < colon) ? equals : colon;
            if (split > 0 && split < t.Length - 1 && _header.Count < 200)
            {
                var key = t[..split].Trim();
                var value = t[(split + 1)..].Trim();
                if (HeaderKeys.Contains(key) && value.Length > 0 && value.Length < 200 && !_header.ContainsKey(key))
                {
                    _header[key] = value;
                }
            }
        }

        private static string? Application(string comment)
        {
            var lower = comment.ToLowerInvariant();
            if (lower.StartsWith("generated by ", StringComparison.Ordinal) || lower.StartsWith("generated with ", StringComparison.Ordinal))
            {
                var text = comment[(lower.StartsWith("generated by ", StringComparison.Ordinal) ? 13 : 15)..];
                var on = text.IndexOf(" on ", StringComparison.Ordinal);
                return (on > 0 ? text[..on] : text).Trim();
            }

            if (lower.StartsWith("generated by", StringComparison.Ordinal) || lower.Contains("cura_steamengine", StringComparison.Ordinal))
            {
                return comment.Trim();
            }

            if (lower.StartsWith("simplify3d", StringComparison.Ordinal) || lower.StartsWith("kisslicer", StringComparison.Ordinal))
            {
                return comment.Trim();
            }

            return null;
        }

        private Model3D Finish()
        {
            var meta = _model.Metadata;
            meta.Format = ModelFormats.Gcode;
            meta.DocumentKind = "toolpath";
            meta.UpAxis = UpAxis.Z;
            meta.Units = _baseInch ? LengthUnit.Inch : LengthUnit.Millimeter;
            meta.Application = _application;
            var printer = _hasE;
            Merge(printer ? _move : _draw, _plain);
            var drawName = printer ? "extrusion" : "cut";
            var moveName = printer ? "travel" : "rapid";
            var root = new SceneNode("toolpath");
            AddSink(root, _draw, drawName, printer ? new Vector4(0.95f, 0.5f, 0.1f, 1) : new Vector4(0.1f, 0.4f, 0.8f, 1));
            AddSink(root, _move, moveName, new Vector4(0.6f, 0.6f, 0.6f, 1));
            if (root.Children.Count == 0)
            {
                throw new ModelFormatException("The G-code file holds no movement.");
            }

            _model.Roots.Add(root);
            if (_mixedUnits)
            {
                _model.Warnings.Add("The file switches between G20 and G21; coordinates were converted to the first unit.");
            }

            if (_expressions)
            {
                _model.Warnings.Add("Parameters and expressions in axis words are not evaluated; those axes were left unchanged.");
            }

            var layers = Math.Max(_declaredLayers, _layerComments);
            if (layers == 0 && printer)
            {
                layers = _zValues.Count;
            }

            if (layers > 0)
            {
                meta.Properties.Add(new ModelProperty("layers", layers.ToString(CultureInfo.InvariantCulture), "gcode"));
            }

            if (_layerHeightComment is { } lh)
            {
                meta.Properties.Add(new ModelProperty("first layer height", lh.ToString("R", CultureInfo.InvariantCulture), "gcode"));
            }
            else if (_zValues.Count > 1)
            {
                var values = _zValues.ToList();
                meta.Properties.Add(new ModelProperty("layer height", (values[1] - values[0]).ToString("0.####", CultureInfo.InvariantCulture), "gcode"));
            }

            meta.Properties.Add(new ModelProperty("moves", _moves.ToString(CultureInfo.InvariantCulture), "gcode"));
            if (_arcs > 0)
            {
                meta.Properties.Add(new ModelProperty("arcs", _arcs.ToString(CultureInfo.InvariantCulture), "gcode"));
            }

            meta.Properties.Add(new ModelProperty("positioning", _absolute ? "absolute" : "relative", "gcode"));
            foreach (var (key, value) in _header)
            {
                meta.Properties.Add(new ModelProperty(key, value, "header"));
            }

            meta.HasGeometry = true;
            meta.Statistics = _model.ComputeStatistics();
            meta.Bounds = _model.ComputeBounds();
            return _model;
        }

        private static void Merge(Sink into, Sink from)
        {
            if (from.Acc.Lines.Count == 0)
            {
                return;
            }

            var offset = (uint)into.Acc.VertexCount;
            into.Acc.Positions.AddRange(from.Acc.Positions);
            into.Acc.Lines.AddRange(from.Acc.Lines.Select(i => i + offset));
        }

        private void AddSink(SceneNode root, Sink sink, string name, Vector4 colour)
        {
            if (sink.Acc.Lines.Count == 0)
            {
                return;
            }

            var material = _model.Materials.Count;
            _model.Materials.Add(new ModelMaterial { Name = name, BaseColor = colour });
            var mesh = sink.Acc.ToMesh(name, material);
            if (mesh is null)
            {
                return;
            }

            _model.Meshes.Add(mesh);
            root.Children.Add(new SceneNode(name) { Mesh = mesh });
        }
    }
}
