using System.Text;
using XRay.OneNote.Saving;

namespace XRay.OneNote.Internal.Pdf;

/// <summary>A font the canvas can measure and draw with.</summary>
internal abstract class PdfFont
{
    protected PdfFont(string name)
    {
        Name = name;
    }

    /// <summary>The name text is drawn with (a standard font name or a TrueType alias).</summary>
    public string Name { get; }

    /// <summary>The ascent in 1/1000 em, for placing the first baseline of a paragraph.</summary>
    public abstract double Ascent { get; }

    public abstract double StringWidth(string text, double size);
}

/// <summary>One of the PDF standard fonts, drawn with WinAnsiEncoding.</summary>
internal sealed class StandardFont : PdfFont
{
    private readonly short[] _widths;

    public StandardFont(string name, short[] widths)
        : base(name)
    {
        _widths = widths;
    }

    public override double Ascent => Name.StartsWith("Times", StringComparison.Ordinal) ? 683 : Name.StartsWith("Courier", StringComparison.Ordinal) ? 629 : 718;

    public override double StringWidth(string text, double size)
    {
        double total = 0;
        foreach (var b in Encode(text))
        {
            total += _widths[b];
        }

        return total * size * 0.001;
    }

    /// <summary>Encodes text in WinAnsiEncoding; characters it cannot represent become '?'.</summary>
    public static byte[] Encode(string text)
    {
        var bytes = new byte[text.Length];
        for (var i = 0; i < text.Length; i++)
        {
            bytes[i] = StandardFontMetrics.ToWinAnsi(text[i]);
        }

        return bytes;
    }
}

/// <summary>A TrueType font embedded as a CIDFontType2 with Identity-H encoding.</summary>
internal sealed class EmbeddedTrueTypeFont : PdfFont
{
    public EmbeddedTrueTypeFont(string alias, TrueTypeFont font)
        : base(alias)
    {
        Font = font;
    }

    public TrueTypeFont Font { get; }

    /// <summary>Glyphs drawn so far, with the text each stands for (for the ToUnicode map).</summary>
    public SortedDictionary<ushort, string> UsedGlyphs { get; } = [];

    public override double Ascent => Font.Ascent;

    public override double StringWidth(string text, double size)
    {
        double total = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            total += Font.CharWidth(rune.Value);
        }

        return total * size * 0.001;
    }

    /// <summary>Two bytes per glyph id, recording each glyph as used.</summary>
    public byte[] Encode(string text)
    {
        var bytes = new List<byte>(text.Length * 2);
        foreach (var rune in text.EnumerateRunes())
        {
            var glyph = Font.GlyphFor(rune.Value);
            UsedGlyphs.TryAdd(glyph, rune.ToString());
            bytes.Add((byte)(glyph >> 8));
            bytes.Add((byte)glyph);
        }

        return [.. bytes];
    }
}

internal static partial class StandardFontMetrics
{
    private static readonly Dictionary<char, byte> WinAnsiHigh = BuildWinAnsiHigh();

    public static bool IsStandard(string name) => WidthTables.ContainsKey(name);

    public static short[] Widths(string name) => WidthTables[name];

    public static byte ToWinAnsi(char c)
    {
        if (c < 0x80 || (c >= 0xA0 && c <= 0xFF))
        {
            return (byte)c;
        }

        return WinAnsiHigh.TryGetValue(c, out var b) ? b : (byte)'?';
    }

    private static Dictionary<char, byte> BuildWinAnsiHigh()
    {
        const string high = "\u20AC\0\u201A\u0192\u201E\u2026\u2020\u2021\u02C6\u2030\u0160\u2039\u0152\0\u017D\0\0\u2018\u2019\u201C\u201D\u2022\u2013\u2014\u02DC\u2122\u0161\u203A\u0153\0\u017E\u0178";
        var map = new Dictionary<char, byte>();
        for (var i = 0; i < high.Length; i++)
        {
            if (high[i] != '\0')
            {
                map[high[i]] = (byte)(0x80 + i);
            }
        }

        return map;
    }
}

/// <summary>The fonts one PDF export uses, by name.</summary>
internal sealed class PdfFontRegistry
{
    private readonly Dictionary<string, PdfFont> _fonts = new(StringComparer.Ordinal);

    public PdfFont Get(string name)
    {
        if (_fonts.TryGetValue(name, out var font))
        {
            return font;
        }

        if (StandardFontMetrics.IsStandard(name))
        {
            font = new StandardFont(name, StandardFontMetrics.Widths(name));
            _fonts[name] = font;
            return font;
        }

        // Unknown names fall back to Helvetica rather than failing the export.
        return Get("Helvetica");
    }

    public bool IsRegistered(string name) => _fonts.ContainsKey(name);

    public void Register(PdfFont font) => _fonts[font.Name] = font;

    public IEnumerable<PdfFont> All => _fonts.Values;
}

/// <summary>
/// Chooses the font for a font group (sans, serif, mono) and style, as the reference writer does:
/// the PDF standard fonts by default, and a TrueType font from the font directories when the text
/// needs characters outside Latin-1 or when TrueType is requested for all text.
/// </summary>
internal sealed class FontResolver
{
    private static readonly Dictionary<string, (string Name, string File)[][]> Candidates = new(StringComparer.Ordinal)
    {
        ["sans"] =
        [
            [("Arial", "arial.ttf"), ("DejaVuSans", "DejaVuSans.ttf"), ("LiberationSans", "LiberationSans-Regular.ttf"), ("NotoSans", "NotoSans-Regular.ttf"), ("FreeSans", "FreeSans.ttf")],
            [("Arial-Bold", "arialbd.ttf"), ("DejaVuSans-Bold", "DejaVuSans-Bold.ttf"), ("LiberationSans-Bold", "LiberationSans-Bold.ttf"), ("NotoSans-Bold", "NotoSans-Bold.ttf"), ("FreeSans-Bold", "FreeSansBold.ttf")],
            [("Arial-Italic", "ariali.ttf"), ("DejaVuSans-Oblique", "DejaVuSans-Oblique.ttf"), ("LiberationSans-Italic", "LiberationSans-Italic.ttf"), ("NotoSans-Italic", "NotoSans-Italic.ttf"), ("FreeSans-Oblique", "FreeSansOblique.ttf")],
            [("Arial-BoldItalic", "arialbi.ttf"), ("DejaVuSans-BoldOblique", "DejaVuSans-BoldOblique.ttf"), ("LiberationSans-BoldItalic", "LiberationSans-BoldItalic.ttf"), ("NotoSans-BoldItalic", "NotoSans-BoldItalic.ttf"), ("FreeSans-BoldOblique", "FreeSansBoldOblique.ttf")],
        ],
        ["serif"] =
        [
            [("TimesNewRoman", "times.ttf"), ("DejaVuSerif", "DejaVuSerif.ttf"), ("LiberationSerif", "LiberationSerif-Regular.ttf"), ("NotoSerif", "NotoSerif-Regular.ttf"), ("FreeSerif", "FreeSerif.ttf")],
            [("TimesNewRoman-Bold", "timesbd.ttf"), ("DejaVuSerif-Bold", "DejaVuSerif-Bold.ttf"), ("LiberationSerif-Bold", "LiberationSerif-Bold.ttf"), ("NotoSerif-Bold", "NotoSerif-Bold.ttf"), ("FreeSerif-Bold", "FreeSerifBold.ttf")],
            [("TimesNewRoman-Italic", "timesi.ttf"), ("DejaVuSerif-Italic", "DejaVuSerif-Italic.ttf"), ("LiberationSerif-Italic", "LiberationSerif-Italic.ttf"), ("NotoSerif-Italic", "NotoSerif-Italic.ttf"), ("FreeSerif-Italic", "FreeSerifItalic.ttf")],
            [("TimesNewRoman-BoldItalic", "timesbi.ttf"), ("DejaVuSerif-BoldItalic", "DejaVuSerif-BoldItalic.ttf"), ("LiberationSerif-BoldItalic", "LiberationSerif-BoldItalic.ttf"), ("NotoSerif-BoldItalic", "NotoSerif-BoldItalic.ttf"), ("FreeSerif-BoldItalic", "FreeSerifBoldItalic.ttf")],
        ],
        ["mono"] =
        [
            [("CourierNew", "cour.ttf"), ("DejaVuSansMono", "DejaVuSansMono.ttf"), ("LiberationMono", "LiberationMono-Regular.ttf"), ("NotoSansMono", "NotoSansMono-Regular.ttf"), ("FreeMono", "FreeMono.ttf")],
            [("CourierNew-Bold", "courbd.ttf"), ("DejaVuSansMono-Bold", "DejaVuSansMono-Bold.ttf"), ("LiberationMono-Bold", "LiberationMono-Bold.ttf"), ("NotoSansMono-Bold", "NotoSansMono-Bold.ttf"), ("FreeMono-Bold", "FreeMonoBold.ttf")],
            [("CourierNew-Italic", "couri.ttf"), ("DejaVuSansMono-Oblique", "DejaVuSansMono-Oblique.ttf"), ("LiberationMono-Italic", "LiberationMono-Italic.ttf"), ("NotoSansMono-Italic", "NotoSansMono-Italic.ttf"), ("FreeMono-Oblique", "FreeMonoOblique.ttf")],
            [("CourierNew-BoldItalic", "courbi.ttf"), ("DejaVuSansMono-BoldOblique", "DejaVuSansMono-BoldOblique.ttf"), ("LiberationMono-BoldItalic", "LiberationMono-BoldItalic.ttf"), ("NotoSansMono-BoldItalic", "NotoSansMono-BoldItalic.ttf"), ("FreeMono-BoldOblique", "FreeMonoBoldOblique.ttf")],
        ],
    };

    private static readonly Dictionary<string, string[]> Standard = new(StringComparer.Ordinal)
    {
        ["sans"] = ["Helvetica", "Helvetica-Bold", "Helvetica-Oblique", "Helvetica-BoldOblique"],
        ["serif"] = ["Times-Roman", "Times-Bold", "Times-Italic", "Times-BoldItalic"],
        ["mono"] = ["Courier", "Courier-Bold", "Courier-Oblique", "Courier-BoldOblique"],
    };

    private static readonly string[] SystemFontDirectories =
    [
        "C:/Windows/Fonts",
        "/usr/share/fonts/truetype/dejavu",
        "/usr/share/fonts/truetype/liberation2",
        "/usr/share/fonts/truetype/liberation",
        "/usr/share/fonts/truetype/noto",
        "/usr/share/fonts/opentype/noto",
        "/Library/Fonts",
        "/System/Library/Fonts",
    ];

    private readonly PdfFontRegistry _registry;
    private readonly FontsSubsystem _fonts;
    private readonly Dictionary<(string Group, bool Bold, bool Italic, bool TrueType), string> _resolved = [];

    public FontResolver(PdfFontRegistry registry, FontsSubsystem? fonts)
    {
        _registry = registry;
        _fonts = fonts ?? FontsSubsystem.Default;
    }

    public static string GroupForFamily(string family)
    {
        var source = family.ToLowerInvariant();
        if (source.Contains("times", StringComparison.Ordinal) || source.Contains("serif", StringComparison.Ordinal))
        {
            return "serif";
        }

        return source.Contains("courier", StringComparison.Ordinal) || source.Contains("consol", StringComparison.Ordinal)
            || source.Contains("mono", StringComparison.Ordinal)
            ? "mono"
            : "sans";
    }

    /// <summary>True when the text has characters the standard fonts cannot draw (beyond Latin-1).</summary>
    public static bool RequiresUnicodeFont(string text)
    {
        foreach (var c in text)
        {
            if (c > '\u00FF')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The font name for a group and style, registering a TrueType font on first use.</summary>
    public string Register(string group, bool bold, bool italic, bool requireUnicode = false)
    {
        var useTrueType = _fonts.UseTrueTypeForAllText || requireUnicode;
        var key = (group, bold, italic, useTrueType);
        if (_resolved.TryGetValue(key, out var name))
        {
            return name;
        }

        var variant = (bold ? 1 : 0) + (italic ? 2 : 0);
        name = Standard[group][variant];
        if (useTrueType && FindFontFile(group, variant) is { } found)
        {
            if (!_registry.IsRegistered(found.Alias))
            {
                _registry.Register(new EmbeddedTrueTypeFont(found.Alias, found.Font));
            }

            name = found.Alias;
        }

        _resolved[key] = name;
        return name;
    }

    // Parsed fonts are immutable, so one copy per file serves every export in the process.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Path, DateTime Written), Lazy<TrueTypeFont?>> LoadedFonts = new();

    private static TrueTypeFont? LoadCached(string path)
    {
        var key = (Path.GetFullPath(path), File.GetLastWriteTimeUtc(path));
        return LoadedFonts.GetOrAdd(key, k => new Lazy<TrueTypeFont?>(() => TrueTypeFont.TryLoad(k.Path))).Value;
    }

    private (string Alias, TrueTypeFont Font)? FindFontFile(string group, int variant)
    {
        var directories = _fonts.FontDirectories.ToList();
        if (_fonts.SearchSystemFontDirectories)
        {
            directories.AddRange(SystemFontDirectories);
        }

        foreach (var (alias, file) in Candidates[group][variant])
        {
            foreach (var directory in directories)
            {
                var path = Path.Combine(directory, file);
                if (File.Exists(path) && LoadCached(path) is { } font)
                {
                    return (alias, font);
                }
            }
        }

        return null;
    }
}

/// <summary>PDF syntax helpers.</summary>
internal static class PdfSyntax
{
    public static string Number(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return "0";
        }

        var text = Math.Round(value, 4).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }

    /// <summary>A literal string from raw bytes, with delimiters and non-printable bytes escaped.</summary>
    public static string LiteralString(ReadOnlySpan<byte> bytes)
    {
        var builder = new StringBuilder(bytes.Length + 2);
        builder.Append('(');
        foreach (var b in bytes)
        {
            switch (b)
            {
                case (byte)'(' or (byte)')' or (byte)'\\':
                    builder.Append('\\').Append((char)b);
                    break;
                case < 0x20 or >= 0x7F:
                    builder.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
                    break;
                default:
                    builder.Append((char)b);
                    break;
            }
        }

        return builder.Append(')').ToString();
    }

    public static string HexString(ReadOnlySpan<byte> bytes) => "<" + Convert.ToHexString(bytes) + ">";

    /// <summary>A text string (for annotations): PDFDocEncoding-compatible ASCII as a literal, otherwise UTF-16BE with a BOM.</summary>
    public static string TextString(string text)
    {
        var ascii = true;
        foreach (var c in text)
        {
            ascii &= c < 0x80;
        }

        return ascii
            ? LiteralString(Encoding.ASCII.GetBytes(text))
            : HexString([0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(text)]);
    }

    public static string Name(string name)
    {
        var builder = new StringBuilder("/");
        foreach (var c in name)
        {
            if (c is > ' ' and < '\u007F' and not ('#' or '/' or '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '%'))
            {
                builder.Append(c);
            }
            else
            {
                foreach (var b in Encoding.UTF8.GetBytes(c.ToString()))
                {
                    builder.Append('#').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        }

        return builder.ToString();
    }
}
