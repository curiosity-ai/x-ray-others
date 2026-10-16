// Ported from common/render_model.py: the render model shared by every input (PostScript, XPS,
// programmatic) and every output (PDF, raster).

namespace XRay.PS.Rendering;

/// <summary>A 2D point in the render model (PostScript user space, y up).</summary>
public readonly record struct Point(double X, double Y);

/// <summary>An affine transform (a, b, c, d, e, f) as in PostScript and PDF.</summary>
public readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
{
    public static Matrix Identity => new(1.0, 0.0, 0.0, 1.0, 0.0, 0.0);

    /// <summary>Applies the transform to a point.</summary>
    public (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);

    /// <summary>Returns <c>left * right</c> in the row-vector convention used by PostScript
    /// (apply <paramref name="right"/> first, then <paramref name="left"/>).</summary>
    public static Matrix Multiply(Matrix left, Matrix right) => new(
        left.A * right.A + left.C * right.B,
        left.B * right.A + left.D * right.B,
        left.A * right.C + left.C * right.D,
        left.B * right.C + left.D * right.D,
        left.A * right.E + left.C * right.F + left.E,
        left.B * right.E + left.D * right.F + left.F);

    public double[] ToArray() => [A, B, C, D, E, F];

    public static Matrix FromArray(IReadOnlyList<double> v) => new(v[0], v[1], v[2], v[3], v[4], v[5]);
}

/// <summary>An axis-aligned rectangle with min/max coordinates.</summary>
public readonly record struct Rect(double XMin, double YMin, double XMax, double YMax);

/// <summary>The kind of a path segment.</summary>
public enum PathSegmentKind
{
    Move,
    Line,
    Curve,
    Close,
}

/// <summary>A path segment: a kind and its control points (one for move/line, three for curve,
/// none for close).</summary>
public sealed class PathSegment(PathSegmentKind kind, Point[] points)
{
    public PathSegmentKind Kind { get; } = kind;
    public Point[] Points { get; } = points;

    public static PathSegment MoveTo(double x, double y) => new(PathSegmentKind.Move, [new Point(x, y)]);
    public static PathSegment LineTo(double x, double y) => new(PathSegmentKind.Line, [new Point(x, y)]);
    public static PathSegment CurveTo(double x1, double y1, double x2, double y2, double x3, double y3) =>
        new(PathSegmentKind.Curve, [new Point(x1, y1), new Point(x2, y2), new Point(x3, y3)]);
    public static PathSegment ClosePath() => new(PathSegmentKind.Close, []);
}

/// <summary>A sequence of path segments.</summary>
public sealed class RenderPath
{
    public RenderPath() => Segments = [];
    public RenderPath(List<PathSegment> segments) => Segments = segments;
    public RenderPath(IEnumerable<PathSegment> segments) => Segments = [.. segments];

    public List<PathSegment> Segments { get; set; }

    public RenderPath Clone() => new(Segments.Select(s => new PathSegment(s.Kind, (Point[])s.Points.Clone())).ToList());
}

/// <summary>Stroke style settings for path rendering.</summary>
public sealed record StrokeStyle(double LineWidth, int LineCap, int LineJoin, double MiterLimit, double[] Dash, double DashPhase);

/// <summary>A fill or stroke paint: a color space kind and its value.</summary>
/// <remarks>
/// <see cref="Value"/> is a <see cref="double"/> for <c>DeviceGray</c>, a <see cref="double"/> array
/// for <c>DeviceRGB</c>/<c>DeviceCMYK</c>, a <see cref="ColorSpacePaint"/> for <c>ColorSpace</c>,
/// a <see cref="PatternPaint"/> for <c>Pattern</c>, and null for <c>PatternBase</c>.
/// </remarks>
public sealed record Paint(string Kind, object? Value)
{
    public static Paint Gray(double gray) => new("DeviceGray", gray);
    public static Paint Rgb(double r, double g, double b) => new("DeviceRGB", new[] { r, g, b });
    public static Paint Cmyk(double c, double m, double y, double k) => new("DeviceCMYK", new[] { c, m, y, k });

    /// <summary>The value as a list of components (a single gray value becomes one component).</summary>
    public double[]? Components => Value switch
    {
        double d => [d],
        double[] a => a,
        _ => null,
    };

    public bool Equals(Paint? other) =>
        other is not null && Kind == other.Kind && ValueEquality.ObjectEquals(Value, other.Value);

    public override int GetHashCode() => Kind.GetHashCode(StringComparison.Ordinal);
}

/// <summary>Base type for render commands.</summary>
public abstract record RenderCommand;

/// <summary>Render a path with an optional stroke and fill.</summary>
public sealed record PathCommand(
    RenderPath Path,
    StrokeStyle? Stroke,
    Paint? Fill,
    string FillRule = "nonzero",
    Paint? StrokePaint = null,
    bool Overprint = false,
    double FillOpacity = 1.0,
    double StrokeOpacity = 1.0) : RenderCommand;

/// <summary>Render text using a font reference and a text-space-to-user-space transform.</summary>
public sealed record TextCommand(
    string Text,
    string FontRef,
    double FontSize,
    Matrix Matrix,
    Paint? Fill,
    double FillOpacity = 1.0,
    int? GlyphId = null,
    double[]? PdfTextAdjustments = null) : RenderCommand;

/// <summary>Render an image resource through a unit-square-to-user-space transform.</summary>
public sealed record ImageCommand(
    string ImageId,
    int Width,
    int Height,
    Matrix Matrix,
    bool Mask = false,
    Paint? MaskPaint = null,
    double Opacity = 1.0) : RenderCommand;

/// <summary>Intersect the clipping path with a path.</summary>
public sealed record ClipCommand(RenderPath Path, string FillRule = "nonzero") : RenderCommand;

/// <summary>Save the current graphics state.</summary>
public sealed record StateSaveCommand : RenderCommand
{
    public static readonly StateSaveCommand Instance = new();
}

/// <summary>Restore the previous graphics state.</summary>
public sealed record StateRestoreCommand : RenderCommand
{
    public static readonly StateRestoreCommand Instance = new();
}

/// <summary>A single renderable page with its commands, sized in points.</summary>
public sealed class RenderPage(double width, double height, List<RenderCommand>? commands = null)
{
    public double Width { get; set; } = width;
    public double Height { get; set; } = height;
    public List<RenderCommand> Commands { get; set; } = commands ?? [];
}

/// <summary>A collection of render pages with their shared resources.</summary>
public sealed class RenderDocument
{
    public List<RenderPage> Pages { get; set; } = [];
    public RenderResources Resources { get; set; } = new();
}

/// <summary>Shared render resources for a document, keyed by resource id.</summary>
public sealed class RenderResources
{
    public Dictionary<string, ColorSpace> ColorSpaces { get; } = [];
    public Dictionary<string, Pattern> Patterns { get; } = [];
    public Dictionary<string, Function> Functions { get; } = [];
    public Dictionary<string, RenderImageResource> Images { get; } = [];
}

/// <summary>Image payload used by the PDF and raster backends.</summary>
public sealed record RenderImageResource(
    byte[] Data,
    int Width,
    int Height,
    string ColorSpace,
    int BitsPerComponent,
    string? Filter = null,
    IReadOnlyDictionary<string, object?>? FilterParams = null,
    double[]? Decode = null,
    bool Mask = false,
    bool MaskPolarity = true,
    byte[]? SoftMask = null);

/// <summary>Path helpers.</summary>
public static class Paths
{
    /// <summary>Creates a closed rectangular path.</summary>
    public static RenderPath FromRect(Rect rect) => new(new List<PathSegment>
    {
        PathSegment.MoveTo(rect.XMin, rect.YMin),
        PathSegment.LineTo(rect.XMax, rect.YMin),
        PathSegment.LineTo(rect.XMax, rect.YMax),
        PathSegment.LineTo(rect.XMin, rect.YMax),
        PathSegment.ClosePath(),
    });
}

/// <summary>Builds render documents incrementally.</summary>
public sealed class RenderModelBuilder
{
    private readonly RenderDocument _document = new();
    private (double Width, double Height)? _defaultPageSize;
    private RenderPage? _activePage;
    private readonly ResourceRegistry<ColorSpace> _colorSpaces;
    private readonly ResourceRegistry<Pattern> _patterns;
    private readonly ResourceRegistry<Function> _functions;

    public RenderModelBuilder()
    {
        _colorSpaces = new ResourceRegistry<ColorSpace>(_document.Resources.ColorSpaces, "CS");
        _patterns = new ResourceRegistry<Pattern>(_document.Resources.Patterns, "P");
        _functions = new ResourceRegistry<Function>(_document.Resources.Functions, "F");
    }

    /// <summary>The page currently being built, if any.</summary>
    public RenderPage? ActivePage => _activePage;

    /// <summary>The document under construction (pages ended so far).</summary>
    public RenderDocument PendingDocument => _document;

    /// <summary>Sets the page size used when a command arrives with no page open.</summary>
    public void SetDefaultPageSize(double width, double height) => _defaultPageSize = (width, height);

    public (double Width, double Height)? DefaultPageSize => _defaultPageSize;

    public void BeginPage(double width, double height)
    {
        if (_activePage is not null)
            throw new InvalidOperationException("page already active");
        _activePage = new RenderPage(width, height);
    }

    public void EndPage()
    {
        if (_activePage is null)
            throw new InvalidOperationException("no active page");
        _document.Pages.Add(_activePage);
        _activePage = null;
    }

    public void AddPath(RenderPath path, StrokeStyle? stroke, Paint? fill, string fillRule = "nonzero", Paint? strokePaint = null,
        bool overprint = false, double fillOpacity = 1.0, double strokeOpacity = 1.0)
    {
        EnsurePage().Commands.Add(new PathCommand(path, stroke, fill, fillRule, strokePaint, overprint, fillOpacity, strokeOpacity));
    }

    public void AddText(string text, string fontRef, double fontSize, Matrix matrix, Paint? fill, double fillOpacity = 1.0,
        int? glyphId = null, double[]? pdfTextAdjustments = null)
    {
        EnsurePage().Commands.Add(new TextCommand(text, fontRef, fontSize, matrix, fill, fillOpacity, glyphId, pdfTextAdjustments));
    }

    public void AddImage(string imageId, int width, int height, Matrix matrix, bool mask = false, Paint? maskPaint = null)
    {
        EnsurePage().Commands.Add(new ImageCommand(imageId, width, height, matrix, mask, maskPaint));
    }

    /// <summary>Appends an already-built command to the current page.</summary>
    public void AddCommand(RenderCommand command) => EnsurePage().Commands.Add(command);

    public void Clip(RenderPath path, string fillRule = "nonzero") => EnsurePage().Commands.Add(new ClipCommand(path, fillRule));

    public void SaveState() => EnsurePage().Commands.Add(StateSaveCommand.Instance);

    public void RestoreState() => EnsurePage().Commands.Add(StateRestoreCommand.Instance);

    /// <summary>Returns the document, closing the active page if one is open.</summary>
    public RenderDocument Document()
    {
        if (_activePage is not null)
        {
            _document.Pages.Add(_activePage);
            _activePage = null;
        }
        return _document;
    }

    public string RegisterColorSpace(ColorSpace colorSpace) => _colorSpaces.Register(colorSpace);

    public string RegisterPattern(Pattern pattern) => _patterns.Register(pattern);

    public string RegisterFunction(Function function) => _functions.Register(function);

    private RenderPage EnsurePage()
    {
        if (_activePage is not null)
            return _activePage;
        if (_defaultPageSize is null)
            throw new InvalidOperationException("default page size not set");
        BeginPage(_defaultPageSize.Value.Width, _defaultPageSize.Value.Height);
        return _activePage!;
    }

    /// <summary>Registers a resource, reusing the id of a structurally equal one (as the Python
    /// <c>_register_resource</c> does with <c>==</c>).</summary>
    private sealed class ResourceRegistry<T>(Dictionary<string, T> mapping, string prefix) where T : class
    {
        private readonly Dictionary<string, (T Value, string Key)> _keys = new(StringComparer.Ordinal);

        public string Register(T value)
        {
            string? structural = null;
            foreach (var (id, existing) in mapping)
            {
                if (ReferenceEquals(existing, value))
                    return id;
                if (!_keys.TryGetValue(id, out var cached) || !ReferenceEquals(cached.Value, existing))
                {
                    cached = (existing, StructuralKey.Of(existing));
                    _keys[id] = cached;
                }
                structural ??= StructuralKey.Of(value);
                if (cached.Key == structural)
                    return id;
            }
            string resourceId = prefix + (mapping.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
            mapping[resourceId] = value;
            return resourceId;
        }
    }
}
