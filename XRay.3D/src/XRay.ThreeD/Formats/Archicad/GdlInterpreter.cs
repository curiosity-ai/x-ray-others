using System.Globalization;
using System.Numerics;
using System.Text;

namespace XRay.ThreeD.Formats.Archicad;

using XRay.ThreeD.Internal;

/// <summary>
/// An interpreter for the 3D part of GDL, the scripting language Archicad library objects are written in: variables and expressions, IF, FOR, WHILE,
/// REPEAT, GOSUB, the transformation stack (ADD, MUL, ROT and their axis forms, DEL), the solids BLOCK, CYLIND, SPHERE, CONE, PRISM, PRISM_ (with arc
/// vertices), the BASE / VERT / EDGE / PGON / BODY polyhedron, RESOL, MATERIAL and DEFINE MATERIAL. Statements it does not know (2D drawing, revolved
/// and swept solids, text, request and global values, arrays) are skipped and listed, so what it draws is what it understood. Written from the language's
/// public reference; lengths are metres. The run is bounded by a statement budget and the cancellation token.
/// </summary>
internal sealed class GdlInterpreter
{
    private const int MaxStatements = 5_000_000;
    private const int MaxGeometry = 20_000_000;

    private readonly CancellationToken _token;
    private readonly long _maxTriangles;
    private readonly Dictionary<string, double> _vars = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _strings = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Matrix4x4> _stack = new();
    private Matrix4x4 _current = Matrix4x4.Identity;
    private int _resol = 36;
    private int _material = -1;
    private long _statements;
    private long _triangles;

    // BASE ... BODY state.
    private readonly List<Vector3> _bodyVertices = new();
    private readonly List<(int A, int B)> _bodyEdges = new();

    public GdlInterpreter(CancellationToken token, long maxTriangles)
    {
        _token = token;
        _maxTriangles = maxTriangles;
    }

    public sealed class MaterialDef
    {
        public string Name = "";
        public Vector3 Rgb = new(0.8f, 0.8f, 0.8f);
        public float Transparency;
    }

    public List<MaterialDef> Materials { get; } = new();

    public SortedDictionary<int, (List<float> Positions, List<float> Normals, List<uint> Indices)> Geometry { get; } = new();

    public HashSet<string> Skipped { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void SetNumber(string name, double value) => _vars[name] = value;

    public void SetString(string name, string value) => _strings[name] = value;

    public long Triangles => _triangles;

    public void Run(string script)
    {
        var lines = Preprocess(script);
        var labels = new Dictionary<int, int>();
        for (var i = 0; i < lines.Count; i++)
        {
            var t = lines[i];
            var colon = t.IndexOf(':');
            if (colon > 0 && int.TryParse(t.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out var label) && t[(colon + 1)..].Trim().Length == 0)
            {
                labels[label] = i;
            }
        }

        var forStack = new List<(int Line, string Var, double End, double Step)>();
        var gosub = new Stack<int>();
        var whileStack = new Stack<int>();
        var pc = 0;
        while (pc < lines.Count)
        {
            Cancel.Every(_token, _statements++, 4095);
            if (_statements > MaxStatements)
            {
                Skipped.Add("(statement budget exhausted)");
                return;
            }

            var line = lines[pc];
            var (word, rest) = SplitWord(line);
            var upper = word.ToUpperInvariant();
            switch (upper)
            {
                case "IF":
                    pc = ExecuteIf(lines, pc, rest);
                    continue;
                case "ELSE":
                    pc = FindMatching(lines, pc, "IF", "ENDIF", stopAtElse: false);
                    break;
                case "ENDIF":
                    break;
                case "FOR":
                {
                    var eq = rest.IndexOf('=');
                    var name = rest[..eq].Trim();
                    var to = IndexOfKeyword(rest, "TO");
                    var step = IndexOfKeyword(rest, "STEP");
                    var start = Eval(rest[(eq + 1)..to]);
                    var end = Eval(rest[(to + 2)..(step < 0 ? rest.Length : step)]);
                    var inc = step < 0 ? 1 : Eval(rest[(step + 4)..]);
                    _vars[name] = start;
                    if (inc == 0 || (inc > 0 ? start > end : start < end))
                    {
                        pc = FindMatching(lines, pc, "FOR", "NEXT", stopAtElse: false);
                        break;
                    }

                    forStack.Add((pc, name, end, inc));
                    break;
                }

                case "NEXT":
                {
                    if (forStack.Count == 0)
                    {
                        break;
                    }

                    var (startLine, name, end, inc) = forStack[^1];
                    var v = _vars.GetValueOrDefault(name) + inc;
                    _vars[name] = v;
                    if (inc > 0 ? v <= end + 1e-12 : v >= end - 1e-12)
                    {
                        pc = startLine;
                    }
                    else
                    {
                        forStack.RemoveAt(forStack.Count - 1);
                    }

                    break;
                }

                case "WHILE":
                    if (Eval(rest.EndsWith("DO", StringComparison.OrdinalIgnoreCase) ? rest[..^2] : rest) == 0)
                    {
                        pc = FindMatching(lines, pc, "WHILE", "ENDWHILE", stopAtElse: false);
                    }
                    else
                    {
                        whileStack.Push(pc);
                    }

                    break;
                case "ENDWHILE":
                    if (whileStack.Count > 0)
                    {
                        pc = whileStack.Pop();
                        continue;
                    }

                    break;
                case "REPEAT":
                    whileStack.Push(pc);
                    break;
                case "UNTIL":
                    if (whileStack.Count > 0)
                    {
                        var top = whileStack.Pop();
                        if (Eval(rest) == 0)
                        {
                            pc = top;
                            whileStack.Push(top);
                        }
                    }

                    break;
                case "GOSUB":
                    if (gosub.Count < 64 && labels.TryGetValue((int)Eval(rest), out var target))
                    {
                        gosub.Push(pc);
                        pc = target;
                    }

                    break;
                case "GOTO":
                    if (labels.TryGetValue((int)Eval(rest), out var jump))
                    {
                        pc = jump;
                    }

                    break;
                case "RETURN":
                    if (gosub.Count > 0)
                    {
                        pc = gosub.Pop();
                    }
                    else
                    {
                        return;
                    }

                    break;
                case "END":
                case "EXIT":
                    return;
                default:
                    Statement(upper, rest, line);
                    break;
            }

            pc++;
        }
    }

    private int ExecuteIf(List<string> lines, int pc, string rest)
    {
        var then = IndexOfKeyword(rest, "THEN");
        if (then < 0)
        {
            return pc + 1;
        }

        var condition = Eval(rest[..then]) != 0;
        var tail = rest[(then + 4)..].Trim();
        if (tail.Length > 0)
        {
            if (condition)
            {
                var (w, r) = SplitWord(tail);
                Statement(w.ToUpperInvariant(), r, tail);
            }

            return pc + 1;
        }

        if (condition)
        {
            return pc + 1;
        }

        // Skip to the matching ELSE (run what follows) or ENDIF.
        var depth = 0;
        for (var i = pc + 1; i < lines.Count; i++)
        {
            var (w, r) = SplitWord(lines[i]);
            var u = w.ToUpperInvariant();
            if (u == "IF" && IndexOfKeyword(r, "THEN") is var t && t >= 0 && r[(t + 4)..].Trim().Length == 0)
            {
                depth++;
            }
            else if (u == "ENDIF")
            {
                if (depth == 0)
                {
                    return i + 1;
                }

                depth--;
            }
            else if (u == "ELSE" && depth == 0)
            {
                return i + 1;
            }
        }

        return lines.Count;
    }

    /// <summary>The index of the line that closes the block opened at <paramref name="open"/>, so that the caller's increment moves past it.</summary>
    private static int FindMatching(List<string> lines, int open, string opener, string closer, bool stopAtElse)
    {
        var depth = 0;
        for (var i = open + 1; i < lines.Count; i++)
        {
            var w = SplitWord(lines[i]).Word.ToUpperInvariant();
            if (w == opener && (opener != "IF" || IndexOfKeyword(SplitWord(lines[i]).Tail, "THEN") is var t && t >= 0 && SplitWord(lines[i]).Tail[(t + 4)..].Trim().Length == 0))
            {
                depth++;
            }
            else if (w == closer)
            {
                if (depth == 0)
                {
                    return i;
                }

                depth--;
            }
        }

        return lines.Count;
    }

    private static List<string> Preprocess(string script)
    {
        var lines = new List<string>();
        var builder = new StringBuilder();
        foreach (var raw in script.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0 && builder.Length == 0)
            {
                continue;
            }

            if (line.EndsWith(',') || line.EndsWith('\\'))
            {
                builder.Append(line.TrimEnd('\\')).Append(' ');
                continue;
            }

            builder.Append(line);
            if (builder.Length > 0)
            {
                lines.Add(builder.ToString().Trim());
            }

            builder.Clear();
        }

        if (builder.Length > 0)
        {
            lines.Add(builder.ToString().Trim());
        }

        return lines;
    }

    private static string StripComment(string line)
    {
        var quote = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"' || line[i] == '\'')
            {
                quote = !quote;
            }
            else if (line[i] == '!' && !quote)
            {
                return line[..i];
            }
        }

        return line;
    }

    private static (string Word, string Tail) SplitWord(string line)
    {
        var i = 0;
        while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_'))
        {
            i++;
        }

        return (line[..i], line[i..].TrimStart());
    }

    private static int IndexOfKeyword(string text, string keyword)
    {
        var quote = false;
        for (var i = 0; i + keyword.Length <= text.Length; i++)
        {
            if (text[i] == '"')
            {
                quote = !quote;
            }

            if (!quote && string.Compare(text, i, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) == 0
                && (i == 0 || !char.IsLetterOrDigit(text[i - 1]) && text[i - 1] != '_')
                && (i + keyword.Length == text.Length || !char.IsLetterOrDigit(text[i + keyword.Length]) && text[i + keyword.Length] != '_'))
            {
                return i;
            }
        }

        return -1;
    }

    // ---- statements ----

    private void Statement(string word, string rest, string line)
    {
        // Assignment: name = expression (names may be followed by nothing else).
        var eq = line.IndexOf('=');
        if (eq > 0 && word.Length > 0 && line[..eq].Trim() is var lhs && lhs.Length > 0 && lhs.All(c => char.IsLetterOrDigit(c) || c == '_') && !IsKeyword(word))
        {
            if (rest.TrimStart().StartsWith('=') && rest.TrimStart()[1..].Trim().StartsWith('"'))
            {
                _strings[lhs] = rest.TrimStart()[1..].Trim().Trim('"');
            }
            else
            {
                _vars[lhs] = Eval(line[(eq + 1)..]);
            }

            return;
        }

        switch (word)
        {
            case "ADD":
            {
                var a = Args(rest);
                Push(Matrix4x4.CreateTranslation(F(a, 0), F(a, 1), F(a, 2)));
                break;
            }

            case "ADDX":
                Push(Matrix4x4.CreateTranslation((float)Eval(rest), 0, 0));
                break;
            case "ADDY":
                Push(Matrix4x4.CreateTranslation(0, (float)Eval(rest), 0));
                break;
            case "ADDZ":
                Push(Matrix4x4.CreateTranslation(0, 0, (float)Eval(rest)));
                break;
            case "MUL":
            {
                var a = Args(rest);
                Push(Matrix4x4.CreateScale(F(a, 0), F(a, 1), F(a, 2)));
                break;
            }

            case "MULX":
                Push(Matrix4x4.CreateScale((float)Eval(rest), 1, 1));
                break;
            case "MULY":
                Push(Matrix4x4.CreateScale(1, (float)Eval(rest), 1));
                break;
            case "MULZ":
                Push(Matrix4x4.CreateScale(1, 1, (float)Eval(rest)));
                break;
            case "ROTX":
                Push(Matrix4x4.CreateRotationX(Rad(Eval(rest))));
                break;
            case "ROTY":
                Push(Matrix4x4.CreateRotationY(Rad(Eval(rest))));
                break;
            case "ROTZ":
            case "ROT":
                Push(Matrix4x4.CreateRotationZ(Rad(Eval(rest))));
                break;
            case "DEL":
                if (rest.StartsWith("TOP", StringComparison.OrdinalIgnoreCase))
                {
                    _stack.Clear();
                }
                else
                {
                    for (var n = (int)Eval(rest); n > 0 && _stack.Count > 0; n--)
                    {
                        _stack.RemoveAt(_stack.Count - 1);
                    }
                }

                Recompute();
                break;
            case "RESOL":
                _resol = Math.Clamp((int)Eval(rest), 3, 360);
                break;
            case "MATERIAL":
                _material = rest.StartsWith('"') ? MaterialIndex(rest.Trim().Trim('"')) : _strings.TryGetValue(rest.Trim(), out var named) ? MaterialIndex(named) : -1;
                break;
            case "DEFINE":
                DefineMaterial(rest);
                break;
            case "BLOCK":
            {
                var a = Args(rest);
                Box(0, 0, 0, F(a, 0), F(a, 1), F(a, 2));
                break;
            }

            case "CYLIND":
            {
                var a = Args(rest);
                Frustum(F(a, 0), F(a, 1), F(a, 1));
                break;
            }

            case "CONE":
            {
                var a = Args(rest);
                Frustum(F(a, 0), F(a, 1), F(a, 2));
                break;
            }

            case "SPHERE":
                Sphere((float)Eval(rest));
                break;
            case "PRISM":
            case "PRISM_":
                Prism(word == "PRISM_", rest);
                break;
            case "BASE":
                _bodyVertices.Clear();
                _bodyEdges.Clear();
                break;
            case "VERT":
            {
                var a = Args(rest);
                _bodyVertices.Add(new Vector3(F(a, 0), F(a, 1), F(a, 2)));
                break;
            }

            case "EDGE":
            {
                var a = Args(rest);
                _bodyEdges.Add(((int)Num(a, 0), (int)Num(a, 1)));
                break;
            }

            case "PGON":
                BodyPolygon(rest);
                break;
            case "BODY":
                _bodyVertices.Clear();
                _bodyEdges.Clear();
                break;
            case "PEN" or "FILL" or "LINE_PROPERTY" or "SET" or "TOLER" or "COOR" or "TEVE" or "TEXTURE" or "SHADOW" or "MODEL" or "LET" or "DIM" or "PARAMETERS" or "VALUES" or "LOCK" or "LIN_" or "PRINT" or "ROTX_" or "NTR" or "FIX" or "SECT_ATTRS" or "HOTSPOT" or "UI_" or "PROJECT2" or "PROJECT" or "LINE2" or "RECT2" or "POLY2" or "POLY2_" or "CIRCLE2" or "ARC2" or "TEXT2" or "HOTSPOT2" or "DRAWINDEX" or "DEFINE_STYLE" or "STYLE" or "DEFINE_FONT":
                break;
            case "":
                break;
            default:
                if (Skipped.Count < 64)
                {
                    Skipped.Add(word);
                }

                break;
        }
    }

    private static bool IsKeyword(string w) =>
        w.Equals("IF", StringComparison.OrdinalIgnoreCase) || w.Equals("FOR", StringComparison.OrdinalIgnoreCase) || w.Equals("WHILE", StringComparison.OrdinalIgnoreCase);

    private void Push(Matrix4x4 m)
    {
        if (_stack.Count < 1024)
        {
            _stack.Add(m);
            _current = m * _current;
        }
    }

    private void Recompute()
    {
        _current = Matrix4x4.Identity;
        foreach (var m in _stack)
        {
            _current = m * _current;
        }
    }

    private static float Rad(double degrees) => (float)(degrees * Math.PI / 180);

    private List<double> Args(string text)
    {
        var parts = new List<double>();
        foreach (var piece in SplitTop(text))
        {
            parts.Add(Eval(piece));
        }

        return parts;
    }

    private static float F(List<double> a, int i) => i < a.Count ? (float)a[i] : 0;

    private static double Num(List<double> a, int i) => i < a.Count ? a[i] : 0;

    private static List<string> SplitTop(string text)
    {
        var parts = new List<string>();
        var depth = 0;
        var quote = false;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"')
            {
                quote = !quote;
            }
            else if (!quote)
            {
                if (c == '(' || c == '[')
                {
                    depth++;
                }
                else if (c == ')' || c == ']')
                {
                    depth--;
                }
                else if (c == ',' && depth == 0)
                {
                    parts.Add(text[start..i]);
                    start = i + 1;
                }
            }
        }

        if (start < text.Length || parts.Count > 0)
        {
            parts.Add(text[start..]);
        }

        return parts;
    }

    // ---- materials ----

    private int MaterialIndex(string name)
    {
        var i = Materials.FindIndex(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0)
        {
            return i;
        }

        Materials.Add(new MaterialDef { Name = name });
        return Materials.Count - 1;
    }

    /// <summary>DEFINE MATERIAL "name" type, r, g, b, ambient, diffuse, specular, transparent, shining, transparency attenuation, specular rgb, emission rgb, emission attenuation.</summary>
    private void DefineMaterial(string rest)
    {
        var (w, r) = SplitWord(rest);
        if (!w.Equals("MATERIAL", StringComparison.OrdinalIgnoreCase) || !r.StartsWith('"'))
        {
            Skipped.Add("DEFINE " + w);
            return;
        }

        var close = r.IndexOf('"', 1);
        if (close < 0)
        {
            return;
        }

        var name = r[1..close];
        var a = Args(r[(close + 1)..].TrimStart().TrimStart(','));
        var index = MaterialIndex(name);
        // a[0] is the type; the surface colour follows.
        if (a.Count >= 4)
        {
            Materials[index].Rgb = new Vector3((float)Math.Clamp(a[1], 0, 1), (float)Math.Clamp(a[2], 0, 1), (float)Math.Clamp(a[3], 0, 1));
        }

        if (a.Count >= 8)
        {
            Materials[index].Transparency = (float)Math.Clamp(a[7], 0, 1);
        }
    }

    // ---- geometry ----

    private (List<float> Positions, List<float> Normals, List<uint> Indices) Bucket()
    {
        if (!Geometry.TryGetValue(_material, out var bucket))
        {
            bucket = (new List<float>(), new List<float>(), new List<uint>());
            Geometry[_material] = bucket;
        }

        return bucket;
    }

    private void Triangle(Vector3 a, Vector3 b, Vector3 c)
    {
        if (++_triangles > _maxTriangles || _triangles > MaxGeometry)
        {
            throw new ModelFormatException("The GDL script draws more triangles than the allowed " + _maxTriangles.ToString(CultureInfo.InvariantCulture) + ".");
        }

        var (positions, normals, indices) = Bucket();
        a = Vector3.Transform(a, _current);
        b = Vector3.Transform(b, _current);
        c = Vector3.Transform(c, _current);
        var n = Vector3.Cross(b - a, c - a);
        n = n.LengthSquared() > 1e-30f ? Vector3.Normalize(n) : Vector3.UnitZ;
        var start = (uint)(positions.Count / 3);
        foreach (var p in new[] { a, b, c })
        {
            positions.Add(p.X);
            positions.Add(p.Y);
            positions.Add(p.Z);
            normals.Add(n.X);
            normals.Add(n.Y);
            normals.Add(n.Z);
        }

        indices.Add(start);
        indices.Add(start + 1);
        indices.Add(start + 2);
    }

    private void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        Triangle(a, b, c);
        Triangle(a, c, d);
    }

    private void Box(float x0, float y0, float z0, float x1, float y1, float z1)
    {
        if (x0 > x1) { (x0, x1) = (x1, x0); }
        if (y0 > y1) { (y0, y1) = (y1, y0); }
        if (z0 > z1) { (z0, z1) = (z1, z0); }
        Vector3 P(float x, float y, float z) => new(x, y, z);
        Quad(P(x0, y0, z0), P(x0, y1, z0), P(x1, y1, z0), P(x1, y0, z0));
        Quad(P(x0, y0, z1), P(x1, y0, z1), P(x1, y1, z1), P(x0, y1, z1));
        Quad(P(x0, y0, z0), P(x1, y0, z0), P(x1, y0, z1), P(x0, y0, z1));
        Quad(P(x1, y0, z0), P(x1, y1, z0), P(x1, y1, z1), P(x1, y0, z1));
        Quad(P(x1, y1, z0), P(x0, y1, z0), P(x0, y1, z1), P(x1, y1, z1));
        Quad(P(x0, y1, z0), P(x0, y0, z0), P(x0, y0, z1), P(x0, y1, z1));
    }

    /// <summary>A cylinder or cone about the z axis from 0 to <paramref name="h"/>, with discs at both ends.</summary>
    private void Frustum(float h, float r0, float r1)
    {
        var n = _resol;
        var flip = h < 0;
        for (var i = 0; i < n; i++)
        {
            var a0 = 2 * MathF.PI * i / n;
            var a1 = 2 * MathF.PI * (i + 1) / n;
            Vector3 B(float a) => new(r0 * MathF.Cos(a), r0 * MathF.Sin(a), 0);
            Vector3 T(float a) => new(r1 * MathF.Cos(a), r1 * MathF.Sin(a), h);
            if (!flip)
            {
                Quad(B(a0), B(a1), T(a1), T(a0));
                if (r0 > 0) Triangle(Vector3.Zero, B(a1), B(a0));
                if (r1 > 0) Triangle(new Vector3(0, 0, h), T(a0), T(a1));
            }
            else
            {
                Quad(B(a0), T(a0), T(a1), B(a1));
                if (r0 > 0) Triangle(Vector3.Zero, B(a0), B(a1));
                if (r1 > 0) Triangle(new Vector3(0, 0, h), T(a1), T(a0));
            }
        }
    }

    private void Sphere(float r)
    {
        var lon = _resol;
        var lat = Math.Max(2, _resol / 2);
        Vector3 P(int i, int j)
        {
            var phi = MathF.PI * j / lat - MathF.PI / 2;
            var theta = 2 * MathF.PI * i / lon;
            return new Vector3(r * MathF.Cos(phi) * MathF.Cos(theta), r * MathF.Cos(phi) * MathF.Sin(theta), r * MathF.Sin(phi));
        }

        for (var j = 0; j < lat; j++)
        {
            for (var i = 0; i < lon; i++)
            {
                if (j == 0)
                {
                    Triangle(P(i, 0), P(i + 1, 1), P(i, 1));
                }
                else if (j == lat - 1)
                {
                    Triangle(P(i, j), P(i + 1, j), P(i, lat));
                }
                else
                {
                    Quad(P(i, j), P(i + 1, j), P(i + 1, j + 1), P(i, j + 1));
                }
            }
        }
    }

    /// <summary>PRISM n, h, x1, y1, ... or PRISM_ n, h, x1, y1, s1, ...: a polygon extruded from z = 0 to h; status 900 gives an arc's centre and 3000 and up ends the arc.</summary>
    private void Prism(bool withStatus, string rest)
    {
        var a = Args(rest);
        var n = (int)Num(a, 0);
        var h = F(a, 1);
        var stride = withStatus ? 3 : 2;
        if (n < 3 || a.Count < 2 + n * stride)
        {
            Skipped.Add("PRISM (malformed)");
            return;
        }

        var points = new List<Vector2>();
        Vector2? center = null;
        for (var i = 0; i < n; i++)
        {
            var x = (float)a[2 + i * stride];
            var y = (float)a[3 + i * stride];
            var s = withStatus ? a[4 + i * stride] : 0;
            if (s < 0)
            {
                Skipped.Add("PRISM_ (holes)");
                break;
            }

            if (withStatus && Math.Abs(s - 900) < 0.5)
            {
                center = new Vector2(x, y);
                continue;
            }

            if (withStatus && s >= 1000 && center is { } c && points.Count > 0)
            {
                var start = points[^1];
                var a0 = MathF.Atan2(start.Y - c.Y, start.X - c.X);
                var a1 = MathF.Atan2(y - c.Y, x - c.X);
                var sweep = a1 - a0;
                while (sweep <= 1e-6f)
                {
                    sweep += 2 * MathF.PI;
                }

                var radius = (start - c).Length();
                var steps = Math.Max(1, (int)MathF.Round(_resol * sweep / (2 * MathF.PI)));
                for (var k = 1; k < steps; k++)
                {
                    var angle = a0 + sweep * k / steps;
                    points.Add(new Vector2(c.X + radius * MathF.Cos(angle), c.Y + radius * MathF.Sin(angle)));
                }

                points.Add(new Vector2(x, y));
                center = null;
                continue;
            }

            points.Add(new Vector2(x, y));
        }

        if (points.Count > 1 && Vector2.DistanceSquared(points[0], points[^1]) < 1e-12f)
        {
            points.RemoveAt(points.Count - 1);
        }

        if (points.Count < 3)
        {
            return;
        }

        var area = 0f;
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var q = points[(i + 1) % points.Count];
            area += p.X * q.Y - q.X * p.Y;
        }

        if (area < 0)
        {
            points.Reverse();
        }

        var bottom = Math.Min(0, h);
        var top = Math.Max(0, h);
        var ring = points.Select(p => new Vector3(p.X, p.Y, 0)).ToList();
        foreach (var (u, v, w) in PolygonTriangulation.Triangulate(ring))
        {
            var pu = points[u];
            var pv = points[v];
            var pw = points[w];
            Triangle(new Vector3(pu, top), new Vector3(pv, top), new Vector3(pw, top));
            Triangle(new Vector3(pw, bottom), new Vector3(pv, bottom), new Vector3(pu, bottom));
        }

        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var q = points[(i + 1) % points.Count];
            Quad(new Vector3(p, bottom), new Vector3(q, bottom), new Vector3(q, top), new Vector3(p, top));
        }
    }

    /// <summary>PGON n, vect, status, e1 ... en: the edge numbers (negative: the edge reversed) walk round the polygon.</summary>
    private void BodyPolygon(string rest)
    {
        var a = Args(rest);
        var n = (int)Num(a, 0);
        if (n < 3 || a.Count < 3 + n)
        {
            return;
        }

        var ring = new List<Vector3>(n);
        for (var i = 0; i < n; i++)
        {
            var e = (int)a[3 + i];
            var index = Math.Abs(e) - 1;
            if (index < 0 || index >= _bodyEdges.Count)
            {
                return;
            }

            var (v0, v1) = _bodyEdges[index];
            var vertex = (e > 0 ? v0 : v1) - 1;
            if (vertex < 0 || vertex >= _bodyVertices.Count)
            {
                return;
            }

            ring.Add(_bodyVertices[vertex]);
        }

        if (n == 3)
        {
            Triangle(ring[0], ring[1], ring[2]);
            return;
        }

        foreach (var (u, v, w) in PolygonTriangulation.Triangulate(ring))
        {
            Triangle(ring[u], ring[v], ring[w]);
        }
    }

    // ---- expressions ----

    private double Eval(string text)
    {
        var tokens = Tokenize(text);
        var position = 0;
        var value = Or(tokens, ref position);
        return double.IsFinite(value) ? value : 0;
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (char.IsDigit(c) || (c == '.' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                var j = i;
                while (j < text.Length && (char.IsDigit(text[j]) || text[j] == '.'))
                {
                    j++;
                }

                if (j < text.Length && (text[j] == 'e' || text[j] == 'E') && j + 1 < text.Length && (char.IsDigit(text[j + 1]) || ((text[j + 1] == '-' || text[j + 1] == '+') && j + 2 < text.Length && char.IsDigit(text[j + 2]))))
                {
                    j += 2;
                    while (j < text.Length && char.IsDigit(text[j]))
                    {
                        j++;
                    }
                }

                tokens.Add(text[i..j]);
                i = j;
            }
            else if (char.IsLetter(c) || c == '_')
            {
                var j = i;
                while (j < text.Length && (char.IsLetterOrDigit(text[j]) || text[j] == '_'))
                {
                    j++;
                }

                tokens.Add(text[i..j]);
                i = j;
            }
            else if (c == '"')
            {
                var j = text.IndexOf('"', i + 1);
                j = j < 0 ? text.Length - 1 : j;
                tokens.Add(text[i..(j + 1)]);
                i = j + 1;
            }
            else if ((c == '<' || c == '>') && i + 1 < text.Length && (text[i + 1] == '=' || (c == '<' && text[i + 1] == '>')))
            {
                tokens.Add(text.Substring(i, 2));
                i += 2;
            }
            else
            {
                tokens.Add(c.ToString());
                i++;
            }
        }

        return tokens;
    }

    private static bool Is(List<string> t, int p, string s) => p < t.Count && string.Equals(t[p], s, StringComparison.OrdinalIgnoreCase);

    private double Or(List<string> t, ref int p)
    {
        var v = And(t, ref p);
        while (Is(t, p, "OR") || Is(t, p, "EXOR"))
        {
            var exor = Is(t, p, "EXOR");
            p++;
            var r = And(t, ref p);
            v = exor ? ((v != 0) ^ (r != 0) ? 1 : 0) : (v != 0 || r != 0 ? 1 : 0);
        }

        return v;
    }

    private double And(List<string> t, ref int p)
    {
        var v = Compare(t, ref p);
        while (Is(t, p, "AND"))
        {
            p++;
            var r = Compare(t, ref p);
            v = v != 0 && r != 0 ? 1 : 0;
        }

        return v;
    }

    private double Compare(List<string> t, ref int p)
    {
        var v = Sum(t, ref p);
        while (p < t.Count && t[p] is "=" or "<>" or "#" or "<" or ">" or "<=" or ">=")
        {
            var op = t[p++];
            var r = Sum(t, ref p);
            v = op switch
            {
                "=" => Math.Abs(v - r) < 1e-9 ? 1 : 0,
                "<>" or "#" => Math.Abs(v - r) >= 1e-9 ? 1 : 0,
                "<" => v < r - 1e-9 ? 1 : 0,
                ">" => v > r + 1e-9 ? 1 : 0,
                "<=" => v <= r + 1e-9 ? 1 : 0,
                _ => v >= r - 1e-9 ? 1 : 0,
            };
        }

        return v;
    }

    private double Sum(List<string> t, ref int p)
    {
        var v = Term(t, ref p);
        while (p < t.Count && t[p] is "+" or "-")
        {
            var op = t[p++];
            var r = Term(t, ref p);
            v = op == "+" ? v + r : v - r;
        }

        return v;
    }

    private double Term(List<string> t, ref int p)
    {
        var v = Power(t, ref p);
        while ((p < t.Count && t[p] is "*" or "/") || Is(t, p, "MOD"))
        {
            var op = t[p++];
            var r = Power(t, ref p);
            v = op == "*" ? v * r : op == "/" ? (r == 0 ? 0 : v / r) : (r == 0 ? 0 : v - Math.Floor(v / r) * r);
        }

        return v;
    }

    private double Power(List<string> t, ref int p)
    {
        var v = Unary(t, ref p);
        while (p < t.Count && t[p] == "^")
        {
            p++;
            v = Math.Pow(v, Unary(t, ref p));
        }

        return v;
    }

    private double Unary(List<string> t, ref int p)
    {
        if (p < t.Count && t[p] == "-")
        {
            p++;
            return -Unary(t, ref p);
        }

        if (p < t.Count && t[p] == "+")
        {
            p++;
            return Unary(t, ref p);
        }

        if (Is(t, p, "NOT"))
        {
            p++;
            return Unary(t, ref p) == 0 ? 1 : 0;
        }

        return Primary(t, ref p);
    }

    private double Primary(List<string> t, ref int p)
    {
        if (p >= t.Count)
        {
            return 0;
        }

        var token = t[p++];
        if (token == "(")
        {
            var v = Or(t, ref p);
            if (p < t.Count && t[p] == ")")
            {
                p++;
            }

            return v;
        }

        if (char.IsDigit(token[0]) || token[0] == '.')
        {
            return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0;
        }

        if (token[0] == '"')
        {
            return 0;
        }

        if (p < t.Count && t[p] == "(")
        {
            p++;
            var args = new List<double>();
            if (p < t.Count && t[p] == ")")
            {
                p++;
            }
            else
            {
                while (true)
                {
                    args.Add(Or(t, ref p));
                    if (p < t.Count && t[p] == ",")
                    {
                        p++;
                        continue;
                    }

                    if (p < t.Count && t[p] == ")")
                    {
                        p++;
                    }

                    break;
                }
            }

            return Function(token.ToUpperInvariant(), args);
        }

        if (token.Equals("PI", StringComparison.OrdinalIgnoreCase))
        {
            return Math.PI;
        }

        if (_vars.TryGetValue(token, out var value))
        {
            return value;
        }

        if (Skipped.Count < 64)
        {
            Skipped.Add("undefined " + token);
        }

        return 0;
    }

    private static double Function(string name, List<double> a)
    {
        double X(int i) => i < a.Count ? a[i] : 0;
        return name switch
        {
            "MIN" => a.Count == 0 ? 0 : a.Min(),
            "MAX" => a.Count == 0 ? 0 : a.Max(),
            "ABS" => Math.Abs(X(0)),
            "SQR" or "SQRT" => X(0) < 0 ? 0 : Math.Sqrt(X(0)),
            "SIN" => Math.Sin(X(0) * Math.PI / 180),
            "COS" => Math.Cos(X(0) * Math.PI / 180),
            "TAN" => Math.Tan(X(0) * Math.PI / 180),
            "ATN" => Math.Atan(X(0)) * 180 / Math.PI,
            "ASN" => Math.Asin(Math.Clamp(X(0), -1, 1)) * 180 / Math.PI,
            "ACS" => Math.Acos(Math.Clamp(X(0), -1, 1)) * 180 / Math.PI,
            "INT" => Math.Truncate(X(0)),
            "FRA" => X(0) - Math.Truncate(X(0)),
            "ROUND_INT" => Math.Round(X(0), MidpointRounding.AwayFromZero),
            "CEIL" => Math.Ceiling(X(0)),
            "EXP" => Math.Exp(X(0)),
            "LGT" => X(0) > 0 ? Math.Log10(X(0)) : 0,
            "LOG" => X(0) > 0 ? Math.Log(X(0)) : 0,
            "SGN" => Math.Sign(X(0)),
            "MOD" => X(1) == 0 ? 0 : X(0) - Math.Floor(X(0) / X(1)) * X(1),
            _ => 0,
        };
    }
}
