using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace XRay.ThreeD.Formats.Mesh;

/// <summary>PLY, ASCII and binary (either byte order): vertices with optional normals and colours, polygon faces (fan triangulated). A file with no faces is a point cloud.</summary>
internal static class PlyReader
{
    private sealed class Property
    {
        public string Name = "";
        public string Type = "";
        public bool IsList;
        public string CountType = "";
    }

    private sealed class Element
    {
        public string Name = "";
        public long Count;
        public readonly List<Property> Properties = new();
    }

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var span = data.Span;
        var (elements, format, bodyStart, comments) = ParseHeader(span);
        var model = new Model3D();
        model.Metadata.Format = ModelFormats.Ply;
        model.Metadata.DocumentKind = "mesh";
        model.Metadata.FormatVersion = format;
        foreach (var comment in comments)
        {
            model.Metadata.Properties.Add(new ModelProperty("comment", comment, "ply"));
        }

        var vertexElement = elements.FirstOrDefault(e => e.Name == "vertex") ?? throw new ModelFormatException("The PLY file has no vertex element.");
        if (vertexElement.Count > 200_000_000)
        {
            throw new ModelFormatException("The PLY file declares too many vertices.");
        }

        var cursor = new Cursor(span, bodyStart, format);
        var vertexCount = (int)vertexElement.Count;
        var positions = new float[vertexCount * 3];
        var normals = new float[vertexCount * 3];
        var colors = new float[vertexCount * 4];
        var hasNormals = vertexElement.Properties.Any(p => p.Name == "nx");
        var hasColors = vertexElement.Properties.Any(p => p.Name is "red" or "r");
        var indices = new List<uint>();

        foreach (var element in elements)
        {
            for (long row = 0; row < element.Count; row++)
            {
                options.Cancellation.ThrowIfCancellationRequested();
                if (element.Name == "vertex")
                {
                    var v = (int)row;
                    colors[4 * v + 3] = 1;
                    foreach (var p in element.Properties)
                    {
                        if (p.IsList)
                        {
                            cursor.SkipList(p);
                            continue;
                        }

                        var value = cursor.ReadScalar(p.Type);
                        switch (p.Name)
                        {
                            case "x": positions[3 * v] = (float)value; break;
                            case "y": positions[3 * v + 1] = (float)value; break;
                            case "z": positions[3 * v + 2] = (float)value; break;
                            case "nx": normals[3 * v] = (float)value; break;
                            case "ny": normals[3 * v + 1] = (float)value; break;
                            case "nz": normals[3 * v + 2] = (float)value; break;
                            case "red" or "r": colors[4 * v] = Color(value, p.Type); break;
                            case "green" or "g": colors[4 * v + 1] = Color(value, p.Type); break;
                            case "blue" or "b": colors[4 * v + 2] = Color(value, p.Type); break;
                            case "alpha" or "a": colors[4 * v + 3] = Color(value, p.Type); break;
                        }
                    }
                }
                else if (element.Name == "face")
                {
                    foreach (var p in element.Properties)
                    {
                        if (p is { IsList: true, Name: "vertex_indices" or "vertex_index" })
                        {
                            var n = (int)cursor.ReadScalar(p.CountType);
                            if (n < 0 || n > 1_000_000)
                            {
                                throw new ModelFormatException("A PLY face has an impossible vertex count.");
                            }

                            var first = 0u;
                            var previous = 0u;
                            for (var i = 0; i < n; i++)
                            {
                                var index = (long)cursor.ReadScalar(p.Type);
                                if (index < 0 || index >= vertexCount)
                                {
                                    throw new ModelFormatException("A PLY face refers to a vertex that does not exist.");
                                }

                                if (i == 0)
                                {
                                    first = (uint)index;
                                }
                                else if (i >= 2)
                                {
                                    indices.Add(first);
                                    indices.Add(previous);
                                    indices.Add((uint)index);
                                }

                                previous = (uint)index;
                            }

                            if (indices.Count / 3 > options.MaxTriangles)
                            {
                                throw new ModelFormatException("The PLY has more triangles than the configured limit.");
                            }
                        }
                        else if (p.IsList)
                        {
                            cursor.SkipList(p);
                        }
                        else
                        {
                            cursor.ReadScalar(p.Type);
                        }
                    }
                }
                else
                {
                    foreach (var p in element.Properties)
                    {
                        if (p.IsList)
                        {
                            cursor.SkipList(p);
                        }
                        else
                        {
                            cursor.ReadScalar(p.Type);
                        }
                    }
                }
            }
        }

        var mesh = new MeshData { Positions = positions, Indices = indices.ToArray() };
        if (hasNormals)
        {
            mesh.Normals = normals;
        }

        if (hasColors)
        {
            mesh.Colors = colors;
        }

        model.Meshes.Add(mesh);
        model.Roots.Add(new SceneNode(null) { Mesh = mesh });
        if (mesh.Indices.Length == 0)
        {
            model.Metadata.DocumentKind = "pointcloud";
            model.Metadata.Notes.Add("The file has no faces; its vertices are written as points.");
        }

        model.Metadata.HasGeometry = true;
        model.Metadata.Statistics = model.ComputeStatistics();
        model.Metadata.Bounds = mesh.ComputeBounds();
        return model;
    }

    private static float Color(double value, string type) => type is "float" or "float32" or "double" or "float64" ? (float)value : (float)(value / 255.0);

    private static (List<Element> Elements, string Format, int BodyStart, List<string> Comments) ParseHeader(ReadOnlySpan<byte> span)
    {
        var elements = new List<Element>();
        var comments = new List<string>();
        var position = 0;
        string? format = null;
        var first = true;
        while (true)
        {
            var end = span[position..].IndexOf((byte)'\n');
            if (end < 0 || position > 1 << 20)
            {
                throw new ModelFormatException("The PLY header is not terminated.");
            }

            var line = Encoding.ASCII.GetString(span.Slice(position, end)).Trim('\r', ' ');
            position += end + 1;
            if (first)
            {
                if (line != "ply")
                {
                    throw new ModelFormatException("Not a PLY file.");
                }

                first = false;
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            switch (parts[0])
            {
                case "format" when parts.Length >= 3:
                    format = parts[1] + " " + parts[2];
                    break;
                case "comment":
                    comments.Add(string.Join(' ', parts.Skip(1)));
                    break;
                case "element" when parts.Length >= 3:
                    elements.Add(new Element { Name = parts[1], Count = long.Parse(parts[2], CultureInfo.InvariantCulture) });
                    break;
                case "property" when elements.Count > 0:
                    elements[^1].Properties.Add(parts[1] == "list" && parts.Length >= 5
                        ? new Property { IsList = true, CountType = parts[2], Type = parts[3], Name = parts[4] }
                        : new Property { Type = parts[1], Name = parts[^1] });
                    break;
                case "end_header":
                    return (elements, format ?? throw new ModelFormatException("The PLY header has no format line."), position, comments);
            }
        }
    }

    private sealed class Cursor
    {
        private readonly byte[] _bytes;
        private readonly string[]? _tokens;
        private int _position;
        private readonly bool _bigEndian;

        public Cursor(ReadOnlySpan<byte> span, int start, string format)
        {
            _bigEndian = format.StartsWith("binary_big_endian", StringComparison.Ordinal);
            if (format.StartsWith("ascii", StringComparison.Ordinal))
            {
                _tokens = Encoding.ASCII.GetString(span[start..]).Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                _bytes = [];
            }
            else if (format.StartsWith("binary", StringComparison.Ordinal))
            {
                _bytes = span[start..].ToArray();
            }
            else
            {
                throw new ModelFormatException($"Unknown PLY format '{format}'.");
            }
        }

        public double ReadScalar(string type)
        {
            if (_tokens is not null)
            {
                if (_position >= _tokens.Length)
                {
                    throw new ModelFormatException("The PLY body ends early.");
                }

                return double.Parse(_tokens[_position++], NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            var size = SizeOf(type);
            if (_position + size > _bytes.Length)
            {
                throw new ModelFormatException("The PLY body ends early.");
            }

            var s = new ReadOnlySpan<byte>(_bytes, _position, size);
            _position += size;
            return type switch
            {
                "char" or "int8" => (sbyte)s[0],
                "uchar" or "uint8" => s[0],
                "short" or "int16" => _bigEndian ? BinaryPrimitives.ReadInt16BigEndian(s) : BinaryPrimitives.ReadInt16LittleEndian(s),
                "ushort" or "uint16" => _bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s),
                "int" or "int32" => _bigEndian ? BinaryPrimitives.ReadInt32BigEndian(s) : BinaryPrimitives.ReadInt32LittleEndian(s),
                "uint" or "uint32" => _bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s),
                "float" or "float32" => _bigEndian ? BinaryPrimitives.ReadSingleBigEndian(s) : BinaryPrimitives.ReadSingleLittleEndian(s),
                "double" or "float64" => _bigEndian ? BinaryPrimitives.ReadDoubleBigEndian(s) : BinaryPrimitives.ReadDoubleLittleEndian(s),
                _ => throw new ModelFormatException($"Unknown PLY type '{type}'."),
            };
        }

        public void SkipList(Property p)
        {
            var n = (long)ReadScalar(p.CountType);
            if (n < 0 || n > 1_000_000)
            {
                throw new ModelFormatException("A PLY list has an impossible length.");
            }

            for (long i = 0; i < n; i++)
            {
                ReadScalar(p.Type);
            }
        }

        private static int SizeOf(string type) => type switch
        {
            "char" or "int8" or "uchar" or "uint8" => 1,
            "short" or "int16" or "ushort" or "uint16" => 2,
            "int" or "int32" or "uint" or "uint32" or "float" or "float32" => 4,
            "double" or "float64" => 8,
            _ => throw new ModelFormatException($"Unknown PLY type '{type}'."),
        };
    }
}
