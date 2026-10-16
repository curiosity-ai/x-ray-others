// Ported from ps/interpreter.py.

using XRay.PS.Internal;

namespace XRay.PS.PostScript;

/// <summary>Executes PostScript objects against a registry of operators.</summary>
public sealed class PsInterpreter(OperatorRegistry operators)
{
    // The original ran on the Python call stack, so very deep procedure nesting failed with a
    // RecursionError at about a thousand frames. The port counts the equivalent frames so the
    // same programs fail the same way, and never overflows the CLR stack.
    [ThreadStatic]
    private static int t_depth;

    // The depth ceiling in force on this thread (0 means the default).
    [ThreadStatic]
    private static int t_maxDepth;

    /// <summary>Frame budget matching the original's default recursion limit.</summary>
    internal const int MaxDepth = 985;

    /// <summary>The key under which the active tokenizer is published in systemdict.</summary>
    public const string TokenizerKey = "__tokenizer__";

    public OperatorRegistry Operators => operators;

    /// <summary>Parses and executes a PostScript byte stream.</summary>
    public void Execute(byte[] data, PsExecutionContext ctx)
    {
        ctx.Dsc = DscMetadata.Parse(data);
        var tokenizer = new PsTokenizer(data);
        var parser = new PsParser(tokenizer) { MaxNesting = ctx.Limits.MaxNestingDepth };
        ctx.Systemdict.Items.TryGetValue(TokenizerKey, out var previous);
        ctx.Systemdict.Items[TokenizerKey] = tokenizer;
        int savedMaxDepth = t_maxDepth;
        t_maxDepth = ctx.Limits.MaxExecutionDepth;
        using var guard = Guard.Begin(ctx.CancellationToken, ctx.Limits.MaxMemoryBytes);
        ctx.HeapBaseline = -1;
        int suppressed = 0;
        try
        {
            while (true)
            {
                try
                {
                    var obj = parser.ParseObject();
                    if (ReferenceEquals(obj, PsParser.EndOfInput))
                        break;
                    ExecuteObject(obj, ctx);
                }
                catch (Exception ex) when (ctx.SuppressErrors && IsRecoverable(ex))
                {
                    ctx.Warnings?.Add(new ConversionWarning("ps", ErrorCode(ex), ex.Message, PageNumber(ctx)));
                    // A runaway job (limit, too many errors) cannot be resumed.
                    if (ex is PsResourceLimitError || ++suppressed > ctx.Limits.MaxSuppressedErrors)
                        break;
                    if (!SkipToEndOfPage(parser, ctx, ref suppressed))
                        break;
                }
            }
        }
        catch (PsQuit)
        {
        }
        finally
        {
            t_maxDepth = savedMaxDepth;
            if (previous is null)
                ctx.Systemdict.Items.Remove(TokenizerKey);
            else
                ctx.Systemdict.Items[TokenizerKey] = previous;
        }
    }

    private static bool IsRecoverable(Exception ex) => ex is PsError or HostErrorException or InvalidOperationException
        or ArgumentException or IndexOutOfRangeException or InvalidCastException or OverflowException or FormatException
        or KeyNotFoundException or NullReferenceException or DivideByZeroException or NotSupportedException or InvalidDataException;

    private static string ErrorCode(Exception ex) => ex switch
    {
        PsError e => e.Code,
        HostErrorException h => h.Kind,
        _ => ex.GetType().Name,
    };

    private static int? PageNumber(PsExecutionContext ctx) =>
        ctx.Extras.TryGetValue(PageCounterKey, out var v) && v is int n ? n : null;

    /// <summary>The key under which showpage handling may publish the number of the page being drawn.</summary>
    internal const string PageCounterKey = "__page_number__";

    // After an error the rest of the page is not trustworthy: skip source objects up to the
    // next showpage, run that one so the page is closed, and resume after it. A job without
    // another showpage ends here, and the page drawn so far is what the caller gets.
    private bool SkipToEndOfPage(PsParser parser, PsExecutionContext ctx, ref int suppressed)
    {
        while (true)
        {
            ctx.CancellationToken.ThrowIfCancellationRequested();
            object? obj;
            try
            {
                obj = parser.ParseObject();
            }
            catch (PsError ex)
            {
                ctx.Warnings?.Add(new ConversionWarning("ps", ex.Code, ex.Message, PageNumber(ctx)));
                if (++suppressed > ctx.Limits.MaxSuppressedErrors)
                    return false;
                continue;
            }
            if (ReferenceEquals(obj, PsParser.EndOfInput))
                return false;
            if (obj is PsName { Literal: false, Value: "showpage" } or PsOperator { Name: "showpage" })
            {
                try
                {
                    ExecuteObject(obj, ctx);
                }
                catch (Exception ex) when (IsRecoverable(ex))
                {
                    ctx.Warnings?.Add(new ConversionWarning("ps", ErrorCode(ex), ex.Message, PageNumber(ctx)));
                }
                return true;
            }
        }
    }

    /// <summary>Executes already-parsed objects in sequence.</summary>
    public void ExecuteObjects(IEnumerable<object?> objects, PsExecutionContext ctx)
    {
        foreach (var obj in objects)
            ExecuteObject(obj, ctx);
    }

    /// <summary>Counts one unit of work against the limits and polls the cancellation token.
    /// Called for every executed object and procedure call, so every loop construct (an empty
    /// <c>{} loop</c> included) passes through it.</summary>
    private static void Tick(PsExecutionContext ctx)
    {
        long n = ++ctx.OperatorCount;
        var limits = ctx.Limits;
        if (n > limits.MaxOperators)
            throw new PsResourceLimitError($"operator limit of {limits.MaxOperators} exceeded");
        if (ctx.OperandStack.Items.Count > limits.MaxOperandStackDepth)
            throw new PsResourceLimitError($"operand stack deeper than {limits.MaxOperandStackDepth}");
        if ((n & 0xFF) != 0)
            return;
        ctx.CancellationToken.ThrowIfCancellationRequested();
        if ((n & 0xFFFF) == 0)
            CheckHeap(ctx);
    }

    private static void CheckHeap(PsExecutionContext ctx)
    {
        if (ctx.Limits.MaxMemoryBytes is not { } max)
            return;
        long now = GC.GetTotalMemory(false);
        if (ctx.HeapBaseline < 0)
        {
            ctx.HeapBaseline = now;
            return;
        }
        if (now - ctx.HeapBaseline <= max)
            return;
        // The number includes garbage not yet collected: confirm after a collection.
        if (GC.GetTotalMemory(true) - ctx.HeapBaseline > max)
            throw new PsResourceLimitError($"memory limit of {max} bytes exceeded");
    }

    /// <summary>Executes one object: executable names run, everything else is pushed.</summary>
    public void ExecuteObject(object? obj, PsExecutionContext ctx)
    {
        Tick(ctx);
        Enter();
        try
        {
            if (obj is PsName name)
            {
                if (name.Literal)
                {
                    ctx.OperandStack.Push(obj);
                    return;
                }
                ExecuteName(name.Value, ctx);
                return;
            }
            if (obj is PsOperator op)
            {
                ExecuteName(op.Name, ctx);
                return;
            }
            ctx.OperandStack.Push(obj);
        }
        catch (PsError ex) when (ctx.ErrorHandler is not null)
        {
            ctx.ErrorHandler(ex);
        }
        finally
        {
            t_depth--;
        }
    }

    /// <summary>Executes a procedure's items in order.</summary>
    public void ExecuteProcedure(PsProcedure proc, PsExecutionContext ctx)
    {
        Tick(ctx);
        Enter();
        try
        {
            // The original iterated the live list, so a procedure that grows itself sees its
            // new items; index-based iteration keeps that.
            var items = proc.Items;
            for (int i = 0; i < items.Count; i++)
                ExecuteObject(items[i], ctx);
        }
        finally
        {
            t_depth--;
        }
    }

    /// <summary>Looks a name up (operators first, then the dictionary stack) and executes it.</summary>
    public void ExecuteName(string name, PsExecutionContext ctx)
    {
        Enter();
        try
        {
            var entry = operators.Get(name);
            if (entry is not null)
            {
                if (ctx.OperandStack.Count < entry.MinOperands)
                    throw new PsRangeError($"stack underflow for operator {name}");
                // The operator's own frames (the registered lambda and its function).
                Enter();
                Enter();
                try
                {
                    entry.Fn(ctx);
                }
                finally
                {
                    t_depth -= 2;
                }
                return;
            }
            if (!LookupDict(name, ctx, out var value))
                throw new PsUndefinedError($"undefined name {name}");
            if (value is PsProcedure proc)
            {
                ExecuteProcedure(proc, ctx);
                return;
            }
            if (value is PsOperator op)
            {
                // Values defined via patterns like `/m /moveto load def` must execute when
                // referenced by executable names.
                ExecuteName(op.Name, ctx);
                return;
            }
            ctx.OperandStack.Push(value);
        }
        finally
        {
            t_depth--;
        }
    }

    private static void Enter()
    {
        if (++t_depth > (t_maxDepth == 0 ? MaxDepth : t_maxDepth))
        {
            t_depth--;
            throw new HostErrorException("RecursionError", "maximum recursion depth exceeded");
        }
    }

    /// <summary>Runs an action with extra frames counted against the recursion budget, for
    /// operators that recurse through helper calls.</summary>
    internal static void CountFrames(int frames, Action action)
    {
        for (int i = 0; i < frames; i++)
            Enter();
        try
        {
            action();
        }
        finally
        {
            t_depth -= frames;
        }
    }

    internal static bool LookupDict(string name, PsExecutionContext ctx, out object? value)
    {
        var items = ctx.DictionaryStack.Items;
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (items[i].Items.TryGetValue(name, out value))
                return true;
        }
        value = null;
        return false;
    }

    /// <summary>Runs work on a thread with a large stack so that the frame budget, not the CLR
    /// stack, is what limits recursion.</summary>
    internal static T RunWithLargeStack<T>(Func<T> work, CancellationToken token = default, long? maxAllocation = null)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            int saved = t_depth;
            t_depth = 0;
            try
            {
                using var guard = Guard.Begin(token, maxAllocation);
                result = work();
            }
            catch (Exception ex)
            {
                error = ex;
            }
            finally
            {
                t_depth = saved;
            }
        }, 512 * 1024 * 1024);
        thread.Start();
        thread.Join();
        if (error is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }
}
