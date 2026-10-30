using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.Cad;

/// <summary>
/// Entities of AC1015 (AutoCAD 2000) drawings: the class table, then LINE, CIRCLE, ARC, POINT, TEXT, LWPOLYLINE, 3DFACE, SOLID and INSERT objects
/// decoded from the bit streams of the object map, with their layers; blocks are expanded as shared meshes under the insert transforms. Other
/// object types (hatches, dimensions, splines, polylines, attributes, XREFs) are counted in a warning.
/// </summary>
internal static class DwgGeometry
{
    private sealed class Bits
    {
        private readonly ReadOnlyMemory<byte> _data;
        public long Position;

        public Bits(ReadOnlyMemory<byte> data)
        {
            _data = data;
        }

        public long Length => _data.Length * 8L;

        public bool Overrun => Position > Length;

        public int Read(int count)
        {
            var span = _data.Span;
            var value = 0;
            for (var i = 0; i < count; i++)
            {
                var index = Position >> 3;
                var bit = index < span.Length ? (span[(int)index] >> (7 - (int)(Position & 7))) & 1 : 0;
                value = (value << 1) | bit;
                Position++;
            }

            return value;
        }

        public bool Bit() => Read(1) != 0;

        public int RC() => Read(8);

        public int BS() => Read(2) switch { 0 => Read(8) | (Read(8) << 8), 1 => Read(8), 2 => 0, _ => 256 };

        public int BL() => Read(2) switch { 0 => Read(8) | (Read(8) << 8) | (Read(8) << 16) | (Read(8) << 24), 1 => Read(8), _ => 0 };

        public double RD()
        {
            Span<byte> b = stackalloc byte[8];
            for (var i = 0; i < 8; i++)
            {
                b[i] = (byte)Read(8);
            }

            return BinaryPrimitives.ReadDoubleLittleEndian(b);
        }

        public double BD() => Read(2) switch { 0 => RD(), 1 => 1.0, 2 => 0.0, _ => 0.0 };

        public double DD(double fallback)
        {
            switch (Read(2))
            {
                case 0:
                    return fallback;
                case 1:
                {
                    Span<byte> b = stackalloc byte[8];
                    BinaryPrimitives.WriteDoubleLittleEndian(b, fallback);
                    for (var i = 0; i < 4; i++)
                    {
                        b[i] = (byte)Read(8);
                    }

                    return BinaryPrimitives.ReadDoubleLittleEndian(b);
                }

                case 2:
                {
                    Span<byte> b = stackalloc byte[8];
                    BinaryPrimitives.WriteDoubleLittleEndian(b, fallback);
                    b[4] = (byte)Read(8);
                    b[5] = (byte)Read(8);
                    for (var i = 0; i < 4; i++)
                    {
                        b[i] = (byte)Read(8);
                    }

                    return BinaryPrimitives.ReadDoubleLittleEndian(b);
                }

                default:
                    return RD();
            }
        }

        public Vector3 BD3() => new((float)BD(), (float)BD(), (float)BD());

        /// <summary>Thickness: a single bit says zero (R2000), otherwise a bit double.</summary>
        public double BT() => Bit() ? 0 : BD();

        /// <summary>Extrusion: a single bit says (0, 0, 1) (R2000), otherwise three bit doubles.</summary>
        public Vector3 BE() => Bit() ? Vector3.UnitZ : BD3();

        public long Handle(long own, out bool relative)
        {
            var code = Read(4);
            var counter = Read(4);
            long value = 0;
            for (var i = 0; i < counter; i++)
            {
                value = (value << 8) | (uint)Read(8);
            }

            relative = code >= 6;
            return code switch
            {
                6 => own + 1,
                8 => own - 1,
                0xA => own + value,
                0xC => own - value,
                _ => value,
            };
        }

        public string TV()
        {
            var length = BS();
            if (length < 0 || length > 4096 || Position + length * 8L > Length)
            {
                Position = Length + 1;
                return "";
            }

            var bytes = new byte[length];
            for (var i = 0; i < length; i++)
            {
                bytes[i] = (byte)Read(8);
            }

            var end = Array.IndexOf(bytes, (byte)0);
            return Encoding.Latin1.GetString(bytes, 0, end < 0 ? length : end);
        }
    }

    private sealed class Obj
    {
        public long Handle;
        public long Location;
        public int Type;
        public string Name = "";
    }

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options, ModelMetadata header)
    {
        var s = data.Span;
        var cancel = options.Cancellation;
        var model = new Model3D();
        var meta = model.Metadata;
        meta.Format = header.Format;
        meta.DocumentKind = "drawing";
        meta.FormatVersion = header.FormatVersion;
        meta.Application = header.Application;
        foreach (var p in header.Properties)
        {
            meta.Properties.Add(p);
        }

        meta.HasPreview = header.HasPreview;
        meta.UpAxis = UpAxis.Z;

        var records = (int)BinaryPrimitives.ReadUInt32LittleEndian(s[0x15..]);
        long mapStart = 0, mapSize = 0, classStart = 0, classSize = 0;
        for (var i = 0; i < records && i < 32 && 0x19 + i * 9 + 9 <= s.Length; i++)
        {
            var number = s[0x19 + i * 9];
            var seeker = BinaryPrimitives.ReadUInt32LittleEndian(s[(0x19 + i * 9 + 1)..]);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(s[(0x19 + i * 9 + 5)..]);
            if (number == 2)
            {
                (mapStart, mapSize) = (seeker, size);
            }
            else if (number == 1)
            {
                (classStart, classSize) = (seeker, size);
            }
        }

        if (mapStart <= 0 || mapStart + mapSize > s.Length)
        {
            throw new ModelFormatException("The DWG object map is missing.");
        }

        var classes = ReadClasses(data, classStart, classSize);
        var objects = DwgReader.ReadObjectMapInternal(s, mapStart, mapSize, cancel);
        var table = new List<Obj>(objects.Count);
        var layerHandles = new Dictionary<long, int>();
        var blockHandles = new HashSet<long>();
        long tick = 0;
        foreach (var (handle, location) in objects)
        {
            Cancel.Every(cancel, tick++);
            var obj = new Obj { Handle = handle, Location = location };
            if (TryOpen(data, location, out var bits))
            {
                obj.Type = bits.BS();
                if (obj.Type >= 500 && classes.TryGetValue(obj.Type, out var name))
                {
                    obj.Name = name;
                }
            }

            table.Add(obj);
            if (obj.Type == 51 && DwgReader.TryReadLayerNameInternal(s, location, out var layerName) && layerName.Length > 0)
            {
                layerHandles[handle] = model.Layers.Count;
                model.Layers.Add(new ModelLayer(layerName));
                meta.LayerNames.Add(layerName);
            }
            else if (obj.Type == 49)
            {
                blockHandles.Add(handle);
            }
        }

        var ranges = BlockRanges(data, table, blockHandles);
        var links = new Dictionary<long, long>();
        var entities = new List<Entity>();
        var skipped = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var obj in table)
        {
            Cancel.Every(cancel, tick++);
            var kind = obj.Type switch
            {
                1 => "TEXT",
                7 => "INSERT",
                17 => "ARC",
                18 => "CIRCLE",
                19 => "LINE",
                27 => "POINT",
                28 => "3DFACE",
                31 => "SOLID",
                77 => "LWPOLYLINE",
                _ => obj.Name == "LWPOLYLINE" ? "LWPOLYLINE" : null,
            };
            if (kind is null)
            {
                if (IsEntityType(obj))
                {
                    if (CommonLink(data, obj) is { } link)
                    {
                        links[obj.Handle] = link;
                    }

                    var label = obj.Name.Length > 0 ? obj.Name : obj.Type == 78 ? "HATCH" : "type " + obj.Type.ToString(CultureInfo.InvariantCulture);
                    skipped[label] = skipped.GetValueOrDefault(label) + 1;
                }

                continue;
            }

            try
            {
                if (Decode(data, obj, kind, layerHandles, blockHandles) is { } entity)
                {
                    entities.Add(entity);
                }
            }
            catch (ModelFormatException)
            {
                skipped["damaged " + kind] = skipped.GetValueOrDefault("damaged " + kind) + 1;
            }
        }

        Build(model, entities, ranges, links, options);
        foreach (var (what, count) in skipped.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            model.Warnings.Add($"{count} {what} object(s) were not read.");
        }

        meta.Properties.Add(new ModelProperty("Entities read", entities.Count.ToString(CultureInfo.InvariantCulture), "dwg"));
        meta.HasGeometry = model.Meshes.Count > 0 || model.Texts.Count > 0;
        model.DetectIs2D();
        meta.Statistics = model.ComputeStatistics();
        meta.Bounds = model.ComputeBounds();
        meta.Notes.Add("Units and colours by layer are not read from the header and layer tables of this release.");
        return model;
    }

    private static bool IsEntityType(Obj o) => o.Type is (>= 1 and <= 36) and not (>= 4 and <= 6) and not (>= 11 and <= 14) or 78 || (o.Type >= 500 && o.Name is "HATCH" or "MTEXT" or "SPLINE" or "ELLIPSE" or "IMAGE" or "WIPEOUT" or "OLE2FRAME");

    private static bool TryOpen(ReadOnlyMemory<byte> data, long location, out Bits bits)
    {
        bits = null!;
        var s = data.Span;
        if (location < 0 || location + 2 > s.Length)
        {
            return false;
        }

        var p = location;
        long size = 0;
        for (var shift = 0; shift < 30; shift += 15)
        {
            if (p + 2 > s.Length)
            {
                return false;
            }

            var word = BinaryPrimitives.ReadUInt16LittleEndian(s[(int)p..]);
            p += 2;
            size |= (long)(word & 0x7FFF) << shift;
            if ((word & 0x8000) == 0)
            {
                break;
            }
        }

        if (size <= 0 || p + size > s.Length)
        {
            return false;
        }

        bits = new Bits(data.Slice((int)p, (int)size));
        return true;
    }

    /// <summary>The class table: class number to the DXF class name (LWPOLYLINE, HATCH, ...).</summary>
    private static Dictionary<int, string> ReadClasses(ReadOnlyMemory<byte> data, long start, long size)
    {
        var result = new Dictionary<int, string>();
        var s = data.Span;
        if (start <= 0 || start + 16 + 4 + size > s.Length || size < 20)
        {
            return result;
        }

        var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(s[(int)(start + 16)..]);
        if (length <= 0 || start + 20 + length > s.Length)
        {
            return result;
        }

        var bits = new Bits(data.Slice((int)start + 20, length));
        try
        {
            while (bits.Position + 16 < bits.Length && result.Count < 10_000)
            {
                var number = bits.BS();
                bits.BS(); // version
                bits.TV(); // application
                bits.TV(); // C++ class name
                var dxfName = bits.TV();
                bits.Bit();
                bits.BS();
                if (bits.Overrun || number < 500 || dxfName.Length == 0)
                {
                    break;
                }

                result[number] = dxfName;
            }
        }
        catch (ModelFormatException)
        {
        }

        return result;
    }

    // ---------------------------------------------------------------------------------------------------------------

    private sealed class BlockRangeSet
    {
        public Dictionary<long, (long First, long Last)> Blocks { get; } = new();

        public long ModelHeader;
        public long PaperHeader;
    }

    /// <summary>
    /// The entities of each block as a handle range: a block header lists its BLOCK entity, its first and last entity and its ENDBLK. The list is
    /// found by looking, in the header's bits, for the handle of a BLOCK entity followed by two entity handles and an ENDBLK handle.
    /// </summary>
    private static BlockRangeSet BlockRanges(ReadOnlyMemory<byte> data, List<Obj> table, HashSet<long> headers)
    {
        var result = new BlockRangeSet();
        var blockNames = new Dictionary<long, string>();
        var endBlocks = new HashSet<long>();
        foreach (var o in table)
        {
            if (o.Type == 5)
            {
                endBlocks.Add(o.Handle);
            }
            else if (o.Type == 4 && TryOpen(data, o.Location, out var b))
            {
                try
                {
                    b.BS();
                    b.Read(32);
                    var h = b.Handle(0, out _);
                    for (var g = 0; g < 4096; g++)
                    {
                        var eed = b.BS();
                        if (eed == 0)
                        {
                            break;
                        }

                        b.Handle(h, out _);
                        b.Position += eed * 8L;
                    }

                    if (b.Bit())
                    {
                        b.Position += (long)b.Read(32) * 8;
                    }

                    b.Read(2);
                    b.BL();
                    b.Bit();
                    b.BS();
                    b.BD();
                    b.Read(2);
                    b.Read(2);
                    b.BS();
                    b.RC();
                    blockNames[o.Handle] = b.TV();
                }
                catch (ModelFormatException)
                {
                }
            }
        }

        foreach (var o in table.Where(t => t.Type == 49))
        {
            if (!TryOpen(data, o.Location, out var b))
            {
                continue;
            }

            b.BS();
            b.Read(32);
            var own = b.Handle(0, out _);
            long start = b.Position;
            for (var p = start; p + 40 < b.Length; p++)
            {
                b.Position = p;
                var blockEntity = b.Handle(own, out var relative);
                if (relative || !blockNames.ContainsKey(blockEntity))
                {
                    continue;
                }

                var first = b.Handle(own, out _);
                var last = b.Handle(own, out _);
                var end = b.Handle(own, out _);
                if (!endBlocks.Contains(end) || b.Overrun)
                {
                    continue;
                }

                result.Blocks[own] = (first, last);
                var name = blockNames[blockEntity];
                if (name.Equals("*Model_Space", StringComparison.OrdinalIgnoreCase))
                {
                    result.ModelHeader = own;
                }
                else if (name.StartsWith("*Paper_Space", StringComparison.OrdinalIgnoreCase))
                {
                    result.PaperHeader = own;
                }

                break;
            }
        }

        return result;
    }

    private sealed class Entity
    {
        public string Kind = "";
        public long Handle;
        public long Owner;
        public int Mode;
        public int Layer = -1;
        public int Colour = 256;
        public long BlockHandle;
        public long Next;
        public bool NoLinks;
        public List<Vector3[]> Polylines = new();
        public List<Vector3[]> Faces = new();
        public Vector3? Point;
        public ModelText? Text;
        public Matrix4x4 Transform = Matrix4x4.Identity;
    }

    /// <summary>The next-entity handle of an entity this reader does not decode: the adjacent handle when the header says there are no explicit links; 0 otherwise.</summary>
    private static long? CommonLink(ReadOnlyMemory<byte> data, Obj obj)
    {
        if (!TryOpen(data, obj.Location, out var b))
        {
            return null;
        }

        try
        {
            b.BS();
            b.Read(32);
            var handle = b.Handle(0, out _);
            for (var guard = 0; guard < 4096; guard++)
            {
                var eed = b.BS();
                if (eed == 0)
                {
                    break;
                }

                b.Handle(handle, out _);
                b.Position += eed * 8L;
            }

            if (b.Bit())
            {
                b.Position += (long)b.Read(32) * 8;
            }

            b.Read(2);
            b.BL();
            var noLinks = b.Bit();
            return b.Overrun ? null : noLinks ? handle + 1 : 0;
        }
        catch (ModelFormatException)
        {
            return null;
        }
    }

    private static Entity? Decode(ReadOnlyMemory<byte> data, Obj obj, string kind, Dictionary<long, int> layers, HashSet<long> blocks)
    {
        if (!TryOpen(data, obj.Location, out var b))
        {
            return null;
        }

        b.BS(); // type
        b.Read(32); // size in bits
        var handle = b.Handle(0, out _);
        for (var guard = 0; guard < 4096; guard++)
        {
            var eed = b.BS();
            if (eed == 0)
            {
                break;
            }

            b.Handle(handle, out _);
            b.Read(0);
            b.Position += eed * 8L;
            if (b.Overrun)
            {
                throw new ModelFormatException("Extended data overruns its object.");
            }
        }

        if (b.Bit())
        {
            var graphic = b.Read(32);
            b.Position += (long)graphic * 8;
        }

        var mode = b.Read(2);
        var reactors = b.BL();
        var noLinks = b.Bit();
        var colour = b.BS();
        b.BD(); // line type scale
        var ltypeFlags = b.Read(2);
        b.Read(2); // plot style flags
        b.BS(); // invisibility
        b.RC(); // line weight
        if (reactors < 0 || reactors > 1000 || b.Overrun)
        {
            throw new ModelFormatException("An entity header is damaged.");
        }

        var e = new Entity { Kind = kind, Handle = handle, Mode = mode, Colour = colour };
        switch (kind)
        {
            case "LINE":
            {
                var zZero = b.Bit();
                double x1 = b.RD();
                double x2 = b.DD(x1);
                double y1 = b.RD();
                double y2 = b.DD(y1);
                double z1 = 0, z2 = 0;
                if (!zZero)
                {
                    z1 = b.RD();
                    z2 = b.DD(z1);
                }

                b.BT();
                var extrusion = b.BE();
                _ = extrusion;
                e.Polylines.Add([new Vector3((float)x1, (float)y1, (float)z1), new Vector3((float)x2, (float)y2, (float)z2)]);
                break;
            }

            case "CIRCLE":
            case "ARC":
            {
                var centre = b.BD3();
                var radius = b.BD();
                b.BT();
                var extrusion = b.BE();
                double a0 = 0, a1 = 2 * Math.PI;
                if (kind == "ARC")
                {
                    a0 = b.BD();
                    a1 = b.BD();
                }

                if (!(radius >= 0) || !double.IsFinite(radius) || !double.IsFinite(a0) || !double.IsFinite(a1))
                {
                    throw new ModelFormatException("A circle or arc has impossible values.");
                }

                e.Polylines.Add(Arc(centre, radius, a0, a1, extrusion, kind == "CIRCLE"));
                break;
            }

            case "POINT":
            {
                var p = b.BD3();
                e.Point = p;
                break;
            }

            case "TEXT":
            {
                var flags = b.RC();
                double elevation = 0;
                if ((flags & 1) == 0)
                {
                    elevation = b.RD();
                }

                var x = b.RD();
                var y = b.RD();
                if ((flags & 2) == 0)
                {
                    b.DD(x);
                    b.DD(y);
                }

                b.BE();
                b.BT();
                if ((flags & 4) == 0)
                {
                    b.RD();
                }

                var rotation = (flags & 8) == 0 ? b.RD() : 0;
                var height = b.RD();
                if ((flags & 0x10) == 0)
                {
                    b.RD();
                }

                var text = b.TV();
                if ((flags & 0x20) == 0)
                {
                    b.BS();
                }

                if ((flags & 0x40) == 0)
                {
                    b.BS();
                }

                if ((flags & 0x80) == 0)
                {
                    b.BS();
                }

                e.Text = new ModelText { Text = text, Position = new Vector3((float)x, (float)y, (float)elevation), Height = (float)height, Rotation = (float)(rotation * 180 / Math.PI) };
                break;
            }

            case "LWPOLYLINE":
            {
                var flag = b.BS();
                if ((flag & 4) != 0)
                {
                    b.BD();
                }

                double elevation = 0;
                if ((flag & 8) != 0)
                {
                    elevation = b.BD();
                }

                if ((flag & 2) != 0)
                {
                    b.BD();
                }

                var normal = Vector3.UnitZ;
                if ((flag & 1) != 0)
                {
                    normal = b.BD3();
                }

                var count = b.BL();
                var bulges = (flag & 16) != 0 ? b.BL() : 0;
                var widths = (flag & 32) != 0 ? b.BL() : 0;
                if (count < 0 || count > 10_000_000 || bulges < 0 || bulges > count || widths < 0 || widths > count || b.Position + (long)count * 2 > b.Length)
                {
                    throw new ModelFormatException("A polyline declares an impossible number of vertices.");
                }

                var points = new (double X, double Y)[count];
                for (var i = 0; i < count; i++)
                {
                    points[i] = i == 0 ? (b.RD(), b.RD()) : (b.DD(points[i - 1].X), b.DD(points[i - 1].Y));
                }

                var bulge = new double[count];
                for (var i = 0; i < bulges; i++)
                {
                    bulge[i] = b.BD();
                }

                for (var i = 0; i < widths; i++)
                {
                    b.BD();
                    b.BD();
                }

                e.Polylines.Add(Lwpolyline(points, bulge, elevation, normal, (flag & 512) != 0));
                break;
            }

            case "3DFACE":
            {
                var noFlag = b.Bit();
                var zZero = b.Bit();
                var corners = new Vector3[4];
                double x = b.RD(), y = b.RD(), z = zZero ? 0 : b.RD();
                corners[0] = new Vector3((float)x, (float)y, (float)z);
                for (var i = 1; i < 4; i++)
                {
                    x = b.DD(x);
                    y = b.DD(y);
                    z = b.DD(z);
                    corners[i] = new Vector3((float)x, (float)y, (float)z);
                }

                if (!noFlag)
                {
                    b.BS();
                }

                e.Faces.Add(corners);
                break;
            }

            case "SOLID":
            {
                var thickness = b.BT();
                var elevation = b.BD();
                var c = new Vector3[4];
                for (var i = 0; i < 4; i++)
                {
                    c[i] = new Vector3((float)b.RD(), (float)b.RD(), (float)elevation);
                }

                _ = thickness;
                b.BE();
                // A SOLID's corners are in "bow tie" order: 1, 2, 4, 3.
                e.Faces.Add([c[0], c[1], c[3], c[2]]);
                break;
            }

            case "INSERT":
            {
                var at = b.BD3();
                double sx, sy, sz;
                switch (b.Read(2))
                {
                    case 0:
                        sx = b.RD();
                        sy = b.DD(sx);
                        sz = b.DD(sx);
                        break;
                    case 1:
                        sx = 1;
                        sy = b.DD(1);
                        sz = b.DD(1);
                        break;
                    case 2:
                        sx = b.RD();
                        sy = sx;
                        sz = sx;
                        break;
                    default:
                        sx = sy = sz = 1;
                        break;
                }

                var rotation = b.BD();
                var extrusion = b.BD3();
                _ = extrusion;
                b.Bit(); // has attributes
                if (!double.IsFinite(sx) || !double.IsFinite(sy) || !double.IsFinite(sz) || !double.IsFinite(rotation))
                {
                    throw new ModelFormatException("An insert has impossible values.");
                }

                // World transform of a block's content: scale, rotate about z, move (System.Numerics row-vector order).
                e.Transform = Matrix4x4.CreateScale((float)sx, (float)sy, (float)sz) * Matrix4x4.CreateRotationZ((float)rotation) * Matrix4x4.CreateTranslation(at);
                break;
            }
        }

        if (b.Overrun)
        {
            throw new ModelFormatException("An entity overruns its data.");
        }

        // Handles: owner (when entmode is 0), reactors, extended dictionary, previous and next, layer, line type, then the entity's own pointers.
        var maxHandles = reactors + 12;
        var list = new List<long>();
        for (var i = 0; i < maxHandles && b.Position + 8 <= b.Length; i++)
        {
            var h = b.Handle(handle, out var relative);
            list.Add(h);
            if (i == 0 && mode == 0)
            {
                e.Owner = h;
            }

            if (e.Layer < 0 && layers.TryGetValue(h, out var layer) && !relative)
            {
                e.Layer = layer;
                // Previous and next entity come right before the layer handle unless the entity says its neighbours are the adjacent handles.
                e.NoLinks = noLinks;
                e.Next = !noLinks && i >= 1 ? list[i - 1] : handle + 1;
            }

            if (kind == "INSERT" && e.BlockHandle == 0 && blocks.Contains(h))
            {
                e.BlockHandle = h;
            }
        }

        _ = noLinks;
        _ = ltypeFlags;
        if (kind == "INSERT" && e.BlockHandle == 0)
        {
            return null;
        }

        return e;
    }

    private static Vector3 OcsToWcs(Vector3 p, Vector3 n)
    {
        if (n == Vector3.UnitZ || n.LengthSquared() < 1e-12f)
        {
            return p;
        }

        n = Vector3.Normalize(n);
        var nx = Math.Abs(n.X) < 1f / 64 && Math.Abs(n.Y) < 1f / 64 ? Vector3.Cross(Vector3.UnitY, n) : Vector3.Cross(Vector3.UnitZ, n);
        nx = Vector3.Normalize(nx);
        var ny = Vector3.Cross(n, nx);
        return nx * p.X + ny * p.Y + n * p.Z;
    }

    private static Vector3[] Arc(Vector3 centre, double radius, double a0, double a1, Vector3 extrusion, bool full)
    {
        if (!full && a1 <= a0)
        {
            a1 += 2 * Math.PI;
        }

        var sweep = full ? 2 * Math.PI : a1 - a0;
        var n = Math.Max(4, (int)Math.Ceiling(sweep / (Math.PI / 32)));
        if (full)
        {
            n = (n + 3) / 4 * 4;
        }

        var points = new Vector3[n + 1];
        for (var i = 0; i <= n; i++)
        {
            var a = a0 + sweep * i / n;
            var local = new Vector3((float)(centre.X + radius * Math.Cos(a)), (float)(centre.Y + radius * Math.Sin(a)), centre.Z);
            points[i] = OcsToWcs(local, extrusion);
        }

        return points;
    }

    private static Vector3[] Lwpolyline((double X, double Y)[] p, double[] bulge, double elevation, Vector3 normal, bool closed)
    {
        var list = new List<Vector3>();
        var count = p.Length;
        var segments = closed ? count : count - 1;
        for (var i = 0; i < count; i++)
        {
            list.Add(OcsToWcs(new Vector3((float)p[i].X, (float)p[i].Y, (float)elevation), normal));
            if (i < segments && Math.Abs(bulge[i]) > 1e-9)
            {
                var j = (i + 1) % count;
                var dx = p[j].X - p[i].X;
                var dy = p[j].Y - p[i].Y;
                var chord = Math.Sqrt(dx * dx + dy * dy);
                if (chord < 1e-12)
                {
                    continue;
                }

                var theta = 4 * Math.Atan(bulge[i]);
                var radius = chord / (2 * Math.Sin(theta / 2));
                var mx = (p[i].X + p[j].X) / 2;
                var my = (p[i].Y + p[j].Y) / 2;
                var h = radius * Math.Cos(theta / 2);
                // The centre lies to the left of the chord for positive bulges (counter-clockwise arcs).
                var cx = mx - dy / chord * h;
                var cy = my + dx / chord * h;
                var start = Math.Atan2(p[i].Y - cy, p[i].X - cx);
                var steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(theta) / (Math.PI / 32)));
                for (var s = 1; s < steps; s++)
                {
                    var a = start + theta * s / steps;
                    list.Add(OcsToWcs(new Vector3((float)(cx + Math.Abs(radius) * Math.Cos(a)), (float)(cy + Math.Abs(radius) * Math.Sin(a)), (float)elevation), normal));
                }
            }
        }

        if (closed && count > 1)
        {
            list.Add(list[0]);
        }

        return list.ToArray();
    }

    // ---------------------------------------------------------------------------------------------------------------

    private static void Build(Model3D model, List<Entity> entities, BlockRangeSet ranges, Dictionary<long, long> links, LoadOptions options)
    {
        var cancel = options.Cancellation;
        var materials = new Dictionary<int, int>();
        int Material(int colour)
        {
            if (colour is <= 0 or >= 256)
            {
                return -1;
            }

            if (!materials.TryGetValue(colour, out var id))
            {
                id = model.Materials.Count;
                var rgb = DxfReader.AciToRgb(colour);
                model.Materials.Add(new ModelMaterial { BaseColor = new Vector4(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1) });
                materials[colour] = id;
            }

            return id;
        }

        var members = new Dictionary<long, List<Entity>>();
        var top = new List<Entity>();
        var byId = new Dictionary<long, Entity>();
        foreach (var e in entities)
        {
            byId[e.Handle] = e;
        }

        // A block's entities are a chain from its first entity: each decoded entity names its successor (the adjacent handle when it has no links).
        var claimed = new HashSet<long>();
        foreach (var (header, range) in ranges.Blocks)
        {
            if (header == ranges.ModelHeader || header == ranges.PaperHeader || range.First == 0)
            {
                continue;
            }

            var list = new List<Entity>();
            var seen = new HashSet<long>();
            var cursor = range.First;
            for (var guard = 0; guard < 1_000_000 && cursor != 0 && seen.Add(cursor); guard++)
            {
                if (byId.TryGetValue(cursor, out var member))
                {
                    list.Add(member);
                    claimed.Add(cursor);
                    if (cursor == range.Last)
                    {
                        break;
                    }

                    cursor = member.Next;
                }
                else if (links.TryGetValue(cursor, out var next))
                {
                    claimed.Add(cursor);
                    if (cursor == range.Last)
                    {
                        break;
                    }

                    cursor = next;
                }
                else
                {
                    break;
                }
            }

            members[header] = list;
        }

        if (ranges.ModelHeader != 0 && ranges.Blocks.TryGetValue(ranges.ModelHeader, out var modelRange) && modelRange.First != 0)
        {
            top.AddRange(entities.Where(e => e.Handle >= modelRange.First && e.Handle <= modelRange.Last && !claimed.Contains(e.Handle)).OrderBy(e => e.Handle));
        }
        else
        {
            // No block table could be read: model space is what the entities say about themselves.
            top.AddRange(entities.Where(e => e.Mode == 2 && !claimed.Contains(e.Handle)));
        }

        var built = new Dictionary<long, List<SceneNode>>();
        long budget = Math.Min(options.MaxTriangles, 50_000_000);
        long tick = 0;

        SceneNode? NodeOf(Entity e, int depth)
        {
            Cancel.Every(cancel, tick++);
            if (--budget < 0)
            {
                throw new ModelFormatException("The drawing has more entities than the configured limit.");
            }

            var node = new SceneNode(e.Kind) { Layer = e.Layer };
            if (e.Kind == "INSERT")
            {
                if (depth > 6 || !members.TryGetValue(e.BlockHandle, out var list))
                {
                    return null;
                }

                if (!built.TryGetValue(e.BlockHandle, out var children))
                {
                    children = new List<SceneNode>();
                    built[e.BlockHandle] = children;
                    foreach (var m in list)
                    {
                        if (NodeOf(m, depth + 1) is { } c)
                        {
                            children.Add(c);
                        }
                    }
                }

                node.Transform = e.Transform;
                foreach (var c in children)
                {
                    node.Children.Add(Share(c));
                }

                return node.Children.Count > 0 ? node : null;
            }

            if (e.Text is { } text)
            {
                text.Layer = e.Layer;
                text.Rgb = e.Colour is > 0 and < 256 ? DxfReader.AciToRgb(e.Colour) : -1;
                model.Texts.Add(text);
                return null;
            }

            if (e.Point is { } point)
            {
                var m = new MeshData { Name = e.Kind, Positions = [point.X, point.Y, point.Z] };
                model.Meshes.Add(m);
                node.Mesh = m;
                return node;
            }

            if (e.Faces.Count > 0)
            {
                var positions = new List<float>();
                var indices = new List<uint>();
                foreach (var f in e.Faces)
                {
                    var first = (uint)(positions.Count / 3);
                    foreach (var c in f)
                    {
                        positions.Add(c.X);
                        positions.Add(c.Y);
                        positions.Add(c.Z);
                    }

                    indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
                }

                var m = new MeshData { Name = e.Kind, Positions = positions.ToArray(), Indices = indices.ToArray() };
                if (Material(e.Colour) is var mat && mat >= 0)
                {
                    m.Groups.Add(new MeshGroup(0, m.Indices.Length, mat));
                }

                model.Meshes.Add(m);
                node.Mesh = m;
                return node;
            }

            if (e.Polylines.Count > 0)
            {
                var positions = new List<float>();
                var segments = new List<uint>();
                foreach (var line in e.Polylines)
                {
                    var first = (uint)(positions.Count / 3);
                    foreach (var c in line)
                    {
                        positions.Add(c.X);
                        positions.Add(c.Y);
                        positions.Add(c.Z);
                    }

                    for (var i = 0; i + 1 < line.Length; i++)
                    {
                        segments.Add(first + (uint)i);
                        segments.Add(first + (uint)i + 1);
                    }
                }

                var m = new MeshData { Name = e.Kind, Positions = positions.ToArray(), EdgeIndices = segments.ToArray(), EdgeMaterial = Material(e.Colour) };
                model.Meshes.Add(m);
                node.Mesh = m;
                return node;
            }

            return null;
        }

        static SceneNode Share(SceneNode source)
        {
            var copy = new SceneNode(source.Name) { Transform = source.Transform, Mesh = source.Mesh, Layer = source.Layer };
            foreach (var child in source.Children)
            {
                copy.Children.Add(Share(child));
            }

            return copy;
        }

        foreach (var e in top)
        {
            if (NodeOf(e, 0) is { } node)
            {
                model.Roots.Add(node);
            }
        }
    }
}
