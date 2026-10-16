using System.Globalization;

namespace XRay.Ics.Tests;

public sealed class RecurrenceTests
{
    private static List<string> Days(string rule, string start, int take = 100, string format = "yyyy-MM-dd")
    {
        var dtstart = DateTime.ParseExact(start, "yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture);
        return RecurrenceRule.Parse(rule).Enumerate(dtstart, null, Tok.Ct).Take(take).Select(d => d.ToString(format, CultureInfo.InvariantCulture)).ToList();
    }

    [Fact]
    public void Daily_count_and_interval()
    {
        Assert.Equal(["2026-01-05", "2026-01-06", "2026-01-07"], Days("FREQ=DAILY;COUNT=3", "2026-01-05T09:00"));
        Assert.Equal(["2026-01-05", "2026-01-08", "2026-01-11"], Days("FREQ=DAILY;INTERVAL=3;COUNT=3", "2026-01-05T09:00"));
    }

    [Fact]
    public void Until_is_inclusive()
    {
        Assert.Equal(["2026-01-05", "2026-01-06", "2026-01-07"], Days("FREQ=DAILY;UNTIL=20260107T090000", "2026-01-05T09:00"));
        Assert.Equal(["2026-01-05", "2026-01-06"], Days("FREQ=DAILY;UNTIL=20260107T085959", "2026-01-05T09:00"));
    }

    [Fact]
    public void Weekly_byday()
    {
        Assert.Equal(["2026-01-05", "2026-01-07", "2026-01-09", "2026-01-12"], Days("FREQ=WEEKLY;BYDAY=MO,WE,FR;COUNT=4", "2026-01-05T09:00"));
        Assert.Equal(["2026-01-07", "2026-01-14", "2026-01-21"], Days("FREQ=WEEKLY;COUNT=3", "2026-01-07T09:00"));
    }

    [Fact]
    public void Wkst_changes_which_week_an_interval_counts_from()
    {
        // The example of RFC 5545 section 3.3.10: the same rule differs only by WKST.
        Assert.Equal(["1997-08-05", "1997-08-10", "1997-08-19", "1997-08-24"], Days("FREQ=WEEKLY;INTERVAL=2;COUNT=4;BYDAY=TU,SU;WKST=MO", "1997-08-05T09:00"));
        Assert.Equal(["1997-08-05", "1997-08-17", "1997-08-19", "1997-08-31"], Days("FREQ=WEEKLY;INTERVAL=2;COUNT=4;BYDAY=TU,SU;WKST=SU", "1997-08-05T09:00"));
        Assert.Equal(["2026-01-04", "2026-01-12", "2026-01-18", "2026-01-26", "2026-02-01", "2026-02-09"], Days("FREQ=WEEKLY;INTERVAL=2;BYDAY=SU,MO;WKST=MO;COUNT=6", "2026-01-04T09:00"));
        Assert.Equal(["2026-01-04", "2026-01-05", "2026-01-18", "2026-01-19", "2026-02-01", "2026-02-02"], Days("FREQ=WEEKLY;INTERVAL=2;BYDAY=SU,MO;WKST=SU;COUNT=6", "2026-01-04T09:00"));
    }

    [Fact]
    public void Monthly_rules()
    {
        Assert.Equal(["2026-01-31", "2026-03-31", "2026-05-31", "2026-07-31"], Days("FREQ=MONTHLY;COUNT=4", "2026-01-31T09:00"));
        Assert.Equal(["2026-01-01", "2026-01-15", "2026-01-31", "2026-02-01", "2026-02-15", "2026-02-28"], Days("FREQ=MONTHLY;BYMONTHDAY=1,15,-1;COUNT=6", "2026-01-01T09:00"));
        Assert.Equal(["2026-01-30", "2026-02-27", "2026-03-27", "2026-04-24"], Days("FREQ=MONTHLY;BYDAY=-1FR;COUNT=4", "2026-01-30T09:00"));
        Assert.Equal(["2026-01-13", "2026-02-10", "2026-03-10"], Days("FREQ=MONTHLY;BYDAY=2TU;COUNT=3", "2026-01-13T09:00"));
        Assert.Equal(["2026-01-30", "2026-02-27", "2026-03-31"], Days("FREQ=MONTHLY;BYDAY=MO,TU,WE,TH,FR;BYSETPOS=-1;COUNT=3", "2026-01-30T09:00"));
        Assert.Equal(["2026-01-03", "2026-02-01", "2026-03-01"], Days("FREQ=MONTHLY;BYDAY=SA,SU;BYSETPOS=1;COUNT=3", "2026-01-03T09:00"));
    }

    [Fact]
    public void Yearly_rules()
    {
        Assert.Equal(["2024-02-29", "2028-02-29", "2032-02-29"], Days("FREQ=YEARLY;COUNT=3", "2024-02-29T09:00"));
        Assert.Equal(["2024-02-29", "2028-02-29"], Days("FREQ=YEARLY;BYMONTH=2;BYMONTHDAY=29;COUNT=2", "2024-02-29T09:00"));
        Assert.Equal(["2026-03-02", "2026-09-07", "2027-03-01", "2027-09-06"], Days("FREQ=YEARLY;BYMONTH=3,9;BYDAY=1MO;COUNT=4", "2026-03-02T09:00"));
        Assert.Equal(["2026-01-01", "2026-04-10", "2026-12-31"], Days("FREQ=YEARLY;BYYEARDAY=1,100,-1;COUNT=3", "2026-01-01T09:00"));
        Assert.Equal(["2026-05-11", "2027-05-17", "2028-05-15"], Days("FREQ=YEARLY;BYWEEKNO=20;BYDAY=MO;COUNT=3", "2026-05-11T09:00"));
        Assert.Equal(["2026-01-05", "2026-04-06", "2026-07-06"], Days("FREQ=YEARLY;BYDAY=1MO;BYMONTH=1,4,7;COUNT=3", "2026-01-05T09:00"));
    }

    [Fact]
    public void Time_parts_expand_and_limit()
    {
        Assert.Equal(["00:00", "05:00", "10:00"], Days("FREQ=HOURLY;INTERVAL=5;COUNT=3", "2026-01-05T00:00", format: "HH:mm"));
        Assert.Equal(["09:00", "09:30", "17:00", "17:30", "09:00"], Days("FREQ=DAILY;BYHOUR=9,17;BYMINUTE=0,30;COUNT=5", "2026-01-05T09:00", format: "HH:mm"));
        Assert.Equal(["09:00", "09:30", "10:00", "10:30", "09:00"], Days("FREQ=MINUTELY;INTERVAL=30;BYHOUR=9,10;COUNT=5", "2026-01-05T09:00", format: "HH:mm"));
        Assert.Equal(["09:00:00", "09:00:15", "09:00:30"], Days("FREQ=SECONDLY;INTERVAL=15;COUNT=3", "2026-01-05T09:00", format: "HH:mm:ss"));
        Assert.Equal(["2026-01-05", "2026-01-06"], Days("FREQ=DAILY;BYDAY=MO,TU;COUNT=2", "2026-01-05T09:00"));
    }

    [Fact]
    public void Instances_before_the_start_are_never_returned_and_unmatched_starts_are_not_invented()
    {
        // DTSTART is a Sunday and the rule asks for Mondays: the rule itself yields only Mondays.
        Assert.Equal(["2026-01-05", "2026-01-12"], Days("FREQ=WEEKLY;BYDAY=MO;COUNT=2", "2026-01-04T09:00"));
    }

    [Fact]
    public void Impossible_rules_end_instead_of_looping()
    {
        Assert.Empty(Days("FREQ=MONTHLY;BYMONTH=2;BYMONTHDAY=30", "2026-01-05T09:00"));
        Assert.Empty(Days("FREQ=SECONDLY;BYMONTH=2;BYMONTHDAY=30", "2026-01-05T09:00"));
        var many = Days("FREQ=YEARLY", "2026-01-05T09:00", int.MaxValue);
        Assert.Equal(9999 - 2026 + 1, many.Count);
    }

    [Fact]
    public void Rules_parse_print_and_describe()
    {
        var rule = RecurrenceRule.Parse("freq=weekly;interval=2;byday=mo,-1fr;wkst=su;count=5");
        Assert.Equal(RecurrenceFrequency.Weekly, rule.Frequency);
        Assert.Equal(2, rule.Interval);
        Assert.Equal(5, rule.Count);
        Assert.Equal(DayOfWeek.Sunday, rule.WeekStart);
        Assert.Equal([new WeekdaySpec(null, DayOfWeek.Monday), new WeekdaySpec(-1, DayOfWeek.Friday)], rule.ByDay);
        Assert.Equal("FREQ=WEEKLY;COUNT=5;INTERVAL=2;BYDAY=MO,-1FR;WKST=SU", rule.ToString());
        Assert.Equal("Occurs every 2 weeks on Monday and last Friday, 5 times.", rule.Describe());
        Assert.Equal("Occurs every day, until 31 January 2026.", RecurrenceRule.Parse("FREQ=DAILY;UNTIL=20260131").Describe());
        Assert.False(RecurrenceRule.TryParse("FREQ=SOMETIMES", out _));
        Assert.False(RecurrenceRule.TryParse("COUNT=3", out _));
        Assert.False(RecurrenceRule.TryParse("", out _));
        Assert.Equal(RecurrenceFrequency.Yearly, RecurrenceRule.Parse("RRULE:FREQ=YEARLY;BYMONTH=13,2;BYMONTHDAY=0,5").Frequency);
        Assert.Equal([2], RecurrenceRule.Parse("FREQ=YEARLY;BYMONTH=13,2").ByMonth);
        Assert.Throws<FormatException>(() => RecurrenceRule.Parse("nope"));
    }

    [Fact]
    public void Vcalendar_1_rules_convert()
    {
        Assert.True(RecurrenceRule.TryParseVCalendar1("W1 MO WE #4", out var weekly));
        Assert.Equal("FREQ=WEEKLY;COUNT=4;BYDAY=MO,WE", weekly.ToString());
        Assert.True(RecurrenceRule.TryParseVCalendar1("D2 #5", out var daily));
        Assert.Equal("FREQ=DAILY;COUNT=5;INTERVAL=2", daily.ToString());
        Assert.True(RecurrenceRule.TryParseVCalendar1("MD1 1 15 #0", out var monthly));
        Assert.Equal("FREQ=MONTHLY;BYMONTHDAY=1,15", monthly.ToString());
        Assert.True(RecurrenceRule.TryParseVCalendar1("MP1 1+ FR 20260701T000000Z", out var position));
        Assert.Equal("FREQ=MONTHLY;UNTIL=20260701T000000Z;BYDAY=1FR", position.ToString());
        Assert.True(RecurrenceRule.TryParseVCalendar1("YM1 6 #3", out var yearly));
        Assert.Equal("FREQ=YEARLY;COUNT=3;BYMONTH=6", yearly.ToString());
        Assert.False(RecurrenceRule.TryParseVCalendar1("XX9", out _));
    }

    private static Calendar Series(params string[] lines) => Tok.Load(Tok.Cal([.. lines]));

    private static string Stamp(IcsDateTime value) => IcsManifest.Format(value);

    [Fact]
    public void Event_expansion_applies_exdate_rdate_and_the_window()
    {
        var calendar = Series("BEGIN:VEVENT", "UID:e", "DTSTART:20260105T090000Z", "DTEND:20260105T100000Z", "RRULE:FREQ=DAILY;COUNT=8",
            "EXDATE:20260107T090000Z,20260109T090000Z", "RDATE:20260120T090000Z", "END:VEVENT");
        var all = calendar.Events[0].Expand(DateTimeOffset.MinValue.AddYears(1), DateTimeOffset.MaxValue.AddYears(-1), 1000, null, Tok.Ct).ToList();
        Assert.Equal(["2026-01-05T09:00:00Z", "2026-01-06T09:00:00Z", "2026-01-08T09:00:00Z", "2026-01-10T09:00:00Z", "2026-01-11T09:00:00Z", "2026-01-12T09:00:00Z", "2026-01-20T09:00:00Z"], all.Select(o => Stamp(o.Start)));
        Assert.All(all, o => Assert.Equal(TimeSpan.FromHours(1), o.End.ToDateTimeOffset() - o.Start.ToDateTimeOffset()));
        var window = calendar.Events[0].Expand(new DateTimeOffset(2026, 1, 8, 9, 30, 0, TimeSpan.Zero), new DateTimeOffset(2026, 1, 11, 9, 0, 0, TimeSpan.Zero), 1000, null, Tok.Ct).ToList();
        // An instance ending after the window start overlaps it; one starting at the window end does not.
        Assert.Equal(["2026-01-08T09:00:00Z", "2026-01-10T09:00:00Z"], window.Select(o => Stamp(o.Start)));
        Assert.Equal(2, calendar.Events[0].Expand(DateTimeOffset.MinValue.AddYears(1), DateTimeOffset.MaxValue.AddYears(-1), 2, null, Tok.Ct).Count());
        Assert.Empty(calendar.Events[0].Expand(DateTimeOffset.MinValue.AddYears(1), DateTimeOffset.MaxValue.AddYears(-1), 0, null, Tok.Ct));
    }

    [Fact]
    public void A_non_recurring_event_is_one_instance()
    {
        var calendar = Series("BEGIN:VEVENT", "UID:e", "DTSTART:20260105T090000Z", "DURATION:PT30M", "END:VEVENT");
        var only = calendar.Events[0].Expand(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddYears(100), 1000, null, Tok.Ct).Single();
        Assert.Equal("2026-01-05T09:30:00Z", Stamp(only.End));
        Assert.Empty(calendar.Events[0].Expand(DateTimeOffset.UnixEpoch, new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero), 1000, null, Tok.Ct));
    }

    [Fact]
    public void Zoned_series_follow_daylight_saving_time()
    {
        var calendar = Series(
            "BEGIN:VTIMEZONE", "TZID:Europe/Berlin", "BEGIN:DAYLIGHT", "TZOFFSETFROM:+0100", "TZOFFSETTO:+0200", "DTSTART:19700329T020000", "RRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=-1SU", "END:DAYLIGHT",
            "BEGIN:STANDARD", "TZOFFSETFROM:+0200", "TZOFFSETTO:+0100", "DTSTART:19701025T030000", "RRULE:FREQ=YEARLY;BYMONTH=10;BYDAY=-1SU", "END:STANDARD", "END:VTIMEZONE",
            "BEGIN:VEVENT", "UID:dst", "DTSTART;TZID=Europe/Berlin:20260316T090000", "DTEND;TZID=Europe/Berlin:20260316T100000", "RRULE:FREQ=WEEKLY;COUNT=4", "END:VEVENT");
        var instances = calendar.Events[0].Expand(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddYears(100), 1000, null, Tok.Ct).ToList();
        Assert.Equal(["2026-03-16T08:00:00Z", "2026-03-23T08:00:00Z", "2026-03-30T07:00:00Z", "2026-04-06T07:00:00Z"], instances.Select(o => Stamp(o.Start)));
        Assert.All(instances, o => Assert.Equal(new TimeSpan(9, 0, 0), o.Start.Value.TimeOfDay));
        Assert.All(instances, o => Assert.Equal(new TimeSpan(10, 0, 0), o.End.Value.TimeOfDay));
    }

    [Fact]
    public void Until_in_utc_is_converted_into_the_zone_of_the_series()
    {
        var calendar = Series("BEGIN:VEVENT", "UID:u", "DTSTART;TZID=America/New_York:20260105T090000", "RRULE:FREQ=DAILY;UNTIL=20260107T140000Z", "END:VEVENT");
        var days = calendar.Events[0].Expand(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddYears(100), 1000, null, Tok.Ct).Select(o => o.Start.Value.Day).ToList();
        Assert.Equal([5, 6, 7], days);
    }

    [Fact]
    public void All_day_series_keep_dates_and_exclusive_ends()
    {
        var calendar = Series("BEGIN:VEVENT", "UID:a", "DTSTART;VALUE=DATE:20260105", "DTEND;VALUE=DATE:20260107", "RRULE:FREQ=WEEKLY;COUNT=3", "EXDATE;VALUE=DATE:20260112", "END:VEVENT");
        var instances = calendar.Events[0].Expand(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddYears(100), 1000, null, Tok.Ct).ToList();
        Assert.Equal(["2026-01-05", "2026-01-19"], instances.Select(o => Stamp(o.Start)));
        Assert.Equal(["2026-01-07", "2026-01-21"], instances.Select(o => Stamp(o.End)));
        Assert.All(instances, o => Assert.True(o.Start.IsDate));
    }

    [Fact]
    public void Floating_series_use_the_floating_zone_for_the_window()
    {
        var calendar = Series("BEGIN:VEVENT", "UID:f", "DTSTART:20260105T233000", "DTEND:20260106T003000", "RRULE:FREQ=DAILY;COUNT=2", "END:VEVENT");
        var tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
        var window = new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero);
        // 23:30 in Tokyo is 14:30 UTC: it starts after a window that closes at 14:00 UTC only when read in UTC.
        Assert.Empty(calendar.Events[0].Expand(window, window.AddHours(3), 10, null, Tok.Ct));
        Assert.Single(calendar.Events[0].Expand(window, window.AddHours(3), 10, tokyo, Tok.Ct));
    }

    [Fact]
    public void Calendar_expansion_applies_overrides_and_cancellations()
    {
        var calendar = Series(
            "BEGIN:VEVENT", "UID:s", "DTSTART:20260105T090000Z", "DTEND:20260105T100000Z", "SUMMARY:Standup", "RRULE:FREQ=DAILY;COUNT=5", "END:VEVENT",
            "BEGIN:VEVENT", "UID:s", "RECURRENCE-ID:20260106T090000Z", "DTSTART:20260106T130000Z", "DTEND:20260106T140000Z", "SUMMARY:Moved", "END:VEVENT",
            "BEGIN:VEVENT", "UID:s", "RECURRENCE-ID:20260108T090000Z", "DTSTART:20260108T090000Z", "STATUS:CANCELLED", "END:VEVENT",
            "BEGIN:VEVENT", "UID:orphan", "RECURRENCE-ID:20260110T090000Z", "DTSTART:20260110T090000Z", "SUMMARY:Orphan", "END:VEVENT");
        var instances = calendar.Expand(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddYears(100), 50, null, Tok.Ct);
        Assert.Equal(["2026-01-05T09:00:00Z", "2026-01-06T13:00:00Z", "2026-01-07T09:00:00Z", "2026-01-09T09:00:00Z", "2026-01-10T09:00:00Z"], instances.Select(o => Stamp(o.Start)));
        Assert.Equal([false, true, false, false, true], instances.Select(o => o.IsOverride));
        Assert.Equal("Moved", instances[1].Event.Summary);
        Assert.Equal(2, calendar.Expand(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddYears(100), 2, null, Tok.Ct).Count);
    }
}
