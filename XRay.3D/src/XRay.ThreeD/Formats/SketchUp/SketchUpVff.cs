using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace XRay.ThreeD.Formats.SketchUp;

using XRay.ThreeD.Internal;

/// <summary>
/// The geometry of a SketchUp 2021 and later model: <c>model.dat</c> inside the ZIP package. It is a tree of tagged records, each a two byte tag, a
/// little-endian 32-bit payload size and the payload; a fixed set of tags holds child records, the others hold values (little-endian integers of
/// any width, IEEE doubles, UTF-8 text). A component or group definition is a <c>7C15</c> record (id, GUID, name, kind); the model itself is the
/// <c>F601</c> record. Inside one: vertices <c>C409</c> (id, position <c>C509</c> as three doubles), edges <c>B80B</c> (id, the two vertex ids),
/// faces <c>AC0D</c> (id, normal, loops <c>9411</c> of coedges <c>A00F</c> = edge id and direction, front material) and instances
/// <c>6419</c> (definition id, name, a 13-double matrix: 3x3 rows, translation, uniform scale). Layers (tags) are <c>8C3C</c> records, materials
/// <c>C832</c> records whose names key the package's <c>materials/*/material.xml</c>. All lengths are inches.
/// The record layout was worked out by community readers of the format (an MIT-licensed one is the reference, see THIRD_PARTY_NOTICES.md) and
/// checked here against the 24 real files of SketchUp 2021 to 2026 that were available.
/// </summary>
internal static class SketchUpVff
{
    private const int MaxDepth = 64;
    private const int MaxNodes = 2_000_000;
    private const long MaxModelBytes = 1L << 30;

    private static readonly HashSet<ushort> Containers =
    [
        0xF401, 0xF701, 0xD430, 0xD530, 0xC832, 0x7C15, 0x8813, 0x8913, 0x8A13, 0x8B13, 0x8C13, 0x8D13, 0x4C1D, 0x6419, 0xF901, 0x7017, 0x7117,
        0xD007, 0xC409, 0x9411, 0x9511, 0x0F01, 0x384A, 0xB80B, 0x9713, 0x2C4C, 0xAC0D, 0xAE0D, 0xF601, 0xF801, 0x983A, 0x993A, 0x8C3C, 0x8D3C,
        0x9013, 0x401F, 0x9113, 0x9213, 0x6942, 0x6C42, 0x6842,
    ];

    private struct Node
    {
        public ushort Tag;
        public int Offset;
        public int Size;
        public int Child;
        public int Next;
    }

    private sealed class Arena
    {
        public byte[] Data = [];
        public Node[] Nodes = [];
        public int Count;
        public CancellationToken Token;

        public int Add(ushort tag, int offset, int size)
        {
            if (Count == Nodes.Length)
            {
                if (Count >= MaxNodes)
                {
                    throw new ModelFormatException("The SketchUp model has too many records.");
                }

                Array.Resize(ref Nodes, Math.Min(MaxNodes, Math.Max(1024, Count * 2)));
            }

            Nodes[Count] = new Node { Tag = tag, Offset = offset, Size = size, Child = -1, Next = -1 };
            return Count++;
        }

        /// <summary>Parses records in [start, end) and returns the first, linking siblings.</summary>
        public int Parse(int start, int end, int depth)
        {
            var first = -1;
            var last = -1;
            var pos = start;
            while (pos <= end - 6)
            {
                Cancel.Every(Token, Count);
                var tag = (ushort)((Data[pos] << 8) | Data[pos + 1]);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(pos + 2));
                if (size > (uint)(end - pos - 6))
                {
                    break;
                }

                var index = Add(tag, pos + 6, (int)size);
                if (Containers.Contains(tag) && size > 0 && depth < MaxDepth)
                {
                    // The array may be reallocated by the recursive call: assign through the field afterwards.
                    var child = Parse(pos + 6, pos + 6 + (int)size, depth + 1);
                    Nodes[index].Child = child;
                }

                if (first < 0)
                {
                    first = index;
                }
                else
                {
                    Nodes[last].Next = index;
                }

                last = index;
                pos += 6 + (int)size;
            }

            return first;
        }

        public ReadOnlySpan<byte> Payload(int n) => Nodes[n].Child >= 0 ? default : Data.AsSpan(Nodes[n].Offset, Nodes[n].Size);

        /// <summary>The first direct child with the tag.</summary>
        public int Direct(int n, ushort tag)
        {
            for (var c = Nodes[n].Child; c >= 0; c = Nodes[c].Next)
            {
                if (Nodes[c].Tag == tag)
                {
                    return c;
                }
            }

            return -1;
        }

        /// <summary>The first descendant with the tag, in document order.</summary>
        public int Deep(int n, ushort tag)
        {
            for (var c = Nodes[n].Child; c >= 0; c = Nodes[c].Next)
            {
                if (Nodes[c].Tag == tag)
                {
                    return c;
                }

                var found = Deep(c, tag);
                if (found >= 0)
                {
                    return found;
                }
            }

            return -1;
        }

        public void All(int n, ushort tag, List<int> into)
        {
            for (var c = Nodes[n].Child; c >= 0; c = Nodes[c].Next)
            {
                if (Nodes[c].Tag == tag)
                {
                    into.Add(c);
                }

                All(c, tag, into);
            }
        }

        public long VarInt(int n)
        {
            var p = Payload(n);
            long v = 0;
            for (var i = 0; i < Math.Min(8, p.Length); i++)
            {
                v |= (long)p[i] << (8 * i);
            }

            return v;
        }

        /// <summary>The id of a record: a <c>DE05</c> child, or a <c>DC05</c> child (which may itself start with a <c>DE05</c> record).</summary>
        public long? EntityId(int n, int depth = 0)
        {
            if (depth > MaxDepth)
            {
                return null;
            }

            for (var c = Nodes[n].Child; c >= 0; c = Nodes[c].Next)
            {
                if (Nodes[c].Tag == 0xDE05)
                {
                    return VarInt(c);
                }

                if (Nodes[c].Tag == 0xDC05)
                {
                    var p = Payload(c);
                    if (p.Length >= 6 && p[0] == 0xDE && p[1] == 0x05)
                    {
                        var length = BinaryPrimitives.ReadUInt32LittleEndian(p[2..]);
                        long v = 0;
                        for (var i = 0; i < Math.Min(8, Math.Min(length, p.Length - 6)); i++)
                        {
                            v |= (long)p[6 + i] << (8 * i);
                        }

                        return v;
                    }

                    return VarInt(c);
                }
            }

            for (var c = Nodes[n].Child; c >= 0; c = Nodes[c].Next)
            {
                if (EntityId(c, depth + 1) is { } id)
                {
                    return id;
                }
            }

            return null;
        }

        public bool TryDouble3(int n, out double x, out double y, out double z)
        {
            var p = Payload(n);
            x = y = z = 0;
            if (p.Length < 24)
            {
                return false;
            }

            x = BitConverter.ToDouble(p[..8]);
            y = BitConverter.ToDouble(p.Slice(8, 8));
            z = BitConverter.ToDouble(p.Slice(16, 8));
            return double.IsFinite(x) && double.IsFinite(y) && double.IsFinite(z);
        }

        public string Text(int n)
        {
            var p = Payload(n);
            return Encoding.UTF8.GetString(p).Replace("\0", "", StringComparison.Ordinal).Trim();
        }
    }

    internal sealed class Face
    {
        public readonly List<(long Edge, bool Forward)[]> Loops = new();
        public double Nx, Ny, Nz = 1;
        public long Material = -1;
        public bool Hidden;
    }

    internal sealed class Instance
    {
        public long Definition = -1;
        public string Name = "";
        public Matrix4x4 Transform = Matrix4x4.Identity;
        public long Material = -1;
        public long Layer = -1;
        public bool Hidden;
    }

    internal sealed class Definition
    {
        public string Name = "";
        public bool IsGroup;
        public readonly Dictionary<long, Vector3> Vertices = new();
        public readonly Dictionary<long, (long A, long B)> Edges = new();
        public readonly Dictionary<long, Face> Faces = new();
        public readonly List<Instance> Instances = new();
    }

    internal sealed record SkpMaterial(string Name, Vector4 Color, bool Textured);

    public static Model3D Read(System.IO.Compression.ZipArchive zip, ModelMetadata meta, LoadOptions options)
    {
        var token = options.Cancellation;
        var entry = ZipEntries.Find(zip, "model.dat") ?? throw new ModelFormatException("The SketchUp package has no model.dat.");
        var arena = new Arena { Data = ZipEntries.Read(entry, MaxModelBytes, token), Token = token };
        arena.Nodes = new Node[Math.Clamp(arena.Data.Length / 24, 1024, MaxNodes)];
        var top = arena.Parse(0, arena.Data.Length, 0);
        // The records are wrapped in one F401 record; its children are the real top level.
        if (top >= 0 && arena.Nodes[top].Tag == 0xF401 && arena.Nodes[top].Next < 0 && arena.Nodes[top].Child >= 0)
        {
            top = arena.Nodes[top].Child;
        }

        var model = new Model3D();
        model.Metadata.Format = ModelFormats.SketchUp;
        model.Metadata.DocumentKind = "model";
        model.Metadata.Units = LengthUnit.Inch;
        model.Metadata.UpAxis = UpAxis.Z;
        model.Metadata.FormatVersion = meta.FormatVersion;
        model.Metadata.Application = meta.Application;
        model.Metadata.HasPreview = meta.HasPreview;
        foreach (var p in meta.Properties)
        {
            model.Metadata.Properties.Add(p);
        }

        var materials = ReadMaterials(zip, token);
        var layers = new Dictionary<long, (string Name, bool Hidden)>();
        var materialNames = new Dictionary<long, string>();
        var definitions = new Dictionary<long, Definition>();
        var root = new Definition { Name = "Model" };
        var hasRoot = false;
        var tick = 0L;

        void Visit(int n, int depth)
        {
            for (; n >= 0; n = arena.Nodes[n].Next)
            {
                Cancel.Every(token, tick++);
                switch (arena.Nodes[n].Tag)
                {
                    case 0x7C15:
                        ReadDefinition(arena, n, definitions, token);
                        break;
                    case 0xF601:
                        hasRoot = true;
                        Extract(arena, arena.Nodes[n].Child, root, token);
                        continue;
                    case 0x993A:
                        ReadLayers(arena, n, layers);
                        break;
                    case 0xC832:
                        ReadMaterialName(arena, n, materialNames);
                        break;
                }

                if (arena.Nodes[n].Child >= 0 && depth < MaxDepth)
                {
                    Visit(arena.Nodes[n].Child, depth + 1);
                }
            }
        }

        Visit(top, 0);

        if (!hasRoot)
        {
            throw new UnsupportedModelFormatException(ModelFormats.SketchUp, "model.dat has no model record (F601); its layout is not the one this reader knows.");
        }

        return Assemble(model, definitions, root, materials, materialNames, layers, options,
            "Geometry decoded from model.dat (vertices, edges, faces with holes, groups and components with transforms, material colours, tags). Textures, UV mapping, edge display, section planes, text and dimensions are not read. Lengths are inches.");
    }

    /// <summary>Builds the scene from decoded definitions; shared by the model.dat reader and the 2013 to 2020 object-stream reader.</summary>
    internal static Model3D Assemble(Model3D model, Dictionary<long, Definition> definitions, Definition root, Dictionary<string, SkpMaterial> materials, Dictionary<long, string> materialNames, Dictionary<long, (string Name, bool Hidden)> layers, LoadOptions options, string note)
    {
        var layerIndex = new Dictionary<long, int>();
        foreach (var (id, layer) in layers.OrderBy(l => l.Key))
        {
            layerIndex[id] = model.Layers.Count;
            model.Layers.Add(new ModelLayer(layer.Name) { Visible = !layer.Hidden });
            model.Metadata.LayerNames.Add(layer.Name);
        }

        var builder = new SceneBuilder(model, definitions, materials, materialNames, layerIndex, options);
        var node = builder.Build(root, null, 0);
        node.Name = model.Metadata.Title ?? "Model";
        model.Roots.Add(node);

        model.Metadata.HasGeometry = model.Meshes.Count > 0;
        model.Metadata.MaterialNames.Clear();
        model.Metadata.MaterialNames.AddRange(model.Materials.Select(m => m.Name ?? ""));
        model.Metadata.Notes.Add(note);
        model.Metadata.Statistics = model.ComputeStatistics();
        model.Metadata.Bounds = model.ComputeBounds();
        return model;
    }

    private static Dictionary<string, SkpMaterial> ReadMaterials(System.IO.Compression.ZipArchive zip, CancellationToken token)
    {
        var result = new Dictionary<string, SkpMaterial>(StringComparer.Ordinal);
        var tick = 0L;
        foreach (var entry in zip.Entries)
        {
            Cancel.Every(token, tick++);
            var name = entry.FullName;
            if (!name.StartsWith("materials/", StringComparison.Ordinal) || !name.EndsWith("/material.xml", StringComparison.Ordinal) || entry.Length >= (1 << 20))
            {
                continue;
            }

            XDocument doc;
            try
            {
                using var stream = new MemoryStream(ZipEntries.Read(entry, 1 << 20, token), writable: false);
                using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1 << 20 });
                doc = XDocument.Load(reader);
            }
            catch (XmlException)
            {
                continue;
            }

            var material = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "material" && e.Attribute("name") is not null);
            if (material is null)
            {
                continue;
            }

            float Channel(string attribute) => int.TryParse((string?)material.Attribute(attribute), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 0, 255) / 255f : 0.8f;
            var alpha = 1f;
            if ((string?)material.Attribute("useTrans") == "1" && float.TryParse((string?)material.Attribute("trans"), NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && float.IsFinite(t))
            {
                alpha = Math.Clamp(1 - t, 0f, 1f);
            }

            var m = new SkpMaterial((string)material.Attribute("name")!, new Vector4(Channel("colorRed"), Channel("colorGreen"), Channel("colorBlue"), alpha), (string?)material.Attribute("hasTexture") == "1");
            result[m.Name] = m;
            var folder = name.Split('/');
            if (folder.Length >= 3)
            {
                result.TryAdd(folder[1], m);
            }
        }

        return result;
    }

    private static void ReadLayers(Arena a, int n, Dictionary<long, (string Name, bool Hidden)> layers)
    {
        for (var c = a.Nodes[n].Child; c >= 0; c = a.Nodes[c].Next)
        {
            if (a.Nodes[c].Tag != 0x8C3C)
            {
                continue;
            }

            var id = a.Deep(c, 0xDC05);
            var name = a.Deep(c, 0x8D3C);
            if (id < 0 || name < 0)
            {
                continue;
            }

            var p = a.Payload(id);
            long lid;
            if (p.Length >= 6 && p[0] == 0xDE && p[1] == 0x05)
            {
                lid = 0;
                var length = BinaryPrimitives.ReadUInt32LittleEndian(p[2..]);
                for (var i = 0; i < Math.Min(8, Math.Min(length, p.Length - 6)); i++)
                {
                    lid |= (long)p[6 + i] << (8 * i);
                }
            }
            else
            {
                lid = a.VarInt(id);
            }

            var hidden = a.Deep(c, 0x8E3C);
            layers[lid] = (a.Text(name), hidden >= 0 && a.Payload(hidden).Length > 0 && a.Payload(hidden)[0] == 1);
        }
    }

    private static void ReadMaterialName(Arena a, int n, Dictionary<long, string> names)
    {
        var id = a.Deep(n, 0xDC05);
        var name = a.Deep(n, 0xCC32);
        if (id < 0 || name < 0)
        {
            return;
        }

        var p = a.Payload(id);
        long mid = 0;
        if (p.Length >= 6 && p[0] == 0xDE && p[1] == 0x05)
        {
            var length = BinaryPrimitives.ReadUInt32LittleEndian(p[2..]);
            for (var i = 0; i < Math.Min(8, Math.Min(length, p.Length - 6)); i++)
            {
                mid |= (long)p[6 + i] << (8 * i);
            }
        }
        else
        {
            mid = a.VarInt(id);
        }

        names[mid] = a.Text(name);
    }

    private static void ReadDefinition(Arena a, int n, Dictionary<long, Definition> definitions, CancellationToken token)
    {
        var id = a.EntityId(n);
        if (id is null)
        {
            return;
        }

        var def = new Definition();
        for (var c = a.Nodes[n].Child; c >= 0; c = a.Nodes[c].Next)
        {
            switch (a.Nodes[c].Tag)
            {
                case 0x7E15: def.Name = a.Text(c); break;
                case 0x8315 when a.Payload(c).Length > 0: def.IsGroup = a.VarInt(c) == 1; break;
            }
        }

        Extract(a, a.Nodes[n].Child, def, token);
        definitions[id.Value] = def;
    }

    private static void Extract(Arena a, int first, Definition def, CancellationToken token)
    {
        var tick = 0L;
        for (var n = first; n >= 0; n = a.Nodes[n].Next)
        {
            Cancel.Every(token, tick++);
            switch (a.Nodes[n].Tag)
            {
                case 0xC409:
                {
                    var id = a.EntityId(n);
                    var pos = a.Deep(n, 0xC509);
                    if (id is not null && pos >= 0 && a.TryDouble3(pos, out var x, out var y, out var z))
                    {
                        def.Vertices[id.Value] = new Vector3((float)x, (float)y, (float)z);
                    }

                    break;
                }

                case 0xB80B:
                {
                    var id = a.EntityId(n);
                    if (id is not null)
                    {
                        var v1 = a.Deep(n, 0xB90B);
                        var v2 = a.Deep(n, 0xBA0B);
                        if (v1 >= 0 && v2 >= 0)
                        {
                            def.Edges[id.Value] = (a.VarInt(v1), a.VarInt(v2));
                        }
                    }

                    break;
                }

                case 0xAC0D:
                    ReadFace(a, n, def, token);
                    break;
                case 0x6419:
                    ReadInstance(a, n, def);
                    break;
                default:
                    if (a.Nodes[n].Child >= 0)
                    {
                        Extract(a, a.Nodes[n].Child, def, token);
                    }

                    break;
            }
        }
    }

    private static void ReadFace(Arena a, int n, Definition def, CancellationToken token)
    {
        var id = a.EntityId(n);
        if (id is null)
        {
            return;
        }

        var face = new Face();
        var normal = a.Deep(n, 0xAD0D);
        if (normal >= 0 && a.TryDouble3(normal, out var nx, out var ny, out var nz))
        {
            face.Nx = nx;
            face.Ny = ny;
            face.Nz = nz;
        }

        var loopsRoot = a.Deep(n, 0xAE0D);
        if (loopsRoot >= 0)
        {
            var loops = new List<int>();
            a.All(loopsRoot, 0x9411, loops);
            var tick = 0L;
            foreach (var loop in loops)
            {
                var coedges = new List<int>();
                a.All(loop, 0xA00F, coedges);
                var list = new List<(long, bool)>(coedges.Count);
                foreach (var co in coedges)
                {
                    Cancel.Every(token, tick++);
                    var p = a.Payload(co);
                    long edge = -1, orient = -1;
                    var pos = 0;
                    while (pos < p.Length - 6)
                    {
                        var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(p[(pos + 2)..]), int.MaxValue);
                        if (size >= 0 && pos + 6 + size <= p.Length)
                        {
                            long v = 0;
                            for (var i = 0; i < Math.Min(8, size); i++)
                            {
                                v |= (long)p[pos + 6 + i] << (8 * i);
                            }

                            if (p[pos] == 0xA1 && p[pos + 1] == 0x0F)
                            {
                                edge = v;
                            }
                            else if (p[pos] == 0xA2 && p[pos + 1] == 0x0F)
                            {
                                orient = v;
                            }
                        }

                        pos += 6 + Math.Max(0, size);
                    }

                    if (edge >= 0 && orient >= 0)
                    {
                        list.Add((edge, orient == 0));
                    }
                }

                if (list.Count > 0)
                {
                    face.Loops.Add(list.ToArray());
                }
            }
        }

        var d007 = a.Direct(n, 0xD007);
        if (d007 >= 0)
        {
            var d107 = a.Direct(d007, 0xD107);
            if (d107 >= 0)
            {
                face.Material = a.VarInt(d107);
            }

            var d307 = a.Direct(d007, 0xD307);
            if (d307 >= 0 && a.Payload(d307).Length > 0)
            {
                face.Hidden = (a.Payload(d307)[0] & 1) != 0;
            }
        }

        def.Faces[id.Value] = face;
    }

    /// <summary>The 13-double instance matrix (3x3 by rows of a column-vector transform, translation, uniform scale) as a row-vector matrix.</summary>
    internal static Matrix4x4 MatrixFrom13(double[] m)
    {
        if (m.Length < 13)
        {
            return Matrix4x4.Identity;
        }

        var s = m[12] == 0 || !double.IsFinite(m[12]) ? 1.0 : m[12];
        return new Matrix4x4(
            (float)(m[0] * s), (float)(m[3] * s), (float)(m[6] * s), 0,
            (float)(m[1] * s), (float)(m[4] * s), (float)(m[7] * s), 0,
            (float)(m[2] * s), (float)(m[5] * s), (float)(m[8] * s), 0,
            (float)m[9], (float)m[10], (float)m[11], 1);
    }

    private static void ReadInstance(Arena a, int n, Definition def)
    {
        var inst = new Instance();
        var defIdx = a.Deep(n, 0x6719);
        if (defIdx < 0)
        {
            return;
        }

        inst.Definition = a.VarInt(defIdx);
        var name = a.Deep(n, 0x6519);
        if (name >= 0)
        {
            inst.Name = a.Text(name);
        }

        var matrix = a.Deep(n, 0x6619);
        if (matrix >= 0 && a.Payload(matrix).Length >= 104)
        {
            var p = a.Payload(matrix);
            Span<double> m = stackalloc double[13];
            for (var i = 0; i < 13; i++)
            {
                m[i] = BitConverter.ToDouble(p.Slice(i * 8, 8));
            }

            inst.Transform = MatrixFrom13(m.ToArray());
        }

        var d007 = a.Direct(n, 0xD007);
        if (d007 >= 0)
        {
            if (a.Direct(d007, 0xD107) is >= 0 and var d107)
            {
                inst.Material = a.VarInt(d107);
            }

            if (a.Direct(d007, 0xD207) is >= 0 and var d207)
            {
                inst.Layer = a.VarInt(d207);
            }

            if (a.Direct(d007, 0xD307) is >= 0 and var d307 && a.Payload(d307).Length > 0)
            {
                inst.Hidden = (a.Payload(d307)[0] & 1) != 0;
            }
        }

        def.Instances.Add(inst);
    }

    internal sealed class SceneBuilder
    {
        private readonly Model3D _model;
        private readonly Dictionary<long, Definition> _definitions;
        private readonly Dictionary<string, SkpMaterial> _materials;
        private readonly Dictionary<long, string> _materialNames;
        private readonly Dictionary<long, int> _layers;
        private readonly LoadOptions _options;
        private readonly CancellationToken _token;
        private readonly Dictionary<(object, long), MeshData?> _meshes = new();
        private readonly Dictionary<string, int> _modelMaterials = new(StringComparer.Ordinal);
        private readonly HashSet<long> _active = new();
        private int _nodes;
        private long _triangles;
        private long _tick;

        public SceneBuilder(Model3D model, Dictionary<long, Definition> definitions, Dictionary<string, SkpMaterial> materials, Dictionary<long, string> materialNames, Dictionary<long, int> layers, LoadOptions options)
        {
            _model = model;
            _definitions = definitions;
            _materials = materials;
            _materialNames = materialNames;
            _layers = layers;
            _options = options;
            _token = options.Cancellation;
        }

        public SceneNode Build(Definition def, long? id, int depth, long inheritedMaterial = -1)
        {
            if (depth > MaxDepth || ++_nodes > MaxNodes)
            {
                throw new ModelFormatException("The SketchUp component tree is too deep or too large.");
            }

            Cancel.Every(_token, _tick++, 63);
            var node = new SceneNode(def.Name);
            if (id is { } key && !_active.Add(key))
            {
                throw new ModelFormatException("The SketchUp component tree contains a cycle.");
            }

            try
            {
                node.Mesh = MeshFor(def, inheritedMaterial);
                foreach (var inst in def.Instances)
                {
                    if (inst.Hidden || !_definitions.TryGetValue(inst.Definition, out var child))
                    {
                        continue;
                    }

                    var childNode = Build(child, inst.Definition, depth + 1, inst.Material >= 0 ? inst.Material : inheritedMaterial);
                    childNode.Transform = inst.Transform;
                    var definitionName = childNode.Name;
                    childNode.Name = !string.IsNullOrEmpty(inst.Name) ? inst.Name : !string.IsNullOrEmpty(definitionName) ? definitionName : (child.IsGroup ? "Group" : "Component");
                    if (!string.IsNullOrEmpty(definitionName) && definitionName != childNode.Name)
                    {
                        childNode.Properties["Definition"] = definitionName;
                    }

                    childNode.Properties["Kind"] = child.IsGroup ? "group" : "component";
                    if (_layers.TryGetValue(inst.Layer, out var layer))
                    {
                        childNode.Layer = layer;
                    }

                    node.Children.Add(childNode);
                }
            }
            finally
            {
                if (id is { } k)
                {
                    _active.Remove(k);
                }
            }

            return node;
        }

        private int MaterialIndex(long id)
        {
            if (id < 0 || !_materialNames.TryGetValue(id, out var name) || !_materials.TryGetValue(name, out var m))
            {
                return -1;
            }

            if (!_modelMaterials.TryGetValue(m.Name, out var index))
            {
                index = _model.Materials.Count;
                _model.Materials.Add(new ModelMaterial { Name = m.Name, BaseColor = m.Color, Roughness = 0.8f });
                _modelMaterials[m.Name] = index;
            }

            return index;
        }

        private MeshData? MeshFor(Definition def, long inherited)
        {
            if (def.Faces.Count == 0)
            {
                return null;
            }

            if (_meshes.TryGetValue((def, inherited), out var cached))
            {
                return cached;
            }

            var byMaterial = new SortedDictionary<int, List<int>>();
            var faceOrder = new List<Face>(def.Faces.Count);
            var positions = new List<float>();
            var normals = new List<float>();
            var indicesByMaterial = new SortedDictionary<int, List<uint>>();
            var faceRanges = new List<(int Material, int Start, int Count)>();
            var tickLocal = 0L;
            foreach (var face in def.Faces.Values)
            {
                Cancel.Every(_token, tickLocal++, 255);
                if (face.Hidden)
                {
                    continue;
                }

                var material = MaterialIndex(face.Material);
                if (material < 0 && inherited >= 0)
                {
                    material = MaterialIndex(inherited);
                }

                if (!indicesByMaterial.TryGetValue(material, out var target))
                {
                    indicesByMaterial[material] = target = new List<uint>();
                }

                var before = target.Count;
                AddFace(def, face, positions, normals, target);
                if (target.Count > before)
                {
                    faceRanges.Add((material, before, target.Count - before));
                }
            }

            MeshData? mesh = null;
            if (indicesByMaterial.Values.Any(l => l.Count > 0))
            {
                mesh = new MeshData { Name = def.Name.Length > 0 ? def.Name : "Model", Positions = positions.ToArray(), Normals = normals.ToArray() };
                var all = new List<uint>();
                var offsets = new Dictionary<int, int>();
                foreach (var (material, list) in indicesByMaterial)
                {
                    if (list.Count == 0)
                    {
                        continue;
                    }

                    offsets[material] = all.Count;
                    mesh.Groups.Add(new MeshGroup(all.Count, list.Count, material));
                    all.AddRange(list);
                }

                mesh.Indices = all.ToArray();
                mesh.Faces = faceRanges.Select(r => new MeshFace(offsets[r.Material] + r.Start, r.Count)).ToArray();
                _model.Meshes.Add(mesh);
            }

            _meshes[(def, inherited)] = mesh;
            return mesh;
        }

        private void AddFace(Definition def, Face face, List<float> positions, List<float> normals, List<uint> indices)
        {
            // Walk each loop's coedges: the start vertex of a coedge is the edge's first vertex when it runs forward, the second otherwise.
            var loopPoints = new List<Vector3[]>(face.Loops.Count);
            foreach (var loop in face.Loops)
            {
                var pts = new List<Vector3>(loop.Length);
                long previous = long.MinValue;
                long firstVertex = long.MinValue;
                foreach (var (edge, forward) in loop)
                {
                    if (!def.Edges.TryGetValue(edge, out var e))
                    {
                        continue;
                    }

                    var v = forward ? e.A : e.B;
                    if (v == previous || !def.Vertices.TryGetValue(v, out var p))
                    {
                        continue;
                    }

                    if (firstVertex == long.MinValue)
                    {
                        firstVertex = v;
                    }

                    pts.Add(p);
                    previous = v;
                }

                if (pts.Count > 1 && previous == firstVertex)
                {
                    pts.RemoveAt(pts.Count - 1);
                }

                if (pts.Count >= 3)
                {
                    loopPoints.Add(pts.ToArray());
                }
            }

            if (loopPoints.Count == 0)
            {
                return;
            }

            // Newell normal of the outer loop (the first one).
            var outer = loopPoints[0];
            var nrm = Vector3.Zero;
            for (var i = 0; i < outer.Length; i++)
            {
                var a = outer[i];
                var b = outer[(i + 1) % outer.Length];
                nrm.X += (a.Y - b.Y) * (a.Z + b.Z);
                nrm.Y += (a.Z - b.Z) * (a.X + b.X);
                nrm.Z += (a.X - b.X) * (a.Y + b.Y);
            }

            if (nrm.LengthSquared() < 1e-20f)
            {
                return;
            }

            nrm = Vector3.Normalize(nrm);
            var declared = new Vector3((float)face.Nx, (float)face.Ny, (float)face.Nz);
            var flip = declared.LengthSquared() > 1e-12f && Vector3.Dot(declared, nrm) < 0;
            var outward = flip ? -nrm : nrm;

            var ux = MathF.Abs(nrm.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY;
            var u = Vector3.Normalize(Vector3.Cross(ux, nrm));
            var w = Vector3.Cross(nrm, u);

            var count = 0;
            foreach (var l in loopPoints)
            {
                count += l.Length;
            }

            var baseIndex = (uint)(positions.Count / 3);
            var points = new List<(double X, double Y)>(count);
            var rings = new List<int[]>(loopPoints.Count);
            foreach (var l in loopPoints)
            {
                var ring = new int[l.Length];
                for (var i = 0; i < l.Length; i++)
                {
                    ring[i] = points.Count;
                    points.Add((Vector3.Dot(l[i], u), Vector3.Dot(l[i], w)));
                    positions.Add(l[i].X);
                    positions.Add(l[i].Y);
                    positions.Add(l[i].Z);
                    normals.Add(outward.X);
                    normals.Add(outward.Y);
                    normals.Add(outward.Z);
                }

                rings.Add(ring);
            }

            int[] tris;
            if (rings.Count == 1 && rings[0].Length == 3)
            {
                tris = [0, 1, 2];
            }
            else
            {
                tris = Triangulator.TriangulateCancellable(points, rings, _token);
            }

            _triangles += tris.Length / 3;
            if (_triangles > _options.MaxTriangles)
            {
                throw new ModelFormatException("The SketchUp model exceeds the triangle limit.");
            }

            for (var i = 0; i + 2 < tris.Length; i += 3)
            {
                // The triangulation is counter-clockwise about +nrm; reverse it when the face's front is the other side.
                if (flip)
                {
                    indices.Add(baseIndex + (uint)tris[i]);
                    indices.Add(baseIndex + (uint)tris[i + 2]);
                    indices.Add(baseIndex + (uint)tris[i + 1]);
                }
                else
                {
                    indices.Add(baseIndex + (uint)tris[i]);
                    indices.Add(baseIndex + (uint)tris[i + 1]);
                    indices.Add(baseIndex + (uint)tris[i + 2]);
                }
            }
        }
    }
}
