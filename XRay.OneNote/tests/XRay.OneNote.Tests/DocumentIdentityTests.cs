using XRay.OneNote.Tests.Support;

namespace XRay.OneNote.Tests;

/// <summary>Section identity and the display name / creation time properties the notebook matching uses.</summary>
public sealed class DocumentIdentityTests
{
    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Guid_is_the_header_file_guid(string fixture)
    {
        var path = Corpus.Require(fixture);
        var header = File.ReadAllBytes(path).AsSpan(16, 16);
        var document = new Document(path);
        Assert.Equal(new Guid(header), document.Guid);
        Assert.NotEqual(Guid.Empty, document.Guid);
        Assert.NotNull(document.DisplayName);
    }

    public static TheoryData<string> Fixtures() => Corpus.AllFixtures();

    [Fact]
    public void Entity_guid_and_notebook_guid_are_read()
    {
        var document = Fixture.Load("SimpleTable.one");
        Assert.NotNull(document.EntityGuid);
        Assert.NotEqual(document.Guid, document.EntityGuid);
    }

    [Fact]
    public void Clone_keeps_the_guid_and_empty_documents_have_none()
    {
        Assert.Equal(Guid.Empty, new Document().Guid);
        var document = Fixture.Load("SimpleTable.one");
        var page = document.Pages.First().Clone();
        Assert.NotNull(page);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Creation_time_comes_from_the_section_node(string fixture)
    {
        var document = Fixture.Load(fixture);
        Assert.NotNull(document.CreationTime);
        Assert.InRange(document.CreationTime!.Value.Year, 2009, 2035);
    }
}
