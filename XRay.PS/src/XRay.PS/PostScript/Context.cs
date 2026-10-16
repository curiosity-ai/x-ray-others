// Ported from ps/context.py, ps/vm.py, ps/operators.py, ps/images.py, ps/dsc.py and
// ps/page_geometry.py.

using XRay.PS.Fonts;
using XRay.PS.Internal;
using XRay.PS.Rendering;

namespace XRay.PS.PostScript;

/// <summary>The current graphics state parameters.</summary>
public sealed class GraphicsState
{
    public Matrix Ctm { get; set; } = Matrix.Identity;
    public double LineWidth { get; set; } = 1.0;
    public int LineCap { get; set; }
    public int LineJoin { get; set; }
    public double MiterLimit { get; set; } = 10.0;
    public double[] Dash { get; set; } = [];
    public double DashPhase { get; set; }
    public double Flatness { get; set; } = 1.0;
    public RenderPath CurrentPath { get; set; } = new();
    public (double X, double Y)? CurrentPoint { get; set; }
    public (double X, double Y)? SubpathStart { get; set; }
    public RenderPath? ClipPath { get; set; }
    public Paint StrokePaint { get; set; } = Paint.Gray(0.0);
    public Paint FillPaint { get; set; } = Paint.Gray(0.0);
    public bool ImageInterpolate { get; set; }
    public Matrix TextMatrix { get; set; } = Matrix.Identity;
    public Matrix TextLineMatrix { get; set; } = Matrix.Identity;
    public FontResource? Font { get; set; }
    public double FontSize { get; set; }
    public double CharSpacing { get; set; }
    public double WordSpacing { get; set; }
    public double TextLeading { get; set; }
    public double TextRise { get; set; }
    public (double Wx, double Wy)? Type3CharWidth { get; set; }
    public (double, double, double, double)? Type3CacheBBox { get; set; }
    public ColorSpace CurrentColorSpace { get; set; } = new DeviceColorSpace("DeviceGray");
    public double[] CurrentColorComponents { get; set; } = [0.0];
    public PsPattern? CurrentPattern { get; set; }
    public bool Overprint { get; set; }

    /// <summary><c>setstrokeadjust</c>; like the original, not carried across gsave.</summary>
    public bool? StrokeAdjust { get; set; }

    public GraphicsState Clone() => new()
    {
        Ctm = Ctm,
        LineWidth = LineWidth,
        LineCap = LineCap,
        LineJoin = LineJoin,
        MiterLimit = MiterLimit,
        Dash = (double[])Dash.Clone(),
        DashPhase = DashPhase,
        Flatness = Flatness,
        CurrentPath = CurrentPath.Clone(),
        CurrentPoint = CurrentPoint,
        SubpathStart = SubpathStart,
        ClipPath = ClipPath?.Clone(),
        StrokePaint = StrokePaint,
        FillPaint = FillPaint,
        ImageInterpolate = ImageInterpolate,
        TextMatrix = TextMatrix,
        TextLineMatrix = TextLineMatrix,
        Font = Font,
        FontSize = FontSize,
        CharSpacing = CharSpacing,
        WordSpacing = WordSpacing,
        TextLeading = TextLeading,
        TextRise = TextRise,
        Type3CharWidth = Type3CharWidth,
        Type3CacheBBox = Type3CacheBBox,
        CurrentColorSpace = CurrentColorSpace,
        CurrentColorComponents = CurrentColorComponents,
        CurrentPattern = CurrentPattern,
        Overprint = Overprint,
    };
}

/// <summary>Interpreter stacks, dictionaries and metadata for one execution.</summary>
public sealed class PsExecutionContext
{
    public PsExecutionContext(PsStack<object?> operandStack, PsStack<object?> executionStack, PsStack<PsDict> dictionaryStack,
        PsStack<GraphicsState> graphicsStateStack, PsDict userdict, PsDict systemdict)
    {
        OperandStack = operandStack;
        ExecutionStack = executionStack;
        DictionaryStack = dictionaryStack;
        GraphicsStateStack = graphicsStateStack;
        Userdict = userdict;
        Systemdict = systemdict;
    }

    public PsStack<object?> OperandStack { get; set; }
    public PsStack<object?> ExecutionStack { get; set; }
    public PsStack<PsDict> DictionaryStack { get; set; }
    public PsStack<GraphicsState> GraphicsStateStack { get; set; }
    public PsDict Userdict { get; set; }
    public PsDict Systemdict { get; set; }
    public DscMetadata? Dsc { get; set; }
    public Action<PsError>? ErrorHandler { get; set; }
    public (double Width, double Height)? DefaultPageSize { get; set; }
    public PsImageStore? ImageStore { get; set; }
    public FontResolver? FontResolver { get; set; }
    public bool CharpathMode { get; set; }
    public bool InType3Glyph { get; set; }
    public Dictionary<string, string> TextFontOverrides { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Ceilings for this execution.</summary>
    public PsInterpreterLimits Limits { get; set; } = PsInterpreterLimits.Default;

    /// <summary>Observed cooperatively by the interpreter loop.</summary>
    public CancellationToken CancellationToken { get; set; }

    /// <summary>On a recoverable error stop the page being interpreted and keep what was drawn.</summary>
    public bool SuppressErrors { get; set; }

    /// <summary>Receives a warning for each error that was suppressed.</summary>
    public List<ConversionWarning>? Warnings { get; set; }

    /// <summary>Objects and procedure calls executed so far (see <see cref="PsInterpreterLimits.MaxOperators"/>).</summary>
    public long OperatorCount { get; set; }

    internal long HeapBaseline { get; set; } = -1;

    /// <summary>Per-context values the operators attach (screen, transfer, CMap state, ...).</summary>
    public Dictionary<string, object?> Extras { get; } = new(StringComparer.Ordinal);

    /// <summary>The current graphics state.</summary>
    public GraphicsState State => GraphicsStateStack.Peek();
}

/// <summary>The state captured by <c>save</c> (dictionary and graphics state stacks; operand and
/// execution stacks are not rolled back by <c>restore</c>).</summary>
public sealed class PsSaveState(PsStack<PsDict> dictionaryStack, PsStack<GraphicsState> graphicsStateStack)
{
    public PsStack<PsDict> DictionaryStack { get; } = dictionaryStack;
    public PsStack<GraphicsState> GraphicsStateStack { get; } = graphicsStateStack;

    public static PsSaveState Save(PsExecutionContext ctx) =>
        new(ctx.DictionaryStack.Clone(), new PsStack<GraphicsState>(ctx.GraphicsStateStack.Items.Select(s => s.Clone())));

    public void Restore(PsExecutionContext ctx)
    {
        ctx.DictionaryStack = DictionaryStack.Clone();
        ctx.GraphicsStateStack = GraphicsStateStack.Clone();
    }
}

/// <summary>A registered operator implementation.</summary>
public sealed record OperatorEntry(string Name, int MinOperands, int? MaxOperands, Action<PsExecutionContext> Fn);

/// <summary>Registers and resolves operator implementations by name.</summary>
public sealed class OperatorRegistry
{
    private readonly Dictionary<string, OperatorEntry> _operators = new(StringComparer.Ordinal);

    public void Register(string name, Action<PsExecutionContext> fn, int minOperands = 0, int? maxOperands = null) =>
        _operators[name] = new OperatorEntry(name, minOperands, maxOperands, fn);

    public OperatorEntry? Get(string name) => _operators.GetValueOrDefault(name);

    public IEnumerable<string> Names => _operators.Keys;
}

/// <summary>A decoded (or still encoded) image registered during interpretation.</summary>
public sealed record PsImageResource(
    string ImageId,
    byte[] Data,
    int Width,
    int Height,
    int BitsPerComponent,
    string ColorSpace,
    bool Interpolate,
    bool Mask,
    string? Filter,
    Dictionary<string, object?>? FilterParams,
    double[]? Decode = null,
    bool MaskPolarity = true);

/// <summary>Stores image resources by id (<c>img1</c>, <c>img2</c>, ...).</summary>
public sealed class PsImageStore
{
    private readonly Dictionary<string, PsImageResource> _images = new(StringComparer.Ordinal);
    private int _counter = 1;

    public string Register(PsImageResource image)
    {
        string imageId = string.IsNullOrEmpty(image.ImageId) ? "img" + _counter.ToString(System.Globalization.CultureInfo.InvariantCulture) : image.ImageId;
        _counter++;
        if (image.ImageId != imageId)
            image = image with { ImageId = imageId };
        _images[imageId] = image;
        return imageId;
    }

    public PsImageResource Get(string imageId) =>
        _images.TryGetValue(imageId, out var image) ? image : throw new PsUndefinedError($"unknown image resource {imageId}");

    public IEnumerable<KeyValuePair<string, PsImageResource>> Items => _images;
}

/// <summary>Document Structuring Convention comments of a PS/EPS file.</summary>
public sealed class DscMetadata
{
    public (int Llx, int Lly, int Urx, int Ury)? BoundingBox { get; set; }
    public (double Llx, double Lly, double Urx, double Ury)? HiResBoundingBox { get; set; }
    public (int Llx, int Lly, int Urx, int Ury)? CropBox { get; set; }
    public (double Width, double Height)? DocumentMediaSize { get; set; }
    public string? Orientation { get; set; }
    public string? PageTransform { get; set; }
    public string? ViewingOrientation { get; set; }
    public string? Title { get; set; }
    public string? Creator { get; set; }
    public string? CreationDate { get; set; }
    public int? LanguageLevel { get; set; }
    public List<string> Extensions { get; } = [];

    /// <summary>Parses DSC comments (<c>%%BoundingBox</c>, <c>%%Title</c>, ...).</summary>
    public static DscMetadata Parse(byte[] data)
    {
        string text = Py.Latin1String(data);
        var metadata = new DscMetadata();
        foreach (string raw in Py.SplitLines(text))
        {
            string line = Py.Strip(raw);
            if (!line.StartsWith("%%", StringComparison.Ordinal))
                continue;
            string Value() => Py.Strip(line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..]);
            string? OrNull(string v) => v.Length > 0 ? v : null;
            if (line.StartsWith("%%BoundingBox:", StringComparison.Ordinal))
            {
                string value = Value();
                if (value.ToLowerInvariant() == "atend")
                    continue;
                var bbox = ParseIntTuple(value);
                if (bbox is not null)
                    metadata.BoundingBox = bbox;
            }
            else if (line.StartsWith("%%HiResBoundingBox:", StringComparison.Ordinal))
            {
                string value = Value();
                if (value.ToLowerInvariant() == "atend")
                    continue;
                var bbox = ParseFloatTuple(value);
                if (bbox is not null)
                    metadata.HiResBoundingBox = bbox;
            }
            else if (line.StartsWith("%%CropBox:", StringComparison.Ordinal))
            {
                var crop = ParseIntTuple(Value());
                if (crop is not null)
                    metadata.CropBox = crop;
            }
            else if (line.StartsWith("%%DocumentMedia:", StringComparison.Ordinal))
            {
                var media = ParseDocumentMedia(Value());
                if (media is not null)
                    metadata.DocumentMediaSize = media;
            }
            else if (line.StartsWith("%%Orientation:", StringComparison.Ordinal))
                metadata.Orientation = OrNull(Value());
            else if (line.StartsWith("%%PageTransform:", StringComparison.Ordinal))
                metadata.PageTransform = OrNull(Value());
            else if (line.StartsWith("%%ViewingOrientation:", StringComparison.Ordinal))
                metadata.ViewingOrientation = OrNull(Value());
            else if (line.StartsWith("%%Title:", StringComparison.Ordinal))
                metadata.Title = OrNull(Value());
            else if (line.StartsWith("%%Creator:", StringComparison.Ordinal))
                metadata.Creator = OrNull(Value());
            else if (line.StartsWith("%%CreationDate:", StringComparison.Ordinal))
                metadata.CreationDate = OrNull(Value());
            else if (line.StartsWith("%%LanguageLevel:", StringComparison.Ordinal))
            {
                if (Py.TryInt(Value(), 10, out long level))
                    metadata.LanguageLevel = (int)level;
            }
            else if (line.StartsWith("%%Extensions:", StringComparison.Ordinal))
            {
                string value = Value();
                if (value.Length > 0)
                    metadata.Extensions.AddRange(Py.Split(value));
            }
        }
        return metadata;
    }

    private static (int, int, int, int)? ParseIntTuple(string value)
    {
        var parts = Py.Split(Py.Strip(value));
        if (parts.Count != 4)
            return null;
        var ints = new int[4];
        for (int i = 0; i < 4; i++)
        {
            if (!Py.TryInt(parts[i], 10, out long v))
                return null;
            ints[i] = (int)v;
        }
        return (ints[0], ints[1], ints[2], ints[3]);
    }

    private static (double, double, double, double)? ParseFloatTuple(string value)
    {
        var parts = Py.Split(Py.Strip(value));
        if (parts.Count != 4)
            return null;
        var vals = new double[4];
        for (int i = 0; i < 4; i++)
        {
            if (!Py.TryFloat(parts[i], out vals[i]))
                return null;
        }
        return (vals[0], vals[1], vals[2], vals[3]);
    }

    private static (double, double)? ParseDocumentMedia(string value)
    {
        var parts = Py.Split(Py.Strip(value));
        if (parts.Count >= 3 && Py.TryFloat(parts[1], out double width) && Py.TryFloat(parts[2], out double height))
        {
            if (width > 0 && height > 0)
                return (width, height);
        }
        var numeric = new List<double>();
        foreach (string part in parts)
        {
            if (Py.TryFloat(part, out double v))
                numeric.Add(v);
        }
        if (numeric.Count >= 2 && numeric[0] > 0 && numeric[1] > 0)
            return (numeric[0], numeric[1]);
        return null;
    }
}

/// <summary>Page size derivation from DSC metadata.</summary>
public static class PageGeometry
{
    /// <summary>The default page size (A4 in points).</summary>
    public static readonly (double Width, double Height) DefaultPageSize = (595.0, 842.0);

    public static (double Width, double Height) PageSizeFromDsc(DscMetadata? dsc)
    {
        if (dsc is null)
            return DefaultPageSize;
        double width, height;
        if (dsc.CropBox is { } crop)
        {
            width = crop.Urx - crop.Llx;
            height = crop.Ury - crop.Lly;
        }
        else if (dsc.DocumentMediaSize is { } media)
        {
            (width, height) = media;
        }
        else if (dsc.HiResBoundingBox is { } hires)
        {
            width = hires.Urx - hires.Llx;
            height = hires.Ury - hires.Lly;
        }
        else if (dsc.BoundingBox is { } bbox)
        {
            width = bbox.Urx - bbox.Llx;
            height = bbox.Ury - bbox.Lly;
        }
        else
        {
            return DefaultPageSize;
        }
        if (width <= 0 || height <= 0)
            return DefaultPageSize;
        string orientation = Py.Strip(dsc.Orientation ?? "").ToLowerInvariant();
        return orientation == "landscape" ? (height, width) : (width, height);
    }
}
