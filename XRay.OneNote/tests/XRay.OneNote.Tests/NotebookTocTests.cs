using XRay.OneNote.Tests.Support;

namespace XRay.OneNote.Tests;

/// <summary>
/// The notebook table of contents: synthetic files written by the inverse of the parser (see
/// <see cref="SyntheticToc"/>), and, when the optional third-party samples are available
/// (XRAY_ONENOTE_RS_SAMPLES), files written by OneNote desktop and OneNote online.
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

    [Fact]
    public void A_later_reference_to_a_file_name_wins_and_names_are_decoded()
    {
        var toc = NotebookToc.Load(
            new MemoryStream(SyntheticToc.Build(
                Notebook,
                Guid.Empty,
                null,
                null,
                [
                    new SyntheticToc.Entry("Notes^Mand^Jmore.one", SectionA, 5, 0x00000001),
                    new SyntheticToc.Entry("Other.one", SectionB, 6, null),
                    new SyntheticToc.Entry("Notes^Mand^Jmore.one", SectionA, 9, 0x00000002),
                ])),
            TestContext.Current.CancellationToken);
        Assert.Equal(["Other.one", "Notes+and,more.one"], toc.Entries.Select(e => e.FileName));
        Assert.Equal(9u, toc.Entries[1].OrderingId);
        Assert.Equal(2u, toc.Entries[1].Color);
    }

    [Fact]
    public void Ties_in_the_ordering_id_keep_file_order()
    {
        var toc = NotebookToc.Load(
            new MemoryStream(SyntheticToc.Build(Notebook, Guid.Empty, null, null,
            [
                new SyntheticToc.Entry("C.one", null, 1, null),
                new SyntheticToc.Entry("A.one", null, 1, null),
                new SyntheticToc.Entry("B.one", null, 0, null),
            ])),
            TestContext.Current.CancellationToken);
        Assert.Equal(["B.one", "C.one", "A.one"], toc.Entries.Select(e => e.FileName));
    }

    [Fact]
    public void Revisions_declared_with_reference_counts_are_read()
    {
        var data = SyntheticToc.BuildWithRefCountNodes(
            Notebook,
            0x00ABCDEF,
            [new SyntheticToc.Entry("One.one", SectionA, 1, null), new SyntheticToc.Entry("Two.one", SectionB, 2, 0x00000099)]);
        var toc = NotebookToc.Load(new MemoryStream(data), TestContext.Current.CancellationToken);
        Assert.Equal(0x00ABCDEFu, toc.Color);
        Assert.Equal(["One.one", "Two.one"], toc.Entries.Select(e => e.FileName));
        Assert.Equal([SectionA, SectionB], toc.Entries.Select(e => e.Guid!.Value));
        Assert.Equal(0x99u, toc.Entries[1].Color);
    }

    [Fact]
    public void Section_groups_are_read_from_their_directories_and_the_recycle_bin_is_not_entered()
    {
        var root = Path.Combine(Path.GetTempPath(), "xray-onenote-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "Group", "Inner"));
            Directory.CreateDirectory(Path.Combine(root, "OneNote_RecycleBin"));
            File.WriteAllBytes(Path.Combine(root, ".onetoc2"), SyntheticToc.Build(Notebook, Guid.Empty, null, null,
            [
                new SyntheticToc.Entry("Top.one", SectionA, 1, null),
                new SyntheticToc.Entry("Group", null, 2, null),
                new SyntheticToc.Entry("OneNote_RecycleBin", null, 3, null),
            ]));
            File.WriteAllBytes(Path.Combine(root, "Group", "Open Notebook.onetoc2"), SyntheticToc.Build(Guid.NewGuid(), Guid.Empty, null, null,
            [
                new SyntheticToc.Entry("Member.one", SectionB, 1, null),
                new SyntheticToc.Entry("Inner", null, 2, null),
            ]));
            File.WriteAllBytes(Path.Combine(root, "Group", "Inner", ".onetoc2"), SyntheticToc.Build(Guid.NewGuid(), Guid.Empty, null, null,
            [
                new SyntheticToc.Entry("Deep.one", null, 1, null),
                new SyntheticToc.Entry("Inner", null, 2, null),
            ]));
            File.WriteAllBytes(Path.Combine(root, "OneNote_RecycleBin", ".onetoc2"), SyntheticToc.Build(Guid.NewGuid(), Guid.Empty, null, null,
            [new SyntheticToc.Entry("Deleted.one", null, 1, null)]));

            var toc = NotebookToc.Load(Path.Combine(root, ".onetoc2"), TestContext.Current.CancellationToken);
            Assert.Equal(["Top.one", "Group", "OneNote_RecycleBin"], toc.Entries.Select(e => e.FileName));
            var group = toc.Entries[1];
            Assert.True(group.IsFolder);
            Assert.Equal(["Member.one", "Inner"], group.Children.Select(e => e.FileName));
            Assert.Equal(["Deep.one", "Inner"], group.Children[1].Children.Select(e => e.FileName));
            Assert.Empty(group.Children[1].Children[1].Children);
            Assert.Empty(toc.Entries[2].Children);
            Assert.Empty(toc.Entries[0].Children);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("joplin/Notebook created on OneNote App/Abrir Bloco de Anotações.onetoc2", "d890422d-763f-4156-bbd9-0760ed3342cd", "Section.one,OneNote_RecycleBin,Section A,Section D")]
    [InlineData("object-revision/Open Notebook.onetoc2", "9cf59d79-cd9e-4936-9333-44274544b327", "New Section 1 2.one,New Section 2.one,New Section 3.one,Ink.one")]
    [InlineData("non-legacy/Open Notebook.onetoc2", "f1da443f-a65f-4513-b200-78d8a9910b8d", "New Section 1 2.one,New Section 2.one,New Section 3.one,New Section Group,OneNote_RecycleBin")]
    [InlineData("joplin/aaa/.onetoc2", "d4ffb4ad-948d-4c17-861d-f4aed81266b3", "Quick Notes.one")]
    public void Real_notebooks_list_their_entries(string relativePath, string identity, string names)
    {
        var toc = NotebookToc.Load(RustSamples.Require(relativePath), TestContext.Current.CancellationToken);
        Assert.Equal(Guid.Parse(identity), toc.Guid);
        Assert.Equal(names.Split(','), toc.Entries.Select(e => e.FileName).ToArray());
    }

    [Fact]
    public void A_real_notebooks_sections_carry_the_identity_of_their_files()
    {
        var path = RustSamples.Require("joplin/Notebook created on OneNote App/Abrir Bloco de Anotações.onetoc2");
        var toc = NotebookToc.Load(path, TestContext.Current.CancellationToken);
        var group = toc.Entries.Single(e => e.FileName == "Section A");
        Assert.Equal(["Section A1.one", "Section B"], group.Children.Select(e => e.FileName));
        var directory = Path.GetDirectoryName(path)!;
        var section = new Document(Path.Combine(directory, "Section A", "Section A1.one"));
        Assert.Equal(section.Guid, group.Children[0].Guid);
        Assert.Equal(["Section B1.one"], group.Children[1].Children.Select(e => e.FileName));
    }

    [Fact]
    public void A_section_group_toc_in_a_package_lists_its_sections_with_their_colors()
    {
        var toc = NotebookToc.Load(RustSamples.Require("New Section Group/Open Notebook.onetoc2"), TestContext.Current.CancellationToken);
        Assert.Equal(["New Section 1.one", "New Section 2.one"], toc.Entries.Select(e => e.FileName));
        Assert.Equal(new uint?[] { 0xD2BB9Bu, 0xB79CABu }, toc.Entries.Select(e => e.Color).ToArray());
    }
}
