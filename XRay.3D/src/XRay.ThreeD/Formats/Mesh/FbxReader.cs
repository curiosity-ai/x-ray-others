using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace XRay.ThreeD.Formats.Mesh;

/// <summary>
/// Autodesk FBX, binary (versions 6100 to 7700, 32 and 64 bit record headers, deflated arrays) and ASCII. Reads the object
/// graph: Model nodes with local transforms (translation, pre/post rotation, rotation order, scaling), Geometry meshes (polygons,
/// per-vertex and per-polygon-vertex normals, vertex colours, per-polygon materials), Materials (diffuse colour, opacity) and the
/// connections that tie them together; the scene's up axis and unit scale. Skinning, animation, cameras and lights are ignored.
/// </summary>
internal static class FbxReader
{
    private const int MaxDepth = 64;

    internal sealed class FbxNode
    {
        public string Name = "";
        public readonly List<object> Props = new();
        public readonly List<FbxNode> Children = new();

        public FbxNode? Child(string name) => Children.FirstOrDefault(c => c.Name == name);

        public IEnumerable<FbxNode> All(string name) => Children.Where(c => c.Name == name);

        public double[] Doubles() => Props.Count > 0 && Props[0] is double[] d ? d : Props.Count > 0 && Props[0] is int[] i ? i.Select(x => (double)x).ToArray() : Props.OfType<double>().ToArray();

        public int[] Ints() => Props.Count > 0 && Props[0] is int[] i ? i : Props.Count > 0 && Props[0] is double[] d ? d.Select(x => (int)x).ToArray() : [];

        public string? Str(int index) => index < Props.Count ? Props[index] as string : null;

        /// <summary>Property <paramref name="index"/> as a number: binary files store doubles, ASCII files write 100 for 100.0.</summary>
        public double? Number(int index) => index < Props.Count ? Props[index] switch { double d => d, long l => l, int i => i, short s => s, _ => null } : null;

        public long Long(int index) => index < Props.Count ? Props[index] switch { long l => l, int i => i, double d => (long)d, short s => s, _ => 0 } : 0;
    }

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var root = data.Span.StartsWith("Kaydara FBX Binary"u8) ? ParseBinary(data.Span, options, out _) : ParseAscii(Encoding.UTF8.GetString(data.Span), options);
        return Build(root, options);
    }

    // ---- binary ----------------------------------------------------------------------------------------------------

    private static FbxNode ParseBinary(ReadOnlySpan<byte> s, LoadOptions options, out uint version)
    {
        if (s.Length < 27)
        {
            throw new ModelFormatException("The FBX file is too short.");
        }

        version = BinaryPrimitives.ReadUInt32LittleEndian(s[23..]);
        var wide = version >= 7500;
        var root = new FbxNode { Name = "" };
        var position = 27;
        var count = 0;
        while (position < s.Length)
        {
            var node = ReadRecord(s, ref position, wide, 0, options, ref count);
            if (node is null)
            {
                break;
            }

            root.Children.Add(node);
        }

        return root;
    }

    private static FbxNode? ReadRecord(ReadOnlySpan<byte> s, ref int position, bool wide, int depth, LoadOptions options, ref int count)
    {
        if (depth > MaxDepth)
        {
            throw new ModelFormatException("FBX nodes are nested too deeply.");
        }

        var headerSize = wide ? 25 : 13;
        if (position + headerSize > s.Length)
        {
            return null;
        }

        long end, numProps, propsLength;
        int nameLength;
        if (wide)
        {
            end = (long)BinaryPrimitives.ReadUInt64LittleEndian(s[position..]);
            numProps = (long)BinaryPrimitives.ReadUInt64LittleEndian(s[(position + 8)..]);
            propsLength = (long)BinaryPrimitives.ReadUInt64LittleEndian(s[(position + 16)..]);
            nameLength = s[position + 24];
        }
        else
        {
            end = BinaryPrimitives.ReadUInt32LittleEndian(s[position..]);
            numProps = BinaryPrimitives.ReadUInt32LittleEndian(s[(position + 4)..]);
            propsLength = BinaryPrimitives.ReadUInt32LittleEndian(s[(position + 8)..]);
            nameLength = s[position + 12];
        }

        if (end == 0)
        {
            position += headerSize; // the null record that ends a child list
            return null;
        }

        if (++count > 5_000_000)
        {
            throw new ModelFormatException("The FBX has too many nodes.");
        }

        options.Cancellation.ThrowIfCancellationRequested();
        var propStart = position + headerSize + nameLength;
        if (end > s.Length || end < propStart || propsLength < 0 || propStart + propsLength > end || numProps < 0 || numProps > propsLength)
        {
            throw new ModelFormatException("An FBX record has inconsistent offsets.");
        }

        var node = new FbxNode { Name = Encoding.UTF8.GetString(s.Slice(position + headerSize, nameLength)) };
        var cursor = new Cursor { P = propStart };
        for (long i = 0; i < numProps; i++)
        {
            node.Props.Add(ReadProperty(s, cursor, (int)(propStart + propsLength)));
        }

        position = (int)(propStart + propsLength);
        while (position < end)
        {
            var child = ReadRecord(s, ref position, wide, depth + 1, options, ref count);
            if (child is null)
            {
                // The null record at the end of the child list; it must land on the record's end.
                break;
            }

            node.Children.Add(child);
        }

        position = (int)end;
        return node;
    }

    private sealed class Cursor
    {
        public int P;
    }

    private static object ReadProperty(ReadOnlySpan<byte> s, Cursor cur, int limit)
    {
        if (cur.P >= limit)
        {
            throw new ModelFormatException("An FBX property is truncated.");
        }

        var type = (char)s[cur.P++];
        int Need(int n)
        {
            if (cur.P + n > limit)
            {
                throw new ModelFormatException("An FBX property is truncated.");
            }

            var at = cur.P;
            cur.P += n;
            return at;
        }

        switch (type)
        {
            case 'Y': return BinaryPrimitives.ReadInt16LittleEndian(s[Need(2)..]);
            case 'C': return s[Need(1)] != 0 ? 1 : 0;
            case 'I': return BinaryPrimitives.ReadInt32LittleEndian(s[Need(4)..]);
            case 'F': return (double)BinaryPrimitives.ReadSingleLittleEndian(s[Need(4)..]);
            case 'D': return BinaryPrimitives.ReadDoubleLittleEndian(s[Need(8)..]);
            case 'L': return BinaryPrimitives.ReadInt64LittleEndian(s[Need(8)..]);
            case 'S':
            case 'R':
                {
                    var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(s[Need(4)..]);
                    if (length < 0 || cur.P + length > limit)
                    {
                        throw new ModelFormatException("An FBX string is truncated.");
                    }

                    var text = type == 'S' ? Encoding.UTF8.GetString(s.Slice(cur.P, length)) : (object)s.Slice(cur.P, length).ToArray();
                    cur.P += length;
                    // Names carry a binary separator: "Model\0\x01Cube" is stored for "Cube::Model".
                    if (text is string str && str.Contains("\0\u0001", StringComparison.Ordinal))
                    {
                        var parts = str.Split("\0\u0001");
                        return parts.Length == 2 ? parts[1] + "::" + parts[0] : str;
                    }

                    return text;
                }

            case 'f':
            case 'd':
            case 'l':
            case 'i':
            case 'b':
                {
                    var arrayLength = BinaryPrimitives.ReadUInt32LittleEndian(s[Need(4)..]);
                    var encoding = BinaryPrimitives.ReadUInt32LittleEndian(s[Need(4)..]);
                    var compressedLength = BinaryPrimitives.ReadUInt32LittleEndian(s[Need(4)..]);
                    var width = type switch { 'f' or 'i' => 4, 'd' or 'l' => 8, _ => 1 };
                    if ((long)arrayLength * width > 1L << 30)
                    {
                        throw new ModelFormatException("An FBX array is too large.");
                    }

                    byte[] raw;
                    if (encoding == 0)
                    {
                        var byteLength = (int)(arrayLength * width);
                        raw = s.Slice(Need(byteLength), byteLength).ToArray();
                    }
                    else if (encoding == 1)
                    {
                        if (compressedLength > limit - cur.P)
                        {
                            throw new ModelFormatException("An FBX compressed array is truncated.");
                        }

                        raw = Inflate(s.Slice(Need((int)compressedLength), (int)compressedLength), (int)(arrayLength * width));
                    }
                    else
                    {
                        throw new ModelFormatException("An FBX array has an unknown encoding.");
                    }

                    return type switch
                    {
                        'd' => Enumerable.Range(0, (int)arrayLength).Select(i => BinaryPrimitives.ReadDoubleLittleEndian(raw.AsSpan(8 * i))).ToArray(),
                        'f' => Enumerable.Range(0, (int)arrayLength).Select(i => (double)BinaryPrimitives.ReadSingleLittleEndian(raw.AsSpan(4 * i))).ToArray(),
                        'i' => Enumerable.Range(0, (int)arrayLength).Select(i => BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4 * i))).ToArray(),
                        'l' => Enumerable.Range(0, (int)arrayLength).Select(i => (double)BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(8 * i))).ToArray(),
                        _ => raw.Select(b => (double)b).ToArray(),
                    };
                }

            default:
                throw new ModelFormatException($"Unknown FBX property type '{type}'.");
        }
    }

    private static byte[] Inflate(ReadOnlySpan<byte> compressed, int expected)
    {
        using var input = new MemoryStream(compressed.ToArray(), writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        var result = new byte[expected];
        var read = 0;
        while (read < expected)
        {
            var n = zlib.Read(result, read, expected - read);
            if (n == 0)
            {
                throw new ModelFormatException("An FBX compressed array is shorter than declared.");
            }

            read += n;
        }

        return result;
    }

    // ---- ASCII -----------------------------------------------------------------------------------------------------

    private static FbxNode ParseAscii(string text, LoadOptions options)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == ';')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }
            }
            else if (char.IsWhiteSpace(c) || c == ',')
            {
                i++;
            }
            else if (c == '"')
            {
                var start = ++i;
                while (i < text.Length && text[i] != '"')
                {
                    i++;
                }

                tokens.Add("\"" + text[start..Math.Min(i, text.Length)]);
                i++;
            }
            else if (c is '{' or '}')
            {
                tokens.Add(c.ToString());
                i++;
            }
            else
            {
                var start = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not (',' or '{' or '}' or '"' or ';'))
                {
                    i++;
                }

                tokens.Add(text[start..i]);
            }
        }

        var root = new FbxNode();
        var p = 0;
        var count = 0;
        while (p < tokens.Count)
        {
            if (tokens[p] == "}")
            {
                p++;
                continue;
            }

            root.Children.Add(ParseAsciiNode(tokens, ref p, 0, options, ref count));
        }

        return root;
    }

    private static FbxNode ParseAsciiNode(List<string> t, ref int p, int depth, LoadOptions options, ref int count)
    {
        if (depth > MaxDepth)
        {
            throw new ModelFormatException("FBX nodes are nested too deeply.");
        }

        if (++count > 5_000_000)
        {
            throw new ModelFormatException("The FBX has too many nodes.");
        }

        options.Cancellation.ThrowIfCancellationRequested();
        var key = t[p++];
        if (!key.EndsWith(':'))
        {
            throw new ModelFormatException($"FBX: '{key}' is not a node name.");
        }

        var node = new FbxNode { Name = key[..^1] };
        var numbers = new List<double>();
        while (p < t.Count && t[p] != "{" && t[p] != "}" && !(t[p].EndsWith(':') && !t[p].StartsWith('"')))
        {
            var token = t[p++];
            if (token.StartsWith('"'))
            {
                var s = token[1..];
                node.Props.Add(s.Contains("::", StringComparison.Ordinal) && false ? s : s);
            }
            else if (token.StartsWith('*'))
            {
                continue; // array length marker: the values follow in the child "a:"
            }
            else if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            {
                node.Props.Add(Math.Abs(d) < long.MaxValue && d == Math.Floor(d) && !token.Contains('.') && !token.Contains('e', StringComparison.OrdinalIgnoreCase) ? (object)(long)d : d);
            }
            else
            {
                node.Props.Add(token); // a bare word (a type code such as Y)
            }
        }

        _ = numbers;
        if (p < t.Count && t[p] == "{")
        {
            p++;
            while (p < t.Count && t[p] != "}")
            {
                node.Children.Add(ParseAsciiNode(t, ref p, depth + 1, options, ref count));
            }

            p++; // }
            // An array is written as a node with one child "a": fold it into this node's properties.
            if (node.Children.Count == 1 && node.Children[0].Name == "a")
            {
                node.Props.Insert(0, node.Children[0].Props.Select(v => v is long l ? l : v is double d ? d : 0.0).Select(Convert.ToDouble).ToArray());
                node.Children.Clear();
            }
        }

        return node;
    }

    // ---- scene -----------------------------------------------------------------------------------------------------

    private static Model3D Build(FbxNode root, LoadOptions options)
    {
        var model = new Model3D();
        var meta = model.Metadata;
        meta.Format = ModelFormats.Fbx;
        meta.DocumentKind = "scene";
        meta.Units = LengthUnit.Centimeter;
        meta.UpAxis = UpAxis.Y;

        var header = root.Child("FBXHeaderExtension");
        if (header?.Child("Creator")?.Str(0) is { } creator)
        {
            meta.Application = creator;
        }

        if (header?.Child("FBXVersion") is { } fv)
        {
            meta.FormatVersion = fv.Long(0).ToString(CultureInfo.InvariantCulture);
        }

        var info = header?.Child("SceneInfo")?.Child("MetaData");
        if (info is not null)
        {
            foreach (var (field, setter) in new (string, Action<string>)[] { ("Title", v => meta.Title = v), ("Author", v => meta.Author = v) })
            {
                if (info.Child(field)?.Str(0) is { Length: > 0 } value)
                {
                    setter(value);
                }
            }
        }

        var unitScale = 1.0;
        if (root.Child("GlobalSettings")?.Child("Properties70") is { } globals)
        {
            foreach (var p in globals.All("P"))
            {
                switch (p.Str(0))
                {
                    case "UpAxis":
                        meta.UpAxis = p.Long(4) switch { 2 => UpAxis.Z, _ => UpAxis.Y };
                        break;
                    case "UnitScaleFactor":
                        unitScale = p.Number(4) ?? unitScale;
                        break;
                }
            }
        }

        meta.Units = unitScale switch
        {
            1.0 => LengthUnit.Centimeter,
            100.0 => LengthUnit.Meter,
            0.1 => LengthUnit.Millimeter,
            2.54 => LengthUnit.Inch,
            30.48 => LengthUnit.Foot,
            _ => LengthUnit.Centimeter,
        };

        var objects = root.Child("Objects");
        if (objects is null)
        {
            throw new ModelFormatException("The FBX has no Objects section.");
        }

        var byId = new Dictionary<long, FbxNode>();
        foreach (var n in objects.Children)
        {
            byId[n.Long(0)] = n;
        }

        var parentOf = new Dictionary<long, long>();
        var childrenOf = new Dictionary<long, List<long>>();
        foreach (var c in root.Child("Connections")?.All("C") ?? [])
        {
            if (c.Str(0) != "OO")
            {
                continue;
            }

            var child = c.Long(1);
            var parent = c.Long(2);
            if (!childrenOf.TryGetValue(parent, out var list))
            {
                childrenOf[parent] = list = new List<long>();
            }

            list.Add(child);
            parentOf[child] = parent;
        }

        // Materials by id with their model-local ordering.
        var materialIndex = new Dictionary<long, int>();
        int MaterialFor(long id)
        {
            if (materialIndex.TryGetValue(id, out var existing))
            {
                return existing;
            }

            var node = byId[id];
            var material = new ModelMaterial { Name = NameOf(node) };
            var props = node.Child("Properties70");
            foreach (var p in props?.All("P") ?? [])
            {
                switch (p.Str(0))
                {
                    case "DiffuseColor" or "Diffuse" when p.Number(4) is { } r && p.Number(5) is { } g && p.Number(6) is { } b:
                        material.BaseColor = new Vector4((float)r, (float)g, (float)b, material.BaseColor.W);
                        break;
                    case "Opacity" when p.Number(4) is { } o:
                        material.BaseColor = new Vector4(material.BaseColor.X, material.BaseColor.Y, material.BaseColor.Z, (float)o);
                        break;
                }
            }

            materialIndex[id] = model.Materials.Count;
            model.Materials.Add(material);
            return materialIndex[id];
        }

        var meshes = new Dictionary<long, MeshData>();
        long triangles = 0;

        SceneNode? Convert(long id, int depth)
        {
            if (depth > MaxDepth || !byId.TryGetValue(id, out var node))
            {
                return null;
            }

            var result = new SceneNode(NameOf(node)) { Transform = TransformOf(node) };
            var kids = childrenOf.GetValueOrDefault(id) ?? [];
            var materialIds = kids.Where(k => byId.TryGetValue(k, out var m) && m.Name == "Material").ToList();
            foreach (var kid in kids)
            {
                if (!byId.TryGetValue(kid, out var child))
                {
                    continue;
                }

                if (child.Name == "Geometry" && child.Str(2) == "Mesh")
                {
                    if (!meshes.TryGetValue(kid, out var mesh))
                    {
                        mesh = BuildMesh(child, materialIds.Select(MaterialFor).ToList(), options, ref triangles);
                        if (mesh is null)
                        {
                            continue;
                        }

                        mesh.Name = NameOf(child);
                        meshes[kid] = mesh;
                        model.Meshes.Add(mesh);
                    }

                    result.Mesh = mesh;
                }
                else if (child.Name == "Model" && Convert(kid, depth + 1) is { } sub)
                {
                    result.Children.Add(sub);
                }
            }

            return result;
        }

        var roots = childrenOf.GetValueOrDefault(0) ?? [];
        var top = new List<SceneNode>();
        foreach (var id in roots)
        {
            if (byId.TryGetValue(id, out var n) && n.Name == "Model" && Convert(id, 0) is { } built)
            {
                top.Add(built);
            }
        }

        if (top.Count == 0)
        {
            // Models without a root connection: take those whose parent is not a model.
            foreach (var n in objects.All("Model").Where(m => !parentOf.TryGetValue(m.Long(0), out var p) || !byId.ContainsKey(p) || byId[p].Name != "Model"))
            {
                if (Convert(n.Long(0), 0) is { } built)
                {
                    top.Add(built);
                }
            }
        }

        foreach (var node in top)
        {
            model.Roots.Add(node);
        }

        if (model.Meshes.Count == 0)
        {
            throw new ModelFormatException("The FBX holds no mesh geometry.");
        }

        meta.PartNames.AddRange(model.Nodes().Where(n => n.Mesh is not null && !string.IsNullOrEmpty(n.Name)).Select(n => n.Name!));
        meta.MaterialNames.AddRange(model.Materials.Select(m => m.Name!).Where(n => !string.IsNullOrEmpty(n)));
        meta.HasGeometry = true;
        meta.Statistics = model.ComputeStatistics();
        meta.Bounds = model.ComputeBounds();
        return model;
    }

    /// <summary>"Model::Cube" is the object called Cube.</summary>
    private static string? NameOf(FbxNode node)
    {
        var name = node.Str(1);
        if (name is null)
        {
            return null;
        }

        var at = name.IndexOf("::", StringComparison.Ordinal);
        return at >= 0 ? name[(at + 2)..] : name;
    }

    private static Matrix4x4 TransformOf(FbxNode model)
    {
        Vector3 Property(string name, Vector3 fallback)
        {
            foreach (var p in model.Child("Properties70")?.All("P") ?? [])
            {
                if (p.Str(0) == name && p.Number(4) is { } x && p.Number(5) is { } y && p.Number(6) is { } z)
                {
                    return new Vector3((float)x, (float)y, (float)z);
                }
            }

            return fallback;
        }

        var order = 0;
        foreach (var p in model.Child("Properties70")?.All("P") ?? [])
        {
            if (p.Str(0) == "RotationOrder" && p.Number(4) is { } o)
            {
                order = (int)o;
            }
        }

        var translation = Property("Lcl Translation", Vector3.Zero);
        var rotation = Property("Lcl Rotation", Vector3.Zero);
        var scaling = Property("Lcl Scaling", Vector3.One);
        var preRotation = Property("PreRotation", Vector3.Zero);
        var postRotation = Property("PostRotation", Vector3.Zero);

        static Matrix4x4 Euler(Vector3 degrees, int order)
        {
            var x = Matrix4x4.CreateRotationX(degrees.X * MathF.PI / 180f);
            var y = Matrix4x4.CreateRotationY(degrees.Y * MathF.PI / 180f);
            var z = Matrix4x4.CreateRotationZ(degrees.Z * MathF.PI / 180f);
            return order switch
            {
                1 => x * z * y,
                2 => y * z * x,
                3 => y * x * z,
                4 => z * x * y,
                5 => z * y * x,
                _ => x * y * z,
            };
        }

        // Column form M = T * Rpre * R * Rpost^-1 * S; the row-vector product runs the other way.
        Matrix4x4.Invert(Euler(postRotation, 0), out var postInverse);
        return Matrix4x4.CreateScale(scaling) * postInverse * Euler(rotation, order) * Euler(preRotation, 0) * Matrix4x4.CreateTranslation(translation);
    }

    private static MeshData? BuildMesh(FbxNode geometry, List<int> materials, LoadOptions options, ref long triangles)
    {
        var vertices = geometry.Child("Vertices")?.Doubles() ?? [];
        var polygon = geometry.Child("PolygonVertexIndex")?.Ints() ?? [];
        if (vertices.Length < 9 || polygon.Length < 3)
        {
            return null;
        }

        var vertexCount = vertices.Length / 3;
        var normals = geometry.Child("LayerElementNormal");
        var normalValues = normals?.Child("Normals")?.Doubles() ?? [];
        var normalIndex = normals?.Child("NormalsIndex")?.Ints() ?? [];
        var normalMapping = normals?.Child("MappingInformationType")?.Str(0) ?? "";
        var normalDirect = (normals?.Child("ReferenceInformationType")?.Str(0) ?? "Direct") == "Direct";

        var colorLayer = geometry.Child("LayerElementColor");
        var colorValues = colorLayer?.Child("Colors")?.Doubles() ?? [];
        var colorIndex = colorLayer?.Child("ColorIndex")?.Ints() ?? [];
        var colorMapping = colorLayer?.Child("MappingInformationType")?.Str(0) ?? "";
        var colorDirect = (colorLayer?.Child("ReferenceInformationType")?.Str(0) ?? "Direct") == "Direct";

        var materialLayer = geometry.Child("LayerElementMaterial");
        var faceMaterials = materialLayer?.Child("Materials")?.Ints() ?? [];
        var materialMapping = materialLayer?.Child("MappingInformationType")?.Str(0) ?? "";

        var positions = new List<float>();
        var outNormals = new List<float>();
        var outColors = new List<float>();
        var lookup = new Dictionary<(int, int, int), uint>();
        var byMaterial = new Dictionary<int, List<uint>>();
        var order = new List<int>();
        var normalsComplete = normalValues.Length >= 3;
        var polygonStart = 0;
        var face = 0;
        for (var corner = 0; corner < polygon.Length; corner++)
        {
            if (polygon[corner] >= 0 && corner + 1 < polygon.Length)
            {
                continue;
            }

            var count = corner - polygonStart + 1;
            var indices = new uint[count];
            for (var k = 0; k < count; k++)
            {
                var c = polygonStart + k;
                var vi = polygon[c] < 0 ? ~polygon[c] : polygon[c];
                if (vi < 0 || vi >= vertexCount)
                {
                    throw new ModelFormatException("An FBX polygon refers to a vertex that does not exist.");
                }

                var ni = normalMapping switch
                {
                    "ByPolygonVertex" => normalDirect ? c : (c < normalIndex.Length ? normalIndex[c] : -1),
                    "ByVertice" or "ByVertex" => normalDirect ? vi : (vi < normalIndex.Length ? normalIndex[vi] : -1),
                    "ByPolygon" => normalDirect ? face : (face < normalIndex.Length ? normalIndex[face] : -1),
                    "AllSame" => 0,
                    _ => -1,
                };
                var ki = colorMapping switch
                {
                    "ByPolygonVertex" => colorDirect ? c : (c < colorIndex.Length ? colorIndex[c] : -1),
                    "ByVertice" or "ByVertex" => colorDirect ? vi : (vi < colorIndex.Length ? colorIndex[vi] : -1),
                    _ => -1,
                };
                if (!lookup.TryGetValue((vi, ni, ki), out var index))
                {
                    index = (uint)(positions.Count / 3);
                    lookup[(vi, ni, ki)] = index;
                    positions.AddRange([(float)vertices[3 * vi], (float)vertices[3 * vi + 1], (float)vertices[3 * vi + 2]]);
                    if (ni >= 0 && 3 * ni + 2 < normalValues.Length)
                    {
                        outNormals.AddRange([(float)normalValues[3 * ni], (float)normalValues[3 * ni + 1], (float)normalValues[3 * ni + 2]]);
                    }
                    else
                    {
                        outNormals.AddRange([0f, 0f, 0f]);
                        normalsComplete = false;
                    }

                    if (colorValues.Length > 0)
                    {
                        if (ki >= 0 && 4 * ki + 3 < colorValues.Length)
                        {
                            outColors.AddRange([(float)colorValues[4 * ki], (float)colorValues[4 * ki + 1], (float)colorValues[4 * ki + 2], (float)colorValues[4 * ki + 3]]);
                        }
                        else
                        {
                            outColors.AddRange([0.8f, 0.8f, 0.8f, 1f]);
                        }
                    }
                }

                indices[k] = index;
            }

            var slot = materialMapping switch
            {
                "ByPolygon" => face < faceMaterials.Length ? faceMaterials[face] : 0,
                "AllSame" => faceMaterials.Length > 0 ? faceMaterials[0] : 0,
                _ => -1,
            };
            var material = slot >= 0 && slot < materials.Count ? materials[slot] : (materials.Count == 1 ? materials[0] : -1);
            if (!byMaterial.TryGetValue(material, out var target))
            {
                byMaterial[material] = target = new List<uint>();
                order.Add(material);
            }

            for (var k = 1; k + 1 < count; k++)
            {
                target.AddRange([indices[0], indices[k], indices[k + 1]]);
                if (++triangles > options.MaxTriangles)
                {
                    throw new ModelFormatException("The FBX has more triangles than the configured limit.");
                }
            }

            polygonStart = corner + 1;
            face++;
        }

        var mesh = new MeshData { Positions = positions.ToArray() };
        if (normalsComplete)
        {
            mesh.Normals = outNormals.ToArray();
        }

        if (outColors.Count == positions.Count / 3 * 4)
        {
            mesh.Colors = outColors.ToArray();
        }

        var all = new List<uint>();
        foreach (var m in order)
        {
            mesh.Groups.Add(new MeshGroup(all.Count, byMaterial[m].Count, m));
            all.AddRange(byMaterial[m]);
        }

        mesh.Indices = all.ToArray();
        if (mesh.Groups.Count == 1 && mesh.Groups[0].Material < 0)
        {
            mesh.Groups.Clear();
        }

        return mesh;
    }
}
