using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.OpenScad;

internal readonly record struct D3(double X, double Y, double Z)
{
    public static D3 operator +(D3 a, D3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);

    public static D3 operator -(D3 a, D3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);

    public static D3 operator *(D3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);

    public double Dot(D3 o) => X * o.X + Y * o.Y + Z * o.Z;

    public D3 Cross(D3 o) => new(Y * o.Z - Z * o.Y, Z * o.X - X * o.Z, X * o.Y - Y * o.X);

    public double Length => Math.Sqrt(Dot(this));

    public D3 Unit()
    {
        var l = Length;
        return l > 0 ? this * (1 / l) : this;
    }

    public static D3 Lerp(D3 a, D3 b, double t) => a + (b - a) * t;
}

/// <summary>A convex polygon, counter-clockwise seen from outside, with its plane and a material index (-1 for none).</summary>
internal sealed class Poly
{
    public Poly(D3[] vertices, int color)
    {
        Vertices = vertices;
        Color = color;
        var n = default(D3);
        // Newell's method: robust for slightly non-planar polygons.
        for (var i = 0; i < vertices.Length; i++)
        {
            var a = vertices[i];
            var b = vertices[(i + 1) % vertices.Length];
            n = new D3(n.X + (a.Y - b.Y) * (a.Z + b.Z), n.Y + (a.Z - b.Z) * (a.X + b.X), n.Z + (a.X - b.X) * (a.Y + b.Y));
        }

        Normal = n.Unit();
        W = Normal.Dot(vertices[0]);
    }

    public D3[] Vertices { get; }

    public D3 Normal { get; private set; }

    public double W { get; private set; }

    public int Color { get; }

    public bool IsDegenerate => Normal.Length < 0.5;

    public Poly Flipped()
    {
        var v = (D3[])Vertices.Clone();
        Array.Reverse(v);
        return new Poly(v, Color);
    }
}

/// <summary>Shared limits and the cancellation token of one CSG operation.</summary>
internal sealed class CsgBudget
{
    public CancellationToken Cancel { get; init; }

    public double Epsilon { get; init; } = 1e-6;

    public int MaxPolygons { get; init; } = 600_000;

    public int MaxDepth { get; init; } = 150_000;

    public long Work;

    public int Polygons;

    public void Tick() => Internal.Cancel.Every(Cancel, Work++);

    public void Count(int added)
    {
        Polygons += added;
        if (Polygons > MaxPolygons)
        {
            throw new CsgTooBig("polygons");
        }
    }
}

internal sealed class CsgTooBig : Exception
{
    public CsgTooBig(string reason = "size")
        : base(reason)
    {
    }
}

/// <summary>Binary space partition boolean operations on closed polygon soups (the classic BSP-tree algorithm, written for this library).</summary>
internal static class Csg
{
    private sealed class Node
    {
        public D3 N;
        public double W;
        public bool HasPlane;
        public Node? Front;
        public Node? Back;
        public List<Poly> Polys = new();

        public void Invert(int depth, CsgBudget b)
        {
            b.Tick();
            if (depth > b.MaxDepth)
            {
                throw new CsgTooBig("depth");
            }

            for (var i = 0; i < Polys.Count; i++)
            {
                Polys[i] = Polys[i].Flipped();
            }

            N = N * -1;
            W = -W;
            (Front, Back) = (Back, Front);
            Front?.Invert(depth + 1, b);
            Back?.Invert(depth + 1, b);
        }

        public List<Poly> Clip(List<Poly> input, int depth, CsgBudget b)
        {
            b.Tick();
            if (!HasPlane)
            {
                return new List<Poly>(input);
            }

            if (depth > b.MaxDepth)
            {
                throw new CsgTooBig("depth");
            }

            List<Poly> front = new(), back = new();
            foreach (var p in input)
            {
                Split(N, W, p, front, back, front, back, b);
            }

            var f = Front is not null ? Front.Clip(front, depth + 1, b) : front;
            var k = Back is not null ? Back.Clip(back, depth + 1, b) : new List<Poly>();
            f.AddRange(k);
            return f;
        }

        public void ClipTo(Node other, int depth, CsgBudget b)
        {
            b.Tick();
            if (depth > b.MaxDepth)
            {
                throw new CsgTooBig("depth");
            }

            Polys = other.Clip(Polys, 0, b);
            Front?.ClipTo(other, depth + 1, b);
            Back?.ClipTo(other, depth + 1, b);
        }

        public void All(List<Poly> into)
        {
            // Iterative to keep deep trees off the stack.
            var stack = new Stack<Node>();
            stack.Push(this);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                into.AddRange(n.Polys);
                if (n.Front is not null)
                {
                    stack.Push(n.Front);
                }

                if (n.Back is not null)
                {
                    stack.Push(n.Back);
                }
            }
        }

        public void Build(List<Poly> polys, int depth, CsgBudget b)
        {
            b.Tick();
            if (polys.Count == 0)
            {
                return;
            }

            if (depth > b.MaxDepth)
            {
                throw new CsgTooBig("depth");
            }

            if (!HasPlane)
            {
                // Of a few candidates take the plane that cuts the fewest polygons: fewer fragments, a shallower tree.
                var pivot = polys[polys.Count / 2];
                if (polys.Count > 16)
                {
                    var best = long.MaxValue;
                    for (var c = 0; c < 6; c++)
                    {
                        var cand = polys[(int)((long)polys.Count * (2 * c + 1) / 12)];
                        long score = 0;
                        var sample = Math.Min(polys.Count, 64);
                        var step = Math.Max(polys.Count / sample, 1);
                        for (var i = 0; i < polys.Count; i += step)
                        {
                            var pos = false;
                            var neg = false;
                            foreach (var v in polys[i].Vertices)
                            {
                                var t = cand.Normal.Dot(v) - cand.W;
                                pos |= t > b.Epsilon;
                                neg |= t < -b.Epsilon;
                            }

                            if (pos && neg)
                            {
                                score++;
                            }
                        }

                        if (score < best)
                        {
                            best = score;
                            pivot = cand;
                        }
                    }
                }

                N = pivot.Normal;
                W = pivot.W;
                HasPlane = true;
            }

            List<Poly> front = new(), back = new();
            foreach (var p in polys)
            {
                Split(N, W, p, Polys, Polys, front, back, b);
            }

            if (front.Count > 0)
            {
                Front ??= new Node();
                Front.Build(front, depth + 1, b);
            }

            if (back.Count > 0)
            {
                Back ??= new Node();
                Back.Build(back, depth + 1, b);
            }
        }
    }

    private static Node Make(List<Poly> polys, CsgBudget b)
    {
        var node = new Node();
        // A deterministic shuffle keeps the tree shallow on curved, ordered meshes.
        var shuffled = new List<Poly>(polys);
        var seed = 0x9E3779B9u;
        for (var i = shuffled.Count - 1; i > 0; i--)
        {
            seed = seed * 1664525u + 1013904223u;
            var j = (int)((ulong)seed * (ulong)(i + 1) >> 32);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        node.Build(shuffled, 0, b);
        return node;
    }

    private static void Split(D3 n, double w, Poly p, List<Poly> coFront, List<Poly> coBack, List<Poly> front, List<Poly> back, CsgBudget b)
    {
        var eps = b.Epsilon;
        var type = 0;
        Span<int> types = p.Vertices.Length <= 64 ? stackalloc int[p.Vertices.Length] : new int[p.Vertices.Length];
        for (var i = 0; i < p.Vertices.Length; i++)
        {
            var t = n.Dot(p.Vertices[i]) - w;
            var s = t < -eps ? 2 : t > eps ? 1 : 0;
            type |= s;
            types[i] = s;
        }

        switch (type)
        {
            case 0:
                (n.Dot(p.Normal) > 0 ? coFront : coBack).Add(p);
                break;
            case 1:
                front.Add(p);
                break;
            case 2:
                back.Add(p);
                break;
            default:
                var f = new List<D3>();
                var k = new List<D3>();
                for (var i = 0; i < p.Vertices.Length; i++)
                {
                    var j = (i + 1) % p.Vertices.Length;
                    int ti = types[i], tj = types[j];
                    D3 vi = p.Vertices[i], vj = p.Vertices[j];
                    if (ti != 2)
                    {
                        f.Add(vi);
                    }

                    if (ti != 1)
                    {
                        k.Add(vi);
                    }

                    if ((ti | tj) == 3)
                    {
                        var t = (w - n.Dot(vi)) / n.Dot(vj - vi);
                        var v = D3.Lerp(vi, vj, t);
                        f.Add(v);
                        k.Add(v);
                    }
                }

                if (f.Count >= 3)
                {
                    var q = new Poly(f.ToArray(), p.Color);
                    if (!q.IsDegenerate)
                    {
                        front.Add(q);
                        b.Count(1);
                    }
                }

                if (k.Count >= 3)
                {
                    var q = new Poly(k.ToArray(), p.Color);
                    if (!q.IsDegenerate)
                    {
                        back.Add(q);
                        b.Count(1);
                    }
                }

                break;
        }
    }

    /// <summary>Runs <paramref name="body"/> on a thread with a large stack: the BSP of a convex solid is as deep as it has faces.</summary>
    public static T OnBigStack<T>(Func<T> body)
    {
        T? result = default;
        Exception? failure = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    result = body();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
            },
            256 * 1024 * 1024);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return result!;
    }

    /// <summary>Splits polygons that are not planar within the tolerance into triangles (a polygon that is off its own plane would split itself forever).</summary>
    public static List<Poly> Prepare(List<Poly> polys, double eps)
    {
        var result = new List<Poly>(polys.Count);
        foreach (var p in polys)
        {
            var planar = true;
            if (p.Vertices.Length > 3)
            {
                foreach (var v in p.Vertices)
                {
                    if (Math.Abs(p.Normal.Dot(v) - p.W) > eps)
                    {
                        planar = false;
                        break;
                    }
                }
            }

            if (planar)
            {
                result.Add(p);
                continue;
            }

            for (var i = 1; i + 1 < p.Vertices.Length; i++)
            {
                var t = new Poly([p.Vertices[0], p.Vertices[i], p.Vertices[i + 1]], p.Color);
                if (!t.IsDegenerate)
                {
                    result.Add(t);
                }
            }
        }

        return result;
    }

    public static List<Poly> Subtract(List<Poly> a, List<Poly> b, CsgBudget budget)
    {
        var na = Make(Prepare(a, budget.Epsilon), budget);
        var nb = Make(Prepare(b, budget.Epsilon), budget);
        na.Invert(0, budget);
        na.ClipTo(nb, 0, budget);
        nb.ClipTo(na, 0, budget);
        nb.Invert(0, budget);
        nb.ClipTo(na, 0, budget);
        nb.Invert(0, budget);
        var rest = new List<Poly>();
        nb.All(rest);
        na.Build(rest, 0, budget);
        na.Invert(0, budget);
        var result = new List<Poly>();
        na.All(result);
        return result;
    }

    public static List<Poly> Intersect(List<Poly> a, List<Poly> b, CsgBudget budget)
    {
        var na = Make(Prepare(a, budget.Epsilon), budget);
        var nb = Make(Prepare(b, budget.Epsilon), budget);
        na.Invert(0, budget);
        nb.ClipTo(na, 0, budget);
        nb.Invert(0, budget);
        na.ClipTo(nb, 0, budget);
        nb.ClipTo(na, 0, budget);
        var rest = new List<Poly>();
        nb.All(rest);
        na.Build(rest, 0, budget);
        na.Invert(0, budget);
        var result = new List<Poly>();
        na.All(result);
        return result;
    }

    /// <summary>Convex hull of a point set as counter-clockwise triangles; null when the points are not in general position (coplanar or fewer than four).</summary>
    public static List<Poly>? Hull(List<D3> input, int color, CsgBudget budget)
    {
        // Deduplicate and find the extent for tolerances.
        var seen = new HashSet<(long, long, long)>();
        var pts = new List<D3>();
        double extent = 0;
        foreach (var p in input)
        {
            extent = Math.Max(extent, Math.Max(Math.Abs(p.X), Math.Max(Math.Abs(p.Y), Math.Abs(p.Z))));
        }

        var q = Math.Max(extent, 1e-9) * 1e-9;
        foreach (var p in input)
        {
            budget.Tick();
            if (seen.Add(((long)Math.Round(p.X / q), (long)Math.Round(p.Y / q), (long)Math.Round(p.Z / q))))
            {
                pts.Add(p);
            }
        }

        if (pts.Count < 4)
        {
            return null;
        }

        var eps = Math.Max(extent, 1e-9) * 1e-9;
        // Initial tetrahedron.
        int i0 = 0, i1 = -1, i2 = -1, i3 = -1;
        for (var i = 1; i < pts.Count && i1 < 0; i++)
        {
            if ((pts[i] - pts[i0]).Length > eps * 1e3)
            {
                i1 = i;
            }
        }

        if (i1 < 0)
        {
            return null;
        }

        for (var i = 1; i < pts.Count && i2 < 0; i++)
        {
            if (i != i1 && (pts[i1] - pts[i0]).Cross(pts[i] - pts[i0]).Length > eps * extent * 1e3)
            {
                i2 = i;
            }
        }

        if (i2 < 0)
        {
            return null;
        }

        var nrm = (pts[i1] - pts[i0]).Cross(pts[i2] - pts[i0]).Unit();
        for (var i = 1; i < pts.Count && i3 < 0; i++)
        {
            if (i != i1 && i != i2 && Math.Abs(nrm.Dot(pts[i] - pts[i0])) > eps * extent * 1e3)
            {
                i3 = i;
            }
        }

        if (i3 < 0)
        {
            return null;
        }

        var faces = new List<int[]>();
        void AddFace(int a, int b, int c, int inside)
        {
            var n = (pts[b] - pts[a]).Cross(pts[c] - pts[a]);
            faces.Add(n.Dot(pts[inside] - pts[a]) > 0 ? [a, c, b] : [a, b, c]);
        }

        AddFace(i0, i1, i2, i3);
        AddFace(i0, i1, i3, i2);
        AddFace(i0, i2, i3, i1);
        AddFace(i1, i2, i3, i0);
        var interior = (pts[i0] + pts[i1] + pts[i2] + pts[i3]) * 0.25;
        for (var i = 0; i < pts.Count; i++)
        {
            budget.Tick();
            if (i == i0 || i == i1 || i == i2 || i == i3)
            {
                continue;
            }

            var p = pts[i];
            var visible = new bool[faces.Count];
            var any = false;
            for (var f = 0; f < faces.Count; f++)
            {
                var fc = faces[f];
                var n = (pts[fc[1]] - pts[fc[0]]).Cross(pts[fc[2]] - pts[fc[0]]).Unit();
                if (n.Dot(p - pts[fc[0]]) > eps * 10)
                {
                    visible[f] = true;
                    any = true;
                }
            }

            if (!any)
            {
                continue;
            }

            var edges = new Dictionary<(int, int), int>();
            for (var f = 0; f < faces.Count; f++)
            {
                if (!visible[f])
                {
                    continue;
                }

                var fc = faces[f];
                for (var e = 0; e < 3; e++)
                {
                    var key = (fc[e], fc[(e + 1) % 3]);
                    edges[key] = 1;
                }
            }

            var next = new List<int[]>();
            for (var f = 0; f < faces.Count; f++)
            {
                if (!visible[f])
                {
                    next.Add(faces[f]);
                }
            }

            foreach (var (a, c) in edges.Keys)
            {
                if (!edges.ContainsKey((c, a)))
                {
                    next.Add([a, c, i]);
                }
            }

            faces = next;
            if (faces.Count > budget.MaxPolygons)
            {
                throw new CsgTooBig();
            }
        }

        _ = interior;
        var result = new List<Poly>(faces.Count);
        foreach (var fc in faces)
        {
            var poly = new Poly([pts[fc[0]], pts[fc[1]], pts[fc[2]]], color);
            if (!poly.IsDegenerate)
            {
                result.Add(poly);
            }
        }

        return result;
    }
}
