// Ported from xps/render.py: renders XPS fixed pages into the shared render model.

using System.Xml.Linq;
using XRay.PS.Fonts;
using XRay.PS.Imaging;
using XRay.PS.Internal;
using XRay.PS.Rendering;

namespace XRay.PS.Xps;

/// <summary>Renders XPS FixedPage XML into a <see cref="RenderModelBuilder"/>.</summary>
public sealed partial class XpsRenderer
{
    private readonly RenderModelBuilder _builder;
    private readonly XpsImageStore _imageStore;
    private XpsPackage? _package;
    private string? _currentPart;
    private readonly Dictionary<string, Dictionary<int, int>> _fontGidToCode = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TrueTypeFont?> _fontCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (double UnitsPerEm, IReadOnlyDictionary<int, double> CodeWidths)> _fontMetricCache = new(StringComparer.Ordinal);
    private readonly Dictionary<(string, long), string> _opacityImageCache = [];
    private (double Width, double Height)? _mediaFitSize;
    private readonly bool _rasterizeSolidStrokes;
    private readonly string _spreadGradientMode;
    private readonly IRasterWriter _maskRasterizer;
    private readonly Dictionary<XElement, (DecodedImage Image, (double X, double Y, double W, double H) Viewport, string TileMode)> _maskBrushImageCache = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<XElement, (XpsImageResource Image, (double, double, double, double) Viewbox, (double X, double Y, double W, double H) Viewport, string TileMode)> _maskBrushVisualCache = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<XElement, List<(double Offset, (double R, double G, double B) Color)>> _gradientRgbStopsCache = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<XElement, List<(double Offset, double Alpha)>> _gradientAlphaStopsCache = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<XElement, (string Tag, double[] Params, string Spread)> _gradientGeometryCache = new(ReferenceEqualityComparer.Instance);
    // A solid brush is sampled once per pixel when a stroke is drawn as an image, and parsing its
    // colour every time was most of what an XPS page with such strokes allocated.
    private readonly Dictionary<XElement, (double R, double G, double B)> _solidBrushRgbCache = new(ReferenceEqualityComparer.Instance);
    private int _depth;

    /// <summary>Observed per element.</summary>
    public CancellationToken CancellationToken { get; set; }

    /// <summary>Skip (with a warning) an element that throws instead of failing the page.</summary>
    public bool SuppressErrors { get; set; }

    /// <summary>Receives a warning for every skipped element.</summary>
    public List<ConversionWarning>? Warnings { get; set; }

    /// <summary>The one-based number of the page being rendered, for warnings.</summary>
    public int? PageNumber { get; set; }

    internal static bool IsRecoverable(Exception ex) => ex is not (OperationCanceledException or OutOfMemoryException or StackOverflowException);

    /// <summary>Creates a renderer.</summary>
    /// <param name="builder">The render model under construction.</param>
    /// <param name="imageStore">Receives decoded images.</param>
    /// <param name="mediaFitSize">Scales pages to fit this size (points), from print tickets.</param>
    /// <param name="rasterizeSolidStrokes">Rasterize hairline solid strokes (PDF output).</param>
    /// <param name="spreadGradientMode">"default" or "pdf" reflect handling.</param>
    /// <param name="maskRasterizer">Renders opacity-masked content to images; the managed
    /// rasterizer when null.</param>
    public XpsRenderer(RenderModelBuilder builder, XpsImageStore imageStore, (double Width, double Height)? mediaFitSize = null,
        bool rasterizeSolidStrokes = false, string spreadGradientMode = "default", IRasterWriter? maskRasterizer = null)
    {
        _builder = builder;
        _imageStore = imageStore;
        _mediaFitSize = mediaFitSize;
        _rasterizeSolidStrokes = rasterizeSolidStrokes;
        _spreadGradientMode = spreadGradientMode;
        _maskRasterizer = maskRasterizer ?? new RenderModelRasterWriter();
    }

    public void SetPackage(XpsPackage package) => _package = package;

    public void SetCurrentPart(string partName) => _currentPart = partName;

    public void SetMediaFitSize((double Width, double Height)? mediaFitSize) => _mediaFitSize = mediaFitSize;

    /// <summary>Renders a FixedPage XML payload as one page.</summary>
    public void RenderFixedPage(byte[] xml, XpsResourceDictionary? resources = null)
    {
        _gradientRgbStopsCache.Clear();
        _gradientAlphaStopsCache.Clear();
        _gradientGeometryCache.Clear();
        var root = XmlUtil.Parse(xml);
        double rawWidth = Or(ParseFloat(XmlUtil.Get(root, "Width")), 0.0) * XpsUnitScale;
        double rawHeight = Or(ParseFloat(XmlUtil.Get(root, "Height")), 0.0) * XpsUnitScale;
        double fitScale = 1.0;
        double width = rawWidth;
        double height = rawHeight;
        if (_mediaFitSize is var (fitWidth, fitHeight) && rawWidth > 0.0 && rawHeight > 0.0 && fitWidth > 0.0 && fitHeight > 0.0)
        {
            fitScale = Math.Min(fitWidth / rawWidth, fitHeight / rawHeight);
            width = rawWidth * fitScale;
            height = rawHeight * fitScale;
        }
        _builder.BeginPage(width, height);
        var pageResources = MergeResources(root, resources, _package, _currentPart);
        // XPS is top-left with +Y down; the render model is +Y up.
        var transform = new Matrix(XpsUnitScale * fitScale, 0.0, 0.0, -(XpsUnitScale * fitScale), 0.0, height);
        foreach (var child in XmlUtil.Children(root))
            RenderElement(child, pageResources, transform);
        _builder.EndPage();
    }

    internal void RenderElement(XElement element, XpsResourceDictionary? resources, Matrix transform)
    {
        // Nesting depth is bounded for hostile documents.
        if (_depth > 256)
            return;
        CancellationToken.ThrowIfCancellationRequested();
        _depth++;
        int commandCount = _builder.ActivePage?.Commands.Count ?? 0;
        try
        {
            RenderElementCore(element, resources, transform);
        }
        catch (Exception ex) when (SuppressErrors && IsRecoverable(ex))
        {
            // Skip the element: drop whatever it had already drawn (a half-drawn element can
            // leave a state save without its restore).
            var commands = _builder.ActivePage?.Commands;
            if (commands is not null && commands.Count > commandCount)
                commands.RemoveRange(commandCount, commands.Count - commandCount);
            Warnings?.Add(new ConversionWarning("xps", "element", $"{element.Name.LocalName}: {ex.Message}", PageNumber));
        }
        finally
        {
            _depth--;
        }
    }

    private void RenderElementCore(XElement element, XpsResourceDictionary? resources, Matrix transform)
    {
        string tag = element.Name.LocalName;
        var combined = Mul(transform, ElementTransform(element, resources));
        switch (tag)
        {
            case "Canvas":
                RenderCanvas(element, resources, combined);
                return;
            case "Path":
                RenderPathElement(element, resources, combined);
                return;
            case "Glyphs":
                RenderGlyphs(element, resources, combined);
                return;
            case "Image":
            {
                string? source = XmlUtil.Get(element, "Source");
                if (string.IsNullOrEmpty(source))
                    return;
                var resource = LoadImage(source);
                if (resource is null)
                    return;
                string imageId = _imageStore.Register(resource);
                int width = (int)Py.Round(Or(ParseFloat(XmlUtil.Get(element, "Width")), resource.Width) * XpsUnitScale);
                int height = (int)Py.Round(Or(ParseFloat(XmlUtil.Get(element, "Height")), resource.Height) * XpsUnitScale);
                _builder.AddImage(imageId, width, height, combined);
                return;
            }
        }
    }

    private void RenderCanvas(XElement element, XpsResourceDictionary? resources, Matrix combined)
    {
        var canvasResources = MergeResources(element, resources, _package, _currentPart);
        if (ExtractOpacityMaskBrush(element, "Canvas.OpacityMask") is not null &&
            TryRenderCanvasOpacityMaskAsImage(element, canvasResources, combined))
        {
            return;
        }
        string? clipData = XmlUtil.Get(element, "Clip");
        bool clip = !string.IsNullOrEmpty(clipData);
        if (clip)
        {
            _builder.SaveState();
            _builder.Clip(ParsePathData(clipData!, combined), ExtractFillRule(element, resources));
        }
        foreach (var child in XmlUtil.Children(element))
            RenderElement(child, canvasResources, combined);
        if (clip)
            _builder.RestoreState();
    }

    private void RenderPathElement(XElement element, XpsResourceDictionary? resources, Matrix combined)
    {
        string? data = ExtractPathData(element, resources);
        RenderPath? fillPath;
        RenderPath? strokePath;
        RenderPath path;
        if (!string.IsNullOrEmpty(data))
        {
            path = ParsePathData(data, combined);
            fillPath = path;
            strokePath = path;
        }
        else
        {
            var geometry = ExtractPathGeometryElement(element, resources);
            if (geometry is null)
                return;
            (fillPath, strokePath) = ParsePathGeometryVariants(geometry, combined);
            path = fillPath ?? strokePath ?? new RenderPath();
        }
        string? clipData = XmlUtil.Get(element, "Clip");
        bool clip = !string.IsNullOrEmpty(clipData);
        if (clip)
        {
            _builder.SaveState();
            _builder.Clip(ParsePathData(clipData!, combined), ExtractFillRule(element, resources));
        }
        void RestoreClip()
        {
            if (clip)
                _builder.RestoreState();
        }
        string fillRule = ExtractFillRule(element, resources);
        var pathBBox = PathBBox(path);
        object? fillValue = ExtractPaintValue(element, "Fill");
        object? strokeValue = ExtractPaintValue(element, "Stroke");
        var fillBrush = ResolveBrushElement(fillValue, resources);
        var fill = ResolvePaint(fillValue, resources, combined, combined);
        var stroke = ResolvePaint(strokeValue, resources, combined, combined);
        var strokeBrush = ResolveBrushElement(strokeValue, resources);
        double? opacity = ParseFloat(XmlUtil.Get(element, "Opacity"));
        if (opacity is double op)
            opacity = Clamp(op, 0.0, 1.0);
        var maskBrush = fillPath is not null ? ExtractOpacityMaskBrush(element, "Path.OpacityMask") : null;
        if (fillPath is not null && maskBrush is not null && stroke is null &&
            TryRenderMaskedSolidFillAsImage(fillPath, pathBBox, fillBrush, maskBrush, combined, opacity ?? 1.0))
        {
            RestoreClip();
            return;
        }
        double fillOpacity = 1.0;
        double strokeOpacity = 1.0;
        if (BrushAlpha(fillValue, resources) is double fillAlpha)
            fillOpacity *= fillAlpha;
        if (BrushAlpha(strokeValue, resources) is double strokeAlpha)
            strokeOpacity *= strokeAlpha;
        if (maskBrush is not null)
        {
            var fillBeforeMask = fill;
            fill = ApplyOpacityMaskToFillPaint(fill, maskBrush, resources, combined, combined, path, pathBBox, fillBrush);
            double? maskAlpha = OpacityFromBrushElement(maskBrush, resources);
            if (maskAlpha is double ma && Equals(fill, fillBeforeMask))
            {
                double alpha = Clamp(ma, 0.0, 1.0);
                fillOpacity *= alpha;
                strokeOpacity *= alpha;
            }
        }
        if (opacity is double opacityValue)
        {
            fillOpacity *= opacityValue;
            strokeOpacity *= opacityValue;
        }
        if (maskBrush is null && fillPath is not null && fillBrush is not null &&
            TryRenderSpreadGradientFillAsImage(fillPath, pathBBox, fillBrush, combined, fillOpacity))
        {
            fill = null;
            fillOpacity = 1.0;
            if (stroke is null)
            {
                RestoreClip();
                return;
            }
        }
        if (fillPath is not null && fillBrush is not null &&
            TryRenderImageBrushFillAsImage(fillPath, pathBBox, fillBrush, combined, fillOpacity))
        {
            fill = null;
            fillOpacity = 1.0;
            if (stroke is null)
            {
                RestoreClip();
                return;
            }
        }
        StrokeStyle? strokeStyle = null;
        if (stroke is not null)
        {
            double baseThickness = Or(ParseFloat(XmlUtil.Get(element, "StrokeThickness")), 1.0) * XpsUnitScale;
            double strokeScale = StrokeScaleFromMatrix(combined) / XpsUnitScale;
            double thickness = baseThickness * strokeScale;
            if (strokePath is not null && fill is null && strokeBrush is not null &&
                TryRenderStrokedBrushAsImage(strokePath, strokeBrush, thickness, combined, opacity ?? 1.0))
            {
                RestoreClip();
                return;
            }
            strokeStyle = new StrokeStyle(thickness, ParseLineCap(element), ParseLineJoin(element),
                Or(ParseFloat(XmlUtil.Get(element, "StrokeMiterLimit")), 10.0), ParseDashPattern(element, thickness), ParseDashPhase(element, thickness));
        }
        if (fillPath is not null && fill is not null)
            _builder.AddPath(fillPath, null, fill, fillRule, null, false, fillOpacity, 1.0);
        if (strokePath is not null && stroke is not null)
            _builder.AddPath(strokePath, strokeStyle, null, fillRule, stroke, false, 1.0, strokeOpacity);
        if (fillPath is null && strokePath is null)
            _builder.AddPath(path, strokeStyle, fill, fillRule, stroke, false, fillOpacity, strokeOpacity);
        RestoreClip();
    }

    private void RenderGlyphs(XElement element, XpsResourceDictionary? resources, Matrix combined)
    {
        var chars = Py.Chars(HtmlEntities.Unescape(XmlUtil.Get(element, "UnicodeString") ?? "")).ToList();
        double fontSize = Or(ParseFloat(XmlUtil.Get(element, "FontRenderingEmSize")), 12.0);
        double originX = Or(ParseFloat(XmlUtil.Get(element, "OriginX")), 0.0);
        double originY = Or(ParseFloat(XmlUtil.Get(element, "OriginY")), 0.0);
        string? fontUri = XmlUtil.Get(element, "FontUri");
        string fontRef = !string.IsNullOrEmpty(fontUri) ? ResolvePart(_currentPart ?? "/", fontUri) : "Helvetica";
        string? indices = XmlUtil.Get(element, "Indices");
        var parsedIndices = !string.IsNullOrEmpty(indices) ? ParseIndices(indices) : [];
        chars = TextFromIndices(fontRef, chars, parsedIndices);
        long bidiLevel = ParseInt(XmlUtil.Get(element, "BidiLevel")) is long bl && bl != 0 ? bl : 0;
        if (chars.Count == 0)
            return;
        string text = string.Concat(chars);
        var brushTransform = Mul(combined, new Matrix(1.0, 0.0, 0.0, 1.0, originX, originY));
        // Keep text upright in user space while preserving the run origin.
        var runMatrix = Mul(brushTransform, new Matrix(1.0, 0.0, 0.0, -1.0, 0.0, 0.0));
        object? fillValue = ExtractPaintValue(element, "Fill");
        var fill = ResolvePaint(fillValue, resources, combined, brushTransform);
        double fillOpacity = 1.0;
        if (BrushAlpha(ExtractPaintValue(element, "Fill"), resources) is double fillAlpha)
            fillOpacity *= fillAlpha;
        var maskBrush = ExtractOpacityMaskBrush(element, "Glyphs.OpacityMask");
        string? clipData = XmlUtil.Get(element, "Clip");
        bool clip = !string.IsNullOrEmpty(clipData);
        if (clip)
        {
            _builder.SaveState();
            _builder.Clip(ParsePathData(clipData!, combined), ExtractFillRule(element, resources));
        }
        bool isSideways = IsTrue(XmlUtil.Get(element, "IsSideways"));
        string style = Py.Strip(XmlUtil.Get(element, "StyleSimulations") ?? "");
        if (style is "ItalicSimulation" or "BoldItalicSimulation")
            runMatrix = Mul(runMatrix, new Matrix(1.0, 0.0, 0.2, 1.0, 0.0, 0.0));
        bool rtl = PyModInt(bidiLevel, 2) == 1;
        bool bold = style is "BoldSimulation" or "BoldItalicSimulation";
        if (maskBrush is not null && !isSideways && !rtl && parsedIndices.Count == 0 && style.Length == 0 &&
            TryRenderGlyphsOpacityMaskAsImage(text, fontRef, fontSize, runMatrix, fill, fillOpacity, maskBrush, brushTransform))
        {
            if (clip)
                _builder.RestoreState();
            return;
        }
        if (rtl && !isSideways && !IndicesHaveMetrics(parsedIndices))
        {
            // One text command anchored at the right-side origin keeps engine-native spacing.
            string textOut = string.Concat(Enumerable.Reverse(chars));
            double runWidth = 0.0;
            foreach (string ch in chars)
                runWidth += GlyphAdvancePoints(fontRef, ch, fontSize);
            var rtlMatrix = Mul(runMatrix, new Matrix(1.0, 0.0, 0.0, 1.0, -runWidth, 0.0));
            _builder.AddText(textOut, fontRef, fontSize, rtlMatrix, fill, fillOpacity);
            if (bold)
                _builder.AddText(textOut, fontRef, fontSize, Mul(rtlMatrix, new Matrix(1.0, 0.0, 0.0, 1.0, fontSize * 0.04, 0.0)), fill, fillOpacity);
        }
        else if (isSideways || IndicesHaveMetrics(parsedIndices) || parsedIndices.Any(e => e.GlyphId is not null) || rtl)
        {
            EmitGlyphRun(chars, fontRef, fontSize, runMatrix, fill, fillOpacity, bidiLevel, isSideways, style, parsedIndices);
        }
        else
        {
            string textOut = PyModInt(bidiLevel, 2) == 1 ? string.Concat(Enumerable.Reverse(chars)) : text;
            _builder.AddText(textOut, fontRef, fontSize, runMatrix, fill, fillOpacity);
            if (bold)
                _builder.AddText(textOut, fontRef, fontSize, Mul(runMatrix, new Matrix(1.0, 0.0, 0.0, 1.0, fontSize * 0.04, 0.0)), fill, fillOpacity);
        }
        if (clip)
            _builder.RestoreState();
    }

    internal XpsImageResource? LoadImage(string source)
    {
        if (_package is null)
            return null;
        string partName = ResolvePart(_currentPart ?? "/", source);
        byte[] data = _package.Read(partName);
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == (byte)'P' && data[2] == (byte)'N' && data[3] == (byte)'G' && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
            return XpsImageDecoders.DecodePng(data);
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xD8)
            return XpsImageDecoders.DecodeJpeg(data);
        string lower = partName.ToLowerInvariant();
        if (lower.EndsWith(".wdp", StringComparison.Ordinal) || lower.EndsWith(".jxr", StringComparison.Ordinal))
            return XpsImageDecoders.DecodeJpegXr(data);
        if (lower.EndsWith(".tif", StringComparison.Ordinal) || lower.EndsWith(".tiff", StringComparison.Ordinal))
            return XpsImageDecoders.DecodeTiff(data);
        throw Py.ValueError("unsupported image format");
    }

    internal XpsImageStore ImageStore => _imageStore;

    internal XpsPackage? Package => _package;

    internal string? CurrentPart => _currentPart;

    internal RenderModelBuilder Builder => _builder;

    // ---- glyph indices --------------------------------------------------------------------

    internal sealed class IndexEntry(long? glyphId = null, double? advance = null, double? uOffset = null, double? vOffset = null, long codeUnitCount = 1, bool skipRender = false)
    {
        public long? GlyphId { get; } = glyphId;
        public double? Advance { get; } = advance;
        public double? UOffset { get; } = uOffset;
        public double? VOffset { get; } = vOffset;
        public long CodeUnitCount { get; } = Math.Max(1, codeUnitCount);
        public bool SkipRender { get; } = skipRender;
    }

    internal static List<IndexEntry> ParseIndices(string value)
    {
        var result = new List<IndexEntry>();
        foreach (string rawSegment in value.Split(';'))
        {
            string segment = Py.Strip(rawSegment);
            if (segment.Length == 0)
            {
                result.Add(new IndexEntry());
                continue;
            }
            long codeUnitCount = 1;
            if (segment.StartsWith('(') && segment.Contains(')', StringComparison.Ordinal))
            {
                int close = segment.IndexOf(')', StringComparison.Ordinal);
                string prefix = Py.Strip(segment[1..close]);
                string remainder = segment[(close + 1)..];
                int colon = prefix.IndexOf(':', StringComparison.Ordinal);
                if (colon >= 0)
                    prefix = Py.Strip(prefix[..colon]);
                if (ParseInt(prefix) is long count && count > 0)
                    codeUnitCount = count;
                segment = Py.Strip(remainder);
                if (segment.Length == 0)
                {
                    result.Add(new IndexEntry(codeUnitCount: codeUnitCount));
                    AddSkips(result, codeUnitCount);
                    continue;
                }
            }
            var parts = segment.Split(',').Select(Py.Strip).ToArray();
            long? glyphId = parts.Length > 0 ? ParseInt(parts[0]) : null;
            double? advance = parts.Length > 1 && parts[1].Length > 0 ? ParseFloat(parts[1]) : null;
            double? uOffset = parts.Length > 2 && parts[2].Length > 0 ? ParseFloat(parts[2]) : null;
            double? vOffset = parts.Length > 3 && parts[3].Length > 0 ? ParseFloat(parts[3]) : null;
            result.Add(new IndexEntry(glyphId, advance, uOffset, vOffset, codeUnitCount));
            AddSkips(result, codeUnitCount);
        }
        return result;
    }

    private static void AddSkips(List<IndexEntry> result, long codeUnitCount)
    {
        // Bounded: a cluster count beyond the string is meaningless.
        long skips = Math.Min(codeUnitCount - 1, 1 << 20);
        for (long i = 0; i < skips; i++)
            result.Add(new IndexEntry(skipRender: true));
    }

    private static bool IndicesHaveMetrics(List<IndexEntry> entries) =>
        entries.Any(e => e.Advance is not null || e.UOffset is not null || e.VOffset is not null);

    private static long PyModInt(long a, long b) => ((a % b) + b) % b;

    private static string FontRefForGlyph(string fontRef, long? glyphId) => glyphId is long g ? $"{fontRef}#gid={g}" : fontRef;

    private List<string> TextFromIndices(string fontRef, List<string> text, List<IndexEntry> entries)
    {
        if (entries.Count == 0 || !entries.Any(e => e.GlyphId is not null))
            return text;
        var reverse = GidToUnicode(fontRef);
        if (reverse.Count == 0)
            return text;
        var chars = new List<string>();
        int maxLen = Math.Max(entries.Count, text.Count);
        for (int i = 0; i < maxLen; i++)
        {
            var entry = i < entries.Count ? entries[i] : null;
            long? gid = entry?.GlyphId;
            if (gid is null)
            {
                if (i < text.Count)
                    chars.Add(text[i]);
                continue;
            }
            if (gid is not long g || g < int.MinValue || g > int.MaxValue || !reverse.TryGetValue((int)g, out int code))
            {
                if (i < text.Count)
                    chars.Add(text[i]);
                continue;
            }
            chars.Add(Py.Chr(code));
        }
        return chars.Count > 0 ? chars : text;
    }

    private void EmitGlyphRun(List<string> chars, string fontRef, double fontSize, Matrix baseMatrix, Paint? fill, double fillOpacity,
        long bidiLevel, bool isSideways, string style, List<IndexEntry> indices)
    {
        bool rtl = PyModInt(bidiLevel, 2) == 1;
        if (isSideways && rtl)
            rtl = false; // invalid per the XPS spec; deterministic fallback
        bool sidewaysPreRotated = isSideways && (Math.Abs(baseMatrix.B) > 1.0e-9 || Math.Abs(baseMatrix.C) > 1.0e-9);
        double penX = isSideways ? SidewaysRunOriginXPoints(fontRef, fontSize) : 0.0;
        double penY = 0.0;
        var entries = indices.Count > 0 ? indices : chars.Select(_ => new IndexEntry()).ToList();
        if (entries.Count < chars.Count)
            entries = [.. entries, .. Enumerable.Range(0, chars.Count - entries.Count).Select(_ => new IndexEntry())];
        bool bold = style is "BoldSimulation" or "BoldItalicSimulation";
        for (int idx = 0; idx < chars.Count; idx++)
        {
            string ch = chars[idx];
            var entry = idx < entries.Count ? entries[idx] : new IndexEntry();
            if (entry.SkipRender)
                continue;
            int? glyphOverride = entry.GlyphId is long g ? (int)Math.Clamp(g, int.MinValue, int.MaxValue) : null;
            // Sideways runs without an explicit advance progress by the em size, as .NET arranges them.
            double advance = isSideways ? fontSize : GlyphAdvancePoints(fontRef, ch, fontSize, glyphOverride);
            if (entry.Advance is double adv)
                advance = (adv * fontSize) / 100.0;
            double uOffset = (entry.UOffset is double u && u != 0 ? u : 0.0) * fontSize / 100.0;
            double vOffset = (entry.VOffset is double v && v != 0 ? v : 0.0) * fontSize / 100.0;
            if (rtl)
                uOffset = -uOffset;
            if (isSideways)
                vOffset = -vOffset;
            double dx = penX + uOffset;
            double dy;
            if (isSideways)
            {
                double width = GlyphAdvancePoints(fontRef, ch, fontSize, glyphOverride);
                dy = penY - (width * 0.5) - vOffset;
                _ = sidewaysPreRotated;
            }
            else
            {
                dy = penY + vOffset;
            }
            var glyphMatrix = Mul(baseMatrix, new Matrix(1.0, 0.0, 0.0, 1.0, dx, dy));
            if (isSideways)
                glyphMatrix = Mul(glyphMatrix, new Matrix(0.0, 1.0, -1.0, 0.0, 0.0, 0.0)); // 90 degrees counter-clockwise
            string glyphFontRef = FontRefForGlyph(fontRef, entry.GlyphId);
            int? glyphIdValue = glyphOverride;
            _builder.AddText(ch, glyphFontRef, fontSize, glyphMatrix, fill, fillOpacity, glyphIdValue);
            if (bold)
                _builder.AddText(ch, glyphFontRef, fontSize, Mul(glyphMatrix, new Matrix(1.0, 0.0, 0.0, 1.0, fontSize * 0.04, 0.0)), fill, fillOpacity, glyphIdValue);
            penX += rtl ? -advance : advance;
        }
    }

    private double GlyphAdvancePoints(string fontRef, string ch, double fontSize, int? glyphIdOverride = null)
    {
        var font = LoadFont(fontRef);
        if (font is null)
        {
            var (unitsPerEm, codeWidths) = LoadFontMetrics(fontRef);
            if (codeWidths.Count > 0 && codeWidths.TryGetValue(Py.Ord(ch), out double widthUnits))
                return (widthUnits / Math.Max(1.0, unitsPerEm)) * fontSize;
            return fontSize * 0.5;
        }
        int glyphId = glyphIdOverride ?? font.GlyphIdForCode(Py.Ord(ch));
        double units = font.GlyphAdvance(glyphId);
        double upem = Math.Max(1.0, font.UnitsPerEm);
        return (units / upem) * fontSize;
    }

    private double SidewaysRunOriginXPoints(string fontRef, double fontSize)
    {
        var font = LoadFont(fontRef);
        if (font is null)
            return fontSize;
        double upem = Math.Max(1.0, font.UnitsPerEm);
        return (font.TypoAscenderUnits() / upem) * fontSize;
    }

    private TrueTypeFont? LoadFont(string fontRef)
    {
        if (_fontCache.TryGetValue(fontRef, out var cached))
            return cached;
        if (_package is null)
            return null;
        string part = NormalizePartRef(fontRef);
        if (!_package.HasPart(part))
            return null;
        TrueTypeFont? font;
        try
        {
            byte[] data = DeobfuscateOdttfBytes(part, _package.Read(part));
            font = new TrueTypeFont(data);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            font = null;
        }
        // Failures are cached too; the original retried, with the same result.
        _fontCache[fontRef] = font;
        return font;
    }

    private (double UnitsPerEm, IReadOnlyDictionary<int, double> CodeWidths) LoadFontMetrics(string fontRef)
    {
        if (_fontMetricCache.TryGetValue(fontRef, out var cached))
            return cached;
        if (_package is null)
            return (1000.0, new Dictionary<int, double>());
        string part = NormalizePartRef(fontRef);
        if (!_package.HasPart(part))
            return (1000.0, new Dictionary<int, double>());
        (double, IReadOnlyDictionary<int, double>) metrics;
        try
        {
            byte[] data = DeobfuscateOdttfBytes(part, _package.Read(part));
            var (units, widths) = TtfMetrics.Parse(data);
            metrics = (units, widths);
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            metrics = (1000.0, new Dictionary<int, double>());
        }
        _fontMetricCache[fontRef] = metrics;
        return metrics;
    }

    private Dictionary<int, int> GidToUnicode(string fontRef)
    {
        if (_fontGidToCode.TryGetValue(fontRef, out var cached))
            return cached;
        var font = LoadFont(fontRef);
        var reverse = new Dictionary<int, int>();
        if (font is not null)
        {
            foreach (var (code, glyphId) in font.Cmap)
            {
                if (glyphId == 0)
                    continue;
                if (!reverse.TryGetValue(glyphId, out int prev) || code < prev)
                    reverse[glyphId] = code;
            }
        }
        _fontGidToCode[fontRef] = reverse;
        return reverse;
    }

    /// <summary>XPS obfuscated fonts XOR their first 32 bytes with the reversed GUID of the
    /// part name. This variant (used for glyph metrics) does not strip braces from the name.</summary>
    internal static byte[] DeobfuscateOdttfBytes(string partName, byte[] data)
    {
        if (!partName.EndsWith(".odttf", StringComparison.OrdinalIgnoreCase) || data.Length < 32)
            return data;
        int slash = partName.LastIndexOf('/');
        string name = slash >= 0 ? partName[(slash + 1)..] : partName;
        string guidText = name[..^6];
        var guid = FromHex(guidText.Replace("-", "", StringComparison.Ordinal));
        if (guid is null || guid.Length != 16)
            return data;
        Array.Reverse(guid);
        var decoded = (byte[])data.Clone();
        for (int i = 0; i < Math.Min(32, decoded.Length); i++)
            decoded[i] ^= guid[i % 16];
        return decoded;
    }

    // Python's bytes.fromhex: hex pairs, ASCII whitespace allowed between pairs.
    private static byte[]? FromHex(string text)
    {
        var bytes = new List<byte>();
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] is ' ' or '\t' or '\n' or '\r' or '\f' or '\v')
            {
                i++;
                continue;
            }
            if (i + 1 >= text.Length || !Uri.IsHexDigit(text[i]) || !Uri.IsHexDigit(text[i + 1]))
                return null;
            bytes.Add((byte)Convert.ToInt32(text.Substring(i, 2), 16));
            i += 2;
        }
        return [.. bytes];
    }
}
