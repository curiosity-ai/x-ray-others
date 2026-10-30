using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace XRay.ThreeD.Formats.SolidWorks;

/// <summary>
/// Decodes the display tessellation SolidWorks saves in <c>Contents/DisplayLists</c> (the mesh it draws
/// itself, in meters). The stream is a serialised object graph; this reader does not walk the graph but
/// finds the two record kinds it needs by their shape:
/// <list type="bullet">
/// <item>A face: <c>[4,8,2,n]</c> + n strip lengths, then <c>[12,100,2,k]</c> + k xyz floats, then
/// <c>[12,100,2,k]</c> + k normal floats, where the strip lengths sum to k.</item>
/// <item>A component instance: a UTF-16 string containing '@' (<c>Pin-1@Gear</c>) followed by a transform
/// (type byte 0: translation + scale; type 1: 3x3 rotation + translation + scale, all doubles).</item>
/// </list>
/// Faces after an instance record belong to it; an instance with no faces repeats the geometry of the
/// last instance that has some, under its own transform.
/// </summary>
internal static class SwDisplayLists
{
    private static readonly byte[] StripHeader = [4, 0, 0, 0, 8, 0, 0, 0, 2, 0, 0, 0];
    private static readonly byte[] FloatHeader = [12, 0, 0, 0, 100, 0, 0, 0, 2, 0, 0, 0];
    private static readonly byte[] StringMarker = [0xFF, 0xFE, 0xFF];

    internal sealed class Face
    {
        public int VertexCount;
        public int[] StripLengths = [];
        public int PositionOffset;
        public int NormalOffset;
        public int Offset;

        /// <summary>Index into <see cref="Result.Appearances"/>, or -1 when the file saved no colour for it.</summary>
        public int Appearance = -1;
    }

    /// <summary>A material the document saved: its name, colour and texture category ("metal", "plastic"...).</summary>
    internal sealed record Appearance(string Name, int Rgb, string? Category);

    internal sealed class Geometry
    {
        public List<Face> Faces { get; } = new();
    }

    internal sealed class Instance
    {
        public string? Name;
        public Matrix4x4 Transform = Matrix4x4.Identity;
        public int Geometry = -1;
    }

    internal sealed class Result
    {
        public List<Geometry> Geometries { get; } = new();

        public List<Instance> Instances { get; } = new();

        public List<Appearance> Appearances { get; } = new();
    }

    /// <summary>Finds faces and instances. With <paramref name="useInstances"/> false (a part) everything is one geometry.</summary>
    public static Result Decode(byte[] stream, bool useInstances, CancellationToken cancellation = default)
    {
        var faces = FindFaces(stream, cancellation);
        var result = new Result();
        if (faces.Count == 0)
        {
            return result;
        }

        var (instances, appearances) = FindStringRecords(stream, useInstances);
        result.Appearances.AddRange(appearances.Select(a => a.Appearance));
        AssignAppearances(faces, appearances);
        if (instances.Count == 0)
        {
            var geometry = new Geometry();
            geometry.Faces.AddRange(faces);
            result.Geometries.Add(geometry);
            result.Instances.Add(new Instance { Geometry = 0 });
            return result;
        }

        // Faces before the first instance record belong to no component: keep them as an unnamed root.
        var orphan = faces.Where(f => f.Offset < instances[0].Offset).ToList();
        if (orphan.Count > 0)
        {
            var g = new Geometry();
            g.Faces.AddRange(orphan);
            result.Geometries.Add(g);
            result.Instances.Add(new Instance { Geometry = 0 });
        }

        var current = -1;
        var faceIndex = orphan.Count;
        for (var i = 0; i < instances.Count; i++)
        {
            var end = i + 1 < instances.Count ? instances[i + 1].Offset : int.MaxValue;
            var geometry = new Geometry();
            while (faceIndex < faces.Count && faces[faceIndex].Offset < end)
            {
                geometry.Faces.Add(faces[faceIndex++]);
            }

            if (geometry.Faces.Count > 0)
            {
                result.Geometries.Add(geometry);
                current = result.Geometries.Count - 1;
            }

            if (current < 0)
            {
                continue;
            }

            result.Instances.Add(new Instance { Name = instances[i].Name, Transform = instances[i].Transform, Geometry = current });
        }

        return result;
    }

    private sealed record InstanceRecord(int Offset, string Name, Matrix4x4 Transform);

    private sealed record AppearanceRecord(int Offset, Appearance Appearance);

    /// <summary>Colour is stateful: a face takes the latest appearance record before it in the stream.</summary>
    private static void AssignAppearances(List<Face> faces, List<AppearanceRecord> appearances)
    {
        var next = 0;
        var current = -1;
        foreach (var face in faces)
        {
            while (next < appearances.Count && appearances[next].Offset < face.Offset)
            {
                current = next++;
            }

            face.Appearance = current;
        }
    }

    private static List<Face> FindFaces(byte[] data, CancellationToken cancellation)
    {
        var faces = new List<Face>();
        var span = data.AsSpan();
        var position = 0;
        while (position < span.Length - 28)
        {
            var hit = span[position..].IndexOf(StripHeader);
            if (hit < 0)
            {
                break;
            }

            var at = position + hit;
            position = at + 1;
            cancellation.ThrowIfCancellationRequested();
            if (!TryReadFace(span, at, out var face, out var end))
            {
                continue;
            }

            faces.Add(face);
            position = end;
        }

        return faces;
    }

    private static bool TryReadFace(ReadOnlySpan<byte> span, int at, out Face face, out int end)
    {
        face = null!;
        end = 0;
        var n = U32(span, at + 12);
        if (n == 0 || n > 4_000_000)
        {
            return false;
        }

        var positionHeader = (long)at + 16 + 4L * n;
        if (positionHeader + 16 > span.Length || !span.Slice((int)positionHeader, 12).SequenceEqual(FloatHeader))
        {
            return false;
        }

        var k = U32(span, (int)positionHeader + 12);
        if (k < 3 || k > 16_000_000)
        {
            return false;
        }

        var normalHeader = positionHeader + 16 + 12L * k;
        if (normalHeader + 16 + 12L * k > span.Length
            || !span.Slice((int)normalHeader, 12).SequenceEqual(FloatHeader)
            || U32(span, (int)normalHeader + 12) != k)
        {
            return false;
        }

        var lengths = new int[n];
        long sum = 0;
        for (var i = 0; i < n; i++)
        {
            var length = U32(span, at + 16 + 4 * i);
            if (length > k)
            {
                return false;
            }

            lengths[i] = (int)length;
            sum += length;
        }

        if (sum != k)
        {
            return false;
        }

        face = new Face
        {
            Offset = at,
            VertexCount = (int)k,
            StripLengths = lengths,
            PositionOffset = (int)positionHeader + 16,
            NormalOffset = (int)normalHeader + 16,
        };
        end = (int)(normalHeader + 16 + 12L * k);
        return true;
    }

    private static (List<InstanceRecord> Instances, List<AppearanceRecord> Appearances) FindStringRecords(byte[] data, bool wantInstances)
    {
        var instances = new List<InstanceRecord>();
        var appearances = new List<AppearanceRecord>();
        var span = data.AsSpan();
        var position = 0;
        while (position < span.Length - 8)
        {
            var hit = span[position..].IndexOf(StringMarker);
            if (hit < 0)
            {
                break;
            }

            var at = position + hit;
            position = at + 3;
            var o = at + 3;
            int length = span[o];
            o++;
            if (length == 0xFF)
            {
                if (o + 2 > span.Length)
                {
                    continue;
                }

                length = BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(o, 2));
                o += 2;
            }

            if (length < 1 || o + 2L * length + 1 > span.Length)
            {
                continue;
            }

            var raw = span.Slice(o, 2 * length);
            var end = o + 2 * length;
            if (wantInstances && length >= 3 && ContainsAt(raw) && TryReadTransform(span, end, out var transform))
            {
                var name = Encoding.Unicode.GetString(raw);
                if (name.Contains('@') && !name.Any(char.IsControl))
                {
                    instances.Add(new InstanceRecord(at, name, transform));
                    continue;
                }
            }

            // Appearance: name, type byte 2 or 3, RGB, then the float 1.0.
            if (end + 9 <= span.Length && (span[end] is 2 or 3) && span.Slice(end + 4, 5).SequenceEqual(AppearanceTail))
            {
                var name = Encoding.Unicode.GetString(raw);
                if (!name.Any(char.IsControl))
                {
                    var rgb = (span[end + 1] << 16) | (span[end + 2] << 8) | span[end + 3];
                    appearances.Add(new AppearanceRecord(at, new Appearance(name, rgb, FindTextureCategory(span, end + 9))));
                }
            }
        }

        return (instances, appearances);
    }

    private static readonly byte[] AppearanceTail = [0, 0, 0, 0x80, 0x3F];

    /// <summary>The texture path that follows an appearance ("&lt;SystemTexture&gt;\graphics\materials\metal\aluminum\x.p2m") names its family.</summary>
    private static string? FindTextureCategory(ReadOnlySpan<byte> span, int from)
    {
        var limit = Math.Min(span.Length - 4, from + 400);
        for (var i = from; i < limit; i++)
        {
            if (span[i] != 0xFF || span[i + 1] != 0xFE || span[i + 2] != 0xFF)
            {
                continue;
            }

            int length = span[i + 3];
            if (length is 0 or 0xFF || i + 4 + 2 * length > span.Length)
            {
                return null;
            }

            var path = Encoding.Unicode.GetString(span.Slice(i + 4, 2 * length));
            const string marker = "materials\\";
            var at = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at < 0)
            {
                return null;
            }

            var rest = path[(at + marker.Length)..];
            var slash = rest.IndexOf('\\');
            return slash > 0 ? rest[..slash].ToLowerInvariant() : null;
        }

        return null;
    }

    private static bool ContainsAt(ReadOnlySpan<byte> utf16)
    {
        for (var i = 0; i + 1 < utf16.Length; i += 2)
        {
            if (utf16[i] == (byte)'@' && utf16[i + 1] == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadTransform(ReadOnlySpan<byte> span, int at, out Matrix4x4 transform)
    {
        transform = Matrix4x4.Identity;
        var type = span[at];
        double[] r;
        double[] t;
        double scale;
        if (type == 1)
        {
            if (at + 1 + 13 * 8 > span.Length)
            {
                return false;
            }

            r = new double[9];
            for (var i = 0; i < 9; i++)
            {
                r[i] = D(span, at + 1 + 8 * i);
            }

            t = [D(span, at + 73), D(span, at + 81), D(span, at + 89)];
            scale = D(span, at + 97);
        }
        else if (type == 0)
        {
            if (at + 1 + 4 * 8 > span.Length)
            {
                return false;
            }

            r = [1, 0, 0, 0, 1, 0, 0, 0, 1];
            t = [D(span, at + 1), D(span, at + 9), D(span, at + 17)];
            scale = D(span, at + 25);
        }
        else
        {
            return false;
        }

        if (!(scale > 1e-6 && scale < 1e6) || r.Any(v => !double.IsFinite(v) || Math.Abs(v) > 1.000001)
            || t.Any(v => !double.IsFinite(v) || Math.Abs(v) > 1e6))
        {
            return false;
        }

        // world = x * R * scale + T with x a row vector, which is System.Numerics' convention.
        transform = new Matrix4x4(
            (float)(r[0] * scale), (float)(r[1] * scale), (float)(r[2] * scale), 0,
            (float)(r[3] * scale), (float)(r[4] * scale), (float)(r[5] * scale), 0,
            (float)(r[6] * scale), (float)(r[7] * scale), (float)(r[8] * scale), 0,
            (float)t[0], (float)t[1], (float)t[2], 1);
        return true;
    }

    /// <summary>
    /// Builds one mesh from a geometry's faces, unrolling each strip into triangles. Faces are grouped by material
    /// (<paramref name="materialOf"/> maps a face's appearance index, or -1, to a material index, or -1); each face keeps its own index range.
    /// </summary>
    public static MeshData BuildMesh(byte[] stream, Geometry geometry, string? name, Func<int, int>? materialOf = null)
    {
        var vertexTotal = 0;
        var faces = geometry.Faces;
        var indexCounts = new int[faces.Count];
        var materials = new int[faces.Count];
        for (var f = 0; f < faces.Count; f++)
        {
            vertexTotal += faces[f].VertexCount;
            var triangles = 0;
            foreach (var length in faces[f].StripLengths)
            {
                triangles += Math.Max(0, length - 2);
            }

            indexCounts[f] = triangles * 3;
            materials[f] = materialOf?.Invoke(faces[f].Appearance) ?? -1;
        }

        // Stable order by material so each material is one contiguous index range.
        var order = Enumerable.Range(0, faces.Count).OrderBy(f => materials[f]).ToArray();
        var positions = new float[vertexTotal * 3];
        var normals = new float[vertexTotal * 3];
        var indices = new uint[indexCounts.Sum()];
        var meshFaces = new MeshFace[faces.Count];
        var vertexStart = new int[faces.Count];
        var vertex = 0;
        for (var f = 0; f < faces.Count; f++)
        {
            vertexStart[f] = vertex;
            MemoryMarshal.Cast<byte, float>(stream.AsSpan(faces[f].PositionOffset, faces[f].VertexCount * 12)).CopyTo(positions.AsSpan(vertex * 3));
            MemoryMarshal.Cast<byte, float>(stream.AsSpan(faces[f].NormalOffset, faces[f].VertexCount * 12)).CopyTo(normals.AsSpan(vertex * 3));
            vertex += faces[f].VertexCount;
        }

        var mesh = new MeshData { Name = name, Positions = positions, Normals = normals, Indices = indices };
        var index = 0;
        var groupStart = 0;
        var groupMaterial = order.Length > 0 ? materials[order[0]] : -1;
        foreach (var f in order)
        {
            if (materials[f] != groupMaterial)
            {
                mesh.Groups.Add(new MeshGroup(groupStart, index - groupStart, groupMaterial));
                groupStart = index;
                groupMaterial = materials[f];
            }

            meshFaces[f] = new MeshFace(index, indexCounts[f]);
            var v = vertexStart[f];
            foreach (var length in faces[f].StripLengths)
            {
                for (var i = 0; i < length - 2; i++)
                {
                    var a = (uint)(v + i);
                    var b = (uint)(v + i + 1);
                    var c = (uint)(v + i + 2);
                    if ((i & 1) == 0)
                    {
                        indices[index++] = a;
                        indices[index++] = b;
                    }
                    else
                    {
                        indices[index++] = b;
                        indices[index++] = a;
                    }

                    indices[index++] = c;
                }

                v += length;
            }
        }

        if (index > groupStart)
        {
            mesh.Groups.Add(new MeshGroup(groupStart, index - groupStart, groupMaterial));
        }

        mesh.Faces = meshFaces;
        return mesh;
    }

    private static uint U32(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(o, 4));

    private static double D(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadDoubleLittleEndian(s.Slice(o, 8));
}
