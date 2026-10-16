// Ported from pdf/writer.py: a PDF 1.4 writer for render-model output.

using System.Globalization;
using System.Text;
using XRay.PS.Fonts;
using XRay.PS.Internal;
using XRay.PS.Rendering;
using static XRay.PS.Pdf.PdfFormat;

namespace XRay.PS.Pdf;

/// <summary>Fields of the PDF document information dictionary.</summary>
public sealed record PdfMetadata(string Title, string Creator, string Producer, string CreationDate, string ModDate, bool Trapped);

/// <summary>Serializes a render document into PDF 1.4 bytes.</summary>
public sealed class PdfWriter
{
    private static readonly HashSet<string> StandardFonts =
    [
        "Courier", "Courier-Bold", "Courier-Oblique", "Courier-BoldOblique",
        "Helvetica", "Helvetica-Bold", "Helvetica-Oblique", "Helvetica-BoldOblique",
        "Times-Roman", "Times-Bold", "Times-Italic", "Times-BoldItalic",
        "Symbol", "ZapfDingbats",
    ];

    // The writer's own small glyph-name table (it differs from the font module's).
    private static readonly Dictionary<string, string> GlyphNameUnicode = new(StringComparer.Ordinal)
    {
        ["space"] = " ",
        ["ring"] = "˚",
        ["lessequal"] = "≤",
        ["mu"] = "μ",
        ["afii61352"] = "№",
    };

    private static readonly Dictionary<string, int> StandardUnicodeToCode = BuildUnicodeToCodeMap(Encodings.Standard);
    private static readonly Dictionary<string, int> SymbolUnicodeToCode = BuildUnicodeToCodeMap(Encodings.Symbol);
    private static readonly Dictionary<string, int> ZapfUnicodeToCode = BuildUnicodeToCodeMap(Encodings.ZapfDingbats);

    private static readonly IReadOnlyDictionary<string, string> EmptyDefs = new Dictionary<string, string>();
    private static readonly IReadOnlyDictionary<string, string> EmptyVariants = new Dictionary<string, string>();

    private readonly PdfMetadata _metadata;
    private readonly bool _noCompression;
    private readonly Func<string, RenderImageResource?>? _imageProvider;
    private readonly Func<string, ISet<int>, PdfEmbeddedFont?>? _fontProvider;

    /// <summary>Creates a writer.</summary>
    /// <param name="metadata">Document information.</param>
    /// <param name="noCompression">Write streams uncompressed.</param>
    /// <param name="imageProvider">Supplies the payload of each image id the document uses.</param>
    /// <param name="fontProvider">Supplies an embedded font for a font reference and the codes it uses.</param>
    public PdfWriter(PdfMetadata metadata, bool noCompression = false,
        Func<string, RenderImageResource?>? imageProvider = null,
        Func<string, ISet<int>, PdfEmbeddedFont?>? fontProvider = null)
    {
        _metadata = metadata;
        _noCompression = noCompression;
        _imageProvider = imageProvider;
        _fontProvider = fontProvider;
    }

    private readonly record struct ExtGStateSpec(double FillOpacity, double StrokeOpacity, bool StrokeAdjust = false);

    private static readonly ExtGStateSpec DefaultSpec = new(1.0, 1.0);

    private sealed class Resources
    {
        public readonly Dictionary<string, string> FontResources = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> FontMap = new(StringComparer.Ordinal);
        public readonly Dictionary<string, PdfEmbeddedFont> EmbeddedFonts = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> FontBase = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> ImageMap = new(StringComparer.Ordinal);
        public readonly Dictionary<string, RenderImageResource> ImageResources = new(StringComparer.Ordinal);
        public readonly Dictionary<string, IReadOnlyDictionary<int, int>> FontCodeMaps = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> FontCodeWidths = new(StringComparer.Ordinal);
    }

    /// <summary>Serializes a render document to PDF 1.4 bytes.</summary>
    public byte[] Write(RenderDocument document)
    {
        var res = CollectResources(document);
        var colorSpaces = document.Resources.ColorSpaces;
        var patterns = document.Resources.Patterns;
        var functions = document.Resources.Functions;
        var (pdfPatterns, patternVariants) = PreparePdfPatterns(document, patterns, colorSpaces);
        var patternSpaceIds = PreparePatternColorSpaces(document, colorSpaces);
        var fontOrder = res.FontResources.Keys.ToList();
        var imageOrder = res.ImageMap.Keys.ToList();

        var ids = new ObjectIds(fontOrder, res.ImageResources, imageOrder, colorSpaces, pdfPatterns, functions, document.Pages.Count, res.EmbeddedFonts);
        var lookup = new Lookup(ids, functions, colorSpaces);

        var objects = new List<(int Id, byte[] Data)>();
        void Add(int id, byte[] body) => objects.Add((id, SerializeObject(id, body)));

        foreach (string fontKey in fontOrder)
        {
            if (res.EmbeddedFonts.TryGetValue(fontKey, out var embedded))
                Add(ids.Fonts[fontKey], FontObjectEmbedded(embedded, ids.FontDescriptors[fontKey], ids.ToUnicode[fontKey], ids.CidFonts.TryGetValue(fontKey, out int cid) ? cid : null));
            else
                Add(ids.Fonts[fontKey], FontObject(res.FontBase[fontKey]));
        }
        foreach (string fontKey in ids.EmbeddedFonts)
            Add(ids.FontDescriptors[fontKey], FontDescriptorObject(res.EmbeddedFonts[fontKey], ids.FontFiles[fontKey]));
        foreach (string fontKey in ids.EmbeddedFonts)
            Add(ids.FontFiles[fontKey], FontFileObject(res.EmbeddedFonts[fontKey], _noCompression));
        foreach (string fontKey in ids.EmbeddedFonts)
            Add(ids.ToUnicode[fontKey], ToUnicodeObject(res.EmbeddedFonts[fontKey], _noCompression));
        foreach (string fontKey in ids.EmbeddedFonts)
        {
            if (!ids.CidFonts.TryGetValue(fontKey, out int cidFontId) || !ids.CidToGidMaps.TryGetValue(fontKey, out int cidMapId))
                continue;
            var embedded = res.EmbeddedFonts[fontKey];
            Add(cidMapId, CidToGidMapObject(embedded, _noCompression));
            Add(cidFontId, CidFontObject(embedded, ids.FontDescriptors[fontKey], cidMapId));
        }
        foreach (string imageId in imageOrder)
        {
            var image = res.ImageResources[imageId];
            bool hasSoftMask = ids.ImageSoftMasks.TryGetValue(imageId, out int softMaskId);
            if (hasSoftMask && image.SoftMask is not null)
                Add(softMaskId, SoftMaskImageObject(image.Width, image.Height, image.SoftMask, _noCompression));
            Add(ids.Images[imageId], ImageObject(image, _noCompression, hasSoftMask ? $"{softMaskId} 0 R" : null));
        }
        foreach (var (funcId, function) in functions)
            Add(ids.Functions[funcId], FunctionObject(function, lookup));
        foreach (var (csId, colorSpace) in colorSpaces)
        {
            if (ids.IccProfiles.TryGetValue(csId, out int iccId))
                Add(iccId, IccProfileObject(colorSpace));
            Add(ids.ColorSpaces[csId], Ascii(SerializeColorSpace(colorSpace, lookup)));
        }
        foreach (var (patternId, pattern) in pdfPatterns)
            Add(ids.Patterns[patternId], PatternObject(pattern, lookup, res));

        var colorSpaceDefs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (csId, colorSpace) in colorSpaces)
            colorSpaceDefs[csId] = SerializeColorSpace(colorSpace, lookup);
        var pageExtGStates = new List<Dictionary<string, ExtGStateSpec>>();
        for (int pageIndex = 0; pageIndex < document.Pages.Count; pageIndex++)
        {
            var page = document.Pages[pageIndex];
            var extgstates = CollectCommandExtGStates(page.Commands);
            pageExtGStates.Add(extgstates);
            byte[] content = RenderPage(page, res, colorSpaceDefs, patternVariants, patternSpaceIds, extgstates);
            Add(ids.Contents[pageIndex], ContentObject(content, _noCompression));
        }
        for (int pageIndex = 0; pageIndex < document.Pages.Count; pageIndex++)
        {
            Add(ids.Pages[pageIndex], PageObject(document.Pages[pageIndex], ids.PagesTree, ids.Contents[pageIndex], res.FontResources,
                res.ImageMap, ids.Fonts, ids.Images, ids.ColorSpaces, ids.Patterns, pageExtGStates[pageIndex]));
        }
        Add(ids.PagesTree, PagesTreeObject(ids.Pages));
        Add(ids.Catalog, CatalogObject(ids.PagesTree));
        Add(ids.Info, InfoObject(_metadata));

        objects.Sort((a, b) => a.Id.CompareTo(b.Id));
        return SerializePdf(objects.Select(o => o.Data).ToList(), ids.Catalog, ids.Info);
    }

    private Resources CollectResources(RenderDocument document)
    {
        var res = new Resources();
        var usedCodes = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);

        void EnsureImage(string imageId)
        {
            if (_imageProvider is null)
                throw Py.ValueError("image provider is required for image commands");
            if (!res.ImageResources.ContainsKey(imageId))
            {
                var image = _imageProvider(imageId) ?? throw Py.ValueError("image provider returned no data");
                res.ImageResources[imageId] = image;
            }
            if (!res.ImageMap.ContainsKey(imageId))
                res.ImageMap[imageId] = $"Im{res.ImageMap.Count + 1}";
        }

        void Visit(RenderCommand command)
        {
            if (command is TextCommand text)
            {
                if (!usedCodes.TryGetValue(text.FontRef, out var codes))
                    usedCodes[text.FontRef] = codes = [];
                foreach (string ch in Py.Chars(text.Text))
                {
                    int code = Py.Ord(ch);
                    if (code is 10 or 13)
                        continue;
                    codes.Add(code);
                }
            }
            if (command is ImageCommand image)
                EnsureImage(image.ImageId);
        }

        foreach (var page in document.Pages)
        {
            foreach (var command in page.Commands)
                Visit(command);
        }
        foreach (var pattern in document.Resources.Patterns.Values)
        {
            if (pattern is not TilingPattern tiling)
                continue;
            foreach (var command in tiling.Commands)
                Visit(command);
        }

        foreach (var (fontRef, codes) in usedCodes)
        {
            PdfEmbeddedFont? embedded = _fontProvider?.Invoke(fontRef, codes);
            string fontKey;
            if (embedded is not null)
            {
                fontKey = fontRef;
                res.EmbeddedFonts[fontKey] = embedded;
                res.FontCodeMaps[fontRef] = new Dictionary<int, int>(embedded.CharCodeMap);
                res.FontCodeWidths[fontRef] = embedded.CodeByteWidth;
            }
            else
            {
                fontKey = ResolveFont(fontRef);
                res.FontBase[fontKey] = fontKey;
            }
            if (!res.FontResources.ContainsKey(fontKey))
                res.FontResources[fontKey] = $"F{res.FontResources.Count + 1}";
            res.FontMap[fontRef] = res.FontResources[fontKey];
            res.FontCodeWidths.TryAdd(fontRef, 1);
        }
        return res;
    }

    private static byte[] RenderPage(RenderPage page, Resources res, IReadOnlyDictionary<string, string> colorSpaceDefs,
        IReadOnlyDictionary<string, string> patternVariants, IReadOnlyDictionary<string, string> patternSpaceIds,
        Dictionary<string, ExtGStateSpec> extgstates)
    {
        var lines = new List<string>();
        var byValues = new Dictionary<ExtGStateSpec, string>();
        foreach (var (name, value) in extgstates)
            byValues[value] = name;
        foreach (var command in page.Commands)
        {
            switch (command)
            {
                case PathCommand path:
                {
                    var values = new ExtGStateSpec(ClampOpacity(path.FillOpacity), ClampOpacity(path.StrokeOpacity), ShouldStrokeAdjust(path));
                    WithState(lines, byValues, values, () => RenderPathCommand(lines, path, colorSpaceDefs, patternVariants, patternSpaceIds));
                    break;
                }
                case ClipCommand clip:
                    RenderPathSegments(lines, clip.Path);
                    lines.Add(clip.FillRule == "evenodd" ? "W*" : "W");
                    lines.Add("n");
                    break;
                case StateSaveCommand:
                    lines.Add("q");
                    break;
                case StateRestoreCommand:
                    lines.Add("Q");
                    break;
                case TextCommand text:
                {
                    var values = new ExtGStateSpec(ClampOpacity(text.FillOpacity), 1.0);
                    WithState(lines, byValues, values, () => RenderTextCommand(lines, text, res.FontMap, colorSpaceDefs, patternVariants, patternSpaceIds, res.FontCodeMaps, res.FontCodeWidths));
                    break;
                }
                case ImageCommand image:
                {
                    res.ImageResources.TryGetValue(image.ImageId, out var resource);
                    bool isCcittMask = resource is not null && resource.Mask && resource.Filter == "CCITTFaxDecode";
                    var values = new ExtGStateSpec(ClampOpacity(image.Opacity), ClampOpacity(image.Opacity));
                    WithState(lines, byValues, values, () => RenderImageCommand(lines, image, res.ImageMap, isCcittMask));
                    break;
                }
            }
        }
        return Latin1Replace(string.Join("\n", lines) + "\n");
    }

    private static void WithState(List<string> lines, Dictionary<ExtGStateSpec, string> byValues, ExtGStateSpec values, Action render)
    {
        if (byValues.TryGetValue(values, out string? name))
        {
            lines.Add("q");
            lines.Add($"/{name} gs");
            render();
            lines.Add("Q");
        }
        else
        {
            render();
        }
    }

    // Some renderers ignore base color components on uncolored tiling patterns, so a colored
    // variant is generated per base color and referenced while painting.
    private static (Dictionary<string, Pattern> Patterns, Dictionary<string, string> Variants) PreparePdfPatterns(
        RenderDocument document, Dictionary<string, Pattern> patterns, Dictionary<string, ColorSpace> colorSpaces)
    {
        var pdfPatterns = new Dictionary<string, Pattern>(patterns);
        var variants = new Dictionary<string, string>(StringComparer.Ordinal);
        int counter = 1;
        foreach (var page in document.Pages)
        {
            foreach (var command in page.Commands)
            {
                foreach (var paint in CommandPaints(command))
                {
                    if (paint.Kind != "Pattern" || paint.Value is not PatternPaint pp)
                        continue;
                    if (pp.BaseSpaceId is null || pp.BaseComponents is null)
                        continue;
                    if (!colorSpaces.TryGetValue(pp.BaseSpaceId, out var baseSpace) || baseSpace is not DeviceColorSpace device)
                        continue;
                    var baseComponents = NormalizeDeviceComponents(device.Name, pp.BaseComponents);
                    string key = VariantKey(pp.PatternId, pp.BaseSpaceId, pp.BaseComponents);
                    if (variants.ContainsKey(key))
                        continue;
                    if (!patterns.TryGetValue(pp.PatternId, out var basePattern) || basePattern is not TilingPattern tiling || tiling.PaintType != 2)
                        continue;
                    string variantId = $"{pp.PatternId}C{counter}";
                    counter++;
                    pdfPatterns[variantId] = ColorizeTilingPattern(tiling, device.Name, baseComponents);
                    variants[key] = variantId;
                }
            }
        }
        return (pdfPatterns, variants);
    }

    private static string VariantKey(string patternId, string baseSpaceId, double[] components) =>
        patternId + "\u0001" + baseSpaceId + "\u0001" + string.Join(",", components.Select(c => (c == 0 ? 0.0 : c).ToString("R", CultureInfo.InvariantCulture)));

    private static IEnumerable<Paint> CommandPaints(RenderCommand command)
    {
        if (command is PathCommand path)
        {
            if (path.Fill is not null)
                yield return path.Fill;
            if (path.StrokePaint is not null)
                yield return path.StrokePaint;
        }
        else if (command is TextCommand text && text.Fill is not null)
        {
            yield return text.Fill;
        }
    }

    // Registers Pattern color spaces for uncolored tiling patterns; maps a base space id to
    // the Pattern color space id.
    private static Dictionary<string, string> PreparePatternColorSpaces(RenderDocument document, Dictionary<string, ColorSpace> colorSpaces)
    {
        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        string Register(ColorSpace value)
        {
            string wanted = StructuralKey.Of(value);
            foreach (var (key, existing) in colorSpaces)
            {
                if (StructuralKey.Of(existing) == wanted)
                    return key;
            }
            string id = $"CS{colorSpaces.Count + 1}";
            colorSpaces[id] = value;
            return id;
        }
        foreach (var page in document.Pages)
        {
            foreach (var command in page.Commands)
            {
                foreach (var paint in CommandPaints(command))
                {
                    if (paint.Kind != "Pattern" || paint.Value is not PatternPaint pp)
                        continue;
                    string? baseId = pp.BaseSpaceId;
                    if (baseId is null || mapping.ContainsKey(baseId))
                        continue;
                    if (!colorSpaces.TryGetValue(baseId, out var baseSpace))
                        continue;
                    mapping[baseId] = Register(new PatternColorSpace(baseSpace));
                }
            }
        }
        return mapping;
    }

    private static double[] NormalizeDeviceComponents(string spaceName, double[] components)
    {
        int target = spaceName switch
        {
            "DeviceRGB" => 3,
            "DeviceCMYK" => 4,
            _ => 1,
        };
        var values = components.ToList();
        while (values.Count < target)
            values.Add(0.0);
        return values.Take(target).ToArray();
    }

    private static TilingPattern ColorizeTilingPattern(TilingPattern pattern, string spaceName, double[] components)
    {
        var colored = new List<RenderCommand>();
        object paintValue = components.Length == 1 ? components[0] : components;
        var paint = new Paint(spaceName, paintValue);
        foreach (var command in pattern.Commands)
        {
            switch (command)
            {
                case PathCommand path:
                {
                    var fill = path.Fill is { Kind: "PatternBase" } ? paint : path.Fill;
                    colored.Add(path with { Fill = fill });
                    break;
                }
                case TextCommand text:
                {
                    var fill = text.Fill is { Kind: "PatternBase" } ? paint : text.Fill;
                    // The original rebuilt the command from its first six fields.
                    colored.Add(new TextCommand(text.Text, text.FontRef, text.FontSize, text.Matrix, fill, text.FillOpacity));
                    break;
                }
                default:
                    colored.Add(command);
                    break;
            }
        }
        return new TilingPattern(1, pattern.TilingType, pattern.BBox, pattern.XStep, pattern.YStep, pattern.Matrix, colored);
    }

    private sealed class ObjectIds
    {
        public readonly Dictionary<string, int> Fonts = new(StringComparer.Ordinal);
        public readonly List<string> EmbeddedFonts;
        public readonly Dictionary<string, int> FontDescriptors = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> FontFiles = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> ToUnicode = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> CidFonts = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> CidToGidMaps = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> Images = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> ImageSoftMasks = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> Functions = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> IccProfiles = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> ColorSpaces = new(StringComparer.Ordinal);
        public readonly Dictionary<string, int> Patterns = new(StringComparer.Ordinal);
        public readonly List<int> Contents = [];
        public readonly List<int> Pages = [];
        public readonly int PagesTree;
        public readonly int Catalog;
        public readonly int Info;

        public ObjectIds(List<string> fonts, Dictionary<string, RenderImageResource> imageResources, List<string> images,
            Dictionary<string, ColorSpace> colorSpaces, Dictionary<string, Pattern> patterns, Dictionary<string, Function> functions,
            int pageCount, Dictionary<string, PdfEmbeddedFont> embeddedFonts)
        {
            int current = 1;
            foreach (string font in fonts)
                Fonts[font] = current++;
            EmbeddedFonts = [.. embeddedFonts.Keys];
            foreach (string fontKey in EmbeddedFonts)
            {
                FontDescriptors[fontKey] = current++;
                FontFiles[fontKey] = current++;
                ToUnicode[fontKey] = current++;
                if (embeddedFonts.TryGetValue(fontKey, out var resource) && resource.Subtype == "Type0")
                {
                    CidFonts[fontKey] = current++;
                    CidToGidMaps[fontKey] = current++;
                }
            }
            foreach (string image in images)
            {
                Images[image] = current++;
                if (imageResources.TryGetValue(image, out var resource) && resource.SoftMask is not null)
                    ImageSoftMasks[image] = current++;
            }
            foreach (string funcId in functions.Keys)
                Functions[funcId] = current++;
            foreach (var (csId, colorSpace) in colorSpaces)
            {
                if (colorSpace is CieBasedColorSpace)
                    IccProfiles[csId] = current++;
                ColorSpaces[csId] = current++;
            }
            foreach (string patternId in patterns.Keys)
                Patterns[patternId] = current++;
            for (int i = 0; i < pageCount; i++)
                Contents.Add(current++);
            for (int i = 0; i < pageCount; i++)
                Pages.Add(current++);
            PagesTree = current++;
            Catalog = current++;
            Info = current++;
        }
    }

    /// <summary>Resolves registered functions and color spaces by value, as the original did
    /// with <c>==</c> scans.</summary>
    private sealed class Lookup(ObjectIds ids, Dictionary<string, Function> functions, Dictionary<string, ColorSpace> colorSpaces)
    {
        private Dictionary<string, string>? _functionKeys;
        private Dictionary<string, string>? _colorSpaceKeys;

        public ObjectIds Ids { get; } = ids;

        public string FunctionRef(Function function)
        {
            _functionKeys ??= BuildIndex(functions);
            if (!_functionKeys.TryGetValue(StructuralKey.Of(function), out string? id))
                throw Py.ValueError("function not registered");
            return $"{Ids.Functions[id]} 0 R";
        }

        public string? ColorSpaceId(ColorSpace colorSpace)
        {
            _colorSpaceKeys ??= BuildIndex(colorSpaces);
            return _colorSpaceKeys.GetValueOrDefault(StructuralKey.Of(colorSpace));
        }

        private static Dictionary<string, string> BuildIndex<T>(Dictionary<string, T> items) where T : notnull
        {
            var index = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in items)
                index.TryAdd(StructuralKey.Of(value), key);
            return index;
        }
    }

    private static byte[] SerializePdf(List<byte[]> objects, int catalogId, int infoId)
    {
        byte[] header = "%PDF-1.4\n"u8.ToArray();
        using var ms = new MemoryStream();
        ms.Write(header);
        var offsets = new List<long>();
        foreach (var obj in objects)
        {
            offsets.Add(ms.Position);
            ms.Write(obj);
        }
        long xrefOffset = ms.Position;
        var xref = new StringBuilder();
        xref.Append("xref\n").Append(CultureInfo.InvariantCulture, $"0 {objects.Count + 1}\n").Append("0000000000 65535 f \n");
        foreach (long offset in offsets)
            xref.Append(offset.ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
        ms.Write(Encoding.ASCII.GetBytes(xref.ToString()));
        string trailerDict = PdfDict([$"/Size {objects.Count + 1}", $"/Root {catalogId} 0 R", $"/Info {infoId} 0 R"]);
        ms.Write(Encoding.ASCII.GetBytes("trailer\n" + trailerDict + "\nstartxref\n" + xrefOffset.ToString(CultureInfo.InvariantCulture) + "\n%%EOF\n"));
        return ms.ToArray();
    }

    private static byte[] SerializeObject(int id, byte[] body)
    {
        byte[] head = Encoding.ASCII.GetBytes($"{id} 0 obj\n");
        byte[] tail = "\nendobj\n"u8.ToArray();
        var result = new byte[head.Length + body.Length + tail.Length];
        head.CopyTo(result, 0);
        body.CopyTo(result, head.Length);
        tail.CopyTo(result, head.Length + body.Length);
        return result;
    }

    private static string PdfDict(IReadOnlyList<string> lines) =>
        lines.Count == 0 ? "<<>>" : "<<\n" + string.Join("\n", lines) + "\n>>";

    private static byte[] FontObject(string baseFont) =>
        Ascii(PdfDict(["/Type /Font", "/Subtype /Type1", $"/BaseFont /{baseFont}"]));

    private static byte[] FontObjectEmbedded(PdfEmbeddedFont embedded, int descId, int toUnicodeId, int? cidFontId)
    {
        if (embedded.Subtype == "Type0")
        {
            if (cidFontId is null)
                throw Py.ValueError("Type0 embedded font requires CID descendant");
            return Ascii(PdfDict([
                "/Type /Font",
                "/Subtype /Type0",
                $"/BaseFont /{embedded.SubsetName}",
                $"/Encoding /{(string.IsNullOrEmpty(embedded.Encoding) ? "Identity-H" : embedded.Encoding)}",
                $"/DescendantFonts [{cidFontId} 0 R]",
                $"/ToUnicode {toUnicodeId} 0 R",
            ]));
        }
        var lines = new List<string>
        {
            "/Type /Font",
            $"/Subtype /{embedded.Subtype}",
            $"/BaseFont /{embedded.SubsetName}",
            $"/FirstChar {embedded.FirstChar}",
            $"/LastChar {embedded.LastChar}",
            $"/Widths {IntArray(embedded.Widths)}",
            $"/FontDescriptor {descId} 0 R",
            $"/ToUnicode {toUnicodeId} 0 R",
        };
        if (!string.IsNullOrEmpty(embedded.Encoding))
            lines.Add($"/Encoding /{embedded.Encoding}");
        return Ascii(PdfDict(lines));
    }

    private static byte[] FontDescriptorObject(PdfEmbeddedFont embedded, int fontFileId)
    {
        var b = embedded.BBox;
        string bbox = IntArray([b.XMin, b.YMin, b.XMax, b.YMax]);
        int flags = embedded.Symbolic ? 4 : 32;
        if (embedded.ItalicAngle != 0)
            flags |= 64;
        return Ascii(PdfDict([
            "/Type /FontDescriptor",
            $"/FontName /{embedded.SubsetName}",
            $"/Flags {flags}",
            $"/FontBBox {bbox}",
            $"/ItalicAngle {embedded.ItalicAngle}",
            $"/Ascent {embedded.Ascent}",
            $"/Descent {embedded.Descent}",
            $"/CapHeight {embedded.Ascent}",
            $"/StemV {embedded.StemV}",
            $"/{embedded.FontFileKey} {fontFileId} 0 R",
        ]));
    }

    private static byte[] CidFontObject(PdfEmbeddedFont embedded, int descId, int cidMapId)
    {
        string widths = embedded.Widths.Count == 0 ? "[]" : $"[{embedded.FirstChar} {IntArray(embedded.Widths)}]";
        return Ascii(PdfDict([
            "/Type /Font",
            "/Subtype /CIDFontType2",
            $"/BaseFont /{embedded.SubsetName}",
            "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >>",
            $"/FontDescriptor {descId} 0 R",
            "/DW 1000",
            $"/W {widths}",
            $"/CIDToGIDMap {cidMapId} 0 R",
        ]));
    }

    private static byte[] CidToGidMapObject(PdfEmbeddedFont embedded, bool noCompression)
    {
        byte[] data = embedded.CidToGidMap ?? [];
        if (!noCompression)
            return StreamObject(Zlib.Compress(data), ["/Filter /FlateDecode"]);
        return StreamObject(data, []);
    }

    private static byte[] FontFileObject(PdfEmbeddedFont embedded, bool noCompression)
    {
        byte[] data = embedded.FontFile;
        var lines = new List<string> { $"/Length1 {data.Length}" };
        if (!noCompression)
        {
            data = Zlib.Compress(data);
            lines.Add("/Filter /FlateDecode");
        }
        return StreamObject(data, lines);
    }

    private static byte[] ToUnicodeObject(PdfEmbeddedFont embedded, bool noCompression)
    {
        byte[] data = Encoding.UTF8.GetBytes(embedded.ToUnicode);
        if (!noCompression)
            return StreamObject(Zlib.Compress(data), ["/Filter /FlateDecode"]);
        return StreamObject(data, []);
    }

    private static byte[] ImageObject(RenderImageResource resource, bool noCompression, string? softMaskRef)
    {
        byte[] data = resource.Data;
        string? filterName = resource.Filter;
        if (filterName is null && !noCompression)
        {
            data = Zlib.Compress(data);
            filterName = "FlateDecode";
        }
        var lines = new List<string>
        {
            "/Type /XObject",
            "/Subtype /Image",
            $"/Width {resource.Width}",
            $"/Height {resource.Height}",
        };
        if (resource.Mask)
        {
            lines.Add("/ImageMask true");
            lines.Add("/BitsPerComponent 1");
            // Stencil polarity differs between raw mask bytes and CCITT-decoded mask streams:
            // raw bytes need Decode [1 0], CCITT streams already align.
            bool isCcittMask = resource.Filter == "CCITTFaxDecode";
            if ((!isCcittMask && resource.MaskPolarity) || (isCcittMask && !resource.MaskPolarity))
                lines.Add("/Decode [1 0]");
        }
        else
        {
            lines.Add($"/ColorSpace /{resource.ColorSpace}");
            lines.Add($"/BitsPerComponent {resource.BitsPerComponent}");
            var decode = NormalizeDecodeForPdf(resource.Decode, resource.BitsPerComponent);
            if (decode is not null)
                lines.Add($"/Decode {NumberArray(decode)}");
            if (softMaskRef is not null)
                lines.Add($"/SMask {softMaskRef}");
        }
        if (filterName is not null)
        {
            lines.Add($"/Filter /{filterName}");
            string? decodeParams = DecodeParams(resource);
            if (decodeParams is not null)
                lines.Add($"/DecodeParms {decodeParams}");
        }
        return StreamObject(data, lines);
    }

    private static byte[] SoftMaskImageObject(int width, int height, byte[] data, bool noCompression)
    {
        var lines = new List<string>
        {
            "/Type /XObject",
            "/Subtype /Image",
            $"/Width {width}",
            $"/Height {height}",
            "/ColorSpace /DeviceGray",
            "/BitsPerComponent 8",
        };
        if (!noCompression)
        {
            data = Zlib.Compress(data);
            lines.Add("/Filter /FlateDecode");
        }
        return StreamObject(data, lines);
    }

    private static string? DecodeParams(RenderImageResource resource)
    {
        if (resource.FilterParams is not { Count: > 0 } source)
            return null;
        var parameters = new Dictionary<string, object?>(source, StringComparer.Ordinal);
        if (resource.Mask && resource.Filter == "CCITTFaxDecode" && parameters.GetValueOrDefault("BlackIs1") is bool blackIs1)
            parameters["BlackIs1"] = !blackIs1;
        var parts = parameters.Keys.Order(StringComparer.Ordinal).Select(k => $"/{k} {PdfObject(parameters[k])}");
        return "<< " + string.Join(" ", parts) + " >>";
    }

    private static string PdfObject(object? value) => value switch
    {
        bool b => b ? "true" : "false",
        int i => Number(i),
        long l => Number(l),
        double d => Number(d),
        byte[] bytes => HexBytes(bytes),
        string s => s.StartsWith('/') ? s : "/" + s,
        IDictionary<string, object?> dict => "<< " + string.Join(" ", dict.Keys.Order(StringComparer.Ordinal).Select(k => $"/{k} {PdfObject(dict[k])}")) + " >>",
        System.Collections.IEnumerable list => "[" + string.Join(" ", list.Cast<object?>().Select(PdfObject)) + "]",
        _ => throw new HostErrorException("TypeError", "unsupported PDF object value"),
    };

    private static double[]? NormalizeDecodeForPdf(double[]? decode, int bitsPerComponent)
    {
        if (decode is null || decode.Length == 0)
            return null;
        if (decode.Max(Math.Abs) <= 1.0)
            return decode;
        int bpc = Math.Max(1, bitsPerComponent);
        double maxSample = (double)((1L << bpc) - 1);
        if (maxSample <= 0.0)
            return decode;
        return decode.Select(v => v / maxSample).ToArray();
    }

    private static byte[] ContentObject(byte[] content, bool noCompression)
    {
        if (!noCompression)
            return StreamObject(Zlib.Compress(content), ["/Filter /FlateDecode"]);
        return StreamObject(content, []);
    }

    private static byte[] StreamObject(byte[] data, IReadOnlyList<string> dictLines)
    {
        var lines = dictLines.ToList();
        lines.Add($"/Length {data.Length}");
        byte[] head = Ascii(PdfDict(lines) + "\nstream\n");
        byte[] tail = "\nendstream"u8.ToArray();
        var result = new byte[head.Length + data.Length + tail.Length];
        head.CopyTo(result, 0);
        data.CopyTo(result, head.Length);
        tail.CopyTo(result, head.Length + data.Length);
        return result;
    }

    private static byte[] PageObject(RenderPage page, int parentId, int contentId, Dictionary<string, string> fontMap,
        Dictionary<string, string> imageMap, Dictionary<string, int> fontIds, Dictionary<string, int> imageIds,
        Dictionary<string, int> colorSpaceIds, Dictionary<string, int> patternIds, Dictionary<string, ExtGStateSpec> extgstates)
    {
        var resources = new List<string>();
        if (fontMap.Count > 0)
        {
            var entries = fontMap.Select(kv => fontIds.TryGetValue(kv.Key, out int id)
                ? $"/{kv.Value} {id} 0 R"
                : throw Py.KeyError($"'{kv.Key}'"));
            resources.Add("/Font << " + string.Join(" ", entries) + " >>");
        }
        if (imageMap.Count > 0)
            resources.Add("/XObject << " + string.Join(" ", imageMap.Select(kv => $"/{kv.Value} {imageIds[kv.Key]} 0 R")) + " >>");
        if (colorSpaceIds.Count > 0)
            resources.Add("/ColorSpace << " + string.Join(" ", colorSpaceIds.Select(kv => $"/{kv.Key} {kv.Value} 0 R")) + " >>");
        if (patternIds.Count > 0)
            resources.Add("/Pattern << " + string.Join(" ", patternIds.Select(kv => $"/{kv.Key} {kv.Value} 0 R")) + " >>");
        if (extgstates.Count > 0)
            resources.Add("/ExtGState << " + ExtGStateEntries(extgstates) + " >>");
        string resourcesDict = resources.Count > 0 ? "<< " + string.Join(" ", resources) + " >>" : "<<>>";
        return Ascii(PdfDict([
            "/Type /Page",
            $"/Parent {parentId} 0 R",
            $"/MediaBox [{FormatRect(new Rect(0.0, 0.0, page.Width, page.Height))}]",
            $"/Resources {resourcesDict}",
            $"/Contents {contentId} 0 R",
        ]));
    }

    private static string ExtGStateEntries(Dictionary<string, ExtGStateSpec> extgstates) =>
        string.Join(" ", extgstates.Select(kv =>
            $"/{kv.Key} << /Type /ExtGState /ca {Number(kv.Value.FillOpacity)} /CA {Number(kv.Value.StrokeOpacity)}{(kv.Value.StrokeAdjust ? " /SA true" : "")} >>"));

    private static double ClampOpacity(double value)
    {
        if (value < 0.0)
            return 0.0;
        if (value > 1.0)
            return 1.0;
        return value;
    }

    private static Dictionary<string, ExtGStateSpec> CollectCommandExtGStates(IEnumerable<RenderCommand> commands)
    {
        var unique = new List<ExtGStateSpec>();
        var seen = new HashSet<ExtGStateSpec>();
        foreach (var command in commands)
        {
            ExtGStateSpec? spec = command switch
            {
                PathCommand path => new ExtGStateSpec(ClampOpacity(path.FillOpacity), ClampOpacity(path.StrokeOpacity), ShouldStrokeAdjust(path)),
                TextCommand text => new ExtGStateSpec(ClampOpacity(text.FillOpacity), 1.0),
                ImageCommand image => new ExtGStateSpec(ClampOpacity(image.Opacity), ClampOpacity(image.Opacity)),
                _ => null,
            };
            if (spec is not ExtGStateSpec s || s == DefaultSpec)
                continue;
            if (seen.Add(s))
                unique.Add(s);
        }
        var result = new Dictionary<string, ExtGStateSpec>(StringComparer.Ordinal);
        for (int i = 0; i < unique.Count; i++)
            result[$"GS{i + 1}"] = unique[i];
        return result;
    }

    private static byte[] PagesTreeObject(List<int> pageIds) =>
        Ascii(PdfDict(["/Type /Pages", $"/Kids [{string.Join(" ", pageIds.Select(id => $"{id} 0 R"))}]", $"/Count {pageIds.Count}"]));

    private static byte[] CatalogObject(int pagesTreeId) => Ascii(PdfDict(["/Type /Catalog", $"/Pages {pagesTreeId} 0 R"]));

    private static byte[] InfoObject(PdfMetadata metadata) => Ascii(PdfDict([
        $"/Title ({EscapeString(metadata.Title)})",
        $"/Creator ({EscapeString(metadata.Creator)})",
        $"/Producer ({EscapeString(metadata.Producer)})",
        $"/CreationDate ({EscapeString(metadata.CreationDate)})",
        $"/ModDate ({EscapeString(metadata.ModDate)})",
        $"/Trapped {(metadata.Trapped ? "true" : "false")}",
    ]));

    private static byte[] FunctionObject(Function function, Lookup lookup)
    {
        switch (function)
        {
            case SampledFunction sampled:
            {
                var lines = new List<string>
                {
                    "/FunctionType 0",
                    $"/Domain {NumberArray(sampled.Domain)}",
                    $"/Range {NumberArray(sampled.Range)}",
                    $"/Size {IntArray(sampled.Size)}",
                    $"/BitsPerSample {sampled.BitsPerSample}",
                };
                if (sampled.Order != 0 && sampled.Order != 1)
                    lines.Add($"/Order {sampled.Order}");
                if (sampled.Encode.Length > 0)
                    lines.Add($"/Encode {NumberArray(sampled.Encode)}");
                if (sampled.Decode.Length > 0)
                    lines.Add($"/Decode {NumberArray(sampled.Decode)}");
                return StreamObject(sampled.Samples, lines);
            }
            case ExponentialFunction exp:
                return Ascii(PdfDict([
                    "/FunctionType 2",
                    $"/Domain {NumberArray(exp.Domain)}",
                    $"/Range {NumberArray(exp.Range)}",
                    $"/C0 {NumberArray(exp.C0)}",
                    $"/C1 {NumberArray(exp.C1)}",
                    $"/N {Number(exp.N)}",
                ]));
            case StitchingFunction stitching:
            {
                var refs = stitching.Functions.Select(lookup.FunctionRef).ToList();
                return Ascii(PdfDict([
                    "/FunctionType 3",
                    $"/Domain {NumberArray(stitching.Domain)}",
                    $"/Range {NumberArray(stitching.Range)}",
                    $"/Functions [{string.Join(" ", refs)}]",
                    $"/Bounds {NumberArray(stitching.Bounds)}",
                    $"/Encode {NumberArray(stitching.Encode)}",
                ]));
            }
            default:
                throw Py.ValueError("unsupported function type");
        }
    }

    private static byte[] IccProfileObject(ColorSpace colorSpace)
    {
        if (colorSpace is not CieBasedColorSpace cie)
            throw Py.ValueError("ICC profile only for CIEBased color spaces");
        return StreamObject(cie.IccProfile, [$"/N {cie.Components}"]);
    }

    private static byte[] PatternObject(Pattern pattern, Lookup lookup, Resources res)
    {
        if (pattern is TilingPattern tiling)
        {
            var extgstates = CollectCommandExtGStates(tiling.Commands);
            byte[] content = RenderPatternCommands(tiling.Commands, res, extgstates);
            // Some renderers do not confine image sampling to the pattern BBox; clip explicitly
            // so paint does not bleed beyond the cell (notably for non-tiling image brushes).
            content = ClipPatternContentToBBox(tiling, content);
            var (tilingType, xStep, yStep) = PdfPatternTilingParams(tiling);
            string xobject = "";
            var imageRefs = new List<string>();
            foreach (string imageId in PatternImageIds(tiling.Commands))
            {
                if (res.ImageMap.TryGetValue(imageId, out string? name) && lookup.Ids.Images.TryGetValue(imageId, out int objId))
                    imageRefs.Add($"/{name} {objId} 0 R");
            }
            if (imageRefs.Count > 0)
                xobject = "/XObject << " + string.Join(" ", imageRefs) + " >> ";
            string fontResources = "";
            var fontRefs = new List<string>();
            foreach (string fontRef in PatternFontRefs(tiling.Commands))
            {
                if (res.FontMap.TryGetValue(fontRef, out string? name) && lookup.Ids.Fonts.TryGetValue(fontRef, out int objId))
                    fontRefs.Add($"/{name} {objId} 0 R");
            }
            if (fontRefs.Count > 0)
                fontResources = "/Font << " + string.Join(" ", fontRefs) + " >> ";
            string extgstateResources = extgstates.Count > 0 ? "/ExtGState << " + ExtGStateEntries(extgstates) + " >> " : "";
            return StreamObject(content, [
                "/Type /Pattern",
                "/PatternType 1",
                $"/PaintType {tiling.PaintType}",
                $"/TilingType {tilingType}",
                $"/BBox {NumberArray(tiling.BBox)}",
                $"/XStep {Number(xStep)}",
                $"/YStep {Number(yStep)}",
                $"/Matrix {NumberArray(tiling.Matrix.ToArray())}",
                $"/Resources << {fontResources}{xobject}{extgstateResources}>>",
            ]);
        }
        if (pattern is ShadingPattern shading)
        {
            string shadingDict = SerializeShading(shading.Shading, lookup);
            return Ascii(PdfDict([
                "/Type /Pattern",
                "/PatternType 2",
                $"/Shading {shadingDict}",
                $"/Matrix {NumberArray(shading.Matrix.ToArray())}",
            ]));
        }
        throw Py.ValueError("unsupported pattern type");
    }

    private static byte[] ClipPatternContentToBBox(TilingPattern pattern, byte[] content)
    {
        double x0 = pattern.BBox[0], y0 = pattern.BBox[1], x1 = pattern.BBox[2], y1 = pattern.BBox[3];
        var sb = new StringBuilder();
        sb.Append("q\n")
            .Append(Number(x0)).Append(' ').Append(Number(y0)).Append(" m\n")
            .Append(Number(x1)).Append(' ').Append(Number(y0)).Append(" l\n")
            .Append(Number(x1)).Append(' ').Append(Number(y1)).Append(" l\n")
            .Append(Number(x0)).Append(' ').Append(Number(y1)).Append(" l\n")
            .Append("h\nW\nn\n");
        string body = Encoding.ASCII.GetString(content);
        sb.Append(body);
        if (!body.EndsWith('\n') && !(body.Length == 0))
            sb.Append('\n');
        sb.Append("Q\n");
        return Ascii(sb.ToString());
    }

    // The render model uses tiling type 0 for a non-tiling cell (XPS TileMode None); PDF only
    // has types 1 to 3, so it is written as type 1 with steps large enough to show one cell.
    private static (int TilingType, double XStep, double YStep) PdfPatternTilingParams(TilingPattern pattern)
    {
        if (pattern.TilingType <= 0)
        {
            double bboxW = Math.Max(1.0, Math.Abs(pattern.BBox[2] - pattern.BBox[0]));
            double bboxH = Math.Max(1.0, Math.Abs(pattern.BBox[3] - pattern.BBox[1]));
            double baseStep = Math.Max(Math.Max(Math.Max(Math.Abs(pattern.XStep), Math.Abs(pattern.YStep)), Math.Max(bboxW, bboxH)), 1.0);
            double large = baseStep * 64.0;
            return (1, large, large);
        }
        return (pattern.TilingType, pattern.XStep, pattern.YStep);
    }

    private static string SerializeColorSpace(ColorSpace colorSpace, Lookup lookup)
    {
        switch (colorSpace)
        {
            case DeviceColorSpace device:
                return "/" + device.Name;
            case PatternColorSpace pattern:
                return pattern.Base is null ? "/Pattern" : $"[/Pattern {SerializeColorSpace(pattern.Base, lookup)}]";
            case IndexedColorSpace indexed:
                return $"[/Indexed {SerializeColorSpace(indexed.Base, lookup)} {indexed.HiVal} {HexBytes(indexed.Lookup)}]";
            case SeparationColorSpace separation:
            {
                string alternate = SerializeColorSpace(separation.Alternate, lookup);
                return $"[/Separation /{separation.Name} {alternate} {lookup.FunctionRef(separation.Tint)}]";
            }
            case DeviceNColorSpace deviceN:
            {
                string names = string.Join(" ", deviceN.Names.Select(n => "/" + n));
                string alternate = SerializeColorSpace(deviceN.Alternate, lookup);
                return $"[/DeviceN [{names}] {alternate} {lookup.FunctionRef(deviceN.Tint)}]";
            }
            case CieBasedColorSpace cie:
            {
                string? csId = lookup.ColorSpaceId(cie);
                if (csId is null || !lookup.Ids.IccProfiles.TryGetValue(csId, out int iccId))
                    return "/DeviceRGB";
                return $"[/ICCBased {iccId} 0 R]";
            }
            default:
                throw Py.ValueError("unsupported color space type");
        }
    }

    private static string SerializeShading(Shading shading, Lookup lookup)
    {
        int type = shading switch
        {
            AxialShading => 2,
            RadialShading => 3,
            _ => throw Py.ValueError("unsupported shading type"),
        };
        double[] coords = shading switch
        {
            AxialShading a => a.Coords,
            RadialShading r => r.Coords,
            _ => [],
        };
        string cs = SerializeColorSpace(shading.ColorSpace, lookup);
        string funcRef = lookup.FunctionRef(shading.Function);
        var lines = new List<string>
        {
            $"/ShadingType {type}",
            $"/ColorSpace {cs}",
            $"/Coords {NumberArray(coords)}",
            $"/Function {funcRef}",
            $"/Extend [{(shading.Extend.Start ? "true" : "false")} {(shading.Extend.End ? "true" : "false")}]",
        };
        if (shading.Domain is { } domain)
            lines.Add($"/Domain [{Number(domain.T0)} {Number(domain.T1)}]");
        return PdfDict(lines);
    }

    private static byte[] RenderPatternCommands(List<RenderCommand> commands, Resources res, Dictionary<string, ExtGStateSpec> extgstates)
    {
        var lines = new List<string>();
        var byValues = new Dictionary<ExtGStateSpec, string>();
        foreach (var (name, value) in extgstates)
            byValues[value] = name;
        foreach (var command in commands)
        {
            switch (command)
            {
                case PathCommand path:
                {
                    var values = new ExtGStateSpec(ClampOpacity(path.FillOpacity), ClampOpacity(path.StrokeOpacity), ShouldStrokeAdjust(path));
                    WithState(lines, byValues, values, () => RenderPathCommand(lines, path, null, null, null));
                    break;
                }
                case ClipCommand clip:
                    RenderPathSegments(lines, clip.Path);
                    lines.Add(clip.FillRule == "evenodd" ? "W*" : "W");
                    lines.Add("n");
                    break;
                case StateSaveCommand:
                    lines.Add("q");
                    break;
                case StateRestoreCommand:
                    lines.Add("Q");
                    break;
                case TextCommand text:
                {
                    var values = new ExtGStateSpec(ClampOpacity(text.FillOpacity), 1.0);
                    WithState(lines, byValues, values, () => RenderTextCommand(lines, text, res.FontMap, null, null, null, res.FontCodeMaps, res.FontCodeWidths));
                    break;
                }
                case ImageCommand image:
                {
                    res.ImageResources.TryGetValue(image.ImageId, out var resource);
                    bool isCcittMask = resource is not null && resource.Mask && resource.Filter == "CCITTFaxDecode";
                    var values = new ExtGStateSpec(ClampOpacity(image.Opacity), ClampOpacity(image.Opacity));
                    WithState(lines, byValues, values, () => RenderImageCommand(lines, image, res.ImageMap, isCcittMask));
                    break;
                }
            }
        }
        return Ascii(string.Join("\n", lines) + "\n");
    }

    private static List<string> PatternImageIds(List<RenderCommand> commands)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();
        foreach (var command in commands)
        {
            if (command is ImageCommand image && seen.Add(image.ImageId))
                ordered.Add(image.ImageId);
        }
        return ordered;
    }

    private static List<string> PatternFontRefs(List<RenderCommand> commands)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>();
        foreach (var command in commands)
        {
            if (command is TextCommand text && !string.IsNullOrEmpty(text.FontRef) && seen.Add(text.FontRef))
                ordered.Add(text.FontRef);
        }
        return ordered;
    }

    private static string NumberArray(IEnumerable<double> values) => "[" + string.Join(" ", values.Select(Number)) + "]";

    private static string IntArray(IEnumerable<int> values) => "[" + string.Join(" ", values.Select(v => Number((long)v))) + "]";

    private static string HexBytes(byte[] data) => "<" + Convert.ToHexStringLower(data) + ">";

    private static void RenderPathCommand(List<string> lines, PathCommand command, IReadOnlyDictionary<string, string>? colorSpaceDefs,
        IReadOnlyDictionary<string, string>? patternVariants, IReadOnlyDictionary<string, string>? patternSpaceIds)
    {
        bool needsStroke = command.Stroke is not null;
        bool needsFill = command.Fill is not null;
        var strokePaint = command.StrokePaint ?? command.Fill;
        if (RenderThinAxisAlignedStrokeAsFill(lines, command, colorSpaceDefs, patternVariants, patternSpaceIds))
            return;
        RenderPathSegments(lines, command.Path);
        if (needsStroke)
            RenderStrokeStyle(lines, command.Stroke!);
        if (command.Overprint)
        {
            if (needsFill)
                lines.Add("true op");
            if (needsStroke)
                lines.Add("true OP");
        }
        if (needsFill)
            RenderPaint(lines, command.Fill!, false, colorSpaceDefs, patternVariants, patternSpaceIds);
        if (needsStroke && strokePaint is not null)
            RenderPaint(lines, strokePaint, true, colorSpaceDefs, patternVariants, patternSpaceIds);
        if (needsStroke && needsFill)
            lines.Add(command.FillRule == "evenodd" ? "B*" : "B");
        else if (needsStroke)
            lines.Add("S");
        else if (needsFill)
            lines.Add(command.FillRule == "evenodd" ? "f*" : "f");
        else
            lines.Add("n");
        if (command.Overprint)
        {
            if (needsFill)
                lines.Add("false op");
            if (needsStroke)
                lines.Add("false OP");
        }
    }

    private static bool RenderThinAxisAlignedStrokeAsFill(List<string> lines, PathCommand command, IReadOnlyDictionary<string, string>? colorSpaceDefs,
        IReadOnlyDictionary<string, string>? patternVariants, IReadOnlyDictionary<string, string>? patternSpaceIds)
    {
        var stroke = command.Stroke;
        if (stroke is null || command.Fill is not null || command.Overprint)
            return false;
        if (stroke.LineJoin != 0 || stroke.Dash.Length > 0 || !(stroke.LineWidth > 0.0 && stroke.LineWidth <= 1.0))
            return false;
        if (command.StrokeOpacity != 1.0 || command.FillOpacity != 1.0)
            return false;
        var strokePaint = command.StrokePaint ?? command.Fill;
        if (strokePaint is null)
            return false;
        var rect = ThinAxisAlignedStrokeRect(command.Path, stroke);
        if (rect is not var (x0, y0, width, height))
            return false;
        // Render the paint first so a failure leaves no partial output, as the original
        // built the list before returning it.
        var paintLines = new List<string>();
        RenderPaint(paintLines, strokePaint, false, colorSpaceDefs, patternVariants, patternSpaceIds);
        lines.Add($"{Number(x0)} {Number(y0)} {Number(width)} {Number(height)} re");
        lines.AddRange(paintLines);
        lines.Add("f");
        return true;
    }

    private static (double X, double Y, double W, double H)? ThinAxisAlignedStrokeRect(RenderPath path, StrokeStyle stroke)
    {
        if (path.Segments.Count != 2)
            return null;
        var move = path.Segments[0];
        var line = path.Segments[1];
        if (move.Kind != PathSegmentKind.Move || line.Kind != PathSegmentKind.Line)
            return null;
        ValidatePoints(move, 1);
        ValidatePoints(line, 1);
        var start = move.Points[0];
        var end = line.Points[0];
        if (start.X != end.X && start.Y != end.Y)
            return null;
        double half = stroke.LineWidth / 2.0;
        double extend = stroke.LineCap == 2 ? half : 0.0;
        if (start.Y == end.Y)
        {
            double xMin = Math.Min(start.X, end.X) - extend;
            double xMax = Math.Max(start.X, end.X) + extend;
            return (xMin, start.Y - half, xMax - xMin, stroke.LineWidth);
        }
        double yMin = Math.Min(start.Y, end.Y) - extend;
        double yMax = Math.Max(start.Y, end.Y) + extend;
        return (start.X - half, yMin, stroke.LineWidth, yMax - yMin);
    }

    private static void RenderTextCommand(List<string> lines, TextCommand command, Dictionary<string, string> fontMap,
        IReadOnlyDictionary<string, string>? colorSpaceDefs, IReadOnlyDictionary<string, string>? patternVariants,
        IReadOnlyDictionary<string, string>? patternSpaceIds, Dictionary<string, IReadOnlyDictionary<int, int>> fontCodeMaps,
        Dictionary<string, int> fontCodeWidths)
    {
        var chars = Py.Chars(command.Text).Where(ch => Py.Ord(ch) is not (10 or 13)).ToList();
        if (chars.Count == 0)
            return;
        lines.Add("BT");
        if (command.Fill is not null)
            RenderPaint(lines, command.Fill, false, colorSpaceDefs, patternVariants, patternSpaceIds);
        string fontName = fontMap.GetValueOrDefault(command.FontRef, "F1");
        lines.Add($"/{fontName} {Number(command.FontSize)} Tf");
        lines.Add($"{FormatMatrix(command.Matrix)} Tm");
        IReadOnlyDictionary<int, int> codeMap = fontCodeMaps.TryGetValue(command.FontRef, out var cm) ? cm : new Dictionary<int, int>();
        int codeWidth = fontCodeWidths.GetValueOrDefault(command.FontRef, 1);
        byte[] textBytes = EncodeTextBytes(chars, codeMap, command.FontRef, codeWidth);
        if (command.PdfTextAdjustments is { Length: > 0 } adjustments)
            lines.Add(FormatTextArray(chars.Count, textBytes, adjustments, codeWidth) + " TJ");
        else
            lines.Add($"<{Convert.ToHexString(textBytes)}> Tj");
        lines.Add("ET");
    }

    private static string FormatTextArray(int charCount, byte[] textBytes, double[] adjustments, int codeByteWidth)
    {
        var parts = new List<string> { "[" };
        for (int idx = 0; idx < charCount; idx++)
        {
            int start = Math.Min(idx * codeByteWidth, textBytes.Length);
            int end = Math.Min(start + codeByteWidth, textBytes.Length);
            parts.Add($"<{Convert.ToHexString(textBytes, start, end - start)}>");
            if (idx < adjustments.Length)
                parts.Add(Number(adjustments[idx]));
        }
        parts.Add("]");
        return string.Join(" ", parts);
    }

    private static Dictionary<string, int> StandardUnicodeToCodeFor(string fontRef) => fontRef switch
    {
        "Symbol" => SymbolUnicodeToCode,
        "ZapfDingbats" => ZapfUnicodeToCode,
        _ => StandardUnicodeToCode,
    };

    private static byte[] EncodeTextBytes(List<string> chars, IReadOnlyDictionary<int, int> codeMap, string fontRef, int codeByteWidth)
    {
        var payload = new List<byte>(chars.Count * codeByteWidth);
        var standardMap = StandardFonts.Contains(fontRef) ? StandardUnicodeToCodeFor(fontRef) : null;
        foreach (string ch in chars)
        {
            int code = Py.Ord(ch);
            int mapped;
            if (codeMap.TryGetValue(code, out int m))
                mapped = m;
            else if (standardMap is not null && standardMap.TryGetValue(ch, out int s))
                mapped = s;
            else
                mapped = code is >= 0 and <= 0xFF ? code : '?';
            if (codeByteWidth == 2)
            {
                if (mapped is < 0 or > 0xFFFF)
                    throw Py.OverflowError("int too big to convert");
                payload.Add((byte)(mapped >> 8));
                payload.Add((byte)(mapped & 0xFF));
            }
            else
            {
                payload.Add((byte)(mapped & 0xFF));
            }
        }
        return [.. payload];
    }

    private static void RenderImageCommand(List<string> lines, ImageCommand command, Dictionary<string, string> imageMap, bool isCcittMask)
    {
        if (!imageMap.TryGetValue(command.ImageId, out string? name))
            throw Py.KeyError($"'{command.ImageId}'");
        lines.Add("q");
        if (command.Mask && command.MaskPaint is not null)
            RenderPaint(lines, command.MaskPaint, false, EmptyDefs, EmptyVariants, EmptyDefs);
        var matrix = command.Matrix;
        if (command.Mask && isCcittMask)
        {
            // PostScript imagemask sample space is vertically inverted relative to PDF image
            // space: apply a unit-space Y flip before painting.
            matrix = MulImageMatrix(command.Matrix, new Matrix(1.0, 0.0, 0.0, -1.0, 0.0, 1.0));
        }
        lines.Add($"{FormatMatrix(matrix)} cm");
        lines.Add($"/{name} Do");
        lines.Add("Q");
    }

    private static Matrix MulImageMatrix(Matrix l, Matrix r) => new(
        l.A * r.A + l.C * r.B,
        l.B * r.A + l.D * r.B,
        l.A * r.C + l.C * r.D,
        l.B * r.C + l.D * r.D,
        l.A * r.E + l.C * r.F + l.E,
        l.B * r.E + l.D * r.F + l.F);

    private static void RenderPathSegments(List<string> lines, RenderPath path)
    {
        foreach (var segment in path.Segments)
        {
            switch (segment.Kind)
            {
                case PathSegmentKind.Move:
                    ValidatePoints(segment, 1);
                    lines.Add($"{Number(segment.Points[0].X)} {Number(segment.Points[0].Y)} m");
                    break;
                case PathSegmentKind.Line:
                    ValidatePoints(segment, 1);
                    lines.Add($"{Number(segment.Points[0].X)} {Number(segment.Points[0].Y)} l");
                    break;
                case PathSegmentKind.Curve:
                {
                    ValidatePoints(segment, 3);
                    var p = segment.Points;
                    lines.Add($"{Number(p[0].X)} {Number(p[0].Y)} {Number(p[1].X)} {Number(p[1].Y)} {Number(p[2].X)} {Number(p[2].Y)} c");
                    break;
                }
                case PathSegmentKind.Close:
                    ValidatePoints(segment, 0);
                    lines.Add("h");
                    break;
            }
        }
    }

    private static bool ShouldStrokeAdjust(PathCommand command)
    {
        var stroke = command.Stroke;
        if (stroke is null || !(stroke.LineWidth > 0.0 && stroke.LineWidth <= 1.0))
            return false;
        if (PathIsAxisAlignedLines(command.Path))
            return true;
        if (!command.Path.Segments.Any(s => s.Kind == PathSegmentKind.Close))
            return false;
        var bbox = PathBBox(command.Path);
        if (bbox is null)
            return false;
        var (x0, y0, x1, y1) = bbox.Value;
        return x1 - x0 <= 8.0 && y1 - y0 <= 8.0;
    }

    private static bool PathIsAxisAlignedLines(RenderPath path)
    {
        Point? current = null;
        bool sawLine = false;
        foreach (var segment in path.Segments)
        {
            if (segment.Kind == PathSegmentKind.Move)
            {
                ValidatePoints(segment, 1);
                current = segment.Points[0];
                continue;
            }
            if (segment.Kind != PathSegmentKind.Line)
                return false;
            if (current is not Point c)
                return false;
            ValidatePoints(segment, 1);
            var target = segment.Points[0];
            if (c.X != target.X && c.Y != target.Y)
                return false;
            current = target;
            sawLine = true;
        }
        return sawLine;
    }

    private static (double, double, double, double)? PathBBox(RenderPath path)
    {
        bool any = false;
        double minX = 0, minY = 0, maxX = 0, maxY = 0;
        foreach (var segment in path.Segments)
        {
            foreach (var p in segment.Points)
            {
                if (!any)
                {
                    minX = maxX = p.X;
                    minY = maxY = p.Y;
                    any = true;
                    continue;
                }
                minX = PyMin(minX, p.X);
                minY = PyMin(minY, p.Y);
                maxX = PyMax(maxX, p.X);
                maxY = PyMax(maxY, p.Y);
            }
        }
        return any ? (minX, minY, maxX, maxY) : null;
    }

    // Python's min/max keep the first value unless a later one compares strictly smaller/larger.
    private static double PyMin(double a, double b) => b < a ? b : a;

    private static double PyMax(double a, double b) => b > a ? b : a;

    private static void RenderStrokeStyle(List<string> lines, StrokeStyle style)
    {
        lines.Add($"{Number(style.LineWidth)} w");
        lines.Add($"{style.LineCap} J");
        lines.Add($"{style.LineJoin} j");
        lines.Add($"{Number(style.MiterLimit)} M");
        lines.Add($"[{string.Join(" ", style.Dash.Select(Number))}] {Number(style.DashPhase)} d");
    }

    private static void RenderPaint(List<string> lines, Paint paint, bool isStroke, IReadOnlyDictionary<string, string>? colorSpaceDefs,
        IReadOnlyDictionary<string, string>? patternVariants, IReadOnlyDictionary<string, string>? patternSpaceIds)
    {
        string op = isStroke ? "CS" : "cs";
        string compOp = isStroke ? "SCN" : "scn";
        switch (paint.Kind)
        {
            case "PatternBase":
                return;
            case "DeviceRGB":
            {
                var v = paint.Value as double[];
                if (v is null || v.Length != 3)
                    throw Py.ValueError("invalid DeviceRGB paint value");
                lines.Add($"{Number(Quantize(v[0]))} {Number(Quantize(v[1]))} {Number(Quantize(v[2]))} {(isStroke ? "RG" : "rg")}");
                return;
            }
            case "DeviceGray":
            {
                double value = paint.Value switch
                {
                    double d => d,
                    double[] { Length: 1 } a => a[0],
                    double[] => throw Py.ValueError("invalid DeviceGray paint value"),
                    _ => throw new HostErrorException("TypeError", "float() argument must be a string or a real number"),
                };
                lines.Add($"{Number(Quantize(value))} {(isStroke ? "G" : "g")}");
                return;
            }
            case "DeviceCMYK":
            {
                var v = paint.Value as double[];
                if (v is null || v.Length != 4)
                    throw Py.ValueError("invalid DeviceCMYK paint value");
                // Naive CMYK to RGB, the same conversion as the raster path, so PDF output stays
                // visually aligned with the image output.
                double r = 1.0 - Math.Min(1.0, v[0] + v[3]);
                double g = 1.0 - Math.Min(1.0, v[1] + v[3]);
                double b = 1.0 - Math.Min(1.0, v[2] + v[3]);
                lines.Add($"{Number(Quantize(r))} {Number(Quantize(g))} {Number(Quantize(b))} {(isStroke ? "RG" : "rg")}");
                return;
            }
            case "ColorSpace":
            {
                if (paint.Value is not ColorSpacePaint csp)
                    throw Py.ValueError("invalid ColorSpace paint value");
                lines.Add($"/{csp.SpaceId} {op}");
                lines.Add($"{string.Join(" ", csp.Components.Select(Number))} {compOp}");
                return;
            }
            case "Pattern":
            {
                if (paint.Value is not PatternPaint pp)
                    throw Py.ValueError("invalid Pattern paint value");
                if (pp.BaseSpaceId is null)
                {
                    lines.Add($"/Pattern {op}");
                    lines.Add($"/{pp.PatternId} {compOp}");
                    return;
                }
                if (patternVariants is not null && pp.BaseComponents is not null &&
                    patternVariants.TryGetValue(VariantKey(pp.PatternId, pp.BaseSpaceId, pp.BaseComponents), out string? variantId))
                {
                    lines.Add($"/Pattern {op}");
                    lines.Add($"/{variantId} {compOp}");
                    return;
                }
                string components = string.Join(" ", (pp.BaseComponents ?? []).Select(Number));
                if (patternSpaceIds is not null && patternSpaceIds.TryGetValue(pp.BaseSpaceId, out string? patternSpace))
                {
                    lines.Add($"/{patternSpace} {op}");
                    lines.Add(components.Length > 0 ? $"{components} /{pp.PatternId} {compOp}" : $"/{pp.PatternId} {compOp}");
                    return;
                }
                string? baseSpace = null;
                if (colorSpaceDefs is not null)
                    baseSpace = colorSpaceDefs.GetValueOrDefault(pp.BaseSpaceId);
                baseSpace ??= "/" + pp.BaseSpaceId;
                lines.Add($"[/Pattern {baseSpace}] {op}");
                lines.Add(components.Length > 0 ? $"{components} /{pp.PatternId} {compOp}" : $"/{pp.PatternId} {compOp}");
                return;
            }
            default:
                throw Py.ValueError($"unsupported paint kind: {paint.Kind}");
        }
    }

    private static string ResolveFont(string fontRef) => StandardFonts.Contains(fontRef) ? fontRef : "Helvetica";

    private static double Quantize(double value)
    {
        value = Math.Max(0.0, Math.Min(1.0, value));
        int b = (int)(value * 255.0);
        return b / 255.0;
    }

    private static void ValidatePoints(PathSegment segment, int expected)
    {
        if (segment.Points.Length != expected)
            throw Py.ValueError($"invalid path segment '{segment.Kind.ToString().ToLowerInvariant()}' points");
    }

    private static string? GlyphNameToUnicode(string? glyphName)
    {
        if (string.IsNullOrEmpty(glyphName))
            return null;
        int dot = glyphName.IndexOf('.', StringComparison.Ordinal);
        string baseName = dot >= 0 ? glyphName[..dot] : glyphName;
        if (baseName == ".notdef")
            return null;
        if (Py.Len(baseName) == 1)
            return baseName;
        if (GlyphNameUnicode.TryGetValue(baseName, out string? direct))
            return direct;
        if (baseName.StartsWith("uni", StringComparison.Ordinal) && baseName.Length > 3)
        {
            string hex = baseName[3..];
            if (hex.Length % 4 == 0)
            {
                var sb = new StringBuilder();
                for (int i = 0; i < hex.Length; i += 4)
                {
                    if (!Py.TryInt(hex.Substring(i, 4), 16, out long v))
                        return null;
                    sb.Append(Py.Chr((int)v));
                }
                return sb.ToString();
            }
        }
        if (baseName.StartsWith('u') && baseName.Length is 5 or 6 or 7)
        {
            if (!Py.TryInt(baseName[1..], 16, out long v) || v > 0x10FFFF)
                return null;
            return Py.Chr((int)v);
        }
        return null;
    }

    private static Dictionary<string, int> BuildUnicodeToCodeMap(IReadOnlyDictionary<int, string> encoding)
    {
        var mapping = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (code, glyph) in encoding)
        {
            string? uni = GlyphNameToUnicode(glyph);
            if (uni is null || Py.Len(uni) != 1)
                continue;
            mapping.TryAdd(uni, code & 0xFF);
        }
        return mapping;
    }
}
