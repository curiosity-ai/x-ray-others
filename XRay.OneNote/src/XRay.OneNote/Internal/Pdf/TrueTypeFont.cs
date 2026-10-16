using System.Buffers.Binary;
using System.Text;

namespace XRay.OneNote.Internal.Pdf;

/// <summary>
/// A TrueType (glyf-outline) font read for PDF embedding: character map, advance widths, the
/// metrics a font descriptor needs, and glyph subsetting. CFF-based OpenType and collections are
/// not supported (<see cref="TryLoad"/> returns null for them).
/// </summary>
internal sealed class TrueTypeFont
{
    private readonly byte[] _data;
    private readonly Dictionary<string, (int Offset, int Length)> _tables;
    private readonly Dictionary<int, ushort> _cmap;
    private readonly ushort[] _advances;
    private readonly int[] _glyphOffsets;

    private TrueTypeFont(byte[] data, Dictionary<string, (int, int)> tables, Dictionary<int, ushort> cmap, ushort[] advances, int[] glyphOffsets)
    {
        _data = data;
        _tables = tables;
        _cmap = cmap;
        _advances = advances;
        _glyphOffsets = glyphOffsets;
    }

    public required string PostScriptName { get; init; }

    public required int UnitsPerEm { get; init; }

    public int GlyphCount => _advances.Length;

    public required double Ascent { get; init; }

    public required double Descent { get; init; }

    public required double CapHeight { get; init; }

    public required double ItalicAngle { get; init; }

    public required int[] BoundingBox { get; init; }

    public required bool IsFixedPitch { get; init; }

    public required int WeightClass { get; init; }

    /// <summary>The advance width of glyph 0 in 1/1000 em: used for characters the font lacks.</summary>
    public double DefaultWidth => Scale(_advances.Length > 0 ? _advances[0] : 0);

    public static TrueTypeFont? TryLoad(string path)
    {
        try
        {
            return Parse(File.ReadAllBytes(path), requireCmap: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                                              or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>Parses font data; a font program without a character map (such as a subset) is accepted when <paramref name="requireCmap"/> is false.</summary>
    internal static TrueTypeFont? TryParse(byte[] data, bool requireCmap = true)
    {
        try
        {
            return Parse(data, requireCmap);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>The glyph for a code point, or 0 (.notdef) when the font has none.</summary>
    public ushort GlyphFor(int codePoint) => _cmap.TryGetValue(codePoint, out var glyph) ? glyph : (ushort)0;

    public bool HasGlyph(int codePoint) => _cmap.ContainsKey(codePoint);

    /// <summary>Advance width of a code point in 1/1000 em; characters without a glyph use <see cref="DefaultWidth"/>.</summary>
    public double CharWidth(int codePoint) => _cmap.TryGetValue(codePoint, out var glyph) ? GlyphWidth(glyph) : DefaultWidth;

    public double GlyphWidth(ushort glyph) => Scale(_advances[Math.Min(glyph, _advances.Length - 1)]);

    /// <summary>
    /// A font program holding only <paramref name="glyphs"/> (plus .notdef and the components of
    /// composite glyphs). Glyph ids are kept, so the subset is addressed with the original ids.
    /// </summary>
    public byte[] Subset(IEnumerable<ushort> glyphs)
    {
        var keep = new SortedSet<int> { 0 };
        var pending = new Stack<int>(glyphs.Select(g => (int)g));
        var glyf = _tables["glyf"];
        while (pending.Count > 0)
        {
            var glyph = pending.Pop();
            if (glyph >= GlyphCount || !keep.Add(glyph) && glyph != 0)
            {
                continue;
            }

            foreach (var component in CompositeComponents(glyf.Offset, glyph))
            {
                if (!keep.Contains(component))
                {
                    pending.Push(component);
                }
            }
        }

        // New glyf/loca: kept glyphs carry their outline, every other glyph is empty.
        using var glyfOut = new MemoryStream();
        var loca = new byte[(GlyphCount + 1) * 4];
        for (var glyph = 0; glyph < GlyphCount; glyph++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(glyph * 4), (uint)glyfOut.Length);
            if (!keep.Contains(glyph))
            {
                continue;
            }

            var start = _glyphOffsets[glyph];
            var end = _glyphOffsets[glyph + 1];
            if (end > start)
            {
                glyfOut.Write(_data, glyf.Offset + start, end - start);
                while (glyfOut.Length % 4 != 0)
                {
                    glyfOut.WriteByte(0);
                }
            }
        }

        BinaryPrimitives.WriteUInt32BigEndian(loca.AsSpan(GlyphCount * 4), (uint)glyfOut.Length);

        var head = Table("head").ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(8), 0); // checkSumAdjustment, set below
        BinaryPrimitives.WriteInt16BigEndian(head.AsSpan(50), 1); // indexToLocFormat: long offsets

        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["head"] = head,
            ["hhea"] = Table("hhea").ToArray(),
            ["maxp"] = Table("maxp").ToArray(),
            ["hmtx"] = Table("hmtx").ToArray(),
            ["loca"] = loca,
            ["glyf"] = glyfOut.ToArray(),
        };
        foreach (var optional in (string[])["cvt ", "fpgm", "prep"])
        {
            if (_tables.ContainsKey(optional))
            {
                tables[optional] = Table(optional).ToArray();
            }
        }

        return WriteFont(tables);
    }

    private double Scale(int units) => units * 1000.0 / UnitsPerEm;

    private ReadOnlySpan<byte> Table(string tag)
    {
        var (offset, length) = _tables[tag];
        return _data.AsSpan(offset, length);
    }

    private IEnumerable<int> CompositeComponents(int glyfOffset, int glyph)
    {
        var start = _glyphOffsets[glyph];
        var end = _glyphOffsets[glyph + 1];
        if (end - start < 10)
        {
            yield break;
        }

        var position = glyfOffset + start;
        var contours = BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(position));
        if (contours >= 0)
        {
            yield break;
        }

        position += 10;
        var limit = glyfOffset + end;
        while (position + 4 <= limit)
        {
            var flags = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(position));
            var component = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(position + 2));
            yield return component;
            position += 4 + ((flags & 0x0001) != 0 ? 4 : 2);
            if ((flags & 0x0008) != 0)
            {
                position += 2;
            }
            else if ((flags & 0x0040) != 0)
            {
                position += 4;
            }
            else if ((flags & 0x0080) != 0)
            {
                position += 8;
            }

            if ((flags & 0x0020) == 0)
            {
                break;
            }
        }
    }

    private static byte[] WriteFont(SortedDictionary<string, byte[]> tables)
    {
        var count = tables.Count;
        var searchRange = 1;
        var entrySelector = 0;
        while (searchRange * 2 <= count)
        {
            searchRange *= 2;
            entrySelector++;
        }

        searchRange *= 16;
        var headerLength = 12 + (16 * count);
        using var output = new MemoryStream();
        var header = new byte[headerLength];
        BinaryPrimitives.WriteUInt32BigEndian(header, 0x00010000);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), (ushort)count);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(10), (ushort)((count * 16) - searchRange));
        output.Write(header);

        var offset = headerLength;
        var index = 0;
        var headOffset = 0;
        foreach (var (tag, bytes) in tables)
        {
            var entry = header.AsSpan(12 + (16 * index));
            Encoding.ASCII.GetBytes(tag, entry);
            BinaryPrimitives.WriteUInt32BigEndian(entry[4..], Checksum(bytes));
            BinaryPrimitives.WriteUInt32BigEndian(entry[8..], (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(entry[12..], (uint)bytes.Length);
            if (tag == "head")
            {
                headOffset = offset;
            }

            output.Write(bytes);
            var padding = (4 - (bytes.Length % 4)) % 4;
            output.Write(new byte[padding]);
            offset += bytes.Length + padding;
            index++;
        }

        var font = output.ToArray();
        header.CopyTo(font, 0);
        var adjustment = unchecked(0xB1B0AFBA - Checksum(font));
        BinaryPrimitives.WriteUInt32BigEndian(font.AsSpan(headOffset + 8), adjustment);
        return font;
    }

    private static uint Checksum(ReadOnlySpan<byte> bytes)
    {
        uint sum = 0;
        for (var i = 0; i < bytes.Length; i += 4)
        {
            uint word = 0;
            for (var j = 0; j < 4; j++)
            {
                word = (word << 8) | (i + j < bytes.Length ? bytes[i + j] : 0u);
            }

            sum = unchecked(sum + word);
        }

        return sum;
    }

    private static TrueTypeFont Parse(byte[] data, bool requireCmap)
    {
        var span = data.AsSpan();
        var version = BinaryPrimitives.ReadUInt32BigEndian(span);
        if (version != 0x00010000 && version != 0x74727565)
        {
            throw new InvalidDataException("Not a TrueType font");
        }

        var tableCount = BinaryPrimitives.ReadUInt16BigEndian(span[4..]);
        var tables = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        for (var i = 0; i < tableCount; i++)
        {
            var record = span.Slice(12 + (16 * i), 16);
            var tag = Encoding.ASCII.GetString(record[..4]);
            var offset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(record[8..]));
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(record[12..]));
            if (offset < 0 || length < 0 || offset > data.Length - length)
            {
                throw new InvalidDataException($"Table {tag} lies outside the file");
            }

            tables[tag] = (offset, length);
        }

        foreach (var required in (string[])["head", "hhea", "maxp", "hmtx", "loca", "glyf"])
        {
            if (!tables.ContainsKey(required))
            {
                throw new InvalidDataException($"Missing {required} table");
            }
        }

        var head = Slice(data, tables["head"]);
        var unitsPerEm = BinaryPrimitives.ReadUInt16BigEndian(head[18..]);
        if (unitsPerEm == 0)
        {
            throw new InvalidDataException("unitsPerEm is zero");
        }

        int[] bbox =
        [
            BinaryPrimitives.ReadInt16BigEndian(head[36..]), BinaryPrimitives.ReadInt16BigEndian(head[38..]),
            BinaryPrimitives.ReadInt16BigEndian(head[40..]), BinaryPrimitives.ReadInt16BigEndian(head[42..]),
        ];
        var indexToLocFormat = BinaryPrimitives.ReadInt16BigEndian(head[50..]);

        var maxp = Slice(data, tables["maxp"]);
        var glyphCount = BinaryPrimitives.ReadUInt16BigEndian(maxp[4..]);

        var hhea = Slice(data, tables["hhea"]);
        var ascender = BinaryPrimitives.ReadInt16BigEndian(hhea[4..]);
        var descender = BinaryPrimitives.ReadInt16BigEndian(hhea[6..]);
        var metricCount = BinaryPrimitives.ReadUInt16BigEndian(hhea[34..]);

        var hmtx = Slice(data, tables["hmtx"]);
        var advances = new ushort[glyphCount];
        ushort last = 0;
        for (var glyph = 0; glyph < glyphCount; glyph++)
        {
            if (glyph < metricCount)
            {
                last = BinaryPrimitives.ReadUInt16BigEndian(hmtx[(glyph * 4)..]);
            }

            advances[glyph] = last;
        }

        var loca = Slice(data, tables["loca"]);
        var glyphOffsets = new int[glyphCount + 1];
        for (var glyph = 0; glyph <= glyphCount; glyph++)
        {
            glyphOffsets[glyph] = indexToLocFormat == 0
                ? BinaryPrimitives.ReadUInt16BigEndian(loca[(glyph * 2)..]) * 2
                : checked((int)BinaryPrimitives.ReadUInt32BigEndian(loca[(glyph * 4)..]));
        }

        var glyfLength = tables["glyf"].Item2;
        for (var glyph = 0; glyph < glyphCount; glyph++)
        {
            if (glyphOffsets[glyph] > glyphOffsets[glyph + 1] || glyphOffsets[glyph + 1] > glyfLength)
            {
                throw new InvalidDataException("Invalid loca table");
            }
        }

        if (requireCmap && !tables.ContainsKey("cmap"))
        {
            throw new InvalidDataException("Missing cmap table");
        }

        var cmap = tables.TryGetValue("cmap", out var cmapRange) ? ParseCmap(Slice(data, cmapRange), glyphCount) : [];
        if (cmap.TryGetValue(0x20, out var space))
        {
            cmap[0xA0] = space;
        }
        else if (cmap.TryGetValue(0xA0, out var nbsp))
        {
            cmap[0x20] = nbsp;
        }

        double capHeight = ascender;
        var weightClass = 400;
        if (tables.TryGetValue("OS/2", out var os2Range))
        {
            var os2 = Slice(data, os2Range);
            weightClass = BinaryPrimitives.ReadUInt16BigEndian(os2[4..]);
            var os2Version = BinaryPrimitives.ReadUInt16BigEndian(os2);
            if (os2Version >= 2 && os2.Length >= 90)
            {
                capHeight = BinaryPrimitives.ReadInt16BigEndian(os2[88..]);
            }
        }

        double italicAngle = 0;
        var fixedPitch = false;
        if (tables.TryGetValue("post", out var postRange))
        {
            var post = Slice(data, postRange);
            italicAngle = BinaryPrimitives.ReadInt32BigEndian(post[4..]) / 65536.0;
            fixedPitch = BinaryPrimitives.ReadUInt32BigEndian(post[12..]) != 0;
        }

        var name = tables.TryGetValue("name", out var nameRange) ? ReadPostScriptName(Slice(data, nameRange)) : null;
        name = SanitizeName(name ?? "Font");

        double ToThousandths(int units) => units * 1000.0 / unitsPerEm;
        return new TrueTypeFont(data, tables, cmap, advances, glyphOffsets)
        {
            PostScriptName = name,
            UnitsPerEm = unitsPerEm,
            Ascent = ToThousandths(ascender),
            Descent = ToThousandths(descender),
            CapHeight = ToThousandths((int)capHeight),
            ItalicAngle = italicAngle,
            BoundingBox = [.. bbox.Select(v => (int)Math.Round(ToThousandths(v)))],
            IsFixedPitch = fixedPitch,
            WeightClass = weightClass,
        };
    }

    private static ReadOnlySpan<byte> Slice(byte[] data, (int Offset, int Length) range) => data.AsSpan(range.Offset, range.Length);

    private static Dictionary<int, ushort> ParseCmap(ReadOnlySpan<byte> cmap, int glyphCount)
    {
        var subtableCount = BinaryPrimitives.ReadUInt16BigEndian(cmap[2..]);
        var best = -1;
        var bestRank = int.MaxValue;
        for (var i = 0; i < subtableCount; i++)
        {
            var record = cmap.Slice(4 + (8 * i), 8);
            var platform = BinaryPrimitives.ReadUInt16BigEndian(record);
            var encoding = BinaryPrimitives.ReadUInt16BigEndian(record[2..]);
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(record[4..]);
            var rank = (platform, encoding) switch
            {
                (3, 10) => 0,
                (0, 4) or (0, 6) => 1,
                (3, 1) => 2,
                (0, _) => 3,
                _ => int.MaxValue,
            };
            if (rank < bestRank && offset >= 0 && offset < cmap.Length)
            {
                bestRank = rank;
                best = offset;
            }
        }

        if (best < 0)
        {
            throw new InvalidDataException("No Unicode cmap");
        }

        var table = cmap[best..];
        var format = BinaryPrimitives.ReadUInt16BigEndian(table);
        var map = new Dictionary<int, ushort>();
        switch (format)
        {
            case 4:
            {
                var segments = BinaryPrimitives.ReadUInt16BigEndian(table[6..]) / 2;
                var ends = 14;
                var starts = ends + (2 * segments) + 2;
                var deltas = starts + (2 * segments);
                var rangeOffsets = deltas + (2 * segments);
                for (var s = 0; s < segments; s++)
                {
                    var end = BinaryPrimitives.ReadUInt16BigEndian(table[(ends + (2 * s))..]);
                    var start = BinaryPrimitives.ReadUInt16BigEndian(table[(starts + (2 * s))..]);
                    var delta = BinaryPrimitives.ReadInt16BigEndian(table[(deltas + (2 * s))..]);
                    var rangeOffset = BinaryPrimitives.ReadUInt16BigEndian(table[(rangeOffsets + (2 * s))..]);
                    for (int c = start; c <= end && c != 0xFFFF; c++)
                    {
                        int glyph;
                        if (rangeOffset == 0)
                        {
                            glyph = (c + delta) & 0xFFFF;
                        }
                        else
                        {
                            var at = rangeOffsets + (2 * s) + rangeOffset + (2 * (c - start));
                            glyph = BinaryPrimitives.ReadUInt16BigEndian(table[at..]);
                            if (glyph != 0)
                            {
                                glyph = (glyph + delta) & 0xFFFF;
                            }
                        }

                        if (glyph != 0 && glyph < glyphCount)
                        {
                            map[c] = (ushort)glyph;
                        }

                    }
                }

                break;
            }

            case 12:
            {
                var groups = BinaryPrimitives.ReadUInt32BigEndian(table[12..]);
                for (var g = 0; g < groups; g++)
                {
                    var group = table.Slice(16 + (12 * g), 12);
                    var start = BinaryPrimitives.ReadUInt32BigEndian(group);
                    var end = BinaryPrimitives.ReadUInt32BigEndian(group[4..]);
                    var glyph = BinaryPrimitives.ReadUInt32BigEndian(group[8..]);
                    for (var c = start; c <= end && c <= 0x10FFFF; c++)
                    {
                        var id = glyph + (c - start);
                        if (id < glyphCount && id != 0)
                        {
                            map[(int)c] = (ushort)id;
                        }
                    }
                }

                break;
            }

            case 0:
                for (var c = 0; c < 256; c++)
                {
                    var glyph = table[6 + c];
                    if (glyph != 0 && glyph < glyphCount)
                    {
                        map[c] = glyph;
                    }
                }

                break;
            default:
                throw new InvalidDataException($"Unsupported cmap format {format}");
        }

        return map;
    }

    private static string? ReadPostScriptName(ReadOnlySpan<byte> name)
    {
        var count = BinaryPrimitives.ReadUInt16BigEndian(name[2..]);
        var storage = BinaryPrimitives.ReadUInt16BigEndian(name[4..]);
        for (var i = 0; i < count; i++)
        {
            var record = name.Slice(6 + (12 * i), 12);
            var platform = BinaryPrimitives.ReadUInt16BigEndian(record);
            var nameId = BinaryPrimitives.ReadUInt16BigEndian(record[6..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(record[8..]);
            var offset = BinaryPrimitives.ReadUInt16BigEndian(record[10..]);
            if (nameId != 6 || storage + offset + length > name.Length)
            {
                continue;
            }

            var bytes = name.Slice(storage + offset, length);
            return platform is 0 or 3 ? Encoding.BigEndianUnicode.GetString(bytes) : Encoding.Latin1.GetString(bytes);
        }

        return null;
    }

    private static string SanitizeName(string value)
    {
        var builder = new StringBuilder();
        foreach (var c in value)
        {
            if (c is > ' ' and < '\u007F' and not ('[' or ']' or '(' or ')' or '{' or '}' or '<' or '>' or '/' or '%' or '#'))
            {
                builder.Append(c);
            }
        }

        return builder.Length > 0 ? builder.ToString() : "Font";
    }
}
