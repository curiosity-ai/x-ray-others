using System.Text.Json.Nodes;
using XRay.OneNote.Tests.Support;

namespace XRay.OneNote.Tests;

/// <summary>
/// The PDF export of every corpus fixture shows what the Python reference's export shows
/// (Goldens/pdf, pypdf manifests): the same page count, link targets and image count per page, and
/// the same text once white space (which depends on how each writer splits draw calls) is removed.
/// </summary>
public sealed class PdfParityTests
{
    public static TheoryData<string> Fixtures => Corpus.AllFixtures();

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Pdf_matches_python_reference(string fixture)
    {
        var path = Corpus.Require(fixture);
        var pdf = new PdfInspector(TestFonts.ExportPdf(new Document(path)));
        var expected = Goldens.Load("pdf", fixture);

        Assert.Equal(expected["page_count"]!.GetValue<int>(), pdf.Pages.Count);
        var pages = expected["pages"]!.AsArray();
        for (var index = 0; index < pages.Count; index++)
        {
            var page = pages[index]!;
            var actual = pdf.Pages[index];
            Assert.Equal(PdfInspector.Squash(page["text"]!.GetValue<string>()), PdfInspector.Squash(actual.Text));
            Assert.Equal(page["links"]!.AsArray().Select(link => link!.GetValue<string>()), actual.Links);
            Assert.Equal(page["image_count"]!.GetValue<int>(), actual.ImageCount);
        }
    }
}
