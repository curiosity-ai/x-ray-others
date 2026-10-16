using XRay.Email.Msg;

namespace XRay.Email.Tests;

/// <summary>GetBestTextBody and GetBestHtmlBody: which stored body is used and what is derived from it.</summary>
public sealed class BodyFallbackTests
{
    private static string? Text(MapiMessage message) => message.GetBestTextBody(TestContext.Current.CancellationToken);

    private static string? Html(MapiMessage message) => message.GetBestHtmlBody(TestContext.Current.CancellationToken);

    private static string FromHtml(string html) => Text(TestMessages.WithHtml(html))!;

    [Fact]
    public void Plain_text_body_wins_when_there_is_one()
    {
        var message = TestMessages.Basic("s", "stored text");
        message.HtmlBody = "<p>other</p>";

        Assert.Equal("stored text", Text(message));
        Assert.Equal(MessageBodyKind.Html, message.GetPreferredBodyKind(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Text_is_derived_from_html_when_no_plain_text_exists()
    {
        Assert.Equal("Hello world", FromHtml("<html><body><p>Hello <b>world</b></p></body></html>"));
    }

    [Fact]
    public void Whitespace_only_plain_text_does_not_hide_the_html_body()
    {
        var message = TestMessages.WithHtml("<p>real</p>");
        message.Body = "  \r\n ";

        Assert.Equal("real", Text(message));
    }

    [Fact]
    public void Paragraphs_are_separated_by_blank_lines_and_line_breaks_by_one()
    {
        Assert.Equal("one\r\n\r\ntwo\r\nthree\r\nfour", FromHtml("<p>one</p><p>two<br>three<br/>four</p>"));
        Assert.Equal("a\r\nb", FromHtml("<div>a</div><div>b</div>"));
        Assert.Equal("Title\r\n\r\nbody", FromHtml("<h1>Title</h1><p>body</p>"));
    }

    [Fact]
    public void List_items_are_bulleted_or_numbered_and_nesting_is_indented()
    {
        Assert.Equal("intro\r\n\r\n• one\r\n• two", FromHtml("<p>intro</p><ul><li>one</li><li>two</li></ul>"));
        Assert.Equal("1. first\r\n2. second", FromHtml("<ol><li>first</li><li>second</li></ol>"));
        Assert.Equal("• a\r\n  • b\r\n• c", FromHtml("<ul><li>a<ul><li>b</li></ul></li><li>c</li></ul>"));
    }

    [Fact]
    public void Table_cells_are_tab_separated_and_rows_are_lines()
    {
        Assert.Equal("a\tb\r\nc\td", FromHtml("<table><tr><td>a</td><td>b</td></tr><tr><td>c</td><td>d</td></tr></table>"));
    }

    [Fact]
    public void Entities_are_decoded_and_markup_in_them_stays_text()
    {
        Assert.Equal("Tom & Jerry <3 é € ©", FromHtml("<p>Tom &amp; Jerry &lt;3 &eacute; &euro; &#169;</p>").Replace(' ', ' ').Replace("©", "©"));
        Assert.Equal("a b", FromHtml("<p>a&nbsp;b</p>"));
    }

    [Fact]
    public void Whitespace_in_markup_collapses_and_preformatted_text_is_kept()
    {
        Assert.Equal("one two three", FromHtml("<p>one\r\n   two\t three</p>"));
        Assert.Equal("code\r\n  indented\r\n\r\nend", FromHtml("<pre>code\r\n  indented</pre>end"));
    }

    [Fact]
    public void Scripts_styles_comments_and_the_head_contribute_nothing()
    {
        Assert.Equal("shown", FromHtml("<html><head><title>no</title><style>p{}</style><script>var x=1;</script></head><body><!-- hidden --><script>alert(1)</script><style>b{}</style>shown</body></html>"));
    }

    [Fact]
    public void Horizontal_rules_and_images_with_no_text_do_not_add_noise()
    {
        Assert.Equal("a\r\n----------\r\nb", FromHtml("a<hr>b"));
        Assert.Equal("x", FromHtml("<img src=\"a.png\">x"));
    }

    [Fact]
    public void Text_is_derived_from_rtf_when_there_is_no_html_or_plain_text()
    {
        var message = TestMessages.WithRtf("{\\rtf1\\ansi\\ansicpg1252{\\fonttbl{\\f0 Arial;}}\\f0 Caf\\'e9\\par second\\par}");

        Assert.Equal("Café\r\nsecond", Text(message));
    }

    [Fact]
    public void Text_is_derived_from_encapsulated_html_rtf_through_the_html_path()
    {
        var message = TestMessages.WithRtf("{\\rtf1\\ansi\\fromhtml1 {\\*\\htmltag64 <p>}one{\\*\\htmltag72 </p>}{\\*\\htmltag64 <p>}two &lt;3{\\*\\htmltag72 </p>}}");

        Assert.Equal("one\r\n\r\ntwo <3", Text(message));
    }

    [Fact]
    public void A_message_without_any_body_gives_null_or_empty_text_and_null_html()
    {
        var message = MapiMessage.Create("empty");

        Assert.True(string.IsNullOrEmpty(Text(message)));
        Assert.Null(Html(message));
        Assert.Equal(MessageBodyKind.None, message.GetPreferredBodyKind(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Best_html_is_the_stored_html_untouched()
    {
        const string Stored = "<html><body onload=\"x()\"><p>as stored</p></body></html>";

        Assert.Equal(Stored, Html(TestMessages.WithHtml(Stored)));
    }

    [Fact]
    public void Best_html_is_recovered_from_encapsulated_rtf_and_rendered_from_plain_rtf()
    {
        var encapsulated = TestMessages.WithRtf("{\\rtf1\\ansi\\fromhtml1 {\\*\\htmltag64 <p>}x{\\*\\htmltag72 </p>}}");
        Assert.Equal("<p>x</p>", Html(encapsulated));

        var plain = TestMessages.WithRtf("{\\rtf1\\ansi hello\\par}");
        Assert.Contains("hello", Html(plain));
        Assert.StartsWith("<p ", Html(plain));
    }

    [Fact]
    public void Best_html_wraps_plain_text_in_an_escaped_whitespace_preserving_block()
    {
        var html = Html(TestMessages.Basic("s", "a <b>\r\n  c & d"));

        Assert.Equal("<pre style=\"white-space:pre-wrap;word-wrap:break-word;margin:0\">a &lt;b&gt;\r\n  c &amp; d</pre>", html);
    }

    [Fact]
    public void Bodies_survive_msg_and_eml_round_trips_in_the_same_priority()
    {
        var message = TestMessages.WithHtml("<p>one</p><p>two</p>");

        Assert.Equal("one\r\n\r\ntwo", Text(TestMessages.ThroughMsg(message)));
        Assert.Equal("one\r\n\r\ntwo", Text(TestMessages.ThroughEml(message)));
    }
}
