using System.Buffers;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.Step;

/// <summary>
/// AP242 tessellated geometry: TESSELLATED_SOLID and TESSELLATED_SHELL (lists of faces), TRIANGULATED_FACE and COMPLEX_TRIANGULATED_FACE
/// (triangles, strips and fans over a COORDINATES_LIST, optionally through a point index and with per-vertex normals) and the two
/// surface-set forms that have no face link. The triangles are used as stored; nothing is re-tessellated.
/// </summary>
internal sealed class StepTessellated
{
    private readonly StepFile _file;
    private readonly CancellationToken _cancel;
    private readonly Dictionary<int, double[]> _coordinates = new();

    public List<string> Warnings { get; } = new();

    public StepTessellated(StepFile file, CancellationToken cancel)
    {
        _file = file;
        _cancel = cancel;
    }

    /// <summary>True for the entity types this class turns into triangles.</summary>
    public static bool IsTessellated(StepEntity e) => e.Type is "TESSELLATED_SOLID" or "TESSELLATED_SHELL" or "TRIANGULATED_FACE" or "COMPLEX_TRIANGULATED_FACE"
        or "TRIANGULATED_SURFACE_SET" or "COMPLEX_TRIANGULATED_SURFACE_SET";

    private static bool IsFace(StepEntity e) => e.Type is "TRIANGULATED_FACE" or "COMPLEX_TRIANGULATED_FACE" or "TRIANGULATED_SURFACE_SET" or "COMPLEX_TRIANGULATED_SURFACE_SET";

    /// <summary>The entity a solid or shell was derived from (its geometric link), or -1; a B-rep drawn from the same representation makes the tessellation redundant.</summary>
    public int LinkOf(StepEntity item) => item.Type switch
    {
        "TESSELLATED_SOLID" => item.Arg(2).ToRef,
        "TRIANGULATED_FACE" or "COMPLEX_TRIANGULATED_FACE" => item.Arg(4).ToRef,
        _ => -1,
    };

    /// <summary>The faces of a tessellated solid, shell or face (a shell can nest, to a bounded depth).</summary>
    public IEnumerable<StepEntity> FacesOf(StepEntity item)
    {
        if (IsFace(item))
        {
            yield return item;
            yield break;
        }

        var stack = new Stack<(StepEntity Item, int Depth)>();
        stack.Push((item, 0));
        var visited = 0;
        while (stack.Count > 0)
        {
            var (current, depth) = stack.Pop();
            if (IsFace(current))
            {
                yield return current;
                continue;
            }

            if (depth > 8 || current.Type is not ("TESSELLATED_SOLID" or "TESSELLATED_SHELL"))
            {
                continue;
            }

            var refs = current.Arg(1).Items;
            for (var i = refs.Count - 1; i >= 0; i--)
            {
                Cancel.Every(_cancel, visited++);
                if (refs[i].IsRef && _file.Get(refs[i].Ref) is { } child)
                {
                    stack.Push((child, depth + 1));
                }
            }
        }
    }

    private double[] Coordinates(int id)
    {
        if (_coordinates.TryGetValue(id, out var cached))
        {
            return cached;
        }

        double[] result = [];
        // COORDINATES_LIST(name, npoints, position_coords)
        if (_file.Get(id) is { Type: "COORDINATES_LIST" } list)
        {
            var points = list.Arg(2).Items;
            result = new double[points.Count * 3];
            for (var i = 0; i < points.Count; i++)
            {
                Cancel.Every(_cancel, i);
                var c = points[i].Items;
                for (var k = 0; k < 3 && k < c.Count; k++)
                {
                    result[3 * i + k] = c[k].Num;
                }
            }
        }

        _coordinates[id] = result;
        return result;
    }

    /// <summary>Triangles of one face with positions and normals of its own vertices; null when it holds no usable triangle.</summary>
    public FaceMesh? Build(StepEntity face)
    {
        _cancel.ThrowIfCancellationRequested();
        var isSet = face.Type is "TRIANGULATED_SURFACE_SET" or "COMPLEX_TRIANGULATED_SURFACE_SET";
        var complex = face.Type is "COMPLEX_TRIANGULATED_FACE" or "COMPLEX_TRIANGULATED_SURFACE_SET";
        // Faces: (name, coordinates, pnmax, normals, geometric_link, pnindex, triangles | strips, fans); sets lack the link.
        var link = isSet ? 0 : 1;
        var coords = Coordinates(face.Arg(1).ToRef);
        var normalArg = face.Arg(3).Items;
        var pnindex = face.Arg(4 + link).Items;
        var pointCount = coords.Length / 3;
        var local = pnindex.Count > 0 ? pnindex.Count : pointCount;
        if (local == 0)
        {
            return null;
        }

        var map = ArrayPool<int>.Shared.Rent(local);
        try
        {
            for (var k = 0; k < local; k++)
            {
                Cancel.Every(_cancel, k);
                var index = (pnindex.Count > 0 ? (long)pnindex[k].Num : k + 1L) - 1;
                map[k] = index >= 0 && index < pointCount ? (int)index : -1;
            }

            var mesh = new FaceMesh();
            mesh.Positions.Capacity = local;
            for (var k = 0; k < local; k++)
            {
                mesh.Positions.Add(map[k] < 0 ? V3.Zero : new V3(coords[3 * map[k]], coords[3 * map[k] + 1], coords[3 * map[k] + 2]));
            }

            // One normal for the whole face, or one per local vertex; anything else is ignored.
            var perVertex = normalArg.Count == local;
            var single = normalArg.Count == 1;
            mesh.Indices.Capacity = 0;
            var skipped = 0;
            var emitted = 0;
            void Add(long a, long b, long c)
            {
                if (a < 1 || b < 1 || c < 1 || a > local || b > local || c > local || map[a - 1] < 0 || map[b - 1] < 0 || map[c - 1] < 0 || a == b || b == c || a == c)
                {
                    skipped++;
                    return;
                }

                mesh.Indices.Add((int)(a - 1));
                mesh.Indices.Add((int)(b - 1));
                mesh.Indices.Add((int)(c - 1));
                Cancel.Every(_cancel, emitted++);
            }

            if (!complex)
            {
                var triangles = face.Arg(5 + link).Items;
                mesh.Indices.Capacity = triangles.Count * 3;
                for (var t = 0; t < triangles.Count; t++)
                {
                    var tri = triangles[t].Items;
                    if (tri.Count >= 3)
                    {
                        Add((long)tri[0].Num, (long)tri[1].Num, (long)tri[2].Num);
                    }
                    else
                    {
                        skipped++;
                    }
                }
            }
            else
            {
                var strips = face.Arg(5 + link).Items;
                foreach (var strip in strips)
                {
                    var s = strip.Items;
                    for (var k = 0; k + 2 < s.Count; k++)
                    {
                        // Alternate triangles of a strip are wound the other way round.
                        if ((k & 1) == 0)
                        {
                            Add((long)s[k].Num, (long)s[k + 1].Num, (long)s[k + 2].Num);
                        }
                        else
                        {
                            Add((long)s[k + 1].Num, (long)s[k].Num, (long)s[k + 2].Num);
                        }
                    }
                }

                foreach (var fan in face.Arg(6 + link).Items)
                {
                    var f = fan.Items;
                    for (var k = 1; k + 1 < f.Count; k++)
                    {
                        Add((long)f[0].Num, (long)f[k].Num, (long)f[k + 1].Num);
                    }
                }
            }

            if (skipped > 0)
            {
                Warnings.Add($"Tessellated face #{face.Id} has {skipped} triangle(s) with invalid indices (skipped).");
            }

            if (mesh.Indices.Count == 0)
            {
                return null;
            }

            FillNormals(mesh, normalArg, perVertex, single);
            return mesh;
        }
        finally
        {
            ArrayPool<int>.Shared.Return(map);
        }
    }

    private void FillNormals(FaceMesh mesh, IReadOnlyList<StepArg> normalArg, bool perVertex, bool single)
    {
        var count = mesh.Positions.Count;
        var provided = perVertex || single;
        V3 Provided(int vertex)
        {
            var c = normalArg[perVertex ? vertex : 0].Items;
            return new V3(c.Count > 0 ? c[0].Num : 0, c.Count > 1 ? c[1].Num : 0, c.Count > 2 ? c[2].Num : 0);
        }

        // Triangles follow the stored normals: a triangle wound against its vertices' normals is turned round.
        var accumulated = new V3[provided ? 0 : count];
        var indices = mesh.Indices;
        for (var t = 0; t + 2 < indices.Count; t += 3)
        {
            Cancel.Every(_cancel, t / 3);
            V3 a = mesh.Positions[indices[t]], b = mesh.Positions[indices[t + 1]], c = mesh.Positions[indices[t + 2]];
            var geometric = (b - a).Cross(c - a);
            if (provided)
            {
                var reference = perVertex ? Provided(indices[t]) + Provided(indices[t + 1]) + Provided(indices[t + 2]) : Provided(0);
                if (geometric.Dot(reference) < 0)
                {
                    (indices[t + 1], indices[t + 2]) = (indices[t + 2], indices[t + 1]);
                }
            }
            else
            {
                // Area-weighted: the cross product's length is twice the triangle's area.
                accumulated[indices[t]] += geometric;
                accumulated[indices[t + 1]] += geometric;
                accumulated[indices[t + 2]] += geometric;
            }
        }

        mesh.Normals.Capacity = count;
        for (var v = 0; v < count; v++)
        {
            var n = provided ? Provided(v) : accumulated[v];
            mesh.Normals.Add(n.Length > 1e-12 ? n.Normalized() : new V3(0, 0, 1));
        }
    }
}
