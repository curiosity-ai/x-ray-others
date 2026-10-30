using System.Buffers;

namespace XRay.ThreeD;

/// <summary>
/// The edge structure of a triangle mesh after welding its vertices by position, which is what decides whether the mesh bounds a solid.
/// </summary>
/// <remarks>
/// Vertices are welded by position (rounded to <c>weldTolerance</c> times the bounding box diagonal), so a mesh that stores a vertex once per
/// face or per normal still counts as connected. Every undirected edge is then tallied by how often triangles traverse it in each direction.
/// An edge a solid's boundary uses is traversed once each way. Where two closed shells touch (two boxes sharing an edge) an edge is used by four
/// triangles, twice each way: that edge is <see cref="NonManifoldEdges"/> but the mesh is still <see cref="IsClosed"/> and its volume is the
/// sum of the shells'.
/// </remarks>
/// <param name="Triangles">Triangles in the mesh (including degenerate ones).</param>
/// <param name="OpenEdges">Edges used by exactly one triangle: a hole or a sheet's border. Zero for a closed mesh.</param>
/// <param name="NonManifoldEdges">Edges used by more than two triangles (touching or overlapping shells); informational.</param>
/// <param name="InconsistentEdges">Edges used by two or more triangles that do not traverse it equally often in both directions: neighbouring triangles wound against each other.</param>
/// <param name="DegenerateTriangles">Triangles whose corners weld to fewer than three vertices; they carry no edges and are ignored.</param>
/// <param name="SignedVolume">The signed volume the triangles enclose, in the mesh's own units cubed: positive when the triangles face outward, negative for an inside-out solid, meaningless for an open mesh.</param>
public readonly record struct MeshTopology(long Triangles, int OpenEdges, int NonManifoldEdges, int InconsistentEdges, int DegenerateTriangles, double SignedVolume)
{
    /// <summary>True when the mesh has no border and no edge is wound against its neighbour: it encloses space, so its volume is a quantity of the model.</summary>
    public bool IsClosed => Triangles - DegenerateTriangles > 0 && OpenEdges == 0 && InconsistentEdges == 0;

    /// <summary>True when the mesh is closed and its triangles face outward (positive volume).</summary>
    public bool IsSolid => IsClosed && SignedVolume > 0;

    /// <summary>True when the mesh is closed but its triangles face inward.</summary>
    public bool IsInsideOut => IsClosed && SignedVolume < 0;

    /// <summary>The default weld tolerance, as a fraction of the mesh's bounding box diagonal.</summary>
    public const double DefaultWeldTolerance = 1e-5;

    /// <summary>Analyses <paramref name="mesh"/>. Returns an empty result (no triangles) for a point or line mesh.</summary>
    public static MeshTopology Analyze(MeshData mesh, double weldTolerance, CancellationToken cancellation)
    {
        var indices = mesh.Indices;
        var triangles = indices.Length / 3;
        var positions = mesh.Positions;
        var vertexCount = positions.Length / 3;
        if (triangles == 0 || vertexCount == 0 || triangles > int.MaxValue / 4)
        {
            return new MeshTopology(triangles, 0, 0, 0, 0, 0);
        }

        // Weld scale: the rounding step is weldTolerance times the bounding box diagonal.
        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
        for (var i = 0; i < vertexCount; i++)
        {
            Internal.Cancel.Every(cancellation, i);
            double x = positions[3 * i], y = positions[3 * i + 1], z = positions[3 * i + 2];
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
            minZ = Math.Min(minZ, z);
            maxZ = Math.Max(maxZ, z);
        }

        var diagonal = Math.Sqrt(((maxX - minX) * (maxX - minX)) + ((maxY - minY) * (maxY - minY)) + ((maxZ - minZ) * (maxZ - minZ)));
        var scale = diagonal > 0 && double.IsFinite(diagonal) ? 1.0 / (diagonal * weldTolerance) : 1e6;

        var capacity = 16;
        while (capacity < vertexCount * 2L)
        {
            capacity <<= 1;
        }

        var table = ArrayPool<int>.Shared.Rent(capacity);
        var weldedId = ArrayPool<int>.Shared.Rent(vertexCount);
        var keys = ArrayPool<ulong>.Shared.Rent(3 * triangles);
        try
        {
            Array.Fill(table, -1, 0, capacity);
            var mask = capacity - 1;
            var next = 0;
            for (var i = 0; i < vertexCount; i++)
            {
                Internal.Cancel.Every(cancellation, i);
                var qx = Math.Round(positions[3 * i] * scale);
                var qy = Math.Round(positions[3 * i + 1] * scale);
                var qz = Math.Round(positions[3 * i + 2] * scale);
                var slot = (int)(Hash(qx, qy, qz) & (uint)mask);
                while (true)
                {
                    var representative = table[slot];
                    if (representative < 0)
                    {
                        table[slot] = i;
                        weldedId[i] = next++;
                        break;
                    }

                    if (Math.Round(positions[3 * representative] * scale) == qx && Math.Round(positions[3 * representative + 1] * scale) == qy && Math.Round(positions[3 * representative + 2] * scale) == qz)
                    {
                        weldedId[i] = weldedId[representative];
                        break;
                    }

                    slot = (slot + 1) & mask;
                }
            }

            // One key per directed edge: low id, high id, direction bit. Sorted, the two directions of an edge are adjacent.
            var count = 0;
            var degenerate = 0;
            double volume = 0;
            for (var t = 0; t < triangles; t++)
            {
                Internal.Cancel.Every(cancellation, t);
                uint ia = indices[3 * t], ib = indices[3 * t + 1], ic = indices[3 * t + 2];
                if (ia >= vertexCount || ib >= vertexCount || ic >= vertexCount)
                {
                    degenerate++;
                    continue;
                }

                double ax = positions[3 * ia], ay = positions[3 * ia + 1], az = positions[3 * ia + 2];
                double bx = positions[3 * ib], by = positions[3 * ib + 1], bz = positions[3 * ib + 2];
                double cx = positions[3 * ic], cy = positions[3 * ic + 1], cz = positions[3 * ic + 2];
                volume += ((ax * ((by * cz) - (bz * cy))) - (ay * ((bx * cz) - (bz * cx))) + (az * ((bx * cy) - (by * cx)))) / 6.0;
                int a = weldedId[ia], b = weldedId[ib], c = weldedId[ic];
                if (a == b || b == c || a == c)
                {
                    degenerate++;
                    continue;
                }

                keys[count++] = EdgeKey(a, b);
                keys[count++] = EdgeKey(b, c);
                keys[count++] = EdgeKey(c, a);
            }

            Array.Sort(keys, 0, count);
            int open = 0, nonManifold = 0, inconsistent = 0;
            for (var i = 0; i < count;)
            {
                Internal.Cancel.Every(cancellation, i);
                var edge = keys[i] >> 1;
                int forward = 0, backward = 0;
                while (i < count && (keys[i] >> 1) == edge)
                {
                    if ((keys[i] & 1) == 0)
                    {
                        forward++;
                    }
                    else
                    {
                        backward++;
                    }

                    i++;
                }

                var uses = forward + backward;
                if (uses == 1)
                {
                    open++;
                }
                else
                {
                    if (uses > 2)
                    {
                        nonManifold++;
                    }

                    if (forward != backward)
                    {
                        inconsistent++;
                    }
                }
            }

            return new MeshTopology(triangles, open, nonManifold, inconsistent, degenerate, volume);
        }
        finally
        {
            ArrayPool<int>.Shared.Return(table);
            ArrayPool<int>.Shared.Return(weldedId);
            ArrayPool<ulong>.Shared.Return(keys);
        }
    }

    private static ulong EdgeKey(int from, int to) =>
        from < to ? ((ulong)(uint)from << 33) | ((ulong)(uint)to << 2) : ((ulong)(uint)to << 33) | ((ulong)(uint)from << 2) | 1UL;

    private static uint Hash(double x, double y, double z)
    {
        // Adding zero turns -0 into +0: the two compare equal and must hash alike.
        var h = (ulong)BitConverter.DoubleToInt64Bits(x + 0.0) * 0x9E3779B97F4A7C15UL;
        h ^= h >> 29;
        h += (ulong)BitConverter.DoubleToInt64Bits(y + 0.0) * 0xC2B2AE3D27D4EB4FUL;
        h ^= h >> 31;
        h += (ulong)BitConverter.DoubleToInt64Bits(z + 0.0) * 0x165667B19E3779F9UL;
        h ^= h >> 32;
        h *= 0x9E3779B97F4A7C15UL;
        return (uint)(h >> 32);
    }
}
