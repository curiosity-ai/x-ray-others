using XRay.OneNote.Saving;
using XRay.OneNote.Tests.Support;

namespace XRay.OneNote.Tests;

/// <summary>The README quick start, run against a corpus fixture, so the documented API stays real.</summary>
public sealed class ReadmeExampleTests
{
    [Fact]
    public void Quick_start_runs()
    {
        var directory = Directory.CreateTempSubdirectory("xray-onenote-readme-");
        try
        {
            var source = Path.Combine(directory.FullName, "Notes.one");
            File.Copy(Corpus.Require("ImageWithTag.one"), source);
            var lines = new List<string>();

            var document = new Document(source);
            lines.Add(document.DisplayName!);
            foreach (var page in document.Pages)
            {
                lines.Add($"# {page.Title?.TitleText?.Text}  (level {page.Level}, by {page.Author})");
                foreach (var outline in page.GetChildNodes<Outline>())
                {
                    foreach (var richText in outline.GetChildNodes<RichText>())
                    {
                        lines.Add(richText.Text);
                    }
                }
            }

            var text = string.Join("\n", document.GetChildNodes<RichText>().Select(rt => rt.Text));
            foreach (var image in document.GetChildNodes<Image>())
            {
                File.WriteAllBytes(Path.Combine(directory.FullName, image.FileName ?? "image.bin"), image.Bytes);
            }

            var pdf = Path.Combine(directory.FullName, "Notes.pdf");
            document.Save(pdf, TestContext.Current.CancellationToken);
            document.Save(Stream.Null, new PdfSaveOptions { PageIndex = 0, PageCount = 1 }, TestContext.Current.CancellationToken);

            var withHistory = new Document(source, new LoadOptions { LoadHistory = true });
            foreach (var version in withHistory.GetPageHistory(withHistory.Pages.First()))
            {
                lines.Add(version.LastModifiedTime?.ToString("O") ?? string.Empty);
            }

            var fonts = new FontsSubsystem { SearchSystemFontDirectories = false };
            fonts.FontDirectories.Add(TestFonts.Directory);
            document.Save(pdf, new PdfSaveOptions { FontsSubsystem = fonts }, TestContext.Current.CancellationToken);

            Assert.Equal("Notes", lines[0]);
            Assert.Contains("# tyty  (level 1, by Дмитрий)", lines);
            Assert.Contains("tyty", text, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(directory.FullName, "Безымянный рисунок.png")));
            Assert.True(new FileInfo(pdf).Length > 1000);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
