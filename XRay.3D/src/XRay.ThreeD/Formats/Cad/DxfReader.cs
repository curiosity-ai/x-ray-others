using System.Globalization;
using System.Numerics;
using System.Text;

namespace XRay.ThreeD.Formats.Cad;

using XRay.ThreeD.Internal;

/// <summary>
/// ASCII DXF (R10 to 2018): layers with colours and visibility, header (version, units, dates), blocks and their inserts
/// (nested, with scale, rotation and arrays), and the geometry entities: lines, polylines (with bulges, 3D, polyface and polygon
/// meshes), circles, arcs, ellipses, splines, points, 3D faces, solids, MESH, hatch boundaries and dimensions (through their blocks).
/// Text is collected into the metadata. One node per layer; lines, filled faces and points of one colour are one mesh each.
/// ACIS solids (3DSOLID, BODY, REGION) are counted and skipped. Binary DXF is not supported.
/// </summary>
internal static class DxfReader
{
    private const int MaxInsertDepth = 16;
    private const double ArcStep = Math.PI / 36; // 5 degrees

    private sealed class Entity
    {
        public string Type = "";
        public readonly List<(int Code, string Value)> Groups = new();
        public readonly List<Entity> Children = new();

        public string? Get(int code)
        {
            foreach (var (c, v) in Groups)
            {
                if (c == code)
                {
                    return v;
                }
            }

            return null;
        }

        public double Num(int code, double fallback = 0) => Get(code) is { } v ? ParseDouble(v, fallback) : fallback;

        public int Int(int code, int fallback = 0) => Get(code) is { } v && int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback;

        public IEnumerable<string> All(int code) => Groups.Where(g => g.Code == code).Select(g => g.Value);

        public Vector3 Point(int xCode, double zFallback = 0) =>
            new((float)Num(xCode), (float)Num(xCode + 10), (float)Num(xCode + 20, zFallback));
    }

    private sealed class Block
    {
        public string Name = "";
        public Vector3 Base;
        public readonly List<Entity> Entities = new();
    }

    private sealed record LayerInfo(string Name, int Color, int TrueColor, bool Visible);

    private enum Kind
    {
        Lines,
        Triangles,
        Points,
    }

    private sealed class Sink
    {
        public readonly List<float> Positions = new();
        public readonly List<uint> Indices = new();
    }

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var span = data.Span;
        if (span.StartsWith("AutoCAD Binary DXF"u8))
        {
            throw new UnsupportedModelFormatException(ModelFormats.Dxf, "Binary DXF files are not supported yet; save the drawing as ASCII DXF.");
        }

        var text = DecodeText(span);
        var parser = new Parser(text, options);
        parser.Parse();
        return new Builder(parser, options).Build();
    }

    private static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
        {
            bytes = bytes[3..];
        }

        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    internal static double ParseDouble(string text, double fallback = 0) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : fallback;

    // ---- parsing -------------------------------------------------------------------------------------------------

    private sealed class Parser
    {
        private readonly string[] _lines;
        private readonly LoadOptions _options;
        private int _position;

        public readonly Dictionary<string, List<(int Code, string Value)>> Header = new(StringComparer.Ordinal);
        public readonly List<LayerInfo> Layers = new();
        public readonly Dictionary<string, Block> Blocks = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<Entity> Entities = new();

        public Parser(string text, LoadOptions options)
        {
            _lines = text.Split('\n');
            _options = options;
        }

        private bool TryRead(out int code, out string value)
        {
            code = 0;
            value = "";
            if (_position >= _lines.Length)
            {
                return false;
            }

            var codeLine = _lines[_position].Trim();
            if (!int.TryParse(codeLine, NumberStyles.Integer, CultureInfo.InvariantCulture, out code))
            {
                if (codeLine.Length == 0 && _position == _lines.Length - 1)
                {
                    return false;
                }

                throw new ModelFormatException($"DXF group code expected at line {_position + 1}.");
            }

            value = _position + 1 < _lines.Length ? _lines[_position + 1].TrimEnd('\r') : "";
            _position += 2;
            return true;
        }

        public void Parse()
        {
            var any = false;
            while (TryRead(out var code, out var value))
            {
                any = true;
                if (code == 0 && value == "SECTION")
                {
                    if (!TryRead(out var c2, out var name) || c2 != 2)
                    {
                        throw new ModelFormatException("A DXF SECTION has no name.");
                    }

                    switch (name.Trim())
                    {
                        case "HEADER":
                            ReadHeader();
                            break;
                        case "TABLES":
                            ReadTables();
                            break;
                        case "BLOCKS":
                            ReadBlocks();
                            break;
                        case "ENTITIES":
                            ReadEntities(Entities);
                            break;
                        default:
                            SkipSection();
                            break;
                    }
                }
                else if (code == 0 && value == "EOF")
                {
                    break;
                }
            }

            if (!any)
            {
                throw new ModelFormatException("The DXF file is empty.");
            }
        }

        private void SkipSection()
        {
            while (TryRead(out var code, out var value))
            {
                if (code == 0 && value == "ENDSEC")
                {
                    return;
                }
            }
        }

        private void ReadHeader()
        {
            List<(int, string)>? current = null;
            while (TryRead(out var code, out var value))
            {
                if (code == 0 && value == "ENDSEC")
                {
                    return;
                }

                if (code == 9)
                {
                    current = new List<(int, string)>();
                    Header[value.Trim()] = current;
                }
                else
                {
                    current?.Add((code, value));
                }
            }
        }

        private void ReadTables()
        {
            var table = "";
            Entity? entry = null;
            void Flush()
            {
                if (entry is not null && table == "LAYER")
                {
                    var name = entry.Get(2) ?? "";
                    var flags = entry.Int(70);
                    var color = entry.Int(62, 7);
                    Layers.Add(new LayerInfo(name, Math.Abs(color), entry.Int(420, -1), (flags & 1) == 0 && color >= 0));
                }

                entry = null;
            }

            while (TryRead(out var code, out var value))
            {
                if (code == 0)
                {
                    Flush();
                    if (value == "ENDSEC")
                    {
                        return;
                    }

                    if (value == "TABLE")
                    {
                        table = "";
                    }
                    else if (value == "ENDTAB")
                    {
                        table = "";
                    }
                    else
                    {
                        entry = new Entity { Type = value };
                    }
                }
                else if (code == 2 && table == "" && entry is null)
                {
                    table = value.Trim();
                }
                else
                {
                    entry?.Groups.Add((code, value));
                }
            }
        }

        private void ReadBlocks()
        {
            var flat = new List<Entity>();
            ReadEntities(flat);
            Block? block = null;
            foreach (var entity in flat)
            {
                if (entity.Type == "BLOCK")
                {
                    block = new Block { Name = entity.Get(2) ?? entity.Get(3) ?? "", Base = entity.Point(10) };
                    Blocks[block.Name] = block;
                }
                else if (entity.Type == "ENDBLK")
                {
                    block = null;
                }
                else
                {
                    block?.Entities.Add(entity);
                }
            }
        }

        /// <summary>Reads entities up to the end of the section. POLYLINE and INSERT own the VERTEX and ATTRIB records that follow them.</summary>
        private void ReadEntities(List<Entity> into)
        {
            Entity? current = null;
            Entity? owner = null;
            while (TryRead(out var code, out var value))
            {
                if (code == 0)
                {
                    if (current is not null)
                    {
                        Commit(current, into, ref owner);
                        current = null;
                    }

                    if (value == "ENDSEC")
                    {
                        return;
                    }

                    current = new Entity { Type = value };
                    _options.Cancellation.ThrowIfCancellationRequested();
                }
                else
                {
                    current?.Groups.Add((code, value));
                }
            }

            if (current is not null)
            {
                Commit(current, into, ref owner);
            }
        }

        private static void Commit(Entity entity, List<Entity> into, ref Entity? owner)
        {
            switch (entity.Type)
            {
                case "VERTEX" when owner is not null:
                case "ATTRIB" when owner is not null:
                    owner.Children.Add(entity);
                    return;
                case "SEQEND":
                    owner = null;
                    return;
                case "POLYLINE":
                    owner = entity;
                    into.Add(entity);
                    return;
                case "INSERT":
                    into.Add(entity);
                    owner = entity.Int(66) == 1 ? entity : null;
                    return;
                default:
                    into.Add(entity);
                    return;
            }
        }
    }

    // ---- geometry ------------------------------------------------------------------------------------------------

    private sealed class Builder
    {
        private readonly Parser _p;
        private readonly LoadOptions _options;
        private readonly Model3D _model = new();
        private readonly Dictionary<string, int> _layerIndex = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<(int Layer, int Rgb, Kind Kind), Sink> _sinks = new();
        private readonly Dictionary<string, int> _entityCounts = new(StringComparer.Ordinal);
        private readonly List<string> _texts = new();
        private readonly HashSet<string> _insertedBlocks = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _skipped = new(StringComparer.Ordinal);
        private long _vertexBudget;
        private bool _any3d;

        public Builder(Parser parser, LoadOptions options)
        {
            _p = parser;
            _options = options;
            _vertexBudget = Math.Min(options.MaxTriangles * 3, 200_000_000);
        }

        private readonly record struct Style(int Layer, int Rgb);

        public Model3D Build()
        {
            var meta = _model.Metadata;
            meta.Format = ModelFormats.Dxf;
            meta.UpAxis = UpAxis.Z;
            ReadHeader(meta);

            foreach (var layer in _p.Layers)
            {
                if (!_layerIndex.ContainsKey(layer.Name))
                {
                    _layerIndex[layer.Name] = _model.Layers.Count;
                    _model.Layers.Add(new ModelLayer(layer.Name) { Visible = layer.Visible });
                }
            }

            var paperSpace = 0;
            foreach (var entity in _p.Entities)
            {
                if (entity.Int(67) == 1)
                {
                    paperSpace++; // layout sheets (viewports, title blocks) are not the model
                    continue;
                }

                Emit(entity, Matrix4x4.Identity, null, 0, 0);
            }

            if (paperSpace > 0)
            {
                meta.Notes.Add($"{paperSpace} paper-space entities (layouts) were not drawn.");
            }

            BuildScene();
            meta.LayerNames.AddRange(_model.Layers.Select(l => l.Name));
            foreach (var (type, count) in _entityCounts.OrderByDescending(e => e.Value).ThenBy(e => e.Key, StringComparer.Ordinal))
            {
                meta.Properties.Add(new ModelProperty(type, count.ToString(CultureInfo.InvariantCulture), "entities"));
            }

            foreach (var text in _texts.Take(200))
            {
                meta.Properties.Add(new ModelProperty("text", text, "text"));
            }

            meta.PartNames.AddRange(_insertedBlocks.Where(b => !b.StartsWith('*')).OrderBy(b => b, StringComparer.Ordinal));
            foreach (var skipped in _skipped.OrderBy(s => s, StringComparer.Ordinal))
            {
                meta.Notes.Add($"{skipped} entities are not drawn.");
            }

            meta.DocumentKind = _any3d ? "model" : "drawing";
            meta.HasGeometry = _model.Meshes.Count > 0;
            meta.Statistics = _model.ComputeStatistics();
            meta.Bounds = _model.ComputeBounds();
            _model.Warnings.AddRange(meta.Notes);
            return _model;
        }

        private void ReadHeader(ModelMetadata meta)
        {
            string? Var(string name, int code) => _p.Header.TryGetValue(name, out var groups) ? groups.FirstOrDefault(g => g.Code == code).Value : null;
            if (Var("$ACADVER", 1)?.Trim() is { Length: > 0 } version)
            {
                meta.FormatVersion = version;
                meta.Application = AutoCadName(version);
            }

            if (Var("$INSUNITS", 70) is { } units && int.TryParse(units.Trim(), out var code))
            {
                meta.Units = UnitOf(code);
            }

            meta.Created = JulianDate(Var("$TDCREATE", 40));
            meta.Modified = JulianDate(Var("$TDUPDATE", 40));
            if (Var("$LASTSAVEDBY", 1) is { Length: > 0 } by)
            {
                meta.LastModifiedBy = by.Trim();
            }

            foreach (var (name, label) in new[] { ("$PROJECTNAME", "Project name"), ("$HYPERLINKBASE", "Hyperlink base"), ("$DWGCODEPAGE", "Code page") })
            {
                if (Var(name, 1) is { Length: > 0 } value)
                {
                    meta.Properties.Add(new ModelProperty(label, value.Trim(), "header"));
                }
            }
        }

        private static DateTimeOffset? JulianDate(string? text)
        {
            if (text is null || !double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var jd) || jd < 2_000_000 || jd > 3_000_000)
            {
                return null;
            }

            return DateTimeOffset.UnixEpoch.AddDays(jd - 2440587.5);
        }

        private static string AutoCadName(string version) => version switch
        {
            "AC1006" => "AutoCAD R10 (AC1006)",
            "AC1009" => "AutoCAD R11/R12 (AC1009)",
            "AC1012" => "AutoCAD R13 (AC1012)",
            "AC1014" => "AutoCAD R14 (AC1014)",
            "AC1015" => "AutoCAD 2000 (AC1015)",
            "AC1018" => "AutoCAD 2004 (AC1018)",
            "AC1021" => "AutoCAD 2007 (AC1021)",
            "AC1024" => "AutoCAD 2010 (AC1024)",
            "AC1027" => "AutoCAD 2013 (AC1027)",
            "AC1032" => "AutoCAD 2018 (AC1032)",
            _ => "AutoCAD (" + version + ")",
        };

        /// <summary>$INSUNITS: the drawing unit code. 0 (unitless) and units without a model equivalent give null.</summary>
        internal static LengthUnit? UnitOf(int code) => code switch
        {
            1 => LengthUnit.Inch,
            2 => LengthUnit.Foot,
            3 => LengthUnit.Mile,
            4 => LengthUnit.Millimeter,
            5 => LengthUnit.Centimeter,
            6 => LengthUnit.Meter,
            7 => LengthUnit.Kilometer,
            8 => LengthUnit.Microinch,
            9 => LengthUnit.Mil,
            10 => LengthUnit.Yard,
            12 => LengthUnit.Nanometer,
            13 => LengthUnit.Micrometer,
            14 => LengthUnit.Decimeter,
            _ => null,
        };

        private int LayerOf(string? name)
        {
            name ??= "0";
            if (!_layerIndex.TryGetValue(name, out var index))
            {
                index = _model.Layers.Count;
                _layerIndex[name] = index;
                _model.Layers.Add(new ModelLayer(name));
            }

            return index;
        }

        private int ColorOf(Entity e, string layerName, int blockRgb)
        {
            if (e.Get(420) is { } trueColor && int.TryParse(trueColor.Trim(), out var tc) && tc >= 0)
            {
                return tc & 0xFFFFFF;
            }

            var aci = e.Int(62, 256);
            if (aci == 0)
            {
                return blockRgb >= 0 ? blockRgb : AciToRgb(7);
            }

            if (aci == 256 || aci < 0)
            {
                var layer = _p.Layers.FirstOrDefault(l => string.Equals(l.Name, layerName, StringComparison.OrdinalIgnoreCase));
                if (layer is null)
                {
                    return AciToRgb(7);
                }

                return layer.TrueColor >= 0 ? layer.TrueColor & 0xFFFFFF : AciToRgb(layer.Color);
            }

            return AciToRgb(aci);
        }

        private Sink SinkFor(int layer, int rgb, Kind kind)
        {
            if (!_sinks.TryGetValue((layer, rgb, kind), out var sink))
            {
                _sinks[(layer, rgb, kind)] = sink = new Sink();
            }

            return sink;
        }

        private void Charge(int vertices)
        {
            _vertexBudget -= vertices;
            if (_vertexBudget < 0)
            {
                throw new ModelFormatException("The DXF produces more geometry than the configured limit.");
            }
        }

        private void AddLine(Style style, Vector3 a, Vector3 b, Matrix4x4 m)
        {
            AddPolyline(style, [a, b], false, m);
        }

        private void AddPolyline(Style style, IReadOnlyList<Vector3> points, bool closed, Matrix4x4 m)
        {
            if (points.Count < 2)
            {
                return;
            }

            Charge(points.Count);
            var sink = SinkFor(style.Layer, style.Rgb, Kind.Lines);
            var start = (uint)(sink.Positions.Count / 3);
            foreach (var point in points)
            {
                var p = Vector3.Transform(point, m);
                sink.Positions.Add(p.X);
                sink.Positions.Add(p.Y);
                sink.Positions.Add(p.Z);
                if (MathF.Abs(p.Z) > 1e-6f)
                {
                    _any3d = true;
                }
            }

            for (var i = 0; i + 1 < points.Count; i++)
            {
                sink.Indices.Add(start + (uint)i);
                sink.Indices.Add(start + (uint)i + 1);
            }

            if (closed && points.Count > 2)
            {
                sink.Indices.Add(start + (uint)points.Count - 1);
                sink.Indices.Add(start);
            }
        }

        private void AddTriangles(Style style, IReadOnlyList<Vector3> corners, IReadOnlyList<int> triangleIndices, Matrix4x4 m)
        {
            Charge(corners.Count);
            var sink = SinkFor(style.Layer, style.Rgb, Kind.Triangles);
            var start = (uint)(sink.Positions.Count / 3);
            foreach (var point in corners)
            {
                var p = Vector3.Transform(point, m);
                if (MathF.Abs(p.Z) > 1e-6f)
                {
                    _any3d = true;
                }

                sink.Positions.Add(p.X);
                sink.Positions.Add(p.Y);
                sink.Positions.Add(p.Z);
            }

            foreach (var i in triangleIndices)
            {
                sink.Indices.Add(start + (uint)i);
            }
        }

        private void AddPoint(Style style, Vector3 point, Matrix4x4 m)
        {
            Charge(1);
            var sink = SinkFor(style.Layer, style.Rgb, Kind.Points);
            var p = Vector3.Transform(point, m);
            sink.Positions.Add(p.X);
            sink.Positions.Add(p.Y);
            sink.Positions.Add(p.Z);
        }

        /// <summary>Arbitrary-axis OCS to WCS as a row-vector matrix.</summary>
        private static Matrix4x4 Ocs(Vector3 normal)
        {
            if (normal == Vector3.Zero || normal.LengthSquared() < 1e-12f)
            {
                return Matrix4x4.Identity;
            }

            var n = Vector3.Normalize(normal);
            if (Math.Abs(n.X - 0) < 1e-7f && Math.Abs(n.Y - 0) < 1e-7f && n.Z > 0)
            {
                return Matrix4x4.Identity;
            }

            var ax = MathF.Abs(n.X) < 1f / 64 && MathF.Abs(n.Y) < 1f / 64 ? Vector3.Cross(Vector3.UnitY, n) : Vector3.Cross(Vector3.UnitZ, n);
            ax = Vector3.Normalize(ax);
            var ay = Vector3.Normalize(Vector3.Cross(n, ax));
            return new Matrix4x4(ax.X, ax.Y, ax.Z, 0, ay.X, ay.Y, ay.Z, 0, n.X, n.Y, n.Z, 0, 0, 0, 0, 1);
        }

        private static Vector3 NormalOf(Entity e) => e.Get(210) is null && e.Get(220) is null && e.Get(230) is null
            ? Vector3.UnitZ
            : new Vector3((float)e.Num(210), (float)e.Num(220), (float)e.Num(230, 1));

        private void Emit(Entity e, Matrix4x4 m, string? insertLayer, int blockRgb, int depth)
        {
            if (depth == 0)
            {
                _entityCounts[e.Type] = _entityCounts.GetValueOrDefault(e.Type) + 1;
            }

            var layerName = e.Get(8) ?? "0";
            if (insertLayer is not null && layerName == "0")
            {
                layerName = insertLayer;
            }

            var style = new Style(LayerOf(layerName), ColorOf(e, layerName, blockRgb));
            switch (e.Type)
            {
                case "LINE":
                    AddLine(style, e.Point(10), e.Point(11), m);
                    break;
                case "POINT":
                    AddPoint(style, e.Point(10), m);
                    break;
                case "CIRCLE":
                    AddPolyline(style, Arc(e.Point(10), e.Num(40), 0, 2 * Math.PI, true), true, Ocs(NormalOf(e)) * m);
                    break;
                case "ARC":
                    {
                        var a0 = e.Num(50) * Math.PI / 180;
                        var a1 = e.Num(51, 360) * Math.PI / 180;
                        AddPolyline(style, Arc(e.Point(10), e.Num(40), a0, a1, false), false, Ocs(NormalOf(e)) * m);
                        break;
                    }

                case "ELLIPSE":
                    AddPolyline(style, Ellipse(e), Math.Abs(e.Num(42, 2 * Math.PI) - e.Num(41, 0) - 2 * Math.PI) < 1e-6, Ocs(NormalOf(e)) * m);
                    break;
                case "LWPOLYLINE":
                    EmitLwPolyline(e, style, m);
                    break;
                case "POLYLINE":
                    EmitPolyline(e, style, m);
                    break;
                case "SPLINE":
                    AddPolyline(style, Spline(e), (e.Int(70) & 1) != 0, m);
                    break;
                case "3DFACE":
                    EmitFace(e, style, m);
                    break;
                case "SOLID":
                case "TRACE":
                    {
                        var ocs = Ocs(NormalOf(e));
                        var c = new[] { e.Point(10), e.Point(11), e.Point(13, e.Point(10).Z), e.Point(12) };
                        var last = e.Get(13) is null ? 2 : 3;
                        AddTriangles(style, c, last == 3 ? [0, 1, 2, 0, 2, 3] : [0, 1, 2], ocs * m);
                        break;
                    }

                case "MESH":
                    EmitMesh(e, style, m);
                    break;
                case "HATCH":
                    EmitHatch(e, style, m);
                    break;
                case "INSERT":
                    EmitInsert(e, m, layerName, style.Rgb, depth);
                    break;
                case "DIMENSION":
                    if (e.Get(2) is { } dimensionBlock && _p.Blocks.TryGetValue(dimensionBlock, out var block))
                    {
                        EmitBlock(block, m, layerName, style.Rgb, depth + 1);
                    }

                    break;
                case "TEXT":
                case "MTEXT":
                case "ATTRIB":
                case "ATTDEF":
                    if (e.Get(1) is { Length: > 0 } content && _texts.Count < 5000)
                    {
                        _texts.Add(CleanText(content));
                    }

                    break;
                case "3DSOLID":
                case "BODY":
                case "REGION":
                case "SURFACE":
                    _skipped.Add(e.Type + " (ACIS)");
                    break;
                case "VIEWPORT":
                case "WIPEOUT":
                case "IMAGE":
                case "LEADER":
                case "MLINE":
                case "RAY":
                case "XLINE":
                case "OLE2FRAME":
                case "UNDERLAY":
                    break;
                default:
                    _skipped.Add(e.Type);
                    break;
            }
        }

        private static string CleanText(string text)
        {
            // MTEXT carries formatting codes: \P is a paragraph break, {\fArial|b0;text} a run, \U+XXXX a character.
            var sb = new StringBuilder();
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '\\' && i + 1 < text.Length)
                {
                    var n = text[i + 1];
                    if (n == 'P' || n == 'p')
                    {
                        sb.Append(' ');
                        i++;
                    }
                    else if (n == 'U' && i + 6 < text.Length && text[i + 2] == '+' && int.TryParse(text.AsSpan(i + 3, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp))
                    {
                        sb.Append((char)cp);
                        i += 6;
                    }
                    else if (n is 'f' or 'F' or 'H' or 'h' or 'C' or 'c' or 'W' or 'w' or 'Q' or 'q' or 'T' or 't' or 'A' or 'a')
                    {
                        var end = text.IndexOf(';', i);
                        i = end < 0 ? text.Length : end;
                    }
                    else
                    {
                        i++;
                        sb.Append(n);
                    }
                }
                else if (c is '{' or '}')
                {
                    continue;
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString().Trim();
        }

        private static List<Vector3> Arc(Vector3 center, double radius, double start, double end, bool full)
        {
            var points = new List<Vector3>();
            if (!(radius > 0) || !double.IsFinite(radius))
            {
                return points;
            }

            if (!full)
            {
                while (end <= start)
                {
                    end += 2 * Math.PI;
                }
            }
            else
            {
                end = start + 2 * Math.PI;
            }

            var segments = Math.Max(full ? 24 : 2, (int)Math.Ceiling((end - start) / ArcStep));
            segments = Math.Min(segments, 720);
            for (var i = 0; i <= segments; i++)
            {
                if (full && i == segments)
                {
                    break;
                }

                var a = start + (end - start) * i / segments;
                points.Add(new Vector3(center.X + (float)(radius * Math.Cos(a)), center.Y + (float)(radius * Math.Sin(a)), center.Z));
            }

            return points;
        }

        private static List<Vector3> Ellipse(Entity e)
        {
            var center = e.Point(10);
            var major = e.Point(11);
            var ratio = e.Num(40, 1);
            var normal = NormalOf(e);
            var minor = Vector3.Cross(normal, major) * (float)ratio;
            var t0 = e.Num(41, 0);
            var t1 = e.Num(42, 2 * Math.PI);
            while (t1 <= t0)
            {
                t1 += 2 * Math.PI;
            }

            var segments = Math.Clamp((int)Math.Ceiling((t1 - t0) / ArcStep), 8, 720);
            var points = new List<Vector3>();
            for (var i = 0; i <= segments; i++)
            {
                var t = t0 + (t1 - t0) * i / segments;
                points.Add(center + major * (float)Math.Cos(t) + minor * (float)Math.Sin(t));
            }

            return points;
        }

        private void EmitLwPolyline(Entity e, Style style, Matrix4x4 m)
        {
            var xs = e.All(10).Select(v => ParseDouble(v)).ToList();
            var ys = e.All(20).Select(v => ParseDouble(v)).ToList();
            var n = Math.Min(xs.Count, ys.Count);
            // Bulges are optional per vertex, so walk the groups in order.
            var bulges = new double[n];
            var vertex = -1;
            foreach (var (code, value) in e.Groups)
            {
                if (code == 10)
                {
                    vertex++;
                }
                else if (code == 42 && vertex >= 0 && vertex < n)
                {
                    bulges[vertex] = ParseDouble(value);
                }
            }

            var elevation = (float)e.Num(38);
            var closed = (e.Int(70) & 1) != 0;
            var points = new List<Vector3>();
            var tick909 = 0L;
            for (var i = 0; i < n; i++)
            {
                Cancel.Every(_options.Cancellation, tick909++);
                var a = new Vector3((float)xs[i], (float)ys[i], elevation);
                points.Add(a);
                var next = i + 1 < n ? i + 1 : (closed ? 0 : -1);
                if (next >= 0 && Math.Abs(bulges[i]) > 1e-9)
                {
                    var b = new Vector3((float)xs[next], (float)ys[next], elevation);
                    points.AddRange(BulgeArc(a, b, bulges[i]));
                }
            }

            AddPolyline(style, points, closed, Ocs(NormalOf(e)) * m);
        }

        /// <summary>Intermediate points of the arc a bulge describes between two polyline vertices.</summary>
        private static IEnumerable<Vector3> BulgeArc(Vector3 a, Vector3 b, double bulge)
        {
            var chord = new Vector2(b.X - a.X, b.Y - a.Y);
            var length = chord.Length();
            if (length < 1e-12)
            {
                yield break;
            }

            // theta is the signed included angle (positive: counter-clockwise). The centre is on the left of the chord a to b at
            // distance (L/2)/tan(theta/2) (negative values put it on the right, which covers clockwise arcs and arcs over 180 degrees).
            var theta = 4 * Math.Atan(bulge);
            var radius = Math.Abs(length / (2 * Math.Sin(theta / 2)));
            var mid = new Vector2((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            var leftNormal = new Vector2(-chord.Y, chord.X) / length;
            var center = mid + leftNormal * (float)((length / 2) / Math.Tan(theta / 2));
            var start = Math.Atan2(a.Y - center.Y, a.X - center.X);
            var segments = Math.Clamp((int)Math.Ceiling(Math.Abs(theta) / ArcStep), 2, 360);
            for (var i = 1; i < segments; i++)
            {
                var angle = start + theta * i / segments;
                yield return new Vector3(center.X + (float)(radius * Math.Cos(angle)), center.Y + (float)(radius * Math.Sin(angle)), a.Z);
            }
        }

        private void EmitPolyline(Entity e, Style style, Matrix4x4 m)
        {
            var flags = e.Int(70);
            var vertices = e.Children.Where(c => c.Type == "VERTEX").ToList();
            if ((flags & 64) != 0)
            {
                // Polyface mesh: vertices (flag 64) then face records (flag 128) with 1-based, signed indices.
                var corners = vertices.Where(v => (v.Int(70) & 64) != 0).Select(v => v.Point(10)).ToList();
                var indices = new List<int>();
                foreach (var face in vertices.Where(v => (v.Int(70) & 64) == 0 && (v.Int(70) & 128) != 0))
                {
                    var ids = new[] { face.Int(71), face.Int(72), face.Int(73), face.Int(74) }.Where(i => i != 0).Select(i => Math.Abs(i) - 1).ToList();
                    if (ids.Any(i => i < 0 || i >= corners.Count))
                    {
                        continue;
                    }

                    for (var k = 1; k + 1 < ids.Count; k++)
                    {
                        indices.AddRange([ids[0], ids[k], ids[k + 1]]);
                    }
                }

                if (indices.Count > 0)
                {
                    AddTriangles(style, corners, indices, m);
                }

                return;
            }

            if ((flags & 16) != 0)
            {
                // Polygon mesh M x N.
                var mCount = e.Int(71);
                var nCount = e.Int(72);
                var grid = vertices.Select(v => v.Point(10)).ToList();
                if (mCount < 2 || nCount < 2 || grid.Count < mCount * nCount)
                {
                    return;
                }

                var closedM = (flags & 1) != 0;
                var closedN = (flags & 32) != 0;
                var indices = new List<int>();
                for (var i = 0; i < (closedM ? mCount : mCount - 1); i++)
                {
                    for (var j = 0; j < (closedN ? nCount : nCount - 1); j++)
                    {
                        var i1 = (i + 1) % mCount;
                        var j1 = (j + 1) % nCount;
                        int a = i * nCount + j, b = i1 * nCount + j, c = i1 * nCount + j1, d = i * nCount + j1;
                        indices.AddRange([a, b, c, a, c, d]);
                    }
                }

                AddTriangles(style, grid.Take(mCount * nCount).ToList(), indices, m);
                return;
            }

            var points = new List<Vector3>();
            var elevation = (float)e.Point(10).Z;
            var is3d = (flags & 8) != 0;
            var tick1013 = 0L;
            for (var i = 0; i < vertices.Count; i++)
            {
                Cancel.Every(_options.Cancellation, tick1013++);
                var v = vertices[i];
                if ((v.Int(70) & 16) != 0 && !is3d)
                {
                    continue; // spline frame control point
                }

                var p = v.Point(10, elevation);
                points.Add(p);
                var bulge = v.Num(42);
                var closed = (flags & 1) != 0;
                var next = i + 1 < vertices.Count ? i + 1 : (closed ? 0 : -1);
                if (!is3d && next >= 0 && Math.Abs(bulge) > 1e-9)
                {
                    points.AddRange(BulgeArc(p, vertices[next].Point(10, elevation), bulge));
                }
            }

            AddPolyline(style, points, (flags & 1) != 0, (is3d ? Matrix4x4.Identity : Ocs(NormalOf(e))) * m);
        }

        private void EmitFace(Entity e, Style style, Matrix4x4 m)
        {
            var corners = new List<Vector3> { e.Point(10), e.Point(11), e.Point(12), e.Get(13) is null ? e.Point(12) : e.Point(13) };
            var quad = e.Get(13) is not null && corners[3] != corners[2];
            AddTriangles(style, corners, quad ? [0, 1, 2, 0, 2, 3] : [0, 1, 2], m);
        }

        private void EmitMesh(Entity e, Style style, Matrix4x4 m)
        {
            // Vertices: 92 gives the count and the 10/20/30 groups follow in order; faces: 93 gives the number of integers in
            // the face list, each face being a count and that many vertex indices (group 90).
            var vertexCount = e.Int(92);
            var points = new List<Vector3>();
            var x = 0.0;
            var y = 0.0;
            var phase = 0;
            var faceInts = new List<int>();
            var inFaces = false;
            var inVertices = false;
            foreach (var (code, value) in e.Groups)
            {
                if (code == 92)
                {
                    inVertices = true;
                    continue;
                }

                if (code == 93)
                {
                    inVertices = false;
                    inFaces = true;
                    continue;
                }

                if (code is 94 or 95 or 140)
                {
                    inFaces = false;
                    continue;
                }

                if (inVertices && code == 10)
                {
                    x = ParseDouble(value);
                    phase = 1;
                }
                else if (inVertices && code == 20 && phase == 1)
                {
                    y = ParseDouble(value);
                    phase = 2;
                }
                else if (inVertices && code == 30 && phase == 2)
                {
                    points.Add(new Vector3((float)x, (float)y, (float)ParseDouble(value)));
                    phase = 0;
                }
                else if (inFaces && code == 90 && int.TryParse(value.Trim(), out var i))
                {
                    faceInts.Add(i);
                }
            }

            if (points.Count != vertexCount || points.Count == 0)
            {
                return;
            }

            var indices = new List<int>();
            for (var i = 0; i < faceInts.Count;)
            {
                var n = faceInts[i++];
                if (n < 3 || i + n > faceInts.Count)
                {
                    break;
                }

                var ids = faceInts.GetRange(i, n);
                i += n;
                if (ids.Any(id => id < 0 || id >= points.Count))
                {
                    continue;
                }

                for (var k = 1; k + 1 < n; k++)
                {
                    indices.AddRange([ids[0], ids[k], ids[k + 1]]);
                }
            }

            if (indices.Count > 0)
            {
                AddTriangles(style, points, indices, m);
            }
        }

        private void EmitHatch(Entity e, Style style, Matrix4x4 m)
        {
            // Boundary paths are drawn as outlines (the fill is not tessellated). Paths are either polylines (flag bit 2) or edge lists.
            var elevation = (float)e.Num(30);
            var matrix = Ocs(NormalOf(e)) * m;
            var groups = e.Groups;
            var i = 0;
            var pathCount = 0;
            while (i < groups.Count && groups[i].Code != 91)
            {
                i++;
            }

            if (i < groups.Count)
            {
                pathCount = int.Parse(groups[i].Value.Trim(), CultureInfo.InvariantCulture);
                i++;
            }

            for (var path = 0; path < pathCount && i < groups.Count; path++)
            {
                while (i < groups.Count && groups[i].Code != 92)
                {
                    i++;
                }

                if (i >= groups.Count)
                {
                    break;
                }

                var type = int.Parse(groups[i].Value.Trim(), CultureInfo.InvariantCulture);
                i++;
                if ((type & 2) != 0)
                {
                    var hasBulge = false;
                    var closed = false;
                    var count = 0;
                    while (i < groups.Count && groups[i].Code != 93)
                    {
                        if (groups[i].Code == 72)
                        {
                            hasBulge = groups[i].Value.Trim() != "0";
                        }
                        else if (groups[i].Code == 73)
                        {
                            closed = groups[i].Value.Trim() != "0";
                        }

                        i++;
                    }

                    if (i < groups.Count)
                    {
                        count = int.Parse(groups[i].Value.Trim(), CultureInfo.InvariantCulture);
                        i++;
                    }

                    var vertices = new List<(Vector3 P, double Bulge)>();
                    for (var v = 0; v < count && i < groups.Count; v++)
                    {
                        if (groups[i].Code != 10)
                        {
                            break;
                        }

                        var x = ParseDouble(groups[i].Value);
                        i++;
                        var y = i < groups.Count && groups[i].Code == 20 ? ParseDouble(groups[i++].Value) : 0;
                        var bulge = hasBulge && i < groups.Count && groups[i].Code == 42 ? ParseDouble(groups[i++].Value) : 0;
                        vertices.Add((new Vector3((float)x, (float)y, elevation), bulge));
                    }

                    var points = new List<Vector3>();
                    for (var v = 0; v < vertices.Count; v++)
                    {
                        points.Add(vertices[v].P);
                        var next = v + 1 < vertices.Count ? v + 1 : (closed ? 0 : -1);
                        if (next >= 0 && Math.Abs(vertices[v].Bulge) > 1e-9)
                        {
                            points.AddRange(BulgeArc(vertices[v].P, vertices[next].P, vertices[v].Bulge));
                        }
                    }

                    AddPolyline(style, points, closed, matrix);
                }
                else
                {
                    var edgeCount = 0;
                    while (i < groups.Count && groups[i].Code != 93)
                    {
                        i++;
                    }

                    if (i < groups.Count)
                    {
                        edgeCount = int.Parse(groups[i].Value.Trim(), CultureInfo.InvariantCulture);
                        i++;
                    }

                    for (var edge = 0; edge < edgeCount && i < groups.Count; edge++)
                    {
                        if (groups[i].Code != 72)
                        {
                            break;
                        }

                        var edgeType = int.Parse(groups[i].Value.Trim(), CultureInfo.InvariantCulture);
                        i++;
                        var values = new Dictionary<int, double>();
                        while (i < groups.Count && groups[i].Code is not (72 or 92 or 97 or 75 or 98))
                        {
                            values[groups[i].Code] = ParseDouble(groups[i].Value);
                            i++;
                        }

                        switch (edgeType)
                        {
                            case 1:
                                AddLine(style, new Vector3((float)values.GetValueOrDefault(10), (float)values.GetValueOrDefault(20), elevation), new Vector3((float)values.GetValueOrDefault(11), (float)values.GetValueOrDefault(21), elevation), matrix);
                                break;
                            case 2:
                                {
                                    var ccw = values.GetValueOrDefault(73, 1) != 0;
                                    var a0 = values.GetValueOrDefault(50) * Math.PI / 180;
                                    var a1 = values.GetValueOrDefault(51, 360) * Math.PI / 180;
                                    if (!ccw)
                                    {
                                        (a0, a1) = (2 * Math.PI - a1, 2 * Math.PI - a0);
                                    }

                                    var pts = Arc(new Vector3((float)values.GetValueOrDefault(10), (float)values.GetValueOrDefault(20), elevation), values.GetValueOrDefault(40), a0, a1, false);
                                    AddPolyline(style, pts, false, matrix);
                                    break;
                                }
                        }
                    }
                }
            }
        }

        private void EmitInsert(Entity e, Matrix4x4 m, string layerName, int rgb, int depth)
        {
            var name = e.Get(2) ?? "";
            if (!_p.Blocks.TryGetValue(name, out var block))
            {
                return;
            }

            _insertedBlocks.Add(name);
            var scale = new Vector3((float)e.Num(41, 1), (float)e.Num(42, 1), (float)e.Num(43, 1));
            var rotation = (float)(e.Num(50) * Math.PI / 180);
            var insertion = e.Point(10);
            var columns = Math.Clamp(e.Int(70, 1), 1, 1000);
            var rows = Math.Clamp(e.Int(71, 1), 1, 1000);
            var columnSpacing = (float)e.Num(44);
            var rowSpacing = (float)e.Num(45);
            var ocs = Ocs(NormalOf(e));
            for (var r = 0; r < rows; r++)
            {
                for (var c = 0; c < columns; c++)
                {
                    var local = Matrix4x4.CreateTranslation(-block.Base)
                        * Matrix4x4.CreateScale(scale)
                        * Matrix4x4.CreateTranslation(c * columnSpacing, r * rowSpacing, 0)
                        * Matrix4x4.CreateRotationZ(rotation)
                        * ocs
                        * Matrix4x4.CreateTranslation(insertion);
                    EmitBlock(block, local * m, layerName, rgb, depth + 1);
                }
            }
        }

        private void EmitBlock(Block block, Matrix4x4 m, string layerName, int rgb, int depth)
        {
            if (depth > MaxInsertDepth)
            {
                _model.Warnings.Add("Block inserts are nested too deeply; the rest was skipped.");
                return;
            }

            foreach (var entity in block.Entities)
            {
                _options.Cancellation.ThrowIfCancellationRequested();
                Emit(entity, m, layerName, rgb, depth);
            }
        }

        private List<Vector3> Spline(Entity e)
        {
            var degree = Math.Max(1, e.Int(71, 3));
            var knots = e.All(40).Select(v => ParseDouble(v)).ToList();
            var weights = e.All(41).Select(v => ParseDouble(v, 1)).ToList();
            var control = new List<Vector3>();
            var fit = new List<Vector3>();
            double x = 0, y = 0;
            var phase = 0;
            foreach (var (code, value) in e.Groups)
            {
                if (code is 10 or 11)
                {
                    x = ParseDouble(value);
                    phase = code;
                }
                else if (code is 20 or 21)
                {
                    y = ParseDouble(value);
                }
                else if (code is 30 or 31)
                {
                    (phase == 10 ? control : fit).Add(new Vector3((float)x, (float)y, (float)ParseDouble(value)));
                }
            }

            if (control.Count < degree + 1 || knots.Count != control.Count + degree + 1)
            {
                return fit.Count > 1 ? fit : control;
            }

            var samples = Math.Clamp(control.Count * 8, 16, 4000);
            var start = knots[degree];
            var end = knots[control.Count];
            var result = new List<Vector3>();
            for (var s = 0; s <= samples; s++)
            {
                var t = start + (end - start) * s / samples;
                if (s == samples)
                {
                    t = end - 1e-9 * Math.Max(1, Math.Abs(end));
                }

                // Basis functions by the Cox-de Boor recursion, evaluated at t.
                var span = degree;
                while (span < control.Count - 1 && t >= knots[span + 1])
                {
                    span++;
                }

                var n = new double[degree + 1];
                var left = new double[degree + 1];
                var right = new double[degree + 1];
                n[0] = 1;
                for (var j = 1; j <= degree; j++)
                {
                    left[j] = t - knots[span + 1 - j];
                    right[j] = knots[span + j] - t;
                    var saved = 0.0;
                    for (var r = 0; r < j; r++)
                    {
                        var denominator = right[r + 1] + left[j - r];
                        var temp = Math.Abs(denominator) < 1e-300 ? 0 : n[r] / denominator;
                        n[r] = saved + right[r + 1] * temp;
                        saved = left[j - r] * temp;
                    }

                    n[j] = saved;
                }

                double px = 0, py = 0, pz = 0, pw = 0;
                for (var j = 0; j <= degree; j++)
                {
                    var index = span - degree + j;
                    var w = n[j] * (index < weights.Count ? weights[index] : 1);
                    px += w * control[index].X;
                    py += w * control[index].Y;
                    pz += w * control[index].Z;
                    pw += w;
                }

                if (Math.Abs(pw) > 1e-300)
                {
                    result.Add(new Vector3((float)(px / pw), (float)(py / pw), (float)(pz / pw)));
                }
            }

            return result;
        }

        private void BuildScene()
        {
            var materials = new Dictionary<int, int>();
            int Material(int rgb)
            {
                if (!materials.TryGetValue(rgb, out var index))
                {
                    index = _model.Materials.Count;
                    materials[rgb] = index;
                    _model.Materials.Add(new ModelMaterial
                    {
                        Name = "#" + rgb.ToString("X6", CultureInfo.InvariantCulture),
                        BaseColor = new Vector4(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f),
                    });
                }

                return index;
            }

            var root = new SceneNode(null);
            var layerNodes = new Dictionary<int, SceneNode>();
            foreach (var ((layer, rgb, kind), sink) in _sinks.OrderBy(k => k.Key.Layer).ThenBy(k => k.Key.Kind).ThenBy(k => k.Key.Rgb))
            {
                if (sink.Positions.Count == 0)
                {
                    continue;
                }

                var layerName = _model.Layers[layer].Name;
                var mesh = new MeshData { Name = layerName, Positions = sink.Positions.ToArray() };
                switch (kind)
                {
                    case Kind.Lines:
                        mesh.EdgeIndices = sink.Indices.ToArray();
                        mesh.EdgeMaterial = Material(rgb);
                        break;
                    case Kind.Triangles:
                        mesh.Indices = sink.Indices.ToArray();
                        mesh.Groups.Add(new MeshGroup(0, mesh.Indices.Length, Material(rgb)));
                        break;
                    case Kind.Points:
                        mesh.Colors = Enumerable.Range(0, sink.Positions.Count / 3)
                            .SelectMany(_ => new[] { ((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f }).ToArray();
                        break;
                }

                _model.Meshes.Add(mesh);
                if (!layerNodes.TryGetValue(layer, out var node))
                {
                    node = new SceneNode(layerName) { Layer = layer };
                    layerNodes[layer] = node;
                    root.Children.Add(node);
                }

                node.Children.Add(new SceneNode(null) { Mesh = mesh, Layer = layer });
            }

            if (root.Children.Count > 0)
            {
                root.Name = "drawing";
                _model.Roots.Add(root);
            }
        }
    }

    /// <summary>AutoCAD Color Index to RGB. Index 7 (white on a dark background, black on a light one) is drawn black.</summary>
    internal static int AciToRgb(int index)
    {
        switch (index)
        {
            case 1: return 0xFF0000;
            case 2: return 0xFFFF00;
            case 3: return 0x00FF00;
            case 4: return 0x00FFFF;
            case 5: return 0x0000FF;
            case 6: return 0xFF00FF;
            case 7: return 0x000000;
            case 8: return 0x808080;
            case 9: return 0xC0C0C0;
            case 250: return 0x333333;
            case 251: return 0x505050;
            case 252: return 0x696969;
            case 253: return 0x828282;
            case 254: return 0xBEBEBE;
            case 255: return 0xFFFFFF;
        }

        if (index < 10 || index > 249)
        {
            return 0x000000;
        }

        // 10..249: 24 hues in steps of 15 degrees, ten shades each: full, 50% tint, then value 80%, 60%, 50%, 30% each with a tint.
        var hue = (index - 10) / 10 * 15.0;
        var shade = (index - 10) % 10;
        double saturation = shade % 2 == 0 ? 1.0 : 0.5;
        double value = (shade / 2) switch { 0 => 1.0, 1 => 0.8, 2 => 0.6, 3 => 0.5, _ => 0.3 };
        return HsvToRgb(hue, saturation, value);
    }

    private static int HsvToRgb(double h, double s, double v)
    {
        var c = v * s;
        var hp = h / 60;
        var x = c * (1 - Math.Abs(hp % 2 - 1));
        double r = 0, g = 0, b = 0;
        if (hp < 1) { r = c; g = x; }
        else if (hp < 2) { r = x; g = c; }
        else if (hp < 3) { g = c; b = x; }
        else if (hp < 4) { g = x; b = c; }
        else if (hp < 5) { r = x; b = c; }
        else { r = c; b = x; }

        var m = v - c;
        // AutoCAD's table truncates (FF7F7F, not FF8080).
        static int Channel(double v) => (int)Math.Floor(v * 255 + 1e-6);
        return (Channel(r + m) << 16) | (Channel(g + m) << 8) | Channel(b + m);
    }
}

/// <summary>Exposes internals to the tests without widening the public surface.</summary>
public static class DxfReaderTestAccess
{
    public static int Aci(int index) => DxfReader.AciToRgb(index);
}
