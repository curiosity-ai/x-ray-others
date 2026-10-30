using System.Globalization;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.OpenScad;

/// <summary>Evaluates a parsed OpenSCAD program into solids and planar shapes.</summary>
internal sealed class ScadEval
{
    private sealed class CallArgs
    {
        public List<object?> Pos { get; } = new();

        public Dictionary<string, object?> Named { get; } = new(StringComparer.Ordinal);

        public object? Get(int index, string name) =>
            Named.TryGetValue(name, out var v) ? v : index >= 0 && index < Pos.Count ? Pos[index] : null;

        public bool Has(int index, string name) => Named.ContainsKey(name) || (index >= 0 && index < Pos.Count);
    }

    private readonly LoadOptions _options;
    private readonly Func<string, string?> _resolve;
    private long _steps;
    private int _depth;
    private int _includeDepth;
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private readonly Dictionary<(float, float, float, float), int> _colorIds = new();
    private readonly double _pi = Math.PI;

    public ScadEval(LoadOptions options, Func<string, string?> resolve)
    {
        _options = options;
        _resolve = resolve;
    }

    public List<string> Warnings { get; } = new();

    public List<ModelMaterial> Materials { get; } = new();

    public List<string> Echo { get; } = new();

    /// <summary>The scale used for CSG tolerances (the largest coordinate seen in a boolean operand).</summary>
    private double _extent = 1;

    private void Warn(string message)
    {
        if (_warned.Add(message))
        {
            Warnings.Add(message);
        }
    }

    public List<Geo> Run(Stmt[] program)
    {
        var global = new ScadEnv(null, null);
        global.Vars["$fn"] = 0.0;
        global.Vars["$fa"] = 12.0;
        global.Vars["$fs"] = 2.0;
        global.Vars["$t"] = 0.0;
        global.Vars["PI"] = _pi;
        global.Vars["$preview"] = false;
        var result = new List<Geo>();
        var root = Exec(new Block(program), global, newScope: false);
        result.AddRange(root);
        return result;
    }

    private void Step()
    {
        if (++_steps > 80_000_000)
        {
            throw new ModelFormatException("The OpenSCAD program does too much work (step limit).");
        }

        Cancel.Every(_options.Cancellation, _steps);
    }

    // ---------------------------------------------------------------- statements

    private List<Geo> Exec(Stmt stmt, ScadEnv env, bool newScope = true)
    {
        Step();
        switch (stmt)
        {
            case Block b:
            {
                var scope = newScope ? new ScadEnv(env, env) : env;
                foreach (var item in b.Items)
                {
                    switch (item)
                    {
                        case ModuleDef m:
                            (scope.Modules ??= new(StringComparer.Ordinal))[m.Name] = m;
                            break;
                        case FuncDef f:
                            (scope.Functions ??= new(StringComparer.Ordinal))[f.Name] = f;
                            break;
                    }
                }

                var all = new List<Geo>();
                foreach (var item in b.Items)
                {
                    all.AddRange(Exec(item, scope, newScope: false));
                }

                return all;
            }

            case AssignStmt a:
                env.Vars[a.Name] = Eval(a.Value, env);
                return [];
            case ModuleDef or FuncDef:
                return [];
            case IncludeStmt inc:
                return Include(inc, env);
            case LetStmt l:
            {
                var scope = new ScadEnv(env, env);
                foreach (var a in l.Assigns)
                {
                    scope.Vars[a.Name!] = Eval(a.Value, scope);
                }

                return Exec(l.Body, scope);
            }

            case IfStmt i:
                if (ScadValue.Truthy(Eval(i.Cond, env)))
                {
                    return Exec(i.Then, env);
                }

                return i.Else is null ? [] : Exec(i.Else, env);
            case ForStmt f:
                return For(f, env);
            case CForStmt c:
            {
                var scope = new ScadEnv(env, env);
                foreach (var a in c.Init)
                {
                    scope.Vars[a.Name!] = Eval(a.Value, scope);
                }

                var all = new List<Geo>();
                var guard = 0;
                while (ScadValue.Truthy(Eval(c.Cond, scope)))
                {
                    Step();
                    if (++guard > 1_000_000)
                    {
                        throw new ModelFormatException("An OpenSCAD for loop does not end.");
                    }

                    all.AddRange(Exec(c.Body, scope));
                    var updates = c.Step.Select(s => (s.Name!, Eval(s.Value, scope))).ToList();
                    foreach (var (n, v) in updates)
                    {
                        scope.Vars[n] = v;
                    }
                }

                return all;
            }

            case Instance inst:
                return InstanceStmt(inst, env);
        }

        return [];
    }

    private List<Geo> Include(IncludeStmt inc, ScadEnv env)
    {
        if (_includeDepth >= 6)
        {
            return [];
        }

        var text = _resolve(inc.Path);
        if (text is null)
        {
            Warn($"The file '{inc.Path}' that the program includes was not supplied.");
            return [];
        }

        _includeDepth++;
        try
        {
            var program = ScadParser.Parse(text, _options.Cancellation);
            if (inc.Use)
            {
                // use <file>: definitions only.
                foreach (var s in program)
                {
                    switch (s)
                    {
                        case ModuleDef m:
                            (env.Modules ??= new(StringComparer.Ordinal))[m.Name] = m;
                            break;
                        case FuncDef f:
                            (env.Functions ??= new(StringComparer.Ordinal))[f.Name] = f;
                            break;
                    }
                }

                return [];
            }

            return Exec(new Block(program), env, newScope: false);
        }
        finally
        {
            _includeDepth--;
        }
    }

    private IEnumerable<object?> Iterate(object? v)
    {
        switch (v)
        {
            case List<object?> l:
                foreach (var x in l)
                {
                    yield return x;
                }

                break;
            case ScadRange r:
                var n = r.Count;
                if (n > 20_000_000)
                {
                    throw new ModelFormatException("An OpenSCAD range is too long.");
                }

                for (long i = 0; i < n; i++)
                {
                    yield return r.Start + i * r.Step;
                }

                break;
            case null:
                break;
            default:
                yield return v;
                break;
        }
    }

    private List<Geo> For(ForStmt f, ScadEnv env)
    {
        var results = new List<Geo>();
        var groups = new List<Geo?>();

        void Loop(int level, ScadEnv scope)
        {
            if (level == f.Vars.Length)
            {
                var g = Exec(f.Body, scope);
                if (f.Intersection)
                {
                    groups.Add(Unite(g));
                }
                else
                {
                    results.AddRange(g);
                }

                return;
            }

            var arg = f.Vars[level];
            foreach (var value in Iterate(Eval(arg.Value, scope)))
            {
                Step();
                var inner = new ScadEnv(scope, scope);
                inner.Vars[arg.Name!] = value;
                Loop(level + 1, inner);
            }
        }

        Loop(0, env);
        if (f.Intersection)
        {
            var one = Intersection(groups.Where(g => g is not null).Select(g => g!).ToList());
            return one is null ? [] : [one];
        }

        return results;
    }

    private List<Geo> InstanceStmt(Instance inst, ScadEnv env)
    {
        if (inst.Modifiers.Contains('*', StringComparison.Ordinal) || inst.Modifiers.Contains('%', StringComparison.Ordinal))
        {
            return [];
        }

        if (inst.Name == "group" && inst.Args.Length == 0)
        {
            return inst.Child is null ? [] : Exec(inst.Child, env);
        }

        var args = BindArgs(inst.Args, env);
        var user = env.FindModule(inst.Name);
        if (user is not null)
        {
            return CallModule(user, args, inst.Child, env);
        }

        return Builtin(inst, args, env);
    }

    private List<Geo> CallModule(ModuleDef def, CallArgs args, Stmt? child, ScadEnv callEnv)
    {
        if (++_depth > 150)
        {
            _depth--;
            throw new ModelFormatException("The OpenSCAD program recurses too deeply.");
        }

        try
        {
            // The module's own environment: the one it was defined in is the lexical parent; here that is found by name lookup from the call site.
            var scope = new ScadEnv(DefinitionEnv(def, callEnv), callEnv);
            Bind(def.Params, args, scope);
            var kids = child is null ? [] : child is Block blk ? blk.Items : [child];
            scope.Children = (kids, callEnv);
            scope.Vars["$children"] = (double)kids.Length;
            return Exec(def.Body, scope);
        }
        finally
        {
            _depth--;
        }
    }

    private static ScadEnv DefinitionEnv(ModuleDef def, ScadEnv from)
    {
        for (var e = from; e is not null; e = e.Lex)
        {
            if (e.Modules is not null && e.Modules.TryGetValue(def.Name, out var m) && ReferenceEquals(m, def))
            {
                return e;
            }
        }

        return from;
    }

    private void Bind(Param[] ps, CallArgs args, ScadEnv scope)
    {
        for (var i = 0; i < ps.Length; i++)
        {
            var p = ps[i];
            if (args.Named.TryGetValue(p.Name, out var named))
            {
                scope.Vars[p.Name] = named;
            }
            else if (i < args.Pos.Count)
            {
                scope.Vars[p.Name] = args.Pos[i];
            }
            else
            {
                scope.Vars[p.Name] = p.Default is null ? null : Eval(p.Default, scope);
            }
        }

        foreach (var (k, v) in args.Named)
        {
            if (k.Length > 0 && k[0] == '$')
            {
                scope.Vars[k] = v;
            }
        }
    }

    private CallArgs BindArgs(Arg[] args, ScadEnv env)
    {
        var r = new CallArgs();
        foreach (var a in args)
        {
            var v = Eval(a.Value, env);
            if (a.Name is null)
            {
                r.Pos.Add(v);
            }
            else
            {
                r.Named[a.Name] = v;
            }
        }

        return r;
    }

    /// <summary>Evaluates each child statement of a module call to one united geometry (null for a child that draws nothing).</summary>
    private List<Geo> ChildGroups(Stmt? child, ScadEnv env)
    {
        var groups = new List<Geo>();
        if (child is null)
        {
            return groups;
        }

        var items = child is Block b ? b.Items : [child];
        var scope = child is Block ? new ScadEnv(env, env) : env;
        foreach (var item in items)
        {
            if (item is ModuleDef m)
            {
                (scope.Modules ??= new(StringComparer.Ordinal))[m.Name] = m;
            }
            else if (item is FuncDef f)
            {
                (scope.Functions ??= new(StringComparer.Ordinal))[f.Name] = f;
            }
        }

        foreach (var item in items)
        {
            var g = Unite(Exec(item, scope, newScope: true));
            if (g is not null)
            {
                groups.Add(g);
            }
        }

        return groups;
    }

    // ---------------------------------------------------------------- geometry assembly

    private Geo? Unite(List<Geo> geos)
    {
        if (geos.Count == 0)
        {
            return null;
        }

        if (geos.Count == 1)
        {
            return geos[0];
        }

        var solids = geos.OfType<Solid>().ToList();
        var shapes = geos.OfType<Shape>().ToList();
        if (solids.Count > 0 && shapes.Count > 0)
        {
            Warn("A group mixes 2D shapes and 3D solids; the 2D shapes were dropped.");
        }

        if (solids.Count > 0)
        {
            var parts = new List<List<Poly>>();
            foreach (var s in solids)
            {
                parts.AddRange(s.Parts);
            }

            return new Solid(parts);
        }

        var regions = new List<List<(double X, double Y)[]>>();
        foreach (var s in shapes)
        {
            regions.AddRange(s.Regions);
        }

        return new Shape(regions);
    }

    private CsgBudget Budget() => new() { Cancel = _options.Cancellation, Epsilon = Math.Max(_extent, 1e-6) * 1e-7, MaxPolygons = (int)Math.Min(_options.MaxTriangles, 600_000) };

    private void Measure(IEnumerable<Poly> polys)
    {
        foreach (var p in polys)
        {
            foreach (var v in p.Vertices)
            {
                _extent = Math.Max(_extent, Math.Max(Math.Abs(v.X), Math.Max(Math.Abs(v.Y), Math.Abs(v.Z))));
            }
        }
    }

    private Geo? Difference(List<Geo> groups)
    {
        if (groups.Count == 0)
        {
            return null;
        }

        var first = groups[0];
        var cutters = groups.Skip(1).ToList();
        if (cutters.Count == 0)
        {
            return first;
        }

        if (first is Solid solid)
        {
            Measure(solid.Polys);
            var cutParts = cutters.OfType<Solid>().SelectMany(c => c.Parts).Where(p => p.Count > 0).ToList();
            foreach (var cp in cutParts)
            {
                Measure(cp);
            }

            try
            {
                var parts = solid.Parts;
                foreach (var cut in cutParts)
                {
                    var box = Box(cut);
                    var next = new List<List<Poly>>(parts.Count);
                    foreach (var part in parts)
                    {
                        if (!Overlap(Box(part), box))
                        {
                            next.Add(part);
                            continue;
                        }

                        var current = part;
                        next.Add(Csg.OnBigStack(() => Csg.Subtract(current, cut, Budget())));
                    }

                    parts = next;
                }

                return new Solid(parts);
            }
            catch (CsgTooBig big)
            {
                Warn("A difference was too large (" + big.Message + ") for the boolean operation; the first operand is drawn uncut.");
                return first;
            }
        }

        if (first is Shape shape)
        {
            var regions = shape.Regions.Select(r => r.ToList()).ToList();
            var unplaced = false;
            foreach (var cutter in cutters.OfType<Shape>())
            {
                foreach (var cut in cutter.Regions)
                {
                    if (cut.Count == 0)
                    {
                        continue;
                    }

                    var outline = cut[0];
                    var host = regions.FirstOrDefault(r => r.Count > 0 && outline.All(p => ScadGeo.Inside(p, r[0])));
                    if (host is null)
                    {
                        unplaced = true;
                        continue;
                    }

                    var hole = outline.Reverse().ToArray();
                    host.Add(hole);
                }
            }

            if (unplaced)
            {
                Warn("A 2D difference where the cut crosses the outline is not computed: that cut was ignored.");
            }

            return new Shape(regions);
        }

        return first;
    }

    private Geo? Intersection(List<Geo> groups)
    {
        if (groups.Count == 0)
        {
            return null;
        }

        if (groups[0] is Solid first)
        {
            Measure(first.Polys);
            try
            {
                var parts = first.Parts;
                foreach (var g in groups.Skip(1).OfType<Solid>())
                {
                    Measure(g.Polys);
                    var next = new List<List<Poly>>();
                    foreach (var a in parts)
                    {
                        foreach (var b in g.Parts)
                        {
                            if (a.Count == 0 || b.Count == 0 || !Overlap(Box(a), Box(b)))
                            {
                                continue;
                            }

                            var pa = a;
                            var pb = b;
                            next.Add(Csg.OnBigStack(() => Csg.Intersect(pa, pb, Budget())));
                        }
                    }

                    parts = next;
                }

                return new Solid(parts);
            }
            catch (CsgTooBig big)
            {
                Warn("An intersection was too large (" + big.Message + ") for the boolean operation; the first operand is drawn.");
                return first;
            }
        }

        if (groups.Count > 1)
        {
            Warn("2D intersections are not computed; the first shape is drawn.");
        }

        return groups[0];
    }

    private Geo? HullOf(List<Geo> groups)
    {
        var geo = Unite(groups);
        if (geo is Shape s)
        {
            return ScadGeo.Hull(s.Regions.SelectMany(r => r).SelectMany(l => l).ToList());
        }

        if (geo is Solid solid)
        {
            Measure(solid.Polys);
            var pts = new List<D3>();
            foreach (var p in solid.Polys)
            {
                pts.AddRange(p.Vertices);
            }

            try
            {
                var hull = Csg.Hull(pts, solid.Polys.FirstOrDefault()?.Color ?? -1, Budget());
                if (hull is not null)
                {
                    return new Solid(hull);
                }
            }
            catch (CsgTooBig)
            {
            }

            Warn("A hull of coplanar or oversized input is drawn as the union of its operands.");
            return solid;
        }

        return null;
    }

    private static (D3 Min, D3 Max) Box(List<Poly> polys)
    {
        var min = new D3(double.MaxValue, double.MaxValue, double.MaxValue);
        var max = new D3(double.MinValue, double.MinValue, double.MinValue);
        foreach (var p in polys)
        {
            foreach (var v in p.Vertices)
            {
                min = new D3(Math.Min(min.X, v.X), Math.Min(min.Y, v.Y), Math.Min(min.Z, v.Z));
                max = new D3(Math.Max(max.X, v.X), Math.Max(max.Y, v.Y), Math.Max(max.Z, v.Z));
            }
        }

        return (min, max);
    }

    private static bool Overlap((D3 Min, D3 Max) a, (D3 Min, D3 Max) b) =>
        a.Min.X <= b.Max.X && b.Min.X <= a.Max.X && a.Min.Y <= b.Max.Y && b.Min.Y <= a.Max.Y && a.Min.Z <= b.Max.Z && b.Min.Z <= a.Max.Z;

    private Geo Paint(Geo geo, int color)
    {
        if (geo is not Solid s)
        {
            return geo;
        }

        return new Solid(s.Parts.Select(part => part.Select(p => p.Color >= 0 ? p : new Poly(p.Vertices, color)).ToList()).ToList());
    }

    private Geo Move(Geo g, M4 m) => g switch
    {
        Solid s => ScadGeo.Transform(s, m),
        Shape sh => ScadGeo.Transform(sh, m),
        _ => g,
    };

    // ---------------------------------------------------------------- built-in modules

    private List<Geo> One(Geo? g) => g is null ? [] : [g];

    private static D3 Vec3(object? v, D3 fallback)
    {
        if (ScadValue.Doubles(v) is { Length: >= 1 } d)
        {
            return new D3(d[0], d.Length > 1 ? d[1] : 0, d.Length > 2 ? d[2] : 0);
        }

        return ScadValue.Num(v) is { } n ? new D3(n, n, n) : fallback;
    }

    private double Special(ScadEnv env, string name, double fallback, CallArgs a)
    {
        if (a.Named.TryGetValue(name, out var v) && ScadValue.Num(v) is { } d)
        {
            return d;
        }

        return env.TryGet(name, out var x) && ScadValue.Num(x) is { } n ? n : fallback;
    }

    private int Frags(double r, ScadEnv env, CallArgs a) => ScadGeo.Fragments(r, Special(env, "$fn", 0, a), Special(env, "$fa", 12, a), Special(env, "$fs", 2, a));

    private List<Geo> Builtin(Instance inst, CallArgs a, ScadEnv env)
    {
        switch (inst.Name)
        {
            case "cube":
            {
                var size = Vec3(a.Get(0, "size"), new D3(1, 1, 1));
                return One(ScadGeo.Cube(size, ScadValue.Truthy(a.Get(1, "center"))));
            }

            case "sphere":
            {
                var r = Radius(a, 0, "r", "d", 1);
                return One(ScadGeo.Sphere(r, Frags(r, env, a)));
            }

            case "cylinder":
            {
                var h = ScadValue.Num(a.Get(0, "h")) ?? 1;
                double r1, r2;
                if (a.Named.ContainsKey("d1") || a.Named.ContainsKey("d2"))
                {
                    r1 = (ScadValue.Num(a.Get(-1, "d1")) ?? 1) / 2;
                    r2 = (ScadValue.Num(a.Get(-1, "d2")) ?? 1) / 2;
                }
                else if (a.Named.ContainsKey("r1") || a.Named.ContainsKey("r2") || (a.Pos.Count > 2))
                {
                    r1 = ScadValue.Num(a.Get(1, "r1")) ?? 1;
                    r2 = ScadValue.Num(a.Get(2, "r2")) ?? 1;
                }
                else
                {
                    r1 = r2 = Radius(a, 1, "r", "d", 1);
                }

                var n = Frags(Math.Max(r1, r2), env, a);
                return One(ScadGeo.Cylinder(h, Math.Max(r1, 0), Math.Max(r2, 0), ScadValue.Truthy(a.Get(3, "center")), n));
            }

            case "polyhedron":
            {
                var pts = (a.Get(0, "points") as List<object?>)?.Select(p => Vec3(p, default)).ToList() ?? [];
                var faces = new List<int[]>();
                foreach (var f in (a.Get(1, "faces") ?? a.Get(-1, "triangles")) as List<object?> ?? [])
                {
                    if (ScadValue.Doubles(f) is { Length: >= 3 } d && d.All(i => i >= 0 && i < pts.Count && i == Math.Floor(i)))
                    {
                        faces.Add(d.Select(i => (int)i).ToArray());
                    }
                }

                return One(ScadGeo.Polyhedron(pts, faces));
            }

            case "square":
            {
                var size = a.Get(0, "size");
                var d = ScadValue.Doubles(size, 2);
                var x = d?[0] ?? ScadValue.Num(size) ?? 1;
                var y = d?[1] ?? ScadValue.Num(size) ?? 1;
                return One(ScadGeo.Square(x, y, ScadValue.Truthy(a.Get(1, "center"))));
            }

            case "circle":
            {
                var r = Radius(a, 0, "r", "d", 1);
                return One(ScadGeo.Circle(r, Frags(r, env, a)));
            }

            case "polygon":
            {
                var pts = new List<(double X, double Y)>();
                foreach (var p in a.Get(0, "points") as List<object?> ?? [])
                {
                    if (ScadValue.Doubles(p, 2) is { } d)
                    {
                        pts.Add((d[0], d[1]));
                    }
                }

                List<int[]>? paths = null;
                if (a.Get(1, "paths") is List<object?> pl)
                {
                    paths = new List<int[]>();
                    foreach (var p in pl)
                    {
                        if (ScadValue.Doubles(p) is { } d && d.All(i => i >= 0 && i < pts.Count))
                        {
                            paths.Add(d.Select(i => (int)i).ToArray());
                        }
                    }
                }

                return One(ScadGeo.Polygon(pts, paths));
            }

            case "translate":
                return One(Transformed(inst, env, M4.Translate(Vec3(a.Get(0, "v"), default).X, Vec3(a.Get(0, "v"), default).Y, Vec3(a.Get(0, "v"), default).Z)));
            case "scale":
            {
                var s = Vec3(a.Get(0, "v"), new D3(1, 1, 1));
                return One(Transformed(inst, env, M4.Scale(s.X, s.Y, s.Z)));
            }

            case "mirror":
                return One(Transformed(inst, env, M4.Mirror(Vec3(a.Get(0, "v"), new D3(1, 0, 0)))));
            case "rotate":
            {
                var angle = a.Get(0, "a");
                if (ScadValue.Doubles(angle) is { } e)
                {
                    if (a.Has(1, "v") && ScadValue.Doubles(a.Get(1, "v"), 3) is { } axis && e.Length == 1)
                    {
                        return One(Transformed(inst, env, M4.RotateAxis(e[0], new D3(axis[0], axis[1], axis[2]))));
                    }

                    return One(Transformed(inst, env, M4.RotateEuler(e.Length > 0 ? e[0] : 0, e.Length > 1 ? e[1] : 0, e.Length > 2 ? e[2] : 0)));
                }

                var deg = ScadValue.Num(angle) ?? 0;
                if (ScadValue.Doubles(a.Get(1, "v"), 3) is { } ax)
                {
                    return One(Transformed(inst, env, M4.RotateAxis(deg, new D3(ax[0], ax[1], ax[2]))));
                }

                return One(Transformed(inst, env, M4.RotateEuler(0, 0, deg)));
            }

            case "multmatrix":
            {
                var m = M4.Identity.V.ToArray();
                if (a.Get(0, "m") is List<object?> rows)
                {
                    for (var i = 0; i < Math.Min(rows.Count, 4); i++)
                    {
                        if (ScadValue.Doubles(rows[i]) is { } row)
                        {
                            for (var j = 0; j < Math.Min(row.Length, 4); j++)
                            {
                                m[i * 4 + j] = row[j];
                            }
                        }
                    }
                }

                return One(Transformed(inst, env, new M4(m)));
            }

            case "resize":
            {
                var geo = Unite(ChildGroups(inst.Child, env));
                if (geo is null)
                {
                    return [];
                }

                var want = Vec3(a.Get(0, "newsize"), default);
                var box = BoundsOf(geo);
                double F(double w, double size) => w > 0 && size > 1e-12 ? w / size : double.NaN;
                var sx = F(want.X, box.X);
                var sy = F(want.Y, box.Y);
                var sz = F(want.Z, box.Z);
                var auto = ScadValue.Truthy(a.Get(1, "auto"));
                var uniform = new[] { sx, sy, sz }.FirstOrDefault(v => !double.IsNaN(v), 1);
                double Pick(double s) => double.IsNaN(s) ? (auto ? uniform : 1) : s;
                return One(Move(geo, M4.Scale(Pick(sx), Pick(sy), Pick(sz))));
            }

            case "color":
            {
                var geo = Unite(ChildGroups(inst.Child, env));
                if (geo is null)
                {
                    return [];
                }

                return One(Paint(geo, ColorIndex(a.Get(0, "c"), ScadValue.Num(a.Get(1, "alpha")))));
            }

            case "union" or "render" or "group":
                return One(Unite(ChildGroups(inst.Child, env).ToList()));
            case "difference":
                return One(Difference(ChildGroups(inst.Child, env)));
            case "intersection":
                return One(Intersection(ChildGroups(inst.Child, env)));
            case "hull":
                return One(HullOf(ChildGroups(inst.Child, env)));
            case "minkowski":
                Warn("minkowski() is not computed: its operands are drawn as a union.");
                return One(Unite(ChildGroups(inst.Child, env)));
            case "offset":
            {
                var geo = Unite(ChildGroups(inst.Child, env));
                if (geo is not Shape shape)
                {
                    return One(geo);
                }

                var d = ScadValue.Num(a.Get(-1, "delta")) ?? ScadValue.Num(a.Get(0, "r")) ?? 0;
                if (a.Named.ContainsKey("r") || a.Pos.Count > 0)
                {
                    Warn("offset(r) is drawn with mitred corners rather than rounded ones.");
                }

                return One(ScadGeo.Offset(shape, d));
            }

            case "linear_extrude":
            {
                var geo = Unite(ChildGroups(inst.Child, env));
                if (geo is not Shape shape)
                {
                    return [];
                }

                var h = ScadValue.Num(a.Get(0, "height")) ?? 100;
                var center = ScadValue.Truthy(a.Get(1, "center"));
                var twist = ScadValue.Num(a.Get(3, "twist")) ?? 0;
                var scaleArg = a.Get(5, "scale");
                double sx = 1, sy = 1;
                if (ScadValue.Doubles(scaleArg, 2) is { } sv)
                {
                    sx = sv[0];
                    sy = sv[1];
                }
                else if (ScadValue.Num(scaleArg) is { } sc)
                {
                    sx = sy = sc;
                }

                var slices = (int)Math.Min(ScadValue.Num(a.Get(4, "slices")) ?? Math.Max(twist == 0 ? 1 : (int)Math.Ceiling(Math.Abs(twist) / 360 * 24 + 1), 1), 2000);
                if (a.Get(4, "slices") is null && twist != 0)
                {
                    slices = Math.Max(slices, Math.Min((int)Math.Ceiling(Math.Abs(twist) / 360 * Frags(MaxRadius(shape), env, a)), 2000));
                }

                return One(ScadGeo.LinearExtrude(shape, h, center, twist, slices, sx, sy, _options.Cancellation));
            }

            case "rotate_extrude":
            {
                var geo = Unite(ChildGroups(inst.Child, env));
                if (geo is not Shape shape)
                {
                    return [];
                }

                var angle = ScadValue.Num(a.Get(0, "angle")) ?? 360;
                var n = Frags(MaxRadius(shape), env, a);
                return One(ScadGeo.RotateExtrude(shape, Math.Clamp(angle, 0.001, 360), n, _options.Cancellation));
            }

            case "children":
            {
                if (!FindChildren(env, out var kids, out var kidEnv))
                {
                    return [];
                }

                var which = a.Get(0, "index");
                var result = new List<Geo>();
                if (which is null)
                {
                    foreach (var k in kids)
                    {
                        result.AddRange(Exec(k, kidEnv));
                    }
                }
                else
                {
                    foreach (var i in Iterate(which))
                    {
                        if (ScadValue.Num(i) is { } idx && idx >= 0 && idx < kids.Length)
                        {
                            result.AddRange(Exec(kids[(int)idx], kidEnv));
                        }
                    }
                }

                return result;
            }

            case "echo":
                Echo.Add(string.Join(", ", a.Pos.Select(ScadValue.Str)));
                return ChildGroups(inst.Child, env);
            case "assert":
                return ChildGroups(inst.Child, env);
            case "projection":
                Warn("projection() is not computed.");
                return [];
            case "text":
                Warn("text() is not drawn (no font outlines).");
                return [];
            case "import" or "surface":
                Warn($"{inst.Name}() reads an external file and is not drawn.");
                return [];
            case "intersection_for" or "for":
                return [];
            default:
                Warn($"Unknown module '{inst.Name}' was skipped.");
                return [];
        }
    }

    private static bool FindChildren(ScadEnv env, out Stmt[] kids, out ScadEnv kidEnv)
    {
        for (var e = env; e is not null; e = e.Dyn)
        {
            if (e.Children is { } c)
            {
                kids = c.Items;
                kidEnv = c.Env;
                return true;
            }
        }

        kids = [];
        kidEnv = env;
        return false;
    }

    private static double MaxRadius(Shape s)
    {
        double r = 0;
        foreach (var region in s.Regions)
        {
            foreach (var loop in region)
            {
                foreach (var p in loop)
                {
                    r = Math.Max(r, Math.Abs(p.X));
                }
            }
        }

        return r;
    }

    private static D3 BoundsOf(Geo g)
    {
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        void Add(double x, double y, double z)
        {
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
            minZ = Math.Min(minZ, z);
            maxZ = Math.Max(maxZ, z);
        }

        if (g is Solid s)
        {
            foreach (var p in s.Polys)
            {
                foreach (var v in p.Vertices)
                {
                    Add(v.X, v.Y, v.Z);
                }
            }
        }
        else if (g is Shape sh)
        {
            foreach (var l in sh.Regions.SelectMany(r => r))
            {
                foreach (var p in l)
                {
                    Add(p.X, p.Y, 0);
                }
            }
        }

        return minX > maxX ? default : new D3(maxX - minX, maxY - minY, maxZ - minZ);
    }

    private Geo? Transformed(Instance inst, ScadEnv env, M4 m)
    {
        var geo = Unite(ChildGroups(inst.Child, env));
        return geo is null ? null : Move(geo, m);
    }

    private double Radius(CallArgs a, int index, string r, string d, double fallback)
    {
        if (a.Named.TryGetValue(d, out var dv) && ScadValue.Num(dv) is { } dd)
        {
            return dd / 2;
        }

        return ScadValue.Num(a.Get(index, r)) ?? fallback;
    }

    private int ColorIndex(object? c, double? alpha)
    {
        float r = 0.8f, g = 0.8f, b = 0.8f, al = 1f;
        if (ScadValue.Doubles(c, 3) is { } d)
        {
            r = (float)d[0];
            g = (float)d[1];
            b = (float)d[2];
            al = d.Length > 3 ? (float)d[3] : 1f;
        }
        else if (c is string name && ScadColors.TryParse(name, out var rgb))
        {
            (r, g, b) = rgb;
        }
        else
        {
            Warn("An unknown colour was replaced by grey.");
        }

        if (alpha is { } a2)
        {
            al = (float)a2;
        }

        var key = (r, g, b, al);
        if (!_colorIds.TryGetValue(key, out var id))
        {
            id = Materials.Count;
            _colorIds[key] = id;
            Materials.Add(new ModelMaterial
            {
                Name = c as string,
                BaseColor = new System.Numerics.Vector4(Math.Clamp(r, 0, 1), Math.Clamp(g, 0, 1), Math.Clamp(b, 0, 1), Math.Clamp(al, 0, 1)),
            });
        }

        return id;
    }

    // ---------------------------------------------------------------- expressions

    public object? Eval(Expr e, ScadEnv env)
    {
        Step();
        switch (e)
        {
            case Lit l:
                return l.Value;
            case Ident id:
                return env.TryGet(id.Name, out var v) ? v : id.Name == "PI" ? _pi : null;
            case VecExpr vec:
            {
                var list = new List<object?>(vec.Items.Length);
                foreach (var item in vec.Items)
                {
                    Emit(item, env, list);
                }

                return list;
            }

            case RangeExpr r:
            {
                var s = ScadValue.Num(Eval(r.Start, env));
                var en = ScadValue.Num(Eval(r.End, env));
                var st = r.Step is null ? 1 : ScadValue.Num(Eval(r.Step, env));
                return s is null || en is null || st is null ? null : new ScadRange(s.Value, st.Value, en.Value);
            }

            case Unary u:
            {
                var x = Eval(u.Operand, env);
                return u.Op switch
                {
                    "!" => !ScadValue.Truthy(x),
                    "-" => Negate(x),
                    _ => x,
                };
            }

            case Binary b:
                return Binary(b, env);
            case Ternary t:
                return ScadValue.Truthy(Eval(t.Cond, env)) ? Eval(t.Then, env) : Eval(t.Else, env);
            case IndexExpr ix:
            {
                var target = Eval(ix.Target, env);
                var index = ScadValue.Num(Eval(ix.Index, env));
                if (index is null || index < 0)
                {
                    return null;
                }

                var i = (int)Math.Min(index.Value, int.MaxValue - 1);
                return target switch
                {
                    List<object?> l2 => i < l2.Count ? l2[i] : null,
                    string s => i < s.Length ? s[i].ToString() : null,
                    _ => null,
                };
            }

            case MemberExpr m:
            {
                var target = Eval(m.Target, env);
                var i = m.Name switch { "x" => 0, "y" => 1, "z" => 2, _ => -1 };
                return target is List<object?> l3 && i >= 0 && i < l3.Count ? l3[i] : null;
            }

            case LetExpr le:
            {
                var scope = new ScadEnv(env, env);
                foreach (var a in le.Assigns)
                {
                    scope.Vars[a.Name!] = Eval(a.Value, scope);
                }

                return Eval(le.Body, scope);
            }

            case FuncLit fl:
                return new FuncVal(fl.Params, fl.Body, env);
            case CallExpr call:
                return Call(call, env);
            case LcFor or LcIf or LcEach or LcCFor:
            {
                var list = new List<object?>();
                Emit(e, env, list);
                return list;
            }
        }

        return null;
    }

    private void Emit(Expr e, ScadEnv env, List<object?> output)
    {
        Step();
        if (output.Count > 50_000_000)
        {
            throw new ModelFormatException("An OpenSCAD list is too long.");
        }

        switch (e)
        {
            case LcFor f:
                EmitFor(f.Vars, 0, env, f.Body, output);
                break;
            case LcCFor c:
            {
                var scope = new ScadEnv(env, env);
                foreach (var a in c.Init)
                {
                    scope.Vars[a.Name!] = Eval(a.Value, scope);
                }

                var guard = 0;
                while (ScadValue.Truthy(Eval(c.Cond, scope)))
                {
                    if (++guard > 5_000_000)
                    {
                        throw new ModelFormatException("An OpenSCAD list comprehension does not end.");
                    }

                    Emit(c.Body, scope, output);
                    var updates = c.Step.Select(s => (s.Name!, Eval(s.Value, scope))).ToList();
                    foreach (var (n, v) in updates)
                    {
                        scope.Vars[n] = v;
                    }
                }

                break;
            }

            case LcIf i:
                if (ScadValue.Truthy(Eval(i.Cond, env)))
                {
                    Emit(i.Then, env, output);
                }
                else if (i.Else is not null)
                {
                    Emit(i.Else, env, output);
                }

                break;
            case LcEach each:
            {
                var inner = new List<object?>();
                Emit(each.Items, env, inner);
                foreach (var x in inner)
                {
                    if (x is List<object?> or ScadRange)
                    {
                        output.AddRange(Iterate(x));
                    }
                    else
                    {
                        output.Add(x);
                    }
                }

                break;
            }

            case LetExpr le:
            {
                var scope = new ScadEnv(env, env);
                foreach (var a in le.Assigns)
                {
                    scope.Vars[a.Name!] = Eval(a.Value, scope);
                }

                Emit(le.Body, scope, output);
                break;
            }

            default:
                output.Add(Eval(e, env));
                break;
        }
    }

    private void EmitFor(Arg[] vars, int level, ScadEnv env, Expr body, List<object?> output)
    {
        if (level == vars.Length)
        {
            Emit(body, env, output);
            return;
        }

        foreach (var value in Iterate(Eval(vars[level].Value, env)))
        {
            var scope = new ScadEnv(env, env);
            scope.Vars[vars[level].Name!] = value;
            EmitFor(vars, level + 1, scope, body, output);
        }
    }

    private static object? Negate(object? x) => x switch
    {
        double d => -d,
        List<object?> l => l.Select(Negate).ToList(),
        _ => null,
    };

    private object? Binary(Binary b, ScadEnv env)
    {
        if (b.Op == "&&")
        {
            return ScadValue.Truthy(Eval(b.Left, env)) && ScadValue.Truthy(Eval(b.Right, env));
        }

        if (b.Op == "||")
        {
            return ScadValue.Truthy(Eval(b.Left, env)) || ScadValue.Truthy(Eval(b.Right, env));
        }

        var l = Eval(b.Left, env);
        var r = Eval(b.Right, env);
        switch (b.Op)
        {
            case "==":
                return ScadValue.Equal(l, r);
            case "!=":
                return !ScadValue.Equal(l, r);
            case "<":
                return ScadValue.Compare(l, r) is { } c1 && c1 < 0;
            case ">":
                return ScadValue.Compare(l, r) is { } c2 && c2 > 0;
            case "<=":
                return ScadValue.Compare(l, r) is { } c3 && c3 <= 0;
            case ">=":
                return ScadValue.Compare(l, r) is { } c4 && c4 >= 0;
            case "+":
                return Add(l, r, 1);
            case "-":
                return Add(l, r, -1);
            case "*":
                return Multiply(l, r);
            case "/":
                return l is double x && r is double y ? x / y : l is List<object?> lv && r is double yy ? lv.Select(i => i is double d ? d / yy : (object?)null).ToList() : null;
            case "%":
                return l is double p && r is double q ? p % q : null;
        }

        return null;
    }

    private static object? Add(object? l, object? r, int sign)
    {
        if (l is double a && r is double b)
        {
            return a + sign * b;
        }

        if (l is List<object?> x && r is List<object?> y && x.Count == y.Count)
        {
            var result = new List<object?>(x.Count);
            for (var i = 0; i < x.Count; i++)
            {
                result.Add(Add(x[i], y[i], sign));
            }

            return result;
        }

        return null;
    }

    private static object? Multiply(object? l, object? r)
    {
        switch (l, r)
        {
            case (double a, double b):
                return a * b;
            case (double s, List<object?> v):
                return v.Select(i => Multiply(s, i)).ToList();
            case (List<object?> v, double s):
                return v.Select(i => Multiply(i, s)).ToList();
            case (List<object?> x, List<object?> y):
                if (x.All(i => i is double) && y.All(i => i is double))
                {
                    return x.Count == y.Count ? x.Select((v, i) => (double)v! * (double)y[i]!).Sum() : null;
                }

                if (x.All(i => i is List<object?>))
                {
                    if (y.All(i => i is double))
                    {
                        return x.Select(row => Multiply(row, y)).ToList();
                    }

                    // matrix * matrix
                    var cols = y.Count > 0 && y[0] is List<object?> first ? first.Count : 0;
                    return x.Select(row => (object?)Enumerable.Range(0, cols).Select(j => Multiply(row, y.Select(yr => (yr as List<object?>)?.ElementAtOrDefault(j)).ToList())).ToList()).ToList();
                }

                if (y.All(i => i is List<object?>) && x.All(i => i is double))
                {
                    var cols = y.Count > 0 && y[0] is List<object?> first ? first.Count : 0;
                    return Enumerable.Range(0, cols).Select(j => Multiply(x, y.Select(yr => (yr as List<object?>)?.ElementAtOrDefault(j)).ToList())).ToList();
                }

                return null;
        }

        return null;
    }

    private object? Call(CallExpr call, ScadEnv env)
    {
        var args = BindArgs(call.Args, env);
        if (call.Callee is Ident id)
        {
            if (env.FindFunction(id.Name) is { } found)
            {
                return CallUser(found.Def.Params, found.Def.Body, found.Env, args, env);
            }

            if (env.TryGet(id.Name, out var held) && held is FuncVal fv)
            {
                return CallUser(fv.Params, fv.Body, fv.Env, args, env);
            }

            return ScadBuiltins.Call(id.Name, args.Pos, args.Named, this);
        }

        return Eval(call.Callee, env) is FuncVal fn ? CallUser(fn.Params, fn.Body, fn.Env, args, env) : null;
    }

    private object? CallUser(Param[] ps, Expr body, ScadEnv defEnv, CallArgs args, ScadEnv callEnv)
    {
        if (++_depth > 150)
        {
            _depth--;
            throw new ModelFormatException("The OpenSCAD program recurses too deeply.");
        }

        try
        {
            var scope = new ScadEnv(defEnv, callEnv);
            var callArgs = args;
            Bind(ps, callArgs, scope);
            return Eval(body, scope);
        }
        finally
        {
            _depth--;
        }
    }

    internal void NoteRandom() => Warn("rands() uses a fixed seed, so its values differ from OpenSCAD's.");

    internal long NextCancelTick() => ++_steps;

    internal CancellationToken Token => _options.Cancellation;
}
