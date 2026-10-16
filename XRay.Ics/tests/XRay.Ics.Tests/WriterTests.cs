using System.Text;

namespace XRay.Ics.Tests;

public sealed class WriterTests
{
    private static string[] PhysicalLines(byte[] data) => Encoding.UTF8.GetString(data).Split("\r\n");

    [Fact]
    public void Output_is_crlf_utf8_without_bom_and_has_required_properties()
    {
        var calendar = new Calendar();
        calendar.Events.Add(new CalendarEvent { Uid = "w@example.com", Summary = "café" });
        var data = calendar.ToBytes(Tok.Ct);
        Assert.False(data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB);
        var text = Encoding.UTF8.GetString(data);
        Assert.StartsWith("BEGIN:VCALENDAR\r\nPRODID:", text, StringComparison.Ordinal);
        Assert.Contains("\r\nVERSION:2.0\r\n", text, StringComparison.Ordinal);
        Assert.EndsWith("END:VEVENT\r\nEND:VCALENDAR\r\n", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", text.Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("SUMMARY:café", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Properties_missing_from_a_loaded_calendar_are_added_without_changing_it()
    {
        var calendar = Tok.Load("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:x\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n");
        var text = calendar.ToIcsString(Tok.Ct);
        Assert.Contains("VERSION:2.0", text, StringComparison.Ordinal);
        Assert.Contains("PRODID:", text, StringComparison.Ordinal);
        Assert.Null(calendar.Version);
    }

    [Theory]
    [InlineData(300, "a")]
    [InlineData(100, "é")]
    [InlineData(100, "€")]
    [InlineData(60, "\U0001F600")]
    public void Lines_fold_at_75_octets_and_never_split_a_character(int count, string unit)
    {
        var value = string.Concat(Enumerable.Repeat(unit, count));
        var calendar = new Calendar();
        calendar.Events.Add(new CalendarEvent { Uid = "fold", Summary = value });
        var data = calendar.ToBytes(Tok.Ct);
        var lines = PhysicalLines(data);
        var strict = new UTF8Encoding(false, true);
        var folded = 0;
        foreach (var line in lines)
        {
            Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, line);
            _ = strict.GetString(Encoding.UTF8.GetBytes(line));
            if (line.StartsWith(' '))
            {
                folded++;
            }
        }

        Assert.True(folded > 0);
        Assert.Equal(value, Tok.Load(data).Events.Single().Summary);
    }

    [Fact]
    public void Text_is_escaped_and_round_trips()
    {
        var calendar = new Calendar();
        var text = "semi; comma, back\\slash\nline two\r\nline three";
        calendar.Events.Add(new CalendarEvent { Uid = "esc", Summary = text, Location = "a,b", Description = text });
        var raw = calendar.ToIcsString(Tok.Ct);
        Assert.Contains("SUMMARY:semi\\; comma\\, back\\\\slash\\nline two\\nline three", raw, StringComparison.Ordinal);
        var again = Tok.Load(raw).Events.Single();
        Assert.Equal("semi; comma, back\\slash\nline two\nline three", again.Summary);
        Assert.Equal("a,b", again.Location);
    }

    [Fact]
    public void Parameters_are_quoted_and_caret_encoded_when_needed()
    {
        var property = new IcsProperty("X-TEST", "v");
        property.Parameters.Add(new IcsParameter("PLAIN", "abc"));
        property.Parameters.Add(new IcsParameter("QUOTED", "a;b:c,d e"));
        property.Parameters.Add(new IcsParameter("CARET", "q\"u^o\nte"));
        property.Parameters.Add(new IcsParameter("MULTI", "x", "y z"));
        var calendar = new Calendar();
        var anEvent = new CalendarEvent { Uid = "p" };
        anEvent.Raw.Properties.Add(property);
        calendar.Events.Add(anEvent);
        var raw = calendar.ToIcsString(Tok.Ct);
        Assert.Contains("X-TEST;PLAIN=abc;QUOTED=\"a;b:c,d e\";CARET=\"q^'u^^o^nte\";MULTI=x,\"y z\":v", raw, StringComparison.Ordinal);
        var again = Tok.Load(raw).Events.Single().Raw.Get("X-TEST")!;
        Assert.Equal("a;b:c,d e", again.GetParameter("QUOTED"));
        Assert.Equal("q\"u^o\nte", again.GetParameter("CARET"));
        Assert.Equal(["x", "y z"], again.GetParameterValues("MULTI"));
    }

    [Fact]
    public void Control_characters_and_line_breaks_in_raw_values_cannot_break_the_output()
    {
        var anEvent = new CalendarEvent { Uid = "c" };
        anEvent.Raw.Properties.Add(new IcsProperty("X-BAD", "one\r\ntwo\nthree\0four"));
        var calendar = new Calendar();
        calendar.Events.Add(anEvent);
        var again = Tok.Load(calendar.ToBytes(Tok.Ct)).Events.Single();
        Assert.Equal("one\\ntwo\\nthreefour", again.Raw.Get("X-BAD")!.Value);
        Assert.Equal("c", again.Uid);
    }

    [Fact]
    public void Loaded_calendars_keep_x_properties_unknown_components_groups_and_order()
    {
        var source = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//a//b//EN\r\nX-WR-CALNAME:Name\r\nBEGIN:X-ODD\r\nX-ONE:1\r\nEND:X-ODD\r\nBEGIN:VEVENT\r\nUID:k\r\nX-B;P=1:b\r\nitem1.X-A:a\r\nSUMMARY:s\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var calendar = Tok.Load(source);
        Assert.Equal(source, calendar.ToIcsString(Tok.Ct));
    }

    [Fact]
    public void Stream_and_byte_outputs_are_identical_and_the_stream_is_left_open()
    {
        var calendar = Tok.Load(Tok.Cal("BEGIN:VEVENT", "UID:s", "SUMMARY:x", "END:VEVENT"));
        using var stream = new MemoryStream();
        calendar.Save(stream, Tok.Ct);
        Assert.True(stream.CanWrite);
        Assert.Equal(calendar.ToBytes(Tok.Ct), stream.ToArray());
        var path = Path.Combine(Path.GetTempPath(), "xray-ics-" + Guid.NewGuid().ToString("N") + ".ics");
        try
        {
            calendar.Save(path, Tok.Ct);
            Assert.Equal(stream.ToArray(), File.ReadAllBytes(path));
            Assert.Equal("x", Calendar.LoadFile(path, null, Tok.Ct).Events.Single().Summary);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Large_inline_attachments_round_trip()
    {
        var bytes = new byte[500_000];
        new Random(7).NextBytes(bytes);
        var calendar = new Calendar();
        var anEvent = new CalendarEvent { Uid = "big" };
        anEvent.AddAttachment(new CalendarAttachment("random.bin", bytes, "application/octet-stream"));
        calendar.Events.Add(anEvent);
        using var stream = new MemoryStream();
        calendar.Save(stream, Tok.Ct);
        Assert.All(PhysicalLines(stream.ToArray()), line => Assert.True(line.Length <= 75));
        var again = Calendar.Load(stream.ToArray(), null, Tok.Ct).Events.Single().Attachments.Single();
        Assert.Equal("random.bin", again.Name);
        Assert.Equal(bytes, again.GetBytes());
    }

    [Fact]
    public void A_vcalendar_1_file_saved_back_drops_its_quoted_printable_markers()
    {
        var source = "BEGIN:VCALENDAR\r\nVERSION:1.0\r\nBEGIN:VEVENT\r\nUID:q\r\nSUMMARY;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:Caf=C3=A9, =\r\nbar\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var saved = Tok.Load(source).ToIcsString(Tok.Ct);
        Assert.Contains("SUMMARY:Café\\, bar", saved, StringComparison.Ordinal);
        Assert.DoesNotContain("QUOTED-PRINTABLE", saved, StringComparison.Ordinal);
        Assert.Equal("Café, bar", Tok.Load(saved).Events.Single().Summary);
    }
}
