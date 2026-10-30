using System.Numerics;
using XRay.ThreeD.Formats.Step;

namespace XRay.ThreeD.Internal;

/// <summary>
/// Collects the triangulated faces of a B-rep into one <see cref="MeshData"/>: faces keep their own index range (<see cref="MeshData.Faces"/>, in the
/// order they were added), and the index list is ordered by material so that each material is one contiguous group.
/// </summary>
internal sealed class FaceMeshAssembler
{
    private readonly List<float> _positions = new();
    private readonly List<float> _normals = new();
    private readonly List<uint> _indices = new();
    private readonly List<(int Start, int Count, int Material)> _faces = new();
    private readonly long _maxTriangles;
    private readonly string _format;

    public FaceMeshAssembler(long maxTriangles, string format)
    {
        _maxTriangles = maxTriangles;
        _format = format;
    }

    public int FaceCount => _faces.Count;

    public long TriangleCount => _indices.Count / 3;

    /// <summary>Adds a face (positions and normals are transformed by <paramref name="transform"/> when given); a material of -1 means none.</summary>
    public void Add(FaceMesh mesh, int material, Matrix4x4? transform = null)
    {
        var vertexBase = _positions.Count / 3;
        _positions.Capacity = Math.Max(_positions.Capacity, _positions.Count + 3 * mesh.Positions.Count);
        _normals.Capacity = Math.Max(_normals.Capacity, _normals.Count + 3 * mesh.Normals.Count);
        foreach (var p in mesh.Positions)
        {
            if (transform is { } m)
            {
                var q = Vector3.Transform(new Vector3((float)p.X, (float)p.Y, (float)p.Z), m);
                _positions.Add(q.X);
                _positions.Add(q.Y);
                _positions.Add(q.Z);
            }
            else
            {
                _positions.Add((float)p.X);
                _positions.Add((float)p.Y);
                _positions.Add((float)p.Z);
            }
        }

        foreach (var n in mesh.Normals)
        {
            if (transform is { } m)
            {
                var q = Vector3.TransformNormal(new Vector3((float)n.X, (float)n.Y, (float)n.Z), m);
                q = q.LengthSquared() > 1e-20f ? Vector3.Normalize(q) : q;
                _normals.Add(q.X);
                _normals.Add(q.Y);
                _normals.Add(q.Z);
            }
            else
            {
                _normals.Add((float)n.X);
                _normals.Add((float)n.Y);
                _normals.Add((float)n.Z);
            }
        }

        var start = _indices.Count;
        _indices.Capacity = Math.Max(_indices.Capacity, start + mesh.Indices.Count);
        var flip = transform is { } t && t.GetDeterminant() < 0; // a mirroring transform turns the triangles inside out
        for (var i = 0; i + 2 < mesh.Indices.Count; i += 3)
        {
            _indices.Add((uint)(vertexBase + mesh.Indices[i]));
            _indices.Add((uint)(vertexBase + mesh.Indices[flip ? i + 2 : i + 1]));
            _indices.Add((uint)(vertexBase + mesh.Indices[flip ? i + 1 : i + 2]));
        }

        _faces.Add((start, _indices.Count - start, material));
        if (_indices.Count / 3 > _maxTriangles)
        {
            throw new ModelFormatException($"The {_format} file produces more triangles than the configured limit.");
        }
    }

    public MeshData? ToMesh(string? name)
    {
        if (_indices.Count == 0)
        {
            return null;
        }

        // Order faces by material so that each material is one contiguous range; keep each face's own range.
        var order = new int[_faces.Count];
        for (var i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }

        Array.Sort(order, (a, b) => _faces[a].Material != _faces[b].Material ? _faces[a].Material.CompareTo(_faces[b].Material) : a.CompareTo(b));
        var sorted = new uint[_indices.Count];
        var meshFaces = new MeshFace[_faces.Count];
        var cursor = 0;
        var mesh = new MeshData { Name = name, Positions = _positions.ToArray(), Normals = _normals.ToArray() };
        var groupStart = 0;
        for (var k = 0; k < order.Length; k++)
        {
            var original = order[k];
            var d = _faces[original];
            for (var t = 0; t < d.Count; t++)
            {
                sorted[cursor + t] = _indices[d.Start + t];
            }

            meshFaces[original] = new MeshFace(cursor, d.Count);
            cursor += d.Count;
            if (k + 1 == order.Length || _faces[order[k + 1]].Material != d.Material)
            {
                mesh.Groups.Add(new MeshGroup(groupStart, cursor - groupStart, d.Material));
                groupStart = cursor;
            }
        }

        mesh.Indices = sorted;
        mesh.Faces = meshFaces;
        if (mesh.Groups.Count == 1 && mesh.Groups[0].Material < 0)
        {
            mesh.Groups.Clear();
        }

        return mesh;
    }
}
