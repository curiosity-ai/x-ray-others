using System.Text;
using XRay.Email.Msg;
using XRay.Email.Msg.Mime;

namespace XRay.Email.Tests;

/// <summary>Header, address, date, transfer-encoding and multipart parsing rules.</summary>
public sealed class MimeParsingTests
{
    private static MapiMessage Eml(string text) => MapiMessage.LoadFromEml(Encoding.UTF8.GetBytes(text.Replace("\n", "\r\n", StringComparison.Ordinal)));

    [Fact]
    public void Adjacent_encoded_words_are_joined_before_decoding()
    {
        // "日本語" split after its fourth UTF-8 byte, in the middle of the second character.
        var bytes = Encoding.UTF8.GetBytes("日本語");
        var value = $"=?UTF-8?B?{Convert.ToBase64String(bytes[..4])}?=\r\n =?UTF-8?B?{Convert.ToBase64String(bytes[4..])}?=";
        Assert.Equal("日本語", HeaderParser.DecodeEncodedWords(value.Replace("\r\n", string.Empty, StringComparison.Ordinal)));
    }

    [Fact]
    public void Whitespace_between_an_encoded_word_and_text_is_kept()
    {
        Assert.Equal("Über die Grüße und Süßes", HeaderParser.DecodeEncodedWords("=?ISO-8859-1?Q?=DCber_die_Gr=FC=DFe?= und =?ISO-8859-1?Q?S=FC=DFes?="));
        Assert.Equal("ab", HeaderParser.DecodeEncodedWords("=?utf-8?q?a?= =?utf-8?q?b?="));
        Assert.Equal("a b", HeaderParser.DecodeEncodedWords("=?utf-8?q?a?= =?iso-8859-1?q?_b?="));
    }

    [Fact]
    public void Rfc2231_parameters_are_decoded()
    {
        var (main, parameters) = HeaderParser.ParseStructuredHeaderValue(
            "attachment; filename*0*=utf-8''%E9%95%B7%E3%81%84; filename*1*=%E5%90%8D.txt; size=12", null);
        Assert.Equal("attachment", main);
        Assert.Equal("長い名.txt", parameters["filename"]);
        Assert.Equal("12", parameters["size"]);

        var (_, extended) = HeaderParser.ParseStructuredHeaderValue("attachment; filename*=iso-8859-1'de'%FCber.pdf; filename=\"fallback.pdf\"", null);
        Assert.Equal("über.pdf", extended["filename"]);

        var (_, quoted) = HeaderParser.ParseStructuredHeaderValue("text/plain; name=\"a; \\\"b\\\".txt\"", null);
        Assert.Equal("a; \"b\".txt", quoted["name"]);
    }

    [Fact]
    public void Address_lists_handle_groups_comments_and_quoting()
    {
        var mailboxes = AddressParser.ParseList(
            "undisclosed-recipients:;, Team: alice@example.com, \"Bob B.\" <bob@example.com>;, carol@example.com (Carol C.), " +
            "\"Smith, John \\\"JJ\\\"\" <john@example.com>, =?utf-8?q?Doe=2C_John?= <doe@example.com>");
        Assert.Equal(
            ["alice@example.com", "bob@example.com", "carol@example.com", "john@example.com", "doe@example.com"],
            mailboxes.Select(item => item.Address));
        Assert.Equal([null, "Bob B.", "Carol C.", "Smith, John \"JJ\"", "Doe, John"], mailboxes.Select(item => item.DisplayName));
    }

    [Theory]
    [InlineData("Tue, 02 Jan 2024 03:04:05 +0000", "2024-01-02T03:04:05Z")]
    [InlineData("5 Jan 99 13:04 EST (Eastern Standard Time)", "1999-01-05T18:04:00Z")]
    [InlineData("Wed, 20 Sep 1995 00:19:00 (EDT) -0400", "1995-09-20T04:19:00Z")]
    [InlineData("Mon, 3 Feb 2020 9:05:00 GMT", "2020-02-03T09:05:00Z")]
    [InlineData("Thu, 8 Mar 2005 17:40:36 EDT", "2005-03-08T21:40:36Z")]
    [InlineData("Sat, 13 Jan 2024 13:13:13 +0900 (JST)", "2024-01-13T04:13:13Z")]
    [InlineData("Sun, 15 Mar 2026 10:30:00 +01:00", "2026-03-15T09:30:00Z")]
    public void Dates_in_current_and_obsolete_syntax_are_parsed(string value, string expected)
    {
        var parsed = HeaderParser.ParseDateHeader(value);
        Assert.NotNull(parsed);
        Assert.Equal(expected, parsed.Value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void Quoted_printable_drops_trailing_whitespace_and_soft_breaks()
    {
        var decoded = TransferEncodingDecoder.DecodeQuotedPrintable("a  \r\nb=  \r\nc=3D=\nd =20\r\n"u8.ToArray());
        Assert.Equal("a\r\nbc=d  \r\n", Encoding.ASCII.GetString(decoded));
    }

    [Fact]
    public void Base64_tolerates_missing_padding_and_junk()
    {
        Assert.Equal("hello"u8.ToArray(), TransferEncodingDecoder.DecodeBase64Lenient("aGVs\r\nbG8"));
        Assert.Equal("hello"u8.ToArray(), TransferEncodingDecoder.DecodeBase64Lenient("aGVs bG8=\r\n=junk"));
        Assert.Equal("hell"u8.ToArray(), TransferEncodingDecoder.DecodeBase64Lenient("aGVsbA>"));
    }

    [Fact]
    public void Multipart_without_closing_boundary_keeps_the_last_part()
    {
        var message = Eml("""
From: a@example.com
Subject: x
Content-Type: multipart/mixed; boundary=b

--b
Content-Type: text/plain

first
--b

no headers here
--b
Content-Type: application/octet-stream
Content-Disposition: attachment; filename=tail.bin

tail
""");
        Assert.Equal("first", message.Body);
        Assert.Equal(2, message.Attachments.Count);
        Assert.Null(message.Attachments[0].Filename);
        Assert.Equal("no headers here", Encoding.UTF8.GetString(message.Attachments[0].Data));
        Assert.Equal("tail.bin", message.Attachments[1].Filename);
    }

    [Fact]
    public void Only_the_line_break_before_a_boundary_is_removed()
    {
        var message = Eml("""
From: a@example.com
Content-Type: multipart/mixed; boundary=b

--b
Content-Type: text/plain

body


--b--
""");
        Assert.Equal("body\r\n\r\n", message.Body);
    }

    [Fact]
    public void Folds_without_leading_whitespace_and_a_byte_order_mark_are_tolerated()
    {
        var text = "﻿From: a@example.com\r\nSubject: =?utf-8?q?one?=\r\n=?utf-8?q?_two?=\r\nTo: b@example.com\r\n\r\nbody\r\n";
        var message = MapiMessage.LoadFromEml(Encoding.UTF8.GetBytes(text));
        Assert.Equal("one two", message.Subject);
        Assert.Equal("a@example.com", message.SenderEmailAddress);
        Assert.Equal("b@example.com", Assert.Single(message.Recipients).EmailAddress);
    }

    [Fact]
    public void Eml_load_keeps_the_header_block_as_transport_headers()
    {
        var message = Eml("""
From: a@example.com
X-Custom: value
Subject: x

body
""");
        Assert.Contains("X-Custom: value", message.TransportMessageHeaders);
    }

    [Fact]
    public void Deeply_nested_multiparts_and_messages_do_not_overflow_the_stack()
    {
        var builder = new StringBuilder("From: a@example.com\r\nSubject: deep\r\n");
        for (var level = 0; level < 2000; level++)
        {
            builder.Append($"Content-Type: multipart/mixed; boundary=\"b{level}\"\r\n\r\n--b{level}\r\n");
        }

        builder.Append("Content-Type: text/plain\r\n\r\nleaf\r\n");
        var message = MapiMessage.LoadFromEml(Encoding.ASCII.GetBytes(builder.ToString()));
        Assert.Equal("deep", message.Subject);

        var nested = new StringBuilder();
        for (var level = 0; level < 500; level++)
        {
            nested.Append($"Subject: level {level}\r\nContent-Type: message/rfc822\r\n\r\n");
        }

        nested.Append("Subject: leaf\r\n\r\nleaf body\r\n");
        var outer = MapiMessage.LoadFromEml(Encoding.ASCII.GetBytes(nested.ToString()));
        Assert.Equal("level 0", outer.Subject);
        Assert.NotNull(outer.Save());
    }

    [Fact]
    public void Written_headers_are_ascii_and_round_trip_non_ascii_text()
    {
        var message = MapiMessage.Create("Grüße, 日本語 and a long subject that has to be folded across several header lines to stay readable", "Body");
        message.SenderName = "Zoë, Ångström";
        message.SenderEmailAddress = "zoe@example.com";
        message.AddRecipient("doe@example.com", "Doe, John");
        message.AddRecipient("taro@example.jp", "山田 太郎");
        message.AddAttachment("résumé; final.pdf", [1, 2, 3], "application/pdf");
        message.AddAttachment("非常に長いファイル名の添付ファイルはRFC2231の継続行に分割されます.txt", "x\ny\n"u8.ToArray(), "text/plain");

        var eml = message.SaveToEml();
        var text = Encoding.UTF8.GetString(eml);
        var headerBlock = text[..text.IndexOf("\r\n\r\n", StringComparison.Ordinal)];
        Assert.True(eml.All(b => b < 0x80), "the written message is 7-bit");
        Assert.All(headerBlock.Split("\r\n"), line => Assert.True(line.Length <= 78, $"header line longer than 78: {line}"));

        message.MessageDeliveryTime = new DateTime(2026, 3, 15, 10, 30, 0, DateTimeKind.Utc);
        Assert.Contains("\r\nDate: Sun, 15 Mar 2026 10:30:00 +0000\r\n", Encoding.ASCII.GetString(message.SaveToEml()));

        var reloaded = MapiMessage.LoadFromEml(eml);
        Assert.Equal(message.Subject, reloaded.Subject);
        Assert.Equal("Zoë, Ångström", reloaded.SenderName);
        Assert.Equal(["Doe, John", "山田 太郎"], reloaded.Recipients.Select(item => item.DisplayName));
        Assert.Equal(message.Attachments.Select(item => item.Filename), reloaded.Attachments.Select(item => item.Filename));
        Assert.Equal("x\ny\n"u8.ToArray(), reloaded.Attachments[1].Data);
    }

    [Fact]
    public void Nested_messages_written_to_eml_keep_their_own_structure()
    {
        var inner = MapiMessage.Create("Inner", "Inner body");
        inner.SenderEmailAddress = "inner@example.com";
        inner.AddAttachment("a.txt", "a"u8.ToArray(), "text/plain");
        inner.AddAttachment("b.txt", "b"u8.ToArray(), "text/plain");
        var outer = MapiMessage.Create("Outer", "Outer body");
        outer.SenderEmailAddress = "outer@example.com";
        outer.AddEmbeddedMessageAttachment(inner, "inner.eml");
        outer.AddAttachment("c.txt", "c"u8.ToArray(), "text/plain");

        var eml = Encoding.ASCII.GetString(outer.SaveToEml());
        // RFC 2046 5.2.1: an embedded message is never base64 encoded.
        Assert.Matches(@"Content-Type: message/rfc822[^\r]*\r\nContent-Transfer-Encoding: 7bit", eml);
        var reloaded = MapiMessage.LoadFromEml(Encoding.ASCII.GetBytes(eml));
        Assert.Equal(2, reloaded.Attachments.Count);
        var embedded = reloaded.Attachments[0].EmbeddedMessage;
        Assert.NotNull(embedded);
        Assert.Equal("Inner", embedded.Subject);
        Assert.Equal(["a.txt", "b.txt"], embedded.Attachments.Select(item => item.Filename));
        Assert.Equal("c.txt", reloaded.Attachments[1].Filename);
    }
}
