namespace XRay.Email.Tests;

/// <summary>
/// Locates the shared curiosity-ai/test_documents corpus: <c>XRAY_TEST_DOCUMENTS</c> when set,
/// otherwise the first directory named <c>test_documents</c> above the test binaries (the
/// submodule at x-ray-others/test_documents, or a clone next to x-ray-others).
/// </summary>
internal static class TestDocuments
{
    private static readonly Lazy<string?> CachedRoot = new(FindRoot);

    public static string? Root => CachedRoot.Value;

    /// <summary>Returns the corpus root, or skips the test when the corpus is not available.</summary>
    public static string RequireRoot()
    {
        return Root ?? throw SkipException();
    }

    /// <summary>
    /// Returns the absolute path of a corpus file. Skips when there is no corpus; fails when the
    /// corpus is present but the file is not, since that means the submodule pin is behind the test.
    /// </summary>
    public static string RequireFile(string relativePath)
    {
        var root = RequireRoot();
        var path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Fixture '{relativePath}' is missing from the corpus at '{root}'. Update the test_documents submodule.");
        return path;
    }

    private static Exception SkipException()
    {
        Assert.Skip("test_documents not found. Run 'git submodule update --init --depth 1 test_documents' or set XRAY_TEST_DOCUMENTS.");
        return new InvalidOperationException("unreachable");
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
            // An uninitialised submodule is an empty directory: treat it as absent.
            if (Directory.Exists(candidate) && Directory.EnumerateFileSystemEntries(candidate).Any())
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
