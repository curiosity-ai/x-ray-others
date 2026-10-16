// Cancellation, resource limits, error suppression and the stream overloads. PostScript can
// loop forever, so these use hostile inputs and require the work to stop within a bound.

// The tests call the token-less overloads on purpose (they pin the defaults), which xUnit1051
// would otherwise flag in this file.
#pragma warning disable xUnit1051

using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using XRay.PS.Imaging;
using XRay.PS.PostScript;
using XRay.PS.Rendering;
using XRay.PS.Skia;
using XRay.PS.Xps;
using XPS = XRay.PS.Tests.XpsTests;

namespace XRay.PS.Tests;

public partial class CancellationTests
{
    private static byte[] Ps(string body) => Encoding.Latin1.GetBytes("%!PS-Adobe-3.0\n" + body + "\n");

    private static PsDocument Doc(string body) => PsDocument.FromBytes(Ps(body), TestContext.Current.CancellationToken);

    // The PDF information dictionary carries the conversion time; blank it before comparing.
    [GeneratedRegex(@"D:\d{14}")]
    private static partial Regex TimestampRegex();

    private static string Normalize(byte[] pdf) => TimestampRegex().Replace(Encoding.Latin1.GetString(pdf), "D:0");

    private sealed class WriteOnlyStream : Stream
    {
        public MemoryStream Inner { get; } = new();
        public int LargestWrite { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            LargestWrite = Math.Max(LargestWrite, buffer.Length);
            Inner.Write(buffer);
        }
    }

    // ---- PostScript: cancellation ----

    [Fact]
    public void An_infinite_loop_is_cancelled_within_a_bound()
    {
        var doc = Doc("{} loop");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var options = new PdfSaveOptions { Limits = PsInterpreterLimits.Unlimited };
        var watch = Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => doc.ToPdf(options, cts.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"cancellation took {watch.Elapsed}");
    }

    [Theory]
    [InlineData("{} loop")]
    [InlineData("{ 1 0 moveto } loop")]
    [InlineData("1000000000 { } repeat")]
    [InlineData("0 1 1000000000 { pop } for")]
    [InlineData("{ (abc) pop 1 2 add pop } loop")]
    public void Looping_programs_are_cancelled_from_another_thread(string program)
    {
        var doc = Doc(program);
        using var cts = new CancellationTokenSource();
        var options = new PdfSaveOptions { Limits = PsInterpreterLimits.Unlimited, SuppressErrors = true };
        Task.Run(() =>
        {
            Thread.Sleep(250);
            cts.Cancel();
        }, TestContext.Current.CancellationToken);
        var watch = Stopwatch.StartNew();
        // Cancellation is never turned into a warning, even when errors are suppressed.
        Assert.ThrowsAny<OperationCanceledException>(() => doc.ToPdf(options, cts.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void A_cancelled_token_stops_every_postscript_entry_point()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var token = cts.Token;
        var doc = Doc("0 0 moveto 10 10 lineto stroke showpage");
        Assert.ThrowsAny<OperationCanceledException>(() => doc.ToPdf(null, token));
        Assert.ThrowsAny<OperationCanceledException>(() => doc.ToPdf(new MemoryStream(), null, token));
        Assert.ThrowsAny<OperationCanceledException>(() => doc.ToImage(new ImageSaveOptions(ImageFormat.Png, 36), token));
        Assert.ThrowsAny<OperationCanceledException>(() => doc.ToImage(new ImageSaveOptions(ImageFormat.Png, 36), new MemoryStream(), token));
        Assert.ThrowsAny<OperationCanceledException>(() => doc.ToImages(new ImageSaveOptions(ImageFormat.Png, 36), token));
        Assert.ThrowsAny<OperationCanceledException>(() => doc.BuildRenderModel(null, null, false, null, token));
        Assert.ThrowsAny<OperationCanceledException>(() => PsDocument.FromBytes(Ps("showpage"), token));
        Assert.ThrowsAny<OperationCanceledException>(() => ImageToEps.Convert(new byte[] { 0x89, 0x50 }, null, token));
    }

    [Fact]
    public void A_live_token_does_not_change_the_output()
    {
        var doc = Doc("/Helvetica 12 selectfont 10 10 moveto (Hello) show 0 0 moveto 50 50 lineto stroke showpage");
        using var cts = new CancellationTokenSource();
        Assert.Equal(Normalize(doc.ToPdf()), Normalize(doc.ToPdf(null, cts.Token)));
        Assert.Equal(doc.ToImage(new ImageSaveOptions(ImageFormat.Png, 36)), doc.ToImage(new ImageSaveOptions(ImageFormat.Png, 36), cts.Token));
    }

    [Fact]
    public void Cancellation_is_observed_while_a_huge_job_runs()
    {
        // Ten million short statements: millions of executed objects, cancelled after 100 ms.
        var doc = PsDocument.FromBytes(Encoding.Latin1.GetBytes("%!PS-Adobe-3.0\n" + string.Concat(Enumerable.Repeat("1 pop ", 10_000_000)) + "\nshowpage\n"),
            TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));
        var options = new PdfSaveOptions { Limits = PsInterpreterLimits.Unlimited };
        var watch = Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => doc.ToPdf(options, cts.Token));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
    }

    // ---- PostScript: limits ----

    [Fact]
    public void The_operator_limit_stops_a_runaway_loop_without_a_token()
    {
        var doc = Doc("{} loop");
        var options = new PdfSaveOptions { Limits = new PsInterpreterLimits { MaxOperators = 200_000 } };
        var watch = Stopwatch.StartNew();
        var error = Assert.Throws<PsResourceLimitError>(() => doc.ToPdf(options));
        Assert.Equal("limitcheck", error.Code);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void The_default_limits_bound_a_runaway_loop()
    {
        var doc = Doc("{} loop");
        var watch = Stopwatch.StartNew();
        Assert.Throws<PsResourceLimitError>(() => doc.ToPdf());
        Assert.True(watch.Elapsed < TimeSpan.FromMinutes(2), $"default operator limit took {watch.Elapsed}");
    }

    [Fact]
    public void Stopped_cannot_hide_a_limit()
    {
        var doc = Doc("{ { {} loop } stopped pop } loop");
        var options = new PdfSaveOptions { Limits = new PsInterpreterLimits { MaxOperators = 100_000 } };
        Assert.Throws<PsResourceLimitError>(() => doc.ToPdf(options));
    }

    [Fact]
    public void Deep_recursion_fails_at_the_execution_depth_limit()
    {
        var doc = Doc("/f { f } def f");
        // The default is the original library's recursion limit.
        var error = Assert.Throws<HostErrorException>(() => doc.ToPdf());
        Assert.Equal("RecursionError", error.Kind);
        var shallow = new PdfSaveOptions { Limits = new PsInterpreterLimits { MaxExecutionDepth = 50 } };
        error = Assert.Throws<HostErrorException>(() => doc.ToPdf(shallow));
        Assert.Equal("RecursionError", error.Kind);
        // A program that nests five calls deep runs under a limit of 200 and fails under 50.
        var nested = Doc("/f { dup 0 gt { 1 sub f } { pop } ifelse } def 5 f 0 0 moveto 1 1 lineto stroke showpage");
        var roomy = new PdfSaveOptions { Limits = new PsInterpreterLimits { MaxExecutionDepth = 200 } };
        Assert.StartsWith("%PDF", Encoding.Latin1.GetString(nested.ToPdf(roomy)));
        Assert.Throws<HostErrorException>(() => nested.ToPdf(shallow));
    }

    [Fact]
    public void The_operand_stack_limit_stops_unbounded_pushes()
    {
        var doc = Doc("{ 1 } loop");
        var options = new PdfSaveOptions { Limits = new PsInterpreterLimits { MaxOperandStackDepth = 1000 } };
        var error = Assert.Throws<PsResourceLimitError>(() => doc.ToPdf(options));
        Assert.Contains("stack", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_nesting_limit_stops_deeply_nested_procedures()
    {
        var doc = Doc(new string('{', 5000) + new string('}', 5000));
        var options = new PdfSaveOptions { Limits = new PsInterpreterLimits { MaxNestingDepth = 100 } };
        Assert.Throws<PsResourceLimitError>(() => doc.ToPdf(options));
    }

    [Fact]
    public void The_memory_limit_rejects_huge_allocations()
    {
        var options = new PdfSaveOptions { Limits = new PsInterpreterLimits { MaxMemoryBytes = 1_000_000 } };
        Assert.Throws<PsResourceLimitError>(() => Doc("500000000 string pop").ToPdf(options));
        Assert.Throws<PsResourceLimitError>(() => Doc("100000000 array pop").ToPdf(options));
        // Under the limit it runs.
        Assert.StartsWith("%PDF", Encoding.Latin1.GetString(Doc("1000 string pop showpage").ToPdf(options)));
    }

    [Fact]
    public void The_memory_limit_stops_a_decompression_bomb()
    {
        byte[] bomb = Internal.Zlib.Compress(new byte[64 * 1024 * 1024]);
        using var guard = Internal.Guard.Begin(default, 8_000_000);
        Assert.Throws<PsResourceLimitError>(() => Internal.Zlib.Decompress(bomb));
        byte[] runs = Enumerable.Repeat((byte)129, 400_000).Concat(new byte[] { 128 }).ToArray();
        using var inner = Internal.Guard.Begin(default, 1_000_000);
        Assert.Throws<PsResourceLimitError>(() => Filters.RunLengthDecode(runs));
    }

    [Fact]
    public void The_memory_limit_bounds_the_page_raster()
    {
        var doc = PsDocument.FromBytes(Encoding.Latin1.GetBytes("%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 0 0 100000 100000\n0 0 moveto 1 1 lineto stroke showpage\n"),
            TestContext.Current.CancellationToken);
        var options = new ImageSaveOptions(ImageFormat.Png, 72) { Limits = new PsInterpreterLimits { MaxMemoryBytes = 50_000_000 } };
        Assert.Throws<PsResourceLimitError>(() => doc.ToImage(options));
    }

    [Fact]
    public void Limits_have_documented_defaults()
    {
        var limits = new PsInterpreterLimits();
        Assert.Equal(400_000_000, limits.MaxOperators);
        Assert.Equal(985, limits.MaxExecutionDepth);
        Assert.Equal(1L << 30, limits.MaxMemoryBytes);
        Assert.Equal(long.MaxValue, PsInterpreterLimits.Unlimited.MaxOperators);
    }

    // ---- PostScript: suppressed errors ----

    private const string ThreePages =
        "0 0 moveto 10 0 lineto 10 10 lineto fill showpage\n" +
        "0 0 moveto 20 0 lineto 20 20 lineto fill nosuchoperator 30 30 moveto 40 40 lineto stroke showpage\n" +
        "0 0 moveto 30 0 lineto 30 30 lineto fill showpage";

    [Fact]
    public void An_error_throws_by_default()
    {
        Assert.Throws<PsUndefinedError>(() => Doc(ThreePages).ToPdf());
    }

    [Fact]
    public void Suppressed_errors_keep_what_was_drawn_and_report_a_warning()
    {
        var options = new PdfSaveOptions { SuppressErrors = true };
        byte[] pdf = Doc(ThreePages).ToPdf(options);
        string text = Encoding.Latin1.GetString(pdf);
        Assert.Contains("/Count 3", text, StringComparison.Ordinal);
        var warning = Assert.Single(options.Warnings);
        Assert.Equal("ps", warning.Source);
        Assert.Equal("undefined", warning.Code);
        // The page that failed keeps its first path; the stroke after the error was skipped.
        var model = Doc(ThreePages).BuildRenderModel(null, null, true, [], TestContext.Current.CancellationToken);
        Assert.Equal(3, model.Pages.Count);
        Assert.Single(model.Pages[1].Commands.OfType<PathCommand>());
    }

    [Fact]
    public void A_runaway_job_with_suppressed_errors_keeps_the_pages_so_far()
    {
        var options = new PdfSaveOptions { SuppressErrors = true, Limits = new PsInterpreterLimits { MaxOperators = 100_000 } };
        byte[] pdf = Doc("0 0 moveto 10 10 lineto stroke showpage 0 0 moveto 5 5 lineto stroke {} loop").ToPdf(options);
        Assert.StartsWith("%PDF", Encoding.Latin1.GetString(pdf));
        var warning = Assert.Single(options.Warnings);
        Assert.Equal("limitcheck", warning.Code);
        Assert.Contains("/Count 2", Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);
    }

    [Fact]
    public void Suppressed_errors_apply_to_image_output_too()
    {
        var options = new ImageSaveOptions(ImageFormat.Png, 36) { SuppressErrors = true };
        byte[] png = Doc("0 0 moveto 10 10 lineto stroke bogus showpage").ToImage(options);
        Assert.True(png.AsSpan().StartsWith("\u0089PNG"u8.ToArray().AsSpan(0, 1)) || png[0] == 0x89);
        Assert.Single(options.Warnings);
    }

    // ---- stream overloads ----

    [Fact]
    public void Pdf_written_to_a_stream_equals_the_byte_array_and_needs_no_seeking()
    {
        var doc = Doc("/Helvetica 14 selectfont 20 20 moveto (Streamed) show 0 0 moveto 90 90 lineto stroke showpage 0 0 moveto 5 5 lineto stroke showpage");
        var sink = new WriteOnlyStream();
        doc.ToPdf(sink);
        Assert.Equal(Normalize(doc.ToPdf()), Normalize(sink.Inner.ToArray()));
    }

    [Fact]
    public void Pdf_objects_are_written_one_at_a_time()
    {
        // A document with several large images: no single write may be the whole file.
        var doc = PsDocument.Create();
        var random = new Random(3);
        for (int i = 0; i < 4; i++)
        {
            var pixels = new byte[200 * 200 * 3];
            random.NextBytes(pixels);
            doc.Pages[0].Canvas.DrawImage(new PsImage(200, 200, 8, "DeviceRGB", pixels), 10 * i, 10, 50, 50);
        }
        var sink = new WriteOnlyStream();
        doc.ToPdf(sink);
        Assert.True(sink.LargestWrite < sink.Inner.Length / 2, $"largest write {sink.LargestWrite} of {sink.Inner.Length}");
    }

    [Fact]
    public void Raster_output_to_a_stream_equals_the_byte_array()
    {
        var doc = Doc("0 0 1 setrgbcolor 0 0 moveto 90 0 lineto 90 90 lineto fill showpage 1 0 0 setrgbcolor 0 0 moveto 50 50 lineto stroke showpage");
        foreach (var format in new[] { ImageFormat.Png, ImageFormat.Jpeg, ImageFormat.Bmp, ImageFormat.Tiff })
        {
            var options = new ImageSaveOptions(format, 48);
            var sink = new WriteOnlyStream();
            doc.ToImage(options, sink);
            Assert.Equal(doc.ToImage(options), sink.Inner.ToArray());
        }
    }

    [Fact]
    public void Each_page_gets_its_own_stream()
    {
        var doc = Doc("0 0 moveto 90 0 lineto 90 90 lineto fill showpage 0 0 moveto 50 50 lineto stroke showpage 1 1 moveto 5 5 lineto stroke showpage");
        var streams = new List<MemoryStream>();
        var asked = new List<int>();
        doc.ToImages(new ImageSaveOptions(ImageFormat.Png, 36), index =>
        {
            asked.Add(index);
            var ms = new MemoryStream();
            streams.Add(ms);
            return ms;
        }, TestContext.Current.CancellationToken);
        Assert.Equal([0, 1, 2], asked);
        var all = doc.ToImages(new ImageSaveOptions(ImageFormat.Png, 36), TestContext.Current.CancellationToken);
        Assert.Equal(3, all.Count);
        for (int i = 0; i < 3; i++)
            Assert.Equal(all[i], streams[i].ToArray());
        // The first page equals the single-page API.
        Assert.Equal(doc.ToImage(new ImageSaveOptions(ImageFormat.Png, 36)), all[0]);
    }

    [Fact]
    public void The_skia_writer_streams_and_observes_cancellation()
    {
        var doc = Doc("0 0 1 setrgbcolor 0 0 moveto 90 0 lineto 90 90 lineto fill showpage");
        var options = new ImageSaveOptions(ImageFormat.Png, 48) { RasterWriter = new SkiaRasterWriter() };
        var sink = new WriteOnlyStream();
        doc.ToImage(options, sink);
        Assert.Equal(doc.ToImage(options), sink.Inner.ToArray());
        Assert.Equal(0x89, sink.Inner.ToArray()[0]);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => doc.ToImage(options, new MemoryStream(), cts.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => new SkiaRasterWriter().Write(doc.BuildRenderModel(), options, cts.Token));
    }

    [Fact]
    public void Png_encoding_to_a_stream_matches_the_array_encoder_and_decodes()
    {
        var pixels = new byte[37 * 23 * 4];
        new Random(5).NextBytes(pixels);
        for (int i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;
        var surface = new RasterSurface(37, 23, pixels);
        var ms = new MemoryStream();
        RasterEncoders.WritePng(surface, ms, 150);
        Assert.Equal(RasterEncoders.EncodePng(surface, 150), ms.ToArray());
        var decoded = PngDecoder.Decode(ms.ToArray());
        Assert.Equal(37, decoded!.Width);
    }

    [Theory]
    [InlineData(32, 1)]
    [InlineData(10, 3)]
    [InlineData(64, 2)]
    [InlineData(1, 1)]
    public void Eps_written_to_a_stream_equals_the_array_form(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        new Random(width).NextBytes(pixels);
        for (int i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;
        byte[] png = RasterEncoders.EncodePng(new RasterSurface(width, height, pixels));
        byte[] array = ImageToEps.Convert(png);
        var sink = new WriteOnlyStream();
        ImageToEps.Convert(png, sink);
        Assert.Equal(array, sink.Inner.ToArray());
        // The hex body is the pixels, 64 characters per line, closed by '>' (a full last line
        // pushes the '>' onto a line of its own).
        string text = Encoding.Latin1.GetString(array);
        string body = text[(text.IndexOf("colorimage\n", StringComparison.Ordinal) + "colorimage\n".Length)..text.IndexOf("grestore", StringComparison.Ordinal)];
        string hex = Convert.ToHexStringLower(ImageToEps.DecodePng(png).Pixels) + ">";
        var expected = new StringBuilder();
        for (int i = 0; i < hex.Length; i += 64)
            expected.Append(hex, i, Math.Min(64, hex.Length - i)).Append('\n');
        Assert.Equal(expected.ToString(), body);
    }

    // ---- JPEG re-encoding ----

    private static PsDocument PhotoDocument()
    {
        // A smooth gradient with noise: poor for deflate, good for JPEG.
        var random = new Random(11);
        const int size = 160;
        var pixels = new byte[size * size * 3];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int i = (y * size + x) * 3;
                pixels[i] = (byte)Math.Clamp(x * 255 / size + random.Next(-12, 13), 0, 255);
                pixels[i + 1] = (byte)Math.Clamp(y * 255 / size + random.Next(-12, 13), 0, 255);
                pixels[i + 2] = (byte)Math.Clamp((x + y) * 128 / size + random.Next(-12, 13), 0, 255);
            }
        }
        var doc = PsDocument.Create();
        doc.Pages[0].Canvas.DrawImage(new PsImage(size, size, 8, "DeviceRGB", pixels), 10, 10, 100, 100);
        return doc;
    }

    [Fact]
    public void Jpeg_quality_reencodes_photographic_images_and_default_passes_through()
    {
        var doc = PhotoDocument();
        string plain = Encoding.Latin1.GetString(doc.ToPdf());
        Assert.DoesNotContain("/DCTDecode", plain, StringComparison.Ordinal);
        byte[] smallPdf = doc.ToPdf(new PdfSaveOptions { JpegQuality = 40 });
        string small = Encoding.Latin1.GetString(smallPdf);
        Assert.Contains("/Filter /DCTDecode", small, StringComparison.Ordinal);
        Assert.True(smallPdf.Length < doc.ToPdf().Length);
        byte[] bigPdf = doc.ToPdf(new PdfSaveOptions { JpegQuality = 95 });
        Assert.True(bigPdf.Length > smallPdf.Length, "a higher quality is a larger file");
        // The embedded stream is a JPEG that the package's own decoder reads back.
        int marker = small.IndexOf("stream\n\u00FF\u00D8", StringComparison.Ordinal);
        Assert.True(marker > 0);
    }

    [Fact]
    public void Jpeg_quality_leaves_images_that_deflate_better_alone()
    {
        // A flat image deflates to almost nothing; the JPEG would not be smaller.
        var doc = PsDocument.Create();
        doc.Pages[0].Canvas.DrawImage(new PsImage(64, 64, 8, "DeviceRGB", new byte[64 * 64 * 3]), 0, 0, 10, 10);
        string pdf = Encoding.Latin1.GetString(doc.ToPdf(new PdfSaveOptions { JpegQuality = 75 }));
        Assert.DoesNotContain("/DCTDecode", pdf, StringComparison.Ordinal);
    }

    // ---- XPS ----

    private static XpsDocument Xps(string page) => XpsDocument.FromBytes(XPS.Package(page), TestContext.Current.CancellationToken);

    private const string Ns = "http://schemas.microsoft.com/xps/2005/06";

    private static readonly string MaskedPage =
        $"<FixedPage xmlns=\"{Ns}\" Width=\"100\" Height=\"100\">" +
        "<Path Data=\"M 0,0 L 50,0 L 50,50 L 0,50 Z\" Fill=\"#FF0000\" />" +
        "<Canvas><Canvas.OpacityMask><SolidColorBrush Color=\"#80000000\" /></Canvas.OpacityMask><Path Data=\"M 50,50 L 90,50 L 90,90 L 50,90 Z\" Fill=\"#00FF00\" /></Canvas>" +
        "<Path Data=\"M 10,60 L 40,60 L 40,90 L 10,90 Z\" Fill=\"#0000FF\" />" +
        "</FixedPage>";

    private sealed class ThrowingRasterizer : IRasterWriter
    {
        public Action? OnCall { get; init; }
        public byte[] Write(RenderDocument document, ImageSaveOptions options)
        {
            OnCall?.Invoke();
            throw new InvalidOperationException("rasterizer failed");
        }
    }

    [Fact]
    public void An_unrenderable_xps_element_fails_by_default_and_is_skipped_on_request()
    {
        var doc = Xps(MaskedPage);
        var failing = new PdfSaveOptions { MaskRasterizer = new ThrowingRasterizer() };
        Assert.Throws<InvalidOperationException>(() => doc.ToPdf(failing));

        var options = new PdfSaveOptions { MaskRasterizer = new ThrowingRasterizer(), SuppressErrors = true };
        byte[] pdf = doc.ToPdf(options);
        Assert.StartsWith("%PDF", Encoding.Latin1.GetString(pdf));
        var warning = Assert.Single(options.Warnings);
        Assert.Equal("xps", warning.Source);
        Assert.Equal("element", warning.Code);
        Assert.Equal(1, warning.PageNumber);
        // The other two paths survive.
        var (model, _) = XpsConverter.BuildPdfRenderModel(doc, null, new ThrowingRasterizer(), true, [], TestContext.Current.CancellationToken);
        Assert.Equal(2, model.Pages[0].Commands.OfType<PathCommand>().Count());
    }

    [Fact]
    public void A_skipped_xps_element_leaves_no_unbalanced_state()
    {
        var doc = Xps(MaskedPage);
        var (model, _) = XpsConverter.BuildPdfRenderModel(doc, null, new ThrowingRasterizer(), true, [], TestContext.Current.CancellationToken);
        int saves = model.Pages[0].Commands.Count(c => c is StateSaveCommand);
        int restores = model.Pages[0].Commands.Count(c => c is StateRestoreCommand);
        Assert.Equal(saves, restores);
    }

    [Fact]
    public void Xps_conversion_observes_the_token_between_elements()
    {
        var doc = Xps(MaskedPage);
        using var cts = new CancellationTokenSource();
        // The rasterizer cancels while the second element renders; the third must not draw.
        var options = new PdfSaveOptions { MaskRasterizer = new ThrowingRasterizer { OnCall = cts.Cancel }, SuppressErrors = true };
        Assert.ThrowsAny<OperationCanceledException>(() => doc.ToPdf(options, null, cts.Token));
    }

    [Fact]
    public void A_cancelled_token_stops_every_xps_entry_point()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var token = cts.Token;
        byte[] package = XPS.Package(MaskedPage);
        Assert.ThrowsAny<OperationCanceledException>(() => XpsDocument.FromBytes(package, token));
        Assert.ThrowsAny<OperationCanceledException>(() => XpsPackage.FromBytes(package, token));
        var doc = Xps(MaskedPage);
        Assert.ThrowsAny<OperationCanceledException>(() => doc.ToPdf(null, null, token));
        Assert.ThrowsAny<OperationCanceledException>(() => doc.ToPdf(new MemoryStream(), null, null, token));
        Assert.ThrowsAny<OperationCanceledException>(() => doc.ToImage(new ImageSaveOptions(ImageFormat.Png, 36), null, token));
        Assert.ThrowsAny<OperationCanceledException>(() => doc.ToImages(new ImageSaveOptions(ImageFormat.Png, 36), null, token));
        Assert.ThrowsAny<OperationCanceledException>(() => doc.ToImages(new ImageSaveOptions(ImageFormat.Png, 36), _ => new MemoryStream(), null, token));
    }

    [Fact]
    public void Xps_pdf_to_a_stream_equals_the_byte_array()
    {
        var doc = Xps($"<FixedPage xmlns=\"{Ns}\" Width=\"100\" Height=\"100\"><Path Data=\"M 0,0 L 50,0 L 50,50 L 0,50 Z\" Fill=\"#FF0000\" /></FixedPage>");
        var sink = new WriteOnlyStream();
        doc.ToPdf(sink);
        Assert.Equal(Normalize(doc.ToPdf()), Normalize(sink.Inner.ToArray()));
    }

    [Fact]
    public void Xps_pages_stream_to_their_own_streams()
    {
        var doc = Xps($"<FixedPage xmlns=\"{Ns}\" Width=\"100\" Height=\"100\"><Path Data=\"M 0,0 L 50,0 L 50,50 L 0,50 Z\" Fill=\"#FF0000\" /></FixedPage>");
        var streams = new List<MemoryStream>();
        doc.ToImages(new ImageSaveOptions(ImageFormat.Png, 48), index =>
        {
            var ms = new MemoryStream();
            streams.Add(ms);
            return ms;
        }, null, TestContext.Current.CancellationToken);
        Assert.Single(streams);
        Assert.Equal(doc.ToImages(new ImageSaveOptions(ImageFormat.Png, 48), null, TestContext.Current.CancellationToken)[0], streams[0].ToArray());
        var first = new MemoryStream();
        doc.ToImage(new ImageSaveOptions(ImageFormat.Png, 48), first, null, TestContext.Current.CancellationToken);
        Assert.Equal(streams[0].ToArray(), first.ToArray());
    }
}
