namespace XRay.Ics;

/// <summary>One instance of a (possibly recurring) event.</summary>
/// <param name="Event">The component the times come from: the master event, or the override that replaced this instance.</param>
/// <param name="Start">The instance start.</param>
/// <param name="End">The instance end (exclusive for all-day values).</param>
/// <param name="IsOverride">True when a RECURRENCE-ID component replaced the instance.</param>
public sealed record IcsOccurrence(CalendarEvent Event, IcsDateTime Start, IcsDateTime End, bool IsOverride);
