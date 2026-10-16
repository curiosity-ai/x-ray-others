using XRay.Ics.Internal;

namespace XRay.Ics;

/// <summary>One FREEBUSY period.</summary>
/// <param name="Start">The start (UTC).</param>
/// <param name="End">The end (UTC).</param>
/// <param name="Type">FBTYPE: FREE, BUSY, BUSY-UNAVAILABLE or BUSY-TENTATIVE (upper-cased); BUSY by default.</param>
public sealed record FreeBusyPeriod(DateTimeOffset Start, DateTimeOffset End, string Type);

/// <summary>A <c>VFREEBUSY</c> component (iTIP free/busy publication and requests).</summary>
public sealed class CalendarFreeBusy : CalendarComponent
{
    public CalendarFreeBusy()
        : base(new IcsComponent("VFREEBUSY"))
    {
    }

    internal CalendarFreeBusy(IcsComponent raw)
        : base(raw)
    {
    }

    public IcsDateTime? End => GetDate("DTEND");

    public IReadOnlyList<FreeBusyPeriod> Periods
    {
        get
        {
            var periods = new List<FreeBusyPeriod>();
            foreach (var property in Raw.GetAll("FREEBUSY"))
            {
                var type = property.GetParameter("FBTYPE")?.ToUpperInvariant() ?? "BUSY";
                foreach (var item in property.Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var slash = item.IndexOf('/');
                    if (slash <= 0 || !IcsDateTime.TryParseValue(item[..slash], out var start, out var kind) || kind == IcsDateTimeKind.Date)
                    {
                        continue;
                    }

                    var startOffset = new DateTimeOffset(DateTime.SpecifyKind(start, DateTimeKind.Utc));
                    var tail = item[(slash + 1)..];
                    if (IcsDuration.TryParse(tail, out var duration))
                    {
                        periods.Add(new FreeBusyPeriod(startOffset, startOffset + duration.ToTimeSpan(), type));
                    }
                    else if (IcsDateTime.TryParseValue(tail, out var end, out _))
                    {
                        periods.Add(new FreeBusyPeriod(startOffset, new DateTimeOffset(DateTime.SpecifyKind(end, DateTimeKind.Utc)), type));
                    }
                }
            }

            return periods;
        }
    }
}
