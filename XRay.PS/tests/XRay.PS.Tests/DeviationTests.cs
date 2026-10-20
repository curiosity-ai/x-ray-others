// Regression tests for the deliberate deviations from the original listed in CLAUDE.md
// ("Deviations from the original"): each failure class our render benchmark against Ghostscript
// found, pinned with a small synthetic input and, where the corpus has one, a real file.

using System.Text;
using XRay.PS.Fonts;
using XRay.PS.Imaging;
using XRay.PS.PostScript;
using XRay.PS.Rendering;

namespace XRay.PS.Tests;

public class DeviationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Interprets a program with every operator set registered, as a conversion does, and returns
    // the context (operand stack included) and the render model.
    private static (PsExecutionContext Ctx, RenderDocument Model) Execute(string program)
    {
        var builder = new RenderModelBuilder();
        builder.SetDefaultPageSize(612, 792);
        var registry = new OperatorRegistry();
        var fonts = new FontResolver();
        var images = new PsImageStore();
        BaseOperators.Register(registry);
        GraphicsOperators.Register(registry, builder);
        ColorOperators.Register(registry, builder);
        TextOperators.Register(registry, builder, fonts);
        ImageOperators.Register(registry, builder, images);
        ExtraOperators.Register(registry, builder);
        var ctx = PsPipeline.CreateDefaultContext(fonts, images);
        ctx.DefaultPageSize = (612, 792);
        new PsInterpreter(registry).Execute(Encoding.Latin1.GetBytes(program), ctx);
        return (ctx, builder.Document());
    }

    private static object? Top(string program) => Execute(program).Ctx.OperandStack.Peek();

    private static byte[] DosEps(byte[] postscript, byte[] preview)
    {
        using var ms = new MemoryStream();
        var header = new byte[30];
        BitConverter.GetBytes(0xC6D3D0C5u).CopyTo(header, 0);
        BitConverter.GetBytes(30u).CopyTo(header, 4);
        BitConverter.GetBytes((uint)postscript.Length).CopyTo(header, 8);
        BitConverter.GetBytes((uint)(30 + postscript.Length)).CopyTo(header, 20);
        BitConverter.GetBytes((uint)preview.Length).CopyTo(header, 24);
        header[28] = 0xFF;
        header[29] = 0xFF;
        ms.Write(header);
        ms.Write(postscript);
        ms.Write(preview);
        return ms.ToArray();
    }

    private static readonly byte[] SmallEps =
        "%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 10 20 110 70\n0 0 1 setrgbcolor 10 20 moveto 110 70 lineto stroke showpage\n"u8.ToArray();

    [Fact]
    public void Dos_eps_binary_renders_its_postscript_section()
    {
        // A TIFF preview after the program, as Photoshop and Illustrator write it.
        byte[] file = DosEps(SmallEps, [.. "II*\0"u8, .. new byte[64]]);
        var doc = PsDocument.FromBytes(file, Token);
        Assert.True(doc.IsEps);
        Assert.Equal((10, 20, 110, 70), doc.Dsc!.BoundingBox);
        Assert.Same(file, doc.Data);
        var model = doc.BuildRenderModel(null, null, false, null, Token);
        var page = Assert.Single(model.Pages);
        Assert.Equal(100.0, page.Width);
        Assert.Contains(page.Commands, c => c is PathCommand);
    }

    [Fact]
    public void Dos_eps_header_pointing_outside_the_file_is_a_syntax_error()
    {
        byte[] file = DosEps(SmallEps, []);
        BitConverter.GetBytes(10_000u).CopyTo(file, 4);
        Assert.Throws<PsSyntaxError>(() => PsDocument.FromBytes(file, Token));
        Assert.Throws<PsSyntaxError>(() => PsDocument.FromBytes(file[..12], Token));
    }

    [Fact]
    public void Dos_eps_section_running_past_the_end_is_read_to_the_end()
    {
        byte[] file = DosEps(SmallEps, []);
        BitConverter.GetBytes(1_000_000u).CopyTo(file, 8);
        var model = PsDocument.FromBytes(file, Token).BuildRenderModel(null, null, false, null, Token);
        Assert.Contains(Assert.Single(model.Pages).Commands, c => c is PathCommand);
    }

    [Fact]
    public void Pjl_job_header_and_trailer_are_skipped()
    {
        byte[] file = Encoding.Latin1.GetBytes(
            "\u001b%-12345X@PJL JOB NAME=\"x\"\r\n@PJL SET RESOLUTION=600\r\n@PJL ENTER LANGUAGE = POSTSCRIPT\r\n" +
            "%!PS-Adobe-3.0\n0 0 moveto 100 100 lineto stroke showpage\n\u001b%-12345X@PJL EOJ\r\n\u001b%-12345X");
        var model = PsDocument.FromBytes(file, Token).BuildRenderModel(null, null, false, null, Token);
        Assert.Contains(Assert.Single(model.Pages).Commands, c => c is PathCommand);
    }

    [Fact]
    public void Backslash_before_a_non_octal_digit_is_dropped()
    {
        var token = new PsTokenizer(@"(\8\9\18\101)"u8.ToArray()).NextToken();
        Assert.Equal("89\u00018A"u8.ToArray(), Assert.IsType<byte[]>(token!.Value.Value));
    }

    [Fact]
    public void Fallback_operators_answer_only_where_no_dictionary_defines_the_name()
    {
        Assert.Equal(6L, Top("5 3 xor"));
        Assert.Equal(42L, Top("/xor { pop pop 42 } def 5 3 xor"));
        Assert.Equal(2.0, Top("100 log"));
        Assert.Equal(8.0, Top("2 3 exp"));
        var screen = Execute("currentscreen").Ctx.OperandStack.Items;
        Assert.IsType<PsProcedure>(screen[^1]);
        Assert.Equal(true, Top("{ stop } stopped"));
    }

    [Fact]
    public void Where_and_systemdict_see_the_built_in_operators()
    {
        var stack = Execute("/moveto where /nosuchname where").Ctx.OperandStack.Items;
        Assert.Equal(false, stack[^1]);
        Assert.Equal(true, stack[^2]);
        Assert.IsType<PsDict>(stack[^3]);
        Assert.Equal(new PsOperator("findfont"), Top("systemdict /findfont get"));
        Assert.Equal(true, Top("systemdict /initmatrix known"));
    }

    [Fact]
    public void Immediate_names_are_substituted_when_scanned()
    {
        var proc = Assert.IsType<PsProcedure>(Top("/a 5 def { //a /a } /a 6 def"));
        Assert.Equal(5L, proc.Items[0]);
        Assert.Equal(new PsName("a", true), proc.Items[1]);
    }

    [Fact]
    public void Getinterval_of_an_array_shares_its_elements()
    {
        Assert.Equal(9L, Top("/a [1 2 3] def a 1 2 getinterval 0 9 put a 1 get"));
        Assert.Equal(7L, Top("/a 3 array def 7 8 a 1 2 getinterval astore pop a 1 get"));
    }

    [Fact]
    public void Exec_of_an_object_that_is_not_executable_pushes_it()
    {
        Assert.Equal(3L, Top("3 exec"));
    }

    [Fact]
    public void Token_scans_an_object_from_a_string()
    {
        var stack = Execute("(  12 rest) token").Ctx.OperandStack.Items;
        Assert.Equal(true, stack[^1]);
        Assert.Equal(12L, stack[^2]);
        Assert.Equal("rest"u8.ToArray(), Assert.IsType<PsString>(stack[^3]).Value);
    }

    [Fact]
    public void Readline_reads_the_program_text_after_the_token()
    {
        var stack = Execute("currentfile 80 string readline\nfirst line\n").Ctx.OperandStack.Items;
        Assert.Equal(true, stack[^1]);
        Assert.Equal("first line"u8.ToArray(), Assert.IsType<PsString>(stack[^2]).Value);
    }

    [Fact]
    public void Flushfile_on_a_subfile_filter_skips_to_after_its_end_marker()
    {
        var stack = Execute("currentfile 0 (%%EOD) /SubFileDecode filter flushfile\nnot program text { ( %%EOD 42").Ctx.OperandStack.Items;
        Assert.Equal(42L, stack[^1]);
    }

    [Fact]
    public void Execform_hands_paintproc_the_form_dictionary()
    {
        var model = Execute("<< /FormType 1 /BBox [0 0 100 100] /Matrix [1 0 0 1 0 0] /PaintProc { begin 0 0 moveto 50 50 lineto stroke end } >> execform").Model;
        Assert.Contains(model.Pages[0].Commands, c => c is PathCommand);
        Assert.Contains(model.Pages[0].Commands, c => c is ClipCommand);
    }

    [Fact]
    public void Rectstroke_takes_a_matrix_operand()
    {
        var model = Execute("10 10 100 200 [4 0 0 1 0 0] rectstroke").Model;
        Assert.Contains(model.Pages[0].Commands, c => c is PathCommand);
    }

    [Fact]
    public void Grestore_with_nothing_saved_is_not_an_error()
    {
        Assert.Equal(1L, Top("grestore grestore 1"));
    }

    [Fact]
    public void Currentcolorspace_returns_an_array_for_a_device_space()
    {
        Assert.Equal(new PsName("DeviceRGB", true), Top("1 0 0 setrgbcolor currentcolorspace 0 get"));
        Assert.Equal(new PsName("DeviceCMYK", true), Top("/DeviceCMYK setcolorspace currentcolorspace 0 get"));
    }

    [Fact]
    public void Tint_procedures_beyond_arithmetic_run_in_the_interpreter()
    {
        var model = Execute("[/Separation /Spot /DeviceCMYK { dup 0.5 gt { pop 1 } if 0 0 0 4 -1 roll } bind] setcolorspace 1 setcolor 0 0 10 10 rectfill").Model;
        Assert.Contains(model.Pages[0].Commands, c => c is PathCommand);
    }

    [Fact]
    public void Exponential_and_stitching_functions_need_no_range()
    {
        var model = Execute("<< /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 100 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >> >> shfill").Model;
        Assert.Single(model.Resources.Functions);
    }

    [Fact]
    public void Scaling_a_scaled_font_multiplies_the_sizes_and_keeps_a_flip()
    {
        var state = Execute("/Helvetica findfont 4 scalefont 2 scalefont [1 0 0 -1 0 0] makefont setfont").Ctx.State;
        Assert.Equal(8.0, state.FontSize, 9);
        Assert.Equal(new Matrix(1, 0, 0, -1, 0, 0), state.FontShape);
    }

    [Fact]
    public void Synthesized_font_dictionaries_answer_for_fontinfo()
    {
        Assert.Equal(-100.0, Top("/Helvetica findfont /FontInfo get /UnderlinePosition get"));
    }

    [Fact]
    public void Statusdict_has_the_printer_procedures_print_files_call()
    {
        Assert.Equal(1L, Top("0 statusdict /setpapertray get exec 1"));
    }

    [Fact]
    public void Definefont_accepts_any_key()
    {
        var stack = Execute("/F << /FontType 3 /FontMatrix [0.001 0 0 0.001 0 0] /FontBBox [0 0 1000 1000] /Encoding 256 array " +
            "/BuildChar { pop pop 1000 0 setcharwidth } >> dup definefont").Ctx.OperandStack.Items;
        Assert.IsType<PsDict>(stack[^1]);
    }

    // Top-down image data ([w 0 0 -h 0 h]) starts at the top of the image; the original drew it
    // upside down. The black sample is the first, so the top-left quarter must be dark.
    [Theory]
    [InlineData("2 2 8 [2 0 0 -2 0 2] {<00ff ffff>} image", true)]
    [InlineData("2 2 8 [2 0 0 2 0 0] {<00ff ffff>} image", false)]
    public void Image_rows_are_placed_by_the_image_matrix(string image, bool topLeftDark)
    {
        string program = "%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 0 0 200 200\n100 100 scale " + image + " showpage\n";
        var options = new ImageSaveOptions(ImageFormat.Png, 72) { OpaqueBackground = true };
        byte[] png = PsDocument.FromBytes(Encoding.Latin1.GetBytes(program), Token).ToImage(options, Token);
        var decoded = PngDecoder.Decode(png)!;
        int stride = decoded.Pixels.Length / (decoded.Width * decoded.Height);
        byte Pixel(int x, int y) => decoded.Pixels[(y * decoded.Width + x) * stride];
        // The image covers the lower-left 100x100 of the 200x200 page.
        Assert.Equal(topLeftDark, Pixel(25, 125) < 128);
        Assert.Equal(!topLeftDark, Pixel(25, 175) < 128);
        Assert.True(Pixel(75, 125) > 128);
    }

    // The original stepped past the start of a contour that ends in a curve, went round it until
    // the segment cap and dropped every later contour.
    [Fact]
    public void Glyph_contour_ending_in_a_curve_is_closed_once()
    {
        List<List<GlyphPoint>> contours =
        [
            [new(0, 0, true), new(100, 0, true), new(100, 100, false)],
            [new(10, 10, true), new(20, 10, true), new(20, 20, true)],
        ];
        var path = GlyphOutlines.ContoursToPath(contours, 1.0, 1, 0, 0, 1, 0, 0);
        Assert.Equal(2, path.Segments.Count(s => s.Kind == PathSegmentKind.Move));
        Assert.True(path.Segments.Count < 12);
    }

    [Fact]
    public void Initclip_undoes_a_clip_set_at_the_same_level()
    {
        var commands = Execute("0 0 10 10 rectclip initclip 100 100 moveto 200 200 lineto stroke").Model.Pages[0].Commands;
        int save = commands.FindIndex(c => c is StateSaveCommand);
        int clip = commands.FindIndex(c => c is ClipCommand);
        int restore = commands.FindIndex(c => c is StateRestoreCommand);
        Assert.True(save >= 0 && save < clip && clip < restore);
        Assert.True(restore < commands.FindIndex(c => c is PathCommand));
    }

    // The original swapped the axes for %%ViewingOrientation: 0 1 -1 0, mirroring the page.
    [Fact]
    public void Viewing_orientation_turns_the_page_without_mirroring_it()
    {
        string program = "%!PS-Adobe-3.0\n%%DocumentMedia: A 612 792 0 () ()\n%%ViewingOrientation: 0 1 -1 0\n%%EndComments\n" +
            "10 20 moveto 30 20 lineto stroke showpage\n";
        var model = PsDocument.FromBytes(Encoding.Latin1.GetBytes(program), Token).BuildRenderModel(null, null, false, null, Token);
        var page = Assert.Single(model.Pages);
        Assert.Equal((792.0, 612.0), (page.Width, page.Height));
        var points = page.Commands.OfType<PathCommand>().Single().Path.Segments.Select(s => s.Points[0]).ToList();
        // (x, y) to (y, 612 - x): the line runs downwards from (20, 602) to (20, 582).
        Assert.Equal(20.0, points[0].X, 6);
        Assert.Equal(602.0, points[0].Y, 6);
        Assert.Equal(582.0, points[1].Y, 6);
    }

    // Corpus files from the benchmark: each was blank, or threw, before.
    [Theory]
    [InlineData("ps/integration/PAGEJAVA-66.eps")] // DOS EPS, Illustrator procsets
    [InlineData("ps/integration/PAGEJAVA-29.eps")] // DOS EPS, multiple data source image
    [InlineData("ps/integration/PAGEJAVA-153.ps")] // PJL header, \9 in a string
    [InlineData("ps/integration/PAGENET-70.eps")] // procedure data source, string token
    [InlineData("ps/integration/PDFNET-50318.eps")] // CID ranges
    [InlineData("ps/functional/FORMS12/ROLODEX.PS")] // execform
    [InlineData("ps/functional/PATHS02/USRPATH3.PS")] // ufill
    [InlineData("ps/integration/PAGEJAVA-95-13-for-testing.ps")] // statusdict
    public void Benchmark_fixture_converts_without_errors_and_draws(string relativePath)
    {
        var doc = PsDocument.FromFile(Corpus.File(relativePath), Token);
        var model = doc.BuildRenderModel(null, null, false, null, Token);
        Assert.Contains(model.Pages, p => p.Commands.Any(c => c is PathCommand or TextCommand or ImageCommand));
    }

    [Fact]
    public void Eggs_reads_fontinfo_and_shows_the_whole_text()
    {
        var doc = PsDocument.FromFile(Corpus.File("ps/functional/TEXT03/EGGS.PS"), Token);
        var model = doc.BuildRenderModel(null, null, false, null, Token);
        string text = string.Concat(model.Pages[0].Commands.OfType<TextCommand>().Select(t => t.Text));
        Assert.Contains("special eggs", text, StringComparison.Ordinal);
        Assert.Contains("Today", text, StringComparison.Ordinal);
    }
}
