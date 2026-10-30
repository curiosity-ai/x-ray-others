using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace XRay.ThreeD.Formats.Cad;

using XRay.ThreeD.Internal;

/// <summary>
/// AutoCAD DWG, from the public Open Design Specification for .dwg files: the version string, the file header (code page, section
/// locator records, preview image address), the preview image (DIB, WMF or PNG) and, for the R13 to R2000 layout (AC1012 to AC1015,
/// whose sections are stored plain), the layer table read through the object map. Later versions (AC1018 and up) keep their data
/// in compressed, encrypted pages; their version and preview are reported, their drawing is not decoded. Geometry is never decoded:
/// <see cref="Read"/> says so.
/// </summary>
internal static class DwgReader
{
    private static readonly byte[] PreviewSentinel = [0x1F, 0x25, 0x6D, 0x07, 0xD4, 0x36, 0x28, 0x28, 0x9D, 0x57, 0xCA, 0x3F, 0x9D, 0x44, 0x10, 0x2B];

    private static readonly (string Code, string Product)[] Versions =
    [
        ("MC0.0", "AutoCAD Release 1.0"), ("AC1.2", "AutoCAD Release 1.2"), ("AC1.4", "AutoCAD Release 1.4"), ("AC1.50", "AutoCAD Release 2.0"),
        ("AC2.10", "AutoCAD Release 2.10"), ("AC1002", "AutoCAD Release 2.5"), ("AC1003", "AutoCAD Release 2.6"), ("AC1004", "AutoCAD Release 9"),
        ("AC1006", "AutoCAD Release 10"), ("AC1009", "AutoCAD Release 11/12"), ("AC1012", "AutoCAD Release 13"), ("AC1014", "AutoCAD Release 14"),
        ("AC1015", "AutoCAD 2000"), ("AC1018", "AutoCAD 2004"), ("AC1021", "AutoCAD 2007"), ("AC1024", "AutoCAD 2010"), ("AC1027", "AutoCAD 2013"),
        ("AC1032", "AutoCAD 2018"),
    ];

    public static bool IsDwg(ReadOnlySpan<byte> head) => head.Length >= 6 && head[0] == (byte)'A' && head[1] == (byte)'C' && (head[2] == (byte)'1' || head[2] == (byte)'2') && char.IsAsciiLetterOrDigit((char)head[3]);

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var metadata = ReadMetadata(data, options);
        if (metadata.FormatVersion == "AC1015" && data.Length > 0x19 + 9)
        {
            return DwgGeometry.Read(data, options, metadata);
        }

        throw new UnsupportedModelFormatException(
            ModelFormats.Dwg,
            $"DWG geometry is not decoded ({metadata.FormatVersion}, {metadata.GetProperty("Release")}); the version, preview and, for AC1012 to AC1015, the layers are available through ReadMetadata and ReadPreview.");
    }

    public static ModelMetadata ReadMetadata(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var s = data.Span;
        if (!IsDwg(s) || s.Length < 0x19)
        {
            throw new ModelFormatException("Not a DWG file.");
        }

        var code = Encoding.ASCII.GetString(s[..6]);
        var product = Array.Find(Versions, v => v.Code == code).Product;
        var meta = new ModelMetadata { Format = ModelFormats.Dwg, DocumentKind = "drawing", FormatVersion = code, Application = product ?? "AutoCAD (unrecognised release)" };
        meta.Properties.Add(new ModelProperty("Release", product ?? code, "dwg"));
        meta.HasGeometry = false;
        var generation = Generation(code);
        if (generation >= 13)
        {
            meta.Properties.Add(new ModelProperty("Maintenance release", s[0x0B].ToString(CultureInfo.InvariantCulture), "dwg"));
            meta.Properties.Add(new ModelProperty("Code page", BinaryPrimitives.ReadUInt16LittleEndian(s[0x13..]).ToString(CultureInfo.InvariantCulture), "dwg"));
        }

        var preview = ReadPreview(data, options);
        meta.HasPreview = preview is not null;
        if (generation is >= 13 and <= 15 && s.Length >= 0x19 + 9)
        {
            ReadSectionsR2000(data, meta, options);
        }
        else if (generation >= 18)
        {
            meta.Notes.Add($"{product ?? code} stores its data in compressed, encrypted pages; the version is reported, the drawing is not decoded.");
        }
        else
        {
            meta.Notes.Add("This DWG release predates the R13 layout and is not decoded.");
        }

        if (generation == 15)
        {
            meta.HasGeometry = true;
            meta.Notes.Add("Entities of this release (lines, circles, arcs, polylines, text, inserts) are decoded by Load; hatches, dimensions and others are not.");
        }
        else
        {
            meta.Notes.Add("DWG geometry is not decoded.");
        }

        return meta;
    }

    /// <summary>1 for AC1.x, otherwise the AutoCAD release generation implied by the version string (13 = AC1012, 14, 15 = AC1015, 18, 21, 24, 27, 32).</summary>
    private static int Generation(string code) => code switch
    {
        "AC1012" => 13,
        "AC1014" => 14,
        "AC1015" => 15,
        "AC1018" => 18,
        "AC1021" => 21,
        "AC1024" => 24,
        "AC1027" => 27,
        "AC1032" => 32,
        _ => 0,
    };

    // ---- preview -------------------------------------------------------------------------------------------------------

    /// <summary>The thumbnail from the preview section the header points at (DIB converted to PNG, WMF and PNG as stored), or null.</summary>
    public static PreviewImage? ReadPreview(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var s = data.Span;
        if (!IsDwg(s) || s.Length < 0x11 || Generation(Encoding.ASCII.GetString(s[..6])) is < 13 or >= 21)
        {
            return null;
        }

        var seeker = (long)BinaryPrimitives.ReadUInt32LittleEndian(s[0x0D..]);
        if (seeker <= 0 || seeker + 16 + 4 + 1 > s.Length || !s.Slice((int)seeker, 16).SequenceEqual(PreviewSentinel))
        {
            return null;
        }

        var count = s[(int)seeker + 20];
        PreviewImage? best = null;
        for (var i = 0; i < count && i < 8; i++)
        {
            var entry = (int)seeker + 21 + i * 9;
            if (entry + 9 > s.Length)
            {
                break;
            }

            var type = s[entry];
            var start = (long)BinaryPrimitives.ReadUInt32LittleEndian(s[(entry + 1)..]);
            var length = (long)BinaryPrimitives.ReadUInt32LittleEndian(s[(entry + 5)..]);
            if (type is < 2 or > 4 || start < 0 || length <= 0 || start + length > s.Length)
            {
                continue;
            }

            var bytes = s.Slice((int)start, (int)length);
            if (type == 4 && bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == (byte)'P')
            {
                best = new PreviewImage("image/png", bytes.ToArray());
            }
            else if (type == 2 && best is null && DibImage.DecodeRgb(bytes, out var w, out var h, options.Cancellation) is { } rgb)
            {
                best = new PreviewImage("image/png", DibImage.EncodePng(rgb, w, h));
            }
            else if (type == 3 && best is null)
            {
                best = new PreviewImage("image/wmf", bytes.ToArray());
            }
        }

        return best;
    }

    // ---- R13 to R2000 sections -----------------------------------------------------------------------------------------

    private static void ReadSectionsR2000(ReadOnlyMemory<byte> data, ModelMetadata meta, LoadOptions options)
    {
        var s = data.Span;
        var records = (int)BinaryPrimitives.ReadUInt32LittleEndian(s[0x15..]);
        if (records is < 0 or > 32 || 0x19 + records * 9 > s.Length)
        {
            return;
        }

        var seeker = new long[records];
        var size = new long[records];
        var number = new int[records];
        for (var i = 0; i < records; i++)
        {
            number[i] = s[0x19 + i * 9];
            seeker[i] = BinaryPrimitives.ReadUInt32LittleEndian(s[(0x19 + i * 9 + 1)..]);
            size[i] = BinaryPrimitives.ReadUInt32LittleEndian(s[(0x19 + i * 9 + 5)..]);
        }

        meta.Properties.Add(new ModelProperty("Sections", string.Join(", ", Enumerable.Range(0, records).Select(i => $"{SectionName(number[i])} ({size[i]} bytes)")), "dwg"));
        var map = Array.FindIndex(number, n => n == 2);
        if (map < 0 || seeker[map] <= 0 || seeker[map] + size[map] > s.Length)
        {
            return;
        }

        try
        {
            var objects = ReadObjectMap(s, seeker[map], size[map], options.Cancellation);
            meta.Properties.Add(new ModelProperty("Objects", objects.Count.ToString(CultureInfo.InvariantCulture), "dwg"));
            var tick = 0L;
            foreach (var (_, location) in objects)
            {
                Cancel.Every(options.Cancellation, tick++);
                if (TryReadLayerName(s, location, out var name) && name.Length > 0 && !meta.LayerNames.Contains(name))
                {
                    meta.LayerNames.Add(name);
                }
            }
        }
        catch (ModelFormatException)
        {
            meta.Notes.Add("The DWG object map could not be read; layers are not listed.");
        }
    }

    private static string SectionName(int number) => number switch
    {
        0 => "header variables",
        1 => "classes",
        2 => "object map",
        3 => "unknown",
        4 => "measurement",
        5 => "auxiliary header",
        _ => "section " + number.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>Handle to file offset pairs from the object map: blocks of big-endian size, then modular-char deltas of handle and location.</summary>
    internal static List<(long Handle, long Location)> ReadObjectMapInternal(ReadOnlySpan<byte> s, long start, long length, CancellationToken token) => ReadObjectMap(s, start, length, token);

    internal static bool TryReadLayerNameInternal(ReadOnlySpan<byte> s, long location, out string name) => TryReadLayerName(s, location, out name);

    private static List<(long Handle, long Location)> ReadObjectMap(ReadOnlySpan<byte> s, long start, long length, CancellationToken token)
    {
        var result = new List<(long, long)>();
        var p = start;
        var end = start + length;
        var tick = 0L;
        while (p + 2 <= end)
        {
            var block = (s[(int)p] << 8) | s[(int)p + 1];
            if (block <= 2)
            {
                break;
            }

            var blockEnd = Math.Min(p + block, end);
            long handle = 0;
            long location = 0;
            var q = p + 2;
            while (q < blockEnd)
            {
                Cancel.Every(token, tick++);
                handle += ReadModularChar(s, ref q, blockEnd, signed: false);
                location += ReadModularChar(s, ref q, blockEnd, signed: true);
                result.Add((handle, location));
                if (result.Count > 5_000_000)
                {
                    throw new ModelFormatException("The DWG object map is implausibly large.");
                }
            }

            p += block + 2; // the block's own size covers its bytes; two more hold its CRC
        }

        return result;
    }

    private static long ReadModularChar(ReadOnlySpan<byte> s, ref long p, long end, bool signed)
    {
        long value = 0;
        var shift = 0;
        while (true)
        {
            if (p >= end || shift > 56)
            {
                throw new ModelFormatException("A DWG modular character is truncated.");
            }

            var b = s[(int)p++];
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

    /// <summary>Reads the name of the object at <paramref name="location"/> when it is a LAYER (object type 51).</summary>
    private static bool TryReadLayerName(ReadOnlySpan<byte> s, long location, out string name)
    {
        name = "";
        if (location < 0 || location + 2 > s.Length)
        {
            return false;
        }

        // Modular short: little-endian 16-bit words, 15 bits each, continued while the high bit is set.
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

        var bits = new BitReader(s.Slice((int)p, (int)size));
        if (bits.ReadBitShort() != 51)
        {
            return false;
        }

        // Common table-entry fields: the object's size in bits (RL), its handle, extended data (size, application handle, raw bytes) and reactor count.
        bits.Skip(32);
        bits.ReadHandle();
        for (var guard = 0; guard < 1024; guard++)
        {
            var eed = bits.ReadBitShort();
            if (eed == 0)
            {
                break;
            }

            bits.ReadHandle();
            bits.Skip(eed * 8L);
        }

        bits.ReadBitLong();
        name = bits.ReadText();
        return !bits.Overrun;
    }

    /// <summary>The DWG bit stream: most significant bit first, with the variable-width codes (BS, BL, H, TV) of the R13 to R2000 layout.</summary>
    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private long _bit;

        public BitReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _bit = 0;
        }

        public readonly bool Overrun => _bit > (long)_data.Length * 8;

        public void Skip(long bits) => _bit += bits;

        private int ReadBits(int count)
        {
            var value = 0;
            for (var i = 0; i < count; i++)
            {
                var index = _bit >> 3;
                var bit = 0;
                if (index < _data.Length)
                {
                    bit = (_data[(int)index] >> (7 - (int)(_bit & 7))) & 1;
                }

                value = (value << 1) | bit;
                _bit++;
            }

            return value;
        }

        public int ReadBitShort() => ReadBits(2) switch
        {
            0 => ReadBits(8) | (ReadBits(8) << 8),
            1 => ReadBits(8),
            2 => 0,
            _ => 256,
        };

        public int ReadBitLong() => ReadBits(2) switch
        {
            0 => ReadBits(8) | (ReadBits(8) << 8) | (ReadBits(8) << 16) | (ReadBits(8) << 24),
            1 => ReadBits(8),
            _ => 0,
        };

        public long ReadHandle()
        {
            ReadBits(4);
            var counter = ReadBits(4);
            long value = 0;
            for (var i = 0; i < counter; i++)
            {
                value = (value << 8) | (uint)ReadBits(8);
            }

            return value;
        }

        public string ReadText()
        {
            var length = ReadBitShort();
            if (length <= 0 || length > 4096 || _bit + length * 8L > (long)_data.Length * 8)
            {
                return "";
            }

            var bytes = new byte[length];
            for (var i = 0; i < length; i++)
            {
                bytes[i] = (byte)ReadBits(8);
            }

            var end = Array.IndexOf(bytes, (byte)0);
            return Encoding.Latin1.GetString(bytes, 0, end < 0 ? length : end);
        }
    }
}
