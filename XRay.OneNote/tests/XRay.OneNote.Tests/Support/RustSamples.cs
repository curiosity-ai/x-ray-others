namespace XRay.OneNote.Tests.Support;

/// <summary>
/// Optional third-party samples (the folder <c>XRAY_ONENOTE_RS_SAMPLES</c> names): section and
/// table-of-contents files from an independent open-source parser's test suite. They are never copied
/// into a repository; tests that use them skip when the variable is unset or the file is absent.
/// </summary>
internal static class RustSamples
{
    public static string? Root
    {
        get
        {
            var value = Environment.GetEnvironmentVariable("XRAY_ONENOTE_RS_SAMPLES");
            return !string.IsNullOrWhiteSpace(value) && Directory.Exists(value) ? value : null;
        }
    }

    /// <summary>The path of a sample, relative to the folder; skips the test when it is unavailable.</summary>
    public static string Require(string relativePath)
    {
        var root = Root;
        if (root is null)
        {
            Assert.Skip("XRAY_ONENOTE_RS_SAMPLES is not set to a samples folder.");
        }

        var path = Path.Combine(root, relativePath);
        if (!File.Exists(path))
        {
            Assert.Skip($"Sample {relativePath} is not in {root}.");
        }

        return path;
    }
}
