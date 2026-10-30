using System.Text;

namespace XRay.ThreeD.Formats.OpenScad;

internal static class ScadColors
{
    private static readonly Dictionary<string, int> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = 0x000000, ["white"] = 0xFFFFFF, ["red"] = 0xFF0000, ["lime"] = 0x00FF00, ["blue"] = 0x0000FF, ["yellow"] = 0xFFFF00,
        ["cyan"] = 0x00FFFF, ["aqua"] = 0x00FFFF, ["magenta"] = 0xFF00FF, ["fuchsia"] = 0xFF00FF, ["silver"] = 0xC0C0C0, ["gray"] = 0x808080,
        ["grey"] = 0x808080, ["maroon"] = 0x800000, ["olive"] = 0x808000, ["green"] = 0x008000, ["purple"] = 0x800080, ["teal"] = 0x008080,
        ["navy"] = 0x000080, ["orange"] = 0xFFA500, ["gold"] = 0xFFD700, ["pink"] = 0xFFC0CB, ["brown"] = 0xA52A2A, ["ivory"] = 0xFFFFF0,
        ["beige"] = 0xF5F5DC, ["tan"] = 0xD2B48C, ["khaki"] = 0xF0E68C, ["coral"] = 0xFF7F50, ["salmon"] = 0xFA8072, ["crimson"] = 0xDC143C,
        ["indigo"] = 0x4B0082, ["violet"] = 0xEE82EE, ["turquoise"] = 0x40E0D0, ["steelblue"] = 0x4682B4, ["lightsteelblue"] = 0xB0C4DE,
        ["gainsboro"] = 0xDCDCDC, ["dimgray"] = 0x696969, ["dimgrey"] = 0x696969, ["lightgray"] = 0xD3D3D3, ["lightgrey"] = 0xD3D3D3,
        ["darkgray"] = 0xA9A9A9, ["darkgrey"] = 0xA9A9A9, ["slategray"] = 0x708090, ["skyblue"] = 0x87CEEB, ["lightblue"] = 0xADD8E6,
        ["darkblue"] = 0x00008B, ["darkgreen"] = 0x006400, ["darkred"] = 0x8B0000, ["forestgreen"] = 0x228B22, ["limegreen"] = 0x32CD32,
        ["chocolate"] = 0xD2691E, ["sienna"] = 0xA0522D, ["peru"] = 0xCD853F, ["wheat"] = 0xF5DEB3, ["lavender"] = 0xE6E6FA,
        ["orchid"] = 0xDA70D6, ["plum"] = 0xDDA0DD, ["royalblue"] = 0x4169E1, ["firebrick"] = 0xB22222, ["tomato"] = 0xFF6347,
        ["goldenrod"] = 0xDAA520, ["darkorange"] = 0xFF8C00, ["seagreen"] = 0x2E8B57, ["lightgreen"] = 0x90EE90, ["snow"] = 0xFFFAFA,
        ["linen"] = 0xFAF0E6, ["mintcream"] = 0xF5FFFA, ["azure"] = 0xF0FFFF, ["whitesmoke"] = 0xF5F5F5, ["cornflowerblue"] = 0x6495ED,
    };

    public static bool TryParse(string name, out (float R, float G, float B) rgb)
    {
        rgb = default;
        int value;
        if (name.StartsWith('#') && (name.Length == 7 || name.Length == 4 || name.Length == 9 || name.Length == 5))
        {
            var hex = name[1..];
            if (hex.Length is 3 or 4)
            {
                hex = string.Concat(hex.Select(c => new string(c, 2)));
            }

            if (!int.TryParse(hex.AsSpan(0, 6), System.Globalization.NumberStyles.HexNumber, null, out value))
            {
                return false;
            }
        }
        else if (!Names.TryGetValue(name, out value))
        {
            return false;
        }

        rgb = (((value >> 16) & 255) / 255f, ((value >> 8) & 255) / 255f, (value & 255) / 255f);
        return true;
    }
}

internal static class ScadBuiltins
{
    private static double D(IReadOnlyList<object?> a, int i) => i < a.Count && ScadValue.Num(a[i]) is { } d ? d : double.NaN;

    private static object? Map(object? v, Func<double, double> f) => v switch
    {
        double d => f(d),
        List<object?> l => l.Select(x => Map(x, f)).ToList(),
        _ => null,
    };

    public static object? Call(string name, List<object?> pos, Dictionary<string, object?> named, ScadEval eval)
    {
        object? Arg(int i, string key) => named.TryGetValue(key, out var v) ? v : i < pos.Count ? pos[i] : null;
        const double Deg = Math.PI / 180;
        switch (name)
        {
            case "sin": return Map(Arg(0, "x"), x => Math.Sin(x * Deg));
            case "cos": return Map(Arg(0, "x"), x => Math.Cos(x * Deg));
            case "tan": return Map(Arg(0, "x"), x => Math.Tan(x * Deg));
            case "asin": return Map(Arg(0, "x"), x => Math.Asin(x) / Deg);
            case "acos": return Map(Arg(0, "x"), x => Math.Acos(x) / Deg);
            case "atan": return Map(Arg(0, "x"), x => Math.Atan(x) / Deg);
            case "atan2": return Math.Atan2(D(pos, 0), D(pos, 1)) / Deg;
            case "abs": return Map(Arg(0, "x"), Math.Abs);
            case "sign": return Map(Arg(0, "x"), x => Math.Sign(x));
            case "floor": return Map(Arg(0, "x"), Math.Floor);
            case "ceil": return Map(Arg(0, "x"), Math.Ceiling);
            case "round": return Map(Arg(0, "x"), x => Math.Round(x, MidpointRounding.AwayFromZero));
            case "sqrt": return Map(Arg(0, "x"), Math.Sqrt);
            case "exp": return Map(Arg(0, "x"), Math.Exp);
            case "ln": return Map(Arg(0, "x"), Math.Log);
            case "log": return pos.Count >= 2 ? Math.Log(D(pos, 1)) / Math.Log(D(pos, 0)) : Map(Arg(0, "x"), Math.Log10);
            case "pow": return Math.Pow(D(pos, 0), D(pos, 1));
            case "min" or "max":
            {
                var values = pos.Count == 1 && pos[0] is List<object?> l ? l : pos;
                var nums = values.Select(ScadValue.Num).Where(n => n is not null).Select(n => n!.Value).ToList();
                return nums.Count == 0 ? null : name == "min" ? nums.Min() : nums.Max();
            }

            case "norm" when ScadValue.Doubles(Arg(0, "v")) is { } v:
                return Math.Sqrt(v.Sum(x => x * x));
            case "cross" when ScadValue.Doubles(Arg(0, "a"), 3) is { } a && ScadValue.Doubles(Arg(1, "b"), 3) is { } b:
                return ScadValue.Vec(a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0]);
            case "len":
                return Arg(0, "x") switch { List<object?> l => (double)l.Count, string s => (double)s.Length, _ => null };
            case "concat":
            {
                var r = new List<object?>();
                foreach (var p in pos)
                {
                    if (p is List<object?> l)
                    {
                        r.AddRange(l);
                    }
                    else
                    {
                        r.Add(p);
                    }
                }

                return r;
            }

            case "str":
                return string.Concat(pos.Select(ScadValue.Str));
            case "chr":
                return string.Concat(pos.SelectMany(p => p is List<object?> l ? l : [p]).Select(p => ScadValue.Num(p) is { } c && c >= 0 && c < 0x110000 ? char.ConvertFromUtf32((int)c) : ""));
            case "ord":
                return Arg(0, "x") is string { Length: > 0 } str ? (double)char.ConvertToUtf32(str, 0) : null;
            case "is_undef": return pos.Count > 0 && pos[0] is null;
            case "is_bool": return pos.Count > 0 && pos[0] is bool;
            case "is_num": return pos.Count > 0 && pos[0] is double;
            case "is_string": return pos.Count > 0 && pos[0] is string;
            case "is_list": return pos.Count > 0 && pos[0] is List<object?>;
            case "is_function": return pos.Count > 0 && pos[0] is FuncVal;
            case "version": return ScadValue.Vec(2021, 1, 0);
            case "lookup" when Arg(1, "table") is List<object?> table:
            {
                var key = D(pos, 0);
                var rows = table.Select(r => ScadValue.Doubles(r, 2)).Where(r => r is not null).Select(r => r!).OrderBy(r => r[0]).ToList();
                if (rows.Count == 0)
                {
                    return null;
                }

                if (key <= rows[0][0])
                {
                    return rows[0][1];
                }

                for (var i = 1; i < rows.Count; i++)
                {
                    if (key <= rows[i][0])
                    {
                        var t = (key - rows[i - 1][0]) / (rows[i][0] - rows[i - 1][0]);
                        return rows[i - 1][1] + t * (rows[i][1] - rows[i - 1][1]);
                    }
                }

                return rows[^1][1];
            }

            case "rands":
            {
                eval.NoteRandom();
                var min = D(pos, 0);
                var max = D(pos, 1);
                var count = (int)Math.Min(Math.Max(D(pos, 2), 0), 10_000_000);
                var seed = pos.Count > 3 ? (ulong)Math.Abs((long)D(pos, 3)) : 12345UL;
                var r = new List<object?>(count);
                var state = seed * 6364136223846793005UL + 1442695040888963407UL;
                for (var i = 0; i < count; i++)
                {
                    state = state * 6364136223846793005UL + 1442695040888963407UL;
                    r.Add(min + (max - min) * ((state >> 11) / (double)(1UL << 53)));
                }

                return r;
            }

            case "search":
                return null;
        }

        return null;
    }
}
