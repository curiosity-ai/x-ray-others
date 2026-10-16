using System.Diagnostics;
using XRay.OneNote.Internal.MsOne;
using XRay.OneNote.Internal.OneStore;
using XRay.OneNote.Internal.Pdf;
using XRay.OneNote.Saving;
using XRay.OneNote.Tests.Support;

namespace XRay.OneNote.Tests;

/// <summary>The family's cancellation convention: pre-cancelled tokens throw, a token cancelled mid-way returns promptly, results do not change.</summary>
public sealed class CancellationTests
{
    private static CancellationToken Cancelled()
    {
        var source = new CancellationTokenSource();
        source.Cancel();
        return source.Token;
    }

    /// <summary>A deliberately large document: enough paragraphs that any export takes far longer than the cancel delay.</summary>
    private static Document Large(int paragraphs)
    {
        var document = new Document { DisplayName = "Large" };
        for (var p = 0; p < 4; p++)
        {
            var page = document.AppendChildLast(new Page { Title = new Title(new RichText("Page " + p)) });
            var outline = page.AppendChildLast(new Outline());
            for (var i = 0; i < paragraphs / 4; i++)
            {
                outline.AppendChildLast(new OutlineElement()).AppendChildLast(new RichText("Paragraph number " + i + " with a few words to wrap around the page width for a while and then some more."));
            }
        }

        return document;
    }

    [Fact]
    public void Load_with_a_cancelled_token_throws()
    {
        var path = Corpus.Require("SimpleTable.one");
        using var stream = File.OpenRead(path);
        Assert.Throws<OperationCanceledException>(() => Document.Load(stream, null, Cancelled()));
        Assert.Throws<OperationCanceledException>(() => Document.Load(path, null, Cancelled()));
    }

    [Fact]
    public void Parser_and_loader_loops_check_the_token()
    {
        var data = File.ReadAllBytes(Corpus.Require("SimpleHistory.one"));
        Assert.Throws<OperationCanceledException>(() => OneStoreParser.Parse(data, null, Cancelled()));
        Assert.Throws<OperationCanceledException>(() => OneNoteLoader.Load(data, null, includePageHistory: true, Cancelled()));
        var parsed = OneStoreParser.Parse(data, null, TestContext.Current.CancellationToken);
        Assert.NotEmpty(parsed.ObjectSpaces);
    }

    [Fact]
    public void Image_decoding_checks_the_token()
    {
        var png = Fixture.Load("3ImagesWithDifferentAlignment.one").GetChildNodes<Image>().First(i => i.Bytes.Length > 0).Bytes;
        Assert.Throws<OperationCanceledException>(() => ImageDecoder.Decode(png, Cancelled()));
        Assert.NotNull(ImageDecoder.Decode(png, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Save_with_a_cancelled_token_throws_and_leaves_no_file()
    {
        var document = Fixture.Load("SimpleTable.one");
        Assert.Throws<OperationCanceledException>(() => document.Save(Stream.Null, new PdfSaveOptions(), Cancelled()));
        Assert.Throws<OperationCanceledException>(() => document.Save(Stream.Null, new HtmlSaveOptions { PerPage = false }, Cancelled()));
        Assert.Throws<OperationCanceledException>(() => document.Save(Stream.Null, new HtmlSaveOptions { PerPage = false, CancellationToken = Cancelled() }, TestContext.Current.CancellationToken));
        var path = Path.Combine(Path.GetTempPath(), "xray-onenote-" + Guid.NewGuid().ToString("N") + ".pdf");
        Assert.Throws<OperationCanceledException>(() => document.Save(path, Cancelled()));
        Assert.False(File.Exists(path));
        Assert.Throws<OperationCanceledException>(() => HtmlWriter.WritePage(document.Pages.First(), Stream.Null, null, 0, Cancelled()));
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData("html")]
    public void Cancelling_mid_way_returns_promptly(string format)
    {
        var document = Large(120_000);
        using var source = new CancellationTokenSource();
        source.CancelAfter(50);
        SaveOptions options = format == "pdf" ? new PdfSaveOptions() : new HtmlSaveOptions { PerPage = false };
        var watch = Stopwatch.StartNew();
        Assert.Throws<OperationCanceledException>(() => document.Save(Stream.Null, options, source.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Took {watch.Elapsed}");
    }

    [Fact]
    public void A_live_token_changes_nothing()
    {
        using var source = new CancellationTokenSource();
        foreach (var fixture in new[] { "FormattedRichText.one", "SimpleTable.one", "3ImagesWithDifferentAlignment.one" })
        {
            var path = Corpus.Require(fixture);
            var plain = new Document(path);
            var withToken = Document.Load(path, null, source.Token);
            Assert.Equal(plain.GetChildNodes<RichText>().Select(r => r.Text), withToken.GetChildNodes<RichText>().Select(r => r.Text));
            Assert.Equal(plain.Guid, withToken.Guid);

            using var first = new MemoryStream();
            using var second = new MemoryStream();
            plain.Save(first, new PdfSaveOptions(), TestContext.Current.CancellationToken);
            plain.Save(second, new PdfSaveOptions(), source.Token);
            Assert.Equal(first.ToArray(), second.ToArray());

            using var third = new MemoryStream();
            using var fourth = new MemoryStream();
            plain.Save(third, new HtmlSaveOptions { PerPage = false }, TestContext.Current.CancellationToken);
            plain.Save(fourth, new HtmlSaveOptions { PerPage = false, CancellationToken = source.Token }, TestContext.Current.CancellationToken);
            Assert.Equal(third.ToArray(), fourth.ToArray());
        }
    }

    [Fact]
    public void Pdf_written_to_a_stream_equals_the_byte_array_form()
    {
        var document = Fixture.Load("NumberedListWithTags.one");
        using var stream = new MemoryStream();
        document.Save(stream, new PdfSaveOptions(), TestContext.Current.CancellationToken);
        var path = Path.Combine(Path.GetTempPath(), "xray-onenote-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            document.Save(path, new PdfSaveOptions(), TestContext.Current.CancellationToken);
            Assert.Equal(stream.ToArray(), File.ReadAllBytes(path));
            Assert.StartsWith("%PDF-1.4", System.Text.Encoding.ASCII.GetString(stream.ToArray(), 0, 8), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
