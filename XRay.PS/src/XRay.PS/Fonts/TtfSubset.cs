// Ported from ps/ttf_subset.py: TrueType subsetting for PDF embedding.

using System.Buffers.Binary;
using XRay.PS.Internal;
using XRay.PS.PostScript;

namespace XRay.PS.Fonts;

/// <summary>Subsets TrueType fonts for embedding: keeps the used glyphs (and their composite
/// components) and writes a format 4 cmap for the PDF codes.</summary>
public static class TtfSubset
{
    private sealed record TableRecord(string Tag, uint Checksum, long Offset, long Length);

    /// <summary>Subsets <paramref name="data"/>; returns the font and the new code-to-glyph map.</summary>
    public static (byte[] Font, Dictionary<int, int> CodeToGid) Subset(byte[] data, ISet<int> usedCodes,
        IReadOnlyDictionary<int, int>? codeRemap = null, IReadOnlyDictionary<int, int>? explicitCodeToGid = null)
    {
        if (data.Length == 0)
            throw new PsTypeError("empty TrueType data");
        var records = ReadTableDirectory(data);
        var tableMap = new Dictionary<string, TableRecord>(StringComparer.Ordinal);
        foreach (var rec in records)
            tableMap[rec.Tag] = rec;
        foreach (string tag in new[] { "head", "hhea", "maxp", "hmtx", "loca", "glyf", "cmap" })
        {
            if (!tableMap.ContainsKey(tag))
                throw new PsTypeError("missing TrueType tables");
        }
        var head = tableMap["head"];
        var hhea = tableMap["hhea"];
        var maxp = tableMap["maxp"];
        var hmtx = tableMap["hmtx"];
        var loca = tableMap["loca"];
        var glyf = tableMap["glyf"];
        var cmap = tableMap["cmap"];

        byte[] headData = SliceTable(data, head);
        byte[] hheaData = SliceTable(data, hhea);
        byte[] maxpData = SliceTable(data, maxp);
        if (headData.Length < 54 || hheaData.Length < 36 || maxpData.Length < 6)
            throw new PsTypeError("invalid TrueType tables");

        int indexToLocFormat = BigEndian.I16(headData, 50);
        int numGlyphs = BigEndian.U16(maxpData, 4);
        int numHMetrics = BigEndian.U16(hheaData, 34);
        var offsets = ReadLoca(data, loca.Offset, indexToLocFormat, numGlyphs);
        var cmapMap = ParseCmapTable(data, cmap.Offset);

        var codeToGid = new SortedDictionary<int, int>();
        if (explicitCodeToGid is not null)
        {
            foreach (int code in explicitCodeToGid.Keys.Where(c => c is >= 0 and <= 0xFFFF).OrderBy(c => c))
                codeToGid[code] = explicitCodeToGid.GetValueOrDefault(code, 0);
        }
        else if (codeRemap is { Count: > 0 })
        {
            foreach (int code in codeRemap.Keys.Where(c => c is >= 0 and <= 0xFFFF).OrderBy(c => c))
            {
                int src = codeRemap.GetValueOrDefault(code, code);
                codeToGid[code] = cmapMap.GetValueOrDefault(src, 0);
            }
        }
        else
        {
            foreach (int code in usedCodes.Where(c => c is >= 0 and <= 0xFFFF).OrderBy(c => c))
                codeToGid[code] = cmapMap.GetValueOrDefault(code, 0);
        }

        var glyphIds = new HashSet<int> { 0 };
        foreach (int gid in codeToGid.Values)
            glyphIds.Add(gid);
        glyphIds = ExpandCompositeGlyphs(data, glyf.Offset, offsets, glyphIds);
        var orderedGlyphs = glyphIds.OrderBy(g => g).ToList();
        var gidMap = new Dictionary<int, int>();
        for (int i = 0; i < orderedGlyphs.Count; i++)
            gidMap[orderedGlyphs[i]] = i;
        var newCodeToGid = new Dictionary<int, int>();
        foreach (var (code, gid) in codeToGid)
            newCodeToGid[code] = gidMap.GetValueOrDefault(gid, 0);

        var (newGlyf, newOffsets) = SubsetGlyf(data, glyf.Offset, offsets, orderedGlyphs, gidMap);
        var hmtxMetrics = ReadHmtx(data, hmtx.Offset, numHMetrics, numGlyphs);
        byte[] newHmtx = BuildHmtx(hmtxMetrics, orderedGlyphs);

        int newNumGlyphs = orderedGlyphs.Count;
        BinaryPrimitives.WriteUInt16BigEndian(maxpData.AsSpan(4), checked((ushort)newNumGlyphs));
        BinaryPrimitives.WriteUInt16BigEndian(hheaData.AsSpan(34), checked((ushort)newNumGlyphs));
        headData.AsSpan(8, 4).Clear();
        int newLocaFormat = indexToLocFormat;
        if (newLocaFormat == 0 && newOffsets[^1] / 2 > 0xFFFF)
            newLocaFormat = 1;
        BinaryPrimitives.WriteInt16BigEndian(headData.AsSpan(50), (short)newLocaFormat);
        byte[] newLoca;
        if (newLocaFormat == 0)
        {
            newLoca = new byte[newOffsets.Count * 2];
            for (int i = 0; i < newOffsets.Count; i++)
                BinaryPrimitives.WriteUInt16BigEndian(newLoca.AsSpan(i * 2), checked((ushort)(newOffsets[i] / 2)));
        }
        else
        {
            newLoca = new byte[newOffsets.Count * 4];
            for (int i = 0; i < newOffsets.Count; i++)
                BinaryPrimitives.WriteUInt32BigEndian(newLoca.AsSpan(i * 4), checked((uint)newOffsets[i]));
        }
        byte[] newCmap = BuildCmapTable(newCodeToGid);
        var updated = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["head"] = headData,
            ["hhea"] = hheaData,
            ["maxp"] = maxpData,
            ["hmtx"] = newHmtx,
            ["loca"] = newLoca,
            ["glyf"] = newGlyf,
            ["cmap"] = newCmap,
        };
        var tableData = records.Select(r => (r.Tag, updated.TryGetValue(r.Tag, out var blob) ? blob : SliceTable(data, r))).ToList();
        return (RebuildFont(data, tableData), newCodeToGid);
    }

    public static bool HasTable(byte[] data, string tag)
    {
        if (tag.Length != 4)
            return false;
        try
        {
            return ReadTableDirectory(data).Any(r => r.Tag == tag);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Returns a font that contains a cmap table, synthesizing one when missing.</summary>
    public static byte[] EnsureCmap(byte[] data, IReadOnlyDictionary<int, int> codeToGid)
    {
        if (HasTable(data, "cmap"))
            return data;
        var records = ReadTableDirectory(data);
        if (records.Count == 0)
            throw new PsTypeError("invalid TrueType data");
        var cmapMap = new Dictionary<int, int>();
        foreach (var (code, gid) in codeToGid)
        {
            if (code is >= 0 and <= 0xFFFF && gid >= 0)
                cmapMap[code] = gid;
        }
        if (cmapMap.Count == 0)
            throw new PsTypeError("cannot synthesize cmap without code map");
        var tableData = new List<(string, byte[])>();
        bool inserted = false;
        foreach (var record in records)
        {
            if (!inserted && string.CompareOrdinal(record.Tag, "cmap") > 0)
            {
                tableData.Add(("cmap", BuildCmapTable(cmapMap)));
                inserted = true;
            }
            tableData.Add((record.Tag, SliceTable(data, record)));
        }
        if (!inserted)
            tableData.Add(("cmap", BuildCmapTable(cmapMap)));
        return RebuildFont(data, tableData);
    }

    private static List<TableRecord> ReadTableDirectory(byte[] data)
    {
        if (data.Length < 12)
            throw new PsTypeError("invalid TrueType data");
        int numTables = BigEndian.U16(data, 4);
        long offset = 12;
        var records = new List<TableRecord>(numTables);
        for (int i = 0; i < numTables; i++)
        {
            string tag = BigEndian.Tag(data, offset);
            uint checksum = (uint)BigEndian.U32(data, offset + 4);
            long tableOffset = BigEndian.U32(data, offset + 8);
            long length = BigEndian.U32(data, offset + 12);
            records.Add(new TableRecord(tag, checksum, tableOffset, length));
            offset += 16;
        }
        return records;
    }

    private static byte[] RebuildFont(byte[] data, List<(string Tag, byte[] Blob)> tableData)
    {
        int numTables = tableData.Count;
        int searchRange = (int)Math.Pow(2, Math.Floor(Math.Log(numTables) / Math.Log(2)));
        searchRange *= 16;
        int entrySelector = searchRange != 0 ? (int)(Math.Log(searchRange / 16.0) / Math.Log(2)) : 0;
        int rangeShift = numTables * 16 - searchRange;
        var records = new List<TableRecord>();
        long offset = 12 + numTables * 16L;
        var blobs = new List<(long Offset, byte[] Blob)>();
        foreach (var (tag, blob) in tableData)
        {
            offset = Align4(offset);
            records.Add(new TableRecord(tag, CalcChecksum(blob), offset, blob.Length));
            blobs.Add((offset, blob));
            offset += blob.Length;
        }
        var fontBytes = new byte[Align4(offset)];
        data.AsSpan(0, Math.Min(4, data.Length)).CopyTo(fontBytes);
        BinaryPrimitives.WriteUInt16BigEndian(fontBytes.AsSpan(4), (ushort)numTables);
        BinaryPrimitives.WriteUInt16BigEndian(fontBytes.AsSpan(6), (ushort)searchRange);
        BinaryPrimitives.WriteUInt16BigEndian(fontBytes.AsSpan(8), (ushort)entrySelector);
        BinaryPrimitives.WriteUInt16BigEndian(fontBytes.AsSpan(10), (ushort)rangeShift);
        int pos = 12;
        foreach (var record in records)
        {
            for (int i = 0; i < 4; i++)
                fontBytes[pos + i] = i < record.Tag.Length ? (byte)record.Tag[i] : (byte)0;
            BinaryPrimitives.WriteUInt32BigEndian(fontBytes.AsSpan(pos + 4), record.Checksum);
            BinaryPrimitives.WriteUInt32BigEndian(fontBytes.AsSpan(pos + 8), (uint)record.Offset);
            BinaryPrimitives.WriteUInt32BigEndian(fontBytes.AsSpan(pos + 12), (uint)record.Length);
            pos += 16;
        }
        foreach (var (tableOffset, blob) in blobs)
            blob.CopyTo(fontBytes, tableOffset);
        UpdateChecksumAdjustment(fontBytes, records);
        return fontBytes;
    }

    private static byte[] SliceTable(byte[] data, TableRecord record)
    {
        long end = Math.Min(data.Length, record.Offset + record.Length);
        if (record.Offset >= data.Length || end < record.Offset)
            return [];
        return data.AsSpan((int)record.Offset, (int)(end - record.Offset)).ToArray();
    }

    private static List<long> ReadLoca(byte[] data, long offset, int indexToLocFormat, int numGlyphs)
    {
        int count = numGlyphs + 1;
        var offsets = new List<long>(count);
        for (int i = 0; i < count; i++)
        {
            if (indexToLocFormat == 0)
            {
                long entry = offset + i * 2L;
                offsets.Add(entry + 2 > data.Length ? 0 : BigEndian.U16(data, entry) * 2L);
            }
            else
            {
                long entry = offset + i * 4L;
                offsets.Add(entry + 4 > data.Length ? 0 : BigEndian.U32(data, entry));
            }
        }
        return offsets;
    }

    private static HashSet<int> ExpandCompositeGlyphs(byte[] data, long glyfOffset, List<long> offsets, HashSet<int> glyphIds)
    {
        var queue = new List<int>(glyphIds);
        var seen = new HashSet<int>(glyphIds);
        int maxGid = offsets.Count - 1;
        while (queue.Count > 0)
        {
            int gid = queue[^1];
            queue.RemoveAt(queue.Count - 1);
            if (gid < 0 || gid >= maxGid)
                continue;
            long start = offsets[gid];
            long end = offsets[gid + 1];
            if (start == end)
                continue;
            foreach (int comp in CompositeComponents(data, glyfOffset + start, glyfOffset + end))
            {
                if (seen.Add(comp))
                    queue.Add(comp);
            }
        }
        return seen;
    }

    private static List<int> CompositeComponents(byte[] data, long offset, long glyphEnd)
    {
        var components = new List<int>();
        if (offset + 10 > glyphEnd)
            return components;
        if (BigEndian.I16(data, offset) >= 0)
            return components;
        long pos = offset + 10;
        int flags = 0x0020;
        while ((flags & 0x0020) != 0)
        {
            if (pos + 4 > glyphEnd)
                break;
            flags = BigEndian.U16(data, pos);
            pos += 2;
            components.Add(BigEndian.U16(data, pos));
            pos += 2;
            pos += (flags & 0x0001) != 0 ? 4 : 2;
            if ((flags & 0x0008) != 0)
                pos += 2;
            else if ((flags & 0x0040) != 0)
                pos += 4;
            else if ((flags & 0x0080) != 0)
                pos += 8;
            if ((flags & 0x0020) == 0 && (flags & 0x0100) != 0)
            {
                if (pos + 2 > glyphEnd)
                    break;
                int instrLen = BigEndian.U16(data, pos);
                pos += 2 + instrLen;
            }
        }
        return components;
    }

    private static (byte[] Glyf, List<long> Offsets) SubsetGlyf(byte[] data, long glyfOffset, List<long> offsets, List<int> glyphIds, Dictionary<int, int> gidMap)
    {
        var newOffsets = new List<long> { 0 };
        using var newData = new MemoryStream();
        int maxGid = offsets.Count - 1;
        foreach (int gid in glyphIds)
        {
            if (gid < 0 || gid >= maxGid)
            {
                newOffsets.Add(newData.Length);
                continue;
            }
            long start = glyfOffset + offsets[gid];
            long end = glyfOffset + offsets[gid + 1];
            byte[] glyphData = start < 0 || end < start || start > data.Length || end > data.Length
                ? []
                : data.AsSpan((int)start, (int)(end - start)).ToArray();
            if (glyphData.Length > 0)
                glyphData = RemapCompositeGlyph(glyphData, gidMap);
            newData.Write(glyphData);
            long pad = Align4(newData.Length) - newData.Length;
            for (int i = 0; i < pad; i++)
                newData.WriteByte(0);
            newOffsets.Add(newData.Length);
        }
        return (newData.ToArray(), newOffsets);
    }

    private static List<(int Advance, int Lsb)> ReadHmtx(byte[] data, long offset, int numHMetrics, int numGlyphs)
    {
        var metrics = new List<(int, int)>();
        int advance = 0;
        for (int i = 0; i < numHMetrics; i++)
        {
            if (offset + 4 > data.Length)
                break;
            advance = BigEndian.U16(data, offset);
            int lsb = BigEndian.I16(data, offset + 2);
            metrics.Add((advance, lsb));
            offset += 4;
        }
        if (numHMetrics == 0)
            advance = 0;
        for (int i = metrics.Count; i < numGlyphs; i++)
        {
            int lsb = offset + 2 > data.Length ? 0 : BigEndian.I16(data, offset);
            metrics.Add((advance, lsb));
            offset += 2;
        }
        return metrics;
    }

    private static byte[] BuildHmtx(List<(int Advance, int Lsb)> metrics, List<int> glyphIds)
    {
        var output = new byte[glyphIds.Count * 4];
        for (int i = 0; i < glyphIds.Count; i++)
        {
            int gid = glyphIds[i];
            var (advance, lsb) = gid >= 0 && gid < metrics.Count ? metrics[gid] : (0, 0);
            BinaryPrimitives.WriteUInt16BigEndian(output.AsSpan(i * 4), (ushort)advance);
            BinaryPrimitives.WriteInt16BigEndian(output.AsSpan(i * 4 + 2), (short)lsb);
        }
        return output;
    }

    private static byte[] RemapCompositeGlyph(byte[] glyphData, Dictionary<int, int> gidMap)
    {
        if (glyphData.Length < 10 || BigEndian.I16(glyphData, 0) >= 0)
            return glyphData;
        var data = (byte[])glyphData.Clone();
        int pos = 10;
        int flags = 0x0020;
        while ((flags & 0x0020) != 0)
        {
            if (pos + 4 > data.Length)
                break;
            flags = BigEndian.U16(data, pos);
            pos += 2;
            int glyphId = BigEndian.U16(data, pos);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(pos), (ushort)gidMap.GetValueOrDefault(glyphId, 0));
            pos += 2;
            pos += (flags & 0x0001) != 0 ? 4 : 2;
            if ((flags & 0x0008) != 0)
                pos += 2;
            else if ((flags & 0x0040) != 0)
                pos += 4;
            else if ((flags & 0x0080) != 0)
                pos += 8;
            if ((flags & 0x0020) == 0 && (flags & 0x0100) != 0)
            {
                if (pos + 2 > data.Length)
                    break;
                int instrLen = BigEndian.U16(data, pos);
                pos += 2 + instrLen;
            }
        }
        return data;
    }

    internal static byte[] BuildCmapTable(IReadOnlyDictionary<int, int> codeToGid)
    {
        var codes = codeToGid.Keys.OrderBy(c => c).ToList();
        if (codes.Count == 0)
            codes = [0xFFFF];
        var segments = codes.Where(c => c != 0xFFFF).Select(c => (Start: c, End: c)).ToList();
        segments.Add((0xFFFF, 0xFFFF));
        int segCount = segments.Count;
        int segCountX2 = segCount * 2;
        int searchRange = segCount != 0 ? 2 * (int)Math.Pow(2, Math.Floor(Math.Log(segCount) / Math.Log(2))) : 0;
        int entrySelector = searchRange != 0 ? (int)(Math.Log(searchRange / 2.0) / Math.Log(2)) : 0;
        int rangeShift = segCountX2 - searchRange;
        int length = 14 + segCount * 8 + 2;
        var fmt4 = new byte[length];
        void W(int at, int value) => BinaryPrimitives.WriteUInt16BigEndian(fmt4.AsSpan(at), checked((ushort)value));
        W(0, 4);
        W(2, length);
        W(4, 0);
        W(6, segCountX2);
        W(8, searchRange);
        W(10, entrySelector);
        W(12, rangeShift);
        int p = 14;
        foreach (var (_, end) in segments)
        {
            W(p, end);
            p += 2;
        }
        W(p, 0);
        p += 2;
        foreach (var (start, _) in segments)
        {
            W(p, start);
            p += 2;
        }
        foreach (var (start, _) in segments)
        {
            int delta = start == 0xFFFF ? 1 : (codeToGid.GetValueOrDefault(start, 0) - start) & 0xFFFF;
            W(p, delta & 0xFFFF);
            p += 2;
        }
        for (int i = 0; i < segCount; i++)
        {
            W(p, 0);
            p += 2;
        }
        var cmap = new byte[12 + fmt4.Length];
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(0), 0);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(6), 1);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(8), 12);
        fmt4.CopyTo(cmap, 12);
        return cmap;
    }

    private static Dictionary<int, int> ParseCmapTable(byte[] data, long offset)
    {
        if (offset + 4 > data.Length)
            return [];
        if (BigEndian.U16(data, offset) != 0)
            return [];
        int numTables = BigEndian.U16(data, offset + 2);
        var candidates = new List<(int Priority, long Offset)>();
        for (int i = 0; i < numTables; i++)
        {
            long record = offset + 4 + i * 8L;
            if (record + 8 > data.Length)
                continue;
            int platformId = BigEndian.U16(data, record);
            int encodingId = BigEndian.U16(data, record + 2);
            long sub = BigEndian.U32(data, record + 4);
            if (platformId == 3 && encodingId == 10)
                candidates.Add((0, offset + sub));
            else if (platformId == 3 && encodingId == 1)
                candidates.Add((1, offset + sub));
            else if (platformId == 0)
                candidates.Add((2, offset + sub));
            else if (platformId == 3 && encodingId == 0)
                candidates.Add((3, offset + sub));
        }
        foreach (var (_, chosen) in candidates.OrderBy(c => c.Priority))
        {
            if (chosen + 2 > data.Length)
                continue;
            int fmt = BigEndian.U16(data, chosen);
            if (fmt == 4)
                return chosen + 14 > data.Length ? [] : TtfMetrics.CmapFormat4(data, chosen);
            if (fmt == 12)
                return chosen + 16 > data.Length ? [] : CmapFormat12Bounded(data, chosen);
        }
        return [];
    }

    private static Dictionary<int, int> CmapFormat12Bounded(byte[] data, long offset)
    {
        long numGroups = BigEndian.U32(data, offset + 12);
        var cmap = new Dictionary<int, int>();
        long groupOffset = offset + 16;
        for (long i = 0; i < numGroups; i++)
        {
            long start = groupOffset + i * 12;
            if (start + 12 > data.Length)
                break;
            long startChar = BigEndian.U32(data, start);
            long endChar = BigEndian.U32(data, start + 4);
            long startGlyph = BigEndian.U32(data, start + 8);
            for (long code = startChar; code <= endChar; code++)
                cmap[(int)code] = (int)(startGlyph + (code - startChar));
        }
        return cmap;
    }

    private static void UpdateChecksumAdjustment(byte[] fontBytes, List<TableRecord> records)
    {
        var head = records.FirstOrDefault(r => r.Tag == "head");
        if (head is null || head.Offset + 12 > fontBytes.Length)
            return;
        fontBytes.AsSpan((int)head.Offset + 8, 4).Clear();
        uint checksum = CalcChecksum(fontBytes);
        uint adjustment = unchecked(0xB1B0AFBA - checksum);
        BinaryPrimitives.WriteUInt32BigEndian(fontBytes.AsSpan((int)head.Offset + 8), adjustment);
    }

    private static uint CalcChecksum(byte[] data)
    {
        uint total = 0;
        int full = data.Length / 4 * 4;
        for (int i = 0; i < full; i += 4)
            total = unchecked(total + BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(i)));
        if (full < data.Length)
        {
            Span<byte> tail = stackalloc byte[4];
            tail.Clear();
            data.AsSpan(full).CopyTo(tail);
            total = unchecked(total + BinaryPrimitives.ReadUInt32BigEndian(tail));
        }
        return total;
    }

    private static long Align4(long value) => (value + 3) & ~3L;
}
