// Not in the original: standard operators it lacks.
//
// Deviation from the original. Every operator here is registered as a fallback
// (OperatorRegistry.RegisterFallback): it is found only when no dictionary on the stack defines
// the name, which is exactly where the original raised `undefined` and abandoned the job. A
// program that ran to completion in the original therefore resolves every name as it did and
// produces the same output; one that defines its own procedure of the same name keeps it. The
// operators were added for real-world files our render benchmark against Ghostscript found
// blank or failed; their semantics follow the PLRM, simplified where the render model has no
// equivalent (a device-independent `infill` answers false, user-path caching is a no-op).

using XRay.PS.Internal;
using XRay.PS.Rendering;

namespace XRay.PS.PostScript;

/// <summary>Raised by <c>stop</c>; <c>stopped</c> catches it like any other error.</summary>
public sealed class PsStopError() : PsError("stop", "stop outside stopped");

/// <summary>Registers the fallback operators the original lacks.</summary>
public static class ExtraOperators
{
    /// <summary>The <c>Extras</c> key under which the pipeline publishes the default CTM.</summary>
    internal const string DefaultMatrixKey = "__default_ctm__";

    public static void Register(OperatorRegistry registry, RenderModelBuilder builder)
    {
        var interpreter = new PsInterpreter(registry);

        // Matrices and the CTM.
        registry.RegisterFallback("initmatrix", ctx => SetCtm(ctx, interpreter, DefaultMatrix(ctx)));
        registry.RegisterFallback("defaultmatrix", ctx => ctx.OperandStack.Push(FillMatrix(ctx.OperandStack.Pop(), DefaultMatrix(ctx))), 1);
        registry.RegisterFallback("identmatrix", ctx => ctx.OperandStack.Push(FillMatrix(ctx.OperandStack.Pop(), Matrix.Identity)), 1);
        registry.RegisterFallback("invertmatrix", ctx => OpInvertMatrix(ctx, interpreter), 2);
        registry.RegisterFallback("initgraphics", ctx => OpInitGraphics(ctx, interpreter));

        // Graphics state queries.
        registry.RegisterFallback("currentlinewidth", ctx => ctx.OperandStack.Push(State(ctx).LineWidth));
        registry.RegisterFallback("currentlinecap", ctx => ctx.OperandStack.Push((long)State(ctx).LineCap));
        registry.RegisterFallback("currentlinejoin", ctx => ctx.OperandStack.Push((long)State(ctx).LineJoin));
        registry.RegisterFallback("currentmiterlimit", ctx => ctx.OperandStack.Push(State(ctx).MiterLimit));
        registry.RegisterFallback("currentdash", OpCurrentDash);
        registry.RegisterFallback("currentstrokeadjust", ctx => ctx.OperandStack.Push(State(ctx).StrokeAdjust ?? false));
        registry.RegisterFallback("currentoverprint", ctx => ctx.OperandStack.Push(State(ctx).Overprint));
        registry.RegisterFallback("currentsmoothness", ctx => ctx.OperandStack.Push(ctx.Extras.TryGetValue("_ps_smoothness", out var v) ? v : 0.02));
        registry.RegisterFallback("setsmoothness", ctx => ctx.Extras["_ps_smoothness"] = GraphicsOperators.PopNumber(ctx), 1);
        registry.RegisterFallback("currentrgbcolor", OpCurrentRgb);
        registry.RegisterFallback("currentcmykcolor", OpCurrentCmyk);
        registry.RegisterFallback("currenthsbcolor", OpCurrentHsb);
        registry.RegisterFallback("currentscreen", OpCurrentScreen);
        registry.RegisterFallback("setcolorscreen", ctx => { Pop(ctx, 12); }, 12);
        registry.RegisterFallback("currentcolorscreen", ctx => { for (int i = 0; i < 4; i++) OpCurrentScreen(ctx); });
        registry.RegisterFallback("setcolorrendering", ctx => ctx.OperandStack.Pop(), 1);
        registry.RegisterFallback("currentcolorrendering", ctx => ctx.OperandStack.Push(new PsDict()));
        registry.RegisterFallback("grestoreall", ctx => OpGRestoreAll(ctx, interpreter));
        registry.RegisterFallback("gstate", ctx => ctx.OperandStack.Push(new PsGState(State(ctx).Clone())));
        registry.RegisterFallback("currentgstate", ctx => { ctx.OperandStack.Pop(); ctx.OperandStack.Push(new PsGState(State(ctx).Clone())); }, 1);
        registry.RegisterFallback("setgstate", OpSetGState, 1);
        registry.RegisterFallback("clipsave", ctx => interpreter.ExecuteName("gsave", ctx));
        registry.RegisterFallback("cliprestore", ctx => OpClipRestore(ctx, interpreter));

        // User paths.
        registry.RegisterFallback("setbbox", ctx => Pop(ctx, 4), 4);
        registry.RegisterFallback("ucache", _ => { });
        registry.RegisterFallback("uappend", ctx => AppendUserPath(ctx, interpreter, ctx.OperandStack.Pop()), 1);
        registry.RegisterFallback("upath", ctx => { ctx.OperandStack.Pop(); ctx.OperandStack.Push(new PsArray([])); }, 1);
        registry.RegisterFallback("ufill", ctx => PaintUserPath(ctx, interpreter, "fill"), 1);
        registry.RegisterFallback("ueofill", ctx => PaintUserPath(ctx, interpreter, "eofill"), 1);
        registry.RegisterFallback("ustroke", ctx => PaintUserPath(ctx, interpreter, "stroke"), 1);
        registry.RegisterFallback("ustrokepath", ctx => PaintUserPath(ctx, interpreter, "strokepath", keepPath: true), 1);
        registry.RegisterFallback("infill", ctx => InsideTest(ctx, 2), 1);
        registry.RegisterFallback("ineofill", ctx => InsideTest(ctx, 2), 1);
        registry.RegisterFallback("instroke", ctx => InsideTest(ctx, 2), 1);
        registry.RegisterFallback("inufill", ctx => InsideTest(ctx, 3), 2);
        registry.RegisterFallback("inueofill", ctx => InsideTest(ctx, 3), 2);
        registry.RegisterFallback("inustroke", ctx => InsideTest(ctx, 3), 2);

        // Arithmetic.
        registry.RegisterFallback("xor", ctx => OpXor(ctx), 2);
        registry.RegisterFallback("exp", OpExp, 2);
        registry.RegisterFallback("ln", ctx => Unary(ctx, Math.Log, "ln"), 1);
        registry.RegisterFallback("log", ctx => Unary(ctx, Math.Log10, "log"), 1);
        registry.RegisterFallback("rand", ctx => ctx.OperandStack.Push((long)NextRandom(ctx)));
        registry.RegisterFallback("srand", ctx => ctx.Extras["_ps_rand"] = BaseOperators.PopInt(ctx), 1);
        registry.RegisterFallback("rrand", ctx => ctx.OperandStack.Push(ctx.Extras.TryGetValue("_ps_rand", out var v) ? v : 0L));
        registry.RegisterFallback("cvrs", OpCvrs, 3);

        // Strings, arrays, dictionaries.
        registry.RegisterFallback("search", ctx => OpSearch(ctx, anchored: false), 2);
        registry.RegisterFallback("anchorsearch", ctx => OpSearch(ctx, anchored: true), 2);
        registry.RegisterFallback("maxlength", OpMaxLength, 1);
        registry.RegisterFallback("store", OpStore, 2);
        registry.RegisterFallback("cleardictstack", ctx => { while (ctx.DictionaryStack.Count > 2) ctx.DictionaryStack.Pop(); });
        registry.RegisterFallback("dictstack", OpDictStack, 1);

        // Access attributes: not modelled, so every object is accessible.
        foreach (string name in (string[])["executeonly", "noaccess"])
            registry.RegisterFallback(name, _ => { }, 1);
        foreach (string name in (string[])["rcheck", "wcheck"])
            registry.RegisterFallback(name, ctx => { ctx.OperandStack.Pop(); ctx.OperandStack.Push(true); }, 1);
        registry.RegisterFallback("gcheck", ctx => { ctx.OperandStack.Pop(); ctx.OperandStack.Push(false); }, 1);
        registry.RegisterFallback("scheck", ctx => { ctx.OperandStack.Pop(); ctx.OperandStack.Push(false); }, 1);

        // Control.
        registry.RegisterFallback("stop", _ => throw new PsStopError());
        registry.RegisterFallback("countexecstack", ctx => ctx.OperandStack.Push(0L));
        registry.RegisterFallback("execstack", ctx => { ctx.OperandStack.Pop(); ctx.OperandStack.Push(new PsArray([])); }, 1);

        // Files: only the program itself (currentfile) can be read.
        registry.RegisterFallback("readline", OpReadLine, 2);
        registry.RegisterFallback("bytesavailable", ctx => { ctx.OperandStack.Pop(); ctx.OperandStack.Push(-1L); }, 1);
        registry.RegisterFallback("filenameforall", ctx => Pop(ctx, 3), 3);
        registry.RegisterFallback("fileposition", ctx => { ctx.OperandStack.Pop(); ctx.OperandStack.Push(0L); }, 1);
        registry.RegisterFallback("resetfile", ctx => ctx.OperandStack.Pop(), 1);
        registry.RegisterFallback("echo", ctx => ctx.OperandStack.Pop(), 1);
        registry.RegisterFallback("prompt", _ => { });
        registry.RegisterFallback("pstack", _ => { });
        registry.RegisterFallback("stack", _ => { });

        // Device, VM and interpreter parameters: accepted and ignored, or a fixed answer.
        registry.RegisterFallback("vmstatus", ctx => { ctx.OperandStack.Push(0L); ctx.OperandStack.Push(0L); ctx.OperandStack.Push(1L << 30); });
        registry.RegisterFallback("vmreclaim", ctx => ctx.OperandStack.Pop(), 1);
        registry.RegisterFallback("setvmthreshold", ctx => ctx.OperandStack.Pop(), 1);
        registry.RegisterFallback("usertime", ctx => ctx.OperandStack.Push(0L));
        registry.RegisterFallback("realtime", ctx => ctx.OperandStack.Push(0L));
        registry.RegisterFallback("setcachelimit", ctx => ctx.OperandStack.Pop(), 1);
        registry.RegisterFallback("setcacheparams", OpClearToMark);
        registry.RegisterFallback("currentcacheparams", ctx => { ctx.OperandStack.Push(new PsMark()); ctx.OperandStack.Push(0L); ctx.OperandStack.Push(0L); });
        registry.RegisterFallback("setucacheparams", OpClearToMark);
        registry.RegisterFallback("cachestatus", ctx => { for (int i = 0; i < 7; i++) ctx.OperandStack.Push(0L); });
        foreach (string name in (string[])["setsystemparams", "settrapparams", "setobjectformat", "setshared"])
            registry.RegisterFallback(name, ctx => ctx.OperandStack.Pop(), 1);
        registry.RegisterFallback("setdevparams", ctx => Pop(ctx, 2), 2);
        registry.RegisterFallback("currentsystemparams", ctx => ctx.OperandStack.Push(Params(
            ("MaxFontCache", 4_000_000L), ("CurFontCache", 0L), ("MaxFormCache", 100_000L), ("CurFormCache", 0L),
            ("MaxPatternCache", 100_000L), ("CurPatternCache", 0L), ("MaxUPathCache", 300_000L), ("CurUPathCache", 0L),
            ("MaxScreenStorage", 84_000L), ("MaxDisplayList", 1_000_000L), ("BuildTime", 1L), ("ByteOrder", false),
            ("RealFormat", PsString.FromText("IEEE")))));
        registry.RegisterFallback("currentuserparams", ctx => ctx.OperandStack.Push(Params(
            ("MaxFontItem", 12_000L), ("MinFontCompress", 3_000L), ("MaxUPathItem", 5_000L), ("MaxFormItem", 100_000L),
            ("MaxPatternItem", 20_000L), ("MaxScreenItem", 48_000L), ("MaxOpStack", 500_000L), ("MaxDictStack", 500L),
            ("MaxExecStack", 5_000L), ("MaxLocalVM", 100_000_000L), ("VMReclaim", 0L), ("VMThreshold", 1_000_000L),
            ("JobName", PsString.FromText("")), ("AccurateScreens", false))));
        registry.RegisterFallback("currenttrapparams", ctx => ctx.OperandStack.Push(new PsDict()));
        registry.RegisterFallback("currentdevparams", ctx => { ctx.OperandStack.Pop(); ctx.OperandStack.Push(new PsDict()); }, 1);
        registry.RegisterFallback("currentobjectformat", ctx => ctx.OperandStack.Push(0L));
        registry.RegisterFallback("currentshared", ctx => ctx.OperandStack.Push(false));
        registry.RegisterFallback("erasepage", _ => { });
        registry.RegisterFallback("copypage", _ => { });
        registry.RegisterFallback("nulldevice", _ => { });
        registry.RegisterFallback("revision", ctx => ctx.OperandStack.Push(0L));
        registry.RegisterFallback("serialnumber", ctx => ctx.OperandStack.Push(0L));
        registry.RegisterFallback("cshow", ctx => OpCShow(ctx, interpreter), 2);
        // CMap construction beyond what the original handles: the ranges are consumed and dropped.
        foreach (var (name, width) in (ReadOnlySpan<(string, int)>)[("cidrange", 3), ("cidchar", 2), ("bfrange", 3), ("notdefrange", 3), ("notdefchar", 2)])
        {
            string key = "_cmap_" + name + "_count";
            registry.RegisterFallback("begin" + name, ctx => ctx.Extras[key] = BaseOperators.PopInt(ctx), 1);
            registry.RegisterFallback("end" + name, ctx =>
            {
                long count = ctx.Extras.TryGetValue(key, out var v) && v is long l ? l : 0;
                for (long i = 0; i < count * width && ctx.OperandStack.Count > 0; i++)
                    ctx.OperandStack.Pop();
            });
        }
        registry.RegisterFallback("undefineresource", ctx =>
        {
            var category = BaseOperators.CoerceKey(ctx.OperandStack.Pop());
            var key = BaseOperators.CoerceKey(ctx.OperandStack.Pop());
            if (BaseOperators.ResourceStore(ctx).TryGetValue(category, out var categoryDict))
                categoryDict.Items.Remove(key);
        }, 2);
        registry.RegisterFallback("undefinefont", ctx => ctx.OperandStack.Pop(), 1);
        registry.RegisterFallback("usecmap", ctx => ctx.OperandStack.Pop(), 1);
        registry.RegisterFallback("usefont", ctx => ctx.OperandStack.Pop(), 1);
        foreach (string name in (string[])["SharedFontDirectory", "GlobalFontDirectory"])
            registry.RegisterFallback(name, ctx => ctx.OperandStack.Push(ctx.Systemdict.Get("FontDirectory") ?? new PsDict()));
        registry.RegisterFallback("shareddict", ctx => ctx.OperandStack.Push(ctx.Systemdict.Get("globaldict") ?? new PsDict()));
        registry.RegisterFallback("internaldict", ctx => { ctx.OperandStack.Pop(); ctx.OperandStack.Push(PersistentDict(ctx, "_ps_internaldict")); }, 1);
    }

    private static GraphicsState State(PsExecutionContext ctx) => ctx.GraphicsStateStack.Peek();

    private static PsDict Params(params (string Key, object? Value)[] entries)
    {
        var d = new PsDict();
        foreach (var (key, value) in entries)
            d.Items[key] = value;
        return d;
    }

    private static PsDict PersistentDict(PsExecutionContext ctx, string key)
    {
        if (ctx.Extras.TryGetValue(key, out var v) && v is PsDict d)
            return d;
        var created = new PsDict();
        ctx.Extras[key] = created;
        return created;
    }

    private static void Pop(PsExecutionContext ctx, int count)
    {
        for (int i = 0; i < count; i++)
            ctx.OperandStack.Pop();
    }

    private static void OpClearToMark(PsExecutionContext ctx)
    {
        while (ctx.OperandStack.Count > 0)
        {
            if (ctx.OperandStack.Pop() is PsMark)
                return;
        }
    }

    internal static Matrix DefaultMatrix(PsExecutionContext ctx) =>
        ctx.Extras.TryGetValue(DefaultMatrixKey, out var v) && v is Matrix m ? m : Matrix.Identity;

    private static void SetCtm(PsExecutionContext ctx, PsInterpreter interpreter, Matrix matrix)
    {
        // Through setmatrix, which keeps the current point where it is on the page.
        ctx.OperandStack.Push(new PsArray([matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F]));
        interpreter.ExecuteName("setmatrix", ctx);
    }

    private static object? FillMatrix(object? target, Matrix m)
    {
        var items = target switch
        {
            PsArray a => a.Items,
            List<object?> l => l,
            _ => throw new PsTypeError("matrix array expected"),
        };
        if (items.Count != 6)
            throw new PsRangeError("matrix must have 6 elements");
        items[0] = m.A;
        items[1] = m.B;
        items[2] = m.C;
        items[3] = m.D;
        items[4] = m.E;
        items[5] = m.F;
        return target;
    }

    private static void OpInvertMatrix(PsExecutionContext ctx, PsInterpreter interpreter)
    {
        var target = ctx.OperandStack.Pop();
        var source = GraphicsOperators.PopMatrix(ctx, interpreter);
        var inverse = GraphicsOperators.InvertCtm(source) ?? throw new PsRangeError("undefinedresult: singular matrix");
        ctx.OperandStack.Push(FillMatrix(target, inverse));
    }

    private static void OpInitGraphics(PsExecutionContext ctx, PsInterpreter interpreter)
    {
        var state = State(ctx);
        state.Ctm = DefaultMatrix(ctx);
        state.CurrentPath = new RenderPath();
        state.CurrentPoint = null;
        state.SubpathStart = null;
        state.LineWidth = 1.0;
        state.LineCap = 0;
        state.LineJoin = 0;
        state.MiterLimit = 10.0;
        state.Dash = [];
        state.DashPhase = 0.0;
        state.Flatness = 1.0;
        state.CurrentColorSpace = new DeviceColorSpace("DeviceGray");
        state.CurrentColorComponents = [0.0];
        state.CurrentPattern = null;
        state.StrokePaint = Paint.Gray(0.0);
        state.FillPaint = Paint.Gray(0.0);
        interpreter.ExecuteName("initclip", ctx);
    }

    private static void OpCurrentDash(PsExecutionContext ctx)
    {
        var state = State(ctx);
        ctx.OperandStack.Push(new PsArray(state.Dash.Select(d => (object?)d).ToList()));
        ctx.OperandStack.Push(state.DashPhase);
    }

    private static (double R, double G, double B) CurrentRgb(GraphicsState state)
    {
        var c = state.CurrentColorComponents;
        string family = state.CurrentColorSpace is DeviceColorSpace d ? d.Name : "";
        return (family, c.Length) switch
        {
            ("DeviceRGB", >= 3) => (c[0], c[1], c[2]),
            ("DeviceCMYK", >= 4) => (1.0 - Math.Min(1.0, c[0] + c[3]), 1.0 - Math.Min(1.0, c[1] + c[3]), 1.0 - Math.Min(1.0, c[2] + c[3])),
            (_, 1) => (c[0], c[0], c[0]),
            (_, >= 3) => (c[0], c[1], c[2]),
            _ => (0.0, 0.0, 0.0),
        };
    }

    private static void OpCurrentRgb(PsExecutionContext ctx)
    {
        var (r, g, b) = CurrentRgb(State(ctx));
        ctx.OperandStack.Push(r);
        ctx.OperandStack.Push(g);
        ctx.OperandStack.Push(b);
    }

    private static void OpCurrentCmyk(PsExecutionContext ctx)
    {
        var state = State(ctx);
        var c = state.CurrentColorComponents;
        if (state.CurrentColorSpace is DeviceColorSpace { Name: "DeviceCMYK" } && c.Length >= 4)
        {
            for (int i = 0; i < 4; i++)
                ctx.OperandStack.Push(c[i]);
            return;
        }
        var (r, g, b) = CurrentRgb(state);
        double cyan = 1.0 - r, magenta = 1.0 - g, yellow = 1.0 - b;
        double black = Math.Min(cyan, Math.Min(magenta, yellow));
        ctx.OperandStack.Push(cyan - black);
        ctx.OperandStack.Push(magenta - black);
        ctx.OperandStack.Push(yellow - black);
        ctx.OperandStack.Push(black);
    }

    private static void OpCurrentHsb(PsExecutionContext ctx)
    {
        var (r, g, b) = CurrentRgb(State(ctx));
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;
        double hue = 0.0;
        if (delta > 0)
        {
            if (max == r)
                hue = (g - b) / delta / 6.0;
            else if (max == g)
                hue = ((b - r) / delta + 2.0) / 6.0;
            else
                hue = ((r - g) / delta + 4.0) / 6.0;
            if (hue < 0)
                hue += 1.0;
        }
        ctx.OperandStack.Push(hue);
        ctx.OperandStack.Push(max > 0 ? delta / max : 0.0);
        ctx.OperandStack.Push(max);
    }

    private static void OpCurrentScreen(PsExecutionContext ctx)
    {
        if (ctx.Extras.TryGetValue("_ps_screen", out var v) && v is ValueTuple<double, double, object?> screen)
        {
            ctx.OperandStack.Push(screen.Item1);
            ctx.OperandStack.Push(screen.Item2);
            ctx.OperandStack.Push(screen.Item3);
            return;
        }
        // A 60 lpi round dot, the usual device default.
        ctx.OperandStack.Push(60.0);
        ctx.OperandStack.Push(45.0);
        ctx.OperandStack.Push(new PsProcedure(
        [
            new PsName("dup"), new PsName("mul"), new PsName("exch"), new PsName("dup"), new PsName("mul"),
            new PsName("add"), 1L, new PsName("exch"), new PsName("sub"),
        ]));
    }

    private static void OpGRestoreAll(PsExecutionContext ctx, PsInterpreter interpreter)
    {
        while (ctx.GraphicsStateStack.Count > 1)
            interpreter.ExecuteName("grestore", ctx);
    }

    private static void OpSetGState(PsExecutionContext ctx)
    {
        if (ctx.OperandStack.Pop() is not PsGState g)
            throw new PsTypeError("setgstate expects gstate");
        ctx.GraphicsStateStack.Items[^1] = g.State.Clone();
    }

    private static void OpClipRestore(PsExecutionContext ctx, PsInterpreter interpreter)
    {
        if (ctx.GraphicsStateStack.Count <= 1)
            return;
        // Keep everything but the clip of the state being restored over.
        var current = State(ctx).Clone();
        interpreter.ExecuteName("grestore", ctx);
        var clip = State(ctx).ClipPath;
        current.ClipPath = clip;
        ctx.GraphicsStateStack.Items[^1] = current;
    }

    // A user path is an array of numbers and operator names (setbbox, moveto, lineto, curveto,
    // arc, arcn, arct, closepath, ucache and their relative forms). The encoded form (a pair of
    // strings) is not supported.
    private static void AppendUserPath(PsExecutionContext ctx, PsInterpreter interpreter, object? userPath)
    {
        var items = userPath switch
        {
            PsArray a => a.Items,
            PsProcedure p => p.Items,
            _ => throw new PsTypeError("user path expected"),
        };
        foreach (var item in items.ToList())
        {
            if (item is PsName { Literal: false } or PsOperator)
                interpreter.ExecuteObject(item is PsName n ? new PsName(n.Value) : item, ctx);
            else if (PsOps.IsNumeric(item))
                ctx.OperandStack.Push(item);
            else
                throw new PsTypeError("user path element expected");
        }
    }

    private static void PaintUserPath(PsExecutionContext ctx, PsInterpreter interpreter, string paint, bool keepPath = false)
    {
        var top = ctx.OperandStack.Pop();
        object? matrix = null;
        if (paint is "stroke" or "strokepath" && top is PsArray { Items.Count: 6 } m && m.Items.All(PsOps.IsNumeric)
            && ctx.OperandStack.Count > 0 && ctx.OperandStack.Peek() is PsArray or PsProcedure)
        {
            matrix = top;
            top = ctx.OperandStack.Pop();
        }
        if (!keepPath)
            interpreter.ExecuteName("gsave", ctx);
        interpreter.ExecuteName("newpath", ctx);
        AppendUserPath(ctx, interpreter, top);
        if (matrix is not null)
        {
            ctx.OperandStack.Push(matrix);
            interpreter.ExecuteName("concat", ctx);
        }
        interpreter.ExecuteName(paint, ctx);
        if (!keepPath)
            interpreter.ExecuteName("grestore", ctx);
    }

    private static void InsideTest(PsExecutionContext ctx, int operands)
    {
        // The render model cannot answer insideness; report "outside" and consume the operands
        // (x y, or a user path, or x y and a user path).
        var top = ctx.OperandStack.Pop();
        if (PsOps.IsNumeric(top) && operands == 2)
            ctx.OperandStack.Pop();
        else if (operands == 3 && ctx.OperandStack.Count >= 2 && PsOps.IsNumeric(ctx.OperandStack.Peek()))
            Pop(ctx, 2);
        else if (operands == 3)
            ctx.OperandStack.Pop();
        ctx.OperandStack.Push(false);
    }

    private static void OpXor(PsExecutionContext ctx)
    {
        var b = ctx.OperandStack.Pop();
        var a = ctx.OperandStack.Pop();
        if (a is bool x && b is bool y)
            ctx.OperandStack.Push(x ^ y);
        else
            ctx.OperandStack.Push(BaseOperators.AsLong(a) ^ BaseOperators.AsLong(b));
    }

    private static void OpExp(PsExecutionContext ctx)
    {
        double exponent = BaseOperators.PopNumber(ctx);
        double baseValue = BaseOperators.PopNumber(ctx);
        double result = Math.Pow(baseValue, exponent);
        if (double.IsNaN(result) || double.IsInfinity(result))
            throw new PsRangeError("undefinedresult: exp");
        ctx.OperandStack.Push(result);
    }

    private static void Unary(PsExecutionContext ctx, Func<double, double> fn, string name)
    {
        double value = BaseOperators.PopNumber(ctx);
        if (value <= 0)
            throw new PsRangeError($"rangecheck: {name} of a non-positive number");
        ctx.OperandStack.Push(fn(value));
    }

    // A deterministic generator (the PLRM's rand is implementation-defined): the same input
    // always draws the same page.
    private static long NextRandom(PsExecutionContext ctx)
    {
        long seed = ctx.Extras.TryGetValue("_ps_rand", out var v) && v is long l ? l : 0L;
        seed = (seed * 16807L) % 2147483647L;
        if (seed <= 0)
            seed += 2147483646L;
        ctx.Extras["_ps_rand"] = seed;
        return seed;
    }

    private static void OpCvrs(PsExecutionContext ctx)
    {
        var target = ctx.OperandStack.Pop() as PsString ?? throw new PsTypeError("cvrs expects string");
        long radix = BaseOperators.PopInt(ctx);
        var value = ctx.OperandStack.Pop();
        if (radix is < 2 or > 36)
            throw new PsRangeError("cvrs radix");
        string text;
        if (radix == 10)
        {
            text = BaseOperators.FormatPsValue(value);
        }
        else
        {
            long number = value is double d ? Py.Int(d) : BaseOperators.CoerceInt(value);
            ulong bits = unchecked((ulong)(uint)number);
            if (bits == 0)
            {
                text = "0";
            }
            else
            {
                var chars = new List<char>();
                while (bits > 0)
                {
                    int digit = (int)(bits % (ulong)radix);
                    chars.Add((char)(digit < 10 ? '0' + digit : 'A' + digit - 10));
                    bits /= (ulong)radix;
                }
                chars.Reverse();
                text = new string([.. chars]);
            }
        }
        var bytes = Py.Latin1.GetBytes(text);
        if (bytes.Length > target.Value.Length)
            throw new PsRangeError("string too small for cvrs");
        bytes.CopyTo(target.Value, 0);
        ctx.OperandStack.Push(new PsString(bytes));
    }

    private static void OpSearch(PsExecutionContext ctx, bool anchored)
    {
        var seek = ctx.OperandStack.Pop() as PsString ?? throw new PsTypeError("search expects string");
        var str = ctx.OperandStack.Pop() as PsString ?? throw new PsTypeError("search expects string");
        var haystack = str.Value;
        int at = anchored
            ? (haystack.AsSpan().StartsWith(seek.Value) ? 0 : -1)
            : haystack.AsSpan().IndexOf(seek.Value);
        if (at < 0)
        {
            ctx.OperandStack.Push(str);
            ctx.OperandStack.Push(false);
            return;
        }
        int after = at + seek.Value.Length;
        ctx.OperandStack.Push(new PsString(haystack[after..]));
        ctx.OperandStack.Push(new PsString(haystack[at..after]));
        if (!anchored)
            ctx.OperandStack.Push(new PsString(haystack[..at]));
        ctx.OperandStack.Push(true);
    }

    private static void OpMaxLength(PsExecutionContext ctx)
    {
        if (ctx.OperandStack.Pop() is not PsDict d)
            throw new PsTypeError("maxlength expects dictionary");
        ctx.OperandStack.Push((long)Math.Max(d.Items.Count, 1) + 1);
    }

    private static void OpStore(PsExecutionContext ctx)
    {
        var value = ctx.OperandStack.Pop();
        var key = BaseOperators.CoerceKey(ctx.OperandStack.Pop());
        var stack = ctx.DictionaryStack.Items;
        for (int i = stack.Count - 1; i >= 0; i--)
        {
            if (stack[i].Items.ContainsKey(key))
            {
                stack[i].Items[key] = value;
                return;
            }
        }
        ctx.DictionaryStack.Peek().Items[key] = value;
    }

    private static void OpDictStack(PsExecutionContext ctx)
    {
        var target = ctx.OperandStack.Pop() as PsArray ?? throw new PsTypeError("dictstack expects array");
        var dicts = ctx.DictionaryStack.Items;
        if (target.Items.Count < dicts.Count)
            throw new PsRangeError("dictstack array too small");
        for (int i = 0; i < dicts.Count; i++)
            target.Items[i] = dicts[i];
        ctx.OperandStack.Push(new PsArray(target.Items.GetRange(0, dicts.Count)));
    }

    private static void OpReadLine(PsExecutionContext ctx)
    {
        var buffer = ctx.OperandStack.Pop() as PsString ?? throw new PsTypeError("readline expects string");
        var source = ctx.OperandStack.Pop();
        bool fromProgram = source is PsFile { Name: "currentfile" } or PsName { Value: "currentfile" } || source is "currentfile";
        if (!fromProgram || ctx.Systemdict.Get(PsInterpreter.TokenizerKey) is not PsTokenizer tokenizer)
        {
            ctx.OperandStack.Push(new PsString([]));
            ctx.OperandStack.Push(false);
            return;
        }
        var data = tokenizer.Data;
        tokenizer.SkipTokenTerminator();
        int start = tokenizer.Position;
        int end = start;
        while (end < tokenizer.Length && data[end] != '\n' && data[end] != '\r')
            end++;
        bool eol = end < tokenizer.Length;
        int next = end;
        if (eol)
        {
            next++;
            if (data[end] == '\r' && next < tokenizer.Length && data[next] == '\n')
                next++;
        }
        tokenizer.Position = next;
        int count = Math.Min(end - start, buffer.Value.Length);
        var line = data.AsSpan(start, count).ToArray();
        line.CopyTo(buffer.Value, 0);
        ctx.OperandStack.Push(new PsString(line));
        ctx.OperandStack.Push(eol);
    }

    private static void OpCShow(PsExecutionContext ctx, PsInterpreter interpreter)
    {
        // Simplified: the string is shown as by show; the procedure is not called per character.
        var text = ctx.OperandStack.Pop();
        ctx.OperandStack.Pop();
        ctx.OperandStack.Push(text);
        interpreter.ExecuteName("show", ctx);
    }
}

/// <summary>Deviation from the original, whose <c>getinterval</c> copied: in PostScript the
/// result of <c>getinterval</c> on an array shares its elements with the array, so storing into
/// the interval (<c>put</c>, <c>astore</c>, <c>putinterval</c>, <c>copy</c>) changes the array.
/// The port still hands out a copy, but remembers where it came from and writes through to the
/// original. A write to the original is not seen by an interval taken earlier.</summary>
internal static class SharedIntervals
{
    private sealed record Origin(List<object?> Parent, int Offset);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<List<object?>, Origin> Origins = new();

    /// <summary>The elements <paramref name="index"/> to <paramref name="index"/>+<paramref name="count"/>
    /// of <paramref name="items"/>, linked back to them.</summary>
    public static List<object?> Slice(List<object?> items, int index, int count)
    {
        var slice = items.GetRange(index, count);
        Origins.AddOrUpdate(slice, new Origin(items, index));
        return slice;
    }

    /// <summary>Stores <paramref name="value"/> at <paramref name="index"/>, and in every array
    /// the list was taken from by <see cref="Slice"/>.</summary>
    public static void Write(List<object?> items, int index, object? value)
    {
        items[index] = value;
        int depth = 0;
        while (Origins.TryGetValue(items, out var origin) && depth++ < 64)
        {
            index += origin.Offset;
            items = origin.Parent;
            if (index >= items.Count)
                return;
            items[index] = value;
        }
    }
}
