// Ported from the opacity-mask and rasterized-fill helpers of xps/render.py. Content the PDF
// model cannot express (opacity masks, spread gradients, hairline brush strokes) is rendered to
// soft-masked images, with coverage from the imaging-library primitives in PillowCompat.

using System.Xml.Linq;
using XRay.PS.Imaging;
using XRay.PS.Internal;
using XRay.PS.Rendering;

namespace XRay.PS.Xps;

public sealed partial class XpsRenderer
{
    private static XElement? ExtractOpacityMaskBrush(XElement element, string wrapperName, XpsResourceDictionary? resources)
    {
        var wrapper = XmlUtil.FindChild(element, wrapperName);
        if (wrapper is not null)
            return wrapper.Elements().FirstOrDefault();
        // Deviation from the original, which read only the property element: ECMA-388 also allows
        // OpacityMask="{StaticResource key}", which it ignored, drawing the element unmasked.
        string? value = XmlUtil.Get(element, "OpacityMask");
        if (string.IsNullOrEmpty(value) || !IsStaticResource(value))
            return null;
        return resources?.Resolve(StaticResourceKey(value));
    }

    /// <summary>Renders the canvas children to an image whose alpha is multiplied by the mask.</summary>
    private bool TryRenderCanvasOpacityMaskAsImage(XElement element, XpsResourceDictionary resources, Matrix paintTransform)
    {
        if (_package is null)
            return false;
        var maskBrush = ExtractOpacityMaskBrush(element, "Canvas.OpacityMask", resources);
        if (maskBrush is null)
            return false;
        var page = _builder.ActivePage;
        if (page is null || page.Width <= 0.0 || page.Height <= 0.0)
            return false;
        var tempBuilder = new RenderModelBuilder();
        var tempStore = new XpsImageStore();
        tempBuilder.BeginPage(page.Width, page.Height);
        var tempRenderer = new XpsRenderer(tempBuilder, tempStore, rasterizeSolidStrokes: _rasterizeSolidStrokes, maskRasterizer: _maskRasterizer) { _depth = _depth };
        tempRenderer.SetPackage(_package);
        if (_currentPart is not null)
            tempRenderer.SetCurrentPart(_currentPart);
        string? clipData = XmlUtil.Get(element, "Clip");
        bool clip = !string.IsNullOrEmpty(clipData);
        if (clip)
        {
            tempBuilder.SaveState();
            var (clipPath, clipRule) = ParseClip(clipData!, resources, paintTransform);
            tempBuilder.Clip(clipPath, clipRule ?? ExtractFillRule(element, resources));
        }
        foreach (var child in XmlUtil.Children(element))
            tempRenderer.RenderElement(child, resources, paintTransform);
        if (clip)
            tempBuilder.RestoreState();
        tempBuilder.EndPage();
        var renderDoc = tempBuilder.Document();
        if (renderDoc.Pages.Count == 0)
            return false;
        XpsConverter.AttachImageResources(renderDoc, tempStore);
        var resource = RasterizeToResource(renderDoc, 96);
        double scaleX = resource.Width / PyMax(page.Width, 1e-6);
        double scaleY = resource.Height / PyMax(page.Height, 1e-6);
        var invPaint = Invert(paintTransform);
        var alpha = new byte[resource.Width * resource.Height];
        var existingAlpha = resource.SoftMask;
        double pageHeight = page.Height;
        for (int py = 0; py < resource.Height; py++)
        {
            Guard.Check();
            double userY = pageHeight - (py + 0.5) / scaleY;
            int row = py * resource.Width;
            for (int px = 0; px < resource.Width; px++)
            {
                double userX = (px + 0.5) / scaleX;
                double brushX = userX, brushY = userY;
                if (invPaint is Matrix inv)
                    (brushX, brushY) = Apply(inv, userX, userY);
                double maskAlpha = Clamp(MaskAlphaFromBrushAt(maskBrush, brushX, brushY), 0.0, 1.0);
                double pixelAlpha = (existingAlpha is not null ? existingAlpha[row + px] : 255) / 255.0;
                alpha[row + px] = (byte)Py.Round(Clamp(pixelAlpha * maskAlpha, 0.0, 1.0) * 255.0);
            }
        }
        string imageId = _imageStore.Register(new XpsImageResource("", resource.Data, resource.Width, resource.Height, 8, "DeviceRGB", null, 96.0, 96.0, alpha));
        _builder.AddImage(imageId, resource.Width, resource.Height, new Matrix(page.Width, 0.0, 0.0, page.Height, 0.0, 0.0));
        return true;
    }

    /// <summary>Rasterizes a one-page document with the mask rasterizer (transparent background,
    /// fractional page size kept) and decodes the PNG as the original did.</summary>
    private XpsImageResource RasterizeToResource(RenderDocument document, int dpi)
    {
        var options = new ImageSaveOptions(ImageFormat.Png, dpi)
        {
            FontResolver = XpsConverter.BuildXpsFontResolver(_package!, document, null),
            OpaqueBackground = false,
            PreserveFractionalPageSize = true,
        };
        return XpsImageDecoders.DecodePng(_maskRasterizer.Write(document, options));
    }

    private bool TryRenderGlyphsOpacityMaskAsImage(string text, string fontRef, double fontSize, Matrix matrix, Paint? fill, double fillOpacity,
        XElement maskBrush, Matrix paintTransform)
    {
        if (_package is null || fill is null)
            return false;
        var page = _builder.ActivePage;
        if (page is null || page.Width <= 0.0 || page.Height <= 0.0)
            return false;
        var tempBuilder = new RenderModelBuilder();
        tempBuilder.BeginPage(page.Width, page.Height);
        var tempMatrix = new Matrix(matrix.A, matrix.B, matrix.C, -matrix.D, matrix.E, page.Height - matrix.F);
        tempBuilder.AddText(text, fontRef, fontSize, tempMatrix, fill, fillOpacity);
        tempBuilder.EndPage();
        var renderDoc = tempBuilder.Document();
        var resource = RasterizeToResource(renderDoc, 300);
        var existingAlpha = resource.SoftMask;
        if (existingAlpha is null)
            return false;
        if (NonzeroAlphaBounds(existingAlpha, resource.Width, resource.Height) is not var (x0, y0, x1, y1))
            return false;
        const int pad = 2;
        x0 = Math.Max(0, x0 - pad);
        y0 = Math.Max(0, y0 - pad);
        x1 = Math.Min(resource.Width, x1 + pad);
        y1 = Math.Min(resource.Height, y1 + pad);
        int cropWidth = x1 - x0;
        int cropHeight = y1 - y0;
        if (cropWidth <= 0 || cropHeight <= 0)
            return false;
        double scaleX = resource.Width / PyMax(page.Width, 1e-6);
        double scaleY = resource.Height / PyMax(page.Height, 1e-6);
        var invPaint = Invert(paintTransform);
        var rgb = new byte[cropWidth * cropHeight * 3];
        var alpha = new byte[cropWidth * cropHeight];
        double pageHeight = page.Height;
        for (int py = 0; py < cropHeight; py++)
        {
            Guard.Check();
            int srcY = y1 - 1 - py;
            double userY = pageHeight - (srcY + 0.5) / scaleY;
            for (int px = 0; px < cropWidth; px++)
            {
                int srcX = x0 + px;
                double userX = (srcX + 0.5) / scaleX;
                var (brushX, brushY) = SampleBrushCoordinates(maskBrush, userX, userY, invPaint);
                int srcIndex = srcY * resource.Width + srcX;
                int dstIndex = py * cropWidth + px;
                int baseIndex = srcIndex * 3;
                for (int c = 0; c < 3; c++)
                    rgb[dstIndex * 3 + c] = baseIndex + c < resource.Data.Length ? resource.Data[baseIndex + c] : (byte)0;
                double maskAlpha = Clamp(MaskAlphaFromBrushAt(maskBrush, brushX, brushY), 0.0, 1.0);
                double pixelAlpha = existingAlpha[srcIndex] / 255.0;
                alpha[dstIndex] = (byte)Py.Round(Clamp(pixelAlpha * maskAlpha, 0.0, 1.0) * 255.0);
            }
        }
        string imageId = _imageStore.Register(new XpsImageResource("", rgb, cropWidth, cropHeight, 8, "DeviceRGB", null, 300.0, 300.0, alpha));
        double imageX = x0 / scaleX;
        double imageY = (y0 / scaleY) - (7.0 / scaleY);
        _builder.AddImage(imageId, cropWidth, cropHeight, new Matrix(cropWidth / scaleX, 0.0, 0.0, cropHeight / scaleY, imageX, imageY));
        return true;
    }

    private Paint? ApplyOpacityMaskToFillPaint(Paint? fill, XElement maskBrush, XpsResourceDictionary? resources, Matrix? paintTransform,
        Matrix? brushOriginTransform, RenderPath? path, (double, double, double, double)? pathBBox, XElement? fillBrush)
    {
        if (fill is null)
            return fill;
        string maskTag = maskBrush.Name.LocalName;
        bool maskGradient = maskTag is "LinearGradientBrush" or "RadialGradientBrush";
        if (pathBBox is { } bbox && fillBrush is not null && fillBrush.Name.LocalName == "SolidColorBrush" && (maskTag == "SolidColorBrush" || maskGradient))
        {
            var solidMask = RasterizeMaskedSolidFill(fillBrush, maskBrush, paintTransform, bbox);
            if (solidMask is not null)
                return solidMask;
        }
        if (pathBBox is { } bbox2 && fillBrush is not null && fillBrush.Name.LocalName is "SolidColorBrush" or "LinearGradientBrush" or "RadialGradientBrush" && maskGradient)
        {
            var gradientMask = RasterizeMaskedGradientFill(fillBrush, maskBrush, paintTransform, path!, bbox2);
            if (gradientMask is not null)
                return gradientMask;
        }
        if (pathBBox is { } bbox3 && fill.Kind == "Pattern" && fill.Value is PatternPaint && maskGradient)
        {
            var shadedMask = RasterizeMaskedShadingFill(fill, maskBrush, paintTransform, path!, bbox3);
            if (shadedMask is not null)
                return shadedMask;
        }
        if (fill.Kind == "DeviceRGB" && fill.Value is double[] { Length: 3 } baseValue)
        {
            var baseColor = (Clamp(baseValue[0], 0.0, 1.0), Clamp(baseValue[1], 0.0, 1.0), Clamp(baseValue[2], 0.0, 1.0));
            if (maskGradient)
            {
                var mod = XmlUtil.Clone(maskBrush);
                foreach (var stop in XmlUtil.Descendants(mod, "GradientStop").ToList())
                {
                    double alpha = AlphaFromColorValue(XmlUtil.Get(stop, "Color")) ?? 1.0;
                    var rgb = (BlendComponent(baseColor.Item1, alpha), BlendComponent(baseColor.Item2, alpha), BlendComponent(baseColor.Item3, alpha));
                    stop.SetAttributeValue("Color", RgbToHex(rgb));
                }
                return PaintFromElement(mod, resources, paintTransform, brushOriginTransform);
            }
            if (maskTag == "ImageBrush" && PaintFromImageOpacityMask(maskBrush, baseColor, paintTransform) is { } imagePaint)
                return imagePaint;
        }
        if (fill.Kind == "Pattern" && fill.Value is PatternPaint && ApplyMaskToImagePatternFill(fill, maskBrush, paintTransform) is { } patternMaskPaint)
            return patternMaskPaint;
        if (OpacityFromBrushElement(maskBrush, resources) is double maskAlpha)
            return ApplyOpacityToPaint(fill, maskAlpha);
        return fill;
    }

    private static (int, int, int, int)? NonzeroAlphaBounds(byte[] alpha, int width, int height)
    {
        int minX = width, minY = height, maxX = -1, maxY = -1;
        for (int y = 0; y < height; y++)
        {
            Guard.Check();
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                if (alpha[row + x] == 0)
                    continue;
                if (x < minX)
                    minX = x;
                if (y < minY)
                    minY = y;
                if (x > maxX)
                    maxX = x;
                if (y > maxY)
                    maxY = y;
            }
        }
        if (maxX < minX || maxY < minY)
            return null;
        return (minX, minY, maxX + 1, maxY + 1);
    }

    private static int PixelCount(double value, double rasterScale) => (int)Math.Max(1, Py.Int(Math.Ceiling(value * rasterScale)));

    private Paint ImagePattern(byte[] pixels, byte[] alpha, int widthPx, int heightPx, double rasterScale, double bboxX0, double bboxY0, double width, double height)
    {
        string imageId = _imageStore.Register(new XpsImageResource("", pixels, widthPx, heightPx, 8, "DeviceRGB", null, 96.0 * rasterScale, 96.0 * rasterScale, alpha));
        var pattern = new TilingPattern(1, 0, [0.0, 0.0, width, height], width, height, new Matrix(1.0, 0.0, 0.0, 1.0, bboxX0, bboxY0),
            [new ImageCommand(imageId, widthPx, heightPx, new Matrix(width, 0.0, 0.0, -height, 0.0, height))]);
        return PatternPaintOf(_builder.RegisterPattern(pattern));
    }

    private static byte ToByte01(double v) => (byte)Py.Round(Clamp(v, 0.0, 1.0) * 255.0);

    private Paint? RasterizeMaskedSolidFill(XElement fillBrush, XElement maskBrush, Matrix? paintTransform, (double X0, double Y0, double X1, double Y1) bbox)
    {
        double width = PyMax(1e-6, bbox.X1 - bbox.X0);
        double height = PyMax(1e-6, bbox.Y1 - bbox.Y0);
        const double rasterScale = 2.0;
        int widthPx = PixelCount(width, rasterScale);
        int heightPx = PixelCount(height, rasterScale);
        if ((long)widthPx * heightPx > 64_000_000)
            return null;
        Matrix? invPaint = paintTransform is Matrix pt ? Invert(pt) : null;
        var rgb = SampleBrushRgb(fillBrush, 0.0, 0.0);
        var pixels = new byte[widthPx * heightPx * 3];
        var alphaBytes = new byte[widthPx * heightPx];
        byte r = ToByte01(rgb.R), g = ToByte01(rgb.G), b = ToByte01(rgb.B);
        for (int py = 0; py < heightPx; py++)
        {
            Guard.Check();
            double userY = bbox.Y0 + (py + 0.5) / rasterScale;
            for (int px = 0; px < widthPx; px++)
            {
                double userX = bbox.X0 + (px + 0.5) / rasterScale;
                var (brushX, brushY) = SampleBrushCoordinates(fillBrush, userX, userY, invPaint);
                double alpha = MaskAlphaFromBrushAt(maskBrush, brushX, brushY);
                int idx = (py * widthPx + px) * 3;
                pixels[idx] = r;
                pixels[idx + 1] = g;
                pixels[idx + 2] = b;
                alphaBytes[py * widthPx + px] = ToByte01(alpha);
            }
        }
        return ImagePattern(pixels, alphaBytes, widthPx, heightPx, rasterScale, bbox.X0, bbox.Y0, width, height);
    }

    /// <summary>Coverage of a path's polygonized subpaths, drawn 4x supersampled and reduced
    /// with LANCZOS, as the original drew it; null when the path is an axis-aligned rectangle.</summary>
    private static GrayImage? PathCoverage(RenderPath path, (double X0, double Y0, double X1, double Y1) bbox, double rasterScale, int widthPx, int heightPx, out bool empty)
    {
        empty = false;
        if (PathIsAxisAlignedRect(path, bbox))
            return null;
        var subpaths = FlattenRenderPath(path.Segments, rasterScale);
        if (subpaths.Count == 0)
        {
            empty = true;
            return null;
        }
        const int supersample = 4;
        var maskHi = new GrayImage(checked(widthPx * supersample), checked(heightPx * supersample));
        foreach (var subpath in subpaths)
        {
            if (subpath.Count < 3)
                continue;
            var polygon = subpath.Select(p => ((p.X - bbox.X0) * rasterScale * supersample, (bbox.Y1 - p.Y) * rasterScale * supersample)).ToList();
            PillowDraw.FillPolygon(maskHi, polygon, 255);
        }
        return PillowResample.Resize(maskHi, widthPx, heightPx, ResampleFilter.Lanczos);
    }

    private bool TryRenderMaskedSolidFillAsImage(RenderPath path, (double, double, double, double)? pathBBox, XElement? fillBrush, XElement maskBrush, Matrix? paintTransform, double opacity)
    {
        if (pathBBox is not var (bx0, by0, bx1, by1) || fillBrush is null)
            return false;
        string maskTag = maskBrush.Name.LocalName;
        if (maskTag is not ("SolidColorBrush" or "LinearGradientBrush" or "RadialGradientBrush"))
            return false;
        if (fillBrush.Name.LocalName != "SolidColorBrush")
            return false;
        var bbox = (bx0, by0, bx1, by1);
        double width = PyMax(1e-6, bx1 - bx0);
        double height = PyMax(1e-6, by1 - by0);
        const double rasterScale = 1.5;
        int widthPx = PixelCount(width, rasterScale);
        int heightPx = PixelCount(height, rasterScale);
        if ((long)widthPx * heightPx > 16_000_000)
            return false;
        var coverage = PathCoverage(path, bbox, rasterScale, widthPx, heightPx, out bool empty);
        if (empty)
            return false;
        Matrix? invPaint = paintTransform is Matrix pt ? Invert(pt) : null;
        var fillRgb = SampleBrushRgb(fillBrush, 0.0, 0.0);
        double? constantMaskAlpha = maskTag == "SolidColorBrush" ? MaskAlphaFromBrushAt(maskBrush, 0.0, 0.0) : null;
        var pixels = new byte[widthPx * heightPx * 3];
        var alphaBytes = new byte[widthPx * heightPx];
        byte r = ToByte01(fillRgb.R), g = ToByte01(fillRgb.G), b = ToByte01(fillRgb.B);
        for (int py = 0; py < heightPx; py++)
        {
            Guard.Check();
            double userY = by0 + (py + 0.5) / rasterScale;
            for (int px = 0; px < widthPx; px++)
            {
                double userX = bx0 + (px + 0.5) / rasterScale;
                double cov = coverage is null ? 1.0 : coverage[px, py] / 255.0;
                double brushX = userX, brushY = userY;
                if (invPaint is Matrix inv)
                    (brushX, brushY) = Apply(inv, userX, userY);
                double alpha = 0.0;
                if (cov > 1e-6)
                    alpha = Clamp((constantMaskAlpha ?? MaskAlphaFromBrushAt(maskBrush, brushX, brushY)) * opacity * cov, 0.0, 1.0);
                int idx = (py * widthPx + px) * 3;
                pixels[idx] = r;
                pixels[idx + 1] = g;
                pixels[idx + 2] = b;
                alphaBytes[py * widthPx + px] = (byte)Py.Round(alpha * 255.0);
            }
        }
        string imageId = _imageStore.Register(new XpsImageResource("", pixels, widthPx, heightPx, 8, "DeviceRGB", null, 96.0 * rasterScale, 96.0 * rasterScale, alphaBytes));
        _builder.AddImage(imageId, widthPx, heightPx, new Matrix(width, 0.0, 0.0, -height, bx0, by0 + height));
        return true;
    }

    private bool TryRenderStrokedBrushAsImage(RenderPath path, XElement strokeBrush, double strokeWidth, Matrix? paintTransform, double opacity)
    {
        string brushTag = strokeBrush.Name.LocalName;
        bool allowed = brushTag is "LinearGradientBrush" or "RadialGradientBrush" || (_rasterizeSolidStrokes && brushTag == "SolidColorBrush");
        if (!allowed)
            return false;
        if (brushTag == "SolidColorBrush" && strokeWidth >= 0.4)
            return false;
        if (PathBBox(path) is not var (px0, py0, px1, py1))
            return false;
        double pad = PyMax(strokeWidth * 1.5, 1.0);
        double bx0 = px0 - pad, by0 = py0 - pad, bx1 = px1 + pad, by1 = py1 + pad;
        double width = PyMax(1e-6, bx1 - bx0);
        double height = PyMax(1e-6, by1 - by0);
        const double rasterScale = 2.0;
        const int supersample = 4;
        int widthPx = PixelCount(width, rasterScale);
        int heightPx = PixelCount(height, rasterScale);
        if ((long)widthPx * heightPx > 16_000_000)
            return false;
        var maskHi = new GrayImage(checked(widthPx * supersample), checked(heightPx * supersample));
        int lineWidthPx = brushTag == "SolidColorBrush"
            ? (int)Math.Max(1, Py.Int(Math.Ceiling(strokeWidth * rasterScale * supersample)) + 1)
            : (int)Math.Max(1, Py.Round(strokeWidth * rasterScale * supersample));
        foreach (var subpath in FlattenRenderPath(path.Segments, rasterScale))
        {
            if (subpath.Count < 2)
                continue;
            var points = subpath.Select(p => ((p.X - bx0) * rasterScale * supersample, (p.Y - by0) * rasterScale * supersample)).ToList();
            PillowDraw.Line(maskHi, points, 255, lineWidthPx, "curve");
        }
        var coverage = PillowResample.Resize(maskHi, widthPx, heightPx, brushTag == "SolidColorBrush" ? ResampleFilter.Box : ResampleFilter.Lanczos);
        Matrix? invPaint = paintTransform is Matrix pt ? Invert(pt) : null;
        var pixels = new byte[widthPx * heightPx * 3];
        var alphaBytes = new byte[widthPx * heightPx];
        for (int py = 0; py < heightPx; py++)
        {
            Guard.Check();
            double userY = by0 + (py + 0.5) / rasterScale;
            for (int px = 0; px < widthPx; px++)
            {
                double userX = bx0 + (px + 0.5) / rasterScale;
                var (brushX, brushY) = SampleBrushCoordinates(strokeBrush, userX, userY, invPaint);
                var rgb = SampleBrushRgb(strokeBrush, brushX, brushY);
                double cov = coverage[px, py] / 255.0;
                int idx = (py * widthPx + px) * 3;
                pixels[idx] = ToByte01(rgb.R);
                pixels[idx + 1] = ToByte01(rgb.G);
                pixels[idx + 2] = ToByte01(rgb.B);
                alphaBytes[py * widthPx + px] = ToByte01(cov * opacity);
            }
        }
        string imageId = _imageStore.Register(new XpsImageResource("", pixels, widthPx, heightPx, 8, "DeviceRGB", null, 96.0 * rasterScale, 96.0 * rasterScale, alphaBytes));
        _builder.AddImage(imageId, widthPx, heightPx, new Matrix(width, 0.0, 0.0, -height, bx0, by0 + height));
        return true;
    }

    private bool TryRenderImageBrushFillAsImage(RenderPath path, (double, double, double, double)? pathBBox, XElement fillBrush, Matrix? paintTransform, double opacity)
    {
        if (paintTransform is not Matrix pt || pathBBox is not { } bbox)
            return false;
        if (fillBrush.Name.LocalName != "ImageBrush")
            return false;
        if (opacity < 0.9999)
            return false;
        if (Py.Strip(OrNull(XmlUtil.Get(fillBrush, "TileMode")) ?? "None").ToLowerInvariant() != "none")
            return false;
        var viewport = ParseRect(XmlUtil.Get(fillBrush, "Viewport"), (0.0, 0.0, 0.0, 0.0));
        if (!PathIsAxisAlignedRect(path, bbox))
            return false;
        string? source = XmlUtil.Get(fillBrush, "ImageSource");
        if (string.IsNullOrEmpty(source))
            return false;
        var image = LoadImage(source);
        if (image is null)
            return false;
        var viewbox = ParseRect(XmlUtil.Get(fillBrush, "Viewbox"), (0.0, 0.0, image.Width, image.Height));
        image = CropImageForViewbox(image, viewbox);
        var brushTransform = BrushTransform(fillBrush);
        bool normalizedViewport = Math.Abs(viewport.X) <= 1e-6 && Math.Abs(viewport.Y) <= 1e-6 && Math.Abs(viewport.W - 1.0) <= 1e-6 && Math.Abs(viewport.H - 1.0) <= 1e-6;
        Matrix finalMatrix;
        if (normalizedViewport)
        {
            var viewportMatrix = new Matrix(PyMax(viewport.W, 1e-9), 0.0, 0.0, PyMax(viewport.H, 1e-9), viewport.X, viewport.Y);
            var imageMatrix = Mul(Mul(brushTransform, viewportMatrix), new Matrix(1.0, 0.0, 0.0, -1.0, 0.0, 1.0));
            finalMatrix = Mul(pt, imageMatrix);
        }
        else
        {
            var viewportMatrix = new Matrix(PyMax(viewport.W, 1e-9), 0.0, 0.0, -PyMax(viewport.H, 1e-9), viewport.X, viewport.Y + PyMax(viewport.H, 1e-9));
            finalMatrix = Mul(pt, Mul(brushTransform, viewportMatrix));
        }
        var imageBBox = MatrixBBox(finalMatrix);
        if (imageBBox is null || !RectClose(bbox, imageBBox.Value))
            return false;
        string imageId = _imageStore.Register(image);
        _builder.AddImage(imageId, image.Width, image.Height, finalMatrix);
        return true;
    }

    private bool TryRenderSpreadGradientFillAsImage(RenderPath path, (double, double, double, double)? pathBBox, XElement fillBrush, Matrix? paintTransform, double opacity)
    {
        if (pathBBox is not var (bx0, by0, bx1, by1) || paintTransform is not Matrix pt)
            return false;
        if (fillBrush.Name.LocalName is not ("LinearGradientBrush" or "RadialGradientBrush"))
            return false;
        string spread = Py.Strip(OrNull(XmlUtil.Get(fillBrush, "SpreadMethod")) ?? "Pad").ToLowerInvariant();
        if (spread is not ("repeat" or "reflect"))
            return false;
        var bbox = (bx0, by0, bx1, by1);
        double width = PyMax(1e-6, bx1 - bx0);
        double height = PyMax(1e-6, by1 - by0);
        const double rasterScale = 2.0;
        int widthPx = PixelCount(width, rasterScale);
        int heightPx = PixelCount(height, rasterScale);
        if ((long)widthPx * heightPx > 16_000_000)
            return false;
        var coverage = PathCoverage(path, bbox, rasterScale, widthPx, heightPx, out bool empty);
        if (empty)
            return false;
        var invPaint = Invert(pt);
        var pixels = new byte[widthPx * heightPx * 3];
        var alphaBytes = new byte[widthPx * heightPx];
        for (int py = 0; py < heightPx; py++)
        {
            Guard.Check();
            double userY = by1 - py / rasterScale;
            for (int px = 0; px < widthPx; px++)
            {
                double userX = bx0 + px / rasterScale;
                double cov = coverage is null ? 1.0 : coverage[px, py] / 255.0;
                (double R, double G, double B) rgb;
                double alpha;
                if (cov > 1e-6)
                {
                    var (brushX, brushY) = SampleBrushCoordinates(fillBrush, userX, userY, invPaint);
                    rgb = SampleGradientBrushRgb(fillBrush, brushX, brushY, _spreadGradientMode);
                    alpha = Clamp(cov * opacity, 0.0, 1.0);
                }
                else
                {
                    rgb = (0.0, 0.0, 0.0);
                    alpha = 0.0;
                }
                int idx = (py * widthPx + px) * 3;
                pixels[idx] = ToByte01(rgb.R);
                pixels[idx + 1] = ToByte01(rgb.G);
                pixels[idx + 2] = ToByte01(rgb.B);
                alphaBytes[py * widthPx + px] = (byte)Py.Round(alpha * 255.0);
            }
        }
        string imageId = _imageStore.Register(new XpsImageResource("", pixels, widthPx, heightPx, 8, "DeviceRGB", null, 96.0 * rasterScale, 96.0 * rasterScale, alphaBytes));
        _builder.AddImage(imageId, widthPx, heightPx, new Matrix(width, 0.0, 0.0, -height, bx0, by0 + height));
        return true;
    }

    private Paint? RasterizeMaskedShadingFill(Paint fill, XElement maskBrush, Matrix? paintTransform, RenderPath path, (double X0, double Y0, double X1, double Y1) bbox)
    {
        var pp = (PatternPaint)fill.Value!;
        if (!_builder.PendingDocument.Resources.Patterns.TryGetValue(pp.PatternId, out var pattern) || pattern is not ShadingPattern shadingPattern)
            return null;
        double width = PyMax(1e-6, bbox.X1 - bbox.X0);
        double height = PyMax(1e-6, bbox.Y1 - bbox.Y0);
        const double rasterScale = 1.5;
        int widthPx = PixelCount(width, rasterScale);
        int heightPx = PixelCount(height, rasterScale);
        if ((long)widthPx * heightPx > 16_000_000)
            return null;
        var coverage = PathCoverage(path, bbox, rasterScale, widthPx, heightPx, out bool empty);
        if (empty)
            return null;
        Matrix? invPaint = paintTransform is Matrix pt ? Invert(pt) : null;
        var pixels = new byte[widthPx * heightPx * 3];
        var alphaBytes = new byte[widthPx * heightPx];
        for (int py = 0; py < heightPx; py++)
        {
            Guard.Check();
            double userY = bbox.Y1 - (py + 0.5) / rasterScale;
            for (int px = 0; px < widthPx; px++)
            {
                double userX = bbox.X0 + (px + 0.5) / rasterScale;
                double cov = coverage is null ? 1.0 : coverage[px, py] / 255.0;
                (double R, double G, double B) rgb = (0.0, 0.0, 0.0);
                double alpha = 0.0;
                if (cov > 1e-6)
                {
                    rgb = SampleShadingPatternRgb(shadingPattern, userX, userY) ?? (0.0, 0.0, 0.0);
                    var (maskX, maskY) = SampleBrushCoordinates(maskBrush, userX, userY, invPaint);
                    alpha = OpacityFromGradientBrushAt(maskBrush, maskX, maskY) * cov;
                }
                int idx = (py * widthPx + px) * 3;
                pixels[idx] = ToByte01(rgb.R);
                pixels[idx + 1] = ToByte01(rgb.G);
                pixels[idx + 2] = ToByte01(rgb.B);
                alphaBytes[py * widthPx + px] = ToByte01(alpha);
            }
        }
        return ImagePattern(pixels, alphaBytes, widthPx, heightPx, rasterScale, bbox.X0, bbox.Y0, width, height);
    }

    private Paint? RasterizeMaskedGradientFill(XElement fillBrush, XElement maskBrush, Matrix? paintTransform, RenderPath path, (double X0, double Y0, double X1, double Y1) bbox)
    {
        double width = PyMax(1e-6, bbox.X1 - bbox.X0);
        double height = PyMax(1e-6, bbox.Y1 - bbox.Y0);
        const double rasterScale = 1.5;
        int widthPx = PixelCount(width, rasterScale);
        int heightPx = PixelCount(height, rasterScale);
        if ((long)widthPx * heightPx > 16_000_000)
            return null;
        var coverage = PathCoverage(path, bbox, rasterScale, widthPx, heightPx, out bool empty);
        if (empty)
            return null;
        Matrix? invPaint = paintTransform is Matrix pt ? Invert(pt) : null;
        var pixels = new byte[widthPx * heightPx * 3];
        var alphaBytes = new byte[widthPx * heightPx];
        for (int py = 0; py < heightPx; py++)
        {
            Guard.Check();
            double userY = bbox.Y1 - (py + 0.5) / rasterScale;
            for (int px = 0; px < widthPx; px++)
            {
                double userX = bbox.X0 + (px + 0.5) / rasterScale;
                double cov = coverage is null ? 1.0 : coverage[px, py] / 255.0;
                (double R, double G, double B) rgb = (0.0, 0.0, 0.0);
                double alpha = 0.0;
                if (cov > 1e-6)
                {
                    var (brushX, brushY) = SampleBrushCoordinates(fillBrush, userX, userY, invPaint);
                    rgb = SampleBrushRgb(fillBrush, brushX, brushY);
                    var (maskX, maskY) = SampleBrushCoordinates(maskBrush, userX, userY, invPaint);
                    alpha = OpacityFromGradientBrushAt(maskBrush, maskX, maskY) * cov;
                }
                int idx = (py * widthPx + px) * 3;
                pixels[idx] = ToByte01(rgb.R);
                pixels[idx + 1] = ToByte01(rgb.G);
                pixels[idx + 2] = ToByte01(rgb.B);
                alphaBytes[py * widthPx + px] = ToByte01(alpha);
            }
        }
        return ImagePattern(pixels, alphaBytes, widthPx, heightPx, rasterScale, bbox.X0, bbox.Y0, width, height);
    }

    private static (double, double, double)? SampleShadingPatternRgb(ShadingPattern pattern, double userX, double userY)
    {
        var inv = Invert(pattern.Matrix);
        double px = userX, py = userY;
        if (inv is Matrix m)
            (px, py) = Apply(m, px, py);
        switch (pattern.Shading)
        {
            case AxialShading axial:
            {
                double x0 = axial.Coords[0], y0 = axial.Coords[1], x1 = axial.Coords[2], y1 = axial.Coords[3];
                double dx = x1 - x0, dy = y1 - y0;
                double denom = dx * dx + dy * dy;
                double t = denom <= 1e-12 ? 0.0 : ((px - x0) * dx + (py - y0) * dy) / denom;
                return EvaluateFunctionRgb(axial.Function, MapShadingParameter(t, axial.Domain));
            }
            case RadialShading radial:
            {
                // The corpus uses concentric radial gradients; the general case is not handled.
                double centerX = radial.Coords[3], centerY = radial.Coords[4];
                double radius = PyMax(Math.Abs(radial.Coords[5]), 1e-9);
                double t = Py.Hypot(px - centerX, py - centerY) / radius;
                return EvaluateFunctionRgb(radial.Function, MapShadingParameter(t, radial.Domain));
            }
            default:
                return null;
        }
    }

    private static double MapShadingParameter(double t, (double T0, double T1)? domain)
    {
        if (domain is not var (d0, d1))
            return Clamp(t, 0.0, 1.0);
        if (Math.Abs(d1 - d0) <= 1e-12)
            return d0;
        t = Clamp(t, 0.0, 1.0);
        return d0 + t * (d1 - d0);
    }

    private static (double, double, double) EvaluateFunctionRgb(Function function, double value)
    {
        switch (function)
        {
            case ExponentialFunction e:
            {
                double d0 = e.Domain[0], d1 = e.Domain[1];
                double u = Math.Abs(d1 - d0) <= 1e-12 ? 0.0 : (value - d0) / (d1 - d0);
                u = Clamp(u, 0.0, 1.0);
                double factor = Py.Pow(u, e.N);
                return (Clamp(e.C0[0] + (e.C1[0] - e.C0[0]) * factor, 0.0, 1.0),
                    Clamp(e.C0[1] + (e.C1[1] - e.C0[1]) * factor, 0.0, 1.0),
                    Clamp(e.C0[2] + (e.C1[2] - e.C0[2]) * factor, 0.0, 1.0));
            }
            case StitchingFunction s:
            {
                if (s.Functions.Count == 0)
                    return (0.0, 0.0, 0.0);
                int idx = 0;
                bool broke = false;
                for (int boundIdx = 0; boundIdx < s.Bounds.Length; boundIdx++)
                {
                    if (value < s.Bounds[boundIdx])
                    {
                        idx = boundIdx;
                        broke = true;
                        break;
                    }
                    idx = boundIdx + 1;
                }
                if (!broke)
                    idx = s.Functions.Count - 1;
                double lower = idx == 0 ? s.Domain[0] : s.Bounds[idx - 1];
                double upper = idx >= s.Bounds.Length ? s.Domain[1] : s.Bounds[idx];
                double e0 = s.Encode[idx * 2], e1 = s.Encode[idx * 2 + 1];
                double mapped = Math.Abs(upper - lower) <= 1e-12 ? e0 : e0 + ((value - lower) / (upper - lower)) * (e1 - e0);
                return EvaluateFunctionRgb(s.Functions[idx], mapped);
            }
            default:
                return (0.0, 0.0, 0.0);
        }
    }

    private Paint? PaintFromImageOpacityMask(XElement maskBrush, (double R, double G, double B) baseColor, Matrix? paintTransform)
    {
        if (_package is null)
            return null;
        string? source = XmlUtil.Get(maskBrush, "ImageSource");
        if (string.IsNullOrEmpty(source))
            return null;
        var img = OpenPackageImage(source);
        if (img is null)
            return null;
        byte[] alphaImg = img.HasAlphaBand ? img.AlphaChannel() : img.ToL();
        int n = img.Width * img.Height;
        var rgb = new byte[n * 3];
        byte r = ToByte01(baseColor.R), g = ToByte01(baseColor.G), b = ToByte01(baseColor.B);
        for (int i = 0; i < n; i++)
        {
            rgb[i * 3] = r;
            rgb[i * 3 + 1] = g;
            rgb[i * 3 + 2] = b;
        }
        var resource = new XpsImageResource("", rgb, img.Width, img.Height, 8, "DeviceRGB", null, 96.0, 96.0, alphaImg);
        string imageId = _imageStore.Register(resource);
        var viewbox = ParseRect(XmlUtil.Get(maskBrush, "Viewbox"), (0.0, 0.0, resource.Width, resource.Height));
        var viewport = ParseRect(XmlUtil.Get(maskBrush, "Viewport"), viewbox);
        string tileMode = Py.Strip(OrNull(XmlUtil.Get(maskBrush, "TileMode")) ?? "None").ToLowerInvariant();
        double xStep = PyMax(viewport.W, 1.0);
        double yStep = PyMax(viewport.H, 1.0);
        var matrix = new Matrix(PyMax(viewport.W, 1.0), 0.0, 0.0, -PyMax(viewport.H, 1.0), 0.0, PyMax(viewport.H, 1.0));
        var pm = new Matrix(XpsUnitScale, 0.0, 0.0, XpsUnitScale, viewport.X * XpsUnitScale, viewport.Y * XpsUnitScale);
        if (paintTransform is Matrix pt)
        {
            double tx = pt.A * viewport.X + pt.C * viewport.Y + pt.E;
            double ty = pt.B * viewport.X + pt.D * viewport.Y + pt.F;
            pm = new Matrix(pt.A, pt.B, pt.C, pt.D, tx, ty);
        }
        var pattern = new TilingPattern(1, tileMode == "tile" ? 1 : 0, [0.0, 0.0, viewport.W, viewport.H], xStep, yStep, pm,
            [new ImageCommand(imageId, resource.Width, resource.Height, matrix)]);
        return PatternPaintOf(_builder.RegisterPattern(pattern));
    }

    private Paint? ApplyMaskToImagePatternFill(Paint fill, XElement maskBrush, Matrix? paintTransform)
    {
        var pp = (PatternPaint)fill.Value!;
        if (!_builder.PendingDocument.Resources.Patterns.TryGetValue(pp.PatternId, out var pattern) || pattern is not TilingPattern tiling)
            return null;
        if (tiling.Commands.Count != 1 || tiling.Commands[0] is not ImageCommand imageCmd)
            return null;
        if (!_imageStore.TryGet(imageCmd.ImageId, out var source))
            return null;
        if (maskBrush.Name.LocalName is not ("LinearGradientBrush" or "RadialGradientBrush"))
            return null;
        var viewport = ParseRect(XmlUtil.Get(maskBrush, "Viewport"), (tiling.BBox[0], tiling.BBox[1], tiling.BBox[2], tiling.BBox[3]));
        int width, height;
        try
        {
            width = (int)Math.Max(1, Py.Round(viewport.W));
            height = (int)Math.Max(1, Py.Round(viewport.H));
        }
        catch (HostErrorException)
        {
            return null;
        }
        byte[] tile;
        int tileW, tileH;
        if (source.Filter == "DCTDecode")
        {
            if (JpegDecoder.DecodeToRgb(source.Data) is not var (decoded, dw, dh))
                return null;
            (tile, tileW, tileH) = (decoded, dw, dh);
        }
        else
        {
            if (source.Data.Length < (long)source.Width * source.Height * 3 || source.Width <= 0 || source.Height <= 0)
                return null;
            (tile, tileW, tileH) = (source.Data, source.Width, source.Height);
        }
        if ((long)width * height > 64_000_000)
            return null;
        byte[] baseImage = tileW == width && tileH == height ? (byte[])tile[..(width * height * 3)].Clone() : PillowResample.ResizeRgb(tile, tileW, tileH, width, height, ResampleFilter.Bicubic);
        for (int yy = 0; yy < height; yy++)
        {
            Guard.Check();
            double py = viewport.Y + yy + 0.5;
            for (int xx = 0; xx < width; xx++)
            {
                double pxX = viewport.X + xx + 0.5;
                double a = OpacityFromGradientBrushAt(maskBrush, pxX, py);
                int idx = (yy * width + xx) * 3;
                for (int c = 0; c < 3; c++)
                    baseImage[idx + c] = (byte)Math.Clamp(Py.Round(baseImage[idx + c] * a + 255.0 * (1.0 - a)), 0, 255);
            }
        }
        string newId = _imageStore.Register(new XpsImageResource("", baseImage, width, height, 8, "DeviceRGB", null));
        var imgMatrix = new Matrix(width, 0.0, 0.0, -height, 0.0, height);
        var pm = new Matrix(XpsUnitScale, 0.0, 0.0, XpsUnitScale, viewport.X * XpsUnitScale, viewport.Y * XpsUnitScale);
        if (paintTransform is Matrix pt)
        {
            double tx = pt.A * viewport.X + pt.C * viewport.Y + pt.E;
            double ty = pt.B * viewport.X + pt.D * viewport.Y + pt.F;
            pm = new Matrix(pt.A, pt.B, pt.C, pt.D, tx, ty);
        }
        var p = new TilingPattern(1, 0, [0.0, 0.0, width, height], width, height, pm, [new ImageCommand(newId, width, height, imgMatrix)]);
        return PatternPaintOf(_builder.RegisterPattern(p));
    }

    internal (double X, double Y) SampleBrushCoordinates(XElement brush, double userX, double userY, Matrix? invPaint)
    {
        double brushX = userX, brushY = userY;
        if (invPaint is Matrix inv)
            (brushX, brushY) = Apply(inv, userX, userY);
        // Absolute gradient coordinates are already page space; image and visual brushes map
        // their own source space.
        if (Py.Strip(XmlUtil.Get(brush, "MappingMode") ?? "").ToLowerInvariant() == "absolute" &&
            brush.Name.LocalName is not ("LinearGradientBrush" or "RadialGradientBrush"))
        {
            var pages = _builder.PendingDocument.Pages;
            double pageHeight = pages.Count > 0 ? pages[^1].Height : 0.0;
            if (pageHeight != 0)
                brushY = pageHeight - brushY;
        }
        return (brushX, brushY);
    }

    private double OpacityFromGradientBrushAt(XElement brush, double x, double y) =>
        InterpolateAlpha(CollectGradientAlphaStops(brush), GradientBrushParameter(brush, x, y));

    private double MaskAlphaFromBrushAt(XElement brush, double x, double y)
    {
        switch (brush.Name.LocalName)
        {
            case "SolidColorBrush":
                return AlphaFromColorValue(XmlUtil.Get(brush, "Color")) is double alpha ? Clamp(alpha, 0.0, 1.0) : 1.0;
            case "LinearGradientBrush":
            case "RadialGradientBrush":
                return OpacityFromGradientBrushAt(brush, x, y);
            case "ImageBrush":
                return OpacityFromImageBrushAt(brush, x, y);
            case "VisualBrush":
                return OpacityFromVisualBrushAt(brush, x, y);
            default:
                return 1.0;
        }
    }

    private double OpacityFromImageBrushAt(XElement brush, double x, double y)
    {
        string? source = XmlUtil.Get(brush, "ImageSource");
        if (string.IsNullOrEmpty(source) || _package is null)
            return 1.0;
        if (!_maskBrushImageCache.TryGetValue(brush, out var cached))
        {
            var pil = DecodedImage.Open(_package.Read(ResolveImageSource(source))) ?? throw new XpsFormatException("cannot identify image file");
            double dpiX = pil.XDpi ?? 96.0;
            double dpiY = pil.YDpi ?? dpiX;
            var viewbox = ParseRect(XmlUtil.Get(brush, "Viewbox"), (0.0, 0.0, pil.Width, pil.Height));
            double sourceLeft = dpiX * viewbox.X / 96.0;
            double sourceTop = dpiY * viewbox.Y / 96.0;
            double sourceWidth = dpiX * viewbox.W / 96.0;
            double sourceHeight = dpiY * viewbox.H / 96.0;
            long left = Py.Round(sourceLeft), top = Py.Round(sourceTop);
            long right = Py.Round(sourceLeft + sourceWidth), bottom = Py.Round(sourceTop + sourceHeight);
            if (right <= left || bottom <= top)
                return 1.0;
            var crop = CropDecoded(pil, (int)Math.Max(0, left), (int)Math.Max(0, top), (int)Math.Min(pil.Width, right), (int)Math.Min(pil.Height, bottom));
            cached = (crop, ParseRect(XmlUtil.Get(brush, "Viewport"), (0.0, 0.0, crop.Width, crop.Height)), Py.Strip(OrNull(XmlUtil.Get(brush, "TileMode")) ?? "None").ToLowerInvariant());
            _maskBrushImageCache[brush] = cached;
        }
        var (image, viewport, tileMode) = cached;
        double vw = PyMax(viewport.W, 1e-9);
        double vh = PyMax(viewport.H, 1e-9);
        double lx = x - viewport.X;
        double ly = y - viewport.Y;
        if (tileMode == "tile")
        {
            lx = Py.Mod(lx, vw);
            ly = Py.Mod(ly, vh);
        }
        else if (lx < 0.0 || ly < 0.0 || lx > vw || ly > vh)
        {
            return 0.0;
        }
        if (image.Width <= 0 || image.Height <= 0)
            return 1.0;
        int sx = Math.Min(image.Width - 1, (int)Math.Max(0, Py.Int((lx / vw) * Math.Max(0, image.Width - 1) + 0.5)));
        int sy = Math.Min(image.Height - 1, (int)Math.Max(0, Py.Int((ly / vh) * Math.Max(0, image.Height - 1) + 0.5)));
        int i = sy * image.Width + sx;
        switch (image.Mode)
        {
            case "RGBA":
                return Clamp(image.Pixels[i * 4 + 3] / 255.0, 0.0, 1.0);
            case "LA":
                // A two-band pixel has neither four nor three values; the original fell through.
                return 1.0;
            case "RGB":
                return Clamp((image.Pixels[i * 3] + (double)image.Pixels[i * 3 + 1] + image.Pixels[i * 3 + 2]) / (255.0 * 3.0), 0.0, 1.0);
            default:
                return 1.0;
        }
    }

    /// <summary>Crops and converts a decoded image the way the mask sampler expected: modes
    /// RGBA and LA stay, anything with alpha becomes RGBA, everything else RGB.</summary>
    private static DecodedImage CropDecoded(DecodedImage image, int left, int top, int right, int bottom)
    {
        int width = Math.Max(0, right - left);
        int height = Math.Max(0, bottom - top);
        string mode;
        byte[] source;
        int bands;
        if (image.Mode is "RGBA" or "LA")
        {
            mode = image.Mode;
            source = image.Pixels;
            bands = image.Mode == "RGBA" ? 4 : 2;
        }
        else if (image.HasAlphaBand)
        {
            mode = "RGBA";
            source = image.ToRgba();
            bands = 4;
        }
        else
        {
            mode = "RGB";
            source = image.ToRgb();
            bands = 3;
        }
        var pixels = new byte[width * height * bands];
        for (int y = 0; y < height; y++)
            Array.Copy(source, ((y + top) * image.Width + left) * bands, pixels, y * width * bands, width * bands);
        return new DecodedImage(mode, width, height, pixels);
    }

    private double OpacityFromVisualBrushAt(XElement brush, double x, double y)
    {
        if (_package is null)
            return 1.0;
        if (!_maskBrushVisualCache.TryGetValue(brush, out var cached))
        {
            var visual = XmlUtil.FindDescendant(brush, "VisualBrush.Visual");
            if (visual is null)
                return 1.0;
            var viewboxValue = ParseRect(XmlUtil.Get(brush, "Viewbox"), (0.0, 0.0, 1.0, 1.0));
            var viewportValue = ParseRect(XmlUtil.Get(brush, "Viewport"), viewboxValue);
            string tile = Py.Strip(OrNull(XmlUtil.Get(brush, "TileMode")) ?? "None").ToLowerInvariant();
            var tempBuilder = new RenderModelBuilder();
            var tempStore = new XpsImageStore();
            tempBuilder.BeginPage(PyMax(viewboxValue.W, 1e-6), PyMax(viewboxValue.H, 1e-6));
            var tempRenderer = new XpsRenderer(tempBuilder, tempStore, rasterizeSolidStrokes: _rasterizeSolidStrokes, maskRasterizer: _maskRasterizer) { _depth = _depth };
            tempRenderer.SetPackage(_package);
            if (_currentPart is not null)
                tempRenderer.SetCurrentPart(_currentPart);
            var localTransform = new Matrix(1.0, 0.0, 0.0, 1.0, -viewboxValue.X, -viewboxValue.Y);
            foreach (var child in XmlUtil.Children(visual))
                tempRenderer.RenderElement(child, null, localTransform);
            tempBuilder.EndPage();
            var renderDoc = tempBuilder.Document();
            XpsConverter.AttachImageResources(renderDoc, tempStore);
            cached = (RasterizeToResource(renderDoc, 300), viewboxValue, viewportValue, tile);
            _maskBrushVisualCache[brush] = cached;
        }
        var (image, viewbox, viewport, tileMode) = cached;
        var (vbX, vbY, vbW, vbH) = viewbox;
        double vw = PyMax(viewport.W, 1e-9);
        double vh = PyMax(viewport.H, 1e-9);
        double lx = x - viewport.X;
        double ly = y - viewport.Y;
        if (tileMode == "tile")
        {
            lx = Py.Mod(lx, vw);
            ly = Py.Mod(ly, vh);
        }
        else if (lx < 0.0 || ly < 0.0 || lx > vw || ly > vh)
        {
            return 0.0;
        }
        double bx = vbX + (lx / vw) * vbW;
        double by = vbY + (ly / vh) * vbH;
        double fx = ((bx - vbX) / PyMax(vbW, 1e-9)) * Math.Max(image.Width - 1, 0);
        double fy = ((by - vbY) / PyMax(vbH, 1e-9)) * Math.Max(image.Height - 1, 0);
        fy = Math.Max(image.Height - 1, 0) - fy;
        int x0 = Math.Min(image.Width - 1, (int)Math.Max(0, Py.Int(Math.Floor(fx))));
        int y0 = Math.Min(image.Height - 1, (int)Math.Max(0, Py.Int(Math.Floor(fy))));
        int x1 = Math.Min(image.Width - 1, x0 + 1);
        int y1 = Math.Min(image.Height - 1, y0 + 1);
        double tx = Clamp(fx - x0, 0.0, 1.0);
        double ty = Clamp(fy - y0, 0.0, 1.0);
        if (image.SoftMask is { } mask && mask.Length >= image.Width * image.Height)
        {
            double a00 = mask[y0 * image.Width + x0] / 255.0;
            double a10 = mask[y0 * image.Width + x1] / 255.0;
            double a01 = mask[y1 * image.Width + x0] / 255.0;
            double a11 = mask[y1 * image.Width + x1] / 255.0;
            double topValue = a00 * (1.0 - tx) + a10 * tx;
            double bottomValue = a01 * (1.0 - tx) + a11 * tx;
            return Clamp(topValue * (1.0 - ty) + bottomValue * ty, 0.0, 1.0);
        }
        double Gray(int ix, int iy)
        {
            int idx = (iy * image.Width + ix) * 3;
            if (idx + 2 >= image.Data.Length)
                return 0.0;
            return (image.Data[idx] + (double)image.Data[idx + 1] + image.Data[idx + 2]) / (255.0 * 3.0);
        }
        double top = Gray(x0, y0) * (1.0 - tx) + Gray(x1, y0) * tx;
        double bottom = Gray(x0, y1) * (1.0 - tx) + Gray(x1, y1) * tx;
        return Clamp(top * (1.0 - ty) + bottom * ty, 0.0, 1.0);
    }

    private (double R, double G, double B) SampleGradientBrushRgb(XElement brush, double x, double y, string mode = "default")
    {
        var stops = CollectGradientRgbStops(brush);
        if (stops.Count == 0)
            return (0.0, 0.0, 0.0);
        return InterpolateRgb(stops, GradientBrushParameter(brush, x, y, mode));
    }

    private (double R, double G, double B) SampleBrushRgb(XElement brush, double x, double y)
    {
        switch (brush.Name.LocalName)
        {
            case "SolidColorBrush":
            {
                if (_solidBrushRgbCache.TryGetValue(brush, out var cached))
                    return cached;
                string colorValue = OrNull(XmlUtil.Get(brush, "Color")) ?? "#000000";
                var rgb = Py.Strip(colorValue).StartsWith("ContextColor", StringComparison.Ordinal) && ParseContextColor(colorValue) is { } context
                    ? context
                    : ParseColor(colorValue);
                _solidBrushRgbCache[brush] = rgb;
                return rgb;
            }
            case "LinearGradientBrush":
            case "RadialGradientBrush":
                return SampleGradientBrushRgb(brush, x, y);
            default:
                return (0.0, 0.0, 0.0);
        }
    }

    private double GradientBrushParameter(XElement brush, double x, double y, string mode = "default")
    {
        var stops = CollectGradientAlphaStops(brush);
        if (stops.Count == 0)
            return 0.0;
        if (!_gradientGeometryCache.TryGetValue(brush, out var geom))
        {
            string tagName = brush.Name.LocalName;
            double[] parameters;
            if (tagName == "LinearGradientBrush")
            {
                var s = ParsePoint(XmlUtil.Get(brush, "StartPoint"));
                var e = ParsePoint(XmlUtil.Get(brush, "EndPoint"));
                parameters = [s.X, s.Y, e.X, e.Y];
            }
            else if (tagName == "RadialGradientBrush")
            {
                var c = ParsePoint(XmlUtil.Get(brush, "Center"));
                var g = ParsePoint(XmlUtil.Get(brush, "GradientOrigin"));
                double rx = Math.Abs(Or(ParseFloat(XmlUtil.Get(brush, "RadiusX")), 0.0));
                double ry = Math.Abs(Or(ParseFloat(XmlUtil.Get(brush, "RadiusY")), 0.0));
                parameters = [c.X, c.Y, g.X, g.Y, rx, ry];
            }
            else
            {
                parameters = [];
            }
            geom = (tagName, parameters, Py.Strip(OrNull(XmlUtil.Get(brush, "SpreadMethod")) ?? "Pad").ToLowerInvariant());
            _gradientGeometryCache[brush] = geom;
        }
        var (tag, p, spread) = geom;
        double t = 0.0;
        if (tag == "LinearGradientBrush")
        {
            double dx = p[2] - p[0], dy = p[3] - p[1];
            double denom = dx * dx + dy * dy;
            t = denom <= 1e-9 ? 0.0 : ((x - p[0]) * dx + (y - p[1]) * dy) / denom;
        }
        else if (tag == "RadialGradientBrush")
        {
            double centerX = p[0], centerY = p[1], originX = p[2], originY = p[3], rx = p[4], ry = p[5];
            if (rx <= 1e-9 || ry <= 1e-9)
            {
                t = 0.0;
            }
            else
            {
                double focusX = (originX - centerX) / rx;
                double focusY = (originY - centerY) / ry;
                double pointX = (x - centerX) / rx;
                double pointY = (y - centerY) / ry;
                double dirX = pointX - focusX;
                double dirY = pointY - focusY;
                if (Math.Abs(dirX) <= 1e-12 && Math.Abs(dirY) <= 1e-12)
                {
                    t = 0.0;
                }
                else
                {
                    double a = dirX * dirX + dirY * dirY;
                    double b = 2.0 * (focusX * dirX + focusY * dirY);
                    double cTerm = focusX * focusX + focusY * focusY - 1.0;
                    double disc = b * b - 4.0 * a * cTerm;
                    if (a <= 1e-12 || disc < 0.0)
                    {
                        t = Py.Sqrt(pointX * pointX + pointY * pointY);
                    }
                    else
                    {
                        double sqrtDisc = Py.Sqrt(PyMax(disc, 0.0));
                        double r1 = (-b - sqrtDisc) / (2.0 * a);
                        double r2 = (-b + sqrtDisc) / (2.0 * a);
                        var roots = new List<double>(2);
                        if (r1 > 1e-12)
                            roots.Add(r1);
                        if (r2 > 1e-12)
                            roots.Add(r2);
                        if (roots.Count == 0)
                            t = Py.Sqrt(pointX * pointX + pointY * pointY);
                        else
                            t = 1.0 / (roots.Count == 1 ? roots[0] : PyMax(roots[0], roots[1]));
                    }
                }
            }
        }
        if (spread == "repeat")
        {
            t = Py.Mod(t, 1.0);
        }
        else if (spread == "reflect")
        {
            if (mode == "pdf")
            {
                double frac = Py.Mod(t, 1.0);
                t = t > 0.0 && Math.Abs(frac) <= 1e-9 ? 1.0 : frac;
            }
            else
            {
                double v = Math.Abs(t);
                long n = Py.Int(Math.Floor(v));
                double frac = v - n;
                t = n % 2 == 0 ? frac : 1.0 - frac;
            }
        }
        else
        {
            t = Clamp(t, 0.0, 1.0);
        }
        return t;
    }

    private List<(double Offset, (double R, double G, double B) Color)> CollectGradientRgbStops(XElement brush)
    {
        if (_gradientRgbStopsCache.TryGetValue(brush, out var cached))
            return cached;
        var values = new List<(double Offset, (double R, double G, double B) Color)>();
        foreach (var stop in XmlUtil.Descendants(brush, "GradientStop"))
        {
            double off = Clamp(Or(ParseFloat(XmlUtil.Get(stop, "Offset")), 0.0), 0.0, 1.0);
            values.Add((off, ParseColor(OrNull(XmlUtil.Get(stop, "Color")) ?? "#000000")));
        }
        if (values.Count == 0)
            return values;
        values = values.OrderBy(v => v.Offset).ToList();
        if (values[0].Offset > 0.0)
            values.Insert(0, (0.0, values[0].Color));
        if (values[^1].Offset < 1.0)
            values.Add((1.0, values[^1].Color));
        _gradientRgbStopsCache[brush] = values;
        return values;
    }

    private static (double, double, double) InterpolateRgb(List<(double Offset, (double R, double G, double B) Color)> stops, double t)
    {
        if (t <= stops[0].Offset)
            return stops[0].Color;
        if (t >= stops[^1].Offset)
            return stops[^1].Color;
        for (int i = 0; i < stops.Count - 1; i++)
        {
            var (o0, c0) = stops[i];
            var (o1, c1) = stops[i + 1];
            if (o0 <= t && t <= o1)
            {
                if (Math.Abs(o1 - o0) <= 1e-9)
                    return c1;
                double u = (t - o0) / (o1 - o0);
                return (Clamp(c0.R + (c1.R - c0.R) * u, 0.0, 1.0), Clamp(c0.G + (c1.G - c0.G) * u, 0.0, 1.0), Clamp(c0.B + (c1.B - c0.B) * u, 0.0, 1.0));
            }
        }
        return stops[^1].Color;
    }

    private List<(double Offset, double Alpha)> CollectGradientAlphaStops(XElement brush)
    {
        if (_gradientAlphaStopsCache.TryGetValue(brush, out var cached))
            return cached;
        var values = new List<(double Offset, double Alpha)>();
        foreach (var stop in XmlUtil.Descendants(brush, "GradientStop"))
        {
            double off = Clamp(Or(ParseFloat(XmlUtil.Get(stop, "Offset")), 0.0), 0.0, 1.0);
            double alpha = AlphaFromColorValue(XmlUtil.Get(stop, "Color")) ?? 1.0;
            values.Add((off, Clamp(alpha, 0.0, 1.0)));
        }
        if (values.Count == 0)
            return values;
        values = values.OrderBy(v => v.Offset).ToList();
        if (values[0].Offset > 0.0)
            values.Insert(0, (0.0, values[0].Alpha));
        if (values[^1].Offset < 1.0)
            values.Add((1.0, values[^1].Alpha));
        _gradientAlphaStopsCache[brush] = values;
        return values;
    }

    private static double InterpolateAlpha(List<(double Offset, double Alpha)> stops, double t)
    {
        if (stops.Count == 0)
            throw new HostErrorException("IndexError", "list index out of range");
        if (t <= stops[0].Offset)
            return stops[0].Alpha;
        if (t >= stops[^1].Offset)
            return stops[^1].Alpha;
        for (int i = 0; i < stops.Count - 1; i++)
        {
            var (o0, a0) = stops[i];
            var (o1, a1) = stops[i + 1];
            if (o0 <= t && t <= o1)
            {
                if (Math.Abs(o1 - o0) <= 1e-9)
                    return a1;
                double u = (t - o0) / (o1 - o0);
                return Clamp(a0 + (a1 - a0) * u, 0.0, 1.0);
            }
        }
        return stops[^1].Alpha;
    }
}
