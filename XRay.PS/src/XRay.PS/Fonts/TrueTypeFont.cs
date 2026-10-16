// Ported from ps/ttf_outline.py: TrueType glyph outlines for raster text rendering.

using XRay.PS.Internal;

namespace XRay.PS.Fonts;

/// <summary>A point of a TrueType glyph contour.</summary>
public readonly record struct GlyphPoint(double X, double Y, bool OnCurve);

/// <summary>A parsed TrueType font: cmap, metrics and glyph outlines.</summary>
public sealed class TrueTypeFont
{
    private readonly byte[] _data;
    private readonly Dictionary<string, long> _tables;
    private readonly int _indexToLocFormat;
    private readonly int _numGlyphs;
    private readonly int _numHMetrics;
    private readonly int _hheaAscent;
    private readonly int _hheaDescent;
    private readonly int _hheaLineGap;
    private readonly long _hmtxOffset;
    private readonly long _locaOffset;
    private readonly long _glyfOffset;
    private readonly Dictionary<int, int> _cmap;
    private readonly int? _os2TypoAsc;
    private readonly int? _os2TypoDesc;
    private readonly long? _vmtxOffset;
    private readonly int _numVMetrics;
    private long[]? _offsets;

    public TrueTypeFont(byte[] data)
    {
        _data = data;
        _tables = ParseTableDirectory(data);
        long head = Require("head");
        long maxp = Require("maxp");
        long hhea = Require("hhea");
        long hmtx = Require("hmtx");
        long cmap = Require("cmap");
        long loca = Require("loca");
        long glyf = Require("glyf");
        UnitsPerEm = BigEndian.U16(data, head + 18);
        _indexToLocFormat = BigEndian.I16(data, head + 50);
        _numGlyphs = BigEndian.U16(data, maxp + 4);
        _numHMetrics = BigEndian.U16(data, hhea + 34);
        _hheaAscent = BigEndian.I16(data, hhea + 4);
        _hheaDescent = BigEndian.I16(data, hhea + 6);
        _hheaLineGap = BigEndian.I16(data, hhea + 8);
        _hmtxOffset = hmtx;
        _locaOffset = loca;
        _glyfOffset = glyf;
        _cmap = ParseCmapTable(data, cmap);
        if (_tables.TryGetValue("OS/2", out long os2) && os2 + 78 <= data.Length)
        {
            _os2TypoAsc = BigEndian.I16(data, os2 + 68);
            _os2TypoDesc = BigEndian.I16(data, os2 + 70);
        }
        bool hasVhea = _tables.TryGetValue("vhea", out long vhea);
        if (_tables.TryGetValue("vmtx", out long vmtx))
            _vmtxOffset = vmtx;
        _numVMetrics = hasVhea && _vmtxOffset is not null ? BigEndian.U16(data, vhea + 34) : 0;
    }

    public int UnitsPerEm { get; }

    public int NumGlyphs => _numGlyphs;

    public byte[] Data => _data;

    public static TrueTypeFont Load(FontLocation path) => new(path.ReadAllBytes());

    private long Require(string tag)
    {
        if (!_tables.TryGetValue(tag, out long offset))
            throw Py.ValueError($"missing b'{tag}' table");
        return offset;
    }

    public int GlyphIdForCode(int code)
    {
        if (_cmap.TryGetValue(code, out int glyphId))
            return glyphId;
        // Symbolic cmaps (platform 3, encoding 0) are frequently encoded as U+F0xx private-use
        // values for 8-bit character codes.
        if (code is >= 0 and <= 0xFF)
        {
            if (_cmap.TryGetValue(0xF000 + code, out glyphId))
                return glyphId;
            if (_cmap.TryGetValue(0xF100 + code, out glyphId))
                return glyphId;
        }
        return 0;
    }

    public bool HasCode(int code) => _cmap.ContainsKey(code);

    public IReadOnlyDictionary<int, int> Cmap => _cmap;

    public double GlyphAdvance(int glyphId)
    {
        glyphId = Math.Max(0, Math.Min(glyphId, _numGlyphs - 1));
        if (glyphId < _numHMetrics)
            return BigEndian.U16(_data, _hmtxOffset + glyphId * 4L);
        long last = _hmtxOffset + (_numHMetrics - 1) * 4L;
        return BigEndian.U16(_data, last);
    }

    public double? GlyphVerticalAdvance(int glyphId)
    {
        if (_vmtxOffset is null || _numVMetrics <= 0)
            return null;
        glyphId = Math.Max(0, Math.Min(glyphId, _numGlyphs - 1));
        if (glyphId < _numVMetrics)
            return BigEndian.U16(_data, _vmtxOffset.Value + glyphId * 4L);
        long last = _vmtxOffset.Value + (_numVMetrics - 1) * 4L;
        return BigEndian.U16(_data, last);
    }

    public double LineSpacingUnits() => _hheaAscent - _hheaDescent + _hheaLineGap;

    public double TypoAscenderUnits() => _os2TypoAsc ?? _hheaAscent;

    public double GlyphTopOriginX(int glyphId) => GlyphAdvance(glyphId) * 0.5;

    public (double TopOriginY, double Descender) GlyphTopOriginYAndDescender(int glyphId)
    {
        var vm = GlyphVerticalMetrics(glyphId);
        int? yMax = GlyphYMax(glyphId);
        if (vm is not null && yMax is not null)
        {
            var (advanceH, topSideBearing) = vm.Value;
            double topOriginY = yMax.Value + topSideBearing;
            double descender = Math.Max(0.0, advanceH - topOriginY);
            return (topOriginY, descender);
        }
        if (_os2TypoAsc is not null && _os2TypoDesc is not null)
            return (_os2TypoAsc.Value, Math.Abs(_os2TypoDesc.Value));
        return (_hheaAscent, Math.Abs(_hheaDescent));
    }

    public double GlyphSidewaysAdvance(int glyphId)
    {
        var (top, descender) = GlyphTopOriginYAndDescender(glyphId);
        return Math.Max(0.0, top + descender);
    }

    public (double AdvanceH, double TopSideBearing)? GlyphVerticalMetrics(int glyphId)
    {
        if (_vmtxOffset is null || _numVMetrics <= 0)
            return null;
        glyphId = Math.Max(0, Math.Min(glyphId, _numGlyphs - 1));
        long vmtx = _vmtxOffset.Value;
        if (glyphId < _numVMetrics)
        {
            long offset = vmtx + glyphId * 4L;
            return (BigEndian.U16(_data, offset), BigEndian.I16(_data, offset + 2));
        }
        long advOff = vmtx + (_numVMetrics - 1) * 4L;
        double advanceH = BigEndian.U16(_data, advOff);
        long tsbOff = vmtx + _numVMetrics * 4L + (glyphId - _numVMetrics) * 2L;
        if (tsbOff + 2 > _data.Length)
            return (advanceH, 0.0);
        return (advanceH, BigEndian.I16(_data, tsbOff));
    }

    public int? GlyphYMax(int glyphId)
    {
        var offsets = GlyphOffsets();
        if (glyphId >= offsets.Length - 1)
            return null;
        long start = offsets[glyphId];
        long end = offsets[glyphId + 1];
        if (start == end)
            return null;
        long header = _glyfOffset + start;
        if (header + 10 > _glyfOffset + end || header + 10 > _data.Length)
            return null;
        return BigEndian.I16(_data, header + 8);
    }

    /// <summary>The contours of a glyph in font units (composites flattened).</summary>
    public List<List<GlyphPoint>> GlyphOutline(int glyphId) => GlyphOutline(glyphId, []);

    private List<List<GlyphPoint>> GlyphOutline(int glyphId, HashSet<int> visiting)
    {
        var offsets = GlyphOffsets();
        if (glyphId < 0 || glyphId >= offsets.Length - 1)
            return [];
        long start = offsets[glyphId];
        long end = offsets[glyphId + 1];
        if (start == end)
            return [];
        return ParseGlyph(_glyfOffset + start, _glyfOffset + end, visiting);
    }

    private long[] GlyphOffsets()
    {
        if (_offsets is not null)
            return _offsets;
        int count = _numGlyphs + 1;
        var offsets = new long[count];
        if (_indexToLocFormat == 0)
        {
            for (int i = 0; i < count; i++)
                offsets[i] = BigEndian.U16(_data, _locaOffset + i * 2L) * 2L;
        }
        else
        {
            for (int i = 0; i < count; i++)
                offsets[i] = BigEndian.U32(_data, _locaOffset + i * 4L);
        }
        return _offsets = offsets;
    }

    private List<List<GlyphPoint>> ParseGlyph(long offset, long glyphEnd, HashSet<int> visiting)
    {
        if (offset + 10 > glyphEnd)
            return [];
        int numberOfContours = BigEndian.I16(_data, offset);
        if (numberOfContours == 0)
            return [];
        if (numberOfContours > 0)
            return ParseSimpleGlyph(offset, glyphEnd, numberOfContours);
        return ParseCompositeGlyph(offset, glyphEnd, visiting);
    }

    private List<List<GlyphPoint>> ParseSimpleGlyph(long offset, long glyphEnd, int contourCount)
    {
        byte[] data = _data;
        long endPtsOffset = offset + 10;
        if (endPtsOffset + contourCount * 2L + 2 > glyphEnd)
            return [];
        var endPts = new List<int>(contourCount);
        for (int i = 0; i < contourCount; i++)
            endPts.Add(BigEndian.U16(data, endPtsOffset + i * 2L));
        int instructionLength = BigEndian.U16(data, endPtsOffset + contourCount * 2L);
        long pointsOffset = endPtsOffset + contourCount * 2L + 2 + instructionLength;
        if (pointsOffset > glyphEnd)
            return [];
        long pointCount = endPts.Count > 0 ? endPts[^1] + 1 : 0;
        long remainingBytes = Math.Max(0, glyphEnd - pointsOffset);
        long maxPointsLimit = remainingBytes * 4;
        if (maxPointsLimit == 0)
            return [];
        if (pointCount > maxPointsLimit)
            pointCount = maxPointsLimit;

        var flags = new List<int>((int)pointCount);
        long i2 = 0;
        while (i2 < pointCount)
        {
            if (pointsOffset >= glyphEnd)
                break;
            int flag = Byte(pointsOffset);
            pointsOffset++;
            flags.Add(flag);
            i2++;
            if ((flag & 0x08) != 0)
            {
                if (pointsOffset >= glyphEnd)
                    break;
                long repeat = Byte(pointsOffset);
                pointsOffset++;
                long remaining = pointCount - i2;
                if (repeat > remaining)
                    repeat = remaining;
                for (long r = 0; r < repeat; r++)
                    flags.Add(flag);
                i2 += repeat;
            }
        }
        if (flags.Count > pointCount)
            flags.RemoveRange((int)pointCount, flags.Count - (int)pointCount);

        var xs = new List<int>(flags.Count);
        var ys = new List<int>(flags.Count);
        int x = 0;
        foreach (int flag in flags)
        {
            if (pointsOffset >= glyphEnd)
                break;
            if ((flag & 0x02) != 0)
            {
                int dx = Byte(pointsOffset);
                pointsOffset++;
                x += (flag & 0x10) != 0 ? dx : -dx;
            }
            else
            {
                int dx;
                if ((flag & 0x10) != 0)
                {
                    dx = 0;
                }
                else
                {
                    if (pointsOffset + 2 > glyphEnd)
                        break;
                    dx = BigEndian.I16(data, pointsOffset);
                    pointsOffset += 2;
                }
                x += dx;
            }
            xs.Add(x);
        }
        int y = 0;
        foreach (int flag in flags)
        {
            if (pointsOffset >= glyphEnd)
                break;
            if ((flag & 0x04) != 0)
            {
                int dy = Byte(pointsOffset);
                pointsOffset++;
                y += (flag & 0x20) != 0 ? dy : -dy;
            }
            else
            {
                int dy;
                if ((flag & 0x20) != 0)
                {
                    dy = 0;
                }
                else
                {
                    if (pointsOffset + 2 > glyphEnd)
                        break;
                    dy = BigEndian.I16(data, pointsOffset);
                    pointsOffset += 2;
                }
                y += dy;
            }
            ys.Add(y);
        }
        if (flags.Count == 0 || xs.Count == 0 || ys.Count == 0)
            return [];
        int maxIndex = Math.Min(flags.Count, Math.Min(xs.Count, ys.Count)) - 1;
        if (maxIndex < 0)
            return [];
        var ends = endPts.Where(e => e >= 0).Select(e => Math.Min(e, maxIndex)).ToList();
        if (ends.Count == 0)
            return [];
        var contours = new List<List<GlyphPoint>>(ends.Count);
        int start = 0;
        foreach (int end in ends)
        {
            var contour = new List<GlyphPoint>(Math.Max(0, end - start + 1));
            for (int i = start; i <= end; i++)
                contour.Add(new GlyphPoint(xs[i], ys[i], (flags[i] & 0x01) != 0));
            contours.Add(contour);
            start = end + 1;
        }
        return contours;
    }

    private int Byte(long offset)
    {
        if (offset < 0 || offset >= _data.Length)
            throw Py.IndexError("index out of range");
        return _data[offset];
    }

    private List<List<GlyphPoint>> ParseCompositeGlyph(long offset, long glyphEnd, HashSet<int> visiting)
    {
        var contours = new List<List<GlyphPoint>>();
        offset += 10;
        int flags = 0x20;
        while ((flags & 0x20) != 0)
        {
            if (offset + 4 > glyphEnd)
                return contours;
            flags = BigEndian.U16(_data, offset);
            int glyphIndex = BigEndian.U16(_data, offset + 2);
            offset += 4;
            int arg1, arg2;
            if ((flags & 0x0001) != 0)
            {
                if (offset + 4 > glyphEnd)
                    return contours;
                arg1 = BigEndian.I16(_data, offset);
                arg2 = BigEndian.I16(_data, offset + 2);
                offset += 4;
            }
            else
            {
                if (offset + 2 > glyphEnd)
                    return contours;
                arg1 = BigEndian.I8(_data, offset);
                arg2 = BigEndian.I8(_data, offset + 1);
                offset += 2;
            }
            int dx = (flags & 0x0002) != 0 ? arg1 : 0;
            int dy = (flags & 0x0002) != 0 ? arg2 : 0;
            double a = 1.0, d = 1.0, b = 0.0, c = 0.0;
            if ((flags & 0x0008) != 0)
            {
                if (offset + 2 > glyphEnd)
                    return contours;
                a = d = F2Dot14(offset);
                offset += 2;
            }
            else if ((flags & 0x0040) != 0)
            {
                if (offset + 4 > glyphEnd)
                    return contours;
                a = F2Dot14(offset);
                d = F2Dot14(offset + 2);
                offset += 4;
            }
            else if ((flags & 0x0080) != 0)
            {
                if (offset + 8 > glyphEnd)
                    return contours;
                a = F2Dot14(offset);
                b = F2Dot14(offset + 2);
                c = F2Dot14(offset + 4);
                d = F2Dot14(offset + 6);
                offset += 8;
            }
            if (visiting.Contains(glyphIndex))
                continue;
            visiting.Add(glyphIndex);
            var component = GlyphOutline(glyphIndex, visiting);
            visiting.Remove(glyphIndex);
            foreach (var contour in component)
            {
                var transformed = new List<GlyphPoint>(contour.Count);
                foreach (var pt in contour)
                    transformed.Add(new GlyphPoint(a * pt.X + c * pt.Y + dx, b * pt.X + d * pt.Y + dy, pt.OnCurve));
                contours.Add(transformed);
            }
        }
        return contours;
    }

    private double F2Dot14(long offset) => BigEndian.I16(_data, offset) / 16384.0;

    private static Dictionary<string, long> ParseTableDirectory(byte[] data)
    {
        int numTables = BigEndian.U16(data, 4);
        long offset = 12;
        var tables = new Dictionary<string, long>(StringComparer.Ordinal);
        for (int i = 0; i < numTables; i++)
        {
            tables[BigEndian.Tag(data, offset)] = BigEndian.U32(data, offset + 8);
            offset += 16;
        }
        return tables;
    }

    private static Dictionary<int, int> ParseCmapTable(byte[] data, long offset)
    {
        int version = BigEndian.U16(data, offset);
        if (version != 0)
            return [];
        int numTables = BigEndian.U16(data, offset + 2);
        long? symbol = null, unicodeBmp = null, unicodeFull = null, platform0 = null;
        for (int i = 0; i < numTables; i++)
        {
            long record = offset + 4 + i * 8L;
            int platformId = BigEndian.U16(data, record);
            int encodingId = BigEndian.U16(data, record + 2);
            long candidate = offset + BigEndian.U32(data, record + 4);
            if (platformId == 3 && encodingId == 10)
                unicodeFull = candidate;
            else if (platformId == 3 && encodingId == 1)
                unicodeBmp = candidate;
            else if (platformId == 3 && encodingId == 0)
                symbol = candidate;
            else if (platformId == 0 && platform0 is null)
                platform0 = candidate;
        }
        // `a or b or c` in the original: an offset of 0 counts as missing.
        long? chosen = Truthy(unicodeFull) ?? Truthy(unicodeBmp) ?? Truthy(symbol) ?? platform0;
        if (chosen is null)
            return [];
        int fmt = BigEndian.U16(data, chosen.Value);
        if (fmt == 4)
            return TtfMetrics.CmapFormat4(data, chosen.Value);
        if (fmt == 12)
            return TtfMetrics.CmapFormat12(data, chosen.Value);
        return [];
    }

    private static long? Truthy(long? value) => value is null or 0 ? null : value;
}
