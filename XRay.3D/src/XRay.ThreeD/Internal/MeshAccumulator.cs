using System.Numerics;

namespace XRay.ThreeD.Internal;

/// <summary>Collects vertices, triangles and line segments for one mesh, within a vertex budget.</summary>
internal sealed class MeshAccumulator
{
    private readonly long _vertexLimit;

    public MeshAccumulator(long triangleLimit)
    {
        _vertexLimit = Math.Min(Math.Max(triangleLimit, 1) * 3, 200_000_000);
    }

    /// <summary>Checked every few thousand vertices and triangles.</summary>
    public CancellationToken Cancel { get; init; }

    public List<float> Positions { get; } = new();

    public List<uint> Triangles { get; } = new();

    public List<uint> Lines { get; } = new();

    public int VertexCount => Positions.Count / 3;

    public int TriangleCount => Triangles.Count / 3;

    public bool IsEmpty => Positions.Count == 0;

    public uint AddVertex(Vector3 p)
    {
        if (VertexCount >= _vertexLimit)
        {
            throw new ModelFormatException("The model has more geometry than the configured limit.");
        }

        Internal.Cancel.Every(Cancel, Positions.Count / 3, 4095);
        Positions.Add(p.X);
        Positions.Add(p.Y);
        Positions.Add(p.Z);
        return (uint)(VertexCount - 1);
    }

    public void AddTriangle(uint a, uint b, uint c)
    {
        if (TriangleCount >= _vertexLimit / 3)
        {
            throw new ModelFormatException("The model has more triangles than the configured limit.");
        }

        Triangles.Add(a);
        Triangles.Add(b);
        Triangles.Add(c);
    }

    public void AddLine(uint a, uint b)
    {
        Lines.Add(a);
        Lines.Add(b);
    }

    /// <summary>A flat polygon with its own vertices (counter-clockwise seen from the side the normal points to).</summary>
    public void AddPolygon(IReadOnlyList<Vector3> corners)
    {
        if (corners.Count < 3)
        {
            return;
        }

        var first = VertexCount;
        foreach (var c in corners)
        {
            AddVertex(c);
        }

        foreach (var t in PolygonTriangulation.Triangulate(corners))
        {
            AddTriangle((uint)(first + t.A), (uint)(first + t.B), (uint)(first + t.C));
        }
    }

    /// <summary>An axis-aligned box in its own coordinates, transformed by <paramref name="m"/> (outward winding).</summary>
    public void AddBox(Vector3 min, Vector3 max, Matrix4x4 m)
    {
        Span<Vector3> c =
        [
            new(min.X, min.Y, min.Z), new(max.X, min.Y, min.Z), new(max.X, max.Y, min.Z), new(min.X, max.Y, min.Z),
            new(min.X, min.Y, max.Z), new(max.X, min.Y, max.Z), new(max.X, max.Y, max.Z), new(min.X, max.Y, max.Z),
        ];
        var first = (int)VertexCount;
        foreach (var p in c)
        {
            AddVertex(Vector3.Transform(p, m));
        }

        ReadOnlySpan<int> t = [0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 1, 2, 6, 1, 6, 5, 2, 3, 7, 2, 7, 6, 3, 0, 4, 3, 4, 7];
        for (var i = 0; i < t.Length; i += 3)
        {
            AddTriangle((uint)(first + t[i]), (uint)(first + t[i + 1]), (uint)(first + t[i + 2]));
        }
    }

    /// <summary>The finished mesh: triangles when there are any, otherwise a line mesh (edges only).</summary>
    public MeshData? ToMesh(string? name, int material = -1)
    {
        if (Positions.Count == 0 || (Triangles.Count == 0 && Lines.Count == 0))
        {
            return null;
        }

        var mesh = new MeshData { Name = name, Positions = Positions.ToArray(), Indices = Triangles.ToArray() };
        if (Triangles.Count > 0 && material >= 0)
        {
            mesh.Groups.Add(new MeshGroup(0, mesh.Indices.Length, material));
        }

        if (Lines.Count > 0)
        {
            mesh.EdgeIndices = Lines.ToArray();
            mesh.EdgeMaterial = material;
        }

        return mesh;
    }
}

/// <summary>Triangulates a planar 3D polygon (any winding, holes not supported) and keeps its winding.</summary>
internal static class PolygonTriangulation
{
    public static List<(int A, int B, int C)> Triangulate(IReadOnlyList<Vector3> corners)
    {
        var result = new List<(int, int, int)>();
        var n = corners.Count;
        if (n < 3)
        {
            return result;
        }

        if (n == 3)
        {
            result.Add((0, 1, 2));
            return result;
        }

        // Newell normal.
        double nx = 0, ny = 0, nz = 0;
        for (var i = 0; i < n; i++)
        {
            Vector3 a = corners[i], b = corners[(i + 1) % n];
            nx += (a.Y - b.Y) * (a.Z + b.Z);
            ny += (a.Z - b.Z) * (a.X + b.X);
            nz += (a.X - b.X) * (a.Y + b.Y);
        }

        var length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (!(length > 1e-20))
        {
            return result;
        }

        var normal = new Vector3((float)(nx / length), (float)(ny / length), (float)(nz / length));
        if (n == 4 && IsConvex(corners, normal))
        {
            result.Add((0, 1, 2));
            result.Add((0, 2, 3));
            return result;
        }

        var u = Vector3.Normalize(Math.Abs(normal.X) < 0.9f ? Vector3.Cross(normal, Vector3.UnitX) : Vector3.Cross(normal, Vector3.UnitY));
        var v = Vector3.Cross(normal, u);
        var points = new (double X, double Y)[n];
        for (var i = 0; i < n; i++)
        {
            points[i] = (Vector3.Dot(corners[i], u), Vector3.Dot(corners[i], v));
        }

        var t = Triangulator.Triangulate(points, [Enumerable.Range(0, n).ToArray()]);
        for (var i = 0; i + 2 < t.Length; i += 3)
        {
            result.Add((t[i], t[i + 1], t[i + 2]));
        }

        return result;
    }

    private static bool IsConvex(IReadOnlyList<Vector3> corners, Vector3 normal)
    {
        for (var i = 0; i < 4; i++)
        {
            var a = corners[(i + 1) % 4] - corners[i];
            var b = corners[(i + 2) % 4] - corners[(i + 1) % 4];
            if (Vector3.Dot(Vector3.Cross(a, b), normal) < 0)
            {
                return false;
            }
        }

        return true;
    }
}
