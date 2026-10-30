using System.Globalization;
using System.Numerics;
using XRay.ThreeD.Formats.Step;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.Parasolid;

/// <summary>
/// Builds a <see cref="Model3D"/> from the nodes of a transmit file: the assembly tree (instances with their transforms), bodies, regions, shells, faces,
/// loops, fins, edges, vertices, the planes, cylinders, cones, spheres, tori and B-surfaces the faces lie on, and the line, circle, ellipse, B-curve and
/// intersection-chart curves of the edges. Faces are tessellated with the machinery of the STEP and ACIS readers; colour and name attributes are applied.
/// </summary>
internal sealed class XtBuilder
{
    private const int MaxChain = 20_000_000;

    private readonly Dictionary<int, XtNode> _nodes;
    private readonly LoadOptions _options;
    private readonly Model3D _model;
    private readonly CancellationToken _cancel;
    private readonly double _linear;
    private readonly double _angular;
    private readonly SurfaceFaceTessellator _tessellator;
    private readonly Dictionary<Vector4, int> _materials = new();
    private readonly Dictionary<int, MeshData?> _bodyMeshes = new();
    private readonly Dictionary<string, int> _skipped = new(StringComparer.Ordinal);
    private long _faceTick;

    public XtBuilder(Dictionary<int, XtNode> nodes, LoadOptions options, Model3D model)
    {
        _nodes = nodes;
        _options = options;
        _model = model;
        _cancel = options.Cancellation;
        double min = double.MaxValue, max = double.MinValue;
        foreach (var n in nodes.Values)
        {
            if (n.Def.Type == 29)
            {
                var (x, y, z) = n.Vec("pvec");
                foreach (var c in new[] { x, y, z })
                {
                    if (double.IsFinite(c))
                    {
                        min = Math.Min(min, c);
                        max = Math.Max(max, c);
                    }
                }
            }
        }

        var extent = max > min ? max - min : 1;
        _linear = Math.Max(options.LinearDeflection, 1e-6) * extent;
        _angular = Math.Max(options.AngularDeflection, 0.02);
        _tessellator = new SurfaceFaceTessellator(_angular, _cancel, model.Warnings);
    }

    public int BodyCount { get; private set; }

    public int FaceCount { get; private set; }

    public void Build()
    {
        var root = Get(1);
        var visited = new HashSet<int>();
        switch (root?.Def.Type)
        {
            case 10:
                AddAssembly(root, null, Matrix4x4.Identity, visited, 0);
                break;
            case 12:
                AddBody(root, null, Matrix4x4.Identity);
                break;
            case 74:
                // An array of parts: the entries of its chain of pointer blocks.
                for (var block = root; block is not null && visited.Add(block.Index); block = Get(block.Ptr("next_block")))
                {
                    foreach (var entry in block.LongArray)
                    {
                        if (Get((int)entry) is { } part)
                        {
                            AddPart(part, null, Matrix4x4.Identity, visited, 0);
                        }
                    }
                }

                break;
            default:
                foreach (var body in _nodes.Values.Where(n => n.Def.Type == 12).OrderBy(n => n.Index))
                {
                    AddBody(body, null, Matrix4x4.Identity);
                }

                break;
        }

        if (_model.Roots.Count == 0)
        {
            foreach (var body in _nodes.Values.Where(n => n.Def.Type == 12).OrderBy(n => n.Index))
            {
                AddBody(body, null, Matrix4x4.Identity);
            }
        }

        foreach (var (what, count) in _skipped.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            _model.Warnings.Add($"{count} face(s) on {what} were not tessellated.");
        }
    }

    private XtNode? Get(long index) => index > 0 && index <= int.MaxValue && _nodes.TryGetValue((int)index, out var n) ? n : null;

    private XtNode? Get(long index, int type) => Get(index) is { } n && n.Def.Type == type ? n : null;

    // ---- assembly tree -----------------------------------------------------------------------------------------

    private void AddPart(XtNode part, SceneNode? parent, Matrix4x4 transform, HashSet<int> path, int depth)
    {
        if (part.Def.Type == 10)
        {
            AddAssembly(part, parent, transform, path, depth);
        }
        else if (part.Def.Type == 12)
        {
            AddBody(part, parent, transform);
        }
    }

    private void AddAssembly(XtNode assembly, SceneNode? parent, Matrix4x4 transform, HashSet<int> path, int depth)
    {
        if (depth > 64 || !path.Add(assembly.Index))
        {
            _model.Warnings.Add("A Parasolid assembly refers to itself or is nested too deeply; the cycle was cut.");
            return;
        }

        var node = new SceneNode(NameOf(assembly) ?? "Assembly") { Transform = transform };
        Attach(node, parent);
        var guard = 0;
        for (var instance = Get(assembly.Ptr("sub_instance"), 11); instance is not null && guard++ < MaxChain; instance = Get(instance.Ptr("next_in_part"), 11))
        {
            Cancel.Every(_cancel, guard);
            if (Get(instance.Ptr("part")) is not { } part)
            {
                continue;
            }

            var local = Get(instance.Ptr("transform"), 100) is { } tf ? ToMatrix(tf) : Matrix4x4.Identity;
            if (part.Def.Type == 10)
            {
                AddAssembly(part, node, local, path, depth + 1);
            }
            else
            {
                AddBody(part, node, local, NameOf(instance));
            }
        }

        path.Remove(assembly.Index);
    }

    private void Attach(SceneNode node, SceneNode? parent)
    {
        if (parent is null)
        {
            _model.Roots.Add(node);
        }
        else
        {
            parent.Children.Add(node);
        }
    }

    /// <summary>The transform of an instance: the 3 x 3 rotation (row major, applied to column vectors), a scale and a translation, in the model's row-vector convention.</summary>
    private static Matrix4x4 ToMatrix(XtNode tf)
    {
        var r = tf.DoubleArray("rotation");
        var (tx, ty, tz) = tf.Vec("translation");
        var s = tf.Dbl("scale");
        s = double.IsFinite(s) && s != 0 ? s : 1;
        double R(int i, int j) => double.IsFinite(r[i * 3 + j]) ? r[i * 3 + j] * s : 0;
        return new Matrix4x4(
            (float)R(0, 0), (float)R(1, 0), (float)R(2, 0), 0,
            (float)R(0, 1), (float)R(1, 1), (float)R(2, 1), 0,
            (float)R(0, 2), (float)R(1, 2), (float)R(2, 2), 0,
            double.IsFinite(tx) ? (float)tx : 0, double.IsFinite(ty) ? (float)ty : 0, double.IsFinite(tz) ? (float)tz : 0, 1);
    }

    private void AddBody(XtNode body, SceneNode? parent, Matrix4x4 transform, string? instanceName = null)
    {
        if (!_bodyMeshes.TryGetValue(body.Index, out var mesh))
        {
            mesh = BuildBodyMesh(body);
            _bodyMeshes[body.Index] = mesh;
        }

        var name = instanceName ?? NameOf(body) ?? "Body " + (++BodyCount).ToString(CultureInfo.InvariantCulture);
        if (mesh is null)
        {
            return;
        }

        var node = new SceneNode(name) { Mesh = mesh, Transform = transform };
        Attach(node, parent);
    }

    // ---- attributes --------------------------------------------------------------------------------------------

    private IEnumerable<(int TypeId, XtNode Attribute)> Attributes(XtNode owner)
    {
        if (!owner.Has("attributes_groups"))
        {
            yield break;
        }

        var guard = 0;
        for (var a = Get(owner.Ptr("attributes_groups")); a is not null && guard++ < 10_000; a = Get(a.Ptr("next")))
        {
            if (a.Def.Type == 81 && Get(a.Ptr("definition"), 80) is { } def)
            {
                yield return ((int)def.Int("type_id"), a);
            }
            else if (a.Def.Type != 81 && a.Def.Type != 91)
            {
                yield break;
            }
        }
    }

    private IEnumerable<XtNode> Values(XtNode attribute) => attribute.LongArray.Select(i => Get(i)).Where(n => n is not null).Select(n => n!);

    private string? NameOf(XtNode node)
    {
        foreach (var (typeId, a) in Attributes(node))
        {
            if (typeId != 8017)
            {
                continue;
            }

            foreach (var v in Values(a))
            {
                if (v.Def.Type == 84 && v.Variable is string s && s.Length > 0)
                {
                    return s;
                }

                if (v.Def.Type == 98 && v.Variable is long[] units && units.Length > 0)
                {
                    return new string(units.Select(u => (char)u).ToArray());
                }
            }
        }

        return null;
    }

    private Vector4? ColourOf(XtNode node)
    {
        foreach (var (typeId, a) in Attributes(node))
        {
            if (typeId != 8001)
            {
                continue;
            }

            foreach (var v in Values(a))
            {
                if (v.Def.Type == 83 && v.Variable is double[] { Length: >= 3 } c && double.IsFinite(c[0]) && double.IsFinite(c[1]) && double.IsFinite(c[2]))
                {
                    return new Vector4((float)Math.Clamp(c[0], 0, 1), (float)Math.Clamp(c[1], 0, 1), (float)Math.Clamp(c[2], 0, 1), 1);
                }
            }
        }

        return null;
    }

    private int Material(Vector4 colour)
    {
        if (!_materials.TryGetValue(colour, out var id))
        {
            id = _model.Materials.Count;
            _model.Materials.Add(new ModelMaterial { BaseColor = colour });
            _materials[colour] = id;
        }

        return id;
    }

    // ---- bodies and faces --------------------------------------------------------------------------------------

    private MeshData? BuildBodyMesh(XtNode body)
    {
        var assembler = new FaceMeshAssembler(_options.MaxTriangles, "Parasolid");
        var bodyColour = ColourOf(body);
        var seen = new HashSet<int>();
        var guard = 0;
        for (var region = Get(body.Ptr("region"), 19); region is not null && guard++ < MaxChain; region = Get(region.Ptr("next"), 19))
        {
            var shells = 0;
            for (var shell = Get(region.Ptr("shell"), 13); shell is not null && shells++ < MaxChain; shell = Get(shell.Ptr("next"), 13))
            {
                AddChain(Get(shell.Ptr("face"), 14), "next", seen, assembler, bodyColour);
                AddChain(Get(shell.Ptr("front_face"), 14), "next_front", seen, assembler, bodyColour);
            }
        }

        // Sheet and wire bodies hang their faces off the body's shell list as well.
        for (var shell = Get(body.Ptr("shell"), 13); shell is not null && guard++ < MaxChain; shell = Get(shell.Ptr("next"), 13))
        {
            AddChain(Get(shell.Ptr("face"), 14), "next", seen, assembler, bodyColour);
            AddChain(Get(shell.Ptr("front_face"), 14), "next_front", seen, assembler, bodyColour);
        }

        var mesh = assembler.ToMesh(NameOf(body) ?? "body");
        if (mesh is not null)
        {
            _model.Meshes.Add(mesh);
        }

        return mesh;
    }

    private void AddChain(XtNode? first, string next, HashSet<int> seen, FaceMeshAssembler assembler, Vector4? bodyColour)
    {
        var guard = 0;
        for (var face = first; face is not null && guard++ < MaxChain; face = Get(face.Ptr(next), 14))
        {
            Cancel.Every(_cancel, _faceTick++);
            if (!seen.Add(face.Index))
            {
                continue;
            }

            FaceCount++;
            var mesh = TessellateFace(face);
            if (mesh is null)
            {
                continue;
            }

            var colour = ColourOf(face) ?? bodyColour;
            assembler.Add(mesh, colour is { } c ? Material(c) : -1);
        }
    }

    private FaceMesh? TessellateFace(XtNode face)
    {
        var label = "Parasolid face " + face.Index.ToString(CultureInfo.InvariantCulture);
        var loops = new List<FaceLoop>();
        var guard = 0;
        for (var loop = Get(face.Ptr("loop"), 15); loop is not null && guard++ < 100_000; loop = Get(loop.Ptr("next"), 15))
        {
            if (LoopPoints(loop) is { Count: >= 2 } points)
            {
                loops.Add(new FaceLoop(points));
            }
        }

        var surfaceNode = Get(face.Ptr("surface"));
        var normalIsClass = true;
        Surface? surface = null;
        if (surfaceNode is not null)
        {
            surface = MakeSurface(surfaceNode, out normalIsClass);
            if (surface is null)
            {
                _skipped[surfaceNode.Def.Name] = _skipped.GetValueOrDefault(surfaceNode.Def.Name) + 1;
                if (loops.Count == 0)
                {
                    return null;
                }
            }
        }

        // The face's normal is the surface's (a '+' face) or its opposite. It points out of the region of the shell behind the face; a face that
        // borders a solid only on its front is drawn the other way round, so that solids face outwards.
        var faceNormalIsClass = (face.Char("sense") == '+') == normalIsClass;
        var sameSense = SolidOnlyInFront(face) ? !faceNormalIsClass : faceNormalIsClass;

        if (loops.Count == 0)
        {
            return surface is SphereSurface or TorusSurface ? WholeSurface(surface, sameSense) : null;
        }

        return _tessellator.Tessellate(surface, loops, sameSense, label, approximatedWarning: surface is null);
    }

    private bool SolidOnlyInFront(XtNode face)
    {
        bool Solid(long shell) => Get(shell, 13) is { } s && Get(s.Ptr("region"), 19) is { } r && r.Char("type") == 'S';
        return Solid(face.Ptr("front_shell")) && !Solid(face.Ptr("shell"));
    }

    private FaceMesh WholeSurface(Surface surface, bool sameSense)
    {
        var mesh = new FaceMesh();
        var nu = Math.Clamp((int)Math.Ceiling(2 * Math.PI / _angular), 8, 256);
        var nv = surface is SphereSurface ? Math.Clamp(nu / 2, 4, 128) : nu;
        var (v0, v1) = surface is SphereSurface ? (-Math.PI / 2, Math.PI / 2) : (0.0, 2 * Math.PI);
        for (var j = 0; j <= nv; j++)
        {
            for (var i = 0; i <= nu; i++)
            {
                var u = 2 * Math.PI * (i % nu) / nu;
                var v = v0 + (v1 - v0) * j / nv;
                var n = surface.Normal(u, v);
                mesh.Positions.Add(surface.At(u, v));
                mesh.Normals.Add(sameSense ? n : -n);
            }
        }

        for (var j = 0; j < nv; j++)
        {
            for (var i = 0; i < nu; i++)
            {
                var a = j * (nu + 1) + i;
                var b = a + 1;
                var d = a + nu + 1;
                var e = d + 1;
                if (sameSense)
                {
                    mesh.Indices.AddRange([a, b, e, a, e, d]);
                }
                else
                {
                    mesh.Indices.AddRange([a, e, b, a, d, e]);
                }
            }
        }

        return mesh;
    }

    // ---- topology to boundary points ---------------------------------------------------------------------------

    private V3? VertexPoint(long vertex)
    {
        if (Get(vertex, 18) is not { } v || Get(v.Ptr("point"), 29) is not { } p)
        {
            return null;
        }

        var (x, y, z) = p.Vec("pvec");
        return double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z) ? new V3(x, y, z) : null;
    }

    private List<V3>? LoopPoints(XtNode loop)
    {
        var first = Get(loop.Ptr("fin"), 17);
        if (first is null)
        {
            return null;
        }

        var points = new List<V3>();
        var fin = first;
        var guard = 0;
        do
        {
            Cancel.Every(_cancel, guard);
            var segment = FinPoints(fin);
            if (segment is null)
            {
                return null;
            }

            for (var i = 0; i + 1 < segment.Count; i++)
            {
                points.Add(segment[i]);
            }

            if (segment.Count == 1)
            {
                points.Add(segment[0]);
            }

            fin = Get(fin.Ptr("forward"), 17);
        }
        while (fin is not null && !ReferenceEquals(fin, first) && guard++ < 1_000_000);

        return points;
    }

    /// <summary>The points a fin traverses, from where it starts to its own vertex (the vertex at its end), along the edge's curve.</summary>
    private List<V3>? FinPoints(XtNode fin)
    {
        if (Get(fin.Ptr("edge"), 16) is not { } edge || Get(edge.Ptr("fin"), 17) is not { } edgeFin)
        {
            // An isolated vertex: a loop of one fin without an edge.
            return VertexPoint(fin.Ptr("vertex")) is { } only ? [only] : null;
        }

        // The edge runs from the vertex of its other fin (backward) to the vertex of its own fin (forward).
        var end = VertexPoint(edgeFin.Ptr("vertex"));
        var start = Get(edgeFin.Ptr("other"), 17) is { } other ? VertexPoint(other.Ptr("vertex")) : null;
        if (start is null || end is null)
        {
            // A ring edge has no vertices: a closed circle or ellipse is walked from parameter zero all the way round.
            return RingPoints(edge, fin.Char("sense") == '+');
        }

        var along = EdgePoints(edge, start.Value, end.Value);
        var positive = fin.Char("sense") == '+';
        if (!positive)
        {
            along = [.. along.AsEnumerable().Reverse()];
        }

        return along;
    }

    private List<V3>? RingPoints(XtNode edge, bool positive)
    {
        var curve = Get(edge.Ptr("curve"));
        if (curve is not { Def.Type: 31 or 32 })
        {
            return null;
        }

        var (cx, cy, cz) = curve.Vec("centre");
        var (nx, ny, nz) = curve.Vec("normal");
        var (xx, xy, xz) = curve.Vec("x_axis");
        var a = curve.Def.Type == 31 ? curve.Dbl("radius") : curve.Dbl("major_radius");
        var b = curve.Def.Type == 31 ? a : curve.Dbl("minor_radius");
        var c = new EllipseCurve { F = Frame.From(new V3(cx, cy, cz), new V3(nx, ny, nz), new V3(xx, xy, xz)), A = a, B = b };
        var forward = curve.Char("sense") != '-' == positive;
        var points = c.Sample(0, forward ? 2 * Math.PI : -2 * Math.PI, Math.Max(_linear, 1e-9), _angular);
        return points;
    }

    private List<V3> EdgePoints(XtNode edge, V3 start, V3 end)
    {
        var curve = Get(edge.Ptr("curve"));
        for (var depth = 0; curve is { Def.Type: 133 } && depth < 8; depth++)
        {
            curve = Get(curve.Ptr("basis_curve"));
        }

        if (curve is null)
        {
            return [start, end];
        }

        var sense = !curve.Has("sense") || curve.Char("sense") != '-';
        switch (curve.Def.Type)
        {
            case 31:
            {
                var (cx, cy, cz) = curve.Vec("centre");
                var (nx, ny, nz) = curve.Vec("normal");
                var (xx, xy, xz) = curve.Vec("x_axis");
                var radius = curve.Dbl("radius");
                var c = new EllipseCurve { F = Frame.From(new V3(cx, cy, cz), new V3(nx, ny, nz), new V3(xx, xy, xz)), A = radius, B = radius };
                return Sweep(c, start, end, sense);
            }

            case 32:
            {
                var (cx, cy, cz) = curve.Vec("centre");
                var (nx, ny, nz) = curve.Vec("normal");
                var (xx, xy, xz) = curve.Vec("x_axis");
                var c = new EllipseCurve { F = Frame.From(new V3(cx, cy, cz), new V3(nx, ny, nz), new V3(xx, xy, xz)), A = curve.Dbl("major_radius"), B = curve.Dbl("minor_radius") };
                return Sweep(c, start, end, sense);
            }

            case 134:
                if (MakeSplineCurve(curve) is { } spline)
                {
                    return Sweep(spline, start, end, sense);
                }

                break;
            case 38:
                if (Get(curve.Ptr("chart"), 40) is { Variable: double[] { Length: >= 6 } chart })
                {
                    return ChartPoints(chart, start, end);
                }

                break;
        }

        return [start, end];
    }

    private List<V3> Sweep(Curve curve, V3 start, V3 end, bool sense)
    {
        var ta = curve.Project(start);
        var tb = curve.Project(end);
        var period = curve.Period;
        if (period > 0)
        {
            if (sense)
            {
                while (tb <= ta + 1e-9)
                {
                    tb += period;
                }
            }
            else
            {
                while (tb >= ta - 1e-9)
                {
                    tb -= period;
                }
            }
        }

        var points = curve.Sample(ta, tb, Math.Max(_linear, 1e-9), _angular);
        points[0] = start;
        points[^1] = end;
        return points;
    }

    private static List<V3> ChartPoints(double[] chart, V3 start, V3 end)
    {
        var all = new List<V3>(chart.Length / 3);
        for (var i = 0; i + 2 < chart.Length; i += 3)
        {
            all.Add(new V3(chart[i], chart[i + 1], chart[i + 2]));
        }

        int Nearest(V3 p)
        {
            var best = 0;
            var d = double.MaxValue;
            for (var i = 0; i < all.Count; i++)
            {
                var l = (all[i] - p).Length;
                if (l < d)
                {
                    d = l;
                    best = i;
                }
            }

            return best;
        }

        var a = Nearest(start);
        var b = Nearest(end);
        var points = new List<V3> { start };
        if (a <= b)
        {
            for (var i = a; i <= b; i++)
            {
                points.Add(all[i]);
            }
        }
        else
        {
            for (var i = a; i >= b; i--)
            {
                points.Add(all[i]);
            }
        }

        points.Add(end);
        return points;
    }

    // ---- geometry ----------------------------------------------------------------------------------------------

    private static V3 Vec(XtNode n, string name)
    {
        var (x, y, z) = n.Vec(name);
        return new V3(double.IsFinite(x) ? x : 0, double.IsFinite(y) ? y : 0, double.IsFinite(z) ? z : 0);
    }

    /// <summary>The surface of a face and whether the node's normal is the one <see cref="Surface.Normal"/> reports (the cone's is the opposite).</summary>
    private Surface? MakeSurface(XtNode s, out bool normalIsClass)
    {
        normalIsClass = true;
        switch (s.Def.Type)
        {
            case 50:
                return new PlaneSurface { F = Frame.From(Vec(s, "pvec"), Vec(s, "normal"), Vec(s, "x_axis")) };
            case 51:
                return new CylinderSurface { F = Frame.From(Vec(s, "pvec"), Vec(s, "axis"), Vec(s, "x_axis")), R = s.Dbl("radius") };
            case 52:
            {
                // R(u, v) = P - vA + (X cos u + Y sin u)(r + v tan a), Y = A x X: the same point set as a cone along -A with the frame turned the other way.
                var radius = s.Dbl("radius");
                var sin = s.Dbl("sin_half_angle");
                var cos = s.Dbl("cos_half_angle");
                if (!double.IsFinite(sin) || !double.IsFinite(cos))
                {
                    return null;
                }

                normalIsClass = false;
                var frame = Frame.From(Vec(s, "pvec"), -Vec(s, "axis"), Vec(s, "x_axis"));
                return Math.Abs(sin) < 1e-12 ? new CylinderSurface { F = frame, R = radius } : new ConeSurface { F = frame, R = radius, Angle = Math.Atan2(sin, Math.Abs(cos)) };
            }

            case 53:
                return new SphereSurface { F = Frame.From(Vec(s, "centre"), Vec(s, "axis"), Vec(s, "x_axis")), R = s.Dbl("radius") };
            case 54:
                return new TorusSurface { F = Frame.From(Vec(s, "centre"), Vec(s, "axis"), Vec(s, "x_axis")), Major = s.Dbl("major_radius"), Minor = s.Dbl("minor_radius") };
            case 124:
                return Get(s.Ptr("nurbs"), 126) is { } nurbs ? MakeSplineSurface(nurbs) : null;
            default:
                return null;
        }
    }

    private static double[] KnotVector(XtNode? knots, XtNode? mult)
    {
        if (knots?.Variable is not double[] k || mult?.Variable is not long[] m)
        {
            return [];
        }

        var total = 0L;
        foreach (var x in m)
        {
            total += Math.Max(x, 0);
        }

        if (total > 1 << 24)
        {
            return [];
        }

        return BSpline.Expand(k, m.Select(x => (int)Math.Clamp(x, 0, 1 << 20)).ToList());
    }

    private Curve? MakeSplineCurve(XtNode curve)
    {
        if (Get(curve.Ptr("nurbs"), 136) is not { } n || Get(n.Ptr("bspline_vertices"), 45)?.Variable is not double[] vertices)
        {
            return null;
        }

        var dim = (int)n.Int("vertex_dim");
        var count = (int)n.Int("n_vertices");
        var degree = (int)n.Int("degree");
        var rational = n.Char("rational") == 'T';
        if (dim < (rational ? 4 : 3) || count <= degree || degree < 1 || degree > 64 || (long)count * dim > vertices.Length)
        {
            return null;
        }

        var knots = KnotVector(Get(n.Ptr("knots"), 128), Get(n.Ptr("knot_mult"), 127));
        if (knots.Length != count + degree + 1)
        {
            return null;
        }

        var control = new V3[count];
        var weights = new double[count];
        for (var i = 0; i < count; i++)
        {
            var w = rational ? vertices[i * dim + 3] : 1;
            if (!double.IsFinite(w) || w == 0)
            {
                return null;
            }

            control[i] = new V3(vertices[i * dim] / w, vertices[i * dim + 1] / w, vertices[i * dim + 2] / w);
            weights[i] = w;
        }

        return new SplineCurve { Degree = degree, Control = control, Weights = weights, Knots = knots };
    }

    private Surface? MakeSplineSurface(XtNode n)
    {
        if (Get(n.Ptr("bspline_vertices"), 45)?.Variable is not double[] vertices)
        {
            return null;
        }

        var dim = (int)n.Int("vertex_dim");
        int nu = (int)n.Int("n_u_vertices"), nv = (int)n.Int("n_v_vertices");
        int du = (int)n.Int("u_degree"), dv = (int)n.Int("v_degree");
        var rational = n.Char("rational") == 'T';
        if (dim < (rational ? 4 : 3) || nu <= du || nv <= dv || du < 1 || dv < 1 || du > 64 || dv > 64 || (long)nu * nv * dim > vertices.Length)
        {
            return null;
        }

        var ku = KnotVector(Get(n.Ptr("u_knots"), 128), Get(n.Ptr("u_knot_mult"), 127));
        var kv = KnotVector(Get(n.Ptr("v_knots"), 128), Get(n.Ptr("v_knot_mult"), 127));
        if (ku.Length != nu + du + 1 || kv.Length != nv + dv + 1)
        {
            return null;
        }

        var control = new V3[nu, nv];
        var weights = new double[nu, nv];
        for (var i = 0; i < nu; i++)
        {
            for (var j = 0; j < nv; j++)
            {
                // The grid is stored with v running fastest.
                var o = (i * nv + j) * dim;
                var w = rational ? vertices[o + 3] : 1;
                if (!double.IsFinite(w) || w == 0)
                {
                    return null;
                }

                control[i, j] = new V3(vertices[o] / w, vertices[o + 1] / w, vertices[o + 2] / w);
                weights[i, j] = w;
            }
        }

        return new SplineSurface { DegreeU = du, DegreeV = dv, Control = control, Weights = rational ? weights : null, KnotsU = ku, KnotsV = kv };
    }
}
