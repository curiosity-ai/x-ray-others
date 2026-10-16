using System.Globalization;
using XRay.Ics.Internal;

namespace XRay.Ics;

/// <summary>A <c>VEVENT</c>.</summary>
public sealed class CalendarEvent : CalendarComponent
{
    public CalendarEvent()
        : base(new IcsComponent("VEVENT"))
    {
    }

    internal CalendarEvent(IcsComponent raw)
        : base(raw)
    {
    }

    public string? Location
    {
        get => Raw.Get("LOCATION")?.TextValue;
        set => Raw.Set("LOCATION", value is null ? null : IcsText.Escape(value));
    }

    public GeoLocation? Geo => GeoLocation.TryParse(Raw.GetValue("GEO"), out var geo) ? geo : null;

    /// <summary>PRIORITY, 0 (undefined) to 9, or null when absent.</summary>
    public int? Priority => int.TryParse(Raw.GetValue("PRIORITY")?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    public CalendarTransparency Transparency => string.Equals(Raw.GetValue("TRANSP")?.Trim(), "TRANSPARENT", StringComparison.OrdinalIgnoreCase) ? CalendarTransparency.Transparent : CalendarTransparency.Opaque;

    /// <summary>
    /// DTEND, else DTSTART plus DURATION, else the start (one day later for an all-day start,
    /// as RFC 5545 defines it).
    /// </summary>
    public IcsDateTime? End
    {
        get
        {
            if (Raw.Get("DTEND") is not null)
            {
                return GetDate("DTEND");
            }

            if (Start is not { } start)
            {
                return null;
            }

            if (IcsDuration.TryParse(Raw.GetValue("DURATION"), out var duration))
            {
                return start.Add(duration.ToTimeSpan());
            }

            return start.IsDate ? start.Add(TimeSpan.FromDays(1)) : start;
        }

        set
        {
            Raw.Properties.RemoveAll(p => p.Name == "DURATION");
            DateParsing.Write(Raw, "DTEND", value);
        }
    }

    /// <summary>The length of the event, or null without a start.</summary>
    public TimeSpan? Duration
    {
        get
        {
            if (IcsDuration.TryParse(Raw.GetValue("DURATION"), out var explicitDuration) && Raw.Get("DTEND") is null)
            {
                return explicitDuration.ToTimeSpan();
            }

            return Start is { } start && End is { } end ? Difference(start, end) : null;
        }
    }

    /// <summary>True for a date-valued start, or Outlook's all-day flag.</summary>
    public bool IsAllDay => Start?.IsDate == true || string.Equals(Raw.GetValue("X-MICROSOFT-CDO-ALLDAYEVENT")?.Trim(), "TRUE", StringComparison.OrdinalIgnoreCase);

    /// <summary>The VALARMs, plus vCalendar 1.0 DALARM/AALARM/PALARM/MALARM properties.</summary>
    public IReadOnlyList<CalendarAlarm> Alarms
    {
        get
        {
            var alarms = new List<CalendarAlarm>();
            foreach (var child in Raw.GetChildren("VALARM"))
            {
                alarms.Add(new CalendarAlarm(child));
            }

            foreach (var name in new[] { "DALARM", "AALARM", "PALARM", "MALARM" })
            {
                foreach (var property in Raw.GetAll(name))
                {
                    if (CalendarAlarm.FromLegacy(property) is { } alarm)
                    {
                        alarms.Add(alarm);
                    }
                }
            }

            return alarms;
        }
    }

    /// <summary>
    /// The instances that overlap [<paramref name="from"/>, <paramref name="to"/>), ascending, at most
    /// <paramref name="limit"/>. Covers FREQ, INTERVAL, COUNT, UNTIL, BYDAY, BYMONTHDAY, BYMONTH,
    /// BYSETPOS, WKST and the other BYxxx parts, plus RDATE and EXDATE. DTSTART is always the first
    /// instance. RECURRENCE-ID overrides in the same calendar are applied by <see cref="Calendar.Expand"/>, not here.
    /// Floating and date values are read in <paramref name="floatingZone"/> (default UTC) for the window test.
    /// </summary>
    public IEnumerable<IcsOccurrence> Expand(DateTimeOffset from, DateTimeOffset to, int limit = 1000, TimeZoneInfo? floatingZone = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (limit <= 0 || Start is not { } start)
        {
            yield break;
        }

        var end = End ?? start;
        var sameClock = end.Kind == start.Kind && string.Equals(end.TzId, start.TzId, StringComparison.Ordinal);
        var wallLength = TimeSpan.FromTicks(end.Value.Ticks - start.Value.Ticks);
        var instantLength = end.ToDateTimeOffset(floatingZone) - start.ToDateTimeOffset(floatingZone);
        var explicitDuration = Raw.Get("DTEND") is null && IcsDuration.TryParse(Raw.GetValue("DURATION"), out var parsed) ? parsed.ToTimeSpan() : (TimeSpan?)null;
        var returned = 0;
        foreach (var instance in EnumerateStarts(cancellationToken))
        {
            var instantStart = instance.ToDateTimeOffset(floatingZone);
            if (instantStart >= to && instantStart > DateTimeMath.AddDays(to, 1))
            {
                yield break;
            }

            IcsDateTime instanceEnd;
            if (explicitDuration is { } length)
            {
                instanceEnd = Make(instance, DateTimeMath.Add(instance.Value, length));
            }
            else if (sameClock)
            {
                instanceEnd = Make(instance, DateTimeMath.Add(instance.Value, wallLength));
            }
            else
            {
                var asInstant = instantStart + instantLength;
                instanceEnd = new IcsDateTime(asInstant.UtcDateTime, IcsDateTimeKind.Utc);
            }

            var instantEnd = instanceEnd.ToDateTimeOffset(floatingZone);
            var overlaps = instantEnd > from && instantStart < to || instantEnd == instantStart && instantStart >= from && instantStart < to;
            if (!overlaps)
            {
                continue;
            }

            yield return new IcsOccurrence(this, instance, instanceEnd, false);
            if (++returned >= limit)
            {
                yield break;
            }
        }
    }

    private static TimeSpan Difference(IcsDateTime start, IcsDateTime end)
    {
        if (start.Offset is { } a && end.Offset is { } b)
        {
            return TimeSpan.FromTicks(DateTimeMath.UtcTicks(end.Value, b) - DateTimeMath.UtcTicks(start.Value, a));
        }

        return TimeSpan.FromTicks(end.Value.Ticks - start.Value.Ticks);
    }
}
