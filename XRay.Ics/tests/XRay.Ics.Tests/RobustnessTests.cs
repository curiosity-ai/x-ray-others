using System.Diagnostics;
using System.Text;

namespace XRay.Ics.Tests;

/// <summary>
/// Damaged input must fail cleanly: a stream that cannot be read as a calendar raises
/// <see cref="IcsFormatException"/>, never another exception type, a hang or a stack overflow.
/// Seeded truncations and byte mutations of every small corpus fixture; set XRAY_ICS_MUTATIONS for a deeper run.
/// </summary>
public sealed class RobustnessTests
{
    private const int MaxFixtureSize = 200 * 1024;

    public static TheoryData<string> Fixtures() => new(IcsManifest.Entries.Keys.Order(StringComparer.Ordinal));

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Damaged_fixture_fails_cleanly(string key)
    {
        var original = File.ReadAllBytes(TestDocuments.RequireFile(IcsManifest.PathOf(key)));
        if (original.Length > MaxFixtureSize)
        {
            return;
        }

        var iterations = int.TryParse(Environment.GetEnvironmentVariable("XRAY_ICS_MUTATIONS"), out var configured) ? configured : 12;
        var random = new Random(Seed(key));
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            var data = Damage(original, random, iteration);
            var clock = Stopwatch.StartNew();
            try
            {
                Exercise(data);
            }
            catch (IcsFormatException)
            {
            }
            catch (Exception exception)
            {
                Assert.Fail($"{key} iteration {iteration}: {exception.GetType().Name}: {exception.Message}\n{exception.StackTrace}");
            }

            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"{key} iteration {iteration} took {clock.Elapsed}");
        }
    }

    [Fact]
    public void Every_prefix_of_a_rich_fixture_loads_or_fails_cleanly()
    {
        var original = File.ReadAllBytes(TestDocuments.RequireFile("ics/xray-ics/outlook-meeting-request.ics"));
        for (var length = 0; length <= original.Length; length += 7)
        {
            try
            {
                Exercise(original[..length]);
            }
            catch (IcsFormatException)
            {
            }
        }
    }

    [Fact]
    public void Random_bytes_and_random_lines_fail_cleanly()
    {
        var random = new Random(1234);
        for (var i = 0; i < 300; i++)
        {
            var data = new byte[random.Next(1, 600)];
            random.NextBytes(data);
            if (i % 2 == 0)
            {
                data = [.. Encoding.ASCII.GetBytes("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\n"), .. data];
            }

            try
            {
                Exercise(data);
            }
            catch (IcsFormatException)
            {
            }
        }

        var alphabet = new[] { "BEGIN:", "END:", "VEVENT", "VCALENDAR", ";", ":", "=", "\"", "\\", "\r\n", "\n", " ", "DTSTART", "RRULE", "FREQ=DAILY;", "TZID=", "20260101T000000Z", "ATTENDEE", "ATTACH", "ENCODING=BASE64", "^", ",", "VTIMEZONE", "STANDARD" };
        for (var i = 0; i < 300; i++)
        {
            var builder = new StringBuilder("BEGIN:VCALENDAR\r\n");
            for (var j = 0; j < random.Next(5, 80); j++)
            {
                builder.Append(alphabet[random.Next(alphabet.Length)]);
            }

            try
            {
                Exercise(Encoding.UTF8.GetBytes(builder.ToString()));
            }
            catch (IcsFormatException)
            {
            }
        }
    }

    [Fact]
    public void Hostile_rules_and_dates_do_not_hang_or_throw()
    {
        var lines = new[]
        {
            "RRULE:FREQ=SECONDLY;INTERVAL=2147483647",
            "RRULE:FREQ=YEARLY;INTERVAL=2147483647;COUNT=5",
            "RRULE:FREQ=DAILY;BYSETPOS=1,2,3,-1,0,99999;BYDAY=1MO,-53SU,54FR",
            "RRULE:FREQ=MONTHLY;BYMONTHDAY=31;BYMONTH=2",
            "RRULE:FREQ=WEEKLY;BYWEEKNO=53,-53,0;WKST=XX",
            "RRULE:FREQ=HOURLY;BYHOUR=24,25;BYMINUTE=61",
            "RRULE:FREQ=YEARLY;UNTIL=00000000T000000Z",
            "RRULE:" + string.Join(";", Enumerable.Repeat("BYDAY=MO,TU,WE", 500)) + ";FREQ=DAILY",
            "DTSTART:99991231T235959Z",
            "DTSTART:00010101T000000Z",
            "DURATION:P99999999999W",
            "RDATE:" + string.Join(",", Enumerable.Repeat("20260101T000000Z", 5000)),
            "EXDATE:" + string.Join(",", Enumerable.Repeat("20260101T000000Z", 5000)),
        };
        foreach (var line in lines)
        {
            var calendar = Tok.Load(Tok.Cal("BEGIN:VEVENT", "UID:h", "DTSTART:20260101T000000Z", line, "END:VEVENT"));
            var clock = Stopwatch.StartNew();
            Exercise(Encoding.UTF8.GetBytes(calendar.ToIcsString(Tok.Ct)));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), line[..Math.Min(40, line.Length)] + " took " + clock.Elapsed);
        }
    }

    [Fact]
    public void Dates_at_the_ends_of_the_range_do_not_throw()
    {
        var sets = new[]
        {
            new[] { "DTSTART:00010101T000000Z", "DURATION:P1D", "RRULE:FREQ=WEEKLY;COUNT=3" },
            new[] { "DTSTART:99991231T235959Z", "DURATION:PT1H", "RRULE:FREQ=DAILY;COUNT=3", "RDATE:99991231T235959Z" },
            new[] { "DTSTART;TZID=Asia/Tokyo:00010101T000000", "DTEND;TZID=America/New_York:00010101T000000", "RRULE:FREQ=MONTHLY;COUNT=3" },
            new[] { "DTSTART;TZID=America/New_York:99991231T230000", "DTEND;TZID=Asia/Tokyo:99991231T235959", "RRULE:FREQ=DAILY;UNTIL=99991231", "EXDATE:99991231T235959Z" },
            new[] { "DTSTART;VALUE=DATE:99991231", "RRULE:FREQ=YEARLY;BYWEEKNO=53,1;BYDAY=MO,SU", "EXDATE;VALUE=DATE:00010101" },
            new[] { "DTSTART;VALUE=DATE:00010101", "DTEND;VALUE=DATE:99991231", "RRULE:FREQ=WEEKLY;UNTIL=99991231T235959Z;WKST=SU" },
            new[] { "DTSTART:00010101T000000", "RRULE:FREQ=SECONDLY;INTERVAL=1;BYSETPOS=-1" },
            new[] { "DTSTART:99991231T235959", "RRULE:FREQ=HOURLY;COUNT=5", "DURATION:P99999W" },
        };
        foreach (var set in sets)
        {
            var calendar = Tok.Load(Tok.Cal(["BEGIN:VEVENT", "UID:x", .. set, "END:VEVENT"]));
            var anEvent = calendar.Events[0];
            _ = anEvent.End;
            _ = anEvent.Duration;
            _ = anEvent.Expand(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 50, null, Tok.Ct).ToList();
            _ = anEvent.Expand(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 50, TimeZoneInfo.FindSystemTimeZoneById("Pacific/Kiritimati"), Tok.Ct).ToList();
            _ = calendar.Expand(DateTimeOffset.MinValue, DateTimeOffset.MaxValue, 50, null, Tok.Ct);
            var result = new IcsExtractResult(calendar, new IcsRenderOptions());
            _ = result.ToHtml(new IcsRenderOptions { DisplayTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo") }, Tok.Ct);
            _ = result.ToPlainText(new IcsRenderOptions { DisplayTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles") }, Tok.Ct);
            _ = anEvent.Start?.ToLocalTime(TimeZoneInfo.FindSystemTimeZoneById("Pacific/Kiritimati"));
            _ = anEvent.Start?.CompareTo(anEvent.End ?? default);
            Exercise(calendar.ToBytes(Tok.Ct));
        }
    }

    internal static void Exercise(byte[] data)
    {
        var calendar = Calendar.Load(data, null, Tok.Ct);
        foreach (var anEvent in calendar.Events.Concat<CalendarComponent>(calendar.Todos).Concat(calendar.Journals).Concat(calendar.FreeBusies))
        {
            _ = anEvent.Start;
            _ = anEvent.Created;
            _ = anEvent.Stamp;
            _ = anEvent.RecurrenceId;
            _ = anEvent.Summary;
            _ = anEvent.Description;
            _ = anEvent.Organizer;
            _ = anEvent.Attendees.Select(a => (a.Address, a.DisplayName, a.ToString())).ToList();
            _ = anEvent.Categories;
            _ = anEvent.RecurrenceRules.Select(r => r.ToString() + r.Describe()).ToList();
            _ = anEvent.RecurrenceDates;
            _ = anEvent.ExceptionDates;
            foreach (var attachment in anEvent.Attachments)
            {
                _ = attachment.Name;
                _ = attachment.Size;
                using var sink = new MemoryStream();
                attachment.WriteTo(sink, Tok.Ct);
            }

            if (anEvent is CalendarEvent calendarEvent)
            {
                _ = calendarEvent.End;
                _ = calendarEvent.Duration;
                _ = calendarEvent.Geo;
                _ = calendarEvent.Alarms.Select(a => (a.Trigger, a.TriggerTime, a.Repeat)).ToList();
                _ = calendarEvent.Expand(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddYears(80), 30, null, Tok.Ct).ToList();
            }

            if (anEvent is CalendarTodo todo)
            {
                _ = todo.Due;
                _ = todo.PercentComplete;
            }

            if (anEvent is CalendarFreeBusy freeBusy)
            {
                _ = freeBusy.Periods;
            }
        }

        _ = calendar.Expand(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddYears(80), 100, null, Tok.Ct);
        _ = calendar.TimeZones.Select(z => z.GetUtcOffset(new DateTime(2026, 7, 1))).ToList();
        var result = new IcsExtractResult(calendar, new IcsRenderOptions());
        _ = result.ToHtml(null, Tok.Ct);
        _ = result.ToPlainText(null, Tok.Ct);
        _ = result.ToMarkdown(null, Tok.Ct);
        var saved = calendar.ToBytes(Tok.Ct);
        _ = Calendar.Load(saved, null, Tok.Ct);
    }

    private static byte[] Damage(byte[] original, Random random, int iteration)
    {
        var data = original.ToArray();
        switch (iteration % 4)
        {
            case 0:
                return data[..random.Next(data.Length + 1)];
            case 1:
            {
                var flips = 1 + random.Next(16);
                for (var flip = 0; flip < flips && data.Length > 0; flip++)
                {
                    data[random.Next(data.Length)] = (byte)random.Next(256);
                }

                return data;
            }

            case 2:
            {
                // Delete a run of bytes.
                if (data.Length < 4)
                {
                    return data;
                }

                var start = random.Next(data.Length - 1);
                var length = random.Next(1, Math.Min(64, data.Length - start));
                return [.. data[..start], .. data[(start + length)..]];
            }

            default:
            {
                // Duplicate a run of bytes.
                if (data.Length < 4)
                {
                    return data;
                }

                var start = random.Next(data.Length - 1);
                var length = random.Next(1, Math.Min(64, data.Length - start));
                return [.. data[..(start + length)], .. data[start..(start + length)], .. data[(start + length)..]];
            }
        }
    }

    private static int Seed(string key)
    {
        var hash = 17;
        foreach (var c in key)
        {
            hash = hash * 31 + c;
        }

        return hash;
    }
}
