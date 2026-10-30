using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.Cad;

/// <summary>
/// The objects of a DWG database (AC1015 and the AC1018 family), decoded from the object map into the tables and entities a DXF file would
/// hold, and written out as DXF text. The drawing is then built by the DXF reader, which already knows hatches, dimensions, text, splines and
/// colours; this class only has to read the bit streams. Everything a release changes is decided here by <see cref="_generation"/>: the object
/// type code (AC1024), the handle stream (AC1024), the string stream (AC1021), the entity header (colour flags, "xdic missing" bit).
/// </summary>
internal sealed partial class DwgDatabase
{
    private readonly DwgSections _sections;
    private readonly int _generation;
    private readonly System.Text.Encoding _textEncoding;
    private readonly LoadOptions _options;
    private readonly CancellationToken _cancel;

    private sealed class Record
    {
        public long Handle;
        public long Offset;
        public int Type;
        public string Name = "";
    }

    private sealed class LayerInfo
    {
        public string Name = "0";
        public int Color = 7;
        public int Rgb = -1;
        public bool Frozen;
        public bool Off;
        public bool Locked;
    }

    private sealed class BlockInfo
    {
        public long Handle;
        public string Name = "";
        public (double X, double Y, double Z) Base;
        public bool Anonymous;
        public bool Xref;
    }

    private sealed class EntityChunk
    {
        public long Handle;
        public long Owner;
        public int Mode;
        public string Kind = "";
        public string Text = "";
        public string Trailer = "";
    }

    private readonly List<Record> _records = new();
    private readonly Dictionary<int, string> _classes = new();
    private readonly Dictionary<long, LayerInfo> _layers = new();
    private readonly Dictionary<long, BlockInfo> _blocks = new();
    private readonly List<EntityChunk> _entities = new();
    private readonly Dictionary<string, int> _skipped = new(StringComparer.Ordinal);

    public DwgDatabase(DwgSections sections, LoadOptions options)
    {
        _sections = sections;
        _generation = sections.Generation;
        _textEncoding = DwgCodePages.For(sections.CodePage);
        _options = options;
        _cancel = options.Cancellation;
    }

    /// <summary>Object kinds that were seen but not decoded, with their counts.</summary>
    public IReadOnlyDictionary<string, int> Skipped => _skipped;

    public int EntityCount => _entities.Count;

    public int ObjectCount => _records.Count;

    /// <summary>The layer names, in object order, without decoding any entity.</summary>
    public List<string> ReadLayerNames()
    {
        ReadHandleMap();
        var names = new List<string>();
        var tick = 0L;
        foreach (var record in _records)
        {
            Cancel.Every(_cancel, tick++);
            try
            {
                if (Open(record, out var bits) && record.Type == 51)
                {
                    ReadLayer(record, bits);
                    names.Add(_layers[record.Handle].Name);
                }
            }
            catch (ModelFormatException)
            {
            }
        }

        return names;
    }

    public int? InsertUnits { get; private set; }

    public string Version => _generation switch { 15 => "AC1015", 18 => "AC1018", 21 => "AC1021", 24 => "AC1024", 27 => "AC1027", _ => "AC1032" };

    // ---- object access --------------------------------------------------------------------------------------------

    private bool Open(Record record, out DwgBits bits)
    {
        bits = null!;
        var data = _sections.Objects;
        var p = record.Offset;
        if (p < 0 || p + 2 > data.Length)
        {
            return false;
        }

        // Modular short: 15 bit words, little endian, continued while the top bit is set.
        long size = 0;
        for (var shift = 0; shift < 30; shift += 15)
        {
            if (p + 2 > data.Length)
            {
                return false;
            }

            var word = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)p));
            p += 2;
            size |= (long)(word & 0x7FFF) << shift;
            if ((word & 0x8000) == 0)
            {
                break;
            }
        }

        long handleBits = 0;
        if (_generation >= 24)
        {
            // Modular char: the size of the handle stream in bits.
            var shift = 0;
            while (true)
            {
                if (p >= data.Length || shift > 28)
                {
                    return false;
                }

                var b = data[p++];
                handleBits |= (long)(b & 0x7F) << shift;
                shift += 7;
                if ((b & 0x80) == 0)
                {
                    break;
                }
            }
        }

        if (size <= 0 || p + size > data.Length + 1)
        {
            return false;
        }

        var start = p * 8;
        bits = new DwgBits(data, start, Math.Min(data.Length * 8L, (p + size) * 8)) { TextEncoding = _textEncoding };
        record.Type = bits.ObjectType(_generation);
        if (_generation < 24)
        {
            bits.HandleStart = start + bits.RL();
        }
        else
        {
            bits.HandleStart = start + size * 8 - handleBits;
        }

        if (_generation >= 21)
        {
            bits.OpenStrings(bits.HandleStart);
        }

        return !bits.Overrun;
    }

    private static string KindOf(int type) => type switch
    {
        1 => "TEXT", 2 => "ATTRIB", 3 => "ATTDEF", 4 => "BLOCK", 5 => "ENDBLK", 6 => "SEQEND", 7 => "INSERT", 8 => "MINSERT",
        10 => "VERTEX", 11 => "VERTEX3D", 12 => "VERTEXMESH", 13 => "VERTEXPFACE", 14 => "VERTEXPFACEFACE",
        15 => "POLYLINE", 16 => "POLYLINE3D", 17 => "ARC", 18 => "CIRCLE", 19 => "LINE",
        20 or 21 or 22 or 23 or 24 or 25 or 26 => "DIMENSION",
        27 => "POINT", 28 => "3DFACE", 29 => "POLYLINEPFACE", 30 => "POLYLINEMESH", 31 => "SOLID", 32 => "TRACE", 34 => "VIEWPORT", 35 => "ELLIPSE",
        36 => "SPLINE", 37 => "REGION", 38 => "3DSOLID", 39 => "BODY", 40 => "RAY", 41 => "XLINE", 44 => "MTEXT", 45 => "LEADER", 47 => "MLINE",
        48 => "BLOCKCONTROL", 49 => "BLOCKHEADER", 50 => "LAYERCONTROL", 51 => "LAYER", 77 => "LWPOLYLINE", 78 => "HATCH",
        _ => "",
    };

    private string KindOf(Record r) => r.Type >= 500 && _classes.TryGetValue(r.Type, out var name) ? name : KindOf(r.Type);

    // ---- loading --------------------------------------------------------------------------------------------------

    /// <summary>
    /// $INSUNITS from the header variables, decoded in order (<see cref="DwgHeaderVariables"/>); when the header does not decode it is found by its
    /// neighbours instead of by counting the ~250 before it: it is a bit short followed by $CEPSNTYPE (0 unless a plot style is attached), then, up to
    /// AC1018, the 38 character fingerprint GUID, from AC1021 on the five byte settings of $SORTENTS to $DIMASSOC. The first such place wins (one with the default $SORTENTS of 127 before one without).
    /// </summary>
    private void ReadHeaderUnits()
    {
        var h = _sections.Header;
        if (h.Length < 40)
        {
            return;
        }

        // The header variables are decoded in order, release by release (DwgHeaderVariables); the neighbour search below is the fallback for a header that does not decode.
        if (DwgHeaderVariables.Read(h, _generation) is { } variables && variables.Numbers.TryGetValue("InsUnits", out var decoded) && decoded is >= 0 and <= 20)
        {
            InsertUnits = decoded > 0 ? (int)decoded : null;
            return;
        }

        if (_generation < 18)
        {
            return;
        }

        var start = _generation >= 24 ? 28 : 20;
        var end = h.Length * 8L;
        int? found = null;
        int? fallback = null;
        for (var p = start * 8L; p < end - 64; p++)
        {
            var b = new DwgBits(h, p, end);
            var units = b.BS();
            if (units is < 1 or > 20 || b.BS() != 0)
            {
                continue;
            }

            bool match;
            var preferred = true;
            if (_generation >= 21)
            {
                preferred = b.RC() == 127;
                match = b.RC() <= 3 && b.RC() <= 1 && b.RC() <= 2 && b.RC() <= 2;
            }
            else
            {
                var length = b.BS();
                match = length is >= 38 and <= 40 && b.RC() == '{';
            }

            if (match && !b.Overrun)
            {
                fallback ??= units;
                if (preferred)
                {
                    found = units;
                    break;
                }
            }
        }

        found ??= fallback;
        InsertUnits = found;
    }

    public void Load()
    {
        ReadHeaderUnits();
        ReadClasses();
        ReadHandleMap();
        var tick = 0L;

        // Pass 1: the type of every object, and the tables that entities refer to (layers, block headers).
        foreach (var record in _records)
        {
            Cancel.Every(_cancel, tick++);
            if (!Open(record, out var bits))
            {
                record.Type = -1;
                continue;
            }

            try
            {
                switch (record.Type)
                {
                    case 51:
                        ReadLayer(record, bits);
                        break;
                    case 49:
                        ReadBlockHeader(record, bits);
                        break;
                }
            }
            catch (ModelFormatException)
            {
            }
        }

        foreach (var record in _records)
        {
            Cancel.Every(_cancel, tick++);
            if (record.Type < 0)
            {
                continue;
            }

            var kind = KindOf(record);
            if (kind.Length == 0 || !IsEntity(record.Type, kind))
            {
                continue;
            }

            try
            {
                if (Open(record, out var bits) && DecodeEntity(record, kind, bits) is { } chunk)
                {
                    _entities.Add(chunk);
                }
                else if (!Decoded(kind))
                {
                    _skipped[kind] = _skipped.GetValueOrDefault(kind) + 1;
                }
            }
            catch (ModelFormatException)
            {
                _skipped["damaged " + kind] = _skipped.GetValueOrDefault("damaged " + kind) + 1;
            }
        }
    }

    private static bool IsEntity(int type, string kind) => type is >= 1 and <= 47 and not (4 or 5) || type is 77 or 78 || (type >= 500 && kind is "HATCH" or "MTEXT" or "SPLINE" or "ELLIPSE" or "IMAGE" or "WIPEOUT" or "LEADER" or "MLINE" or "OLE2FRAME" or "MESH");

    private static bool Decoded(string kind) => kind is "LINE" or "CIRCLE" or "ARC" or "POINT" or "TEXT" or "ATTRIB" or "ATTDEF" or "INSERT" or "MINSERT" or "LWPOLYLINE" or "3DFACE" or "SOLID" or "TRACE"
        or "VERTEX" or "VERTEX3D" or "VERTEXPFACE" or "VERTEXPFACEFACE" or "VERTEXMESH" or "POLYLINE" or "POLYLINE3D" or "POLYLINEPFACE" or "POLYLINEMESH" or "ELLIPSE" or "SPLINE" or "MTEXT" or "HATCH" or "DIMENSION" or "RAY" or "XLINE" or "SEQEND";

    private void ReadClasses()
    {
        var c = _sections.Classes;
        if (c.Length < 28)
        {
            return;
        }

        var size = BinaryPrimitives.ReadUInt32LittleEndian(c.AsSpan(16));
        var start = 20;
        long bitSize = 0;
        if (_generation >= 24)
        {
            start = 28;
            bitSize = BinaryPrimitives.ReadUInt32LittleEndian(c.AsSpan(24));
        }

        if (size == 0 || start + size > c.Length + 2)
        {
            return;
        }

        var end = start * 8L + size * 8;
        var main = new DwgBits(c, start * 8L, end) { TextEncoding = _textEncoding };
        if (_generation >= 21)
        {
            // The bit size counts from the size field itself (byte 24); the string stream ends there.
            var strEnd = 24 * 8L + bitSize;
            main = new DwgBits(c, start * 8L, strEnd) { TextEncoding = _textEncoding };
            main.OpenStrings(strEnd);
        }

        if (_generation >= 18)
        {
            main.BL();
            main.Bit();
        }

        try
        {
            for (var i = 0; i < 10_000 && !main.Overrun; i++)
            {
                var number = main.BS();
                main.BS();
                if (_generation < 21)
                {
                    main.TV();
                    main.TV();
                }
                else
                {
                    main.Strings?.TU();
                    main.Strings?.TU();
                }

                var dxf = main.TV();
                main.Bit();
                main.BS();
                if (_generation >= 18)
                {
                    main.BL();
                    main.BS();
                    main.BS();
                    main.BL();
                    main.BL();
                }

                if (main.Overrun || number < 500 || dxf.Length == 0)
                {
                    break;
                }

                _classes[number] = dxf;
            }
        }
        catch (ModelFormatException)
        {
        }
    }

    private void ReadHandleMap()
    {
        var s = _sections.Handles;
        long handle = 0;
        long location = 0;
        var p = 0;
        var tick = 0L;
        while (p + 2 <= s.Length)
        {
            var block = (s[p] << 8) | s[p + 1];
            if (block <= 2)
            {
                break;
            }

            var blockEnd = Math.Min(p + block, s.Length);
            var q = p + 2;
            while (q < blockEnd)
            {
                Cancel.Every(_cancel, tick++);
                handle += ModularChar(s, ref q, blockEnd, false);
                location += ModularChar(s, ref q, blockEnd, true);
                if (location >= 0 && location < _sections.Objects.Length)
                {
                    _records.Add(new Record { Handle = handle, Offset = location });
                }

                if (_records.Count > 5_000_000)
                {
                    throw new ModelFormatException("The DWG object map is implausibly large.");
                }
            }

            p += block + 2;
        }
    }

    private static long ModularChar(byte[] s, ref int p, int end, bool signed)
    {
        long value = 0;
        var shift = 0;
        while (true)
        {
            if (p >= end || shift > 56)
            {
                throw new ModelFormatException("A DWG modular character is truncated.");
            }

            var b = s[p++];
            if ((b & 0x80) != 0)
            {
                value |= (long)(b & 0x7F) << shift;
                shift += 7;
                continue;
            }

            if (signed)
            {
                value |= (long)(b & 0x3F) << shift;
                return (b & 0x40) != 0 ? -value : value;
            }

            return value | ((long)b << shift);
        }
    }

    // ---- common headers -------------------------------------------------------------------------------------------

    /// <summary>Handle, extended data and (for non entities) the reactor count and flags.</summary>
    private long ReadObjectPrefix(DwgBits b)
    {
        var handle = b.Handle(0);
        for (var guard = 0; guard < 4096; guard++)
        {
            var eed = b.BS();
            if (eed == 0)
            {
                break;
            }

            b.Handle(handle);
            b.Position += eed * 8L;
            if (b.Overrun)
            {
                throw new ModelFormatException("Extended data overruns its object.");
            }
        }

        return handle;
    }

    private void ReadLayer(Record record, DwgBits b)
    {
        ReadObjectPrefix(b);
        b.BL();
        if (_generation >= 18)
        {
            b.Bit();
        }

        if (_generation >= 27)
        {
            b.Bit();
        }

        var layer = new LayerInfo { Name = b.TV() };
        if (_generation < 24)
        {
            b.Bit();
            b.BS();
            b.Bit();
        }

        var flags = b.BS();
        layer.Frozen = (flags & 1) != 0 || (flags & 2) != 0;
        layer.Locked = (flags & 8) != 0;
        var color = b.BS();
        if (_generation >= 18)
        {
            var rgb = b.BL();
            var type = b.RC();
            if ((type & 1) != 0)
            {
                b.TV();
            }

            if ((type & 2) != 0)
            {
                b.TV();
            }

            if (((uint)rgb >> 24) == 0xC2)
            {
                layer.Rgb = rgb & 0xFFFFFF;
            }
        }

        if (b.Overrun)
        {
            throw new ModelFormatException("A layer overruns its data.");
        }

        layer.Off = color < 0;
        layer.Color = Math.Abs(color);
        _layers[record.Handle] = layer;
    }

    private void ReadBlockHeader(Record record, DwgBits b)
    {
        ReadObjectPrefix(b);
        b.BL();
        if (_generation >= 18)
        {
            b.Bit();
        }

        if (_generation >= 27)
        {
            b.Bit();
        }

        var block = new BlockInfo { Handle = record.Handle, Name = b.TV() };
        if (_generation < 21)
        {
            b.Bit();
        }

        b.BS();
        if (_generation < 21)
        {
            b.Bit();
        }

        block.Anonymous = b.Bit();
        b.Bit(); // has attributes
        block.Xref = b.Bit();
        b.Bit(); // xref overlaid
        b.Bit(); // loaded
        if (_generation >= 18)
        {
            b.BL();
        }

        block.Base = b.BD3();
        if (b.Overrun)
        {
            throw new ModelFormatException("A block header overruns its data.");
        }

        _blocks[record.Handle] = block;
    }

    private sealed class Header
    {
        public long Handle;
        public int Mode;
        public int Reactors;
        public bool NoLinks;
        public int Color = 256;
        public int Rgb = -1;
        public int LineTypeFlags;
        public int Lineweight;
        public int Invisible;
        public bool Graphic;
    }

    private Header ReadEntityHeader(DwgBits b)
    {
        var h = new Header { Handle = ReadObjectPrefix(b) };
        h.Graphic = b.Bit();
        if (h.Graphic)
        {
            var bytes = _generation >= 24 ? b.BLL() : b.RL();
            b.Position += bytes * 8;
        }

        h.Mode = (int)b.Read(2);
        h.Reactors = b.BL();
        if (_generation >= 18)
        {
            b.Bit();
        }

        if (_generation >= 27)
        {
            b.Bit();
        }

        if (_generation == 15)
        {
            h.NoLinks = b.Bit();
            h.Color = b.BS();
        }
        else
        {
            var (index, rgb, _) = b.EntityColor();
            h.Color = index;
            h.Rgb = rgb;
        }

        b.BD(); // line type scale
        h.LineTypeFlags = (int)b.Read(2);
        b.Read(2); // plot style flags
        if (_generation >= 21)
        {
            b.Read(2);
            b.RC();
        }

        if (_generation >= 24)
        {
            b.Read(3);
        }

        h.Invisible = b.BS();
        h.Lineweight = b.RC();
        if (h.Reactors is < 0 or > 10_000 || b.Overrun)
        {
            throw new ModelFormatException("An entity header is damaged.");
        }

        return h;
    }

    /// <summary>The handles of the handle stream, in order, resolved against the entity's own handle.</summary>
    private List<long> ReadHandles(DwgBits b, Header h, int maximum)
    {
        var list = new List<long>();
        b.Position = b.HandleStart;
        var limit = Math.Min(maximum + h.Reactors, 4096);
        while (list.Count < limit && b.Position + 8 <= b.End)
        {
            list.Add(b.Handle(h.Handle));
        }

        return list;
    }

    private long LayerOf(List<long> handles, out int index)
    {
        for (var i = 0; i < handles.Count; i++)
        {
            if (_layers.ContainsKey(handles[i]))
            {
                index = i;
                return handles[i];
            }
        }

        index = -1;
        return 0;
    }

    private long BlockHandleAfter(List<long> handles, int from, bool anonymousOnly = false)
    {
        for (var i = Math.Max(0, from); i < handles.Count; i++)
        {
            if (_blocks.TryGetValue(handles[i], out var block) && (!anonymousOnly || block.Anonymous))
            {
                return handles[i];
            }
        }

        return 0;
    }

    // ---- DXF text -------------------------------------------------------------------------------------------------

    private sealed class Dxf
    {
        private readonly StringBuilder _sb = new();

        public static string TypeName(string kind) => kind switch
        {
            "POLYLINE3D" or "POLYLINEPFACE" or "POLYLINEMESH" => "POLYLINE",
            "VERTEX3D" or "VERTEXMESH" or "VERTEXPFACE" or "VERTEXPFACEFACE" => "VERTEX",
            "MINSERT" => "INSERT",
            _ => kind,
        };

        public Dxf G(int code, string value)
        {
            _sb.Append(code.ToString(CultureInfo.InvariantCulture)).Append('\n').Append(value.Replace('\n', ' ').Replace('\r', ' ')).Append('\n');
            return this;
        }

        public Dxf G(int code, double value) => G(code, value.ToString("R", CultureInfo.InvariantCulture));

        public Dxf G(int code, int value) => G(code, value.ToString(CultureInfo.InvariantCulture));

        public Dxf P(int code, (double X, double Y, double Z) p) => G(code, p.X).G(code + 10, p.Y).G(code + 20, p.Z);

        public Dxf P2(int code, double x, double y) => G(code, x).G(code + 10, y);

        public override string ToString() => _sb.ToString();
    }

    private Dxf Begin(string type, Header h, long layerHandle)
    {
        var d = new Dxf().G(0, type).G(5, h.Handle.ToString("X", CultureInfo.InvariantCulture));
        d.G(8, _layers.TryGetValue(layerHandle, out var layer) ? layer.Name : "0");
        if (h.Color != 256)
        {
            d.G(62, h.Color);
        }

        if (h.Rgb >= 0)
        {
            d.G(420, h.Rgb);
        }

        return d;
    }

    private static string Hex(long handle) => handle.ToString("X", CultureInfo.InvariantCulture);

    /// <summary>Writes the drawing as DXF text: header, layer table, blocks, model space.</summary>
    public string ToDxf()
    {
        var sb = new StringBuilder();
        void G(int code, string value) => sb.Append(code.ToString(CultureInfo.InvariantCulture)).Append('\n').Append(value).Append('\n');

        G(0, "SECTION");
        G(2, "HEADER");
        G(9, "$ACADVER");
        G(1, Version);
        if (InsertUnits is { } units)
        {
            G(9, "$INSUNITS");
            G(70, units.ToString(CultureInfo.InvariantCulture));
        }

        G(0, "ENDSEC");
        G(0, "SECTION");
        G(2, "TABLES");
        G(0, "TABLE");
        G(2, "LAYER");
        G(70, _layers.Count.ToString(CultureInfo.InvariantCulture));
        foreach (var layer in _layers.Values)
        {
            G(0, "LAYER");
            G(2, layer.Name);
            G(70, ((layer.Frozen ? 1 : 0) | (layer.Locked ? 4 : 0)).ToString(CultureInfo.InvariantCulture));
            G(62, (layer.Off ? -layer.Color : layer.Color).ToString(CultureInfo.InvariantCulture));
            if (layer.Rgb >= 0)
            {
                G(420, layer.Rgb.ToString(CultureInfo.InvariantCulture));
            }

            G(6, "CONTINUOUS");
        }

        G(0, "ENDTAB");
        G(0, "ENDSEC");

        var children = new Dictionary<long, List<EntityChunk>>();
        var byBlock = new Dictionary<long, List<EntityChunk>>();
        var model = new List<EntityChunk>();
        foreach (var e in _entities.OrderBy(e => e.Handle))
        {
            if (e.Mode == 2 && !IsChild(e.Kind))
            {
                model.Add(e);
            }
            else if (e.Mode == 0 && (e.Kind is "VERTEX" or "VERTEX3D" or "VERTEXMESH" or "VERTEXPFACE" or "VERTEXPFACEFACE" or "ATTRIB" or "SEQEND" || !_blocks.ContainsKey(e.Owner)))
            {
                if (!children.TryGetValue(e.Owner, out var list))
                {
                    children[e.Owner] = list = new List<EntityChunk>();
                }

                list.Add(e);
            }
            else if (e.Mode == 0)
            {
                if (!byBlock.TryGetValue(e.Owner, out var list))
                {
                    byBlock[e.Owner] = list = new List<EntityChunk>();
                }

                list.Add(e);
            }
        }

        void Write(EntityChunk e, int depth)
        {
            sb.Append(e.Text);
            if (depth < 4 && children.TryGetValue(e.Handle, out var kids))
            {
                foreach (var kid in kids)
                {
                    Write(kid, depth + 1);
                }
            }

            sb.Append(e.Trailer);
        }

        G(0, "SECTION");
        G(2, "BLOCKS");
        foreach (var block in _blocks.Values.OrderBy(b => b.Handle))
        {
            if (block.Xref || block.Name.StartsWith("*Model_Space", StringComparison.OrdinalIgnoreCase) || block.Name.StartsWith("*Paper_Space", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            G(0, "BLOCK");
            G(8, "0");
            G(2, block.Name);
            G(70, block.Anonymous ? "1" : "0");
            G(10, block.Base.X.ToString("R", CultureInfo.InvariantCulture));
            G(20, block.Base.Y.ToString("R", CultureInfo.InvariantCulture));
            G(30, block.Base.Z.ToString("R", CultureInfo.InvariantCulture));
            G(3, block.Name);
            if (byBlock.TryGetValue(block.Handle, out var members))
            {
                foreach (var m in members)
                {
                    Write(m, 0);
                }
            }

            G(0, "ENDBLK");
            G(8, "0");
        }

        G(0, "ENDSEC");
        G(0, "SECTION");
        G(2, "ENTITIES");
        foreach (var e in model)
        {
            Write(e, 0);
        }

        G(0, "ENDSEC");
        G(0, "EOF");
        return sb.ToString();
    }

    private static bool IsChild(string kind) => kind is "VERTEX" or "VERTEX3D" or "VERTEXMESH" or "VERTEXPFACE" or "VERTEXPFACEFACE" or "ATTRIB" or "SEQEND";
}
