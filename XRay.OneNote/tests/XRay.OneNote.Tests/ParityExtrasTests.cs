using System.Text;
using XRay.OneNote.Saving;
using XRay.OneNote.Tests.Support;

namespace XRay.OneNote.Tests;

/// <summary>
/// Hyperlink addresses, named paragraph styles, handwriting recognition, inline object placeholders, section
/// color, cell backgrounds and image layout, read from files written by OneNote and checked against what an
/// independent parser reports for them (the optional third-party samples, XRAY_ONENOTE_RS_SAMPLES).
/// </summary>
public sealed class ParityExtrasTests
{
    private static string Html(Document document)
    {
        using var stream = new MemoryStream();
        document.Save(stream, new HtmlSaveOptions { PerPage = false }, TestContext.Current.CancellationToken);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [Fact]
    public void Paragraph_style_ids_are_read_and_choose_the_html_element()
    {
        var document = new Document(RustSamples.Require("joplin/checkboxes_and_unicode.one"));
        var histogram = document.GetChildNodes<RichText>()
            .GroupBy(rt => rt.ParagraphStyle.StyleId ?? "(none)")
            .ToDictionary(group => group.Key, group => group.Count());
        Assert.Equal(4, histogram["PageTitle"]);
        Assert.Equal(8, histogram["PageDateTime"]);
        Assert.Equal(4, histogram["h2"]);
        Assert.Equal(2, histogram["h3"]);
        Assert.Equal(2, histogram["cite"]);

        var html = Html(document);
        Assert.Contains("<h2 class=\"on-p", html, StringComparison.Ordinal);
        Assert.Contains("<h3 class=\"on-p", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<h1 class=\"on-p", html, StringComparison.Ordinal);
        Assert.Contains("<p class=\"on-p", html, StringComparison.Ordinal);

        var small = new Document(RustSamples.Require("New Section 1.one"));
        Assert.Single(small.GetChildNodes<RichText>(), rt => rt.ParagraphStyle.StyleId == "h1");
    }

    [Fact]
    public void Code_and_quotation_styles_choose_pre_and_blockquote()
    {
        var text = new RichText("let x = 1;") { ParagraphStyle = new ParagraphStyle { StyleId = "code" } };
        var quote = new RichText("quoted") { ParagraphStyle = new ParagraphStyle { StyleId = "blockquote" } };
        var outline = new Outline();
        var first = new OutlineElement();
        first.AppendChildLast(text);
        var second = new OutlineElement();
        second.AppendChildLast(quote);
        outline.AppendChildLast(first);
        outline.AppendChildLast(second);
        var page = new Page();
        page.AppendChildLast(outline);
        var document = new Document();
        document.AppendChildLast(page);

        var html = Html(document);
        Assert.Contains("<pre class=\"on-p\">let x = 1;</pre>", html, StringComparison.Ordinal);
        Assert.Contains("<blockquote class=\"on-p\">quoted</blockquote>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void A_field_code_hyperlink_gives_its_address_to_the_link_text()
    {
        var document = new Document(RustSamples.Require("joplin/hyperlink_is_broken/Quick Notes.one"));
        var linked = document.GetChildNodes<RichText>().SelectMany(rt => rt.TextRuns).Where(run => run.Style.HyperlinkAddress is not null).ToList();
        var run = Assert.Single(linked);
        Assert.Equal("https://www.youtube.com/watch?v=-U-uj1jaHtk", run.Style.HyperlinkAddress);
        Assert.Equal("Is Mexico safe for shooting Street Photography?", run.Text);

        // The field code stays in the text; the writers hide it and link the display text.
        Assert.Contains(document.GetChildNodes<RichText>(), rt => rt.Text.Contains("HYPERLINK \"https://www.youtube.com", StringComparison.Ordinal));
        var html = Html(document);
        Assert.Contains("href=\"https://www.youtube.com/watch?v=-U-uj1jaHtk\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("HYPERLINK \"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Recognized_handwriting_is_exposed_on_the_page_and_not_in_its_children()
    {
        var document = new Document(RustSamples.Require("handwriting_recognition.one"));
        var pages = document.Pages.ToList();
        Assert.Equal("Hello World\nHello World", pages[0].InkRecognition!.Text);
        Assert.Null(pages[1].InkRecognition);
        Assert.DoesNotContain(pages[0].GetChildNodes<RichText>(), rt => rt.Text.Contains("Hello World", StringComparison.Ordinal));
        Assert.Equal("Hello World\nHello World", pages[0].Clone().InkRecognition!.Text);
    }

    [Fact]
    public void Placeholders_for_inline_ink_are_not_text()
    {
        var document = new Document(RustSamples.Require("joplin/desktop_missing_ink.one"));
        Assert.DoesNotContain(document.GetChildNodes<RichText>(), rt => rt.Text.Contains('￼', StringComparison.Ordinal));
    }

    [Fact]
    public void Section_color_cell_background_and_image_layout_match_the_independent_parser()
    {
        var document = new Document(RustSamples.Require("New Section 1.one"));
        Assert.Equal(14985354u, document.Color);
        var cell = document.GetChildNodes<TableCell>().First(c => c.BackgroundColor is not null);
        Assert.Equal(0xE6E6E7u, cell.BackgroundColor);
        Assert.Contains("<td class=\"on-cell\" style=\"background-color:", Html(document), StringComparison.Ordinal);

        var image = new Document(RustSamples.Require("New Section Group/New Section 2.one")).GetChildNodes<Image>().First();
        Assert.Equal(12.395833, image.LayoutMaxWidth!.Value, 4);
        Assert.False(image.IsBackground);
    }

    [Fact]
    public void The_notebook_table_of_contents_color_reads_like_the_section_color()
    {
        var toc = NotebookToc.Load(RustSamples.Require("Open Notebook.onetoc2"), TestContext.Current.CancellationToken);
        Assert.Equal(14985354u, toc.Entries.Single().Color);
        Assert.Equal(new Document(RustSamples.Require("New Section 1.one")).Color, toc.Entries.Single().Color);
    }
}
