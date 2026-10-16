using XRay.Ics.Internal;

namespace XRay.Ics;

/// <summary>A <c>VALARM</c>, or a vCalendar 1.0 DALARM/AALARM/PALARM/MALARM property.</summary>
public sealed class CalendarAlarm
{
    public CalendarAlarm(IcsComponent raw)
    {
        Raw = raw ?? throw new ArgumentNullException(nameof(raw));
    }

    public IcsComponent Raw { get; }

    /// <summary>AUDIO, DISPLAY, EMAIL or PROCEDURE (upper-cased); null when absent.</summary>
    public string? Action => Raw.GetValue("ACTION")?.Trim().ToUpperInvariant();

    public string? Description => Raw.Get("DESCRIPTION")?.TextValue;

    public string? Summary => Raw.Get("SUMMARY")?.TextValue;

    public int? Repeat => int.TryParse(Raw.GetValue("REPEAT"), out var repeat) ? repeat : null;

    public IcsDuration? RepeatInterval => IcsDuration.TryParse(Raw.GetValue("DURATION"), out var duration) ? duration : null;

    /// <summary>A relative trigger (before the start when negative), or null for an absolute one.</summary>
    public IcsDuration? Trigger
    {
        get
        {
            var property = Raw.Get("TRIGGER");
            if (property is null || string.Equals(property.GetParameter("VALUE"), "DATE-TIME", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return IcsDuration.TryParse(property.Value, out var duration) ? duration : null;
        }
    }

    /// <summary>True when the trigger is relative to the end (RELATED=END) rather than the start.</summary>
    public bool TriggerRelativeToEnd => string.Equals(Raw.Get("TRIGGER")?.GetParameter("RELATED"), "END", StringComparison.OrdinalIgnoreCase);

    /// <summary>An absolute trigger time, or null.</summary>
    public IcsDateTime? TriggerTime
    {
        get
        {
            var property = Raw.Get("TRIGGER");
            if (property is null || !IcsDateTime.TryParseValue(property.Value, out var value, out var kind) || kind == IcsDateTimeKind.Date)
            {
                return null;
            }

            return new IcsDateTime(value, kind);
        }
    }

    internal static CalendarAlarm? FromLegacy(IcsProperty property)
    {
        // vCalendar 1.0: DALARM:runTime;snoozeTime;repeatCount;displayString
        var parts = property.Value.Split(';');
        if (parts.Length == 0 || !IcsDateTime.TryParseValue(parts[0], out var time, out var kind))
        {
            return null;
        }

        var raw = new IcsComponent("VALARM");
        raw.Set("ACTION", property.Name switch
        {
            "AALARM" => "AUDIO",
            "PALARM" => "PROCEDURE",
            "MALARM" => "EMAIL",
            _ => "DISPLAY",
        });
        var trigger = raw.Set("TRIGGER", new IcsDateTime(time, kind).ToIcsString());
        trigger?.Parameters.Add(new IcsParameter("VALUE", "DATE-TIME"));
        if (parts.Length > 1 && IcsDuration.TryParse(parts[1], out _))
        {
            raw.Set("DURATION", parts[1]);
        }

        if (parts.Length > 2 && int.TryParse(parts[2], out var repeat) && repeat > 0)
        {
            raw.Set("REPEAT", repeat.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        if (parts.Length > 3 && !string.IsNullOrWhiteSpace(parts[3]))
        {
            raw.Set("DESCRIPTION", parts[3]);
        }

        return new CalendarAlarm(raw);
    }
}
