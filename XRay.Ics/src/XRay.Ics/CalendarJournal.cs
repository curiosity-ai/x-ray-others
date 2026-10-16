namespace XRay.Ics;

/// <summary>A <c>VJOURNAL</c> entry.</summary>
public sealed class CalendarJournal : CalendarComponent
{
    public CalendarJournal()
        : base(new IcsComponent("VJOURNAL"))
    {
    }

    internal CalendarJournal(IcsComponent raw)
        : base(raw)
    {
    }
}
