// Ported from image/raster_renderer.py: the pure managed rasterizer for render-model pages.
// Scanline fills, square-brush strokes, nearest-neighbour image sampling and supersampling
// follow the original exactly so that output matches it pixel for pixel.

using XRay.PS.Fonts;
using XRay.PS.Internal;
using XRay.PS.Rendering;

namespace XRay.PS.Imaging;

/// <summary>Renders one page of a <see cref="RenderDocument"/> to a <see cref="RasterSurface"/>.</summary>
public sealed class RasterRenderer
{
    private readonly int _dpi;
    private readonly FontResolver? _fontResolver;
    private readonly Rgba _background;
    private readonly bool _preserveFractionalPageSize;
    private readonly Dictionary<string, TrueTypeFont> _fontCache = new(StringComparer.Ordinal);

    /// <summary>Creates a renderer.</summary>
    /// <param name="dpi">Output resolution.</param>
    /// <param name="fontResolver">Resolves font references to outlines; text draws as boxes without one.</param>
    /// <param name="background">Background RGBA.</param>
    /// <param name="preserveFractionalPageSize">Truncate fractional page sizes instead of rounding.</param>
    public RasterRenderer(int dpi = 72, FontResolver? fontResolver = null, (byte R, byte G, byte B, byte A)? background = null,
        bool preserveFractionalPageSize = false)
    {
        if (dpi <= 0)
            throw new ArgumentException("dpi must be positive", nameof(dpi));
        _dpi = dpi;
        _fontResolver = fontResolver;
        var bg = background ?? (255, 255, 255, 255);
        _background = new Rgba(bg.R, bg.G, bg.B, bg.A);
        _preserveFractionalPageSize = preserveFractionalPageSize;
    }

    /// <summary>Renders the page at <paramref name="pageIndex"/>.</summary>
    public RasterSurface Render(RenderDocument document, int pageIndex = 0)
    {
        if (pageIndex < 0 || pageIndex >= document.Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex), "page_index out of range");
        var page = document.Pages[pageIndex];
        double scale = _dpi / 72.0;
        var (hasCurve, minStrokePx) = AnalyzePageForSupersample(page, scale);
        int supersample = ChooseSupersample(hasCurve, minStrokePx);
        var (widthPx, heightPx) = PagePixelSize(page.Width, page.Height, scale, _preserveFractionalPageSize);
        var surface = RasterSurface.Create(checked(widthPx * supersample), checked(heightPx * supersample),
            (_background.R, _background.G, _background.B, _background.A));
        var resources = document.Resources;
        var clipStack = new Stack<byte[]?>();
        byte[]? currentClip = null;
        double ss = scale * supersample;
        foreach (var command in page.Commands)
        {
            Guard.Check();
            switch (command)
            {
                case StateSaveCommand:
                    clipStack.Push(currentClip is null ? null : (byte[])currentClip.Clone());
                    continue;
                case StateRestoreCommand:
                    currentClip = clipStack.Count > 0 ? clipStack.Pop() : null;
                    surface.ClipMask = currentClip;
                    continue;
                case ClipCommand clip:
                {
                    var mask = BuildClipMask(clip.Path.Segments, ss, surface.Width, surface.Height, clip.FillRule == "evenodd");
                    currentClip = currentClip is null ? mask : IntersectMasks(currentClip, mask);
                    surface.ClipMask = currentClip;
                    continue;
                }
                case PathCommand path:
                    DrawPath(surface, path, ss, resources);
                    break;
                case TextCommand text:
                    DrawText(surface, text, ss, resources);
                    break;
                case ImageCommand image:
                    DrawImage(surface, image, resources, ss);
                    break;
            }
        }
        if (supersample > 1)
            surface = Downsample(surface, supersample);
        return surface;
    }

    private void DrawPath(RasterSurface surface, PathCommand command, double scale, RenderResources resources)
    {
        var rect = AxisAlignedRect(command.Path.Segments);
        List<List<Point>>? subpaths = null;
        if (rect is null || command.Stroke is not null)
            subpaths = FlattenPath(command.Path.Segments, scale);
        if (command.Fill is not null)
        {
            var fillColor = PaintToRgba(command.Fill);
            if (command.Fill.Kind == "Pattern" && command.Fill.Value is PatternPaint pp)
            {
                resources.Patterns.TryGetValue(pp.PatternId, out var pattern);
                if (pattern is TilingPattern tiling)
                {
                    var sampler = PatternSampler(tiling, resources, pp, scale, surface.Height);
                    FillPathsPattern(surface, subpaths ?? [], scale, sampler, command.FillRule == "evenodd");
                }
                else if (pattern is ShadingPattern && fillColor is Rgba shadingColor)
                {
                    if (rect is { } r)
                        FillRect(surface, r, scale, shadingColor);
                    else
                        FillPaths(surface, subpaths ?? [], scale, shadingColor, command.FillRule == "evenodd");
                }
            }
            else if (fillColor is Rgba color)
            {
                if (rect is { } r)
                    FillRect(surface, r, scale, color);
                else
                    FillPaths(surface, subpaths ?? [], scale, color, command.FillRule == "evenodd");
            }
        }
        if (command.Stroke is not null)
        {
            var strokeColor = PaintToRgba(command.StrokePaint ?? command.Fill) ?? Rgba.Black;
            int strokeWidth = Math.Max(1, (int)Py.Round(command.Stroke.LineWidth * scale));
            var dashPattern = command.Stroke.Dash.Length > 0 ? command.Stroke.Dash.Select(v => v * scale).ToList() : [];
            double dashPhase = command.Stroke.DashPhase * scale;
            foreach (var subpath in subpaths ?? [])
            {
                var points = ToPixels(subpath, scale, surface.Height);
                if (dashPattern.Count > 0)
                {
                    DrawDashedPolyline(surface, points, dashPattern, dashPhase, strokeWidth, strokeColor);
                }
                else
                {
                    for (int i = 0; i + 1 < points.Count; i++)
                        DrawStrokedSegment(surface, points[i], points[i + 1], strokeWidth, strokeColor);
                }
            }
        }
    }

    private void DrawText(RasterSurface surface, TextCommand command, double scale, RenderResources resources)
    {
        var color = PaintToRgba(command.Fill) ?? Rgba.Black;
        var font = ResolveFont(command.FontRef);
        if (font is not null)
        {
            FontResource? metricsFont = null;
            if (_fontResolver is not null)
            {
                try
                {
                    metricsFont = _fontResolver.Resolve(command.FontRef);
                }
                catch (Exception exc) when (exc is not (OperationCanceledException or PostScript.PsResourceLimitError))
                {
                    metricsFont = null;
                }
            }
            DrawTextOutline(surface, command, scale, resources, font, metricsFont);
            return;
        }
        FontResource? fallbackFont = null;
        if (_fontResolver is not null)
        {
            try
            {
                fallbackFont = _fontResolver.Resolve(command.FontRef);
            }
            catch (Exception exc) when (exc is not (OperationCanceledException or PostScript.PsResourceLimitError))
            {
                fallbackFont = null;
            }
        }
        Func<int, int, Rgba?>? patternSampler = null;
        if (command.Fill is { Kind: "Pattern", Value: PatternPaint pp } &&
            resources.Patterns.TryGetValue(pp.PatternId, out var pattern) && pattern is TilingPattern tiling)
        {
            patternSampler = PatternSampler(tiling, resources, pp, scale, surface.Height);
        }
        double fontSize = command.FontSize;
        double x = command.Matrix.E;
        double y = command.Matrix.F;
        double shift = PalatinoBaselineShift(command.FontRef, fontSize);
        if (shift != 0)
        {
            x -= command.Matrix.C * shift;
            y -= command.Matrix.D * shift;
        }
        double a = command.Matrix.A;
        double b = command.Matrix.B;
        foreach (string ch in Py.Chars(command.Text))
        {
            if (ch == " ")
            {
                if (fallbackFont is not null)
                {
                    double units = GlyphAdvanceUnits(fallbackFont, ch);
                    double adv = units / Math.Max(1.0, fallbackFont.UnitsPerEm) * fontSize;
                    x += a * adv;
                    y += b * adv;
                }
                continue;
            }
            double width = fontSize * 0.5;
            double height = fontSize;
            if (patternSampler is not null)
                FillRectPattern(surface, x, y, width, height, scale, patternSampler);
            else
                DrawRect(surface, x, y, width, height, scale, color);
            if (fallbackFont is null)
            {
                x += fontSize * 0.6;
                continue;
            }
            double advanceUnits = GlyphAdvanceUnits(fallbackFont, ch);
            double advance = advanceUnits / Math.Max(1.0, fallbackFont.UnitsPerEm) * fontSize;
            x += a * advance;
            y += b * advance;
        }
    }

    private TrueTypeFont? ResolveFont(string fontRef)
    {
        if (_fontResolver is null)
            return null;
        FontResource? resource;
        try
        {
            resource = _fontResolver.Resolve(fontRef);
        }
        catch (Exception exc) when (exc is not (OperationCanceledException or PostScript.PsResourceLimitError))
        {
            resource = null;
        }
        byte[]? embeddedData = resource?.FontProgram;
        if (embeddedData is not { Length: > 0 })
            embeddedData = _fontResolver.GetEmbeddedType42(fontRef)?.Data;
        if (embeddedData is { Length: > 0 })
        {
            string key = "embedded:" + fontRef;
            if (_fontCache.TryGetValue(key, out var cached))
                return cached;
            TrueTypeFont? font;
            try
            {
                font = new TrueTypeFont(embeddedData);
            }
            catch (Exception exc) when (exc is not (OperationCanceledException or PostScript.PsResourceLimitError))
            {
                font = null;
            }
            if (font is not null)
            {
                _fontCache[key] = font;
                return font;
            }
        }
        var path = _fontResolver.ResolveTtfPath(fontRef);
        if (path is null)
            return null;
        string pathKey = path.ToString();
        if (_fontCache.TryGetValue(pathKey, out var cachedPath))
            return cachedPath;
        try
        {
            var font = TrueTypeFont.Load(path);
            _fontCache[pathKey] = font;
            return font;
        }
        catch (Exception exc) when (exc is not (OperationCanceledException or PostScript.PsResourceLimitError))
        {
            return null;
        }
    }

    private double GlyphAdvanceUnits(FontResource font, string ch)
    {
        int code = Py.Ord(ch);
        double? widthUnits = null;
        if (font.CodeWidths is not null && font.CodeWidths.TryGetValue(code, out double w))
            widthUnits = w;
        if (widthUnits is null)
        {
            string glyphName = font.Encoding.TryGetValue(code, out string? g) ? g : ".notdef";
            widthUnits = _fontResolver is not null ? _fontResolver.GetGlyphWidth(font, glyphName) : 0.0;
        }
        if (widthUnits == 0.0)
            widthUnits = font.UnitsPerEm * 0.5;
        return widthUnits.Value;
    }

    private void DrawTextOutline(RasterSurface surface, TextCommand command, double scale, RenderResources resources,
        TrueTypeFont font, FontResource? metricsFont)
    {
        var m = command.Matrix;
        double a = m.A, b = m.B, c = m.C, d = m.D, e = m.E, f = m.F;
        double shift = PalatinoBaselineShift(command.FontRef, command.FontSize);
        if (shift != 0)
        {
            e -= c * shift;
            f -= d * shift;
        }
        double sizeScale = (command.FontSize != 0 ? command.FontSize : 1.0) / Math.Max(1.0, font.UnitsPerEm);
        var fill = command.Fill;
        Func<int, int, Rgba?>? patternSampler = null;
        Rgba? fillColor = fill is not null ? PaintToRgba(fill) : Rgba.Black;
        if (fill is { Kind: "Pattern", Value: PatternPaint pp } &&
            resources.Patterns.TryGetValue(pp.PatternId, out var pattern) && pattern is TilingPattern tiling)
        {
            patternSampler = PatternSampler(tiling, resources, pp, scale, surface.Height);
        }
        foreach (string ch in Py.Chars(command.Text))
        {
            int glyphId = font.GlyphIdForCode(Py.Ord(ch));
            var contours = font.GlyphOutline(glyphId);
            if (contours.Count > 0)
            {
                // Glyphs are always filled even-odd here: TrueType winding varies across fonts and
                // nonzero filling may close counters.
                var path = GlyphOutlines.ContoursToPath(contours, sizeScale, a, b, c, d, e, f);
                var subpaths = FlattenPath(path.Segments, scale);
                if (patternSampler is not null)
                    FillPathsPattern(surface, subpaths, scale, patternSampler, evenOdd: true);
                else if (fillColor is Rgba color)
                    FillPaths(surface, subpaths, scale, color, evenOdd: true);
            }
            double advance;
            if (metricsFont is not null)
            {
                double units = GlyphAdvanceUnits(metricsFont, ch);
                advance = units / Math.Max(1.0, metricsFont.UnitsPerEm) * command.FontSize;
            }
            else
            {
                advance = font.GlyphAdvance(glyphId) * sizeScale;
            }
            e += a * advance;
            f += b * advance;
        }
    }

    private static void DrawImagePlaceholder(RasterSurface surface, ImageCommand command, double scale) =>
        DrawRect(surface, command.Matrix.E, command.Matrix.F, command.Width, command.Height, scale, new Rgba(220, 220, 220, 255));

    private static void DrawImage(RasterSurface surface, ImageCommand command, RenderResources resources, double scale)
    {
        if (!resources.Images.TryGetValue(command.ImageId, out var resource))
        {
            DrawImagePlaceholder(surface, command, scale);
            return;
        }
        var (imageData, imageWidth, imageHeight, colorSpace, bitsPerComponent) = ImageDecoding.Materialize(resource);
        var matrix = command.Matrix;
        var inv = InvertMatrix(matrix);
        if (inv is not Matrix invMatrix)
        {
            DrawImagePlaceholder(surface, command, scale);
            return;
        }
        Span<(double X, double Y)> corners =
        [
            ApplyMatrix(matrix, 0.0, 0.0),
            ApplyMatrix(matrix, 1.0, 0.0),
            ApplyMatrix(matrix, 0.0, 1.0),
            ApplyMatrix(matrix, 1.0, 1.0),
        ];
        double minX = corners[0].X, maxX = corners[0].X, minY = corners[0].Y, maxY = corners[0].Y;
        for (int i = 1; i < 4; i++)
        {
            minX = PyMin(minX, corners[i].X);
            maxX = PyMax(maxX, corners[i].X);
            minY = PyMin(minY, corners[i].Y);
            maxY = PyMax(maxY, corners[i].Y);
        }
        int px0 = Math.Max(0, FloorInt(minX * scale));
        int px1 = Math.Min(surface.Width - 1, CeilInt(maxX * scale));
        int py0 = Math.Max(0, FloorInt((surface.Height - 1) - maxY * scale));
        int py1 = Math.Min(surface.Height - 1, CeilInt((surface.Height - 1) - minY * scale));
        if (px1 < px0 || py1 < py0)
            return;

        var maskColor = command.MaskPaint is not null ? (PaintToRgba(command.MaskPaint) ?? Rgba.Black) : Rgba.Black;
        int aaSamples = resource.Width <= 128 || resource.Height <= 128 ? 2 : 1;
        double projectedW = Math.Max(0.0, (maxX - minX) * scale);
        bool isMask = resource.Mask || command.Mask;
        bool tinyMaskMode = isMask && resource.Width <= 32 && projectedW <= 3.0;
        double smallMaskXBias = tinyMaskMode ? 0.5 : 0.0;
        // PostScript imagemask polarity: true paints 1-bit samples, false paints 0-bit samples.
        bool invertMask = !resource.MaskPolarity;
        int maskSamples = tinyMaskMode ? 1 : (aaSamples > 1 ? 8 : 1);
        for (int py = py0; py <= py1; py++)
        {
            for (int px = px0; px <= px1; px++)
            {
                if (isMask)
                {
                    var (coverage, total) = SampleMaskCoverage(invMatrix, px, py, scale, surface.Height, maskSamples,
                        imageData, imageWidth, imageHeight, invertMask, smallMaskXBias);
                    if (total == 0 || coverage <= 0.0)
                        continue;
                    if (tinyMaskMode || coverage >= 0.999)
                    {
                        surface.SetPixel(px, py, maskColor);
                        continue;
                    }
                    var bg = surface.GetPixel(px, py);
                    double alpha = coverage * (maskColor.A / 255.0);
                    double invAlpha = 1.0 - alpha;
                    int outA = (int)Py.Round(bg.A * invAlpha + maskColor.A * alpha);
                    surface.SetPixel(px, py, Rgba.FromInts(
                        (int)Py.Round(bg.R * invAlpha + maskColor.R * alpha),
                        (int)Py.Round(bg.G * invAlpha + maskColor.G * alpha),
                        (int)Py.Round(bg.B * invAlpha + maskColor.B * alpha),
                        Math.Clamp(outA, 0, 255)));
                    continue;
                }
                var sampled = SampleImageColorAverage(invMatrix, px, py, scale, surface.Height, aaSamples, imageData,
                    imageWidth, imageHeight, colorSpace, bitsPerComponent, resource.Decode, 0.0);
                if (sampled is not Rgba color)
                    continue;
                if (color.A >= 255)
                {
                    surface.SetPixel(px, py, color);
                }
                else if (color.A > 0)
                {
                    var bg = surface.GetPixel(px, py);
                    if (bg.A == 0)
                    {
                        surface.SetPixel(px, py, color);
                    }
                    else
                    {
                        double alpha = color.A / 255.0;
                        surface.SetPixel(px, py, Rgba.FromInts(
                            (int)Py.Round(bg.R * (1.0 - alpha) + color.R * alpha),
                            (int)Py.Round(bg.G * (1.0 - alpha) + color.G * alpha),
                            (int)Py.Round(bg.B * (1.0 - alpha) + color.B * alpha),
                            255));
                    }
                }
            }
        }
    }

    private static void DrawRect(RasterSurface surface, double x, double y, double width, double height, double scale, Rgba color)
    {
        int x0 = (int)Py.Round(x * scale);
        int y0 = (int)Py.Round((surface.Height - 1) - y * scale - height * scale);
        int x1 = (int)Py.Round(x * scale + width * scale);
        int y1 = (int)Py.Round((surface.Height - 1) - y * scale);
        for (int py = Math.Min(y0, y1); py <= Math.Max(y0, y1); py++)
        {
            for (int px = Math.Min(x0, x1); px <= Math.Max(x0, x1); px++)
                surface.SetPixel(px, py, color);
        }
    }

    private static void FillRectPattern(RasterSurface surface, double x, double y, double width, double height, double scale, Func<int, int, Rgba?> sampler)
    {
        int x0 = (int)Py.Round(x * scale);
        int y0 = (int)Py.Round((surface.Height - 1) - y * scale - height * scale);
        int x1 = (int)Py.Round(x * scale + width * scale);
        int y1 = (int)Py.Round((surface.Height - 1) - y * scale);
        for (int py = Math.Min(y0, y1); py <= Math.Max(y0, y1); py++)
        {
            for (int px = Math.Min(x0, x1); px <= Math.Max(x0, x1); px++)
            {
                if (sampler(px, py) is Rgba color)
                    surface.SetPixel(px, py, color);
            }
        }
    }

    internal static double PalatinoBaselineShift(string fontRef, double fontSize)
    {
        string lower = fontRef.ToLowerInvariant();
        if (!lower.StartsWith("palatino", StringComparison.Ordinal))
            return 0.0;
        // Palatino Bold Italic already aligns with the baseline output.
        if (lower.Contains("bolditalic", StringComparison.Ordinal) || lower.Contains("bold-italic", StringComparison.Ordinal))
            return 0.0;
        double factor;
        if (fontSize <= 100.0)
            factor = 0.16;
        else if (fontSize >= 250.0)
            factor = 0.19;
        else
            factor = 0.16 + (fontSize - 100.0) * (0.03 / 150.0);
        return fontSize * factor;
    }

    internal static (int Width, int Height) PagePixelSize(double widthPt, double heightPt, double scale, bool preserveFractional)
    {
        if (preserveFractional)
        {
            var snapped = SnapA4ApproxPagePixelSize(widthPt, heightPt, scale);
            if (snapped is not null)
                return snapped.Value;
            return (Math.Max(1, (int)Py.Int(widthPt * scale - 1e-6)), Math.Max(1, (int)Py.Int(heightPt * scale - 1e-6)));
        }
        return (Math.Max(1, (int)Py.Round(NormalizePagePoints(widthPt) * scale)), Math.Max(1, (int)Py.Round(NormalizePagePoints(heightPt) * scale)));
    }

    private static double NormalizePagePoints(double value)
    {
        double rounded = Math.Round(value, MidpointRounding.ToEven);
        if (Math.Abs(value - rounded) < 1e-6)
            return rounded;
        return Py.Int(value);
    }

    private static (int, int)? SnapA4ApproxPagePixelSize(double pageWidth, double pageHeight, double scale)
    {
        const double tolerance = 0.3;
        double dpi = scale * 72.0;
        int a4W = Math.Max(1, (int)Py.Int((210.0 / 25.4) * dpi));
        int a4H = Math.Max(1, (int)Py.Int((297.0 / 25.4) * dpi));
        if (Math.Abs(pageWidth - 595.5) <= tolerance && Math.Abs(pageHeight - 842.25) <= tolerance)
            return (a4W, a4H);
        if (Math.Abs(pageWidth - 842.25) <= tolerance && Math.Abs(pageHeight - 595.5) <= tolerance)
            return (a4H, a4W);
        return null;
    }

    internal static Rgba? PaintToRgba(Paint? paint)
    {
        if (paint is null)
            return null;
        switch (paint.Kind)
        {
            case "DeviceRGB" when paint.Value is double[] rgb:
                if (rgb.Length != 3)
                    throw Py.ValueError("too many values to unpack");
                return new Rgba(ToByte(rgb[0]), ToByte(rgb[1]), ToByte(rgb[2]), 255);
            case "DeviceGray":
            {
                double gray = paint.Value switch
                {
                    double[] arr => arr[0],
                    double d => d,
                    _ => throw new HostErrorException("TypeError", "unsupported gray value"),
                };
                byte g = ToByte(gray);
                return new Rgba(g, g, g, 255);
            }
            case "DeviceCMYK" when paint.Value is double[] cmyk:
            {
                if (cmyk.Length != 4)
                    throw Py.ValueError("too many values to unpack");
                double r = 1.0 - Math.Min(1.0, cmyk[0] + cmyk[3]);
                double g = 1.0 - Math.Min(1.0, cmyk[1] + cmyk[3]);
                double b = 1.0 - Math.Min(1.0, cmyk[2] + cmyk[3]);
                return new Rgba(ToByte(r), ToByte(g), ToByte(b), 255);
            }
            default:
                return Rgba.Black;
        }
    }

    internal static byte ToByte(double value)
    {
        if (value > 1.0)
            value /= 255.0;
        return (byte)Math.Clamp(Py.Int(value * 255), 0, 255);
    }

    private static int RowBytes(int width, int bitsPerComponent, int components)
    {
        long rowBits = (long)Math.Max(0, width) * Math.Max(1, bitsPerComponent) * Math.Max(1, components);
        return (int)((rowBits + 7) / 8);
    }

    private static bool SampleMaskBit(byte[] data, int width, int x, int y)
    {
        long row = RowBytes(width, 1, 1);
        long index = y * row + (x / 8);
        if (index < 0 || index >= data.Length)
            return false;
        int shift = 7 - (x % 8);
        return ((data[index] >> shift) & 1) == 1;
    }

    private static Rgba? SampleImageRgba(byte[] data, int width, int height, string colorSpace, int bitsPerComponent, double[]? decode, int x, int y)
    {
        if (x < 0 || y < 0 || x >= width || y >= height)
            return null;
        if (colorSpace == "DeviceGray")
        {
            if (bitsPerComponent == 1)
            {
                bool bit = SampleMaskBit(data, width, x, y);
                byte gray = DecodedGray(bit ? 1.0 : 0.0, decode);
                return new Rgba(gray, gray, gray, 255);
            }
            if (bitsPerComponent == 8)
            {
                long index = (long)y * RowBytes(width, 8, 1) + x;
                if (index < 0 || index >= data.Length)
                    return null;
                byte gray = DecodedGray(data[index] / 255.0, decode);
                return new Rgba(gray, gray, gray, 255);
            }
            return null;
        }
        if (colorSpace == "DeviceRGB" && bitsPerComponent == 8)
        {
            long index = (long)y * RowBytes(width, 8, 3) + x * 3L;
            if (index < 0 || index + 2 >= data.Length)
                return null;
            double r = data[index] / 255.0, g = data[index + 1] / 255.0, b = data[index + 2] / 255.0;
            if (decode is not null && decode.Length >= 6)
            {
                r = decode[0] + r * (decode[1] - decode[0]);
                g = decode[2] + g * (decode[3] - decode[2]);
                b = decode[4] + b * (decode[5] - decode[4]);
            }
            return new Rgba(ToByte(r), ToByte(g), ToByte(b), 255);
        }
        if (colorSpace == "DeviceCMYK" && bitsPerComponent == 8)
        {
            long index = (long)y * RowBytes(width, 8, 4) + x * 4L;
            if (index < 0 || index + 3 >= data.Length)
                return null;
            double c = data[index] / 255.0, m = data[index + 1] / 255.0, yk = data[index + 2] / 255.0, k = data[index + 3] / 255.0;
            if (decode is not null && decode.Length >= 8)
            {
                c = decode[0] + c * (decode[1] - decode[0]);
                m = decode[2] + m * (decode[3] - decode[2]);
                yk = decode[4] + yk * (decode[5] - decode[4]);
                k = decode[6] + k * (decode[7] - decode[6]);
            }
            return new Rgba(ToByte(1.0 - Math.Min(1.0, c + k)), ToByte(1.0 - Math.Min(1.0, m + k)), ToByte(1.0 - Math.Min(1.0, yk + k)), 255);
        }
        return null;
    }

    private static (double Coverage, int Total) SampleMaskCoverage(Matrix inv, int px, int py, double scale, int surfaceHeight, int samples,
        byte[] data, int width, int height, bool invertMask, double xBias)
    {
        int painted = 0;
        int total = 0;
        for (int syIndex = 0; syIndex < samples; syIndex++)
        {
            for (int sxIndex = 0; sxIndex < samples; sxIndex++)
            {
                double userX = (px + xBias + (sxIndex + 0.5) / samples) / scale;
                double userY = (surfaceHeight - (py + (syIndex + 0.5) / samples)) / scale;
                if (SampleSourceCoords(inv, userX, userY, width, height) is not var (sx, sy))
                    continue;
                bool bit = SampleMaskBit(data, width, sx, sy);
                bool paintBit = invertMask ? !bit : bit;
                total++;
                if (paintBit)
                    painted++;
            }
        }
        if (total == 0)
            return (0.0, 0);
        return ((double)painted / total, total);
    }

    private static Rgba? SampleImageColorAverage(Matrix inv, int px, int py, double scale, int surfaceHeight, int samples, byte[] data,
        int width, int height, string colorSpace, int bitsPerComponent, double[]? decode, double xBias)
    {
        long accR = 0, accG = 0, accB = 0;
        int total = 0;
        for (int syIndex = 0; syIndex < samples; syIndex++)
        {
            for (int sxIndex = 0; sxIndex < samples; sxIndex++)
            {
                double userX = (px + xBias + (sxIndex + 0.5) / samples) / scale;
                double userY = (surfaceHeight - (py + (syIndex + 0.5) / samples)) / scale;
                if (SampleSourceCoords(inv, userX, userY, width, height) is not var (sx, sy))
                    continue;
                if (SampleImageRgba(data, width, height, colorSpace, bitsPerComponent, decode, sx, sy) is not Rgba rgba)
                    continue;
                accR += rgba.R;
                accG += rgba.G;
                accB += rgba.B;
                total++;
            }
        }
        if (total == 0)
            return null;
        double coverage = total / (double)(samples * samples);
        int alpha = (int)Py.Round(255.0 * coverage);
        return Rgba.FromInts((int)Py.Round(accR / (double)total), (int)Py.Round(accG / (double)total), (int)Py.Round(accB / (double)total), Math.Clamp(alpha, 0, 255));
    }

    private static (int X, int Y)? SampleSourceCoords(Matrix inv, double userX, double userY, int width, int height)
    {
        var (u, v) = ApplyMatrix(inv, userX, userY);
        if (u < 0.0 || v < 0.0 || u >= 1.0 || v >= 1.0)
            return null;
        int sx = Math.Min(width - 1, Math.Max(0, (int)Py.Int(u * (width - 1) + 0.5)));
        int sy = Math.Min(height - 1, Math.Max(0, (int)Py.Int((1.0 - v) * (height - 1) + 0.5)));
        return (sx, sy);
    }

    private static byte DecodedGray(double value, double[]? decode)
    {
        if (decode is null || decode.Length < 2)
            return ToByte(value);
        return ToByte(decode[0] + value * (decode[1] - decode[0]));
    }

    internal static List<List<Point>> FlattenPath(IReadOnlyList<PathSegment> segments, double scale, int curveSteps = 36)
    {
        var subpaths = new List<List<Point>>();
        var current = new List<Point>();
        Point? start = null;
        Point? currentPoint = null;
        int total = segments.Count;
        for (int idx = 0; idx < total; idx++)
        {
            var segment = segments[idx];
            switch (segment.Kind)
            {
                case PathSegmentKind.Move:
                    if (current.Count > 0)
                        subpaths.Add(current);
                    current = [segment.Points[0]];
                    start = segment.Points[0];
                    currentPoint = segment.Points[0];
                    break;
                case PathSegmentKind.Line:
                    if (currentPoint is null)
                    {
                        current = [segment.Points[0]];
                        start = segment.Points[0];
                    }
                    else
                    {
                        current.Add(segment.Points[0]);
                    }
                    currentPoint = segment.Points[0];
                    break;
                case PathSegmentKind.Curve:
                {
                    if (currentPoint is not Point p0)
                        continue;
                    var p1 = segment.Points[0];
                    var p2 = segment.Points[1];
                    var p3 = segment.Points[2];
                    int steps = CurveSteps(p0, p1, p2, p3, scale, curveSteps);
                    for (int step = 1; step <= steps; step++)
                    {
                        double t = (double)step / steps;
                        double mt = 1 - t;
                        double x = Math.Pow(mt, 3) * p0.X + 3 * Math.Pow(mt, 2) * t * p1.X + 3 * mt * Math.Pow(t, 2) * p2.X + Math.Pow(t, 3) * p3.X;
                        double y = Math.Pow(mt, 3) * p0.Y + 3 * Math.Pow(mt, 2) * t * p1.Y + 3 * mt * Math.Pow(t, 2) * p2.Y + Math.Pow(t, 3) * p3.Y;
                        current.Add(new Point(x, y));
                    }
                    currentPoint = p3;
                    break;
                }
                case PathSegmentKind.Close:
                    if (start is Point s && current.Count > 0)
                        current.Add(s);
                    if (current.Count > 0)
                        subpaths.Add(current);
                    if (idx + 1 < total && start is Point s2)
                    {
                        // Keep the current point at the subpath start so a following lineto
                        // connects correctly after closepath.
                        current = [s2];
                        currentPoint = s2;
                    }
                    else
                    {
                        current = [];
                        start = null;
                        currentPoint = null;
                    }
                    break;
            }
        }
        if (current.Count > 0)
            subpaths.Add(current);
        return subpaths;
    }

    private static int CurveSteps(Point p0, Point p1, Point p2, Point p3, double scale, int baseSteps)
    {
        double length = Py.Hypot(p1.X - p0.X, p1.Y - p0.Y) + Py.Hypot(p2.X - p1.X, p2.Y - p1.Y) + Py.Hypot(p3.X - p2.X, p3.Y - p2.Y);
        long adaptive = Py.Int(length * scale / 6);
        if (adaptive < baseStepsLong(baseSteps))
            return baseSteps;
        return (int)Math.Min(200, adaptive);

        static long baseStepsLong(int v) => v;
    }

    private static List<Point> EnsureClosed(List<Point> points)
    {
        if (points.Count == 0 || points[0] == points[^1])
            return points;
        var closed = new List<Point>(points.Count + 1);
        closed.AddRange(points);
        closed.Add(points[0]);
        return closed;
    }

    internal static (double XMin, double YMin, double XMax, double YMax)? AxisAlignedRect(IReadOnlyList<PathSegment> segments)
    {
        var points = new List<Point>();
        Point? start = null;
        Point? current = null;
        foreach (var segment in segments)
        {
            switch (segment.Kind)
            {
                case PathSegmentKind.Move:
                    if (points.Count > 0)
                        return null;
                    start = segment.Points[0];
                    current = start;
                    points.Add(segment.Points[0]);
                    break;
                case PathSegmentKind.Line:
                    if (current is null)
                        return null;
                    current = segment.Points[0];
                    points.Add(segment.Points[0]);
                    break;
                case PathSegmentKind.Close:
                    if (start is not Point s)
                        return null;
                    points.Add(s);
                    break;
                default:
                    return null;
            }
        }
        if (points.Count < 4)
            return null;
        if (points[0] == points[^1])
            points.RemoveAt(points.Count - 1);
        if (points.Count != 4)
            return null;
        var xs = new HashSet<double>();
        var ys = new HashSet<double>();
        foreach (var pt in points)
        {
            xs.Add(Py.Round(pt.X, 6) + 0.0);
            ys.Add(Py.Round(pt.Y, 6) + 0.0);
        }
        if (xs.Count != 2 || ys.Count != 2)
            return null;
        for (int idx = 0; idx < 4; idx++)
        {
            var a = points[idx];
            var b = points[(idx + 1) % 4];
            if (!(Math.Abs(a.X - b.X) < 1e-6 || Math.Abs(a.Y - b.Y) < 1e-6))
                return null;
        }
        return (xs.Min(), ys.Min(), xs.Max(), ys.Max());
    }

    private static List<(double X, double Y)> ToPixels(List<Point> points, double scale, int heightPx)
    {
        var result = new List<(double, double)>(points.Count);
        foreach (var point in points)
            result.Add((point.X * scale, (heightPx - 1) - point.Y * scale));
        return result;
    }

    private static void DrawLine(RasterSurface surface, (double X, double Y) start, (double X, double Y) end, int width, Rgba color)
    {
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        long steps = Py.Int(Math.Max(Math.Abs(dx), Math.Abs(dy))) + 1;
        for (long step = 0; step <= steps; step++)
        {
            double t = steps != 0 ? (double)step / steps : 0.0;
            double x = start.X + dx * t;
            double y = start.Y + dy * t;
            DrawSquare(surface, ClampInt(Py.Round(x)), ClampInt(Py.Round(y)), width, color);
        }
    }

    private static void DrawStrokedSegment(RasterSurface surface, (double X, double Y) start, (double X, double Y) end, int width, Rgba color)
    {
        if (width <= 6)
        {
            DrawLine(surface, start, end, width, color);
            return;
        }
        double dx = end.X - start.X;
        double dy = end.Y - start.Y;
        double length = Py.Hypot(dx, dy);
        if (length <= 1e-6)
        {
            DrawSquare(surface, ClampInt(Py.Round(start.X)), ClampInt(Py.Round(start.Y)), width, color);
            return;
        }
        double half = width / 2.0;
        double ux = dx / length;
        double uy = dy / length;
        double px = -uy * half;
        double py = ux * half;
        FillPolygonPixels(surface,
        [
            (start.X + px, start.Y + py),
            (start.X - px, start.Y - py),
            (end.X - px, end.Y - py),
            (end.X + px, end.Y + py),
        ], color);
    }

    private static List<double> NormalizeDashPattern(List<double> pattern)
    {
        var cleaned = pattern.Select(Math.Abs).Where(v => v > 1e-6).ToList();
        if (cleaned.Count == 0)
            return [];
        if (cleaned.Count % 2 == 1)
            cleaned.AddRange(cleaned.ToList());
        return cleaned;
    }

    private static (List<double> Pattern, int Index, double Remaining) InitDashState(List<double> pattern, double phase)
    {
        var normalized = NormalizeDashPattern(pattern);
        if (normalized.Count == 0)
            return ([], 0, 0.0);
        double total = 0.0;
        foreach (double v in normalized)
            total += v;
        if (total <= 1e-6)
            return ([], 0, 0.0);
        phase = Py.Mod(phase, total);
        int index = 0;
        while (phase > normalized[index])
        {
            Guard.Check();
            phase -= normalized[index];
            index = (index + 1) % normalized.Count;
        }
        return (normalized, index, normalized[index] - phase);
    }

    private static void DrawDashedPolyline(RasterSurface surface, List<(double X, double Y)> points, List<double> pattern, double phase, int width, Rgba color)
    {
        if (width <= 1)
        {
            // Thin strokes fall back to solid segments.
            for (int i = 0; i + 1 < points.Count; i++)
                DrawStrokedSegment(surface, points[i], points[i + 1], width, color);
            return;
        }
        var (normalized, index, remaining) = InitDashState(pattern, phase);
        if (normalized.Count == 0)
        {
            for (int i = 0; i + 1 < points.Count; i++)
                DrawStrokedSegment(surface, points[i], points[i + 1], width, color);
            return;
        }
        bool on = index % 2 == 0;
        for (int i = 0; i + 1 < points.Count; i++)
        {
            var (x0, y0) = points[i];
            var (x1, y1) = points[i + 1];
            double dx = x1 - x0;
            double dy = y1 - y0;
            double totalLen = Py.Hypot(dx, dy);
            if (totalLen <= 1e-6)
                continue;
            double remainingLen = totalLen;
            double traveled = 0.0;
            while (remainingLen > 1e-6)
            {
                Guard.Check();
                double step = Math.Min(remainingLen, remaining);
                double t0 = traveled / totalLen;
                double t1 = (traveled + step) / totalLen;
                if (on)
                    DrawStrokedSegment(surface, (x0 + dx * t0, y0 + dy * t0), (x0 + dx * t1, y0 + dy * t1), width, color);
                remainingLen -= step;
                traveled += step;
                remaining -= step;
                if (remaining <= 1e-6)
                {
                    index = (index + 1) % normalized.Count;
                    remaining = normalized[index];
                    on = index % 2 == 0;
                }
            }
        }
    }

    private static void DrawSquare(RasterSurface surface, int cx, int cy, int size, Rgba color)
    {
        int radius = Math.Max(0, size / 2);
        int yStart = Math.Max(cy - radius, -1);
        int yEnd = Math.Min(cy + radius, surface.Height);
        int xStart = Math.Max(cx - radius, -1);
        int xEnd = Math.Min(cx + radius, surface.Width);
        for (int y = yStart; y <= yEnd; y++)
        {
            Guard.Check();
            for (int x = xStart; x <= xEnd; x++)
                surface.SetPixel(x, y, color);
        }
    }

    // Python's sort is stable; ties keep insertion order.
    private static void SortIntersections(List<(double X, int Direction)> intersections)
    {
        if (intersections.Count < 2)
            return;
        var sorted = intersections.Select((item, i) => (item, i)).OrderBy(p => p.item.X).ThenBy(p => p.i).Select(p => p.item).ToList();
        intersections.Clear();
        intersections.AddRange(sorted);
    }

    private static void FillPaths(RasterSurface surface, List<List<Point>> subpaths, double scale, Rgba color, bool evenOdd)
    {
        var intersectionsByRow = new Dictionary<int, List<(double X, int Direction)>>();
        foreach (var subpath in subpaths)
        {
            if (subpath.Count < 3)
                continue;
            var points = ToPixels(EnsureClosed(subpath), scale, surface.Height);
            for (int i = 0; i + 1 < points.Count; i++)
            {
                var (x1, y1) = points[i];
                var (x2, y2) = points[i + 1];
                if (y1 == y2)
                    continue;
                if (!double.IsFinite(y1) || !double.IsFinite(y2))
                    throw double.IsNaN(y1) || double.IsNaN(y2) ? Py.ValueError("cannot convert float NaN to integer") : Py.OverflowError("cannot convert float infinity to integer");
                int direction = 1;
                if (y1 > y2)
                {
                    (x1, y1, x2, y2) = (x2, y2, x1, y1);
                    direction = -1;
                }
                double yStartD = Math.Floor(y1);
                double yEndD = Math.Ceiling(y2);
                if (yEndD < 0 || yStartD >= surface.Height)
                    continue;
                int yStart = (int)Math.Max(0, yStartD);
                int yEnd = (int)Math.Min(surface.Height - 1, yEndD);
                for (int y = yStart; y <= yEnd; y++)
                {
                    Guard.Check();
                    double scanY = y + 0.5;
                    if (scanY < y1 || scanY >= y2)
                        continue;
                    double t = (scanY - y1) / (y2 - y1);
                    double x = x1 + t * (x2 - x1);
                    if (!intersectionsByRow.TryGetValue(y, out var list))
                        intersectionsByRow[y] = list = [];
                    list.Add((x, direction));
                }
            }
        }
        foreach (var (y, intersections) in intersectionsByRow)
        {
            SortIntersections(intersections);
            FillRow(intersections, evenOdd, (start, end) => FillSpan(surface, y, CeilInt(start), FloorInt(end), color));
        }
    }

    /// <summary>Walks sorted crossings and reports each filled span (start x, end x).</summary>
    private static void FillRow(List<(double X, int Direction)> intersections, bool evenOdd, Action<double, double> span)
    {
        if (evenOdd)
        {
            for (int idx = 0; idx + 1 < intersections.Count; idx += 2)
                span(intersections[idx].X, intersections[idx + 1].X);
            return;
        }
        int winding = 0;
        double? startX = null;
        foreach (var (x, direction) in intersections)
        {
            int prev = winding;
            winding += direction;
            if (prev == 0 && winding != 0)
            {
                startX = x;
            }
            else if (prev != 0 && winding == 0 && startX is double s)
            {
                span(s, x);
                startX = null;
            }
        }
    }

    private static void FillRect(RasterSurface surface, (double X0, double Y0, double X1, double Y1) rect, double scale, Rgba color)
    {
        double minX = Math.Min(rect.X0, rect.X1);
        double maxX = Math.Max(rect.X0, rect.X1);
        double minY = Math.Min(rect.Y0, rect.Y1);
        double maxY = Math.Max(rect.Y0, rect.Y1);
        double cx0 = minX * scale, cy0 = (surface.Height - 1) - minY * scale;
        double cx1 = maxX * scale, cy1 = (surface.Height - 1) - maxY * scale;
        int xStart = FloorInt(Math.Min(cx0, cx1));
        int xEnd = CeilInt(Math.Max(cx0, cx1));
        int yStart = FloorInt(Math.Min(cy0, cy1));
        int yEnd = CeilInt(Math.Max(cy0, cy1));
        yStart = Math.Max(yStart, -1);
        yEnd = Math.Min(yEnd, surface.Height);
        for (int y = yStart; y <= yEnd; y++)
        {
            Guard.Check();
            FillSpan(surface, y, xStart, xEnd, color);
        }
    }

    private static void FillPolygonPixels(RasterSurface surface, List<(double X, double Y)> points, Rgba color)
    {
        if (points.Count < 3)
            return;
        var closed = points[0] != points[^1] ? [.. points, points[0]] : points;
        var edges = new List<(double X1, double Y1, double X2, double Y2)>();
        for (int i = 0; i + 1 < closed.Count; i++)
            edges.Add((closed[i].X, closed[i].Y, closed[i + 1].X, closed[i + 1].Y));
        ScanEdges(edges, surface.Height, evenOdd: false, (y, s, e) =>
            FillSpan(surface, y, Math.Max(0, CeilInt(s)), Math.Min(surface.Width - 1, FloorInt(e)), color), forceNonZero: true);
    }

    /// <summary>Scan converts edges over rows [floor(min y), ceil(max y)] clipped to the height.</summary>
    private static void ScanEdges(List<(double X1, double Y1, double X2, double Y2)> edges, int heightPx, bool evenOdd,
        Action<int, double, double> span, bool forceNonZero = false)
    {
        if (edges.Count == 0)
            return;
        double minYs = double.PositiveInfinity, maxYs = double.NegativeInfinity;
        bool first = true;
        foreach (var (_, y1, _, y2) in edges)
        {
            if (first)
            {
                minYs = y1;
                maxYs = y1;
                first = false;
            }
            else
            {
                minYs = PyMin(minYs, y1);
                maxYs = PyMax(maxYs, y1);
            }
            minYs = PyMin(minYs, y2);
            maxYs = PyMax(maxYs, y2);
        }
        if (!double.IsFinite(minYs) || !double.IsFinite(maxYs))
            throw double.IsNaN(minYs) || double.IsNaN(maxYs) ? Py.ValueError("cannot convert float NaN to integer") : Py.OverflowError("cannot convert float infinity to integer");
        int minY = (int)Math.Max(0, Math.Floor(minYs));
        int maxY = (int)Math.Min(heightPx - 1, Math.Ceiling(maxYs));
        var intersections = new List<(double X, int Direction)>();
        for (int y = minY; y <= maxY; y++)
        {
            Guard.Check();
            double scanY = y + 0.5;
            intersections.Clear();
            foreach (var (x1, y1, x2, y2) in edges)
            {
                if (y1 == y2)
                    continue;
                if (scanY < Math.Min(y1, y2) || scanY >= Math.Max(y1, y2))
                    continue;
                double t = (scanY - y1) / (y2 - y1);
                double x = x1 + t * (x2 - x1);
                intersections.Add((x, y2 > y1 ? 1 : -1));
            }
            SortIntersections(intersections);
            int row = y;
            FillRow(intersections, evenOdd && !forceNonZero, (s, e) => span(row, s, e));
        }
    }

    private static void FillSpan(RasterSurface surface, int y, int xStart, int xEnd, Rgba color)
    {
        if (y < 0 || y >= surface.Height)
            return;
        if (xEnd < xStart)
            return;
        int x0 = Math.Max(0, xStart);
        int x1 = Math.Min(surface.Width - 1, xEnd);
        if (x1 < x0)
            return;
        if (surface.ClipMask is null)
        {
            int row = y * surface.Width * 4;
            var pixels = surface.Pixels;
            for (int px = x0; px <= x1; px++)
            {
                int idx = row + px * 4;
                pixels[idx] = color.R;
                pixels[idx + 1] = color.G;
                pixels[idx + 2] = color.B;
                pixels[idx + 3] = color.A;
            }
            return;
        }
        for (int px = x0; px <= x1; px++)
            surface.SetPixel(px, y, color);
    }

    private static (bool HasCurve, double? MinWidth) AnalyzePageForSupersample(RenderPage page, double scale)
    {
        bool hasCurve = false;
        double? minWidth = null;
        foreach (var command in page.Commands)
        {
            Guard.Check();
            if (command is not PathCommand path)
                continue;
            if (path.Stroke is not null)
            {
                double width = path.Stroke.LineWidth * scale;
                minWidth = minWidth is double mw ? PyMin(mw, width) : width;
            }
            if (path.Path.Segments.Any(s => s.Kind == PathSegmentKind.Curve))
                hasCurve = true;
        }
        return (hasCurve, minWidth);
    }

    private static int ChooseSupersample(bool hasCurve, double? minStrokePx)
    {
        // Supersample only when curves are present and strokes are thin.
        if (!hasCurve || minStrokePx is not double width)
            return 1;
        if (width < 2.0)
            return 3;
        if (width < 6.0)
            return 2;
        return 1;
    }

    private static void FillPathsPattern(RasterSurface surface, List<List<Point>> subpaths, double scale, Func<int, int, Rgba?> sampler, bool evenOdd)
    {
        var edges = new List<(double, double, double, double)>();
        foreach (var subpath in subpaths)
        {
            if (subpath.Count < 3)
                continue;
            var points = ToPixels(EnsureClosed(subpath), scale, surface.Height);
            for (int i = 0; i + 1 < points.Count; i++)
                edges.Add((points[i].X, points[i].Y, points[i + 1].X, points[i + 1].Y));
        }
        ScanEdges(edges, surface.Height, evenOdd, (y, s, e) =>
        {
            int fillStart = CeilInt(s);
            int fillEnd = FloorInt(e);
            for (int px = fillStart; px <= fillEnd; px++)
            {
                if (sampler(px, y) is Rgba color)
                    surface.SetPixel(px, y, color);
            }
        });
    }

    private static byte[] BuildClipMask(IReadOnlyList<PathSegment> segments, double scale, int widthPx, int heightPx, bool evenOdd)
    {
        var subpaths = FlattenPath(segments, scale);
        var edges = new List<(double, double, double, double)>();
        foreach (var subpath in subpaths)
        {
            if (subpath.Count < 3)
                continue;
            var points = ToPixels(EnsureClosed(subpath), scale, heightPx);
            for (int i = 0; i + 1 < points.Count; i++)
                edges.Add((points[i].X, points[i].Y, points[i + 1].X, points[i + 1].Y));
        }
        var mask = new byte[checked(widthPx * heightPx)];
        ScanEdges(edges, heightPx, evenOdd, (y, s, e) =>
        {
            int fillStart = Math.Max(0, CeilInt(s));
            int fillEnd = Math.Min(widthPx - 1, FloorInt(e));
            int row = y * widthPx;
            for (int px = fillStart; px <= fillEnd; px++)
                mask[row + px] = 1;
        });
        return mask;
    }

    private static byte[] IntersectMasks(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
            throw Py.ValueError("clip mask size mismatch");
        var merged = new byte[left.Length];
        for (int i = 0; i < left.Length; i++)
            merged[i] = left[i] != 0 && right[i] != 0 ? (byte)1 : (byte)0;
        return merged;
    }

    private static Func<int, int, Rgba?> PatternSampler(TilingPattern pattern, RenderResources resources, PatternPaint paint, double scale, int heightPx)
    {
        var baseColor = PatternBaseColor(resources, paint);
        double patternScale = MatrixScale(pattern.Matrix);
        double tileScale = scale * patternScale;
        var tileSurface = RenderPatternTile(pattern, resources, tileScale, baseColor);
        var inv = InvertMatrix(pattern.Matrix);
        double xMin = pattern.BBox[0], yMin = pattern.BBox[1], xMax = pattern.BBox[2], yMax = pattern.BBox[3];
        double width = Math.Max(1e-6, xMax - xMin);
        double height = Math.Max(1e-6, yMax - yMin);
        double stepX = Math.Abs(pattern.XStep) > 1e-6 ? pattern.XStep : width;
        double stepY = Math.Abs(pattern.YStep) > 1e-6 ? pattern.YStep : height;
        int tileH = tileSurface.Height;
        return (px, py) =>
        {
            double userX = (px + 0.5) / scale;
            double userY = (heightPx - 1 - py + 0.5) / scale;
            if (inv is Matrix m)
                (userX, userY) = ApplyMatrix(m, userX, userY);
            double cellX = Py.Mod(userX - xMin, stepX) + xMin;
            double cellY = Py.Mod(userY - yMin, stepY) + yMin;
            if (cellX < xMin || cellY < yMin || cellX >= xMax || cellY >= yMax)
                return null;
            double localX = cellX - xMin;
            double localY = cellY - yMin;
            int tx = ClampInt(Py.Int(localX * tileScale));
            int ty = ClampInt(Py.Int((tileH - 1) - localY * tileScale));
            var color = tileSurface.GetPixel(tx, ty);
            if (color.A == 0)
                return null;
            return color;
        };
    }

    private static Rgba PatternBaseColor(RenderResources resources, PatternPaint paint)
    {
        if (paint.BaseSpaceId is null || paint.BaseComponents is null)
            return Rgba.Black;
        if (resources.ColorSpaces.TryGetValue(paint.BaseSpaceId, out var space) && space is DeviceColorSpace device)
        {
            switch (device.Name)
            {
                case "DeviceGray":
                    if (paint.BaseComponents.Length == 0)
                        throw new HostErrorException("IndexError", "tuple index out of range");
                    return PaintToRgba(new Paint("DeviceGray", paint.BaseComponents[0])) ?? Rgba.Black;
                case "DeviceRGB":
                    return PaintToRgba(new Paint("DeviceRGB", paint.BaseComponents)) ?? Rgba.Black;
                case "DeviceCMYK":
                    return PaintToRgba(new Paint("DeviceCMYK", paint.BaseComponents)) ?? Rgba.Black;
            }
        }
        return Rgba.Black;
    }

    private static RasterSurface RenderPatternTile(TilingPattern pattern, RenderResources resources, double scale, Rgba baseColor)
    {
        double xMin = pattern.BBox[0], yMin = pattern.BBox[1], xMax = pattern.BBox[2], yMax = pattern.BBox[3];
        double width = Math.Max(1.0, xMax - xMin);
        double height = Math.Max(1.0, yMax - yMin);
        int tileW = (int)Math.Max(1, Py.Round(width * scale));
        int tileH = (int)Math.Max(1, Py.Round(height * scale));
        var surface = RasterSurface.Create(tileW, tileH, (255, 255, 255, 0));
        IEnumerable<RenderCommand> commands = pattern.Commands;
        if (Math.Abs(xMin) > 1e-6 || Math.Abs(yMin) > 1e-6)
            commands = pattern.Commands.Select(c => TranslateCommand(c, -xMin, -yMin)).ToList();
        foreach (var command in commands)
        {
            Guard.Check();
            if (command is PathCommand path)
                DrawPatternPath(surface, path, scale, baseColor);
            else if (command is TextCommand text)
                DrawPatternText(surface, text, scale, text.Fill, baseColor);
            // Images in patterns are not supported.
        }
        _ = resources;
        return surface;
    }

    private static void DrawPatternPath(RasterSurface surface, PathCommand command, double scale, Rgba baseColor)
    {
        var subpaths = FlattenPath(command.Path.Segments, scale);
        var fill = command.Fill;
        Rgba? fillColor = null;
        if (fill is { Kind: "PatternBase" })
            fillColor = baseColor;
        else if (fill is not null)
            fillColor = PaintToRgba(fill);
        if (fillColor is Rgba fc)
            FillPaths(surface, subpaths, scale, fc, command.FillRule == "evenodd");
        if (command.Stroke is not null)
        {
            var strokePaint = command.StrokePaint ?? command.Fill;
            Rgba? strokeColor = null;
            if (strokePaint is { Kind: "PatternBase" })
                strokeColor = baseColor;
            else if (strokePaint is not null)
                strokeColor = PaintToRgba(strokePaint);
            var color = strokeColor ?? fillColor ?? Rgba.Black;
            int strokeWidth = Math.Max(1, (int)Py.Round(command.Stroke.LineWidth * scale));
            foreach (var subpath in subpaths)
            {
                var points = ToPixels(subpath, scale, surface.Height);
                for (int i = 0; i + 1 < points.Count; i++)
                    DrawLine(surface, points[i], points[i + 1], strokeWidth, color);
            }
        }
    }

    private static void DrawPatternText(RasterSurface surface, TextCommand command, double scale, Paint? fill, Rgba baseColor)
    {
        var color = fill is { Kind: "PatternBase" } ? baseColor : (PaintToRgba(fill) ?? Rgba.Black);
        double originX = command.Matrix.E;
        double originY = command.Matrix.F;
        double fontSize = command.FontSize;
        double advance = fontSize * 0.6;
        int index = 0;
        foreach (string ch in Py.Chars(command.Text))
        {
            if (ch != " ")
                DrawRect(surface, originX + index * advance, originY, fontSize * 0.5, fontSize, scale, color);
            index++;
        }
    }

    private static RenderCommand TranslateCommand(RenderCommand command, double dx, double dy)
    {
        switch (command)
        {
            case PathCommand path:
            {
                var segments = path.Path.Segments.Select(s => new PathSegment(s.Kind, s.Points.Select(p => new Point(p.X + dx, p.Y + dy)).ToArray())).ToList();
                return path with { Path = new RenderPath(segments) };
            }
            case TextCommand text:
            {
                var m = text.Matrix;
                return new TextCommand(text.Text, text.FontRef, text.FontSize, new Matrix(m.A, m.B, m.C, m.D, m.E + dx, m.F + dy), text.Fill, text.FillOpacity);
            }
            case ImageCommand image:
            {
                var m = image.Matrix;
                return image with { Matrix = new Matrix(m.A, m.B, m.C, m.D, m.E + dx, m.F + dy) };
            }
            default:
                return command;
        }
    }

    internal static Matrix? InvertMatrix(Matrix m)
    {
        double det = m.A * m.D - m.B * m.C;
        if (Math.Abs(det) < 1e-12)
            return null;
        double inv = 1.0 / det;
        return new Matrix(m.D * inv, -m.B * inv, -m.C * inv, m.A * inv, (m.C * m.F - m.D * m.E) * inv, (m.B * m.E - m.A * m.F) * inv);
    }

    internal static (double X, double Y) ApplyMatrix(Matrix m, double x, double y) => (m.A * x + m.C * y + m.E, m.B * x + m.D * y + m.F);

    internal static double MatrixScale(Matrix m)
    {
        double scaleX = Py.Hypot(m.A, m.B);
        double scaleY = Py.Hypot(m.C, m.D);
        if (scaleX == 0 && scaleY == 0)
            return 1.0;
        if (scaleX == 0)
            return scaleY;
        if (scaleY == 0)
            return scaleX;
        return (scaleX + scaleY) / 2.0;
    }

    private static RasterSurface Downsample(RasterSurface surface, int factor)
    {
        if (factor <= 1)
            return surface;
        int newWidth = Math.Max(1, surface.Width / factor);
        int newHeight = Math.Max(1, surface.Height / factor);
        var pixels = new byte[newWidth * newHeight * 4];
        int count = factor * factor;
        var src = surface.Pixels;
        for (int y = 0; y < newHeight; y++)
        {
            Guard.Check();
            for (int x = 0; x < newWidth; x++)
            {
                int sumR = 0, sumG = 0, sumB = 0;
                for (int yy = 0; yy < factor; yy++)
                {
                    for (int xx = 0; xx < factor; xx++)
                    {
                        int idx = ((y * factor + yy) * surface.Width + x * factor + xx) * 4;
                        sumR += src[idx];
                        sumG += src[idx + 1];
                        sumB += src[idx + 2];
                    }
                }
                int dst = (y * newWidth + x) * 4;
                pixels[dst] = (byte)(sumR / count);
                pixels[dst + 1] = (byte)(sumG / count);
                pixels[dst + 2] = (byte)(sumB / count);
                pixels[dst + 3] = 255;
            }
        }
        return new RasterSurface(newWidth, newHeight, pixels);
    }

    private static double PyMin(double a, double b) => b < a ? b : a;

    private static double PyMax(double a, double b) => b > a ? b : a;

    private static int FloorInt(double v) => ClampInt(Py.Int(Math.Floor(v)));

    private static int CeilInt(double v) => ClampInt(Py.Int(Math.Ceiling(v)));

    // Coordinates beyond the int range never touch the surface; clamp them so loops stay finite.
    private static int ClampInt(long v) => (int)Math.Clamp(v, -1_000_000_000L, 1_000_000_000L);
}
