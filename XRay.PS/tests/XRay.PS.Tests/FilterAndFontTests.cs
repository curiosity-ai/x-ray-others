// Ported from tests/ps/test_image_filters.py, test_font_cache.py, test_font_types.py and
// test_type1_parser.py.

using System.Text;
using XRay.PS.Fonts;
using XRay.PS.Internal;
using XRay.PS.PostScript;
using XRay.PS.Rendering;
using static XRay.PS.Tests.ColorOperatorTests;

namespace XRay.PS.Tests;

public class FilterTests
{
    [Fact]
    public void Ascii_hex_decodes_to_the_eod_marker() => Assert.Equal("abc"u8.ToArray(), Filters.AsciiHexDecode("61 62 63>"u8));

    [Fact]
    public void Ascii85_round_trips()
    {
        // base64.a85encode(b"hello")
        Assert.Equal("hello"u8.ToArray(), Ascii85.DecodeFilter("BOu!rDZ"u8));
    }

    [Fact]
    public void Run_length_stops_at_eod() => Assert.Equal("ABC"u8.ToArray(), Filters.RunLengthDecode([2, (byte)'A', (byte)'B', (byte)'C', 128]));

    [Fact]
    public void Lzw_decodes_nine_bit_codes() => Assert.Equal("AB"u8.ToArray(), Filters.LzwDecode(PackLzwCodes([256, 65, 66, 257])));

    private static byte[] PackLzwCodes(int[] codes)
    {
        long buffer = 0;
        int count = 0;
        var output = new List<byte>();
        foreach (int code in codes)
        {
            buffer = (buffer << 9) | (uint)code;
            count += 9;
            while (count >= 8)
            {
                output.Add((byte)((buffer >> (count - 8)) & 0xFF));
                count -= 8;
                buffer &= (1L << count) - 1;
            }
        }
        if (count > 0)
            output.Add((byte)((buffer << (8 - count)) & 0xFF));
        return [.. output];
    }

    private static (RenderModelBuilder, OperatorRegistry, PsImageStore, PsExecutionContext) Setup()
    {
        var builder = new RenderModelBuilder();
        builder.SetDefaultPageSize(100, 100);
        var registry = new OperatorRegistry();
        var store = new PsImageStore();
        ImageOperators.Register(registry, builder, store);
        return (builder, registry, store, PsPipeline.CreateDefaultContext());
    }

    [Fact]
    public void Image_registers_a_resource()
    {
        var (builder, registry, store, ctx) = Setup();
        foreach (var operand in new object[] { 1L, 1L, 8L, Arr(1L, 0L, 0L, 1L, 0L, 0L), new PsString([0xFF]) })
            ctx.OperandStack.Push(operand);
        registry.Get("image")!.Fn(ctx);
        var command = Assert.IsType<ImageCommand>(builder.Document().Pages[0].Commands[0]);
        Assert.Equal(new byte[] { 0xFF }, store.Get(command.ImageId).Data);
    }

    [Fact]
    public void Imagemask_registers_a_one_bit_mask()
    {
        var (builder, registry, store, ctx) = Setup();
        foreach (var operand in new object[] { 1L, 1L, true, Arr(1L, 0L, 0L, 1L, 0L, 0L), new PsString([0xFF]) })
            ctx.OperandStack.Push(operand);
        registry.Get("imagemask")!.Fn(ctx);
        var command = Assert.IsType<ImageCommand>(builder.Document().Pages[0].Commands[0]);
        var resource = store.Get(command.ImageId);
        Assert.True(resource.Mask);
        Assert.Equal(1, resource.BitsPerComponent);
    }

    // The original test registered no base operators, so `[` was undefined.
    [Fact]
    public void Image_from_source_through_the_pipeline()
    {
        var builder = new RenderModelBuilder();
        var registry = new OperatorRegistry();
        var store = new PsImageStore();
        BaseOperators.Register(registry);
        ImageOperators.Register(registry, builder, store);
        var pipeline = new PsPipeline(new PsInterpreter(registry), registry, builder, imageStore: store);
        var doc = pipeline.BuildRenderModel("1 1 8 [1 0 0 1 0 0] <FF> image"u8.ToArray());
        Assert.IsType<ImageCommand>(Assert.Single(doc.Pages).Commands[0]);
    }
}

public sealed class FontTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("xrayps-fonts-").FullName;

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    private static byte[] Liberation(string style) => FontLocation.Bundled($"LiberationSerif-{style}.ttf").ReadAllBytes();

    private string Copy(string style, string name)
    {
        string path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, Liberation(style));
        return path;
    }

    // The original test also expected a fuzzy match for an unknown family ("UnitTestFontTypo") and
    // fails against the original code, which finds none; that assertion is left out.
    [Fact]
    public void Find_font_prefers_the_additional_folder_and_reads_styles()
    {
        string regular = Copy("Regular", "UnitTestFont-Regular.ttf");
        string boldItalic = Copy("BoldItalic", "UnitTestFont-BoldItalic.ttf");
        Copy("Regular", "Optima.ttf");
        string optimaBold = Copy("Regular", "Optima_B.ttf");
        var cache = new FontCache();
        cache.Load(_tempDir);

        var record = cache.FindFont("UnitTestFont", null);
        Assert.NotNull(record);
        Assert.Equal(Path.GetFullPath(regular), record.Path.Path);
        Assert.Equal("Regular", record.Style);

        var bi = cache.FindFont("UnitTestFont-BoldItalic", null);
        Assert.NotNull(bi);
        Assert.Equal(Path.GetFullPath(boldItalic), bi.Path.Path);
        Assert.Equal("BoldItalic", bi.Style);

        var optima = cache.FindFont("Optima-Bold", null);
        Assert.NotNull(optima);
        Assert.Equal(Path.GetFullPath(optimaBold), optima.Path.Path);
        Assert.Equal("Bold", optima.Style);
    }

    [Fact]
    public void Metrics_include_units_and_widths()
    {
        var cache = new FontCache();
        cache.Load(null);
        var metrics = cache.MetricsFor(FontLocation.File(Copy("Regular", "metrics.ttf")));
        Assert.True(metrics.UnitsPerEm > 0);
        Assert.NotEmpty(metrics.CodeWidths);
    }

    private static byte[] Encrypt(byte[] plain, int r)
    {
        var output = new byte[plain.Length];
        for (int i = 0; i < plain.Length; i++)
        {
            int cipher = plain[i] ^ (r >> 8);
            output[i] = (byte)cipher;
            r = ((cipher + r) * 52845 + 22719) & 0xFFFF;
        }
        return output;
    }

    // lenIV 4, then hsbw(0, 500), endchar.
    private static readonly byte[] HsbwCharstring = [0, 0, 0, 0, 139, 248, 136, 13, 14];

    [Fact]
    public void Charstring_width_comes_from_hsbw() => Assert.Equal(500.0, Type1Parser.ParseCharstringWidth(Encrypt(HsbwCharstring, 4330), 4));

    [Fact]
    public void Type1_widths_come_from_the_embedded_program()
    {
        byte[] charEncrypted = Encrypt(HsbwCharstring, 4330);
        var privatePlain = new List<byte>();
        privatePlain.AddRange(Encoding.Latin1.GetBytes($"/lenIV 4 def\n/CharStrings 1 dict dup begin\n/A {charEncrypted.Length} RD "));
        privatePlain.AddRange(charEncrypted);
        privatePlain.AddRange(" ND\nend\n"u8.ToArray());
        byte[] eexec = Encrypt([0, 0, 0, 0, .. privatePlain], 55665);
        var encoding = Enumerable.Range(0, 256).Select(i => (object?)new PsName(i == 65 ? "A" : ".notdef", Literal: true)).ToArray();
        var fontDict = Dict(("FontName", new PsName("EmbeddedType1", Literal: true)), ("FontMatrix", Arr(0.001, 0.0, 0.0, 0.001, 0.0, 0.0)),
            ("Encoding", Arr(encoding)), ("__type1_program__", new PsString(eexec)));
        var font = FontTypes.LoadType1Font(fontDict);
        Assert.NotNull(font.CodeWidths);
        Assert.Equal(500.0, font.CodeWidths[65]);
    }

    [Fact]
    public void Type42_metrics_come_from_sfnts()
    {
        var font = FontTypes.LoadType42Font(Dict(("FontName", "TestType42"), ("sfnts", Arr(new PsString(Liberation("Regular"))))));
        Assert.True(font.UnitsPerEm > 0);
        Assert.NotNull(font.CodeWidths);
        Assert.NotEmpty(font.CodeWidths);
    }

    [Fact]
    public void Type1_resource_blocks_give_name_and_widths()
    {
        byte[] data = File.ReadAllBytes(Corpus.File("ps/functional/FONTS10/PAGEJAVA-295.ps"));
        string text = Encoding.Latin1.GetString(data);
        int start = text.IndexOf("%%BeginResource: font (ArialMT)", StringComparison.Ordinal);
        int end = text.IndexOf("%%EndResource", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var metrics = Type1Parser.ParseResourceBlock(data[start..(end + "%%EndResource".Length)]);
        Assert.NotNull(metrics);
        Assert.Equal("ArialMT", metrics.FontName);
        Assert.True(metrics.UnitsPerEm > 0);
        Assert.True(metrics.CodeWidths[65] > 0.0);
    }
}
