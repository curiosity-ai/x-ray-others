// Ported from ps/type1_parser.py and ps/font_types.py.

using System.Text.RegularExpressions;
using XRay.PS.Internal;
using XRay.PS.PostScript;

namespace XRay.PS.Fonts;

/// <summary>Metrics extracted from an embedded Type 1 font.</summary>
public sealed record Type1Metrics(
    string FontName,
    int UnitsPerEm,
    IReadOnlyDictionary<string, double> GlyphWidths,
    IReadOnlyDictionary<int, double> CodeWidths,
    IReadOnlyDictionary<int, string> Encoding,
    (int, int, int, int)? FontBBox,
    byte[]? FontProgramType1);

/// <summary>Parses embedded Type 1 fonts: eexec decryption and charstring widths.</summary>
public static class Type1Parser
{
    // Python's \s in a bytes pattern is ASCII whitespace only.
    private const string Ws = "[ \\t\\n\\r\\f\\v]";
    private const string NonWs = "[^ \\t\\n\\r\\f\\v]";

    public static Type1Metrics? ParseEmbeddedType1(PsDict fontDict, byte[]? programBytes)
    {
        string? fontName = FontNameFromDict(fontDict);
        if (string.IsNullOrEmpty(fontName))
            return null;
        var encoding = EncodingFromDict(fontDict);
        int unitsPerEm = UnitsPerEmFromDict(fontDict);
        var fontBBox = FontBBoxFromDict(fontDict);
        IReadOnlyDictionary<string, double> glyphWidths = new Dictionary<string, double>();
        if (programBytes is { Length: > 0 })
        {
            try
            {
                byte[] decrypted = DecryptEexec(programBytes);
                glyphWidths = ParseWidthsFromPrivateDict(decrypted);
            }
            catch (Exception)
            {
                glyphWidths = new Dictionary<string, double>();
            }
        }
        var codeWidths = new Dictionary<int, double>();
        foreach (var (code, glyphName) in encoding)
        {
            if (glyphWidths.TryGetValue(glyphName, out double width))
                codeWidths[code] = width;
        }
        return new Type1Metrics(fontName, unitsPerEm, glyphWidths, codeWidths, encoding, fontBBox,
            programBytes is { Length: > 0 } ? programBytes : null);
    }

    /// <summary>Parses a full <c>%%BeginResource</c>/<c>%%EndResource</c> Type 1 block.</summary>
    public static Type1Metrics? ParseResourceBlock(byte[] block)
    {
        string text = Py.Latin1String(block);
        if (!text.Contains("/FontType 1", StringComparison.Ordinal) || !text.Contains("currentfile eexec", StringComparison.Ordinal))
            return null;
        string? fontName = ExtractFontName(text);
        if (string.IsNullOrEmpty(fontName))
            return null;
        var fontDict = new PsDict();
        fontDict.Items["FontName"] = new PsName(fontName, true);
        fontDict.Items["Encoding"] = EncodingArrayFromBlock(text);
        var matrix = ExtractFontMatrix(text);
        if (matrix is not null)
            fontDict.Items["FontMatrix"] = new PsArray(matrix.Select(v => (object?)v).ToList());
        var bbox = ExtractFontBBox(text);
        if (bbox is not null)
            fontDict.Items["FontBBox"] = new PsArray(bbox.Select(v => (object?)(double)v).ToList());
        byte[]? program = ExtractEexecBytes(block, text);
        return ParseEmbeddedType1(fontDict, program);
    }

    public static byte[] DecryptEexec(byte[] cipher)
    {
        int r = 55665;
        var plain = new byte[cipher.Length];
        for (int i = 0; i < cipher.Length; i++)
        {
            int value = cipher[i];
            plain[i] = (byte)(value ^ (r >> 8));
            r = ((value + r) * 52845 + 22719) & 0xFFFF;
        }
        // eexec has a random warm-up prefix (typically 4 bytes).
        if (plain.Length <= 4)
            return [];
        return plain[4..];
    }

    public static Dictionary<string, double> ParseWidthsFromPrivateDict(byte[] program)
    {
        var widths = new Dictionary<string, double>(StringComparer.Ordinal);
        int charStringsPos = IndexOf(program, "/CharStrings"u8, 0);
        if (charStringsPos < 0)
            return widths;
        int lenIV = ExtractLenIV(program);
        int cursor = charStringsPos;
        int dataLen = program.Length;
        while (cursor < dataLen)
        {
            int nameStart = Array.IndexOf(program, (byte)'/', cursor);
            if (nameStart < 0)
                break;
            int nameEnd = ScanTokenEnd(program, nameStart + 1);
            if (nameEnd <= nameStart + 1)
            {
                cursor = nameStart + 1;
                continue;
            }
            string glyphName = Py.Latin1String(program.AsSpan(nameStart + 1, nameEnd - nameStart - 1));
            var lengthInfo = ParseIntToken(program, nameEnd);
            if (lengthInfo is null)
            {
                cursor = nameEnd;
                continue;
            }
            var (charLen, tokenPos) = lengthInfo.Value;
            if (charLen <= 0)
            {
                cursor = tokenPos;
                continue;
            }
            tokenPos = SkipWs(program, tokenPos);
            int? rdEnd = MatchRdToken(program, tokenPos);
            if (rdEnd is null)
            {
                cursor = tokenPos + 1;
                continue;
            }
            int dataStart = SkipOneWs(program, rdEnd.Value);
            long dataEnd = (long)dataStart + charLen;
            if (dataEnd > dataLen)
                break;
            var width = ParseCharstringWidth(program.AsSpan(dataStart, (int)(dataEnd - dataStart)), lenIV);
            if (width is not null)
                widths[glyphName] = width.Value;
            cursor = (int)dataEnd;
        }
        return widths;
    }

    /// <summary>Decrypts a Type 1 charstring and returns the width from hsbw/sbw.</summary>
    public static double? ParseCharstringWidth(ReadOnlySpan<byte> charstring, int lenIV = 4)
    {
        if (charstring.IsEmpty)
            return null;
        byte[] decrypted = DecryptCharstring(charstring);
        int start = 0;
        if (lenIV > 0)
        {
            if (decrypted.Length <= lenIV)
                return null;
            start = lenIV;
        }
        else if (lenIV < 0)
        {
            // decrypted[lenIV:] with a negative lenIV keeps the last -lenIV bytes.
            start = Math.Max(0, decrypted.Length + lenIV);
        }
        var stack = new List<long>();
        int idx = start;
        int size = decrypted.Length;
        while (idx < size)
        {
            int b0 = decrypted[idx++];
            if (b0 is >= 32 and <= 246)
            {
                stack.Add(b0 - 139);
                continue;
            }
            if (b0 is >= 247 and <= 250)
            {
                if (idx >= size)
                    return null;
                int b1 = decrypted[idx++];
                stack.Add((b0 - 247) * 256 + b1 + 108);
                continue;
            }
            if (b0 is >= 251 and <= 254)
            {
                if (idx >= size)
                    return null;
                int b1 = decrypted[idx++];
                stack.Add(-(b0 - 251) * 256 - b1 - 108);
                continue;
            }
            if (b0 == 255)
            {
                if (idx + 4 > size)
                    return null;
                int value = (decrypted[idx] << 24) | (decrypted[idx + 1] << 16) | (decrypted[idx + 2] << 8) | decrypted[idx + 3];
                idx += 4;
                stack.Add(value);
                continue;
            }
            if (b0 == 12)
            {
                if (idx >= size)
                    return null;
                int b1 = decrypted[idx++];
                if (b1 == 7 && stack.Count >= 4)
                    return stack[2];
                stack.Clear();
                continue;
            }
            if (b0 == 13 && stack.Count >= 2)
                return stack[^1];
            stack.Clear();
        }
        return null;
    }

    internal static byte[] DecryptCharstring(ReadOnlySpan<byte> cipher)
    {
        int r = 4330;
        var plain = new byte[cipher.Length];
        for (int i = 0; i < cipher.Length; i++)
        {
            int value = cipher[i];
            plain[i] = (byte)(value ^ (r >> 8));
            r = ((value + r) * 52845 + 22719) & 0xFFFF;
        }
        return plain;
    }

    private static string? ExtractFontName(string block)
    {
        var m = Regex.Match(block, "/FontName" + Ws + "+/(" + NonWs + "+)" + Ws + "+def");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static List<double>? ExtractFontMatrix(string block)
    {
        var m = Regex.Match(block, "/FontMatrix" + Ws + "*\\[([^\\]]+)\\]");
        if (!m.Success)
            return null;
        var values = new List<double>();
        foreach (string part in AsciiSplit(m.Groups[1].Value))
        {
            if (!Py.TryFloat(part, out double v))
                return null;
            values.Add(v);
        }
        return values.Count == 6 ? values : null;
    }

    private static int[]? ExtractFontBBox(string block)
    {
        var m = Regex.Match(block, "/FontBBox" + Ws + "*\\{([^\\}]+)\\}");
        if (!m.Success)
            return null;
        var values = new List<int>();
        foreach (string part in AsciiSplit(m.Groups[1].Value))
        {
            if (!Py.TryFloat(part, out double v))
                return null;
            values.Add((int)Py.Int(v));
        }
        return values.Count == 4 ? [.. values] : null;
    }

    /// <summary>bytes.split() with no argument: ASCII whitespace.</summary>
    private static IEnumerable<string> AsciiSplit(string text) =>
        text.Split([' ', '\t', '\n', '\r', '\f', '\v'], StringSplitOptions.RemoveEmptyEntries);

    private static PsArray EncodingArrayFromBlock(string block)
    {
        var values = new List<object?>(256);
        for (int i = 0; i < 256; i++)
            values.Add(new PsName(".notdef", true));
        foreach (Match m in Regex.Matches(block, "dup" + Ws + "+([0-9]+)" + Ws + "+/(" + NonWs + "+)" + Ws + "+put"))
        {
            if (!Py.TryInt(m.Groups[1].Value, 10, out long code))
                continue;
            if (code is >= 0 and < 256)
                values[(int)code] = new PsName(m.Groups[2].Value, true);
        }
        return new PsArray(values);
    }

    private static byte[]? ExtractEexecBytes(byte[] block, string text)
    {
        var marker = Regex.Match(text, "currentfile" + Ws + "+eexec");
        if (!marker.Success)
            return null;
        int start = marker.Index + marker.Length;
        int end = text.IndexOf("cleartomark", start, StringComparison.Ordinal);
        if (end < 0)
            end = block.Length;
        var hex = new List<byte>();
        for (int i = start; i < end; i++)
        {
            byte b = block[i];
            if (b is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F')
                hex.Add(b);
        }
        if (hex.Count == 0)
            return null;
        if (hex.Count % 2 == 1)
            hex.RemoveAt(hex.Count - 1);
        if (hex.Count == 0)
            return null;
        return PsTokenizer.HexDecode(hex);
    }

    private static string? FontNameFromDict(PsDict fontDict) => fontDict.Get("FontName") switch
    {
        PsName n => n.Value,
        string s => s,
        _ => null,
    };

    private static IReadOnlyDictionary<int, string> EncodingFromDict(PsDict fontDict)
    {
        if (fontDict.Get("Encoding") is PsArray array)
        {
            var mapping = new Dictionary<int, string>();
            for (int i = 0; i < array.Items.Count; i++)
            {
                if (array.Items[i] is PsName n)
                    mapping[i] = n.Value;
                else if (array.Items[i] is string s)
                    mapping[i] = s;
            }
            if (mapping.Count > 0)
                return mapping;
        }
        return new Dictionary<int, string>(Encodings.Standard);
    }

    private static int UnitsPerEmFromDict(PsDict fontDict)
    {
        if (fontDict.Get("FontMatrix") is PsArray value && value.Items.Count >= 4)
        {
            foreach (var scale in new[] { SafeFloat(value.Items[0]), SafeFloat(value.Items[3]) })
            {
                if (scale is not null && Math.Abs(scale.Value) > 1e-12)
                {
                    long units = Py.Round(1.0 / Math.Abs(scale.Value));
                    if (units > 0)
                        return (int)units;
                }
            }
        }
        return 1000;
    }

    private static (int, int, int, int)? FontBBoxFromDict(PsDict fontDict)
    {
        if (fontDict.Get("FontBBox") is not PsArray value || value.Items.Count < 4)
            return null;
        var parts = new int[4];
        for (int i = 0; i < 4; i++)
        {
            var f = SafeFloat(value.Items[i]);
            if (f is null || double.IsNaN(f.Value) || double.IsInfinity(f.Value))
                return null;
            parts[i] = (int)Py.Int(f.Value);
        }
        return (parts[0], parts[1], parts[2], parts[3]);
    }

    internal static double? SafeFloat(object? value) => value switch
    {
        long or double or bool or int => PsOps.ToDouble(value),
        string s when Py.TryFloat(s, out double d) => d,
        _ => null,
    };

    private static int ExtractLenIV(byte[] program)
    {
        string text = Py.Latin1String(program);
        var m = Regex.Match(text, "/lenIV" + Ws + "+(-?[0-9]+)" + Ws + "+def");
        if (!m.Success || !Py.TryInt(m.Groups[1].Value, 10, out long value))
            return 4;
        return (int)value;
    }

    private static bool IsTokenEnd(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or 0
        or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'/' or (byte)'%';

    private static bool IsWs(byte b) => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or 0;

    private static int ScanTokenEnd(byte[] data, int index)
    {
        while (index < data.Length && !IsTokenEnd(data[index]))
            index++;
        return index;
    }

    private static int SkipWs(byte[] data, int index)
    {
        while (index < data.Length && IsWs(data[index]))
            index++;
        return index;
    }

    private static int SkipOneWs(byte[] data, int index) => index < data.Length && IsWs(data[index]) ? index + 1 : index;

    private static (int Value, int Index)? ParseIntToken(byte[] data, int index)
    {
        index = SkipWs(data, index);
        if (index >= data.Length)
            return null;
        int start = index;
        if (data[index] is (byte)'+' or (byte)'-')
            index++;
        bool hasDigit = false;
        while (index < data.Length && data[index] is >= 48 and <= 57)
        {
            hasDigit = true;
            index++;
        }
        if (!hasDigit)
            return null;
        if (!Py.TryInt(Py.Latin1String(data.AsSpan(start, index - start)), 10, out long value))
            return null;
        return ((int)Math.Clamp(value, int.MinValue, int.MaxValue), index);
    }

    private static int? MatchRdToken(byte[] data, int index)
    {
        if (index + 2 <= data.Length && ((data[index] == 'R' && data[index + 1] == 'D') || (data[index] == '-' && data[index + 1] == '|')))
            return index + 2;
        return null;
    }

    internal static int IndexOf(byte[] data, ReadOnlySpan<byte> needle, int start)
    {
        if (start > data.Length)
            return -1;
        int idx = data.AsSpan(start).IndexOf(needle);
        return idx < 0 ? -1 : idx + start;
    }
}

/// <summary>Loaders for Type 1, Type 3 and Type 42 font dictionaries (ps/font_types.py).</summary>
public static class FontTypes
{
    public static FontResource LoadType1Font(PsDict fontDict)
    {
        string fontName = FontName(fontDict);
        byte[]? program = fontDict.Get("__type1_program__") switch
        {
            PsString s => s.Value,
            byte[] b => b,
            _ => null,
        };
        var metrics = Type1Parser.ParseEmbeddedType1(fontDict, program);
        if (metrics is null)
            return new FontResource(fontName, "Type1", 1000, FontResource.EmptyEncoding, FontResource.EmptyWidths, false);
        return new FontResource(metrics.FontName, "Type1", metrics.UnitsPerEm, metrics.Encoding, metrics.GlyphWidths, false,
            CodeWidths: metrics.CodeWidths, FontProgram: metrics.FontProgramType1);
    }

    public static FontResource LoadType3Font(PsDict fontDict)
    {
        string fontName = FontName(fontDict);
        Dictionary<object, PsProcedure>? procMap = null;
        if (fontDict.Get("CharProcs") is PsDict charProcs)
        {
            procMap = new Dictionary<object, PsProcedure>(PsKeyComparer.Instance);
            foreach (var (key, value) in charProcs.Items)
            {
                if (value is PsProcedure proc)
                    procMap[key] = proc;
            }
        }
        return new FontResource(fontName, "Type3", 1000, FontResource.EmptyEncoding, FontResource.EmptyWidths, false, CharProcs: procMap);
    }

    public static FontResource LoadType42Font(PsDict fontDict)
    {
        string fontName = FontName(fontDict);
        if (fontDict.Get("sfnts") is not PsArray sfnts)
            throw new PsTypeError("sfnts missing for Type42");
        byte[] data = JoinSfnts(sfnts);
        Dictionary<int, int>? codeMap = null;
        int unitsPerEm;
        Dictionary<int, double> codeWidths;
        IReadOnlyDictionary<string, double> glyphWidths;
        try
        {
            (unitsPerEm, codeWidths) = TtfMetrics.Parse(data);
            glyphWidths = TtfMetrics.GlyphWidthsFromEncoding(codeWidths, Encodings.Standard);
            var fallback = CodeWidthsFromType42Dict(fontDict, data);
            if (fallback is not null)
            {
                foreach (var (code, width) in fallback.Value.CodeWidths)
                    codeWidths.TryAdd(code, width);
                if (fallback.Value.CodeMap.Count > 0)
                    codeMap = fallback.Value.CodeMap;
            }
        }
        catch (Exception)
        {
            var fallback = CodeWidthsFromType42Dict(fontDict, data);
            if (fallback is not null)
            {
                unitsPerEm = fallback.Value.UnitsPerEm;
                codeWidths = fallback.Value.CodeWidths;
                glyphWidths = fallback.Value.GlyphWidths;
                codeMap = fallback.Value.CodeMap;
            }
            else
            {
                unitsPerEm = 1000;
                codeWidths = [];
                glyphWidths = new Dictionary<string, double>();
            }
        }
        return new FontResource(fontName, "Type42", unitsPerEm, Encodings.Standard, glyphWidths, false,
            CodeWidths: codeWidths, FontProgram: data, CodeMap: codeMap);
    }

    private static string FontName(PsDict fontDict) => fontDict.Get("FontName") switch
    {
        PsName n => n.Value,
        string s => s,
        _ => throw new PsTypeError("FontName missing"),
    };

    private static byte[] JoinSfnts(PsArray sfnts)
    {
        using var ms = new MemoryStream();
        foreach (var item in sfnts.Items)
        {
            if (item is PsString s)
                ms.Write(s.Value);
            else if (item is byte[] b)
                ms.Write(b);
            else
                throw new PsTypeError("invalid sfnts entry");
        }
        return ms.ToArray();
    }

    private static (int UnitsPerEm, Dictionary<int, double> CodeWidths, Dictionary<string, double> GlyphWidths, Dictionary<int, int> CodeMap)?
        CodeWidthsFromType42Dict(PsDict fontDict, byte[] data)
    {
        var widthsInfo = TtfHmtxWidths(data);
        if (widthsInfo is null)
            return null;
        var (unitsPerEm, widthsByGid) = widthsInfo.Value;
        if (fontDict.Get("CharStrings") is not PsDict charStrings)
            return null;
        var glyphWidths = new Dictionary<string, double>(StringComparer.Ordinal);
        var glyphIds = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (glyphNameObj, glyphIdRaw) in charStrings.Items)
        {
            if (!PsOps.IsNumeric(glyphIdRaw))
                continue;
            long glyphId = Py.Int(PsOps.ToDouble(glyphIdRaw));
            if (glyphId >= 0 && glyphId < widthsByGid.Count && glyphNameObj is string glyphName)
            {
                glyphWidths[glyphName] = widthsByGid[(int)glyphId];
                glyphIds[glyphName] = (int)glyphId;
            }
        }
        if (glyphWidths.Count == 0)
            return null;
        var codeWidths = new Dictionary<int, double>();
        var codeMap = new Dictionary<int, int>();
        if (fontDict.Get("Encoding") is PsArray encoding)
        {
            for (int code = 0; code < encoding.Items.Count; code++)
            {
                string? glyphName = encoding.Items[code] switch
                {
                    PsName n => n.Value,
                    string s => s,
                    _ => null,
                };
                if (glyphName is null)
                    continue;
                if (glyphWidths.TryGetValue(glyphName, out double width))
                    codeWidths[code] = width;
                if (!glyphIds.TryGetValue(glyphName, out int gid))
                    continue;
                codeMap[code] = gid;
                if (glyphName.StartsWith("uni", StringComparison.Ordinal) && glyphName.Length > 3)
                {
                    if (Py.TryInt(glyphName[3..], 16, out long u))
                        codeMap[(int)u] = gid;
                }
                else if (glyphName.StartsWith('u') && glyphName.Length is 5 or 6 or 7)
                {
                    if (Py.TryInt(glyphName[1..], 16, out long u))
                        codeMap[(int)u] = gid;
                }
            }
        }
        return (unitsPerEm, codeWidths, glyphWidths, codeMap);
    }

    private static (int UnitsPerEm, List<double> Widths)? TtfHmtxWidths(byte[] data)
    {
        if (data.Length < 12)
            return null;
        int numTables = BigEndian.U16(data, 4);
        var tables = new Dictionary<string, long>(StringComparer.Ordinal);
        for (int index = 0; index < numTables; index++)
        {
            long entry = 12 + index * 16L;
            if (entry + 16 > data.Length)
                return null;
            tables[BigEndian.Tag(data, entry)] = BigEndian.U32(data, entry + 8);
        }
        if (!tables.TryGetValue("head", out long head) || !tables.TryGetValue("hhea", out long hhea)
            || !tables.TryGetValue("maxp", out long maxp) || !tables.TryGetValue("hmtx", out long hmtx))
            return null;
        if (head + 20 > data.Length || hhea + 36 > data.Length || maxp + 6 > data.Length)
            return null;
        int unitsPerEm = BigEndian.U16(data, head + 18);
        int numHMetrics = BigEndian.U16(data, hhea + 34);
        int numGlyphs = BigEndian.U16(data, maxp + 4);
        var widths = new List<double>();
        long cursor = hmtx;
        int lastWidth = 0;
        for (int i = 0; i < numHMetrics; i++)
        {
            if (cursor + 4 > data.Length)
                return null;
            int width = BigEndian.U16(data, cursor);
            lastWidth = width;
            widths.Add(width);
            cursor += 4;
        }
        while (widths.Count < numGlyphs)
            widths.Add(lastWidth);
        return (unitsPerEm, widths);
    }
}
