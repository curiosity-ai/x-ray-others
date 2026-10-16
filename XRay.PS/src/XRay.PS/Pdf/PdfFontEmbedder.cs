// Ported from ps/pdf_font_embed.py: builds embedded PDF fonts from font resolver data.

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using XRay.PS.Fonts;
using XRay.PS.Internal;
using XRay.PS.PostScript;

namespace XRay.PS.Pdf;

/// <summary>Creates subset TrueType fonts for the PDF writer.</summary>
public static partial class PdfFontEmbedder
{
    private static readonly HashSet<string> StandardFonts =
    [
        "Courier", "Courier-Bold", "Courier-Oblique", "Courier-BoldOblique",
        "Helvetica", "Helvetica-Bold", "Helvetica-Oblique", "Helvetica-BoldOblique",
        "Times-Roman", "Times-Bold", "Times-Italic", "Times-BoldItalic",
        "Symbol", "ZapfDingbats",
    ];

    /// <summary>Builds the embedded font for <paramref name="fontName"/> covering
    /// <paramref name="usedCodes"/>, or null when the font is a standard font or cannot be
    /// found. A font reference may carry <c>#gid=N</c> and <c>#gmap=code:gid,...</c> suffixes.</summary>
    public static PdfEmbeddedFont? BuildEmbeddedFont(string fontName, ISet<int> usedCodes, FontResolver resolver, int? glyphIdOverride = null)
    {
        if (usedCodes.Count == 0)
            return null;
        string originalFontName = fontName;
        (fontName, int? embeddedGlyphId, var glyphMapOverrides) = SplitFontRefGlyphId(fontName);
        glyphIdOverride ??= embeddedGlyphId;
        if (StandardFonts.Contains(fontName))
            return null;
        var candidateNames = new List<string> { originalFontName };
        if (fontName != originalFontName)
            candidateNames.Add(fontName);
        bool allowFullEmbedOnSubsetFailure = false;
        EmbeddedType42? embedded = null;
        Dictionary<int, int> type42CodeMap = [];
        foreach (string candidate in candidateNames)
        {
            embedded = resolver.GetEmbeddedType42(candidate);
            if (embedded is not null)
            {
                type42CodeMap = ExtractType42CodeToGid(resolver, candidate);
                break;
            }
        }
        if (type42CodeMap.Count == 0)
            type42CodeMap = ExtractType42CodeToGid(resolver, fontName);

        byte[] data;
        int unitsPerEm;
        IReadOnlyDictionary<int, double> codeWidths;
        if (embedded is not null)
        {
            data = embedded.Data;
            unitsPerEm = embedded.UnitsPerEm;
            codeWidths = embedded.CodeWidths;
            allowFullEmbedOnSubsetFailure = true;
        }
        else
        {
            FontResource? resolved = null;
            foreach (string candidate in candidateNames)
            {
                try
                {
                    resolved = resolver.Resolve(candidate);
                }
                catch (Exception exc) when (exc is not (OperationCanceledException or PostScript.PsResourceLimitError))
                {
                    resolved = null;
                }
                if (resolved is not null)
                    break;
            }
            var source = resolved;
            if (source is not null && source.Descendant is not null && source.FontProgram is null)
                source = source.Descendant;
            if (source is not null && source.FontProgram is not null && source.CodeWidths is not null)
            {
                data = source.FontProgram;
                unitsPerEm = source.UnitsPerEm;
                codeWidths = source.CodeWidths;
                allowFullEmbedOnSubsetFailure = true;
                string? baseName = FontNameFromDict(source.FontDict);
                if (!string.IsNullOrEmpty(baseName))
                    candidateNames.Add(baseName);
            }
            else
            {
                var loaded = LoadFontFromPath(fontName, resolver);
                if (loaded is null)
                    return null;
                (data, unitsPerEm, codeWidths) = loaded.Value;
            }
        }

        if (!TtfSubset.HasTable(data, "cmap") && type42CodeMap.Count > 0)
        {
            try
            {
                data = TtfSubset.EnsureCmap(data, type42CodeMap);
            }
            catch (Exception exc) when (exc is not (OperationCanceledException or PostScript.PsResourceLimitError))
            {
                // Keep the original program.
            }
        }

        var unicodeCodes = usedCodes.Where(c => c is >= 0 and <= 0xFFFF).OrderBy(c => c).ToList();
        if (unicodeCodes.Count == 0)
            return null;

        var pdfToUnicode = AssignPdfCodes(unicodeCodes, 0xFF, preserveDirect: true);
        int codeByteWidth = 1;
        if (pdfToUnicode.Count == 0)
        {
            pdfToUnicode = AssignPdfCodes(unicodeCodes, 0xFFFF, preserveDirect: false);
            codeByteWidth = 2;
        }
        if (pdfToUnicode.Count == 0)
            return null;

        Dictionary<int, int>? explicitPdfCodeToGid;
        if (glyphIdOverride is int overrideGid)
            explicitPdfCodeToGid = pdfToUnicode.Keys.ToDictionary(k => k, _ => overrideGid);
        else
            explicitPdfCodeToGid = ExplicitCodeToGidFromFont(data, pdfToUnicode, glyphMapOverrides);

        var subsetCodes = new HashSet<int>(pdfToUnicode.Keys) { 0 };
        byte[]? subsetData;
        Dictionary<int, int> codeToGid = [];
        try
        {
            (subsetData, codeToGid) = TtfSubset.Subset(data, subsetCodes, pdfToUnicode, explicitPdfCodeToGid);
        }
        catch (Exception exc) when (exc is not (OperationCanceledException or PostScript.PsResourceLimitError))
        {
            subsetData = allowFullEmbedOnSubsetFailure && TtfSubset.HasTable(data, "cmap") ? data : null;
            if (subsetData is null)
            {
                foreach (string candidate in candidateNames)
                {
                    var loaded = LoadFontFromPath(candidate, resolver);
                    if (loaded is null)
                        continue;
                    var (fallbackData, fallbackUnits, fallbackWidths) = loaded.Value;
                    try
                    {
                        (subsetData, codeToGid) = TtfSubset.Subset(fallbackData, subsetCodes, pdfToUnicode, explicitPdfCodeToGid);
                    }
                    catch (Exception exc2) when (exc2 is not (OperationCanceledException or PostScript.PsResourceLimitError))
                    {
                        continue;
                    }
                    data = fallbackData;
                    unitsPerEm = fallbackUnits;
                    codeWidths = fallbackWidths;
                    break;
                }
            }
            if (subsetData is null)
                return null;
        }

        int firstChar = pdfToUnicode.Keys.Min();
        int lastChar = pdfToUnicode.Keys.Max();
        var widths = BuildWidthsRemapped(codeWidths, unitsPerEm, firstChar, lastChar, pdfToUnicode, explicitPdfCodeToGid, data);
        var metrics = ReadMetrics(subsetData);
        string tag = SubsetTag(fontName, unicodeCodes);
        string subsetName = $"{tag}+{SanitizePdfFontName(fontName)}";
        string toUnicode = PdfEmbeddedFont.BuildToUnicode(pdfToUnicode);
        var charCodeMap = new Dictionary<int, int>();
        foreach (var (pdfCode, unicodeCode) in pdfToUnicode)
            charCodeMap[unicodeCode] = pdfCode;
        byte[]? cidToGidMap = null;
        string subtype = "TrueType";
        string encoding = "WinAnsiEncoding";
        if (codeByteWidth == 2)
        {
            cidToGidMap = BuildCidToGidMap(codeToGid, lastChar);
            subtype = "Type0";
            encoding = "Identity-H";
        }
        return new PdfEmbeddedFont(fontName, subsetName, subtype, encoding, false, firstChar, lastChar, widths,
            "FontFile2", subsetData, metrics.Ascent, metrics.Descent, metrics.BBox, metrics.ItalicAngle, metrics.StemV,
            toUnicode, charCodeMap, codeByteWidth, cidToGidMap);
    }

    /// <summary>Splits the <c>#gid=</c> and <c>#gmap=</c> suffixes off a font reference.</summary>
    internal static (string Base, int? GlyphId, Dictionary<int, int> GlyphMap) SplitFontRefGlyphId(string fontRef)
    {
        var glyphMap = new Dictionary<int, int>();
        string baseName = fontRef;
        int gmap = baseName.IndexOf("#gmap=", StringComparison.Ordinal);
        if (gmap >= 0)
        {
            string suffix = baseName[(gmap + 6)..];
            baseName = baseName[..gmap];
            foreach (string item in suffix.Split(','))
            {
                int colon = item.IndexOf(':', StringComparison.Ordinal);
                if (colon < 0)
                    continue;
                if (Py.TryInt(item[..colon], 16, out long code) && Py.TryInt(item[(colon + 1)..], 10, out long gid))
                    glyphMap[(int)code] = (int)gid;
            }
        }
        int gidIndex = baseName.IndexOf("#gid=", StringComparison.Ordinal);
        if (gidIndex < 0)
            return (baseName, null, glyphMap);
        string gidText = baseName[(gidIndex + 5)..];
        baseName = baseName[..gidIndex];
        return Py.TryInt(gidText, 10, out long value) ? (baseName, (int)value, glyphMap) : (baseName, null, glyphMap);
    }

    private static Dictionary<int, int>? ExplicitCodeToGidFromFont(byte[] data, Dictionary<int, int> pdfToUnicode, Dictionary<int, int>? glyphMapOverrides)
    {
        TrueTypeFont font;
        try
        {
            font = new TrueTypeFont(data);
        }
        catch (Exception exc) when (exc is not (OperationCanceledException or PostScript.PsResourceLimitError))
        {
            return null;
        }
        var result = new Dictionary<int, int>();
        var overrides = glyphMapOverrides ?? [];
        foreach (var (pdfCode, unicodeCode) in pdfToUnicode)
        {
            int gid = overrides.TryGetValue(unicodeCode, out int o) ? o : font.GlyphIdForCode(unicodeCode);
            if (gid < 0)
                gid = 0;
            result[pdfCode] = gid;
        }
        return result;
    }

    private static Dictionary<int, int> ExtractType42CodeToGid(FontResolver resolver, string fontName)
    {
        if (!resolver.DefinedFonts.TryGetValue(fontName, out var defined))
        {
            if (resolver.Aliases.TryGetValue(fontName, out string? aliased) && !string.IsNullOrEmpty(aliased))
                resolver.DefinedFonts.TryGetValue(aliased, out defined);
        }
        var fontDict = defined?.FontDict;
        if (fontDict is null)
            return [];
        if (fontDict.Get("CharStrings") is not PsDict charStrings || fontDict.Get("Encoding") is not PsArray encoding)
            return [];
        var glyphToGid = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (key, raw) in charStrings.Items)
        {
            if (key is not string glyphName)
                continue;
            int? gid = raw switch
            {
                long l => (int)l,
                double d => (int)Py.Int(d),
                bool b => b ? 1 : 0,
                _ => null,
            };
            if (gid is int g)
                glyphToGid[glyphName] = g;
        }
        if (glyphToGid.Count == 0)
            return [];
        var codeToGid = new Dictionary<int, int>();
        for (int code = 0; code < encoding.Items.Count; code++)
        {
            string? glyphName = encoding.Items[code] switch
            {
                PsName name => name.Value,
                string s => s,
                _ => null,
            };
            if (glyphName is null)
                continue;
            if (glyphToGid.TryGetValue(glyphName, out int gid))
                codeToGid[code] = gid;
        }
        return codeToGid;
    }

    private static (byte[] Data, int UnitsPerEm, IReadOnlyDictionary<int, double> CodeWidths)? LoadFontFromPath(string fontName, FontResolver resolver)
    {
        var path = resolver.ResolveTtfPath(fontName);
        if (path is null)
        {
            FontRecord? record;
            try
            {
                record = resolver.Cache.FindFont(fontName, null);
            }
            catch (Exception exc) when (exc is not (OperationCanceledException or PostScript.PsResourceLimitError))
            {
                record = null;
            }
            if (record is not null && record.Path.Exists)
                path = record.Path;
        }
        if (path is null || !path.Exists)
            return null;
        byte[] data;
        try
        {
            data = path.ReadAllBytes();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        try
        {
            var metrics = resolver.Cache.MetricsFor(path);
            return (data, metrics.UnitsPerEm, metrics.CodeWidths);
        }
        catch (Exception exc) when (exc is not (OperationCanceledException or PostScript.PsResourceLimitError))
        {
            return null;
        }
    }

    private static string? FontNameFromDict(PsDict? fontDict)
    {
        if (fontDict is null)
            return null;
        object? value = fontDict.Get("FontName");
        if (value is PsName name && !string.IsNullOrEmpty(name.Value))
            return name.Value;
        if (value is string s && s.Length > 0)
            return s;
        return null;
    }

    private static List<int> BuildWidthsRemapped(IReadOnlyDictionary<int, double> codeWidths, int unitsPerEm, int firstChar, int lastChar,
        Dictionary<int, int> pdfToUnicode, Dictionary<int, int>? explicitPdfCodeToGid, byte[]? fontData)
    {
        var widths = new List<int>();
        double scale = 1000.0 / Math.Max(1, unitsPerEm);
        TrueTypeFont? font = null;
        if (explicitPdfCodeToGid is { Count: > 0 } && fontData is not null)
        {
            try
            {
                font = new TrueTypeFont(fontData);
            }
            catch (Exception exc) when (exc is not (OperationCanceledException or PostScript.PsResourceLimitError))
            {
                font = null;
            }
        }
        for (int pdfCode = firstChar; pdfCode <= lastChar; pdfCode++)
        {
            if (!pdfToUnicode.TryGetValue(pdfCode, out int unicodeCode))
            {
                widths.Add(0);
                continue;
            }
            double widthUnits = codeWidths.TryGetValue(unicodeCode, out double w) ? w : 0.0;
            if (font is not null && explicitPdfCodeToGid!.TryGetValue(pdfCode, out int gid))
                widthUnits = font.GlyphAdvance(gid);
            widths.Add((int)Py.Round(widthUnits * scale));
        }
        return widths;
    }

    private static Dictionary<int, int> AssignPdfCodes(List<int> unicodeCodes, int maxPdfCode, bool preserveDirect)
    {
        // Insertion-ordered like the original dict.
        var assigned = new Dictionary<int, int>();
        var assignedValues = new HashSet<int>();
        var usedPdfCodes = new HashSet<int>();
        if (preserveDirect)
        {
            foreach (int code in unicodeCodes)
            {
                if (code >= 0 && code <= maxPdfCode && !usedPdfCodes.Contains(code))
                {
                    assigned[code] = code;
                    assignedValues.Add(code);
                    usedPdfCodes.Add(code);
                }
            }
        }
        int nextPdf = maxPdfCode <= 0xFF ? 0x21 : 1;
        foreach (int code in unicodeCodes)
        {
            if (assignedValues.Contains(code))
                continue;
            while (usedPdfCodes.Contains(nextPdf) && nextPdf <= maxPdfCode)
                nextPdf++;
            if (nextPdf > maxPdfCode)
                return [];
            assigned[nextPdf] = code;
            assignedValues.Add(code);
            usedPdfCodes.Add(nextPdf);
        }
        return assigned;
    }

    private static byte[] BuildCidToGidMap(Dictionary<int, int> codeToGid, int lastChar)
    {
        var payload = new byte[(lastChar + 1) * 2];
        for (int code = 0; code <= lastChar; code++)
            BinaryPrimitives.WriteUInt16BigEndian(payload.AsSpan(code * 2), (ushort)(codeToGid.GetValueOrDefault(code, 0) & 0xFFFF));
        return payload;
    }

    private readonly record struct FontMetricsInfo(int Ascent, int Descent, (int, int, int, int) BBox, int ItalicAngle, int StemV);

    private static FontMetricsInfo ReadMetrics(byte[] data)
    {
        var tables = TtfMetrics.ParseTableDirectory(data);
        if (!tables.TryGetValue("head", out long head) || !tables.TryGetValue("hhea", out long hhea))
            throw Py.ValueError("missing TrueType tables");
        bool hasPost = tables.TryGetValue("post", out long post);
        int units = BigEndian.U16(data, head + 18);
        double scale = 1000.0 / Math.Max(1, units);
        int xMin = BigEndian.I16(data, head + 36);
        int yMin = BigEndian.I16(data, head + 38);
        int xMax = BigEndian.I16(data, head + 40);
        int yMax = BigEndian.I16(data, head + 42);
        int ascent = BigEndian.I16(data, hhea + 4);
        int descent = BigEndian.I16(data, hhea + 6);
        int italicAngle = 0;
        if (hasPost && post + 8 <= data.Length)
            italicAngle = (int)Py.Round(BigEndian.I32(data, post + 4) / 65536.0);
        var bbox = ((int)Py.Round(xMin * scale), (int)Py.Round(yMin * scale), (int)Py.Round(xMax * scale), (int)Py.Round(yMax * scale));
        return new FontMetricsInfo((int)Py.Round(ascent * scale), (int)Py.Round(descent * scale), bbox, italicAngle, 80);
    }

    /// <summary>A six-letter subset tag. The original derived it from the process-randomized
    /// string hash; this port uses a stable digest so output is reproducible.</summary>
    internal static string SubsetTag(string fontName, IEnumerable<int> codes)
    {
        string seed = $"{fontName}:{string.Join(",", codes)}";
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        ulong value = BinaryPrimitives.ReadUInt64BigEndian(digest);
        var letters = new char[6];
        for (int i = 0; i < 6; i++)
        {
            letters[i] = (char)('A' + (int)(value % 26));
            value /= 26;
        }
        return new string(letters);
    }

    internal static string SanitizePdfFontName(string fontName)
    {
        string name = Py.Strip(fontName);
        if (name.Length == 0)
            return "EmbeddedFont";
        // XPS font refs are often package-like paths such as /Resources/mshei.ttf; PDF names
        // in /BaseFont and /FontName must avoid raw "/" tokens.
        var parts = name.Split('/').Where(p => p.Length > 0 && p != ".").ToList();
        string pathName = parts.Count > 0 ? parts[^1] : "";
        if (pathName.Length == 0)
            pathName = name;
        int dot = pathName.LastIndexOf('.');
        string stem = dot >= 0 ? pathName[..dot] : pathName;
        string safe = UnsafeNameChars().Replace(stem, "-").Trim('-', '_');
        return safe.Length > 0 ? safe : "EmbeddedFont";
    }

    [GeneratedRegex("[^A-Za-z0-9_-]+")]
    private static partial Regex UnsafeNameChars();
}
