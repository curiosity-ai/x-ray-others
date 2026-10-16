// Ported from the FontResource / EmbeddedType42 types and the TrueType metric helpers of
// ps/fonts.py.

using XRay.PS.Internal;
using XRay.PS.PostScript;

namespace XRay.PS.Fonts;

/// <summary>A resolved font: its metrics, encoding and, when embedded, its program.</summary>
public sealed record FontResource(
    string Name,
    string FontType,
    int UnitsPerEm,
    IReadOnlyDictionary<int, string> Encoding,
    IReadOnlyDictionary<string, double> GlyphWidths,
    bool Substitute,
    IReadOnlyDictionary<object, PsProcedure>? CharProcs = null,
    IReadOnlyDictionary<int, double>? CodeWidths = null,
    FontResource? Descendant = null,
    int? EscChar = null,
    IReadOnlyList<FontResource>? FdepVector = null,
    IReadOnlyList<int>? FmapEncoding = null,
    PsDict? FontDict = null,
    IReadOnlyDictionary<int, int>? CodeMap = null,
    byte[]? FontProgram = null,
    int? FmapType = null)
{
    internal static readonly IReadOnlyDictionary<int, string> EmptyEncoding = new Dictionary<int, string>();
    internal static readonly IReadOnlyDictionary<string, double> EmptyWidths = new Dictionary<string, double>();

    // Reference identity: the original compared these frozen dataclasses field by field, but
    // nothing relies on two separately resolved fonts comparing equal.
    public bool Equals(FontResource? other) => ReferenceEquals(this, other);

    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}

/// <summary>An embedded TrueType program registered under a font name (Type 42 or XPS).</summary>
public sealed record EmbeddedType42(byte[] Data, int UnitsPerEm, IReadOnlyDictionary<int, double> CodeWidths);

/// <summary>Where a font file lives: on disk, or bundled in this assembly.</summary>
public sealed class FontLocation : IEquatable<FontLocation>
{
    private const string ResourcePrefix = "XRay.PS.Resources.";

    private FontLocation(string path, bool embedded)
    {
        Path = path;
        Embedded = embedded;
    }

    /// <summary>The file path, or the bundled resource's file name.</summary>
    public string Path { get; }

    /// <summary>True for a font bundled with the library.</summary>
    public bool Embedded { get; }

    public static FontLocation File(string path) => new(System.IO.Path.GetFullPath(path), false);

    public static FontLocation Bundled(string fileName) => new(fileName, true);

    public string FileName => System.IO.Path.GetFileName(Path);

    public string Stem => System.IO.Path.GetFileNameWithoutExtension(Path);

    public string Extension => System.IO.Path.GetExtension(Path);

    public bool Exists => Embedded ? BundledResources.Exists(Path) : System.IO.File.Exists(Path);

    public byte[] ReadAllBytes() => Embedded ? BundledResources.Read(Path) : System.IO.File.ReadAllBytes(Path);

    public bool Equals(FontLocation? other) => other is not null && other.Embedded == Embedded && string.Equals(other.Path, Path, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as FontLocation);

    public override int GetHashCode() => HashCode.Combine(Path, Embedded);

    public override string ToString() => Embedded ? "bundled:" + Path : Path;

    internal static string ResourceName(string fileName) => ResourcePrefix + fileName;
}

/// <summary>Files bundled with the library (the original's <c>resources/</c> folder).</summary>
internal static class BundledResources
{
    private static readonly Dictionary<string, byte[]> Cache = new(StringComparer.Ordinal);

    public static bool Exists(string fileName) =>
        typeof(BundledResources).Assembly.GetManifestResourceInfo(FontLocation.ResourceName(fileName)) is not null;

    public static byte[] Read(string fileName)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(fileName, out var cached))
                return cached;
            using var stream = typeof(BundledResources).Assembly.GetManifestResourceStream(FontLocation.ResourceName(fileName))
                ?? throw new FileNotFoundException("bundled resource not found", fileName);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var bytes = ms.ToArray();
            Cache[fileName] = bytes;
            return bytes;
        }
    }
}

/// <summary>TrueType metric parsing used for text advance widths (<c>parse_ttf_metrics</c>).</summary>
public static class TtfMetrics
{
    /// <summary>Returns the units per em and the advance width per character code.</summary>
    public static (int UnitsPerEm, Dictionary<int, double> CodeWidths) Parse(byte[] data)
    {
        var tables = ParseTableDirectory(data);
        tables.TryGetValue("head", out long head);
        bool hasHead = tables.ContainsKey("head");
        if (!hasHead || !tables.TryGetValue("hhea", out long hhea) || !tables.TryGetValue("maxp", out long maxp)
            || !tables.TryGetValue("hmtx", out long hmtx) || !tables.TryGetValue("cmap", out long cmap))
            throw new PsTypeError("missing TrueType tables");
        int unitsPerEm = BigEndian.U16(data, head + 18);
        int numHMetrics = BigEndian.U16(data, hhea + 34);
        int numGlyphs = BigEndian.U16(data, maxp + 4);
        var widths = ReadHmtxWidths(data, hmtx, numHMetrics, numGlyphs);
        var cmapMap = ParseCmapTable(data, cmap);
        var codeWidths = new Dictionary<int, double>();
        foreach (var (code, glyphId) in cmapMap)
        {
            if (glyphId < widths.Count)
                codeWidths[code] = widths[glyphId];
        }
        return (unitsPerEm, codeWidths);
    }

    internal static Dictionary<string, long> ParseTableDirectory(byte[] data)
    {
        if (data.Length < 12)
            throw new PsTypeError("invalid TrueType data");
        int numTables = BigEndian.U16(data, 4);
        long offset = 12;
        var tables = new Dictionary<string, long>(StringComparer.Ordinal);
        for (int i = 0; i < numTables; i++)
        {
            string tag = BigEndian.Tag(data, offset);
            tables[tag] = BigEndian.U32(data, offset + 8);
            offset += 16;
        }
        return tables;
    }

    private static List<double> ReadHmtxWidths(byte[] data, long offset, int numHMetrics, int numGlyphs)
    {
        var widths = new List<double>(Math.Max(numGlyphs, numHMetrics));
        long current = offset;
        int lastWidth = 0;
        for (int i = 0; i < numHMetrics; i++)
        {
            int width = BigEndian.U16(data, current);
            lastWidth = width;
            widths.Add(width);
            current += 4;
        }
        while (widths.Count < numGlyphs)
            widths.Add(lastWidth);
        return widths;
    }

    private static Dictionary<int, int> ParseCmapTable(byte[] data, long offset)
    {
        int version = BigEndian.U16(data, offset);
        if (version != 0)
            throw new PsTypeError("unsupported cmap version");
        int numTables = BigEndian.U16(data, offset + 2);
        var candidates = new List<(int Priority, long Offset)>();
        for (int i = 0; i < numTables; i++)
        {
            long record = offset + 4 + i * 8;
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
            int fmt = BigEndian.U16(data, chosen);
            if (fmt == 4)
                return CmapFormat4(data, chosen);
            if (fmt == 12)
                return CmapFormat12(data, chosen);
        }
        return [];
    }

    internal static Dictionary<int, int> CmapFormat4(byte[] data, long offset)
    {
        int segCount = BigEndian.U16(data, offset + 6) / 2;
        long endOffset = offset + 14;
        var endCodes = new int[segCount];
        for (int i = 0; i < segCount; i++)
            endCodes[i] = BigEndian.U16(data, endOffset + i * 2);
        long startOffset = endOffset + segCount * 2 + 2;
        var startCodes = new int[segCount];
        for (int i = 0; i < segCount; i++)
            startCodes[i] = BigEndian.U16(data, startOffset + i * 2);
        long deltaOffset = startOffset + segCount * 2;
        var deltas = new int[segCount];
        for (int i = 0; i < segCount; i++)
            deltas[i] = BigEndian.I16(data, deltaOffset + i * 2);
        long rangeOffset = deltaOffset + segCount * 2;
        var rangeOffsets = new int[segCount];
        for (int i = 0; i < segCount; i++)
            rangeOffsets[i] = BigEndian.U16(data, rangeOffset + i * 2);
        long glyphArrayOffset = rangeOffset + segCount * 2;
        var cmap = new Dictionary<int, int>();
        for (int i = 0; i < segCount; i++)
        {
            int start = startCodes[i];
            int end = endCodes[i];
            if (start > end)
                continue;
            for (int code = start; code <= end; code++)
            {
                if (code == 0xFFFF)
                    continue;
                int roffset = rangeOffsets[i];
                int glyphId;
                if (roffset == 0)
                {
                    glyphId = (code + deltas[i]) & 0xFFFF;
                }
                else
                {
                    long idx = (roffset / 2) + (code - start) - (segCount - i);
                    long glyphOffset = glyphArrayOffset + idx * 2;
                    if (glyphOffset + 2 > data.Length)
                        continue;
                    glyphId = BigEndian.U16(data, glyphOffset);
                    if (glyphId != 0)
                        glyphId = (glyphId + deltas[i]) & 0xFFFF;
                }
                cmap[code] = glyphId;
            }
        }
        return cmap;
    }

    internal static Dictionary<int, int> CmapFormat12(byte[] data, long offset)
    {
        long numGroups = BigEndian.U32(data, offset + 12);
        var cmap = new Dictionary<int, int>();
        long groupOffset = offset + 16;
        for (long i = 0; i < numGroups; i++)
        {
            long startChar = BigEndian.U32(data, groupOffset + i * 12);
            long endChar = BigEndian.U32(data, groupOffset + i * 12 + 4);
            long startGlyph = BigEndian.U32(data, groupOffset + i * 12 + 8);
            for (long code = startChar; code <= endChar; code++)
                cmap[(int)code] = (int)(startGlyph + (code - startChar));
        }
        return cmap;
    }

    /// <summary>Maps advance widths onto glyph names through an encoding
    /// (<c>_glyph_widths_from_encoding</c>).</summary>
    public static Dictionary<string, double> GlyphWidthsFromEncoding(IReadOnlyDictionary<int, double> codeWidths, IReadOnlyDictionary<int, string> encoding)
    {
        var widths = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (code, name) in encoding)
        {
            if (codeWidths.TryGetValue(code, out double width))
                widths[name] = width;
        }
        return widths;
    }
}
