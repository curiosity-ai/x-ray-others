using System.Numerics;

namespace XRay.ThreeD;

/// <summary>
/// Feature edges of a triangle mesh: the lines a CAD renderer draws over the shading. An edge is kept when it has only one
/// triangle (an open boundary), more than two (non-manifold), joins two different source faces, or the two triangles meet at
/// more than the crease angle. Vertices at the same position count as one (faces and strips store their own copies).
/// </summary>
public static class MeshEdges
{
    /// <summary>Returns pairs of indices into <see cref="MeshData.Positions"/>, one pair per edge.</summary>
    public static uint[] Extract(MeshData mesh, float creaseAngleDegrees = 30f, bool faceBoundaries = true, CancellationToken cancellation = default)
    {
        if (mesh.Indices.Length < 3 || mesh.VertexCount == 0)
        {
            return [];
        }

        var canonical = Canonicalise(mesh, out var representative);
        var triangleCount = mesh.Indices.Length / 3;
        var faceOfTriangle = FaceTable(mesh, triangleCount);
        var cosLimit = MathF.Cos(creaseAngleDegrees * MathF.PI / 180f);

        var normals = new Vector3[triangleCount];
        for (var t = 0; t < triangleCount; t++)
        {
            normals[t] = TriangleNormal(mesh.Positions, mesh.Indices[3 * t], mesh.Indices[3 * t + 1], mesh.Indices[3 * t + 2]);
        }

        // key: lower canonical id in the high half. Value: first triangle, second triangle, triangle count.
        var edges = new Dictionary<ulong, (int First, int Second, int Count)>(triangleCount * 2);
        for (var t = 0; t < triangleCount; t++)
        {
            if ((t & 0x3FFF) == 0)
            {
                cancellation.ThrowIfCancellationRequested();
            }

            for (var k = 0; k < 3; k++)
            {
                var a = canonical[mesh.Indices[3 * t + k]];
                var b = canonical[mesh.Indices[3 * t + (k + 1) % 3]];
                if (a == b)
                {
                    continue;
                }

                var key = a < b ? ((ulong)a << 32) | (uint)b : ((ulong)b << 32) | (uint)a;
                edges[key] = edges.TryGetValue(key, out var e)
                    ? (e.First, e.Count == 1 ? t : e.Second, e.Count + 1)
                    : (t, -1, 1);
            }
        }

        var result = new List<uint>();
        foreach (var (key, e) in edges)
        {
            var keep = e.Count != 2;
            if (!keep)
            {
                keep = faceBoundaries && faceOfTriangle is not null && faceOfTriangle[e.First] != faceOfTriangle[e.Second];
            }

            if (!keep)
            {
                var n1 = normals[e.First];
                var n2 = normals[e.Second];
                keep = n1 != Vector3.Zero && n2 != Vector3.Zero && Vector3.Dot(n1, n2) < cosLimit;
            }

            if (keep)
            {
                result.Add(representative[(int)(key >> 32)]);
                result.Add(representative[(int)(key & 0xFFFFFFFF)]);
            }
        }

        return result.ToArray();
    }

    private static int[] Canonicalise(MeshData mesh, out List<uint> representative)
    {
        var box = mesh.ComputeBounds();
        var extent = MathF.Max(box.Size.X, MathF.Max(box.Size.Y, box.Size.Z));
        var tolerance = Math.Max(extent * 1e-6, 1e-9);
        var lookup = new Dictionary<(long, long, long), int>(mesh.VertexCount);
        var canonical = new int[mesh.VertexCount];
        representative = new List<uint>();
        for (var v = 0; v < mesh.VertexCount; v++)
        {
            var key = (
                (long)Math.Round(mesh.Positions[3 * v] / tolerance),
                (long)Math.Round(mesh.Positions[3 * v + 1] / tolerance),
                (long)Math.Round(mesh.Positions[3 * v + 2] / tolerance));
            if (!lookup.TryGetValue(key, out var id))
            {
                id = representative.Count;
                lookup[key] = id;
                representative.Add((uint)v);
            }

            canonical[v] = id;
        }

        return canonical;
    }

    private static int[]? FaceTable(MeshData mesh, int triangleCount)
    {
        if (mesh.Faces.Length == 0)
        {
            return null;
        }

        var table = new int[triangleCount];
        Array.Fill(table, -1);
        for (var f = 0; f < mesh.Faces.Length; f++)
        {
            var first = mesh.Faces[f].FirstIndex / 3;
            var count = mesh.Faces[f].IndexCount / 3;
            for (var t = first; t < first + count && t < triangleCount; t++)
            {
                table[t] = f;
            }
        }

        return table;
    }

    private static Vector3 TriangleNormal(float[] p, uint a, uint b, uint c)
    {
        var pa = new Vector3(p[3 * a], p[3 * a + 1], p[3 * a + 2]);
        var pb = new Vector3(p[3 * b], p[3 * b + 1], p[3 * b + 2]);
        var pc = new Vector3(p[3 * c], p[3 * c + 1], p[3 * c + 2]);
        var n = Vector3.Cross(pb - pa, pc - pa);
        var length = n.Length();
        return length > 1e-20f && float.IsFinite(length) ? n / length : Vector3.Zero;
    }
}
