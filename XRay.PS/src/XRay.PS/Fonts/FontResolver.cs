// Ported from the FontResolver class of ps/fonts.py.

using XRay.PS.PostScript;

namespace XRay.PS.Fonts;

/// <summary>Resolves fonts from names or PostScript font dictionaries: defined and embedded fonts
/// first, then the standard-14 substitutes, installed fonts and the bundled Liberation Serif.</summary>
public sealed class FontResolver
{
    private readonly string? _additionalFontsFolder;
    private readonly FontCache _fontCache;
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal)
    {
        // Common PostScript names emitted by Office/Cairo workflows.
        ["ArialMT"] = "Helvetica",
        ["Arial-BoldMT"] = "Helvetica-Bold",
        ["Arial-ItalicMT"] = "Helvetica-Oblique",
        ["Arial-BoldItalicMT"] = "Helvetica-BoldOblique",
        ["TimesNewRomanPSMT"] = "Times-Roman",
        ["TimesNewRomanPS-BoldMT"] = "Times-Bold",
        ["TimesNewRomanPS-ItalicMT"] = "Times-Italic",
        ["TimesNewRomanPS-BoldItalicMT"] = "Times-BoldItalic",
        ["SymbolMT"] = "Symbol",
        ["ZapfDingbatsITC"] = "ZapfDingbats",
    };
    private readonly Dictionary<string, EmbeddedType42> _embeddedType42 = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FontResource> _definedFonts = new(StringComparer.Ordinal);

    private static readonly HashSet<string> Standard12 =
    [
        "Helvetica", "Helvetica-Bold", "Helvetica-Oblique", "Helvetica-BoldOblique",
        "Times-Roman", "Times-Bold", "Times-Italic", "Times-BoldItalic",
        "Courier", "Courier-Bold", "Courier-Oblique", "Courier-BoldOblique",
    ];

    public FontResolver(string? additionalFontsFolder = null, FontCache? fontCache = null)
    {
        _additionalFontsFolder = string.IsNullOrEmpty(additionalFontsFolder) ? null : Path.GetFullPath(additionalFontsFolder);
        _fontCache = fontCache ?? FontCache.Shared;
        _fontCache.Load(additionalFontsFolder);
    }

    /// <summary>The additional fonts folder this resolver was created with.</summary>
    public string? AdditionalFontsFolder => _additionalFontsFolder;

    public FontCache Cache => _fontCache;

    /// <summary>Font name aliases; <c>definefont</c> adds derived names here.</summary>
    internal Dictionary<string, string> Aliases => _aliases;

    /// <summary>Fonts registered by <c>definefont</c>, by name.</summary>
    internal IReadOnlyDictionary<string, FontResource> DefinedFonts => _definedFonts;

    private string ApplyAlias(string fontName) => _aliases.GetValueOrDefault(fontName, fontName);

    /// <summary>Resolves a font by name.</summary>
    public FontResource Resolve(string fontName)
    {
        if (_definedFonts.TryGetValue(fontName, out var defined))
            return defined;
        fontName = ApplyAlias(fontName);
        if (_definedFonts.TryGetValue(fontName, out defined))
            return defined;
        if (_embeddedType42.TryGetValue(fontName, out var embedded))
        {
            var glyphWidths = TtfMetrics.GlyphWidthsFromEncoding(embedded.CodeWidths, Encodings.Standard);
            return new FontResource(fontName, "Type42", embedded.UnitsPerEm, Encodings.Standard, glyphWidths, false,
                CodeWidths: embedded.CodeWidths, FontProgram: embedded.Data);
        }
        if (fontName is "ZapfDingbats" or "Wingdings" or "Webdings")
            return FallbackDingbats(fontName);
        if (fontName == "Symbol")
            return FallbackSymbol(fontName);
        if (Standard12.Contains(fontName))
        {
            IReadOnlyDictionary<int, double>? codeWidths = null;
            IReadOnlyDictionary<string, double> glyphWidths = FontResource.EmptyWidths;
            int units = 1000;
            var ttfPath = ResolveTtfPath(fontName);
            if (ttfPath is not null && ttfPath.Exists)
            {
                (units, codeWidths) = LoadTtfMetrics(ttfPath);
                glyphWidths = TtfMetrics.GlyphWidthsFromEncoding(codeWidths, Encodings.Standard);
            }
            else if (fontName.StartsWith("Courier", StringComparison.Ordinal))
            {
                var courier = new Dictionary<int, double>();
                for (int code = 0; code < 256; code++)
                    courier[code] = 600.0;
                codeWidths = courier;
                glyphWidths = TtfMetrics.GlyphWidthsFromEncoding(courier, Encodings.Standard);
            }
            return new FontResource(fontName, "Type1", units, Encodings.Standard, glyphWidths, false, CodeWidths: codeWidths);
        }
        var record = _fontCache.FindFont(fontName, null);
        if (record is not null)
        {
            try
            {
                var (units, codeWidths) = LoadTtfMetrics(record.Path);
                var glyphWidths = TtfMetrics.GlyphWidthsFromEncoding(codeWidths, Encodings.Standard);
                return new FontResource(fontName, "Type42", units, Encodings.Standard, glyphWidths, true, CodeWidths: codeWidths);
            }
            catch (Exception exc) when (exc is not (OperationCanceledException or PostScript.PsResourceLimitError))
            {
                return FallbackLiberation(fontName);
            }
        }
        return FallbackLiberation(fontName);
    }

    /// <summary>Resolves a TrueType/OpenType file for a font name when one is available.</summary>
    public FontLocation? ResolveTtfPath(string fontName)
    {
        fontName = ApplyAlias(fontName);
        if (_definedFonts.TryGetValue(fontName, out var defined))
        {
            if (defined.FontProgram is { Length: > 0 })
                return null;
            if (_aliases.TryGetValue(fontName, out var baseAlias) && baseAlias != fontName)
                return ResolveTtfPath(baseAlias);
            // Common convention for re-encoded variants: Palatino-Roman-Reverse -> Palatino-Roman
            int dash = fontName.LastIndexOf('-');
            if (dash >= 0)
            {
                string b = fontName[..dash];
                if (b.Length > 0 && b != fontName)
                    return ResolveTtfPath(b);
            }
            return null;
        }
        if (_embeddedType42.ContainsKey(fontName))
            return null;
        if (fontName == "ZapfDingbats")
        {
            var path = ResolveZapfPath();
            return path is not null && path.Exists ? path : null;
        }
        if (fontName is "Wingdings" or "Webdings")
        {
            var path = FontLocation.Bundled("djVuDingbats.ttf");
            return path.Exists ? path : null;
        }
        if (fontName == "Symbol")
        {
            var path = ResolveSymbolPath();
            return path is not null && path.Exists ? path : null;
        }
        var record = _fontCache.FindFont(fontName, null);
        if (record is not null && record.Path.Exists)
            return record.Path;
        var fallback = ResolveLiberationPath(fontName);
        return fallback.Exists ? fallback : null;
    }

    private static IReadOnlyDictionary<int, string> NamedEncoding(string name) => name switch
    {
        "SymbolEncoding" => Encodings.Symbol,
        "ZapfDingbatsEncoding" => Encodings.ZapfDingbats,
        "ISOLatin1Encoding" or "ISOLatin1" => Encodings.IsoLatin1,
        _ => Encodings.Standard,
    };

    internal IReadOnlyDictionary<int, string> EncodingFromDict(PsDict fontDict)
    {
        var encodingValue = fontDict.Get("Encoding");
        if (encodingValue is string s)
            return NamedEncoding(s);
        if (encodingValue is PsName n)
            return NamedEncoding(n.Value);
        if (encodingValue is PsArray array)
        {
            var mapping = new Dictionary<int, string>();
            for (int index = 0; index < array.Items.Count; index++)
            {
                var item = array.Items[index];
                if (item is PsName name)
                    mapping[index] = name.Value;
                else if (item is string str)
                    mapping[index] = str;
            }
            if (mapping.Count > 0)
                return mapping;
        }
        if (encodingValue is PsDict dict)
        {
            var baseValue = dict.Get("BaseEncoding");
            var b = new Dictionary<int, string>(baseValue is PsName bn ? NamedEncoding(bn.Value) : Encodings.Standard);
            if (dict.Get("Differences") is PsArray differences)
            {
                long? code = null;
                foreach (var item in differences.Items)
                {
                    if (PsOps.IsNumeric(item))
                    {
                        code = Internal.Py.Int(PsOps.ToDouble(item));
                        continue;
                    }
                    if (code is null)
                        continue;
                    if (item is PsName name)
                    {
                        b[(int)code.Value] = name.Value;
                        code++;
                    }
                    else if (item is string str)
                    {
                        b[(int)code.Value] = str;
                        code++;
                    }
                }
            }
            return b;
        }
        return Encodings.Standard;
    }

    /// <summary>Resolves a font from a PostScript font dictionary.</summary>
    public FontResource ResolveFromDict(PsDict fontDict)
    {
        string fontType = NormalizeFontType(fontDict.Get("FontType"));
        var fontNameValue = fontDict.Get("FontName");
        string fontName = fontNameValue switch
        {
            PsName n => n.Value,
            string s => s,
            _ => "UnknownFont",
        };
        var encoding = EncodingFromDict(fontDict);
        if (fontType is "Type1" or "Type3" or "Type42")
        {
            FontResource resource;
            if (fontType == "Type1")
            {
                resource = FontTypes.LoadType1Font(fontDict);
            }
            else if (fontType == "Type3")
            {
                resource = FontTypes.LoadType3Font(fontDict);
            }
            else
            {
                try
                {
                    resource = FontTypes.LoadType42Font(fontDict);
                }
                catch (PsTypeError)
                {
                    // Some procsets clone/findfont Type42 dictionaries and rely on inherited
                    // resolver data rather than an explicit sfnts payload in the clone.
                    resource = Resolve(fontName);
                }
                if (resource.FontProgram is not null && resource.CodeWidths is not null)
                    RegisterEmbeddedType42(fontName, resource.FontProgram, resource.UnitsPerEm, resource.CodeWidths);
            }
            var glyphWidths = resource.GlyphWidths;
            if (resource.CodeWidths is not null)
                glyphWidths = TtfMetrics.GlyphWidthsFromEncoding(resource.CodeWidths, encoding);
            return new FontResource(fontName, resource.FontType, resource.UnitsPerEm, encoding, glyphWidths, false,
                CharProcs: resource.CharProcs, CodeWidths: resource.CodeWidths, FontDict: fontDict,
                FontProgram: resource.FontProgram, CodeMap: resource.CodeMap);
        }
        if (fontType is "Type0" or "CIDKeyed")
        {
            var descendants = new List<FontResource>();
            if (fontDict.Get("DescendantFonts") is PsArray descendantValue)
            {
                foreach (var item in descendantValue.Items)
                {
                    if (item is PsDict d)
                        descendants.Add(ResolveFromDict(d));
                    else if (item is FontResource fr)
                        descendants.Add(fr);
                }
            }
            if (fontDict.Get("FDepVector") is PsArray fdepValue)
            {
                var items = fdepValue.Items;
                int index = 0;
                while (index < items.Count)
                {
                    var item = items[index];
                    var next = index + 1 < items.Count ? items[index + 1] : null;
                    // Common PostScript pattern: [/FontName findfont ...]
                    if (item is PsName pn && next is PsName { Value: "findfont" })
                    {
                        descendants.Add(Resolve(pn.Value));
                        index += 2;
                        continue;
                    }
                    if (item is string ps && next is PsName { Value: "findfont" })
                    {
                        descendants.Add(Resolve(ps));
                        index += 2;
                        continue;
                    }
                    if (item is FontResource fr)
                        descendants.Add(fr);
                    else if (item is PsDict d)
                        descendants.Add(ResolveFromDict(d));
                    else if (item is PsName n)
                        descendants.Add(Resolve(n.Value));
                    else if (item is string str)
                        descendants.Add(Resolve(str));
                    index++;
                }
            }
            var descendant = descendants.Count > 0 ? descendants[0] : null;
            int? escChar = null;
            var escValue = fontDict.Get("EscChar");
            if (PsOps.IsNumeric(escValue))
                escChar = (int)(Internal.Py.Int(PsOps.ToDouble(escValue)) & 0xFF);
            List<int>? fmapEncoding = null;
            if (fontDict.Get("Encoding") is PsArray encArray)
            {
                var mapped = new List<int>();
                foreach (var item in encArray.Items)
                {
                    if (PsOps.IsNumeric(item))
                        mapped.Add((int)Internal.Py.Int(PsOps.ToDouble(item)));
                }
                if (mapped.Count > 0)
                    fmapEncoding = mapped;
            }
            int? fmapType = null;
            var fmapValue = fontDict.Get("FMapType");
            if (PsOps.IsNumeric(fmapValue))
                fmapType = (int)Internal.Py.Int(PsOps.ToDouble(fmapValue));
            var resolvedEncoding = descendant is not null ? descendant.Encoding : encoding;
            Dictionary<int, int>? codeMap = null;
            if (fontDict.Get("__CodeMap__") is Dictionary<int, int> rawMap && rawMap.Count > 0)
                codeMap = new Dictionary<int, int>(rawMap);
            return new FontResource(fontName, fontType, 1000, resolvedEncoding,
                descendant?.GlyphWidths ?? FontResource.EmptyWidths, false,
                Descendant: descendant, CodeWidths: descendant?.CodeWidths, EscChar: escChar,
                FdepVector: descendants.Count > 0 ? descendants : null, FmapEncoding: fmapEncoding,
                FontDict: fontDict, CodeMap: codeMap, FmapType: fmapType);
        }
        throw new PsUndefinedError($"unsupported font type {fontType}");
    }

    public void RegisterEmbeddedType42(string fontName, byte[] data, int unitsPerEm, IReadOnlyDictionary<int, double> codeWidths) =>
        _embeddedType42[fontName] = new EmbeddedType42(data, unitsPerEm, new Dictionary<int, double>(codeWidths));

    public EmbeddedType42? GetEmbeddedType42(string fontName) => _embeddedType42.GetValueOrDefault(ApplyAlias(fontName));

    public void RegisterDefinedFont(string fontName, FontResource resource) => _definedFonts[fontName] = resource;

    public double GetGlyphWidth(FontResource font, string glyphName)
    {
        if (font.GlyphWidths.TryGetValue(glyphName, out double w))
            return w;
        if (font.GlyphWidths.TryGetValue(".notdef", out w))
            return w;
        return 0.0;
    }

    private FontResource FallbackLiberation(string fontName)
    {
        var path = ResolveLiberationPath(fontName);
        if (!path.Exists)
            return new FontResource(fontName, "Type1", 1000, Encodings.Standard, FontResource.EmptyWidths, true);
        var (units, codeWidths) = LoadTtfMetrics(path);
        var glyphWidths = TtfMetrics.GlyphWidthsFromEncoding(codeWidths, Encodings.Standard);
        return new FontResource(fontName, "Type42", units, Encodings.Standard, glyphWidths, true, CodeWidths: codeWidths);
    }

    private FontResource FallbackDingbats(string fontName)
    {
        var path = ResolveZapfPath();
        if (path is null || !path.Exists)
            return new FontResource(fontName, "Type1", 1000, Encodings.ZapfDingbats, FontResource.EmptyWidths, true);
        var (units, codeWidths) = LoadTtfMetrics(path);
        var glyphWidths = TtfMetrics.GlyphWidthsFromEncoding(codeWidths, Encodings.ZapfDingbats);
        return new FontResource(fontName, "Type42", units, Encodings.ZapfDingbats, glyphWidths, true, CodeWidths: codeWidths);
    }

    private FontResource FallbackSymbol(string fontName)
    {
        var path = ResolveSymbolPath();
        if (path is null || !path.Exists)
            return new FontResource(fontName, "Type1", 1000, Encodings.Symbol, FontResource.EmptyWidths, true);
        var (units, codeWidths) = LoadTtfMetrics(path);
        var glyphWidths = TtfMetrics.GlyphWidthsFromEncoding(codeWidths, Encodings.Symbol);
        return new FontResource(fontName, "Type42", units, Encodings.Symbol, glyphWidths, true, CodeWidths: codeWidths);
    }

    internal static FontLocation? ResolveSymbolPath() =>
        SystemFonts.Find(["Standard Symbols PS:style=Regular", "Standard Symbols PS", "Symbol"]);

    internal FontLocation? ResolveZapfPath()
    {
        var candidates = new List<FontLocation>();
        if (_additionalFontsFolder is not null)
        {
            candidates.Add(FontLocation.File(Path.Combine(_additionalFontsFolder, "ITCZapfDingbats.ttf")));
            candidates.Add(FontLocation.File(Path.Combine(_additionalFontsFolder, "ZapfDingbats.ttf")));
        }
        candidates.Add(FontLocation.Bundled("djVuDingbats.ttf"));
        foreach (var candidate in candidates)
        {
            if (candidate.Exists)
                return candidate;
        }
        return SystemFonts.Find(["ITC Zapf Dingbats:style=Regular", "Zapf Dingbats", "Dingbats", "D050000L"]);
    }

    private static bool Has(string name, string token) => name.Contains(token, StringComparison.Ordinal);

    private FontLocation ResolveLiberationPath(string fontName)
    {
        string name = fontName.ToLowerInvariant();
        bool bold = Has(name, "bold");
        bool italic = Has(name, "italic") || Has(name, "oblique");
        FontLocation? found;
        if (name.StartsWith("arial", StringComparison.Ordinal))
        {
            var windowsArial = ResolveWindowsArialPath(name);
            if (windowsArial is not null)
                return windowsArial;
            if (bold && italic && (found = SystemFonts.Find(["Arimo:style=Bold Italic", "Arimo Bold Italic", "Arial:style=Bold Italic", "Arial Bold Italic", "Nimbus Sans:style=Bold Italic", "Nimbus Sans Bold Italic", "Liberation Sans Bold Italic", "DejaVu Sans Bold Oblique"])) is not null)
                return found;
            if (bold && (found = SystemFonts.Find(["Arimo:style=Bold", "Arimo Bold", "Arial:style=Bold", "Arial Bold", "Nimbus Sans:style=Bold", "Nimbus Sans Bold", "Liberation Sans Bold", "DejaVu Sans Bold"])) is not null)
                return found;
            if (italic && (found = SystemFonts.Find(["Arimo:style=Italic", "Arimo Italic", "Arial:style=Italic", "Arial Italic", "Nimbus Sans:style=Italic", "Nimbus Sans Italic", "Liberation Sans Italic", "DejaVu Sans Oblique"])) is not null)
                return found;
            if ((found = SystemFonts.Find(["Arimo:style=Regular", "Arimo", "Arimo Regular", "Arimo-R", "Nimbus Sans:style=Regular", "Nimbus Sans", "Arial:style=Regular", "Arial", "Liberation Sans Regular", "DejaVu Sans"])) is not null)
                return found;
        }
        if (name.StartsWith("courier", StringComparison.Ordinal))
        {
            if (bold && italic && (found = SystemFonts.Find(["Nimbus Mono PS:style=Bold Italic", "Nimbus Mono PS Bold Italic", "NimbusMonoPS-BoldItalic", "Liberation Mono Bold Italic", "DejaVu Sans Mono Bold Oblique"])) is not null)
                return found;
            if (bold && (found = SystemFonts.Find(["Nimbus Mono PS:style=Bold", "Nimbus Mono PS Bold", "NimbusMonoPS-Bold", "Liberation Mono Bold", "DejaVu Sans Mono Bold"])) is not null)
                return found;
            if (italic && (found = SystemFonts.Find(["Nimbus Mono PS:style=Italic", "Nimbus Mono PS Italic", "NimbusMonoPS-Italic", "Liberation Mono Italic", "DejaVu Sans Mono Oblique"])) is not null)
                return found;
            if ((found = SystemFonts.Find(["Nimbus Mono PS:style=Regular", "Nimbus Mono PS", "NimbusMonoPS-Regular", "Liberation Mono Regular", "DejaVu Sans Mono Book", "DejaVu Sans Mono"])) is not null)
                return found;
        }
        if (name.StartsWith("helvetica", StringComparison.Ordinal))
        {
            if (bold && italic && (found = SystemFonts.Find(["Nimbus Sans:style=Bold Italic", "Nimbus Sans Bold Italic", "NimbusSans-BoldItalic", "Liberation Sans Bold Italic", "DejaVu Sans Bold Oblique"])) is not null)
                return found;
            if (bold && (found = SystemFonts.Find(["Nimbus Sans:style=Bold", "Nimbus Sans Bold", "NimbusSans-Bold", "Liberation Sans Bold", "DejaVu Sans Bold"])) is not null)
                return found;
            if (italic && (found = SystemFonts.Find(["Nimbus Sans:style=Italic", "Nimbus Sans Italic", "NimbusSans-Italic", "Liberation Sans Italic", "DejaVu Sans Oblique"])) is not null)
                return found;
            if ((found = SystemFonts.Find(["Nimbus Sans:style=Regular", "Nimbus Sans", "NimbusSans-Regular", "Liberation Sans Regular", "DejaVu Sans"])) is not null)
                return found;
        }
        if (name.StartsWith("times", StringComparison.Ordinal))
        {
            if (bold && italic && (found = SystemFonts.Find(["Nimbus Roman:style=Bold Italic", "Nimbus Roman Bold Italic", "NimbusRoman-BoldItalic", "Liberation Serif Bold Italic", "DejaVu Serif Bold Italic"])) is not null)
                return found;
            if (bold && (found = SystemFonts.Find(["Nimbus Roman:style=Bold", "Nimbus Roman Bold", "NimbusRoman-Bold", "Liberation Serif Bold", "DejaVu Serif Bold"])) is not null)
                return found;
            if (italic && (found = SystemFonts.Find(["Nimbus Roman:style=Italic", "Nimbus Roman Italic", "NimbusRoman-Italic", "Liberation Serif Italic", "DejaVu Serif Italic"])) is not null)
                return found;
            if ((found = SystemFonts.Find(["Nimbus Roman:style=Regular", "Nimbus Roman", "NimbusRoman-Regular", "Liberation Serif Regular", "DejaVu Serif"])) is not null)
                return found;
        }
        if (bold && italic)
            return FontLocation.Bundled("LiberationSerif-BoldItalic.ttf");
        if (bold)
            return FontLocation.Bundled("LiberationSerif-Bold.ttf");
        if (italic)
            return FontLocation.Bundled("LiberationSerif-Italic.ttf");
        return FontLocation.Bundled("LiberationSerif-Regular.ttf");
    }

    private static FontLocation? ResolveWindowsArialPath(string lowerName)
    {
        // The original looked for the Windows fonts folder through a WSL mount; native Windows
        // installs are covered by the system font scan.
        const string windowsFonts = "/mnt/c/Windows/Fonts";
        if (!Directory.Exists(windowsFonts))
            return null;
        bool bold = Has(lowerName, "bold");
        bool italic = Has(lowerName, "italic") || Has(lowerName, "oblique");
        string file = bold && italic ? "arialbi.ttf" : bold ? "arialbd.ttf" : italic ? "ariali.ttf" : "arial.ttf";
        string candidate = Path.Combine(windowsFonts, file);
        return File.Exists(candidate) ? FontLocation.File(candidate) : null;
    }

    private (int Units, IReadOnlyDictionary<int, double> CodeWidths) LoadTtfMetrics(FontLocation path)
    {
        var metrics = _fontCache.MetricsFor(path);
        return (metrics.UnitsPerEm, metrics.CodeWidths);
    }

    internal static string NormalizeFontType(object? value)
    {
        if (value is PsName name)
            value = name.Value;
        if (PsOps.IsNumeric(value))
        {
            long v = Internal.Py.Int(PsOps.ToDouble(value));
            switch (v)
            {
                case 0:
                    return "Type0";
                case 1:
                    return "Type1";
                case 3:
                    return "Type3";
                case 42:
                    return "Type42";
            }
        }
        if (value is string s)
        {
            string normalized = s.TrimStart('/');
            if (normalized is "Type0" or "CIDKeyed" or "Type1" or "Type3" or "Type42")
                return normalized;
            if (normalized.Length > 0 && normalized.All(Internal.Py.IsDigit) && Internal.Py.TryInt(normalized, 10, out long parsed))
                return NormalizeFontType(parsed);
        }
        throw new PsTypeError("unsupported font type");
    }
}
