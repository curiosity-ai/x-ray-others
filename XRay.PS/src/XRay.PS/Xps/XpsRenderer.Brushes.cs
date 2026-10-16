// Ported from the brush and opacity helpers of xps/render.py: solid, gradient, image and visual
// brushes as render-model paints, and opacity folded into paints, patterns and images.

using System.Xml.Linq;
using XRay.PS.Imaging;
using XRay.PS.Internal;
using XRay.PS.Rendering;

namespace XRay.PS.Xps;

public sealed partial class XpsRenderer
{
    private static readonly Paint Black = Paint.Rgb(0.0, 0.0, 0.0);

    private static Paint Rgb((double R, double G, double B) c) => Paint.Rgb(c.R, c.G, c.B);

    private static Paint PatternPaintOf(string patternId) => new("Pattern", new PatternPaint(patternId, null, null));

    internal Paint? ResolvePaint(object? value, XpsResourceDictionary? resources, Matrix? paintTransform, Matrix? brushOriginTransform)
    {
        if (value is null)
            return null;
        if (value is XElement element)
            return PaintFromElement(XmlUtil.Clone(element), resources, paintTransform, brushOriginTransform);
        string text = Py.Strip((string)value);
        if (text.StartsWith("{StaticResource", StringComparison.Ordinal))
        {
            var resource = resources?.Resolve(StaticResourceKey(text));
            if (resource is not null)
                return PaintFromElement(resource, resources, paintTransform, brushOriginTransform);
            return Black;
        }
        if (text.StartsWith('<'))
            return PaintFromElement(XmlUtil.Parse(text), resources, paintTransform, brushOriginTransform);
        if (text.StartsWith("ContextColor", StringComparison.Ordinal) && ParseContextColor(text) is { } context)
            return Rgb(context);
        return Rgb(ParseColor(text));
    }

    internal Paint PaintFromElement(XElement element, XpsResourceDictionary? resources, Matrix? paintTransform, Matrix? brushOriginTransform)
    {
        switch (element.Name.LocalName)
        {
            case "SolidColorBrush":
            {
                string colorValue = OrNull(XmlUtil.Get(element, "Color")) ?? "#000000";
                if (Py.Strip(colorValue).StartsWith("ContextColor", StringComparison.Ordinal) && ParseContextColor(colorValue) is { } context)
                    return Rgb(context);
                return Rgb(ParseColor(colorValue));
            }
            case "LinearGradientBrush":
            case "RadialGradientBrush":
                return GradientToPattern(element, paintTransform);
            case "ImageBrush":
                if (ImageBrushToPattern(element, paintTransform) is { } imagePattern)
                    return imagePattern;
                break;
            case "VisualBrush":
                if (VisualBrushToPattern(element, resources, brushOriginTransform ?? paintTransform) is { } visualPattern)
                    return visualPattern;
                break;
        }
        return Black;
    }

    internal static XElement? ResolveBrushElement(object? value, XpsResourceDictionary? resources)
    {
        if (value is null)
            return null;
        if (value is XElement element)
            return XmlUtil.Clone(element);
        string text = Py.Strip((string)value);
        if (text.StartsWith("{StaticResource", StringComparison.Ordinal))
            return resources?.Resolve(StaticResourceKey(text));
        if (text.StartsWith('<'))
            return XmlUtil.Parse(text);
        if (text.Length > 0)
            return new XElement("SolidColorBrush", new XAttribute("Color", text));
        return null;
    }

    internal static double? BrushAlpha(object? value, XpsResourceDictionary? resources)
    {
        if (value is null || value is string { Length: 0 })
            return null;
        if (value is string s && ExtractColorAlpha(Py.Strip(s)) is double alpha)
            return alpha;
        var brush = ResolveBrushElement(value, resources);
        if (brush is null)
            return null;
        if (brush.Name.LocalName == "SolidColorBrush")
            return ExtractColorAlpha(XmlUtil.Get(brush, "Color"));
        return null;
    }

    private Paint GradientToPattern(XElement element, Matrix? paintTransform)
    {
        var stops = new List<(double Offset, (double R, double G, double B) Color)>();
        foreach (var stop in XmlUtil.Descendants(element, "GradientStop"))
        {
            double offset = Or(ParseFloat(XmlUtil.Get(stop, "Offset")), 0.0);
            string colorValue = OrNull(XmlUtil.Get(stop, "Color")) ?? "#000000";
            stops.Add((Clamp(offset, 0.0, 1.0), ParseColor(colorValue)));
        }
        if (stops.Count == 0)
            stops = [(0.0, (0.0, 0.0, 0.0)), (1.0, (1.0, 1.0, 1.0))];
        stops = stops.OrderBy(s => s.Offset).ToList();
        if (stops[0].Offset > 0.0)
            stops.Insert(0, (0.0, stops[0].Color));
        if (stops[^1].Offset < 1.0)
            stops.Add((1.0, stops[^1].Color));
        string spread = Py.Strip(XmlUtil.Get(element, "SpreadMethod") ?? "Pad").ToLowerInvariant();
        if (spread.Length == 0)
            spread = "pad";
        (double T0, double T1)? gradientDomain = null;
        if (spread is "repeat" or "reflect")
            (stops, gradientDomain) = ExpandGradientStopsForSpread(stops, spread, 8);
        var functions = new List<Function>();
        var bounds = new List<double>();
        var encode = new List<double>();
        for (int idx = 0; idx < stops.Count - 1; idx++)
        {
            var left = stops[idx];
            var right = stops[idx + 1];
            if (right.Offset <= left.Offset)
                continue;
            var fn = new ExponentialFunction([0.0, 1.0], [0.0, 1.0, 0.0, 1.0, 0.0, 1.0], [left.Color.R, left.Color.G, left.Color.B], [right.Color.R, right.Color.G, right.Color.B], 1.0);
            _builder.RegisterFunction(fn);
            functions.Add(fn);
            if (idx < stops.Count - 2)
                bounds.Add(right.Offset);
            encode.Add(0.0);
            encode.Add(1.0);
        }
        Function function;
        if (functions.Count == 0)
        {
            var fn = new ExponentialFunction([0.0, 1.0], [0.0, 1.0, 0.0, 1.0, 0.0, 1.0],
                [stops[0].Color.R, stops[0].Color.G, stops[0].Color.B], [stops[^1].Color.R, stops[^1].Color.G, stops[^1].Color.B], 1.0);
            _builder.RegisterFunction(fn);
            function = fn;
        }
        else if (functions.Count == 1)
        {
            function = functions[0];
        }
        else
        {
            var (domainStart, domainEnd) = gradientDomain ?? (0.0, 1.0);
            function = new StitchingFunction([domainStart, domainEnd], [0.0, 1.0, 0.0, 1.0, 0.0, 1.0], functions, [.. bounds], [.. encode]);
            _builder.RegisterFunction(function);
        }
        bool radial = element.Name.LocalName == "RadialGradientBrush";
        Shading shading;
        double sx = 1.0, sy = 1.0;
        if (!radial)
        {
            var start = ParsePoint(XmlUtil.Get(element, "StartPoint"));
            var end = ParsePoint(XmlUtil.Get(element, "EndPoint"));
            shading = new AxialShading(new DeviceColorSpace("DeviceRGB"), [start.X, start.Y, end.X, end.Y], gradientDomain, function, (true, true));
        }
        else
        {
            var center = ParsePoint(XmlUtil.Get(element, "Center"));
            var origin = ParsePoint(XmlUtil.Get(element, "GradientOrigin"));
            double radiusX = Or(ParseFloat(XmlUtil.Get(element, "RadiusX")), 0.0);
            double radiusY = Or(ParseFloat(XmlUtil.Get(element, "RadiusY")), 0.0);
            sx = Math.Abs(radiusX) > 1e-9 ? radiusX : 1.0;
            sy = Math.Abs(radiusY) > 1e-9 ? radiusY : 1.0;
            shading = new RadialShading(new DeviceColorSpace("DeviceRGB"), [origin.X / sx, origin.Y / sy, 0.0, center.X / sx, center.Y / sy, 1.0], gradientDomain, function, (true, true));
        }
        Matrix matrix = Identity;
        if (paintTransform is Matrix pm)
            matrix = radial ? Mul(pm, new Matrix(sx, 0.0, 0.0, sy, 0.0, 0.0)) : pm;
        else if (radial)
            matrix = new Matrix(sx, 0.0, 0.0, sy, 0.0, 0.0);
        return PatternPaintOf(_builder.RegisterPattern(new ShadingPattern(shading, matrix)));
    }

    private static (List<(double, (double, double, double))>, (double, double)) ExpandGradientStopsForSpread(
        List<(double Offset, (double R, double G, double B) Color)> stops, string spreadMethod, int cycles)
    {
        if (cycles < 1)
            cycles = 1;
        var expanded = new List<(double Offset, (double, double, double) Color)>();
        for (int cycle = -cycles; cycle <= cycles; cycle++)
        {
            IEnumerable<(double Offset, (double R, double G, double B) Color)> seq = stops;
            if (spreadMethod == "reflect" && ((cycle % 2) + 2) % 2 == 1)
                seq = Enumerable.Reverse(stops).Select(s => (1.0 - s.Offset, s.Color));
            foreach (var (off, color) in seq)
                expanded.Add((cycle + off, color));
        }
        expanded = expanded.OrderBy(e => e.Offset).ToList();
        var deduped = new List<(double, (double, double, double))>();
        foreach (var (off, color) in expanded)
        {
            if (deduped.Count > 0 && Math.Abs(off - deduped[^1].Item1) < 1e-9)
                deduped[^1] = (off, color);
            else
                deduped.Add((off, color));
        }
        return (deduped, (-cycles, cycles + 1));
    }

    internal Paint? ImageBrushToPattern(XElement element, Matrix? paintTransform)
    {
        string? source = XmlUtil.Get(element, "ImageSource");
        if (string.IsNullOrEmpty(source))
            return null;
        var image = LoadImage(source);
        if (image is null)
            return null;
        image = CropImageForViewbox(image, ParseRect(XmlUtil.Get(element, "Viewbox"), (0.0, 0.0, image.Width, image.Height)));
        string imageId = _imageStore.Register(image);
        var viewbox = ParseRect(XmlUtil.Get(element, "Viewbox"), (0.0, 0.0, image.Width, image.Height));
        var viewport = ParseRect(XmlUtil.Get(element, "Viewport"), viewbox);
        string tileMode = Py.Strip(OrNull(XmlUtil.Get(element, "TileMode")) ?? "None").ToLowerInvariant();
        double xStep = PyMax(viewport.W, 1.0);
        double yStep = PyMax(viewport.H, 1.0);
        var matrix = new Matrix(PyMax(viewport.W, 1.0), 0.0, 0.0, -PyMax(viewport.H, 1.0), 0.0, PyMax(viewport.H, 1.0));
        var commands = new List<RenderCommand> { new ImageCommand(imageId, image.Width, image.Height, matrix) };
        var pm = new Matrix(1.0, 0.0, 0.0, 1.0, viewport.X, viewport.Y);
        var brushTransform = BrushTransform(element);
        if (brushTransform != Identity)
            pm = Mul(brushTransform, pm);
        if (paintTransform is Matrix pt)
            pm = tileMode == "tile" ? pm with { E = pm.E + pt.E, F = pm.F + pt.F } : Mul(pt, pm);
        var pattern = new TilingPattern(1, tileMode == "tile" ? 1 : 0, [0.0, 0.0, viewport.W, viewport.H], xStep, yStep, pm, commands);
        return PatternPaintOf(_builder.RegisterPattern(pattern));
    }

    private Paint? VisualBrushToPattern(XElement element, XpsResourceDictionary? resources, Matrix? paintTransform)
    {
        var visual = XmlUtil.FindDescendant(element, "VisualBrush.Visual");
        if (visual is null)
            return null;
        var viewbox = ParseRect(XmlUtil.Get(element, "Viewbox"), (0.0, 0.0, 1.0, 1.0));
        var viewport = ParseRect(XmlUtil.Get(element, "Viewport"), viewbox);
        var nestedBuilder = new RenderModelBuilder();
        nestedBuilder.BeginPage(viewbox.W * XpsUnitScale, viewbox.H * XpsUnitScale);
        var nested = new XpsRenderer(nestedBuilder, _imageStore, maskRasterizer: _maskRasterizer) { _depth = _depth };
        if (_package is not null)
            nested.SetPackage(_package);
        if (_currentPart is not null)
            nested.SetCurrentPart(_currentPart);
        var transform = new Matrix(XpsUnitScale, 0.0, 0.0, -XpsUnitScale, -viewbox.X * XpsUnitScale, (viewbox.Y + viewbox.H) * XpsUnitScale);
        foreach (var child in XmlUtil.Children(visual))
            nested.RenderElement(child, resources, transform);
        nestedBuilder.EndPage();
        var page = nestedBuilder.Document().Pages[0];
        if (page.Commands.Count == 0)
            return null;
        double xStep = PyMax(viewport.W, 1.0);
        double yStep = PyMax(viewport.H, 1.0);
        double sx = viewport.W / PyMax(viewbox.W, 1e-9);
        double sy = viewport.H / PyMax(viewbox.H, 1e-9);
        var matrix = new Matrix(sx, 0.0, 0.0, sy, (viewport.X - viewbox.X * sx) * XpsUnitScale, (viewport.Y - viewbox.Y * sy) * XpsUnitScale);
        if (paintTransform is Matrix pt)
            matrix = matrix with { E = matrix.E + pt.E, F = matrix.F + pt.F };
        var pattern = new TilingPattern(1, 1, [0.0, 0.0, viewport.W * XpsUnitScale, viewport.H * XpsUnitScale], xStep * XpsUnitScale, yStep * XpsUnitScale,
            matrix, [.. page.Commands]);
        return PatternPaintOf(_builder.RegisterPattern(pattern));
    }

    /// <summary>A <c>ContextColor profile A,C,M,Y,K</c> value as RGB. The original converted
    /// through the referenced ICC profile when its color library was present; this port uses the
    /// original's fallback conversion.</summary>
    internal static (double R, double G, double B)? ParseContextColor(string value)
    {
        var parts = Py.Split(value);
        if (parts.Count < 3)
            return null;
        string compsRaw = string.Concat(parts.Skip(2));
        var comps = SpaceComma().Split(Py.Strip(compsRaw)).Where(c => c.Length > 0).ToList();
        if (comps.Count == 0)
            return null;
        var floats = new List<double>();
        foreach (string item in comps)
        {
            if (ParseFloat(item) is not double number)
                return null;
            floats.Add(number);
        }
        double alpha = 1.0;
        double c, m, y, k;
        if (floats.Count >= 5)
        {
            alpha = Clamp(floats[0], 0.0, 1.0);
            (c, m, y, k) = (floats[1], floats[2], floats[3], floats[4]);
        }
        else if (floats.Count >= 4)
        {
            (c, m, y, k) = (floats[0], floats[1], floats[2], floats[3]);
        }
        else
        {
            return null;
        }
        c = Clamp(c, 0.0, 1.0);
        m = Clamp(m, 0.0, 1.0);
        y = Clamp(y, 0.0, 1.0);
        k = Clamp(k, 0.0, 1.0);
        double r = (1.0 - c) * (1.0 - k);
        double g = (1.0 - m) * (1.0 - k);
        double b = (1.0 - y) * (1.0 - k);
        if (alpha < 1.0)
        {
            r = BlendComponent(r, alpha);
            g = BlendComponent(g, alpha);
            b = BlendComponent(b, alpha);
        }
        return (r, g, b);
    }

    private double? OpacityFromBrushElement(XElement element, XpsResourceDictionary? resources)
    {
        _ = resources;
        switch (element.Name.LocalName)
        {
            case "SolidColorBrush":
                return AlphaFromColorValue(XmlUtil.Get(element, "Color"));
            case "LinearGradientBrush":
            case "RadialGradientBrush":
            {
                var stops = XmlUtil.Descendants(element, "GradientStop").ToList();
                if (stops.Count == 0)
                    return null;
                double weighted = 0.0, total = 0.0, prev = 0.0;
                foreach (var stop in stops)
                {
                    double off = Clamp(Or(ParseFloat(XmlUtil.Get(stop, "Offset")), 0.0), 0.0, 1.0);
                    if (AlphaFromColorValue(XmlUtil.Get(stop, "Color")) is not double alpha)
                        continue;
                    double segment = PyMax(0.0, off - prev);
                    weighted += alpha * segment;
                    total += segment;
                    prev = off;
                }
                if (total <= 1e-9)
                    return AlphaFromColorValue(XmlUtil.Get(stops[0], "Color"));
                return Clamp(weighted / total, 0.0, 1.0);
            }
            case "ImageBrush":
                return OpacityFromImageBrush(element);
            case "VisualBrush":
                // Conservative fallback: partial visibility rather than full knockout.
                return 0.5;
            default:
                return null;
        }
    }

    private double? OpacityFromImageBrush(XElement element)
    {
        if (_package is null)
            return null;
        string? source = XmlUtil.Get(element, "ImageSource");
        if (string.IsNullOrEmpty(source))
            return null;
        var image = OpenPackageImage(source);
        if (image is null)
            return null;
        if (!image.HasAlphaBand)
            return 1.0;
        var alpha = image.AlphaChannel();
        long acc = 0;
        foreach (byte a in alpha)
            acc += a;
        long totalPx = Math.Max(1, (long)image.Width * image.Height);
        return Clamp(acc / (255.0 * totalPx), 0.0, 1.0);
    }

    /// <summary>Opens a package image generically (the original's <c>Image.open</c>), or null.</summary>
    private DecodedImage? OpenPackageImage(string source)
    {
        if (_package is null)
            return null;
        try
        {
            string partName = ResolvePart(_currentPart ?? "/", source);
            return DecodedImage.Open(_package.Read(partName));
        }
        catch (XpsFormatException)
        {
            return null;
        }
    }

    internal Paint? ApplyOpacityToPaint(Paint? paint, double opacity)
    {
        if (paint is null || opacity >= 0.9999)
            return paint;
        if (paint.Kind == "DeviceRGB")
        {
            if (paint.Value is double[] { Length: 3 } rgb)
                return Paint.Rgb(BlendComponent(rgb[0], opacity), BlendComponent(rgb[1], opacity), BlendComponent(rgb[2], opacity));
            return paint;
        }
        if (paint.Kind != "Pattern" || paint.Value is not PatternPaint pp)
            return paint;
        if (!_builder.PendingDocument.Resources.Patterns.TryGetValue(pp.PatternId, out var pattern))
            return paint;
        var adjusted = ApplyOpacityToPattern(pattern, opacity);
        string newId = _builder.RegisterPattern(adjusted);
        return new Paint("Pattern", new PatternPaint(newId, pp.BaseSpaceId, pp.BaseComponents));
    }

    private Pattern ApplyOpacityToPattern(Pattern pattern, double opacity)
    {
        switch (pattern)
        {
            case ShadingPattern shadingPattern:
            {
                var function = ApplyOpacityToFunction(shadingPattern.Shading.Function, opacity);
                RegisterFunctionTree(function);
                Shading shading = shadingPattern.Shading switch
                {
                    AxialShading a => new AxialShading(a.ColorSpace, a.Coords, a.Domain, function, a.Extend),
                    RadialShading r => new RadialShading(r.ColorSpace, r.Coords, r.Domain, function, r.Extend),
                    var other => other,
                };
                return new ShadingPattern(shading, shadingPattern.Matrix);
            }
            case TilingPattern tiling:
                return new TilingPattern(tiling.PaintType, tiling.TilingType, tiling.BBox, tiling.XStep, tiling.YStep, tiling.Matrix, ApplyOpacityToCommands(tiling.Commands, opacity));
            default:
                return pattern;
        }
    }

    private static Function ApplyOpacityToFunction(Function function, double opacity) => function switch
    {
        ExponentialFunction e => new ExponentialFunction(e.Domain, e.Range, e.C0.Select(v => BlendComponent(v, opacity)).ToArray(), e.C1.Select(v => BlendComponent(v, opacity)).ToArray(), e.N),
        StitchingFunction s => new StitchingFunction(s.Domain, s.Range, s.Functions.Select(f => ApplyOpacityToFunction(f, opacity)).ToList(), s.Bounds, s.Encode),
        _ => function,
    };

    private void RegisterFunctionTree(Function function)
    {
        if (function is StitchingFunction stitching)
        {
            foreach (var part in stitching.Functions)
                RegisterFunctionTree(part);
        }
        _builder.RegisterFunction(function);
    }

    private List<RenderCommand> ApplyOpacityToCommands(List<RenderCommand> commands, double opacity)
    {
        var updated = new List<RenderCommand>(commands.Count);
        foreach (var command in commands)
        {
            switch (command)
            {
                case PathCommand path:
                    updated.Add(new PathCommand(path.Path, path.Stroke, ApplyOpacityToPaint(path.Fill, opacity), path.FillRule,
                        ApplyOpacityToPaint(path.StrokePaint, opacity), path.Overprint, Clamp(path.FillOpacity * opacity, 0.0, 1.0), Clamp(path.StrokeOpacity * opacity, 0.0, 1.0)));
                    break;
                case TextCommand text:
                    updated.Add(new TextCommand(text.Text, text.FontRef, text.FontSize, text.Matrix, ApplyOpacityToPaint(text.Fill, opacity), Clamp(text.FillOpacity * opacity, 0.0, 1.0)));
                    break;
                case ImageCommand image:
                    updated.Add(new ImageCommand(OpacityAdjustedImageId(image.ImageId, opacity), image.Width, image.Height, image.Matrix, image.Mask,
                        ApplyOpacityToPaint(image.MaskPaint, opacity), Clamp(image.Opacity * opacity, 0.0, 1.0)));
                    break;
                default:
                    updated.Add(command);
                    break;
            }
        }
        return updated;
    }

    private string OpacityAdjustedImageId(string imageId, double opacity)
    {
        var key = (imageId, Py.Round(Clamp(opacity, 0.0, 1.0) * 1000.0));
        if (_opacityImageCache.TryGetValue(key, out string? cached))
            return cached;
        if (!_imageStore.TryGet(imageId, out var image))
            throw Py.ValueError($"unknown image resource {imageId}");
        var adjusted = BlendImageResourceWithWhite(image, opacity);
        if (adjusted is null)
        {
            _opacityImageCache[key] = imageId;
            return imageId;
        }
        string newId = _imageStore.Register(adjusted);
        _opacityImageCache[key] = newId;
        return newId;
    }

    private static XpsImageResource? BlendImageResourceWithWhite(XpsImageResource image, double opacity)
    {
        double alpha = Clamp(opacity, 0.0, 1.0);
        if (alpha >= 0.9999)
            return image;
        byte[]? raw = null;
        int width = image.Width;
        int height = image.Height;
        if (image.Filter is null && image.ColorSpace == "DeviceRGB" && image.BitsPerComponent == 8)
        {
            if (image.Data.Length == (long)width * height * 3)
                raw = image.Data;
        }
        else if (image.Filter == "DCTDecode")
        {
            var decoded = JpegDecoder.DecodeToRgb(image.Data);
            if (decoded is null)
                return null;
            (raw, width, height) = decoded.Value;
        }
        if (raw is null)
            return null;
        var blended = new byte[raw.Length];
        double inv = 1.0 - alpha;
        for (int i = 0; i < raw.Length; i++)
            blended[i] = (byte)Py.Round(raw[i] * alpha + 255.0 * inv);
        return new XpsImageResource("", blended, width, height, 8, "DeviceRGB", null, image.XDpi, image.YDpi);
    }

    internal static XpsImageResource CropImageForViewbox(XpsImageResource image, (double X, double Y, double W, double H) viewbox)
    {
        if (viewbox.X == 0.0 && viewbox.Y == 0.0 &&
            Math.Abs(viewbox.W - (image.Width * 96.0 / PyMax(1e-6, image.XDpi))) < 0.5 &&
            Math.Abs(viewbox.H - (image.Height * 96.0 / PyMax(1e-6, image.YDpi))) < 0.5)
        {
            return image;
        }
        double sourceLeft = image.XDpi * viewbox.X / 96.0;
        double sourceTop = image.YDpi * viewbox.Y / 96.0;
        double sourceWidth = image.XDpi * viewbox.W / 96.0;
        double sourceHeight = image.YDpi * viewbox.H / 96.0;
        // The original read the data as raw RGB; anything shorter (an encoded JPEG) failed and
        // was returned unchanged.
        long needed = (long)image.Width * image.Height * 3;
        if (image.Width <= 0 || image.Height <= 0 || image.Data.Length < needed)
            return image;
        long left, top, right, bottom;
        try
        {
            left = Py.Round(sourceLeft);
            top = Py.Round(sourceTop);
            right = Py.Round(sourceLeft + sourceWidth);
            bottom = Py.Round(sourceTop + sourceHeight);
        }
        catch (HostErrorException)
        {
            return image;
        }
        if (right <= left || bottom <= top)
            return image;
        long cropW = Math.Max(1, right - left);
        long cropH = Math.Max(1, bottom - top);
        if (cropW * cropH > 200_000_000)
            return image;
        var data = new byte[cropW * cropH * 3];
        Array.Fill(data, (byte)255);
        long srcL = Math.Max(0, left), srcT = Math.Max(0, top);
        long srcR = Math.Min(image.Width, right), srcB = Math.Min(image.Height, bottom);
        if (srcR > srcL && srcB > srcT)
        {
            for (long y = srcT; y < srcB; y++)
            {
                long dy = y - top;
                Array.Copy(image.Data, (y * image.Width + srcL) * 3, data, (dy * cropW + (srcL - left)) * 3, (srcR - srcL) * 3);
            }
        }
        return new XpsImageResource("", data, (int)cropW, (int)cropH, 8, "DeviceRGB", null, image.XDpi, image.YDpi);
    }
}
