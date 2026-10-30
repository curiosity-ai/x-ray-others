using System.Globalization;
using System.Numerics;
using System.Text;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.Rhino;

/// <summary>
/// Rhino 3DM (openNURBS archives, versions with 64-bit chunk lengths, 50 and later). Reads layers (with parents, visibility, colours),
/// materials, object attributes (names, colours, user strings), block definitions and instances (shared meshes under transforms),
/// points, point clouds, line, polyline, arc and NURBS curves (sampled to line segments) and meshes. Not read: B-reps (including their
/// render meshes), surfaces, extrusions, poly-curves, SubD, text and annotations; each is counted in a warning.
/// </summary>
internal static class RhinoReader
{
    private const string Magic = "3D Geometry File Format ";

    private sealed class Layer
    {
        public string Name = "";
        public int Index;
        public bool Visible = true;
        public bool Locked;
        public int MaterialIndex = -1;
        public uint Color;
        public Guid Id;
        public Guid ParentId;
        public string Path = "";
    }

    private sealed class Material
    {
        public string? Name;
        public Guid Id;
        public Vector4 Diffuse = new(0.8f, 0.8f, 0.8f, 1);
        public float Shine;
    }

    private sealed class Obj
    {
        public int Type;
        public Guid Class;
        public int DataStart;
        public int DataEnd;
        public int UserStart = -1;
        public int UserEnd = -1;
        public int RecordStart;
        public int RecordEnd;
        public Guid Id;
        public string Name = "";
        public int Layer;
        public int MaterialIndex = -1;
        public uint Color;
        public int ColorSource;
        public int MaterialSource;
        public bool Visible = true;
        public int Mode;
        public readonly List<(string Key, string Value)> UserStrings = new();
    }

    private sealed class Block
    {
        public Guid Id;
        public string Name = "";
        public string Description = "";
        public List<Guid> Members = new();
        public List<SceneNode>? Built;
        public bool Building;
        public int DataStart;
        public int DataEnd;
    }

    public static bool Sniff(ReadOnlySpan<byte> head) => head.Length >= 32 && Encoding.ASCII.GetString(head[..24]) == Magic;

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var cancel = options.Cancellation;
        if (!Sniff(data.Span))
        {
            throw new ModelFormatException("This is not a 3dm archive.");
        }

        var versionText = Encoding.ASCII.GetString(data.Span.Slice(24, 8)).Trim();
        if (!int.TryParse(versionText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) || version < 1 || version > 1000)
        {
            throw new ModelFormatException("The 3dm file version is unreadable.");
        }

        var model = new Model3D();
        var meta = model.Metadata;
        meta.Format = ModelFormats.Rhino;
        meta.DocumentKind = "model";
        meta.UpAxis = UpAxis.Z;
        meta.FormatVersion = "3dm " + version.ToString(CultureInfo.InvariantCulture) + " (Rhino " + RhinoRelease(version) + ")";
        meta.Application = "Rhinoceros";

        if (version < 50)
        {
            throw new UnsupportedModelFormatException(ModelFormats.Rhino, $"Rhino archive version {version} (Rhino {RhinoRelease(version)}) uses 32-bit chunk lengths and older record layouts; versions 50 and later are read.");
        }

        var top = new RhinoReaderCursor(data, 32, data.Length, wideLengths: true);
        var layers = new List<Layer>();
        var materials = new List<Material>();
        var objects = new List<Obj>();
        var blocks = new Dictionary<Guid, Block>();
        long tick = 0;
        var ended = false;
        while (top.Remaining >= 12)
        {
            Cancel.Every(cancel, tick++);
            var chunk = top.Chunk();
            if (chunk.Type == 0x7fff)
            {
                ended = true;
                break;
            }

            var inner = top.Sub(chunk);
            switch (chunk.Type)
            {
                case 0x10000014:
                    ReadProperties(inner, model, cancel);
                    break;
                case 0x10000015:
                    ReadSettings(inner, model, cancel);
                    break;
                case 0x10000010:
                    ReadTable(inner, 0x20008040, cancel, (c, cur) => materials.Add(ReadMaterial(cur, c, data)));
                    break;
                case 0x10000011:
                    ReadTable(inner, 0x20008050, cancel, (c, cur) => layers.Add(ReadLayer(cur, c, data, layers.Count)));
                    break;
                case 0x10000013:
                    ReadTable(inner, 0x20008070, cancel, (c, cur) => objects.Add(ReadObject(cur, c, data, cancel)));
                    break;
                case 0x10000021:
                    ReadTable(inner, 0x20008076, cancel, (c, cur) =>
                    {
                        if (ReadBlock(cur, c, data) is { } block)
                        {
                            blocks[block.Id] = block;
                        }
                    });
                    break;
            }
        }

        if (!ended)
        {
            throw new ModelFormatException("The 3dm file has no end marker; it is truncated.");
        }

        foreach (var block in blocks.Values)
        {
            ResolveMembers(block, data, objects, cancel);
        }

        return Build(model, data, options, layers, materials, objects, blocks);
    }

    private static string RhinoRelease(int version) => version switch
    {
        >= 80 => "8",
        >= 70 => "7",
        >= 60 => "6",
        >= 50 => "5",
        >= 40 => "4",
        >= 30 => "3",
        _ => "2",
    };

    private static void ReadTable(RhinoReaderCursor table, uint recordType, CancellationToken cancel, Action<RhinoChunk, RhinoReaderCursor> record)
    {
        long tick = 0;
        while (table.Remaining >= 12)
        {
            Cancel.Every(cancel, tick++);
            var c = table.Chunk();
            if (c.IsShort)
            {
                if (c.Type == 0xffffffff)
                {
                    break;
                }

                continue;
            }

            if (c.Type == recordType)
            {
                record(c, table.Sub(c));
            }
        }
    }

    // ------------------------------------------------------------------ properties and settings

    private static void ReadProperties(RhinoReaderCursor table, Model3D model, CancellationToken cancel)
    {
        var meta = model.Metadata;
        long tick = 0;
        while (table.Remaining >= 12)
        {
            Cancel.Every(cancel, tick++);
            var c = table.Chunk();
            if (c.IsShort)
            {
                continue;
            }

            var r = table.Sub(c);
            try
            {
                switch (c.Type)
                {
                    case 0x20008021: // revision history
                        r.Byte();
                        meta.Author = NullIfEmpty(r.String());
                        meta.Created = ReadTime(r);
                        meta.LastModifiedBy = NullIfEmpty(r.String());
                        meta.Modified = ReadTime(r);
                        var revision = r.Int();
                        if (revision > 0)
                        {
                            meta.Properties.Add(new ModelProperty("Revision", revision.ToString(CultureInfo.InvariantCulture)));
                        }

                        break;
                    case 0x20008024: // application
                        r.Byte();
                        var app = r.String();
                        var url = r.String();
                        var details = r.String();
                        if (app.Length > 0)
                        {
                            meta.Application = app;
                        }

                        if (url.Length > 0)
                        {
                            meta.Properties.Add(new ModelProperty("Application URL", url));
                        }

                        if (details.Length > 0)
                        {
                            meta.Properties.Add(new ModelProperty("Application details", details));
                        }

                        break;
                    case 0x20008022: // notes: version, visible, html, window rectangle, text
                        r.Byte();
                        r.Skip(6 * 4);
                        var notes = r.String();
                        if (notes.Length > 0)
                        {
                            meta.Notes.Add(notes);
                        }

                        break;
                }
            }
            catch (ModelFormatException)
            {
                // A property block that does not parse is left out; the model itself is read from other tables.
            }
        }
    }

    private static string? NullIfEmpty(string s) => s.Length == 0 ? null : s;

    private static DateTimeOffset? ReadTime(RhinoReaderCursor r)
    {
        int sec = r.Int(), min = r.Int(), hour = r.Int(), day = r.Int(), month = r.Int(), year = r.Int();
        r.Skip(8);
        try
        {
            return new DateTimeOffset(year + 1900, month + 1, day, hour, min, sec, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static void ReadSettings(RhinoReaderCursor table, Model3D model, CancellationToken cancel)
    {
        long tick = 0;
        while (table.Remaining >= 12)
        {
            Cancel.Every(cancel, tick++);
            var c = table.Chunk();
            if (c.IsShort || c.Type != 0x20008031)
            {
                continue;
            }

            var r = table.Sub(c);
            try
            {
                r.Skip(4);
                var unit = r.Int();
                var absolute = r.Double();
                var angle = r.Double();
                var relative = r.Double();
                r.Skip(8);
                var scale = r.Double();
                model.Metadata.Units = unit switch
                {
                    1 => LengthUnit.Micrometer,
                    2 => LengthUnit.Millimeter,
                    3 => LengthUnit.Centimeter,
                    4 => LengthUnit.Meter,
                    5 => LengthUnit.Kilometer,
                    6 => LengthUnit.Microinch,
                    7 => LengthUnit.Mil,
                    8 => LengthUnit.Inch,
                    9 => LengthUnit.Foot,
                    10 => LengthUnit.Yard,
                    11 => LengthUnit.Mile,
                    13 => LengthUnit.Nanometer,
                    14 => LengthUnit.Decimeter,
                    _ => null,
                };
                if (double.IsFinite(absolute))
                {
                    model.Metadata.Properties.Add(new ModelProperty("Absolute tolerance", absolute.ToString("R", CultureInfo.InvariantCulture)));
                }

                if (double.IsFinite(angle))
                {
                    model.Metadata.Properties.Add(new ModelProperty("Angle tolerance (degrees)", (angle * 180 / Math.PI).ToString("0.######", CultureInfo.InvariantCulture)));
                }

                if (double.IsFinite(relative))
                {
                    model.Metadata.Properties.Add(new ModelProperty("Relative tolerance", relative.ToString("R", CultureInfo.InvariantCulture)));
                }

                if (model.Metadata.Units is null && double.IsFinite(scale) && scale > 0)
                {
                    model.Metadata.Properties.Add(new ModelProperty("Custom unit (metres)", scale.ToString("R", CultureInfo.InvariantCulture)));
                }
            }
            catch (ModelFormatException)
            {
            }
        }
    }

    // ------------------------------------------------------------------ class chunks

    /// <summary>Finds the class id and class data of an ON_Object written with WriteObject: 0x27ffa holding 0x2fffb (uuid) and 0x2fffc (data).</summary>
    private static bool ReadClass(RhinoReaderCursor record, out Guid id, out int dataStart, out int dataEnd)
    {
        id = default;
        dataStart = dataEnd = 0;
        var found = 0;
        while (record.Remaining >= 12)
        {
            var c = record.Chunk();
            if (c.IsShort)
            {
                if (c.Type == 0x80027fff)
                {
                    break;
                }

                continue;
            }

            if (c.Type == 0x27ffa)
            {
                var inner = record.Sub(c);
                while (inner.Remaining >= 12)
                {
                    var k = inner.Chunk();
                    if (k.IsShort)
                    {
                        continue;
                    }

                    if (k.Type == 0x2fffb && k.Length >= 16)
                    {
                        id = new Guid(record.Span.Slice(k.DataStart, 16));
                        found |= 1;
                    }
                    else if (k.Type == 0x2fffc)
                    {
                        dataStart = k.DataStart;
                        dataEnd = k.DataEnd;
                        found |= 2;
                    }
                }

                return found == 3;
            }
        }

        return found == 3;
    }

    private static Layer ReadLayer(RhinoReaderCursor record, RhinoChunk chunk, ReadOnlyMemory<byte> data, int ordinal)
    {
        var layer = new Layer { Index = ordinal, Name = "Layer " + (ordinal + 1).ToString(CultureInfo.InvariantCulture) };
        if (!ReadClass(record, out _, out var start, out var end))
        {
            return layer;
        }

        var r = new RhinoReaderCursor(data, start, end, true);
        try
        {
            r.Byte();
            r.Int(); // legacy mode
            layer.Index = r.Int();
            r.Int(); // IGES level
            layer.MaterialIndex = r.Int();
            r.Int();
            layer.Color = r.UInt();
            r.Skip(20);
            layer.Name = r.String();
            layer.Visible = r.Byte() != 0;
            r.Int(); // line type
            r.UInt(); // plot colour
            r.Double(); // plot weight
            layer.Locked = r.Byte() != 0;
            layer.Id = r.Guid();
            layer.ParentId = r.Guid();
        }
        catch (ModelFormatException)
        {
            // Older layer layouts: keep what was read.
        }

        return layer;
    }

    private static Material ReadMaterial(RhinoReaderCursor record, RhinoChunk chunk, ReadOnlyMemory<byte> data)
    {
        var material = new Material();
        if (!ReadClass(record, out _, out var start, out var end))
        {
            return material;
        }

        var r = new RhinoReaderCursor(data, start, end, true);
        try
        {
            // The class body is an anonymous chunk: [int, int, anonymous chunk (component attributes: id and name)], then the colours.
            var outer = r.Chunk();
            if (outer.Type != 0x40008000)
            {
                return material;
            }

            var body = r.Sub(outer);
            body.Skip(8);
            var attributes = body.Chunk();
            var a = body.Sub(attributes);
            a.Skip(12);
            material.Id = a.Guid();
            a.Skip(4);
            material.Name = NullIfEmpty(a.String());
            body.Skip(20);
            var diffuse = body.UInt();
            body.Skip(4 + 4 + 4 + 4); // emission, specular, reflection, transparent colour
            body.Skip(8 + 8); // index of refraction, reflectivity
            material.Shine = (float)Math.Clamp(body.Double() / 255.0, 0, 1);
            var transparency = body.Double();
            material.Diffuse = new Vector4((diffuse & 255) / 255f, ((diffuse >> 8) & 255) / 255f, ((diffuse >> 16) & 255) / 255f, (float)Math.Clamp(1 - transparency, 0, 1));
        }
        catch (ModelFormatException)
        {
        }

        return material;
    }

    private static Obj ReadObject(RhinoReaderCursor record, RhinoChunk chunk, ReadOnlyMemory<byte> data, CancellationToken cancel)
    {
        var o = new Obj { Mode = 0, RecordStart = chunk.DataStart, RecordEnd = chunk.DataEnd };
        while (record.Remaining >= 12)
        {
            var c = record.Chunk();
            if (c.IsShort)
            {
                if (c.Type == 0x82000071)
                {
                    o.Type = (int)c.Value;
                }
                else if (c.Type == 0x8200007f)
                {
                    break;
                }

                continue;
            }

            switch (c.Type)
            {
                case 0x27ffa:
                {
                    var one = new RhinoReaderCursor(data, c.DataStart - 12, c.End, true);
                    if (ReadClass(one, out var id, out var s, out var e))
                    {
                        o.Class = id;
                        o.DataStart = s;
                        o.DataEnd = e;
                    }

                    break;
                }

                case 0x2008072:
                    ReadAttributes(record.Sub(c), o);
                    break;
                case 0x2000073:
                    o.UserStart = c.DataStart;
                    o.UserEnd = c.DataEnd;
                    ReadUserStrings(new RhinoReaderCursor(data, c.DataStart, c.DataEnd, true), o, cancel);
                    break;
            }
        }

        return o;
    }

    /// <summary>ON_3dmObjectAttributes: version, id, layer index, then (item id, value) pairs ending with 0.</summary>
    private static void ReadAttributes(RhinoReaderCursor r, Obj o)
    {
        try
        {
            r.Byte();
            o.Id = r.Guid();
            o.Layer = r.Int();
            while (r.Remaining > 0)
            {
                var item = r.Byte();
                switch (item)
                {
                    case 0:
                        return;
                    case 1:
                        o.Name = r.String();
                        break;
                    case 2:
                        r.String();
                        break;
                    case 3:
                        r.Int();
                        break;
                    case 4:
                        o.MaterialIndex = r.Int();
                        break;
                    case 5:
                    {
                        var inner = r.Chunk();
                        _ = inner;
                        break;
                    }

                    case 6:
                        o.Color = r.UInt();
                        break;
                    case 7:
                        r.UInt();
                        break;
                    case 8:
                        r.Double();
                        break;
                    case 9:
                        r.Byte();
                        break;
                    case 10:
                        r.Int();
                        break;
                    case 11:
                        o.Visible = r.Byte() != 0;
                        break;
                    case 12:
                        o.Mode = r.Byte();
                        break;
                    case 13:
                        o.ColorSource = r.Byte();
                        break;
                    case 14:
                    case 15:
                    case 17:
                        r.Byte();
                        break;
                    case 16:
                        o.MaterialSource = r.Byte();
                        break;
                    case 22:
                        r.Int();
                        break;
                    default:
                        return; // an item this reader does not know: the size is unknown, so stop
                }
            }
        }
        catch (ModelFormatException)
        {
        }
    }

    private static readonly byte[] UserStringList = [0x29, 0xde, 0x28, 0xce, 0xc5, 0xf4, 0xaa, 0x4f, 0xa5, 0x0a, 0xc3, 0xa6, 0x84, 0x9b, 0x63, 0x29];

    /// <summary>User strings live in user-data records; each pair is a leaf anonymous chunk [int 1][int 0][key][value] found by scanning nested chunks.</summary>
    private static void ReadUserStrings(RhinoReaderCursor r, Obj o, CancellationToken cancel)
    {
        var span = r.Span[r.Pos..r.End];
        if (span.IndexOf(UserStringList) < 0)
        {
            return;
        }

        ScanPairs(r, r.Pos, r.End, o.UserStrings, 0, cancel);
    }

    private static void ScanPairs(RhinoReaderCursor r, int start, int end, List<(string, string)> into, int depth, CancellationToken cancel)
    {
        if (depth > 8 || into.Count > 10_000)
        {
            return;
        }

        var span = r.Span;
        var i = start;
        long tick = 0;
        while (i + 12 <= end)
        {
            Cancel.Every(cancel, tick++, 255);
            if (BitConverter.ToUInt32(span.Slice(i, 4)) == 0x40008000)
            {
                var length = BitConverter.ToInt64(span.Slice(i + 4, 8));
                if (length >= 4 && length <= end - i - 12)
                {
                    var dataStart = i + 12;
                    var dataEnd = dataStart + (int)length - 4;
                    if (!TryPair(r, dataStart, dataEnd, into))
                    {
                        ScanPairs(r, dataStart, dataEnd, into, depth + 1, cancel);
                    }

                    i = dataStart + (int)length;
                    continue;
                }
            }

            i++;
        }
    }

    private static bool TryPair(RhinoReaderCursor r, int start, int end, List<(string, string)> into)
    {
        try
        {
            var c = r.SubRange(start, end);
            if (c.Int() != 1 || c.Int() != 0)
            {
                return false;
            }

            var key = c.String();
            var value = c.String();
            if (c.Remaining != 0 || key.Length == 0)
            {
                return false;
            }

            into.Add((key, value));
            return true;
        }
        catch (ModelFormatException)
        {
            return false;
        }
    }

    private static Block? ReadBlock(RhinoReaderCursor record, RhinoChunk chunk, ReadOnlyMemory<byte> data)
    {
        if (!ReadClass(record, out _, out var start, out var end))
        {
            return null;
        }

        var r = new RhinoReaderCursor(data, start, end, true);
        try
        {
            var outer = r.Chunk();
            if (outer.Type != 0x40008000)
            {
                return null;
            }

            var body = r.Sub(outer);
            body.Skip(8);
            var first = body.Chunk();
            var a = body.Sub(first);
            a.Skip(10);
            var block = new Block { Id = a.Guid() };
            a.Skip(7);
            block.Name = a.String();
            block.DataStart = outer.DataStart;
            block.DataEnd = outer.DataEnd;
            block.Description = ReadDescription(r, outer);
            return block;
        }
        catch (ModelFormatException)
        {
            return null;
        }
    }

    /// <summary>The member objects of a block: a count followed by that many object ids that exist in the object table.</summary>
    private static void ResolveMembers(Block block, ReadOnlyMemory<byte> data, List<Obj> objects, CancellationToken cancel)
    {
        var ids = new HashSet<Guid>();
        foreach (var o in objects)
        {
            ids.Add(o.Id);
        }

        var span = data.Span;
        for (var p = block.DataStart; p + 4 <= block.DataEnd; p++)
        {
            Cancel.Every(cancel, p, 255);
            var count = BitConverter.ToInt32(span.Slice(p, 4));
            if (count <= 0 || count > 1_000_000 || (long)p + 4 + 16L * count > block.DataEnd + 4)
            {
                continue;
            }

            var ok = true;
            for (var k = 0; k < count && ok; k++)
            {
                ok = ids.Contains(new Guid(span.Slice(p + 4 + k * 16, 16)));
            }

            if (ok)
            {
                for (var k = 0; k < count; k++)
                {
                    block.Members.Add(new Guid(span.Slice(p + 4 + k * 16, 16)));
                }

                return;
            }
        }
    }

    private static string ReadDescription(RhinoReaderCursor r, RhinoChunk outer)
    {
        // Strings appear as [count][UTF-16 units]; the name was read already, so take the second plausible one.
        var span = r.Span;
        var found = 0;
        for (var p = outer.DataStart + 40; p + 8 <= outer.DataEnd; p++)
        {
            var count = BitConverter.ToInt32(span.Slice(p, 4));
            if (count < 2 || count > 512 || p + 4 + count * 2 > outer.DataEnd)
            {
                continue;
            }

            var ok = span[p + 4 + (count - 1) * 2] == 0 && span[p + 5 + (count - 1) * 2] == 0;
            for (var k = 0; k < count - 1 && ok; k++)
            {
                var ch = BitConverter.ToUInt16(span.Slice(p + 4 + k * 2, 2));
                ok = ch >= 32 && ch != 0xFFFF;
            }

            if (ok && ++found == 2)
            {
                return Encoding.Unicode.GetString(span.Slice(p + 4, (count - 1) * 2));
            }
        }

        return "";
    }

    // ------------------------------------------------------------------ building the model

    private static Model3D Build(Model3D model, ReadOnlyMemory<byte> data, LoadOptions options, List<Layer> layers, List<Material> materials, List<Obj> objects, Dictionary<Guid, Block> blocks)
    {
        var meta = model.Metadata;
        var cancel = options.Cancellation;

        // Layers: full paths from the parent chain, indexed by the layer index objects use.
        var byId = new Dictionary<Guid, Layer>();
        foreach (var l in layers)
        {
            byId[l.Id] = l;
        }

        string PathOf(Layer l, int depth = 0)
        {
            if (l.Path.Length > 0)
            {
                return l.Path;
            }

            var path = depth < 32 && l.ParentId != Guid.Empty && byId.TryGetValue(l.ParentId, out var parent) && !ReferenceEquals(parent, l) ? PathOf(parent, depth + 1) + "::" + l.Name : l.Name;
            l.Path = path;
            return path;
        }

        var layerIndex = new Dictionary<int, int>();
        foreach (var l in layers)
        {
            var path = PathOf(l);
            var parentVisible = true;
            for (var p = l; p.ParentId != Guid.Empty && byId.TryGetValue(p.ParentId, out var parent) && !ReferenceEquals(parent, p);)
            {
                parentVisible &= parent.Visible;
                p = parent;
                if (p == l)
                {
                    break;
                }
            }

            layerIndex[l.Index] = model.Layers.Count;
            model.Layers.Add(new ModelLayer(path) { Visible = l.Visible && parentVisible });
            meta.LayerNames.Add(path);
        }

        // Materials are created on demand.
        var materialCache = new Dictionary<(float, float, float, float, string?), int>();
        int ModelMaterial(Vector4 color, string? name, float shine = 0)
        {
            var key = (color.X, color.Y, color.Z, color.W, name);
            if (!materialCache.TryGetValue(key, out var index))
            {
                index = model.Materials.Count;
                model.Materials.Add(new ModelMaterial { Name = name, BaseColor = color, Roughness = shine > 0 ? 1 - shine : 0.6f });
                materialCache[key] = index;
                if (name is not null && !meta.MaterialNames.Contains(name))
                {
                    meta.MaterialNames.Add(name);
                }
            }

            return index;
        }

        Layer? LayerOf(Obj o) => layers.FirstOrDefault(l => l.Index == o.Layer);

        int Appearance(Obj o)
        {
            var layer = LayerOf(o);
            var materialIndex = o.MaterialSource == 1 ? o.MaterialIndex : o.MaterialSource == 0 && layer is not null ? layer.MaterialIndex : -1;
            if (materialIndex >= 0 && materialIndex < materials.Count)
            {
                var m = materials[materialIndex];
                return ModelMaterial(m.Diffuse, m.Name, m.Shine);
            }

            uint rgb;
            if (o.ColorSource == 1)
            {
                rgb = o.Color;
            }
            else if (layer is not null)
            {
                rgb = layer.Color;
            }
            else
            {
                return -1;
            }

            return ModelMaterial(new Vector4((rgb & 255) / 255f, ((rgb >> 8) & 255) / 255f, ((rgb >> 16) & 255) / 255f, 1), null);
        }

        var skipped = new Dictionary<string, int>(StringComparer.Ordinal);
        void Skipped(string what) => skipped[what] = skipped.GetValueOrDefault(what) + 1;

        // Geometry of one object as a node (null when it has none this reader decodes).
        SceneNode? Geometry(Obj o)
        {
            var r = new RhinoReaderCursor(data, o.DataStart, o.DataEnd, true);
            var node = new SceneNode(o.Name.Length > 0 ? o.Name : null);
            if (layerIndex.TryGetValue(o.Layer, out var li))
            {
                node.Layer = li;
            }

            foreach (var (k, v) in o.UserStrings)
            {
                node.Properties[k] = v;
            }

            try
            {
                if (o.Class == RhinoGeometry.MeshClass)
                {
                    var mesh = RhinoGeometry.Mesh(r, options);
                    if (mesh is null)
                    {
                        return null;
                    }

                    var material = Appearance(o);
                    if (material >= 0 && mesh.Colors.Length == 0)
                    {
                        mesh.Groups.Add(new MeshGroup(0, mesh.Indices.Length, material));
                    }

                    mesh.Name = node.Name;
                    node.Mesh = mesh;
                    model.Meshes.Add(mesh);
                    return node;
                }

                if (o.Class == RhinoGeometry.PointClass)
                {
                    var p = RhinoGeometry.Point(r);
                    var mesh = new MeshData { Name = node.Name, Positions = [p.X, p.Y, p.Z] };
                    ColourPoints(mesh, Appearance(o), model);
                    node.Mesh = mesh;
                    model.Meshes.Add(mesh);
                    return node;
                }

                if (o.Class == RhinoGeometry.PointCloudClass)
                {
                    var points = RhinoGeometry.PointCloud(r, cancel);
                    if (points.Count == 0)
                    {
                        return null;
                    }

                    var positions = new float[points.Count * 3];
                    for (var i = 0; i < points.Count; i++)
                    {
                        positions[i * 3] = points[i].X;
                        positions[i * 3 + 1] = points[i].Y;
                        positions[i * 3 + 2] = points[i].Z;
                    }

                    var mesh = new MeshData { Name = node.Name, Positions = positions };
                    ColourPoints(mesh, Appearance(o), model);
                    node.Mesh = mesh;
                    model.Meshes.Add(mesh);
                    return node;
                }

                var lines = new RhinoGeometry.Lines();
                if (o.Class == RhinoGeometry.LineCurveClass)
                {
                    RhinoGeometry.LineCurve(r, lines);
                }
                else if (o.Class == RhinoGeometry.PolylineCurveClass)
                {
                    RhinoGeometry.PolylineCurve(r, lines, cancel);
                }
                else if (o.Class == RhinoGeometry.ArcCurveClass)
                {
                    RhinoGeometry.ArcCurve(r, lines, options);
                }
                else if (o.Class == RhinoGeometry.NurbsCurveClass || o.Class == RhinoGeometry.NurbsCurveClass2)
                {
                    RhinoGeometry.NurbsCurve(r, lines, options);
                }
                else
                {
                    // A B-rep, surface or extrusion: the file may still carry the shaded-display meshes it was saved with.
                    var embedded = EmbeddedMeshes(data, o, options);
                    if (embedded.Count == 0)
                    {
                        Skipped(DescribeClass(o));
                        return null;
                    }

                    var material = Appearance(o);
                    foreach (var mesh in embedded)
                    {
                        if (material >= 0 && mesh.Colors.Length == 0)
                        {
                            mesh.Groups.Add(new MeshGroup(0, mesh.Indices.Length, material));
                        }

                        model.Meshes.Add(mesh);
                    }

                    if (embedded.Count == 1)
                    {
                        embedded[0].Name = node.Name;
                        node.Mesh = embedded[0];
                    }
                    else
                    {
                        foreach (var mesh in embedded)
                        {
                            node.Children.Add(new SceneNode(null) { Mesh = mesh, Layer = node.Layer });
                        }
                    }

                    return node;
                }

                if (lines.Segments.Count == 0)
                {
                    return null;
                }

                var edges = new MeshData { Name = node.Name, Positions = lines.Positions.ToArray(), EdgeIndices = lines.Segments.ToArray() };
                edges.EdgeMaterial = Appearance(o);
                node.Mesh = edges;
                model.Meshes.Add(edges);
                return node;
            }
            catch (ModelFormatException ex) when (ex is not UnsupportedModelFormatException)
            {
                Skipped("damaged " + DescribeClass(o));
                return null;
            }
            catch (UnsupportedModelFormatException ex)
            {
                model.Warnings.Add(ex.Message);
                return null;
            }
        }

        var objectById = new Dictionary<Guid, Obj>();
        foreach (var o in objects)
        {
            objectById[o.Id] = o;
        }

        var instanceCount = 0;
        List<SceneNode> BuildBlock(Block block, int depth)
        {
            if (block.Built is not null)
            {
                return block.Built;
            }

            var list = new List<SceneNode>();
            if (block.Building || depth > 8)
            {
                model.Warnings.Add($"Block '{block.Name}' contains itself; the inner use was dropped.");
                return list;
            }

            block.Building = true;
            foreach (var id in block.Members)
            {
                if (objectById.TryGetValue(id, out var member) && Place(member, depth + 1) is { } n)
                {
                    list.Add(n);
                }
            }

            block.Building = false;
            block.Built = list;
            return list;
        }

        SceneNode? CloneForInstance(SceneNode source)
        {
            var copy = new SceneNode(source.Name) { Transform = source.Transform, Mesh = source.Mesh, Layer = source.Layer };
            foreach (var (k, v) in source.Properties)
            {
                copy.Properties[k] = v;
            }

            foreach (var child in source.Children)
            {
                copy.Children.Add(CloneForInstance(child)!);
            }

            return copy;
        }

        SceneNode? Place(Obj o, int depth)
        {
            Cancel.Every(cancel, instanceCount++);
            if (o.Class == RhinoGeometry.InstanceRefClass)
            {
                var r = new RhinoReaderCursor(data, o.DataStart, o.DataEnd, true);
                try
                {
                    r.Byte();
                    var blockId = r.Guid();
                    var m = new double[16];
                    for (var i = 0; i < 16; i++)
                    {
                        m[i] = r.Double();
                    }

                    if (!blocks.TryGetValue(blockId, out var block))
                    {
                        Skipped("instances of a missing block");
                        return null;
                    }

                    var node = new SceneNode(o.Name.Length > 0 ? o.Name : block.Name);
                    // The file's matrix is a column-vector transform; System.Numerics is row-vector, so transpose.
                    node.Transform = new Matrix4x4(
                        (float)m[0], (float)m[4], (float)m[8], (float)m[12],
                        (float)m[1], (float)m[5], (float)m[9], (float)m[13],
                        (float)m[2], (float)m[6], (float)m[10], (float)m[14],
                        (float)m[3], (float)m[7], (float)m[11], (float)m[15]);
                    if (layerIndex.TryGetValue(o.Layer, out var li))
                    {
                        node.Layer = li;
                    }

                    foreach (var child in BuildBlock(block, depth))
                    {
                        node.Children.Add(CloneForInstance(child)!);
                    }

                    if (!meta.PartNames.Contains(block.Name))
                    {
                        meta.PartNames.Add(block.Name);
                    }

                    return node.Children.Count == 0 ? null : node;
                }
                catch (ModelFormatException)
                {
                    Skipped("damaged instance");
                    return null;
                }
            }

            return Geometry(o);
        }

        var hidden = 0;
        foreach (var o in objects)
        {
            Cancel.Every(cancel, instanceCount++);
            if (o.Mode == 3)
            {
                continue; // a member of a block definition: drawn through its instances
            }

            if (!o.Visible || o.Mode == 1)
            {
                hidden++;
                continue;
            }

            if (Place(o, 0) is { } node)
            {
                model.Roots.Add(node);
            }
        }

        // Block definitions that nothing places still count as parts; their members are drawn only through references.
        foreach (var block in blocks.Values)
        {
            if (!meta.PartNames.Contains(block.Name))
            {
                meta.PartNames.Add(block.Name);
            }
        }

        if (hidden > 0)
        {
            model.Warnings.Add($"{hidden} hidden object(s) were left out.");
        }

        foreach (var (what, count) in skipped.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            model.Warnings.Add($"{count} {what} object(s) were not read.");
        }

        meta.Properties.Add(new ModelProperty("Objects", objects.Count.ToString(CultureInfo.InvariantCulture)));
        meta.Properties.Add(new ModelProperty("Layers", layers.Count.ToString(CultureInfo.InvariantCulture)));
        meta.Properties.Add(new ModelProperty("Block definitions", blocks.Count.ToString(CultureInfo.InvariantCulture)));
        foreach (var mat in materials)
        {
            if (mat.Name is not null && !meta.MaterialNames.Contains(mat.Name))
            {
                meta.MaterialNames.Add(mat.Name);
            }
        }

        meta.HasGeometry = model.Meshes.Count > 0;
        meta.Statistics = model.ComputeStatistics();
        meta.Bounds = model.ComputeBounds();
        return model;
    }

    private static readonly byte[] MeshClassBytes = RhinoGeometry.MeshClass.ToByteArray();

    /// <summary>
    /// The display meshes saved inside an object that has none of its own (a B-rep keeps one per face in its user data): every mesh class chunk found
    /// in the object's record. Their number is counted as a "render mesh" in the metadata of the loader's caller through the warnings.
    /// </summary>
    private static List<MeshData> EmbeddedMeshes(ReadOnlyMemory<byte> data, Obj o, LoadOptions options)
    {
        var list = new List<MeshData>();
        var span = data.Span;
        var from = o.RecordStart;
        long tick = 0;
        while (from < o.RecordEnd && list.Count < 100_000)
        {
            Cancel.Every(options.Cancellation, tick++);
            var found = span[from..o.RecordEnd].IndexOf(MeshClassBytes);
            if (found < 0)
            {
                break;
            }

            var at = from + found;
            from = at + 16;
            // The uuid sits in a 0x2fffb chunk (type, 8-byte length of 20) that opens a 0x27ffa class chunk.
            var header = at - 12;
            var classStart = header - 12;
            if (classStart < o.RecordStart || BitConverter.ToUInt32(span.Slice(header, 4)) != 0x2fffb || BitConverter.ToUInt32(span.Slice(classStart, 4)) != 0x27ffa)
            {
                continue;
            }

            try
            {
                var cursor = new RhinoReaderCursor(data, classStart, o.RecordEnd, true);
                var one = cursor.Chunk();
                var inner = new RhinoReaderCursor(data, classStart, one.End, true);
                if (ReadClass(inner, out _, out var s, out var e) && RhinoGeometry.Mesh(new RhinoReaderCursor(data, s, e, true), options) is { } mesh)
                {
                    list.Add(mesh);
                }

                from = Math.Max(from, one.End);
            }
            catch (ModelFormatException)
            {
            }
        }

        return list;
    }

    private static void ColourPoints(MeshData mesh, int material, Model3D model)
    {
        if (material < 0)
        {
            return;
        }

        var c = model.Materials[material].BaseColor;
        var n = mesh.VertexCount;
        mesh.Colors = new float[n * 4];
        for (var i = 0; i < n; i++)
        {
            mesh.Colors[i * 4] = c.X;
            mesh.Colors[i * 4 + 1] = c.Y;
            mesh.Colors[i * 4 + 2] = c.Z;
            mesh.Colors[i * 4 + 3] = 1;
        }
    }

    private static string DescribeClass(Obj o)
    {
        if (o.Class == RhinoGeometry.BrepClass || o.Class == RhinoGeometry.Brep6Class)
        {
            return "B-rep without display meshes";
        }

        if (o.Class == RhinoGeometry.ExtrusionClass)
        {
            return "extrusion without display meshes";
        }

        if (o.Class == RhinoGeometry.SubDClass)
        {
            return "SubD";
        }

        if (o.Class == RhinoGeometry.NurbsSurfaceClass)
        {
            return "surface";
        }

        if (o.Class == RhinoGeometry.PolyCurveClass)
        {
            return "poly-curve";
        }

        return o.Type switch
        {
            0x8 => "surface",
            0x10 => "B-rep without display meshes",
            0x200 => "annotation",
            0x2000 => "text dot",
            0x10000 => "hatch",
            0x40000 => "SubD",
            0x40000000 => "extrusion without display meshes",
            _ => "object of type 0x" + o.Type.ToString("x", CultureInfo.InvariantCulture),
        };
    }
}
