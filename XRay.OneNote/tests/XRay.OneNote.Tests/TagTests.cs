using XRay.OneNote.Tests.Support;

namespace XRay.OneNote.Tests;

/// <summary>Note tags on text, images, tables and attachments (ported from the Python tag tests).</summary>
public sealed class TagTests
{
    private static bool IsMeaningful(NoteTag tag) => !string.IsNullOrEmpty(tag.Label) || tag.Icon is not null;

    private static List<NoteTag> AllTags(Document document) =>
    [
        .. document.GetChildNodes<RichText>().SelectMany(rt => rt.Tags),
        .. document.GetChildNodes<Image>().SelectMany(image => image.Tags),
        .. document.GetChildNodes<Table>().SelectMany(table => table.Tags),
        .. document.GetChildNodes<AttachedFile>().SelectMany(file => file.Tags),
    ];

    private static (string Text, HashSet<string> Labels) FirstRichText(CompositeNode node)
    {
        var richTexts = node.GetChildNodes<RichText>();
        if (richTexts.Count == 0)
        {
            return (string.Empty, []);
        }

        return (richTexts[0].Text, [.. richTexts[0].Tags.Select(tag => tag.Label).OfType<string>()]);
    }

    [Fact]
    public void Image_with_tag_exposes_tags()
    {
        var tagged = Fixture.Load("ImageWithTag.one").GetChildNodes<Image>().Where(image => image.Tags.Count > 0).ToList();
        Assert.NotEmpty(tagged);
        Assert.Contains(tagged.SelectMany(image => image.Tags), IsMeaningful);
    }

    [Fact]
    public void Table_with_tag_exposes_tags()
    {
        var tagged = Fixture.Load("TableWithTag.one").GetChildNodes<Table>().Where(table => table.Tags.Count > 0).ToList();
        Assert.NotEmpty(tagged);
        Assert.Contains(tagged.SelectMany(table => table.Tags), IsMeaningful);
    }

    [Fact]
    public void Rich_text_tags_are_exposed()
    {
        var document = Fixture.Load("TagSizes.one");
        Assert.Contains(document.GetChildNodes<RichText>().SelectMany(rt => rt.Tags), IsMeaningful);

        var elements = document.GetChildNodes<OutlineElement>();
        var texts = elements.Select(e => FirstRichText(e).Text).Where(t => t.Length > 0).ToList();
        Assert.True(texts.Count >= 4);
        foreach (var expected in (string[])["66(6-9)", "10(10-17)", "18(18-23)"])
        {
            Assert.Contains(expected, texts);
            Assert.Contains("Важно", FirstRichText(elements.First(e => FirstRichText(e).Text == expected)).Labels);
        }

        var last = elements.First(e => FirstRichText(e).Text is "24(24-…)" or "24(242-…)");
        Assert.Contains("Важно", FirstRichText(last).Labels);
    }

    [Fact]
    public void Outline_element_tags_and_list_metadata()
    {
        var document = Fixture.Load("NumberedListWithTags.one");
        var outlines = document.GetChildNodes<Outline>();
        Assert.Equal(2, outlines.Count);

        var tagSizes = outlines.First(o => o.GetChildNodes<RichText>().Any(rt => rt.Text == "66(6-9)"));
        var tagSizesMap = new Dictionary<string, HashSet<string>>();
        foreach (var element in tagSizes.GetChildNodes<OutlineElement>())
        {
            var (text, labels) = FirstRichText(element);
            if (text.Length > 0)
            {
                tagSizesMap[text] = labels;
            }
        }

        foreach (var key in (string[])["66(6-9)", "10(10-17)", "18(18-23)"])
        {
            Assert.Contains("Важно", tagSizesMap[key]);
        }

        Assert.Contains("Важно", tagSizesMap.First(p => p.Key is "24(24-…)" or "24(242-…)").Value);

        var listOutline = outlines.First(o => o.GetChildNodes<RichText>().Any(rt => rt.Text == "First"));
        var topLevel = listOutline.OfType<OutlineElement>().ToList();
        Assert.Equal(2, topLevel.Count);

        var (text0, labels0) = FirstRichText(topLevel[0]);
        var (text1, labels1) = FirstRichText(topLevel[1]);
        Assert.Equal("First", text0);
        Assert.Equal("Second", text1);
        Assert.Contains("Важно", labels0);
        Assert.Contains("Вопрос", labels0);
        Assert.Contains("Запланировать собрание", labels1);

        var nested = topLevel[0].OfType<OutlineElement>().ToList();
        Assert.True(nested.Count >= 3);
        var nestedMap = nested.Select(FirstRichText).ToDictionary(p => p.Text, p => p.Labels);
        Assert.Superset(new HashSet<string> { "Важно", "Вопрос" }, nestedMap["First-first"]);
        Assert.Superset(new HashSet<string> { "Важно", "Вопрос" }, nestedMap["First-second"]);
        Assert.Superset(new HashSet<string> { "Контакт", "Послушать музыку", "Запланировать собрание" }, nestedMap["First-third"]);

        Assert.All(topLevel, element => Assert.NotNull(element.NumberList));
        var formats = listOutline.GetChildNodes<OutlineElement>().Select(e => e.NumberList?.Format).OfType<string>().ToHashSet();
        Assert.True(formats.Count >= 2);
        Assert.Contains(AllTags(document), IsMeaningful);
    }

    [Fact]
    public void Attachment_fixture_carries_tags()
    {
        var document = Fixture.Load("AttachedFileWithTag.one");
        Assert.NotEmpty(document.GetChildNodes<AttachedFile>());
        Assert.Contains(AllTags(document), IsMeaningful);
    }

    [Fact]
    public void Number_list_header_substitutes_the_number()
    {
        Assert.Equal("3)", new NumberList { Format = "{0})" }.GetNumberedListHeader(3));
        Assert.Equal("•", new NumberList { Format = "•" }.GetNumberedListHeader(3));
        Assert.Equal(string.Empty, new NumberList().GetNumberedListHeader(1));
    }
}
