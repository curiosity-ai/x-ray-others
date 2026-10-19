using System.Globalization;
using System.Text;
using XRay.Ics.Internal;

namespace XRay.Ics;

public enum RecurrenceFrequency
{
    Secondly,
    Minutely,
    Hourly,
    Daily,
    Weekly,
    Monthly,
    Yearly,
}

/// <summary>A BYDAY entry: a weekday with an optional ordinal (<c>-1FR</c> is the last Friday).</summary>
public readonly record struct WeekdaySpec(int? Ordinal, DayOfWeek Day)
{
    public override string ToString() => (Ordinal?.ToString(CultureInfo.InvariantCulture) ?? string.Empty) + RecurrenceRule.DayCodes[(int)Day];
}

/// <summary>
/// An RFC 5545 recurrence rule (<c>RRULE</c> value): frequency, interval, COUNT/UNTIL and the BYxxx
/// parts, with an expansion generator that follows RFC 5545 section 3.3.10.
/// </summary>
public sealed class RecurrenceRule
{
    internal static readonly string[] DayCodes = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];

    private const int MaxPeriods = 1_000_000;

    public RecurrenceFrequency Frequency { get; set; } = RecurrenceFrequency.Daily;

    public int Interval { get; set; } = 1;

    public int? Count { get; set; }

    /// <summary>The UNTIL bound: a date, a floating time or a UTC time.</summary>
    public IcsDateTime? Until { get; set; }

    public List<int> BySecond { get; } = [];

    public List<int> ByMinute { get; } = [];

    public List<int> ByHour { get; } = [];

    public List<WeekdaySpec> ByDay { get; } = [];

    public List<int> ByMonthDay { get; } = [];

    public List<int> ByYearDay { get; } = [];

    public List<int> ByWeekNo { get; } = [];

    public List<int> ByMonth { get; } = [];

    public List<int> BySetPos { get; } = [];

    public DayOfWeek WeekStart { get; set; } = DayOfWeek.Monday;

    public static RecurrenceRule Parse(string value)
    {
        return TryParse(value, out var rule) ? rule : throw new FormatException($"Not a recurrence rule: '{value}'.");
    }

    /// <summary>Parses an RRULE value; unknown parts are ignored, an invalid or missing FREQ fails.</summary>
    public static bool TryParse(string? value, out RecurrenceRule rule)
    {
        rule = new RecurrenceRule();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        if (text.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase))
        {
            text = text[6..];
        }

        var haveFreq = false;
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = part.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            var key = part[..equals].Trim().ToUpperInvariant();
            var data = part[(equals + 1)..].Trim();
            switch (key)
            {
                case "FREQ":
                    haveFreq = TryParseFrequency(data, out var frequency);
                    if (haveFreq)
                    {
                        rule.Frequency = frequency;
                    }

                    break;
                case "INTERVAL":
                    if (int.TryParse(data, NumberStyles.Integer, CultureInfo.InvariantCulture, out var interval) && interval > 0)
                    {
                        rule.Interval = interval;
                    }

                    break;
                case "COUNT":
                    if (int.TryParse(data, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count) && count > 0)
                    {
                        rule.Count = count;
                    }

                    break;
                case "UNTIL":
                    if (IcsDateTime.TryParseValue(data, out var until, out var kind))
                    {
                        rule.Until = new IcsDateTime(until, kind);
                    }

                    break;
                case "BYSECOND":
                    ParseInts(data, rule.BySecond, 0, 60);
                    break;
                case "BYMINUTE":
                    ParseInts(data, rule.ByMinute, 0, 59);
                    break;
                case "BYHOUR":
                    ParseInts(data, rule.ByHour, 0, 23);
                    break;
                case "BYMONTHDAY":
                    ParseInts(data, rule.ByMonthDay, -31, 31);
                    break;
                case "BYYEARDAY":
                    ParseInts(data, rule.ByYearDay, -366, 366);
                    break;
                case "BYWEEKNO":
                    ParseInts(data, rule.ByWeekNo, -53, 53);
                    break;
                case "BYMONTH":
                    ParseInts(data, rule.ByMonth, 1, 12);
                    break;
                case "BYSETPOS":
                    ParseInts(data, rule.BySetPos, -366, 366);
                    break;
                case "BYDAY":
                    foreach (var item in data.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        if (TryParseWeekday(item.Trim(), out var spec))
                        {
                            rule.ByDay.Add(spec);
                        }
                    }

                    break;
                case "WKST":
                    if (TryParseDayCode(data, out var weekStart))
                    {
                        rule.WeekStart = weekStart;
                    }

                    break;
            }
        }

        return haveFreq;
    }

    /// <summary>
    /// Converts a vCalendar 1.0 RRULE (<c>W1 MO TU #10</c>, <c>MD1 1 15 #0</c>, <c>MP1 1+ FR 20260101T000000Z</c>,
    /// <c>YM1 6 #0</c>) to a rule. Returns false when it cannot be read.
    /// </summary>
    public static bool TryParseVCalendar1(string? value, out RecurrenceRule rule)
    {
        rule = new RecurrenceRule();
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var tokens = value.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        var head = tokens[0].ToUpperInvariant();
        int digitStart = head.Length;
        while (digitStart > 0 && char.IsAsciiDigit(head[digitStart - 1]))
        {
            digitStart--;
        }

        var kind = head[..digitStart];
        if (digitStart == head.Length || !int.TryParse(head[digitStart..], NumberStyles.None, CultureInfo.InvariantCulture, out var interval) || interval < 1)
        {
            return false;
        }

        rule.Interval = interval;
        switch (kind)
        {
            case "D":
                rule.Frequency = RecurrenceFrequency.Daily;
                break;
            case "W":
                rule.Frequency = RecurrenceFrequency.Weekly;
                break;
            case "MD":
            case "MP":
                rule.Frequency = RecurrenceFrequency.Monthly;
                break;
            case "YM":
            case "YD":
                rule.Frequency = RecurrenceFrequency.Yearly;
                break;
            default:
                return false;
        }

        int? pendingOrdinal = null;
        for (var i = 1; i < tokens.Length; i++)
        {
            var token = tokens[i].ToUpperInvariant();
            if (token[0] == '#')
            {
                if (int.TryParse(token[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var count) && count > 0)
                {
                    rule.Count = count;
                }

                continue;
            }

            if (IcsDateTime.TryParseValue(token, out var until, out var untilKind) && token.Length >= 8)
            {
                rule.Until = new IcsDateTime(until, untilKind);
                continue;
            }

            if (TryParseDayCode(token, out var day))
            {
                rule.ByDay.Add(new WeekdaySpec(pendingOrdinal, day));
                continue;
            }

            if (token == "LD")
            {
                rule.ByMonthDay.Add(-1);
                continue;
            }

            var sign = 1;
            var number = token;
            if (token.EndsWith('-'))
            {
                sign = -1;
                number = token[..^1];
            }
            else if (token.EndsWith('+'))
            {
                number = token[..^1];
            }

            if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                continue;
            }

            switch (kind)
            {
                case "MP":
                    pendingOrdinal = n * sign;
                    break;
                case "MD":
                    rule.ByMonthDay.Add(n * sign);
                    break;
                case "YM":
                    if (n is >= 1 and <= 12)
                    {
                        rule.ByMonth.Add(n);
                    }

                    break;
                case "YD":
                    rule.ByYearDay.Add(n * sign);
                    break;
            }
        }

        return true;
    }

    private static bool TryParseFrequency(string text, out RecurrenceFrequency frequency)
    {
        switch (text.ToUpperInvariant())
        {
            case "SECONDLY":
                frequency = RecurrenceFrequency.Secondly;
                return true;
            case "MINUTELY":
                frequency = RecurrenceFrequency.Minutely;
                return true;
            case "HOURLY":
                frequency = RecurrenceFrequency.Hourly;
                return true;
            case "DAILY":
                frequency = RecurrenceFrequency.Daily;
                return true;
            case "WEEKLY":
                frequency = RecurrenceFrequency.Weekly;
                return true;
            case "MONTHLY":
                frequency = RecurrenceFrequency.Monthly;
                return true;
            case "YEARLY":
                frequency = RecurrenceFrequency.Yearly;
                return true;
            default:
                frequency = default;
                return false;
        }
    }

    private static void ParseInts(string data, List<int> target, int min, int max)
    {
        foreach (var item in data.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(item.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number >= min && number <= max && (min == 0 || number != 0))
            {
                target.Add(number);
            }
        }
    }

    private static bool TryParseDayCode(string text, out DayOfWeek day)
    {
        day = default;
        if (text.Length < 2)
        {
            return false;
        }

        var code = text[..2].ToUpperInvariant();
        for (var i = 0; i < DayCodes.Length; i++)
        {
            if (DayCodes[i] == code)
            {
                day = (DayOfWeek)i;
                return text.Length == 2;
            }
        }

        return false;
    }

    private static bool TryParseWeekday(string text, out WeekdaySpec spec)
    {
        spec = default;
        if (text.Length < 2 || !TryParseDayCode(text[^2..], out var day))
        {
            return false;
        }

        var prefix = text[..^2];
        if (prefix.Length == 0)
        {
            spec = new WeekdaySpec(null, day);
            return true;
        }

        if (int.TryParse(prefix, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ordinal) && ordinal is >= -53 and <= 53 && ordinal != 0)
        {
            spec = new WeekdaySpec(ordinal, day);
            return true;
        }

        return false;
    }

    /// <summary>The canonical RRULE value.</summary>
    public override string ToString()
    {
        var builder = new StringBuilder("FREQ=").Append(Frequency.ToString().ToUpperInvariant());
        if (Until is { } until)
        {
            builder.Append(";UNTIL=").Append(until.ToIcsString());
        }

        if (Count is { } count)
        {
            builder.Append(";COUNT=").Append(count.ToString(CultureInfo.InvariantCulture));
        }

        if (Interval != 1)
        {
            builder.Append(";INTERVAL=").Append(Interval.ToString(CultureInfo.InvariantCulture));
        }

        Append(builder, "BYSECOND", BySecond);
        Append(builder, "BYMINUTE", ByMinute);
        Append(builder, "BYHOUR", ByHour);
        if (ByDay.Count > 0)
        {
            builder.Append(";BYDAY=").Append(string.Join(",", ByDay.Select(spec => spec.ToString())));
        }

        Append(builder, "BYMONTHDAY", ByMonthDay);
        Append(builder, "BYYEARDAY", ByYearDay);
        Append(builder, "BYWEEKNO", ByWeekNo);
        Append(builder, "BYMONTH", ByMonth);
        Append(builder, "BYSETPOS", BySetPos);
        if (WeekStart != DayOfWeek.Monday)
        {
            builder.Append(";WKST=").Append(DayCodes[(int)WeekStart]);
        }

        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string name, List<int> values)
    {
        if (values.Count > 0)
        {
            builder.Append(';').Append(name).Append('=').Append(string.Join(",", values.Select(v => v.ToString(CultureInfo.InvariantCulture))));
        }
    }

    /// <summary>
    /// Generates the wall-clock instances of the rule in ascending order, starting at
    /// <paramref name="start"/> (the DTSTART reading). Instances before <paramref name="start"/> are
    /// never returned. <paramref name="untilWall"/> overrides <see cref="Until"/> (which is otherwise read as written, a
    /// date meaning the whole day) and is already in the same wall-clock terms as <paramref name="start"/>. The sequence is lazy and bounded:
    /// it ends at COUNT, at the bound, past year 9999 or after one million periods.
    /// </summary>
    public IEnumerable<DateTime> Enumerate(DateTime start, DateTime? untilWall = null, CancellationToken cancellationToken = default)
    {
        start = DateTime.SpecifyKind(start, DateTimeKind.Unspecified);
        untilWall ??= Until is { } declared ? (declared.IsDate ? DateTimeMath.Add(DateTimeMath.AddDays(declared.Value.Date, 1), TimeSpan.FromTicks(-1)) : declared.Value) : null;
        var interval = Math.Max(1, Interval);
        var plan = new Plan(this, start);
        var emitted = 0;
        for (long period = 0; period < MaxPeriods; period++)
        {
            if ((period & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (!plan.TryBuild(period, interval, out var candidates))
            {
                yield break;
            }

            candidates.Sort();
            var selected = ApplySetPos(candidates);
            foreach (var candidate in selected)
            {
                if (candidate < start)
                {
                    continue;
                }

                if (untilWall is { } until && candidate > until)
                {
                    yield break;
                }

                if (Count is { } count && emitted >= count)
                {
                    yield break;
                }

                emitted++;
                yield return candidate;
            }

            if (Count is { } limit && emitted >= limit)
            {
                yield break;
            }

        }
    }

    private List<DateTime> ApplySetPos(List<DateTime> candidates)
    {
        if (BySetPos.Count == 0 || candidates.Count == 0)
        {
            return candidates;
        }

        var picked = new SortedSet<DateTime>();
        foreach (var position in BySetPos)
        {
            var index = position > 0 ? position - 1 : candidates.Count + position;
            if (index >= 0 && index < candidates.Count)
            {
                picked.Add(candidates[index]);
            }
        }

        return [.. picked];
    }

    /// <summary>One-sentence English description, for recurrence summaries.</summary>
    public string Describe(IcsDateTime? start = null)
    {
        var culture = CultureInfo.InvariantCulture;
        var builder = new StringBuilder("Occurs every ");
        var unit = Frequency switch
        {
            RecurrenceFrequency.Secondly => "second",
            RecurrenceFrequency.Minutely => "minute",
            RecurrenceFrequency.Hourly => "hour",
            RecurrenceFrequency.Daily => "day",
            RecurrenceFrequency.Weekly => "week",
            RecurrenceFrequency.Monthly => "month",
            _ => "year",
        };
        if (Interval > 1)
        {
            builder.Append(Interval.ToString(culture)).Append(' ').Append(unit).Append('s');
        }
        else
        {
            builder.Append(unit);
        }

        if (ByDay.Count > 0)
        {
            builder.Append(" on ").Append(JoinAnd(ByDay.Select(spec => (spec.Ordinal is { } o ? Ordinal(o) + " " : string.Empty) + culture.DateTimeFormat.GetDayName(spec.Day)).ToList()));
        }

        if (ByMonthDay.Count > 0)
        {
            builder.Append(" on day ").Append(JoinAnd(ByMonthDay.Select(d => d == -1 ? "last" : d.ToString(culture)).ToList()));
        }

        if (ByMonth.Count > 0)
        {
            builder.Append(" in ").Append(JoinAnd(ByMonth.Select(m => culture.DateTimeFormat.GetMonthName(m)).ToList()));
        }

        if (BySetPos.Count > 0)
        {
            builder.Append(" (position ").Append(string.Join(", ", BySetPos.Select(p => p.ToString(culture)))).Append(')');
        }

        if (start is { } begin)
        {
            builder.Append(", starting ").Append(begin.Value.ToString("d MMMM yyyy", culture));
        }

        if (Count is { } count)
        {
            builder.Append(", ").Append(count.ToString(culture)).Append(count == 1 ? " time" : " times");
        }
        else if (Until is { } until)
        {
            builder.Append(", until ").Append(until.Value.ToString("d MMMM yyyy", culture));
        }

        builder.Append('.');
        return builder.ToString();
    }

    private static string Ordinal(int n) => n switch
    {
        -1 => "last",
        1 => "first",
        2 => "second",
        3 => "third",
        4 => "fourth",
        5 => "fifth",
        < 0 => "-" + (-n).ToString(CultureInfo.InvariantCulture) + "th from last",
        _ => n.ToString(CultureInfo.InvariantCulture) + "th",
    };

    private static string JoinAnd(List<string> items)
    {
        return items.Count switch
        {
            0 => string.Empty,
            1 => items[0],
            2 => items[0] + " and " + items[1],
            _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
        };
    }

    /// <summary>Per-enumeration state: the implicit BYxxx defaults the RFC derives from DTSTART.</summary>
    private sealed class Plan
    {
        private readonly RecurrenceRule rule;
        private readonly DateTime start;
        private readonly List<int> byMonth;
        private readonly List<int> byMonthDay;
        private readonly List<WeekdaySpec> byDay;
        private readonly bool monthScopedOrdinals;
        private readonly DateTime firstWeekStart;

        // AddTimes runs once per matching day, and a time zone's rule is expanded day by day over two
        // centuries; ordering the time lists there allocated three times per day.
        private readonly int[] hours;
        private readonly int[] minutes;
        private readonly int[] seconds;

        public Plan(RecurrenceRule rule, DateTime start)
        {
            hours = [.. (rule.ByHour.Count > 0 ? rule.ByHour : [start.Hour]).Distinct().Order()];
            minutes = [.. (rule.ByMinute.Count > 0 ? rule.ByMinute : [start.Minute]).Distinct().Order()];
            seconds = [.. (rule.BySecond.Count > 0 ? rule.BySecond : [start.Second]).Distinct().Order()];

            this.rule = rule;
            this.start = start;
            byMonth = [.. rule.ByMonth];
            byMonthDay = [.. rule.ByMonthDay];
            byDay = [.. rule.ByDay];
            monthScopedOrdinals = rule.ByMonth.Count > 0;
            var anyDayRule = rule.ByDay.Count > 0 || rule.ByMonthDay.Count > 0 || rule.ByYearDay.Count > 0 || rule.ByWeekNo.Count > 0;
            switch (rule.Frequency)
            {
                case RecurrenceFrequency.Yearly:
                    if (!anyDayRule)
                    {
                        if (byMonth.Count == 0)
                        {
                            byMonth.Add(start.Month);
                        }

                        byMonthDay.Add(start.Day);
                    }

                    break;
                case RecurrenceFrequency.Monthly:
                    if (rule.ByDay.Count == 0 && rule.ByMonthDay.Count == 0 && rule.ByYearDay.Count == 0)
                    {
                        byMonthDay.Add(start.Day);
                    }

                    break;
                case RecurrenceFrequency.Weekly:
                    if (rule.ByDay.Count == 0)
                    {
                        byDay.Add(new WeekdaySpec(null, start.DayOfWeek));
                    }

                    break;
            }

            firstWeekStart = StartOfWeek(start.Date, rule.WeekStart);
        }

        public bool TryBuild(long period, int interval, out List<DateTime> result)
        {
            result = [];
            switch (rule.Frequency)
            {
                case RecurrenceFrequency.Yearly:
                {
                    var year = start.Year + period * interval;
                    if (year > 9999)
                    {
                        return false;
                    }

                    AddDays(result, new DateTime((int)year, 1, 1), DateTime.IsLeapYear((int)year) ? 366 : 365);
                    return true;
                }

                case RecurrenceFrequency.Monthly:
                {
                    var index = start.Year * 12L + (start.Month - 1) + period * interval;
                    var year = index / 12;
                    if (year > 9999)
                    {
                        return false;
                    }

                    var month = (int)(index % 12) + 1;
                    AddDays(result, new DateTime((int)year, month, 1), DateTime.DaysInMonth((int)year, month));
                    return true;
                }

                case RecurrenceFrequency.Weekly:
                {
                    var days = 7L * interval * period;
                    if ((DateTime.MaxValue - firstWeekStart).TotalDays < days + 8)
                    {
                        return false;
                    }

                    AddDays(result, firstWeekStart.AddDays(days), 7);
                    return true;
                }

                case RecurrenceFrequency.Daily:
                {
                    var days = (long)interval * period;
                    if ((DateTime.MaxValue - start).TotalDays < days + 2)
                    {
                        return false;
                    }

                    var day = start.Date.AddDays(days);
                    if (DayAllowed(day))
                    {
                        AddTimes(result, day);
                    }

                    return true;
                }

                default:
                    return BuildSubDaily(period, interval, result);
            }
        }

        private bool BuildSubDaily(long period, int interval, List<DateTime> result)
        {
            var unit = rule.Frequency switch
            {
                RecurrenceFrequency.Hourly => TimeSpan.TicksPerHour,
                RecurrenceFrequency.Minutely => TimeSpan.TicksPerMinute,
                _ => TimeSpan.TicksPerSecond,
            };
            var step = unit * interval;
            if (step <= 0 || period > (DateTime.MaxValue.Ticks - start.Ticks) / step - 1)
            {
                return false;
            }

            var t = new DateTime(start.Ticks + step * period, DateTimeKind.Unspecified);
            if (!DayAllowed(t.Date))
            {
                return true;
            }

            var hours = rule.Frequency == RecurrenceFrequency.Hourly ? [t.Hour] : (rule.ByHour.Count > 0 ? rule.ByHour : [t.Hour]);
            if (rule.Frequency == RecurrenceFrequency.Hourly && rule.ByHour.Count > 0 && !rule.ByHour.Contains(t.Hour))
            {
                return true;
            }

            if (rule.Frequency != RecurrenceFrequency.Hourly)
            {
                // Minutely and secondly: BYHOUR limits.
                if (rule.ByHour.Count > 0 && !rule.ByHour.Contains(t.Hour))
                {
                    return true;
                }

                hours = [t.Hour];
            }

            List<int> minutes;
            if (rule.Frequency == RecurrenceFrequency.Hourly)
            {
                minutes = rule.ByMinute.Count > 0 ? rule.ByMinute : [t.Minute];
            }
            else
            {
                if (rule.ByMinute.Count > 0 && !rule.ByMinute.Contains(t.Minute))
                {
                    return true;
                }

                minutes = [t.Minute];
            }

            List<int> seconds;
            if (rule.Frequency == RecurrenceFrequency.Secondly)
            {
                if (rule.BySecond.Count > 0 && !rule.BySecond.Contains(t.Second))
                {
                    return true;
                }

                seconds = [t.Second];
            }
            else
            {
                seconds = rule.BySecond.Count > 0 ? rule.BySecond : [t.Second];
            }

            foreach (var hour in hours)
            {
                foreach (var minute in minutes)
                {
                    foreach (var second in seconds)
                    {
                        result.Add(t.Date.AddHours(hour).AddMinutes(minute).AddSeconds(Math.Min(second, 59)));
                    }
                }
            }

            return true;
        }

        private void AddDays(List<DateTime> result, DateTime first, int count)
        {
            for (var i = 0; i < count; i++)
            {
                var day = first.AddDays(i);

                // A day outside BYMONTH fails DayAllowed's first test, so the rest of its month can be
                // stepped over whole: the same days are kept, without asking about the others one by one.
                if (byMonth.Count > 0 && !byMonth.Contains(day.Month))
                {
                    i += DateTime.DaysInMonth(day.Year, day.Month) - day.Day;
                    continue;
                }

                if (DayAllowed(day))
                {
                    AddTimes(result, day);
                }
            }
        }

        private void AddTimes(List<DateTime> result, DateTime day)
        {
            foreach (var hour in hours)
            {
                foreach (var minute in minutes)
                {
                    foreach (var second in seconds)
                    {
                        result.Add(day.AddHours(hour).AddMinutes(minute).AddSeconds(Math.Min(second, 59)).AddTicks(start.Ticks % TimeSpan.TicksPerSecond));
                    }
                }
            }
        }

        private bool DayAllowed(DateTime day)
        {
            if (byMonth.Count > 0 && !byMonth.Contains(day.Month))
            {
                return false;
            }

            if (rule.ByWeekNo.Count > 0 && rule.Frequency == RecurrenceFrequency.Yearly && !WeekNumberMatches(day))
            {
                return false;
            }

            if (rule.ByYearDay.Count > 0)
            {
                var daysInYear = DateTime.IsLeapYear(day.Year) ? 366 : 365;
                var ok = false;
                foreach (var n in rule.ByYearDay)
                {
                    if (n > 0 ? day.DayOfYear == n : day.DayOfYear == daysInYear + n + 1)
                    {
                        ok = true;
                        break;
                    }
                }

                if (!ok)
                {
                    return false;
                }
            }

            if (byMonthDay.Count > 0)
            {
                var daysInMonth = DateTime.DaysInMonth(day.Year, day.Month);
                var ok = false;
                foreach (var n in byMonthDay)
                {
                    if (n > 0 ? day.Day == n : day.Day == daysInMonth + n + 1)
                    {
                        ok = true;
                        break;
                    }
                }

                if (!ok)
                {
                    return false;
                }
            }

            if (byDay.Count > 0)
            {
                var ok = false;
                foreach (var spec in byDay)
                {
                    if (spec.Day == day.DayOfWeek && OrdinalMatches(spec.Ordinal, day))
                    {
                        ok = true;
                        break;
                    }
                }

                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }

        private bool OrdinalMatches(int? ordinal, DateTime day)
        {
            if (ordinal is not { } n)
            {
                return true;
            }

            switch (rule.Frequency)
            {
                case RecurrenceFrequency.Monthly:
                    return Nth(n, day.Day, DateTime.DaysInMonth(day.Year, day.Month));
                case RecurrenceFrequency.Yearly:
                    return monthScopedOrdinals
                        ? Nth(n, day.Day, DateTime.DaysInMonth(day.Year, day.Month))
                        : Nth(n, day.DayOfYear, DateTime.IsLeapYear(day.Year) ? 366 : 365);
                default:
                    return true;
            }
        }

        private static bool Nth(int n, int dayOfScope, int daysInScope)
        {
            return n > 0 ? (dayOfScope - 1) / 7 + 1 == n : (daysInScope - dayOfScope) / 7 + 1 == -n;
        }

        private bool WeekNumberMatches(DateTime day)
        {
            var year = day.Year;
            var weekYear = year;
            if (day < Week1Start(year, rule.WeekStart))
            {
                weekYear = year - 1;
            }
            else if (year < 9999 && day >= Week1Start(year + 1, rule.WeekStart))
            {
                weekYear = year + 1;
            }

            var first = Week1Start(weekYear, rule.WeekStart);
            var number = (day - first).Days / 7 + 1;
            var total = weekYear >= 9999 ? 52 : (Week1Start(weekYear + 1, rule.WeekStart) - first).Days / 7;
            foreach (var n in rule.ByWeekNo)
            {
                if (n > 0 ? number == n : number == total + n + 1)
                {
                    return true;
                }
            }

            return false;
        }

        private static DateTime Week1Start(int year, DayOfWeek weekStart)
        {
            if (year < 1)
            {
                return DateTime.MinValue;
            }

            var january1 = new DateTime(year, 1, 1);
            var startOfWeek = StartOfWeek(january1, weekStart);
            var daysInYear = 7 - (january1 - startOfWeek).Days;
            return daysInYear >= 4 ? startOfWeek : DateTimeMath.AddDays(startOfWeek, 7);
        }
    }

    internal static DateTime StartOfWeek(DateTime date, DayOfWeek weekStart)
    {
        var diff = ((int)date.DayOfWeek - (int)weekStart + 7) % 7;
        return DateTimeMath.AddDays(date.Date, -diff);
    }
}
