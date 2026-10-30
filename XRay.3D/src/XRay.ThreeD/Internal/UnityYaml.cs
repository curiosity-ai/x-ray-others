using System.Globalization;

namespace XRay.ThreeD.Internal;

/// <summary>
/// The subset of YAML that Unity's text serialisation writes: block maps and lists (a list may sit at its key's indentation), flow maps
/// (<c>{x: 1, y: 2}</c>), scalars kept as strings, and the <c>--- !u!&lt;class&gt; &amp;&lt;file id&gt;</c> document separators.
/// Nodes are <see cref="Dictionary{TKey,TValue}"/> of string to object, <see cref="List{T}"/> of object, or string.
/// </summary>
internal static class UnityYaml
{
    public sealed record Document(int ClassId, long FileId, bool Stripped, string Type, Dictionary<string, object> Body);

    private const int MaxDepth = 64;

    public static List<Document> Parse(string text, CancellationToken cancel, int maxDocuments = 500_000)
    {
        var documents = new List<Document>();
        var lines = new List<(int Indent, string Text)>();
        var header = (ClassId: 0, FileId: 0L, Stripped: false);
        var tick = 0L;

        void Flush()
        {
            if (header.ClassId == 0)
            {
                lines.Clear();
                return;
            }

            var p = 0;
            var depth = 0;
            Dictionary<string, object> body = new(StringComparer.Ordinal);
            string type = "";
            if (lines.Count > 0)
            {
                var root = ParseMap(lines, ref p, lines[0].Indent, ref depth, cancel);
                // A document is "TypeName:" followed by its fields.
                if (root.Count == 1)
                {
                    var only = root.First();
                    type = only.Key;
                    body = only.Value as Dictionary<string, object> ?? new Dictionary<string, object>(StringComparer.Ordinal);
                }
            }

            if (documents.Count >= maxDocuments)
            {
                throw new ModelFormatException("The Unity YAML file has too many objects.");
            }

            documents.Add(new Document(header.ClassId, header.FileId, header.Stripped, type, body));
            lines.Clear();
        }

        foreach (var raw in text.AsSpan().EnumerateLines())
        {
            Cancel.Every(cancel, tick++);
            if (raw.Length == 0 || raw[0] == '%' || raw[0] == '#')
            {
                continue;
            }

            if (raw.StartsWith("--- !u!"))
            {
                Flush();
                var rest = raw[7..];
                var amp = rest.IndexOf('&');
                if (amp < 0 || !int.TryParse(rest[..amp].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var cls))
                {
                    header = (0, 0, false);
                    continue;
                }

                var idPart = rest[(amp + 1)..];
                var space = idPart.IndexOf(' ');
                var stripped = space >= 0 && idPart[space..].Contains("stripped", StringComparison.Ordinal);
                if (space >= 0)
                {
                    idPart = idPart[..space];
                }

                long.TryParse(idPart, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id);
                header = (cls, id, stripped);
                continue;
            }

            if (raw.StartsWith("---"))
            {
                Flush();
                header = (0, 0, false);
                continue;
            }

            var indent = 0;
            while (indent < raw.Length && raw[indent] == ' ')
            {
                indent++;
            }

            if (indent == raw.Length)
            {
                continue;
            }

            lines.Add((indent, raw[indent..].TrimEnd().ToString()));
        }

        Flush();
        return documents;
    }

    private static Dictionary<string, object> ParseMap(List<(int Indent, string Text)> lines, ref int p, int indent, ref int depth, CancellationToken cancel)
    {
        if (++depth > MaxDepth)
        {
            throw new ModelFormatException("Unity YAML is nested too deeply.");
        }

        var map = new Dictionary<string, object>(StringComparer.Ordinal);
        while (p < lines.Count && lines[p].Indent == indent && !lines[p].Text.StartsWith("- ", StringComparison.Ordinal) && lines[p].Text != "-")
        {
            Cancel.Every(cancel, p);
            var text = lines[p].Text;
            var colon = FindColon(text);
            p++;
            if (colon < 0)
            {
                continue;
            }

            var key = text[..colon].Trim();
            var value = text[(colon + 1)..].Trim();
            if (value.Length > 0)
            {
                map[key] = value.StartsWith('{') ? ParseFlow(value) : value;
            }
            else if (p < lines.Count && (lines[p].Indent > indent || (lines[p].Indent == indent && (lines[p].Text.StartsWith("- ", StringComparison.Ordinal) || lines[p].Text == "-"))))
            {
                map[key] = ParseBlock(lines, ref p, lines[p].Indent, ref depth, cancel);
            }
            else
            {
                map[key] = "";
            }
        }

        depth--;
        return map;
    }

    private static object ParseBlock(List<(int Indent, string Text)> lines, ref int p, int indent, ref int depth, CancellationToken cancel)
    {
        if (lines[p].Text.StartsWith("- ", StringComparison.Ordinal) || lines[p].Text == "-")
        {
            if (++depth > MaxDepth)
            {
                throw new ModelFormatException("Unity YAML is nested too deeply.");
            }

            var list = new List<object>();
            while (p < lines.Count && lines[p].Indent == indent && (lines[p].Text.StartsWith("- ", StringComparison.Ordinal) || lines[p].Text == "-"))
            {
                Cancel.Every(cancel, p);
                var content = lines[p].Text.Length > 2 ? lines[p].Text[2..].TrimStart() : "";
                if (content.Length == 0)
                {
                    p++;
                    if (p < lines.Count && lines[p].Indent > indent)
                    {
                        list.Add(ParseBlock(lines, ref p, lines[p].Indent, ref depth, cancel));
                    }
                }
                else if (content.StartsWith('{'))
                {
                    list.Add(ParseFlow(content));
                    p++;
                }
                else if (FindColon(content) >= 0)
                {
                    // "- key: value": the item is a map whose first entry sits two columns in.
                    lines[p] = (indent + 2, content);
                    list.Add(ParseMap(lines, ref p, indent + 2, ref depth, cancel));
                }
                else
                {
                    list.Add(content);
                    p++;
                }
            }

            depth--;
            return list;
        }

        return ParseMap(lines, ref p, indent, ref depth, cancel);
    }

    private static int FindColon(string text)
    {
        if (text.StartsWith('{') || text.StartsWith('"'))
        {
            return -1;
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == ':' && (i + 1 == text.Length || text[i + 1] == ' '))
            {
                return i;
            }
        }

        return -1;
    }

    private static Dictionary<string, object> ParseFlow(string text)
    {
        var map = new Dictionary<string, object>(StringComparer.Ordinal);
        var inner = text.Trim().TrimStart('{').TrimEnd('}');
        foreach (var part in inner.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var colon = part.IndexOf(':');
            if (colon > 0)
            {
                map[part[..colon].Trim()] = part[(colon + 1)..].Trim();
            }
        }

        return map;
    }

    // ---- accessors ------------------------------------------------------------------------------------------------

    public static string? Str(Dictionary<string, object> map, string key) => map.TryGetValue(key, out var v) ? v as string : null;

    public static Dictionary<string, object>? Map(Dictionary<string, object> map, string key) => map.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;

    public static List<object>? List(Dictionary<string, object> map, string key) => map.TryGetValue(key, out var v) ? v as List<object> : null;

    public static double Num(Dictionary<string, object>? map, string key, double fallback = 0)
    {
        if (map is not null && map.TryGetValue(key, out var v) && v is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d))
        {
            return d;
        }

        return fallback;
    }

    public static long Long(Dictionary<string, object>? map, string key, long fallback = 0) =>
        map is not null && map.TryGetValue(key, out var v) && v is string s && long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l) ? l : fallback;

    /// <summary>A <c>{fileID: n, guid: g}</c> reference.</summary>
    public static (long FileId, string? Guid) Ref(Dictionary<string, object>? map, string key)
    {
        var r = map is null ? null : Map(map, key);
        return (Long(r, "fileID"), r is not null ? Str(r, "guid") : null);
    }
}
