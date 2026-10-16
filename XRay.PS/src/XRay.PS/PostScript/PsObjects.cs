// Ported from ps/objects.py, ps/errors.py and ps/stack.py.
//
// PostScript values are carried as plain CLR objects, the way the original carried Python
// objects: long (integer), double (real), bool, null, string (a dictionary key), and the
// classes below for names, strings, arrays, dictionaries and the rest. PsOps holds the
// helpers that give them the original's comparison, hashing and truthiness semantics.

using XRay.PS.Rendering;

namespace XRay.PS.PostScript;

/// <summary>A PostScript name; literal names are written with a leading slash.</summary>
public sealed record PsName(string Value, bool Literal = false)
{
    public override string ToString() => Literal ? "/" + Value : Value;
}

/// <summary>A reference to a built-in operator.</summary>
public sealed record PsOperator(string Name)
{
    public override string ToString() => "--" + Name + "--";
}

/// <summary>A PostScript string (bytes). Mutable in place, like the original.</summary>
public sealed class PsString(byte[] value, string? remainingFilter = null, Dictionary<string, object?>? filterParams = null)
{
    public byte[] Value { get; set; } = value;

    /// <summary>A filter left undecoded when the string was read through a filter chain
    /// (DCTDecode or CCITTFaxDecode, which image consumers decode themselves).</summary>
    public string? RemainingFilter { get; } = remainingFilter;

    public Dictionary<string, object?>? FilterParams { get; } = filterParams;

    public static PsString FromText(string text) => new(Internal.Py.Latin1.GetBytes(text));

    public string Text => Internal.Py.Latin1String(Value);
}

/// <summary>A PostScript array (literal).</summary>
public sealed class PsArray(List<object?> items)
{
    public List<object?> Items { get; set; } = items;
}

/// <summary>An executable array (procedure).</summary>
public sealed class PsProcedure(List<object?> items)
{
    public List<object?> Items { get; set; } = items;
}

/// <summary>A PostScript dictionary, ordered by insertion like the original's Python dict.</summary>
public sealed class PsDict
{
    public PsDict() => Items = new OrderedDictionary<object, object?>(PsKeyComparer.Instance);

    public PsDict(OrderedDictionary<object, object?> items) => Items = items;

    public OrderedDictionary<object, object?> Items { get; }

    public static OrderedDictionary<object, object?> NewItems() => new(PsKeyComparer.Instance);

    public object? Get(object key) => Items.TryGetValue(key, out var v) ? v : null;
}

/// <summary>A mark on the operand stack (<c>mark</c>, <c>[</c> or <c>&lt;&lt;</c>).</summary>
public sealed record PsMark(string Kind = "mark");

/// <summary>A <c>gstate</c> object.</summary>
public sealed record PsGState(GraphicsState State);

/// <summary>A file object. Only <c>currentfile</c> with an optional filter chain is modelled.</summary>
public sealed record PsFile(string? Name, string? Mode, IReadOnlyList<FilterSpec> Filters)
{
    public static readonly IReadOnlyList<FilterSpec> NoFilters = [];
}

/// <summary>A filter name and its parameter dictionary.</summary>
public sealed record FilterSpec(string Name, Dictionary<string, object?>? Params);

/// <summary>A font identifier.</summary>
public sealed record PsFontId(object Id);

/// <summary>An instantiated pattern (the result of <c>makepattern</c>).</summary>
public sealed record PsPattern(string PatternId, Pattern Pattern);

/// <summary>Base class for PostScript errors (<c>typecheck</c>, <c>rangecheck</c>, ...).</summary>
public class PsError(string code, string message) : Exception($"{code}: {message}")
{
    public string Code { get; } = code;
    public string PsMessage { get; } = message;
}

public sealed class PsSyntaxError(string message = "syntax error") : PsError("syntaxerror", message);
public sealed class PsTypeError(string message = "type check") : PsError("typecheck", message);
public sealed class PsRangeError(string message = "range check") : PsError("rangecheck", message);
public sealed class PsUndefinedError(string message = "undefined") : PsError("undefined", message);
public sealed class PsIOError(string message = "io error") : PsError("ioerror", message);
public sealed class PsLimitCheck(string message = "limit check") : PsError("limitcheck", message);
public sealed class PsInvalidAccess(string message = "invalid access") : PsError("invalidaccess", message);

/// <summary>Raised by <c>quit</c> to stop execution without an error.</summary>
public sealed class PsQuit : Exception
{
    public PsQuit()
        : base("quit")
    {
    }
}

/// <summary>A stack for interpreter execution (operand, dictionary and graphics state stacks).</summary>
public sealed class PsStack<T>
{
    public PsStack() => Items = [];

    public PsStack(IEnumerable<T> items) => Items = [.. items];

    /// <summary>The items, bottom first.</summary>
    public List<T> Items { get; }

    public int Count => Items.Count;

    public void Push(T item) => Items.Add(item);

    public T Pop()
    {
        if (Items.Count == 0)
            throw new PsRangeError("stack underflow");
        var item = Items[^1];
        Items.RemoveAt(Items.Count - 1);
        return item;
    }

    public T Peek()
    {
        if (Items.Count == 0)
            throw new PsRangeError("stack underflow");
        return Items[^1];
    }

    public void Clear() => Items.Clear();

    public PsStack<T> Clone() => new(Items);

    public List<T> ToList() => [.. Items];
}
