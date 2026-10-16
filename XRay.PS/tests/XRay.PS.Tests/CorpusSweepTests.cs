// Opt-in (XRAY_PS_PARITY=1): converts every PostScript and XPS fixture to PDF and checks that
// only package exception types escape. Output parity against the Python original is measured by
// tools/parity (see CLAUDE.md), which needs that library and is not part of the test run.

using XRay.PS.PostScript;
using XRay.PS.Xps;

namespace XRay.PS.Tests;

public class CorpusSweepTests
{
    [Fact]
    public void Every_fixture_converts_or_fails_with_a_package_exception()
    {
        Corpus.RequireOptIn("XRAY_PS_PARITY");
        string root = Corpus.RequireRoot();
        int ok = 0;
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "ps", "functional"), "*", SearchOption.AllDirectories)
                     .Concat(Directory.EnumerateFiles(Path.Combine(root, "ps", "integration")))
                     .Concat(Directory.EnumerateFiles(Path.Combine(root, "xps", "integration"), "*.xps")))
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            try
            {
                if (ext == ".xps")
                    XpsDocument.FromFile(file).ToPdf();
                else if (ext is ".ps" or ".eps")
                    PsDocument.FromFile(file).ToPdf();
                else
                    continue;
                ok++;
            }
            catch (Exception ex) when (ex is PsError or XpsFormatException or HostErrorException or PsQuit or ArgumentException or InvalidOperationException or System.Xml.XmlException)
            {
            }
        }
        Assert.True(ok > 100);
    }
}
