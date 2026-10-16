using System.Text;
using XRay.Email.Msg;
using XRay.Email.Msg.Html;

namespace XRay.Email.Tests;

/// <summary>RTF to HTML and text, including the encapsulated-HTML form of MS-OXRTFEX.</summary>
public sealed class RtfConverterTests
{
    private static string Html(string rtf) => RtfConverter.ToHtml(rtf, TestContext.Current.CancellationToken);

    private static string Text(string rtf) => RtfConverter.ToText(rtf, TestContext.Current.CancellationToken);

    private static string Deencapsulate(string rtf) => RtfConverter.DeencapsulateHtml(rtf, TestContext.Current.CancellationToken);

    [Fact]
    public void Encapsulation_is_recognised_from_the_header()
    {
        Assert.True(RtfConverter.IsEncapsulatedHtml("{\\rtf1\\ansi\\fromhtml1 x}"));
        Assert.False(RtfConverter.IsEncapsulatedHtml("{\\rtf1\\ansi\\fromhtml0 x}"));
        Assert.False(RtfConverter.IsEncapsulatedHtml("{\\rtf1\\ansi x}"));
        Assert.True(RtfConverter.IsEncapsulatedText("{\\rtf1\\ansi\\fromtext x}"));
    }

    [Fact]
    public void Htmltag_groups_are_kept_and_htmlrtf_regions_are_dropped()
    {
        const string Rtf = "{\\rtf1\\ansi\\fromhtml1 {\\*\\htmltag64 <p>}\\htmlrtf {\\b \\htmlrtf0 bold text\\htmlrtf \\b0}\\htmlrtf0 {\\*\\htmltag72 </p>}}";

        Assert.Equal("<p>bold text</p>", Deencapsulate(Rtf));
    }

    [Fact]
    public void Entities_in_htmltag_groups_pass_through_and_their_rtf_fallbacks_are_dropped()
    {
        const string Rtf = "{\\rtf1\\ansi\\fromhtml1 a {\\*\\htmltag64 &amp;}\\htmlrtf \\'26\\htmlrtf0  b {\\*\\htmltag64 &nbsp;}\\htmlrtf \\~\\htmlrtf0 c}";

        Assert.Equal("a &amp; b &nbsp;c", Deencapsulate(Rtf));
    }

    [Fact]
    public void Htmlrtf_state_is_scoped_to_its_group()
    {
        const string Rtf = "{\\rtf1\\ansi\\fromhtml1 {\\htmlrtf hidden}shown}";

        Assert.Equal("shown", Deencapsulate(Rtf));
    }

    [Fact]
    public void Mhtmltag_groups_and_other_destinations_are_ignored_in_encapsulated_html()
    {
        const string Rtf = "{\\rtf1\\ansi\\fromhtml1{\\fonttbl{\\f0 Arial;}}{\\colortbl;\\red0\\green0\\blue0;}{\\*\\generator x;}{\\*\\mhtmltag64 <img src=\"cid:x\">}{\\*\\htmltag64 <img src=\"a.png\">}}";

        Assert.Equal("<img src=\"a.png\">", Deencapsulate(Rtf));
    }

    [Fact]
    public void Ansi_bytes_follow_the_document_code_page_and_unicode_escapes_skip_their_fallback()
    {
        Assert.Equal("Café", Deencapsulate("{\\rtf1\\ansi\\ansicpg1252\\fromhtml1 Caf\\'e9}"));
        Assert.Equal("привет", Deencapsulate("{\\rtf1\\ansi\\ansicpg1251\\fromhtml1 \\'ef\\'f0\\'e8\\'e2\\'e5\\'f2}"));
        Assert.Equal("a€b", Deencapsulate("{\\rtf1\\ansi\\uc1\\fromhtml1 a\\u8364?b}"));
        Assert.Equal("a€b", Deencapsulate("{\\rtf1\\ansi\\uc2\\fromhtml1 a\\u8364\\'80\\'80b}"));
        Assert.Equal("ﬁ", Deencapsulate("{\\rtf1\\ansi\\fromhtml1 \\u-1279?}"));
    }

    [Fact]
    public void Double_byte_code_pages_decode_pairs_of_bytes()
    {
        // Shift-JIS "日本" is 93 FA 96 7B
        Assert.Equal("日本", Deencapsulate("{\\rtf1\\ansi\\ansicpg932\\fromhtml1 \\'93\\'fa\\'96\\'7b}"));
    }

    [Fact]
    public void Font_charsets_override_the_document_code_page()
    {
        const string Rtf = "{\\rtf1\\ansi\\ansicpg1252\\fromhtml1{\\fonttbl{\\f0\\fcharset0 Arial;}{\\f1\\fcharset204 Arial;}}\\f1 \\'ef\\'f0\\f0  \\'e9}";

        Assert.Equal("пр é", Deencapsulate(Rtf));
    }

    [Fact]
    public void Escaped_braces_and_backslashes_are_text()
    {
        Assert.Equal("{a}\\b", Text("{\\rtf1\\ansi \\{a\\}\\\\b}"));
    }

    [Fact]
    public void Paragraph_breaks_and_text_formatting_become_html()
    {
        const string Rtf = "{\\rtf1\\ansi{\\fonttbl{\\f0 Arial;}}{\\colortbl;\\red255\\green0\\blue0;}\\f0\\fs24 plain \\b bold\\b0 \\i italic\\i0 \\ul under\\ulnone \\cf1 red\\cf0 \\strike gone\\strike0\\par next\\par}";

        var html = Html(Rtf);

        Assert.Contains("font-weight:bold", html);
        Assert.Contains("font-style:italic", html);
        Assert.Contains("text-decoration:underline", html);
        Assert.Contains("color:#ff0000", html);
        Assert.Contains("text-decoration:line-through", html);
        Assert.Contains("font-size:12pt", html);
        Assert.Contains("font-family:'Arial'", html);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "<p ").Count);
        Assert.EndsWith("next</span></p>", html);
    }

    [Fact]
    public void Empty_paragraphs_keep_their_line_and_alignment_is_kept()
    {
        var html = Html("{\\rtf1\\ansi a\\par\\par\\pard\\qc b\\par}");

        Assert.Contains("<p style=\"margin:0\">&nbsp;</p>", html);
        Assert.Contains("text-align:center", html);
    }

    [Fact]
    public void Line_breaks_tabs_and_special_characters()
    {
        var html = Html("{\\rtf1\\ansi a\\line b\\tab c\\emdash d\\endash e\\bullet f\\lquote g\\rquote h\\ldblquote i\\rdblquote j\\~k\\par}");

        Assert.Contains("a<br>b&emsp;c—d–e•f‘g’h“i”j k", html);
    }

    [Fact]
    public void Hyperlink_fields_become_anchors_and_unsafe_ones_stay_text()
    {
        const string Safe = "{\\rtf1\\ansi {\\field{\\*\\fldinst{HYPERLINK \"https://example.com/a?b=1&c=2\"}}{\\fldrslt link text}} after\\par}";
        const string Unsafe = "{\\rtf1\\ansi {\\field{\\*\\fldinst{HYPERLINK \"javascript:alert(1)\"}}{\\fldrslt click}}\\par}";

        var safe = Html(Safe);
        Assert.Contains("<a href=\"https://example.com/a?b=1&amp;c=2\">link text</a>", safe);
        Assert.Contains("after", safe);

        var unsafeHtml = Html(Unsafe);
        Assert.DoesNotContain("<a", unsafeHtml);
        Assert.Contains("click", unsafeHtml);
        Assert.DoesNotContain("javascript", unsafeHtml);
    }

    [Fact]
    public void Tables_become_html_tables()
    {
        const string Rtf = "{\\rtf1\\ansi\\trowd\\cellx1000\\cellx2000\\pard\\intbl A\\cell B\\cell\\row\\trowd\\cellx1000\\cellx2000\\pard\\intbl C\\cell \\cell\\row\\pard after\\par}";

        var html = Html(Rtf);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "<table"));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "<tr>").Count);
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(html, "<td").Count);
        Assert.Contains("&nbsp;</td>", html);
        Assert.True(html.IndexOf("</table>", StringComparison.Ordinal) < html.IndexOf("after", StringComparison.Ordinal));
    }

    [Fact]
    public void Embedded_png_and_jpeg_pictures_become_data_uris_and_other_kinds_are_dropped()
    {
        var hex = Convert.ToHexString(TestMessages.Png).ToLowerInvariant();
        var png = Html("{\\rtf1\\ansi before {\\pict\\pngblip\\picwgoal1500 " + hex + "} after\\par}");
        var emf = Html("{\\rtf1\\ansi before {\\pict\\emfblip " + hex + "} after\\par}");

        Assert.Contains("<img src=\"" + TestMessages.PngDataUri + "\" style=\"width:100px;height:auto\" alt=\"\">", png);
        Assert.DoesNotContain("<img", emf);
        Assert.Contains("after", emf);
    }

    [Fact]
    public void Skipped_destinations_do_not_leak_text()
    {
        const string Rtf = "{\\rtf1\\ansi{\\fonttbl{\\f0 Arial;}}{\\stylesheet{\\s1 Normal;}}{\\info{\\title Hidden Title}{\\author Someone}}{\\*\\unknowndest secret}{\\header header text}visible\\par}";

        Assert.Equal("visible", Text(Rtf));
    }

    [Fact]
    public void Plain_text_has_crlf_breaks_and_tab_separated_cells()
    {
        const string Rtf = "{\\rtf1\\ansi one\\par two\\line three\\par\\par\\par four\\par \\trowd\\cellx1\\cellx2\\intbl a\\cell b\\cell\\row}";

        Assert.Equal("one\r\ntwo\r\nthree\r\n\r\nfour\r\na\tb", Text(Rtf));
    }

    [Fact]
    public void Damaged_rtf_never_throws()
    {
        const string Noise = "{}\\\r\n'*;ab 1-";
        var random = new Random(7);
        const string Seed = "{\\rtf1\\ansi{\\fonttbl{\\f0 Arial;}}{\\colortbl;\\red1\\green2\\blue3;}\\f0\\fs24 text \\b bold\\par {\\field{\\*\\fldinst{HYPERLINK \"http://x\"}}{\\fldrslt y}}\\'e9\\u8364?{\\pict\\pngblip 89504e47}\\bin5 abcde}";
        for (var iteration = 0; iteration < 400; iteration++)
        {
            var chars = Seed.ToCharArray();
            for (var edits = 0; edits < 4; edits++)
            {
                chars[random.Next(chars.Length)] = Noise[random.Next(Noise.Length)];
            }

            var text = new string(chars)[..random.Next(1, chars.Length)];
            _ = Html(text);
            _ = Text(text);
            _ = Deencapsulate(text);
        }
    }

    [Fact]
    public void Absurd_nesting_is_cut_off_instead_of_overflowing()
    {
        var rtf = "{\\rtf1 " + new string('{', 100_000) + "text" + new string('}', 100_000) + "}";

        _ = Html(rtf);
        _ = Text(rtf);
    }

    [Fact]
    public void Compressed_rtf_round_trips_through_the_decompressor_with_a_token()
    {
        var compressed = TestMessages.UncompressedRtf("{\\rtf1 hello}");

        Assert.Equal("{\\rtf1 hello}", Encoding.Latin1.GetString(CompressedRtf.TryDecompress(compressed, TestContext.Current.CancellationToken)!));
    }
}
