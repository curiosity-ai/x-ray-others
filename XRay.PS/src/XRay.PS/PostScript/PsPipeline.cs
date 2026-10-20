// Ported from ps/pipeline.py.

using System.Text.RegularExpressions;
using XRay.PS.Fonts;
using XRay.PS.Internal;
using XRay.PS.Rendering;

namespace XRay.PS.PostScript;

/// <summary>Converts PS/EPS byte streams into render documents.</summary>
public sealed class PsPipeline(PsInterpreter interpreter, OperatorRegistry operators, RenderModelBuilder builder,
    FontResolver? fontResolver = null, PsImageStore? imageStore = null)
{
    // Python \s in a bytes pattern is ASCII whitespace.
    private static readonly Regex Type1ResourceRe = new(
        "%%BeginResource:[ \\t\\n\\r\\f\\v]*font[ \\t\\n\\r\\f\\v]*\\([^\\r\\n)]*\\).*?%%EndResource",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    /// <summary>The product string published in systemdict.</summary>
    public const string ProductName = "XRay.PS";

    public OperatorRegistry Operators => operators;

    /// <summary>Parses PS/EPS bytes and returns the render document.</summary>
    public RenderDocument BuildRenderModel(byte[] data) => BuildRenderModel(data, null, false, null, default);

    /// <summary>Parses PS/EPS bytes and returns the render document, under resource limits.</summary>
    /// <param name="data">The job.</param>
    /// <param name="limits">Ceilings on work and memory; the defaults when null.</param>
    /// <param name="suppressErrors">On an error, abandon the current page, keep what was drawn
    /// and continue after the next <c>showpage</c>, instead of throwing.</param>
    /// <param name="warnings">Receives a warning for every suppressed error.</param>
    /// <param name="cancellationToken">Polled by the interpreter loop.</param>
    public RenderDocument BuildRenderModel(byte[] data, PsInterpreterLimits? limits, bool suppressErrors,
        List<ConversionWarning>? warnings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Deviation from the original: interpret the PostScript inside a DOS EPS binary file or a
        // PJL job rather than the wrapper's bytes. Any other input is passed through unchanged.
        data = PsInput.Unwrap(data);
        var ctx = CreateDefaultContext(fontResolver, imageStore);
        ctx.Limits = limits ?? PsInterpreterLimits.Default;
        ctx.SuppressErrors = suppressErrors;
        ctx.Warnings = warnings;
        ctx.CancellationToken = cancellationToken;
        RegisterEmbeddedType1Resources(data, ctx.FontResolver);
        byte[] processed = PreprocessType1Resources(data);
        var dsc = DscMetadata.Parse(data);
        ctx.Dsc = dsc;
        bool isEps = IsEpsDocument(data);
        (double Width, double Height) size;
        if (isEps || dsc.CropBox is not null || dsc.DocumentMediaSize is not null)
        {
            size = PageGeometry.PageSizeFromDsc(dsc);
        }
        else
        {
            // For non-EPS jobs %%BoundingBox often describes content extents, not the page
            // device size; keep the default A4 page unless an explicit size is provided.
            size = PageGeometry.PageSizeFromDsc(null);
        }
        ctx.DefaultPageSize = size;
        if (isEps)
        {
            var origin = BBoxOriginFromDsc(dsc);
            if (origin is { } o && (o.X != 0.0 || o.Y != 0.0))
                ctx.GraphicsStateStack.Peek().Ctm = new Matrix(1.0, 0.0, 0.0, 1.0, -o.X, -o.Y);
        }
        builder.SetDefaultPageSize(size.Width, size.Height);
        interpreter.Execute(processed, ctx);
        return builder.Document();
    }

    internal static bool IsEpsDocument(byte[] data)
    {
        if (data.Length == 0)
            return false;
        string head = Py.Latin1String(data.AsSpan(0, Math.Min(2048, data.Length)));
        var lines = Py.SplitLines(head);
        string firstLine = lines.Count > 0 ? lines[0] : "";
        return firstLine.StartsWith("%!PS-Adobe-", StringComparison.Ordinal) && firstLine.Contains("EPSF", StringComparison.Ordinal);
    }

    private static (double X, double Y)? BBoxOriginFromDsc(DscMetadata? dsc)
    {
        if (dsc is null || dsc.CropBox is not null || dsc.DocumentMediaSize is not null)
            return null;
        if (dsc.HiResBoundingBox is { } hires)
            return (hires.Llx, hires.Lly);
        if (dsc.BoundingBox is { } bbox)
            return (bbox.Llx, bbox.Lly);
        return null;
    }

    /// <summary>Strips embedded Type 1 resource blocks that rely on <c>currentfile eexec</c>.</summary>
    public static byte[] PreprocessType1Resources(byte[] data)
    {
        string text = Py.Latin1String(data);
        using var output = new MemoryStream(data.Length);
        int cursor = 0;
        foreach (Match match in Type1ResourceRe.Matches(text))
        {
            string block = match.Value;
            if (!block.Contains("/FontType 1", StringComparison.Ordinal) || !block.Contains("currentfile eexec", StringComparison.Ordinal))
                continue;
            output.Write(data, cursor, match.Index - cursor);
            output.WriteByte((byte)'\n');
            cursor = match.Index + match.Length;
        }
        if (cursor == 0)
            return data;
        output.Write(data, cursor, data.Length - cursor);
        return output.ToArray();
    }

    private static void RegisterEmbeddedType1Resources(byte[] data, FontResolver? resolver)
    {
        if (resolver is null)
            return;
        string text = Py.Latin1String(data);
        foreach (Match match in Type1ResourceRe.Matches(text))
        {
            var block = Py.Latin1.GetBytes(match.Value);
            var metrics = Type1Parser.ParseResourceBlock(block);
            if (metrics is null)
                continue;
            resolver.RegisterDefinedFont(metrics.FontName, new FontResource(metrics.FontName, "Type1", metrics.UnitsPerEm, metrics.Encoding,
                metrics.GlyphWidths, false, CodeWidths: metrics.CodeWidths, FontProgram: metrics.FontProgramType1));
        }
    }

    /// <summary>Creates the initial interpreter context: systemdict with the standard
    /// encodings, userdict, an initial graphics state.</summary>
    public static PsExecutionContext CreateDefaultContext(FontResolver? fontResolver = null, PsImageStore? imageStore = null)
    {
        var systemdict = new PsDict();
        var userdict = new PsDict();
        var globaldict = new PsDict();
        var items = systemdict.Items;
        items["true"] = true;
        items["false"] = false;
        items["null"] = null;
        items["languagelevel"] = 3L;
        items["userdict"] = userdict;
        items["globaldict"] = globaldict;
        items["systemdict"] = systemdict;
        items["statusdict"] = new PsDict();
        items["errordict"] = new PsDict();
        items["$error"] = new PsDict();
        items["FontDirectory"] = new PsDict();
        items["version"] = PsString.FromText("3017");
        items["product"] = PsString.FromText(ProductName);
        items["setglobal"] = new PsOperator("setglobal");
        items["currentglobal"] = new PsOperator("currentglobal");
        items["setuserparams"] = new PsOperator("setuserparams");
        items["setpacking"] = new PsOperator("setpacking");
        items["currentpacking"] = new PsOperator("currentpacking");
        items["StandardEncoding"] = BuildEncodingArray(Encodings.Standard);
        items["ISOLatin1Encoding"] = BuildEncodingArray(Encodings.IsoLatin1);
        items["ISOLatin1"] = BuildEncodingArray(Encodings.IsoLatin1);
        items["SymbolEncoding"] = BuildEncodingArray(Encodings.Symbol);
        items["ZapfDingbatsEncoding"] = BuildEncodingArray(Encodings.ZapfDingbats);
        var dictStack = new PsStack<PsDict>([systemdict, userdict]);
        return new PsExecutionContext(new PsStack<object?>(), new PsStack<object?>(), dictStack, new PsStack<GraphicsState>([new GraphicsState()]), userdict, systemdict)
        {
            ImageStore = imageStore ?? new PsImageStore(),
            FontResolver = fontResolver ?? new FontResolver(),
        };
    }

    private static PsArray BuildEncodingArray(IReadOnlyDictionary<int, string> mapping)
    {
        // The names are built once per encoding and shared: a PsName is an immutable value, while
        // the array holding them is a fresh one per document because a program may `put` into it.
        PsName[] names = EncodingNames.GetValue(mapping, static m =>
        {
            var built = new PsName[256];
            for (int i = 0; i < 256; i++)
                built[i] = new PsName(".notdef", true);
            foreach (var (code, name) in m)
            {
                if (code is >= 0 and < 256)
                    built[code] = new PsName(name, true);
            }
            return built;
        });
        return new PsArray(new List<object?>(names));
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyDictionary<int, string>, PsName[]> EncodingNames = new();
}
