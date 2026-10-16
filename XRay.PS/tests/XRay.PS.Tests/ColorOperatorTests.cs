// Ported from tests/ps/test_color_ops.py.

using XRay.PS.PostScript;
using XRay.PS.Rendering;

namespace XRay.PS.Tests;

public class ColorOperatorTests
{
    private readonly RenderModelBuilder _builder = new();
    private readonly OperatorRegistry _registry = new();
    private readonly PsExecutionContext _ctx = PsPipeline.CreateDefaultContext();

    public ColorOperatorTests()
    {
        _builder.SetDefaultPageSize(100, 100);
        _ctx.DefaultPageSize = (100, 100);
        GraphicsOperators.Register(_registry, _builder);
        ColorOperators.Register(_registry, _builder);
    }

    internal static PsDict Dict(params (string Key, object? Value)[] entries)
    {
        var dict = new PsDict();
        foreach (var (key, value) in entries)
            dict.Items[key] = value;
        return dict;
    }

    internal static PsArray Arr(params object?[] items) => new([.. items]);

    private void Call(string name, params object?[] operands)
    {
        foreach (var operand in operands)
            _ctx.OperandStack.Push(operand);
        _registry.Get(name)!.Fn(_ctx);
    }

    private static PsDict Type2Function(PsArray range, PsArray c0, PsArray c1) => Dict(
        ("FunctionType", 2L), ("Domain", Arr(0L, 1L)), ("Range", range), ("C0", c0), ("C1", c1), ("N", 1L));

    [Fact]
    public void Setrgbcolor_sets_both_paints()
    {
        Call("setrgbcolor", 0.1, 0.2, 0.3);
        Assert.Equal("DeviceRGB", _ctx.State.StrokePaint.Kind);
        Assert.Equal("DeviceRGB", _ctx.State.FillPaint.Kind);
    }

    [Fact]
    public void Indexed_color_space_registers_a_resource()
    {
        Call("setcolorspace", Arr(new PsName("Indexed"), new PsName("DeviceRGB"), 1L, new PsString([0, 0, 0, 0xFF, 0, 0])));
        Call("setcolor", 1L);
        Assert.Equal("DeviceRGB", _ctx.State.FillPaint.Kind);
        Assert.NotEmpty(_builder.Document().Resources.ColorSpaces);
    }

    [Fact]
    public void DeviceN_setcolor_goes_through_the_tint_transform()
    {
        var space = Arr(new PsName("DeviceN"), Arr(new PsName("Cyan"), new PsName("Magenta")), new PsName("DeviceRGB"),
            Type2Function(Arr(0L, 1L, 0L, 1L, 0L, 1L), Arr(0L, 0L, 0L), Arr(1L, 1L, 1L)));
        Call("setcolorspace", space);
        Call("setcolor", 0.2, 0.7);
        Assert.Equal("DeviceRGB", _ctx.State.FillPaint.Kind);
    }

    [Fact]
    public void Makepattern_registers_a_pattern()
    {
        var pattern = Dict(("PatternType", 1L), ("PaintType", 1L), ("TilingType", 1L), ("BBox", Arr(0L, 0L, 10L, 10L)),
            ("XStep", 10L), ("YStep", 10L), ("PaintProc", new PsProcedure([0L, 0L, new PsName("moveto")])));
        Call("makepattern", pattern, Arr(1L, 0L, 0L, 1L, 0L, 0L));
        var result = Assert.IsType<PsPattern>(_ctx.OperandStack.Pop());
        Assert.StartsWith("P", result.PatternId);
        Assert.NotEmpty(_builder.Document().Resources.Patterns);
    }

    [Fact]
    public void Shfill_emits_a_pattern_paint()
    {
        var shading = Dict(("ShadingType", 2L), ("ColorSpace", new PsName("DeviceRGB")), ("Coords", Arr(0L, 0L, 10L, 0L)),
            ("Function", Type2Function(Arr(0L, 1L, 0L, 1L, 0L, 1L), Arr(0L, 0L, 0L), Arr(1L, 1L, 1L))), ("Extend", Arr(true, true)));
        Call("shfill", shading);
        var doc = _builder.Document();
        var command = Assert.IsType<PathCommand>(doc.Pages[0].Commands[0]);
        Assert.Equal("Pattern", command.Fill!.Kind);
    }

    [Fact]
    public void Functions_evaluate_within_their_range()
    {
        var sampled = new SampledFunction([0.0, 1.0], [0.0, 1.0], [2], 8, 1, [0.0, 1.0], [0.0, 1.0], [0x00, 0xFF]);
        Assert.InRange(sampled.Evaluate([0.5])[0], 0.0, 1.0);
        var exp = new ExponentialFunction([0.0, 1.0], [0.0, 1.0], [0.0], [1.0], 1.0);
        Assert.InRange(exp.Evaluate([0.5])[0], 0.0, 1.0);
        var stitch = new StitchingFunction([0.0, 1.0], [0.0, 1.0], [exp, exp], [0.5], [0.0, 1.0, 0.0, 1.0]);
        Assert.InRange(stitch.Evaluate([0.25])[0], 0.0, 1.0);
    }

    [Fact]
    public void The_default_icc_profile_is_bundled() => Assert.NotEmpty(ColorSpaces.LoadDefaultIccProfile());

    [Fact]
    public void Separation_names_may_be_strings()
    {
        var space = Arr(new PsName("Separation"), new PsString("Magenta"u8.ToArray()), new PsName("DeviceCMYK"),
            Type2Function(Arr(0L, 1L, 0L, 1L, 0L, 1L, 0L, 1L), Arr(0L, 0L, 0L, 0L), Arr(0L, 1L, 0L, 0L)));
        Call("setcolorspace", space);
        Assert.Equal("Magenta", Assert.IsType<SeparationColorSpace>(_ctx.State.CurrentColorSpace).Name);
    }

    [Fact]
    public void Separation_tint_transforms_may_be_procedures()
    {
        var space = Arr(new PsName("Separation"), new PsString("Magenta"u8.ToArray()), new PsName("DeviceCMYK"),
            new PsProcedure([0.0, new PsName("exch"), 0.0, 0.0]));
        Call("setcolorspace", space);
        Call("setcolor", 0.5);
        Assert.Equal("DeviceCMYK", _ctx.State.FillPaint.Kind);
        Assert.NotEmpty(_builder.Document().Resources.Functions);
    }

    [Fact]
    public void Named_cie_dictionaries_resolve()
    {
        _ctx.DictionaryStack.Peek().Items["CIEColors"] = Dict(("N", 3L));
        Call("setcolorspace", Arr(new PsName("CIEBasedABC", Literal: true), new PsName("CIEColors")));
        Assert.Equal(3, Assert.IsType<CieBasedColorSpace>(_ctx.State.CurrentColorSpace).Components);
    }

    [Fact]
    public void Cie_setcolor_normalizes_by_the_declared_ranges()
    {
        Call("setcolorspace", Arr(new PsName("CIEBasedABC", Literal: true), Dict(("RangeABC", Arr(0L, 2L, 0L, 4L, 0L, 8L)))));
        Call("setcolor", 1L, 2L, 4L);
        Assert.Equal("DeviceRGB", _ctx.State.FillPaint.Kind);
        Assert.Equal([0.5, 0.5, 0.5], _ctx.State.FillPaint.Components!);
    }

    [Fact]
    public void Sethsbcolor_wraps_hue_and_clamps_saturation_and_brightness()
    {
        Call("sethsbcolor", 1.2, 2.0, -0.5);
        Assert.Equal("DeviceRGB", Assert.IsType<DeviceColorSpace>(_ctx.State.CurrentColorSpace).Name);
        Assert.Equal([0.0, 0.0, 0.0], _ctx.State.CurrentColorComponents);
    }

    [Fact]
    public void Overprint_reaches_the_path_command()
    {
        Call("setoverprint", true);
        Call("setgray", 0.0);
        Call("moveto", 0L, 0L);
        Call("lineto", 10L, 0L);
        Call("lineto", 10L, 10L);
        Call("closepath");
        Call("fill");
        var command = Assert.IsType<PathCommand>(_builder.Document().Pages[0].Commands[0]);
        Assert.True(command.Overprint);
    }

    // The original test registered no base operators, so `[` was undefined; with them the
    // indexed space is parsed from PostScript source.
    [Fact]
    public void Indexed_color_from_source_registers_a_color_space()
    {
        var builder = new RenderModelBuilder();
        var registry = new OperatorRegistry();
        BaseOperators.Register(registry);
        GraphicsOperators.Register(registry, builder);
        ColorOperators.Register(registry, builder);
        var pipeline = new PsPipeline(new PsInterpreter(registry), registry, builder);
        var doc = pipeline.BuildRenderModel("[/Indexed /DeviceRGB 1 <000000 ff0000>] setcolorspace 1 setcolor newpath 0 0 moveto 10 0 lineto 10 10 lineto closepath fill"u8.ToArray());
        Assert.NotEmpty(doc.Resources.ColorSpaces);
        var command = Assert.IsType<PathCommand>(doc.Pages[0].Commands.OfType<PathCommand>().First());
        Assert.Equal([1.0, 0.0, 0.0], command.Fill!.Components!);
    }
}
