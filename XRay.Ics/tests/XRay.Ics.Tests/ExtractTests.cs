using System.Text;

namespace XRay.Ics.Tests;

public sealed class ExtractTests
{
    private const string Meeting =
        "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//t//EN\r\nMETHOD:REQUEST\r\n"
        + "BEGIN:VEVENT\r\nUID:meet@example.com\r\nDTSTAMP:20260101T080000Z\r\nCREATED:20251230T101500Z\r\n"
        + "DTSTART:20260105T100000Z\r\nDTEND:20260105T113000Z\r\nSUMMARY:Kick<off> & \"plan\"\r\nLOCATION:Room 4\\, Building A\r\n"
        + "DESCRIPTION:Line one\\n<b>not bold</b> & more\\n\\nLast line\\n\r\n"
        + "ORGANIZER;CN=Olivia Organizer:mailto:Olivia@Example.com\r\n"
        + "ATTENDEE;CN=Adam;ROLE=REQ-PARTICIPANT:mailto:adam@example.com\r\n"
        + "ATTENDEE;CN=Chair Carl;ROLE=CHAIR:mailto:carl@example.com\r\n"
        + "ATTENDEE;ROLE=NON-PARTICIPANT:mailto:observer@example.com\r\n"
        + "ATTENDEE;CN=Opt Olga;ROLE=OPT-PARTICIPANT:mailto:olga@example.com\r\n"
        + "ATTENDEE;CN=Room 1;CUTYPE=ROOM:mailto:room1@example.com\r\n"
        + "ATTACH;ENCODING=BASE64;VALUE=BINARY;FILENAME=hello.txt:SGVsbG8gd29ybGQ=\r\n"
        + "ATTACH;ENCODING=BASE64;VALUE=BINARY:SGVsbG8=\r\n"
        + "ATTACH;FILENAME=report.pdf:https://example.com/report.pdf\r\n"
        + "END:VEVENT\r\nEND:VCALENDAR\r\n";

    [Fact]
    public void Fields_match_what_an_appointment_consumer_reads()
    {
        var result = IcsExtractor.Extract(Meeting, null, Tok.Ct);
        Assert.Equal(new DateTimeOffset(2025, 12, 30, 10, 15, 0, TimeSpan.Zero), result.CreatedDate);
        Assert.Equal("Olivia Organizer", result.Organizer!.CommonName);
        Assert.Equal("Olivia@Example.com", result.Organizer.Address);
        Assert.Equal(["adam@example.com", "carl@example.com", "observer@example.com"], result.Attendees.Select(a => a.Address));
        Assert.Equal(["olga@example.com"], result.OptionalAttendees.Select(a => a.Address));
        Assert.Equal(["room1@example.com"], result.Resources.Select(a => a.Address));
        Assert.Equal("Kick<off> & \"plan\"", result.Summary);
        Assert.Equal("Line one\n<b>not bold</b> & more\n\nLast line\n", result.Description);
        Assert.Equal("Room 4, Building A", result.Location);
        Assert.Equal(CalendarMethod.Request, result.Method);
        Assert.Equal(3, result.Attachments.Count);
        Assert.Equal(["hello.txt", "report.pdf"], result.Attachments.Where(a => !string.IsNullOrWhiteSpace(a.Name)).Select(a => a.Name));
        var hello = result.Attachments[0];
        using var stream = hello.Open();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        Assert.Equal("Hello world", reader.ReadToEnd());
        Assert.Equal("2026-01-05T10:00:00Z", IcsManifest.Format(result.Start));
        Assert.Equal("2026-01-05T11:30:00Z", IcsManifest.Format(result.End));
    }

    [Fact]
    public void Every_input_shape_gives_the_same_result()
    {
        var bytes = Encoding.UTF8.GetBytes(Meeting);
        var path = Path.Combine(Path.GetTempPath(), "xray-ics-" + Guid.NewGuid().ToString("N") + ".ics");
        File.WriteAllBytes(path, bytes);
        try
        {
            var expected = IcsExtractor.Extract(Meeting, null, Tok.Ct).ToHtml(null, Tok.Ct);
            Assert.Equal(expected, IcsExtractor.Extract(bytes, null, Tok.Ct).ToHtml(null, Tok.Ct));
            Assert.Equal(expected, IcsExtractor.Extract(new MemoryStream(bytes), null, Tok.Ct).ToHtml(null, Tok.Ct));
            Assert.Equal(expected, IcsExtractor.ExtractFile(path, null, Tok.Ct).ToHtml(null, Tok.Ct));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Html_is_self_contained_escaped_and_deterministic()
    {
        var result = IcsExtractor.Extract(Meeting, null, Tok.Ct);
        var html = result.ToHtml(null, Tok.Ct);
        Assert.Equal(html, result.ToHtml(null, Tok.Ct));
        Assert.StartsWith("<!DOCTYPE html>\n<html lang=\"en\">\n<head>\n<meta charset=\"utf-8\">\n<title>Kick&lt;off&gt; &amp; &quot;plan&quot;</title>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href=", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<h1 class=\"subject\">Kick&lt;off&gt; &amp; &quot;plan&quot;</h1>", html, StringComparison.Ordinal);
        Assert.Contains("<tr><th>When</th><td>Monday, 5 January 2026 10:00 - 11:30 (UTC)</td></tr>", html, StringComparison.Ordinal);
        Assert.Contains("<tr><th>Where</th><td>Room 4, Building A</td></tr>", html, StringComparison.Ordinal);
        Assert.Contains("<tr><th>Organizer</th><td>Olivia Organizer &lt;Olivia@Example.com&gt;</td></tr>", html, StringComparison.Ordinal);
        Assert.Contains("<tr><th>Required</th><td>Adam &lt;adam@example.com&gt;<br>Chair Carl &lt;carl@example.com&gt;<br>observer@example.com</td></tr>", html, StringComparison.Ordinal);
        Assert.Contains("<tr><th>Optional</th><td>Opt Olga &lt;olga@example.com&gt;</td></tr>", html, StringComparison.Ordinal);
        Assert.Contains("<tr><th>Resources</th><td>Room 1 &lt;room1@example.com&gt;</td></tr>", html, StringComparison.Ordinal);
        Assert.Contains("<div class=\"description\">Line one<br>\n&lt;b&gt;not bold&lt;/b&gt; &amp; more<br>\n<br>\nLast line</div>", html, StringComparison.Ordinal);
        Assert.Contains("<li>hello.txt (11 B)</li>", html, StringComparison.Ordinal);
        Assert.Contains("<li>report.pdf</li>", html, StringComparison.Ordinal);
        Assert.Contains("<li>(unnamed) (5 B)</li>", html, StringComparison.Ordinal);
        Assert.EndsWith("</body>\n</html>\n", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Html_streams_as_utf8_without_a_bom()
    {
        var result = IcsExtractor.Extract(Tok.Cal("BEGIN:VEVENT", "UID:u", "SUMMARY:café 日本", "END:VEVENT"), null, Tok.Ct);
        using var stream = new MemoryStream();
        result.ToHtml(stream, null, Tok.Ct);
        var bytes = stream.ToArray();
        Assert.Equal(result.ToHtmlBytes(null, Tok.Ct), bytes);
        Assert.False(bytes[0] == 0xEF);
        Assert.Contains("café 日本", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void Local_time_zone_option_converts_timed_values()
    {
        var result = IcsExtractor.Extract(Meeting, null, Tok.Ct);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        var html = result.ToHtml(new IcsRenderOptions { DisplayTimeZone = zone }, Tok.Ct);
        Assert.Contains("Monday, 5 January 2026 11:00 - 12:30 (UTC+01:00, Europe/Berlin)", html, StringComparison.Ordinal);
        var extractOptions = new IcsExtractOptions { Render = new IcsRenderOptions { DisplayTimeZone = zone } };
        Assert.Equal(html, IcsExtractor.Extract(Meeting, extractOptions, Tok.Ct).ToHtml(null, Tok.Ct));
    }

    [Fact]
    public void When_line_covers_zoned_floating_all_day_and_multi_day_values()
    {
        string When(params string[] lines) => IcsExtractor.Extract(Tok.Cal(["BEGIN:VEVENT", "UID:w", .. lines, "END:VEVENT"]), null, Tok.Ct).ToPlainText(null, Tok.Ct).Split('\n').Single(l => l.StartsWith("When: ", StringComparison.Ordinal));
        Assert.Equal("When: Tuesday, 14 July 2026 09:00 - 10:00 (UTC-04:00, America/New_York)", When("DTSTART;TZID=America/New_York:20260714T090000", "DTEND;TZID=America/New_York:20260714T100000"));
        Assert.Equal("When: Tuesday, 10 March 2026 14:30 - 15:30", When("DTSTART:20260310T143000", "DTEND:20260310T153000"));
        Assert.Equal("When: Friday, 20 March 2026 (all day)", When("DTSTART;VALUE=DATE:20260320", "DTEND;VALUE=DATE:20260321"));
        Assert.Equal("When: Monday, 23 March 2026 - Wednesday, 25 March 2026 (all day)", When("DTSTART;VALUE=DATE:20260323", "DTEND;VALUE=DATE:20260326"));
        Assert.Equal("When: Monday, 5 January 2026 22:00 - Tuesday, 6 January 2026 02:00 (UTC)", When("DTSTART:20260105T220000Z", "DTEND:20260106T020000Z"));
        Assert.Equal("When: Monday, 5 January 2026 10:00 (UTC)", When("DTSTART:20260105T100000Z"));
        Assert.Equal("When: Monday, 5 January 2026 09:00 - 10:00 (Nowhere/Land)", When("DTSTART;TZID=Nowhere/Land:20260105T090000", "DTEND;TZID=Nowhere/Land:20260105T100000"));
    }

    [Fact]
    public void Recurrence_is_summarised()
    {
        var text = IcsExtractor.Extract(Tok.Cal("BEGIN:VEVENT", "UID:r", "DTSTART:20260105T100000Z", "DTEND:20260105T110000Z", "RRULE:FREQ=WEEKLY;BYDAY=MO,WE;COUNT=6", "EXDATE:20260107T100000Z", "RDATE:20260130T100000Z", "END:VEVENT"), null, Tok.Ct).ToPlainText(null, Tok.Ct);
        Assert.Contains("Recurrence: Occurs every week on Monday and Wednesday, starting 5 January 2026, 6 times.", text, StringComparison.Ordinal);
        Assert.Contains("Additional dates: 1.", text, StringComparison.Ordinal);
        Assert.Contains("Excluded dates: 1.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Plain_text_and_markdown_cover_all_components_by_default_and_html_only_the_first()
    {
        var result = IcsExtractor.Extract(Tok.Cal(
            "BEGIN:VEVENT", "UID:1", "SUMMARY:First *event*", "DTSTART:20260105T100000Z", "DESCRIPTION:body one", "END:VEVENT",
            "BEGIN:VEVENT", "UID:2", "SUMMARY:Second", "DTSTART:20260106T100000Z", "DESCRIPTION:body two", "END:VEVENT",
            "BEGIN:VTODO", "UID:3", "SUMMARY:A todo", "DUE:20260110T170000Z", "PERCENT-COMPLETE:25", "END:VTODO",
            "BEGIN:VJOURNAL", "UID:4", "SUMMARY:A journal", "DTSTART;VALUE=DATE:20260111", "DESCRIPTION:Dear diary", "END:VJOURNAL"), null, Tok.Ct);
        var plain = result.ToPlainText(null, Tok.Ct);
        Assert.Contains("First *event*", plain, StringComparison.Ordinal);
        Assert.Contains("body two", plain, StringComparison.Ordinal);
        Assert.Contains("A todo\nDue: Saturday, 10 January 2026 17:00 (UTC)\nProgress: 25%", plain, StringComparison.Ordinal);
        Assert.Contains("A journal\nDate: Sunday, 11 January 2026 (all day)", plain, StringComparison.Ordinal);
        var markdown = result.ToMarkdown(null, Tok.Ct);
        Assert.Contains("# First \\*event\\*", markdown, StringComparison.Ordinal);
        Assert.Contains("# Second", markdown, StringComparison.Ordinal);
        var html = result.ToHtml(null, Tok.Ct);
        Assert.Contains("First *event*", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Second", html, StringComparison.Ordinal);
        var all = result.ToHtml(new IcsRenderOptions { AllComponents = true }, Tok.Ct);
        Assert.Contains("Second", all, StringComparison.Ordinal);
        Assert.Contains("A todo", all, StringComparison.Ordinal);
        var firstOnly = result.ToPlainText(new IcsRenderOptions { AllComponents = false }, Tok.Ct);
        Assert.DoesNotContain("Second", firstOnly, StringComparison.Ordinal);
    }

    [Fact]
    public void Cancelled_and_tentative_items_say_so()
    {
        var cancelled = IcsExtractor.Extract(Tok.Cal("METHOD:CANCEL", "BEGIN:VEVENT", "UID:c", "SUMMARY:Gone", "DTSTART:20260105T100000Z", "END:VEVENT"), null, Tok.Ct).ToPlainText(null, Tok.Ct);
        Assert.Contains("This item has been cancelled.", cancelled, StringComparison.Ordinal);
        var tentative = IcsExtractor.Extract(Tok.Cal("BEGIN:VEVENT", "UID:c", "SUMMARY:Maybe", "STATUS:TENTATIVE", "DTSTART:20260105T100000Z", "END:VEVENT"), null, Tok.Ct).ToPlainText(null, Tok.Ct);
        Assert.Contains("Tentative.", tentative, StringComparison.Ordinal);
    }

    [Fact]
    public void Calendars_without_components_and_items_without_a_subject_render()
    {
        var empty = IcsExtractor.Extract("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nX-WR-CALNAME:Empty one\r\nEND:VCALENDAR\r\n", null, Tok.Ct);
        Assert.Null(empty.Component);
        Assert.Null(empty.Summary);
        Assert.Null(empty.CreatedDate);
        Assert.Empty(empty.Attendees);
        Assert.Contains("<title>Empty one</title>", empty.ToHtml(null, Tok.Ct), StringComparison.Ordinal);
        Assert.Equal(string.Empty, empty.ToPlainText(null, Tok.Ct));
        Assert.Contains("(no subject)", IcsExtractor.Extract(Tok.Cal("BEGIN:VEVENT", "UID:n", "END:VEVENT"), null, Tok.Ct).ToHtml(null, Tok.Ct), StringComparison.Ordinal);
    }

    [Fact]
    public void Created_date_falls_back_to_the_stamp()
    {
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.Zero), IcsExtractor.Extract(Tok.Cal("BEGIN:VEVENT", "UID:n", "DTSTAMP:20260101T080000Z", "END:VEVENT"), null, Tok.Ct).CreatedDate);
    }

    [Fact]
    public void Attachment_list_can_be_switched_off()
    {
        var html = IcsExtractor.Extract(Meeting, null, Tok.Ct).ToHtml(new IcsRenderOptions { IncludeAttachments = false }, Tok.Ct);
        Assert.DoesNotContain("hello.txt", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Extraction_of_non_calendar_input_raises_IcsFormatException()
    {
        Assert.Throws<IcsFormatException>(() => IcsExtractor.Extract("not a calendar", null, Tok.Ct));
        Assert.Throws<IcsFormatException>(() => IcsExtractor.Extract([1, 2, 3], null, Tok.Ct));
        Assert.Throws<IcsFormatException>(() => IcsExtractor.Extract(new MemoryStream([]), null, Tok.Ct));
    }

    [Fact]
    public void Options_reach_the_reader()
    {
        var options = new IcsExtractOptions { Parse = new IcsParseOptions { MaxInputBytes = 10 } };
        Assert.Throws<IcsFormatException>(() => IcsExtractor.Extract(Meeting, options, Tok.Ct));
    }

    [Fact]
    public void Real_fixtures_render_into_the_documented_sections()
    {
        var path = TestDocuments.RequireFile("ics/xray-ics/outlook-meeting-request.ics");
        var result = IcsExtractor.ExtractFile(path, null, Tok.Ct);
        Assert.Equal("Weekly sync", result.Summary);
        Assert.Equal(CalendarMethod.Request, result.Method);
        Assert.Equal(["ana@contoso.example"], result.Attendees.Select(a => a.Address));
        Assert.Equal(["bob@contoso.example"], result.OptionalAttendees.Select(a => a.Address));
        Assert.Equal("<p>Please join the weekly sync.</p><ul><li>Status</li><li>Risks</li></ul>", result.HtmlDescription!.Replace("<html><body>", string.Empty, StringComparison.Ordinal).Replace("</body></html>", string.Empty, StringComparison.Ordinal));
        var html = result.ToHtml(null, Tok.Ct);
        Assert.Contains("Monday, 12 January 2026 10:00 - 11:00 (UTC+01:00, W. Europe Standard Time)", html, StringComparison.Ordinal);
    }
}
