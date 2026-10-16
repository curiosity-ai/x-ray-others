using System.Globalization;
using XRay.Ics.Internal;

namespace XRay.Ics;

/// <summary>A <c>VTODO</c>.</summary>
public sealed class CalendarTodo : CalendarComponent
{
    public CalendarTodo()
        : base(new IcsComponent("VTODO"))
    {
    }

    internal CalendarTodo(IcsComponent raw)
        : base(raw)
    {
    }

    public string? Location
    {
        get => Raw.Get("LOCATION")?.TextValue;
        set => Raw.Set("LOCATION", value is null ? null : IcsText.Escape(value));
    }

    public IcsDateTime? Due
    {
        get => GetDate("DUE");
        set => DateParsing.Write(Raw, "DUE", value);
    }

    public IcsDateTime? Completed
    {
        get => GetDate("COMPLETED");
        set => DateParsing.Write(Raw, "COMPLETED", value);
    }

    /// <summary>PERCENT-COMPLETE, 0 to 100.</summary>
    public int? PercentComplete => int.TryParse(Raw.GetValue("PERCENT-COMPLETE")?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    public int? Priority => int.TryParse(Raw.GetValue("PRIORITY")?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    public TimeSpan? Duration => IcsDuration.TryParse(Raw.GetValue("DURATION"), out var duration) ? duration.ToTimeSpan() : null;

    public IReadOnlyList<CalendarAlarm> Alarms => [.. Raw.GetChildren("VALARM").Select(child => new CalendarAlarm(child))];
}
