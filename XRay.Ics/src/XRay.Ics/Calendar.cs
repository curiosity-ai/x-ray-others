using System.Globalization;
using System.Text;
using XRay.Ics.Internal;

namespace XRay.Ics;

/// <summary>
/// A <c>VCALENDAR</c> object: the iCalendar (RFC 5545 / iTIP RFC 5546) or vCalendar 1.0 content of an
/// .ics file, with typed events, to-dos, journal entries, free/busy components and time zones. The
/// raw tree is kept in <see cref="Raw"/>, so a load followed by <see cref="Save(Stream, CancellationToken)"/>
/// preserves X- properties and unknown components.
/// </summary>
public sealed class Calendar
{
    private readonly List<CalendarTimeZone> timeZones = [];
    private readonly ZoneResolver resolver;

    /// <summary>Creates an empty RFC 5545 calendar (VERSION 2.0).</summary>
    public Calendar()
        : this(NewRaw(), [])
    {
    }

    internal Calendar(IcsComponent raw, List<string> warnings)
    {
        Raw = raw;
        Warnings = warnings;
        Events = new ComponentList<CalendarEvent>(this);
        Todos = new ComponentList<CalendarTodo>(this);
        Journals = new ComponentList<CalendarJournal>(this);
        FreeBusies = new ComponentList<CalendarFreeBusy>(this);
        resolver = new ZoneResolver(FindTimeZone);
        foreach (var child in raw.Children)
        {
            switch (child.Name)
            {
                case "VEVENT":
                    Events.AddLoaded(new CalendarEvent(child));
                    break;
                case "VTODO":
                    Todos.AddLoaded(new CalendarTodo(child));
                    break;
                case "VJOURNAL":
                    Journals.AddLoaded(new CalendarJournal(child));
                    break;
                case "VFREEBUSY":
                    FreeBusies.AddLoaded(new CalendarFreeBusy(child));
                    break;
                case "VTIMEZONE":
                    timeZones.Add(new CalendarTimeZone(child));
                    break;
            }
        }
    }

    private static IcsComponent NewRaw()
    {
        var raw = new IcsComponent("VCALENDAR");
        raw.Set("PRODID", "-//Curiosity GmbH//X-Ray.Ics//EN");
        raw.Set("VERSION", "2.0");
        return raw;
    }

    /// <summary>The underlying component tree.</summary>
    public IcsComponent Raw { get; }

    public ComponentList<CalendarEvent> Events { get; }

    public ComponentList<CalendarTodo> Todos { get; }

    public ComponentList<CalendarJournal> Journals { get; }

    public ComponentList<CalendarFreeBusy> FreeBusies { get; }

    public IReadOnlyList<CalendarTimeZone> TimeZones => timeZones;

    /// <summary>Notes about damage the reader tolerated (skipped lines, unclosed components, ...).</summary>
    public IList<string> Warnings { get; }

    internal ZoneResolver Resolver => resolver;

    public string? Version
    {
        get => Raw.GetValue("VERSION")?.Trim();
        set => Raw.Set("VERSION", value);
    }

    public string? ProductId
    {
        get => Raw.Get("PRODID")?.TextValue;
        set => Raw.Set("PRODID", value is null ? null : IcsText.Escape(value));
    }

    public string? CalendarScale => Raw.GetValue("CALSCALE")?.Trim();

    /// <summary>True for vCalendar 1.0 (VERSION:1.0).</summary>
    public bool IsVCalendar1 => Version is { } version && version.StartsWith("1.", StringComparison.Ordinal);

    /// <summary>The METHOD as written (upper-cased), or null.</summary>
    public string? MethodText
    {
        get => Raw.GetValue("METHOD")?.Trim().ToUpperInvariant();
        set => Raw.Set("METHOD", value?.ToUpperInvariant());
    }

    public CalendarMethod Method => MethodText switch
    {
        null or "" => CalendarMethod.None,
        "PUBLISH" => CalendarMethod.Publish,
        "REQUEST" => CalendarMethod.Request,
        "REPLY" => CalendarMethod.Reply,
        "ADD" => CalendarMethod.Add,
        "CANCEL" => CalendarMethod.Cancel,
        "REFRESH" => CalendarMethod.Refresh,
        "COUNTER" => CalendarMethod.Counter,
        "DECLINECOUNTER" => CalendarMethod.DeclineCounter,
        _ => CalendarMethod.Other,
    };

    /// <summary>The calendar's name (NAME or X-WR-CALNAME).</summary>
    public string? Name => (Raw.Get("NAME") ?? Raw.Get("X-WR-CALNAME"))?.TextValue;

    public string? Description => (Raw.Get("DESCRIPTION") ?? Raw.Get("X-WR-CALDESC"))?.TextValue;

    /// <summary>X-WR-TIMEZONE, the default zone some producers (Google) declare for the whole calendar.</summary>
    public string? DefaultTimeZoneId => Raw.Get("X-WR-TIMEZONE")?.Value.Trim();

    /// <summary>The vCalendar 1.0 <c>TZ</c> offset that its zone-less times are read in.</summary>
    public TimeSpan? LegacyUtcOffset => IsVCalendar1 && CalendarTimeZone.TryParseOffset(Raw.GetValue("TZ"), out var offset) ? offset : null;

    /// <summary>The first event, then the first to-do, then the first journal entry: what an appointment view shows.</summary>
    public CalendarComponent? Primary => Events.Count > 0 ? Events[0] : Todos.Count > 0 ? Todos[0] : Journals.Count > 0 ? Journals[0] : null;

    public void AddTimeZone(CalendarTimeZone timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        timeZones.Add(timeZone);
        Raw.Children.Add(timeZone.Raw);
    }

    private CalendarTimeZone? FindTimeZone(string tzId)
    {
        var wanted = tzId.Trim().TrimStart('/');
        foreach (var zone in timeZones)
        {
            if (zone.TzId is { } id && string.Equals(id.TrimStart('/'), wanted, StringComparison.OrdinalIgnoreCase))
            {
                return zone;
            }
        }

        return null;
    }

    /// <summary>Reads a calendar from a file.</summary>
    public static Calendar LoadFile(string path, IcsParseOptions? options = null, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        return Load(stream, options, cancellationToken);
    }

    /// <summary>
    /// Reads a calendar. Several VCALENDAR objects in one input are merged into the first.
    /// Damaged input is read as far as it goes; only input with no calendar at all, or over a
    /// limit, raises <see cref="IcsFormatException"/>.
    /// </summary>
    public static Calendar Load(Stream stream, IcsParseOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= IcsParseOptions.Default;
        return Load(ReadAll(stream, options, cancellationToken), options, cancellationToken);
    }

    public static Calendar Load(byte[] data, IcsParseOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(data);
        options ??= IcsParseOptions.Default;
        cancellationToken.ThrowIfCancellationRequested();
        return Merge(IcsReader.Read(data, options, cancellationToken));
    }

    /// <summary>Reads a calendar from text.</summary>
    public static Calendar Parse(string text, IcsParseOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        options ??= IcsParseOptions.Default;
        cancellationToken.ThrowIfCancellationRequested();
        if (text.Length > options.MaxInputBytes)
        {
            throw new IcsFormatException($"The input is {text.Length} characters, over the limit of {options.MaxInputBytes}.");
        }

        return Merge(IcsReader.ReadText(text, false, options, cancellationToken));
    }

    /// <summary>Reads every VCALENDAR of the input as its own calendar.</summary>
    public static IReadOnlyList<Calendar> LoadAll(Stream stream, IcsParseOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= IcsParseOptions.Default;
        var result = IcsReader.Read(ReadAll(stream, options, cancellationToken), options, cancellationToken);
        return [.. result.Calendars.Select(raw => new Calendar(raw, [.. result.Warnings]))];
    }

    private static Calendar Merge(IcsReader.ParseResult result)
    {
        var first = result.Calendars[0];
        for (var i = 1; i < result.Calendars.Count; i++)
        {
            first.Children.AddRange(result.Calendars[i].Children);
        }

        if (result.Calendars.Count > 1)
        {
            result.Warnings.Add($"{result.Calendars.Count} VCALENDAR objects were merged into one.");
        }

        return new Calendar(first, result.Warnings);
    }

    internal static byte[] ReadAll(Stream stream, IcsParseOptions options, CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            if (memory.Length + read > options.MaxInputBytes)
            {
                throw new IcsFormatException($"The input is larger than the limit of {options.MaxInputBytes} bytes.");
            }

            memory.Write(buffer, 0, read);
        }

        return memory.ToArray();
    }

    /// <summary>Writes the calendar as RFC 5545 text: CRLF line ends, UTF-8 without BOM, lines folded at 75 octets.</summary>
    public void Save(Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        IcsWriter.Write(WithRequiredProperties(), destination, cancellationToken);
    }

    public void Save(string path, CancellationToken cancellationToken = default)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920);
        Save(stream, cancellationToken);
    }

    public byte[] ToBytes(CancellationToken cancellationToken = default)
    {
        using var memory = new MemoryStream();
        Save(memory, cancellationToken);
        return memory.ToArray();
    }

    public string ToIcsString(CancellationToken cancellationToken = default) => new UTF8Encoding(false).GetString(ToBytes(cancellationToken));

    private IcsComponent WithRequiredProperties()
    {
        if (Raw.Get("VERSION") is not null && Raw.Get("PRODID") is not null)
        {
            return Raw;
        }

        var copy = new IcsComponent("VCALENDAR");
        copy.Properties.AddRange(Raw.Properties);
        copy.Children.AddRange(Raw.Children);
        if (copy.Get("PRODID") is null)
        {
            copy.Properties.Insert(0, new IcsProperty("PRODID", "-//Curiosity GmbH//X-Ray.Ics//EN"));
        }

        if (copy.Get("VERSION") is null)
        {
            copy.Properties.Insert(1, new IcsProperty("VERSION", "2.0"));
        }

        return copy;
    }

    /// <summary>
    /// The instances of every event overlapping [from, to), ordered by start, at most
    /// <paramref name="limit"/>. RECURRENCE-ID components replace the instance they name (and a
    /// cancelled override removes it); an override whose master is not in the calendar is one
    /// standalone instance.
    /// </summary>
    public IReadOnlyList<IcsOccurrence> Expand(DateTimeOffset from, DateTimeOffset to, int limit = 1000, TimeZoneInfo? floatingZone = null, CancellationToken cancellationToken = default)
    {
        var result = new List<IcsOccurrence>();
        var byUid = Events.Where(e => e.Uid is not null).GroupBy(e => e.Uid!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        foreach (var anEvent in Events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (anEvent.RecurrenceId is not null)
            {
                var hasMaster = anEvent.Uid is not null && byUid[anEvent.Uid].Any(e => e.RecurrenceId is null);
                if (hasMaster)
                {
                    continue;
                }

                result.AddRange(anEvent.Expand(from, to, limit, floatingZone, cancellationToken).Select(o => o with { IsOverride = true }));
                continue;
            }

            var overrides = anEvent.Uid is null ? [] : byUid[anEvent.Uid].Where(e => e.RecurrenceId is not null).ToList();
            // Overrides can remove at most that many instances, so this many are enough to fill the first `limit` of the result.
            var perEventLimit = limit >= int.MaxValue - overrides.Count ? int.MaxValue : limit + overrides.Count;
            foreach (var occurrence in anEvent.Expand(DateTimeMath.AddDays(from, -1), DateTimeMath.AddDays(to, 1), perEventLimit, floatingZone, cancellationToken))
            {
                var replaced = overrides.FirstOrDefault(o => SameInstance(o.RecurrenceId!.Value, occurrence.Start, floatingZone));
                if (replaced is not null)
                {
                    overrides.Remove(replaced);
                    if (replaced.Status == CalendarStatus.Cancelled)
                    {
                        continue;
                    }

                    var start = replaced.Start ?? occurrence.Start;
                    var end = replaced.End ?? start;
                    if (Overlaps(start, end, from, to, floatingZone))
                    {
                        result.Add(new IcsOccurrence(replaced, start, end, true));
                    }

                    continue;
                }

                if (Overlaps(occurrence.Start, occurrence.End, from, to, floatingZone))
                {
                    result.Add(occurrence);
                }
            }

            // An override that moved an instance into the window from outside the master's own window.
            foreach (var leftover in overrides)
            {
                if (leftover.Status != CalendarStatus.Cancelled && leftover.Start is { } start && Overlaps(start, leftover.End ?? start, from, to, floatingZone))
                {
                    result.Add(new IcsOccurrence(leftover, start, leftover.End ?? start, true));
                }
            }
        }

        result.Sort((a, b) => a.Start.ToDateTimeOffset(floatingZone).CompareTo(b.Start.ToDateTimeOffset(floatingZone)));
        if (result.Count > limit)
        {
            result.RemoveRange(limit, result.Count - limit);
        }

        return result;
    }

    private static bool SameInstance(IcsDateTime recurrenceId, IcsDateTime start, TimeZoneInfo? floatingZone)
    {
        if (recurrenceId.IsDate || start.IsDate)
        {
            return recurrenceId.Value.Date == start.Value.Date;
        }

        if (recurrenceId.Offset is not null && start.Offset is not null)
        {
            return recurrenceId.ToDateTimeOffset(floatingZone).UtcDateTime == start.ToDateTimeOffset(floatingZone).UtcDateTime;
        }

        return recurrenceId.Value == start.Value;
    }

    private static bool Overlaps(IcsDateTime start, IcsDateTime end, DateTimeOffset from, DateTimeOffset to, TimeZoneInfo? floatingZone)
    {
        var s = start.ToDateTimeOffset(floatingZone);
        var e = end.ToDateTimeOffset(floatingZone);
        return e > from && s < to || e == s && s >= from && s < to;
    }
}
