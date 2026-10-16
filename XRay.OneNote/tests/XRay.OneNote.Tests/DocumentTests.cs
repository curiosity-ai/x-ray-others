using XRay.OneNote.Tests.Support;

namespace XRay.OneNote.Tests;

/// <summary>Loading, traversal, editing, cloning and visiting (ported from the Python document tests).</summary>
public sealed class DocumentTests
{
    [Fact]
    public void Constructs_from_path()
    {
        var document = Fixture.Load("FormattedRichText.one");
        Assert.NotEmpty(document);
        Assert.NotNull(document.FirstChild);
        Assert.NotNull(document.LastChild);
        Assert.Equal("FormattedRichText", document.DisplayName);
    }

    [Fact]
    public void Constructs_from_stream()
    {
        using var stream = File.OpenRead(Corpus.Require("FormattedRichText.one"));
        var document = new Document(stream);
        Assert.NotEmpty(document);

        // A stream has no file name, so the section name falls back to the first page title.
        Assert.Equal("One hyperlink", document.DisplayName);
    }

    [Fact]
    public void Reports_the_file_format() => Assert.Equal(FileFormat.OneNote2010, Fixture.Load("FormattedRichText.one").FileFormat);

    [Fact]
    public void Every_page_has_a_title_that_is_its_first_child()
    {
        var document = Fixture.Load("FormattedRichText.one");
        var pages = document.GetChildNodes<Page>();
        Assert.NotEmpty(pages);
        Assert.True(document.GetChildNodes<Title>().Count >= pages.Count);
        Assert.NotNull(pages[0].Title);
        Assert.Same(pages[0].Title, pages[0].FirstChild);
        Assert.Equal("One hyperlink", pages[0].Title!.TitleText!.Text);
        Assert.True(pages[0].Title!.TitleText!.IsTitleText);
        Assert.True(pages[0].Title!.TitleDate!.IsTitleDate);
        Assert.True(pages[0].Title!.TitleTime!.IsTitleTime);
    }

    [Fact]
    public void Nodes_know_their_document()
    {
        var document = Fixture.Load("SimpleTable.one");
        var cell = document.GetChildNodes<TableCell>()[0];
        Assert.Same(document, cell.Document);
        Assert.Null(new Page().Document);
    }

    [Fact]
    public void Rich_text_replace_changes_text()
    {
        var target = Fixture.Load("FormattedRichText.one").GetChildNodes<RichText>().First(rt => rt.Text.Contains(' ', StringComparison.Ordinal));
        var before = target.Text;
        var returned = target.Replace(" ", "  ");
        Assert.Same(target, returned);
        Assert.NotEqual(before, target.Text);
        Assert.Equal(before.Replace(" ", "  ", StringComparison.Ordinal), target.Text);
    }

    [Fact]
    public void Rich_text_exposes_runs_and_length()
    {
        var richText = Fixture.Load("FormattedRichText.one").GetChildNodes<RichText>().First(rt => rt.TextRuns.Count > 0);
        Assert.NotEmpty(richText.TextRuns);
        Assert.Equal(richText.Text.Length, richText.Length);
    }

    [Fact]
    public void Page_clone_duplicates_the_content_tree()
    {
        var page = Fixture.Load("FormattedRichText.one").GetChildNodes<Page>()[0];
        var cloned = page.Clone();

        Assert.NotSame(page, cloned);
        Assert.Equal(page.Count(), cloned.Count());
        Assert.Equal(page.GetChildNodes<RichText>().Count, cloned.GetChildNodes<RichText>().Count);
        Assert.Null(cloned.ParentNode);
        Assert.Same(cloned.Title, cloned.FirstChild);
        Assert.Same(cloned, cloned.Title!.ParentNode);
        Assert.Equal(page.Title!.TitleText!.Text, cloned.Title.TitleText!.Text);

        // The copy is independent of the original.
        cloned.GetChildNodes<RichText>()[1].Text = "changed";
        Assert.NotEqual("changed", page.GetChildNodes<RichText>()[1].Text);
    }

    [Fact]
    public void Page_clone_accepts_the_history_flag() => Assert.NotNull(Fixture.Load("FormattedRichText.one").GetChildNodes<Page>()[0].Clone(cloneHistory: true));

    private sealed class CountingVisitor : DocumentVisitor
    {
        public int DocumentStarts { get; private set; }

        public int Pages { get; private set; }

        public int RichTexts { get; private set; }

        public List<string> Events { get; } = [];

        public override void VisitDocumentStart(Document document) => DocumentStarts++;

        public override void VisitPageStart(Page page) => Pages++;

        public override void VisitTitleStart(Title title) => Events.Add("title");

        public override void VisitOutlineStart(Outline outline) => Events.Add("outline");

        public override void VisitRichTextStart(RichText richText) => RichTexts++;
    }

    [Fact]
    public void Accept_visits_document_pages_and_content()
    {
        var document = Fixture.Load("SimpleTable.one");
        var visitor = new CountingVisitor();
        document.Accept(visitor);

        Assert.Equal(1, visitor.DocumentStarts);
        Assert.Equal(1, visitor.Pages);
        Assert.Equal(document.GetChildNodes<RichText>().Count, visitor.RichTexts);
        Assert.Contains("outline", visitor.Events);
    }

    [Fact]
    public void Keeps_subpages_from_multiple_page_spaces()
    {
        var document = Fixture.Load("Issue1Subpages.one");
        var pages = document.GetChildNodes<Page>();

        Assert.Equal(2, document.Count());
        Assert.Equal(["Page1", "Subpage2"], pages.Select(page => page.Title?.TitleText?.Text));
        Assert.Equal([1, 2], pages.Select(page => page.Level ?? 0));

        var texts = pages.Select(page => page.GetChildNodes<Outline>()
            .SelectMany(outline => outline.GetChildNodes<OutlineElement>())
            .SelectMany(element => element.GetChildNodes<RichText>())
            .Select(rt => rt.Text)
            .Where(text => text.Length > 0)
            .ToArray()).ToArray();
        Assert.Equal([["Content1"], ["Content2"]], texts);
    }

    [Theory]
    [InlineData("SimpleTable.one")]
    [InlineData("3ImagesWithDifferentAlignment.one")]
    public void Single_page_fixtures_do_not_get_synthetic_titles(string fixture)
    {
        var page = Assert.Single(Fixture.Load(fixture).GetChildNodes<Page>());
        Assert.Null(page.Title);
        Assert.Equal(1, page.Level);
    }

    [Fact]
    public void Composite_node_child_editing_keeps_parents()
    {
        var page = new Page();
        var first = page.AppendChildLast(new Outline());
        var zeroth = page.AppendChildFirst(new Outline());
        var middle = page.InsertChild(1, new Outline());

        Assert.Equal([zeroth, middle, first], page.Cast<Outline>());
        Assert.Same(page, middle.ParentNode);

        page.RemoveChild(middle);
        Assert.Null(middle.ParentNode);
        Assert.Equal([zeroth, first], page.Cast<Outline>());
        Assert.Throws<ArgumentException>(() => page.RemoveChild(middle));
    }

    [Fact]
    public void Loads_every_corpus_fixture()
    {
        foreach (var fixture in Corpus.Fixtures)
        {
            var document = Fixture.Load(fixture);
            Assert.NotEmpty(document.Pages);
            Assert.False(string.IsNullOrEmpty(document.DisplayName));
        }
    }
}
