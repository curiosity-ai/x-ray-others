![X-Ray](https://raw.githubusercontent.com/curiosity-ai/x-ray-others/main/assets/logo.png)

# X-Ray.Ics

A .NET 10 library for iCalendar (`.ics`, RFC 5545 with the iTIP basics of RFC 5546) and vCalendar 1.0:
a tolerant reader, typed events, to-dos, journal entries and free/busy components, time zone
resolution, recurrence expansion, a writer, and extraction of an appointment to HTML, plain text and
Markdown. It has no dependencies outside the .NET base class library.

Part of the [X-Ray](https://github.com/curiosity-ai/x-ray) family of document libraries.

## Install

```sh
dotnet add package X-Ray.Ics
```

## Features

- **Tolerant reader**: CRLF, LF and CR line ends in one file, folding with space or tab, quoted
  parameter values and RFC 6868 carets, TEXT unescaping, X- properties and vCard-style groups kept.
  Encodings are detected: UTF-8 with or without BOM, UTF-16 and UTF-32, and legacy bytes through
  Windows-1252 or the code page a line names (`CHARSET=`). Damaged input is read as far as it goes
  (unclosed components, stray `END`, garbage lines, truncation) and reports what it skipped in
  `Calendar.Warnings`. Only input that is not a calendar at all, or over a limit, raises `IcsFormatException`.
- **vCalendar 1.0**: `ENCODING=QUOTED-PRINTABLE` (with soft line breaks) and `CHARSET=` on any line,
  bare parameters, `TZ`, `DALARM`/`AALARM`/`PALARM`/`MALARM`, and the `D1 #5`, `W1 MO WE`, `MD1 1 15`
  style of recurrence rule.
- **Typed model**: `Calendar` with `CalendarEvent`, `CalendarTodo`, `CalendarJournal`,
  `CalendarFreeBusy`, `CalendarTimeZone`. Uid, summary, description (plus Outlook's HTML description),
  location, geo, start, end and duration, created, last-modified, stamp, status, priority,
  classification, transparency, organizer, attendees, categories, URL, alarms, attachments and the iTIP
  `METHOD`. The raw tree (`Calendar.Raw`) keeps everything else, so a load and a save loses nothing.
- **Time zones**: `TZID` times resolve to an offset through the calendar's own `VTIMEZONE` rules, else
  IANA ids, Windows zone names and Mozilla-style ids through `TimeZoneInfo`. Floating times, dates
  (all-day), UTC and unresolvable zones are kept apart in `IcsDateTime.Kind`.
- **Attendees**: role, participation status, RSVP, calendar user type, common name and e-mail
  (`mailto:` removed, the `EMAIL` parameter used when the value is not an address), split into
  required, optional, non-participant, chair and resource lists.
- **Recurrence**: `RecurrenceRule` parses `RRULE` and expands FREQ from SECONDLY to YEARLY with INTERVAL,
  COUNT, UNTIL, BYSECOND/BYMINUTE/BYHOUR, BYDAY (with ordinals), BYMONTHDAY, BYYEARDAY, BYWEEKNO,
  BYMONTH, BYSETPOS and WKST. `CalendarEvent.Expand(from, to, limit)` adds RDATE and EXDATE and keeps
  daylight saving time; `Calendar.Expand` also applies `RECURRENCE-ID` overrides and cancellations.
- **Attachments**: inline base64 (`FILENAME`, `X-FILENAME`, `FMTTYPE`), `data:` URIs and URI references;
  read as a stream or written straight to a stream without keeping a copy.
- **Extraction**: `IcsExtractor` returns the created date, organizer, attendees and optional attendees,
  summary, description and attachments of the primary component, and renders an Outlook-style
  appointment page (`ToHtml`), plain text (`ToPlainText`) and Markdown (`ToMarkdown`).
- **Writer**: `Calendar.Save(Stream)` writes RFC 5545 text, CRLF, UTF-8 without BOM, folded at 75 octets
  without splitting a character, with TEXT and parameter escaping.
- **Streams and cancellation**: every load, save, expand, render and extract method takes a
  `CancellationToken` and stops promptly; output goes to a `Stream` without buffering the whole file.

## Quick start

Read what an indexer needs from an appointment:

```csharp
using XRay.Ics;

IcsExtractResult appointment = IcsExtractor.ExtractFile("meeting.ics");

Console.WriteLine(appointment.Summary);
Console.WriteLine(appointment.CreatedDate);                       // CREATED, else DTSTAMP
Console.WriteLine($"{appointment.Organizer?.CommonName} <{appointment.Organizer?.Address}>");
foreach (var person in appointment.Attendees)         Console.WriteLine($"  to: {person}");
foreach (var person in appointment.OptionalAttendees) Console.WriteLine($"  cc: {person}");
Console.WriteLine(appointment.Description);

string html = appointment.ToHtml();                                // the meeting view
string text = appointment.ToPlainText();
string markdown = appointment.ToMarkdown();

foreach (var attachment in appointment.Attachments.Where(a => !string.IsNullOrWhiteSpace(a.Name)))
{
    await using var file = File.Create(Path.Combine(folder, attachment.Name!));
    attachment.WriteTo(file);                                      // inline attachments only (HasData)
}
```

Work with the calendar itself:

```csharp
using XRay.Ics;

var calendar = Calendar.LoadFile("team.ics", cancellationToken: token);
foreach (var item in calendar.Events)
{
    Console.WriteLine($"{item.Summary}: {item.Start} - {item.End} ({item.Method})");
}

// Instances in a window, with RECURRENCE-ID overrides applied.
foreach (var occurrence in calendar.Expand(DateTimeOffset.Now, DateTimeOffset.Now.AddMonths(3), limit: 200))
{
    Console.WriteLine($"{occurrence.Start}  {occurrence.Event.Summary}");
}
```

Create a calendar and write it:

```csharp
var calendar = new Calendar { MethodText = "REQUEST" };
var meeting = new CalendarEvent
{
    Uid = Guid.NewGuid() + "@example.com",
    Summary = "Planning",
    Start = new IcsDateTime(new DateTime(2026, 5, 4, 9, 0, 0), IcsDateTimeKind.Utc),
    End = new IcsDateTime(new DateTime(2026, 5, 4, 10, 0, 0), IcsDateTimeKind.Utc),
    Organizer = new CalendarAddress("boss@example.com", "The Boss"),
};
meeting.AddAttendee(new CalendarAddress("ana@example.com", "Ana"));
meeting.SetRecurrence(RecurrenceRule.Parse("FREQ=WEEKLY;COUNT=4"));
calendar.Events.Add(meeting);

await using var output = File.Create("planning.ics");
calendar.Save(output);
```

### Times

`IcsDateTime` has a `Kind`: `Date` (all-day), `Floating`, `Utc` or `Zoned`. `Value` is the wall-clock
reading as written; `Offset` is the UTC offset when it can be known (UTC, and zones the calendar's
`VTIMEZONE` or the system resolves). `ToDateTimeOffset(floatingZone)` reads floating times and dates
in the zone you choose, UTC by default; `ToLocalTime(zone)` converts. A zone nothing can resolve keeps
its wall clock with `Offset == null` rather than guessing.

### The rendering

`ToHtml` writes one self-contained page: subject, a table (When with the recurrence summarised, Where,
Organizer, Required, Optional, Resources, Categories), the description with its line breaks kept and
its markup escaped, and the attachment list. It has no script, no external references, no timestamps
and does not depend on the machine, so it can be compared in tests. `IcsRenderOptions.DisplayTimeZone`
converts times to a zone and labels them with its offset; without it each time keeps its own zone. Plain
text and Markdown cover every component, the page only the first, unless `AllComponents` says otherwise.

### Limits

`IcsParseOptions` bounds the input size (256 MiB), the length of a logical line (64 Mi characters),
component nesting (16), the number of components and properties per component, and the encoding used for
bytes that are not UTF-8. Recurrence expansion ends at COUNT, UNTIL, year 9999 or after a million periods,
so a rule that never matches cannot hang a caller.

## Compatibility notes

- A recurrence set always includes DTSTART as its first instance, as RFC 5545 defines it; the rule itself
  only produces instances that match it.
- `\"` is not an escape in iCalendar TEXT and is kept; `\:` is unescaped, as common producers write it.
- `ISO-8859-1` and `US-ASCII` charsets are read as Windows-1252, which is what the files mean.
- A calendar loaded from vCalendar 1.0 is saved with quoted-printable markers removed (the text is plain
  after loading) but keeps `VERSION:1.0`; convert deliberately if you need RFC 5545 output.
- Several `VCALENDAR` objects in one input are merged into the first (`Calendar.LoadAll` keeps them apart).

## License

MIT. See `LICENSE`.
