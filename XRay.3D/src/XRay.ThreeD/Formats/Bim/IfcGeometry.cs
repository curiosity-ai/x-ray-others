using System.Numerics;
using XRay.ThreeD.Formats.Step;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.Bim;

/// <summary>
/// The geometry half of the IFC reader: placements, profiles, and the representation items that can be turned into triangles
/// (triangulated and polygonal face sets, faceted B-reps and surface models, extruded area solids, mapped items, and the first
/// operand of boolean results). Everything else is reported through <see cref="Unsupported"/>.
/// </summary>
internal sealed class IfcGeometry
{
    private const int MaxDepth = 8;
    private readonly StepFile _f;
    private readonly LoadOptions _options;
    private readonly Dictionary<int, Matrix4x4> _placements = new();

    public IfcGeometry(StepFile file, LoadOptions options)
    {
        _f = file;
        _options = options;
    }

    /// <summary>Representation item types seen that this reader does not turn into triangles, with how often.</summary>
    public Dictionary<string, int> Unsupported { get; } = new(StringComparer.Ordinal);

    /// <summary>Notes about approximations (ignored voids, ignored boolean operands).</summary>
    public HashSet<string> Approximations { get; } = new(StringComparer.Ordinal);

    /// <summary>Triangle index ranges (start, count) with the material of the item that made them, since the last reset.</summary>
    public List<(int Start, int Count, int Material)> Runs { get; } = new();

    /// <summary>Material (index into the model's materials) of a styled representation item, by item id.</summary>
    public Dictionary<int, int> ItemMaterials { get; } = new();

    // ---- placements ------------------------------------------------------------------------------------------------

    public static Vector3 Point(StepEntity? e)
    {
        if (e is null || e.Arg(0).Items.Count == 0)
        {
            return Vector3.Zero;
        }

        var c = e.Arg(0).Items;
        return new Vector3((float)c[0].Num, c.Count > 1 ? (float)c[1].Num : 0, c.Count > 2 ? (float)c[2].Num : 0);
    }

    private Vector3? Direction(StepArg arg)
    {
        var e = _f.Get(arg);
        if (e is null || e.Arg(0).Items.Count == 0)
        {
            return null;
        }

        var c = e.Arg(0).Items;
        var v = new Vector3((float)c[0].Num, c.Count > 1 ? (float)c[1].Num : 0, c.Count > 2 ? (float)c[2].Num : 0);
        var length = v.Length();
        return float.IsFinite(length) && length > 1e-12f ? v / length : null;
    }

    private static Matrix4x4 FromAxes(Vector3 x, Vector3 y, Vector3 z, Vector3 origin) =>
        new(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, origin.X, origin.Y, origin.Z, 1);

    /// <summary>IfcAxis2Placement3D(Location, Axis, RefDirection) as a row-vector matrix.</summary>
    public Matrix4x4 Axis3(StepArg arg)
    {
        var e = _f.Get(arg);
        if (e is null)
        {
            return Matrix4x4.Identity;
        }

        var origin = Point(_f.Get(e.Arg(0)));
        var z = Direction(e.Arg(1)) ?? Vector3.UnitZ;
        var x = Direction(e.Arg(2)) ?? (MathF.Abs(z.X) > 0.9f ? Vector3.UnitY : Vector3.UnitX);
        x -= z * Vector3.Dot(x, z);
        if (!(x.Length() > 1e-9f))
        {
            x = MathF.Abs(z.X) > 0.9f ? Vector3.UnitY : Vector3.UnitX;
            x -= z * Vector3.Dot(x, z);
        }

        x = Vector3.Normalize(x);
        return FromAxes(x, Vector3.Cross(z, x), z, origin);
    }

    /// <summary>The world placement of an IfcLocalPlacement chain (cycles and chains longer than 256 stop the walk).</summary>
    public Matrix4x4 World(int placementId)
    {
        var chain = new List<StepEntity>();
        var seen = new HashSet<int>();
        var id = placementId;
        while (id >= 0 && seen.Add(id) && chain.Count < 256)
        {
            if (_placements.TryGetValue(id, out var known))
            {
                return Accumulate(known, chain);
            }

            var e = _f.Get(id);
            if (e is null || e.Type != "IFCLOCALPLACEMENT")
            {
                break;
            }

            chain.Add(e);
            id = e.Arg(0).ToRef;
        }

        return Accumulate(Matrix4x4.Identity, chain);
    }

    private Matrix4x4 Accumulate(Matrix4x4 baseMatrix, List<StepEntity> chain)
    {
        var world = baseMatrix;
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            world = Axis3(chain[i].Arg(1)) * world;
            _placements[chain[i].Id] = world;
        }

        return world;
    }

    /// <summary>The placement relative to its own PlacementRelTo, or null when the placement is not a local placement.</summary>
    public Matrix4x4? Relative(int placementId) => _f.Get(placementId) is { Type: "IFCLOCALPLACEMENT" } e ? Axis3(e.Arg(1)) : null;

    public int RelativeTo(int placementId) => _f.Get(placementId) is { Type: "IFCLOCALPLACEMENT" } e ? e.Arg(0).ToRef : -1;

    // ---- items -----------------------------------------------------------------------------------------------------

    /// <summary>Adds the triangles of a representation item. Returns false when the item has no supported geometry.</summary>
    public bool AddItem(int id, Matrix4x4 m, MeshAccumulator acc, int depth, int material = -1)
    {
        var e = _f.Get(id);
        if (e is null || depth > MaxDepth)
        {
            return false;
        }

        if (ItemMaterials.TryGetValue(id, out var styled))
        {
            material = styled;
        }

        var before = acc.Triangles.Count;
        var lines = acc.Lines.Count;
        var ok = true;
        var wrapper = false;
        switch (e.Type)
        {
            case "IFCTRIANGULATEDFACESET":
                TriangulatedFaceSet(e, m, acc);
                break;
            case "IFCPOLYGONALFACESET":
                PolygonalFaceSet(e, m, acc);
                break;
            case "IFCFACETEDBREP":
            case "IFCFACETEDBREPWITHVOIDS":
                Shell(_f.Get(e.Arg(0)), m, acc);
                break;
            case "IFCSHELLBASEDSURFACEMODEL":
            case "IFCFACEBASEDSURFACEMODEL":
                foreach (var shell in e.Arg(0).Items)
                {
                    Shell(_f.Get(shell), m, acc);
                }

                break;
            case "IFCEXTRUDEDAREASOLID":
            case "IFCEXTRUDEDAREASOLIDTAPERED":
                ok = Extrusion(e, m, acc);
                break;
            case "IFCMAPPEDITEM":
                wrapper = true;
                ok = Mapped(e, m, acc, depth, material);
                break;
            case "IFCBOOLEANRESULT":
            case "IFCBOOLEANCLIPPINGRESULT":
                Approximations.Add("Boolean operations (openings, clipping) are not applied; the first operand is drawn.");
                wrapper = true;
                ok = AddOperand(e.Arg(1), m, acc, depth + 1, material);
                break;
            case "IFCCSGSOLID":
                wrapper = true;
                ok = AddOperand(e.Arg(0), m, acc, depth + 1, material);
                break;
            case "IFCPOLYLINE" or "IFCINDEXEDPOLYCURVE" or "IFCCOMPOSITECURVE" or "IFCTRIMMEDCURVE" or "IFCCIRCLE" or "IFCLINE":
                return false; // curves are not drawn, and not an error either
            default:
                ok = false;
                break;
        }

        if (ok && acc.Triangles.Count == before && acc.Lines.Count == lines)
        {
            ok = false;
        }

        if (wrapper)
        {
            return ok;
        }

        if (!ok)
        {
            Unsupported[e.Type] = Unsupported.GetValueOrDefault(e.Type) + 1;
        }
        else
        {
            Runs.Add((before, acc.Triangles.Count - before, material));
        }

        if (ok && m.GetDeterminant() < 0)
        {
            // A mirroring transform turns the triangles inside out.
            for (var i = before; i + 2 < acc.Triangles.Count; i += 3)
            {
                (acc.Triangles[i + 1], acc.Triangles[i + 2]) = (acc.Triangles[i + 2], acc.Triangles[i + 1]);
            }
        }

        return ok;
    }

    private bool AddOperand(StepArg operand, Matrix4x4 m, MeshAccumulator acc, int depth, int material) =>
        operand.IsRef && AddItem(operand.Ref, m, acc, depth, material);

    private bool Mapped(StepEntity e, Matrix4x4 m, MeshAccumulator acc, int depth, int material)
    {
        var source = _f.Get(e.Arg(0));
        var representation = source is null ? null : _f.Get(source.Arg(1));
        if (source is null || representation is null)
        {
            return false;
        }

        var origin = Axis3(source.Arg(0));
        var inverse = Matrix4x4.Invert(origin, out var inv) ? inv : Matrix4x4.Identity;
        var target = Operator(_f.Get(e.Arg(1)));
        var transform = inverse * target * m;
        var any = false;
        foreach (var item in representation.Arg(3).Items)
        {
            if (item.IsRef)
            {
                any |= AddItem(item.Ref, transform, acc, depth + 1, material);
            }
        }

        return any;
    }

    private Matrix4x4 Operator(StepEntity? e)
    {
        if (e is null || !e.Type.StartsWith("IFCCARTESIANTRANSFORMATIONOPERATOR3D", StringComparison.Ordinal))
        {
            return Matrix4x4.Identity;
        }

        var x = Direction(e.Arg(0)) ?? Vector3.UnitX;
        var y = Direction(e.Arg(1)) ?? Vector3.UnitY;
        var z = Direction(e.Arg(4)) ?? Vector3.Cross(x, y);
        var scale = e.Arg(3).IsNull ? 1f : (float)e.Arg(3).Num;
        var s2 = e.Args.Count > 5 && !e.Arg(5).IsNull ? (float)e.Arg(5).Num : scale;
        var s3 = e.Args.Count > 6 && !e.Arg(6).IsNull ? (float)e.Arg(6).Num : scale;
        return FromAxes(x * scale, y * s2, z * s3, Point(_f.Get(e.Arg(2))));
    }

    // ---- face sets and shells --------------------------------------------------------------------------------------

    private static float[] Coordinates(StepEntity? list)
    {
        var rows = list?.Arg(0).Items ?? [];
        var result = new float[rows.Count * 3];
        for (var i = 0; i < rows.Count; i++)
        {
            var c = rows[i].Items;
            for (var k = 0; k < 3 && k < c.Count; k++)
            {
                result[3 * i + k] = (float)c[k].Num;
            }
        }

        return result;
    }

    private static int[]? Remap(StepArg arg) => arg.IsNull ? null : arg.Items.Select(i => (int)i.Num).ToArray();

    private void TriangulatedFaceSet(StepEntity e, Matrix4x4 m, MeshAccumulator acc)
    {
        var coordinates = Coordinates(_f.Get(e.Arg(0)));
        var count = coordinates.Length / 3;
        var first = acc.VertexCount;
        for (var i = 0; i < count; i++)
        {
            acc.AddVertex(Vector3.Transform(new Vector3(coordinates[3 * i], coordinates[3 * i + 1], coordinates[3 * i + 2]), m));
        }

        var pn = Remap(e.Arg(4));
        Span<int> v = stackalloc int[3];
        foreach (var triple in e.Arg(3).Items)
        {
            if (triple.Items.Count < 3)
            {
                continue;
            }

            var valid = true;
            for (var k = 0; k < 3; k++)
            {
                var index = (int)triple.Items[k].Num;
                if (pn is not null && index >= 1 && index <= pn.Length)
                {
                    index = pn[index - 1];
                }

                if (index < 1 || index > count)
                {
                    valid = false;
                    break;
                }

                v[k] = index - 1;
            }

            if (valid)
            {
                acc.AddTriangle((uint)(first + v[0]), (uint)(first + v[1]), (uint)(first + v[2]));
            }
        }
    }

    private void PolygonalFaceSet(StepEntity e, Matrix4x4 m, MeshAccumulator acc)
    {
        var coordinates = Coordinates(_f.Get(e.Arg(0)));
        var count = coordinates.Length / 3;
        var pn = Remap(e.Arg(3));
        var corners = new List<Vector3>();
        foreach (var faceRef in e.Arg(2).Items)
        {
            var face = _f.Get(faceRef);
            if (face is null)
            {
                continue;
            }

            corners.Clear();
            foreach (var item in face.Arg(0).Items)
            {
                var index = (int)item.Num;
                if (pn is not null && index >= 1 && index <= pn.Length)
                {
                    index = pn[index - 1];
                }

                if (index >= 1 && index <= count)
                {
                    corners.Add(Vector3.Transform(new Vector3(coordinates[3 * index - 3], coordinates[3 * index - 2], coordinates[3 * index - 1]), m));
                }
            }

            if (face.Type == "IFCINDEXEDPOLYGONALFACEWITHVOIDS")
            {
                Approximations.Add("Holes in polygonal faces are filled.");
            }

            acc.AddPolygon(corners);
        }
    }

    private void Shell(StepEntity? shell, Matrix4x4 m, MeshAccumulator acc)
    {
        if (shell is null)
        {
            return;
        }

        foreach (var faceRef in shell.Arg(0).Items)
        {
            var face = _f.Get(faceRef);
            if (face is null)
            {
                continue;
            }

            List<Vector3>? outer = null;
            var holes = new List<List<Vector3>>();
            foreach (var boundRef in face.Arg(0).Items)
            {
                var bound = _f.Get(boundRef);
                var loop = bound is null ? null : _f.Get(bound.Arg(0));
                if (bound is null || loop is null || loop.Type != "IFCPOLYLOOP")
                {
                    continue;
                }

                var points = LoopPoints(loop, m);
                if (!bound.Arg(1).Bool && bound.Arg(1).Type == StepArg.Kind.Enum)
                {
                    points.Reverse();
                }

                if (outer is null && (bound.Type == "IFCFACEOUTERBOUND" || face.Arg(0).Items.Count == 1))
                {
                    outer = points;
                }
                else
                {
                    holes.Add(points);
                }
            }

            if (outer is null && holes.Count > 0)
            {
                outer = holes[0];
                holes.RemoveAt(0);
            }

            if (outer is not null)
            {
                AddFace(acc, outer, holes);
            }
        }
    }

    private List<Vector3> LoopPoints(StepEntity loop, Matrix4x4 m)
    {
        var points = new List<Vector3>();
        foreach (var p in loop.Arg(0).Items)
        {
            points.Add(Vector3.Transform(Point(_f.Get(p)), m));
        }

        if (points.Count > 1 && points[0] == points[^1])
        {
            points.RemoveAt(points.Count - 1);
        }

        return points;
    }

    private static void AddFace(MeshAccumulator acc, List<Vector3> outer, List<List<Vector3>> holes)
    {
        holes.RemoveAll(h => h.Count < 3);
        if (holes.Count == 0 || outer.Count < 3)
        {
            acc.AddPolygon(outer);
            return;
        }

        double nx = 0, ny = 0, nz = 0;
        for (var i = 0; i < outer.Count; i++)
        {
            Vector3 a = outer[i], b = outer[(i + 1) % outer.Count];
            nx += (a.Y - b.Y) * (a.Z + b.Z);
            ny += (a.Z - b.Z) * (a.X + b.X);
            nz += (a.X - b.X) * (a.Y + b.Y);
        }

        var length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (!(length > 1e-20))
        {
            return;
        }

        var normal = new Vector3((float)(nx / length), (float)(ny / length), (float)(nz / length));
        var u = Vector3.Normalize(Math.Abs(normal.X) < 0.9f ? Vector3.Cross(normal, Vector3.UnitX) : Vector3.Cross(normal, Vector3.UnitY));
        var v = Vector3.Cross(normal, u);
        var all = new List<Vector3>(outer);
        var loops = new List<int[]> { Enumerable.Range(0, outer.Count).ToArray() };
        foreach (var hole in holes)
        {
            loops.Add(Enumerable.Range(all.Count, hole.Count).ToArray());
            all.AddRange(hole);
        }

        var first = acc.VertexCount;
        foreach (var p in all)
        {
            acc.AddVertex(p);
        }

        var points = all.Select(p => ((double)Vector3.Dot(p, u), (double)Vector3.Dot(p, v))).ToList();
        var t = Triangulator.Triangulate(points, loops);
        for (var i = 0; i + 2 < t.Length; i += 3)
        {
            acc.AddTriangle((uint)(first + t[i]), (uint)(first + t[i + 1]), (uint)(first + t[i + 2]));
        }
    }

    // ---- extrusions ------------------------------------------------------------------------------------------------

    private bool Extrusion(StepEntity e, Matrix4x4 m, MeshAccumulator acc)
    {
        var profile = _f.Get(e.Arg(0));
        var loops = Profile(profile, out var reason);
        if (loops is null)
        {
            Approximations.Add($"Extrusions of {reason} are not drawn.");
            return false;
        }

        var position = Axis3(e.Arg(1));
        var direction = Direction(e.Arg(2)) ?? Vector3.UnitZ;
        var depth = (float)e.Arg(3).Num;
        if (!float.IsFinite(depth) || Math.Abs(depth) < 1e-9f || Math.Abs(direction.Z) < 1e-6f)
        {
            return false;
        }

        var offset = direction * depth;
        var transform = position * m;
        // Orient: the outer loop counter-clockwise, holes clockwise, so the sides below face outwards.
        for (var i = 0; i < loops.Count; i++)
        {
            var area = SignedArea(loops[i]);
            if ((i == 0 && area < 0) || (i > 0 && area > 0))
            {
                loops[i].Reverse();
            }
        }

        var all = new List<Vector2>();
        var rings = new List<int[]>();
        foreach (var loop in loops)
        {
            rings.Add(Enumerable.Range(all.Count, loop.Count).ToArray());
            all.AddRange(loop);
        }

        var points2 = all.Select(p => ((double)p.X, (double)p.Y)).ToList();
        var cap = Triangulator.Triangulate(points2, rings);
        var up = direction.Z > 0;
        var bottom = acc.VertexCount;
        foreach (var p in all)
        {
            acc.AddVertex(Vector3.Transform(new Vector3(p.X, p.Y, 0), transform));
        }

        var top = acc.VertexCount;
        foreach (var p in all)
        {
            acc.AddVertex(Vector3.Transform(new Vector3(p.X, p.Y, 0) + offset, transform));
        }

        for (var i = 0; i + 2 < cap.Length; i += 3)
        {
            // The profile faces +Z; the cap that faces the extrusion direction is the far one.
            if (up)
            {
                acc.AddTriangle((uint)(top + cap[i]), (uint)(top + cap[i + 1]), (uint)(top + cap[i + 2]));
                acc.AddTriangle((uint)(bottom + cap[i]), (uint)(bottom + cap[i + 2]), (uint)(bottom + cap[i + 1]));
            }
            else
            {
                acc.AddTriangle((uint)(bottom + cap[i]), (uint)(bottom + cap[i + 1]), (uint)(bottom + cap[i + 2]));
                acc.AddTriangle((uint)(top + cap[i]), (uint)(top + cap[i + 2]), (uint)(top + cap[i + 1]));
            }
        }

        foreach (var ring in rings)
        {
            for (var k = 0; k < ring.Length; k++)
            {
                var a = ring[k];
                var b = ring[(k + 1) % ring.Length];
                if (up)
                {
                    acc.AddTriangle((uint)(bottom + a), (uint)(bottom + b), (uint)(top + b));
                    acc.AddTriangle((uint)(bottom + a), (uint)(top + b), (uint)(top + a));
                }
                else
                {
                    acc.AddTriangle((uint)(bottom + a), (uint)(top + b), (uint)(bottom + b));
                    acc.AddTriangle((uint)(bottom + a), (uint)(top + a), (uint)(top + b));
                }
            }
        }

        return true;
    }

    private static double SignedArea(List<Vector2> loop)
    {
        double area = 0;
        for (var i = 0; i < loop.Count; i++)
        {
            var a = loop[i];
            var b = loop[(i + 1) % loop.Count];
            area += (double)a.X * b.Y - (double)b.X * a.Y;
        }

        return area / 2;
    }

    /// <summary>The loops of a profile (outer first) in the profile's own plane, or null with the profile type in <paramref name="reason"/>.</summary>
    private List<List<Vector2>>? Profile(StepEntity? profile, out string reason)
    {
        reason = profile?.Type ?? "a missing profile";
        if (profile is null)
        {
            return null;
        }

        List<List<Vector2>>? loops;
        switch (profile.Type)
        {
            case "IFCRECTANGLEPROFILEDEF":
                loops = [Rectangle(profile.Arg(3).Num, profile.Arg(4).Num)];
                break;
            case "IFCROUNDEDRECTANGLEPROFILEDEF":
                loops = [Rectangle(profile.Arg(3).Num, profile.Arg(4).Num)];
                break;
            case "IFCRECTANGLEHOLLOWPROFILEDEF":
                {
                    var wall = profile.Arg(5).Num;
                    loops = [Rectangle(profile.Arg(3).Num, profile.Arg(4).Num), Rectangle(profile.Arg(3).Num - 2 * wall, profile.Arg(4).Num - 2 * wall)];
                    break;
                }

            case "IFCCIRCLEPROFILEDEF":
                loops = [Circle(profile.Arg(3).Num)];
                break;
            case "IFCCIRCLEHOLLOWPROFILEDEF":
                loops = [Circle(profile.Arg(3).Num), Circle(profile.Arg(3).Num - profile.Arg(4).Num)];
                break;
            case "IFCARBITRARYCLOSEDPROFILEDEF":
            case "IFCARBITRARYPROFILEDEFWITHVOIDS":
                {
                    var outer = Curve(_f.Get(profile.Arg(2)));
                    if (outer is null)
                    {
                        reason = "profiles bounded by " + (_f.Get(profile.Arg(2))?.Type ?? "an unknown curve");
                        return null;
                    }

                    loops = [outer];
                    if (profile.Type == "IFCARBITRARYPROFILEDEFWITHVOIDS")
                    {
                        foreach (var inner in profile.Arg(3).Items)
                        {
                            if (Curve(_f.Get(inner)) is { } hole)
                            {
                                loops.Add(hole);
                            }
                        }
                    }

                    break;
                }

            default:
                return null;
        }

        // Profile position: IfcAxis2Placement2D(Location, RefDirection).
        var placement = _f.Get(profile.Arg(2));
        if (placement is { Type: "IFCAXIS2PLACEMENT2D" })
        {
            var origin = Point(_f.Get(placement.Arg(0)));
            var refDirection = placement.Arg(1).IsRef && _f.Get(placement.Arg(1)) is { } d && d.Arg(0).Items.Count >= 2
                ? new Vector2((float)d.Arg(0).Items[0].Num, (float)d.Arg(0).Items[1].Num)
                : Vector2.UnitX;
            refDirection = refDirection.Length() > 1e-9f ? Vector2.Normalize(refDirection) : Vector2.UnitX;
            var y = new Vector2(-refDirection.Y, refDirection.X);
            foreach (var loop in loops)
            {
                for (var i = 0; i < loop.Count; i++)
                {
                    loop[i] = new Vector2(origin.X, origin.Y) + refDirection * loop[i].X + y * loop[i].Y;
                }
            }
        }

        loops.RemoveAll(l => l.Count < 3);
        if (loops.Count == 0)
        {
            return null;
        }

        return loops;
    }

    private static List<Vector2> Rectangle(double x, double y)
    {
        float hx = (float)(x / 2), hy = (float)(y / 2);
        return [new(-hx, -hy), new(hx, -hy), new(hx, hy), new(-hx, hy)];
    }

    private List<Vector2> Circle(double radius)
    {
        var steps = (int)Math.Clamp(Math.Ceiling(2 * Math.PI / Math.Max(_options.AngularDeflection, 0.02)), 12, 128);
        var result = new List<Vector2>(steps);
        for (var i = 0; i < steps; i++)
        {
            var angle = 2 * Math.PI * i / steps;
            result.Add(new Vector2((float)(radius * Math.Cos(angle)), (float)(radius * Math.Sin(angle))));
        }

        return result;
    }

    /// <summary>The points of a closed 2D curve: a polyline, or an indexed poly curve of straight and arc segments.</summary>
    private List<Vector2>? Curve(StepEntity? curve)
    {
        if (curve is null)
        {
            return null;
        }

        var points = new List<Vector2>();
        if (curve.Type == "IFCPOLYLINE")
        {
            foreach (var p in curve.Arg(0).Items)
            {
                var c = Point(_f.Get(p));
                points.Add(new Vector2(c.X, c.Y));
            }
        }
        else if (curve.Type == "IFCINDEXEDPOLYCURVE")
        {
            var list = _f.Get(curve.Arg(0));
            var rows = list?.Arg(0).Items ?? [];
            Vector2 At(int index) => index >= 1 && index <= rows.Count && rows[index - 1].Items.Count >= 2
                ? new Vector2((float)rows[index - 1].Items[0].Num, (float)rows[index - 1].Items[1].Num)
                : Vector2.Zero;
            if (curve.Arg(1).IsNull)
            {
                for (var i = 1; i <= rows.Count; i++)
                {
                    points.Add(At(i));
                }
            }
            else
            {
                foreach (var segment in curve.Arg(1).Items)
                {
                    var indices = segment.Items.Count > 0 ? segment.Items[0].Items.Select(i => (int)i.Num).ToArray() : [];
                    if (segment.Text == "IFCARCINDEX" && indices.Length == 3)
                    {
                        AddArc(points, At(indices[0]), At(indices[1]), At(indices[2]));
                    }
                    else
                    {
                        foreach (var i in indices)
                        {
                            points.Add(At(i));
                        }
                    }
                }
            }
        }
        else
        {
            return null;
        }

        var cleaned = new List<Vector2>();
        foreach (var p in points)
        {
            if (cleaned.Count == 0 || Vector2.DistanceSquared(cleaned[^1], p) > 1e-12f)
            {
                cleaned.Add(p);
            }
        }

        if (cleaned.Count > 1 && Vector2.DistanceSquared(cleaned[0], cleaned[^1]) <= 1e-12f)
        {
            cleaned.RemoveAt(cleaned.Count - 1);
        }

        return cleaned.Count >= 3 ? cleaned : null;
    }

    /// <summary>Appends the arc from <paramref name="a"/> through <paramref name="b"/> to <paramref name="c"/>, start point included.</summary>
    private void AddArc(List<Vector2> points, Vector2 a, Vector2 b, Vector2 c)
    {
        double ax = a.X, ay = a.Y, bx = b.X, by = b.Y, cx = c.X, cy = c.Y;
        var d = 2 * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
        if (Math.Abs(d) < 1e-12)
        {
            points.Add(a);
            points.Add(c);
            return;
        }

        var ux = ((ax * ax + ay * ay) * (by - cy) + (bx * bx + by * by) * (cy - ay) + (cx * cx + cy * cy) * (ay - by)) / d;
        var uy = ((ax * ax + ay * ay) * (cx - bx) + (bx * bx + by * by) * (ax - cx) + (cx * cx + cy * cy) * (bx - ax)) / d;
        var radius = Math.Sqrt((ax - ux) * (ax - ux) + (ay - uy) * (ay - uy));
        var t0 = Math.Atan2(ay - uy, ax - ux);
        var t1 = Math.Atan2(by - uy, bx - ux);
        var t2 = Math.Atan2(cy - uy, cx - ux);
        double Wrap(double angle) => angle - 2 * Math.PI * Math.Floor(angle / (2 * Math.PI));
        var counterClockwise = Wrap(t1 - t0) < Wrap(t2 - t0);
        var sweep = counterClockwise ? Wrap(t2 - t0) : -Wrap(t0 - t2);
        var steps = (int)Math.Clamp(Math.Ceiling(Math.Abs(sweep) / Math.Max(_options.AngularDeflection, 0.02)), 2, 64);
        for (var i = 0; i < steps; i++)
        {
            var angle = t0 + sweep * i / steps;
            points.Add(new Vector2((float)(ux + radius * Math.Cos(angle)), (float)(uy + radius * Math.Sin(angle))));
        }

        points.Add(c);
    }
}
