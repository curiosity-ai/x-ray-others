using XRay.OneNote.Tests.Support;

namespace XRay.OneNote.Tests;

/// <summary>
/// The notebook table of contents is best effort: these tests read synthetic files written by the inverse of
/// the parser (see <see cref="SyntheticToc"/>), not files written by OneNote.
/// </summary>
public sealed class NotebookTocTests
{
    private static readonly Guid Notebook = new("11111111-2222-3333-4444-555555555555");
    private static readonly Guid SectionA = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid SectionB = new("bbbbbbbb-0000-0000-0000-000000000002");

    private static byte[] Sample() => SyntheticToc.Build(
        Notebook,
        Guid.Empty,
        notebookColor: 0x00FF0000,
        history: true,
        [
            new SyntheticToc.Entry("Second.one", SectionB, 20, 0x0000FF00),
            new SyntheticToc.Entry("First.one", SectionA, 10, 0xFFFFFFFF),
            new SyntheticToc.Entry("Folder", null, 30, null),
        ]);

    [Fact]
    public void Reads_identity_color_and_ordered_entries()
    {
        var toc = NotebookToc.Load(new MemoryStream(Sample()), TestContext.Current.CancellationToken);
        Assert.Equal(Notebook, toc.Guid);
        Assert.Equal(0x00FF0000u, toc.Color);
        Assert.True(toc.EnableHistory);
        Assert.Equal(["First.one", "Second.one", "Folder"], toc.Entries.Select(e => e.FileName));
        Assert.Equal([SectionA, SectionB], toc.Sections.Select(e => e.Guid!.Value));
        Assert.Null(toc.Entries[0].Color);
        Assert.Equal(0x0000FF00u, toc.Entries[1].Color);
        Assert.True(toc.Entries[2].IsFolder);
        Assert.Equal(10u, toc.Entries[0].OrderingId);
    }

    [Fact]
    public void Loads_from_a_path_and_names_the_notebook_after_the_file()
    {
        var path = Path.Combine(Path.GetTempPath(), "xray-onenote-" + Guid.NewGuid().ToString("N") + ".onetoc2");
        try
        {
            File.WriteAllBytes(path, Sample());
            var toc = NotebookToc.Load(path, TestContext.Current.CancellationToken);
            Assert.Equal(Path.GetFileNameWithoutExtension(path), toc.DisplayName);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Empty_notebook_has_no_entries()
    {
        var toc = NotebookToc.Load(new MemoryStream(SyntheticToc.Build(Notebook, Guid.Empty, null, null, [])), TestContext.Current.CancellationToken);
        Assert.Empty(toc.Entries);
        Assert.Null(toc.Color);
        Assert.Null(toc.EnableHistory);
    }

    [Fact]
    public void A_section_file_is_not_a_table_of_contents()
    {
        var section = SyntheticToc.Build(Notebook, Guid.Empty, null, null, [], section: true);
        Assert.Throws<UnsupportedFileFormatException>(() => NotebookToc.Load(new MemoryStream(section), TestContext.Current.CancellationToken));
        var path = Corpus.Require("SimpleTable.one");
        using var stream = File.OpenRead(path);
        Assert.Throws<UnsupportedFileFormatException>(() => NotebookToc.Load(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Damaged_data_only_raises_the_packages_exceptions()
    {
        var data = Sample();
        for (var length = 0; length < data.Length; length += 37)
        {
            try
            {
                _ = NotebookToc.Load(new MemoryStream(data[..length]), TestContext.Current.CancellationToken);
            }
            catch (OneNoteException)
            {
            }
        }

        var random = new Random(7);
        for (var round = 0; round < 300; round++)
        {
            var copy = (byte[])data.Clone();
            for (var i = 0; i < 4; i++)
            {
                copy[random.Next(1024, copy.Length)] = (byte)random.Next(256);
            }

            try
            {
                _ = NotebookToc.Load(new MemoryStream(copy), TestContext.Current.CancellationToken);
            }
            catch (OneNoteException)
            {
            }
        }
    }

    [Fact]
    public void Cancelled_token_throws()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() => NotebookToc.Load(new MemoryStream(Sample()), source.Token));
    }

    [Fact]
    public void Section_guid_is_the_file_guid_a_toc_entry_names()
    {
        var document = Fixture.Load("SimpleTable.one");
        Assert.NotEqual(Guid.Empty, document.Guid);
        var toc = NotebookToc.Load(
            new MemoryStream(SyntheticToc.Build(Notebook, Guid.Empty, null, null, [new SyntheticToc.Entry("SimpleTable.one", document.Guid, 1, null)])),
            TestContext.Current.CancellationToken);
        Assert.Equal("SimpleTable.one", toc.Sections.Single(s => s.Guid == document.Guid).FileName);
    }
}
