namespace XRay.ThreeD.Internal;

/// <summary>
/// Constrained Delaunay triangulation of a planar point set. Points are inserted one by one (Bowyer-Watson), the segments of the
/// constraint polylines are forced into the mesh by edge flips (Sloan's algorithm), and the triangles outside the region enclosed by
/// the constraint loops are dropped (even-odd rule, so holes work). Adjacency is a map from directed edges to triangles.
/// </summary>
internal sealed class Triangulator
{
    /// <summary>
    /// Edge keys pack two vertex numbers into one long; the default hash (the xor of the halves) sends the edges of consecutive
    /// vertices, which are exactly the ones a loop produces, to a handful of buckets.
    /// </summary>
    private sealed class EdgeComparer : IEqualityComparer<long>
    {
        public static readonly EdgeComparer Instance = new();

        public bool Equals(long x, long y) => x == y;

        public int GetHashCode(long key) => (int)(((ulong)key * 0x9E3779B97F4A7C15UL) >> 32);
    }

    private readonly double[] _x;
    private readonly double[] _y;
    private readonly int _n;
    private readonly List<int> _a = new();
    private readonly List<int> _b = new();
    private readonly List<int> _c = new();
    private readonly List<bool> _dead = new();
    private readonly Stack<int> _free = new();
    private readonly Dictionary<long, int> _edge = new(EdgeComparer.Instance);
    private readonly HashSet<long> _constrained = new(EdgeComparer.Instance);
    private readonly int[] _vertexTriangle;
    private int _hint;

    /// <summary>
    /// Returns triangle corner indices (counter-clockwise in the given coordinates) for the region enclosed by <paramref name="loops"/>
    /// (closed polylines of indices into <paramref name="points"/>). Points not on a loop are interior sample points.
    /// </summary>
    public static int[] Triangulate(IReadOnlyList<(double X, double Y)> points, IReadOnlyList<int[]> loops)
    {
        if (points.Count < 3)
        {
            return [];
        }

        return new Triangulator(points).Run(loops);
    }

    private Triangulator(IReadOnlyList<(double X, double Y)> points)
    {
        _n = points.Count;
        _x = new double[_n + 3];
        _y = new double[_n + 3];
        _vertexTriangle = new int[_n + 3];
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        for (var i = 0; i < _n; i++)
        {
            minX = Math.Min(minX, points[i].X);
            maxX = Math.Max(maxX, points[i].X);
            minY = Math.Min(minY, points[i].Y);
            maxY = Math.Max(maxY, points[i].Y);
        }

        var size = Math.Max(Math.Max(maxX - minX, maxY - minY), 1e-12);
        var cx = (minX + maxX) / 2;
        var cy = (minY + maxY) / 2;
        // Normalise to a unit box and nudge each point by a deterministic hash: regular sample grids would otherwise give exactly
        // cocircular quadruples that the incircle test cannot order consistently.
        for (var i = 0; i < _n; i++)
        {
            var h = unchecked((uint)(i * 2654435761u));
            _x[i] = (points[i].X - cx) / size + ((h & 0xFFFF) / 65535.0 - 0.5) * 1e-9;
            _y[i] = (points[i].Y - cy) / size + (((h >> 16) & 0xFFFF) / 65535.0 - 0.5) * 1e-9;
        }

        _x[_n] = -50;
        _y[_n] = -25;
        _x[_n + 1] = 50;
        _y[_n + 1] = -25;
        _x[_n + 2] = 0;
        _y[_n + 2] = 75;
    }


    private int[] Run(IReadOnlyList<int[]> loops)
    {
        AddTriangle(_n, _n + 1, _n + 2);
        // Insertion order: a deterministic shuffle. Spatially sorted insertion makes long thin fans around clustered points (holes full
        // of cocircular samples), which is much slower than the expected O(n log n) of a random order.
        var order = new int[_n];
        for (var i = 0; i < _n; i++)
        {
            order[i] = i;
        }

        var state = 0x9E3779B97F4A7C15UL;
        for (var i = _n - 1; i > 0; i--)
        {
            state ^= state << 13;
            state ^= state >> 7;
            state ^= state << 17;
            var k = (int)(state % (ulong)(i + 1));
            (order[i], order[k]) = (order[k], order[i]);
        }

        foreach (var i in order)
        {
            Insert(i);
        }


        foreach (var loop in loops)
        {
            for (var k = 0; k < loop.Length; k++)
            {
                var a = loop[k];
                var b = loop[(k + 1) % loop.Length];
                if (a != b)
                {
                    InsertConstraint(a, b, 0);
                }
            }
        }

        return Collect();
    }

    /// <summary>Position of the cell (x, y) of a 65536 x 65536 grid along the Hilbert curve.</summary>
    private static long HilbertKey(int x, int y)
    {
        long d = 0;
        for (var s = 1 << 15; s > 0; s >>= 1)
        {
            var rx = (x & s) > 0 ? 1 : 0;
            var ry = (y & s) > 0 ? 1 : 0;
            d += (long)s * s * ((3 * rx) ^ ry);
            if (ry == 0)
            {
                if (rx == 1)
                {
                    x = 65535 - x;
                    y = 65535 - y;
                }

                (x, y) = (y, x);
            }
        }

        return d;
    }

    // ---- predicates ----------------------------------------------------------------------------------------------

    private double Orient(int p, int q, int r) => (_x[q] - _x[p]) * (_y[r] - _y[p]) - (_y[q] - _y[p]) * (_x[r] - _x[p]);

    private bool InCircle(int a, int b, int c, int d)
    {
        double adx = _x[a] - _x[d], ady = _y[a] - _y[d];
        double bdx = _x[b] - _x[d], bdy = _y[b] - _y[d];
        double cdx = _x[c] - _x[d], cdy = _y[c] - _y[d];
        double ad = adx * adx + ady * ady, bd = bdx * bdx + bdy * bdy, cd = cdx * cdx + cdy * cdy;
        var det = adx * (bdy * cd - bd * cdy) - ady * (bdx * cd - bd * cdx) + ad * (bdx * cdy - bdy * cdx);
        return det > 1e-18;
    }

    // ---- triangle store ------------------------------------------------------------------------------------------

    private static long Key(int a, int b) => ((long)a << 32) | (uint)b;

    private static long UndirectedKey(int a, int b) => a < b ? Key(a, b) : Key(b, a);

    private int AddTriangle(int a, int b, int c)
    {
        int t;
        if (_free.Count > 0)
        {
            t = _free.Pop();
            _a[t] = a;
            _b[t] = b;
            _c[t] = c;
            _dead[t] = false;
        }
        else
        {
            _a.Add(a);
            _b.Add(b);
            _c.Add(c);
            _dead.Add(false);
            t = _a.Count - 1;
        }

        _edge[Key(a, b)] = t;
        _edge[Key(b, c)] = t;
        _edge[Key(c, a)] = t;
        _vertexTriangle[a] = t;
        _vertexTriangle[b] = t;
        _vertexTriangle[c] = t;
        return t;
    }

    private void RemoveTriangle(int t)
    {
        _edge.Remove(Key(_a[t], _b[t]));
        _edge.Remove(Key(_b[t], _c[t]));
        _edge.Remove(Key(_c[t], _a[t]));
        _dead[t] = true;
        _free.Push(t);
    }

    /// <summary>The triangle on the left of the directed edge a to b, or -1.</summary>
    private int Across(int a, int b) => _edge.TryGetValue(Key(a, b), out var t) ? t : -1;

    private int Third(int t, int u, int v) => _a[t] != u && _a[t] != v ? _a[t] : _b[t] != u && _b[t] != v ? _b[t] : _c[t];

    private bool Has(int t, int v) => _a[t] == v || _b[t] == v || _c[t] == v;

    // ---- insertion -----------------------------------------------------------------------------------------------

    private int Locate(int p)
    {
        var t = _hint;
        if (t >= _dead.Count || _dead[t])
        {
            t = Live();
        }

        for (var steps = 0; steps < 3 * _dead.Count + 32; steps++)
        {
            int a = _a[t], b = _b[t], c = _c[t];
            if (Orient(a, b, p) < 0 && Across(b, a) is var n1 and >= 0)
            {
                t = n1;
            }
            else if (Orient(b, c, p) < 0 && Across(c, b) is var n2 and >= 0)
            {
                t = n2;
            }
            else if (Orient(c, a, p) < 0 && Across(a, c) is var n3 and >= 0)
            {
                t = n3;
            }
            else
            {
                return t;
            }
        }

        for (var s = 0; s < _dead.Count; s++)
        {
            if (!_dead[s] && Orient(_a[s], _b[s], p) >= 0 && Orient(_b[s], _c[s], p) >= 0 && Orient(_c[s], _a[s], p) >= 0)
            {
                return s;
            }
        }

        return Live();
    }

    private int Live()
    {
        for (var i = 0; i < _dead.Count; i++)
        {
            if (!_dead[i])
            {
                return i;
            }
        }

        return 0;
    }

    private void Insert(int p)
    {
        var start = Locate(p);
        var bad = new List<int> { start };
        var inCavity = new HashSet<int> { start };
        for (var i = 0; i < bad.Count; i++)
        {
            var t = bad[i];
            Visit(_b[t], _a[t]);
            Visit(_c[t], _b[t]);
            Visit(_a[t], _c[t]);
        }

        void Visit(int u, int v)
        {
            // Edge (v, u) of the triangle: the neighbour lies on the left of (u, v).
            var n = Across(u, v);
            if (n >= 0 && !inCavity.Contains(n) && InCircle(_a[n], _b[n], _c[n], p))
            {
                inCavity.Add(n);
                bad.Add(n);
            }
        }

        var boundary = new List<(int U, int V)>();
        foreach (var t in bad)
        {
            Edge(_a[t], _b[t]);
            Edge(_b[t], _c[t]);
            Edge(_c[t], _a[t]);
        }

        void Edge(int u, int v)
        {
            var n = Across(v, u);
            if (n < 0 || !inCavity.Contains(n))
            {
                boundary.Add((u, v));
            }
        }

        foreach (var t in bad)
        {
            RemoveTriangle(t);
        }

        foreach (var (u, v) in boundary)
        {
            _hint = AddTriangle(u, v, p);
        }
    }

    // ---- constraints -----------------------------------------------------------------------------------------------

    private void InsertConstraint(int a, int b, int depth)
    {
        _constrained.Add(UndirectedKey(a, b));
        if (Across(a, b) >= 0 || Across(b, a) >= 0 || depth > 50)
        {
            return;
        }

        // Find the triangle of the fan around a that the segment a-b leaves through.
        var crossing = new List<(int U, int V)>();
        var first = -1;
        var fan = FanAround(a);
        foreach (var t in fan)
        {
            Rotate(t, a, out var u, out var v); // t = (a, u, v) counter-clockwise
            if (u == b || v == b)
            {
                return;
            }

            if (Orient(a, u, b) > 0 && Orient(a, v, b) < 0)
            {
                first = t;
                crossing.Add((u, v));
                break;
            }
        }

        if (first < 0)
        {
            // b lies on an edge from a (collinear): split at the vertex that is on the segment.
            foreach (var t in fan)
            {
                Rotate(t, a, out var u, out var v);
                foreach (var w in new[] { u, v })
                {
                    if (Math.Abs(Orient(a, w, b)) < 1e-14 && Between(a, w, b))
                    {
                        InsertConstraint(a, w, depth + 1);
                        InsertConstraint(w, b, depth + 1);
                        return;
                    }
                }
            }

            return;
        }

        // Walk through the triangles the segment crosses.
        var current = first;
        var cu = crossing[0].U;
        var cv = crossing[0].V;
        for (var guard = 0; guard < 1_000_000; guard++)
        {
            var next = Across(cv, cu); // across edge (u, v) the triangle on the other side
            if (next < 0)
            {
                return;
            }

            var w = Third(next, cu, cv);
            if (w == b)
            {
                break;
            }

            var side = Orient(a, b, w);
            if (Math.Abs(side) < 1e-14 && Between(a, w, b))
            {
                // The segment passes through the vertex w: split it.
                FlipAll(crossing, a, w, depth);
                InsertConstraint(a, w, depth + 1);
                InsertConstraint(w, b, depth + 1);
                return;
            }

            if (side > 0)
            {
                // w is left of a-b: the segment leaves through edge (cu, w)... keep the edge whose ends straddle the segment.
                crossing.Add((cu, w));
                cv = w;
            }
            else
            {
                crossing.Add((w, cv));
                cu = w;
            }

            current = next;
        }

        _ = current;
        FlipAll(crossing, a, b, depth);
    }

    /// <summary>Whether the point w, known to be collinear with a-b, lies strictly between them.</summary>
    private bool Between(int a, int w, int b)
    {
        var dot = (_x[w] - _x[a]) * (_x[b] - _x[a]) + (_y[w] - _y[a]) * (_y[b] - _y[a]);
        var length2 = (_x[b] - _x[a]) * (_x[b] - _x[a]) + (_y[b] - _y[a]) * (_y[b] - _y[a]);
        return dot > 1e-18 && dot < length2 - 1e-18;
    }

    private void FlipAll(List<(int U, int V)> crossing, int a, int b, int depth)
    {
        var queue = new LinkedList<(int U, int V)>(crossing);
        var created = new List<(int, int)>();
        var attempts = 0;
        while (queue.Count > 0 && attempts++ < 100_000)
        {
            var (u, v) = queue.First!.Value;
            queue.RemoveFirst();
            var t1 = Across(u, v);
            var t2 = Across(v, u);
            if (t1 < 0 || t2 < 0)
            {
                continue;
            }

            var p = Third(t1, u, v);
            var q = Third(t2, u, v);
            // Convex quad p, u, q, v (all four turns strictly positive) can be flipped.
            if (Orient(p, u, q) > 0 && Orient(p, q, v) > 0 && Orient(u, q, v) != 0 && Orient(v, p, u) != 0)
            {
                RemoveTriangle(t1);
                RemoveTriangle(t2);
                AddTriangle(p, u, q);
                AddTriangle(p, q, v);
                if (Crosses(a, b, p, q))
                {
                    queue.AddLast((p, q));
                }
                else
                {
                    created.Add((p, q));
                }
            }
            else
            {
                queue.AddLast((u, v));
            }
        }

        // Restore the Delaunay property around the edges the flips created (never across a constraint).
        var changed = true;
        for (var pass = 0; changed && pass < 50; pass++)
        {
            changed = false;
            foreach (var (u, v) in created)
            {
                if (_constrained.Contains(UndirectedKey(u, v)))
                {
                    continue;
                }

                var t1 = Across(u, v);
                var t2 = Across(v, u);
                if (t1 < 0 || t2 < 0)
                {
                    continue;
                }

                var p = Third(t1, u, v);
                var q = Third(t2, u, v);
                if (InCircle(_a[t1], _b[t1], _c[t1], q) && Orient(p, u, q) > 0 && Orient(p, q, v) > 0)
                {
                    RemoveTriangle(t1);
                    RemoveTriangle(t2);
                    AddTriangle(p, u, q);
                    AddTriangle(p, q, v);
                    changed = true;
                    break;
                }
            }
        }
    }

    private List<int> FanAround(int a)
    {
        var result = new List<int>();
        var start = _vertexTriangle[a];
        if (start >= _dead.Count || _dead[start] || !Has(start, a))
        {
            start = -1;
            for (var t = 0; t < _dead.Count; t++)
            {
                if (!_dead[t] && Has(t, a))
                {
                    start = t;
                    break;
                }
            }
        }

        if (start < 0)
        {
            return result;
        }

        var current = start;
        for (var guard = 0; guard < 10_000; guard++)
        {
            result.Add(current);
            Rotate(current, a, out _, out var v);
            var next = Across(a, v); // the triangle across edge (a, v) of the counter-clockwise triangle (a, u, v)
            if (next < 0 || next == start)
            {
                break;
            }

            current = next;
        }

        if (result.Count > 0 && Across(a, RotateV(result[^1], a)) < 0)
        {
            // Open fan (a on the hull): also walk the other way.
            current = start;
            for (var guard = 0; guard < 10_000; guard++)
            {
                Rotate(current, a, out var u, out _);
                var prev = Across(u, a);
                if (prev < 0)
                {
                    break;
                }

                result.Insert(0, prev);
                current = prev;
            }
        }

        return result;
    }

    private int RotateV(int t, int a)
    {
        Rotate(t, a, out _, out var v);
        return v;
    }

    /// <summary>For a triangle containing <paramref name="a"/>, the other two vertices in counter-clockwise order after a.</summary>
    private void Rotate(int t, int a, out int u, out int v)
    {
        if (_a[t] == a)
        {
            u = _b[t];
            v = _c[t];
        }
        else if (_b[t] == a)
        {
            u = _c[t];
            v = _a[t];
        }
        else
        {
            u = _a[t];
            v = _b[t];
        }
    }

    private bool Crosses(int a, int b, int c, int d)
    {
        if (a == c || a == d || b == c || b == d)
        {
            return false;
        }

        return Orient(a, b, c) * Orient(a, b, d) < 0 && Orient(c, d, a) * Orient(c, d, b) < 0;
    }

    // ---- result ----------------------------------------------------------------------------------------------------

    private int[] Collect()
    {
        // Parity flood from the outside: crossing a constrained edge toggles inside/outside.
        var state = new sbyte[_dead.Count]; // 0 unknown, 1 outside, 2 inside
        var queue = new Queue<int>();
        for (var t = 0; t < _dead.Count; t++)
        {
            if (!_dead[t] && (_a[t] >= _n || _b[t] >= _n || _c[t] >= _n))
            {
                state[t] = 1;
                queue.Enqueue(t);
            }
        }

        while (queue.Count > 0)
        {
            var t = queue.Dequeue();
            Step(t, _a[t], _b[t]);
            Step(t, _b[t], _c[t]);
            Step(t, _c[t], _a[t]);
        }

        void Step(int t, int u, int v)
        {
            var n = Across(v, u);
            if (n < 0 || state[n] != 0)
            {
                return;
            }

            var constrained = _constrained.Contains(UndirectedKey(u, v));
            state[n] = (sbyte)(constrained ? 3 - state[t] : state[t]);
            queue.Enqueue(n);
        }

        var result = new List<int>();
        for (var t = 0; t < _dead.Count; t++)
        {
            if (!_dead[t] && state[t] == 2)
            {
                result.Add(_a[t]);
                result.Add(_b[t]);
                result.Add(_c[t]);
            }
        }

        return result.ToArray();
    }
}
