// Ported from image/skia_raster_writer.py: rasterizes the first page of a render model with
// SkiaSharp. The original's environment switches (pattern raster mode, stroke antialiasing and a
// text scale override) are not carried over; their defaults are what this writer does.
//
// The original kept its typeface, outline-font and pattern-tile caches on the writer instance and
// keyed pattern tiles by object id, so a reused writer could serve one document's fonts or tiles to
// the next. Here every cache lives for one Write call, which gives the same output as a fresh
// writer and makes a single instance safe to reuse and to share between threads.

using SkiaSharp;
using XRay.PS.Fonts;
using XRay.PS.Imaging;
using XRay.PS.Internal;
using XRay.PS.PostScript;
using XRay.PS.Rendering;

namespace XRay.PS.Skia;

/// <summary>A raster writer that draws the render model with Skia: anti-aliased paths, hinted glyph
/// rasterization, gradient and image shaders. It produces the default raster output of the library
/// X-Ray.PS was ported from.</summary>
/// <remarks>PNG and JPEG are drawn with Skia. TIFF and BMP, and documents whose images Skia cannot
/// take safely (1-pixel images, CCITT data, other than 8-bit samples, full-page soft masks), fall
/// back to the managed <see cref="RenderModelRasterWriter"/>.</remarks>
public sealed class SkiaRasterWriter : IRasterWriter
{
    /// <inheritdoc />
    public byte[] Write(RenderDocument document, ImageSaveOptions options) => Write(document, options, default(CancellationToken));

    /// <inheritdoc />
    public byte[] Write(RenderDocument document, ImageSaveOptions options, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream();
        Write(document, options, ms, cancellationToken);
        return ms.ToArray();
    }

    /// <summary>Renders the first page and writes the encoded image to <paramref name="output"/>.
    /// Skia's encoder output is written from its native buffer without a managed copy.</summary>
    public void Write(RenderDocument document, ImageSaveOptions options, Stream output, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(output);
        if (options.Dpi <= 0)
            throw new ArgumentException("dpi must be positive", nameof(options));
        cancellationToken.ThrowIfCancellationRequested();
        using var guard = Guard.Begin(cancellationToken, options.MaxRasterBytes);
        if (options.Format is not (ImageFormat.Png or ImageFormat.Jpeg) || HasFragileSkiaImages(document))
        {
            new RenderModelRasterWriter().Write(document, options, output, cancellationToken);
            return;
        }

        var page = GetPage(document);
        double scale = options.Dpi / 72.0;
        var (widthPx, heightPx) = PagePixelSize(page.Width, page.Height, scale, options.PreserveFractionalPageSize);
        Guard.Charge((long)widthPx * heightPx * 4, "page raster");

        bool png = options.Format == ImageFormat.Png;
        bool opaque = !png || options.OpaqueBackground;
        var info = new SKImageInfo(widthPx, heightPx, SKColorType.Rgba8888, opaque ? SKAlphaType.Opaque : SKAlphaType.Premul);
        using var surface = SKSurface.Create(info) ?? throw new InvalidOperationException("could not create a Skia surface");
        var canvas = surface.Canvas;
        canvas.Clear(opaque ? SKColors.White : SKColors.Transparent);

        canvas.Save();
        canvas.Scale((float)scale, (float)-scale);
        canvas.Translate(0, (float)-page.Height);

        var fontResolver = options.FontResolver ?? new FontResolver(options.AdditionalFontsFolder);
        var state = new DrawState(document, scale, fontResolver);

        foreach (var command in page.Commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (command)
            {
                case StateSaveCommand:
                    canvas.Save();
                    break;
                case StateRestoreCommand:
                    canvas.Restore();
                    break;
                case ClipCommand clip:
                    using (var path = PathToSkia(clip.Path.Segments, clip.FillRule))
                        canvas.ClipPath(path, SKClipOperation.Intersect, true);
                    break;
                case PathCommand pathCommand:
                    DrawPath(canvas, pathCommand, state);
                    break;
                case TextCommand text:
                    DrawText(canvas, text, state);
                    break;
                case ImageCommand image:
                    DrawImage(canvas, image, document, flipSampleY: true);
                    break;
            }
        }

        canvas.Restore();

        using var snapshot = surface.Snapshot();
        using var encoded = png
            ? snapshot.Encode(SKEncodedImageFormat.Png, 100)
            : snapshot.Encode(SKEncodedImageFormat.Jpeg, options.JpegQuality ?? 80);
        if (encoded is null)
        {
            new RenderModelRasterWriter().Write(document, options, output, cancellationToken);
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (png)
            RasterEncoders.AddPngDpi(encoded.AsSpan(), options.Dpi, output);
        else
            output.Write(encoded.AsSpan());
        output.Flush();
    }

    /// <summary>The per-call drawing context and caches.</summary>
    private sealed class DrawState(RenderDocument document, double scale, FontResolver fontResolver)
    {
        public RenderDocument Document { get; } = document;
        public double Scale { get; } = scale;
        public FontResolver FontResolver { get; } = fontResolver;
        public Dictionary<string, SKTypeface> Typefaces { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, TrueTypeFont?> OutlineFonts { get; } = new(StringComparer.Ordinal);
        public Dictionary<Pattern, Dictionary<(Rgba Color, double Scale), PatternTile>> PatternTiles { get; } =
            new(ReferenceEqualityComparer.Instance);
    }

    private sealed record PatternTile(
        SKImage Image,
        double StepX,
        double StepY,
        double XMin,
        double YMin,
        double Width,
        double Height,
        Matrix? InvMatrix,
        Matrix Matrix,
        int WidthPx,
        int HeightPx);

    // ---- document checks -------------------------------------------------------------------

    internal static bool HasFragileSkiaImages(RenderDocument document)
    {
        // Images Skia cannot take safely go through the managed renderer, as in the original.
        foreach (var image in document.Resources.Images.Values)
        {
            if (image.Width <= 1 || image.Height <= 1)
                return true;
            if (image.Filter == "CCITTFaxDecode")
                return true;
            if (image.Mask)
            {
                if (image.ColorSpace != "DeviceGray" || image.BitsPerComponent is not (1 or 8))
                    return true;
                continue;
            }
            if (image.BitsPerComponent != 8)
                return true;
        }
        foreach (var page in document.Pages)
        {
            foreach (var command in page.Commands)
            {
                if (command is not ImageCommand imageCommand)
                    continue;
                if (!document.Resources.Images.TryGetValue(imageCommand.ImageId, out var image) || image.SoftMask is null)
                    continue;
                if (IsFullPageSoftMaskImage(imageCommand, page))
                    return true;
            }
        }
        return false;
    }

    private static bool IsFullPageSoftMaskImage(ImageCommand command, RenderPage page)
    {
        var m = command.Matrix;
        if (Math.Abs(m.B) > 1e-6 || Math.Abs(m.C) > 1e-6)
            return false;
        if (Math.Abs(m.E) > 1e-6)
            return false;
        if (Math.Abs(m.A - page.Width) > 0.5)
            return false;
        if (Math.Abs(Math.Abs(m.D) - page.Height) > 0.5)
            return false;
        return Math.Abs(m.F - page.Height) <= 0.5;
    }

    private static RenderPage GetPage(RenderDocument document) =>
        document.Pages.Count == 0 ? throw new ArgumentException("document has no pages", nameof(document)) : document.Pages[0];

    // ---- page size -------------------------------------------------------------------------

    /// <summary>The Skia writer's page size rule: integer-point pages round, fractional points
    /// truncate to an integer point first unless fractional sizes are kept (XPS print-ticket
    /// fitting), where near-A4 pages snap to A4 and fractional sizes truncate.</summary>
    internal static (int Width, int Height) PagePixelSize(double widthPt, double heightPt, double scale, bool preserveFractional)
    {
        double pageWidth = preserveFractional ? widthPt : NormalizePagePoints(widthPt);
        double pageHeight = preserveFractional ? heightPt : NormalizePagePoints(heightPt);
        if (!preserveFractional)
            return (Math.Max(1, (int)Py.Round(pageWidth * scale)), Math.Max(1, (int)Py.Round(pageHeight * scale)));
        var snapped = SnapA4ApproxPagePixelSize(pageWidth, pageHeight, scale);
        if (snapped is not null)
            return snapped.Value;
        double widthValue = pageWidth * scale;
        double heightValue = pageHeight * scale;
        bool widthIsInteger = Math.Abs(pageWidth - Py.Round(pageWidth)) < 1e-6;
        bool heightIsInteger = Math.Abs(pageHeight - Py.Round(pageHeight)) < 1e-6;
        double widthRounded = Py.Round(widthValue);
        double heightRounded = Py.Round(heightValue);
        int widthPx = widthIsInteger && Math.Abs(widthValue - widthRounded) < 1e-6
            ? Math.Max(1, (int)widthRounded)
            : Math.Max(1, (int)Py.Int(widthValue - 1e-6));
        int heightPx = heightIsInteger && Math.Abs(heightValue - heightRounded) < 1e-6
            ? Math.Max(1, (int)heightRounded)
            : Math.Max(1, (int)Py.Int(heightValue - 1e-6));
        return (widthPx, heightPx);
    }

    private static double NormalizePagePoints(double value)
    {
        double rounded = Py.Round(value);
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

    // ---- paths -----------------------------------------------------------------------------

    private static void DrawPath(SKCanvas canvas, PathCommand command, DrawState state)
    {
        var document = state.Document;
        using var path = PathToSkia(command.Path.Segments, command.FillRule);
        if (command.Fill is not null)
        {
            if (command.Fill.Kind == "Pattern" && command.Fill.Value is PatternPaint patternPaint)
            {
                document.Resources.Patterns.TryGetValue(patternPaint.PatternId, out var pattern);
                if (pattern is TilingPattern tiling)
                {
                    bool done = false;
                    if (TilingPatternIsImageOnly(tiling))
                    {
                        var tile = GetPatternTile(tiling, patternPaint, state);
                        bool useShader = tiling.TilingType != 0;
                        done = useShader && tile is not null && FillPathPatternWithShader(canvas, path, tile, tiling, command.FillOpacity);
                    }
                    if (!done)
                        WithOpacityLayer(canvas, command.FillOpacity, () => FillPathPattern(canvas, path, patternPaint, state));
                }
                else if (pattern is ShadingPattern shading)
                {
                    WithOpacityLayer(canvas, command.FillOpacity, () => FillPathShading(canvas, path, shading));
                }
            }
            else if (RasterRenderer.PaintToRgba(command.Fill) is Rgba rgba)
            {
                using var paint = new SKPaint
                {
                    IsAntialias = !IsAxisAlignedRectPath(command.Path.Segments),
                    Style = SKPaintStyle.Fill,
                    ColorF = new SKColorF(rgba.R / 255f, rgba.G / 255f, rgba.B / 255f, (float)Math.Clamp(command.FillOpacity, 0.0, 1.0)),
                };
                canvas.DrawPath(path, paint);
            }
        }
        if (command.Stroke is null)
            return;
        const bool strokeAntialias = true;
        var strokePaint = command.StrokePaint ?? command.Fill;
        Pattern? strokePattern = null;
        if (strokePaint is not null && strokePaint.Kind == "Pattern" && strokePaint.Value is PatternPaint sp)
            document.Resources.Patterns.TryGetValue(sp.PatternId, out strokePattern);
        if (strokePattern is TilingPattern strokeTiling)
        {
            var paintValue = (PatternPaint)strokePaint!.Value!;
            var tile = GetPatternTile(strokeTiling, paintValue, state);
            if (tile is not null && TilingPatternIsImageOnly(strokeTiling) &&
                StrokePathPatternWithShader(canvas, path, tile, strokeTiling, command.Stroke, command.StrokeOpacity, strokeAntialias))
                return;
            using var strokePath = StrokeFillPath(path, command.Stroke, strokeAntialias);
            if (strokePath is not null)
                WithOpacityLayer(canvas, command.StrokeOpacity, () => FillPathPattern(canvas, strokePath, paintValue, state));
        }
        else if (strokePattern is ShadingPattern strokeShading)
        {
            using var strokePath = StrokeFillPath(path, command.Stroke, strokeAntialias);
            if (strokePath is not null)
                WithOpacityLayer(canvas, command.StrokeOpacity, () => FillPathShading(canvas, strokePath, strokeShading));
        }
        else
        {
            var color = RasterRenderer.PaintToRgba(strokePaint) ?? Rgba.Black;
            using var paint = new SKPaint
            {
                IsAntialias = strokeAntialias,
                Style = SKPaintStyle.Stroke,
                ColorF = new SKColorF(color.R / 255f, color.G / 255f, color.B / 255f, (float)Math.Clamp(command.StrokeOpacity, 0.0, 1.0)),
                StrokeWidth = (float)command.Stroke.LineWidth,
            };
            ApplyStrokeStyle(paint, command.Stroke);
            canvas.DrawPath(path, paint);
        }
    }

    /// <summary>Runs a draw inside save/restore, in a layer at <paramref name="opacity"/> when it is
    /// below one.</summary>
    private static void WithOpacityLayer(SKCanvas canvas, double opacity, Action draw)
    {
        canvas.Save();
        SKPaint? layerPaint = null;
        if (opacity < 0.9999)
        {
            layerPaint = AlphaPaint(opacity);
            canvas.SaveLayer(layerPaint);
        }
        draw();
        if (layerPaint is not null)
        {
            canvas.Restore();
            layerPaint.Dispose();
        }
        canvas.Restore();
    }

    private static SKPaint AlphaPaint(double alpha) => new() { ColorF = new SKColorF(0, 0, 0, (float)Math.Clamp(alpha, 0.0, 1.0)) };

    private static SKPath? StrokeFillPath(SKPath path, StrokeStyle stroke, bool antialias)
    {
        using var paint = new SKPaint { IsAntialias = antialias, Style = SKPaintStyle.Stroke, StrokeWidth = (float)stroke.LineWidth };
        ApplyStrokeStyle(paint, stroke);
        using var builder = new SKPathBuilder();
        if (!paint.GetFillPath(path, builder))
            return null;
        return builder.Detach();
    }

    private static void ApplyStrokeStyle(SKPaint paint, StrokeStyle stroke)
    {
        paint.StrokeCap = stroke.LineCap switch
        {
            1 => SKStrokeCap.Round,
            2 => SKStrokeCap.Square,
            _ => SKStrokeCap.Butt,
        };
        paint.StrokeJoin = stroke.LineJoin switch
        {
            1 => SKStrokeJoin.Round,
            2 => SKStrokeJoin.Bevel,
            _ => SKStrokeJoin.Miter,
        };
        paint.StrokeMiter = (float)stroke.MiterLimit;
        if (stroke.Dash.Length > 0)
        {
            // Skia rejects an odd or degenerate interval list; the original then drew solid.
            paint.PathEffect = stroke.Dash.Length % 2 == 0
                ? SKPathEffect.CreateDash(stroke.Dash.Select(d => (float)d).ToArray(), (float)stroke.DashPhase)
                : null;
        }
    }

    internal static SKPath PathToSkia(IReadOnlyList<PathSegment> segments, string fillRule = "nonzero")
    {
        using var builder = new SKPathBuilder();
        bool hasCurrent = false;
        foreach (var segment in segments)
        {
            switch (segment.Kind)
            {
                case PathSegmentKind.Move:
                {
                    var pt = segment.Points[0];
                    builder.MoveTo((float)pt.X, (float)pt.Y);
                    hasCurrent = true;
                    break;
                }
                case PathSegmentKind.Line:
                {
                    var pt = segment.Points[0];
                    if (!hasCurrent)
                        builder.MoveTo((float)pt.X, (float)pt.Y);
                    else
                        builder.LineTo((float)pt.X, (float)pt.Y);
                    hasCurrent = true;
                    break;
                }
                case PathSegmentKind.Curve when segment.Points.Length == 3:
                {
                    var (c1, c2, end) = (segment.Points[0], segment.Points[1], segment.Points[2]);
                    builder.CubicTo((float)c1.X, (float)c1.Y, (float)c2.X, (float)c2.Y, (float)end.X, (float)end.Y);
                    hasCurrent = true;
                    break;
                }
                case PathSegmentKind.Close:
                    builder.Close();
                    break;
            }
        }
        if (fillRule == "evenodd")
            builder.FillType = SKPathFillType.EvenOdd;
        return builder.Detach();
    }

    private static bool IsAxisAlignedRectPath(IReadOnlyList<PathSegment> segments)
    {
        if (segments.Count != 5)
            return false;
        if (segments[0].Kind != PathSegmentKind.Move || segments[1].Kind != PathSegmentKind.Line || segments[2].Kind != PathSegmentKind.Line ||
            segments[3].Kind != PathSegmentKind.Line || segments[4].Kind != PathSegmentKind.Close)
            return false;
        for (int i = 0; i < 4; i++)
        {
            if (segments[i].Points.Length != 1)
                return false;
        }
        var (p0, p1, p2, p3) = (segments[0].Points[0], segments[1].Points[0], segments[2].Points[0], segments[3].Points[0]);
        return Math.Abs(p0.Y - p1.Y) < 1e-9 && Math.Abs(p1.X - p2.X) < 1e-9 && Math.Abs(p2.Y - p3.Y) < 1e-9 && Math.Abs(p3.X - p0.X) < 1e-9;
    }

    // ---- text ------------------------------------------------------------------------------

    private static void DrawText(SKCanvas canvas, TextCommand command, DrawState state)
    {
        if (command.Fill is not null && command.Fill.Kind == "Pattern" && command.Fill.Value is PatternPaint patternPaint)
        {
            DrawTextPattern(canvas, command, patternPaint, state);
            return;
        }
        var rgba = command.Fill is not null ? RasterRenderer.PaintToRgba(command.Fill) ?? Rgba.Black : Rgba.Black;
        if (DrawTextWithFont(canvas, command, rgba, state))
            return;
        DrawTextFallback(canvas, command, rgba, state, state.FontResolver);
    }

    private static SKPaint TextPaint(Rgba rgba) => new()
    {
        IsAntialias = true,
        Style = SKPaintStyle.Fill,
        ColorF = new SKColorF(rgba.R / 255f, rgba.G / 255f, rgba.B / 255f, rgba.A / 255f),
    };

    private static double FontSizeOrOne(TextCommand command) => command.FontSize != 0 ? command.FontSize : 1.0;

    private static bool DrawTextFallback(SKCanvas canvas, TextCommand command, Rgba rgba, DrawState state, FontResolver? fontResolver)
    {
        var typeface = SKTypeface.Default;
        if (typeface is null)
            return false;
        using var paint = TextPaint(rgba);
        var (effectiveSize, matrix) = NormalizeTextMatrixForFontSize(command, FontSizeOrOne(command));
        using var font = new SKFont(typeface, (float)effectiveSize);
        ConfigureSkiaTextFont(font);
        canvas.Save();
        Concat(canvas, matrix);
        canvas.Scale(1f, -1f);
        DrawTextWithoutKerning(canvas, command.Text, font, paint, state, command.FontRef, FontSizeOrOne(command), fontResolver,
            pdfTextAdjustments: command.PdfTextAdjustments);
        canvas.Restore();
        return true;
    }

    private static bool DrawTextWithFont(SKCanvas canvas, TextCommand command, Rgba rgba, DrawState state)
    {
        var typeface = ResolveTextTypeface(command.FontRef, state);
        if (typeface is null)
            return false;
        using var paint = TextPaint(rgba);
        var (effectiveSize, matrix) = NormalizeTextMatrixForFontSize(command, FontSizeOrOne(command));
        using var font = new SKFont(typeface, (float)effectiveSize);
        ConfigureSkiaTextFont(font);
        if (DrawExplicitGidTextAsPath(canvas, command, font, rgba, matrix))
            return true;
        canvas.Save();
        Concat(canvas, matrix);
        // The canvas is already flipped for PostScript (y up); flip text space back so glyphs stand upright.
        canvas.Scale(1f, -1f);
        double baselineShift = RasterRenderer.PalatinoBaselineShift(command.FontRef, effectiveSize);
        if (baselineShift != 0)
            canvas.Translate(0f, (float)baselineShift);
        DrawTextWithoutKerning(canvas, command.Text, font, paint, state, command.FontRef, effectiveSize, state.FontResolver,
            pdfTextAdjustments: command.PdfTextAdjustments);
        canvas.Restore();
        return true;
    }

    private static bool DrawExplicitGidTextAsPath(SKCanvas canvas, TextCommand command, SKFont font, Rgba rgba, Matrix matrix)
    {
        var (baseFontRef, glyphIdOverride, glyphOverrides) = SplitFontRef(command.FontRef ?? "");
        if (!baseFontRef.EndsWith(".odttf", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrEmpty(command.Text))
            return false;
        int[] codes = CodePoints(command.Text);
        var glyphs = new List<int>(codes.Length);
        for (int index = 0; index < codes.Length; index++)
        {
            int? gid = null;
            if (index == 0 && glyphIdOverride is not null)
                gid = glyphIdOverride.Value;
            else if (glyphOverrides.Count > 0 && glyphOverrides.TryGetValue(codes[index], out int mapped))
                gid = mapped;
            if (gid is null)
                return false;
            glyphs.Add(Math.Max(0, gid.Value));
        }
        using var builder = new SKPathBuilder();
        double x = 0.0;
        bool empty = true;
        for (int i = 0; i < glyphs.Count; i++)
        {
            using var glyphPath = font.GetGlyphPath(ToGlyphId(glyphs[i]));
            if (glyphPath is not null && !glyphPath.IsEmpty)
            {
                builder.AddPath(glyphPath, (float)x, 0f, SKPathAddMode.Append);
                empty = false;
            }
            x += font.MeasureText(Py.Chr(codes[i]));
        }
        if (empty)
            return false;
        builder.FillType = SKPathFillType.EvenOdd;
        using var path = builder.Detach();
        using var paint = TextPaint(rgba);
        canvas.Save();
        Concat(canvas, matrix);
        canvas.Scale(1f, -1f);
        canvas.DrawPath(path, paint);
        canvas.Restore();
        return true;
    }

    private static void ConfigureSkiaTextFont(SKFont font)
    {
        font.Subpixel = true;
        font.BaselineSnap = false;
        font.LinearMetrics = true;
        // Subpixel edging (grayscale on an RGBA surface) and normal hinting, as the original chose.
        font.Edging = SKFontEdging.SubpixelAntialias;
        font.Hinting = SKFontHinting.Normal;
    }

    private static (double FontSize, Matrix Matrix) NormalizeTextMatrixForFontSize(TextCommand command, double fontSize)
    {
        var m = command.Matrix;
        if (Math.Abs(m.B) > 1.0e-9 || Math.Abs(m.C) > 1.0e-9)
            return (fontSize, m);
        double sx = Math.Abs(m.A);
        double sy = Math.Abs(m.D);
        if (sx <= 1.0e-9 || sy <= 1.0e-9)
            return (fontSize, m);
        if (Math.Abs(sx - sy) > 1.0e-6)
            return (fontSize, m);
        return (fontSize * sx, new Matrix(m.A >= 0 ? 1.0 : -1.0, 0.0, 0.0, m.D >= 0 ? 1.0 : -1.0, m.E, m.F));
    }

    private static readonly HashSet<string> SymbolicFontNames = new(StringComparer.Ordinal) { "Symbol", "ZapfDingbats", "Webdings", "Wingdings" };

    /// <summary>Draws text with explicit advances so the engine applies no kerning, as PostScript
    /// <c>show</c> expects.</summary>
    private static void DrawTextWithoutKerning(SKCanvas canvas, string text, SKFont font, SKPaint paint, DrawState state,
        string? fontRef = null, double? fontSize = null, FontResolver? fontResolver = null, int? glyphIdOverride = null,
        double[]? pdfTextAdjustments = null)
    {
        if (string.IsNullOrEmpty(text))
            return;
        int[] codes = CodePoints(text);
        var (baseFontRef, fontRefGlyphId, glyphOverrides) = SplitFontRef(fontRef ?? "");
        List<double>? advances = null;
        int[] drawCodes = codes;
        List<int>? glyphsFromCodeMap = null;
        int? effectiveGlyphIdOverride = glyphIdOverride ?? fontRefGlyphId;
        if (fontResolver is not null && baseFontRef.Length > 0)
        {
            // Mirrors the original's single try block: a failure anywhere falls back to measured
            // advances, but the glyph mapping and remapped text computed before it are kept.
            try
            {
                FontResource resource;
                try
                {
                    resource = fontResolver.Resolve(fontRef is { Length: > 0 } ? fontRef : baseFontRef);
                }
                catch (Exception)
                {
                    resource = fontResolver.Resolve(baseFontRef);
                }
                var codeMap = resource.CodeMap ?? new Dictionary<int, int>();
                double units = resource.UnitsPerEm != 0 ? resource.UnitsPerEm : 1000;
                var outlineFont = ResolveOutlineFont(fontRef is { Length: > 0 } ? fontRef : baseFontRef, fontResolver, state);
                double resolvedSize = fontSize is double fs && fs != 0 ? fs : 1.0;
                bool symbolicMode = SymbolicFontNames.Contains(baseFontRef) || SymbolicFontNames.Contains(resource.Name);
                if (symbolicMode)
                    drawCodes = RemapSymbolicCodes(codes, font);
                if (effectiveGlyphIdOverride is not null && codes.Length == 1)
                {
                    glyphsFromCodeMap = [effectiveGlyphIdOverride.Value];
                }
                else if (outlineFont is not null)
                {
                    var mapped = new List<int>(codes.Length);
                    for (int idx = 0; idx < codes.Length; idx++)
                    {
                        int gid;
                        if (idx == 0 && effectiveGlyphIdOverride is not null)
                            gid = effectiveGlyphIdOverride.Value;
                        else if (glyphOverrides.TryGetValue(codes[idx], out int overrideGid))
                            gid = overrideGid;
                        else
                            gid = outlineFont.GlyphIdForCode(codes[idx]);
                        mapped.Add(Math.Max(0, gid));
                    }
                    if (mapped.Count > 0 && mapped.Any(g => g != 0))
                        glyphsFromCodeMap = mapped;
                }
                // Explicit code-map glyph ids apply only to symbolic fonts; other text is already
                // Unicode and forcing the code map would pick wrong glyphs for punctuation.
                if (codeMap.Count > 0 && symbolicMode && glyphsFromCodeMap is null)
                {
                    var mapped = new List<int>(codes.Length);
                    for (int i = 0; i < codes.Length; i++)
                    {
                        int source = codes[i], drawn = drawCodes[i];
                        mapped.Add(FirstNonZero(codeMap, source, drawn, source & 0xFF, drawn & 0xFF));
                    }
                    if (mapped.Count > 0 && mapped.Any(g => g != 0))
                        glyphsFromCodeMap = mapped;
                }

                var computed = new List<double>(codes.Length);
                for (int i = 0; i < codes.Length; i++)
                {
                    int sourceCode = codes[i];
                    int? gid = null;
                    if (glyphsFromCodeMap is not null && glyphsFromCodeMap.Count > computed.Count)
                        gid = glyphsFromCodeMap[computed.Count];
                    else if (effectiveGlyphIdOverride is not null && codes.Length == 1)
                        gid = effectiveGlyphIdOverride;
                    else if (glyphOverrides.Count > 0 && glyphOverrides.TryGetValue(sourceCode, out int og))
                        gid = og;
                    double? width = null;
                    if (gid is not null && outlineFont is not null && gid.Value >= 0)
                        width = outlineFont.GlyphAdvance(gid.Value);
                    if (width is null && resource.CodeWidths is not null)
                    {
                        if (resource.CodeWidths.TryGetValue(sourceCode, out double cw))
                            width = cw;
                        else if (sourceCode > 0xFF && resource.CodeWidths.TryGetValue(sourceCode & 0xFF, out double lowWidth))
                            width = lowWidth;
                    }
                    if (width is null)
                    {
                        if (!resource.Encoding.TryGetValue(sourceCode, out string? glyphName) && sourceCode > 0xFF)
                            resource.Encoding.TryGetValue(sourceCode & 0xFF, out glyphName);
                        width = fontResolver.GetGlyphWidth(resource, string.IsNullOrEmpty(glyphName) ? ".notdef" : glyphName);
                    }
                    if (width == 0.0 && outlineFont is not null)
                    {
                        gid ??= outlineFont.GlyphIdForCode(sourceCode);
                        if (gid.Value >= 0)
                            width = outlineFont.GlyphAdvance(gid.Value);
                    }
                    if (width == 0.0)
                    {
                        double measured = font.MeasureText(Py.Chr(drawCodes[i]));
                        if (measured > 0.0)
                        {
                            computed.Add(measured);
                            continue;
                        }
                        width = units * 0.5;
                    }
                    computed.Add(width!.Value / units * resolvedSize);
                }
                advances = computed;
            }
            catch (Exception)
            {
                advances = null;
            }
        }
        advances ??= codes.Select(c => (double)font.MeasureText(Py.Chr(c))).ToList();
        if (pdfTextAdjustments is { Length: > 0 })
        {
            double resolvedSize = fontSize is double fs && fs != 0 ? fs : 1.0;
            for (int index = 0; index < pdfTextAdjustments.Length && index < advances.Count; index++)
                advances[index] -= pdfTextAdjustments[index] / 1000.0 * resolvedSize;
        }

        // One draw call keeps it fast while preserving PostScript spacing.
        ushort[] glyphs = glyphsFromCodeMap is { Count: > 0 }
            ? glyphsFromCodeMap.Select(ToGlyphId).ToArray()
            : font.GetGlyphs(drawCodes);
        if (baseFontRef == "Webdings" && glyphs.Length == codes.Length && glyphs.Any(g => g == 0))
        {
            // Webdings often arrives as private-use code points; remap the bullet only when the
            // initial conversion left glyphs missing.
            int[] remapped = drawCodes.Select(c => c == 0xF0B7 ? 0x2022 : c).ToArray();
            if (!remapped.AsSpan().SequenceEqual(drawCodes))
            {
                ushort[] remappedGlyphs = font.GetGlyphs(remapped);
                if (remappedGlyphs.Any(g => g != 0))
                    glyphs = remappedGlyphs;
            }
        }
        if (glyphs.Length == advances.Count)
        {
            var positions = new float[advances.Count];
            double x = 0.0;
            for (int i = 0; i < advances.Count; i++)
            {
                positions[i] = (float)x;
                x += advances[i];
            }
            using var builder = new SKTextBlobBuilder();
            builder.AddHorizontalRun(glyphs, font, positions, 0f);
            using var blob = builder.Build();
            if (blob is not null)
            {
                canvas.DrawText(blob, 0f, 0f, paint);
                return;
            }
        }
        // Per-character fallback when a single run cannot be built.
        double pen = 0.0;
        for (int index = 0; index < drawCodes.Length; index++)
        {
            canvas.DrawText(Py.Chr(drawCodes[index]), (float)pen, 0f, SKTextAlign.Left, font, paint);
            pen += advances[index];
        }
    }

    /// <summary>Symbolic fonts may carry their glyphs at U+F0xx while PostScript strings hold 8-bit
    /// codes, or the reverse; remap the text the way that resolves in this typeface.</summary>
    private static int[] RemapSymbolicCodes(int[] codes, SKFont font)
    {
        int? sampleCode = null, samplePua = null;
        foreach (int c in codes)
        {
            if (sampleCode is null && c is >= 32 and <= 0xFF)
                sampleCode = c;
            if (samplePua is null && c is >= 0xF000 and <= 0xF1FF)
                samplePua = c;
        }
        bool usePuaUp = false, usePuaDown = false;
        if (sampleCode is int sc)
        {
            ushort raw = font.GetGlyph(sc);
            ushort pua = font.GetGlyph(0xF000 + sc);
            usePuaUp = pua != 0 && raw == 0;
        }
        if (samplePua is int sp)
        {
            ushort raw = font.GetGlyph(sp & 0xFF);
            ushort pua = font.GetGlyph(sp);
            usePuaDown = raw != 0 && pua == 0;
        }
        if (usePuaUp)
            return codes.Select(c => c is >= 0 and <= 0xFF ? 0xF000 + c : c).ToArray();
        if (usePuaDown)
            return codes.Select(c => c is >= 0xF000 and <= 0xF1FF ? c & 0xFF : c).ToArray();
        return codes;
    }

    private static int FirstNonZero(IReadOnlyDictionary<int, int> map, params int[] keys)
    {
        foreach (int key in keys)
        {
            if (map.TryGetValue(key, out int value) && value != 0)
                return value;
        }
        return 0;
    }

    private static ushort ToGlyphId(int gid) => (ushort)Math.Clamp(gid, 0, ushort.MaxValue);

    /// <summary>The text's code points (a lone surrogate stays one code point, as in a Python string).</summary>
    internal static int[] CodePoints(string text)
    {
        var result = new List<int>(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char ch = text[i];
            if (char.IsHighSurrogate(ch) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                result.Add(char.ConvertToUtf32(ch, text[i + 1]));
                i++;
            }
            else
            {
                result.Add(ch);
            }
        }
        return [.. result];
    }

    // ---- fonts -----------------------------------------------------------------------------

    private static SKTypeface? ResolveTypeface(FontLocation location, DrawState state)
    {
        string key = (location.Embedded ? "bundled:" : "file:") + location.Path;
        if (state.Typefaces.TryGetValue(key, out var cached))
            return cached;
        SKTypeface? typeface;
        if (location.Embedded)
        {
            using var data = SKData.CreateCopy(location.ReadAllBytes());
            typeface = SKTypeface.FromData(data, 0);
        }
        else
        {
            typeface = SKTypeface.FromFile(location.Path, 0);
        }
        if (typeface is null)
            return null;
        state.Typefaces[key] = typeface;
        return typeface;
    }

    private static SKTypeface? ResolveTextTypeface(string fontRef, DrawState state)
    {
        var resolver = state.FontResolver;
        var embedded = ResolveEmbeddedTypeface(fontRef, state);
        if (embedded is not null)
            return embedded;

        var (baseFontRef, _, _) = SplitFontRef(fontRef);
        var candidates = new List<string> { baseFontRef };
        if (TextOperators.StandardFonts.Contains(baseFontRef))
            candidates.Add(StandardFontFallback(baseFontRef));
        try
        {
            var resolved = resolver.Resolve(baseFontRef);
            candidates.Add(resolved.Name);
            if (TextOperators.StandardFonts.Contains(resolved.Name))
                candidates.Add(StandardFontFallback(resolved.Name));
        }
        catch (Exception)
        {
            // An unresolvable name keeps the candidates found so far.
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate) || !seen.Add(candidate))
                continue;
            var path = resolver.ResolveTtfPath(candidate);
            if (path is null)
                continue;
            var typeface = ResolveTypeface(path, state);
            if (typeface is not null)
                return typeface;
        }
        foreach (string candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate))
                continue;
            var typeface = SKTypeface.FromFamilyName(candidate);
            if (typeface is not null)
                return typeface;
        }
        return SKTypeface.Default;
    }

    internal static (string Base, int? GlyphId, Dictionary<int, int> GlyphMap) SplitFontRef(string fontRef)
    {
        string baseRef = fontRef;
        var glyphMap = new Dictionary<int, int>();
        int gmap = baseRef.IndexOf("#gmap=", StringComparison.Ordinal);
        if (gmap >= 0)
        {
            string suffix = baseRef[(gmap + 6)..];
            baseRef = baseRef[..gmap];
            foreach (string item in suffix.Split(','))
            {
                int colon = item.IndexOf(':', StringComparison.Ordinal);
                if (colon < 0)
                    continue;
                if (Py.TryInt(item[..colon], 16, out long code) && Py.TryInt(item[(colon + 1)..], 10, out long gid) &&
                    code is >= int.MinValue and <= int.MaxValue && gid is >= int.MinValue and <= int.MaxValue)
                    glyphMap[(int)code] = (int)gid;
            }
        }
        int? glyphId = null;
        int gidIndex = baseRef.IndexOf("#gid=", StringComparison.Ordinal);
        if (gidIndex >= 0)
        {
            string suffix = baseRef[(gidIndex + 5)..];
            baseRef = baseRef[..gidIndex];
            if (Py.TryInt(suffix, 10, out long value) && value is >= int.MinValue and <= int.MaxValue)
                glyphId = (int)value;
        }
        return (baseRef, glyphId, glyphMap);
    }

    /// <summary>The font program behind a reference: the resolved font's own program, else a
    /// registered Type 42/XPS font. <paramref name="emptyStopsLookup"/> follows the original's two
    /// call sites, one of which kept an empty program instead of looking further.</summary>
    private static byte[]? EmbeddedFontData(string fontRef, FontResolver resolver, bool emptyStopsLookup, out FontResource? resource)
    {
        var (baseFontRef, _, _) = SplitFontRef(fontRef);
        resource = null;
        foreach (string candidate in new[] { fontRef, baseFontRef })
        {
            try
            {
                resource = resolver.Resolve(candidate);
            }
            catch (Exception)
            {
                resource = null;
            }
            if (resource?.FontProgram is { Length: > 0 })
                break;
        }
        byte[]? data = resource?.FontProgram;
        bool lookFurther = emptyStopsLookup ? data is null : data is not { Length: > 0 };
        if (lookFurther)
        {
            foreach (string candidate in new[] { fontRef, baseFontRef })
            {
                var embedded = resolver.GetEmbeddedType42(candidate);
                if (embedded is not null)
                {
                    data = embedded.Data;
                    break;
                }
            }
        }
        return data;
    }

    private static SKTypeface? ResolveEmbeddedTypeface(string fontRef, DrawState state)
    {
        byte[]? data = EmbeddedFontData(fontRef, state.FontResolver, emptyStopsLookup: true, out _);
        if (data is not { Length: > 0 })
            return null;
        string key = "embedded:" + fontRef;
        if (state.Typefaces.TryGetValue(key, out var cached))
            return cached;
        using var skData = SKData.CreateCopy(data);
        var typeface = SKTypeface.FromData(skData, 0);
        if (typeface is null)
            return null;
        state.Typefaces[key] = typeface;
        return typeface;
    }

    private static TrueTypeFont? ResolveOutlineFont(string fontRef, FontResolver resolver, DrawState state)
    {
        byte[]? embeddedData = EmbeddedFontData(fontRef, resolver, emptyStopsLookup: false, out var resource);
        if (embeddedData is { Length: > 0 })
        {
            string key = "embedded-ttf:" + fontRef;
            if (state.OutlineFonts.TryGetValue(key, out var cachedEmbedded))
                return cachedEmbedded;
            TrueTypeFont? font;
            try
            {
                font = new TrueTypeFont(embeddedData);
            }
            catch (Exception)
            {
                font = null;
            }
            state.OutlineFonts[key] = font;
            return font;
        }
        var (baseFontRef, _, _) = SplitFontRef(fontRef);
        var path = resolver.ResolveTtfPath(baseFontRef);
        if (path is null && resource is not null)
        {
            if (resource.Name == "Symbol")
                path = FontResolver.ResolveSymbolPath();
            else if (resource.Name is "ZapfDingbats" or "Wingdings" or "Webdings")
                path = resolver.ResolveZapfPath();
        }
        if (path is null)
            return null;
        string pathKey = "ttf:" + (path.Embedded ? "bundled:" : "") + path.Path;
        if (state.OutlineFonts.TryGetValue(pathKey, out var cached))
            return cached;
        try
        {
            var font = TrueTypeFont.Load(path);
            state.OutlineFonts[pathKey] = font;
            return font;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string StandardFontFallback(string name)
    {
        string lower = name.ToLowerInvariant();
        bool isBold = lower.Contains("bold", StringComparison.Ordinal);
        bool isItalic = lower.Contains("italic", StringComparison.Ordinal) || lower.Contains("oblique", StringComparison.Ordinal);
        if (name == "ZapfDingbats")
            return "DjVuDingbats";
        string baseName;
        if (name.StartsWith("Courier", StringComparison.Ordinal))
            baseName = "CourierNew";
        else if (name.StartsWith("Helvetica", StringComparison.Ordinal))
            baseName = "Helvetica";
        else if (name.StartsWith("Times", StringComparison.Ordinal))
            baseName = "TimesNewRoman";
        else
            return name;
        string suffix = isBold && isItalic ? "-BoldItalic" : isBold ? "-Bold" : isItalic ? "-Italic" : "";
        return baseName + suffix;
    }

    // ---- images ----------------------------------------------------------------------------

    private static void DrawImagePlaceholder(SKCanvas canvas, ImageCommand command)
    {
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, ColorF = new SKColorF(220 / 255f, 220 / 255f, 220 / 255f, 1f) };
        canvas.DrawRect(SKRect.Create((float)command.Matrix.E, (float)command.Matrix.F, command.Width, command.Height), paint);
    }

    private static void DrawImage(SKCanvas canvas, ImageCommand command, RenderDocument document, bool flipSampleY = false)
    {
        Guard.Check();
        if (!document.Resources.Images.TryGetValue(command.ImageId, out var resource))
        {
            DrawImagePlaceholder(canvas, command);
            return;
        }
        bool isMaskImage = resource.Mask || command.Mask;
        SKImage? image;
        int imageWidth, imageHeight;
        try
        {
            var (data, width, height, colorSpace, bitsPerComponent) = MaterializeImageResource(resource);
            imageWidth = width;
            imageHeight = height;
            if (isMaskImage)
            {
                var maskColor = RasterRenderer.PaintToRgba(command.MaskPaint) ?? Rgba.Black;
                image = MaskImageFromResourceData(data, width, height, bitsPerComponent, resource.MaskPolarity, maskColor, resource.Decode);
            }
            else
            {
                image = ImageFromResourceData(data, width, height, colorSpace, bitsPerComponent, resource.SoftMask, resource.Decode);
            }
        }
        catch (Exception)
        {
            image = null;
            imageWidth = imageHeight = 0;
        }
        if (image is null)
        {
            DrawImagePlaceholder(canvas, command);
            return;
        }
        using (image)
        {
            canvas.Save();
            Concat(canvas, command.Matrix);
            using var paint = new SKPaint { IsAntialias = !isMaskImage };
            var sampling = new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None);
            if (flipSampleY)
            {
                // Image sample space is y up in PostScript and y down in Skia.
                canvas.Translate(0f, 1f);
                canvas.Scale(1f, -1f);
            }
            bool useScaledDrawImage = !isMaskImage && (resource.SoftMask is not null || (long)imageWidth * imageHeight >= 3_000_000);
            if (useScaledDrawImage)
            {
                // Draw in pixel space and normalize into the unit square, as the original did for
                // soft-masked and very large images.
                canvas.Scale(1f / Math.Max(imageWidth, 1), 1f / Math.Max(imageHeight, 1));
                canvas.DrawImage(image, 0f, 0f, sampling, paint);
            }
            else
            {
                canvas.DrawImage(image, SKRect.Create(0, 0, imageWidth, imageHeight), SKRect.Create(0, 0, 1, 1), sampling, paint);
            }
            canvas.Restore();
        }
    }

    private static (byte[] Data, int Width, int Height, string ColorSpace, int BitsPerComponent) MaterializeImageResource(RenderImageResource resource)
    {
        if (resource.Filter != "DCTDecode")
            return (resource.Data, resource.Width, resource.Height, resource.ColorSpace, resource.BitsPerComponent);
        var decoded = JpegDecoder.DecodeToRgb(resource.Data);
        if (decoded is null)
            return (resource.Data, resource.Width, resource.Height, resource.ColorSpace, resource.BitsPerComponent);
        var (rgb, width, height) = decoded.Value;
        return (rgb, width, height, "DeviceRGB", 8);
    }

    private static SKImage? ImageFromResourceData(byte[] data, int width, int height, string colorSpace, int bitsPerComponent,
        byte[]? softMask, double[]? decode)
    {
        if (width <= 0 || height <= 0)
            return null;
        long total = (long)width * height;
        byte[] rgba;
        if (colorSpace == "DeviceRGB")
        {
            if (bitsPerComponent != 8 || data.Length < total * 3)
                return null;
            rgba = new byte[checked(total * 4)];
            for (long i = 0, src = 0, dst = 0; i < total; i++, src += 3, dst += 4)
            {
                rgba[dst] = data[src];
                rgba[dst + 1] = data[src + 1];
                rgba[dst + 2] = data[src + 2];
                rgba[dst + 3] = 255;
            }
        }
        else if (colorSpace == "DeviceGray")
        {
            if (bitsPerComponent == 8)
            {
                if (data.Length < total)
                    return null;
                rgba = new byte[checked(total * 4)];
                for (long i = 0; i < total; i++)
                {
                    byte gray = data[i];
                    rgba[i * 4] = gray;
                    rgba[i * 4 + 1] = gray;
                    rgba[i * 4 + 2] = gray;
                    rgba[i * 4 + 3] = 255;
                }
            }
            else if (bitsPerComponent == 1)
            {
                int row = (width + 7) / 8;
                if (data.Length < (long)row * height)
                    return null;
                rgba = new byte[checked(total * 4)];
                long dst = 0;
                for (int y = 0; y < height; y++)
                {
                    long rowOff = (long)y * row;
                    for (int x = 0; x < width; x++)
                    {
                        int bit = (data[rowOff + x / 8] >> (7 - x % 8)) & 1;
                        int value = bit != 0 ? 255 : 0;
                        if (decode is { Length: >= 2 })
                            value = (int)Math.Clamp(Py.Round((decode[0] + bit * (decode[1] - decode[0])) * 255.0), 0, 255);
                        rgba[dst] = (byte)value;
                        rgba[dst + 1] = (byte)value;
                        rgba[dst + 2] = (byte)value;
                        rgba[dst + 3] = 255;
                        dst += 4;
                    }
                }
            }
            else
            {
                return null;
            }
        }
        else
        {
            return null;
        }
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, softMask is not null ? SKAlphaType.Premul : SKAlphaType.Opaque);
        if (softMask is not null && softMask.Length >= total)
            ApplySoftMaskToRgba(rgba, softMask, total);
        return SKImage.FromPixelCopy(info, rgba, width * 4);
    }

    private static SKImage? MaskImageFromResourceData(byte[] data, int width, int height, int bitsPerComponent, bool maskPolarity, Rgba maskColor,
        double[]? decode)
    {
        if (width <= 0 || height <= 0)
            return null;
        if (bitsPerComponent is not (1 or 8))
            return null;
        long total = (long)width * height;
        int row = (width + 7) / 8;
        if (bitsPerComponent == 1 ? data.Length < (long)row * height : data.Length < total)
            return null;
        var rgba = new byte[checked(total * 4)];
        long dst = 0;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double sample = bitsPerComponent == 1
                    ? (data[(long)y * row + x / 8] >> (7 - x % 8)) & 1
                    : data[(long)y * width + x] / 255.0;
                if (decode is { Length: >= 2 })
                    sample = decode[0] + (decode[1] - decode[0]) * sample;
                bool painted = sample >= 0.5;
                if (!maskPolarity)
                    painted = !painted;
                int alpha = painted ? maskColor.A : 0;
                int pr, pg, pb;
                if (alpha >= 255)
                    (pr, pg, pb) = (maskColor.R, maskColor.G, maskColor.B);
                else if (alpha <= 0)
                    pr = pg = pb = 0;
                else
                    (pr, pg, pb) = ((maskColor.R * alpha + 127) / 255, (maskColor.G * alpha + 127) / 255, (maskColor.B * alpha + 127) / 255);
                rgba[dst] = (byte)pr;
                rgba[dst + 1] = (byte)pg;
                rgba[dst + 2] = (byte)pb;
                rgba[dst + 3] = (byte)alpha;
                dst += 4;
            }
        }
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        return SKImage.FromPixelCopy(info, rgba, width * 4);
    }

    internal static void ApplySoftMaskToRgba(byte[] rgba, byte[] softMask, long total)
    {
        for (long idx = 0, dst = 0; idx < total; idx++, dst += 4)
        {
            int alpha = softMask[idx];
            if (alpha <= 0)
            {
                rgba[dst] = rgba[dst + 1] = rgba[dst + 2] = rgba[dst + 3] = 0;
            }
            else if (alpha < 255)
            {
                rgba[dst] = (byte)((rgba[dst] * alpha + 127) / 255);
                rgba[dst + 1] = (byte)((rgba[dst + 1] * alpha + 127) / 255);
                rgba[dst + 2] = (byte)((rgba[dst + 2] * alpha + 127) / 255);
                rgba[dst + 3] = (byte)alpha;
            }
        }
    }

    // ---- tiling patterns -------------------------------------------------------------------

    private static void FillPathPattern(SKCanvas canvas, SKPath path, PatternPaint paint, DrawState state)
    {
        if (!state.Document.Resources.Patterns.TryGetValue(paint.PatternId, out var p) || p is not TilingPattern pattern)
            return;
        var tile = GetPatternTile(pattern, paint, state);
        if (tile is null)
            return;
        bool nonTiling = pattern.TilingType == 0;
        if (TilingPatternIsImageOnly(pattern) && !nonTiling && FillPathPatternWithShader(canvas, path, tile, pattern, 1.0))
            return;
        var bounds = path.Bounds;
        if (bounds.IsEmpty)
            return;
        double minX = bounds.Left, minY = bounds.Top, maxX = bounds.Right, maxY = bounds.Bottom;
        if (tile.InvMatrix is Matrix inv)
        {
            var corners = new[]
            {
                RasterRenderer.ApplyMatrix(inv, minX, minY),
                RasterRenderer.ApplyMatrix(inv, minX, maxY),
                RasterRenderer.ApplyMatrix(inv, maxX, minY),
                RasterRenderer.ApplyMatrix(inv, maxX, maxY),
            };
            minX = corners.Min(c => c.X);
            minY = corners.Min(c => c.Y);
            maxX = corners.Max(c => c.X);
            maxY = corners.Max(c => c.Y);
        }
        double stepX = tile.StepX, stepY = tile.StepY, tileW = tile.Width, tileH = tile.Height;
        if (stepX <= 1e-6 || stepY <= 1e-6)
            return;
        double startX = 0, endX = 0, startY = 0, endY = 0;
        if (!nonTiling)
        {
            startX = Math.Floor((minX - tile.XMin) / stepX) - 1;
            endX = Math.Ceiling((maxX - tile.XMin) / stepX) + 1;
            startY = Math.Floor((minY - tile.YMin) / stepY) - 1;
            endY = Math.Ceiling((maxY - tile.YMin) / stepY) + 1;
            if (!double.IsFinite(startX) || !double.IsFinite(endX) || !double.IsFinite(startY) || !double.IsFinite(endY))
                return;
            double tileCount = (endX - startX + 1) * (endY - startY + 1);
            if (tileCount > 4096)
            {
                // Pathological brush transforms: draw the one tile under the path's center.
                double centerX = (minX + maxX) * 0.5;
                double centerY = (minY + maxY) * 0.5;
                startX = endX = Py.Round((centerX - tile.XMin) / stepX);
                startY = endY = Py.Round((centerY - tile.YMin) / stepY);
            }
        }

        canvas.Save();
        canvas.ClipPath(path, SKClipOperation.Intersect, true);
        canvas.Save();
        Concat(canvas, tile.Matrix);
        // Linear filtering smooths patterned glyphs; image tiles stay nearest.
        var sampling = TilingPatternIsImageOnly(pattern)
            ? new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None)
            : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        for (double ix = startX; ix <= endX; ix++)
        {
            for (double iy = startY; iy <= endY; iy++)
            {
                double x0 = tile.XMin + ix * stepX;
                double y0 = tile.YMin + iy * stepY;
                canvas.Save();
                if (nonTiling)
                {
                    canvas.Translate((float)x0, (float)y0);
                    canvas.Scale((float)(tileW / Math.Max(tile.WidthPx, 1.0)), (float)(tileH / Math.Max(tile.HeightPx, 1.0)));
                    canvas.DrawImage(tile.Image, 0f, 0f, sampling, null);
                }
                else
                {
                    canvas.Translate((float)x0, (float)(y0 + tileH));
                    canvas.Scale(1f, -1f);
                    canvas.DrawImage(tile.Image, SKRect.Create(0, 0, (float)tileW, (float)tileH), sampling, null);
                }
                canvas.Restore();
            }
        }
        canvas.Restore();
        canvas.Restore();
    }

    /// <summary>The image-to-user transform of a tile shader, or null when it does not invert.</summary>
    private static Matrix? TileShaderImageToUser(PatternTile tile, bool nonTiling)
    {
        double sx = tile.WidthPx / Math.Max(tile.Width, 1e-6);
        double sy = tile.HeightPx / Math.Max(tile.Height, 1e-6);
        // A non-tiling image brush keeps the source orientation (no extra y flip).
        var patternToImage = nonTiling
            ? new Matrix(sx, 0.0, 0.0, sy, -tile.XMin * sx, -tile.YMin * sy)
            : new Matrix(sx, 0.0, 0.0, -sy, -tile.XMin * sx, (tile.YMin + tile.Height) * sy);
        var userToPattern = tile.InvMatrix ?? Matrix.Identity;
        var userToImage = Matrix.Multiply(patternToImage, userToPattern);
        return RasterRenderer.InvertMatrix(userToImage);
    }

    private static SKPath TileClipPath(PatternTile tile)
    {
        var p0 = RasterRenderer.ApplyMatrix(tile.Matrix, tile.XMin, tile.YMin);
        var p1 = RasterRenderer.ApplyMatrix(tile.Matrix, tile.XMin + tile.Width, tile.YMin);
        var p2 = RasterRenderer.ApplyMatrix(tile.Matrix, tile.XMin + tile.Width, tile.YMin + tile.Height);
        var p3 = RasterRenderer.ApplyMatrix(tile.Matrix, tile.XMin, tile.YMin + tile.Height);
        using var builder = new SKPathBuilder();
        builder.MoveTo((float)p0.X, (float)p0.Y);
        builder.LineTo((float)p1.X, (float)p1.Y);
        builder.LineTo((float)p2.X, (float)p2.Y);
        builder.LineTo((float)p3.X, (float)p3.Y);
        builder.Close();
        return builder.Detach();
    }

    private static bool DrawWithTileShader(SKCanvas canvas, SKPath path, PatternTile tile, TilingPattern pattern, SKPaint paint)
    {
        bool nonTiling = pattern.TilingType == 0;
        if (TileShaderImageToUser(tile, nonTiling) is not Matrix imageToUser)
            return false;
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        using var shader = tile.Image.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, sampling, ToSkMatrix(imageToUser));
        if (shader is null)
            return false;
        paint.Shader = shader;
        if (nonTiling)
        {
            // Sample only the first tile's bounds.
            using var clip = TileClipPath(tile);
            canvas.Save();
            canvas.ClipPath(clip, SKClipOperation.Intersect, true);
            canvas.DrawPath(path, paint);
            canvas.Restore();
            return true;
        }
        canvas.DrawPath(path, paint);
        return true;
    }

    private static bool FillPathPatternWithShader(SKCanvas canvas, SKPath path, PatternTile tile, TilingPattern pattern, double opacity)
    {
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, ColorF = new SKColorF(0, 0, 0, (float)Math.Clamp(opacity, 0.0, 1.0)) };
        return DrawWithTileShader(canvas, path, tile, pattern, paint);
    }

    private static bool StrokePathPatternWithShader(SKCanvas canvas, SKPath path, PatternTile tile, TilingPattern pattern, StrokeStyle stroke,
        double strokeOpacity, bool antialias)
    {
        using var paint = new SKPaint
        {
            IsAntialias = antialias,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = (float)stroke.LineWidth,
            ColorF = new SKColorF(0, 0, 0, (float)Math.Clamp(strokeOpacity, 0.0, 1.0)),
        };
        ApplyStrokeStyle(paint, stroke);
        return DrawWithTileShader(canvas, path, tile, pattern, paint);
    }

    private static void DrawTextPattern(SKCanvas canvas, TextCommand command, PatternPaint paint, DrawState state)
    {
        state.Document.Resources.Patterns.TryGetValue(paint.PatternId, out var pattern);
        if (pattern is ShadingPattern shading && DrawTextShading(canvas, command, shading, state))
            return;
        if (pattern is TilingPattern tiling && TilingPatternUsesSoftMaskImage(tiling, state.Document))
        {
            // Masked image tiles fill the glyph outlines geometrically; plain image tiles keep the
            // shader path so their phase matches.
            using var textPath = BuildTextPathForPattern(command, state);
            if (textPath is not null)
            {
                FillPathPattern(canvas, textPath, paint, state);
                return;
            }
        }
        if (DrawTextPatternWithShader(canvas, command, paint, state))
            return;
        if (pattern is TilingPattern)
        {
            using var textPath = BuildTextPathForPattern(command, state);
            if (textPath is not null)
            {
                FillPathPattern(canvas, textPath, paint, state);
                return;
            }
        }
        DrawTextFallback(canvas, command, PatternBaseColor(state.Document, paint), state, null);
    }

    private static bool DrawTextShading(SKCanvas canvas, TextCommand command, ShadingPattern pattern, DrawState state)
    {
        var textToUser = TextLocalToUserMatrix(command);
        var userToText = RasterRenderer.InvertMatrix(textToUser);
        using var shader = BuildShadingShader(pattern, userToText);
        if (shader is null)
            return false;
        var typeface = ResolveTextTypeface(command.FontRef, state);
        if (typeface is null)
            return false;
        double fontSize = FontSizeOrOne(command);
        using var font = new SKFont(typeface, (float)fontSize);
        ConfigureSkiaTextFont(font);
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Shader = shader };
        canvas.Save();
        Concat(canvas, textToUser);
        DrawTextWithoutKerning(canvas, command.Text, font, paint, state, command.FontRef, fontSize, state.FontResolver);
        canvas.Restore();
        return true;
    }

    private static void FillPathShading(SKCanvas canvas, SKPath path, ShadingPattern pattern)
    {
        using var shader = BuildShadingShader(pattern);
        if (shader is null)
            return;
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Shader = shader };
        canvas.DrawPath(path, paint);
    }

    private static SKShader? BuildShadingShader(ShadingPattern pattern, Matrix? userToGeom = null)
    {
        var stops = SampleShadingStops(pattern);
        if (stops.Count == 0)
            return null;
        float[] positions = stops.Select(s => (float)s.T).ToArray();
        SKColor[] colors = stops.Select(s => s.Color).ToArray();
        var m = pattern.Matrix;
        switch (pattern.Shading)
        {
            case AxialShading axial when axial.Coords.Length == 4:
            {
                var p0 = RasterRenderer.ApplyMatrix(m, axial.Coords[0], axial.Coords[1]);
                var p1 = RasterRenderer.ApplyMatrix(m, axial.Coords[2], axial.Coords[3]);
                if (userToGeom is Matrix g)
                {
                    p0 = RasterRenderer.ApplyMatrix(g, p0.X, p0.Y);
                    p1 = RasterRenderer.ApplyMatrix(g, p1.X, p1.Y);
                }
                return SKShader.CreateLinearGradient(ToPoint(p0), ToPoint(p1), colors, positions, SKShaderTileMode.Clamp);
            }
            case RadialShading radial when radial.Coords.Length == 6:
            {
                double[] c = radial.Coords;
                var c0 = RasterRenderer.ApplyMatrix(m, c[0], c[1]);
                var c1 = RasterRenderer.ApplyMatrix(m, c[3], c[4]);
                double sx = Py.Hypot(m.A, m.B);
                double sy = Py.Hypot(m.C, m.D);
                double scale = Math.Max(1e-6, (sx + sy) * 0.5);
                double rr0 = Math.Max(0.0, c[2] * scale);
                double rr1 = Math.Max(0.0, c[5] * scale);
                if (userToGeom is Matrix g)
                {
                    // Approximate the radii in local space by mapping a point on each circle.
                    var c0Local = RasterRenderer.ApplyMatrix(g, c0.X, c0.Y);
                    var c1Local = RasterRenderer.ApplyMatrix(g, c1.X, c1.Y);
                    var p0Edge = RasterRenderer.ApplyMatrix(g, c0.X + rr0, c0.Y);
                    var p1Edge = RasterRenderer.ApplyMatrix(g, c1.X + rr1, c1.Y);
                    rr0 = Py.Hypot(p0Edge.X - c0Local.X, p0Edge.Y - c0Local.Y);
                    rr1 = Py.Hypot(p1Edge.X - c1Local.X, p1Edge.Y - c1Local.Y);
                    c0 = c0Local;
                    c1 = c1Local;
                }
                if (Math.Abs(c0.X - c1.X) <= 1e-6 && Math.Abs(c0.Y - c1.Y) <= 1e-6 && rr0 <= 1e-6)
                    return SKShader.CreateRadialGradient(ToPoint(c1), (float)Math.Max(rr1, 1e-6), colors, positions, SKShaderTileMode.Clamp);
                return SKShader.CreateTwoPointConicalGradient(ToPoint(c0), (float)rr0, ToPoint(c1), (float)rr1, colors, positions, SKShaderTileMode.Clamp);
            }
            default:
                return null;
        }
    }

    private static List<(double T, SKColor Color)> SampleShadingStops(ShadingPattern pattern, int sampleCount = 33)
    {
        var shading = pattern.Shading;
        sampleCount = Math.Max(sampleCount, 2);
        var (d0, d1) = shading.Domain ?? (0.0, 1.0);
        if (d1 == d0)
            d1 = d0 + 1.0;
        var stops = new List<(double, SKColor)>();
        for (int idx = 0; idx < sampleCount; idx++)
        {
            double t = idx / (double)(sampleCount - 1);
            double value = d0 + (d1 - d0) * t;
            double[] comps;
            try
            {
                comps = shading.Function.Evaluate([value]);
            }
            catch (Exception)
            {
                continue;
            }
            if (comps.Length < 3)
                continue;
            static byte Channel(double v) => (byte)Math.Clamp(Py.Round(v * 255.0), 0, 255);
            stops.Add((t, new SKColor(Channel(comps[0]), Channel(comps[1]), Channel(comps[2]), 255)));
        }
        return stops;
    }

    private static bool DrawTextPatternWithShader(SKCanvas canvas, TextCommand command, PatternPaint paint, DrawState state)
    {
        if (string.IsNullOrEmpty(command.Text))
            return false;
        if (!state.Document.Resources.Patterns.TryGetValue(paint.PatternId, out var p) || p is not TilingPattern pattern)
            return false;
        if (!TilingPatternIsImageOnly(pattern))
            return false;
        var tile = GetPatternTile(pattern, paint, state);
        if (tile is null)
            return false;
        var typeface = ResolveTextTypeface(command.FontRef, state);
        if (typeface is null)
            return false;
        var localMatrix = PatternShaderLocalMatrix(tile, command, imageSpace: true);
        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        using var shader = tile.Image.ToShader(SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, sampling, localMatrix);
        if (shader is null)
            return false;
        using var textPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Shader = shader };
        double fontSize = FontSizeOrOne(command);
        using var font = new SKFont(typeface, (float)fontSize);
        ConfigureSkiaTextFont(font);
        canvas.Save();
        Concat(canvas, command.Matrix);
        canvas.Scale(1f, -1f);
        double baselineShift = RasterRenderer.PalatinoBaselineShift(command.FontRef, fontSize);
        if (baselineShift != 0)
            canvas.Translate(0f, (float)baselineShift);
        DrawTextWithoutKerning(canvas, command.Text, font, textPaint, state, command.FontRef, fontSize, state.FontResolver);
        canvas.Restore();
        return true;
    }

    private static SKMatrix PatternShaderLocalMatrix(PatternTile tile, TextCommand command, bool imageSpace)
    {
        // Text pattern coordinates resolve in the glyph drawing space (matrix plus the local y flip).
        var textToUser = TextLocalToUserMatrix(command);
        var textToPattern = tile.InvMatrix is Matrix inv ? Matrix.Multiply(inv, textToUser) : textToUser;
        if (imageSpace)
        {
            // The XPS image-tile glyph fill needs a baseline-relative phase correction.
            textToPattern = Matrix.Multiply(textToPattern, new Matrix(1.0, 0.0, 0.0, 1.0, 0.0, -6.0));
        }
        double sx = tile.WidthPx / Math.Max(tile.Width, 1e-6);
        double sy = tile.HeightPx / Math.Max(tile.Height, 1e-6);
        var patternToImage = new Matrix(sx, 0.0, 0.0, -sy, -tile.XMin * sx, (tile.YMin + tile.Height) * sy);
        var textToImage = Matrix.Multiply(patternToImage, textToPattern);
        var imageToText = RasterRenderer.InvertMatrix(textToImage) ?? Matrix.Identity;
        return ToSkMatrix(imageToText);
    }

    private static bool TilingPatternIsImageOnly(TilingPattern pattern) => pattern.Commands.Count == 1 && pattern.Commands[0] is ImageCommand;

    private static bool TilingPatternUsesSoftMaskImage(TilingPattern pattern, RenderDocument document)
    {
        if (!TilingPatternIsImageOnly(pattern))
            return false;
        var command = (ImageCommand)pattern.Commands[0];
        return document.Resources.Images.TryGetValue(command.ImageId, out var image) && image.SoftMask is { Length: > 0 };
    }

    private static Matrix TextLocalToUserMatrix(TextCommand command)
    {
        var result = Matrix.Multiply(command.Matrix, new Matrix(1.0, 0.0, 0.0, -1.0, 0.0, 0.0));
        double baselineShift = RasterRenderer.PalatinoBaselineShift(command.FontRef, FontSizeOrOne(command));
        if (baselineShift != 0)
            result = Matrix.Multiply(result, new Matrix(1.0, 0.0, 0.0, 1.0, 0.0, baselineShift));
        return result;
    }

    private static SKPath? BuildTextPathForPattern(TextCommand command, DrawState state)
    {
        if (string.IsNullOrEmpty(command.Text))
            return null;
        var typeface = ResolveTextTypeface(command.FontRef, state);
        if (typeface is null)
            return null;
        using var font = new SKFont(typeface, (float)FontSizeOrOne(command));
        int[] codes = CodePoints(command.Text);
        ushort[] glyphs = font.GetGlyphs(codes);
        if (glyphs.Length != codes.Length)
            return null;
        using var builder = new SKPathBuilder();
        double x = 0.0;
        bool empty = true;
        for (int i = 0; i < glyphs.Length; i++)
        {
            using var glyphPath = font.GetGlyphPath(glyphs[i]);
            if (glyphPath is not null && !glyphPath.IsEmpty)
            {
                builder.AddPath(glyphPath, (float)x, 0f, SKPathAddMode.Append);
                empty = false;
            }
            x += font.MeasureText(Py.Chr(codes[i]));
        }
        if (empty)
            return null;
        // Even-odd keeps counters open whatever the font's contour winding. No Palatino baseline
        // shift here, so patterned show text lines up with charpath outlines.
        builder.FillType = SKPathFillType.EvenOdd;
        var path = builder.Detach();
        var flip = SKMatrix.CreateScale(1f, -1f);
        path.Transform(in flip);
        var matrix = ToSkMatrix(command.Matrix);
        path.Transform(in matrix);
        return path;
    }

    private static PatternTile? GetPatternTile(TilingPattern pattern, PatternPaint paint, DrawState state)
    {
        var baseColor = PatternBaseColor(state.Document, paint);
        if (!state.PatternTiles.TryGetValue(pattern, out var byKey))
        {
            byKey = [];
            state.PatternTiles[pattern] = byKey;
        }
        if (byKey.TryGetValue((baseColor, state.Scale), out var cached))
            return cached;
        var tile = BuildPatternTile(pattern, baseColor, state);
        if (tile is null)
            return null;
        byKey[(baseColor, state.Scale)] = tile;
        return tile;
    }

    private static Rgba PatternBaseColor(RenderDocument document, PatternPaint paint)
    {
        if (paint.BaseSpaceId is null || paint.BaseComponents is null)
            return Rgba.Black;
        if (document.Resources.ColorSpaces.TryGetValue(paint.BaseSpaceId, out var space) && space is DeviceColorSpace device)
        {
            switch (device.Name)
            {
                case "DeviceGray":
                    return RasterRenderer.PaintToRgba(new Paint("DeviceGray", paint.BaseComponents[0])) ?? Rgba.Black;
                case "DeviceRGB":
                    return RasterRenderer.PaintToRgba(new Paint("DeviceRGB", paint.BaseComponents)) ?? Rgba.Black;
                case "DeviceCMYK":
                    return RasterRenderer.PaintToRgba(new Paint("DeviceCMYK", paint.BaseComponents)) ?? Rgba.Black;
            }
        }
        return Rgba.Black;
    }

    private static PatternTile? BuildPatternTile(TilingPattern pattern, Rgba baseColor, DrawState state)
    {
        if (pattern.BBox.Length < 4)
            return null;
        double xMin = pattern.BBox[0], yMin = pattern.BBox[1], xMax = pattern.BBox[2], yMax = pattern.BBox[3];
        double bboxWidth = Math.Max(1.0, xMax - xMin);
        double bboxHeight = Math.Max(1.0, yMax - yMin);
        double stepX = Math.Abs(Math.Abs(pattern.XStep) > 1e-6 ? pattern.XStep : bboxWidth);
        double stepY = Math.Abs(Math.Abs(pattern.YStep) > 1e-6 ? pattern.YStep : bboxHeight);
        double width = stepX, height = stepY;
        double wScaled = Math.Round(width * state.Scale, MidpointRounding.ToEven);
        double hScaled = Math.Round(height * state.Scale, MidpointRounding.ToEven);
        if (!(wScaled <= 16384) || !(hScaled <= 16384))
            return null;
        int widthPx = Math.Max(1, (int)wScaled);
        int heightPx = Math.Max(1, (int)hScaled);
        using var surface = SKSurface.Create(new SKImageInfo(widthPx, heightPx));
        if (surface is null)
            return null;
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        var matrix = TileMatrix(state.Scale, height, xMin, yMin);
        canvas.SetMatrix(matrix);

        foreach (var command in pattern.Commands)
        {
            switch (command)
            {
                case StateSaveCommand:
                    canvas.Save();
                    break;
                case StateRestoreCommand:
                    canvas.Restore();
                    break;
                case ClipCommand clip:
                    using (var clipPath = PathToSkia(clip.Path.Segments, clip.FillRule))
                        canvas.ClipPath(clipPath, SKClipOperation.Intersect, true);
                    break;
                case PathCommand pathCommand:
                    DrawPatternPath(canvas, pathCommand, baseColor);
                    break;
                case TextCommand text:
                    DrawPatternText(canvas, text, baseColor, state);
                    break;
                case ImageCommand image:
                    DrawImage(canvas, image, state.Document);
                    break;
            }
        }

        var snapshot = surface.Snapshot();
        return new PatternTile(snapshot, stepX, stepY, xMin, yMin, width, height, RasterRenderer.InvertMatrix(pattern.Matrix), pattern.Matrix, widthPx, heightPx);
    }

    private static SKMatrix TileMatrix(double scale, double bboxHeight, double xMin, double yMin) =>
        new((float)scale, 0f, (float)(-scale * xMin), 0f, (float)-scale, (float)(scale * (bboxHeight + yMin)), 0f, 0f, 1f);

    private static void DrawPatternPath(SKCanvas canvas, PathCommand command, Rgba baseColor)
    {
        using var path = PathToSkia(command.Path.Segments, command.FillRule);
        var fillColor = PatternPaintColor(command.Fill, baseColor);
        if (fillColor is Rgba fill)
        {
            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, ColorF = ToColorF(fill) };
            canvas.DrawPath(path, paint);
        }
        if (command.Stroke is null)
            return;
        var strokePaint = command.StrokePaint ?? command.Fill;
        Rgba? strokeColor = strokePaint is null ? null
            : strokePaint.Kind == "PatternBase" ? baseColor
            : RasterRenderer.PaintToRgba(strokePaint);
        var color = strokeColor ?? fillColor ?? Rgba.Black;
        using var strokeSkPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            ColorF = ToColorF(color),
            StrokeWidth = (float)command.Stroke.LineWidth,
        };
        ApplyStrokeStyle(strokeSkPaint, command.Stroke);
        canvas.DrawPath(path, strokeSkPaint);
    }

    private static void DrawPatternText(SKCanvas canvas, TextCommand command, Rgba baseColor, DrawState state)
    {
        var rgba = PatternPaintColor(command.Fill, baseColor) ?? Rgba.Black;
        if (DrawTextWithFont(canvas, command, rgba, state))
            return;
        DrawTextFallback(canvas, command, rgba, state, null);
    }

    private static Rgba? PatternPaintColor(Paint? paint, Rgba baseColor)
    {
        if (paint is null)
            return null;
        if (paint.Kind == "PatternBase")
            return baseColor;
        return RasterRenderer.PaintToRgba(paint);
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static SKColorF ToColorF(Rgba c) => new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

    private static SKPoint ToPoint((double X, double Y) p) => new((float)p.X, (float)p.Y);

    /// <summary>The Skia matrix of a PostScript (a, b, c, d, e, f) transform.</summary>
    internal static SKMatrix ToSkMatrix(Matrix m) =>
        new((float)m.A, (float)m.C, (float)m.E, (float)m.B, (float)m.D, (float)m.F, 0f, 0f, 1f);

    private static void Concat(SKCanvas canvas, Matrix m)
    {
        var sk = ToSkMatrix(m);
        canvas.Concat(in sk);
    }
}
