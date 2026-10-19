using XRay.OneNote.Tests.Support;

namespace XRay.OneNote.Tests;

/// <summary>Note tag state: completed, disabled, created and completed times come from the tag's own state set.</summary>
public sealed class TagStateTests
{
    [Fact]
    public void Open_tags_have_no_completion_time_and_a_creation_time()
    {
        var tags = Fixture.Load("NumberedListWithTags.one").GetChildNodes<RichText>().SelectMany(rt => rt.Tags).ToList();
        Assert.NotEmpty(tags);
        Assert.All(tags, tag =>
        {
            Assert.Equal(TagStatus.Open, tag.Status);
            Assert.Null(tag.CompletedTime);
            Assert.NotNull(tag.CreationTime);
            Assert.True(tag.CreationTime!.Value.Year >= 2015);
        });
    }

    [Fact]
    public void A_ticked_checkbox_is_completed()
    {
        var table = Fixture.Load("TableWithTag.one").GetChildNodes<Table>().Single();
        var tag = Assert.Single(table.Tags);
        Assert.Equal(3, tag.Icon);
        Assert.Equal(TagStatus.Completed, tag.Status);
        Assert.NotNull(tag.CompletedTime);
    }

    [Fact]
    public void Checkbox_states_follow_the_independent_parser_on_the_checkbox_sample()
    {
        var document = new Document(RustSamples.Require("joplin/checkboxes_and_unicode.one"));
        var states = document.GetChildNodes<RichText>()
            .SelectMany(rt => rt.Tags.Where(tag => tag.Icon == 3).Select(tag => (rt.Text, tag)))
            .ToList();
        Assert.Equal(7, states.Count);
        Assert.Equal(3, states.Count(s => s.tag.Status == TagStatus.Completed && s.tag.CompletedTime is not null));
        Assert.Equal(4, states.Count(s => s.tag.Status == TagStatus.Open && s.tag.CompletedTime is null));
        var first = states.Single(s => s.Text.StartsWith("Rédiger carnet MS-OneNote", StringComparison.Ordinal));
        Assert.Equal(new DateTime(2025, 10, 23, 7, 26, 57, DateTimeKind.Utc), first.tag.CompletedTime);
    }
}
