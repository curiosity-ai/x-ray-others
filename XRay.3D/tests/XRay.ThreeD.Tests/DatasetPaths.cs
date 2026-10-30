namespace XRay.ThreeD.Tests;

/// <summary>Finds XRay.3D/dataset above the test binaries. The dataset is committed with the repository, so tests never skip.</summary>
internal static class DatasetPaths
{
    public static string Root { get; } = Find();

    public static string File(string relative)
    {
        var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(System.IO.File.Exists(path), $"dataset file missing: {relative}");
        return path;
    }

    public static IEnumerable<string> Files(string folder, string pattern = "*") =>
        Directory.EnumerateFiles(Path.Combine(Root, folder), pattern).OrderBy(f => f, StringComparer.Ordinal);

    private static string Find()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "dataset");
            if (Directory.Exists(candidate) && Directory.Exists(Path.Combine(candidate, "sldprt")))
            {
                return candidate;
            }

            var nested = Path.Combine(directory.FullName, "XRay.3D", "dataset");
            if (Directory.Exists(nested))
            {
                return nested;
            }
        }

        throw new DirectoryNotFoundException("XRay.3D/dataset not found above " + AppContext.BaseDirectory);
    }

    public static string CorpusFile(string name) => Path.Combine(AppContext.BaseDirectory, "Corpus", name);
}
