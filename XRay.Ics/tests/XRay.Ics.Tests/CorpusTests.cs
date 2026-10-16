using System.Text.Json;

namespace XRay.Ics.Tests;

/// <summary>Every fixture of test_documents/ics against the expectations read by independent readers.</summary>
public sealed class CorpusTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Readable() => new(IcsManifest.Keys(e => e.TryGetProperty("parsed", out var p) && p.GetBoolean()));

    public static TheoryData<string> Unreadable() => new(IcsManifest.Keys(e => (!e.TryGetProperty("parsed", out var p) || !p.GetBoolean()) && !e.TryGetProperty("not_a_calendar", out _)));

    public static TheoryData<string> NotCalendars() => new(IcsManifest.Keys(e => e.TryGetProperty("not_a_calendar", out _)));

    public static TheoryData<string> All() => new(IcsManifest.Entries.Keys.Order(StringComparer.Ordinal).Where(k => !IcsManifest.Entries[k].TryGetProperty("not_a_calendar", out _)));

    [Fact]
    public void Manifest_covers_every_fixture_in_the_corpus()
    {
        var root = TestDocuments.RequireRoot();
        var missing = new List<string>();
        foreach (var source in new[] { "xray-ics", "icalendar-py", "ical-net" })
        {
            var folder = Path.Combine(root, "ics", source);
            Assert.True(Directory.Exists(folder), $"{folder} is missing from the corpus.");
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (name is "README.md" or "LICENSE" or "LICENSE.rst" or "license.md")
                {
                    continue;
                }

                var key = Path.GetRelativePath(Path.Combine(root, "ics"), file).Replace('\\', '/');
                if (!IcsManifest.Entries.ContainsKey(key))
                {
                    missing.Add(key);
                }
            }
        }

        Assert.True(missing.Count == 0, "Fixtures not in the manifest (rebuild it with tools/corpus/build_manifest.py): " + string.Join(", ", missing));
    }

    [Theory]
    [MemberData(nameof(Readable))]
    public void Fixture_matches_the_independent_reader(string key)
    {
        var path = TestDocuments.RequireFile(IcsManifest.PathOf(key));
        var calendar = Calendar.Load(File.ReadAllBytes(path), null, Ct);
        var differences = IcsManifest.Compare(calendar, IcsManifest.Entries[key]);
        Assert.True(differences.Length == 0, key + "\n" + differences);
    }

    [Theory]
    [MemberData(nameof(Unreadable))]
    public void Fixture_the_oracle_cannot_read_still_loads_or_fails_cleanly(string key)
    {
        var path = TestDocuments.RequireFile(IcsManifest.PathOf(key));
        try
        {
            var calendar = Calendar.Load(File.ReadAllBytes(path), null, Ct);
            _ = calendar.Events.Select(e => (e.Start, e.End, e.Attendees, e.Organizer, e.Attachments, e.Summary)).ToList();
            _ = calendar.Expand(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddYears(60), 50, null, Ct);
        }
        catch (IcsFormatException)
        {
        }
    }

    [Theory]
    [MemberData(nameof(NotCalendars))]
    public void Non_calendar_input_raises_IcsFormatException(string key)
    {
        var path = TestDocuments.RequireFile(IcsManifest.PathOf(key));
        Assert.Throws<IcsFormatException>(() => Calendar.Load(File.ReadAllBytes(path), null, Ct));
    }

    [Theory]
    [MemberData(nameof(All))]
    public void Fixture_renders_and_round_trips(string key)
    {
        var path = TestDocuments.RequireFile(IcsManifest.PathOf(key));
        Calendar calendar;
        try
        {
            calendar = Calendar.Load(File.ReadAllBytes(path), null, Ct);
        }
        catch (IcsFormatException)
        {
            return;
        }

        var result = new IcsExtractResult(calendar, new IcsRenderOptions());
        var html = result.ToHtml(null, Ct);
        Assert.Equal(html, result.ToHtml(null, Ct));
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("<!DOCTYPE html>", html, StringComparison.Ordinal);
        _ = result.ToPlainText(null, Ct);
        _ = result.ToMarkdown(null, Ct);

        var saved = calendar.ToBytes(Ct);
        var again = Calendar.Load(saved, null, Ct);
        Assert.Equal(Dump(calendar.Raw), Dump(again.Raw));
    }

    internal static string Dump(IcsComponent component)
    {
        var builder = new System.Text.StringBuilder();
        Write(builder, component, 0);
        return builder.ToString();
    }

    private static void Write(System.Text.StringBuilder builder, IcsComponent component, int depth)
    {
        builder.Append(' ', depth).Append(component.Name).Append('\n');
        foreach (var property in component.Properties)
        {
            if (depth == 0 && property.Name is "PRODID" or "VERSION")
            {
                continue;
            }

            // Values that were quoted-printable are written back as plain TEXT: compare their text.
            var decoded = string.Equals(property.GetParameter("ENCODING"), "QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase);
            builder.Append(' ', depth + 1).Append(property.Group is null ? string.Empty : property.Group + ".").Append(property.Name);
            foreach (var parameter in property.Parameters.Where(p => !decoded || p.Name is not ("ENCODING" or "CHARSET")))
            {
                builder.Append(';').Append(parameter.Name).Append('=').Append(string.Join("|", parameter.Values.Select(Clean)));
            }

            var value = decoded && property.Name != "CATEGORIES" ? XRay.Ics.Internal.IcsText.Escape(property.TextValue) : property.Value;
            builder.Append(':').Append(Clean(value)).Append('\n');
        }

        foreach (var child in component.Children)
        {
            Write(builder, child, depth + 1);
        }
    }

    // The writer drops control characters and turns line breaks into the \n escape.
    private static string Clean(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\r' && i + 1 < value.Length && value[i + 1] == '\n')
            {
                i++;
                builder.Append("\\n");
            }
            else if (c is '\r' or '\n')
            {
                builder.Append("\\n");
            }
            else if (c >= ' ' || c == '\t')
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
