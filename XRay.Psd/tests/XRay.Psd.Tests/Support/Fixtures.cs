using XRay.Psd.Imaging;

namespace XRay.Psd.Tests.Support;

/// <summary>
/// Locates the PSD fixtures in the shared curiosity-ai/test_documents corpus (its <c>psd/</c>
/// folder, with the psd-tools subset in <c>psd/psd-tools/</c>).
/// </summary>
/// <remarks>
/// The corpus root is <c>XRAY_TEST_DOCUMENTS</c> when set, otherwise the first non-empty directory
/// named <c>test_documents</c> found walking up from the test output directory (the
/// x-ray-others submodule, or a clone next to x-ray-others). When there is no corpus, tests that
/// need it are skipped; when the corpus is there but a file is missing, they fail.
/// </remarks>
internal static class Fixtures
{
    /// <summary>Environment variable that names the test_documents root explicitly.</summary>
    public const string EnvironmentVariable = "XRAY_TEST_DOCUMENTS";

    /// <summary>The single theory row produced when the corpus is absent; every test given it skips.</summary>
    public const string MissingCorpus = "(test_documents not found)";

    private static readonly Lazy<string?> CorpusRoot = new(FindCorpusRoot);

    private static readonly Lazy<string> PackageRootPath = new(() =>
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "XRay.Psd.slnx")))
            {
                return directory.FullName;
            }
        }

        return AppContext.BaseDirectory;
    });

    /// <summary>True when the test_documents corpus was found.</summary>
    public static bool IsAvailable => CorpusRoot.Value is not null;

    /// <summary>The <c>psd/</c> folder of the corpus. Skips the calling test when there is no corpus.</summary>
    public static string RootDirectory
    {
        get
        {
            var root = CorpusRoot.Value;
            if (root is null)
            {
                Assert.Skip(
                    "test_documents not found: run 'git submodule update --init --depth 1 test_documents' in x-ray-others, " +
                    "clone curiosity-ai/test_documents next to it, or set " + EnvironmentVariable + " to its root.");
            }

            var psd = Path.Combine(root, "psd");
            if (!Directory.Exists(psd))
            {
                throw new DirectoryNotFoundException($"test_documents was found at {root} but has no psd/ folder; update the corpus.");
            }

            return psd;
        }
    }

    /// <summary>The XRay.Psd folder (where XRay.Psd.slnx is), for test-output/ and local-test-fixtures/.</summary>
    public static string PackageRoot => PackageRootPath.Value;

    public static string PathOf(string name) => Path.Combine(RootDirectory, name);

    public static PsdDocument Load(string name) => PsdDocument.Load(PathOf(name));

    /// <summary>A file from the psd-tools subset (test_documents/psd/psd-tools), by its path relative to psd-tools' tests/psd_files.</summary>
    public static string PsdTools(string relativePath) =>
        Path.Combine(RootDirectory, "psd-tools", relativePath);

    /// <summary>
    /// The Photoshop reference capture of a fixture: the .bmp beside it, or the one
    /// capture whose name differs (the Blend If round-trip fixture's render).
    /// </summary>
    public static string ReferenceBmpOf(string name)
    {
        var path = Path.ChangeExtension(PathOf(name), ".bmp");
        if (!File.Exists(path) && name.EndsWith("-roundtrip.psd", StringComparison.Ordinal))
        {
            path = PathOf(name[..^"-roundtrip.psd".Length] + "-render.bmp");
        }

        return path;
    }

    /// <summary>Every PSD and PSB directly in the fixture folder. Skips the calling test when there is no corpus.</summary>
    public static IEnumerable<string> AllDocuments() =>
        Directory.EnumerateFiles(RootDirectory)
            .Where(f => f.EndsWith(".psd", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".psb", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .OfType<string>()
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>
    /// <see cref="AllDocuments"/> for theory data, which is enumerated at discovery where a skip
    /// cannot be raised: without a corpus it is the single <see cref="MissingCorpus"/> row, and
    /// the test given that row skips when it asks for the file.
    /// </summary>
    public static IEnumerable<string> TheoryDocuments() => IsAvailable ? AllDocuments() : [MissingCorpus];

    /// <summary>Writes a diagnostic image under test-output/ in the XRay.Psd folder.</summary>
    public static void SaveArtifact(string name, RgbaImage image)
    {
        var output = Path.Combine(PackageRoot, "test-output");
        Directory.CreateDirectory(output);
        image.SavePng(Path.Combine(output, name));
    }

    private static string? FindCorpusRoot()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } configured)
        {
            return Directory.Exists(configured)
                ? Path.GetFullPath(configured)
                : throw new DirectoryNotFoundException($"{EnvironmentVariable} is set to {configured}, which does not exist.");
        }

        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            // An uninitialized submodule is an empty directory: keep walking past it.
            var candidate = new DirectoryInfo(Path.Combine(directory.FullName, "test_documents"));
            if (candidate.Exists && candidate.EnumerateFileSystemInfos().Any())
            {
                return candidate.FullName;
            }
        }

        return null;
    }
}
