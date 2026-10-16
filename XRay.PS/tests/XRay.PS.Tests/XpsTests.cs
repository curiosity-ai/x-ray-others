// Ported from tests/xps/test_xps_pipeline.py, test_xps_output_rules.py, test_print_tickets.py,
// test_xps_creation_editing.py and test_xps_integration.py.

using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using XRay.PS.Imaging;
using XRay.PS.Pdf;
using XRay.PS.Rendering;
using XRay.PS.Skia;
using XRay.PS.Xps;

namespace XRay.PS.Tests;

public partial class XpsTests
{
    private const string Ns = "http://schemas.microsoft.com/xps/2005/06";
    private const string PrintTicketXml = "<psf:PrintTicket xmlns:psf='http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework'/>";
    private const string PrintTicketType = "http://schemas.microsoft.com/xps/2005/06/printticket";

    internal static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(data);
            }
        }
        return ms.ToArray();
    }

    private static byte[] U(string text) => Encoding.UTF8.GetBytes(text);

    internal static byte[] Package(string fixedPage, params (string Name, byte[] Data)[] extra) => Zip(
        [
            ("FixedDocSeq.fdseq", U($"<FixedDocumentSequence xmlns=\"{Ns}\"><DocumentReference Source=\"Documents/1/FixedDoc.fdoc\" /></FixedDocumentSequence>")),
            ("Documents/1/FixedDoc.fdoc", U($"<FixedDocument xmlns=\"{Ns}\"><PageContent Source=\"Pages/1.fpage\" /></FixedDocument>")),
            ("Documents/1/Pages/1.fpage", U(fixedPage)),
            .. extra,
        ]);

    private static readonly string SimplePage = $"<FixedPage xmlns=\"{Ns}\" Width=\"10\" Height=\"10\"><Path Data=\"M 0,0 L 10,0 L 10,10 L 0,10 Z\" Fill=\"#FF0000\" /></FixedPage>";

    private static RenderDocument Render(byte[] packageBytes, out XpsImageStore store, string part = "/Documents/1/Pages/1.fpage", bool setPackage = true)
    {
        var package = XpsPackage.FromBytes(packageBytes);
        var builder = new RenderModelBuilder();
        store = new XpsImageStore();
        var renderer = new XpsRenderer(builder, store);
        if (setPackage)
        {
            renderer.SetPackage(package);
            renderer.SetCurrentPart(part);
        }
        renderer.RenderFixedPage(package.Read(part));
        return builder.Document();
    }

    private static RenderDocument RenderCorpus(string relativePath, out XpsImageStore store) =>
        Render(File.ReadAllBytes(Corpus.File(relativePath)), out store);

    // A 3x2 gray (180) baseline JPEG, as Pillow writes Image.new("L", (3, 2), 180).
    private static readonly byte[] GrayscaleJpeg = Convert.FromHexString(
        "ffd8ffe000104a46494600010101006000600000ffdb004300080606070605080707070909080a0c140d0c0b0b0c1912130f141d1a1f1e1d1a1c1c20242e2720222c231c1c2837292c30313434341f27393d38323c2e333432ffc0000b080002000301011100ffc4001f0000010501010101010100000000000000000102030405060708090a0bffc400b5100002010303020403050504040000017d01020300041105122131410613516107227114328191a1082342b1c11552d1f02433627282090a161718191a25262728292a3435363738393a434445464748494a535455565758595a636465666768696a737475767778797a838485868788898a92939495969798999aa2a3a4a5a6a7a8a9aab2b3b4b5b6b7b8b9bac2c3c4c5c6c7c8c9cad2d3d4d5d6d7d8d9dae1e2e3e4e5e6e7e8e9eaf1f2f3f4f5f6f7f8f9faffda0008010100003f00ed2bffd9");

    private static byte[] PngChunk(string type, byte[] payload)
    {
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        var output = new List<byte>();
        output.AddRange(BigEndianBytes((uint)payload.Length));
        output.AddRange(typeBytes);
        output.AddRange(payload);
        output.AddRange(BigEndianBytes(Crc32Of([.. typeBytes, .. payload])));
        return [.. output];
    }

    private static uint Crc32Of(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return ~crc;
    }

    private static byte[] BigEndianBytes(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] ZlibCompress(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return ms.ToArray();
    }

    private static byte[] Png(byte[] ihdr, params (string Type, byte[] Payload)[] chunks)
    {
        var output = new List<byte> { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };
        output.AddRange(PngChunk("IHDR", ihdr));
        foreach (var (type, payload) in chunks)
            output.AddRange(PngChunk(type, payload));
        output.AddRange(PngChunk("IEND", []));
        return [.. output];
    }

    private static byte[] Ihdr(uint width, uint height, byte depth, byte colorType) =>
        [.. BigEndianBytes(width), .. BigEndianBytes(height), depth, colorType, 0, 0, 0];

    private static byte[] MinimalPng() => Png(Ihdr(1, 1, 8, 2), ("IDAT", ZlibCompress([0x00, 0xFF, 0x00, 0x00])));

    [Fact]
    public void Parser_finds_the_fixed_pages()
    {
        var package = XpsPackage.FromBytes(Package(SimplePage));
        Assert.Equal(["/Documents/1/Pages/1.fpage"], new XpsParser(package).FixedPageParts());
    }

    [Fact]
    public void Interleaved_pieces_are_joined()
    {
        var package = XpsPackage.FromBytes(Zip(("FixedDocumentSequence.fdseq/[0].piece", "ABC"u8.ToArray()), ("FixedDocumentSequence.fdseq/[1].last.piece", "DEF"u8.ToArray())));
        Assert.Equal("ABCDEF"u8.ToArray(), package.Read("/FixedDocumentSequence.fdseq"));
    }

    [Fact]
    public void Static_resources_resolve_path_geometry()
    {
        string page = $"<FixedPage xmlns=\"{Ns}\" xmlns:x=\"{Ns}\"><FixedPage.Resources><ResourceDictionary><PathGeometry x:Key=\"R1\" Figures=\"M 0,0 L 1,0 L 1,1 Z\" /></ResourceDictionary></FixedPage.Resources><Path Data=\"{{StaticResource R1}}\" Fill=\"#000000\" /></FixedPage>";
        var doc = Render(Package(page), out _, setPackage: false);
        Assert.NotEmpty(Assert.Single(doc.Pages).Commands);
    }

    [Fact]
    public void External_resource_dictionaries_resolve()
    {
        const string key = "http://schemas.microsoft.com/xps/2005/06/resourcedictionary-key";
        string page = $"<FixedPage xmlns=\"{Ns}\" xmlns:x=\"{key}\" Width=\"10\" Height=\"10\"><FixedPage.Resources><ResourceDictionary Source=\"/Resources/resources.dict\" /></FixedPage.Resources><Path Stroke=\"{{StaticResource magenta}}\" StrokeThickness=\"2\" Data=\"M 1,1 L 9,1\" /></FixedPage>";
        string dict = $"<ResourceDictionary xmlns=\"{Ns}\" xmlns:x=\"{key}\"><SolidColorBrush x:Key=\"magenta\" Color=\"#FF00FF\" /></ResourceDictionary>";
        var doc = Render(Package(page, ("Resources/resources.dict", U(dict))), out _);
        var command = Assert.Single(doc.Pages).Commands[0];
        if (command is PathCommand path)
        {
            Assert.NotNull(path.StrokePaint);
            Assert.Equal("DeviceRGB", path.StrokePaint.Kind);
            var rgb = path.StrokePaint.Components!;
            Assert.True(rgb[0] > 0.9 && rgb[1] < 0.2 && rgb[2] > 0.9);
        }
        else
        {
            var image = Assert.IsType<ImageCommand>(command);
            Assert.True(image.Width > 0 && image.Height > 0);
        }
    }

    [Fact]
    public void Static_resource_render_transforms_apply()
    {
        const string key = "http://schemas.microsoft.com/xps/2005/06/resourcedictionary-key";
        string page = $"<FixedPage xmlns=\"{Ns}\" xmlns:x=\"{key}\" Width=\"10\" Height=\"10\"><FixedPage.Resources><ResourceDictionary><MatrixTransform x:Key=\"rotate\" Matrix=\"0,1,-1,0,10,0\" /></ResourceDictionary></FixedPage.Resources><Path RenderTransform=\"{{StaticResource rotate}}\" Data=\"M 0,0 L 2,0\" Stroke=\"#000000\" StrokeThickness=\"1\" /></FixedPage>";
        var command = Render(Package(page), out _).Pages[0].Commands[0];
        if (command is PathCommand path)
        {
            Assert.Equal(7.5, path.Path.Segments[0].Points[0].X, 6);
            Assert.Equal(7.5, path.Path.Segments[0].Points[0].Y, 6);
        }
        else
        {
            var image = Assert.IsType<ImageCommand>(command);
            Assert.True(image.Matrix.E > 0 && image.Matrix.F > 0);
        }
    }

    [Fact]
    public void Child_render_transform_elements_apply()
    {
        string page = $"<FixedPage xmlns=\"{Ns}\" Width=\"20\" Height=\"20\"><Canvas><Canvas.RenderTransform><MatrixTransform Matrix=\"2,0,0,2,0,0\" /></Canvas.RenderTransform><Path Data=\"M 1,1 L 3,1\" Stroke=\"#000000\" StrokeThickness=\"1\" /></Canvas></FixedPage>";
        var command = Render(Package(page), out _).Pages[0].Commands[0];
        if (command is PathCommand path)
        {
            Assert.Equal(1.5, path.Path.Segments[0].Points[0].X, 6);
            Assert.Equal(13.5, path.Path.Segments[0].Points[0].Y, 6);
        }
        else
        {
            Assert.True(Assert.IsType<ImageCommand>(command).Matrix.A > 0);
        }
    }

    private static (XpsRenderer Renderer, RenderModelBuilder Builder) ImageRenderer()
    {
        var package = XpsPackage.FromBytes(Zip(("Images/test.png", MinimalPng())));
        var builder = new RenderModelBuilder();
        var renderer = new XpsRenderer(builder, new XpsImageStore());
        renderer.SetPackage(package);
        return (renderer, builder);
    }

    [Fact]
    public void Image_brush_patterns_use_the_viewport_geometry()
    {
        var (renderer, builder) = ImageRenderer();
        var brush = XElement.Parse($"<ImageBrush xmlns=\"{Ns}\" ImageSource=\"/Images/test.png\" Viewbox=\"0,0,128,96\" Viewport=\"0,0,128,96\" TileMode=\"Tile\" />");
        var paint = renderer.ImageBrushToPattern(brush, null);
        Assert.NotNull(paint);
        Assert.Equal("Pattern", paint.Kind);
        var pattern = Assert.IsType<TilingPattern>(builder.Document().Resources.Patterns[((PatternPaint)paint.Value!).PatternId]);
        Assert.Equal([0.0, 0.0, 128.0, 96.0], pattern.BBox);
        Assert.Equal(128.0, pattern.XStep);
        Assert.Equal(96.0, pattern.YStep);
        var image = Assert.IsType<ImageCommand>(Assert.Single(pattern.Commands));
        Assert.Equal(128.0, image.Matrix.A, 6);
        Assert.Equal(-96.0, image.Matrix.D, 6);
        Assert.Equal(96.0, image.Matrix.F, 6);
    }

    [Fact]
    public void Image_brush_patterns_apply_the_paint_transform_once()
    {
        var (renderer, builder) = ImageRenderer();
        var brush = XElement.Parse($"<ImageBrush xmlns=\"{Ns}\" ImageSource=\"/Images/test.png\" Viewbox=\"0,0,258.24,56.64\" Viewport=\"50,20,193.68,42.48\" />");
        var paint = renderer.ImageBrushToPattern(brush, new Matrix(0.525, 0.0, 0.0, -0.525, 0.0, 792.0));
        Assert.NotNull(paint);
        var pattern = Assert.IsType<TilingPattern>(builder.Document().Resources.Patterns[((PatternPaint)paint.Value!).PatternId]);
        Assert.Equal([0.0, 0.0, 193.68, 42.48], pattern.BBox);
        Assert.Equal(0.525, pattern.Matrix.A, 6);
        Assert.Equal(-0.525, pattern.Matrix.D, 6);
        Assert.Equal(26.25, pattern.Matrix.E, 6);
        Assert.Equal(781.5, pattern.Matrix.F, 6);
    }

    [Fact]
    public void Smooth_cubic_path_data_reflects_the_previous_control_point()
    {
        var path = XpsRenderer.ParsePathData("M 10,150 c 25,-50 50,50 75,0 s 50,50 75,0 50,50 75,0 c -25,-50 -50,50 -75,0 s -50,50 -75,0 -50,50 -75,0", Matrix.Identity);
        var curves = path.Segments.Where(s => s.Kind == PathSegmentKind.Curve).ToList();
        Assert.Equal(6, curves.Count);
        Assert.Equal(110.0, curves[1].Points[0].X, 6);
        Assert.Equal(100.0, curves[1].Points[0].Y, 6);
        Assert.Equal(85.0, curves[0].Points[2].X, 6);
        Assert.Equal(150.0, curves[0].Points[2].Y, 6);
    }

    [Fact]
    public void Arc_segments_become_curves()
    {
        const string key = "http://schemas.microsoft.com/xps/2005/06/resourcedictionary-key";
        string page = $"<FixedPage xmlns=\"{Ns}\" xmlns:x=\"{key}\" Width=\"100\" Height=\"100\"><FixedPage.Resources><ResourceDictionary><PathGeometry x:Key=\"oval\" Figures=\"M 20,50 A 30,20 0 0 1 80,50 30,20 0 0 1 20,50\" /></ResourceDictionary></FixedPage.Resources><Path Data=\"{{StaticResource oval}}\" Fill=\"#000000\" /></FixedPage>";
        var command = Assert.IsType<PathCommand>(Render(Package(page), out _).Pages[0].Commands[0]);
        Assert.True(command.Path.Segments.Count(s => s.Kind == PathSegmentKind.Curve) >= 2);
    }

    [Fact]
    public void Poly_quadratic_segments_become_curves()
    {
        var geometry = XElement.Parse($"<PathGeometry xmlns=\"{Ns}\"><PathFigure StartPoint=\"10,50\" IsClosed=\"true\"><PolyQuadraticBezierSegment Points=\"25,10 50,50 75,90 90,50\" /></PathFigure></PathGeometry>");
        var (fill, _) = XpsRenderer.ParsePathGeometryVariants(geometry, Matrix.Identity);
        Assert.NotNull(fill);
        var curves = fill.Segments.Where(s => s.Kind == PathSegmentKind.Curve).ToList();
        Assert.Equal(2, curves.Count);
        Assert.Equal((50.0, 50.0), (curves[0].Points[^1].X, curves[0].Points[^1].Y));
        Assert.Equal((90.0, 50.0), (curves[1].Points[^1].X, curves[1].Points[^1].Y));
    }

    [Fact]
    public void Absolute_radial_gradient_coordinates_are_not_flipped()
    {
        var builder = new RenderModelBuilder();
        builder.BeginPage(500.0, 600.0);
        builder.EndPage();
        var renderer = new XpsRenderer(builder, new XpsImageStore());
        var brush = XElement.Parse($"<RadialGradientBrush xmlns=\"{Ns}\" MappingMode=\"Absolute\" Center=\"250,80\" GradientOrigin=\"250,80\" RadiusX=\"50\" RadiusY=\"40\" />");
        var (x, y) = renderer.SampleBrushCoordinates(brush, 250.0, 80.0, null);
        Assert.Equal(250.0, x, 6);
        Assert.Equal(80.0, y, 6);
    }

    [Fact]
    public void Paths_fill_even_odd_by_default()
    {
        var command = Assert.IsType<PathCommand>(Render(Package(SimplePage), out _, setPackage: false).Pages[0].Commands[0]);
        Assert.Equal("evenodd", command.FillRule);
    }

    [Fact]
    public void Png_decoding_handles_rgb_palette_and_gray()
    {
        var minimal = XpsImageDecoders.DecodePng(MinimalPng());
        Assert.Equal((1, 1), (minimal.Width, minimal.Height));

        var palette = XpsImageDecoders.DecodePng(Png(Ihdr(2, 1, 4, 3), ("PLTE", [255, 0, 0, 0, 255, 0]), ("tRNS", [255, 64]), ("IDAT", ZlibCompress([0x00, 0x01]))));
        Assert.Equal((2, 1), (palette.Width, palette.Height));
        Assert.Equal(new byte[] { 255, 0, 0, 0, 255, 0 }, palette.Data);
        Assert.Equal(new byte[] { 255, 64 }, palette.SoftMask);

        var gray = XpsImageDecoders.DecodePng(Png(Ihdr(2, 1, 1, 0), ("IDAT", ZlibCompress([0x00, 0x40]))));
        Assert.Equal(new byte[] { 0, 0, 0, 255, 255, 255 }, gray.Data);
        Assert.Null(gray.SoftMask);
    }

    [Fact]
    public void Grayscale_jpeg_stays_device_gray()
    {
        var resource = XpsImageDecoders.DecodeJpeg(GrayscaleJpeg);
        Assert.Equal((3, 2), (resource.Width, resource.Height));
        Assert.Equal("DeviceGray", resource.ColorSpace);
        Assert.Equal("DCTDecode", resource.Filter);
    }

    [Fact]
    public void Parent_relative_image_brush_parts_resolve()
    {
        string page = $"<FixedPage xmlns=\"{Ns}\" Width=\"100\" Height=\"100\"><Path Data=\"M 0,0 L 10,0 L 10,10 L 0,10 Z\"><Path.Fill><ImageBrush ImageSource=\"../Resources/Images/1.JPG\" Viewbox=\"0,0,1,1\" Viewport=\"0,0,1,1\" /></Path.Fill></Path></FixedPage>";
        var doc = Render(Package(page, ("Documents/1/Resources/Images/1.JPG", GrayscaleJpeg)), out var store);
        Assert.Single(doc.Pages);
        Assert.Single(store.Items);
    }

    private static (double, double)? JobMediaSize(XpsDocument document)
    {
        foreach (var ticket in document.GetPrintTickets())
        {
            if (ticket.Scope == PrintTicketScope.Job && XpsConverter.PrintTicketMediaSizePoints(ticket.Xml) is { } size)
                return size;
        }
        return null;
    }

    [Fact]
    public void Job_media_size_honors_landscape_orientation()
    {
        string page = $"<FixedPage xmlns=\"{Ns}\" Width=\"1122.5196850393702\" Height=\"793.70078740157476\"><Path Data=\"M 0,0 L 10,0 L 10,10 L 0,10 Z\" Fill=\"#FF0000\" /></FixedPage>";
        string rels = "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rIdPT1\" Type=\"http://schemas.microsoft.com/xps/2005/06/printticket\" Target=\"MetaData/Job_PT.xml\" /></Relationships>";
        string ticket = """
            <psf:PrintTicket xmlns:psf="http://schemas.microsoft.com/windows/2003/08/printing/printschemaframework" xmlns:psk="http://schemas.microsoft.com/windows/2003/08/printing/printschemakeywords">
            <psf:Feature name="psk:PageMediaSize"><psf:Option name="psk:ISOA4">
              <psf:ScoredProperty name="psk:MediaSizeWidth"><psf:Value>210000</psf:Value></psf:ScoredProperty>
              <psf:ScoredProperty name="psk:MediaSizeHeight"><psf:Value>297000</psf:Value></psf:ScoredProperty>
            </psf:Option></psf:Feature>
            <psf:Feature name="psk:PageOrientation"><psf:Option name="psk:Landscape" /></psf:Feature>
            </psf:PrintTicket>
            """;
        var document = XpsDocument.FromBytes(Package(page, ("_rels/FixedDocSeq.fdseq.rels", U(rels)), ("MetaData/Job_PT.xml", U(ticket))));
        var size = JobMediaSize(document);
        Assert.NotNull(size);
        Assert.Equal(841.8897637795, size.Value.Item1, 6);
        Assert.Equal(595.2755905512, size.Value.Item2, 6);
    }

    [Fact]
    public void A_landscape_job_ticket_keeps_the_page_landscape()
    {
        var document = XpsDocument.FromFile(Corpus.File("xps/integration/38976.xps"));
        var size = JobMediaSize(document);
        var renderDoc = document.BuildRenderModel(forPdf: false);
        var page = Assert.Single(renderDoc.Pages);
        Assert.NotNull(size);
        Assert.Equal(841.8897637795, size.Value.Item1, 6);
        Assert.Equal(595.2755905512, size.Value.Item2, 6);
        Assert.Equal(841.8897637795, page.Width, 6);
        Assert.Equal(595.2755905512, page.Height, 6);
    }

    [GeneratedRegex(@"/MediaBox \[0 0 ([0-9.]+) ([0-9.]+)\]")]
    private static partial Regex MediaBoxRegex();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_landscape_job_ticket_reaches_the_pdf_and_the_images(bool skia)
    {
        var document = XpsDocument.FromFile(Corpus.File("xps/integration/38976.xps"));
        var match = MediaBoxRegex().Match(Encoding.Latin1.GetString(document.ToPdf()));
        Assert.True(match.Success);
        Assert.Equal("842", match.Groups[1].Value);
        Assert.Equal("595.5", match.Groups[2].Value);
        var images = document.ToImages(new ImageSaveOptions(ImageFormat.Png, 300) { RasterWriter = skia ? new SkiaRasterWriter() : null });
        var image = DecodedImage.Open(Assert.Single(images))!;
        Assert.Equal((3507, 2480), (image.Width, image.Height));
    }

    [Theory]
    [InlineData("xps/integration/canvas-opacity-mask.xps")]
    [InlineData("xps/integration/glyphs-opacity-mask.xps")]
    public void Opacity_masks_are_rasterized_as_masked_images(string path)
    {
        var page = RenderCorpus(path, out var store).Pages[0];
        var images = page.Commands.OfType<ImageCommand>().ToList();
        Assert.NotEmpty(images);
        Assert.NotNull(store.Get(images[^1].ImageId).SoftMask);
    }

    [Fact]
    public void Clipped_glyphs_are_wrapped_in_save_clip_restore()
    {
        var commands = RenderCorpus("xps/integration/example.xps", out _).Pages[0].Commands;
        int index = commands.FindIndex(c => c is TextCommand t && t.Text == "Fibers for Food Service Uniforms");
        Assert.True(index >= 2);
        Assert.IsType<StateSaveCommand>(commands[index - 2]);
        Assert.IsType<ClipCommand>(commands[index - 1]);
        Assert.IsType<StateRestoreCommand>(commands[index + 1]);
    }

    // The original test fails against the original code (no 27x55 image command is emitted on that
    // page); this pins what both emit: clips are present and no such image command.
    [Fact]
    public void Logo_path_clip_wraps_its_image_brush_fill()
    {
        var commands = RenderCorpus("xps/integration/language.xps", out _).Pages[0].Commands;
        Assert.DoesNotContain(commands, c => c is ImageCommand { Width: 27, Height: 55 });
        Assert.Contains(commands, c => c is ClipCommand);
    }

    [Fact]
    public void Reflected_linear_gradients_are_rasterized_as_images()
    {
        var commands = RenderCorpus("xps/integration/LinearGradientBrush_3.xps", out _).Pages[0].Commands;
        Assert.Contains(commands, c => c is ImageCommand);
    }

    [Fact]
    public void Indices_text_uses_exact_glyph_placement()
    {
        var commands = RenderCorpus("xps/integration/example.xps", out _).Pages[0].Commands;
        var letters = "Executive Summary".Select(c => c.ToString()).ToHashSet();
        var run = commands.OfType<TextCommand>().Where(t => letters.Contains(t.Text)).ToList();
        Assert.Contains("Executive Summary", string.Concat(run.Select(t => t.Text)));
        Assert.All(run, t => Assert.Null(t.PdfTextAdjustments));
    }

    [Fact]
    public void Scaled_paths_scale_stroke_width_and_dashes()
    {
        string page = $"<FixedPage xmlns=\"{Ns}\" Width=\"100\" Height=\"100\"><Canvas RenderTransform=\"0.5,0,0,0.5,0,0\"><Path Stroke=\"#000000\" StrokeThickness=\"2\" StrokeDashArray=\"3 1\" StrokeDashOffset=\"2\" StrokeDashCap=\"Flat\" StrokeLineJoin=\"Round\" Data=\"M 0,0 L 20,0\" /></Canvas></FixedPage>";
        var command = Assert.IsType<PathCommand>(Render(Package(page), out _, setPackage: false).Pages[0].Commands[0]);
        Assert.NotNull(command.Stroke);
        Assert.Equal(0.75, command.Stroke.LineWidth, 6);
        Assert.Equal(0, command.Stroke.LineCap);
        Assert.Equal(1, command.Stroke.LineJoin);
        Assert.Equal([2.25, 0.75], command.Stroke.Dash);
        Assert.Equal(1.5, command.Stroke.DashPhase, 6);
    }

    // ---- output rules ----

    [Fact]
    public void Pdf_output_embeds_the_xps_fonts()
    {
        var doc = XpsDocument.FromFile(Corpus.File("xps/integration/mb03.xps"));
        Assert.Contains("/FontFile2", Encoding.Latin1.GetString(doc.ToPdf()));
        var (renderDoc, _) = XpsConverter.BuildRenderDocument(doc);
        var resolver = XpsConverter.BuildXpsFontResolver(doc.Package, renderDoc, null);
        Assert.NotNull(resolver.GetEmbeddedType42("/Resources/arial.ttf"));
        Assert.NotNull(PdfFontEmbedder.BuildEmbeddedFont("/Resources/arial.ttf", new HashSet<int> { 'A', 'r' }, resolver));
    }

    private sealed class CountingWriter : IRasterWriter
    {
        public List<double> PageWidths { get; } = [];
        public FontResolverSeen? Seen { get; private set; }

        public byte[] Write(RenderDocument document, ImageSaveOptions options)
        {
            PageWidths.Add(document.Pages[0].Width);
            Seen = new FontResolverSeen(options.FontResolver?.GetEmbeddedType42("/Resources/arial.ttf") is not null);
            return Encoding.ASCII.GetBytes($"png-{PageWidths.Count}");
        }
    }

    private sealed record FontResolverSeen(bool HasArial);

    [Fact]
    public void Image_output_hands_the_writer_the_xps_font_resolver()
    {
        var writer = new CountingWriter();
        byte[] data = XpsDocument.FromFile(Corpus.File("xps/integration/mb03.xps")).ToImage(new ImageSaveOptions(ImageFormat.Png, 96) { RasterWriter = writer });
        Assert.Equal("png-1"u8.ToArray(), data);
        Assert.True(writer.Seen?.HasArial);
    }

    [Fact]
    public void Image_output_writes_one_image_per_fixed_page()
    {
        var writer = new CountingWriter();
        var data = XpsDocument.FromFile(Corpus.File("xps/integration/BadColor.xps")).ToImages(new ImageSaveOptions(ImageFormat.Png, 96) { RasterWriter = writer });
        Assert.Equal(["png-1", "png-2", "png-3"], data.Select(Encoding.ASCII.GetString));
        Assert.Equal(3, writer.PageWidths.Count);
        Assert.All(writer.PageWidths, w => Assert.Equal(writer.PageWidths[0], w));
    }

    [Fact]
    public void Large_character_sets_embed_as_type0()
    {
        var doc = XpsDocument.FromFile(Corpus.File("xps/integration/OSHARED-36877.xps"));
        var (renderDoc, _) = XpsConverter.BuildRenderDocument(doc);
        var resolver = XpsConverter.BuildXpsFontResolver(doc.Package, renderDoc, null);
        var embedded = PdfFontEmbedder.BuildEmbeddedFont("/Resources/d4d6ef8f-9a15-4a9d-aea8-273954df915f.ODTTF", Enumerable.Range(0x21, 300).ToHashSet(), resolver);
        Assert.NotNull(embedded);
        Assert.Equal("Type0", embedded.Subtype);
        Assert.Equal("Identity-H", embedded.Encoding);
        Assert.Equal(2, embedded.CodeByteWidth);
        Assert.NotNull(embedded.CidToGidMap);
    }

    [Fact]
    public void Indices_glyph_ids_survive_into_the_render_commands()
    {
        var commands = RenderCorpus("xps/integration/OSHARED-36877.xps", out _).Pages[0].Commands.OfType<TextCommand>()
            .Where(t => t.FontRef.StartsWith("/Resources/d4d6ef8f-9a15-4a9d-aea8-273954df915f.ODTTF#gid=", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(commands);
        Assert.Contains(commands, t => t.GlyphId == 281);
        Assert.Contains(commands, t => t.Text == "T");
    }

    [Theory]
    [InlineData("xps/integration/PAGEJAVA-87.xps", 793.76, null)]
    [InlineData("xps/integration/path-variants.xps", 419.40, 593.15)]
    public void Page_print_ticket_media_sizes_apply(string path, double width, double? height)
    {
        var renderDoc = XpsDocument.FromFile(Corpus.File(path)).BuildRenderModel(forPdf: false);
        var page = Assert.Single(renderDoc.Pages);
        Assert.Equal(width, page.Width, 2);
        if (height is double h)
            Assert.Equal(h, page.Height, 2);
    }

    // ---- print tickets ----

    private static List<XElement> Relationships(ZipArchive archive, string relsPath)
    {
        var entry = archive.GetEntry(relsPath);
        if (entry is null)
            return [];
        using var stream = entry.Open();
        return XDocument.Load(stream).Descendants().Where(e => e.Name.LocalName == "Relationship").ToList();
    }

    [Fact]
    public void Document_print_tickets_are_written_with_a_relationship()
    {
        var doc = XpsDocument.Create();
        doc.AddPage(100, 100);
        doc = XpsDocument.FromBytes(doc.Save());
        doc.SetPrintTicket(PrintTicketScope.Document, PrintTicketXml);
        using var archive = new ZipArchive(new MemoryStream(doc.Save()));
        var rels = Relationships(archive, "Documents/1/_rels/FixedDoc.fdoc.rels").Where(r => (string?)r.Attribute("Type") == PrintTicketType).ToList();
        Assert.NotEmpty(rels);
        Assert.NotNull(archive.GetEntry("Documents/1/" + (string?)rels[0].Attribute("Target")));
    }

    [Fact]
    public void Page_print_tickets_can_be_removed()
    {
        var doc = XpsDocument.Create();
        doc.AddPage(100, 100);
        doc = XpsDocument.FromBytes(doc.Save());
        doc.SetPrintTicket(PrintTicketScope.Page, PrintTicketXml, pageIndex: 0);
        using (var archive = new ZipArchive(new MemoryStream(doc.Save())))
            Assert.Contains(Relationships(archive, "Documents/1/Pages/_rels/1.fpage.rels"), r => (string?)r.Attribute("Type") == PrintTicketType);
        doc = XpsDocument.FromBytes(doc.Save());
        doc.RemovePrintTicket(PrintTicketScope.Page, pageIndex: 0);
        using (var archive = new ZipArchive(new MemoryStream(doc.Save())))
            Assert.DoesNotContain(Relationships(archive, "Documents/1/Pages/_rels/1.fpage.rels"), r => (string?)r.Attribute("Type") == PrintTicketType);
    }

    [Fact]
    public void Job_print_tickets_can_be_read_back()
    {
        var doc = XpsDocument.Create();
        doc.AddPage(100, 100);
        doc = XpsDocument.FromBytes(doc.Save());
        doc.SetPrintTicket(PrintTicketScope.Job, PrintTicketXml);
        var tickets = doc.GetPrintTickets();
        Assert.Contains(tickets, t => t.Scope == PrintTicketScope.Job);
        Assert.Contains(tickets, t => t.Xml.Contains("PrintTicket", StringComparison.Ordinal));
    }

    // ---- creation and editing ----

    private static List<string> PageParts(byte[] package)
    {
        using var archive = new ZipArchive(new MemoryStream(package));
        return archive.Entries.Select(e => e.FullName).Where(n => n.EndsWith(".fpage", StringComparison.Ordinal)).ToList();
    }

    [Fact]
    public void Created_documents_serialize_their_paths()
    {
        var doc = XpsDocument.Create();
        var page = doc.AddPage(200, 100);
        page.Elements.Add(new XpsPath("M 0,0 L 10,0 Z") { Fill = "#FF0000" });
        byte[] data = doc.Save();
        string part = Assert.Single(PageParts(data));
        using var archive = new ZipArchive(new MemoryStream(data));
        using var stream = archive.GetEntry(part)!.Open();
        Assert.Single(XDocument.Load(stream).Descendants(), e => e.Name.LocalName == "Path");
    }

    [Fact]
    public void Inserting_and_removing_pages_updates_the_package()
    {
        var doc = XpsDocument.Create();
        doc.AddPage(100, 100);
        doc.AddPage(200, 200);
        doc.InsertPage(1, new XpsFixedPage(50, 50));
        doc.RemovePage(0);
        Assert.Equal(2, PageParts(doc.Save()).Count);
    }

    [Fact]
    public void Editing_an_existing_document_keeps_its_pages()
    {
        var doc = XpsDocument.FromFile(Corpus.File("xps/integration/Simple.xps"));
        doc.AddPage(100, 100);
        Assert.True(PageParts(doc.Save()).Count >= 2);
    }

    [Fact]
    public void Simple_xps_converts_to_pdf() =>
        Assert.StartsWith("%PDF", Encoding.Latin1.GetString(XpsDocument.FromFile(Corpus.File("xps/integration/Simple.xps")).ToPdf()));
}
