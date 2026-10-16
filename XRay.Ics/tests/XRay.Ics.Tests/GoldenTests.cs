using System.Globalization;
using System.Text.Json;

namespace XRay.Ics.Tests;

/// <summary>
/// Compares extraction with reference output recorded by another implementation, when such
/// reference files are present. They are not part of the corpus: drop them into
/// <c>test_documents/ics/golden/</c> (or the folder named by <c>XRAY_ICS_GOLDEN</c>) and the tests below run;
/// without the folder they skip.
///
/// One file per fixture, at the fixture's path below <c>ics/</c> with <c>.ics</c> replaced by <c>.golden.json</c>
/// (for example <c>golden/xray-ics/basic-event.golden.json</c>), and optionally the HTML next to it as
/// <c>.golden.html</c>:
/// <code>
/// {
///   "created":    "2025-12-30T10:15:00+00:00" | null,
///   "organizer":  { "name": "...", "address": "..." } | null,
///   "attendees":  [ { "name": "...", "address": "..." } ],      // required attendees
///   "optional_attendees": [ { "name": "...", "address": "..." } ],
///   "summary":    "..." | null,
///   "description": "..." | null,
///   "attachments": [ "name.ext" ]                              // named attachments only
/// }
/// </code>
/// Addresses are compared case-insensitively (a consumer lower-cases them); everything else exactly.
/// </summary>
public sealed class GoldenTests
{
    private static string? GoldenRoot()
    {
        var configured = Environment.GetEnvironmentVariable("XRAY_ICS_GOLDEN");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Directory.Exists(configured) ? configured : null;
        }

        var root = TestDocuments.Root;
        var folder = root is null ? null : Path.Combine(root, "ics", "golden");
        return folder is not null && Directory.Exists(folder) ? folder : null;
    }

    public static TheoryData<string> Goldens()
    {
        var folder = GoldenRoot();
        var files = folder is null ? [] : Directory.EnumerateFiles(folder, "*.golden.json", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/')).Order(StringComparer.Ordinal).ToList();
        return files.Count == 0 ? new TheoryData<string> { "(no golden files)" } : new TheoryData<string>(files);
    }

    [Theory]
    [MemberData(nameof(Goldens))]
    public void Extraction_matches_the_reference(string golden)
    {
        var folder = GoldenRoot();
        if (folder is null || golden == "(no golden files)")
        {
            Assert.Skip("No golden reference files (test_documents/ics/golden or XRAY_ICS_GOLDEN); parity with a reference implementation is not checked.");
            return;
        }

        var fixture = golden[..^".golden.json".Length] + ".ics";
        var path = TestDocuments.RequireFile("ics/" + fixture);
        var result = IcsExtractor.ExtractFile(path, null, Tok.Ct);
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, golden)));
        var expected = document.RootElement;
        var problems = new List<string>();

        if (expected.TryGetProperty("created", out var created))
        {
            var want = created.ValueKind == JsonValueKind.Null ? (DateTimeOffset?)null : DateTimeOffset.Parse(created.GetString()!, CultureInfo.InvariantCulture);
            if (want != result.CreatedDate)
            {
                problems.Add($"created: expected {want}, got {result.CreatedDate}");
            }
        }

        if (expected.TryGetProperty("organizer", out var organizer))
        {
            Person(problems, "organizer", organizer, result.Organizer);
        }

        if (expected.TryGetProperty("attendees", out var attendees))
        {
            People(problems, "attendees", attendees, result.Attendees);
        }

        if (expected.TryGetProperty("optional_attendees", out var optional))
        {
            People(problems, "optional_attendees", optional, result.OptionalAttendees);
        }

        Text(problems, "summary", expected, result.Summary);
        Text(problems, "description", expected, result.Description);
        if (expected.TryGetProperty("attachments", out var attachments))
        {
            var want = attachments.EnumerateArray().Select(a => a.GetString()).ToList();
            var got = result.Attachments.Where(a => !string.IsNullOrWhiteSpace(a.Name)).Select(a => a.Name).ToList();
            if (!want.SequenceEqual(got))
            {
                problems.Add($"attachments: expected [{string.Join(", ", want)}], got [{string.Join(", ", got)}]");
            }
        }

        var htmlPath = Path.Combine(folder, golden[..^".golden.json".Length] + ".golden.html");
        if (File.Exists(htmlPath) && File.ReadAllText(htmlPath) != result.ToHtml(null, Tok.Ct))
        {
            problems.Add("html differs from the golden page");
        }

        Assert.True(problems.Count == 0, fixture + "\n" + string.Join("\n", problems));
    }

    private static void Text(List<string> problems, string name, JsonElement expected, string? got)
    {
        if (expected.TryGetProperty(name, out var value) && (value.ValueKind == JsonValueKind.Null ? null : value.GetString()) != got)
        {
            problems.Add($"{name}: expected '{value}', got '{got}'");
        }
    }

    private static void People(List<string> problems, string name, JsonElement expected, IReadOnlyList<CalendarAddress> got)
    {
        if (expected.GetArrayLength() != got.Count)
        {
            problems.Add($"{name}: expected {expected.GetArrayLength()} entries, got {got.Count}");
            return;
        }

        var i = 0;
        foreach (var item in expected.EnumerateArray())
        {
            Person(problems, $"{name}[{i}]", item, got[i]);
            i++;
        }
    }

    private static void Person(List<string> problems, string name, JsonElement expected, CalendarAddress? got)
    {
        if (expected.ValueKind == JsonValueKind.Null || got is null)
        {
            if (expected.ValueKind != JsonValueKind.Null || got is not null)
            {
                problems.Add($"{name}: expected {(expected.ValueKind == JsonValueKind.Null ? "none" : "one")}, got {(got is null ? "none" : "one")}");
            }

            return;
        }

        var wantName = expected.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        var wantAddress = expected.TryGetProperty("address", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
        if (!string.Equals(wantName ?? string.Empty, got.CommonName ?? string.Empty, StringComparison.Ordinal))
        {
            problems.Add($"{name} name: expected '{wantName}', got '{got.CommonName}'");
        }

        if (!string.Equals(wantAddress, got.Address, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add($"{name} address: expected '{wantAddress}', got '{got.Address}'");
        }
    }
}
