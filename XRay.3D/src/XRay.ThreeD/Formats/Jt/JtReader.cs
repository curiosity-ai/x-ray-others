using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace XRay.ThreeD.Formats.Jt;

using XRay.ThreeD.Internal;

/// <summary>
/// JT files (ISO 14306, JT 8 to 10): an 80 byte version banner, a byte order flag, a table of contents of segments, and the logical scene graph segment
/// (nodes, attributes and properties as length-prefixed elements). Shape geometry sits in separate "late loaded" segments named by properties, each holding one
/// LOD element whose topologically compressed mesh (JT 9 and 10) or quantised strips (JT 8) are decoded by <see cref="JtMeshInput"/> and <see cref="JtVertexData"/>.
/// Segment payloads are zlib (JT 8 and 9) or xz (JT 10). The layouts and codecs follow the MIT-licensed FerroJT reader (see THIRD_PARTY_NOTICES.md).
/// Only triangle strip set LODs are decoded; other shape kinds, B-rep segments and PMI are skipped.
/// </summary>
internal static class JtReader
{
    private const long MaxSegmentBytes = 1L << 28;
    private const int MaxNodes = 2_000_000;
    private const int MaxDepth = 256;

    private static readonly Guid16 NodeBase = G(0x10dd1035, 0x2ac8, 0x11d1, 0x9b6b0080c7bb5997);
    private static readonly Guid16 NodeGroup = G(0x10dd101b, 0x2ac8, 0x11d1, 0x9b6b0080c7bb5997);
    private static readonly Guid16 NodeInstance = G(0x10dd102a, 0x2ac8, 0x11d1, 0x9b6b0080c7bb5997);
    private static readonly Guid16 NodeLod = G(0x10dd102c, 0x2ac8, 0x11d1, 0x9b6b0080c7bb5997);
    private static readonly Guid16 NodePartition = G(0x10dd103e, 0x2ac8, 0x11d1, 0x9b6b0080c7bb5997);
    private static readonly Guid16 NodeRangeLod = G(0x10dd104c, 0x2ac8, 0x11d1, 0x9b6b0080c7bb5997);
    private static readonly Guid16 NodeShapeBase = G(0x10dd1059, 0x2ac8, 0x11d1, 0x9b6b0080c7bb5997);
    private static readonly Guid16 NodeShapeTriStripSet = G(0x10dd1077, 0x2ac8, 0x11d1, 0x9b6b0080c7bb5997);
    private static readonly Guid16 NodeShapeVertex = G(0x10dd107f, 0x2ac8, 0x11d1, 0x6b9b0080c7bb5997);
    private static readonly Guid16 ShapeLodBase = G(0x10dd10a4, 0x2ac8, 0x11d1, 0x9b6b0080c7bb5997);
    private static readonly Guid16 ShapeLodTriStripSet = G(0x10dd10ab, 0x2ac8, 0x11d1, 0x9b6b0080c7bb5997);
    private static readonly Guid16 ShapeLodVertex = G(0x10dd10b0, 0x2ac8, 0x11d1, 0x6b9b0080c7bb5997);
    private static readonly Guid16 AttributeMaterial = G(0x10dd1030, 0x2ac8, 0x11d1, 0x9b6b0080c7bb5997);
    private static readonly Guid16 AttributeTransform = G(0x10dd1083, 0x2ac8, 0x11d1, 0x9b6b0080c7bb5997);
    private static readonly Guid16 PropertyBase = G(0x10dd104b, 0x2ac8, 0x11d1, 0x9b6b0080c7bb5997);
    private static readonly Guid16 PropertyString = G(0x10dd106e, 0x2ac8, 0x11d1, 0x9b6b0080c7bb5997);
    private static readonly Guid16 PropertyLateLoaded = G(0xe0b05be5, 0xfbbd, 0x11d1, 0xa3a700aa00d10954);
    private static readonly Guid16 NodePart = G(0xce357244, 0x38fb, 0x11d1, 0xa506006097bdc6e1UL);
    private static readonly Guid16 NodeMetaData = G(0xce357245, 0x38fb, 0x11d1, 0xa506006097bdc6e1UL);
    private static readonly Guid16 ElementProxyMetaData = G(0xce357247, 0x38fb, 0x11d1, 0xa506006097bdc6e1UL);
    private static readonly Guid16 ElementPmiManager = G(0xce357249, 0x38fb, 0x11d1, 0xa506006097bdc6e1UL);
    private static readonly Guid16 XtBRep = G(0x873a70e0, 0x2ac9, 0x11d1, 0x9b6b0080c7bb5997);

    private static readonly byte[] TopologyMarker = [0x30, 0xf8, 0x4c, 0xbe, 0xbc, 0x4f, 0x9b, 0x5f, 0xb9, 0x26, 0x92, 0x78, 0xd2, 0xe1, 0x09, 0x01];

    private static Guid16 G(uint a, ushort b, ushort c, ulong tail) => new(((ulong)a << 32) | ((ulong)b << 16) | c, tail);

    // ---------------------------------------------------------------- file envelope

    private sealed class Header
    {
        public int Major, Minor;
        public bool BigEndian;
        public long TocOffset;
        public Guid16 LsgGuid;
    }

    private readonly record struct TocEntry(Guid16 Guid, long Offset, uint Length, int Kind);

    private sealed class Segment
    {
        public Guid16 Guid;
        public int Kind;
        public byte[] Body = [];
    }

    public static bool Sniff(ReadOnlySpan<byte> d) =>
        d.Length >= 81 && d.StartsWith("Version "u8) && Encoding.ASCII.GetString(d[..Math.Min(d.Length, 80)]).Contains(" JT", StringComparison.Ordinal);

    private static Header ReadHeader(byte[] data)
    {
        if (data.Length < 81 + 4)
        {
            throw new ModelFormatException("The file is too short to hold a JT header.");
        }

        var banner = Encoding.ASCII.GetString(data, 0, 80);
        if (!banner.Contains("Version", StringComparison.Ordinal))
        {
            throw new ModelFormatException("The JT header has no version marker.");
        }

        var tokens = banner.Split([' ', '\t', '\0'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 2)
        {
            throw new ModelFormatException("The JT header has no version.");
        }

        var dot = tokens[1].IndexOf('.', StringComparison.Ordinal);
        if (dot < 1 || !int.TryParse(tokens[1].AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out var major) || !int.TryParse(tokens[1].AsSpan(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            throw new ModelFormatException("The JT version is not major.minor.");
        }

        var big = data[80] != 0;
        var c = new JtCursor(data, big, major, 81);
        c.I32();
        var toc = major >= 10 ? (long)c.U64() : c.U32();
        return new Header { Major = major, Minor = minor, BigEndian = big, TocOffset = toc, LsgGuid = c.ReadGuid() };
    }

    private static List<TocEntry> ReadToc(byte[] data, Header h)
    {
        if (h.TocOffset <= 0 || h.TocOffset >= data.Length)
        {
            throw new ModelFormatException("The JT table of contents lies outside the file.");
        }

        var c = new JtCursor(data, h.BigEndian, h.Major, (int)h.TocOffset);
        var count = c.I32();
        var row = h.Major >= 10 ? 32 : 28;
        if (count < 1 || (long)count * row > c.Remaining)
        {
            throw new ModelFormatException("The JT table of contents is empty or cut off.");
        }

        var list = new List<TocEntry>(count);
        for (var i = 0; i < count; i++)
        {
            var guid = c.ReadGuid();
            var offset = h.Major >= 10 ? (long)c.U64() : c.U32();
            var length = c.U32();
            var attrib = c.U32();
            list.Add(new TocEntry(guid, offset, length, (int)((attrib >> 24) & 0xFF)));
        }

        return list;
    }

    private static bool HasCompressionHeader(int kind) => kind is 1 or 2 or 3 or 4 or 17 or 18 or 20 or 23 or 24 or 30;

    private static byte[] Inflate(ReadOnlySpan<byte> packed, CancellationToken token)
    {
        var zlib = packed.Length >= 2 && (packed[0] & 0x0F) == 8 && ((packed[0] << 8) | packed[1]) % 31 == 0;
        using var input = new MemoryStream(packed.ToArray(), writable: false);
        using Stream stream = zlib ? new ZLibStream(input, CompressionMode.Decompress) : new DeflateStream(input, CompressionMode.Decompress);
        var output = new MemoryStream();
        var buffer = new byte[1 << 16];
        int n;
        try
        {
            while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                token.ThrowIfCancellationRequested();
                output.Write(buffer, 0, n);
                if (output.Length > MaxSegmentBytes)
                {
                    throw new ModelFormatException("A JT segment expands past the size limit.");
                }
            }
        }
        catch (InvalidDataException ex)
        {
            throw new ModelFormatException("A JT segment is not valid deflate data: " + ex.Message, ex);
        }

        if (output.Length == 0)
        {
            throw new ModelFormatException("A JT segment inflates to nothing.");
        }

        return output.ToArray();
    }

    private static Segment ReadSegment(byte[] data, long offset, Header h, CancellationToken token)
    {
        if (offset < 0 || offset >= data.Length)
        {
            throw new ModelFormatException("A JT segment lies outside the file.");
        }

        var c = new JtCursor(data, h.BigEndian, h.Major, (int)offset);
        var guid = c.ReadGuid();
        var kind = c.I32();
        var size = c.I32();
        if (size < 0)
        {
            throw new ModelFormatException("A JT segment has a negative length.");
        }

        var envelope = c.Pos - (int)offset;
        byte[] body;
        if (HasCompressionHeader(kind))
        {
            var flag = c.U32();
            var dataLength = c.I32();
            var algorithm = c.U8();
            if ((flag == 2 && algorithm == 2) || (flag == 3 && algorithm == 3))
            {
                var want = dataLength - 1;
                if (want < 0 || want > c.Remaining)
                {
                    throw new ModelFormatException("A JT segment declares more compressed bytes than the file holds.");
                }

                var packed = c.Bytes(want);
                body = flag == 2 ? Inflate(packed, token) : XzDecoder.DecodeConcatenated(packed, MaxSegmentBytes, token);
                if (body.Length == 0)
                {
                    throw new ModelFormatException("A JT segment decompresses to nothing.");
                }
            }
            else
            {
                body = c.Bytes(Math.Min(Math.Max(0, size - envelope), c.Remaining)).ToArray();
            }
        }
        else
        {
            body = c.Bytes(Math.Min(Math.Max(0, size - envelope), c.Remaining)).ToArray();
        }

        return new Segment { Guid = guid, Kind = kind, Body = body };
    }

    // ---------------------------------------------------------------- elements

    private enum ObjClass
    {
        None,
        Node,
        Attribute,
        Property,
        Element,
    }

    private static ObjClass ClassOf(Guid16 g)
    {
        if (g == NodePartition || g == NodePart || g == NodeMetaData || g == NodeLod || g == NodeRangeLod || g == NodeInstance || g == NodeGroup || g == NodeBase
            || g == NodeShapeBase || g == NodeShapeVertex || g == NodeShapeTriStripSet)
        {
            return ObjClass.Node;
        }

        if (g == AttributeTransform || g == AttributeMaterial)
        {
            return ObjClass.Attribute;
        }

        if (g == PropertyString || g == PropertyLateLoaded || g == PropertyBase)
        {
            return ObjClass.Property;
        }

        if (g == ShapeLodTriStripSet || g == ShapeLodVertex || g == ShapeLodBase || g == ElementProxyMetaData || g == XtBRep || g == ElementPmiManager)
        {
            return ObjClass.Element;
        }

        return ObjClass.None;
    }

    private readonly record struct RawElement(Guid16 Guid, int ObjectId, int BodyOffset, int BodyLength);

    private static RawElement? ReadEnvelope(JtCursor c, bool lsg)
    {
        var length = c.I32();
        var start = c.Pos;
        if (length < 0)
        {
            throw c.Fail("A JT element length is negative");
        }

        var guid = c.ReadGuid();
        if (guid == Guid16.EndOfElement)
        {
            return null;
        }

        var known = ClassOf(guid) != ObjClass.None;
        if (known)
        {
            c.U8();
        }

        var objectId = known && (lsg || c.Major > 8) ? c.I32() : 0;
        var bodyOffset = c.Pos;
        var end = (long)start + length;
        if (end > c.Pos + c.Remaining || end < bodyOffset)
        {
            throw c.Fail("A JT element extends past the end of its segment");
        }

        return new RawElement(guid, objectId, bodyOffset, (int)(end - bodyOffset));
    }

    private static void FinishElement(JtCursor c, RawElement e)
    {
        var end = e.BodyOffset + e.BodyLength;
        if (c.Pos > end)
        {
            throw c.Fail("A JT element body was read past its declared end");
        }

        c.Pos = end;
    }

    // ---------------------------------------------------------------- scene graph objects

    private sealed class Node
    {
        public Guid16 Type;
        public string Kind = "";
        public string Name = "";
        public string FileName = "";
        public readonly List<int> Attributes = new();
        public readonly List<int> Children = new();
        public int Target = -1;
        public bool IsShape;
        public readonly List<LateLoad> LateLoads = new();
    }

    private sealed class Attr
    {
        public Matrix4x4? Transform;
        public float[]? Diffuse;
        public float[]? Emission;
        public float Shininess;
    }

    private readonly record struct LateLoad(int PropertyId, Guid16 Segment);

    private sealed class Prop
    {
        public string? Text;
        public bool IsLateLoaded;
        public Guid16 Segment;
    }

    private sealed class LodGeometry
    {
        public int[] Indices = [];
        public float[] Vertices = [];
        public float[] Normals = [];
        public List<(int Start, int Count)>? Polygons;
    }

    private sealed class Scene
    {
        public readonly Dictionary<int, Node> Nodes = new();
        public readonly Dictionary<int, Attr> Attributes = new();
        public readonly Dictionary<int, Prop> Properties = new();
        public readonly Dictionary<int, LodGeometry> Geometry = new();
        public readonly Dictionary<int, List<ModelProperty>> Meta = new();
        public int Root = -1;
        public int Skipped;
    }

    private static void ReadBase(JtCursor c, Node n, bool withAttributes)
    {
        if (c.Major > 8)
        {
            c.ReadVersion();
        }

        c.U32();
        if (withAttributes)
        {
            var count = c.ReadLen();
            if ((long)count * 4 > c.Remaining)
            {
                throw c.Fail("A JT attribute list is cut off");
            }

            for (var i = 0; i < count; i++)
            {
                n.Attributes.Add(c.I32());
            }
        }
    }

    private static void ReadGroup(JtCursor c, Node n)
    {
        ReadBase(c, n, true);
        if (c.Major > 8)
        {
            c.ReadVersion();
        }

        var count = c.ReadLen();
        if ((long)count * 4 > c.Remaining)
        {
            throw c.Fail("A JT child list is cut off");
        }

        for (var i = 0; i < count; i++)
        {
            n.Children.Add(c.I32());
        }
    }

    private static void SkipBounds(JtCursor c) => c.Skip(24);

    private static Node ReadNode(Guid16 guid, JtCursor c)
    {
        var n = new Node { Type = guid };
        if (guid == NodePartition)
        {
            n.Kind = "Partition";
            ReadGroup(c, n);
            if (c.Major >= 10)
            {
                c.ReadVersion();
            }

            var flags = c.I32();
            n.FileName = c.ReadMbString();
            SkipBounds(c);
            c.F32();
            c.Skip(24);
            if ((flags & 1) != 0 && c.Major < 10)
            {
                SkipBounds(c);
            }
        }
        else if (guid == NodePart || guid == NodeMetaData)
        {
            n.Kind = guid == NodePart ? "Part" : "MetaData";
            ReadGroup(c, n);
            c.ReadVersion();
            if (guid == NodePart)
            {
                c.ReadVersion();
                c.I32();
            }
        }
        else if (guid == NodeLod || guid == NodeRangeLod)
        {
            n.Kind = guid == NodeLod ? "LOD" : "RangeLOD";
            ReadGroup(c, n);
            if (c.Major > 8)
            {
                c.ReadVersion();
            }

            if (c.Major < 10)
            {
                c.ReadVecF32();
                c.F32();
            }

            if (guid == NodeRangeLod)
            {
                if (c.Major > 8)
                {
                    c.ReadVersion();
                }

                c.ReadVecF32();
                c.Skip(12);
            }
        }
        else if (guid == NodeInstance)
        {
            n.Kind = "Instance";
            ReadBase(c, n, true);
            if (c.Major > 8)
            {
                c.ReadVersion();
            }

            n.Target = c.I32();
        }
        else if (guid == NodeGroup || guid == NodeBase)
        {
            n.Kind = "Group";
            ReadGroup(c, n);
        }
        else
        {
            n.Kind = "Shape";
            n.IsShape = true;
            ReadBase(c, n, true);
            if (c.Major > 8)
            {
                c.ReadVersion();
            }

            if (c.Major >= 10)
            {
                SkipBounds(c);
            }
            else
            {
                SkipBounds(c);
                SkipBounds(c);
            }

            c.F32();
            c.Skip(24);
            c.I32();
            c.F32();
        }

        return n;
    }

    private static void ReadAttributeBase(JtCursor c, out int version)
    {
        version = 0;
        if (c.Major > 8)
        {
            version = c.ReadVersion();
        }

        c.U8();
        c.U32();
        if (c.Major >= 10)
        {
            c.U32();
        }
    }

    private static Attr ReadAttribute(Guid16 guid, JtCursor c)
    {
        var a = new Attr();
        ReadAttributeBase(c, out var version);
        if (c.Major > 8)
        {
            c.ReadVersion();
        }

        if (guid == AttributeTransform)
        {
            var stored = c.U16();
            var wide = c.Major > 8;
            Span<double> m = [1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1];
            var mask = 0x8000;
            for (var i = 0; i < 16; i++)
            {
                if ((mask & stored) != 0)
                {
                    m[i] = wide ? c.F64() : c.F32();
                }

                mask >>= 1;
            }

            a.Transform = new Matrix4x4((float)m[0], (float)m[1], (float)m[2], (float)m[3], (float)m[4], (float)m[5], (float)m[6], (float)m[7],
                (float)m[8], (float)m[9], (float)m[10], (float)m[11], (float)m[12], (float)m[13], (float)m[14], (float)m[15]);
            return a;
        }

        var flags = c.U16();
        float[] Colour(bool single)
        {
            if (single)
            {
                var v = c.F32();
                return [v, v, v, 1];
            }

            return [c.F32(), c.F32(), c.F32(), c.F32()];
        }

        Colour((flags & 3) == 3);
        a.Diffuse = Colour(false);
        Colour((flags & 5) == 5);
        a.Emission = Colour((flags & 9) == 9);
        a.Shininess = c.F32();
        if (c.Major >= 10)
        {
            c.F32();
            c.F32();
            if (version >= 2)
            {
                c.U32();
            }
        }
        else if (version == 2)
        {
            c.F32();
        }

        return a;
    }

    private static Prop ReadProperty(Guid16 guid, JtCursor c)
    {
        var p = new Prop();
        if (guid == PropertyBase)
        {
            return p;
        }

        if (c.Major > 8)
        {
            c.ReadVersion();
        }

        c.U32();
        if (c.Major > 8)
        {
            c.ReadVersion();
        }

        if (guid == PropertyString)
        {
            p.Text = c.ReadMbString();
            return p;
        }

        p.IsLateLoaded = true;
        p.Segment = c.ReadGuid();
        c.I32();
        if (c.Major > 8)
        {
            c.I32();
            if (c.Major < 10)
            {
                c.I32();
            }
        }

        return p;
    }

    private static string StripName(string name)
    {
        var start = 0;
        while (start < name.Length && name[start] == ';')
        {
            start++;
        }

        var end = name.IndexOf(';', start);
        return end < 0 ? name[start..] : name[start..end];
    }

    private static Scene ReadScene(byte[] data, Header h, Dictionary<Guid16, TocEntry> toc, CancellationToken token, List<string> notes)
    {
        var scene = new Scene();
        if (!toc.TryGetValue(h.LsgGuid, out var lsgEntry))
        {
            throw new UnsupportedModelFormatException(ModelFormats.Jt, "The JT file has no logical scene graph segment.");
        }

        var seg = ReadSegment(data, lsgEntry.Offset, h, token);
        var c = new JtCursor(seg.Body, h.BigEndian, h.Major);
        var tick = 0L;
        while (ReadEnvelope(c, true) is { } e)
        {
            Cancel.Every(token, tick++);
            switch (ClassOf(e.Guid))
            {
                case ObjClass.Node:
                {
                    c.Pos = e.BodyOffset;
                    var node = ReadNode(e.Guid, c);
                    scene.Nodes[e.ObjectId] = node;
                    if (scene.Root < 0)
                    {
                        scene.Root = e.ObjectId;
                    }

                    break;
                }

                case ObjClass.Attribute:
                    c.Pos = e.BodyOffset;
                    scene.Attributes[e.ObjectId] = ReadAttribute(e.Guid, c);
                    break;
                case ObjClass.None:
                    scene.Skipped++;
                    break;
            }

            FinishElement(c, e);
            if (scene.Nodes.Count + scene.Attributes.Count > MaxNodes)
            {
                throw new ModelFormatException("The JT scene graph has too many objects.");
            }
        }

        while (ReadEnvelope(c, true) is { } e)
        {
            Cancel.Every(token, tick++);
            if (ClassOf(e.Guid) == ObjClass.Property)
            {
                c.Pos = e.BodyOffset;
                scene.Properties[e.ObjectId] = ReadProperty(e.Guid, c);
            }

            FinishElement(c, e);
        }

        // The property table binds names and late-loaded segments to objects.
        c.I16();
        var length = c.I32();
        for (var i = 0; i < length; i++)
        {
            Cancel.Every(token, tick++);
            var objectId = c.I32();
            var loads = new List<LateLoad>();
            string? name = null;
            while (true)
            {
                var key = c.I32();
                if (key == 0)
                {
                    break;
                }

                var value = c.I32();
                if (!scene.Properties.TryGetValue(value, out var prop))
                {
                    continue;
                }

                if (prop.IsLateLoaded)
                {
                    loads.Add(new LateLoad(value, prop.Segment));
                }
                else if (prop.Text is not null && scene.Properties.TryGetValue(key, out var keyProp) && keyProp.Text == "JT_PROP_NAME")
                {
                    name = prop.Text;
                }
            }

            if (scene.Nodes.TryGetValue(objectId, out var node))
            {
                if (name is not null)
                {
                    node.Name = StripName(name);
                }

                node.LateLoads.AddRange(loads);
            }
        }

        // Late-loaded segments: shape LODs and meta data. A segment that cannot be read is left out.
        foreach (var node in scene.Nodes.Values)
        {
            foreach (var load in node.LateLoads)
            {
                Cancel.Every(token, tick++);
                if (scene.Geometry.ContainsKey(load.PropertyId) || scene.Meta.ContainsKey(load.PropertyId) || !toc.TryGetValue(load.Segment, out var entry))
                {
                    continue;
                }

                try
                {
                    LoadElement(data, entry, h, scene, load.PropertyId, token);
                }
                catch (ModelFormatException ex) when (ex is not UnsupportedModelFormatException)
                {
                    notes.Add("A late-loaded segment was skipped: " + ex.Message);
                }
            }
        }

        return scene;
    }

    private static void LoadElement(byte[] data, TocEntry entry, Header h, Scene scene, int propertyId, CancellationToken token)
    {
        var seg = ReadSegment(data, entry.Offset, h, token);
        var c = new JtCursor(seg.Body, h.BigEndian, h.Major);
        if (ReadEnvelope(c, false) is not { } e || ClassOf(e.Guid) != ObjClass.Element)
        {
            return;
        }

        c.Pos = e.BodyOffset;
        if (e.Guid == ShapeLodTriStripSet || e.Guid == ShapeLodVertex)
        {
            var geometry = ReadShapeLod(c, e.Guid == ShapeLodTriStripSet, token);
            scene.Geometry[propertyId] = geometry;
        }
        else if (e.Guid == ElementProxyMetaData)
        {
            scene.Meta[propertyId] = ReadProxyMetaData(c);
        }
    }

    private static List<ModelProperty> ReadProxyMetaData(JtCursor c)
    {
        var list = new List<ModelProperty>();
        c.ReadVersion();
        string Key()
        {
            try
            {
                return c.ReadMbString();
            }
            catch (ModelFormatException)
            {
                return "";
            }
        }

        var key = Key();
        while (key.Length > 0 && list.Count < 4096)
        {
            if (c.Remaining < 1)
            {
                break;
            }

            var type = c.U8();
            string value;
            switch (type)
            {
                case 1: value = Key(); break;
                case 2: value = c.I32().ToString(CultureInfo.InvariantCulture); break;
                case 3: value = c.F32().ToString("0.######", CultureInfo.InvariantCulture); break;
                case 4:
                {
                    var y = c.I16();
                    var mo = c.I16();
                    var d = c.I16();
                    var hh = c.I16();
                    var mi = c.I16();
                    var ss = c.I16();
                    value = string.Create(CultureInfo.InvariantCulture, $"{y}-{mo:00}-{d:00}T{hh:00}:{mi:00}:{ss:00}");
                    break;
                }

                default:
                    return list;
            }

            list.Add(new ModelProperty(key, value, "JT"));
            key = Key();
        }

        return list;
    }

    // ---------------------------------------------------------------- shape LODs

    private static LodGeometry ReadShapeLod(JtCursor c, bool triStrip, CancellationToken token)
    {
        var major = c.Major;
        if (major < 9)
        {
            if (major > 7)
            {
                c.ReadVersion();
            }

            c.Skip(4);
            c.Skip(4);
            if (triStrip)
            {
                c.ReadVersion();
            }

            return ReadVertexBasedRep(c, token);
        }

        c.ReadVersion();
        c.ReadVersion();
        var binding = ReadVertexBinding2(c);
        if (major >= 10)
        {
            c.ReadVersion();
            c.ReadVersion();
            c.I32();
            if (triStrip)
            {
                if (!c.Bytes(16).SequenceEqual(TopologyMarker))
                {
                    throw c.Fail("The JT topology marker of a LOD is invalid");
                }

                c.Skip(9);
            }
        }
        else
        {
            c.ReadVersion();
            c.I32();
            c.ReadVersion();
        }

        if (!triStrip)
        {
            return new LodGeometry();
        }

        var geometry = ReadTopologicallyCompressed(c, binding, token);
        c.ReadVersion();
        return geometry;
    }

    private readonly record struct Binding(int VertexComponents, bool HasNormals);

    private static Binding ReadVertexBinding2(JtCursor c)
    {
        int lsb;
        if (!c.BigEndian)
        {
            lsb = c.U8();
            c.U32();
            c.Skip(2);
            c.U8();
        }
        else
        {
            c.U8();
            c.Skip(2);
            c.U32();
            lsb = c.U8();
        }

        var vertex = lsb & 7;
        var normal = (lsb >> 3) & 1;
        return new Binding(vertex switch { 1 => 2, 2 => 3, 4 => 4, _ => 0 }, normal != 0);
    }

    private static byte[] ReadQuantParams(JtCursor c) => c.Bytes(4).ToArray();

    private static LodGeometry ReadVertexBasedRep(JtCursor c, CancellationToken token)
    {
        c.ReadVersion();
        var normalBinding = c.U8();
        c.U8();
        c.U8();
        var qp = ReadQuantParams(c);
        var primitives = JtPackage.Load1(c, token);
        var offsets = primitives.Decode(c.BigEndian, UnpackKind.Stride1, token);
        if (offsets.Length < 2)
        {
            throw c.Fail("The primitive list of a JT shape rep is empty");
        }

        return qp[0] == 0 ? ReadLossless(c, normalBinding, offsets, token) : ReadQuantized(c, normalBinding, offsets, token);
    }

    private static int[] ExpandStrips(int[] offsets, int[]? slots)
    {
        var result = new List<int>();
        var off1 = 1;
        var off2 = 2;
        for (var w = 0; w + 1 < offsets.Length; w++)
        {
            var origin = offsets[w];
            while (origin < offsets[w + 1] - 2)
            {
                foreach (var s in new[] { origin, origin + off1, origin + off2 })
                {
                    result.Add(slots is null ? s : (uint)s < (uint)slots.Length ? slots[s] : -1);
                }

                off1 ^= 3;
                off2 ^= 3;
                origin++;
                if (result.Count > 1 << 27)
                {
                    throw new ModelFormatException("A JT strip set is too large.");
                }
            }
        }

        return result.ToArray();
    }

    private static LodGeometry ReadLossless(JtCursor c, byte normalBinding, int[] offsets, CancellationToken token)
    {
        c.I32();
        var declared = c.I32();
        byte[]? packed = declared > 0 ? c.Bytes(declared).ToArray() : null;
        var count = offsets[^1];
        if (count < 0 || count > 1 << 26)
        {
            throw c.Fail("A JT vertex count is out of range");
        }

        var withNormals = normalBinding == 1;
        var vertices = new float[count * 3];
        var normals = withNormals ? new float[count * 3] : [];
        JtCursor source;
        if (packed is not null)
        {
            source = new JtCursor(Inflate(packed, token), c.BigEndian, c.Major);
        }
        else
        {
            source = c;
        }

        for (var i = 0; i < count; i++)
        {
            Cancel.Every(token, i);
            if (withNormals)
            {
                for (var k = 0; k < 3; k++)
                {
                    normals[i * 3 + k] = source.F32();
                }
            }

            for (var k = 0; k < 3; k++)
            {
                vertices[i * 3 + k] = source.F32();
            }
        }

        return new LodGeometry { Indices = ExpandStrips(offsets, null), Vertices = vertices, Normals = normals };
    }

    private static LodGeometry ReadQuantized(JtCursor c, byte normalBinding, int[] offsets, CancellationToken token)
    {
        var coords = JtVertexData.LoadQuantizedCoords(c, token).Decode(c.BigEndian, token);
        var normals = JtDecoded.None;
        if (normalBinding != 0)
        {
            normals = JtVertexData.LoadQuantizedNormals(c, token).Decode(c.BigEndian, token);
        }

        var slots = JtPackage.Load1(c, token).Decode(c.BigEndian, UnpackKind.StripIdx, token);
        return new LodGeometry { Indices = ExpandStrips(offsets, slots), Vertices = coords.Data, Normals = normals.Data };
    }

    private static LodGeometry ReadTopologicallyCompressed(JtCursor c, Binding binding, CancellationToken token)
    {
        var meshInput = JtMeshInput.Load(c, token);
        ReadVertexBinding2(c);
        ReadQuantParams(c);
        var vertexCount = c.I32();
        c.I32();
        if (vertexCount == 0)
        {
            return new LodGeometry();
        }

        var coordsPresent = binding.VertexComponents > 0;
        var normalsPresent = binding.HasNormals;
        var coords = JtDecoded.None;
        var uniqueVertexCount = 0;
        if (coordsPresent)
        {
            var data = JtVertexData.LoadCompressedCoords(c, token);
            uniqueVertexCount = data.OutVertexCount;
            coords = data.Decode(c.BigEndian, token);
        }

        var norms = JtDecoded.None;
        if (normalsPresent)
        {
            norms = JtVertexData.LoadCompressedNormals(c, token).Decode(c.BigEndian, token);
        }

        var topology = meshInput.Decode(c.BigEndian, token);
        if (topology.VertexIndices.Length == 0)
        {
            return new LodGeometry();
        }

        if (coordsPresent && normalsPresent)
        {
            // One output vertex per distinct (vertex, normal) pair of the corners.
            var pairs = new Dictionary<(int, int), int>();
            var indices = new int[topology.VertexIndices.Length];
            var vertices = new List<float>();
            var normals = new List<float>();
            for (var i = 0; i < indices.Length; i++)
            {
                Cancel.Every(token, i);
                var v = topology.VertexIndices[i];
                var nIndex = topology.NormalIndices[i];
                if ((uint)v >= (uint)uniqueVertexCount)
                {
                    indices[i] = -1;
                    continue;
                }

                if (!pairs.TryGetValue((v, nIndex), out var id))
                {
                    id = pairs.Count;
                    pairs[(v, nIndex)] = id;
                    for (var k = 0; k < coords.Components; k++)
                    {
                        vertices.Add(coords.Get(v, k));
                    }

                    for (var k = 0; k < norms.Components; k++)
                    {
                        normals.Add((uint)nIndex < (uint)norms.Count ? norms.Get(nIndex, k) : 0f);
                    }
                }

                indices[i] = id;
            }

            return new LodGeometry { Indices = indices, Vertices = vertices.ToArray(), Normals = normals.ToArray(), Polygons = topology.Polygons };
        }

        if (coordsPresent)
        {
            return new LodGeometry { Indices = topology.VertexIndices, Vertices = coords.Data, Polygons = topology.Polygons };
        }

        return new LodGeometry();
    }

    // ---------------------------------------------------------------- model

    /// <summary>The binary Parasolid transmit streams the segments of a JT file hold (the XT B-rep elements): every segment body is searched for transmit headers.</summary>
    internal static List<byte[]> FindXtStreams(ReadOnlyMemory<byte> memory, CancellationToken token)
    {
        var data = memory.ToArray();
        var h = ReadHeader(data);
        var result = new List<byte[]>();
        var tick = 0L;
        foreach (var entry in ReadToc(data, h))
        {
            Cancel.Every(token, tick++, 0);
            try
            {
                result.AddRange(Parasolid.ParasolidReader.SplitStreams(ReadSegment(data, entry.Offset, h, token).Body));
            }
            catch (ModelFormatException)
            {
                // A segment that cannot be read holds no stream we can use.
            }
        }

        return result;
    }

    public static ModelMetadata ReadMetadata(ReadOnlyMemory<byte> memory, LoadOptions options)
    {
        var data = memory.ToArray();
        var h = ReadHeader(data);
        var toc = ReadToc(data, h);
        var meta = new ModelMetadata { Format = ModelFormats.Jt, DocumentKind = "model", FormatVersion = h.Major + "." + h.Minor, Application = "JT " + h.Major + "." + h.Minor, HasGeometry = false };
        meta.Notes.Add(string.Create(CultureInfo.InvariantCulture, $"{toc.Count} segments, {(h.BigEndian ? "big" : "little")}-endian."));
        meta.HasGeometry = toc.Any(t => t.Kind is >= 6 and <= 16);
        return meta;
    }

    public static Model3D Read(ReadOnlyMemory<byte> memory, LoadOptions options) => Read(memory, options, 0);

    private static Model3D Read(ReadOnlyMemory<byte> memory, LoadOptions options, int depth)
    {
        var token = options.Cancellation;
        var data = memory.ToArray();
        var h = ReadHeader(data);
        var tocList = ReadToc(data, h);
        var toc = new Dictionary<Guid16, TocEntry>();
        foreach (var t in tocList)
        {
            toc.TryAdd(t.Guid, t);
        }

        var notes = new List<string>();
        var scene = ReadScene(data, h, toc, token, notes);

        var model = new Model3D();
        var meta = model.Metadata;
        meta.Format = ModelFormats.Jt;
        meta.DocumentKind = "model";
        meta.FormatVersion = h.Major + "." + h.Minor;
        meta.Application = "JT " + h.Major + "." + h.Minor;
        meta.UpAxis = UpAxis.Y;
        if (options.AssumedUnits is { } units)
        {
            meta.Units = units;
        }

        meta.Notes.Add(string.Create(CultureInfo.InvariantCulture, $"{tocList.Count} segments. Units are not stored in a JT file."));
        foreach (var n in notes.Distinct().Take(8))
        {
            meta.Notes.Add(n);
        }

        if (scene.Skipped > 0)
        {
            meta.Notes.Add(string.Create(CultureInfo.InvariantCulture, $"{scene.Skipped} scene graph objects of kinds this reader does not know were skipped."));
        }

        var builder = new Builder(model, scene, options, depth);
        if (scene.Root >= 0)
        {
            var root = builder.Build(scene.Root, 0, null);
            if (root is not null)
            {
                model.Roots.Add(root);
            }
        }

        if (model.Roots.Count == 0)
        {
            model.Roots.Add(new SceneNode("JT"));
        }

        if (model.Meshes.Count == 0 && Parasolid.ParasolidReader.ReadEmbedded(FindXtStreams(memory, token), options) is { } brep)
        {
            // No tessellated shapes: the XT B-rep elements (Parasolid transmit data) are the geometry.
            model.Meshes.AddRange(brep.Meshes);
            model.Materials.AddRange(brep.Materials);
            model.Roots.Clear();
            model.Roots.AddRange(brep.Roots);
            model.Warnings.AddRange(brep.Warnings);
            meta.Units ??= LengthUnit.Meter;
            meta.Notes.Add("The geometry is tessellated from the Parasolid B-rep (XT) elements of the file, which stores no tessellated shapes.");
        }

        meta.HasGeometry = model.Meshes.Count > 0;
        meta.MaterialNames.AddRange(model.Materials.Select(m => m.Name ?? ""));
        foreach (var name in model.Roots.SelectMany(Names).Distinct().Take(512))
        {
            meta.PartNames.Add(name);
        }

        meta.Statistics = model.ComputeStatistics();
        meta.Bounds = model.ComputeBounds();
        return model;
    }

    private static IEnumerable<string> Names(SceneNode n)
    {
        var stack = new Stack<SceneNode>();
        stack.Push(n);
        var guard = 0;
        while (stack.Count > 0 && guard++ < 100_000)
        {
            var node = stack.Pop();
            if (!string.IsNullOrEmpty(node.Name) && node.Properties.ContainsKey("Kind") && node.Properties["Kind"] == "Part")
            {
                yield return node.Name!;
            }

            foreach (var child in node.Children)
            {
                stack.Push(child);
            }
        }
    }

    private sealed class Builder
    {
        private readonly Model3D _model;
        private readonly Scene _scene;
        private readonly LoadOptions _options;
        private readonly CancellationToken _token;
        private readonly HashSet<int> _active = new();
        private readonly Dictionary<(int, int), MeshData?> _meshes = new();
        private readonly Dictionary<string, int> _materials = new(StringComparer.Ordinal);
        private int _nodes;
        private long _triangles;
        private long _tick;

        private readonly int _depth;
        private readonly Dictionary<string, Model3D?> _companions = new(StringComparer.OrdinalIgnoreCase);

        public Builder(Model3D model, Scene scene, LoadOptions options, int depth)
        {
            _depth = depth;
            _model = model;
            _scene = scene;
            _options = options;
            _token = options.Cancellation;
        }

        public SceneNode? Build(int id, int depth, int? inheritedMaterial)
        {
            Cancel.Every(_token, _tick++, 63);
            if (!_scene.Nodes.TryGetValue(id, out var node))
            {
                return null;
            }

            if (depth > MaxDepth || ++_nodes > MaxNodes)
            {
                throw new ModelFormatException("The JT scene graph is too deep or too large.");
            }

            if (!_active.Add(id))
            {
                return null;
            }

            try
            {
                var sn = new SceneNode(node.Name.Length > 0 ? node.Name : node.Kind);
                sn.Properties["Kind"] = node.Kind;
                var material = inheritedMaterial;
                foreach (var attrId in node.Attributes)
                {
                    if (!_scene.Attributes.TryGetValue(attrId, out var attr))
                    {
                        continue;
                    }

                    if (attr.Transform is { } m && sn.Transform == Matrix4x4.Identity)
                    {
                        sn.Transform = m;
                    }

                    if (attr.Diffuse is not null)
                    {
                        material = MaterialIndex(attr);
                    }
                }

                foreach (var load in node.LateLoads)
                {
                    if (_scene.Geometry.TryGetValue(load.PropertyId, out var geometry))
                    {
                        sn.Mesh ??= MeshFor(load.PropertyId, geometry, node.Name.Length > 0 ? node.Name : node.Kind, material);
                    }

                    if (_scene.Meta.TryGetValue(load.PropertyId, out var props))
                    {
                        foreach (var p in props)
                        {
                            sn.Properties.TryAdd(p.Name, p.Value);
                        }
                    }
                }

                // Alternative levels of detail: the first child is the finest.
                var children = node.Kind is "LOD" or "RangeLOD" ? node.Children.Take(1) : node.Children;
                foreach (var child in children)
                {
                    if (Build(child, depth + 1, material) is { } cn)
                    {
                        sn.Children.Add(cn);
                    }
                }

                if (node.Target >= 0 && Build(node.Target, depth + 1, material) is { } target)
                {
                    sn.Children.Add(target);
                }

                if (node.Kind == "Partition" && node.FileName.Length > 0)
                {
                    AddPartition(sn, node.FileName);
                }

                return sn;
            }
            finally
            {
                _active.Remove(id);
            }
        }

        /// <summary>
        /// A partition node names the JT file it was saved as. When it has no content of its own, that file holds its subtree: the name is recorded in the metadata; when the caller supplies
        /// <see cref="LoadOptions.ResolveCompanion"/> the file is read (once, however many partitions name it; nested to three levels) and its scene
        /// is placed under the node.
        /// </summary>
        private void AddPartition(SceneNode sn, string fileName)
        {
            sn.Properties["file"] = fileName;
            if (sn.Children.Count > 0 || sn.Mesh is not null)
            {
                return; // the partition carries its own content: the name is the file it was saved as, not a reference
            }

            var meta = _model.Metadata;
            if (!meta.ComponentFiles.Contains(fileName, StringComparer.OrdinalIgnoreCase) && meta.ComponentFiles.Count < 512)
            {
                meta.ComponentFiles.Add(fileName);
            }

            if (_options.ResolveCompanion is null || _depth >= 3 || _companions.Count >= 256)
            {
                return;
            }

            if (!_companions.TryGetValue(fileName, out var component))
            {
                component = null;
                if (_options.ResolveCompanion(fileName) is { Length: > 0 } bytes)
                {
                    try
                    {
                        component = Read(bytes, _options, _depth + 1);
                        ModelMerge.AdoptResources(_model, component);
                    }
                    catch (ModelFormatException ex) when (ex is not UnsupportedModelFormatException)
                    {
                        _model.Metadata.Notes.Add("The partition file " + fileName + " could not be read: " + ex.Message);
                        component = null;
                    }
                }
                else
                {
                    _model.Metadata.Notes.Add("The partition file " + fileName + " was not supplied.");
                }

                _companions[fileName] = component;
            }

            if (component is not null)
            {
                foreach (var root in component.Roots)
                {
                    sn.Children.Add(ModelMerge.CloneTree(root));
                }
            }
        }

        private int MaterialIndex(Attr attr)
        {
            var d = attr.Diffuse!;
            var e = attr.Emission ?? [0, 0, 0, 1];
            var key = string.Create(CultureInfo.InvariantCulture, $"{d[0]:0.###},{d[1]:0.###},{d[2]:0.###},{d[3]:0.###},{attr.Shininess:0.#}");
            if (!_materials.TryGetValue(key, out var index))
            {
                index = _model.Materials.Count;
                var roughness = Math.Clamp(1f - MathF.Sqrt(attr.Shininess / (attr.Shininess + 1f)), 0f, 1f);
                _model.Materials.Add(new ModelMaterial
                {
                    Name = "Material " + (index + 1).ToString(CultureInfo.InvariantCulture),
                    BaseColor = new Vector4(Math.Clamp(d[0], 0, 1), Math.Clamp(d[1], 0, 1), Math.Clamp(d[2], 0, 1), Math.Clamp(d[3], 0, 1)),
                    Roughness = float.IsFinite(roughness) ? roughness : 0.6f,
                });
                _materials[key] = index;
                _ = e;
            }

            return index;
        }

        private MeshData? MeshFor(int key, LodGeometry g, string name, int? material)
        {
            var cacheKey = (key, material ?? -1);
            if (_meshes.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }

            MeshData? mesh = null;
            var vertexCount = g.Vertices.Length / 3;
            if (vertexCount > 0 && g.Indices.Length >= 3)
            {
                var tris = new List<uint>(g.Indices.Length);
                void Triangle(int a, int b, int c)
                {
                    if ((uint)a < (uint)vertexCount && (uint)b < (uint)vertexCount && (uint)c < (uint)vertexCount && a != b && b != c && a != c)
                    {
                        tris.Add((uint)a);
                        tris.Add((uint)b);
                        tris.Add((uint)c);
                    }
                }

                if (g.Polygons is { } polygons)
                {
                    foreach (var (start, count) in polygons)
                    {
                        Cancel.Every(_token, _tick++);
                        for (var k = 1; k + 1 < count; k++)
                        {
                            Triangle(g.Indices[start], g.Indices[start + k], g.Indices[start + k + 1]);
                        }
                    }
                }
                else
                {
                    for (var i = 0; i + 2 < g.Indices.Length; i += 3)
                    {
                        Cancel.Every(_token, _tick++);
                        Triangle(g.Indices[i], g.Indices[i + 1], g.Indices[i + 2]);
                    }
                }

                if (tris.Count > 0)
                {
                    _triangles += tris.Count / 3;
                    if (_triangles > _options.MaxTriangles)
                    {
                        throw new ModelFormatException("The JT model exceeds the triangle limit.");
                    }

                    mesh = new MeshData { Name = name, Positions = g.Vertices, Indices = tris.ToArray() };
                    if (g.Normals.Length == g.Vertices.Length)
                    {
                        mesh.Normals = g.Normals;
                    }

                    if (material is { } m)
                    {
                        mesh.Groups.Add(new MeshGroup(0, mesh.Indices.Length, m));
                    }

                    _model.Meshes.Add(mesh);
                }
            }

            _meshes[cacheKey] = mesh;
            return mesh;
        }
    }
}
