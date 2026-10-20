// Ported from ps/color_spaces.py, ps/functions.py, ps/patterns.py and ps/color_ops.py.

using XRay.PS.Fonts;
using XRay.PS.Internal;
using XRay.PS.Rendering;

namespace XRay.PS.PostScript;

/// <summary>Parses PostScript color space objects into render-model color spaces.</summary>
public static class ColorSpaces
{
    public static ColorSpace Parse(object? obj, RenderModelBuilder builder)
    {
        // Deviation: a parsed space handed back (currentcolorspace setcolorspace) is itself.
        if (obj is ColorSpace parsed)
            return parsed;
        if (obj is PsName name)
            return DeviceSpace(name.Value);
        if (obj is string s)
            return DeviceSpace(s);
        if (obj is not PsArray array)
            throw new PsTypeError("color space must be name or array");
        if (array.Items.Count == 0)
            throw new PsRangeError("empty color space array");
        string tagName = array.Items[0] switch
        {
            PsName n => n.Value,
            string str => str,
            _ => throw new PsTypeError("invalid color space tag"),
        };
        if (tagName == "Indexed")
            return ParseIndexed(array, builder);
        if (tagName == "Separation")
            return ParseSeparation(array, builder);
        if (tagName == "DeviceN")
            return ParseDeviceN(array, builder);
        if (tagName.StartsWith("CIEBased", StringComparison.Ordinal))
            return ParseCie(array);
        if (tagName == "Pattern")
            return new PatternColorSpace(array.Items.Count > 1 ? Parse(array.Items[1], builder) : null);
        return DeviceSpace(tagName);
    }

    /// <summary>The bundled sRGB profile used when a CIE-based space carries none.</summary>
    public static byte[] LoadDefaultIccProfile() => BundledResources.Read("sRGB_IEC61966-2-1_black_scaled.icc");

    private static ColorSpace DeviceSpace(string name)
    {
        if (name is "DeviceRGB" or "DeviceCMYK" or "DeviceGray")
            return new DeviceColorSpace(name);
        throw new PsTypeError($"unsupported device color space {name}");
    }

    private static ColorSpace ParseIndexed(PsArray array, RenderModelBuilder builder)
    {
        if (array.Items.Count < 4)
            throw new PsRangeError("Indexed color space requires base, hival, lookup");
        var b = Parse(array.Items[1], builder);
        var hival = array.Items[2];
        if (!PsOps.IsNumeric(hival))
            throw new PsTypeError("Indexed hival must be numeric");
        byte[] data;
        switch (array.Items[3])
        {
            case PsString ps:
                data = ps.Value;
                break;
            case byte[] bytes:
                data = bytes;
                break;
            case PsArray lookupArray:
                data = new byte[lookupArray.Items.Count];
                for (int i = 0; i < data.Length; i++)
                {
                    var item = lookupArray.Items[i];
                    if (!PsOps.IsNumeric(item))
                        throw new PsTypeError("Indexed lookup array must be numeric");
                    data[i] = (byte)(Py.Int(PsOps.ToDouble(item)) & 0xFF);
                }
                break;
            default:
                throw new PsTypeError("Indexed lookup must be string or array");
        }
        return new IndexedColorSpace(b, (int)Py.Int(PsOps.ToDouble(hival)), data);
    }

    private static ColorSpace ParseSeparation(PsArray array, RenderModelBuilder builder)
    {
        if (array.Items.Count < 4)
            throw new PsRangeError("Separation color space requires name, alternate, tint");
        string name = CoerceColorantName(array.Items[1]);
        var alternate = Parse(array.Items[2], builder);
        var tint = PsFunctions.Parse(array.Items[3], builder, 1, alternate);
        return new SeparationColorSpace(name, alternate, tint);
    }

    private static ColorSpace ParseDeviceN(PsArray array, RenderModelBuilder builder)
    {
        if (array.Items.Count < 4)
            throw new PsRangeError("DeviceN color space requires names, alternate, tint");
        if (array.Items[1] is not PsArray namesValue)
            throw new PsTypeError("DeviceN names must be array");
        var names = namesValue.Items.Select(CoerceColorantName).ToList();
        var alternate = Parse(array.Items[2], builder);
        var tint = PsFunctions.Parse(array.Items[3], builder, names.Count, alternate);
        return new DeviceNColorSpace(names, alternate, tint);
    }

    private static ColorSpace ParseCie(PsArray array)
    {
        if (array.Items.Count < 2)
            throw new PsRangeError("CIEBased color space requires dictionary");
        if (array.Items[1] is not PsDict data)
            throw new PsTypeError("CIEBased requires dictionary");
        int components = 3;
        var nValue = data.Get("N");
        if (PsOps.IsNumeric(nValue))
            components = (int)Py.Int(PsOps.ToDouble(nValue));
        double[]? ranges = null;
        var rangesValue = Or(data.Get("RangeABC"), data.Get("RangeLMN"));
        if (rangesValue is PsArray rangeArray)
        {
            var parsed = rangeArray.Items.Where(PsOps.IsNumeric).Select(PsOps.ToDouble).ToList();
            if (parsed.Count >= 2)
                ranges = [.. parsed];
        }
        var icc = Or(Or(data.Get("ICCProfile"), data.Get("Profile")), data.Get("DataSource"));
        byte[] profile = icc switch
        {
            PsString ps => ps.Value,
            byte[] bytes => bytes,
            _ => LoadDefaultIccProfile(),
        };
        return new CieBasedColorSpace(profile, components, ranges);
    }

    /// <summary>Python's <c>a or b</c>.</summary>
    internal static object? Or(object? a, object? b) => PsOps.Truthy(a) ? a : b;

    private static string CoerceColorantName(object? value) => value switch
    {
        PsName n => n.Value,
        string s => s,
        PsString ps => ps.Text,
        byte[] b => Py.Latin1String(b),
        _ => throw new PsTypeError("Separation/DeviceN colorant name must be name or string"),
    };
}

/// <summary>Parses PostScript function dictionaries and tint procedures.</summary>
public static class PsFunctions
{
    public static Function Parse(object? obj, RenderModelBuilder builder) => Parse(obj, builder, 1, null);

    /// <summary>Parses a tint transform taking <paramref name="inputs"/> inputs (a DeviceN tint
    /// transform takes one per colorant) into <paramref name="alternate"/>.</summary>
    public static Function Parse(object? obj, RenderModelBuilder builder, int inputs, ColorSpace? alternate)
    {
        Function function;
        if (obj is PsProcedure proc)
        {
            function = ParseProcedureTint(proc, inputs, alternate);
            builder.RegisterFunction(function);
            return function;
        }
        if (obj is not PsDict dict)
            throw new PsTypeError("function dictionary expected");
        var ftypeValue = dict.Get("FunctionType");
        if (!PsOps.IsNumeric(ftypeValue))
            throw new PsTypeError("FunctionType must be numeric");
        long ftype = Py.Int(PsOps.ToDouble(ftypeValue));
        function = ftype switch
        {
            0 => ParseSampled(dict),
            2 => ParseExponential(dict),
            3 => ParseStitching(dict, builder),
            _ => throw new PsRangeError($"unsupported FunctionType {ftype}"),
        };
        builder.RegisterFunction(function);
        return function;
    }

    // The interpreter that runs a tint procedure the evaluator below cannot (see UseInterpreter).
    [ThreadStatic]
    private static (PsExecutionContext Ctx, PsInterpreter Interpreter)? t_interpreter;

    /// <summary>Lets <see cref="Parse(object?, RenderModelBuilder, int, ColorSpace?)"/> run tint procedures with <paramref name="interpreter"/>
    /// for the extent of the returned scope.</summary>
    internal static IDisposable UseInterpreter(PsExecutionContext ctx, PsInterpreter interpreter)
    {
        var previous = t_interpreter;
        t_interpreter = (ctx, interpreter);
        return new Restore(previous);
    }

    private sealed class Restore((PsExecutionContext, PsInterpreter)? previous) : IDisposable
    {
        public void Dispose() => t_interpreter = previous;
    }

    private static ExponentialFunction ParseProcedureTint(PsProcedure proc, int inputs, ColorSpace? alternate)
    {
        // PostScript tint transforms in Separation/DeviceN are often simple procedures
        // (eg `{ 0 exch 0 0 }`), not PDF function dictionaries.
        List<double> c0, c1;
        try
        {
            c0 = EvalTintProcedure(proc, 0.0);
            c1 = EvalTintProcedure(proc, 1.0);
        }
        catch (Exception ex) when (ex is PsTypeError or HostErrorException && t_interpreter is not null)
        {
            // Deviation from the original, which gave up on a procedure using anything but
            // arithmetic and stack operators (a bound procedure, a conditional, a lookup): run
            // it with the interpreter instead, sampled at the same two points.
            try
            {
                c0 = RunTintProcedure(proc, 0.0, inputs);
                c1 = RunTintProcedure(proc, 1.0, inputs);
            }
            catch (Exception failed) when (failed is PsError or HostErrorException && failed is not PsResourceLimitError
                && InkRamp(alternate) is { } ramp)
            {
                // PostScript runs the procedure only when the colour is painted, so a probe that
                // sets a space it never paints with must not fail here: no ink to full ink.
                (c0, c1) = ramp;
            }
        }
        if (c0.Count == 0 || c0.Count != c1.Count)
            throw new PsTypeError("unsupported tint transform procedure");
        var range = new List<double>();
        for (int i = 0; i < c0.Count; i++)
        {
            range.Add(PyMin3(c0[i], c1[i], 0.0));
            range.Add(PyMax3(c0[i], c1[i], 1.0));
        }
        return new ExponentialFunction([0.0, 1.0], [.. range], [.. c0], [.. c1], 1.0);
    }

    private static (List<double>, List<double>)? InkRamp(ColorSpace? alternate) => alternate switch
    {
        DeviceColorSpace { Name: "DeviceGray" } => ([1.0], [0.0]),
        DeviceColorSpace { Name: "DeviceRGB" } => ([1.0, 1.0, 1.0], [0.0, 0.0, 0.0]),
        DeviceColorSpace { Name: "DeviceCMYK" } => ([0.0, 0.0, 0.0, 0.0], [0.0, 0.0, 0.0, 1.0]),
        _ => null,
    };

    private static List<double> RunTintProcedure(PsProcedure proc, double tint, int inputs)
    {
        var (ctx, interpreter) = t_interpreter!.Value;
        var scratch = new PsExecutionContext(new PsStack<object?>(), new PsStack<object?>(), ctx.DictionaryStack.Clone(),
            new PsStack<GraphicsState>([ctx.State.Clone()]), ctx.Userdict, ctx.Systemdict)
        {
            Dsc = ctx.Dsc,
            DefaultPageSize = ctx.DefaultPageSize,
            ImageStore = ctx.ImageStore,
            FontResolver = ctx.FontResolver,
            CharpathMode = true,
            Limits = ctx.Limits,
            CancellationToken = ctx.CancellationToken,
        };
        for (int i = 0; i < Math.Max(1, inputs); i++)
            scratch.OperandStack.Push(tint);
        interpreter.ExecuteProcedure(proc, scratch);
        var result = new List<double>();
        foreach (var item in scratch.OperandStack.Items)
        {
            if (!PsOps.IsNumeric(item))
                throw new PsTypeError("unsupported tint transform procedure");
            result.Add(PsOps.ToDouble(item));
        }
        return result;
    }

    private static double PyMin3(double a, double b, double c) => GraphicsOperators.PyMin(GraphicsOperators.PyMin(a, b), c);

    private static double PyMax3(double a, double b, double c) => GraphicsOperators.PyMax(GraphicsOperators.PyMax(a, b), c);

    private static List<double> EvalTintProcedure(PsProcedure proc, double tint)
    {
        var stack = new List<double> { tint };
        foreach (var item in proc.Items)
        {
            if (PsOps.IsNumeric(item))
            {
                stack.Add(PsOps.ToDouble(item));
                continue;
            }
            if (item is PsName name)
            {
                if (name.Literal)
                    throw new PsTypeError("unsupported literal name in tint procedure");
                EvalTintOperator(name.Value, stack);
                continue;
            }
            throw new PsTypeError("unsupported tint transform procedure");
        }
        return stack;
    }

    private static void EvalTintOperator(string name, List<double> stack)
    {
        double Pop()
        {
            if (stack.Count == 0)
                throw Py.IndexError("pop from empty list");
            double v = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            return v;
        }
        switch (name)
        {
            case "add":
            {
                double b = Pop(), a = Pop();
                stack.Add(a + b);
                return;
            }
            case "sub":
            {
                double b = Pop(), a = Pop();
                stack.Add(a - b);
                return;
            }
            case "mul":
            {
                double b = Pop(), a = Pop();
                stack.Add(a * b);
                return;
            }
            case "div":
            {
                double b = Pop(), a = Pop();
                stack.Add(Py.Div(a, b));
                return;
            }
            case "neg":
                stack.Add(-Pop());
                return;
            case "abs":
                stack.Add(Math.Abs(Pop()));
                return;
            case "dup":
                if (stack.Count == 0)
                    throw Py.IndexError();
                stack.Add(stack[^1]);
                return;
            case "exch":
                if (stack.Count < 2)
                    throw Py.IndexError();
                (stack[^1], stack[^2]) = (stack[^2], stack[^1]);
                return;
            case "pop":
                Pop();
                return;
        }
        throw new PsTypeError($"unsupported tint procedure operator: {name}");
    }

    private static SampledFunction ParseSampled(PsDict data)
    {
        var domain = RequireNumberList(data, "Domain");
        var range = RequireNumberList(data, "Range");
        var size = RequireNumberList(data, "Size").Select(v => (int)Py.Int(v)).ToArray();
        int bits = RequireInt(data, "BitsPerSample");
        var orderValue = data.Items.TryGetValue("Order", out var ov) ? ov : 1L;
        int order = orderValue switch
        {
            long l => (int)l,
            double d => (int)Py.Int(d),
            bool b => b ? 1 : 0,
            _ => throw Py.TypeError("int() argument must be a string, a bytes-like object or a real number"),
        };
        var encode = OptionalNumberList(data, "Encode") is { Count: > 0 } e ? e : DefaultEncode(domain);
        var decode = OptionalNumberList(data, "Decode") is { Count: > 0 } d2 ? d2 : [.. range];
        byte[] samples = data.Get("DataSource") switch
        {
            PsString ps => ps.Value,
            byte[] b => b,
            _ => throw new PsTypeError("DataSource must be string or bytes"),
        };
        return new SampledFunction([.. domain], [.. range], size, bits, order, [.. encode], [.. decode], samples);
    }

    private static ExponentialFunction ParseExponential(PsDict data)
    {
        var domain = RequireNumberList(data, "Domain");
        // Deviation from the original, which required it: Range is optional for a type 2
        // function; without it each output is taken as 0..1, as C0 and C1 imply.
        var range = data.Get("Range") is null ? DefaultRange(OptionalNumberList(data, "C0")?.Count ?? OptionalNumberList(data, "C1")?.Count ?? 1)
            : RequireNumberList(data, "Range");
        var c0 = OptionalNumberList(data, "C0") is { Count: > 0 } a ? a : Enumerable.Repeat(0.0, range.Count / 2).ToList();
        var c1 = OptionalNumberList(data, "C1") is { Count: > 0 } b ? b : Enumerable.Repeat(1.0, range.Count / 2).ToList();
        var n = data.Get("N");
        if (!PsOps.IsNumeric(n))
            throw new PsTypeError("N must be numeric");
        return new ExponentialFunction([.. domain], [.. range], [.. c0], [.. c1], PsOps.ToDouble(n));
    }

    private static StitchingFunction ParseStitching(PsDict data, RenderModelBuilder builder)
    {
        var domain = RequireNumberList(data, "Domain");
        if (data.Get("Functions") is not PsArray functionsObj)
            throw new PsTypeError("Functions must be array");
        var functions = functionsObj.Items.Select(item => Parse(item, builder)).ToList();
        // Deviation (see ParseExponential): an absent Range is the first sub-function's.
        var range = data.Get("Range") is null ? FunctionRange(functions.Count > 0 ? functions[0] : null) : RequireNumberList(data, "Range");
        var bounds = RequireNumberList(data, "Bounds");
        var encode = RequireNumberList(data, "Encode");
        return new StitchingFunction([.. domain], [.. range], functions, [.. bounds], [.. encode]);
    }

    private static List<double> DefaultRange(int outputs) =>
        Enumerable.Range(0, Math.Max(1, outputs) * 2).Select(i => i % 2 == 0 ? 0.0 : 1.0).ToList();

    private static List<double> FunctionRange(Function? function) => function switch
    {
        ExponentialFunction e => [.. e.Range],
        StitchingFunction st => [.. st.Range],
        SampledFunction sa => [.. sa.Range],
        _ => DefaultRange(1),
    };

    internal static List<double> RequireNumberList(PsDict data, string key)
    {
        if (data.Get(key) is not PsArray value)
            throw new PsTypeError($"{key} must be array");
        return NumberItems(value, key);
    }

    private static List<double> NumberItems(PsArray value, string key)
    {
        var result = new List<double>(value.Items.Count);
        foreach (var item in value.Items)
        {
            if (!PsOps.IsNumeric(item))
                throw new PsTypeError($"{key} must be numeric array");
            result.Add(PsOps.ToDouble(item));
        }
        return result;
    }

    private static List<double>? OptionalNumberList(PsDict data, string key)
    {
        var value = data.Get(key);
        if (value is null)
            return null;
        if (value is not PsArray array)
            throw new PsTypeError($"{key} must be array");
        return NumberItems(array, key);
    }

    private static int RequireInt(PsDict data, string key)
    {
        var value = data.Get(key);
        if (!PsOps.IsNumeric(value))
            throw new PsTypeError($"{key} must be numeric");
        return (int)Py.Int(PsOps.ToDouble(value));
    }

    private static List<double> DefaultEncode(List<double> domain)
    {
        var encode = new List<double>();
        for (int i = 0; i < domain.Count; i += 2)
        {
            encode.Add(domain[i]);
            if (i + 1 >= domain.Count)
                throw Py.IndexError();
            encode.Add(domain[i + 1]);
        }
        return encode;
    }
}

/// <summary>Builds tiling and shading patterns (<c>makepattern</c>, <c>shfill</c>).</summary>
public static class PsPatterns
{
    public static TilingPattern BuildTilingPattern(PsDict patternDict, Matrix matrix, PsExecutionContext ctx, RenderModelBuilder builder)
    {
        int paintType = RequireInt(patternDict, "PaintType");
        int tilingType = RequireInt(patternDict, "TilingType");
        var bbox = RequireBBox(patternDict);
        double xStep = RequireNumber(patternDict, "XStep");
        double yStep = RequireNumber(patternDict, "YStep");
        if (patternDict.Get("PaintProc") is not PsProcedure paintProc)
            throw new PsRangeError("PaintProc missing");

        var patternBuilder = new RenderModelBuilder();
        double width = bbox[2] - bbox[0];
        double height = bbox[3] - bbox[1];
        if (width <= 0 || height <= 0)
            (width, height) = (1.0, 1.0);
        patternBuilder.SetDefaultPageSize(width, height);

        var registry = new OperatorRegistry();
        BaseOperators.Register(registry);
        GraphicsOperators.Register(registry, patternBuilder);
        ColorOperators.Register(registry, patternBuilder);
        TextOperators.Register(registry, patternBuilder, ctx.FontResolver ?? new FontResolver());
        ImageOperators.Register(registry, patternBuilder, ctx.ImageStore ?? new PsImageStore());
        ExtraOperators.Register(registry, patternBuilder);
        var interpreter = new PsInterpreter(registry);

        var patternCtx = PsPipeline.CreateDefaultContext();
        patternCtx.FontResolver = ctx.FontResolver;
        patternCtx.ImageStore = ctx.ImageStore;
        patternCtx.DefaultPageSize = (width, height);
        patternCtx.CharpathMode = ctx.CharpathMode;
        patternCtx.OperandStack.Push(patternDict);
        interpreter.ExecuteProcedure(paintProc, patternCtx);
        var document = patternBuilder.Document();
        var commands = document.Pages.Count > 0 ? document.Pages[0].Commands : [];
        if (paintType == 2)
            commands = StripPatternColors(commands);
        return new TilingPattern(paintType, tilingType, bbox, xStep, yStep, matrix, commands);
    }

    public static ShadingPattern BuildShadingPattern(PsDict patternDict, Matrix matrix, PsExecutionContext ctx, RenderModelBuilder builder)
    {
        if (patternDict.Get("Shading") is not PsDict shadingObj)
            throw new PsRangeError("Shading dictionary missing");
        return new ShadingPattern(BuildShading(shadingObj, ctx, builder), matrix);
    }

    public static Shading BuildShading(PsDict shadingDict, PsExecutionContext ctx, RenderModelBuilder builder)
    {
        int shadingType = RequireInt(shadingDict, "ShadingType");
        var colorSpaceObj = shadingDict.Get("ColorSpace") ?? throw new PsRangeError("Shading ColorSpace missing");
        var colorSpace = ColorSpaces.Parse(colorSpaceObj, builder);
        if (shadingDict.Get("Coords") is not PsArray coords)
            throw new PsTypeError("Coords must be array");
        var coordsList = ArrayNumbers(coords);
        var domain = OptionalDomain(shadingDict);
        var extend = OptionalExtend(shadingDict);
        if (shadingDict.Get("Function") is not PsDict functionObj)
            throw new PsTypeError("Function must be dictionary");
        var func = PsFunctions.Parse(functionObj, builder);
        if (shadingType == 2)
        {
            if (coordsList.Count != 4)
                throw new PsRangeError("Axial shading requires 4 coords");
            return new AxialShading(colorSpace, [.. coordsList], domain, func, extend);
        }
        if (shadingType == 3)
        {
            if (coordsList.Count != 6)
                throw new PsRangeError("Radial shading requires 6 coords");
            return new RadialShading(colorSpace, [.. coordsList], domain, func, extend);
        }
        throw new PsRangeError($"unsupported ShadingType {shadingType}");
    }

    private static List<RenderCommand> StripPatternColors(List<RenderCommand> commands)
    {
        var stripped = new List<RenderCommand>(commands.Count);
        foreach (var command in commands)
        {
            switch (command)
            {
                case PathCommand pc when pc.Fill is not null:
                    // type(command)(path, stroke, fill): the other fields return to defaults.
                    stripped.Add(new PathCommand(pc.Path, pc.Stroke, new Paint("PatternBase", null)));
                    break;
                case TextCommand tc when tc.Fill is not null:
                    stripped.Add(new TextCommand(tc.Text, tc.FontRef, tc.FontSize, tc.Matrix, new Paint("PatternBase", null)));
                    break;
                default:
                    stripped.Add(command);
                    break;
            }
        }
        return stripped;
    }

    private static int RequireInt(PsDict data, string key)
    {
        var value = data.Get(key);
        if (!PsOps.IsNumeric(value))
            throw new PsTypeError($"{key} must be numeric");
        return (int)Py.Int(PsOps.ToDouble(value));
    }

    private static double RequireNumber(PsDict data, string key)
    {
        var value = data.Get(key);
        if (!PsOps.IsNumeric(value))
            throw new PsTypeError($"{key} must be numeric");
        return PsOps.ToDouble(value);
    }

    private static double[] RequireBBox(PsDict data)
    {
        if (data.Get("BBox") is not PsArray bbox)
            throw new PsTypeError("BBox must be array");
        var values = ArrayNumbers(bbox);
        if (values.Count != 4)
            throw new PsRangeError("BBox must have 4 values");
        return [.. values];
    }

    private static List<double> ArrayNumbers(PsArray array)
    {
        var values = new List<double>(array.Items.Count);
        foreach (var item in array.Items)
        {
            if (!PsOps.IsNumeric(item))
                throw new PsTypeError("array must be numeric");
            values.Add(PsOps.ToDouble(item));
        }
        return values;
    }

    private static (double, double)? OptionalDomain(PsDict data)
    {
        var domain = data.Get("Domain");
        if (domain is null)
            return null;
        if (domain is not PsArray array)
            throw new PsTypeError("Domain must be array");
        var values = ArrayNumbers(array);
        if (values.Count != 2)
            throw new PsRangeError("Domain must have 2 values");
        return (values[0], values[1]);
    }

    private static (bool, bool) OptionalExtend(PsDict data)
    {
        var extend = data.Get("Extend");
        if (extend is null)
            return (false, false);
        if (extend is not PsArray array)
            throw new PsTypeError("Extend must be array");
        var values = new List<bool>();
        foreach (var item in array.Items)
        {
            if (item is bool b)
                values.Add(b);
            else if (PsOps.IsNumeric(item))
                values.Add(PsOps.ToDouble(item) != 0);
            else
                throw new PsTypeError("Extend must be boolean array");
        }
        if (values.Count != 2)
            throw new PsRangeError("Extend must have 2 values");
        return (values[0], values[1]);
    }
}

/// <summary>Registers the color, color space and pattern operators.</summary>
public static class ColorOperators
{
    public static void Register(OperatorRegistry registry, RenderModelBuilder builder)
    {
        registry.Register("setgray", OpSetGray, 1);
        registry.Register("currentgray", OpCurrentGray);
        registry.Register("setrgbcolor", OpSetRgb, 3);
        registry.Register("sethsbcolor", OpSetHsb, 3);
        registry.Register("setcmykcolor", OpSetCmyk, 4);
        registry.Register("setscreen", OpSetScreen, 3);
        registry.Register("settransfer", ctx => ctx.Extras["_ps_transfer"] = ctx.OperandStack.Pop(), 1);
        registry.Register("currenttransfer", ctx => ctx.OperandStack.Push(ctx.Extras.TryGetValue("_ps_transfer", out var v) ? v : IdentityTransferProc()));
        registry.Register("setoverprint", OpSetOverprint, 1);
        registry.Register("setblackgeneration", ctx => ctx.Extras["_ps_blackgeneration"] = ctx.OperandStack.Pop(), 1);
        registry.Register("currentblackgeneration", ctx => ctx.OperandStack.Push(ctx.Extras.TryGetValue("_ps_blackgeneration", out var v) ? v : IdentityTransferProc()));
        registry.Register("setundercolorremoval", ctx => ctx.Extras["_ps_undercolorremoval"] = ctx.OperandStack.Pop(), 1);
        registry.Register("currentundercolorremoval", ctx => ctx.OperandStack.Push(ctx.Extras.TryGetValue("_ps_undercolorremoval", out var v) ? v : IdentityTransferProc()));
        registry.Register("setcolortransfer", OpSetColorTransfer, 4);
        registry.Register("currentcolortransfer", OpCurrentColorTransfer);
        registry.Register("sethalftone", ctx => ctx.Extras["_ps_halftone"] = ctx.OperandStack.Pop(), 1);
        registry.Register("currenthalftone", ctx => ctx.OperandStack.Push(ctx.Extras.TryGetValue("_ps_halftone", out var v) ? v : DefaultHalftone()));
        var interpreter = new PsInterpreter(registry);
        registry.Register("setcolorspace", ctx => { using var _ = PsFunctions.UseInterpreter(ctx, interpreter); OpSetColorSpace(ctx, builder); }, 1);
        registry.Register("currentcolorspace", ctx => ctx.OperandStack.Push(CurrentColorSpaceOperand(State(ctx))));
        registry.Register("setcolor", ctx => OpSetColor(ctx, builder), 1);
        registry.Register("currentcolor", ctx => { foreach (double v in State(ctx).CurrentColorComponents) ctx.OperandStack.Push(v); });
        registry.Register("makepattern", ctx => { using var _ = PsFunctions.UseInterpreter(ctx, interpreter); OpMakePattern(ctx, builder); }, 2);
        registry.Register("setpattern", ctx => OpSetPattern(ctx, builder), 1);
        registry.Register("shfill", ctx => { using var _ = PsFunctions.UseInterpreter(ctx, interpreter); OpShFill(ctx, builder); }, 1);
    }

    private static GraphicsState State(PsExecutionContext ctx) => ctx.GraphicsStateStack.Peek();

    // Deviation from the original, whose currenthalftone was null until a program set one: a
    // device always has a halftone, and prologs read it (currenthalftone /HalftoneType get).
    private static PsDict DefaultHalftone()
    {
        var halftone = new PsDict();
        halftone.Items["HalftoneType"] = 1L;
        halftone.Items["Frequency"] = 60.0;
        halftone.Items["Angle"] = 45.0;
        halftone.Items["SpotFunction"] = new PsProcedure(
        [
            new PsName("dup"), new PsName("mul"), new PsName("exch"), new PsName("dup"), new PsName("mul"),
            new PsName("add"), 1L, new PsName("exch"), new PsName("sub"),
        ]);
        return halftone;
    }

    private static void SetPaint(GraphicsState state, Paint paint)
    {
        state.StrokePaint = paint;
        state.FillPaint = paint;
    }

    private static void OpSetGray(PsExecutionContext ctx)
    {
        double gray = Normalize01(PopNumber(ctx));
        var state = State(ctx);
        state.CurrentColorSpace = new DeviceColorSpace("DeviceGray");
        state.CurrentColorComponents = [gray];
        SetPaint(state, Paint.Gray(gray));
    }

    private static void OpCurrentGray(PsExecutionContext ctx)
    {
        var state = State(ctx);
        if (state.CurrentColorSpace is DeviceColorSpace { Name: "DeviceGray" })
        {
            ctx.OperandStack.Push(state.CurrentColorComponents.Length > 0 ? state.CurrentColorComponents[0] : 0.0);
            return;
        }
        if (state.CurrentColorSpace is DeviceColorSpace { Name: "DeviceRGB" })
        {
            var p = PadComponents(state.CurrentColorComponents, 3);
            ctx.OperandStack.Push(0.299 * p[0] + 0.587 * p[1] + 0.114 * p[2]);
            return;
        }
        if (state.CurrentColorSpace is DeviceColorSpace { Name: "DeviceCMYK" })
        {
            var p = PadComponents(state.CurrentColorComponents, 4);
            ctx.OperandStack.Push(1.0 - GraphicsOperators.PyMin(1.0, 0.3 * p[0] + 0.59 * p[1] + 0.11 * p[2] + p[3]));
            return;
        }
        ctx.OperandStack.Push(0.0);
    }

    private static void OpSetRgb(PsExecutionContext ctx)
    {
        double b = Normalize01(PopNumber(ctx));
        double g = Normalize01(PopNumber(ctx));
        double r = Normalize01(PopNumber(ctx));
        var state = State(ctx);
        state.CurrentColorSpace = new DeviceColorSpace("DeviceRGB");
        state.CurrentColorComponents = [r, g, b];
        SetPaint(state, Paint.Rgb(r, g, b));
    }

    private static void OpSetHsb(PsExecutionContext ctx)
    {
        double b = PopNumber(ctx);
        double s = PopNumber(ctx);
        double h = PopNumber(ctx);
        var (r, g, v) = HsvToRgb(WrapHue(h), Normalize01(s), Normalize01(b));
        var state = State(ctx);
        state.CurrentColorSpace = new DeviceColorSpace("DeviceRGB");
        state.CurrentColorComponents = [r, g, v];
        SetPaint(state, Paint.Rgb(r, g, v));
    }

    private static void OpSetCmyk(PsExecutionContext ctx)
    {
        double k = Normalize01(PopNumber(ctx));
        double y = Normalize01(PopNumber(ctx));
        double m = Normalize01(PopNumber(ctx));
        double c = Normalize01(PopNumber(ctx));
        var state = State(ctx);
        state.CurrentColorSpace = new DeviceColorSpace("DeviceCMYK");
        state.CurrentColorComponents = [c, m, y, k];
        SetPaint(state, Paint.Cmyk(c, m, y, k));
    }

    private static void OpSetScreen(PsExecutionContext ctx)
    {
        var proc = ctx.OperandStack.Pop();
        double angle = PopNumber(ctx);
        double frequency = PopNumber(ctx);
        // Treated as a graphics-state hint with no raster effect.
        ctx.Extras["_ps_screen"] = (frequency, angle, proc);
    }

    private static void OpSetOverprint(PsExecutionContext ctx)
    {
        var value = ctx.OperandStack.Pop();
        bool flag;
        if (value is bool b)
            flag = b;
        else if (PsOps.IsNumeric(value))
            flag = PsOps.ToDouble(value) != 0;
        else
            throw new PsTypeError("setoverprint expects boolean");
        ctx.Extras["_ps_overprint"] = flag;
        State(ctx).Overprint = flag;
    }

    private static void OpSetColorTransfer(PsExecutionContext ctx)
    {
        var t4 = ctx.OperandStack.Pop();
        var t3 = ctx.OperandStack.Pop();
        var t2 = ctx.OperandStack.Pop();
        var t1 = ctx.OperandStack.Pop();
        ctx.Extras["_ps_colortransfer"] = new[] { t1, t2, t3, t4 };
    }

    private static void OpCurrentColorTransfer(PsExecutionContext ctx)
    {
        if (!ctx.Extras.TryGetValue("_ps_colortransfer", out var value) || value is not object?[] transfer)
        {
            var identity = IdentityTransferProc();
            transfer = [identity, identity, identity, identity];
        }
        foreach (var proc in transfer)
            ctx.OperandStack.Push(proc);
    }

    // Deviation from the original, which pushed its parsed color space object, on which
    // `currentcolorspace 0 get` failed: a device space is returned as the array [/DeviceRGB] the
    // PLRM specifies, and any other space as the operand that set it.
    private static object? CurrentColorSpaceOperand(GraphicsState state)
    {
        var space = state.CurrentColorSpace;
        if (space is DeviceColorSpace device)
            return new PsArray([new PsName(device.Name, true)]);
        if (state.ColorSpaceSource is { } source && ReferenceEquals(source.Space, space) && source.Source is PsArray)
            return source.Source;
        return space;
    }

    private static void OpSetColorSpace(PsExecutionContext ctx, RenderModelBuilder builder)
    {
        var spaceObj = ResolveColorSpaceObject(ctx.OperandStack.Pop(), ctx);
        var space = ColorSpaces.Parse(spaceObj, builder);
        var state = State(ctx);
        state.ColorSpaceSource = (space, spaceObj);
        state.CurrentColorSpace = space;
        state.CurrentColorComponents = [];
        state.CurrentPattern = null;
        SyncPaintsFromState(state, builder);
    }

    private static void OpSetColor(PsExecutionContext ctx, RenderModelBuilder builder)
    {
        var state = State(ctx);
        var space = state.CurrentColorSpace;
        if (space is DeviceColorSpace device)
        {
            switch (device.Name)
            {
                case "DeviceGray":
                {
                    double gray = Normalize01(PopNumber(ctx));
                    state.CurrentColorComponents = [gray];
                    SetPaint(state, Paint.Gray(gray));
                    return;
                }
                case "DeviceRGB":
                {
                    double b = Normalize01(PopNumber(ctx));
                    double g = Normalize01(PopNumber(ctx));
                    double r = Normalize01(PopNumber(ctx));
                    state.CurrentColorComponents = [r, g, b];
                    SetPaint(state, Paint.Rgb(r, g, b));
                    return;
                }
                case "DeviceCMYK":
                {
                    double k = Normalize01(PopNumber(ctx));
                    double y = Normalize01(PopNumber(ctx));
                    double m = Normalize01(PopNumber(ctx));
                    double c = Normalize01(PopNumber(ctx));
                    state.CurrentColorComponents = [c, m, y, k];
                    SetPaint(state, Paint.Cmyk(c, m, y, k));
                    return;
                }
            }
        }
        if (space is SeparationColorSpace separation)
        {
            double tint = Normalize01(PopNumber(ctx));
            state.CurrentColorComponents = [tint];
            var alt = EvaluateTint(separation.Tint, [tint]);
            if (ApplyAlternatePaint(state, separation.Alternate, alt))
                return;
        }
        if (space is DeviceNColorSpace deviceN)
        {
            var tints = new List<double>();
            for (int i = 0; i < deviceN.Names.Count; i++)
                tints.Add(Normalize01(PopNumber(ctx)));
            tints.Reverse();
            state.CurrentColorComponents = [.. tints];
            var alt = EvaluateTint(deviceN.Tint, tints);
            if (ApplyAlternatePaint(state, deviceN.Alternate, alt))
                return;
        }
        if (space is IndexedColorSpace indexed)
        {
            long index = Py.Round(PopNumber(ctx));
            index = Math.Max(0, Math.Min(indexed.HiVal, index));
            state.CurrentColorComponents = [index];
            if (ApplyAlternatePaint(state, indexed.Base, IndexedComponents(indexed, (int)index)))
                return;
        }
        if (space is CieBasedColorSpace cie)
        {
            var components = new List<double>();
            for (int i = 0; i < Math.Max(1, cie.Components); i++)
                components.Add(PopNumber(ctx));
            components.Reverse();
            state.CurrentColorComponents = [.. components];
            var (r, g, b) = CieToRgbFallback(components, cie);
            SetPaint(state, Paint.Rgb(r, g, b));
            return;
        }
        if (space is PatternColorSpace patternSpace)
        {
            PsPattern? patternObj = null;
            if (ctx.OperandStack.Count > 0 && ctx.OperandStack.Peek() is PsPattern)
                patternObj = (PsPattern)ctx.OperandStack.Pop()!;
            var components = PopComponents(ctx, patternSpace.Base);
            if (patternObj is null && ctx.OperandStack.Count > 0 && ctx.OperandStack.Peek() is PsPattern)
                patternObj = (PsPattern)ctx.OperandStack.Pop()!;
            if (patternObj is not null)
                state.CurrentPattern = patternObj;
            state.CurrentColorComponents = components;
            ApplyPatternPaint(state, builder);
            return;
        }
        state.CurrentColorComponents = PopComponents(ctx, space);
        SyncPaintsFromState(state, builder);
    }

    private static void OpMakePattern(PsExecutionContext ctx, RenderModelBuilder builder)
    {
        var matrix = PopMatrix(ctx);
        if (ctx.OperandStack.Pop() is not PsDict patternDict)
            throw new PsTypeError("pattern dictionary expected");
        var patternType = patternDict.Get("PatternType");
        if (!PsOps.IsNumeric(patternType))
            throw new PsRangeError("PatternType missing");
        long type = Py.Int(PsOps.ToDouble(patternType));
        Pattern pattern = type switch
        {
            1 => PsPatterns.BuildTilingPattern(patternDict, matrix, ctx, builder),
            2 => PsPatterns.BuildShadingPattern(patternDict, matrix, ctx, builder),
            _ => throw new PsRangeError($"unsupported PatternType {type}"),
        };
        string patternId = builder.RegisterPattern(pattern);
        ctx.OperandStack.Push(new PsPattern(patternId, pattern));
    }

    private static void OpSetPattern(PsExecutionContext ctx, RenderModelBuilder builder)
    {
        if (ctx.OperandStack.Pop() is not PsPattern pattern)
            throw new PsTypeError("pattern expected");
        var state = State(ctx);
        state.CurrentPattern = pattern;
        // setpattern implies the Pattern colorspace: uncolored tiling patterns keep the current
        // base colorspace, colored patterns and shading patterns have no base.
        ColorSpace? baseSpace = null;
        if (pattern.Pattern is TilingPattern { PaintType: 2 })
            baseSpace = state.CurrentColorSpace is PatternColorSpace pcs ? pcs.Base : state.CurrentColorSpace;
        state.CurrentColorSpace = new PatternColorSpace(baseSpace);
        ApplyPatternPaint(state, builder);
    }

    private static void OpShFill(PsExecutionContext ctx, RenderModelBuilder builder)
    {
        if (ctx.OperandStack.Pop() is not PsDict shadingDict)
            throw new PsTypeError("shading dictionary expected");
        var wrapper = new PsDict();
        wrapper.Items["Shading"] = shadingDict;
        var pattern = PsPatterns.BuildShadingPattern(wrapper, Matrix.Identity, ctx, builder);
        string patternId = builder.RegisterPattern(pattern);
        var paint = new Paint("Pattern", new PatternPaint(patternId, null, null));
        var state = State(ctx);
        var path = state.ClipPath;
        if (path is null)
        {
            if (ctx.DefaultPageSize is null)
                return;
            path = Paths.FromRect(new Rect(0, 0, ctx.DefaultPageSize.Value.Width, ctx.DefaultPageSize.Value.Height));
        }
        builder.AddPath(path, null, paint, overprint: state.Overprint);
    }

    private static void ApplyPatternPaint(GraphicsState state, RenderModelBuilder builder)
    {
        var pattern = state.CurrentPattern;
        if (pattern is null)
            return;
        string? baseSpaceId = null;
        double[]? baseComponents = null;
        if (state.CurrentColorSpace is PatternColorSpace { Base: { } b })
        {
            baseSpaceId = builder.RegisterColorSpace(b);
            baseComponents = state.CurrentColorComponents;
        }
        SetPaint(state, new Paint("Pattern", new PatternPaint(pattern.PatternId, baseSpaceId, baseComponents)));
    }

    private static void SyncPaintsFromState(GraphicsState state, RenderModelBuilder builder)
    {
        var space = state.CurrentColorSpace;
        if (space is PatternColorSpace)
        {
            if (state.CurrentPattern is not null)
                ApplyPatternPaint(state, builder);
            return;
        }
        if (space is DeviceColorSpace device)
        {
            switch (device.Name)
            {
                case "DeviceGray":
                    double gray = state.CurrentColorComponents.Length > 0 ? Normalize01(state.CurrentColorComponents[0]) : 0.0;
                    SetPaint(state, Paint.Gray(gray));
                    return;
                case "DeviceRGB":
                    var rgb = PadComponents(state.CurrentColorComponents, 3).Select(Normalize01).ToArray();
                    SetPaint(state, Paint.Rgb(rgb[0], rgb[1], rgb[2]));
                    return;
                case "DeviceCMYK":
                    var cmyk = PadComponents(state.CurrentColorComponents, 4).Select(Normalize01).ToArray();
                    SetPaint(state, Paint.Cmyk(cmyk[0], cmyk[1], cmyk[2], cmyk[3]));
                    return;
            }
        }
        string spaceId = builder.RegisterColorSpace(space);
        SetPaint(state, new Paint("ColorSpace", new ColorSpacePaint(spaceId, state.CurrentColorComponents)));
    }

    private static double PopNumber(PsExecutionContext ctx)
    {
        var value = ctx.OperandStack.Pop();
        if (PsOps.IsNumeric(value))
            return PsOps.ToDouble(value);
        throw new PsTypeError("number expected");
    }

    private static PsProcedure IdentityTransferProc() => new([]);

    private static Matrix PopMatrix(PsExecutionContext ctx)
    {
        var value = ctx.OperandStack.Pop();
        var items = value switch
        {
            PsArray a => a.Items,
            List<object?> l => l,
            _ => throw new PsTypeError("matrix array expected"),
        };
        if (items.Count != 6)
            throw new PsRangeError("matrix must have 6 elements");
        var v = items.Select(PyFloat).ToArray();
        return new Matrix(v[0], v[1], v[2], v[3], v[4], v[5]);
    }

    /// <summary>Python <c>float(x)</c> for a PostScript value.</summary>
    internal static double PyFloat(object? item) => item switch
    {
        long or double or bool or int => PsOps.ToDouble(item),
        string s when Py.TryFloat(s, out double d) => d,
        string s => throw Py.ValueError($"could not convert string to float: '{s}'"),
        _ => throw Py.TypeError("float() argument must be a string or a real number"),
    };

    private static double[] PopComponents(PsExecutionContext ctx, ColorSpace? space)
    {
        if (space is PatternColorSpace { Base: null })
            return [];
        var components = new List<double>();
        while (ctx.OperandStack.Count > 0 && PsOps.IsNumeric(ctx.OperandStack.Peek()))
            components.Add(PsOps.ToDouble(ctx.OperandStack.Pop()));
        components.Reverse();
        return [.. components];
    }

    private static double[] PadComponents(double[] values, int count)
    {
        var padded = new double[count];
        for (int i = 0; i < count; i++)
            padded[i] = i < values.Length ? values[i] : 0.0;
        return padded;
    }

    internal static double Normalize01(double value)
    {
        if (value < 0.0)
            return 0.0;
        if (value > 1.0)
            return 1.0;
        return value;
    }

    private static double WrapHue(double value)
    {
        double wrapped = Py.Mod(value, 1.0);
        return wrapped >= 0.0 ? wrapped : wrapped + 1.0;
    }

    /// <summary>Python <c>colorsys.hsv_to_rgb</c>.</summary>
    internal static (double R, double G, double B) HsvToRgb(double h, double s, double v)
    {
        if (s == 0.0)
            return (v, v, v);
        long i = Py.Int(h * 6.0);
        double f = (h * 6.0) - i;
        double p = v * (1.0 - s);
        double q = v * (1.0 - s * f);
        double t = v * (1.0 - s * (1.0 - f));
        i = ((i % 6) + 6) % 6;
        return i switch
        {
            0 => (v, t, p),
            1 => (q, v, p),
            2 => (p, v, t),
            3 => (p, q, v),
            4 => (t, p, v),
            _ => (v, p, q),
        };
    }

    private static List<double> EvaluateTint(Function function, List<double> inputs) => [.. function.Evaluate(inputs)];

    private static bool ApplyAlternatePaint(GraphicsState state, ColorSpace alternate, IReadOnlyList<double> components)
    {
        if (alternate is not DeviceColorSpace device)
            return false;
        switch (device.Name)
        {
            case "DeviceGray":
                SetPaint(state, Paint.Gray(Normalize01(components.Count > 0 ? components[0] : 0.0)));
                return true;
            case "DeviceRGB":
            {
                var padded = new double[3];
                for (int i = 0; i < Math.Min(3, components.Count); i++)
                    padded[i] = Normalize01(components[i]);
                SetPaint(state, Paint.Rgb(padded[0], padded[1], padded[2]));
                return true;
            }
            case "DeviceCMYK":
            {
                var padded = new double[4];
                for (int i = 0; i < Math.Min(4, components.Count); i++)
                    padded[i] = Normalize01(components[i]);
                SetPaint(state, Paint.Cmyk(padded[0], padded[1], padded[2], padded[3]));
                return true;
            }
        }
        return false;
    }

    private static List<double> IndexedComponents(IndexedColorSpace space, int index)
    {
        int componentCount = 1;
        if (space.Base is DeviceColorSpace d)
        {
            if (d.Name == "DeviceRGB")
                componentCount = 3;
            else if (d.Name == "DeviceCMYK")
                componentCount = 4;
        }
        int start = index * componentCount;
        var result = new List<double>(componentCount);
        for (int i = 0; i < componentCount; i++)
        {
            int pos = start + i;
            result.Add(pos < space.Lookup.Length ? space.Lookup[pos] / 255.0 : 0.0);
        }
        return result;
    }

    private static (double, double, double) CieToRgbFallback(List<double> components, CieBasedColorSpace space)
    {
        var padded = new double[3];
        var ranges = space.Ranges;
        for (int idx = 0; idx < Math.Min(3, components.Count); idx++)
        {
            double value = components[idx];
            if (ranges is not null && ranges.Length >= idx * 2 + 2)
            {
                double lo = ranges[idx * 2];
                double hi = ranges[idx * 2 + 1];
                if (hi != lo)
                    value = (value - lo) / (hi - lo);
            }
            padded[idx] = Normalize01(value);
        }
        return (padded[0], padded[1], padded[2]);
    }

    private static object? ResolveColorSpaceObject(object? value, PsExecutionContext ctx)
    {
        if (value is PsArray array)
            return new PsArray(array.Items.Select(item => ResolveColorSpaceObject(item, ctx)).ToList());
        if (value is PsName { Literal: false } name && PsInterpreter.LookupDict(name.Value, ctx, out var resolved) && resolved is not null)
            return ResolveColorSpaceObject(resolved, ctx);
        return value;
    }
}
