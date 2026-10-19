using XRay.OneNote.Tests.Support;

namespace XRay.OneNote.Tests;

/// <summary>
/// FSSHTTPB packages (MS-ONESTORE 2.8: OneNote online, OneDrive downloads) load into the same document model as
/// classic section files. Checked against what an independent parser reports for its sample files, which are
/// not part of this repository (XRAY_ONENOTE_RS_SAMPLES).
/// </summary>
public sealed class FsshttpbTests
{
    // The independent parser takes a page's title from its first text when no title is stored; this package
    // leaves such a page untitled. (file, page index)
    private static readonly HashSet<(string, int)> UntitledByDesign =
    [
        ("joplin/notebook_with_chinese_char_on_link/Quick Notes.one", 1),
    ];

    public static TheoryData<string> Samples()
    {
        var data = new TheoryData<string>();
        foreach (var expected in RustSampleExpectations.All)
        {
            data.Add(expected.Path);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public void Identity_name_levels_and_titles_match_the_independent_parser(string relativePath)
    {
        var expected = RustSampleExpectations.All.Single(e => e.Path == relativePath);
        var document = new Document(RustSamples.Require(relativePath));

        Assert.Equal(Guid.Parse(expected.Identity), document.Guid);
        Assert.Equal(expected.DisplayName, document.DisplayName);
        var pages = document.Pages.ToList();
        Assert.Equal(expected.Levels, pages.Select(page => page.Level ?? 0).ToArray());
        for (var index = 0; index < pages.Count; index++)
        {
            var title = pages[index].Title?.TitleText?.Text ?? string.Empty;
            if (UntitledByDesign.Contains((relativePath, index)) || string.IsNullOrEmpty(expected.Titles[index]))
            {
                continue;
            }

            Assert.Equal(expected.Titles[index], title.Trim());
        }
    }

    [Fact]
    public void Pre_cancelled_load_of_a_package_throws()
    {
        var path = RustSamples.Require("joplin/Simple notebook/Quick Notes.one");
        using var source = new CancellationTokenSource();
        source.Cancel();
        Assert.Throws<OperationCanceledException>(() => Document.Load(path, null, source.Token));
    }

    [Fact]
    public void A_package_without_pages_data_is_reported_cleanly()
    {
        var data = File.ReadAllBytes(RustSamples.Require("non-legacy/New Section 3.one"));
        // Cut inside the data element package: whatever was read stays, nothing else escapes.
        var cut = data[..(data.Length / 2)];
        try
        {
            _ = new Document(new MemoryStream(cut));
        }
        catch (OneNoteException)
        {
        }
    }

    [Fact]
    public void A_notebook_table_of_contents_is_not_a_section()
    {
        var path = RustSamples.Require("joplin/Simple notebook/.onetoc2");
        Assert.Throws<UnsupportedFileFormatException>(() => new Document(path));
    }
}
