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
        var parser = new PsParser(tokenizer);
        ctx.Systemdict.Items.TryGetValue(TokenizerKey, out var previous);
        ctx.Systemdict.Items[TokenizerKey] = tokenizer;
        try
        {
            while (true)
            {
                var obj = parser.ParseObject();
                if (ReferenceEquals(obj, PsParser.EndOfInput))
                    break;
                ExecuteObject(obj, ctx);
            }
        }
        catch (PsQuit)
        {
        }
        finally
        {
            if (previous is null)
                ctx.Systemdict.Items.Remove(TokenizerKey);
            else
                ctx.Systemdict.Items[TokenizerKey] = previous;
        }
    }

    /// <summary>Executes already-parsed objects in sequence.</summary>
    public void ExecuteObjects(IEnumerable<object?> objects, PsExecutionContext ctx)
    {
        foreach (var obj in objects)
            ExecuteObject(obj, ctx);
    }

    /// <summary>Executes one object: executable names run, everything else is pushed.</summary>
    public void ExecuteObject(object? obj, PsExecutionContext ctx)
    {
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
        if (++t_depth > MaxDepth)
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
    internal static T RunWithLargeStack<T>(Func<T> work)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            int saved = t_depth;
            t_depth = 0;
            try
            {
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
