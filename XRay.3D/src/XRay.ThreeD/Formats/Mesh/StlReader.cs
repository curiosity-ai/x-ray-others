using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace XRay.ThreeD.Formats.Mesh;

using XRay.ThreeD.Internal;

/// <summary>Binary and ASCII STL. Vertices are welded by position and normals are left to the viewer (flat shading).</summary>
internal static class StlReader
{
    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var span = data.Span;
        var model = new Model3D();
        model.Metadata.Format = ModelFormats.Stl;
        model.Metadata.DocumentKind = "mesh";
        model.Metadata.UpAxis = UpAxis.Z;
        model.Metadata.Notes.Add("STL stores no units or up axis; Z-up is assumed.");

        var builder = new WeldedMesh();
        string? name = null;
        if (IsBinary(span))
        {
            var count = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(80, 4));
            if (count > options.MaxTriangles)
            {
                throw new ModelFormatException("The STL has more triangles than the configured limit.");
            }

            var header = Encoding.ASCII.GetString(span[..80]).Trim('\0', ' ');
            if (header.Length > 0 && header.All(c => c >= 0x20 && c < 0x7F) && !header.StartsWith("COLOR=", StringComparison.Ordinal))
            {
                name = header;
            }

            var tick35 = 0L;
            for (var i = 0; i < count; i++)
            {
                Cancel.Every(options.Cancellation, tick35++);
                options.Cancellation.ThrowIfCancellationRequested();
                var o = 84 + 50 * i + 12;
                for (var v = 0; v < 3; v++)
                {
                    builder.AddVertex(
                        BinaryPrimitives.ReadSingleLittleEndian(span.Slice(o + 12 * v, 4)),
                        BinaryPrimitives.ReadSingleLittleEndian(span.Slice(o + 12 * v + 4, 4)),
                        BinaryPrimitives.ReadSingleLittleEndian(span.Slice(o + 12 * v + 8, 4)));
                }
            }
        }
        else
        {
            name = ReadAscii(span, builder, options);
        }

        if (builder.TriangleCount == 0)
        {
            throw new ModelFormatException("The STL file holds no triangles.");
        }

        var mesh = builder.ToMesh(name);
        model.Meshes.Add(mesh);
        model.Roots.Add(new SceneNode(name) { Mesh = mesh });
        model.Metadata.Title = name;
        model.Metadata.HasGeometry = true;
        model.Metadata.Statistics = model.ComputeStatistics();
        model.Metadata.Bounds = mesh.ComputeBounds();
        return model;
    }

    private static bool IsBinary(ReadOnlySpan<byte> span)
    {
        if (span.Length < 84)
        {
            return false;
        }

        var count = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(80, 4));
        return 84L + 50L * count == span.Length;
    }

    private static string? ReadAscii(ReadOnlySpan<byte> span, WeldedMesh builder, LoadOptions options)
    {
        var text = Encoding.UTF8.GetString(span);
        string? name = null;
        var pending = new float[9];
        var n = 0;
        var tick85 = 0L;
        foreach (var raw in text.AsSpan().EnumerateLines())
        {
            Cancel.Every(options.Cancellation, tick85++);
            var line = raw.Trim();
            if (line.StartsWith("solid", StringComparison.OrdinalIgnoreCase))
            {
                if (name is null && line.Length > 5)
                {
                    name = line[5..].Trim().ToString();
                }

                continue;
            }

            if (!line.StartsWith("vertex", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rest = line[6..];
            var parts = rest.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 3)
            {
                throw new ModelFormatException("An STL vertex line has fewer than three coordinates.");
            }

            for (var i = 0; i < 3; i++)
            {
                pending[n++] = float.Parse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            if (n == 9)
            {
                builder.AddVertex(pending[0], pending[1], pending[2]);
                builder.AddVertex(pending[3], pending[4], pending[5]);
                builder.AddVertex(pending[6], pending[7], pending[8]);
                n = 0;
                options.Cancellation.ThrowIfCancellationRequested();
                if (builder.TriangleCount > options.MaxTriangles)
                {
                    throw new ModelFormatException("The STL has more triangles than the configured limit.");
                }
            }
        }

        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}

/// <summary>Collects a triangle soup and merges vertices that have identical coordinates.</summary>
internal sealed class WeldedMesh
{
    private readonly Dictionary<(uint, uint, uint), uint> _lookup = new();
    private readonly List<float> _positions = new();
    private readonly List<uint> _indices = new();

    public int TriangleCount => _indices.Count / 3;

    public void AddVertex(float x, float y, float z)
    {
        var key = (BitConverter.SingleToUInt32Bits(x), BitConverter.SingleToUInt32Bits(y), BitConverter.SingleToUInt32Bits(z));
        if (!_lookup.TryGetValue(key, out var index))
        {
            index = (uint)(_positions.Count / 3);
            _lookup[key] = index;
            _positions.Add(x);
            _positions.Add(y);
            _positions.Add(z);
        }

        _indices.Add(index);
    }

    public MeshData ToMesh(string? name) => new() { Name = name, Positions = _positions.ToArray(), Indices = _indices.ToArray() };
}
