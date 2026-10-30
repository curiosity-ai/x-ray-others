using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace XRay.ThreeD.Formats.Mesh;

using XRay.ThreeD.Internal;

/// <summary>
/// The Blitz3D model format (<c>BB3D</c>): a tree of length-prefixed chunks (TEXS textures, BRUS brushes, NODE nodes holding MESH, BONE, KEYS and
/// ANIM). Meshes, per-triangle brushes (colour, shininess, first texture), normals, vertex colours, the first UV set and the node hierarchy are read;
/// bones, weights and animation keys are skipped. The format is left-handed with +Z forward: the model is mirrored on Z (positions, node
/// transforms by conjugation, triangle winding reversed) to the right-handed convention of <see cref="Model3D"/>. Files that start with
/// <c>BZ85</c> are the furniture design package's own, zlib-compressed container: only its title is read, and the geometry is reported as not decoded.
/// </summary>
internal static class B3dReader
{
    private const int MaxDepth = 64;

    public static bool IsBlitz(ReadOnlySpan<byte> d) => d.Length >= 8 && d[0] == 'B' && d[1] == 'B' && d[2] == '3' && d[3] == 'D';

    public static bool IsBazis(ReadOnlySpan<byte> d) => d.Length >= 4 && d[0] == 'B' && d[1] == 'Z' && d[2] == '8' && d[3] == '5';

    private sealed class Brush
    {
        public string Name = "";
        public Vector4 Color = Vector4.One;
        public float Shininess;
        public int Texture = -1;
    }

    private sealed class State
    {
        public Model3D Model = null!;
        public LoadOptions Options = null!;
        public List<(string File, int Flags)> Textures = new();
        public List<Brush> Brushes = new();
        public int[] TextureMap = [];
        public long Tick;
        public long Triangles;
        public int NodeCount;
    }

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var span = data.Span;
        var model = new Model3D();
        model.Metadata.Format = ModelFormats.B3d;
        model.Metadata.DocumentKind = "scene";
        if (IsBazis(span))
        {
            ReadBazis(span, model);
            return model;
        }

        if (!IsBlitz(span))
        {
            throw new ModelFormatException("The file is not a Blitz3D model.");
        }

        var length = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
        if (length > span.Length - 8 || length < 4)
        {
            throw new ModelFormatException("The Blitz3D file's length does not match its size.");
        }

        var end = 8 + (int)length;
        var version = BinaryPrimitives.ReadInt32LittleEndian(span[8..]);
        model.Metadata.FormatVersion = $"{version / 100}.{version % 100}";
        var state = new State { Model = model, Options = options };
        var texTable = new List<int>();
        var p = 12;
        while (p + 8 <= end)
        {
            Cancel.Every(options.Cancellation, state.Tick++);
            var (tag, size, body) = Chunk(span, p, end);
            switch (tag)
            {
                case "TEXS": ReadTextures(span, body, body + size, state); break;
                case "BRUS": ReadBrushes(span, body, body + size, state); break;
                case "NODE": ReadNode(span, body, body + size, state, null, 0); break;
            }

            p = body + size;
        }

        model.Metadata.HasGeometry = model.Meshes.Count > 0;
        model.Metadata.Statistics = model.ComputeStatistics();
        model.Metadata.Bounds = model.ComputeBounds();
        return model;
    }

    private static (string Tag, int Size, int Body) Chunk(ReadOnlySpan<byte> d, int p, int end)
    {
        if (p + 8 > end)
        {
            throw new ModelFormatException("A Blitz3D chunk header is truncated.");
        }

        var size = BinaryPrimitives.ReadUInt32LittleEndian(d[(p + 4)..]);
        if (size > (uint)(end - p - 8))
        {
            throw new ModelFormatException("A Blitz3D chunk runs past its parent.");
        }

        return (Encoding.ASCII.GetString(d.Slice(p, 4)), (int)size, p + 8);
    }

    private static string CString(ReadOnlySpan<byte> d, ref int p, int end)
    {
        var start = p;
        while (p < end && d[p] != 0)
        {
            p++;
        }

        if (p >= end)
        {
            throw new ModelFormatException("A Blitz3D string is not terminated.");
        }

        var s = Encoding.Latin1.GetString(d[start..p]);
        p++;
        return s;
    }

    private static float F(ReadOnlySpan<byte> d, ref int p, int end)
    {
        if (p + 4 > end)
        {
            throw new ModelFormatException("A Blitz3D record is truncated.");
        }

        var v = BinaryPrimitives.ReadSingleLittleEndian(d[p..]);
        p += 4;
        return v;
    }

    private static int I(ReadOnlySpan<byte> d, ref int p, int end)
    {
        if (p + 4 > end)
        {
            throw new ModelFormatException("A Blitz3D record is truncated.");
        }

        var v = BinaryPrimitives.ReadInt32LittleEndian(d[p..]);
        p += 4;
        return v;
    }

    private static void ReadTextures(ReadOnlySpan<byte> d, int p, int end, State s)
    {
        while (p < end)
        {
            Cancel.Every(s.Options.Cancellation, s.Tick++);
            var file = CString(d, ref p, end);
            var flags = I(d, ref p, end);
            p += 4 + 20; // blend, then x/y position, x/y scale, rotation
            if (p > end)
            {
                throw new ModelFormatException("A Blitz3D texture record is truncated.");
            }

            if (s.Textures.Count >= 100_000)
            {
                throw new ModelFormatException("The Blitz3D file lists too many textures.");
            }

            s.Textures.Add((file, flags));
        }

        s.TextureMap = new int[s.Textures.Count];
        Array.Fill(s.TextureMap, -2);
    }

    private static void ReadBrushes(ReadOnlySpan<byte> d, int p, int end, State s)
    {
        var textureCount = I(d, ref p, end);
        if (textureCount < 0 || textureCount > 8)
        {
            throw new ModelFormatException("A Blitz3D brush list declares an impossible texture count.");
        }

        while (p < end)
        {
            Cancel.Every(s.Options.Cancellation, s.Tick++);
            var b = new Brush { Name = CString(d, ref p, end) };
            b.Color = new Vector4(F(d, ref p, end), F(d, ref p, end), F(d, ref p, end), F(d, ref p, end));
            b.Shininess = F(d, ref p, end);
            p += 8; // blend, fx
            for (var t = 0; t < textureCount; t++)
            {
                var id = I(d, ref p, end);
                if (t == 0)
                {
                    b.Texture = id;
                }
            }

            if (s.Brushes.Count >= 1_000_000)
            {
                throw new ModelFormatException("The Blitz3D file lists too many brushes.");
            }

            s.Brushes.Add(b);
        }
    }

    private static readonly Matrix4x4 MirrorZ = Matrix4x4.CreateScale(1, 1, -1);

    private static void ReadNode(ReadOnlySpan<byte> d, int p, int end, State s, SceneNode? parent, int depth)
    {
        if (depth > MaxDepth || ++s.NodeCount > 1_000_000)
        {
            throw new ModelFormatException("The Blitz3D node tree is too deep or too large.");
        }

        var name = CString(d, ref p, end);
        var position = new Vector3(F(d, ref p, end), F(d, ref p, end), F(d, ref p, end));
        var scale = new Vector3(F(d, ref p, end), F(d, ref p, end), F(d, ref p, end));
        var w = F(d, ref p, end);
        var q = new Quaternion(F(d, ref p, end), F(d, ref p, end), F(d, ref p, end), w);
        if (!float.IsFinite(q.X + q.Y + q.Z + q.W) || q.LengthSquared() < 1e-12f)
        {
            q = Quaternion.Identity;
        }

        q = Quaternion.Normalize(q);
        var local = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(q) * Matrix4x4.CreateTranslation(position);
        var node = new SceneNode(name) { Transform = MirrorZ * local * MirrorZ };
        if (parent is null)
        {
            s.Model.Roots.Add(node);
        }
        else
        {
            parent.Children.Add(node);
        }

        while (p + 8 <= end)
        {
            Cancel.Every(s.Options.Cancellation, s.Tick++);
            var (tag, size, body) = Chunk(d, p, end);
            switch (tag)
            {
                case "NODE": ReadNode(d, body, body + size, s, node, depth + 1); break;
                case "MESH": ReadMesh(d, body, body + size, s, node); break;
            }

            p = body + size;
        }
    }

    private static void ReadMesh(ReadOnlySpan<byte> d, int p, int end, State s, SceneNode node)
    {
        var meshBrush = I(d, ref p, end);
        var mesh = new MeshData { Name = node.Name };
        var uvSets = 0;
        var uvSize = 0;
        var triangles = new List<(int Brush, uint[] Indices)>();
        while (p + 8 <= end)
        {
            Cancel.Every(s.Options.Cancellation, s.Tick++);
            var (tag, size, body) = Chunk(d, p, end);
            var q = body;
            var stop = body + size;
            if (tag == "VRTS" && mesh.Positions.Length == 0)
            {
                var flags = I(d, ref q, stop);
                uvSets = I(d, ref q, stop);
                uvSize = I(d, ref q, stop);
                if (uvSets < 0 || uvSets > 8 || uvSize < 0 || uvSize > 4)
                {
                    throw new ModelFormatException("A Blitz3D vertex list declares an impossible texture coordinate layout.");
                }

                var floats = 3 + ((flags & 1) != 0 ? 3 : 0) + ((flags & 2) != 0 ? 4 : 0) + uvSets * uvSize;
                var count = (stop - q) / (4 * floats);
                if (count * 4 * floats != stop - q)
                {
                    throw new ModelFormatException("A Blitz3D vertex list has a size that is not a whole number of vertices.");
                }

                mesh.Positions = new float[count * 3];
                if ((flags & 1) != 0)
                {
                    mesh.Normals = new float[count * 3];
                }

                if ((flags & 2) != 0)
                {
                    mesh.Colors = new float[count * 4];
                }

                if (uvSets > 0 && uvSize >= 2)
                {
                    mesh.TexCoords = new float[count * 2];
                }

                for (var v = 0; v < count; v++)
                {
                    Cancel.Every(s.Options.Cancellation, s.Tick++);
                    mesh.Positions[3 * v] = F(d, ref q, stop);
                    mesh.Positions[3 * v + 1] = F(d, ref q, stop);
                    mesh.Positions[3 * v + 2] = -F(d, ref q, stop);
                    if ((flags & 1) != 0)
                    {
                        mesh.Normals[3 * v] = F(d, ref q, stop);
                        mesh.Normals[3 * v + 1] = F(d, ref q, stop);
                        mesh.Normals[3 * v + 2] = -F(d, ref q, stop);
                    }

                    if ((flags & 2) != 0)
                    {
                        for (var c = 0; c < 4; c++)
                        {
                            mesh.Colors[4 * v + c] = F(d, ref q, stop);
                        }
                    }

                    for (var set = 0; set < uvSets; set++)
                    {
                        for (var c = 0; c < uvSize; c++)
                        {
                            var value = F(d, ref q, stop);
                            if (set == 0 && c < 2 && mesh.TexCoords.Length > 0)
                            {
                                mesh.TexCoords[2 * v + c] = value;
                            }
                        }
                    }
                }
            }
            else if (tag == "TRIS")
            {
                var brush = I(d, ref q, stop);
                var count = (stop - q) / 12;
                if (count * 12 != stop - q)
                {
                    throw new ModelFormatException("A Blitz3D triangle list has a size that is not a whole number of triangles.");
                }

                s.Triangles += count;
                if (s.Triangles > s.Options.MaxTriangles)
                {
                    throw new ModelFormatException("The Blitz3D file exceeds the triangle limit.");
                }

                var ids = new uint[count * 3];
                for (var t = 0; t < count; t++)
                {
                    Cancel.Every(s.Options.Cancellation, s.Tick++);
                    var a = I(d, ref q, stop);
                    var b = I(d, ref q, stop);
                    var c = I(d, ref q, stop);
                    if ((uint)a >= (uint)(mesh.Positions.Length / 3) || (uint)b >= (uint)(mesh.Positions.Length / 3) || (uint)c >= (uint)(mesh.Positions.Length / 3))
                    {
                        throw new ModelFormatException("A Blitz3D triangle refers to a vertex that does not exist.");
                    }

                    // Mirroring on Z flips the winding.
                    ids[3 * t] = (uint)a;
                    ids[3 * t + 1] = (uint)c;
                    ids[3 * t + 2] = (uint)b;
                }

                triangles.Add((brush, ids));
            }

            p = body + size;
        }

        if (mesh.Positions.Length == 0 || triangles.Count == 0)
        {
            return;
        }

        var indices = new List<uint>();
        var byMaterial = new Dictionary<int, int>();
        foreach (var (brush, ids) in triangles)
        {
            var id = brush >= 0 ? brush : meshBrush;
            var material = id >= 0 && id < s.Brushes.Count ? MaterialFor(s, id, byMaterial) : -1;
            mesh.Groups.Add(new MeshGroup(indices.Count, ids.Length, material));
            indices.AddRange(ids);
        }

        mesh.Indices = indices.ToArray();
        if (mesh.Groups.Count == 1 && mesh.Groups[0].Material < 0)
        {
            mesh.Groups.Clear();
        }

        s.Model.Meshes.Add(mesh);
        node.Mesh = mesh;
    }

    private static int MaterialFor(State s, int brushId, Dictionary<int, int> cache)
    {
        if (cache.TryGetValue(brushId, out var existing))
        {
            return existing;
        }

        var b = s.Brushes[brushId];
        var m = new ModelMaterial
        {
            Name = b.Name.Length > 0 ? b.Name : null,
            BaseColor = new Vector4(Math.Clamp(b.Color.X, 0, 1), Math.Clamp(b.Color.Y, 0, 1), Math.Clamp(b.Color.Z, 0, 1), Math.Clamp(b.Color.W, 0, 1)),
            Roughness = Math.Clamp(1f - b.Shininess, 0f, 1f),
        };
        if (b.Texture >= 0 && b.Texture < s.Textures.Count)
        {
            var slot = s.TextureMap[b.Texture];
            if (slot == -2)
            {
                slot = -1;
                var file = s.Textures[b.Texture].File;
                var key = file.Replace('\\', '/');
                var bytes = s.Options.ResolveCompanion?.Invoke(key) ?? s.Options.ResolveCompanion?.Invoke(key[(key.LastIndexOf('/') + 1)..]);
                if (bytes is null)
                {
                    s.Model.Warnings.Add($"The texture '{file}' was not found; the material keeps its colour.");
                }
                else if (TextureImages.Create(file, bytes, out var problem, s.Options.Cancellation) is { } texture)
                {
                    slot = s.Model.Textures.Count;
                    s.Model.Textures.Add(texture);
                }
                else if (problem is not null)
                {
                    s.Model.Warnings.Add(problem);
                }

                s.TextureMap[b.Texture] = slot;
            }

            if (slot >= 0)
            {
                m.BaseColorTexture = new ModelTextureRef(slot);
            }
        }

        var index = s.Model.Materials.Count;
        s.Model.Materials.Add(m);
        s.Model.Metadata.MaterialNames.Add(m.Name ?? "Brush " + brushId);
        cache[brushId] = index;
        return index;
    }

    /// <summary>The container is <c>BZ85</c>, a version byte, then length-prefixed UTF-16 field names and values and zlib streams; the title is the "Name" field of the "Article" header.</summary>
    private static void ReadBazis(ReadOnlySpan<byte> d, Model3D model)
    {
        model.Metadata.Application = "Bazis furniture design";
        model.Metadata.FormatVersion = "BZ85";
        // Header strings are stored as UTF-16LE behind a type byte and a 4-byte character count; the title is the longest printable run.
        var marker = FindUtf16(d[..Math.Min(d.Length, 512)]);
        if (marker is not null)
        {
            model.Metadata.Title = marker;
        }

        model.Metadata.Notes.Add("The Bazis container (zlib-compressed object streams) is identified and its title read; its geometry is not decoded.");
    }

    private static string? FindUtf16(ReadOnlySpan<byte> d)
    {
        // The longest run of printable UTF-16LE characters (ASCII range) of at least three characters.
        string? best = null;
        for (var start = 0; start < 2; start++)
        {
            var sb = new StringBuilder();
            for (var i = start; i + 1 < d.Length; i += 2)
            {
                if (d[i + 1] == 0 && d[i] >= 0x20 && d[i] < 0x7F)
                {
                    sb.Append((char)d[i]);
                }
                else
                {
                    if (sb.Length >= 3 && (best is null || sb.Length > best.Length))
                    {
                        best = sb.ToString();
                    }

                    sb.Clear();
                }
            }

            if (sb.Length >= 3 && (best is null || sb.Length > best.Length))
            {
                best = sb.ToString();
            }
        }

        return best;
    }
}
