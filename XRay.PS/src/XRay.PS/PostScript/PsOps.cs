using System.Globalization;
using XRay.PS.Internal;

namespace XRay.PS.PostScript;

/// <summary>Dictionary-key semantics of the original: integers, reals and booleans that are
/// numerically equal are the same key; strings compare ordinally; records by value.</summary>
public sealed class PsKeyComparer : IEqualityComparer<object>
{
    public static readonly PsKeyComparer Instance = new();

    public new bool Equals(object? x, object? y)
    {
        if (ReferenceEquals(x, y))
            return true;
        if (x is null || y is null)
            return false;
        if (PsOps.IsNumeric(x) && PsOps.IsNumeric(y))
            return PsOps.NumericEquals(x, y);
        if (x is string sx && y is string sy)
            return string.Equals(sx, sy, StringComparison.Ordinal);
        return x.Equals(y);
    }

    public int GetHashCode(object obj)
    {
        switch (obj)
        {
            case long l:
                return l.GetHashCode();
            case bool b:
                return (b ? 1L : 0L).GetHashCode();
            case double d:
                if (Math.Floor(d) == d && Math.Abs(d) < 9e18)
                    return ((long)d).GetHashCode();
                return d.GetHashCode();
            case int i:
                return ((long)i).GetHashCode();
            case string s:
                return StringComparer.Ordinal.GetHashCode(s);
            default:
                return obj.GetHashCode();
        }
    }
}

/// <summary>Value semantics shared by the operators: Python-style equality, ordering,
/// truthiness and numeric coercion over the CLR objects that carry PostScript values.</summary>
public static class PsOps
{
    /// <summary>True for the values Python's <c>isinstance(x, (int, float))</c> accepts
    /// (booleans included, as in Python).</summary>
    public static bool IsNumeric(object? value) => value is long or double or bool or int;

    /// <summary>True for Python <c>isinstance(x, int)</c> (booleans included).</summary>
    public static bool IsInt(object? value) => value is long or bool or int;

    public static double ToDouble(object? value) => value switch
    {
        long l => l,
        double d => d,
        bool b => b ? 1.0 : 0.0,
        int i => i,
        _ => throw new PsTypeError("number expected"),
    };

    internal static bool NumericEquals(object x, object y)
    {
        if (x is double || y is double)
            return ToDouble(x) == ToDouble(y);
        return ToLong(x) == ToLong(y);
    }

    private static long ToLong(object x) => x switch
    {
        long l => l,
        bool b => b ? 1 : 0,
        int i => i,
        _ => 0,
    };

    /// <summary>Python <c>a == b</c> over PostScript values.</summary>
    public static bool PyEquals(object? a, object? b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a is null || b is null)
            return false;
        if (IsNumeric(a) && IsNumeric(b))
            return NumericEquals(a, b);
        switch (a)
        {
            case string sa:
                return b is string sb && string.Equals(sa, sb, StringComparison.Ordinal);
            case PsString psa:
                return b is PsString psb && psa.Value.AsSpan().SequenceEqual(psb.Value)
                    && psa.RemainingFilter == psb.RemainingFilter && ParamsEqual(psa.FilterParams, psb.FilterParams);
            case PsArray aa:
                return b is PsArray ab && ListEquals(aa.Items, ab.Items);
            case PsProcedure pa:
                return b is PsProcedure pb && ListEquals(pa.Items, pb.Items);
            case PsDict da:
                return b is PsDict db && DictEquals(da, db);
            case List<object?> la:
                return b is List<object?> lb && ListEquals(la, lb);
            case byte[] ba:
                return b is byte[] bb && ba.AsSpan().SequenceEqual(bb);
            default:
                return a.Equals(b);
        }
    }

    private static bool ParamsEqual(Dictionary<string, object?>? a, Dictionary<string, object?>? b)
    {
        if (a is null || b is null)
            return a is null && b is null;
        if (a.Count != b.Count)
            return false;
        foreach (var (k, v) in a)
        {
            if (!b.TryGetValue(k, out var w) || !PyEquals(v, w))
                return false;
        }
        return true;
    }

    private static bool ListEquals(List<object?> a, List<object?> b)
    {
        if (a.Count != b.Count)
            return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!PyEquals(a[i], b[i]))
                return false;
        }
        return true;
    }

    private static bool DictEquals(PsDict a, PsDict b)
    {
        if (a.Items.Count != b.Items.Count)
            return false;
        foreach (var (k, v) in a.Items)
        {
            if (!b.Items.TryGetValue(k, out var w) || !PyEquals(v, w))
                return false;
        }
        return true;
    }

    /// <summary>Python ordering comparison <c>a op b</c> for op in &lt;, &gt;, &lt;=, &gt;=; raises the
    /// original's <c>TypeError</c> for values Python cannot order.</summary>
    public static bool PyCompare(object? a, object? b, string op)
    {
        if (IsNumeric(a) && IsNumeric(b))
        {
            if (a is double || b is double)
            {
                double x = ToDouble(a);
                double y = ToDouble(b);
                return op switch { "<" => x < y, ">" => x > y, "<=" => x <= y, _ => x >= y };
            }
            long lx = ToLong(a!);
            long ly = ToLong(b!);
            return op switch { "<" => lx < ly, ">" => lx > ly, "<=" => lx <= ly, _ => lx >= ly };
        }
        if (a is string sa && b is string sb)
        {
            int c = string.CompareOrdinal(sa, sb);
            return op switch { "<" => c < 0, ">" => c > 0, "<=" => c <= 0, _ => c >= 0 };
        }
        throw Py.TypeError($"'{op}' not supported between instances of '{TypeName(a)}' and '{TypeName(b)}'");
    }

    /// <summary>Python truthiness.</summary>
    public static bool Truthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        long l => l != 0,
        double d => d != 0.0,
        int i => i != 0,
        string s => s.Length > 0,
        System.Collections.ICollection c => c.Count > 0,
        _ => true,
    };

    internal static string TypeName(object? value) => value switch
    {
        null => "NoneType",
        long or int => "int",
        double => "float",
        bool => "bool",
        string => "str",
        _ => value.GetType().Name,
    };

    /// <summary>Formats a value as the original's <c>str()</c> would for simple values.</summary>
    public static string Str(object? value) => value switch
    {
        null => "None",
        bool b => b ? "True" : "False",
        long l => l.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        double d => Py.Repr(d),
        string s => s,
        PsName n => $"PsName(value='{n.Value}', literal={(n.Literal ? "True" : "False")})",
        PsOperator o => $"PsOperator(name='{o.Name}')",
        PsMark m => $"PsMark(kind='{m.Kind}')",
        _ => value.GetType().Name,
    };
}
