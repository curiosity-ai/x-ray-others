// Ported from tests/ps/test_text_ops.py.

using XRay.PS.Fonts;
using XRay.PS.PostScript;
using XRay.PS.Rendering;

namespace XRay.PS.Tests;

public class TextOperatorTests
{
    private readonly RenderModelBuilder _builder = new();
    private readonly OperatorRegistry _registry = new();
    private readonly PsExecutionContext _ctx = PsPipeline.CreateDefaultContext();

    public TextOperatorTests()
    {
        _builder.SetDefaultPageSize(100, 100);
        GraphicsOperators.Register(_registry, _builder);
        TextOperators.Register(_registry, _builder, new FontResolver());
    }

    private void Call(string name, params object?[] operands)
    {
        foreach (var operand in operands)
            _ctx.OperandStack.Push(operand);
        _registry.Get(name)!.Fn(_ctx);
    }

    private static PsString Str(string text) => PsString.FromText(text);

    private List<TextCommand> TextCommands() => _builder.Document().Pages[0].Commands.OfType<TextCommand>().ToList();

    private double StringWidth(string text)
    {
        Call("stringwidth", Str(text));
        _ctx.OperandStack.Pop();
        return PsOps.ToDouble(_ctx.OperandStack.Pop());
    }

    [Fact]
    public void Show_emits_a_text_command()
    {
        Call("selectfont", new PsName("Helvetica"), 12L);
        Call("show", Str("Hi"));
        Assert.IsType<TextCommand>(_builder.Document().Pages[0].Commands[0]);
    }

    [Fact]
    public void Xshow_splits_the_text_per_glyph()
    {
        Call("selectfont", new PsName("Helvetica"), 12L);
        Call("xshow", Str("AB"), new PsArray([1L, 1L]));
        Assert.Equal(2, _builder.Document().Pages[0].Commands.Count);
    }

    [Fact]
    public void Missing_fonts_fall_back_to_a_substitute_with_widths()
    {
        var font = new FontResolver().Resolve("MissingFont");
        Assert.True(font.Substitute);
        Assert.NotEmpty(font.GlyphWidths);
    }

    // The original test pushed the operands in the wrong order and failed with a typecheck. Both
    // implementations take the adjusted character as a one-character string, in the cx cy char
    // string order.
    [Fact]
    public void Widthshow_adds_the_offset_after_each_matching_character()
    {
        Call("selectfont", new PsName("Helvetica"), 12L);
        double baseWidth = StringWidth("AA");
        _ctx.State.TextMatrix = Matrix.Identity;
        Call("widthshow", 2.0, 0.0, Str("A"), Str("AA"));
        Assert.Equal(baseWidth + 4.0, _ctx.State.TextMatrix.E, 4);
    }

    [Fact]
    public void Show_advances_through_a_scaled_text_matrix()
    {
        Call("selectfont", new PsName("Helvetica"), 12L);
        double baseWidth = StringWidth("A");
        _ctx.State.TextMatrix = new Matrix(2, 0, 0, 2, 0, 0);
        Call("show", Str("A"));
        Assert.Equal(baseWidth * 2.0, _ctx.State.TextMatrix.E, 4);
    }

    // Operand order corrected as for widthshow: cx cy char ax ay string.
    [Fact]
    public void Awidthshow_advances_the_text()
    {
        Call("selectfont", new PsName("Helvetica"), 12L);
        double baseWidth = StringWidth("A");
        _ctx.State.TextMatrix = Matrix.Identity;
        Call("awidthshow", 1.0, 0.0, Str("A"), 2.0, 0.0, Str("A"));
        Assert.Equal(baseWidth + 3.0, _ctx.State.TextMatrix.E, 4);
    }

    private static PsProcedure SquareProc(long size) => new([
        0L, 0L, new PsOperator("moveto"), size, 0L, new PsOperator("lineto"), size, size, new PsOperator("lineto"),
        0L, size, new PsOperator("lineto"), new PsOperator("closepath"), new PsOperator("fill")]);

    private static FontResource Type3Font(PsProcedure proc) =>
        new("Type3Font", "Type3", 1000, new Dictionary<int, string> { [65] = "A" }, new Dictionary<string, double>(), false,
            CharProcs: new Dictionary<object, PsProcedure> { ["A"] = proc });

    [Fact]
    public void Type3_glyph_procedures_emit_paths()
    {
        _ctx.State.Font = Type3Font(SquareProc(10));
        _ctx.State.FontSize = 12;
        Call("show", Str("A"));
        Assert.Contains(_builder.Document().Pages[0].Commands, c => c is PathCommand);
    }

    // The original test expected charpath to collect the Type 3 outline and fails against the
    // original code: a Type 3 procedure that fills paints instead of adding to the path. This
    // pins that shared behaviour.
    [Fact]
    public void Charpath_of_a_filling_type3_glyph_adds_no_segments()
    {
        _ctx.State.Font = Type3Font(SquareProc(5));
        _ctx.State.FontSize = 12;
        Call("charpath", Str("A"), true);
        Assert.Empty(_ctx.State.CurrentPath.Segments);
    }

    [Fact]
    public void Type0_fmaptype2_maps_selector_and_character_bytes()
    {
        var empty = new FontResource("EmptyFont", "Type3", 1000, new Dictionary<int, string>(), new Dictionary<string, double>(), false,
            CharProcs: new Dictionary<object, PsProcedure>(), CodeWidths: new Dictionary<int, double>());
        var cards = new FontResource("Helvetica", "Type1", 1000, new Dictionary<int, string> { [65] = "A" },
            new Dictionary<string, double> { ["A"] = 600.0 }, false, CodeWidths: new Dictionary<int, double> { [65] = 600.0 });
        var fmap = new int[256];
        fmap['C'] = 1;
        var composite = new FontResource("Playing-Cards", "Type0", 1000, new Dictionary<int, string> { [65] = "A" },
            new Dictionary<string, double> { ["A"] = 600.0 }, false, Descendant: cards, FdepVector: [empty, cards],
            FmapEncoding: fmap, FmapType: 2);
        _ctx.State.Font = composite;
        _ctx.State.FontSize = 12;
        Call("show", Str("CA"));
        var text = Assert.Single(TextCommands());
        Assert.Equal("A", text.Text);
        Assert.Equal("Helvetica", text.FontRef);
    }

    [Fact]
    public void Xshow_with_a_full_array_uses_the_explicit_advances()
    {
        Call("selectfont", new PsName("Helvetica"), 12L);
        Call("xshow", Str("AB"), new PsArray([100.0, 200.0]));
        Assert.Equal(300.0, _ctx.State.TextMatrix.E, 6);
    }

    [Fact]
    public void Show_follows_a_reencoded_font()
    {
        _ctx.State.Font = new FontResource("ReencodedFont", "Type1", 1000,
            new Dictionary<int, string> { ['P'] = "K", ['o'] = "l", ['!'] = "exclam" },
            new Dictionary<string, double> { ["K"] = 500.0, ["l"] = 500.0, ["exclam"] = 500.0 }, false,
            CodeWidths: new Dictionary<int, double> { ['P'] = 500.0, ['o'] = 500.0, ['!'] = 500.0 });
        _ctx.State.FontSize = 12;
        Call("show", Str("Po!"));
        Assert.Equal("Kl!", Assert.Single(TextCommands()).Text);
    }

    [Fact]
    public void Pipeline_shows_text()
    {
        var builder = new RenderModelBuilder();
        var registry = new OperatorRegistry();
        TextOperators.Register(registry, builder, new FontResolver());
        var pipeline = new PsPipeline(new PsInterpreter(registry), registry, builder);
        var doc = pipeline.BuildRenderModel("/Helvetica 12 selectfont (Hi) show"u8.ToArray());
        Assert.IsType<TextCommand>(Assert.Single(doc.Pages).Commands[0]);
    }
}
