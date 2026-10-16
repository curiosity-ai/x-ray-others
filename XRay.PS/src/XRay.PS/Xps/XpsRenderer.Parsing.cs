// Ported from the parsing helpers of xps/render.py: numbers, points, colors, transforms, path
// data and path geometry, resource dictionaries and part names.

using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using XRay.PS.Internal;
using XRay.PS.Rendering;

namespace XRay.PS.Xps;

public sealed partial class XpsRenderer
{
    /// <summary>XPS units (1/96 inch) to points.</summary>
    public const double XpsUnitScale = 72.0 / 96.0;

    internal static readonly Matrix Identity = new(1.0, 0.0, 0.0, 1.0, 0.0, 0.0);

    internal static double? ParseFloat(string? value)
    {
        if (value is null)
            return null;
        return Py.TryFloat(value, out double d) ? d : null;
    }

    internal static long? ParseInt(string? value)
    {
        if (value is null)
            return null;
        return Py.TryInt(value, 10, out long v) ? v : null;
    }

    /// <summary>Python's <c>value or default</c> for an optional float: zero (either sign) and
    /// missing values take the default, NaN does not.</summary>
    internal static double Or(double? value, double fallback) => value is double v && v != 0.0 ? v : fallback;

    internal static bool IsTrue(string? value) => value is not null && Py.Strip(value).ToLowerInvariant() is "1" or "true" or "yes";

    internal static bool IsFalse(string? value) => value is not null && Py.Strip(value).ToLowerInvariant() is "0" or "false" or "no";

    internal static double Clamp(double value, double minimum, double maximum)
    {
        if (value < minimum)
            return minimum;
        if (value > maximum)
            return maximum;
        return value;
    }

    internal static string StaticResourceKey(string text) =>
        Py.Strip(text.Replace("{StaticResource", "", StringComparison.Ordinal).Replace("}", "", StringComparison.Ordinal));

    internal static bool IsStaticResource(string text) => Py.Strip(text).StartsWith("{StaticResource", StringComparison.Ordinal);

    // ---- resource dictionaries -------------------------------------------------------------

    internal static XpsResourceDictionary MergeResources(XElement element, XpsResourceDictionary? parent, XpsPackage? package, string? currentPart)
    {
        var items = new Dictionary<string, XElement>(StringComparer.Ordinal);
        var visitedElements = new HashSet<XElement>(ReferenceEqualityComparer.Instance);
        var visitedParts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var res in XmlUtil.Descendants(element, "ResourceDictionary").ToList())
            CollectResourceDictionary(res, items, package, currentPart, visitedElements, visitedParts, 0);
        return new XpsResourceDictionary(items, parent);
    }

    private static void CollectResourceDictionary(XElement dictionary, Dictionary<string, XElement> items, XpsPackage? package, string? currentPart,
        HashSet<XElement> visitedElements, HashSet<string> visitedParts, int depth)
    {
        if (depth > 200 || !visitedElements.Add(dictionary))
            return;
        string? source = XmlUtil.Get(dictionary, "Source");
        if (!string.IsNullOrEmpty(source) && package is not null)
        {
            string partName = ResolvePart(currentPart ?? "/", source);
            if (!visitedParts.Contains(partName) && package.HasPart(partName))
            {
                visitedParts.Add(partName);
                XElement? sourceRoot;
                try
                {
                    sourceRoot = XmlUtil.Parse(package.Read(partName));
                }
                catch (System.Xml.XmlException)
                {
                    sourceRoot = null;
                }
                if (sourceRoot is not null)
                {
                    if (sourceRoot.Name.LocalName == "ResourceDictionary")
                    {
                        CollectResourceDictionary(sourceRoot, items, package, partName, visitedElements, visitedParts, depth + 1);
                    }
                    else
                    {
                        foreach (var child in XmlUtil.Descendants(sourceRoot, "ResourceDictionary").ToList())
                            CollectResourceDictionary(child, items, package, partName, visitedElements, visitedParts, depth + 1);
                    }
                }
            }
        }
        foreach (var child in XmlUtil.Children(dictionary))
        {
            if (child.Name.LocalName == "ResourceDictionary")
            {
                CollectResourceDictionary(child, items, package, currentPart, visitedElements, visitedParts, depth + 1);
                continue;
            }
            string? key = ResourceKey(child);
            if (!string.IsNullOrEmpty(key))
                items[key] = child;
        }
    }

    private static string? ResourceKey(XElement element)
    {
        foreach (var (key, value) in XmlUtil.Attributes(element))
        {
            if (key.EndsWith("Key", StringComparison.Ordinal))
                return value;
        }
        return null;
    }

    // ---- path data lookup ------------------------------------------------------------------

    internal static string? ExtractPathData(XElement element, XpsResourceDictionary? resources)
    {
        string? data = XmlUtil.Get(element, "Data");
        if (!string.IsNullOrEmpty(data))
        {
            data = Py.Strip(data);
            if (data.StartsWith("{StaticResource", StringComparison.Ordinal))
            {
                string key = StaticResourceKey(data);
                var resource = resources?.Resolve(key);
                return resource is not null ? PathDataFromResource(resource) : null;
            }
            return data;
        }
        var dataNode = XmlUtil.FindDescendant(element, "Path.Data");
        if (dataNode is null)
            return null;
        var geometry = XmlUtil.FindDescendant(dataNode, "PathGeometry");
        if (geometry is null)
            return null;
        return OrNull(XmlUtil.Get(geometry, "Figures")) ?? XmlUtil.Get(geometry, "Data");
    }

    private static string? OrNull(string? value) => string.IsNullOrEmpty(value) ? null : value;

    internal static XElement? ExtractPathGeometryElement(XElement element, XpsResourceDictionary? resources)
    {
        string? data = XmlUtil.Get(element, "Data");
        if (!string.IsNullOrEmpty(data))
        {
            data = Py.Strip(data);
            if (data.StartsWith("{StaticResource", StringComparison.Ordinal))
            {
                var resource = resources?.Resolve(StaticResourceKey(data));
                if (resource is not null)
                    return resource.Name.LocalName == "PathGeometry" ? resource : XmlUtil.FindDescendant(resource, "PathGeometry");
                return null;
            }
        }
        var dataNode = XmlUtil.FindDescendant(element, "Path.Data");
        return dataNode is null ? null : XmlUtil.FindDescendant(dataNode, "PathGeometry");
    }

    /// <summary>The fill rule, with the XPS PathGeometry default of EvenOdd.</summary>
    internal static string ExtractFillRule(XElement element, XpsResourceDictionary? resources)
    {
        static string? Rule(string? raw)
        {
            if (string.IsNullOrEmpty(raw))
                return null;
            string rule = Py.Strip(raw).ToLowerInvariant();
            return rule is "evenodd" or "nonzero" ? rule : null;
        }
        if (Rule(XmlUtil.Get(element, "FillRule")) is string direct)
            return direct;
        string? data = XmlUtil.Get(element, "Data");
        if (!string.IsNullOrEmpty(data))
        {
            string compact = Py.LStrip(data);
            if (compact.StartsWith("F1", StringComparison.Ordinal))
                return "nonzero";
            if (compact.StartsWith("F0", StringComparison.Ordinal))
                return "evenodd";
        }
        if (!string.IsNullOrEmpty(data) && Py.Strip(data).StartsWith("{StaticResource", StringComparison.Ordinal))
        {
            var resource = resources?.Resolve(StaticResourceKey(data));
            if (resource is not null)
            {
                if (Rule(XmlUtil.Get(resource, "FillRule")) is string r1)
                    return r1;
                var geometry = XmlUtil.FindDescendant(resource, "PathGeometry");
                if (geometry is not null && Rule(XmlUtil.Get(geometry, "FillRule")) is string r2)
                    return r2;
            }
        }
        var dataNode = XmlUtil.FindDescendant(element, "Path.Data");
        if (dataNode is not null)
        {
            var geometry = XmlUtil.FindDescendant(dataNode, "PathGeometry");
            if (geometry is not null && Rule(XmlUtil.Get(geometry, "FillRule")) is string r3)
                return r3;
        }
        return "evenodd";
    }

    private static string? PathDataFromResource(XElement resource)
    {
        if (resource.Name.LocalName == "PathGeometry")
            return OrNull(XmlUtil.Get(resource, "Figures")) ?? XmlUtil.Get(resource, "Data");
        var geometry = XmlUtil.FindDescendant(resource, "PathGeometry");
        if (geometry is null)
            return null;
        return OrNull(XmlUtil.Get(geometry, "Figures")) ?? XmlUtil.Get(geometry, "Data");
    }

    /// <summary>A paint attribute value, or the brush element of a property element
    /// (<c>Path.Fill</c>). The original serialized that element and parsed it again; the port
    /// carries a detached copy.</summary>
    internal static object? ExtractPaintValue(XElement element, string name)
    {
        string? value = XmlUtil.Get(element, name);
        if (!string.IsNullOrEmpty(value))
            return value;
        string wrapperName = element.Name.LocalName + "." + name;
        var wrapper = XmlUtil.FindDescendant(element, wrapperName);
        if (wrapper is null)
        {
            foreach (var child in element.Elements())
            {
                if (child.Name.LocalName.EndsWith("." + name, StringComparison.Ordinal))
                {
                    wrapper = child;
                    break;
                }
            }
        }
        var brush = wrapper?.Elements().FirstOrDefault();
        return brush is null ? null : XmlUtil.Clone(brush);
    }

    // ---- points, rects, numbers ------------------------------------------------------------

    [GeneratedRegex("[ ,]+")]
    private static partial Regex SpaceComma();

    [GeneratedRegex(@"[-+]?[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?")]
    private static partial Regex NumberToken();

    [GeneratedRegex(@"[A-Za-z]|[-+]?[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?")]
    private static partial Regex PathToken();

    [GeneratedRegex(@"^[-+]?[0-9]*\.?[0-9]+(?:[eE][-+]?[0-9]+)?$")]
    private static partial Regex WholeNumber();

    internal static string[] SplitSpaceComma(string value) => SpaceComma().Split(value);

    internal static (double X, double Y) ParsePoint(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return (0.0, 0.0);
        var parts = SpaceComma().Split(Py.Strip(value));
        if (parts.Length >= 2)
            return (Or(ParseFloat(parts[0]), 0.0), Or(ParseFloat(parts[1]), 0.0));
        return (0.0, 0.0);
    }

    internal static List<(double X, double Y)> ParsePoints(string? value)
    {
        var points = new List<(double, double)>();
        if (string.IsNullOrEmpty(value))
            return points;
        var nums = new List<double>();
        foreach (string item in SpaceComma().Split(Py.Strip(value)))
        {
            if (item.Length == 0)
                continue;
            if (ParseFloat(item) is double v)
                nums.Add(v);
        }
        for (int i = 0; i + 1 < nums.Count; i += 2)
            points.Add((nums[i], nums[i + 1]));
        return points;
    }

    internal static (double X, double Y, double W, double H) ParseRect(string? value, (double, double, double, double) fallback)
    {
        if (string.IsNullOrEmpty(value))
            return fallback;
        var parts = SpaceComma().Split(Py.Strip(value));
        if (parts.Length < 4)
            return fallback;
        var x = ParseFloat(parts[0]);
        var y = ParseFloat(parts[1]);
        var w = ParseFloat(parts[2]);
        var h = ParseFloat(parts[3]);
        if (x is null || y is null || w is null || h is null)
            return fallback;
        return (x.Value, y.Value, w.Value, h.Value);
    }

    internal static List<double> ParseNumbers(string value)
    {
        var result = new List<double>();
        foreach (Match m in NumberToken().Matches(value))
            result.Add(Py.TryFloat(m.Value, out double d) ? d : throw Py.ValueError($"could not convert string to float: '{m.Value}'"));
        return result;
    }

    // ---- colors ----------------------------------------------------------------------------

    internal static double BlendComponent(double value, double opacity) => value * opacity + (1.0 - opacity);

    private static int HexByte(string text)
    {
        if (!Py.TryInt(text, 16, out long v))
            throw Py.ValueError($"invalid literal for int() with base 16: '{text}'");
        return (int)v;
    }

    internal static (double R, double G, double B) ParseColor(string value)
    {
        value = Py.Strip(value);
        if (value.StartsWith("sc#", StringComparison.Ordinal))
        {
            var parts = SpaceComma().Split(value[3..]);
            if (parts.Length >= 4)
            {
                double alpha = Clamp(Or(ParseFloat(parts[0]), 0.0), 0.0, 1.0);
                double r = Clamp(Or(ParseFloat(parts[1]), 0.0), 0.0, 1.0);
                double g = Clamp(Or(ParseFloat(parts[2]), 0.0), 0.0, 1.0);
                double b = Clamp(Or(ParseFloat(parts[3]), 0.0), 0.0, 1.0);
                if (alpha < 1.0)
                {
                    r = BlendComponent(r, alpha);
                    g = BlendComponent(g, alpha);
                    b = BlendComponent(b, alpha);
                }
                return (r, g, b);
            }
            if (parts.Length >= 3)
                return (Clamp(Or(ParseFloat(parts[0]), 0.0), 0.0, 1.0), Clamp(Or(ParseFloat(parts[1]), 0.0), 0.0, 1.0), Clamp(Or(ParseFloat(parts[2]), 0.0), 0.0, 1.0));
        }
        if (value.StartsWith('#'))
        {
            var raw = Py.Chars(value[1..]).ToList();
            double alpha = 1.0;
            if (raw.Count == 8)
            {
                alpha = HexByte(raw[0] + raw[1]) / 255.0;
                raw = raw.Skip(2).ToList();
            }
            if (raw.Count == 6)
            {
                double r = HexByte(raw[0] + raw[1]) / 255.0;
                double g = HexByte(raw[2] + raw[3]) / 255.0;
                double b = HexByte(raw[4] + raw[5]) / 255.0;
                if (alpha < 1.0)
                {
                    // Blend with a white background (the original's model had no per-color alpha here).
                    r = r * alpha + (1.0 - alpha);
                    g = g * alpha + (1.0 - alpha);
                    b = b * alpha + (1.0 - alpha);
                }
                return (r, g, b);
            }
        }
        return (0.0, 0.0, 0.0);
    }

    internal static double? ExtractColorAlpha(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        value = Py.Strip(value);
        if (value.StartsWith("sc#", StringComparison.Ordinal))
        {
            var parts = SpaceComma().Split(value[3..]);
            if (parts.Length >= 4 && ParseFloat(parts[0]) is double alpha)
                return Clamp(alpha, 0.0, 1.0);
            return null;
        }
        if (value.StartsWith('#'))
        {
            var raw = Py.Chars(value[1..]).ToList();
            if (raw.Count == 8)
                return Py.TryInt(raw[0] + raw[1], 16, out long v) ? v / 255.0 : null;
        }
        return null;
    }

    internal static double? AlphaFromColorValue(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        string text = Py.Strip(value);
        if (text.StartsWith('#'))
        {
            var raw = Py.Chars(text[1..]).ToList();
            if (raw.Count == 8)
                return Py.TryInt(raw[0] + raw[1], 16, out long v) ? Clamp(v / 255.0, 0.0, 1.0) : null;
            return 1.0;
        }
        if (text.StartsWith("sc#", StringComparison.Ordinal))
        {
            var parts = SpaceComma().Split(text[3..]).Where(p => p.Length > 0).ToList();
            if (parts.Count >= 4)
                return ParseFloat(parts[0]) is double alpha ? Clamp(alpha, 0.0, 1.0) : null;
            return 1.0;
        }
        return null;
    }

    internal static string RgbToHex((double R, double G, double B) rgb)
    {
        int r = (int)Py.Round(Clamp(rgb.R, 0.0, 1.0) * 255.0);
        int g = (int)Py.Round(Clamp(rgb.G, 0.0, 1.0) * 255.0);
        int b = (int)Py.Round(Clamp(rgb.B, 0.0, 1.0) * 255.0);
        return string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}");
    }

    // ---- transforms --------------------------------------------------------------------------

    internal static Matrix Mul(Matrix m1, Matrix m2) => new(
        m1.A * m2.A + m1.C * m2.B,
        m1.B * m2.A + m1.D * m2.B,
        m1.A * m2.C + m1.C * m2.D,
        m1.B * m2.C + m1.D * m2.D,
        m1.A * m2.E + m1.C * m2.F + m1.E,
        m1.B * m2.E + m1.D * m2.F + m1.F);

    internal static Matrix? Invert(Matrix m)
    {
        double det = m.A * m.D - m.B * m.C;
        if (Math.Abs(det) < 1e-12)
            return null;
        double inv = 1.0 / det;
        return new Matrix(m.D * inv, -m.B * inv, -m.C * inv, m.A * inv, (m.C * m.F - m.D * m.E) * inv, (m.B * m.E - m.A * m.F) * inv);
    }

    internal static (double X, double Y) Apply(Matrix m, double x, double y) => (m.A * x + m.C * y + m.E, m.B * x + m.D * y + m.F);

    internal static Point ApplyPoint(Matrix m, Point p) => new(m.A * p.X + m.C * p.Y + m.E, m.B * p.X + m.D * p.Y + m.F);

    internal static Matrix ElementTransform(XElement element, XpsResourceDictionary? resources)
    {
        double tx = 0.0, ty = 0.0;
        foreach (var (key, value) in XmlUtil.Attributes(element))
        {
            if (key.EndsWith("Canvas.Left", StringComparison.Ordinal))
                tx = Or(ParseFloat(value), 0.0);
            if (key.EndsWith("Canvas.Top", StringComparison.Ordinal))
                ty = Or(ParseFloat(value), 0.0);
        }
        var transform = new Matrix(1.0, 0.0, 0.0, 1.0, tx, ty);
        string? raw = XmlUtil.Get(element, "RenderTransform");
        if (!string.IsNullOrEmpty(raw))
        {
            if (Py.Strip(raw).StartsWith("{StaticResource", StringComparison.Ordinal) && resources is not null)
            {
                var resource = resources.Resolve(StaticResourceKey(raw));
                if (resource is not null)
                    return Mul(transform, MatrixFromTransformElement(resource));
            }
            var numbers = ParseNumbers(raw);
            if (numbers.Count == 6)
                return Mul(transform, new Matrix(numbers[0], numbers[1], numbers[2], numbers[3], numbers[4], numbers[5]));
        }
        foreach (var child in element.Elements())
        {
            if (!child.Name.LocalName.EndsWith(".RenderTransform", StringComparison.Ordinal))
                continue;
            var transformChild = child.Elements().FirstOrDefault();
            if (transformChild is null)
                continue;
            return Mul(transform, MatrixFromTransformElement(transformChild));
        }
        return transform;
    }

    internal static double StrokeScaleFromMatrix(Matrix m)
    {
        double sx = Py.Hypot(m.A, m.B);
        double sy = Py.Hypot(m.C, m.D);
        if (sx <= 1e-12 && sy <= 1e-12)
            return 1.0;
        if (sx <= 1e-12)
            return sy;
        if (sy <= 1e-12)
            return sx;
        return (sx + sy) * 0.5;
    }

    internal static (double, double, double, double)? MatrixBBox(Matrix m)
    {
        Span<Point> points = [ApplyPoint(m, new Point(0, 0)), ApplyPoint(m, new Point(1, 0)), ApplyPoint(m, new Point(1, 1)), ApplyPoint(m, new Point(0, 1))];
        double minX = points[0].X, minY = points[0].Y, maxX = points[0].X, maxY = points[0].Y;
        for (int i = 1; i < 4; i++)
        {
            minX = PyMin(minX, points[i].X);
            minY = PyMin(minY, points[i].Y);
            maxX = PyMax(maxX, points[i].X);
            maxY = PyMax(maxY, points[i].Y);
        }
        return (minX, minY, maxX, maxY);
    }

    internal static bool RectClose((double, double, double, double) left, (double, double, double, double) right, double tolerance = 1.0) =>
        Math.Abs(left.Item1 - right.Item1) <= tolerance && Math.Abs(left.Item2 - right.Item2) <= tolerance &&
        Math.Abs(left.Item3 - right.Item3) <= tolerance && Math.Abs(left.Item4 - right.Item4) <= tolerance;

    internal static double PyMin(double a, double b) => b < a ? b : a;

    internal static double PyMax(double a, double b) => b > a ? b : a;

    internal static int ParseLineCap(XElement element)
    {
        if (!string.IsNullOrEmpty(XmlUtil.Get(element, "StrokeDashArray")))
        {
            string dashCap = Py.Strip(XmlUtil.Get(element, "StrokeDashCap") ?? "").ToLowerInvariant();
            return dashCap switch
            {
                "round" => 1,
                "square" => 2,
                _ => 0,
            };
        }
        string start = Py.Strip(XmlUtil.Get(element, "StrokeStartLineCap") ?? "").ToLowerInvariant();
        string end = Py.Strip(XmlUtil.Get(element, "StrokeEndLineCap") ?? "").ToLowerInvariant();
        string cap = start.Length > 0 ? start : end;
        return cap switch
        {
            "round" => 1,
            "square" => 2,
            _ => 0,
        };
    }

    internal static int ParseLineJoin(XElement element) => Py.Strip(XmlUtil.Get(element, "StrokeLineJoin") ?? "").ToLowerInvariant() switch
    {
        "round" => 1,
        "bevel" => 2,
        _ => 0,
    };

    internal static double[] ParseDashPattern(XElement element, double lineWidth)
    {
        string? raw = XmlUtil.Get(element, "StrokeDashArray");
        if (string.IsNullOrEmpty(raw))
            return [];
        return ParseNumbers(raw).Select(v => PyMax(0.0, v) * lineWidth).ToArray();
    }

    internal static double ParseDashPhase(XElement element, double lineWidth) =>
        PyMax(0.0, Or(ParseFloat(XmlUtil.Get(element, "StrokeDashOffset")), 0.0)) * lineWidth;

    internal static Matrix MatrixFromTransformElement(XElement element)
    {
        switch (element.Name.LocalName)
        {
            case "MatrixTransform":
            {
                var numbers = ParseNumbers(XmlUtil.Get(element, "Matrix") ?? "");
                if (numbers.Count == 6)
                    return new Matrix(numbers[0], numbers[1], numbers[2], numbers[3], numbers[4], numbers[5]);
                break;
            }
            case "TransformGroup":
            {
                var matrix = Identity;
                foreach (var child in element.Elements())
                    matrix = Mul(matrix, MatrixFromTransformElement(child));
                return matrix;
            }
            case "TranslateTransform":
                return new Matrix(1.0, 0.0, 0.0, 1.0, Or(ParseFloat(XmlUtil.Get(element, "X")), 0.0), Or(ParseFloat(XmlUtil.Get(element, "Y")), 0.0));
            case "ScaleTransform":
                return new Matrix(Or(ParseFloat(XmlUtil.Get(element, "ScaleX")), 1.0), 0.0, 0.0, Or(ParseFloat(XmlUtil.Get(element, "ScaleY")), 1.0), 0.0, 0.0);
            case "RotateTransform":
            {
                double angle = Py.Radians(Or(ParseFloat(XmlUtil.Get(element, "Angle")), 0.0));
                double cx = Or(ParseFloat(XmlUtil.Get(element, "CenterX")), 0.0);
                double cy = Or(ParseFloat(XmlUtil.Get(element, "CenterY")), 0.0);
                double cos = Math.Cos(angle);
                double sin = Math.Sin(angle);
                return new Matrix(cos, sin, -sin, cos, cx - cx * cos + cy * sin, cy - cx * sin - cy * cos);
            }
            case "SkewTransform":
            {
                double ax = Math.Tan(Py.Radians(Or(ParseFloat(XmlUtil.Get(element, "AngleX")), 0.0)));
                double ay = Math.Tan(Py.Radians(Or(ParseFloat(XmlUtil.Get(element, "AngleY")), 0.0)));
                return new Matrix(1.0, ay, ax, 1.0, 0.0, 0.0);
            }
        }
        return Identity;
    }

    internal static Matrix BrushTransform(XElement element)
    {
        string? raw = XmlUtil.Get(element, "Transform");
        if (!string.IsNullOrEmpty(raw))
        {
            var numbers = ParseNumbers(raw);
            if (numbers.Count == 6)
                return new Matrix(numbers[0], numbers[1], numbers[2], numbers[3], numbers[4], numbers[5]);
        }
        var wrapper = XmlUtil.FindDescendant(element, element.Name.LocalName + ".Transform");
        var first = wrapper?.Elements().FirstOrDefault();
        return first is null ? Identity : MatrixFromTransformElement(first);
    }

    // ---- paths -------------------------------------------------------------------------------

    internal static (double, double, double, double)? PathBBox(RenderPath path)
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

    internal static bool PathIsAxisAlignedRect(RenderPath path, (double X0, double Y0, double X1, double Y1) bbox)
    {
        var subpaths = FlattenRenderPath(path.Segments, 1.0, 4);
        if (subpaths.Count != 1)
            return false;
        var points = new List<Point>();
        foreach (var point in subpaths[0])
        {
            if (points.Count == 0 || Math.Abs(point.X - points[^1].X) > 1e-6 || Math.Abs(point.Y - points[^1].Y) > 1e-6)
                points.Add(point);
        }
        if (points.Count > 1 && Math.Abs(points[0].X - points[^1].X) <= 1e-6 && Math.Abs(points[0].Y - points[^1].Y) <= 1e-6)
            points.RemoveAt(points.Count - 1);
        if (points.Count != 4)
            return false;
        var xs = points.Select(p => Py.Round(p.X, 6) + 0.0).ToHashSet();
        var ys = points.Select(p => Py.Round(p.Y, 6) + 0.0).ToHashSet();
        var bx = new HashSet<double> { Py.Round(bbox.X0, 6) + 0.0, Py.Round(bbox.X1, 6) + 0.0 };
        var by = new HashSet<double> { Py.Round(bbox.Y0, 6) + 0.0, Py.Round(bbox.Y1, 6) + 0.0 };
        if (!xs.SetEquals(bx) || !ys.SetEquals(by))
            return false;
        for (int idx = 0; idx < 4; idx++)
        {
            var p1 = points[idx];
            var p2 = points[(idx + 1) % 4];
            if (Math.Abs(p1.X - p2.X) > 1e-6 && Math.Abs(p1.Y - p2.Y) > 1e-6)
                return false;
        }
        return true;
    }

    internal static List<List<Point>> FlattenRenderPath(IReadOnlyList<PathSegment> segments, double scale, int curveSteps = 36)
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
                    Point p1 = segment.Points[0], p2 = segment.Points[1], p3 = segment.Points[2];
                    double length = Py.Hypot(p1.X - p0.X, p1.Y - p0.Y) + Py.Hypot(p2.X - p1.X, p2.Y - p1.Y) + Py.Hypot(p3.X - p2.X, p3.Y - p2.Y);
                    long adaptive = Py.Int(length * scale / 6);
                    int steps = adaptive < curveSteps ? curveSteps : (int)Math.Min(200, adaptive);
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

    private static bool StartsWithLetter(string token) => token.Length > 0 && char.IsAsciiLetter(token[0]);

    private static bool StartsWithSignedDigit(string token)
    {
        int i = 0;
        if (i < token.Length && token[i] is '+' or '-')
            i++;
        return i < token.Length && char.IsAsciiDigit(token[i]);
    }

    /// <summary>Parses XPS abbreviated path syntax into a transformed path.</summary>
    internal static RenderPath ParsePathData(string data, Matrix transform)
    {
        var tokens = MergeExponentTokens(PathToken().Matches(data).Select(m => m.Value).ToList());
        var segments = new List<PathSegment>();
        int idx = 0;
        var current = new Point(0.0, 0.0);
        Point? start = null;
        string? command = null;
        Point? lastCubicCtrl2 = null;
        double NextNumber()
        {
            if (idx >= tokens.Count)
                throw new HostErrorException("IndexError", "list index out of range");
            string token = tokens[idx];
            if (!Py.TryFloat(token, out double value))
                throw Py.ValueError($"could not convert string to float: '{token}'");
            idx++;
            return value;
        }
        while (idx < tokens.Count)
        {
            Guard.Check();
            // The original looped forever on a curve with too few numbers left; stop instead.
            int iterationStart = idx;
            string token = tokens[idx];
            if (StartsWithLetter(token))
            {
                command = token;
                idx++;
                if (command is "Z" or "z")
                {
                    if (start is Point s)
                    {
                        segments.Add(PathSegment.ClosePath());
                        current = s;
                    }
                    continue;
                }
                if (command is "F" or "f")
                {
                    if (idx < tokens.Count && StartsWithSignedDigit(tokens[idx]))
                        idx++;
                    continue;
                }
            }
            if (command is null)
                break;
            string cmd = command;
            switch (cmd)
            {
                case "M":
                case "m":
                {
                    double x = NextNumber();
                    double y = NextNumber();
                    if (cmd == "m")
                    {
                        x += current.X;
                        y += current.Y;
                    }
                    segments.Add(new PathSegment(PathSegmentKind.Move, [ApplyPoint(transform, new Point(x, y))]));
                    current = new Point(x, y);
                    start = current;
                    lastCubicCtrl2 = null;
                    command = cmd == "M" ? "L" : "l";
                    break;
                }
                case "L":
                case "l":
                {
                    double x = NextNumber();
                    double y = NextNumber();
                    if (cmd == "l")
                    {
                        x += current.X;
                        y += current.Y;
                    }
                    segments.Add(new PathSegment(PathSegmentKind.Line, [ApplyPoint(transform, new Point(x, y))]));
                    current = new Point(x, y);
                    lastCubicCtrl2 = null;
                    break;
                }
                case "C":
                case "c":
                    while (idx + 5 < tokens.Count && !StartsWithLetter(tokens[idx]))
                    {
                        double x1 = NextNumber(), y1 = NextNumber(), x2 = NextNumber(), y2 = NextNumber(), x3 = NextNumber(), y3 = NextNumber();
                        if (cmd == "c")
                        {
                            x1 += current.X;
                            y1 += current.Y;
                            x2 += current.X;
                            y2 += current.Y;
                            x3 += current.X;
                            y3 += current.Y;
                        }
                        segments.Add(new PathSegment(PathSegmentKind.Curve,
                            [ApplyPoint(transform, new Point(x1, y1)), ApplyPoint(transform, new Point(x2, y2)), ApplyPoint(transform, new Point(x3, y3))]));
                        current = new Point(x3, y3);
                        lastCubicCtrl2 = new Point(x2, y2);
                        if (idx >= tokens.Count || StartsWithLetter(tokens[idx]))
                            break;
                        if (idx + 5 >= tokens.Count)
                            break;
                    }
                    break;
                case "Q":
                case "q":
                {
                    double x1 = NextNumber(), y1 = NextNumber(), x2 = NextNumber(), y2 = NextNumber();
                    if (cmd == "q")
                    {
                        x1 += current.X;
                        y1 += current.Y;
                        x2 += current.X;
                        y2 += current.Y;
                    }
                    var c1 = new Point(current.X + (2.0 / 3.0) * (x1 - current.X), current.Y + (2.0 / 3.0) * (y1 - current.Y));
                    var c2 = new Point(x2 + (2.0 / 3.0) * (x1 - x2), y2 + (2.0 / 3.0) * (y1 - y2));
                    var p3 = new Point(x2, y2);
                    segments.Add(new PathSegment(PathSegmentKind.Curve, [ApplyPoint(transform, c1), ApplyPoint(transform, c2), ApplyPoint(transform, p3)]));
                    current = p3;
                    lastCubicCtrl2 = null;
                    break;
                }
                case "S":
                case "s":
                    while (idx + 3 < tokens.Count && !StartsWithLetter(tokens[idx]))
                    {
                        double x2 = NextNumber(), y2 = NextNumber(), x3 = NextNumber(), y3 = NextNumber();
                        if (cmd == "s")
                        {
                            x2 += current.X;
                            y2 += current.Y;
                            x3 += current.X;
                            y3 += current.Y;
                        }
                        double x1, y1;
                        if (lastCubicCtrl2 is not Point last)
                        {
                            x1 = current.X;
                            y1 = current.Y;
                        }
                        else
                        {
                            x1 = 2.0 * current.X - last.X;
                            y1 = 2.0 * current.Y - last.Y;
                        }
                        segments.Add(new PathSegment(PathSegmentKind.Curve,
                            [ApplyPoint(transform, new Point(x1, y1)), ApplyPoint(transform, new Point(x2, y2)), ApplyPoint(transform, new Point(x3, y3))]));
                        current = new Point(x3, y3);
                        lastCubicCtrl2 = new Point(x2, y2);
                        if (idx >= tokens.Count || StartsWithLetter(tokens[idx]))
                            break;
                        if (idx + 3 >= tokens.Count)
                            break;
                    }
                    break;
                case "A":
                case "a":
                {
                    double rx = NextNumber(), ry = NextNumber(), rotation = NextNumber();
                    long largeArc = Py.Int(NextNumber());
                    long sweep = Py.Int(NextNumber());
                    double x = NextNumber(), y = NextNumber();
                    if (cmd == "a")
                    {
                        x += current.X;
                        y += current.Y;
                    }
                    var end = new Point(x, y);
                    var cubics = ArcToCubicBeziers(current, end, rx, ry, rotation, largeArc != 0, sweep != 0);
                    if (cubics.Count == 0)
                    {
                        segments.Add(new PathSegment(PathSegmentKind.Line, [ApplyPoint(transform, end)]));
                    }
                    else
                    {
                        foreach (var (c1, c2, p) in cubics)
                            segments.Add(new PathSegment(PathSegmentKind.Curve, [ApplyPoint(transform, c1), ApplyPoint(transform, c2), ApplyPoint(transform, p)]));
                    }
                    current = end;
                    lastCubicCtrl2 = null;
                    break;
                }
                default:
                    idx++;
                    break;
            }
            if (idx == iterationStart)
                break;
        }
        return new RenderPath(segments);
    }

    private static List<string> MergeExponentTokens(List<string> tokens)
    {
        if (tokens.Count == 0)
            return tokens;
        var merged = new List<string>(tokens.Count);
        int idx = 0;
        while (idx < tokens.Count)
        {
            Guard.Check();
            string token = tokens[idx];
            if (idx + 2 < tokens.Count && WholeNumber().IsMatch(token) && tokens[idx + 1] is "e" or "E" && WholeNumber().IsMatch(tokens[idx + 2]))
            {
                merged.Add(token + "e" + tokens[idx + 2]);
                idx += 3;
                continue;
            }
            merged.Add(token);
            idx++;
        }
        return merged;
    }

    internal static List<(Point C1, Point C2, Point End)> ArcToCubicBeziers(Point start, Point end, double rx, double ry, double rotationDeg, bool largeArc, bool sweep)
    {
        var curves = new List<(Point, Point, Point)>();
        if (rx == 0.0 || ry == 0.0)
            return curves;
        if (start.X == end.X && start.Y == end.Y)
            return curves;
        rx = Math.Abs(rx);
        ry = Math.Abs(ry);
        double phi = Py.Radians(Py.Mod(rotationDeg, 360.0));
        double cosPhi = Math.Cos(phi);
        double sinPhi = Math.Sin(phi);
        double dx2 = (start.X - end.X) * 0.5;
        double dy2 = (start.Y - end.Y) * 0.5;
        double x1p = cosPhi * dx2 + sinPhi * dy2;
        double y1p = -sinPhi * dx2 + cosPhi * dy2;
        double lam = (x1p * x1p) / (rx * rx) + (y1p * y1p) / (ry * ry);
        if (lam > 1.0)
        {
            double s = Py.Sqrt(lam);
            rx *= s;
            ry *= s;
        }
        double sign = largeArc == sweep ? -1.0 : 1.0;
        double rx2 = rx * rx;
        double ry2 = ry * ry;
        double num = rx2 * ry2 - rx2 * y1p * y1p - ry2 * x1p * x1p;
        double den = rx2 * y1p * y1p + ry2 * x1p * x1p;
        if (den == 0.0)
            return curves;
        double coef = sign * Py.Sqrt(PyMax(0.0, num / den));
        double cxp = coef * (rx * y1p / ry);
        double cyp = coef * (-ry * x1p / rx);
        double cx = cosPhi * cxp - sinPhi * cyp + (start.X + end.X) * 0.5;
        double cy = sinPhi * cxp + cosPhi * cyp + (start.Y + end.Y) * 0.5;
        double ux = (x1p - cxp) / rx;
        double uy = (y1p - cyp) / ry;
        double vx = (-x1p - cxp) / rx;
        double vy = (-y1p - cyp) / ry;
        double theta1 = Math.Atan2(uy, ux);
        double delta = Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
        if (!sweep && delta > 0.0)
            delta -= 2.0 * Math.PI;
        else if (sweep && delta < 0.0)
            delta += 2.0 * Math.PI;
        int parts = (int)Math.Max(1, Py.Int(Math.Ceiling(Math.Abs(delta) / (Math.PI / 2.0))));
        double step = delta / parts;
        for (int idx = 0; idx < parts; idx++)
        {
            double t1 = theta1 + idx * step;
            double t2 = t1 + step;
            double dt = t2 - t1;
            double alpha = (4.0 / 3.0) * Math.Tan(dt / 4.0);
            double x1 = Math.Cos(t1), y1 = Math.Sin(t1), x2 = Math.Cos(t2), y2 = Math.Sin(t2);
            var p1 = new Point(cx + rx * (cosPhi * x1 - sinPhi * y1), cy + ry * (sinPhi * x1 + cosPhi * y1));
            var p2 = new Point(cx + rx * (cosPhi * x2 - sinPhi * y2), cy + ry * (sinPhi * x2 + cosPhi * y2));
            var c1 = new Point(p1.X + alpha * (-rx * (cosPhi * y1 + sinPhi * x1)), p1.Y + alpha * (-ry * (sinPhi * y1 - cosPhi * x1)));
            var c2 = new Point(p2.X + alpha * (rx * (cosPhi * y2 + sinPhi * x2)), p2.Y + alpha * (ry * (sinPhi * y2 - cosPhi * x2)));
            curves.Add((c1, c2, p2));
        }
        return curves;
    }

    private sealed record FigureSegment(PathSegmentKind Kind, Point[] Points, bool IsStroked);

    private sealed record Figure(Point Start, List<FigureSegment> Segments, bool IsClosed, bool IsFilled);

    internal static (RenderPath? Fill, RenderPath? Stroke) ParsePathGeometryVariants(XElement geometry, Matrix transform)
    {
        var fillSegments = new List<PathSegment>();
        var strokeSegments = new List<PathSegment>();
        foreach (var figure in ParsePathGeometryFigures(geometry))
        {
            if (figure.IsFilled)
                fillSegments.AddRange(FigureToFillSegments(figure, transform));
            strokeSegments.AddRange(FigureToStrokeSegments(figure, transform));
        }
        return (fillSegments.Count > 0 ? new RenderPath(fillSegments) : null, strokeSegments.Count > 0 ? new RenderPath(strokeSegments) : null);
    }

    private static List<Figure> ParsePathGeometryFigures(XElement geometry)
    {
        var figures = new List<Figure>();
        foreach (var figure in XmlUtil.Descendants(geometry, "PathFigure"))
        {
            var (sx, sy) = ParsePoint(XmlUtil.Get(figure, "StartPoint"));
            var curr = new Point(sx, sy);
            var figureSegments = new List<FigureSegment>();
            foreach (var seg in figure.Elements())
            {
                string tag = seg.Name.LocalName;
                bool isStroked = !IsFalse(XmlUtil.Get(seg, "IsStroked"));
                switch (tag)
                {
                    case "PolyLineSegment":
                        foreach (var (x, y) in ParsePoints(XmlUtil.Get(seg, "Points")))
                        {
                            figureSegments.Add(new FigureSegment(PathSegmentKind.Line, [new Point(x, y)], isStroked));
                            curr = new Point(x, y);
                        }
                        break;
                    case "ArcSegment":
                    {
                        var (ex, ey) = ParsePoint(XmlUtil.Get(seg, "Point"));
                        var (w, h) = ParsePoint(XmlUtil.Get(seg, "Size"));
                        double rotation = Or(ParseFloat(XmlUtil.Get(seg, "RotationAngle")), 0.0);
                        bool largeArc = IsTrue(XmlUtil.Get(seg, "IsLargeArc"));
                        bool sweep = Py.Strip(XmlUtil.Get(seg, "SweepDirection") ?? "Counterclockwise").ToLowerInvariant() == "clockwise";
                        var cubics = ArcToCubicBeziers(curr, new Point(ex, ey), w, h, rotation, largeArc, sweep);
                        if (cubics.Count == 0)
                            figureSegments.Add(new FigureSegment(PathSegmentKind.Line, [new Point(ex, ey)], isStroked));
                        else
                            foreach (var (c1, c2, p) in cubics)
                                figureSegments.Add(new FigureSegment(PathSegmentKind.Curve, [c1, c2, p], isStroked));
                        curr = new Point(ex, ey);
                        break;
                    }
                    case "BezierSegment":
                    {
                        var points = ParsePoints(XmlUtil.Get(seg, "Points"));
                        if (points.Count >= 3)
                        {
                            figureSegments.Add(new FigureSegment(PathSegmentKind.Curve,
                                [new Point(points[0].X, points[0].Y), new Point(points[1].X, points[1].Y), new Point(points[2].X, points[2].Y)], isStroked));
                            curr = new Point(points[2].X, points[2].Y);
                        }
                        break;
                    }
                    case "PolyQuadraticBezierSegment":
                    {
                        var points = ParsePoints(XmlUtil.Get(seg, "Points"));
                        for (int i = 0; i + 1 < points.Count; i += 2)
                        {
                            var ctrl = new Point(points[i].X, points[i].Y);
                            var end = new Point(points[i + 1].X, points[i + 1].Y);
                            var c1 = new Point(curr.X + (2.0 / 3.0) * (ctrl.X - curr.X), curr.Y + (2.0 / 3.0) * (ctrl.Y - curr.Y));
                            var c2 = new Point(end.X + (2.0 / 3.0) * (ctrl.X - end.X), end.Y + (2.0 / 3.0) * (ctrl.Y - end.Y));
                            figureSegments.Add(new FigureSegment(PathSegmentKind.Curve, [c1, c2, end], isStroked));
                            curr = end;
                        }
                        break;
                    }
                    case "PolyBezierSegment":
                    {
                        var points = ParsePoints(XmlUtil.Get(seg, "Points"));
                        for (int i = 0; i + 2 < points.Count; i += 3)
                        {
                            figureSegments.Add(new FigureSegment(PathSegmentKind.Curve,
                                [new Point(points[i].X, points[i].Y), new Point(points[i + 1].X, points[i + 1].Y), new Point(points[i + 2].X, points[i + 2].Y)], isStroked));
                            curr = new Point(points[i + 2].X, points[i + 2].Y);
                        }
                        break;
                    }
                }
            }
            figures.Add(new Figure(new Point(sx, sy), figureSegments, IsTrue(XmlUtil.Get(figure, "IsClosed")), !IsFalse(XmlUtil.Get(figure, "IsFilled"))));
        }
        return figures;
    }

    private static List<PathSegment> FigureToFillSegments(Figure figure, Matrix transform)
    {
        var segments = new List<PathSegment> { new(PathSegmentKind.Move, [ApplyPoint(transform, figure.Start)]) };
        foreach (var segment in figure.Segments)
            segments.Add(new PathSegment(segment.Kind, segment.Points.Select(p => ApplyPoint(transform, p)).ToArray()));
        if (figure.IsClosed)
            segments.Add(PathSegment.ClosePath());
        return segments;
    }

    private static List<PathSegment> FigureToStrokeSegments(Figure figure, Matrix transform)
    {
        var strokeSegments = new List<PathSegment>();
        var currentPoint = figure.Start;
        bool subpathOpen = false;
        foreach (var segment in figure.Segments)
        {
            var endPoint = segment.Points.Length > 0 ? segment.Points[^1] : currentPoint;
            if (segment.IsStroked)
            {
                if (!subpathOpen)
                {
                    strokeSegments.Add(new PathSegment(PathSegmentKind.Move, [ApplyPoint(transform, currentPoint)]));
                    subpathOpen = true;
                }
                strokeSegments.Add(new PathSegment(segment.Kind, segment.Points.Select(p => ApplyPoint(transform, p)).ToArray()));
            }
            else
            {
                subpathOpen = false;
            }
            currentPoint = endPoint;
        }
        if (figure.IsClosed && currentPoint != figure.Start)
        {
            if (!subpathOpen)
                strokeSegments.Add(new PathSegment(PathSegmentKind.Move, [ApplyPoint(transform, currentPoint)]));
            strokeSegments.Add(new PathSegment(PathSegmentKind.Line, [ApplyPoint(transform, figure.Start)]));
        }
        return strokeSegments;
    }

    // ---- part names --------------------------------------------------------------------------

    internal static string ResolvePart(string basePart, string target)
    {
        if (target.StartsWith('/'))
            return NormalizePartRef(target);
        int slash = basePart.LastIndexOf('/');
        string baseDir = slash >= 0 ? basePart[..slash] : basePart;
        if (baseDir.Length == 0)
            return NormalizePartRef(target);
        return NormalizePartRef(baseDir + "/" + target);
    }

    internal static string NormalizePartRef(string value)
    {
        int hash = value.IndexOf('#', StringComparison.Ordinal);
        if (hash >= 0)
            value = value[..hash];
        int query = value.IndexOf('?', StringComparison.Ordinal);
        if (query >= 0)
            value = value[..query];
        return XpsPackage.NormalizePart(value);
    }
}
