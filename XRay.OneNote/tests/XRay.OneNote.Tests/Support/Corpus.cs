namespace XRay.OneNote.Tests.Support;

/// <summary>
/// Locates the shared curiosity-ai/test_documents corpus: <c>XRAY_TEST_DOCUMENTS</c> when set, else
/// the first directory named <c>test_documents</c> walking up from the test binaries.
/// </summary>
internal static class Corpus
{
    private static readonly Lazy<string?> RootPath = new(FindRoot);

    /// <summary>The fixtures this package reads, all under <c>onenote/</c>.</summary>
    public static readonly string[] Fixtures =
    [
        "3ImagesWithDifferentAlignment.one",
        "AttachedFileWithTag.one",
        "FormattedRichText.one",
        "ImageWithTag.one",
        "Issue1Subpages.one",
        "NumberedListWithTags.one",
        "OnePageWithFile.one",
        "SimpleHistory.one",
        "SimpleImageFromSeparateFile.one",
        "SimpleTable.one",
        "TableWithTag.one",
        "TagSizes.one",
    ];

    public static string? Root => RootPath.Value;

    /// <summary>
    /// The path of <c>onenote/<paramref name="name"/></c>. Skips the test when the corpus is absent and
    /// fails it when the corpus is present but the file is not.
    /// </summary>
    public static string Require(string name)
    {
        var root = Root;
        if (root is null)
        {
            Assert.Skip("test_documents not found: run `git submodule update --init --depth 1 test_documents` or set XRAY_TEST_DOCUMENTS.");
        }

        var path = Path.Combine(root, "onenote", name);
        if (!File.Exists(path))
        {
            Assert.Fail($"Fixture onenote/{name} is missing from the corpus at {root}; the corpus is behind the tests.");
        }

        return path;
    }

    public static TheoryData<string> AllFixtures()
    {
        var data = new TheoryData<string>();
        foreach (var fixture in Fixtures)
        {
            data.Add(fixture);
        }

        return data;
    }

    private static string? FindRoot()
    {
        var fromEnvironment = Environment.GetEnvironmentVariable("XRAY_TEST_DOCUMENTS");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return Directory.Exists(fromEnvironment) ? Path.GetFullPath(fromEnvironment) : null;
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "test_documents");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            if (directory.Name == "test_documents")
            {
                return directory.FullName;
            }
        }

        return null;
    }
}
