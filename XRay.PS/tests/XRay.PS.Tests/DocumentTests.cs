// Ported from tests/ps/test_ps_creation_editing.py, test_output_metadata.py,
// test_xmp_metadata.py, test_ps_raster_integration.py, test_image_to_eps.py, and
// tests/image/test_raster_output.py, test_glyph_fill_policy.py, test_xps_raster_integration.py.

using System.Text;
using XRay.PS.Fonts;
using XRay.PS.Imaging;
using XRay.PS.PostScript;
using XRay.PS.Rendering;
using XRay.PS.Skia;
using XRay.PS.Xps;

namespace XRay.PS.Tests;

public class DocumentTests
{
    private static ReadOnlySpan<byte> PngSignature => [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    private static string Latin1(byte[] data) => Encoding.Latin1.GetString(data);

    [Fact]
    public void Created_eps_has_a_bounding_box_and_one_page()
    {
        string text = Latin1(PsDocument.Create(isEps: true, width: 200.0, height: 100.0).Save());
        Assert.StartsWith("%!PS-Adobe-3.0 EPSF-3.0", text);
        Assert.Contains("%%BoundingBox: 0 0 200 100", text);
        Assert.Contains("%%Pages: 1", text);
    }

    [Fact]
    public void Adding_inserting_and_removing_pages_updates_the_page_comments()
    {
        var doc = PsDocument.Create(isEps: false);
        doc.AddPage(100.0, 100.0);
        doc.InsertPage(1, 50.0, 50.0);
        doc.RemovePage(0);
        string text = Latin1(doc.Save());
        Assert.Contains("%%Pages: 2", text);
        Assert.Equal(2, text.Split("%%Page:").Length - 1);
    }

    [Fact]
    public void Canvas_drawing_appends_operators()
    {
        var doc = PsDocument.Create(isEps: true, width: 100.0, height: 100.0);
        var page = doc.GetPage(0);
        page.Canvas.MoveTo(0, 0);
        page.Canvas.LineTo(10, 0);
        page.Canvas.Stroke();
        page.Canvas.DrawText("Hello", 5, 5, "Helvetica", 12);
        Assert.Contains("moveto", page.Content[0]);
        Assert.Contains(page.Content, line => line.Contains("show", StringComparison.Ordinal));
    }

    [Fact]
    public void Saving_an_existing_eps_preserves_its_content()
    {
        byte[] data = "%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 0 0 10 10\n%%Pages: 1\n%%Page: 1 1\n10 10 moveto\n%%Trailer\n%%EOF\n"u8.ToArray();
        var doc = PsDocument.FromBytes(data);
        Assert.True(doc.IsEps);
        Assert.NotNull(doc.Dsc);
        string text = Latin1(doc.Save());
        Assert.Contains("10 10 moveto", text);
        Assert.Contains("%%Pages: 1", text);
        Assert.Equal(1, text.Split("%%Page:").Length - 1);
    }

    [Fact]
    public void Pdf_metadata_comes_from_the_dsc()
    {
        var meta = PsConverter.BuildPdfMetadata(new DscMetadata { Title = "Title", Creator = "Creator" });
        Assert.Equal("Title", meta.Title);
        Assert.Equal("Creator", meta.Creator);
        Assert.Equal("XRay.PS", meta.Producer);
        Assert.False(meta.Trapped);
    }

    [Fact]
    public void No_compression_and_document_info()
    {
        byte[] pdf = PsDocument.FromBytes("%!PS-Adobe-3.0 EPSF-3.0\n%%Title: Sample\n%%Creator: Tool\n"u8.ToArray())
            .ToPdf(new PdfSaveOptions { NoCompression = true });
        string text = Latin1(pdf);
        Assert.DoesNotContain("/Filter /FlateDecode", text);
        Assert.Contains("/Title (Sample)", text);
        Assert.Contains("/Creator (Tool)", text);
    }

    private sealed class RecordingWriter : IRasterWriter
    {
        public RenderDocument? LastDocument { get; private set; }

        public byte[] Write(RenderDocument document, ImageSaveOptions options)
        {
            LastDocument = document;
            return "dummy-image"u8.ToArray();
        }
    }

    [Fact]
    public void Image_conversion_uses_the_configured_raster_writer()
    {
        var writer = new RecordingWriter();
        byte[] data = PsDocument.FromBytes("%!PS\n"u8.ToArray()).ToImage(new ImageSaveOptions(ImageFormat.Png) { RasterWriter = writer });
        Assert.Equal("dummy-image"u8.ToArray(), data);
        Assert.NotNull(writer.LastDocument);
    }

    [Fact]
    public void Xmp_can_be_read_replaced_and_removed()
    {
        string path = Corpus.File("ps/xmp/PAGENET-361-1.eps");
        Assert.Contains("x:xmpmeta", PsDocument.FromFile(path).GetXmp());

        const string replacement = "<x:xmpmeta xmlns:x='adobe:ns:meta/'></x:xmpmeta>";
        var doc = PsDocument.FromFile(path);
        doc.SetXmp(replacement);
        Assert.Equal(replacement, Xmp.Extract(doc.Save()));

        doc = PsDocument.FromFile(path);
        doc.RemoveXmp();
        byte[] saved = doc.Save();
        Assert.Null(Xmp.Extract(saved));
        Assert.DoesNotContain("x:xmpmeta", Latin1(saved));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ps_converts_to_png(bool skia)
    {
        var doc = PsDocument.FromFile(Corpus.File("ps/integration/minimal.ps"));
        byte[] data = doc.ToImage(new ImageSaveOptions(ImageFormat.Png, 72) { RasterWriter = skia ? new SkiaRasterWriter() : null });
        Assert.True(data.AsSpan().StartsWith(PngSignature));
    }

    [Fact]
    public void Xps_converts_to_bmp()
    {
        var doc = XpsDocument.FromFile(Corpus.File("xps/integration/Simple.xps"));
        Assert.True(doc.ToImage(new ImageSaveOptions(ImageFormat.Bmp, 72)).AsSpan().StartsWith("BM"u8));
    }

    [Fact]
    public void Images_convert_to_eps()
    {
        int converted = 0;
        foreach (string path in Directory.EnumerateFiles(Corpus.File("ps/images")).Order(StringComparer.Ordinal))
        {
            Func<byte[], EpsImageInfo>? decoder = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".png" => ImageToEps.DecodePng,
                ".jpg" or ".jpeg" => ImageToEps.DecodeJpeg,
                ".tif" or ".tiff" => ImageToEps.DecodeTiff,
                ".bmp" => ImageToEps.DecodeBmp,
                _ => null,
            };
            if (decoder is null)
                continue;
            var info = decoder(File.ReadAllBytes(path));
            string text = Latin1(ImageToEps.ConvertFile(path));
            Assert.StartsWith("%!PS-Adobe-3.0 EPSF-3.0", text);
            string bbox = text.Split('\n').First(l => l.StartsWith("%%BoundingBox:", StringComparison.Ordinal));
            Assert.Equal($"%%BoundingBox: 0 0 {info.Width} {info.Height}", bbox);
            Assert.Contains(info.ColorSpace == "DeviceRGB" ? "colorimage" : " image", text);
            converted++;
        }
        Assert.True(converted > 0);
    }

    [Fact]
    public void Raster_size_follows_the_dpi()
    {
        var page = new RenderPage(72, 72, [new PathCommand(Paths.FromRect(new Rect(0, 0, 72, 72)), null, Paint.Rgb(1, 0, 0))]);
        var doc = new RenderDocument { Pages = [page] };
        var s72 = new RasterRenderer(72).Render(doc);
        var s144 = new RasterRenderer(144).Render(doc);
        Assert.Equal((72, 72), (s72.Width, s72.Height));
        Assert.Equal((144, 144), (s144.Width, s144.Height));
    }

    [Fact]
    public void Encoders_write_their_signatures()
    {
        var surface = new RasterRenderer(72).Render(new RenderDocument { Pages = [new RenderPage(8, 8)] });
        Assert.True(RasterEncoders.EncodePng(surface).AsSpan().StartsWith(PngSignature));
        Assert.True(RasterEncoders.EncodeBmp(surface).AsSpan().StartsWith("BM"u8));
        Assert.True(RasterEncoders.EncodeTiff(surface).AsSpan().StartsWith("II"u8) || RasterEncoders.EncodeTiff(surface).AsSpan().StartsWith("MM"u8));
        byte[] jpeg = RasterEncoders.EncodeJpeg(surface);
        Assert.True(jpeg.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8]));
        Assert.True(jpeg.Length > 4);
    }

    [Fact]
    public void Near_a4_xps_pages_snap_to_a4_pixels()
    {
        Assert.Equal((2480, 3507), SkiaRasterWriter.PagePixelSize(595.5, 842.25, 300.0 / 72.0, preserveFractional: true));
        Assert.Equal((3506, 2480), SkiaRasterWriter.PagePixelSize(841.68, 595.44, 300.0 / 72.0, preserveFractional: true));
    }

    private static List<GlyphPoint> Square(double x0, double y0, double x1, double y1, bool clockwise) => clockwise
        ? [new(x0, y0, true), new(x1, y0, true), new(x1, y1, true), new(x0, y1, true)]
        : [new(x0, y0, true), new(x0, y1, true), new(x1, y1, true), new(x1, y0, true)];

    [Fact]
    public void Glyph_contours_with_the_same_winding_fill_even_odd()
    {
        Assert.True(GlyphOutlines.NeedEvenOdd([Square(0, 0, 1000, 1000, true), Square(250, 250, 750, 750, true)]));
        Assert.False(GlyphOutlines.NeedEvenOdd([Square(0, 0, 1000, 1000, true), Square(250, 250, 750, 750, false)]));
        Assert.False(GlyphOutlines.NeedEvenOdd([Square(0, 0, 1000, 1000, true)]));
    }
}
