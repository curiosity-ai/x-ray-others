using XRay.OneNote.Internal.OneStore;
using XRay.OneNote.Tests.Support;

namespace XRay.OneNote.Tests;

/// <summary>Revision manifests and page history (ported from the Python history tests).</summary>
public sealed class HistoryTests
{
    private static readonly HashSet<string> TitleLines = ["2 февраля 2021 г.", "16:34", "19:01"];

    private static string PageBodyText(Page page)
    {
        var texts = page.GetChildNodes<RichText>().Select(rt => rt.Text).Where(text => text.Length > 0 && !TitleLines.Contains(text)).ToList();
        return texts.Count > 0 ? texts[^1] : string.Empty;
    }

    private static List<string> NonEmptyOutlineTexts(Page page) =>
        page.GetChildNodes<Outline>()
            .Select(outline => outline.GetChildNodes<RichText>().Select(rt => rt.Text).Where(text => text.Length > 0).ToList())
            .Where(texts => texts.Count > 0)
            .Select(texts => texts[^1])
            .ToList();

    private static List<List<string>> TableGrid(Page page)
    {
        var tables = page.GetChildNodes<Table>();
        if (tables.Count == 0)
        {
            return [];
        }

        return tables[0].GetChildNodes<TableRow>()
            .Select(row => row.GetChildNodes<TableCell>()
                .Select(cell => cell.GetChildNodes<RichText>().Select(rt => rt.Text).Where(text => text.Length > 0).LastOrDefault() ?? string.Empty)
                .ToList())
            .ToList();
    }

    private static OneStoreFile Parse(string fixture) => OneStoreParser.Parse(File.ReadAllBytes(Corpus.Require(fixture)), null);

    [Fact]
    public void Parser_keeps_default_and_history_roots_separate()
    {
        var contentSpace = Parse("SimpleHistory.one").ObjectSpaces[1];
        var defaultRoot = contentSpace.GetLatestRoot(1);
        Assert.NotNull(defaultRoot);
        Assert.Equal("PageManifestNode", contentSpace.Objects[defaultRoot.Value].JcidName);

        var contextRootKinds = contentSpace.Revisions
            .Where(r => r.ContextId is not null && r.RootRoles.ContainsKey(1) && r.Objects.ContainsKey(r.RootRoles[1]))
            .Select(r => r.Objects[r.RootRoles[1]].JcidName)
            .ToHashSet();
        Assert.Contains("VersionHistoryContent", contextRootKinds);
    }

    [Fact]
    public void Page_history_returns_all_revisions_in_order()
    {
        var document = Fixture.Load("SimpleHistory.one", loadHistory: true);
        var page = document.GetChildNodes<Page>()[0];
        var history = document.GetPageHistory(page);

        Assert.Equal("Third text", PageBodyText(history.Current));
        Assert.Equal(["", "First text", "Second text"], history.Select(PageBodyText));
    }

    [Fact]
    public void Without_load_history_a_page_has_no_earlier_versions()
    {
        var document = Fixture.Load("SimpleHistory.one");
        var page = document.GetChildNodes<Page>()[0];
        var history = document.GetPageHistory(page);
        Assert.Same(page, history.Current);
        Assert.Empty(history);
        Assert.Empty(document.GetPageHistory(new Page()));
    }

    [Theory]
    [InlineData("SimpleTable.one")]
    [InlineData("TagSizes.one")]
    [InlineData("3ImagesWithDifferentAlignment.one")]
    public void Parser_materializes_incremental_current_roots(string fixture)
    {
        var contentSpace = Parse(fixture).ObjectSpaces.MaxBy(space => space.Revisions.Count)!;
        var defaultRoot = contentSpace.GetLatestRoot(1);
        Assert.NotNull(defaultRoot);
        Assert.True(contentSpace.Objects.ContainsKey(defaultRoot.Value));
        Assert.Equal("PageManifestNode", contentSpace.Objects[defaultRoot.Value].JcidName);
    }

    [Fact]
    public void Incremental_history_reconstructs_simple_table_versions()
    {
        var document = Fixture.Load("SimpleTable.one", loadHistory: true);
        var page = document.GetChildNodes<Page>()[0];
        var history = document.GetPageHistory(page);

        Assert.Equal("0", PageBodyText(page));
        Assert.Equal("0", PageBodyText(history.Current));
        Assert.Equal(["", "fdf"], history.Select(PageBodyText));
    }

    [Fact]
    public void Simple_table_published_history_matches_the_ui()
    {
        var document = Fixture.Load("SimpleTable.one", loadHistory: true);
        var current = document.GetChildNodes<Page>()[0];
        var history = document.GetPageHistory(current);

        Assert.Equal(2, history.Count);
        Assert.Same(current, history.Current);
        Assert.Equal([["1", "2", "3"], ["6", "5", "4"], ["7", "8", "9"], ["b", "a", "0"]], TableGrid(current));

        Assert.Equal("fdf", PageBodyText(history[1]));
        Assert.Equal(["▪", "fdf"], NonEmptyOutlineTexts(history[1]));
        Assert.Equal(string.Empty, PageBodyText(history[0]));
        Assert.Empty(NonEmptyOutlineTexts(history[0]));
    }

    [Fact]
    public void Incremental_history_reconstructs_image_alignment_versions()
    {
        var document = Fixture.Load("3ImagesWithDifferentAlignment.one", loadHistory: true);
        var page = document.GetChildNodes<Page>()[0];
        var history = document.GetPageHistory(page);
        var texts = history.Select(PageBodyText).ToList();

        Assert.Equal("Image in the outline with left alignment", PageBodyText(page));
        Assert.Equal("Image in the outline with left alignment", PageBodyText(history.Current));
        Assert.Equal(["", "fdf", "0"], texts.Take(3));
        Assert.All(texts.Skip(3), text => Assert.Equal("tt", text));
    }
}
