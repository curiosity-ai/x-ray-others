// Ported from ps/font_cache.py, plus the system font lookup (_system_font) of ps/fonts.py.

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using XRay.PS.PostScript;

namespace XRay.PS.Fonts;

/// <summary>A font file found on the system or in the additional fonts folder.</summary>
public sealed record FontRecord(string Name, string Style, FontLocation Path, bool IsMonospace);

/// <summary>Advance-width metrics of a font file.</summary>
public sealed record FontMetrics(int UnitsPerEm, IReadOnlyDictionary<int, double> CodeWidths);

/// <summary>Indexes installed fonts and caches their metrics.</summary>
/// <remarks>Like the original, the process-wide instance is loaded once, with the additional
/// fonts folder of the first resolver that asks for it.</remarks>
public sealed class FontCache
{
    private static FontCache? s_instance;
    private static readonly object s_lock = new();

    private bool _loaded;
    private List<FontRecord> _records = [];
    private readonly Dictionary<FontLocation, FontMetrics> _metricsCache = [];
    private string? _additionalFolder;

    /// <summary>The process-wide cache.</summary>
    public static FontCache Shared
    {
        get
        {
            lock (s_lock)
                return s_instance ??= new FontCache();
        }
    }

    public IReadOnlyList<FontRecord> Records => _records;

    public void Load(string? additionalFontsFolder)
    {
        lock (this)
        {
            if (_loaded)
                return;
            _additionalFolder = null;
            if (!string.IsNullOrEmpty(additionalFontsFolder))
            {
                try
                {
                    _additionalFolder = System.IO.Path.GetFullPath(additionalFontsFolder);
                }
                catch (Exception)
                {
                    _additionalFolder = null;
                }
            }
            var records = new List<FontRecord>();
            if (_additionalFolder is not null && Directory.Exists(_additionalFolder))
                records.AddRange(ScanFolder(_additionalFolder));
            records.AddRange(ScanSystemFonts());
            _records = records;
            _loaded = true;
        }
    }

    public FontRecord? FindFont(string fontName, string? fontStyle)
    {
        if (!_loaded)
            Load(_additionalFolder);
        string name = NormalizeFamilyName(fontName);
        string style = fontStyle is not null ? NormalizeStyle(fontStyle) : NormalizeStyle(fontName);
        var matches = _records.Where(r => r.Name == name).ToList();
        if (matches.Count > 0)
        {
            var preferred = matches.FirstOrDefault(r => r.Style == style);
            return preferred ?? matches[0];
        }
        return NearestMatch(name, style);
    }

    public FontMetrics MetricsFor(FontLocation path)
    {
        lock (_metricsCache)
        {
            if (_metricsCache.TryGetValue(path, out var cached))
                return cached;
        }
        byte[] data;
        try
        {
            data = path.ReadAllBytes();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new PsTypeError("cannot read font data");
        }
        var (units, codeWidths) = TtfMetrics.Parse(data);
        var metrics = new FontMetrics(units, codeWidths);
        lock (_metricsCache)
            _metricsCache[path] = metrics;
        return metrics;
    }

    private FontRecord? NearestMatch(string name, string style)
    {
        if (_records.Count == 0)
            return null;
        IEnumerable<FontRecord> candidates = _records;
        var styleMatches = _records.Where(r => r.Style == style).ToList();
        if (styleMatches.Count > 0)
            candidates = styleMatches;
        FontRecord? best = null;
        double bestRatio = 0.0;
        foreach (var rec in candidates)
        {
            double ratio = Internal.SequenceMatcher.Ratio(name, rec.Name);
            if (ratio > bestRatio)
            {
                bestRatio = ratio;
                best = rec;
            }
        }
        // Avoid random substitutions for unrelated families (eg Arial -> Palatino).
        if (best is null || bestRatio < 0.86)
            return null;
        return best;
    }

    private static List<FontRecord> ScanSystemFonts()
    {
        var records = new List<FontRecord>();
        string? output = SystemFonts.RunTool("fc-list", ["--format", "%{family}|%{style}|%{file}|%{spacing}\n"]);
        if (output is not null)
        {
            foreach (string line in Internal.Py.SplitLines(output))
            {
                var record = ParseFcLine(line);
                if (record is not null)
                    records.Add(record);
            }
            return records;
        }
        foreach (string folder in SystemFonts.FallbackDirectories())
            records.AddRange(ScanFolder(folder));
        return records;
    }

    private static List<FontRecord> ScanFolder(string folder)
    {
        var records = new List<FontRecord>();
        try
        {
            foreach (string path in Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 }))
            {
                string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (ext is not (".ttf" or ".otf"))
                    continue;
                records.Add(RecordFromPath(path));
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return records;
    }

    private static FontRecord? ParseFcLine(string line)
    {
        var parts = line.Split('|').Select(p => p.Trim()).ToArray();
        if (parts.Length < 4)
            return null;
        string family = parts[0].Split(',')[0];
        string style = parts[1].Length > 0 ? parts[1].Split(',')[0] : "";
        string path = parts[2];
        if (!File.Exists(path))
            return null;
        bool isMonospace = parts[3].Trim() == "100";
        return new FontRecord(NormalizeFamilyName(family), NormalizeStyle(style.Length > 0 ? style : "Regular"), FontLocation.File(path), isMonospace);
    }

    private static FontRecord RecordFromPath(string path)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(path);
        var tokens = SplitNameTokens(name);
        return new FontRecord(FamilyFromTokens(name, tokens), InferStyleFromTokens(name, tokens), FontLocation.File(path), NameLooksMonospace(name));
    }

    internal static string NormalizeName(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (char ch in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) || char.IsNumber(ch))
                sb.Append(ch);
        }
        return sb.ToString();
    }

    internal static string NormalizeFamilyName(string value) => StripStyleTokens(NormalizeName(value));

    internal static string NormalizeStyle(string value)
    {
        string text = NormalizeName(value);
        if (text.Contains("mediumitalic", StringComparison.Ordinal) || text.Contains("mediumoblique", StringComparison.Ordinal))
            return "Italic";
        bool bold = text.Contains("bold", StringComparison.Ordinal);
        bool italic = text.Contains("italic", StringComparison.Ordinal) || text.Contains("oblique", StringComparison.Ordinal);
        if (bold && italic)
            return "BoldItalic";
        if (bold)
            return "Bold";
        if (italic)
            return "Italic";
        return "Regular";
    }

    private static List<string> SplitNameTokens(string value) =>
        Regex.Split(value, "[^A-Za-z0-9]+").Where(t => t.Length > 0).ToList();

    private static readonly HashSet<string> BoldTokens = ["b", "bd", "bold", "black", "demi"];
    private static readonly HashSet<string> ItalicTokens = ["i", "it", "italic", "oblique", "mediumitalic", "mediumoblique"];

    private static string InferStyleFromTokens(string value, List<string> tokens)
    {
        string text = NormalizeName(value);
        var lower = tokens.Select(t => t.ToLowerInvariant()).ToList();
        bool hasBold = lower.Any(BoldTokens.Contains) || text.Contains("bold", StringComparison.Ordinal);
        bool hasItalic = lower.Any(ItalicTokens.Contains) || text.Contains("italic", StringComparison.Ordinal) || text.Contains("oblique", StringComparison.Ordinal);
        if (hasBold && hasItalic)
            return "BoldItalic";
        if (hasBold)
            return "Bold";
        if (hasItalic)
            return "Italic";
        return "Regular";
    }

    private static readonly HashSet<string> StyleTokens =
    [
        "mediumitalic", "mediumoblique", "b", "bd", "bold", "black", "demi", "i", "it", "italic", "oblique",
        "regular", "roman", "bolditalic", "italicbold", "boldoblique", "obliquebold",
    ];

    private static string FamilyFromTokens(string value, List<string> tokens)
    {
        if (tokens.Count == 0)
            return NormalizeFamilyName(value);
        var family = tokens.Where(t => !StyleTokens.Contains(t.ToLowerInvariant())).ToList();
        if (family.Count == 0)
            family = [tokens[0]];
        return NormalizeName(string.Concat(family));
    }

    private static readonly string[] StripTokens =
    [
        "mediumitalic", "mediumoblique", "bolditalic", "italicbold", "boldoblique", "obliquebold",
        "bold", "italic", "oblique", "regular", "roman",
    ];

    private static string StripStyleTokens(string value)
    {
        string result = value;
        foreach (string token in StripTokens)
            result = result.Replace(token, "", StringComparison.Ordinal);
        return result;
    }

    private static bool NameLooksMonospace(string value)
    {
        string lower = value.ToLowerInvariant();
        return lower.Contains("mono", StringComparison.Ordinal) || lower.Contains("fixed", StringComparison.Ordinal) || lower.Contains("courier", StringComparison.Ordinal);
    }
}

/// <summary>System font discovery. Where fontconfig is installed (most Linux systems) its
/// <c>fc-list</c> and <c>fc-match</c> tools are asked, exactly as the original did, so the same
/// files are chosen; elsewhere the standard font folders are scanned.</summary>
public static class SystemFonts
{
    private static readonly Dictionary<string, string?> MatchCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, bool> ToolAvailable = new(StringComparer.Ordinal);

    /// <summary>When false, fontconfig is never consulted and only folders are scanned.</summary>
    public static bool UseFontConfig { get; set; } = true;

    /// <summary>The first family that resolves to an existing font file
    /// (<c>_system_font</c>).</summary>
    public static FontLocation? Find(IReadOnlyList<string> families)
    {
        foreach (string family in families)
        {
            string? path = Match(family);
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                return FontLocation.File(path);
        }
        return null;
    }

    private static string? Match(string family)
    {
        lock (MatchCache)
        {
            if (MatchCache.TryGetValue(family, out var cached))
                return cached;
        }
        string? result;
        string? output = RunTool("fc-match", ["-f", "%{file}", family]);
        if (output is not null)
        {
            result = Internal.Py.Strip(output);
        }
        else if (!IsToolAvailable("fc-match"))
        {
            result = ManagedMatch(family);
        }
        else
        {
            result = null;
        }
        lock (MatchCache)
            MatchCache[family] = result;
        return result;
    }

    /// <summary>Without fontconfig: an exact family (and style, when given) match among the
    /// scanned font files.</summary>
    private static string? ManagedMatch(string pattern)
    {
        string family = pattern;
        string? style = null;
        int colon = pattern.IndexOf(":style=", StringComparison.Ordinal);
        if (colon >= 0)
        {
            family = pattern[..colon];
            style = pattern[(colon + 7)..];
        }
        string name = FontCache.NormalizeFamilyName(family);
        string wanted = FontCache.NormalizeStyle(style ?? family);
        var records = FontCache.Shared.Records.Where(r => !r.Path.Embedded && r.Name == name).ToList();
        if (records.Count == 0)
            return null;
        return (records.FirstOrDefault(r => r.Style == wanted) ?? records[0]).Path.Path;
    }

    internal static IEnumerable<string> FallbackDirectories()
    {
        var candidates = new List<string> { "/usr/share/fonts", "/usr/local/share/fonts" };
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
            candidates.Add(System.IO.Path.Combine(home, ".fonts"));
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        if (!string.IsNullOrEmpty(windows))
            candidates.Add(windows);
        candidates.Add("/Library/Fonts");
        candidates.Add("/System/Library/Fonts");
        return candidates.Where(Directory.Exists).Distinct();
    }

    private static bool IsToolAvailable(string tool)
    {
        lock (ToolAvailable)
        {
            if (ToolAvailable.TryGetValue(tool, out bool known))
                return known;
        }
        bool found = false;
        if (UseFontConfig && !OperatingSystem.IsWindows() && !OperatingSystem.IsBrowser())
        {
            string? pathVar = Environment.GetEnvironmentVariable("PATH");
            if (pathVar is not null)
                found = pathVar.Split(System.IO.Path.PathSeparator).Any(dir => dir.Length > 0 && File.Exists(System.IO.Path.Combine(dir, tool)));
        }
        lock (ToolAvailable)
            ToolAvailable[tool] = found;
        return found;
    }

    /// <summary>Runs a fontconfig tool and returns its standard output, or null when the tool is
    /// unavailable or fails.</summary>
    internal static string? RunTool(string tool, IReadOnlyList<string> args)
    {
        if (!UseFontConfig || !IsToolAvailable(tool))
            return null;
        try
        {
            var psi = new ProcessStartInfo(tool)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
            };
            foreach (string arg in args)
                psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi);
            if (process is null)
                return null;
            var stdout = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            string text = stdout.GetAwaiter().GetResult();
            return process.ExitCode == 0 ? text : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
