using System.Globalization;
using System.Text;

namespace XRay.ThreeD.Formats.OpenScad;

internal readonly record struct ScadRange(double Start, double Step, double End)
{
    public long Count => Step == 0 || (Step > 0 && End < Start) || (Step < 0 && End > Start) ? 0 : (long)Math.Floor((End - Start) / Step + 1e-9) + 1;
}

internal sealed record FuncVal(Param[] Params, Expr Body, ScadEnv Env);

internal sealed class ScadEnv
{
    public ScadEnv(ScadEnv? lex, ScadEnv? dyn)
    {
        Lex = lex;
        Dyn = dyn;
    }

    public ScadEnv? Lex { get; }

    public ScadEnv? Dyn { get; }

    public Dictionary<string, object?> Vars { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, ModuleDef>? Modules { get; set; }

    public Dictionary<string, FuncDef>? Functions { get; set; }

    /// <summary>The statements given to this module call as children, with the environment they are evaluated in.</summary>
    public (Stmt[] Items, ScadEnv Env)? Children { get; set; }

    public bool TryGet(string name, out object? value)
    {
        var special = name.Length > 0 && name[0] == '$';
        for (var e = this; e is not null; e = special ? e.Dyn : e.Lex)
        {
            if (e.Vars.TryGetValue(name, out value))
            {
                return true;
            }
        }

        value = null;
        return false;
    }

    public ModuleDef? FindModule(string name)
    {
        for (var e = this; e is not null; e = e.Lex)
        {
            if (e.Modules is not null && e.Modules.TryGetValue(name, out var m))
            {
                return m;
            }
        }

        return null;
    }

    public (FuncDef Def, ScadEnv Env)? FindFunction(string name)
    {
        for (var e = this; e is not null; e = e.Lex)
        {
            if (e.Functions is not null && e.Functions.TryGetValue(name, out var f))
            {
                return (f, e);
            }
        }

        return null;
    }
}

internal static class ScadValue
{
    public static bool Truthy(object? v) => v switch
    {
        null => false,
        bool b => b,
        double d => d != 0,
        string s => s.Length > 0,
        List<object?> l => l.Count > 0,
        _ => true,
    };

    public static double? Num(object? v) => v switch { double d => d, bool b => b ? 1 : 0, _ => null };

    public static bool Equal(object? a, object? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        if (a is double x && b is double y)
        {
            return x == y;
        }

        if (a is bool p && b is bool q)
        {
            return p == q;
        }

        if (a is string s && b is string t)
        {
            return s == t;
        }

        if (a is List<object?> l && b is List<object?> m)
        {
            if (l.Count != m.Count)
            {
                return false;
            }

            for (var i = 0; i < l.Count; i++)
            {
                if (!Equal(l[i], m[i]))
                {
                    return false;
                }
            }

            return true;
        }

        return false;
    }

    public static int? Compare(object? a, object? b)
    {
        if (a is double x && b is double y)
        {
            return x < y ? -1 : x > y ? 1 : 0;
        }

        if (a is string s && b is string t)
        {
            return Math.Sign(string.CompareOrdinal(s, t));
        }

        if (a is bool p && b is bool q)
        {
            return p.CompareTo(q);
        }

        return null;
    }

    public static string Str(object? v)
    {
        switch (v)
        {
            case null:
                return "undef";
            case bool b:
                return b ? "true" : "false";
            case double d:
                return d.ToString("0.######", CultureInfo.InvariantCulture);
            case string s:
                return s;
            case List<object?> l:
                var sb = new StringBuilder("[");
                for (var i = 0; i < l.Count; i++)
                {
                    sb.Append(i > 0 ? ", " : "").Append(l[i] is string q ? "\"" + q + "\"" : Str(l[i]));
                }

                return sb.Append(']').ToString();
            case ScadRange r:
                return $"[{Str(r.Start)} : {Str(r.Step)} : {Str(r.End)}]";
            default:
                return "function";
        }
    }

    public static double[]? Doubles(object? v, int minLength = 0)
    {
        if (v is not List<object?> l || l.Count < minLength)
        {
            return null;
        }

        var r = new double[l.Count];
        for (var i = 0; i < r.Length; i++)
        {
            if (Num(l[i]) is not { } d)
            {
                return null;
            }

            r[i] = d;
        }

        return r;
    }

    public static List<object?> Vec(params double[] values)
    {
        var l = new List<object?>(values.Length);
        foreach (var d in values)
        {
            l.Add(d);
        }

        return l;
    }
}
