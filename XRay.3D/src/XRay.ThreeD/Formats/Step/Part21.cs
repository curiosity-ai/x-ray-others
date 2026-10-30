using System.Globalization;
using System.Text;

namespace XRay.ThreeD.Formats.Step;

using XRay.ThreeD.Internal;

/// <summary>A parsed STEP value: a reference (#12), number, string, enumeration (.T.), list, typed value (LENGTH_MEASURE(1.)) or null ($, *).</summary>
internal sealed class StepArg
{
    public enum Kind
    {
        Null,
        Star,
        Number,
        String,
        Enum,
        Ref,
        List,
        Typed,
    }

    public Kind Type;
    public double Number;
    public int Ref;
    public string Text = "";
    public List<StepArg> Items = [];

    public static readonly StepArg Null = new() { Type = Kind.Null };

    public bool IsNull => Type is Kind.Null or Kind.Star;

    public bool IsRef => Type == Kind.Ref;

    public IReadOnlyList<StepArg> List => Items;

    public double Num => Type == Kind.Number ? Number : Type == Kind.Typed && Items.Count > 0 ? Items[0].Num : 0;

    public string Str => Type is Kind.String or Kind.Enum ? Text : "";

    public bool Bool => Type == Kind.Enum && Text.Equals("T", StringComparison.OrdinalIgnoreCase);

    public int ToRef => Type == Kind.Ref ? Ref : -1;

    public IEnumerable<int> Refs => Items.Where(i => i.Type == Kind.Ref).Select(i => i.Ref);
}

/// <summary>One data-section entity. A complex entity (several types in one instance) lists them in <see cref="Parts"/>.</summary>
internal sealed class StepEntity
{
    public int Id;
    public string Type = "";
    public List<(string Type, int Start, int End)> Parts = new();
    internal string Source = "";
    internal int Start;
    internal int End;
    private List<StepArg>? _args;

    public bool IsComplex => Parts.Count > 1;

    public IReadOnlyList<StepArg> Args => _args ??= Part21.ParseArgs(Source, Start, End);

    public IReadOnlyList<StepArg> ArgsOf(string type)
    {
        foreach (var (t, start, end) in Parts)
        {
            if (t == type)
            {
                return Part21.ParseArgs(Source, start, end);
            }
        }

        return [];
    }

    public bool Is(string type) => Type == type || Parts.Any(p => p.Type == type);

    public StepArg Arg(int index) => index < Args.Count ? Args[index] : StepArg.Null;
}

/// <summary>ISO 10303-21 (STEP physical file) reader: header values and the entity table; arguments are parsed on demand.</summary>
internal sealed class StepFile
{
    public readonly Dictionary<string, List<StepArg>> Header = new(StringComparer.Ordinal);
    public readonly Dictionary<int, StepEntity> Entities = new();
    public readonly List<StepEntity> InOrder = new();
    private Dictionary<string, List<StepEntity>>? _byType;

    public IReadOnlyList<StepEntity> OfType(string type)
    {
        if (_byType is null)
        {
            _byType = new Dictionary<string, List<StepEntity>>(StringComparer.Ordinal);
            foreach (var entity in InOrder)
            {
                foreach (var t in entity.Parts.Count > 1 ? entity.Parts.Select(p => p.Type) : [entity.Type])
                {
                    if (!_byType.TryGetValue(t, out var list))
                    {
                        _byType[t] = list = new List<StepEntity>();
                    }

                    list.Add(entity);
                }
            }
        }

        return _byType.TryGetValue(type, out var result) ? result : [];
    }

    public StepEntity? Get(int id) => Entities.GetValueOrDefault(id);

    public StepEntity? Get(StepArg arg) => arg.IsRef ? Entities.GetValueOrDefault(arg.Ref) : null;

    public static StepFile Parse(string text, LoadOptions options)
    {
        var file = new StepFile();
        var dataStart = text.IndexOf("DATA;", StringComparison.Ordinal);
        if (!text.StartsWith("ISO-10303-21", StringComparison.Ordinal) && text.IndexOf("ISO-10303-21", StringComparison.Ordinal) is < 0 or > 1000 || dataStart < 0)
        {
            throw new ModelFormatException("Not a STEP physical file (ISO-10303-21).");
        }

        // Header: ENTITY(args); statements before DATA.
        var headerEnd = dataStart;
        var p = text.IndexOf("HEADER;", StringComparison.Ordinal);
        if (p >= 0)
        {
            p += 7;
            while (p < headerEnd)
            {
                p = SkipSpace(text, p);
                if (p >= headerEnd || text.AsSpan(p).StartsWith("ENDSEC"))
                {
                    break;
                }

                var nameStart = p;
                while (p < headerEnd && text[p] != '(' && text[p] != ';')
                {
                    p++;
                }

                var name = text[nameStart..p].Trim();
                var end = FindStatementEnd(text, p, headerEnd);
                if (p < headerEnd && text[p] == '(')
                {
                    file.Header[name] = Part21.ParseArgs(text, p + 1, end - 1);
                }

                p = end + 1;
            }
        }

        // Data: #id = TYPE(args); or #id = (TYPE(args) TYPE(args));
        p = dataStart + 5;
        var count = 0;
        while (p < text.Length)
        {
            p = SkipSpace(text, p);
            if (p >= text.Length || text.AsSpan(p).StartsWith("ENDSEC"))
            {
                break;
            }

            if (text[p] != '#')
            {
                p = FindStatementEnd(text, p, text.Length) + 1;
                continue;
            }

            if (++count % 4096 == 0)
            {
                options.Cancellation.ThrowIfCancellationRequested();
            }

            var idStart = ++p;
            while (p < text.Length && char.IsDigit(text[p]))
            {
                p++;
            }

            if (!int.TryParse(text.AsSpan(idStart, p - idStart), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            {
                throw new ModelFormatException("A STEP entity has an invalid id.");
            }

            p = SkipSpace(text, p);
            if (p >= text.Length || text[p] != '=')
            {
                throw new ModelFormatException($"STEP: '=' expected after #{id}.");
            }

            p = SkipSpace(text, p + 1);
            var statementEnd = FindStatementEnd(text, p, text.Length);
            var entity = new StepEntity { Id = id, Source = text };
            if (text[p] == '(')
            {
                // Complex entity: a sequence of TYPE(args) inside one pair of parentheses.
                var q = p + 1;
                var inner = statementEnd - 1;
                while (q < inner)
                {
                    q = SkipSpace(text, q);
                    if (q >= inner)
                    {
                        break;
                    }

                    var ts = q;
                    while (q < inner && text[q] != '(' && !char.IsWhiteSpace(text[q]))
                    {
                        q++;
                    }

                    var type = text[ts..q].Trim();
                    q = SkipSpace(text, q);
                    var argEnd = q < inner && text[q] == '(' ? MatchParen(text, q, inner) : q;
                    entity.Parts.Add((type, q + 1, argEnd));
                    q = argEnd + 1;
                }

                entity.Type = entity.Parts.Count > 0 ? entity.Parts[0].Type : "";
                entity.Start = p + 1;
                entity.End = inner;
            }
            else
            {
                var ts = p;
                while (p < text.Length && text[p] != '(' && text[p] != ';')
                {
                    p++;
                }

                entity.Type = text[ts..p].Trim();
                entity.Start = p + 1;
                entity.End = Math.Max(entity.Start, statementEnd - 1);
                entity.Parts.Add((entity.Type, entity.Start, entity.End));
            }

            file.Entities[id] = entity;
            file.InOrder.Add(entity);
            p = statementEnd + 1;
        }

        if (file.Entities.Count == 0)
        {
            throw new ModelFormatException("The STEP file has no entities.");
        }

        return file;
    }

    private static int SkipSpace(string s, int p)
    {
        while (p < s.Length)
        {
            if (char.IsWhiteSpace(s[p]))
            {
                p++;
            }
            else if (s[p] == '/' && p + 1 < s.Length && s[p + 1] == '*')
            {
                var end = s.IndexOf("*/", p + 2, StringComparison.Ordinal);
                p = end < 0 ? s.Length : end + 2;
            }
            else
            {
                break;
            }
        }

        return p;
    }

    /// <summary>The index of the ';' that ends the statement starting at <paramref name="p"/>, skipping strings and comments.</summary>
    private static int FindStatementEnd(string s, int p, int limit)
    {
        while (p < limit)
        {
            var c = s[p];
            if (c == '\'')
            {
                p++;
                while (p < limit)
                {
                    if (s[p] == '\'')
                    {
                        if (p + 1 < limit && s[p + 1] == '\'')
                        {
                            p += 2;
                            continue;
                        }

                        break;
                    }

                    p++;
                }
            }
            else if (c == '/' && p + 1 < limit && s[p + 1] == '*')
            {
                var end = s.IndexOf("*/", p + 2, StringComparison.Ordinal);
                p = end < 0 ? limit : end + 1;
            }
            else if (c == ';')
            {
                return p;
            }

            p++;
        }

        return Math.Min(p, limit);
    }

    private static int MatchParen(string s, int open, int limit)
    {
        var depth = 0;
        for (var p = open; p < limit; p++)
        {
            switch (s[p])
            {
                case '\'':
                    p++;
                    while (p < limit && !(s[p] == '\'' && !(p + 1 < limit && s[p + 1] == '\'')))
                    {
                        p += s[p] == '\'' ? 2 : 1;
                    }

                    break;
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    if (depth == 0)
                    {
                        return p;
                    }

                    break;
            }
        }

        return limit;
    }
}

internal static class Part21
{
    /// <summary>Parses the arguments between <paramref name="start"/> and <paramref name="end"/> (exclusive; the outer parentheses are not included).</summary>
    public static List<StepArg> ParseArgs(string s, int start, int end)
    {
        var position = start;
        var result = new List<StepArg>();
        while (true)
        {
            SkipSpaceAndCommas(s, ref position, end);
            if (position >= end)
            {
                break;
            }

            result.Add(ParseValue(s, ref position, end, 0));
        }

        return result;
    }

    private static void SkipSpaceAndCommas(string s, ref int p, int end)
    {
        while (p < end)
        {
            if (char.IsWhiteSpace(s[p]) || s[p] == ',')
            {
                p++;
            }
            else if (s[p] == '/' && p + 1 < end && s[p + 1] == '*')
            {
                var close = s.IndexOf("*/", p + 2, StringComparison.Ordinal);
                p = close < 0 || close >= end ? end : close + 2;
            }
            else
            {
                break;
            }
        }
    }

    private static StepArg ParseValue(string s, ref int p, int end, int depth)
    {
        if (depth > 64)
        {
            throw new ModelFormatException("STEP values are nested too deeply.");
        }

        var c = s[p];
        switch (c)
        {
            case '$':
                p++;
                return StepArg.Null;
            case '*':
                p++;
                return new StepArg { Type = StepArg.Kind.Star };
            case '#':
                {
                    var start = ++p;
                    while (p < end && char.IsDigit(s[p]))
                    {
                        p++;
                    }

                    return new StepArg { Type = StepArg.Kind.Ref, Ref = int.TryParse(s.AsSpan(start, p - start), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : -1 };
                }

            case '\'':
                {
                    var sb = new StringBuilder();
                    p++;
                    while (p < end)
                    {
                        if (s[p] == '\'')
                        {
                            if (p + 1 < end && s[p + 1] == '\'')
                            {
                                sb.Append('\'');
                                p += 2;
                                continue;
                            }

                            break;
                        }

                        sb.Append(s[p++]);
                    }

                    p++;
                    return new StepArg { Type = StepArg.Kind.String, Text = Decode(sb.ToString()) };
                }

            case '.':
                {
                    var start = ++p;
                    while (p < end && s[p] != '.')
                    {
                        p++;
                    }

                    var text = s[start..Math.Min(p, end)];
                    p++;
                    return new StepArg { Type = StepArg.Kind.Enum, Text = text };
                }

            case '(':
                {
                    p++;
                    var list = new StepArg { Type = StepArg.Kind.List };
                    while (true)
                    {
                        SkipSpaceAndCommas(s, ref p, end);
                        if (p >= end)
                        {
                            break;
                        }

                        if (s[p] == ')')
                        {
                            p++;
                            break;
                        }

                        list.Items.Add(ParseValue(s, ref p, end, depth + 1));
                    }

                    return list;
                }

            default:
                if (char.IsDigit(c) || c is '-' or '+' or '.')
                {
                    var start = p;
                    while (p < end && (char.IsDigit(s[p]) || s[p] is '-' or '+' or '.' or 'E' or 'e'))
                    {
                        p++;
                    }

                    return new StepArg { Type = StepArg.Kind.Number, Number = double.TryParse(s.AsSpan(start, p - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0 };
                }

                {
                    // TYPE_NAME(args)
                    var start = p;
                    while (p < end && (char.IsLetterOrDigit(s[p]) || s[p] == '_'))
                    {
                        p++;
                    }

                    var name = s[start..p];
                    if (p == start)
                    {
                        p++; // skip a stray character
                        return StepArg.Null;
                    }

                    SkipSpaceAndCommas(s, ref p, end);
                    var typed = new StepArg { Type = StepArg.Kind.Typed, Text = name };
                    if (p < end && s[p] == '(')
                    {
                        p++;
                        while (true)
                        {
                            SkipSpaceAndCommas(s, ref p, end);
                            if (p >= end)
                            {
                                break;
                            }

                            if (s[p] == ')')
                            {
                                p++;
                                break;
                            }

                            typed.Items.Add(ParseValue(s, ref p, end, depth + 1));
                        }
                    }

                    return typed;
                }
        }
    }

    /// <summary>ISO 10303-21 string escapes: \X\HH (one byte), \X2\HHHH...\X0\ (UTF-16), \S\c (the next character plus 128).</summary>
    public static string Decode(string text)
    {
        if (!text.Contains('\\'))
        {
            return text;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\')
            {
                sb.Append(text[i]);
                continue;
            }

            if (i + 3 < text.Length && text[i + 1] == 'X' && text[i + 2] == '\\')
            {
                if (int.TryParse(text.AsSpan(i + 3, Math.Min(2, text.Length - i - 3)), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                {
                    sb.Append((char)b);
                }

                i += 4;
            }
            else if (i + 3 < text.Length && text[i + 1] == 'X' && text[i + 2] == '2' && text[i + 3] == '\\')
            {
                var end = text.IndexOf("\\X0\\", i + 4, StringComparison.Ordinal);
                var hex = text[(i + 4)..(end < 0 ? text.Length : end)];
                for (var k = 0; k + 3 < hex.Length + 0 && k + 4 <= hex.Length; k += 4)
                {
                    if (int.TryParse(hex.AsSpan(k, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp))
                    {
                        sb.Append((char)cp);
                    }
                }

                i = end < 0 ? text.Length : end + 3;
            }
            else if (i + 3 < text.Length && text[i + 1] == 'S' && text[i + 2] == '\\')
            {
                sb.Append((char)(text[i + 3] + 128));
                i += 3;
            }
            else
            {
                sb.Append(text[i]);
            }
        }

        return sb.ToString();
    }
}
