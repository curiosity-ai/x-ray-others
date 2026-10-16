// Ported from tests/ps/test_pdf_writer.py, tests/pdf/test_pdf_writer_unicode.py and
// tests/pdf/test_pdf_font_embedding.py.

using System.Text;
using XRay.PS.Fonts;
using XRay.PS.Pdf;
using XRay.PS.PostScript;
using XRay.PS.Rendering;

namespace XRay.PS.Tests;

public sealed class PdfWriterTests : IDisposable
{
    private static readonly PdfMetadata Metadata = new("", "", "XRay.PS", "D:20260101000000", "D:20260101000000", false);
    private readonly string _tempDir = Directory.CreateTempSubdirectory("xrayps-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static RenderDocument Doc(params RenderPage[] pages) => new() { Pages = [.. pages] };

    private static string FirstStream(byte[] pdf)
    {
        string text = Encoding.Latin1.GetString(pdf);
        int start = text.IndexOf("stream\n", StringComparison.Ordinal);
        if (start < 0)
            return "";
        start += "stream\n".Length;
        int end = text.IndexOf("\nendstream", start, StringComparison.Ordinal);
        return text[start..end];
    }

    private static bool Contains(byte[] data, string text) => Encoding.Latin1.GetString(data).Contains(text, StringComparison.Ordinal);

    [Fact]
    public void Writes_a_pdf_1_4_header_and_trailer()
    {
        byte[] pdf = new PdfWriter(Metadata).Write(Doc(new RenderPage(100, 100)));
        Assert.StartsWith("%PDF-1.4", Encoding.Latin1.GetString(pdf));
        Assert.True(Contains(pdf, "trailer\n<<"));
    }

    [Fact]
    public void Rect_paths_use_path_operators()
    {
        var command = new PathCommand(Paths.FromRect(new Rect(0, 0, 10, 20)), new StrokeStyle(1.0, 0, 0, 10.0, [], 0.0), Paint.Rgb(0, 0, 0));
        string content = FirstStream(new PdfWriter(Metadata, noCompression: true).Write(Doc(new RenderPage(100, 100, [command]))));
        Assert.Contains(" m", content);
        Assert.Contains(" l", content);
        Assert.Contains("\nh\n", content);
        Assert.DoesNotContain(" re", content);
    }

    [Fact]
    public void No_compression_disables_the_flate_filter()
    {
        byte[] pdf = new PdfWriter(Metadata, noCompression: true).Write(Doc(new RenderPage(100, 100)));
        Assert.False(Contains(pdf, "/Filter /FlateDecode"));
    }

    [Fact]
    public void Text_emits_text_operators()
    {
        var text = new TextCommand("Hi", "Helvetica", 12.0, Matrix.Identity, Paint.Gray(0.0));
        string content = FirstStream(new PdfWriter(Metadata, noCompression: true).Write(Doc(new RenderPage(100, 100, [text]))));
        foreach (string op in new[] { "BT", "ET", "Tf", "Tj" })
            Assert.Contains(op, content);
    }

    [Fact]
    public void A_missing_image_provider_is_an_error()
    {
        var image = new ImageCommand("img1", 10, 10, Matrix.Identity);
        var ex = Assert.Throws<HostErrorException>(() => new PdfWriter(Metadata).Write(Doc(new RenderPage(100, 100, [image]))));
        Assert.StartsWith("ValueError", ex.Message);
    }

    [Fact]
    public void Overprint_toggles_op_around_the_path()
    {
        var command = new PathCommand(Paths.FromRect(new Rect(0, 0, 10, 20)), new StrokeStyle(1.0, 0, 0, 10.0, [], 0.0), Paint.Cmyk(0, 1, 0, 0), Overprint: true);
        string content = FirstStream(new PdfWriter(Metadata, noCompression: true).Write(Doc(new RenderPage(100, 100, [command]))));
        foreach (string op in new[] { "true op", "true OP", "false op", "false OP" })
            Assert.Contains(op, content);
    }

    [Fact]
    public void Cmyk_paint_is_written_as_rgb()
    {
        var command = new PathCommand(Paths.FromRect(new Rect(0, 0, 10, 20)), null, Paint.Cmyk(1, 0, 1, 0));
        string content = FirstStream(new PdfWriter(Metadata, noCompression: true).Write(Doc(new RenderPage(100, 100, [command]))));
        Assert.Contains("0 1 0 rg", content);
        Assert.DoesNotContain(" k", content);
    }

    [Fact]
    public void Thin_axis_aligned_strokes_become_filled_rectangles()
    {
        var path = new RenderPath([PathSegment.MoveTo(4.5, 650.625), PathSegment.LineTo(207.0, 650.625)]);
        var command = new PathCommand(path, new StrokeStyle(0.75, 0, 0, 10.0, [], 0.0), null, StrokePaint: Paint.Rgb(0, 0, 0));
        string content = FirstStream(new PdfWriter(Metadata, noCompression: true).Write(Doc(new RenderPage(300, 700, [command]))));
        Assert.Contains("4.5 650.25 202.5 0.75 re", content);
        Assert.Contains("\nf\n", content);
        Assert.DoesNotContain("\nS\n", content);
    }

    [Fact]
    public void Thin_diagonal_strokes_stay_strokes()
    {
        var path = new RenderPath([PathSegment.MoveTo(4.5, 650.625), PathSegment.LineTo(207.0, 651.0)]);
        var command = new PathCommand(path, new StrokeStyle(0.75, 0, 0, 10.0, [], 0.0), null, StrokePaint: Paint.Rgb(0, 0, 0));
        string content = FirstStream(new PdfWriter(Metadata, noCompression: true).Write(Doc(new RenderPage(300, 700, [command]))));
        Assert.Contains("\nS\n", content);
        Assert.DoesNotContain(" re", content);
    }

    [Fact]
    public void Unicode_text_does_not_crash_the_standard_font_path()
    {
        var builder = new RenderModelBuilder();
        builder.BeginPage(100, 100);
        builder.AddText("Привет 漢字", "Helvetica", 12, Matrix.Identity, null);
        builder.EndPage();
        byte[] pdf = new PdfWriter(Metadata).Write(builder.Document());
        Assert.StartsWith("%PDF-1.4", Encoding.Latin1.GetString(pdf));
    }

    private FontResolver ResolverWithFont(string fileName)
    {
        File.WriteAllBytes(Path.Combine(_tempDir, fileName), FontLocation.Bundled("LiberationSerif-Regular.ttf").ReadAllBytes());
        var cache = new FontCache();
        cache.Load(_tempDir);
        return new FontResolver(_tempDir, cache);
    }

    [Fact]
    public void Embeds_a_font_from_the_additional_folder()
    {
        var resolver = ResolverWithFont("UnitTestFont-Regular.ttf");
        var builder = new RenderModelBuilder();
        builder.BeginPage(200, 200);
        builder.AddText("Hello", "UnitTestFont", 12, Matrix.Identity, null);
        builder.EndPage();
        var writer = new PdfWriter(Metadata, noCompression: true, fontProvider: (name, codes) => PdfFontEmbedder.BuildEmbeddedFont(name, codes, resolver));
        byte[] pdf = writer.Write(builder.Document());
        Assert.True(Contains(pdf, "/FontFile2"));
        Assert.True(Contains(pdf, "/ToUnicode"));
        Assert.True(Contains(pdf, "+UnitTestFont"));
    }

    [Fact]
    public void Embeds_a_defined_type42_font_program()
    {
        var resolver = ResolverWithFont("DefinedType42.ttf");
        var location = FontLocation.File(Path.Combine(_tempDir, "DefinedType42.ttf"));
        var metrics = resolver.Cache.MetricsFor(location);
        resolver.RegisterDefinedFont("f-0-0", new FontResource("f-0-0", "Type42", metrics.UnitsPerEm, new Dictionary<int, string>(),
            new Dictionary<string, double>(), false, CodeWidths: metrics.CodeWidths, FontProgram: location.ReadAllBytes()));
        var builder = new RenderModelBuilder();
        builder.BeginPage(200, 200);
        builder.AddText("AXISPOINT", "f-0-0", 12, Matrix.Identity, null);
        builder.EndPage();
        var writer = new PdfWriter(Metadata, noCompression: true, fontProvider: (name, codes) => PdfFontEmbedder.BuildEmbeddedFont(name, codes, resolver));
        byte[] pdf = writer.Write(builder.Document());
        Assert.True(Contains(pdf, "/FontFile2"));
        Assert.True(Contains(pdf, "+f-0-0"));
    }

    [Fact]
    public void Embeds_a_type42_font_without_cmap_through_a_synthesized_cmap()
    {
        string sample = Corpus.File("ps/functional/FONTS10/TrueType.ps");
        var resolver = new FontResolver();
        var builder = new RenderModelBuilder();
        var registry = new OperatorRegistry();
        var images = new PsImageStore();
        BaseOperators.Register(registry);
        GraphicsOperators.Register(registry, builder);
        ColorOperators.Register(registry, builder);
        ImageOperators.Register(registry, builder, images);
        TextOperators.Register(registry, builder, resolver);
        var pipeline = new PsPipeline(new PsInterpreter(registry), registry, builder, resolver, images);
        var doc = pipeline.BuildRenderModel(File.ReadAllBytes(sample));
        var used = new Dictionary<string, HashSet<int>>();
        foreach (var command in doc.Pages.SelectMany(p => p.Commands).OfType<TextCommand>())
        {
            if (!used.TryGetValue(command.FontRef, out var set))
                used[command.FontRef] = set = [];
            foreach (var rune in command.Text.EnumerateRunes())
                set.Add(rune.Value);
        }
        Assert.Contains("f-0-0", used.Keys);
        var embedded = PdfFontEmbedder.BuildEmbeddedFont("f-0-0", used["f-0-0"], resolver);
        Assert.NotNull(embedded);
        Assert.NotEmpty(embedded.FontFile);
        Assert.Contains((int)'A', embedded.CharCodeMap.Keys);
        Assert.True(TtfSubset.HasTable(embedded.FontFile, "cmap"));
    }
}
