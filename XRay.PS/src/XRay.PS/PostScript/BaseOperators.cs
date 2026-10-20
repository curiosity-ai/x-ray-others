// Ported from ps/base_ops.py: stack, math, dictionary, string and control operators.
//
// The semantics are the original's, including where they differ from the PostScript
// manual: arithmetic always yields reals, `exit` is only caught by `loop`, `0 copy`
// duplicates the whole stack, and host errors (division by zero, ...) are not caught by
// `stopped`.

using System.Numerics;
using System.Text;
using XRay.PS.Internal;

namespace XRay.PS.PostScript;

internal sealed class LoopExitException : Exception
{
    public LoopExitException()
        : base("exit outside of loop")
    {
    }
}

/// <summary>Registers the core stack, math, dictionary and control operators.</summary>
public static class BaseOperators
{
    public static void Register(OperatorRegistry registry)
    {
        var interpreter = new PsInterpreter(registry);
        registry.Register("pop", ctx => ctx.OperandStack.Pop(), 1);
        registry.Register("dup", ctx => ctx.OperandStack.Push(ctx.OperandStack.Peek()), 1);
        registry.Register("exch", OpExch, 2);
        registry.Register("clear", ctx => ctx.OperandStack.Clear());
        registry.Register("copy", OpCopy, 1);
        registry.Register("roll", OpRoll, 2);
        registry.Register("index", OpIndex, 1);
        registry.Register("count", ctx => ctx.OperandStack.Push((long)ctx.OperandStack.Count));

        registry.Register("add", ctx => BinaryNumber(ctx, (a, b) => a + b), 2);
        registry.Register("sub", ctx => BinaryNumber(ctx, (a, b) => a - b), 2);
        registry.Register("mul", ctx => BinaryNumber(ctx, (a, b) => a * b), 2);
        registry.Register("div", ctx => BinaryNumber(ctx, Py.Div), 2);
        registry.Register("idiv", OpIdiv, 2);
        registry.Register("mod", ctx => BinaryNumber(ctx, Py.Mod), 2);
        registry.Register("neg", ctx => ctx.OperandStack.Push(-PopNumber(ctx)), 1);
        registry.Register("abs", ctx => ctx.OperandStack.Push(Math.Abs(PopNumber(ctx))), 1);
        registry.Register("sqrt", ctx => ctx.OperandStack.Push(Py.Sqrt(PopNumber(ctx))), 1);
        registry.Register("sin", ctx => ctx.OperandStack.Push(Math.Sin(Py.Radians(PopNumber(ctx)))), 1);
        registry.Register("cos", ctx => ctx.OperandStack.Push(Math.Cos(Py.Radians(PopNumber(ctx)))), 1);
        registry.Register("atan", OpAtan, 2);

        registry.Register("eq", ctx => { var b = ctx.OperandStack.Pop(); var a = ctx.OperandStack.Pop(); ctx.OperandStack.Push(PsOps.PyEquals(a, b)); }, 2);
        registry.Register("ne", ctx => { var b = ctx.OperandStack.Pop(); var a = ctx.OperandStack.Pop(); ctx.OperandStack.Push(!PsOps.PyEquals(a, b)); }, 2);
        registry.Register("lt", ctx => BinaryCompare(ctx, "<"), 2);
        registry.Register("gt", ctx => BinaryCompare(ctx, ">"), 2);
        registry.Register("le", ctx => BinaryCompare(ctx, "<="), 2);
        registry.Register("ge", ctx => BinaryCompare(ctx, ">="), 2);
        registry.Register("and", ctx => BitOp(ctx, "and"), 2);
        registry.Register("or", ctx => BitOp(ctx, "or"), 2);
        registry.Register("not", OpNot, 1);
        registry.Register("bitshift", OpBitshift, 2);

        registry.Register("array", OpArray, 1);
        registry.Register("packedarray", OpPackedArray, 1);
        registry.Register("matrix", ctx => ctx.OperandStack.Push(new PsArray([1.0, 0.0, 0.0, 1.0, 0.0, 0.0])));
        registry.Register("string", OpString, 1);
        registry.Register("readstring", ctx => OpReadString(ctx, false), 2);
        registry.Register("readhexstring", ctx => OpReadString(ctx, true), 2);
        registry.Register("token", OpToken, 1);
        registry.Register("status", ctx => { ctx.OperandStack.Pop(); ctx.OperandStack.Push(false); }, 1);
        registry.Register("flushfile", OpFlushFile, 1);
        registry.Register("closefile", ctx => ctx.OperandStack.Pop(), 1);
        registry.Register("currentfile", ctx => ctx.OperandStack.Push(new PsFile("currentfile", null, PsFile.NoFilters)));
        registry.Register("filter", OpFilter, 2);
        registry.Register("length", OpLength, 1);
        registry.Register("get", ctx => OpGet(ctx, registry), 2);
        registry.Register("put", OpPut, 3);
        registry.Register("getinterval", OpGetInterval, 3);
        registry.Register("putinterval", OpPutInterval, 3);
        registry.Register("aload", OpAload, 1);
        registry.Register("astore", OpAstore, 1);

        registry.Register("dict", ctx => { PopInt(ctx); ctx.OperandStack.Push(new PsDict()); }, 1);
        registry.Register("<<", ctx => ctx.OperandStack.Push(new PsMark("dictmark")));
        registry.Register(">>", OpDictClose);
        registry.Register("begin", OpBegin, 1);
        registry.Register("end", ctx => ctx.DictionaryStack.Pop());
        registry.Register("def", OpDef, 2);
        registry.Register("undef", OpUndef, 2);
        registry.Register("load", ctx => OpLoad(ctx, registry), 1);
        registry.Register("currentdict", ctx => ctx.OperandStack.Push(ctx.DictionaryStack.Peek()));
        registry.Register("countdictstack", ctx => ctx.OperandStack.Push((long)ctx.DictionaryStack.Count));
        registry.Register("known", ctx => OpKnown(ctx, registry), 2);

        registry.Register("exec", ctx => OpExec(ctx, interpreter), 1);
        registry.Register("if", ctx => OpIf(ctx, interpreter), 2);
        registry.Register("ifelse", ctx => OpIfElse(ctx, interpreter), 3);
        registry.Register("repeat", ctx => OpRepeat(ctx, interpreter), 2);
        registry.Register("for", ctx => OpFor(ctx, interpreter), 4);
        registry.Register("forall", ctx => OpForall(ctx, interpreter), 2);
        registry.Register("loop", ctx => OpLoop(ctx, interpreter), 1);
        registry.Register("exit", _ => throw new LoopExitException());
        registry.Register("bind", ctx => OpBind(ctx, registry), 1);

        registry.Register("mark", ctx => ctx.OperandStack.Push(new PsMark()));
        registry.Register("[", ctx => ctx.OperandStack.Push(new PsMark()));
        registry.Register("]", OpCloseMarkArray);
        registry.Register("cleartomark", OpClearToMark);
        registry.Register("counttomark", OpCountToMark);
        registry.Register("cvs", OpCvs, 2);
        registry.Register("print", ctx => ctx.OperandStack.Pop(), 1);
        registry.Register("=", ctx => ctx.OperandStack.Pop(), 1);
        registry.Register("==", ctx => ctx.OperandStack.Pop(), 1);
        registry.Register("flush", _ => { });
        registry.Register("cvi", OpCvi, 1);
        registry.Register("cvr", OpCvr, 1);
        registry.Register("cvn", OpCvn, 1);
        registry.Register("cvx", OpCvx, 1);
        registry.Register("xcheck", OpXcheck, 1);
        registry.Register("stopped", ctx => OpStopped(ctx, interpreter), 1);
        registry.Register("execform", ctx => OpExecForm(ctx, interpreter), 1);
        registry.Register("where", ctx => OpWhere(ctx, registry), 1);
        registry.Register("type", OpType, 1);
        registry.Register("round", ctx => ctx.OperandStack.Push(Py.Round(PopNumber(ctx))), 1);
        registry.Register("floor", ctx => ctx.OperandStack.Push(Py.Int(Math.Floor(PopNumber(ctx)))), 1);
        registry.Register("ceiling", ctx => ctx.OperandStack.Push(Py.Int(Math.Ceiling(PopNumber(ctx)))), 1);
        registry.Register("truncate", ctx => ctx.OperandStack.Push(Py.Int(PopNumber(ctx))), 1);
        registry.Register("cvlit", OpCvlit, 1);
        registry.Register("readonly", ctx => ctx.OperandStack.Push(ctx.OperandStack.Pop()), 1);
        registry.Register("findresource", OpFindResource, 2);
        registry.Register("defineresource", OpDefineResource, 3);
        registry.Register("resourcestatus", OpResourceStatus, 2);
        registry.Register("resourceforall", OpResourceForall, 3);
        registry.Register("currentpagedevice", OpCurrentPageDevice);
        registry.Register("setpagedevice", OpSetPageDevice, 1);
        registry.Register("setuserparams", OpSetUserParams, 1);
        registry.Register("currentglobal", ctx => ctx.OperandStack.Push(PsOps.Truthy(ctx.Systemdict.Get("__currentglobal__"))));
        registry.Register("setglobal", ctx => ctx.Systemdict.Items["__currentglobal__"] = PsOps.Truthy(ctx.OperandStack.Pop()), 1);
        registry.Register("currentpacking", ctx => ctx.OperandStack.Push(PsOps.Truthy(ctx.Systemdict.Get("__currentpacking__"))));
        registry.Register("setpacking", ctx => ctx.Systemdict.Items["__currentpacking__"] = PsOps.Truthy(ctx.OperandStack.Pop()), 1);
        registry.Register("quit", _ => throw new PsQuit());
    }

    private static void OpExch(PsExecutionContext ctx)
    {
        var b = ctx.OperandStack.Pop();
        var a = ctx.OperandStack.Pop();
        ctx.OperandStack.Push(b);
        ctx.OperandStack.Push(a);
    }

    private static void OpCopy(PsExecutionContext ctx)
    {
        var value = ctx.OperandStack.Pop();
        if (PsOps.IsNumeric(value))
        {
            long count = CoerceInt(value);
            if (count < 0)
                throw new PsRangeError("copy count must be non-negative");
            var items = ctx.OperandStack.Items;
            if (count > items.Count)
                throw new PsRangeError("stack underflow");
            // items[-count:] in the original: a count of 0 selects the whole stack.
            int start = count == 0 ? 0 : items.Count - (int)count;
            items.AddRange(items.GetRange(start, items.Count - start));
            return;
        }
        var destination = value;
        var source = ctx.OperandStack.Pop();
        switch (source)
        {
            case PsArray sa when destination is PsArray da:
                if (da.Items.Count < sa.Items.Count)
                    throw new PsRangeError("copy rangecheck");
                var snapshot = sa.Items.ToList();
                for (int i = 0; i < snapshot.Count; i++)
                    SharedIntervals.Write(da.Items, i, snapshot[i]);
                ctx.OperandStack.Push(da);
                return;
            case PsProcedure sp when destination is PsProcedure dp:
                if (dp.Items.Count < sp.Items.Count)
                    throw new PsRangeError("copy rangecheck");
                var procSnapshot = sp.Items.ToList();
                for (int i = 0; i < procSnapshot.Count; i++)
                    SharedIntervals.Write(dp.Items, i, procSnapshot[i]);
                ctx.OperandStack.Push(dp);
                return;
            // Deviation from the original, which required the same kind on both sides: an array
            // and a procedure (an executable array) copy into each other like arrays.
            case PsArray or PsProcedure when destination is PsArray or PsProcedure:
            {
                var from = source is PsArray fa ? fa.Items : ((PsProcedure)source).Items;
                var into = destination is PsArray ta ? ta.Items : ((PsProcedure)destination!).Items;
                if (into.Count < from.Count)
                    throw new PsRangeError("copy rangecheck");
                var copied = from.ToList();
                for (int i = 0; i < copied.Count; i++)
                    SharedIntervals.Write(into, i, copied[i]);
                ctx.OperandStack.Push(destination);
                return;
            }
            case PsString ss when destination is PsString ds:
                if (ds.Value.Length < ss.Value.Length)
                    throw new PsRangeError("copy rangecheck");
                var newValue = (byte[])ds.Value.Clone();
                ss.Value.CopyTo(newValue, 0);
                ds.Value = newValue;
                ctx.OperandStack.Push(ds);
                return;
            case PsDict sd when destination is PsDict dd:
                foreach (var (k, v) in sd.Items.ToList())
                    dd.Items[k] = v;
                ctx.OperandStack.Push(dd);
                return;
        }
        throw new PsTypeError("copy expects integer or compatible composite objects");
    }

    private static void OpRoll(PsExecutionContext ctx)
    {
        long shift = PopInt(ctx);
        long count = PopInt(ctx);
        if (count <= 0)
            return;
        var items = ctx.OperandStack.Items;
        if (count > items.Count)
            throw new PsRangeError("stack underflow");
        shift = ((shift % count) + count) % count;
        if (shift == 0)
            return;
        int n = (int)count;
        int s = (int)shift;
        int baseIndex = items.Count - n;
        var tail = items.GetRange(baseIndex, n);
        for (int i = 0; i < n; i++)
            items[baseIndex + i] = tail[(i - s + n) % n];
    }

    private static void OpIndex(PsExecutionContext ctx)
    {
        long index = PopInt(ctx);
        if (index < 0)
            throw new PsRangeError("index must be non-negative");
        var items = ctx.OperandStack.Items;
        if (index >= items.Count)
            throw new PsRangeError("stack underflow");
        ctx.OperandStack.Push(items[items.Count - 1 - (int)index]);
    }

    private static void BinaryNumber(PsExecutionContext ctx, Func<double, double, double> fn)
    {
        double b = PopNumber(ctx);
        double a = PopNumber(ctx);
        ctx.OperandStack.Push(fn(a, b));
    }

    private static void BinaryCompare(PsExecutionContext ctx, string op)
    {
        var b = ctx.OperandStack.Pop();
        var a = ctx.OperandStack.Pop();
        ctx.OperandStack.Push(PsOps.PyCompare(a, b, op));
    }

    private static void OpIdiv(PsExecutionContext ctx)
    {
        double b = PopNumber(ctx);
        double a = PopNumber(ctx);
        ctx.OperandStack.Push(Py.Int(Py.Div(a, b)));
    }

    private static void OpAtan(PsExecutionContext ctx)
    {
        double x = PopNumber(ctx);
        double y = PopNumber(ctx);
        ctx.OperandStack.Push(Py.Degrees(Math.Atan2(y, x)));
    }

    private static void BitOp(PsExecutionContext ctx, string op)
    {
        var b = ctx.OperandStack.Pop();
        var a = ctx.OperandStack.Pop();
        if (a is bool ba && b is bool bb)
        {
            ctx.OperandStack.Push(op == "and" ? ba && bb : ba || bb);
            return;
        }
        if (PsOps.IsInt(a) && PsOps.IsInt(b))
        {
            long la = AsLong(a);
            long lb = AsLong(b);
            ctx.OperandStack.Push(op == "and" ? la & lb : la | lb);
            return;
        }
        throw new PsTypeError($"{op} expects bool or int operands");
    }

    internal static long AsLong(object? value) => value switch
    {
        long l => l,
        bool b => b ? 1 : 0,
        int i => i,
        _ => throw new PsTypeError("integer expected"),
    };

    private static void OpNot(PsExecutionContext ctx)
    {
        var value = ctx.OperandStack.Pop();
        if (value is bool b)
        {
            ctx.OperandStack.Push(!b);
            return;
        }
        if (PsOps.IsInt(value))
        {
            ctx.OperandStack.Push(~AsLong(value));
            return;
        }
        throw new PsTypeError("not expects bool or int operand");
    }

    private static void OpBitshift(PsExecutionContext ctx)
    {
        long shift = PopInt(ctx);
        long value = PopInt(ctx);
        BigInteger result = shift >= 0 ? new BigInteger(value) << (int)Math.Min(shift, 4096) : new BigInteger(value) >> (int)Math.Min(-shift, 4096);
        ctx.OperandStack.Push(result > long.MaxValue ? long.MaxValue : result < long.MinValue ? long.MinValue : (long)result);
    }

    private static void OpArray(PsExecutionContext ctx)
    {
        long size = PopInt(ctx);
        if (size < 0)
            throw new PsRangeError("array size must be non-negative");
        if (size > 100_000_000)
            throw new HostErrorException("MemoryError", "array too large");
        Guard.Charge(size * 8, "array");
        ctx.OperandStack.Push(new PsArray(Enumerable.Repeat<object?>(null, (int)size).ToList()));
    }

    private static void OpPackedArray(PsExecutionContext ctx)
    {
        long size = PopInt(ctx);
        if (size < 0)
            throw new PsRangeError("packedarray size must be non-negative");
        if (size > ctx.OperandStack.Count)
            throw new PsRangeError("stack underflow");
        var items = new List<object?>((int)size);
        for (int i = 0; i < size; i++)
            items.Add(ctx.OperandStack.Pop());
        items.Reverse();
        ctx.OperandStack.Push(new PsArray(items));
    }

    private static void OpString(PsExecutionContext ctx)
    {
        long size = PopInt(ctx);
        if (size < 0)
            throw new PsRangeError("string size must be non-negative");
        if (size > int.MaxValue / 2)
            throw new HostErrorException("MemoryError", "string too large");
        Guard.Charge(size, "string");
        ctx.OperandStack.Push(new PsString(new byte[size]));
    }

    private static void OpFilter(PsExecutionContext ctx)
    {
        var filterObj = ctx.OperandStack.Pop();
        PsDict? parameters = null;
        var source = ctx.OperandStack.Pop();
        if (source is PsDict d)
        {
            parameters = d;
            source = ctx.OperandStack.Pop();
        }
        string filterName = filterObj switch
        {
            PsName n => n.Value,
            string s => s,
            _ => throw new PsTypeError("filter expects name"),
        };
        var paramsDict = parameters is not null ? FilterParamsToDict(parameters) : null;
        // Deviation from the original, which took the operand below the name for the source:
        // SubFileDecode reads `source count string /SubFileDecode filter` when no dictionary is
        // given (PLRM 3.13), and the original then left the count and the source on the stack.
        if (parameters is null && filterName is "SubFileDecode" && source is PsString eod
            && ctx.OperandStack.Count >= 2 && PsOps.IsNumeric(ctx.OperandStack.Peek()))
        {
            long eodCount = PopInt(ctx);
            source = ctx.OperandStack.Pop();
            paramsDict = new Dictionary<string, object?> { ["EODCount"] = eodCount, ["EODString"] = eod.Value };
        }
        var existing = new List<FilterSpec>();
        if (source is PsFile { Name: "currentfile" } file)
        {
            existing.AddRange(file.Filters);
        }
        else if (source is PsName { Value: "currentfile" } || source is "currentfile")
        {
        }
        else
        {
            // Unsupported sources are passed through as an empty file wrapper so prologs that
            // bind filter chains continue executing.
            ctx.OperandStack.Push(new PsFile(null, null, PsFile.NoFilters));
            return;
        }
        existing.Add(new FilterSpec(filterName, paramsDict));
        ctx.OperandStack.Push(new PsFile("currentfile", null, existing));
    }

    private static void OpFlushFile(PsExecutionContext ctx)
    {
        var file = ctx.OperandStack.Pop();
        // Deviation from the original, which ignored flushfile: flushing a SubFileDecode filter
        // over the program skips its data, up to and past the end-of-data string, so the job
        // goes on after it (Illustrator embeds its XMP packet this way). Plain currentfile is
        // still left alone.
        if (file is not PsFile { Name: "currentfile" } f || ctx.Systemdict.Get(PsInterpreter.TokenizerKey) is not PsTokenizer tokenizer)
            return;
        foreach (var filter in f.Filters)
        {
            if (Filters.NormalizeName(filter.Name) != "SubFileDecode" || filter.Params?.GetValueOrDefault("EODString") is not byte[] { Length: > 0 } eod)
                continue;
            long passes = filter.Params.GetValueOrDefault("EODCount") is long l && l > 0 ? l : 0;
            int pos = tokenizer.Position;
            for (long i = 0; i <= passes; i++)
            {
                int at = tokenizer.Data.AsSpan(pos, tokenizer.Length - pos).IndexOf(eod);
                if (at < 0)
                {
                    pos = tokenizer.Length;
                    break;
                }
                pos += at + eod.Length;
            }
            tokenizer.Position = pos;
            return;
        }
    }

    private static void OpReadString(PsExecutionContext ctx, bool hexMode)
    {
        var target = ctx.OperandStack.Pop();
        var source = ctx.OperandStack.Pop();
        if (target is not PsString targetString)
            throw new PsTypeError(hexMode ? "readhexstring expects string" : "readstring expects string");
        int count = targetString.Value.Length;
        var (data, remainingFilter, filterParams) = ReadFromSource(ctx, source, count, hexMode);
        bool complete = data.Length >= count;
        if (remainingFilter is null)
        {
            if (data.Length < count)
                data = [.. data, .. targetString.Value.AsSpan(data.Length)];
            else if (data.Length > count)
                data = data[..count];
            // Deviation from the original, which left the string it was given untouched: the
            // bytes read are stored in it, so a program that reads into a buffer and then uses
            // the buffer by name sees them. (A new array: strings are copied on write here.)
            targetString.Value = (byte[])data.Clone();
        }
        ctx.OperandStack.Push(new PsString(data, remainingFilter, filterParams));
        ctx.OperandStack.Push(complete);
    }

    private static void OpToken(PsExecutionContext ctx)
    {
        var source = ctx.OperandStack.Pop();
        if (source is PsString text)
        {
            // Deviation from the original, which answered false for a string: scan one object
            // from it and push the rest of the string, the object and true.
            var stringTokenizer = new PsTokenizer(text.Value);
            var parsed = new PsParser(stringTokenizer).ParseObject();
            if (ReferenceEquals(parsed, PsParser.EndOfInput))
            {
                ctx.OperandStack.Push(false);
                return;
            }
            int rest = stringTokenizer.Position;
            // The scanner consumes the one white-space character that ends a token.
            if (rest < text.Value.Length && text.Value[rest] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or 0)
                rest++;
            ctx.OperandStack.Push(new PsString(text.Value[rest..]));
            ctx.OperandStack.Push(parsed);
            ctx.OperandStack.Push(true);
            return;
        }
        if (source is not PsFile { Name: "currentfile" })
        {
            ctx.OperandStack.Push(false);
            return;
        }
        if (ctx.Systemdict.Get(PsInterpreter.TokenizerKey) is not PsTokenizer tokenizer)
        {
            ctx.OperandStack.Push(false);
            return;
        }
        var obj = new PsParser(tokenizer).ParseObject();
        if (ReferenceEquals(obj, PsParser.EndOfInput))
        {
            ctx.OperandStack.Push(false);
            return;
        }
        ctx.OperandStack.Push(obj);
        ctx.OperandStack.Push(true);
    }

    private static void OpLength(PsExecutionContext ctx)
    {
        var value = ctx.OperandStack.Pop();
        long length = value switch
        {
            PsArray a => a.Items.Count,
            PsProcedure p => p.Items.Count,
            PsDict d => d.Items.Count,
            PsString s => s.Value.Length,
            PsName n => n.Value.Length,
            string s => s.Length,
            _ => throw new PsTypeError("length expects array, dict, or string"),
        };
        ctx.OperandStack.Push(length);
    }

    private static void OpGet(PsExecutionContext ctx, OperatorRegistry registry)
    {
        var key = ctx.OperandStack.Pop();
        var container = ctx.OperandStack.Pop();
        switch (container)
        {
            case PsArray a:
                ctx.OperandStack.Push(a.Items[CheckIndex(CoerceInt(key), a.Items.Count, "get rangecheck")]);
                return;
            case PsProcedure p:
                ctx.OperandStack.Push(p.Items[CheckIndex(CoerceInt(key), p.Items.Count, "get rangecheck")]);
                return;
            case PsString s:
                ctx.OperandStack.Push((long)s.Value[CheckIndex(CoerceInt(key), s.Value.Length, "get rangecheck")]);
                return;
            case PsDict d:
                var name = CoerceKey(key);
                if (!d.Items.TryGetValue(name, out var value))
                {
                    if (IsSystemOperator(ctx, d, name, registry))
                    {
                        ctx.OperandStack.Push(new PsOperator((string)name));
                        return;
                    }
                    // Deviation from the original: findfont hands out a summary of a font defined
                    // by the program, so a key only the program's dictionary has (a Type 3 font's
                    // CharProcs, FontBBox, FontInfo) is read from that dictionary.
                    if (d.Items.TryGetValue("__font_dict__", out var original) && original is PsDict programFont
                        && programFont.Items.TryGetValue(name, out var fromProgram))
                    {
                        ctx.OperandStack.Push(fromProgram);
                        return;
                    }
                    // ... and a font the resolver supplied answers for the entries every font
                    // dictionary has (FontInfo with its underline metrics, FontBBox, PaintType).
                    if (d.Items.TryGetValue("__font_resource__", out var supplied) && supplied is Fonts.FontResource font
                        && SyntheticFontEntry(font, name) is { } synthetic)
                    {
                        ctx.OperandStack.Push(synthetic);
                        return;
                    }
                    throw new PsUndefinedError($"undefined key {PsOps.Str(name)}");
                }
                ctx.OperandStack.Push(value);
                return;
        }
        throw new PsTypeError("get expects array, dict, or string");
    }

    private static int CheckIndex(long index, int count, string message)
    {
        if (index < 0 || index >= count)
            throw new PsRangeError(message);
        return (int)index;
    }

    private static void OpPut(PsExecutionContext ctx)
    {
        var value = ctx.OperandStack.Pop();
        var key = ctx.OperandStack.Pop();
        var container = ctx.OperandStack.Pop();
        switch (container)
        {
            case PsArray a:
                SharedIntervals.Write(a.Items, CheckIndex(CoerceInt(key), a.Items.Count, "put rangecheck"), value);
                return;
            case PsProcedure p:
                SharedIntervals.Write(p.Items, CheckIndex(CoerceInt(key), p.Items.Count, "put rangecheck"), value);
                return;
            case PsString s:
            {
                int index = CheckIndex(CoerceInt(key), s.Value.Length, "put rangecheck");
                long byteValue;
                try
                {
                    byteValue = CoerceInt(value);
                }
                catch (PsTypeError)
                {
                    throw new PsTypeError("put expects int for string");
                }
                if (byteValue is < 0 or > 255)
                    throw new PsRangeError("put expects value in 0..255 for string");
                var newValue = (byte[])s.Value.Clone();
                newValue[index] = (byte)byteValue;
                s.Value = newValue;
                return;
            }
            case PsDict d:
                d.Items[CoerceKey(key)] = value;
                return;
        }
        throw new PsTypeError("put expects array, procedure, dict, or string");
    }

    private static void OpGetInterval(PsExecutionContext ctx)
    {
        long count = PopInt(ctx);
        long index = PopInt(ctx);
        var container = ctx.OperandStack.Pop();
        if (count < 0 || index < 0)
            throw new PsRangeError("getinterval expects non-negative index and count");
        switch (container)
        {
            case PsArray a:
                if (index + count > a.Items.Count)
                    throw new PsRangeError("getinterval rangecheck");
                ctx.OperandStack.Push(new PsArray(SharedIntervals.Slice(a.Items, (int)index, (int)count)));
                return;
            case PsProcedure p:
                if (index + count > p.Items.Count)
                    throw new PsRangeError("getinterval rangecheck");
                ctx.OperandStack.Push(new PsProcedure(SharedIntervals.Slice(p.Items, (int)index, (int)count)));
                return;
            case PsString s:
                if (index + count > s.Value.Length)
                    throw new PsRangeError("getinterval rangecheck");
                ctx.OperandStack.Push(new PsString(s.Value.AsSpan((int)index, (int)count).ToArray()));
                return;
        }
        throw new PsTypeError("getinterval expects array or string");
    }

    private static void OpPutInterval(PsExecutionContext ctx)
    {
        var source = ctx.OperandStack.Pop();
        long index = PopInt(ctx);
        var container = ctx.OperandStack.Pop();
        if (index < 0)
            throw new PsRangeError("putinterval expects non-negative index");
        if (container is PsArray or PsProcedure)
        {
            var target = container is PsArray a ? a.Items : ((PsProcedure)container).Items;
            var sourceItems = source switch
            {
                PsArray sa => sa.Items,
                PsProcedure sp => sp.Items,
                _ => throw new PsTypeError("putinterval expects array source"),
            };
            long end = index + sourceItems.Count;
            if (end > target.Count)
                throw new PsRangeError("putinterval rangecheck");
            var snapshot = sourceItems.ToList();
            for (int i = 0; i < snapshot.Count; i++)
                SharedIntervals.Write(target, (int)index + i, snapshot[i]);
            return;
        }
        if (container is PsString s)
        {
            byte[] sourceBytes = source switch
            {
                PsString ss => ss.Value,
                string str => Py.Latin1.GetBytes(str),
                _ => throw new PsTypeError("putinterval expects string source"),
            };
            long end = index + sourceBytes.Length;
            if (end > s.Value.Length)
                throw new PsRangeError("putinterval rangecheck");
            var newValue = (byte[])s.Value.Clone();
            sourceBytes.CopyTo(newValue, (int)index);
            s.Value = newValue;
            return;
        }
        throw new PsTypeError("putinterval expects array or string");
    }

    private static void OpAload(PsExecutionContext ctx)
    {
        var array = ctx.OperandStack.Pop();
        var items = array switch
        {
            PsArray a => a.Items,
            PsProcedure p => p.Items,
            _ => throw new PsTypeError("aload expects array"),
        };
        foreach (var item in items.ToList())
            ctx.OperandStack.Push(item);
        ctx.OperandStack.Push(array);
    }

    private static void OpAstore(PsExecutionContext ctx)
    {
        var array = ctx.OperandStack.Pop();
        var items = array switch
        {
            PsArray a => a.Items,
            PsProcedure p => p.Items,
            _ => throw new PsTypeError("astore expects array"),
        };
        for (int index = items.Count - 1; index >= 0; index--)
            SharedIntervals.Write(items, index, ctx.OperandStack.Pop());
        ctx.OperandStack.Push(array);
    }

    private static void OpDictClose(PsExecutionContext ctx)
    {
        var items = new List<object?>();
        bool found = false;
        while (ctx.OperandStack.Count > 0)
        {
            var value = ctx.OperandStack.Pop();
            if (value is PsMark { Kind: "dictmark" })
            {
                found = true;
                break;
            }
            items.Add(value);
        }
        if (!found)
            throw new PsRangeError(">> without <<");
        items.Reverse();
        if (items.Count % 2 != 0)
            throw new PsRangeError("dict stack mismatch");
        var dict = new PsDict();
        for (int i = 0; i < items.Count; i += 2)
            dict.Items[CoerceKey(items[i])] = items[i + 1];
        ctx.OperandStack.Push(dict);
    }

    private static void OpBegin(PsExecutionContext ctx)
    {
        var value = ctx.OperandStack.Pop();
        if (value is not PsDict d)
            throw new PsTypeError("begin expects dictionary");
        ctx.DictionaryStack.Push(d);
    }

    private static void OpDef(PsExecutionContext ctx)
    {
        var value = ctx.OperandStack.Pop();
        var key = ctx.OperandStack.Pop();
        if (key is not PsName && value is PsProcedure proc && proc.Items.Count > 0 && proc.Items[0] is PsName { Literal: true } head)
        {
            key = head;
            value = new PsProcedure(proc.Items.Skip(1).ToList());
        }
        ctx.DictionaryStack.Peek().Items[CoerceKey(key)] = value;
    }

    private static void OpUndef(PsExecutionContext ctx)
    {
        var key = CoerceKey(ctx.OperandStack.Pop());
        var dictionary = ctx.OperandStack.Pop();
        if (dictionary is not PsDict d)
            throw new PsTypeError("undef expects dictionary");
        d.Items.Remove(key);
    }

    private static void OpLoad(PsExecutionContext ctx, OperatorRegistry registry)
    {
        var key = ctx.OperandStack.Pop();
        string? keyName = key switch
        {
            PsName n => n.Value,
            string s => s,
            PsOperator op => op.Name,
            _ => null,
        };
        if (keyName is null)
        {
            // Deviation from the original, which accepted only names: any key can be loaded
            // (a string is looked up as the name it spells, a number as itself).
            var anyKey = key is null || key is PsDict or PsArray or PsProcedure or PsMark ? throw new PsTypeError("load expects name") : CoerceKey(key);
            if (anyKey is string spelled)
            {
                keyName = spelled;
            }
            else
            {
                var stack = ctx.DictionaryStack.Items;
                for (int i = stack.Count - 1; i >= 0; i--)
                {
                    if (stack[i].Items.TryGetValue(anyKey, out var found))
                    {
                        ctx.OperandStack.Push(found);
                        return;
                    }
                }
                throw new PsUndefinedError($"undefined key {PsOps.Str(anyKey)}");
            }
        }
        string name = keyName;
        if (PsInterpreter.LookupDict(name, ctx, out var value))
        {
            ctx.OperandStack.Push(value);
            return;
        }
        // Deviation: a fallback operator (see ExtraOperators) loads like a built-in one.
        if (registry.Get(name) is not null || registry.GetFallback(name) is not null)
        {
            ctx.OperandStack.Push(new PsOperator(name));
            return;
        }
        throw new PsUndefinedError($"undefined name {name}");
    }

    private static void OpKnown(PsExecutionContext ctx, OperatorRegistry registry)
    {
        var key = CoerceKey(ctx.OperandStack.Pop());
        var dictionary = ctx.OperandStack.Pop();
        if (dictionary is not PsDict d)
            throw new PsTypeError("known expects dictionary");
        ctx.OperandStack.Push(d.Items.ContainsKey(key) || IsSystemOperator(ctx, d, key, registry));
    }

    private static object? SyntheticFontEntry(Fonts.FontResource font, object key)
    {
        double units = font.UnitsPerEm > 0 ? font.UnitsPerEm : 1000.0;
        double Em(double thousandths) => thousandths * units / 1000.0;
        switch (key)
        {
            case "FontInfo":
                var info = new PsDict();
                info.Items["FamilyName"] = PsString.FromText(font.Name);
                info.Items["FullName"] = PsString.FromText(font.Name);
                info.Items["Weight"] = PsString.FromText(font.Name.Contains("Bold", StringComparison.Ordinal) ? "Bold" : "Medium");
                info.Items["ItalicAngle"] = 0.0;
                info.Items["isFixedPitch"] = font.Name.StartsWith("Courier", StringComparison.Ordinal);
                info.Items["UnderlinePosition"] = Em(-100);
                info.Items["UnderlineThickness"] = Em(50);
                return info;
            case "FontBBox":
                return new PsArray([Em(-200), Em(-250), Em(1200), Em(950)]);
            case "PaintType":
                return 0L;
            case "StrokeWidth":
                return 0.0;
            default:
                return null;
        }
    }

    // Deviation (see OpWhere): systemdict answers for the built-in operators too.
    private static bool IsSystemOperator(PsExecutionContext ctx, PsDict d, object key, OperatorRegistry registry) =>
        ReferenceEquals(d, ctx.Systemdict) && key is string name && (registry.Get(name) is not null || registry.GetFallback(name) is not null);

    private static void OpExec(PsExecutionContext ctx, PsInterpreter interpreter)
    {
        var value = ctx.OperandStack.Pop();
        switch (value)
        {
            case PsProcedure proc:
                interpreter.ExecuteProcedure(proc, ctx);
                return;
            case PsName n:
                interpreter.ExecuteName(n.Value, ctx);
                return;
            case PsOperator op:
                interpreter.ExecuteName(op.Name, ctx);
                return;
            case string s:
                interpreter.ExecuteName(s, ctx);
                return;
        }
        // Deviation from the original, which raised a typecheck: per the PLRM, exec of an object
        // that is not executable (a number, a string, a dictionary) pushes it back.
        ctx.OperandStack.Push(value);
    }

    private static void OpStopped(PsExecutionContext ctx, PsInterpreter interpreter)
    {
        var value = ctx.OperandStack.Pop();
        try
        {
            switch (value)
            {
                case PsProcedure proc:
                    interpreter.ExecuteProcedure(proc, ctx);
                    break;
                case PsName n:
                    interpreter.ExecuteName(n.Value, ctx);
                    break;
                case PsOperator op:
                    interpreter.ExecuteName(op.Name, ctx);
                    break;
                case string s:
                    interpreter.ExecuteName(s, ctx);
                    break;
                default:
                    throw new PsTypeError("stopped expects executable object");
            }
        }
        catch (PsError ex) when (ex is not PsResourceLimitError)
        {
            ctx.OperandStack.Push(true);
            return;
        }
        ctx.OperandStack.Push(false);
    }

    private static void OpExecForm(PsExecutionContext ctx, PsInterpreter interpreter)
    {
        var form = ctx.OperandStack.Pop();
        if (form is not PsDict d)
            return;
        if (d.Get("PaintProc") is not PsProcedure paintProc)
            return;
        // Deviation from the original, which ran PaintProc bare: per the PLRM execform saves the
        // graphics state, concatenates the form's Matrix, clips to its BBox and hands PaintProc
        // the form dictionary on the stack.
        interpreter.ExecuteName("gsave", ctx);
        int depth = ctx.GraphicsStateStack.Count;
        try
        {
            if (d.Get("Matrix") is PsArray matrix)
            {
                ctx.OperandStack.Push(matrix);
                interpreter.ExecuteName("concat", ctx);
            }
            if (d.Get("BBox") is PsArray { Items.Count: 4 } bbox && bbox.Items.All(PsOps.IsNumeric))
            {
                double x0 = PsOps.ToDouble(bbox.Items[0]), y0 = PsOps.ToDouble(bbox.Items[1]);
                double x1 = PsOps.ToDouble(bbox.Items[2]), y1 = PsOps.ToDouble(bbox.Items[3]);
                interpreter.ExecuteName("newpath", ctx);
                foreach (double v in (double[])[Math.Min(x0, x1), Math.Min(y0, y1), Math.Abs(x1 - x0), Math.Abs(y1 - y0)])
                    ctx.OperandStack.Push(v);
                interpreter.ExecuteName("rectpath", ctx);
                interpreter.ExecuteName("clip", ctx);
                interpreter.ExecuteName("newpath", ctx);
            }
            ctx.OperandStack.Push(d);
            interpreter.ExecuteProcedure(paintProc, ctx);
        }
        finally
        {
            while (ctx.GraphicsStateStack.Count >= depth && ctx.GraphicsStateStack.Count > 1)
                interpreter.ExecuteName("grestore", ctx);
        }
    }

    private static void OpIf(PsExecutionContext ctx, PsInterpreter interpreter)
    {
        var proc = ctx.OperandStack.Pop();
        bool condition = PopBool(ctx);
        if (condition)
        {
            if (proc is not PsProcedure p)
                throw new PsTypeError("if expects procedure");
            interpreter.ExecuteProcedure(p, ctx);
        }
    }

    private static void OpIfElse(PsExecutionContext ctx, PsInterpreter interpreter)
    {
        var procFalse = ctx.OperandStack.Pop();
        var procTrue = ctx.OperandStack.Pop();
        bool condition = PopBool(ctx);
        var proc = condition ? procTrue : procFalse;
        if (proc is not PsProcedure p)
            throw new PsTypeError("ifelse expects procedure");
        interpreter.ExecuteProcedure(p, ctx);
    }

    private static void OpRepeat(PsExecutionContext ctx, PsInterpreter interpreter)
    {
        var proc = ctx.OperandStack.Pop();
        long count = PopInt(ctx);
        if (proc is not PsProcedure p)
            throw new PsTypeError("repeat expects procedure");
        for (long i = 0; i < count; i++)
            interpreter.ExecuteProcedure(p, ctx);
    }

    private static void OpFor(PsExecutionContext ctx, PsInterpreter interpreter)
    {
        var proc = ctx.OperandStack.Pop();
        double limit = PopNumber(ctx);
        double increment = PopNumber(ctx);
        double initial = PopNumber(ctx);
        if (proc is not PsProcedure p)
            throw new PsTypeError("for expects procedure");
        if (increment == 0)
            return;
        double value = initial;
        if (increment > 0)
        {
            while (value <= limit + 1e-9)
            {
                ctx.OperandStack.Push(value);
                interpreter.ExecuteProcedure(p, ctx);
                value += increment;
            }
        }
        else
        {
            while (value >= limit - 1e-9)
            {
                ctx.OperandStack.Push(value);
                interpreter.ExecuteProcedure(p, ctx);
                value += increment;
            }
        }
    }

    private static void OpForall(PsExecutionContext ctx, PsInterpreter interpreter)
    {
        var proc = ctx.OperandStack.Pop();
        var container = ctx.OperandStack.Pop();
        if (proc is not PsProcedure p)
            throw new PsTypeError("forall expects procedure");
        switch (container)
        {
            case PsArray a:
            {
                var items = a.Items;
                for (int i = 0; i < items.Count; i++)
                {
                    ctx.OperandStack.Push(items[i]);
                    interpreter.ExecuteProcedure(p, ctx);
                }
                return;
            }
            // Deviation from the original, which rejected it: forall over a procedure (an
            // executable array) visits its elements like an array's.
            case PsProcedure procedure:
            {
                var items = procedure.Items;
                for (int i = 0; i < items.Count; i++)
                {
                    ctx.OperandStack.Push(items[i]);
                    interpreter.ExecuteProcedure(p, ctx);
                }
                return;
            }
            case PsString s:
            {
                var bytes = s.Value;
                foreach (byte b in bytes)
                {
                    ctx.OperandStack.Push((long)b);
                    interpreter.ExecuteProcedure(p, ctx);
                }
                return;
            }
            case PsDict d:
            {
                var items = d.Items;
                int initialCount = items.Count;
                for (int i = 0; i < items.Count; i++)
                {
                    if (items.Count != initialCount)
                        throw new HostErrorException("RuntimeError", "dictionary changed size during iteration");
                    var (key, value) = items.GetAt(i);
                    ctx.OperandStack.Push(key is string sk ? new PsName(sk, true) : key);
                    ctx.OperandStack.Push(value);
                    interpreter.ExecuteProcedure(p, ctx);
                }
                if (items.Count != initialCount)
                    throw new HostErrorException("RuntimeError", "dictionary changed size during iteration");
                return;
            }
        }
        throw new PsTypeError("forall expects array, dict, or string");
    }

    private static void OpLoop(PsExecutionContext ctx, PsInterpreter interpreter)
    {
        var proc = ctx.OperandStack.Pop();
        if (proc is not PsProcedure p)
            throw new PsTypeError("loop expects procedure");
        while (true)
        {
            try
            {
                interpreter.ExecuteProcedure(p, ctx);
            }
            catch (LoopExitException)
            {
                break;
            }
        }
    }

    private static void OpBind(PsExecutionContext ctx, OperatorRegistry registry)
    {
        var proc = ctx.OperandStack.Pop();
        if (proc is not PsProcedure p)
            throw new PsTypeError("bind expects procedure");
        BindProcedure(p, ctx, registry);
        ctx.OperandStack.Push(p);
    }

    internal static void BindProcedure(PsProcedure proc, PsExecutionContext ctx, OperatorRegistry registry)
    {
        var bound = new List<object?>(proc.Items.Count);
        foreach (var item in proc.Items)
        {
            if (item is PsProcedure inner)
            {
                PsInterpreter.CountFrames(1, () => BindProcedure(inner, ctx, registry));
                bound.Add(inner);
                continue;
            }
            if (item is PsName { Literal: false } name)
            {
                var replacement = BindLookup(name.Value, ctx, registry);
                if (replacement is not null)
                {
                    bound.Add(replacement);
                    continue;
                }
            }
            bound.Add(item);
        }
        proc.Items = bound;
    }

    private static PsOperator? BindLookup(string name, PsExecutionContext ctx, OperatorRegistry registry)
    {
        if (PsInterpreter.LookupDict(name, ctx, out var value))
            return value as PsOperator;
        if (registry.Get(name) is not null)
            return new PsOperator(name);
        return null;
    }

    private static void OpClearToMark(PsExecutionContext ctx)
    {
        while (true)
        {
            if (ctx.OperandStack.Pop() is PsMark)
                return;
        }
    }

    private static void OpCountToMark(PsExecutionContext ctx)
    {
        var items = ctx.OperandStack.Items;
        long count = 0;
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (items[i] is PsMark)
            {
                ctx.OperandStack.Push(count);
                return;
            }
            count++;
        }
        throw new PsRangeError("unmatched mark");
    }

    private static void OpCloseMarkArray(PsExecutionContext ctx)
    {
        var items = new List<object?>();
        while (ctx.OperandStack.Count > 0)
        {
            var item = ctx.OperandStack.Pop();
            if (item is PsMark { Kind: "mark" })
            {
                items.Reverse();
                ctx.OperandStack.Push(new PsArray(items));
                return;
            }
            items.Add(item);
        }
        throw new PsRangeError("] without mark");
    }

    private static void OpCvs(PsExecutionContext ctx)
    {
        var container = ctx.OperandStack.Pop();
        var value = ctx.OperandStack.Pop();
        if (container is not PsString s)
            throw new PsTypeError("cvs expects string");
        string text = FormatPsValue(value);
        var encoded = new byte[text.Length];
        int n = 0;
        foreach (var rune in text.EnumerateRunes())
            encoded[n++] = rune.Value < 128 ? (byte)rune.Value : (byte)'?';
        encoded = encoded[..n];
        if (encoded.Length > s.Value.Length)
            throw new PsRangeError("string too small for cvs");
        ctx.OperandStack.Push(new PsString(encoded));
    }

    private static void OpCvi(PsExecutionContext ctx)
    {
        var value = ctx.OperandStack.Pop();
        if (value is bool b)
        {
            ctx.OperandStack.Push(b ? 1L : 0L);
            return;
        }
        if (PsOps.IsNumeric(value))
        {
            ctx.OperandStack.Push(value is long l ? l : Py.Int(PsOps.ToDouble(value)));
            return;
        }
        if (value is PsString s)
        {
            string text = Py.Strip(s.Text);
            if (Py.TryFloat(text, out double d))
            {
                ctx.OperandStack.Push(Py.Int(d));
                return;
            }
            throw new PsTypeError("cvi expects numeric string");
        }
        throw new PsTypeError("cvi expects number or string");
    }

    private static void OpCvr(PsExecutionContext ctx)
    {
        var value = ctx.OperandStack.Pop();
        if (PsOps.IsNumeric(value))
        {
            ctx.OperandStack.Push(PsOps.ToDouble(value));
            return;
        }
        if (value is PsString s)
        {
            if (Py.TryFloat(Py.Strip(s.Text), out double d))
            {
                ctx.OperandStack.Push(d);
                return;
            }
            throw new PsTypeError("cvr expects numeric string");
        }
        throw new PsTypeError("cvr expects number or string");
    }

    private static void OpCvn(PsExecutionContext ctx)
    {
        var value = ctx.OperandStack.Pop();
        if (value is not PsString s)
            throw new PsTypeError("cvn expects string");
        ctx.OperandStack.Push(new PsName(s.Text, true));
    }

    private static void OpCvx(PsExecutionContext ctx)
    {
        var value = ctx.OperandStack.Pop();
        switch (value)
        {
            case PsName n:
                ctx.OperandStack.Push(new PsName(n.Value, false));
                return;
            case PsArray a:
                ctx.OperandStack.Push(new PsProcedure(a.Items.ToList()));
                return;
        }
        ctx.OperandStack.Push(value);
    }

    private static void OpXcheck(PsExecutionContext ctx)
    {
        var value = ctx.OperandStack.Pop();
        ctx.OperandStack.Push(value is PsProcedure or PsOperator || value is PsName { Literal: false });
    }

    private static void OpWhere(PsExecutionContext ctx, OperatorRegistry registry)
    {
        var name = CoerceKey(ctx.OperandStack.Pop());
        var items = ctx.DictionaryStack.Items;
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (items[i].Items.ContainsKey(name))
            {
                ctx.OperandStack.Push(items[i]);
                ctx.OperandStack.Push(true);
                return;
            }
        }
        // Deviation from the original, which kept operators outside every dictionary: in
        // PostScript they live in systemdict, so `/findfont where` finds it there. Answered only
        // after the dictionaries, so a name a program defined itself is found as before.
        if (name is string op && (registry.Get(op) is not null || registry.GetFallback(op) is not null))
        {
            ctx.OperandStack.Push(ctx.Systemdict);
            ctx.OperandStack.Push(true);
            return;
        }
        ctx.OperandStack.Push(false);
    }

    private static void OpType(PsExecutionContext ctx)
    {
        var value = ctx.OperandStack.Pop();
        string t = value switch
        {
            PsArray => "arraytype",
            PsDict => "dicttype",
            PsString => "stringtype",
            PsProcedure => "arraytype",
            PsName => "nametype",
            PsOperator => "operatortype",
            bool => "booleantype",
            long or int => "integertype",
            double => "realtype",
            null => "nulltype",
            _ => "unknowntype",
        };
        ctx.OperandStack.Push(new PsName(t, true));
    }

    private static void OpCvlit(PsExecutionContext ctx)
    {
        var value = ctx.OperandStack.Pop();
        ctx.OperandStack.Push(value is PsName n ? new PsName(n.Value, true) : value);
    }

    private static void OpFindResource(PsExecutionContext ctx)
    {
        var category = CoerceKey(ctx.OperandStack.Pop());
        var key = CoerceKey(ctx.OperandStack.Pop());
        var resources = ResourceStore(ctx);
        if (resources.TryGetValue(category, out var categoryDict) && categoryDict.Items.TryGetValue(key, out var value))
        {
            ctx.OperandStack.Push(value);
            return;
        }
        // Keep execution moving even when optional resources are unavailable.
        ctx.OperandStack.Push(new PsDict());
    }

    private static void OpDefineResource(PsExecutionContext ctx)
    {
        var category = CoerceKey(ctx.OperandStack.Pop());
        var instance = ctx.OperandStack.Pop();
        var key = CoerceKey(ctx.OperandStack.Pop());
        var resources = ResourceStore(ctx);
        if (!resources.TryGetValue(category, out var categoryDict))
        {
            categoryDict = new PsDict();
            resources[category] = categoryDict;
        }
        categoryDict.Items[key] = instance;
        ctx.OperandStack.Push(instance);
    }

    private static void OpResourceStatus(PsExecutionContext ctx)
    {
        var category = CoerceKey(ctx.OperandStack.Pop());
        var key = CoerceKey(ctx.OperandStack.Pop());
        var resources = ResourceStore(ctx);
        if (resources.TryGetValue(category, out var categoryDict) && categoryDict.Items.ContainsKey(key))
        {
            ctx.OperandStack.Push(0L);
            ctx.OperandStack.Push(0L);
            ctx.OperandStack.Push(true);
            return;
        }
        ctx.OperandStack.Push(false);
    }

    private static void OpResourceForall(PsExecutionContext ctx)
    {
        var proc = ctx.OperandStack.Pop();
        ctx.OperandStack.Pop();
        ctx.OperandStack.Pop();
        if (proc is not PsProcedure)
            throw new PsTypeError("resourceforall expects procedure");
    }

    private static void OpCurrentPageDevice(PsExecutionContext ctx)
    {
        ctx.OperandStack.Push(ctx.Systemdict.Get("pagedevice") as PsDict ?? new PsDict());
    }

    private static void OpSetPageDevice(PsExecutionContext ctx)
    {
        if (ctx.OperandStack.Pop() is PsDict d)
            ctx.Systemdict.Items["pagedevice"] = d;
    }

    private static void OpSetUserParams(PsExecutionContext ctx)
    {
        if (ctx.OperandStack.Pop() is not PsDict value)
            throw new PsTypeError("setuserparams expects dictionary");
        if (ctx.Systemdict.Get("__userparams__") is not PsDict parameters)
        {
            parameters = new PsDict();
            ctx.Systemdict.Items["__userparams__"] = parameters;
        }
        foreach (var (k, v) in value.Items.ToList())
            parameters.Items[k] = v;
    }

    internal static (byte[] Data, string? RemainingFilter, Dictionary<string, object?>? Params) ReadFromSource(
        PsExecutionContext ctx, object? source, int count, bool hexMode)
    {
        if (ctx.Systemdict.Get(PsInterpreter.TokenizerKey) is not PsTokenizer tokenizer)
            return ([], null, null);
        IReadOnlyList<FilterSpec> filters;
        if (source is PsFile { Name: "currentfile" } file)
            filters = file.Filters;
        else if (source is PsName { Value: "currentfile" } || source is "currentfile")
            filters = PsFile.NoFilters;
        else
            return ([], null, null);
        if (hexMode && filters.Count == 0)
            return (tokenizer.ReadAsciiHexDecoded(count).Data, null, null);
        if (filters.Count == 0)
        {
            // Deviation from the original, which started reading at the white-space character
            // that ended the token before the data, so binary data came out one byte late.
            tokenizer.SkipTokenTerminator();
            return (tokenizer.ReadRaw(count), null, null);
        }
        string firstFilter = Filters.NormalizeName(filters[0].Name);
        // Deviation from the original, which decoded an ASCII85 filter to its end-of-data mark on
        // every read (so a second read of the same filter decoded the program text after it) and
        // a RunLengthDecode filter from a fixed count of encoded bytes: the filter's data is read
        // to its end-of-data mark once, decoded, kept with the filter object, and later reads
        // continue from where the previous one stopped.
        if (firstFilter is "ASCII85Decode" or "RunLengthDecode" && source is PsFile filterFile)
        {
            if (!DecodedFilters.TryGetValue(filterFile, out var pending))
            {
                tokenizer.SkipTokenTerminator();
                var encodedAll = firstFilter == "ASCII85Decode" ? tokenizer.ReadUntilAscii85Eod() : tokenizer.ReadRunLengthSource();
                var all = Filters.Decode(encodedAll, filters, allowEncoded: true);
                if (all.RemainingFilter is not null)
                    return (all.Data, all.RemainingFilter, all.Params);
                pending = new DecodedFilter(all.Data);
                DecodedFilters.AddOrUpdate(filterFile, pending);
            }
            int take = Math.Min(count, pending.Data.Length - pending.Offset);
            var chunk = pending.Data.AsSpan(pending.Offset, take).ToArray();
            pending.Offset += take;
            return (chunk, null, null);
        }
        byte[] encoded;
        if (firstFilter == "ASCIIHexDecode")
            encoded = count > 0 ? tokenizer.ReadAsciiHexSource(count) : tokenizer.ReadUntilAsciiHexEod();
        else if (firstFilter == "ASCII85Decode")
            encoded = tokenizer.ReadUntilAscii85Eod();
        else
            encoded = tokenizer.ReadRaw(count);
        var decoded = Filters.Decode(encoded, filters, allowEncoded: true);
        return (decoded.Data, decoded.RemainingFilter, decoded.Params);
    }

    private sealed class DecodedFilter(byte[] data)
    {
        public byte[] Data { get; } = data;

        public int Offset { get; set; }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PsFile, DecodedFilter> DecodedFilters = new();

    internal static Dictionary<string, object?> FilterParamsToDict(PsDict value)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, raw) in value.Items)
            result[key as string ?? PsOps.Str(key)] = ToFilterParam(raw);
        return result;
    }

    private static object? ToFilterParam(object? value) => value switch
    {
        PsName n => n.Value,
        PsString s => s.Value,
        PsArray a => a.Items.Select(ToFilterParam).ToList(),
        PsDict d => FilterParamsToDict(d),
        _ => value,
    };

    internal static double PopNumber(PsExecutionContext ctx)
    {
        var value = ctx.OperandStack.Pop();
        if (PsOps.IsNumeric(value))
            return PsOps.ToDouble(value);
        throw new PsTypeError("number expected");
    }

    internal static long PopInt(PsExecutionContext ctx) => CoerceInt(ctx.OperandStack.Pop());

    internal static long CoerceInt(object? value)
    {
        if (value is PsName n)
            value = n.Value;
        switch (value)
        {
            case bool b:
                return b ? 1 : 0;
            case long l:
                return l;
            case int i:
                return i;
            case double d:
                return Py.Int(d);
            case string s:
            {
                string text = Py.Strip(s);
                long? radix = ParseRadixIntegerText(text);
                if (radix is not null)
                    return radix.Value;
                if (Py.TryInt(text, 10, out long parsed))
                    return parsed;
                throw new PsTypeError("integer expected");
            }
        }
        throw new PsTypeError("integer expected");
    }

    /// <summary>The dictionary key a value stands for (<c>_coerce_key</c>).</summary>
    internal static object CoerceKey(object? value) => value switch
    {
        PsName n => n.Value,
        PsOperator o => o.Name,
        PsString s => s.Text,
        string s => s,
        bool b => b,
        long l => l,
        int i => (long)i,
        double d => Math.Floor(d) == d && !double.IsInfinity(d) ? Py.Int(d) : d,
        _ => throw new PsTypeError("key expected"),
    };

    internal static bool PopBool(PsExecutionContext ctx)
    {
        var value = ctx.OperandStack.Pop();
        if (value is bool b)
            return b;
        if (PsOps.IsNumeric(value))
            return PsOps.ToDouble(value) != 0;
        throw new PsTypeError("boolean expected");
    }

    internal static string FormatPsValue(object? value)
    {
        switch (value)
        {
            case bool b:
                return b ? "true" : "false";
            case long l:
                return Py.Str(l);
            case double d:
            {
                if (Math.Abs(d) <= 1e-6)
                    return "0";
                long asInt = Py.Int(d);
                if ((double)asInt == d)
                    return Py.Str(asInt);
                string text = Py.FormatG(d, 6);
                if (text.Contains('e') || text.Contains('E'))
                    text = Py.FormatF(d, 6);
                return text.TrimEnd('0').TrimEnd('.');
            }
            case PsName n:
                return n.Value;
            case PsString s:
                return s.Text;
        }
        return PsOps.Str(value);
    }

    private static long? ParseRadixIntegerText(string text)
    {
        long sign = 1;
        string body = text;
        if (body.StartsWith('+'))
        {
            body = body[1..];
        }
        else if (body.StartsWith('-'))
        {
            sign = -1;
            body = body[1..];
        }
        int hash = body.IndexOf('#', StringComparison.Ordinal);
        if (hash < 0)
            return null;
        string baseText = body[..hash];
        string numberText = body[(hash + 1)..];
        if (baseText.Length == 0 || numberText.Length == 0 || !Py.TryInt(baseText, 10, out long radix) || radix < 2 || radix > 36)
            return null;
        if (!Py.TryInt(numberText, (int)radix, out long number))
            return null;
        return sign * number;
    }

    internal static Dictionary<object, PsDict> ResourceStore(PsExecutionContext ctx)
    {
        if (ctx.Systemdict.Get("__resources__") is Dictionary<object, PsDict> existing)
            return existing;
        var resources = new Dictionary<object, PsDict>(PsKeyComparer.Instance);
        ctx.Systemdict.Items["__resources__"] = resources;
        return resources;
    }
}
