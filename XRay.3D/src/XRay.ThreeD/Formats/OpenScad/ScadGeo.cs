using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.OpenScad;

internal abstract class Geo
{
}

/// <summary>A union of parts, each a closed polygon set of its own (parts may overlap; a boolean operation works on one part at a time).</summary>
internal sealed class Solid : Geo
{
    public Solid(List<Poly> polys)
    {
        Parts = [polys];
    }

    public Solid(List<List<Poly>> parts)
    {
        Parts = parts;
    }

    public List<List<Poly>> Parts { get; }

    public IEnumerable<Poly> Polys => Parts.SelectMany(p => p);

    public int PolyCount => Parts.Sum(p => p.Count);
}

/// <summary>A planar shape: regions, each a list of loops whose first loop is the outline and whose others are holes.</summary>
internal sealed class Shape : Geo
{
    public Shape(List<List<(double X, double Y)[]>> regions)
    {
        Regions = regions;
    }

    public List<List<(double X, double Y)[]>> Regions { get; }
}

/// <summary>4x4 transform, column-vector convention (p' = M p), row-major storage like OpenSCAD's multmatrix.</summary>
internal readonly struct M4
{
    public readonly double[] V;

    public M4(double[] v)
    {
        V = v;
    }

    public static M4 Identity => new([1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1]);

    public static M4 Translate(double x, double y, double z) => new([1, 0, 0, x, 0, 1, 0, y, 0, 0, 1, z, 0, 0, 0, 1]);

    public static M4 Scale(double x, double y, double z) => new([x, 0, 0, 0, 0, y, 0, 0, 0, 0, z, 0, 0, 0, 0, 1]);

    public static M4 Mirror(D3 n)
    {
        n = n.Unit();
        return new([
            1 - 2 * n.X * n.X, -2 * n.X * n.Y, -2 * n.X * n.Z, 0,
            -2 * n.X * n.Y, 1 - 2 * n.Y * n.Y, -2 * n.Y * n.Z, 0,
            -2 * n.X * n.Z, -2 * n.Y * n.Z, 1 - 2 * n.Z * n.Z, 0,
            0, 0, 0, 1]);
    }

    public static M4 RotateAxis(double degrees, D3 axis)
    {
        axis = axis.Unit();
        var a = degrees * Math.PI / 180;
        double c = Math.Cos(a), s = Math.Sin(a), t = 1 - c;
        double x = axis.X, y = axis.Y, z = axis.Z;
        return new([
            t * x * x + c, t * x * y - s * z, t * x * z + s * y, 0,
            t * x * y + s * z, t * y * y + c, t * y * z - s * x, 0,
            t * x * z - s * y, t * y * z + s * x, t * z * z + c, 0,
            0, 0, 0, 1]);
    }

    public static M4 RotateEuler(double xd, double yd, double zd) =>
        RotateAxis(zd, new D3(0, 0, 1)).Then(RotateAxis(yd, new D3(0, 1, 0)), RotateAxis(xd, new D3(1, 0, 0)));

    /// <summary>this * a * b (a and b are applied first, b before a).</summary>
    public M4 Then(M4 a, M4 b) => this * (a * b);

    public static M4 operator *(M4 a, M4 b)
    {
        var r = new double[16];
        for (var i = 0; i < 4; i++)
        {
            for (var j = 0; j < 4; j++)
            {
                double s = 0;
                for (var k = 0; k < 4; k++)
                {
                    s += a.V[i * 4 + k] * b.V[k * 4 + j];
                }

                r[i * 4 + j] = s;
            }
        }

        return new M4(r);
    }

    public D3 Apply(D3 p) => new(
        V[0] * p.X + V[1] * p.Y + V[2] * p.Z + V[3],
        V[4] * p.X + V[5] * p.Y + V[6] * p.Z + V[7],
        V[8] * p.X + V[9] * p.Y + V[10] * p.Z + V[11]);

    public double Determinant3 =>
        V[0] * (V[5] * V[10] - V[6] * V[9]) - V[1] * (V[4] * V[10] - V[6] * V[8]) + V[2] * (V[4] * V[9] - V[5] * V[8]);
}

/// <summary>Primitives, extrusions and planar operations of the OpenSCAD evaluator.</summary>
internal static class ScadGeo
{
    public static Solid Transform(Solid s, M4 m)
    {
        var flip = m.Determinant3 < 0;
        var parts = new List<List<Poly>>(s.Parts.Count);
        foreach (var part in s.Parts)
        {
            var list = new List<Poly>(part.Count);
            foreach (var p in part)
            {
                var v = new D3[p.Vertices.Length];
                for (var i = 0; i < v.Length; i++)
                {
                    v[i] = m.Apply(p.Vertices[flip ? v.Length - 1 - i : i]);
                }

                var q = new Poly(v, p.Color);
                if (!q.IsDegenerate)
                {
                    list.Add(q);
                }
            }

            parts.Add(list);
        }

        return new Solid(parts);
    }

    public static Shape Transform(Shape s, M4 m)
    {
        var flip = m.Determinant3 < 0;
        var regions = new List<List<(double, double)[]>>();
        foreach (var region in s.Regions)
        {
            var loops = new List<(double, double)[]>();
            foreach (var loop in region)
            {
                var l = new (double, double)[loop.Length];
                for (var i = 0; i < l.Length; i++)
                {
                    var p = m.Apply(new D3(loop[i].X, loop[i].Y, 0));
                    l[flip ? l.Length - 1 - i : i] = (p.X, p.Y);
                }

                loops.Add(l);
            }

            regions.Add(loops);
        }

        return new Shape(regions);
    }

    public static int Fragments(double r, double fn, double fa, double fs)
    {
        if (r < 1e-9)
        {
            return 3;
        }

        if (fn > 0)
        {
            return (int)Math.Min(Math.Max(fn, 3), 2_000);
        }

        return (int)Math.Min(Math.Ceiling(Math.Max(Math.Min(360 / Math.Max(fa, 1e-3), r * 2 * Math.PI / Math.Max(fs, 1e-3)), 5)), 2_000);
    }

    public static Solid Cube(D3 size, bool center)
    {
        var o = center ? new D3(-size.X / 2, -size.Y / 2, -size.Z / 2) : default;
        D3 P(int x, int y, int z) => new(o.X + x * size.X, o.Y + y * size.Y, o.Z + z * size.Z);
        var polys = new List<Poly>
        {
            new([P(0, 0, 0), P(0, 1, 0), P(1, 1, 0), P(1, 0, 0)], -1),
            new([P(0, 0, 1), P(1, 0, 1), P(1, 1, 1), P(0, 1, 1)], -1),
            new([P(0, 0, 0), P(1, 0, 0), P(1, 0, 1), P(0, 0, 1)], -1),
            new([P(1, 0, 0), P(1, 1, 0), P(1, 1, 1), P(1, 0, 1)], -1),
            new([P(1, 1, 0), P(0, 1, 0), P(0, 1, 1), P(1, 1, 1)], -1),
            new([P(0, 1, 0), P(0, 0, 0), P(0, 0, 1), P(0, 1, 1)], -1),
        };
        var s = new Solid(polys);
        return size.X * size.Y * size.Z < 0 ? Transform(s, M4.Identity) : s;
    }

    public static Solid Sphere(double r, int fragments)
    {
        var rings = Math.Max((fragments + 1) / 2, 2);
        var ring = new D3[rings][];
        for (var i = 0; i < rings; i++)
        {
            var phi = Math.PI * (i + 0.5) / rings;
            var rr = r * Math.Sin(phi);
            var z = r * Math.Cos(phi);
            ring[i] = new D3[fragments];
            for (var j = 0; j < fragments; j++)
            {
                var a = 2 * Math.PI * j / fragments;
                ring[i][j] = new D3(rr * Math.Cos(a), rr * Math.Sin(a), z);
            }
        }

        var polys = new List<Poly>();
        polys.Add(new Poly(ring[0].ToArray(), -1));
        var bottom = ring[rings - 1].ToArray();
        Array.Reverse(bottom);
        polys.Add(new Poly(bottom, -1));
        for (var i = 0; i + 1 < rings; i++)
        {
            for (var j = 0; j < fragments; j++)
            {
                var k = (j + 1) % fragments;
                polys.Add(new Poly([ring[i][j], ring[i + 1][j], ring[i + 1][k], ring[i][k]], -1));
            }
        }

        return new Solid(polys.Where(p => !p.IsDegenerate).ToList());
    }

    public static Solid Cylinder(double h, double r1, double r2, bool center, int n)
    {
        var z0 = center ? -h / 2 : 0;
        var z1 = z0 + h;
        D3 B(int i) => new(r1 * Math.Cos(2 * Math.PI * i / n), r1 * Math.Sin(2 * Math.PI * i / n), z0);
        D3 T(int i) => new(r2 * Math.Cos(2 * Math.PI * i / n), r2 * Math.Sin(2 * Math.PI * i / n), z1);
        var polys = new List<Poly>();
        if (r1 > 0)
        {
            polys.Add(new Poly(Enumerable.Range(0, n).Reverse().Select(B).ToArray(), -1));
        }

        if (r2 > 0)
        {
            polys.Add(new Poly(Enumerable.Range(0, n).Select(T).ToArray(), -1));
        }

        for (var i = 0; i < n; i++)
        {
            var j = (i + 1) % n;
            if (r1 > 0 && r2 > 0)
            {
                polys.Add(new Poly([B(i), B(j), T(j), T(i)], -1));
            }
            else if (r1 > 0)
            {
                polys.Add(new Poly([B(i), B(j), T(0)], -1));
            }
            else if (r2 > 0)
            {
                polys.Add(new Poly([B(0), T(j), T(i)], -1));
            }
        }

        return new Solid(polys.Where(p => !p.IsDegenerate).ToList());
    }

    public static Solid Polyhedron(List<D3> points, List<int[]> faces)
    {
        var polys = new List<Poly>();
        foreach (var f in faces)
        {
            // OpenSCAD lists faces clockwise seen from outside; fan the polygon into triangles in the opposite order.
            for (var i = 1; i + 1 < f.Length; i++)
            {
                var p = new Poly([points[f[0]], points[f[i + 1]], points[f[i]]], -1);
                if (!p.IsDegenerate)
                {
                    polys.Add(p);
                }
            }
        }

        return new Solid(polys);
    }

    public static Shape Circle(double r, int n)
    {
        var loop = new (double, double)[n];
        for (var i = 0; i < n; i++)
        {
            loop[i] = (r * Math.Cos(2 * Math.PI * i / n), r * Math.Sin(2 * Math.PI * i / n));
        }

        return new Shape([[loop]]);
    }

    public static Shape Square(double x, double y, bool center)
    {
        var ox = center ? -x / 2 : 0;
        var oy = center ? -y / 2 : 0;
        return new Shape([[new (double, double)[] { (ox, oy), (ox + x, oy), (ox + x, oy + y), (ox, oy + y) }]]);
    }

    public static double SignedArea((double X, double Y)[] l)
    {
        double a = 0;
        for (var i = 0; i < l.Length; i++)
        {
            var (x1, y1) = l[i];
            var (x2, y2) = l[(i + 1) % l.Length];
            a += x1 * y2 - x2 * y1;
        }

        return a / 2;
    }

    /// <summary>Outline counter-clockwise, holes clockwise, and a region per outline when a polygon has several top-level paths.</summary>
    public static Shape Polygon(List<(double X, double Y)> points, List<int[]>? paths)
    {
        var loops = new List<(double X, double Y)[]>();
        if (paths is null || paths.Count == 0)
        {
            loops.Add(points.ToArray());
        }
        else
        {
            foreach (var path in paths)
            {
                loops.Add(path.Select(i => points[i]).ToArray());
            }
        }

        var region = new List<(double, double)[]>();
        for (var i = 0; i < loops.Count; i++)
        {
            var l = loops[i];
            if (l.Length < 3)
            {
                continue;
            }

            var ccw = SignedArea(l) > 0;
            if ((i == 0) != ccw)
            {
                Array.Reverse(l);
            }

            region.Add(l);
        }

        return new Shape(region.Count == 0 ? [] : [region]);
    }

    private static int[] AllTriangles(List<(double X, double Y)[]> loops, List<(double X, double Y)> pts, CancellationToken cancel)
    {
        var idx = new List<int[]>();
        foreach (var loop in loops)
        {
            var ids = new int[loop.Length];
            for (var i = 0; i < loop.Length; i++)
            {
                ids[i] = pts.Count;
                pts.Add(loop[i]);
            }

            idx.Add(ids);
        }

        return Triangulator.TriangulateCancellable(pts, idx, cancel);
    }

    public static Solid LinearExtrude(Shape shape, double height, bool center, double twist, int slices, double scaleX, double scaleY, CancellationToken cancel)
    {
        var polys = new List<Poly>();
        slices = Math.Max(slices, 1);
        var z0 = center ? -height / 2 : 0;
        D3 Point((double X, double Y) p, int level)
        {
            var t = (double)level / slices;
            var sx = 1 + (scaleX - 1) * t;
            var sy = 1 + (scaleY - 1) * t;
            var a = -twist * t * Math.PI / 180;
            double c = Math.Cos(a), s = Math.Sin(a);
            double x = p.X * sx, y = p.Y * sy;
            return new D3(x * c - y * s, x * s + y * c, z0 + height * t);
        }

        foreach (var region in shape.Regions)
        {
            cancel.ThrowIfCancellationRequested();
            var pts = new List<(double X, double Y)>();
            var tri = AllTriangles(region, pts, cancel);
            for (var i = 0; i + 2 < tri.Length; i += 3)
            {
                var a = Point(pts[tri[i]], 0);
                var b = Point(pts[tri[i + 1]], 0);
                var c = Point(pts[tri[i + 2]], 0);
                var bottom = new Poly([a, c, b], -1);
                if (!bottom.IsDegenerate)
                {
                    polys.Add(bottom);
                }

                if (scaleX > 1e-9 && scaleY > 1e-9)
                {
                    var top = new Poly([Point(pts[tri[i]], slices), Point(pts[tri[i + 1]], slices), Point(pts[tri[i + 2]], slices)], -1);
                    if (!top.IsDegenerate)
                    {
                        polys.Add(top);
                    }
                }
            }

            foreach (var loop in region)
            {
                for (var level = 0; level < slices; level++)
                {
                    Cancel.Every(cancel, level);
                    for (var i = 0; i < loop.Length; i++)
                    {
                        var j = (i + 1) % loop.Length;
                        var p = new Poly([Point(loop[i], level), Point(loop[j], level), Point(loop[j], level + 1), Point(loop[i], level + 1)], -1);
                        if (!p.IsDegenerate)
                        {
                            polys.Add(p);
                        }
                        else
                        {
                            // A collapsing top (scale 0): keep the triangle that is left.
                            var t1 = new Poly([Point(loop[i], level), Point(loop[j], level), Point(loop[j], level + 1)], -1);
                            if (!t1.IsDegenerate)
                            {
                                polys.Add(t1);
                            }
                        }
                    }
                }
            }
        }

        return new Solid(polys);
    }

    public static Solid RotateExtrude(Shape shape, double angle, int fragments, CancellationToken cancel)
    {
        var full = angle >= 360 - 1e-9;
        var steps = full ? fragments : Math.Max((int)Math.Ceiling(fragments * angle / 360), 1);
        var polys = new List<Poly>();
        D3 At((double X, double Y) p, int step)
        {
            var a = (full ? 360.0 * step / steps : angle * step / steps) * Math.PI / 180;
            var x = Math.Max(p.X, 0);
            return new D3(x * Math.Cos(a), x * Math.Sin(a), p.Y);
        }

        foreach (var region in shape.Regions)
        {
            cancel.ThrowIfCancellationRequested();
            foreach (var loop in region)
            {
                for (var step = 0; step < steps; step++)
                {
                    Cancel.Every(cancel, step);
                    var next = full ? (step + 1) % steps : step + 1;
                    for (var i = 0; i < loop.Length; i++)
                    {
                        var j = (i + 1) % loop.Length;
                        var a = At(loop[i], step);
                        var b = At(loop[i], next);
                        var c = At(loop[j], next);
                        var d = At(loop[j], step);
                        var q = new Poly([a, b, c, d], -1);
                        if (!q.IsDegenerate)
                        {
                            polys.Add(q);
                        }
                        else
                        {
                            foreach (var tri in new[] { new[] { a, b, c }, new[] { a, c, d } })
                            {
                                var t = new Poly(tri, -1);
                                if (!t.IsDegenerate)
                                {
                                    polys.Add(t);
                                }
                            }
                        }
                    }
                }
            }

            if (!full)
            {
                var pts = new List<(double X, double Y)>();
                var tri = AllTriangles(region, pts, cancel);
                for (var i = 0; i + 2 < tri.Length; i += 3)
                {
                    var s = new Poly([At(pts[tri[i]], 0), At(pts[tri[i + 1]], 0), At(pts[tri[i + 2]], 0)], -1);
                    var e = new Poly([At(pts[tri[i + 2]], steps), At(pts[tri[i + 1]], steps), At(pts[tri[i]], steps)], -1);
                    if (!s.IsDegenerate)
                    {
                        polys.Add(s);
                    }

                    if (!e.IsDegenerate)
                    {
                        polys.Add(e);
                    }
                }
            }
        }

        return new Solid(polys);
    }

    /// <summary>Mitre offset of every loop (outlines grow for positive distances, holes shrink).</summary>
    public static Shape Offset(Shape shape, double d)
    {
        var regions = new List<List<(double, double)[]>>();
        foreach (var region in shape.Regions)
        {
            var loops = new List<(double, double)[]>();
            foreach (var loop in region)
            {
                var n = loop.Length;
                var o = new (double, double)[n];
                for (var i = 0; i < n; i++)
                {
                    var prev = loop[(i + n - 1) % n];
                    var cur = loop[i];
                    var next = loop[(i + 1) % n];
                    var (n1x, n1y) = Right(prev, cur);
                    var (n2x, n2y) = Right(cur, next);
                    var dot = n1x * n2x + n1y * n2y;
                    var k = d / Math.Max(1 + dot, 0.25);
                    o[i] = (cur.X + (n1x + n2x) * k, cur.Y + (n1y + n2y) * k);
                }

                // A loop that collapsed (inverted) is dropped.
                if (Math.Sign(SignedArea(o)) == Math.Sign(SignedArea(loop)) && Math.Abs(SignedArea(o)) > 1e-12)
                {
                    loops.Add(o);
                }
                else if (loops.Count == 0)
                {
                    break;
                }
            }

            if (loops.Count > 0)
            {
                regions.Add(loops);
            }
        }

        return new Shape(regions);
    }

    private static (double, double) Right((double X, double Y) a, (double X, double Y) b)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y;
        var l = Math.Sqrt(dx * dx + dy * dy);
        return l < 1e-15 ? (0, 0) : (dy / l, -dx / l);
    }

    public static Shape Hull(List<(double X, double Y)> input)
    {
        var pts = input.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
        if (pts.Count < 3)
        {
            return new Shape([]);
        }

        double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var h = new List<(double X, double Y)>();
        foreach (var p in pts)
        {
            while (h.Count >= 2 && Cross(h[^2], h[^1], p) <= 0)
            {
                h.RemoveAt(h.Count - 1);
            }

            h.Add(p);
        }

        var lower = h.Count + 1;
        for (var i = pts.Count - 2; i >= 0; i--)
        {
            while (h.Count >= lower && Cross(h[^2], h[^1], pts[i]) <= 0)
            {
                h.RemoveAt(h.Count - 1);
            }

            h.Add(pts[i]);
        }

        h.RemoveAt(h.Count - 1);
        return h.Count < 3 ? new Shape([]) : new Shape([[h.Select(p => (p.X, p.Y)).ToArray()]]);
    }

    public static bool Inside((double X, double Y) p, (double X, double Y)[] loop)
    {
        var inside = false;
        for (int i = 0, j = loop.Length - 1; i < loop.Length; j = i++)
        {
            if ((loop[i].Y > p.Y) != (loop[j].Y > p.Y) && p.X < (loop[j].X - loop[i].X) * (p.Y - loop[i].Y) / (loop[j].Y - loop[i].Y) + loop[i].X)
            {
                inside = !inside;
            }
        }

        return inside;
    }
}
