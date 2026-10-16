namespace XRay.PS.Tests;

/// <summary>Finds the shared test_documents corpus: <c>XRAY_TEST_DOCUMENTS</c>, else the nearest
/// non-empty <c>test_documents</c> directory above the test binaries. Without a corpus the corpus
/// tests skip; with a corpus but a missing file they fail, because the submodule pin is behind.</summary>
internal static class Corpus
{
    private static readonly Lazy<string?> RootDir = new(FindRoot);

    public static string? Root => RootDir.Value;

    private static string? FindRoot()
    {
        string? env = Environment.GetEnvironmentVariable("XRAY_TEST_DOCUMENTS");
        if (!string.IsNullOrWhiteSpace(env))
            return Directory.Exists(env) ? Path.GetFullPath(env) : null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "test_documents");
            // An uninitialised submodule is an empty directory: keep walking past it.
            if (Directory.Exists(candidate) && Directory.EnumerateFileSystemEntries(candidate).Any())
                return candidate;
        }
        return null;
    }

    /// <summary>The full path of a corpus file, skipping the test when there is no corpus.</summary>
    public static string File(string relativePath)
    {
        string root = RequireRoot();
        string path = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(System.IO.File.Exists(path) || Directory.Exists(path),
            $"corpus file {relativePath} is missing under {root}; update the test_documents submodule");
        return path;
    }

    /// <summary>The corpus root, skipping the test when there is no corpus.</summary>
    public static string RequireRoot()
    {
        if (Root is null)
            Assert.Skip("test_documents corpus not found; run `git submodule update --init --depth 1 test_documents` or set XRAY_TEST_DOCUMENTS");
        return Root!;
    }

    /// <summary>Skips unless the opt-in environment variable is set to 1.</summary>
    public static void RequireOptIn(string variable)
    {
        if (Environment.GetEnvironmentVariable(variable) != "1")
            Assert.Skip($"set {variable}=1 to run this sweep");
    }
}
