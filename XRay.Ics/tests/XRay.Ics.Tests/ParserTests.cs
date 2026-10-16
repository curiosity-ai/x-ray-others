using System.Text;

namespace XRay.Ics.Tests;

public sealed class ParserTests
{
    [Fact]
    public void Lines_unfold_with_crlf_lf_and_cr_and_tab_or_space()
    {
        var calendar = Tok.Load("BEGIN:VCALENDAR\r\nVERSION:2.0\nBEGIN:VEVENT\rUID:u1\r\nSUMMARY:one\r\n two\n\tthree\r four\r\nEND:VEVENT\r\nEND:VCALENDAR");
        Assert.Equal("onetwothreefour", calendar.Events.Single().Summary);
    }

    [Fact]
    public void Blank_lines_before_a_continuation_are_skipped()
    {
        var calendar = Tok.Load("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID\r\n\r\n :abc\r\nEND:VEVENT\r\nEND:VCALENDAR");
        Assert.Equal("abc", calendar.Events.Single().Uid);
    }

    [Fact]
    public void Names_are_case_insensitive_and_groups_are_kept()
    {
        var calendar = Tok.Load("begin:vcalendar\r\nbegin:vevent\r\nuid:x\r\nsummary:Hi\r\nitem1.X-Thing;Type=a:v\r\nend:vevent\r\nend:vcalendar\r\n");
        var anEvent = calendar.Events.Single();
        Assert.Equal("Hi", anEvent.Summary);
        var grouped = anEvent.Raw.Get("X-THING");
        Assert.NotNull(grouped);
        Assert.Equal("item1", grouped.Group);
        Assert.Equal("a", grouped.GetParameter("type"));
    }

    [Fact]
    public void Quoted_parameters_keep_separators_and_split_on_unquoted_commas()
    {
        var property = Tok.OneEvent("X-P;A=\"x;y:z,w\";TYPE=WORK,HOME,\"a,b\":value").Raw.Get("X-P")!;
        Assert.Equal("x;y:z,w", property.GetParameter("A"));
        Assert.Equal(["WORK", "HOME", "a,b"], property.GetParameterValues("TYPE"));
        Assert.Equal("value", property.Value);
    }

    [Fact]
    public void Caret_encoding_in_parameters_is_decoded()
    {
        var property = Tok.OneEvent("X-P;N=\"a^'b^^c^nd\":v").Raw.Get("X-P")!;
        Assert.Equal("a\"b^c\nd", property.GetParameter("N"));
    }

    [Fact]
    public void Backslash_escaped_separators_in_unquoted_parameters_are_literal()
    {
        var anEvent = Tok.OneEvent("ORGANIZER;CN=Doe\\, John\\; Jr:mailto:john@example.com");
        Assert.Equal("Doe, John; Jr", anEvent.Organizer!.CommonName);
        Assert.Equal("john@example.com", anEvent.Organizer.Email);
    }

    [Fact]
    public void Text_values_unescape_the_rfc_escapes_and_keep_other_backslashes()
    {
        var anEvent = Tok.OneEvent(@"SUMMARY:a\;b\,c\\d\ne\Nf", @"DESCRIPTION:C:\Users\x and \:colon");
        Assert.Equal("a;b,c\\d\ne\nf", anEvent.Summary);
        Assert.Equal("C:\\Users\\x and :colon", anEvent.Description);
    }

    [Fact]
    public void Categories_split_on_unescaped_commas_only()
    {
        var anEvent = Tok.OneEvent(@"CATEGORIES:Work,Big\, Loud,Plan", "CATEGORIES:Extra");
        Assert.Equal(["Work", "Big, Loud", "Plan", "Extra"], anEvent.Categories);
    }

    [Fact]
    public void Empty_values_and_values_with_colons_survive()
    {
        var anEvent = Tok.OneEvent("X-EMPTY:", "X-URL:https://example.com:8080/a?b=c:d", "URL:http://x/y");
        Assert.Equal(string.Empty, anEvent.Raw.Get("X-EMPTY")!.Value);
        Assert.Equal("https://example.com:8080/a?b=c:d", anEvent.Raw.Get("X-URL")!.Value);
        Assert.Equal("http://x/y", anEvent.Url);
    }

    [Fact]
    public void Quoted_printable_with_charset_and_soft_breaks_is_decoded()
    {
        var text = "BEGIN:VCALENDAR\r\nVERSION:1.0\r\nBEGIN:VEVENT\r\nUID:q\r\nSUMMARY;CHARSET=UTF-8;ENCODING=QUOTED-PRINTABLE:Caf=C3=A9 =\r\nau lait\r\nDESCRIPTION;ENCODING=QUOTED-PRINTABLE;CHARSET=ISO-8859-1:a=0D=0Ab=E9\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n";
        var anEvent = Tok.Load(text).Events.Single();
        Assert.Equal("Café au lait", anEvent.Summary);
        Assert.Equal("a\nbé", anEvent.Description);
    }

    [Fact]
    public void Bare_vcalendar_parameters_become_type_or_encoding()
    {
        var anEvent = Tok.OneEvent("X-TEL;WORK;PREF:123", "X-Q;QUOTED-PRINTABLE:a=41");
        Assert.Equal(["WORK", "PREF"], anEvent.Raw.Get("X-TEL")!.GetParameterValues("TYPE"));
        Assert.Equal("aA", anEvent.Raw.Get("X-Q")!.Value);
    }

    public static TheoryData<string, byte[]> EncodedVariants()
    {
        var text = Tok.Cal("BEGIN:VEVENT", "UID:enc", "SUMMARY:Caf\u00e9 \u20ac \u65e5\u672c \U0001F600", "END:VEVENT");
        return new TheoryData<string, byte[]>
        {
            { "utf8", Encoding.UTF8.GetBytes(text) },
            { "utf8-bom", [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text)] },
            { "utf16le-bom", [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(text)] },
            { "utf16be-bom", [.. Encoding.BigEndianUnicode.GetPreamble(), .. Encoding.BigEndianUnicode.GetBytes(text)] },
            { "utf16le", Encoding.Unicode.GetBytes(text) },
            { "utf16be", Encoding.BigEndianUnicode.GetBytes(text) },
            { "utf32le-bom", [.. new UTF32Encoding(false, true).GetPreamble(), .. new UTF32Encoding(false, true).GetBytes(text)] },
        };
    }

    [Theory]
    [MemberData(nameof(EncodedVariants))]
    public void Unicode_encodings_are_detected(string name, byte[] data)
    {
        Assert.NotNull(name);
        Assert.Equal("Caf\u00e9 \u20ac \u65e5\u672c \U0001F600", Tok.Load(data).Events.Single().Summary);
    }

    [Fact]
    public void Legacy_bytes_fall_back_to_windows_1252_and_per_line_charset_applies()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var head = Encoding.ASCII.GetBytes("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:l\r\nSUMMARY:Caf");
        var body = new List<byte>(head) { 0xE9 };
        body.AddRange(Encoding.ASCII.GetBytes(" \u0080\r\nLOCATION;CHARSET=windows-1251:"));
        body.AddRange(Encoding.GetEncoding(1251).GetBytes("\u041c\u043e\u0441\u043a\u0432\u0430"));
        body.AddRange(Encoding.ASCII.GetBytes("\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n"));
        var anEvent = Tok.Load([.. body]).Events.Single();
        Assert.Equal("Caf\u00e9 ?", anEvent.Summary!.Replace('\u0080', '?').Replace('\u20ac', '?'));
        Assert.Equal("\u041c\u043e\u0441\u043a\u0432\u0430", anEvent.Location);
    }

    [Fact]
    public void Fallback_encoding_option_is_used_for_legacy_bytes()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var data = new List<byte>(Encoding.ASCII.GetBytes("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:l\r\nSUMMARY:"));
        data.AddRange(Encoding.GetEncoding(1251).GetBytes("\u041f\u0440\u0438\u0432\u0435\u0442"));
        data.AddRange(Encoding.ASCII.GetBytes("\r\nEND:VEVENT\r\nEND:VCALENDAR\r\n"));
        var calendar = Calendar.Load([.. data], new IcsParseOptions { FallbackEncoding = Encoding.GetEncoding(1251) }, Tok.Ct);
        Assert.Equal("\u041f\u0440\u0438\u0432\u0435\u0442", calendar.Events.Single().Summary);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello world")]
    [InlineData("BEGIN:VCARD\r\nFN:A\r\nEND:VCARD\r\n")]
    [InlineData("BEGIN:VCALENDAR")]
    [InlineData("\0\0\0\0")]
    public void Non_calendar_input_raises_IcsFormatException(string text)
    {
        // "BEGIN:VCALENDAR" alone is a calendar with nothing in it, so it must load.
        if (text == "BEGIN:VCALENDAR")
        {
            Assert.Empty(Tok.Load(text).Events);
            return;
        }

        Assert.Throws<IcsFormatException>(() => Tok.Load(text));
        Assert.Throws<IcsFormatException>(() => Calendar.Load(Encoding.UTF8.GetBytes(text), null, Tok.Ct));
        Assert.Throws<IcsFormatException>(() => Calendar.Load(new MemoryStream(Encoding.UTF8.GetBytes(text)), null, Tok.Ct));
    }

    [Fact]
    public void Damaged_input_is_read_as_far_as_it_goes()
    {
        var calendar = Tok.Load("BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nUID:a\r\nSUMMARY:kept\r\nnot a line\r\nEND:VTODO\r\nEND:VEVENT\r\nBEGIN:VEVENT\r\nUID:b\r\nSUMMARY:also kept");
        Assert.Equal(["kept", "also kept"], calendar.Events.Select(e => e.Summary));
        Assert.NotEmpty(calendar.Warnings);
    }

    [Fact]
    public void Components_without_a_calendar_wrapper_are_wrapped()
    {
        var calendar = Tok.Load("BEGIN:VEVENT\r\nUID:x\r\nSUMMARY:bare\r\nEND:VEVENT\r\n");
        Assert.Equal("bare", calendar.Events.Single().Summary);
    }

    [Fact]
    public void Several_calendars_merge_and_LoadAll_keeps_them_apart()
    {
        var text = Tok.Cal("BEGIN:VEVENT", "UID:1", "END:VEVENT") + Tok.Cal("METHOD:CANCEL", "BEGIN:VEVENT", "UID:2", "END:VEVENT");
        Assert.Equal(["1", "2"], Tok.Load(text).Events.Select(e => e.Uid));
        var all = Calendar.LoadAll(new MemoryStream(Encoding.UTF8.GetBytes(text)), null, Tok.Ct);
        Assert.Equal(2, all.Count);
        Assert.Equal(CalendarMethod.Cancel, all[1].Method);
        Assert.Equal(CalendarMethod.None, all[0].Method);
    }

    [Fact]
    public void Unknown_components_and_x_properties_are_preserved_in_the_raw_tree()
    {
        var calendar = Tok.Load(Tok.Cal("X-WR-CALNAME:Mine", "BEGIN:X-CUSTOM", "X-A:1", "BEGIN:VEVENT", "UID:n", "END:VEVENT", "END:X-CUSTOM"));
        Assert.Equal("Mine", calendar.Name);
        Assert.Contains(calendar.Raw.Children, c => c.Name == "X-CUSTOM");
        Assert.Empty(calendar.Events);
    }

    [Fact]
    public void Input_over_the_size_limit_raises_IcsFormatException()
    {
        var data = Encoding.UTF8.GetBytes(Tok.Cal("BEGIN:VEVENT", "UID:1", "END:VEVENT"));
        var options = new IcsParseOptions { MaxInputBytes = 20 };
        Assert.Throws<IcsFormatException>(() => Calendar.Load(data, options, Tok.Ct));
        Assert.Throws<IcsFormatException>(() => Calendar.Load(new MemoryStream(data), options, Tok.Ct));
    }

    [Fact]
    public void A_line_over_the_length_limit_is_dropped_with_a_warning()
    {
        var calendar = Calendar.Parse(Tok.Cal("BEGIN:VEVENT", "UID:1", "SUMMARY:" + new string('x', 500), "LOCATION:ok", "END:VEVENT"), new IcsParseOptions { MaxLineLength = 100 }, Tok.Ct);
        var anEvent = calendar.Events.Single();
        Assert.Null(anEvent.Summary);
        Assert.Equal("ok", anEvent.Location);
        Assert.Contains(calendar.Warnings, w => w.Contains("maximum line length", StringComparison.Ordinal));
    }

    [Fact]
    public void Nesting_past_the_depth_limit_is_skipped_and_the_rest_is_read()
    {
        var lines = new List<string> { "BEGIN:VCALENDAR" };
        lines.AddRange(Enumerable.Repeat("BEGIN:X-DEEP", 100));
        lines.AddRange(Enumerable.Repeat("END:X-DEEP", 100));
        lines.AddRange(["BEGIN:VEVENT", "UID:after", "END:VEVENT", "END:VCALENDAR"]);
        var calendar = Tok.Load(string.Join("\r\n", lines));
        Assert.Equal("after", calendar.Events.Single().Uid);
    }

    [Fact]
    public void Component_and_property_count_limits_apply()
    {
        var lines = new List<string> { "BEGIN:VCALENDAR" };
        for (var i = 0; i < 50; i++)
        {
            lines.AddRange(["BEGIN:VEVENT", "UID:" + i, "X-A:1", "X-B:2", "X-C:3", "END:VEVENT"]);
        }

        lines.Add("END:VCALENDAR");
        var calendar = Calendar.Parse(string.Join("\r\n", lines), new IcsParseOptions { MaxComponents = 10, MaxPropertiesPerComponent = 2 }, Tok.Ct);
        Assert.Equal(9, calendar.Events.Count);
        Assert.All(calendar.Events, e => Assert.True(e.Raw.Properties.Count <= 2));
    }

    [Fact]
    public void Stream_input_matches_byte_input()
    {
        var bytes = Encoding.UTF8.GetBytes(Tok.Cal("BEGIN:VEVENT", "UID:s", "SUMMARY:stream", "END:VEVENT"));
        using var stream = new MemoryStream(bytes);
        Assert.Equal("stream", Calendar.Load(stream, null, Tok.Ct).Events.Single().Summary);
    }

    [Fact]
    public void Very_long_inline_lines_load_quickly()
    {
        var payload = Convert.ToBase64String(new byte[3_000_000]);
        var text = Tok.Cal("BEGIN:VEVENT", "UID:big", "ATTACH;ENCODING=BASE64;VALUE=BINARY;FILENAME=zero.bin:" + payload, "END:VEVENT");
        var started = DateTime.UtcNow;
        var attachment = Tok.Load(text).Events.Single().Attachments.Single();
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(20));
        Assert.Equal(3_000_000, attachment.Size);
    }
}
