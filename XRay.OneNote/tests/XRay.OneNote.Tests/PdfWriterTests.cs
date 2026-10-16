using XRay.OneNote.Internal.Pdf;
using XRay.OneNote.Saving;
using XRay.OneNote.Tests.Support;

namespace XRay.OneNote.Tests;

/// <summary>PDF export behavior (ported from the Python save options and PDF golden tests).</summary>
public sealed class PdfWriterTests
{
    private static Document DocumentWith(params Node[] content)
    {
        var document = new Document();
        var page = document.AppendChildLast(new Page());
        foreach (var node in content)
        {
            if (node is Title title)
            {
                page.Title = title;
            }

            page.AppendChildLast(node);
        }

        return document;
    }

    private static RichText TaggedBody()
    {
        var tagged = new RichText("Tagged body");
        tagged.Tags.AddRange([NoteTag.CreateYellowStar("Важно"), NoteTag.CreateQuestionMark("Вопрос"), NoteTag.CreateMusicalNote("Послушать музыку")]);
        return tagged;
    }

    [Fact]
    public void Pdf_save_options_defaults_and_settings()
    {
        var options = new PdfSaveOptions();
        Assert.Equal(SaveFormat.Pdf, options.SaveFormat);
        Assert.Equal(0, options.PageIndex);
        Assert.Null(options.PageCount);
        Assert.Null(typeof(SaveOptions).GetProperty(nameof(SaveOptions.SaveFormat))!.GetSetMethod());

        var fonts = new FontsSubsystem();
        options = new PdfSaveOptions { PageIndex = 2, PageCount = 3, FontsSubsystem = fonts };
        Assert.Equal((2, 3), (options.PageIndex, options.PageCount!.Value));
        Assert.Same(fonts, options.FontsSubsystem);
    }

    [Fact]
    public void Saves_pdf_to_a_stream()
    {
        var document = Fixture.Load("FormattedRichText.one");
        using var stream = new MemoryStream();
        document.Save(stream, new PdfSaveOptions(), TestContext.Current.CancellationToken);
        Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(stream.ToArray(), 0, 4));

        using var second = new MemoryStream();
        document.Save(second, SaveFormat.Pdf, TestContext.Current.CancellationToken);
        Assert.True(second.Length > 100);
    }

    [Fact]
    public void Infers_the_format_from_the_path_extension()
    {
        var document = Fixture.Load("FormattedRichText.one");
        var directory = Directory.CreateTempSubdirectory("xray-onenote-");
        try
        {
            var output = Path.Combine(directory.FullName, "inferred.pdf");
            document.Save(output, TestContext.Current.CancellationToken);
            Assert.StartsWith("%PDF", File.ReadAllText(output)[..4]);
            Assert.Throws<UnsupportedSaveFormatException>(() => document.Save(Path.Combine(directory.FullName, "unsupported-output.one"), TestContext.Current.CancellationToken));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void Renders_note_tags()
    {
        var canvas = RecordingCanvas.Render(DocumentWith(TaggedBody()));
        Assert.Contains("Tagged body", canvas.DrawnStrings.Select(s => s.Text));
        Assert.NotEmpty(canvas.RectCalls);
        Assert.NotEmpty(canvas.LineCalls);
        Assert.Equal(1, canvas.PolygonCalls);
    }

    [Fact]
    public void Renders_tags_in_visual_reverse_order()
    {
        var canvas = RecordingCanvas.Render(DocumentWith(TaggedBody()));
        (double, double, double)[] tagColors = [PdfExporter.TagColor(121), PdfExporter.TagColor(15), PdfExporter.TagColor(13)];
        var drawn = canvas.FillColors.Where(color => tagColors.Contains(color)).Distinct().ToList();
        Assert.Equal(tagColors, drawn);
    }

    [Fact]
    public void Question_tag_uses_a_distinct_color()
    {
        Assert.Equal((0.64, 0.32, 0.82), PdfExporter.TagColor(15));
        Assert.NotEqual(PdfExporter.TagColor(13), PdfExporter.TagColor(15));
    }

    [Fact]
    public void Does_not_duplicate_rich_text()
    {
        var title = new Title(new RichText("Title text"), new RichText("2025-01-01"), new RichText("10:00"));
        var strings = RecordingCanvas.Render(DocumentWith(title, new RichText("Body text"))).DrawnStrings.Select(s => s.Text).ToList();
        foreach (var expected in (string[])["Title text", "2025-01-01", "10:00", "Body text"])
        {
            Assert.Equal(1, strings.Count(s => s == expected));
        }
    }

    [Fact]
    public void Applies_rich_text_run_styles()
    {
        var styled = new RichText(textRuns:
        [
            new TextRun("Bold", new TextStyle { IsBold = true, FontColor = 0x0000FF, Highlight = 0xFFFF00, IsUnderline = true }),
            new TextRun(" Blue", new TextStyle { IsItalic = true, FontName = "Times New Roman", FontSize = 14.0 }),
        ]);
        var canvas = RecordingCanvas.Render(DocumentWith(styled));

        Assert.Equal(1, canvas.DrawnStrings.Count(s => s.Text == "Bold"));
        Assert.Equal(1, canvas.DrawnStrings.Count(s => s.Text == " Blue"));
        Assert.Contains(canvas.FontCalls, f => f.Size == 14 && f.Name.Contains("Italic", StringComparison.Ordinal));
        Assert.Contains(canvas.FontCalls, f => f.Size == 11 && f.Name.Contains("Bold", StringComparison.Ordinal));
        Assert.Contains((1.0, 0.0, 0.0), canvas.FillColors);
        Assert.Contains((0.0, 1.0, 1.0), canvas.FillColors);
        Assert.NotEmpty(canvas.RectCalls);
        Assert.NotEmpty(canvas.LineCalls);
    }

    [Fact]
    public void Keeps_spaces_and_puts_title_date_and_time_on_one_line()
    {
        var title = new Title(new RichText("One hyperlink"), new RichText("2025-01-01"), new RichText("13:12"));
        var strings = RecordingCanvas.Render(DocumentWith(title, new RichText("This is hyperlink."))).DrawnStrings;

        Assert.Contains("One hyperlink", strings.Select(s => s.Text));
        Assert.Contains("This is hyperlink.", strings.Select(s => s.Text));
        var date = strings.First(s => s.Text == "2025-01-01");
        var time = strings.First(s => s.Text == "13:12");
        Assert.Equal(date.Y, time.Y);
        Assert.True(time.X > date.X);
    }

    [Fact]
    public void Converts_image_size_to_points()
    {
        var canvas = RecordingCanvas.Render(DocumentWith(new Image { Bytes = "image-bytes"u8.ToArray(), Width = 10.0, Height = 5.0 }));
        var call = Assert.Single(canvas.ImageCalls);
        Assert.Equal(40, call.X);
        Assert.Equal(842 - 40 - (5.0 * 28.35), call.Y, 2);
        Assert.Equal(10.0 * 28.35, call.Width, 2);
        Assert.Equal(5.0 * 28.35, call.Height, 2);
        Assert.True(call.PreserveAspectRatio);
    }

    [Fact]
    public void Skips_internal_hyperlink_markup()
    {
        var rendered = string.Concat(RecordingCanvas.Render(Fixture.Load("FormattedRichText.one")).DrawnStrings.Select(s => s.Text));
        Assert.DoesNotContain("HYPERLINK", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("﷟", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Creates_clickable_hyperlinks()
    {
        var canvas = RecordingCanvas.Render(Fixture.Load("FormattedRichText.one"));
        Assert.Contains(canvas.LinkCalls, link => link.Url == "https://www.google.com");
    }

    [Fact]
    public void Applies_the_default_hyperlink_style()
    {
        var canvas = RecordingCanvas.Render(Fixture.Load("FormattedRichText.one"));
        Assert.Contains("hyperlink", canvas.DrawnStrings.Select(s => s.Text));
        Assert.Contains((0.0, 0.2, 0.65), canvas.FillColors);
        Assert.NotEmpty(canvas.LineCalls);
    }

    [Fact]
    public void Uses_standard_fonts_by_default()
    {
        var resolver = new FontResolver(new PdfFontRegistry(), new FontsSubsystem { SearchSystemFontDirectories = false });
        Assert.Equal("Helvetica", resolver.Register("sans", false, false));
        Assert.Equal("Times-Italic", resolver.Register("serif", false, true));
        Assert.Equal("Courier-BoldOblique", resolver.Register("mono", true, true));
        Assert.Equal("serif", FontResolver.GroupForFamily("Times New Roman"));
        Assert.Equal("mono", FontResolver.GroupForFamily("Consolas"));
        Assert.Equal("sans", FontResolver.GroupForFamily("Calibri"));
    }

    [Fact]
    public void Uses_a_unicode_font_for_cyrillic()
    {
        var registry = new PdfFontRegistry();
        var resolver = new FontResolver(registry, TestFonts.Bundled());
        Assert.Equal("LiberationSans", resolver.Register("sans", false, false, FontResolver.RequiresUnicodeFont("Привет")));
        Assert.IsType<EmbeddedTrueTypeFont>(registry.Get("LiberationSans"));

        // Without a font that has the glyphs, the standard font is the fallback.
        var none = new FontResolver(new PdfFontRegistry(), new FontsSubsystem { SearchSystemFontDirectories = false });
        Assert.Equal("Helvetica", none.Register("sans", false, false, requireUnicode: true));
    }

    [Fact]
    public void Embedded_cyrillic_text_round_trips_through_the_pdf()
    {
        var pdf = new PdfInspector(TestFonts.ExportPdf(Fixture.Load("AttachedFileWithTag.one")));
        Assert.Contains("10 апреля 2015 г.", pdf.Pages[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Numbered_list_markers_are_rendered()
    {
        var text = new PdfInspector(TestFonts.ExportPdf(Fixture.Load("NumberedListWithTags.one"))).Pages[0].Text;
        foreach (var marker in (string[])["1.", "2.", "a.", "b.", "c.", "i.", "ii."])
        {
            Assert.Contains(marker, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Attachments_are_exported_by_file_name_only()
    {
        var text = new PdfInspector(TestFonts.ExportPdf(Fixture.Load("AttachedFileWithTag.one"))).Pages[0].Text;
        Assert.DoesNotContain("[Attachment]", text, StringComparison.Ordinal);
        Assert.Contains("TestOneNoteSaveAsTiffByFormat.tiff", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Важно, Дела", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Document_without_pages_shows_its_name()
    {
        var canvas = RecordingCanvas.Render(new Document());
        Assert.Equal("Empty document", Assert.Single(canvas.DrawnStrings).Text);
        var named = RecordingCanvas.Render(new Document { DisplayName = "Notes" });
        Assert.Equal("Notes", Assert.Single(named.DrawnStrings).Text);
    }

    [Fact]
    public void A_trailing_empty_page_is_dropped()
    {
        // The reference writer emits a page only once something was drawn on it.
        var document = new Document();
        document.AppendChildLast(new Page());
        Assert.Empty(new PdfInspector(TestFonts.ExportPdf(document)).Pages);

        var page = document.AppendChildLast(new Page());
        page.AppendChildLast(new RichText("x"));
        Assert.Equal(2, new PdfInspector(TestFonts.ExportPdf(document)).Pages.Count);
    }

    [Fact]
    public void Page_range_selects_pages()
    {
        var document = Fixture.Load("Issue1Subpages.one");
        var second = new PdfInspector(TestFonts.ExportPdf(document, new PdfSaveOptions { PageIndex = 1, FontsSubsystem = TestFonts.Bundled() }));
        Assert.Single(second.Pages);
        Assert.Contains("Subpage2", second.Pages[0].Text, StringComparison.Ordinal);

        var first = new PdfInspector(TestFonts.ExportPdf(document, new PdfSaveOptions { PageCount = 1, FontsSubsystem = TestFonts.Bundled() }));
        Assert.Contains("Page1", Assert.Single(first.Pages).Text, StringComparison.Ordinal);

        var none = RecordingCanvas.Render(document, new PdfSaveOptions { PageIndex = 5 });
        Assert.Equal("Issue1Subpages", Assert.Single(none.DrawnStrings).Text);
    }

    [Fact]
    public void Long_text_wraps_and_breaks_pages()
    {
        var document = new Document();
        var page = document.AppendChildLast(new Page());
        page.AppendChildLast(new RichText(string.Join(' ', Enumerable.Repeat("lorem ipsum dolor sit amet", 600))));
        var pdf = new PdfInspector(TestFonts.ExportPdf(document));
        Assert.True(pdf.Pages.Count > 1);
        Assert.Equal(600 * 5, pdf.Pages.Sum(p => p.Text.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Length));
    }

    [Fact]
    public void Tall_tables_split_between_rows()
    {
        var document = new Document();
        var table = document.AppendChildLast(new Page()).AppendChildLast(new Table());
        for (var row = 0; row < 120; row++)
        {
            var tableRow = table.AppendChildLast(new TableRow());
            for (var column = 0; column < 2; column++)
            {
                tableRow.AppendChildLast(new TableCell()).AppendChildLast(new RichText($"r{row}c{column}"));
            }
        }

        var pdf = new PdfInspector(TestFonts.ExportPdf(document));
        Assert.True(pdf.Pages.Count > 1);
        var all = string.Join(' ', pdf.Pages.Select(p => p.Text));
        Assert.Contains("r0c0", all, StringComparison.Ordinal);
        Assert.Contains("r119c1", all, StringComparison.Ordinal);
    }

    [Fact]
    public void List_markers_follow_the_numbering_style()
    {
        Assert.Equal("xiv", PdfExporter.ToRoman(14).ToLowerInvariant());
        Assert.Equal("aa", PdfExporter.ToAlpha(27));
        var state = new Dictionary<(int, string), int>();
        var list = new NumberList { Format = "\u0003�\u0004." };
        Assert.Equal("a.", PdfExporter.ResolveListMarkerText(list, 0, state));
        Assert.Equal("b.", PdfExporter.ResolveListMarkerText(list, 0, state));
        Assert.Equal("a.", PdfExporter.ResolveListMarkerText(list, 1, state));
        Assert.Equal("5.", PdfExporter.ResolveListMarkerText(new NumberList { Format = "�\u0000.", Restart = 5 }, 0, state));
        Assert.Equal("•", PdfExporter.ResolveListMarkerText(new NumberList { Format = "•" }, 0, state));
    }
}
