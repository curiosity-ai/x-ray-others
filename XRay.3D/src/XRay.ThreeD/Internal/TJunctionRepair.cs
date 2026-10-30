using System.Numerics;

namespace XRay.ThreeD.Internal;

/// <summary>
/// Closes the cracks a polygon-splitting operation leaves: a vertex that lies on the interior of another triangle's open edge (a T-junction)
/// is inserted into that edge, so the two sides share the same edge pieces. Boolean operations on polygon soups (BSP trees) produce them
/// wherever a split polygon meets an unsplit neighbour; the surface is geometrically closed but topologically open, which a renderer shows as
/// nothing and a solid test counts as a hole.
/// </summary>
/// <remarks>Only edges used by a single triangle are examined, so a closed mesh costs one pass over its edges. Triangles keep their winding and group.</remarks>
internal static class TJunctionRepair
{
    /// <summary>
    /// Rewrites <paramref name="indices"/> (triangles) so that every open edge passes through no vertex without sharing it. Vertices are indices into
    /// <paramref name="positions"/> (x, y, z per vertex) and must already be welded: two vertices at one place must be one index.
    /// Returns the new triangle list; the input is returned unchanged when there is nothing to repair.
    /// </summary>
    public static uint[] Repair(float[] positions, uint[] indices, IReadOnlyList<(int Start, int Count)> groups, out (int Start, int Count)[] newGroups, CancellationToken cancel)
    {
        newGroups = groups.ToArray();
        var vertexCount = positions.Length / 3;
        if (indices.Length < 12 || vertexCount < 4)
        {
            return indices;
        }

        // Edge use counts by undirected pair.
        var uses = new Dictionary<ulong, int>(indices.Length);
        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            Cancel.Every(cancel, i / 3);
            for (var k = 0; k < 3; k++)
            {
                uint a = indices[i + k], b = indices[i + ((k + 1) % 3)];
                if (a == b)
                {
                    continue;
                }

                var key = Key(a, b);
                uses[key] = uses.GetValueOrDefault(key) + 1;
            }
        }

        var open = new List<(uint A, uint B)>();
        var boundaryVertices = new HashSet<uint>();
        foreach (var (key, count) in uses)
        {
            if (count == 1)
            {
                var a = (uint)(key >> 32);
                var b = (uint)key;
                open.Add((a, b));
                boundaryVertices.Add(a);
                boundaryVertices.Add(b);
            }
        }

        if (open.Count < 3)
        {
            return indices;
        }

        // Bounding box of the open vertices and a uniform grid over them.
        var box = new BoundingBox(new Vector3(float.MaxValue), new Vector3(float.MinValue));
        foreach (var v in boundaryVertices)
        {
            box = box.Include(Position(positions, v));
        }

        var diagonal = (box.Max - box.Min).Length();
        if (!(diagonal > 0) || !float.IsFinite(diagonal))
        {
            return indices;
        }

        var eps = diagonal * 1e-5;
        var cell = Math.Max(diagonal / Math.Max(1.0, Math.Cbrt(boundaryVertices.Count) * 2), eps * 4);
        long CellOf(double x, double min) => (long)Math.Floor((x - min) / cell);
        static ulong CellKey(long x, long y, long z) => ((ulong)(x & 0x1FFFFF) << 42) | ((ulong)(y & 0x1FFFFF) << 21) | (ulong)(z & 0x1FFFFF);
        var grid = new Dictionary<ulong, List<uint>>();
        foreach (var v in boundaryVertices)
        {
            var p = Position(positions, v);
            var key = CellKey(CellOf(p.X, box.Min.X), CellOf(p.Y, box.Min.Y), CellOf(p.Z, box.Min.Z));
            if (!grid.TryGetValue(key, out var list))
            {
                grid[key] = list = new List<uint>();
            }

            list.Add(v);
        }

        var splits = new Dictionary<ulong, List<uint>>();
        var seen = new HashSet<uint>();
        var edgeNumber = 0;
        foreach (var (a, b) in open)
        {
            Cancel.Every(cancel, edgeNumber++);
            var pa = Position(positions, a);
            var pb = Position(positions, b);
            var ab = pb - pa;
            var length2 = ab.LengthSquared();
            if (length2 <= 0)
            {
                continue;
            }

            seen.Clear();
            List<(float T, uint V)>? found = null;
            var lo = Vector3.Min(pa, pb) - new Vector3((float)eps);
            var hi = Vector3.Max(pa, pb) + new Vector3((float)eps);
            long x0 = CellOf(lo.X, box.Min.X), x1 = CellOf(hi.X, box.Min.X), y0 = CellOf(lo.Y, box.Min.Y), y1 = CellOf(hi.Y, box.Min.Y), z0 = CellOf(lo.Z, box.Min.Z), z1 = CellOf(hi.Z, box.Min.Z);
            var span = (x1 - x0 + 1) * (y1 - y0 + 1) * (z1 - z0 + 1);

            void Test(List<uint> list)
            {
                foreach (var v in list)
                {
                    if (v == a || v == b || !seen.Add(v))
                    {
                        continue;
                    }

                    var p = Position(positions, v);
                    var t = Vector3.Dot(p - pa, ab) / length2;
                    if (t <= 1e-6f || t >= 1 - 1e-6f)
                    {
                        continue;
                    }

                    if ((p - (pa + (ab * t))).Length() <= eps)
                    {
                        (found ??= new()).Add((t, v));
                    }
                }
            }

            if (span <= 512)
            {
                for (var x = x0; x <= x1; x++)
                {
                    for (var y = y0; y <= y1; y++)
                    {
                        for (var z = z0; z <= z1; z++)
                        {
                            if (grid.TryGetValue(CellKey(x, y, z), out var list))
                            {
                                Test(list);
                            }
                        }
                    }
                }
            }
            else
            {
                // A long edge: walk it and look at the cells around each sample instead of its whole bounding box.
                var steps = (int)Math.Min(100_000, Math.Ceiling(Math.Sqrt(length2) / cell) * 2) + 1;
                for (var s = 0; s <= steps; s++)
                {
                    var p = pa + (ab * ((float)s / steps));
                    long cx = CellOf(p.X, box.Min.X), cy = CellOf(p.Y, box.Min.Y), cz = CellOf(p.Z, box.Min.Z);
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        for (var dy = -1; dy <= 1; dy++)
                        {
                            for (var dz = -1; dz <= 1; dz++)
                            {
                                if (grid.TryGetValue(CellKey(cx + dx, cy + dy, cz + dz), out var list))
                                {
                                    Test(list);
                                }
                            }
                        }
                    }
                }
            }

            if (found is { Count: > 0 })
            {
                found.Sort((l, r) => l.T.CompareTo(r.T));
                // Stored in the direction low index to high index.
                var chain = found.Select(f => f.V).ToList();
                if (a > b)
                {
                    chain.Reverse();
                }

                splits[Key(a, b)] = chain;
            }
        }

        if (splits.Count == 0)
        {
            return indices;
        }

        var result = new List<uint>(indices.Length + (splits.Count * 6));
        var outGroups = new List<(int Start, int Count)>();
        void Emit(uint a, uint b, uint c, int depth)
        {
            if (depth < 4)
            {
                // The first edge of this triangle that has vertices to take in.
                for (var k = 0; k < 3; k++)
                {
                    var u = k == 0 ? a : k == 1 ? b : c;
                    var v = k == 0 ? b : k == 1 ? c : a;
                    var w = k == 0 ? c : k == 1 ? a : b;
                    if (splits.TryGetValue(Key(u, v), out var chain))
                    {
                        var previous = u;
                        for (var i = 0; i <= chain.Count; i++)
                        {
                            var next = i == chain.Count ? v : (u < v ? chain[i] : chain[chain.Count - 1 - i]);
                            Emit(previous, next, w, depth + 1);
                            previous = next;
                        }

                        return;
                    }
                }
            }

            result.Add(a);
            result.Add(b);
            result.Add(c);
        }

        var ranges = groups.Count > 0 ? groups : new[] { (0, indices.Length) };
        foreach (var (start, count) in ranges)
        {
            var first = result.Count;
            for (var i = start; i + 2 < start + count; i += 3)
            {
                Cancel.Every(cancel, i / 3);
                Emit(indices[i], indices[i + 1], indices[i + 2], 0);
            }

            outGroups.Add((first, result.Count - first));
        }

        newGroups = outGroups.ToArray();
        return result.ToArray();
    }

    private static ulong Key(uint a, uint b) => a < b ? ((ulong)a << 32) | b : ((ulong)b << 32) | a;

    private static Vector3 Position(float[] p, uint i) => new(p[3 * i], p[3 * i + 1], p[3 * i + 2]);
}
