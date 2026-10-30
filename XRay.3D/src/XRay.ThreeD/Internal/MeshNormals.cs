using System.Numerics;

namespace XRay.ThreeD.Internal;

/// <summary>Derives normals for meshes that have none (welded STL, bare PLY): smooth (area weighted, per vertex) or flat (per triangle, vertices unwelded).</summary>
internal static class MeshNormals
{
    /// <summary>
    /// The mesh to write: the same mesh when it has normals, has source faces (CAD meshes keep their face structure), is not a triangle mesh
    /// or <paramref name="mode"/> is <see cref="GlbNormals.Keep"/>; otherwise a copy with normals.
    /// </summary>
    public static MeshData Apply(MeshData mesh, GlbNormals mode, CancellationToken cancel)
    {
        if (mode == GlbNormals.Keep || mesh.Indices.Length == 0 || mesh.Normals.Length == mesh.Positions.Length || mesh.Faces.Length > 0)
        {
            return mesh;
        }

        return mode == GlbNormals.Smooth ? Smooth(mesh, cancel) : Flat(mesh, cancel);
    }

    private static Vector3 Corner(float[] p, uint i) => new(p[3 * i], p[3 * i + 1], p[3 * i + 2]);

    private static MeshData Copy(MeshData mesh) => new()
    {
        Name = mesh.Name,
        Positions = mesh.Positions,
        Colors = mesh.Colors,
        TexCoords = mesh.TexCoords,
        Indices = mesh.Indices,
        Faces = mesh.Faces,
        EdgeIndices = mesh.EdgeIndices,
        EdgeMaterial = mesh.EdgeMaterial,
    };

    private static MeshData Smooth(MeshData mesh, CancellationToken cancel)
    {
        var p = mesh.Positions;
        var normals = new float[p.Length];
        for (var t = 0; t + 2 < mesh.Indices.Length; t += 3)
        {
            Cancel.Every(cancel, t / 3, 4095);
            uint a = mesh.Indices[t], b = mesh.Indices[t + 1], c = mesh.Indices[t + 2];
            var n = Vector3.Cross(Corner(p, b) - Corner(p, a), Corner(p, c) - Corner(p, a)); // length = twice the area: weights by area
            foreach (var v in (ReadOnlySpan<uint>)[a, b, c])
            {
                normals[3 * v] += n.X;
                normals[3 * v + 1] += n.Y;
                normals[3 * v + 2] += n.Z;
            }
        }

        Normalise(normals);
        var result = Copy(mesh);
        result.Normals = normals;
        foreach (var g in mesh.Groups)
        {
            result.Groups.Add(g);
        }

        return result;
    }

    private static MeshData Flat(MeshData mesh, CancellationToken cancel)
    {
        var count = mesh.Indices.Length;
        var p = mesh.Positions;
        var positions = new float[count * 3];
        var normals = new float[count * 3];
        var colors = mesh.Colors.Length == mesh.VertexCount * 4 ? new float[count * 4] : [];
        var uv = mesh.TexCoords.Length == mesh.VertexCount * 2 ? new float[count * 2] : [];
        var indices = new uint[count];
        for (var t = 0; t + 2 < count; t += 3)
        {
            Cancel.Every(cancel, t / 3, 4095);
            var n = Vector3.Cross(Corner(p, mesh.Indices[t + 1]) - Corner(p, mesh.Indices[t]), Corner(p, mesh.Indices[t + 2]) - Corner(p, mesh.Indices[t]));
            var length = n.Length();
            n = length > 0 ? n / length : Vector3.UnitZ;
            for (var k = 0; k < 3; k++)
            {
                var source = mesh.Indices[t + k];
                var o = t + k;
                Array.Copy(p, 3 * source, positions, 3 * o, 3);
                normals[3 * o] = n.X;
                normals[3 * o + 1] = n.Y;
                normals[3 * o + 2] = n.Z;
                if (colors.Length > 0)
                {
                    Array.Copy(mesh.Colors, 4 * source, colors, 4 * o, 4);
                }

                if (uv.Length > 0)
                {
                    Array.Copy(mesh.TexCoords, 2 * source, uv, 2 * o, 2);
                }

                indices[o] = (uint)o;
            }
        }

        var result = new MeshData { Name = mesh.Name, Positions = positions, Normals = normals, Colors = colors, TexCoords = uv, Indices = indices };
        foreach (var g in mesh.Groups)
        {
            result.Groups.Add(g);
        }

        return result;
    }

    private static void Normalise(float[] normals)
    {
        for (var i = 0; i + 2 < normals.Length; i += 3)
        {
            var length = MathF.Sqrt(normals[i] * normals[i] + normals[i + 1] * normals[i + 1] + normals[i + 2] * normals[i + 2]);
            if (length > 1e-20f)
            {
                normals[i] /= length;
                normals[i + 1] /= length;
                normals[i + 2] /= length;
            }
            else
            {
                normals[i] = 0;
                normals[i + 1] = 0;
                normals[i + 2] = 1;
            }
        }
    }
}
