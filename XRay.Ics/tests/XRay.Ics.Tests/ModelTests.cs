using System.Text;

namespace XRay.Ics.Tests;

public sealed class ModelTests
{
    private const string BerlinZone = "BEGIN:VTIMEZONE\r\nTZID:Europe/Berlin\r\nBEGIN:DAYLIGHT\r\nTZOFFSETFROM:+0100\r\nTZOFFSETTO:+0200\r\nDTSTART:19700329T020000\r\nRRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=-1SU\r\nEND:DAYLIGHT\r\nBEGIN:STANDARD\r\nTZOFFSETFROM:+0200\r\nTZOFFSETTO:+0100\r\nDTSTART:19701025T030000\r\nRRULE:FREQ=YEARLY;BYMONTH=10;BYDAY=-1SU\r\nEND:STANDARD\r\nEND:VTIMEZONE";

    private static string Utc(IcsDateTime? value) => value!.Value.ToDateTimeOffset().UtcDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Event_fields_are_typed()
    {
        var anEvent = Tok.OneEvent(
            "DTSTAMP:20260101T080000Z", "CREATED:20251230T101500Z", "LAST-MODIFIED:20251231T090000Z", "DTSTART:20260105T100000Z", "DTEND:20260105T113000Z",
            "SUMMARY:Kickoff", "DESCRIPTION:a\\nb", "LOCATION:Room 4\\, A", "STATUS:TENTATIVE", "CLASS:PRIVATE", "TRANSP:TRANSPARENT", "PRIORITY:3", "SEQUENCE:2",
            "GEO:52.5;13.4", "URL:https://example.com", "CATEGORIES:A,B", "COMMENT:note");
        Assert.Equal("t@example.com", anEvent.Uid);
        Assert.Equal("Kickoff", anEvent.Summary);
        Assert.Equal("a\nb", anEvent.Description);
        Assert.Equal("Room 4, A", anEvent.Location);
        Assert.Equal(CalendarStatus.Tentative, anEvent.Status);
        Assert.Equal(CalendarClassification.Private, anEvent.Classification);
        Assert.Equal(CalendarTransparency.Transparent, anEvent.Transparency);
        Assert.Equal(3, anEvent.Priority);
        Assert.Equal(2, anEvent.Sequence);
        Assert.Equal(new GeoLocation(52.5, 13.4), anEvent.Geo);
        Assert.Equal("note", anEvent.Comment);
        Assert.Equal("2025-12-30 10:15", Utc(anEvent.Created));
        Assert.Equal("2025-12-31 09:00", Utc(anEvent.LastModified));
        Assert.Equal("2026-01-01 08:00", Utc(anEvent.Stamp));
        Assert.Equal(TimeSpan.FromMinutes(90), anEvent.Duration);
        Assert.False(anEvent.IsAllDay);
        Assert.False(anEvent.IsRecurring);
        Assert.Equal(IcsDateTimeKind.Utc, anEvent.Start!.Value.Kind);
    }

    [Fact]
    public void End_falls_back_to_duration_and_then_to_the_rfc_defaults()
    {
        Assert.Equal("2026-01-05 10:45", Utc(Tok.OneEvent("DTSTART:20260105T100000Z", "DURATION:PT45M").End));
        Assert.Equal(TimeSpan.FromMinutes(45), Tok.OneEvent("DTSTART:20260105T100000Z", "DURATION:PT45M").Duration);
        Assert.Equal("2026-01-05 10:00", Utc(Tok.OneEvent("DTSTART:20260105T100000Z").End));
        var allDay = Tok.OneEvent("DTSTART;VALUE=DATE:20260105");
        Assert.True(allDay.IsAllDay);
        Assert.Equal(new DateTime(2026, 1, 6), allDay.End!.Value.Value);
        Assert.Equal(TimeSpan.FromDays(1), allDay.Duration);
        Assert.Null(Tok.OneEvent("SUMMARY:x").End);
        Assert.Null(Tok.OneEvent("SUMMARY:x").Duration);
    }

    [Fact]
    public void Utc_floating_and_date_values_keep_their_kind()
    {
        var anEvent = Tok.OneEvent("DTSTART:20260310T143000", "DTEND:20260310T153000", "CREATED;VALUE=DATE:20260301");
        Assert.True(anEvent.Start!.Value.IsFloating);
        Assert.Null(anEvent.Start.Value.Offset);
        Assert.Equal(new DateTime(2026, 3, 10, 14, 30, 0), anEvent.Start.Value.Value);
        Assert.True(anEvent.Created!.Value.IsDate);
        var instant = anEvent.Start.Value.ToDateTimeOffset(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        Assert.Equal(TimeSpan.FromHours(-4), instant.Offset);
        Assert.Equal(new DateTimeOffset(2026, 3, 10, 14, 30, 0, TimeSpan.Zero), anEvent.Start.Value.ToDateTimeOffset());
    }

    [Fact]
    public void Vtimezone_rules_give_the_summer_and_winter_offsets()
    {
        var calendar = Tok.Load(Tok.Cal(BerlinZone,
            "BEGIN:VEVENT", "UID:s", "DTSTART;TZID=Europe/Berlin:20260706T100000", "DTEND;TZID=Europe/Berlin:20260706T113000", "END:VEVENT",
            "BEGIN:VEVENT", "UID:w", "DTSTART;TZID=Europe/Berlin:20260112T100000", "END:VEVENT",
            "BEGIN:VEVENT", "UID:edge1", "DTSTART;TZID=Europe/Berlin:20260329T015959", "END:VEVENT",
            "BEGIN:VEVENT", "UID:edge2", "DTSTART;TZID=Europe/Berlin:20260329T030000", "END:VEVENT"));
        Assert.Single(calendar.TimeZones);
        Assert.Equal("Europe/Berlin", calendar.TimeZones[0].TzId);
        Assert.Equal(TimeSpan.FromHours(2), calendar.Events[0].Start!.Value.Offset);
        Assert.Equal("2026-07-06 08:00", Utc(calendar.Events[0].Start));
        Assert.Equal("2026-07-06 09:30", Utc(calendar.Events[0].End));
        Assert.Equal(TimeSpan.FromHours(1), calendar.Events[1].Start!.Value.Offset);
        Assert.Equal(TimeSpan.FromHours(1), calendar.Events[2].Start!.Value.Offset);
        Assert.Equal(TimeSpan.FromHours(2), calendar.Events[3].Start!.Value.Offset);
        Assert.Equal(IcsDateTimeKind.Zoned, calendar.Events[0].Start!.Value.Kind);
        Assert.Equal("Europe/Berlin", calendar.Events[0].Start!.Value.TzId);
    }

    [Fact]
    public void A_calendars_own_vtimezone_wins_over_the_system_zone()
    {
        var calendar = Tok.Load(Tok.Cal(
            "BEGIN:VTIMEZONE", "TZID:Europe/Berlin", "BEGIN:STANDARD", "TZOFFSETFROM:+0530", "TZOFFSETTO:+0530", "DTSTART:16010101T000000", "END:STANDARD", "END:VTIMEZONE",
            "BEGIN:VEVENT", "UID:c", "DTSTART;TZID=Europe/Berlin:20260706T100000", "END:VEVENT"));
        Assert.Equal(TimeSpan.FromMinutes(330), calendar.Events[0].Start!.Value.Offset);
    }

    [Theory]
    [InlineData("America/New_York", "20260715T090000", 13)]
    [InlineData("America/New_York", "20260115T090000", 14)]
    [InlineData("Eastern Standard Time", "20260715T090000", 13)]
    [InlineData("W. Europe Standard Time", "20260715T090000", 7)]
    [InlineData("/mozilla.org/20050126_1/Europe/Paris", "20260715T090000", 7)]
    [InlineData("/Europe/Stockholm", "20260115T090000", 8)]
    [InlineData("GMT+0200", "20260115T090000", 7)]
    [InlineData("UTC", "20260115T090000", 9)]
    [InlineData("Asia/Kolkata", "20260115T090000", 3)]
    public void System_and_windows_zone_names_resolve(string tzid, string local, int expectedUtcHour)
    {
        var anEvent = Tok.OneEvent($"DTSTART;TZID={tzid}:{local}");
        Assert.NotNull(anEvent.Start!.Value.Offset);
        Assert.Equal(expectedUtcHour, anEvent.Start.Value.ToDateTimeOffset().UtcDateTime.Hour);
    }

    [Fact]
    public void An_unknown_zone_keeps_the_wall_clock_without_an_offset()
    {
        var start = Tok.OneEvent("DTSTART;TZID=Nowhere/Land:20260715T090000").Start!.Value;
        Assert.Equal(IcsDateTimeKind.Zoned, start.Kind);
        Assert.Equal("Nowhere/Land", start.TzId);
        Assert.Null(start.Offset);
        Assert.Equal(new DateTime(2026, 7, 15, 9, 0, 0), start.Value);
        Assert.Null(start.ToDateTimeOffsetOrNull());
        Assert.Equal(new DateTimeOffset(2026, 7, 15, 9, 0, 0, TimeSpan.Zero), start.ToDateTimeOffset());
    }

    [Fact]
    public void Dates_with_a_declared_date_type_ignore_a_time_and_a_zone()
    {
        var start = Tok.OneEvent("DTSTART;VALUE=DATE;TZID=Europe/Paris:20260715", "DTEND;VALUE=DATE:20260716T120000").Start!.Value;
        Assert.True(start.IsDate);
        Assert.True(Tok.OneEvent("DTSTART;VALUE=DATE:20260715T080000").Start!.Value.IsDate);
    }

    [Fact]
    public void Unparseable_dates_are_null()
    {
        var anEvent = Tok.OneEvent("DTSTART:2026-13-45T25:61:61", "DTEND:nope", "CREATED:00000000T000000Z");
        Assert.Null(anEvent.Start);
        Assert.Null(anEvent.End);
        Assert.Null(anEvent.Created);
    }

    [Theory]
    [InlineData("20260105T100000Z", "2026-01-05 10:00")]
    [InlineData("2026-01-05T10:00:00Z", "2026-01-05 10:00")]
    [InlineData("20260105t100000z", "2026-01-05 10:00")]
    [InlineData("20260105T2400", "2026-01-06 00:00")]
    public void Date_time_text_is_read_tolerantly(string value, string expected)
    {
        Assert.Equal(expected, Utc(Tok.OneEvent("DTSTART:" + value).Start));
    }

    [Fact]
    public void Attendees_split_by_role_and_type()
    {
        var anEvent = Tok.OneEvent(
            "ORGANIZER;CN=Olivia;SENT-BY=\"mailto:asst@example.com\":MAILTO:Olivia@Example.COM",
            "ATTENDEE;CN=Req;ROLE=REQ-PARTICIPANT;PARTSTAT=ACCEPTED;RSVP=TRUE:mailto:req@example.com",
            "ATTENDEE:mailto:nocn@example.com",
            "ATTENDEE;ROLE=OPT-PARTICIPANT;PARTSTAT=DECLINED;RSVP=FALSE;CN=Opt:mailto:opt@example.com",
            "ATTENDEE;ROLE=CHAIR;CN=Chair:mailto:chair@example.com",
            "ATTENDEE;ROLE=NON-PARTICIPANT;CN=Watcher:mailto:watch@example.com",
            "ATTENDEE;CUTYPE=ROOM;ROLE=NON-PARTICIPANT:mailto:room@example.com",
            "ATTENDEE;CUTYPE=RESOURCE:mailto:projector@example.com",
            "ATTENDEE;CN=Apple;EMAIL=apple@example.com:/principals/users/apple/",
            "ATTENDEE;CN=Exchange:/O=EXAMPLE/cn=ex1",
            "ATTENDEE;CN=Bare:bare@example.com",
            "ATTENDEE;CN=Jane;DELEGATED-TO=\"mailto:deputy@example.com\":mailto:jane@example.com");
        Assert.Equal("Olivia@Example.COM", anEvent.Organizer!.Email);
        Assert.Equal("Olivia", anEvent.Organizer.CommonName);
        Assert.Equal("asst@example.com", anEvent.Organizer.SentBy);
        Assert.Equal(11, anEvent.Attendees.Count);
        Assert.Equal(["req@example.com", "nocn@example.com", "apple@example.com", "/O=EXAMPLE/cn=ex1", "bare@example.com", "jane@example.com"], anEvent.RequiredAttendees.Select(a => a.Address));
        Assert.Equal(["opt@example.com"], anEvent.OptionalAttendees.Select(a => a.Address));
        Assert.Equal(["chair@example.com"], anEvent.Chairs.Select(a => a.Address));
        Assert.Equal(["watch@example.com"], anEvent.NonParticipants.Select(a => a.Address));
        Assert.Equal(["room@example.com", "projector@example.com"], anEvent.Resources.Select(a => a.Address));
        var req = anEvent.Attendees[0];
        Assert.Equal(ParticipationStatus.Accepted, req.Status);
        Assert.True(req.Rsvp);
        Assert.Equal(AttendeeRole.RequiredParticipant, req.Role);
        Assert.Equal(CalendarUserType.Individual, req.UserType);
        Assert.Equal("Req <req@example.com>", req.ToString());
        Assert.Null(anEvent.Attendees[1].Rsvp);
        Assert.Equal(ParticipationStatus.NeedsAction, anEvent.Attendees[1].Status);
        Assert.Equal(ParticipationStatus.Declined, anEvent.OptionalAttendees[0].Status);
        Assert.False(anEvent.OptionalAttendees[0].Rsvp);
        Assert.Equal(CalendarUserType.Room, anEvent.Resources[0].UserType);
        Assert.Null(anEvent.Attendees.Single(a => a.Address.StartsWith('/') && a.Address.Contains("EXAMPLE", StringComparison.Ordinal)).Email);
        Assert.Equal(["deputy@example.com"], anEvent.Attendees.Single(a => a.CommonName == "Jane").DelegatedTo);
        Assert.Equal("apple@example.com", anEvent.Attendees.Single(a => a.CommonName == "Apple").Email);
        Assert.Equal("/principals/users/apple/", anEvent.Attendees.Single(a => a.CommonName == "Apple").Uri);
    }

    [Fact]
    public void Vcalendar_1_attendee_forms_are_read()
    {
        var calendar = Tok.Load("BEGIN:VCALENDAR\r\nVERSION:1.0\r\nBEGIN:VEVENT\r\nUID:v\r\nATTENDEE;ROLE=ATTENDEE;STATUS=ACCEPTED;RSVP=YES:Hans Muster <hans@example.com>\r\nATTENDEE;EXPECT=FYI:fyi@example.com\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        var attendees = calendar.Events.Single().Attendees;
        Assert.Equal("hans@example.com", attendees[0].Email);
        Assert.Equal("Hans Muster", attendees[0].CommonName);
        Assert.Equal(ParticipationStatus.Accepted, attendees[0].Status);
        Assert.True(attendees[0].Rsvp);
        Assert.Equal(AttendeeRole.OptionalParticipant, attendees[1].Role);
        Assert.True(calendar.IsVCalendar1);
    }

    [Fact]
    public void Inline_and_referenced_attachments_are_read()
    {
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var anEvent = Tok.OneEvent(
            "ATTACH;ENCODING=BASE64;VALUE=BINARY;FMTTYPE=image/png;FILENAME=pixel.png:" + Convert.ToBase64String(png),
            "ATTACH;ENCODING=BASE64;VALUE=BINARY;X-FILENAME=notes.txt:SGVsbG8",
            "ATTACH;ENCODING=BASE64;VALUE=BINARY:SGVsbG8=",
            "ATTACH;FMTTYPE=application/pdf;FILENAME=report.pdf:https://example.com/files/x.pdf",
            "ATTACH:https://example.com/files/some%20document.docx?v=2",
            "ATTACH:ftp://example.com/",
            "ATTACH;FILENAME=data.txt:data:text/plain;base64,SGVsbG8gZGF0YQ==");
        var attachments = anEvent.Attachments;
        Assert.Equal(7, attachments.Count);
        Assert.Equal("pixel.png", attachments[0].Name);
        Assert.Equal("image/png", attachments[0].ContentType);
        Assert.True(attachments[0].IsInline);
        Assert.True(attachments[0].HasData);
        Assert.Equal(png, attachments[0].GetBytes());
        Assert.Equal(png.Length, attachments[0].Size);
        using (var stream = attachments[0].Open())
        {
            Assert.Equal(png.Length, stream.Length);
        }

        Assert.Equal("notes.txt", attachments[1].Name);
        Assert.Equal("Hello", Encoding.ASCII.GetString(attachments[1].GetBytes()));
        Assert.Null(attachments[2].Name);
        Assert.Equal("report.pdf", attachments[3].Name);
        Assert.False(attachments[3].IsInline);
        Assert.False(attachments[3].HasData);
        Assert.Equal("https://example.com/files/x.pdf", attachments[3].Uri);
        Assert.Null(attachments[3].Size);
        Assert.Empty(attachments[3].GetBytes());
        Assert.Equal("some document.docx", attachments[4].Name);
        Assert.Null(attachments[5].Name);
        Assert.Equal("Hello data", Encoding.ASCII.GetString(attachments[6].GetBytes()));
        using var copy = new MemoryStream();
        attachments[0].WriteTo(copy, Tok.Ct);
        Assert.Equal(png, copy.ToArray());
    }

    [Fact]
    public void Alarms_are_typed()
    {
        var anEvent = Tok.OneEvent(
            "BEGIN:VALARM", "ACTION:DISPLAY", "DESCRIPTION:Reminder", "TRIGGER:-PT15M", "END:VALARM",
            "BEGIN:VALARM", "ACTION:AUDIO", "TRIGGER;RELATED=END:PT0S", "REPEAT:2", "DURATION:PT5M", "END:VALARM",
            "BEGIN:VALARM", "ACTION:DISPLAY", "TRIGGER;VALUE=DATE-TIME:20260306T080000Z", "END:VALARM");
        var alarms = anEvent.Alarms;
        Assert.Equal(3, alarms.Count);
        Assert.Equal("DISPLAY", alarms[0].Action);
        Assert.Equal("Reminder", alarms[0].Description);
        Assert.Equal(TimeSpan.FromMinutes(-15), alarms[0].Trigger!.Value.ToTimeSpan());
        Assert.False(alarms[0].TriggerRelativeToEnd);
        Assert.True(alarms[1].TriggerRelativeToEnd);
        Assert.Equal(2, alarms[1].Repeat);
        Assert.Equal(TimeSpan.FromMinutes(5), alarms[1].RepeatInterval!.Value.ToTimeSpan());
        Assert.Null(alarms[2].Trigger);
        Assert.Equal(new DateTime(2026, 3, 6, 8, 0, 0), alarms[2].TriggerTime!.Value.Value);
    }

    [Fact]
    public void Legacy_alarm_properties_become_alarms()
    {
        var calendar = Tok.Load("BEGIN:VCALENDAR\r\nVERSION:1.0\r\nBEGIN:VEVENT\r\nUID:a\r\nDALARM:20260315T074500;PT5M;2;Time to go\r\nAALARM:20260315T074000;;;\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        var alarms = calendar.Events.Single().Alarms;
        Assert.Equal(["DISPLAY", "AUDIO"], alarms.Select(a => a.Action));
        Assert.Equal("Time to go", alarms[0].Description);
        Assert.Equal(2, alarms[0].Repeat);
    }

    [Fact]
    public void Todos_journals_and_free_busy_are_typed()
    {
        var calendar = Tok.Load(Tok.Cal(
            "BEGIN:VTODO", "UID:t", "SUMMARY:todo", "DUE:20260310T170000Z", "PERCENT-COMPLETE:40", "PRIORITY:1", "STATUS:IN-PROCESS", "COMPLETED:20260311T000000Z", "END:VTODO",
            "BEGIN:VJOURNAL", "UID:j", "DTSTART;VALUE=DATE:20260304", "SUMMARY:journal", "DESCRIPTION:Dear diary", "END:VJOURNAL",
            "BEGIN:VFREEBUSY", "UID:f", "DTSTART:20260301T000000Z", "DTEND:20260302T000000Z", "FREEBUSY;FBTYPE=BUSY:20260301T090000Z/20260301T100000Z,20260301T130000Z/PT1H", "FREEBUSY;FBTYPE=BUSY-TENTATIVE:20260301T150000Z/20260301T160000Z", "END:VFREEBUSY"));
        var todo = calendar.Todos.Single();
        Assert.Equal(40, todo.PercentComplete);
        Assert.Equal(1, todo.Priority);
        Assert.Equal(CalendarStatus.InProcess, todo.Status);
        Assert.Equal("2026-03-10 17:00", Utc(todo.Due));
        Assert.Equal("2026-03-11 00:00", Utc(todo.Completed));
        var journal = calendar.Journals.Single();
        Assert.Equal("Dear diary", journal.Description);
        Assert.True(journal.Start!.Value.IsDate);
        var freeBusy = calendar.FreeBusies.Single();
        Assert.Equal(3, freeBusy.Periods.Count);
        Assert.Equal(TimeSpan.FromHours(1), freeBusy.Periods[1].End - freeBusy.Periods[1].Start);
        Assert.Equal("BUSY-TENTATIVE", freeBusy.Periods[2].Type);
        Assert.Same(todo, calendar.Primary);
    }

    [Theory]
    [InlineData("REQUEST", CalendarMethod.Request)]
    [InlineData("cancel", CalendarMethod.Cancel)]
    [InlineData("REPLY", CalendarMethod.Reply)]
    [InlineData("PUBLISH", CalendarMethod.Publish)]
    [InlineData("COUNTER", CalendarMethod.Counter)]
    [InlineData("DECLINECOUNTER", CalendarMethod.DeclineCounter)]
    [InlineData("ADD", CalendarMethod.Add)]
    [InlineData("REFRESH", CalendarMethod.Refresh)]
    [InlineData("WHATEVER", CalendarMethod.Other)]
    public void Method_maps_to_the_itip_enum(string text, CalendarMethod expected)
    {
        var calendar = Tok.Load(Tok.Cal("METHOD:" + text, "BEGIN:VEVENT", "UID:m", "END:VEVENT"));
        Assert.Equal(expected, calendar.Method);
        Assert.Equal(expected, calendar.Events.Single().Method);
        Assert.Equal(text.ToUpperInvariant(), calendar.MethodText);
    }

    [Fact]
    public void Calendar_level_properties_are_exposed()
    {
        var calendar = Tok.Load("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//x//y//EN\r\nCALSCALE:GREGORIAN\r\nX-WR-CALNAME:Name\r\nX-WR-CALDESC:About\r\nX-WR-TIMEZONE:Europe/Zurich\r\nEND:VCALENDAR\r\n");
        Assert.Equal("2.0", calendar.Version);
        Assert.Equal("-//x//y//EN", calendar.ProductId);
        Assert.Equal("GREGORIAN", calendar.CalendarScale);
        Assert.Equal("Name", calendar.Name);
        Assert.Equal("About", calendar.Description);
        Assert.Equal("Europe/Zurich", calendar.DefaultTimeZoneId);
        Assert.Null(calendar.Primary);
        Assert.False(calendar.IsVCalendar1);
    }

    [Fact]
    public void Vcalendar_1_tz_offset_applies_to_zoneless_times()
    {
        var calendar = Tok.Load("BEGIN:VCALENDAR\r\nVERSION:1.0\r\nTZ:-05:00\r\nBEGIN:VEVENT\r\nUID:z\r\nDTSTART:20260315T080000\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        Assert.Equal(TimeSpan.FromHours(-5), calendar.LegacyUtcOffset);
        Assert.Equal("2026-03-15 13:00", Utc(calendar.Events.Single().Start));
    }

    [Fact]
    public void Html_description_comes_from_x_alt_desc()
    {
        var anEvent = Tok.OneEvent("DESCRIPTION:plain", "X-ALT-DESC;FMTTYPE=text/html:<p>rich\\, text</p>");
        Assert.Equal("<p>rich, text</p>", anEvent.HtmlDescription);
        Assert.Null(Tok.OneEvent("DESCRIPTION:plain").HtmlDescription);
    }

    [Fact]
    public void Components_can_be_built_edited_and_removed()
    {
        var calendar = new Calendar { MethodText = "request" };
        var anEvent = new CalendarEvent
        {
            Uid = "built@example.com",
            Summary = "Built; with, specials\nand a line",
            Description = "desc",
            Location = "Here",
            Start = new IcsDateTime(new DateTime(2026, 5, 1, 9, 0, 0), IcsDateTimeKind.Utc),
            End = new IcsDateTime(new DateTime(2026, 5, 1, 10, 0, 0), IcsDateTimeKind.Utc),
            Organizer = new CalendarAddress("boss@example.com", "The Boss"),
        };
        anEvent.AddAttendee(new CalendarAddress("a@example.com", "A") { Role = AttendeeRole.OptionalParticipant, Status = ParticipationStatus.Accepted, Rsvp = true });
        anEvent.AddAttachment(new CalendarAttachment("hello.txt", Encoding.ASCII.GetBytes("hi"), "text/plain"));
        anEvent.AddAttachment(new CalendarAttachment(new Uri("https://example.com/f.pdf"), contentType: "application/pdf"));
        anEvent.SetRecurrence(RecurrenceRule.Parse("FREQ=DAILY;COUNT=3"));
        calendar.Events.Add(anEvent);
        Assert.Same(calendar, anEvent.Calendar);
        Assert.Equal(CalendarMethod.Request, anEvent.Method);

        var again = Tok.Load(calendar.ToBytes(Tok.Ct)).Events.Single();
        Assert.Equal("Built; with, specials\nand a line", again.Summary);
        Assert.Equal("boss@example.com", again.Organizer!.Email);
        Assert.Equal("The Boss", again.Organizer.CommonName);
        Assert.Equal(AttendeeRole.OptionalParticipant, again.Attendees.Single().Role);
        Assert.Equal(ParticipationStatus.Accepted, again.Attendees.Single().Status);
        Assert.True(again.Attendees.Single().Rsvp);
        Assert.Equal("hello.txt", again.Attachments[0].Name);
        Assert.Equal("hi", Encoding.ASCII.GetString(again.Attachments[0].GetBytes()));
        Assert.Equal("f.pdf", again.Attachments[1].Name);
        Assert.Equal(3, again.RecurrenceRules.Single().Count);

        calendar.Events.Remove(anEvent);
        Assert.Null(anEvent.Calendar);
        Assert.DoesNotContain(calendar.Raw.Children, c => c.Name == "VEVENT");
    }

    [Fact]
    public void Ics_date_time_helpers_behave()
    {
        var a = new IcsDateTime(new DateTime(2026, 1, 1, 12, 0, 0), IcsDateTimeKind.Utc);
        var b = new IcsDateTime(new DateTime(2026, 1, 1, 13, 0, 0), IcsDateTimeKind.Zoned, "X", TimeSpan.FromHours(2));
        Assert.True(a.CompareTo(b) > 0);
        Assert.Equal("20260101T120000Z", a.ToIcsString());
        Assert.Equal("2026-01-01T13:00:00+02:00", b.ToString());
        Assert.Equal(a, new IcsDateTime(new DateTime(2026, 1, 1, 12, 0, 0), IcsDateTimeKind.Utc));
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 7, 0, 0, TimeSpan.FromHours(-4)), a.ToLocalTime(TimeZoneInfo.CreateCustomTimeZone("x", TimeSpan.FromHours(-4), "x", "x")).AddHours(-1));
        Assert.True(IcsDuration.TryParse("-P1DT2H30M", out var duration));
        Assert.Equal(-(26 * 60 + 30), duration.ToTimeSpan().TotalMinutes);
        Assert.Equal("P14D", IcsDuration.FromTimeSpan(TimeSpan.FromDays(14)).ToString());
        Assert.False(IcsDuration.TryParse("P1H", out _));
        Assert.False(IcsDuration.TryParse("garbage", out _));
        Assert.True(IcsDuration.TryParse("P2W", out var weeks));
        Assert.Equal("P2W", weeks.ToString());
    }
}
