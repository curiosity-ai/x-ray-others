using System.IO.Compression;
using System.Numerics;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.Rhino;

/// <summary>Decoders for the geometry classes of a 3dm file: points, curves sampled to line segments, point clouds and meshes.</summary>
internal static class RhinoGeometry
{
    public static readonly Guid PointClass = new("c3101a1d-f157-11d3-bfe7-0010830122f0");
    public static readonly Guid PointCloudClass = new("2488f347-f8fa-11d3-bfec-0010830122f0");
    public static readonly Guid LineCurveClass = new("4ed7d4db-e947-11d3-bfe5-0010830122f0");
    public static readonly Guid NurbsCurveClass = new("4ed7d4dd-e947-11d3-bfe5-0010830122f0");
    public static readonly Guid PolylineCurveClass = new("4ed7d4e6-e947-11d3-bfe5-0010830122f0");
    public static readonly Guid MeshClass = new("4ed7d4e4-e947-11d3-bfe5-0010830122f0");
    public static readonly Guid ArcCurveClass = new("cf33be2a-09b4-11d4-bffb-0010830122f0");
    public static readonly Guid NurbsCurveClass2 = new("5eaf1119-0b51-11d4-bffe-0010830122f0");
    public static readonly Guid PolyCurveClass = new("4ed7d4e0-e947-11d3-bfe5-0010830122f0");
    public static readonly Guid Brep6Class = new("f06fc243-a32a-4608-9dd8-a7d2c4ce2a36");
    public static readonly Guid ExtrusionClass = new("36f53175-72b8-4d47-bf1f-b4e6fc24f4b9");
    public static readonly Guid SubDClass = new("f09ba4d9-455b-42c3-ba3b-e6ccacef853b");
    public static readonly Guid NurbsSurfaceClass = new("4ed7d4de-e947-11d3-bfe5-0010830122f0");
    public static readonly Guid BrepClass = new("60b5dbc5-e660-11d3-bfe4-0010830122f0");
    public static readonly Guid InstanceRefClass = new("f9cfb638-b9d4-4340-87e3-c56e7865d96a");

    /// <summary>Line segments as index pairs into the positions.</summary>
    public sealed class Lines
    {
        public List<float> Positions { get; } = new();

        public List<uint> Segments { get; } = new();

        public void Polyline(IReadOnlyList<Vector3> points, bool close = false)
        {
            if (points.Count < 2)
            {
                return;
            }

            var first = (uint)(Positions.Count / 3);
            foreach (var p in points)
            {
                Positions.Add(p.X);
                Positions.Add(p.Y);
                Positions.Add(p.Z);
            }

            for (var i = 0; i + 1 < points.Count; i++)
            {
                Segments.Add(first + (uint)i);
                Segments.Add(first + (uint)i + 1);
            }

            if (close && points.Count > 2)
            {
                Segments.Add(first + (uint)points.Count - 1);
                Segments.Add(first);
            }
        }
    }

    public static Vector3 ReadPoint(RhinoReaderCursor r) => new((float)r.Double(), (float)r.Double(), (float)r.Double());

    /// <summary>ON_Point: a version byte and three doubles.</summary>
    public static Vector3 Point(RhinoReaderCursor r)
    {
        r.Byte();
        return ReadPoint(r);
    }

    /// <summary>ON_PointCloud: version, count, then the points (colours, normals and flags follow and are not read).</summary>
    public static List<Vector3> PointCloud(RhinoReaderCursor r, CancellationToken cancel)
    {
        r.Byte();
        var count = r.Int();
        if (count < 0 || (long)count * 24 > r.Remaining)
        {
            throw new ModelFormatException("A point cloud declares more points than it holds.");
        }

        var list = new List<Vector3>(count);
        for (var i = 0; i < count; i++)
        {
            Cancel.Every(cancel, i);
            list.Add(ReadPoint(r));
        }

        return list;
    }

    /// <summary>ON_LineCurve: version, two points, domain, dimension.</summary>
    public static void LineCurve(RhinoReaderCursor r, Lines into)
    {
        r.Byte();
        into.Polyline([ReadPoint(r), ReadPoint(r)]);
    }

    /// <summary>ON_PolylineCurve: version, point count, points, parameter count, parameters, dimension.</summary>
    public static void PolylineCurve(RhinoReaderCursor r, Lines into, CancellationToken cancel)
    {
        r.Byte();
        var count = r.Int();
        if (count < 0 || (long)count * 24 > r.Remaining)
        {
            throw new ModelFormatException("A polyline declares more points than it holds.");
        }

        var points = new List<Vector3>(count);
        for (var i = 0; i < count; i++)
        {
            Cancel.Every(cancel, i);
            points.Add(ReadPoint(r));
        }

        into.Polyline(points);
    }

    /// <summary>ON_ArcCurve: version, the arc (plane of 16 doubles, radius, angle interval), domain, dimension.</summary>
    public static void ArcCurve(RhinoReaderCursor r, Lines into, LoadOptions options)
    {
        r.Byte();
        var origin = ReadPoint(r);
        var xAxis = ReadPoint(r);
        var yAxis = ReadPoint(r);
        r.Skip(7 * 8); // z axis and the plane equation
        var radius = r.Double();
        r.Skip(9 * 8); // the circle's three points
        var a0 = r.Double();
        var a1 = r.Double();
        if (!double.IsFinite(radius) || !double.IsFinite(a0) || !double.IsFinite(a1) || radius < 0 || Math.Abs(a1 - a0) > 2 * Math.PI + 1e-6)
        {
            throw new ModelFormatException("An arc in the 3dm file has impossible values.");
        }

        var n = Math.Clamp((int)Math.Ceiling(Math.Abs(a1 - a0) / Math.Max(options.AngularDeflection, 0.01)), 4, 720);
        var points = new List<Vector3>(n + 1);
        for (var i = 0; i <= n; i++)
        {
            var a = a0 + (a1 - a0) * i / n;
            points.Add(origin + xAxis * (float)(radius * Math.Cos(a)) + yAxis * (float)(radius * Math.Sin(a)));
        }

        into.Polyline(points);
    }

    /// <summary>ON_NurbsCurve: version, dimension, rational flag, order, control point count, reserved, bounding box, knots (without the two end knots), control points.</summary>
    public static void NurbsCurve(RhinoReaderCursor r, Lines into, LoadOptions options)
    {
        r.Byte();
        var dim = r.Int();
        var rational = r.Int() != 0;
        var order = r.Int();
        var cvCount = r.Int();
        r.Skip(8 + 48);
        var knotCount = r.Int();
        if (dim < 1 || dim > 3 || order < 2 || order > 64 || cvCount < order || cvCount > 1 << 20 || knotCount != order + cvCount - 2 || (long)knotCount * 8 > r.Remaining)
        {
            throw new ModelFormatException("A NURBS curve in the 3dm file has impossible dimensions.");
        }

        var knots = new double[knotCount + 2];
        for (var i = 0; i < knotCount; i++)
        {
            knots[i + 1] = r.Double();
        }

        knots[0] = knots[1];
        knots[^1] = knots[^2];
        for (var i = 1; i < knots.Length; i++)
        {
            if (!(knots[i] >= knots[i - 1]))
            {
                throw new ModelFormatException("A NURBS curve has decreasing knots.");
            }
        }

        var stored = r.Int();
        if (stored != cvCount)
        {
            throw new ModelFormatException("A NURBS curve repeats a different control point count.");
        }

        var stride = dim + (rational ? 1 : 0);
        if ((long)cvCount * stride * 8 > r.Remaining)
        {
            throw new ModelFormatException("A NURBS curve is truncated.");
        }

        var cv = new double[cvCount * 4];
        for (var i = 0; i < cvCount; i++)
        {
            double x = 0, y = 0, z = 0, w = 1;
            var c = new double[stride];
            for (var k = 0; k < stride; k++)
            {
                c[k] = r.Double();
            }

            x = c[0];
            y = dim > 1 ? c[1] : 0;
            z = dim > 2 ? c[2] : 0;
            if (rational)
            {
                w = c[dim];
            }

            cv[i * 4] = x;
            cv[i * 4 + 1] = y;
            cv[i * 4 + 2] = z;
            cv[i * 4 + 3] = w;
        }

        var degree = order - 1;
        var t0 = knots[degree];
        var t1 = knots[cvCount];
        if (!(t1 > t0))
        {
            return;
        }

        var spans = Math.Max(cvCount - degree, 1);
        var perSpan = degree == 1 ? 1 : Math.Clamp((int)Math.Ceiling(Math.PI / 2 / Math.Max(options.AngularDeflection, 0.01)) * 2, 4, 64);
        var samples = Math.Min(spans * perSpan, 20_000);
        var points = new List<Vector3>(samples + 1);
        var basis = new double[order];
        Span<double> left = stackalloc double[order];
        Span<double> right = stackalloc double[order];
        for (var s = 0; s <= samples; s++)
        {
            Cancel.Every(options.Cancellation, s);
            var t = s == samples ? t1 : t0 + (t1 - t0) * s / samples;
            // Find the span.
            var span = degree;
            while (span < cvCount - 1 && t >= knots[span + 1])
            {
                span++;
            }

            // Cox-de Boor basis functions of the span.
            basis[0] = 1;
            for (var j = 1; j <= degree; j++)
            {
                left[j] = t - knots[span + 1 - j];
                right[j] = knots[span + j] - t;
                double saved = 0;
                for (var rr = 0; rr < j; rr++)
                {
                    var denominator = right[rr + 1] + left[j - rr];
                    var temp = denominator == 0 ? 0 : basis[rr] / denominator;
                    basis[rr] = saved + right[rr + 1] * temp;
                    saved = left[j - rr] * temp;
                }

                basis[j] = saved;
            }

            double px = 0, py = 0, pz = 0, pw = 0;
            for (var j = 0; j <= degree; j++)
            {
                var index = span - degree + j;
                if (index < 0 || index >= cvCount)
                {
                    continue;
                }

                var b = basis[j];
                var w = rational ? cv[index * 4 + 3] : 1;
                // The stored coordinates are homogeneous: already multiplied by the weight.
                px += b * cv[index * 4];
                py += b * cv[index * 4 + 1];
                pz += b * cv[index * 4 + 2];
                pw += b * w;
            }

            if (rational)
            {
                if (Math.Abs(pw) < 1e-300)
                {
                    continue;
                }

                px /= pw;
                py /= pw;
                pz /= pw;
            }

            points.Add(new Vector3((float)px, (float)py, (float)pz));
        }

        into.Polyline(points);
    }

    /// <summary>
    /// ON_Mesh (chunk version 3): counts, parameter boxes, the face array (quads, with the smallest index size that fits), then
    /// compressed buffers for vertices, normals, texture coordinates, curvature and colours. Returns null for a mesh with no faces.
    /// </summary>
    public static MeshData? Mesh(RhinoReaderCursor r, LoadOptions options)
    {
        var start = r.Pos;
        var version = r.Byte();
        if ((version >> 4) != 3)
        {
            throw new UnsupportedModelFormatException(ModelFormats.Rhino, $"A mesh of class version {version >> 4}.{version & 15} is not supported.");
        }

        var vcount = r.Int();
        var fcount = r.Int();
        if (vcount < 0 || fcount < 0 || vcount > 100_000_000 || fcount > options.MaxTriangles)
        {
            throw new ModelFormatException("A mesh declares an impossible size.");
        }

        if (vcount == 0 || fcount == 0)
        {
            return null;
        }

        // Find the face array: an index size of 1, 2 or 4 bytes, and after the faces a vertex buffer of exactly vcount floats times three.
        var faceStart = -1;
        var indexSize = 0;
        var limit = r.End;
        for (var s = start + 100; s < start + 400 && faceStart < 0; s++)
        {
            if (s + 4 > limit)
            {
                break;
            }

            var k = BitConverter.ToInt32(r.Span.Slice(s, 4));
            if (k != 1 && k != 2 && k != 4)
            {
                continue;
            }

            var after = (long)s + 4 + (long)fcount * 4 * k;
            if (after + 9 > limit || (k == 1 && vcount > 256) || (k == 2 && vcount > 65536))
            {
                continue;
            }

            var size = BitConverter.ToUInt32(r.Span.Slice((int)after, 4));
            var method = r.Span[(int)after + 8];
            if (size == (uint)vcount * 12 && method <= 1)
            {
                faceStart = s + 4;
                indexSize = k;
            }
        }

        if (faceStart < 0)
        {
            throw new ModelFormatException("The face array of a mesh could not be located.");
        }

        r.Pos = faceStart;
        var indices = new List<uint>(fcount * 3 + 3);
        for (var f = 0; f < fcount; f++)
        {
            Cancel.Every(options.Cancellation, f);
            uint a = Index(r, indexSize), b = Index(r, indexSize), c = Index(r, indexSize), d = Index(r, indexSize);
            if (a >= vcount || b >= vcount || c >= vcount || d >= vcount)
            {
                throw new ModelFormatException("A mesh face refers to a vertex that does not exist.");
            }

            indices.Add(a);
            indices.Add(b);
            indices.Add(c);
            if (c != d)
            {
                indices.Add(a);
                indices.Add(c);
                indices.Add(d);
            }
        }

        var mesh = new MeshData { Indices = indices.ToArray() };
        mesh.Positions = ReadFloats(r, vcount * 3, options.Cancellation) ?? throw new ModelFormatException("A mesh has no vertices.");
        try
        {
            var normals = ReadFloats(r, vcount * 3, options.Cancellation);
            if (normals is not null)
            {
                mesh.Normals = normals;
            }

            var texture = ReadFloats(r, vcount * 2, options.Cancellation);
            if (texture is not null)
            {
                for (var i = 1; i < texture.Length; i += 2)
                {
                    texture[i] = 1 - texture[i];
                }

                mesh.TexCoords = texture;
            }

            ReadFloats(r, vcount * 4, options.Cancellation, optionalSize: true); // curvature
            var colors = ReadBytes(r, vcount * 4, options.Cancellation);
            if (colors is not null)
            {
                mesh.Colors = new float[vcount * 4];
                for (var i = 0; i < vcount; i++)
                {
                    mesh.Colors[i * 4] = colors[i * 4] / 255f;
                    mesh.Colors[i * 4 + 1] = colors[i * 4 + 1] / 255f;
                    mesh.Colors[i * 4 + 2] = colors[i * 4 + 2] / 255f;
                    mesh.Colors[i * 4 + 3] = 1f;
                }
            }
        }
        catch (ModelFormatException)
        {
            // The optional buffers after the vertices are best effort.
        }

        return mesh;
    }

    private static uint Index(RhinoReaderCursor r, int size) => size switch
    {
        1 => r.Byte(),
        2 => (uint)(r.Byte() | r.Byte() << 8),
        _ => r.UInt(),
    };

    private static float[]? ReadFloats(RhinoReaderCursor r, int expectedCount, CancellationToken cancel, bool optionalSize = false)
    {
        var bytes = ReadBuffer(r, (long)expectedCount * 4, cancel, optionalSize);
        if (bytes is null)
        {
            return null;
        }

        var result = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, result, 0, result.Length * 4);
        return result;
    }

    private static byte[]? ReadBytes(RhinoReaderCursor r, int expected, CancellationToken cancel) => ReadBuffer(r, expected, cancel, false);

    /// <summary>One compressed buffer: size, CRC, method (0 stored, 1 deflate in an anonymous chunk). A size of zero means the array is absent.</summary>
    private static byte[]? ReadBuffer(RhinoReaderCursor r, long expected, CancellationToken cancel, bool optionalSize)
    {
        var size = r.UInt();
        if (size == 0)
        {
            return null;
        }

        if (size != expected && !optionalSize)
        {
            throw new ModelFormatException("A mesh buffer has an unexpected size.");
        }

        if (size > int.MaxValue / 2 || size > 1L << 30)
        {
            throw new ModelFormatException("A mesh buffer is too large.");
        }

        var crc = r.UInt();
        var method = r.Byte();
        var result = new byte[size];
        if (method == 0)
        {
            r.Span.Slice(r.Pos, (int)Math.Min(size, (uint)r.Remaining)).CopyTo(result);
            r.Skip((int)size);
        }
        else if (method == 1)
        {
            var written = 0;
            while (written < size)
            {
                var chunk = r.Chunk();
                if (chunk.Type != 0x40008000)
                {
                    throw new ModelFormatException("A compressed mesh buffer is malformed.");
                }

                using var input = new MemoryStream(r.Memory.Slice(chunk.DataStart, chunk.Length).ToArray(), writable: false);
                using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                var tick = 0L;
                while (written < size)
                {
                    Cancel.Every(cancel, tick++, 15);
                    var n = zlib.Read(result, written, (int)Math.Min(size - written, 1 << 16));
                    if (n == 0)
                    {
                        break;
                    }

                    written += n;
                }

                if (written < size && chunk.End >= r.End)
                {
                    throw new ModelFormatException("A compressed mesh buffer ends early.");
                }
            }
        }
        else
        {
            throw new ModelFormatException("A mesh buffer uses an unknown compression.");
        }

        if (Crc32.Update(0, result) != crc)
        {
            throw new ModelFormatException("A mesh buffer fails its checksum.");
        }

        return result;
    }
}
