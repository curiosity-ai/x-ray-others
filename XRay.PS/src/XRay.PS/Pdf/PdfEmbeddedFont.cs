// Ported from pdf/fonts.py: PDF font embedding helpers.

using System.Globalization;
using System.Text;

namespace XRay.PS.Pdf;

/// <summary>A subset TrueType font ready to embed into a PDF.</summary>
public sealed record PdfEmbeddedFont(
    string BaseName,
    string SubsetName,
    string Subtype,
    string? Encoding,
    bool Symbolic,
    int FirstChar,
    int LastChar,
    IReadOnlyList<int> Widths,
    string FontFileKey,
    byte[] FontFile,
    int Ascent,
    int Descent,
    (int XMin, int YMin, int XMax, int YMax) BBox,
    int ItalicAngle,
    int StemV,
    string ToUnicode,
    IReadOnlyDictionary<int, int> CharCodeMap,
    int CodeByteWidth = 1,
    byte[]? CidToGidMap = null)
{
    /// <summary>Builds a ToUnicode CMap mapping each code to itself.</summary>
    public static string BuildToUnicode(IEnumerable<int> codes)
    {
        var mapping = new SortedDictionary<int, int>();
        foreach (int code in codes)
            mapping[code] = code;
        return BuildToUnicode(mapping);
    }

    /// <summary>Builds a ToUnicode CMap from a code to Unicode mapping.</summary>
    public static string BuildToUnicode(IReadOnlyDictionary<int, int> codes)
    {
        if (codes.Count == 0)
            return "";
        var ordered = codes.Keys.OrderBy(k => k).ToList();
        int maxCode = ordered[^1];
        int hexWidth = maxCode <= 0xFF ? 2 : 4;
        int high = hexWidth == 2 ? 0xFF : 0xFFFF;
        string fmt = "X" + hexWidth.ToString(CultureInfo.InvariantCulture);
        var lines = new List<string>
        {
            "/CIDInit /ProcSet findresource begin",
            "12 dict begin",
            "begincmap",
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def",
            "/CMapName /Adobe-Identity-UCS def",
            "/CMapType 2 def",
            "1 begincodespacerange",
            $"<{0.ToString(fmt, CultureInfo.InvariantCulture)}> <{high.ToString(fmt, CultureInfo.InvariantCulture)}>",
            "endcodespacerange",
        };
        var chunk = new List<(int Code, int Dst)>();
        foreach (int code in ordered)
        {
            chunk.Add((code, codes[code]));
            if (chunk.Count == 100)
            {
                BfCharBlock(lines, chunk, fmt);
                chunk.Clear();
            }
        }
        if (chunk.Count > 0)
            BfCharBlock(lines, chunk, fmt);
        lines.Add("endcmap");
        lines.Add("CMapName currentdict /CMap defineresource pop");
        lines.Add("end");
        lines.Add("end");
        return string.Join("\n", lines);
    }

    private static void BfCharBlock(List<string> lines, List<(int Code, int Dst)> codes, string fmt)
    {
        lines.Add($"{codes.Count} beginbfchar");
        var sb = new StringBuilder();
        foreach (var (code, dst) in codes)
        {
            sb.Clear();
            sb.Append('<').Append(code.ToString(fmt, CultureInfo.InvariantCulture)).Append("> <");
            // Python's "{:04X}" pads to at least four digits.
            sb.Append(dst.ToString("X4", CultureInfo.InvariantCulture)).Append('>');
            lines.Add(sb.ToString());
        }
        lines.Add("endbfchar");
    }
}
