using System.Globalization;
using XRay.Ics.Internal;

namespace XRay.Ics;

/// <summary>
/// A <c>VTIMEZONE</c> component: the zone's own STANDARD and DAYLIGHT rules, used to give
/// <c>TZID</c> times an offset without any operating-system time zone data.
/// </summary>
public sealed class CalendarTimeZone
{
    private const int LastYear = 2100;
    private const int MaxOnsetsPerObservance = 5000;
    private readonly Lazy<List<Onset>> onsets;

    public CalendarTimeZone(IcsComponent raw)
    {
        Raw = raw ?? throw new ArgumentNullException(nameof(raw));
        onsets = new Lazy<List<Onset>>(BuildOnsets, LazyThreadSafetyMode.PublicationOnly);
    }

    public IcsComponent Raw { get; }

    public string? TzId => Raw.Get("TZID")?.Value.Trim();

    public string? Location => Raw.Get("X-LIC-LOCATION")?.TextValue;

    /// <summary>The UTC offset in effect at a wall-clock reading in this zone, or null when the component has no usable rule.</summary>
    public TimeSpan? GetUtcOffset(DateTime local)
    {
        var list = onsets.Value;
        if (list.Count == 0)
        {
            return null;
        }

        var low = 0;
        var high = list.Count - 1;
        var found = -1;
        while (low <= high)
        {
            var mid = (low + high) >>> 1;
            if (list[mid].Local <= local)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return found >= 0 ? list[found].OffsetTo : list[0].OffsetFrom;
    }

    internal static bool TryParseOffset(string? text, out TimeSpan offset)
    {
        offset = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        var sign = 1;
        var index = 0;
        if (value[0] is '+' or '-')
        {
            sign = value[0] == '-' ? -1 : 1;
            index = 1;
        }

        var digits = value[index..].Replace(":", string.Empty, StringComparison.Ordinal);
        if (digits.Length is not (2 or 4 or 6) || !digits.All(char.IsAsciiDigit))
        {
            return false;
        }

        var hours = int.Parse(digits.AsSpan(0, 2), NumberStyles.None, CultureInfo.InvariantCulture);
        var minutes = digits.Length >= 4 ? int.Parse(digits.AsSpan(2, 2), NumberStyles.None, CultureInfo.InvariantCulture) : 0;
        var seconds = digits.Length == 6 ? int.Parse(digits.AsSpan(4, 2), NumberStyles.None, CultureInfo.InvariantCulture) : 0;
        if (hours > 23 || minutes > 59 || seconds > 59)
        {
            return false;
        }

        offset = new TimeSpan(hours, minutes, seconds) * sign;
        return true;
    }

    private List<Onset> BuildOnsets()
    {
        var result = new List<Onset>();
        foreach (var observance in Raw.Children)
        {
            if (!string.Equals(observance.Name, "STANDARD", StringComparison.OrdinalIgnoreCase) && !string.Equals(observance.Name, "DAYLIGHT", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!IcsDateTime.TryParseValue(observance.GetValue("DTSTART"), out var dtstart, out _)
                || !TryParseOffset(observance.GetValue("TZOFFSETFROM"), out var from)
                || !TryParseOffset(observance.GetValue("TZOFFSETTO"), out var to))
            {
                continue;
            }

            result.Add(new Onset(dtstart, from, to));
            foreach (var rdate in observance.GetAll("RDATE"))
            {
                foreach (var item in rdate.Value.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var startText = item.Split('/')[0];
                    if (IcsDateTime.TryParseValue(startText, out var extra, out _))
                    {
                        result.Add(new Onset(extra, from, to));
                    }
                }
            }

            var ruleText = observance.GetValue("RRULE");
            if (ruleText is not null && RecurrenceRule.TryParse(ruleText, out var rule))
            {
                var last = new DateTime(LastYear, 12, 31);
                DateTime? until = last;
                if (rule.Until is { } bound)
                {
                    var wall = bound.IsUtc ? DateTimeMath.Add(bound.Value, from) : bound.Value;
                    until = wall < last ? wall : last;
                }

                var count = 0;
                foreach (var occurrence in rule.Enumerate(dtstart, until))
                {
                    if (occurrence != dtstart)
                    {
                        result.Add(new Onset(occurrence, from, to));
                    }

                    if (++count >= MaxOnsetsPerObservance)
                    {
                        break;
                    }
                }
            }
        }

        result.Sort((a, b) => a.Local.CompareTo(b.Local));
        return result;
    }

    private readonly record struct Onset(DateTime Local, TimeSpan OffsetFrom, TimeSpan OffsetTo);
}
