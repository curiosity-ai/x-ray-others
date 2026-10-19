using XRay.OneNote.Internal.OneStore;
using XRay.OneNote.Tests.Support;

namespace XRay.OneNote.Tests;

/// <summary>Pages come in the order the section's page series name their object spaces.</summary>
public sealed class PageOrderTests
{
    [Fact]
    public void Object_space_references_are_decoded_from_the_osid_stream()
    {
        var guid = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var table = new Dictionary<uint, Guid> { [1] = guid };

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0u);                // OID stream: no entries
        writer.Write(2u);                // OSID stream: two entries
        writer.Write((1u << 8) | 2u);
        writer.Write((1u << 8) | 3u);
        writer.Write((ushort)1);         // one property
        writer.Write(0x2C001D63u);       // ChildGraphSpaceElementNodes: array of object space ids
        writer.Write(2u);                // element count
        writer.Flush();

        var set = PropertySetParser.ParseObjectBlob(stream.ToArray(), table);
        var value = set.Get(PropertyNames.ChildGraphSpaceElementNodes);
        Assert.Equal(PropertyKind.List, value.Kind);
        Assert.Equal(
            [new ExtendedGuid(guid, 2), new ExtendedGuid(guid, 3)],
            value.Items.Select(item => item.AsObjectSpaceId()!.Value).ToArray());
    }

    [Fact]
    public void Checkboxes_sample_pages_are_in_series_order()
    {
        var document = new Document(RustSamples.Require("joplin/checkboxes_and_unicode.one"));
        var titles = document.Pages.Select(page => page.Title?.TitleText?.Text ?? "").ToList();
        Assert.Equal(4, titles.Count);
        Assert.StartsWith("Section 2 - simple page 1", titles[0]);
        Assert.StartsWith("Section 2 - avec titre", titles[1]);
        Assert.StartsWith("Test niveau de titre", titles[2]);
        Assert.StartsWith("Test Todo", titles[3]);
    }

    [Fact]
    public void Page_creation_time_comes_from_the_page_metadata()
    {
        var document = new Document(RustSamples.Require("joplin/checkboxes_and_unicode.one"));
        var created = document.Pages.Select(page => page.CreationTime).ToList();
        Assert.Equal(new DateTime(2025, 10, 21, 11, 48, 58, DateTimeKind.Utc), created[0]!.Value.AddTicks(-(created[0]!.Value.Ticks % TimeSpan.TicksPerSecond)));
        Assert.Equal(new DateTime(2025, 10, 21, 11, 55, 31, DateTimeKind.Utc), created[1]!.Value.AddTicks(-(created[1]!.Value.Ticks % TimeSpan.TicksPerSecond)));
        Assert.All(created, time => Assert.NotNull(time));
    }

    [Fact]
    public void Corpus_pages_with_page_metadata_have_a_creation_time()
    {
        var document = Fixture.Load("Issue1Subpages.one");
        Assert.All(document.Pages, page => Assert.NotNull(page.CreationTime));
    }
}
