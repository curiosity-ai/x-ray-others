using System.Globalization;
using System.Text.Json;

namespace XRay.Ics.Tests;

/// <summary>
/// The expected fields of every .ics fixture, produced by independent readers (python icalendar,
/// recurring_ical_events) through tools/corpus/build_manifest.py.
/// </summary>
internal static class IcsManifest
{
    private static readonly Lazy<IReadOnlyDictionary<string, JsonElement>> CachedEntries = new(Load);

    public static IReadOnlyDictionary<string, JsonElement> Entries => CachedEntries.Value;

    /// <summary>Corpus-relative path (<c>ics/...</c>) of a manifest key.</summary>
    public static string PathOf(string key) => "ics/" + key;

    public static IEnumerable<string> Keys(Func<JsonElement, bool> filter) => Entries.Where(item => filter(item.Value)).Select(item => item.Key).Order(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, JsonElement> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Corpus", "ics-manifest.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateObject().ToDictionary(item => item.Name, item => item.Value.Clone(), StringComparer.Ordinal);
    }

    public static string Format(IcsDateTime? value)
    {
        if (value is not { } date)
        {
            return "null";
        }

        if (date.IsDate)
        {
            return date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        if (date.Offset is { } offset)
        {
            return (date.Value - offset).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        }

        return date.Value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static string? Str(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Has(JsonElement element, string name) => element.TryGetProperty(name, out _);

    /// <summary>Compares a loaded calendar with its manifest entry and returns every mismatch, one per line.</summary>
    public static string Compare(Calendar calendar, JsonElement expected)
    {
        var problems = new List<string>();
        if (expected.TryGetProperty("counts", out var counts))
        {
            Check(problems, "VEVENT", counts, calendar.Events.Count);
            Check(problems, "VTODO", counts, calendar.Todos.Count);
            Check(problems, "VJOURNAL", counts, calendar.Journals.Count);
            Check(problems, "VFREEBUSY", counts, calendar.FreeBusies.Count);
            Check(problems, "VTIMEZONE", counts, calendar.TimeZones.Count);
        }

        if (Str(expected, "method") is { } method && !string.Equals(calendar.MethodText, method, StringComparison.Ordinal))
        {
            problems.Add($"method: expected {method}, got {calendar.MethodText}");
        }

        if (expected.TryGetProperty("version", out var versionElement) && versionElement.ValueKind == JsonValueKind.String && calendar.Version != versionElement.GetString())
        {
            problems.Add($"version: expected {versionElement.GetString()}, got {calendar.Version}");
        }

        if (expected.TryGetProperty("events", out var events))
        {
            var partial = expected.TryGetProperty("events_partial", out var flag) && flag.GetBoolean();
            var actual = calendar.Events.ToList();
            if (!partial && actual.Count != events.GetArrayLength())
            {
                problems.Add($"event count: expected {events.GetArrayLength()}, got {actual.Count}");
            }
            else
            {
                var index = 0;
                foreach (var item in events.EnumerateArray())
                {
                    var uid = Str(item, "uid");
                    var match = partial ? actual.FirstOrDefault(e => e.Uid == uid) : actual[index];
                    index++;
                    if (match is null)
                    {
                        problems.Add($"event {uid}: missing");
                        continue;
                    }

                    CompareEvent(problems, match, item, partial);
                }
            }
        }

        if (expected.TryGetProperty("expansion", out var expansion) && expansion.ValueKind == JsonValueKind.Object)
        {
            CompareExpansion(problems, calendar, expansion);
        }

        return string.Join("\n", problems);
    }

    private static void Check(List<string> problems, string name, JsonElement counts, int actual)
    {
        if (counts.TryGetProperty(name, out var value) && value.GetInt32() != actual)
        {
            problems.Add($"{name} count: expected {value.GetInt32()}, got {actual}");
        }
    }

    private static void CompareEvent(List<string> problems, CalendarEvent actual, JsonElement expected, bool partial)
    {
        var uid = actual.Uid;
        void Text(string name, string? got)
        {
            if (partial && !Has(expected, name))
            {
                return;
            }

            var want = Str(expected, name);
            if (!string.Equals(want, got, StringComparison.Ordinal))
            {
                problems.Add($"{uid} {name}: expected '{want}', got '{got}'");
            }
        }

        Text("uid", actual.Uid);
        Text("summary", actual.Summary);
        Text("description", actual.Description);
        Text("location", actual.Location);
        Text("start", Format(actual.Start) is "null" ? null : Format(actual.Start));
        Text("end", actual.Start is null ? null : Format(actual.End));
        Text("created", Format(actual.Created) is "null" ? null : Format(actual.Created));
        Text("stamp", Format(actual.Stamp) is "null" ? null : Format(actual.Stamp));
        if (!partial || Has(expected, "status"))
        {
            Text("status", actual.Raw.GetValue("STATUS")?.Trim().ToUpperInvariant());
        }

        if (!partial || Has(expected, "organizer"))
        {
            var organizer = expected.TryGetProperty("organizer", out var o) && o.ValueKind == JsonValueKind.Object ? o : (JsonElement?)null;
            if (organizer is null != (actual.Organizer is null))
            {
                problems.Add($"{uid} organizer: expected {(organizer is null ? "none" : "one")}, got {(actual.Organizer is null ? "none" : "one")}");
            }
            else if (organizer is { } want && actual.Organizer is { } got)
            {
                ComparePerson(problems, uid + " organizer", got, want, attendee: false);
            }
        }

        if (!partial || Has(expected, "attendees"))
        {
            var attendees = expected.GetProperty("attendees");
            var got = actual.Attendees;
            if (attendees.GetArrayLength() != got.Count)
            {
                problems.Add($"{uid} attendee count: expected {attendees.GetArrayLength()}, got {got.Count}");
            }
            else
            {
                var i = 0;
                foreach (var want in attendees.EnumerateArray())
                {
                    ComparePerson(problems, $"{uid} attendee[{i}]", got[i], want, attendee: true);
                    i++;
                }
            }
        }

        if (!partial || Has(expected, "attachments"))
        {
            var attachments = expected.GetProperty("attachments");
            var got = actual.Attachments;
            if (attachments.GetArrayLength() != got.Count)
            {
                problems.Add($"{uid} attachment count: expected {attachments.GetArrayLength()}, got {got.Count}");
            }
            else
            {
                var i = 0;
                foreach (var want in attachments.EnumerateArray())
                {
                    var a = got[i];
                    if (Str(want, "name") != a.Name)
                    {
                        problems.Add($"{uid} attachment[{i}] name: expected '{Str(want, "name")}', got '{a.Name}'");
                    }

                    if (Str(want, "content_type") != a.ContentType)
                    {
                        problems.Add($"{uid} attachment[{i}] type: expected '{Str(want, "content_type")}', got '{a.ContentType}'");
                    }

                    if (Str(want, "uri") != a.Uri)
                    {
                        problems.Add($"{uid} attachment[{i}] uri: expected '{Str(want, "uri")}', got '{a.Uri}'");
                    }

                    long? size = want.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : null;
                    if (size != (a.HasData ? a.GetBytes().LongLength : null))
                    {
                        problems.Add($"{uid} attachment[{i}] size: expected {size}, got {(a.HasData ? a.GetBytes().LongLength : null)}");
                    }

                    i++;
                }
            }
        }

        if (!partial || Has(expected, "categories"))
        {
            var want = expected.GetProperty("categories").EnumerateArray().Select(c => c.GetString()).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!.Trim()).ToList();
            if (!want.SequenceEqual(actual.Categories))
            {
                problems.Add($"{uid} categories: expected [{string.Join("|", want)}], got [{string.Join("|", actual.Categories)}]");
            }
        }

        if (!partial || Has(expected, "recurring"))
        {
            // The oracle counts an unreadable RRULE as recurring; the library only counts one it can read.
            var want = expected.GetProperty("recurring").GetBoolean();
            var got = actual.IsRecurring;
            if (want != got)
            {
                problems.Add($"{uid} recurring: expected {want}, got {got}");
            }
        }

        if (!partial || Has(expected, "all_day"))
        {
            var want = expected.GetProperty("all_day").GetBoolean();
            if (want != (actual.Start?.IsDate == true))
            {
                problems.Add($"{uid} all_day: expected {want}, got {actual.Start?.IsDate == true}");
            }
        }
    }

    private static void ComparePerson(List<string> problems, string label, CalendarAddress got, JsonElement want, bool attendee)
    {
        if (Str(want, "email") != got.Email)
        {
            problems.Add($"{label} email: expected '{Str(want, "email")}', got '{got.Email}'");
        }

        if (Str(want, "address") != got.Address && !(want.TryGetProperty("address_unreliable", out var unreliable) && unreliable.GetBoolean()))
        {
            problems.Add($"{label} address: expected '{Str(want, "address")}', got '{got.Address}'");
        }

        if (Str(want, "cn") != got.CommonName)
        {
            problems.Add($"{label} cn: expected '{Str(want, "cn")}', got '{got.CommonName}'");
        }

        if (!attendee)
        {
            return;
        }

        var role = got.Role switch
        {
            AttendeeRole.OptionalParticipant => "OPT-PARTICIPANT",
            AttendeeRole.NonParticipant => "NON-PARTICIPANT",
            AttendeeRole.Chair => "CHAIR",
            _ => "REQ-PARTICIPANT",
        };
        if (Str(want, "role") != role)
        {
            problems.Add($"{label} role: expected '{Str(want, "role")}', got '{role}'");
        }

        var partstat = got.Status switch
        {
            ParticipationStatus.InProcess => "IN-PROCESS",
            ParticipationStatus.NeedsAction => "NEEDS-ACTION",
            _ => got.Status.ToString().ToUpperInvariant(),
        };
        if (Str(want, "partstat") != partstat)
        {
            problems.Add($"{label} partstat: expected '{Str(want, "partstat")}', got '{partstat}'");
        }

        bool? rsvp = want.TryGetProperty("rsvp", out var r) && r.ValueKind is JsonValueKind.True or JsonValueKind.False ? r.GetBoolean() : null;
        if (rsvp != got.Rsvp)
        {
            problems.Add($"{label} rsvp: expected {rsvp}, got {got.Rsvp}");
        }

        if (Str(want, "cutype") != got.UserType.ToString().ToUpperInvariant())
        {
            problems.Add($"{label} cutype: expected '{Str(want, "cutype")}', got '{got.UserType.ToString().ToUpperInvariant()}'");
        }
    }

    private static void CompareExpansion(List<string> problems, Calendar calendar, JsonElement expansion)
    {
        var from = DateTimeOffset.Parse(expansion.GetProperty("from").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var to = DateTimeOffset.Parse(expansion.GetProperty("to").GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var wanted = expansion.GetProperty("first").EnumerateArray().Select(item => (Start: item.GetProperty("start").GetString()!, Uid: item.GetProperty("uid").GetString()!)).ToList();
        var got = calendar.Expand(from, to, 200000)
            .Select(o => (Start: Format(o.Start), Uid: o.Event.Uid ?? "None"))
            .OrderBy(x => x.Start, StringComparer.Ordinal).ThenBy(x => x.Uid, StringComparer.Ordinal)
            .Take(wanted.Count)
            .ToList();
        if (!wanted.SequenceEqual(got))
        {
            problems.Add("expansion: expected [" + string.Join(", ", wanted.Select(w => w.Start + " " + w.Uid)) + "] got [" + string.Join(", ", got.Select(g => g.Start + " " + g.Uid)) + "]");
        }
    }
}
