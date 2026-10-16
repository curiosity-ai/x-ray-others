using System.Text;
using System.Text.RegularExpressions;
using XRay.Email.Msg;

namespace XRay.Email.Tests;

/// <summary>The Outlook-style HTML rendering of a message: header block, body selection, sanitising, inlining.</summary>
public sealed class PreviewHtmlTests
{
    private static string Render(MapiMessage message, MailHtmlOptions? options = null) =>
        message.ToHtml(options, TestContext.Current.CancellationToken);

    [Fact]
    public void Header_block_lists_each_field_in_outlook_order()
    {
        var message = TestMessages.Basic("Quarterly <plan> & budget", "Body text");
        message.AddRecipient("carol@example.com", "Carol", MapiMessage.RecipientTypeCc);
        message.AddRecipient("dave@example.com", "Dave", MapiMessage.RecipientTypeBcc);
        message.AddAttachment("plan.pdf", new byte[2048], "application/pdf");

        var html = Render(message);

        Assert.Contains("<th>From:</th><td>Alice Example &lt;alice@example.com&gt;</td>", html);
        Assert.Contains("<th>Sent:</th><td>Tuesday, March 5, 2024 2:40 PM UTC</td>", html);
        Assert.Contains("<th>To:</th><td>Bob Example &lt;bob@example.com&gt;</td>", html);
        Assert.Contains("<th>Cc:</th><td>Carol &lt;carol@example.com&gt;</td>", html);
        Assert.Contains("<th>Bcc:</th><td>Dave &lt;dave@example.com&gt;</td>", html);
        Assert.Contains("<th>Subject:</th><td>Quarterly &lt;plan&gt; &amp; budget</td>", html);
        Assert.Contains("<th>Attachments:</th><td><span class=\"xray-mail-attachment\">plan.pdf (2 KB)</span></td>", html);

        var order = new[] { "From:", "Sent:", "To:", "Cc:", "Bcc:", "Subject:", "Attachments:" }.Select(label => html.IndexOf($"<th>{label}</th>", StringComparison.Ordinal)).ToArray();
        Assert.All(order, index => Assert.True(index >= 0));
        Assert.Equal(order.Order(), order);
    }

    [Fact]
    public void Bcc_cc_and_attachment_rows_are_absent_when_there_is_nothing_to_show()
    {
        var html = Render(TestMessages.Basic());

        Assert.DoesNotContain("<th>Bcc:</th>", html);
        Assert.DoesNotContain("<th>Cc:</th>", html);
        Assert.DoesNotContain("<th>Attachments:</th>", html);
    }

    [Fact]
    public void Sender_without_a_display_name_and_name_equal_to_address_show_the_address_once()
    {
        var message = TestMessages.Basic();
        message.SenderName = "alice@example.com";
        message.Recipients.Clear();
        message.AddRecipient("only@example.com", null);

        var html = Render(message);

        Assert.Contains("<th>From:</th><td>alice@example.com</td>", html);
        Assert.Contains("<th>To:</th><td>only@example.com</td>", html);
    }

    [Fact]
    public void Display_line_stands_in_for_a_missing_recipient_table()
    {
        var message = TestMessages.Basic();
        message.Recipients.Clear();
        message.SetProperty(0x0E04, (ushort)PropertyTypeCode.PtypString, "Zed; Yan");

        Assert.Contains("<th>To:</th><td>Zed; Yan</td>", Render(message));
    }

    [Fact]
    public void Sent_time_follows_the_requested_zone_and_format()
    {
        var plusTwo = TimeZoneInfo.CreateCustomTimeZone("plus2", TimeSpan.FromHours(2), "plus2", "plus2");
        var message = TestMessages.Basic();

        Assert.Contains("Tuesday, March 5, 2024 4:40 PM UTC+02:00", Render(message, new MailHtmlOptions { TimeZone = plusTwo }));
        Assert.Contains("<td>2024-03-05 14:40</td>", Render(message, new MailHtmlOptions { DateFormat = "yyyy-MM-dd HH:mm", ShowTimeZone = false }));
    }

    [Fact]
    public void Sent_falls_back_from_client_submit_time_to_submit_time_to_delivery_time()
    {
        var message = TestMessages.Basic();
        message.ClientSubmitTime = null;
        message.SubmitTime = new DateTime(2024, 1, 2, 3, 4, 0, DateTimeKind.Utc);
        Assert.Contains("Tuesday, January 2, 2024 3:04 AM UTC", Render(message));

        message.SubmitTime = null;
        message.MessageDeliveryTime = new DateTime(2024, 2, 3, 4, 5, 0, DateTimeKind.Utc);
        Assert.Contains("Saturday, February 3, 2024 4:05 AM UTC", Render(message));

        message.MessageDeliveryTime = null;
        Assert.DoesNotContain("<th>Sent:</th>", Render(message));
    }

    [Fact]
    public void Subject_prefix_control_characters_are_not_shown()
    {
        var message = TestMessages.Basic("\u0001\u0001RE: status");

        var html = Render(message);

        Assert.Contains("<td>RE: status</td>", html);
        Assert.Contains("<title>RE: status</title>", html);
    }

    [Fact]
    public void Document_declares_utf8_once_and_titles_itself_with_the_subject()
    {
        var html = Render(TestMessages.WithHtml("<html><head><meta charset=\"windows-1252\"><title>other</title></head><body><p>x</p></body></html>", "Grüße ✓"));

        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Single(Regex.Matches(html, "<meta charset=", RegexOptions.IgnoreCase));
        Assert.Contains("<meta charset=\"utf-8\">", html);
        Assert.Contains("<title>Grüße ✓</title>", html);
        Assert.DoesNotContain("other", html);
    }

    [Fact]
    public void Written_stream_is_utf8_without_a_byte_order_mark_and_equals_the_string()
    {
        var message = TestMessages.WithHtml("<p>Привет, 世界</p>", "Grüße ✓");
        using var stream = new MemoryStream();

        message.WriteHtml(stream, null, TestContext.Current.CancellationToken);

        var bytes = stream.ToArray();
        Assert.False(bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]));
        Assert.Equal(Render(message), Encoding.UTF8.GetString(bytes));
        Assert.Contains("Привет, 世界", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Output_is_deterministic_and_does_not_depend_on_the_source_format()
    {
        var message = TestMessages.WithHtml("<p>Same</p>");
        message.AddAttachment("a.txt", "x"u8.ToArray(), "text/plain");

        var first = Render(message);

        Assert.Equal(first, Render(message));
        Assert.Equal(first, Render(TestMessages.ThroughMsg(message)));
    }

    [Fact]
    public void Html_body_is_lifted_out_of_its_document_with_styles_and_body_attributes_kept()
    {
        var message = TestMessages.WithHtml("<!DOCTYPE html><html><head><title>t</title><style>p.a{color:red}</style></head><body bgcolor=\"#eeeeee\" text=\"navy\"><p class=\"a\">Hello</p></body></html>");

        var html = Render(message);

        Assert.Contains("<style>p.a{color:red}</style>", html);
        Assert.Contains("<p class=\"a\">Hello</p>", html);
        Assert.Contains("style=\"background-color:#eeeeee;color:navy;\"", html);
        Assert.DoesNotContain("<body bgcolor", html);
        Assert.Single(Regex.Matches(html, "<body>"));
    }

    [Fact]
    public void Html_is_preferred_over_rtf_and_plain_text()
    {
        var message = TestMessages.WithRtf("{\\rtf1\\ansi From rtf.\\par}");
        message.Body = "from text";
        message.HtmlBody = "<p>from html</p>";

        var html = Render(message);

        Assert.Contains("from html", html);
        Assert.DoesNotContain("from rtf", html);
        Assert.DoesNotContain("from text", html);
    }

    [Theory]
    [InlineData("<script>alert(1)</script><p>kept</p>", "<script", "alert(1)")]
    [InlineData("<p onclick=\"alert(1)\" onmouseover=alert(2) ONLOAD='x'>t</p>", "onclick", "onmouseover")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>", "javascript", "href=")]
    [InlineData("<a href=\" JaVa&#x09;ScRiPt:alert(1)\">x</a>", "script:", "href=")]
    [InlineData("<a href=\"vbscript:msgbox(1)\">x</a>", "vbscript", "href=")]
    [InlineData("<a href=\"data:text/html;base64,PHNjcmlwdD4=\">x</a>", "data:text", "href=")]
    [InlineData("<iframe src=\"http://example.com\"></iframe><object data=\"x.swf\"></object><embed src=\"x.swf\"><applet code=\"x\"></applet>", "<iframe", "<object")]
    [InlineData("<p style=\"width:expression(alert(1))\">x</p>", "expression", "alert")]
    [InlineData("<style>@import url(http://example.com/x.css); p{behavior:url(x.htc);-moz-binding:url(x)}</style><p>x</p>", "@import", "behavior")]
    [InlineData("<meta http-equiv=\"refresh\" content=\"0;url=http://example.com\"><base href=\"http://example.com/\"><p>x</p>", "refresh", "<base")]
    [InlineData("<svg onload=\"alert(1)\"><a xlink:href=\"javascript:alert(2)\">x</a></svg>", "onload", "javascript")]
    [InlineData("<img src=x onerror=alert(1)>", "onerror", "alert")]
    [InlineData("<p style=\"background:url('javascript:alert(1)')\">x</p>", "javascript", "alert")]
    [InlineData("<noscript><img src=\"http://example.com/p.gif\"></noscript>", "noscript", "p.gif")]
    public void Active_content_is_removed_by_default(string body, string forbidden1, string forbidden2)
    {
        var html = Render(TestMessages.WithHtml("<html><body>" + body + "</body></html>"));
        var bodyPart = html[html.IndexOf("xray-mail-body", StringComparison.Ordinal)..];

        Assert.DoesNotContain(forbidden1, bodyPart, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(forbidden2, bodyPart, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scripting_markup_can_be_left_alone_when_sanitising_is_off()
    {
        var body = "<script>alert(1)</script><p onclick=\"x()\"><a href=\"javascript:y()\">z</a></p>";

        var html = Render(TestMessages.WithHtml("<html><body>" + body + "</body></html>"), new MailHtmlOptions { SanitizeHtml = false });

        Assert.Contains("<script>", html);
        Assert.Contains("onclick=\"x()\"", html);
        Assert.Contains("href=\"javascript:y()\"", html);
    }

    [Fact]
    public void Ordinary_markup_links_and_images_survive_sanitising()
    {
        const string Body = "<p style=\"color:red\">Hi <a href=\"https://example.com/a?b=1&amp;c=2\" title=\"t\">link</a> <a href=\"mailto:x@example.com\">m</a></p><img src=\"https://example.com/i.png\" width=\"5\"><table><tr><td colspan=\"2\">c</td></tr></table>";

        var html = Render(TestMessages.WithHtml("<html><body>" + Body + "</body></html>"));

        Assert.Contains("<p style=\"color:red\">", html);
        Assert.Contains("<a href=\"https://example.com/a?b=1&amp;c=2\" title=\"t\">link</a>", html);
        Assert.Contains("href=\"mailto:x@example.com\"", html);
        Assert.Contains("<img src=\"https://example.com/i.png\" width=\"5\">", html);
        Assert.Contains("<td colspan=\"2\">c</td>", html);
    }

    [Fact]
    public void Conditional_comments_and_malformed_markup_do_not_break_the_page()
    {
        var html = Render(TestMessages.WithHtml("<html><body><!--[if mso]><p>mso only</p><![endif]--><p>fine <b>bold <i>x</p> < 3 <img src=\"unterminated><p>tail</p></body></html>"));

        Assert.DoesNotContain("mso only", html);
        Assert.Contains("fine", html);
    }

    [Fact]
    public void Cid_images_become_data_uris_by_content_id_with_escapes_and_in_css()
    {
        var message = TestMessages.WithHtml("<p><img src=\"cid:logo@example.com\"> <img src=\"cid:banner%40example.com\"> <img src=\"CID:LOGO@EXAMPLE.COM\"></p><div style=\"background:url(cid:logo@example.com)\"></div><img src=\"cid:missing@example.com\">");
        message.AddAttachment("logo.png", TestMessages.Png, "image/png", "<logo@example.com>");
        message.AddAttachment("banner.gif", TestMessages.Gif, null, "banner@example.com");

        var html = Render(message);

        var png = "data:image/png;base64," + Convert.ToBase64String(TestMessages.Png);
        Assert.Equal(3, Regex.Matches(html, Regex.Escape(png)).Count);
        Assert.Contains("data:image/gif;base64," + Convert.ToBase64String(TestMessages.Gif), html);
        Assert.Contains("src=\"cid:missing@example.com\"", html);
        Assert.Contains("url(" + png + ")", html);
        Assert.DoesNotContain("logo.png", html[html.IndexOf("xray-mail-body", StringComparison.Ordinal)..]);
        Assert.DoesNotContain("<th>Attachments:</th>", html);
    }

    [Fact]
    public void Content_location_references_are_embedded_too()
    {
        var message = TestMessages.WithHtml("<img src=\"images/pixel.png\">");
        message.AddAttachment("pixel.png", TestMessages.Png, "image/png").ContentLocation = "images/pixel.png";

        var html = Render(message);

        Assert.Contains(TestMessages.PngDataUri, html);
        Assert.DoesNotContain("<th>Attachments:</th>", html);
    }

    [Fact]
    public void Embedding_can_be_switched_off_and_then_the_image_is_listed()
    {
        var message = TestMessages.WithHtml("<img src=\"cid:logo@example.com\">");
        message.AddAttachment("logo.png", TestMessages.Png, "image/png", "logo@example.com");

        var html = Render(message, new MailHtmlOptions { EmbedInlineImages = false });

        Assert.Contains("src=\"cid:logo@example.com\"", html);
        Assert.DoesNotContain("data:image", html);
        Assert.Contains("logo.png (", html);
    }

    [Fact]
    public void Images_over_the_size_limit_and_non_images_keep_their_reference()
    {
        var message = TestMessages.WithHtml("<img src=\"cid:big\"><img src=\"cid:doc\">");
        message.AddAttachment("big.png", TestMessages.Png, "image/png", "big");
        message.AddAttachment("doc.pdf", [1, 2, 3], "application/pdf", "doc");

        var html = Render(message, new MailHtmlOptions { MaxInlineImageBytes = 10 });

        Assert.Contains("src=\"cid:big\"", html);
        Assert.Contains("src=\"cid:doc\"", html);
    }

    [Fact]
    public void Image_type_is_taken_from_the_bytes_when_the_declared_type_is_wrong_or_missing()
    {
        var message = TestMessages.WithHtml("<img src=\"cid:a\">");
        message.AddAttachment("a.dat", TestMessages.Png, "application/octet-stream", "a");

        Assert.Contains(TestMessages.PngDataUri, Render(message));
    }

    [Fact]
    public void Plain_text_body_keeps_its_whitespace_and_is_escaped()
    {
        var message = TestMessages.Basic("Plain", "Line one\r\n  indented <b>not bold</b> & more\r\n\tTabbed");

        var html = Render(message);

        Assert.Contains("class=\"xray-mail-text\">Line one\r\n  indented &lt;b&gt;not bold&lt;/b&gt; &amp; more\r\n\tTabbed</div>", html);
        Assert.Contains("white-space:pre-wrap", html);
        Assert.DoesNotContain("<b>not bold", html);
    }

    [Fact]
    public void Plain_text_urls_are_linked_only_when_asked()
    {
        var message = TestMessages.Basic("Plain", "See https://example.com/a?x=1&y=2, and www.example.org/docs. Mail a@b.example.");

        Assert.DoesNotContain("<a ", Render(message));

        var linked = Render(message, new MailHtmlOptions { LinkifyPlainText = true });
        Assert.Contains("<a href=\"https://example.com/a?x=1&amp;y=2\" rel=\"noopener noreferrer\">https://example.com/a?x=1&amp;y=2</a>,", linked);
        Assert.Contains("<a href=\"http://www.example.org/docs\" rel=\"noopener noreferrer\">www.example.org/docs</a>.", linked);
        Assert.Equal(2, Regex.Matches(linked, "<a ").Count);
    }

    [Fact]
    public void Message_without_a_body_still_has_a_header_and_an_empty_body()
    {
        var html = Render(TestMessages.Basic("Empty", null));

        Assert.Contains("<th>Subject:</th><td>Empty</td>", html);
        Assert.Matches("<div class=\"xray-mail-body\">\\s*</div>", html);
    }

    [Fact]
    public void Header_and_document_wrapper_can_be_left_out()
    {
        var message = TestMessages.WithHtml("<p>x</p>");

        var fragment = Render(message, new MailHtmlOptions { FullDocument = false, IncludeHeader = false });

        Assert.DoesNotContain("<html", fragment);
        Assert.DoesNotContain("<!DOCTYPE", fragment);
        Assert.DoesNotContain("xray-mail-header\">", fragment);
        Assert.Contains("<p>x</p>", fragment);
    }

    [Fact]
    public void Rtf_body_is_rendered_when_there_is_no_html()
    {
        var message = TestMessages.WithRtf("{\\rtf1\\ansi{\\fonttbl{\\f0 Arial;}}\\f0\\fs24 Hello \\b world\\b0 !\\par Second line\\par}");

        var html = Render(message);

        Assert.Contains("Hello </span>", html);
        Assert.Contains("font-weight:bold", html);
        Assert.Contains(">world</span>", html);
        Assert.Contains("Second line", html);
        Assert.Equal(MessageBodyKind.Rtf, message.GetPreferredBodyKind(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Encapsulated_html_rtf_yields_the_original_html()
    {
        const string Rtf = "{\\rtf1\\ansi\\ansicpg1252\\fromhtml1 {\\*\\htmltag64 <html><body>}{\\*\\htmltag64 <p class=\"x\">}\\htmlrtf {\\htmlrtf0 Caf\\'e9 {\\*\\htmltag64 &amp;}\\htmlrtf \\'26\\htmlrtf0  tea{\\*\\htmltag72 </p>}\\htmlrtf \\par}\\htmlrtf0 {\\*\\htmltag241 </body></html>}}";

        var html = Render(TestMessages.WithRtf(Rtf));

        Assert.Contains("<p class=\"x\">Café &amp; tea</p>", html);
        Assert.DoesNotContain("htmlrtf", html);
    }

    [Fact]
    public void Rtf_that_only_wraps_plain_text_defers_to_the_plain_text_body()
    {
        var message = TestMessages.WithRtf("{\\rtf1\\ansi\\fromtext {\\fonttbl{\\f0 Courier;}}\\f0 wrapped text\\par}");
        message.Body = "exact   spacing\r\nkept";

        var html = Render(message);

        Assert.Contains("exact   spacing\r\nkept", html);
        Assert.Equal(MessageBodyKind.PlainText, message.GetPreferredBodyKind(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Eml_roundtrip_keeps_inline_images_through_content_id()
    {
        var message = TestMessages.WithHtml("<img src=\"cid:logo@example.com\">");
        message.AddAttachment("logo.png", TestMessages.Png, "image/png", "logo@example.com");

        Assert.Contains(TestMessages.PngDataUri, Render(TestMessages.ThroughEml(message)));
        Assert.Contains(TestMessages.PngDataUri, Render(TestMessages.ThroughMsg(message)));
    }
}
