using System.Text;
using XRay.OneNote.Saving;
using XRay.OneNote.Tests.Support;

namespace XRay.OneNote.Tests;

/// <summary>
/// The HTML writer: golden comparison against this package's own earlier output (not third-party
/// reference output), structure, options and callbacks.
/// </summary>
public sealed class HtmlWriterTests
{
    private static HtmlSaveOptions GoldenOptions() => new()
    {
        PerPage = false,
        ImageSavingCallback = args => args.Url = $"img-{args.GlobalImageIndex}.{args.Extension}",
    };

    private static string Render(Document document, HtmlSaveOptions? options = null)
    {
        using var stream = new MemoryStream();
        document.Save(stream, options ?? new HtmlSaveOptions { PerPage = false });
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? GoldenDirectory()
    {
        var root = Corpus.Root;
        return root is null ? null : Path.Combine(root, "onenote", "html-golden");
    }

    public static TheoryData<string> Fixtures() => Corpus.AllFixtures();

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Matches_the_golden(string fixture)
    {
        var document = Fixture.Load(fixture);
        var html = Render(document, GoldenOptions()).Replace("\r\n", "\n", StringComparison.Ordinal);
        var directory = GoldenDirectory();
        Assert.NotNull(directory);
        var golden = Path.Combine(directory, Path.GetFileNameWithoutExtension(fixture) + ".html");
        if (Environment.GetEnvironmentVariable("XRAY_ONENOTE_UPDATE_GOLDENS") == "1")
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(golden, html, new UTF8Encoding(false));
        }

        if (!File.Exists(golden))
        {
            Assert.Skip("No HTML golden at " + golden + ": run with XRAY_ONENOTE_UPDATE_GOLDENS=1 to create it, or drop reference output there.");
        }

        Assert.Equal(File.ReadAllText(golden).Replace("\r\n", "\n", StringComparison.Ordinal), html);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Output_is_deterministic_and_script_free(string fixture)
    {
        var first = Render(Fixture.Load(fixture), GoldenOptions());
        var second = Render(Fixture.Load(fixture), GoldenOptions());
        Assert.Equal(first, second);
        Assert.StartsWith("<!DOCTYPE html>\n<html", first, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", first, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", first, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<meta charset=\"utf-8\">", first, StringComparison.Ordinal);
        Assert.Contains("<style>", first, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Every_paragraph_appears_in_order(string fixture)
    {
        var document = Fixture.Load(fixture);
        var html = Render(document, GoldenOptions());
        var texts = new List<string>();
        foreach (var page in document.Pages)
        {
            if (page.Title?.TitleText is { } title)
            {
                texts.Add(Visible(title));
            }

            foreach (var richText in page.GetChildNodes<RichText>())
            {
                if (!richText.IsTitleText && !richText.IsTitleDate && !richText.IsTitleTime)
                {
                    texts.Add(Visible(richText));
                }
            }
        }

        // Paragraph text runs are written back to back, so compare run by run.
        HtmlText.AssertInOrder(html, texts.Where(t => t.Length > 0).SelectMany(t => t.Split('\n')).Where(t => t.Length > 0));
    }

    private static string Visible(RichText richText) =>
        string.Concat(richText.TextRuns.Where(r => !r.Style.IsHidden).Select(r => StripFieldCodes(r.Text))).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace('\v', '\n');

    private static string StripFieldCodes(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, "﷟HYPERLINK\\s+\"[^\"]*\"", string.Empty).Replace("﷟", string.Empty, StringComparison.Ordinal);

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Structure_counts_match_the_document(string fixture)
    {
        var document = Fixture.Load(fixture);
        var html = Render(document, GoldenOptions());
        Assert.Equal(document.GetChildNodes<Image>().Count(i => i.Bytes.Length > 0), HtmlText.Count(html, "<img "));
        Assert.Equal(document.GetChildNodes<Table>().Count, HtmlText.Count(html, "<table "));
        Assert.Equal(document.GetChildNodes<TableCell>().Count, HtmlText.Count(html, "<td "));
        Assert.Equal(document.GetChildNodes<TableRow>().Count, HtmlText.Count(html, "<tr "));
        Assert.Equal(document.GetChildNodes<AttachedFile>().Count, HtmlText.Count(html, "<div class=\"on-file\">"));
        Assert.Equal(document.Pages.Count(), HtmlText.Count(html, "<article "));
        var expectedTags = document.GetChildNodes<Node>().Sum(TagCount);
        Assert.Equal(expectedTags, HtmlText.Count(html, "<span class=\"on-tag "));
    }

    private static int TagCount(Node node) => node switch
    {
        RichText r => Dedupe(r.Tags),
        Image i => Dedupe(i.Tags),
        Table t => Dedupe(t.Tags),
        AttachedFile f => Dedupe(f.Tags),
        _ => 0,
    };

    private static int Dedupe(List<NoteTag> tags) =>
        tags.Select(t => (t.Icon, t.Label, t.FontColor, t.Highlight, t.CreationTime, t.CompletedTime)).Distinct().Count();

    [Fact]
    public void Tables_have_the_right_cell_counts()
    {
        var document = Fixture.Load("SimpleTable.one");
        var table = document.GetChildNodes<Table>()[0];
        var html = Render(document);
        var rows = table.OfType<TableRow>().ToList();
        Assert.Equal(rows.Count, HtmlText.Count(html, "<tr class=\"on-row\">"));
        Assert.Equal(rows.Sum(r => r.OfType<TableCell>().Count()), HtmlText.Count(html, "<td class=\"on-cell\">"));
    }

    [Fact]
    public void Numbered_lists_carry_their_numbers()
    {
        var document = Fixture.Load("NumberedListWithTags.one");
        var html = Render(document);
        Assert.Contains("on-marker-number", html, StringComparison.Ordinal);
        Assert.Contains(">1.<", html.Replace(">1.</span>", ">1.<", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Pages_carry_their_times_and_tables_their_column_widths()
    {
        var html = Render(Fixture.Load("Issue1Subpages.one"));
        Assert.Contains("data-created=\"2026-01-20T14:50:35Z\"", html, StringComparison.Ordinal);
        Assert.Contains("<colgroup><col style=\"width:49.48px\"><col style=\"width:189.6px\">", Render(Fixture.Load("TableWithTag.one")), StringComparison.Ordinal);
        Assert.Contains("on-tag-completed", Render(Fixture.Load("TableWithTag.one")), StringComparison.Ordinal);
    }

    [Fact]
    public void Tags_are_written_with_state_classes()
    {
        var html = Render(Fixture.Load("TagSizes.one"));
        Assert.Contains("on-tag-todo", html, StringComparison.Ordinal);
        Assert.Contains("data-shape=", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Images_use_the_callback_url_and_report_their_page()
    {
        var document = Fixture.Load("3ImagesWithDifferentAlignment.one");
        var seen = new List<(int Page, int Index, int Global, string Format, long Size)>();
        var html = Render(document, new HtmlSaveOptions
        {
            PerPage = false,
            ImageSavingCallback = args =>
            {
                seen.Add((args.PageIndex, args.ImageIndex, args.GlobalImageIndex, args.Format, args.Size));
                args.Url = "./api/blob/get/" + args.GlobalImageIndex;
            },
        });
        Assert.NotEmpty(seen);
        Assert.Equal(seen.Count, HtmlText.Count(html, "<img "));
        Assert.All(seen, s => Assert.Equal("png", s.Format));
        Assert.Equal(Enumerable.Range(0, seen.Count), seen.Select(s => s.Global));
        Assert.Contains("src=\"./api/blob/get/0\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data:image", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Images_are_embedded_as_data_uris_by_default_and_the_stream_receives_bytes()
    {
        var document = Fixture.Load("ImageWithTag.one");
        var html = Render(document);
        Assert.Contains("src=\"data:image/", html, StringComparison.Ordinal);

        var captured = new MemoryStream();
        var html2 = Render(document, new HtmlSaveOptions
        {
            PerPage = false,
            ImageSavingCallback = args =>
            {
                if (args.GlobalImageIndex == 0)
                {
                    args.OutputStream = captured;
                    args.KeepStreamOpen = true;
                    args.SkipEmbedding = true;
                }
            },
        });
        Assert.Equal(document.GetChildNodes<Image>().First(i => i.Bytes.Length > 0).Bytes, captured.ToArray());
        Assert.Contains("on-image-skipped", html2, StringComparison.Ordinal);
    }

    [Fact]
    public void Attached_files_are_named_and_linked_through_the_callback()
    {
        var document = Fixture.Load("OnePageWithFile.one");
        var plain = Render(document);
        Assert.Contains("on-file-name", plain, StringComparison.Ordinal);
        var linked = Render(document, new HtmlSaveOptions { PerPage = false, FileSavingCallback = args => args.Url = "files/" + args.FileIndex });
        Assert.Contains("class=\"on-file-link\" href=\"files/0\"", linked, StringComparison.Ordinal);
    }

    [Fact]
    public void Per_page_output_goes_to_the_streams_the_callback_supplies()
    {
        var document = Fixture.Load("Issue1Subpages.one");
        var pages = document.Pages.ToList();
        Assert.True(pages.Count > 1);
        var streams = new Dictionary<int, MemoryStream>();
        var titles = new List<string?>();
        var levels = new List<int>();
        document.Save(new MemoryStream(), new HtmlSaveOptions
        {
            PageSavingCallback = args =>
            {
                var stream = new MemoryStream();
                streams[args.PageIndex] = stream;
                titles.Add(args.Title);
                levels.Add(args.Level);
                args.OutputStream = stream;
                args.KeepStreamOpen = true;
            },
        }, TestContext.Current.CancellationToken);
        Assert.Equal(Enumerable.Range(0, pages.Count), streams.Keys);
        Assert.Equal(pages.Select(p => p.Title?.TitleText?.Text), titles);
        Assert.Equal(pages.Select(p => p.Level ?? 1), levels);
        foreach (var (index, stream) in streams)
        {
            var html = Encoding.UTF8.GetString(stream.ToArray());
            Assert.Equal(1, HtmlText.Count(html, "<article "));
            Assert.Contains($"data-page-index=\"{index}\"", html, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Per_page_to_one_stream_without_a_callback_is_refused()
    {
        var document = Fixture.Load("Issue1Subpages.one");
        Assert.Throws<InvalidOperationException>(() => document.Save(new MemoryStream(), new HtmlSaveOptions(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Saving_to_a_path_writes_one_file_per_page()
    {
        var document = Fixture.Load("Issue1Subpages.one");
        var directory = Path.Combine(Path.GetTempPath(), "xray-onenote-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            document.Save(Path.Combine(directory, "out.html"), new HtmlSaveOptions { EmbedCss = false }, TestContext.Current.CancellationToken);
            var files = Directory.GetFiles(directory).Select(Path.GetFileName).Order().ToList();
            Assert.Contains("out_000.html", files);
            Assert.Contains("onenote.css", files);
            Assert.Equal(document.Pages.Count() + 1, files.Count);
            Assert.Contains("<link rel=\"stylesheet\" href=\"onenote.css\">", File.ReadAllText(Path.Combine(directory, "out_000.html")), StringComparison.Ordinal);

            document.Save(Path.Combine(directory, "all.html"), TestContext.Current.CancellationToken);
            Assert.Equal(document.Pages.Count(), HtmlText.Count(File.ReadAllText(Path.Combine(directory, "all.html")), "<article "));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static Document Synthetic(params RichText[] paragraphs)
    {
        var document = new Document { DisplayName = "Synthetic" };
        var page = document.AppendChildLast(new Page { Title = new Title(new RichText("Title")) });
        var outline = page.AppendChildLast(new Outline());
        foreach (var paragraph in paragraphs)
        {
            outline.AppendChildLast(new OutlineElement()).AppendChildLast(paragraph);
        }

        return document;
    }

    [Fact]
    public void Hidden_text_is_marked_and_can_be_shown()
    {
        var hidden = new TextStyle { IsHidden = true };
        var document = Synthetic(new RichText(textRuns: [new TextRun("ocr words", hidden)]));
        var plain = Render(document);
        Assert.Contains("class=\"on-p on-all-hidden\"", plain, StringComparison.Ordinal);
        Assert.Contains("<span class=\"on-hidden\">ocr words</span>", plain, StringComparison.Ordinal);

        var shown = Render(document, new HtmlSaveOptions { PerPage = false, ShowHiddenText = true });
        Assert.DoesNotContain("class=\"on-p on-all-hidden\"", shown, StringComparison.Ordinal);
        Assert.Contains("<span class=\"on-hidden on-hidden-shown\">ocr words</span>", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_formatting_is_written()
    {
        var style = new TextStyle
        {
            IsBold = true, IsItalic = true, IsUnderline = true, IsStrikethrough = true, IsSuperscript = true,
            FontColor = 0x0000FF, Highlight = 0x00FFFF, FontName = "Arial", FontSize = 14,
        };
        var html = Render(Synthetic(new RichText(textRuns: [new TextRun("a<b & \"c\"\nnext", style)])));
        Assert.Contains("style=\"color:#ff0000;background-color:#ffff00;font-family:'Arial';font-size:14pt\"", html, StringComparison.Ordinal);
        Assert.Contains("<strong><em><u><s><sup>a&lt;b &amp; &quot;c&quot;<br>next</sup></s></u></em></strong>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Hyperlinks_are_anchors_and_unsafe_schemes_are_dropped()
    {
        var ok = new TextStyle { IsHyperlink = true, HyperlinkAddress = "https://example.com/a?b=1&c=2" };
        var bad = new TextStyle { IsHyperlink = true, HyperlinkAddress = "javascript:alert(1)" };
        var html = Render(Synthetic(new RichText(textRuns: [new TextRun("good", ok), new TextRun("bad", bad)])));
        Assert.Contains("<a class=\"on-link\" href=\"https://example.com/a?b=1&amp;c=2\">", html, StringComparison.Ordinal);
        Assert.DoesNotContain("javascript", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bad", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Control_characters_are_not_written()
    {
        var html = Render(Synthetic(new RichText("a\u0001b￾c\uD800d")));
        Assert.Contains("abc�d", html, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_document_still_renders()
    {
        var html = Render(new Document { DisplayName = "Empty" });
        Assert.Contains("<title>Empty</title>", html, StringComparison.Ordinal);
        Assert.Contains("</html>", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Low_level_api_writes_a_page()
    {
        var document = Fixture.Load("FormattedRichText.one");
        using var stream = new MemoryStream();
        HtmlWriter.WritePage(document.Pages.First(), stream, cancellationToken: TestContext.Current.CancellationToken);
        var html = Encoding.UTF8.GetString(stream.ToArray());
        Assert.Equal(1, HtmlText.Count(html, "<article "));
        Assert.False(string.IsNullOrEmpty(HtmlWriter.StyleSheet));
        Assert.Throws<NotSupportedException>(() => new HtmlSaveOptions { ExportFonts = true });
    }

    [Fact]
    public void Page_selection_applies()
    {
        var document = Fixture.Load("Issue1Subpages.one");
        var html = Render(document, new HtmlSaveOptions { PerPage = false, PageIndex = 1, PageCount = 1 });
        Assert.Equal(1, HtmlText.Count(html, "<article "));
        Assert.Contains("data-page-index=\"1\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Save_by_extension_writes_html()
    {
        var document = Fixture.Load("SimpleTable.one");
        var path = Path.Combine(Path.GetTempPath(), "xray-onenote-" + Guid.NewGuid().ToString("N") + ".html");
        try
        {
            document.Save(path, TestContext.Current.CancellationToken);
            Assert.Contains("<table ", File.ReadAllText(path), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
