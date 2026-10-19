using System.Text.Json.Nodes;

namespace XRay.OneNote.Tests.Support;

/// <summary>Reads the goldens produced from the Python reference by <c>tools/regenerate_goldens.py</c>.</summary>
internal static class Goldens
{
    public static JsonNode Load(string kind, string fixture)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Goldens", kind, Path.GetFileNameWithoutExtension(fixture) + ".json");
        Assert.True(File.Exists(path), $"Missing golden {path}; run tools/regenerate_goldens.py.");
        return JsonNode.Parse(File.ReadAllText(path))!;
    }

    /// <summary>
    /// Compares two JSON trees: same keys, same array lengths, equal strings and booleans, numbers
    /// within a relative tolerance. Returns the differences as JSON paths (empty when equal).
    /// </summary>
    public static List<string> Diff(JsonNode? expected, JsonNode? actual, string path = "$", int limit = 50, Func<string, bool>? ignore = null)
    {
        var differences = new List<string>();

        // Round-trip through text so values built in memory compare like values read from a file.
        Walk(Reparse(expected), Reparse(actual), path, differences, limit, ignore);
        return differences;
    }

    private static JsonNode? Reparse(JsonNode? node) => node is null ? null : JsonNode.Parse(node.ToJsonString());

    private static void Walk(JsonNode? expected, JsonNode? actual, string path, List<string> differences, int limit, Func<string, bool>? ignore)
    {
        if (differences.Count >= limit || (ignore is not null && ignore(path)))
        {
            return;
        }

        switch (expected, actual)
        {
            case (null, null):
                return;
            case (null, _) or (_, null):
                differences.Add($"{path}: expected {Show(expected)}, actual {Show(actual)}");
                return;
            case (JsonObject e, JsonObject a):
                foreach (var key in e.Select(p => p.Key).Union(a.Select(p => p.Key)))
                {
                    if (!e.ContainsKey(key) || !a.ContainsKey(key))
                    {
                        differences.Add($"{path}.{key}: present only in {(e.ContainsKey(key) ? "expected" : "actual")}");
                        continue;
                    }

                    Walk(e[key], a[key], $"{path}.{key}", differences, limit, ignore);
                }

                return;
            case (JsonArray e, JsonArray a):
                if (e.Count != a.Count)
                {
                    differences.Add($"{path}: expected {e.Count} items, actual {a.Count}");
                }

                for (var index = 0; index < Math.Min(e.Count, a.Count); index++)
                {
                    Walk(e[index], a[index], $"{path}[{index}]", differences, limit, ignore);
                }

                return;
            case (JsonValue e, JsonValue a):
                if (!ValuesEqual(e, a))
                {
                    differences.Add($"{path}: expected {Show(e)}, actual {Show(a)}");
                }

                return;
            default:
                differences.Add($"{path}: expected {Show(expected)}, actual {Show(actual)}");
                return;
        }
    }

    private static bool ValuesEqual(JsonValue expected, JsonValue actual)
    {
        var kind = expected.GetValueKind();
        if (kind == System.Text.Json.JsonValueKind.Number && actual.GetValueKind() == System.Text.Json.JsonValueKind.Number)
        {
            var e = expected.GetValue<double>();
            var a = actual.GetValue<double>();
            return Math.Abs(e - a) <= 1e-9 * Math.Max(1.0, Math.Abs(e));
        }

        return kind == actual.GetValueKind() && expected.ToJsonString() == actual.ToJsonString();
    }

    private static string Show(JsonNode? node)
    {
        var text = node?.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) ?? "null";
        return text.Length > 160 ? text[..160] + "..." : text;
    }
}
