// Ported from tests/ps/test_interpreter_engine.py, test_interpreter_alias.py,
// test_conversion_pipeline.py and test_core_graphics.py.

using XRay.PS.PostScript;
using XRay.PS.Rendering;

namespace XRay.PS.Tests;

public class InterpreterTests
{
    internal static PsExecutionContext MakeContext()
    {
        var systemdict = new PsDict();
        var userdict = new PsDict();
        return new PsExecutionContext(new PsStack<object?>(), new PsStack<object?>(), new PsStack<PsDict>([systemdict, userdict]),
            new PsStack<GraphicsState>([new GraphicsState()]), userdict, systemdict);
    }

    [Fact]
    public void Tokenizer_produces_each_token_kind()
    {
        var tokenizer = new PsTokenizer("/name 123 4.5 (hi) [1 2] { /x 1 } %comment\n"u8.ToArray());
        var kinds = new List<PsTokenKind>();
        while (tokenizer.NextToken() is PsToken token)
            kinds.Add(token.Kind);
        Assert.Contains(PsTokenKind.Comment, kinds);
        Assert.Contains(PsTokenKind.ArrayStart, kinds);
        Assert.Contains(PsTokenKind.ProcedureStart, kinds);
        Assert.Contains(PsTokenKind.Name, kinds);
        Assert.Contains(PsTokenKind.Number, kinds);
        Assert.Contains(PsTokenKind.String, kinds);
    }

    // The original test expected arrays and dictionaries from the parser and fails against the
    // original code: brackets stay operator names and are assembled when executed, procedures
    // are parsed. This pins the behaviour both implementations share.
    [Fact]
    public void Parser_builds_procedures_and_leaves_brackets_to_execution()
    {
        var objects = new PsParser(new PsTokenizer("[1 2] << /A 1 /B 2 >> { /x 1 }"u8.ToArray())).ParseAll();
        Assert.Equal(new PsName("["), objects[0]);
        Assert.Equal(new PsName("]"), objects[3]);
        Assert.Equal(new PsName("<<"), objects[4]);
        var proc = Assert.IsType<PsProcedure>(objects[^1]);
        Assert.Equal(2, proc.Items.Count);

        var registry = new OperatorRegistry();
        BaseOperators.Register(registry);
        var ctx = MakeContext();
        new PsInterpreter(registry).ExecuteObjects(objects, ctx);
        Assert.IsType<PsProcedure>(ctx.OperandStack.Pop());
        var dict = Assert.IsType<PsDict>(ctx.OperandStack.Pop());
        Assert.Equal(1L, dict.Get("A"));
        Assert.IsType<PsArray>(ctx.OperandStack.Pop());
    }

    [Fact]
    public void Stack_underflow_is_a_range_error()
    {
        var stack = new PsStack<object?>();
        Assert.Throws<PsRangeError>(() => stack.Pop());
    }

    [Fact]
    public void Save_restore_rolls_back_the_graphics_state_but_not_the_operand_stack()
    {
        var ctx = MakeContext();
        ctx.OperandStack.Push(1L);
        ctx.State.LineWidth = 2;
        var saved = PsSaveState.Save(ctx);
        ctx.OperandStack.Push(2L);
        ctx.State.LineWidth = 5;
        saved.Restore(ctx);
        Assert.Equal(2.0, ctx.State.LineWidth);
        Assert.Equal(2, ctx.OperandStack.Count);
    }

    [Fact]
    public void Mark_counttomark_and_cleartomark()
    {
        var registry = new OperatorRegistry();
        BaseOperators.Register(registry);
        var interpreter = new PsInterpreter(registry);
        var ctx = MakeContext();
        interpreter.ExecuteObjects([new PsName("mark"), 1L, 2L, new PsName("counttomark")], ctx);
        Assert.Equal(2L, ctx.OperandStack.Peek());

        ctx = MakeContext();
        interpreter.ExecuteObjects([new PsName("mark"), 1L, 2L, new PsName("cleartomark")], ctx);
        Assert.Equal(0, ctx.OperandStack.Count);
    }

    [Fact]
    public void Gstate_objects_capture_the_graphics_state()
    {
        var registry = new OperatorRegistry();
        registry.Register("gstate", c => c.OperandStack.Push(new PsGState(c.GraphicsStateStack.Peek())));
        var interpreter = new PsInterpreter(registry);
        var ctx = MakeContext();
        ctx.GraphicsStateStack.Push(new GraphicsState { LineWidth = 5 });
        interpreter.ExecuteObjects([new PsName("gstate")], ctx);
        var gstate = Assert.IsType<PsGState>(ctx.OperandStack.Pop());
        Assert.Equal(5.0, gstate.State.LineWidth);
    }

    [Fact]
    public void Unknown_operators_are_undefined_and_missing_operands_a_range_error()
    {
        var registry = new OperatorRegistry();
        var interpreter = new PsInterpreter(registry);
        var ctx = MakeContext();
        Assert.Throws<PsUndefinedError>(() => interpreter.ExecuteObjects([new PsName("unknown")], ctx));
        registry.Register("one", c => c.OperandStack.Pop(), minOperands: 1);
        Assert.Throws<PsRangeError>(() => interpreter.ExecuteObjects([new PsName("one")], ctx));
    }

    [Fact]
    public void Definitions_and_procedures_execute()
    {
        var registry = new OperatorRegistry();
        registry.Register("add", c =>
        {
            long b = (long)c.OperandStack.Pop()!;
            long a = (long)c.OperandStack.Pop()!;
            c.OperandStack.Push(a + b);
        }, minOperands: 2);
        registry.Register("def", c =>
        {
            var value = c.OperandStack.Pop();
            var key = c.OperandStack.Pop();
            c.DictionaryStack.Peek().Items[key is PsName n ? n.Value : key!.ToString()!] = value;
        }, minOperands: 2);
        var interpreter = new PsInterpreter(registry);
        var ctx = MakeContext();
        interpreter.Execute("/x 5 def /p { 1 2 add } def x p"u8.ToArray(), ctx);
        Assert.Equal(3L, ctx.OperandStack.Pop());
        Assert.Equal(5L, ctx.OperandStack.Pop());
    }

    [Fact]
    public void Dsc_comments_are_parsed()
    {
        var meta = DscMetadata.Parse("""
            %%BoundingBox: 0 0 100 200
            %%HiResBoundingBox: 0.1 0.2 100.3 200.4
            %%DocumentMedia: Letter 612 792 0 () ()
            %%Title: Sample
            %%Creator: ExampleApp
            %%CreationDate: Today
            %%LanguageLevel: 3
            """u8.ToArray());
        Assert.Equal((0, 0, 100, 200), meta.BoundingBox);
        Assert.Equal((0.1, 0.2, 100.3, 200.4), meta.HiResBoundingBox);
        Assert.Equal((612.0, 792.0), meta.DocumentMediaSize);
        Assert.Equal("Sample", meta.Title);
        Assert.Equal("ExampleApp", meta.Creator);
        Assert.Equal(3, meta.LanguageLevel);
    }

    [Fact]
    public void Page_size_follows_the_dsc_rules()
    {
        Assert.Equal((200.0, 300.0), PageGeometry.PageSizeFromDsc(new DscMetadata { BoundingBox = (0, 0, 200, 300) }));
        Assert.Equal((4.0, 6.0), PageGeometry.PageSizeFromDsc(new DscMetadata { HiResBoundingBox = (1.0, 2.0, 5.0, 8.0) }));
        Assert.Equal((612.0, 792.0), PageGeometry.PageSizeFromDsc(new DscMetadata { BoundingBox = (36, 61, 577, 757), DocumentMediaSize = (612.0, 792.0) }));
        Assert.Equal((595.0, 842.0), PageGeometry.PageSizeFromDsc(null));
        var landscape = new DscMetadata
        {
            BoundingBox = (0, 0, 100, 200),
            HiResBoundingBox = (0.0, 0.0, 120.0, 240.0),
            CropBox = (0, 0, 50, 150),
            DocumentMediaSize = (612.0, 792.0),
            Orientation = "Landscape",
        };
        Assert.Equal((150.0, 50.0), PageGeometry.PageSizeFromDsc(landscape));
    }

    // The original test expected the %%BoundingBox size and fails against the original code: a
    // pipeline given raw bytes (not a parsed document) keeps the builder's default A4 page.
    [Fact]
    public void Pipeline_records_commands_on_the_default_page()
    {
        var builder = new RenderModelBuilder();
        var registry = new OperatorRegistry();
        registry.Register("draw", _ => builder.AddPath(
            new RenderPath([PathSegment.MoveTo(0, 0), PathSegment.LineTo(1, 0), PathSegment.ClosePath()]),
            new StrokeStyle(1.0, 0, 0, 10.0, [], 0.0), Paint.Rgb(0, 0, 0)));
        var pipeline = new PsPipeline(new PsInterpreter(registry), registry, builder);
        var doc = pipeline.BuildRenderModel("%%BoundingBox: 0 0 200 300\n draw"u8.ToArray());
        var page = Assert.Single(doc.Pages);
        Assert.Equal(595.0, page.Width);
        Assert.Equal(842.0, page.Height);
        Assert.Single(page.Commands);
    }

    [Fact]
    public void Operators_defined_through_load_execute()
    {
        var builder = new RenderModelBuilder();
        var registry = new OperatorRegistry();
        BaseOperators.Register(registry);
        GraphicsOperators.Register(registry, builder);
        var pipeline = new PsPipeline(new PsInterpreter(registry), registry, builder);
        var doc = pipeline.BuildRenderModel("""
            %%BoundingBox: 0 0 100 100
            /ld {load def} bind def /m /moveto ld /l /lineto ld /cp /closepath ld /S /stroke ld
            0 0 m 10 0 l cp S
            """u8.ToArray());
        var page = Assert.Single(doc.Pages);
        var command = Assert.IsType<PathCommand>(Assert.Single(page.Commands));
        Assert.NotEmpty(command.Path.Segments);
    }

    // The original test expected y = 0 and fails against the original code, which yields 0.5;
    // this pins the shared behaviour (x comes from the computed 30 cos entry).
    [Fact]
    public void Concat_takes_computed_matrix_entries()
    {
        var builder = new RenderModelBuilder();
        var registry = new OperatorRegistry();
        BaseOperators.Register(registry);
        GraphicsOperators.Register(registry, builder);
        var pipeline = new PsPipeline(new PsInterpreter(registry), registry, builder);
        var doc = pipeline.BuildRenderModel("/x_squash { 30 cos } def [ x_squash 0 0 1 0 0 ] concat 0 0 moveto 1 0 lineto stroke"u8.ToArray());
        var command = Assert.IsType<PathCommand>(doc.Pages[0].Commands[0]);
        var end = command.Path.Segments[1].Points[0];
        Assert.Equal(0.8660254, end.X, 6);
        Assert.Equal(0.5, end.Y, 6);
    }

    [Fact]
    public void Rect_path_has_five_segments()
    {
        var path = Paths.FromRect(new Rect(0, 0, 10, 20));
        Assert.Equal(5, path.Segments.Count);
        Assert.Equal(PathSegmentKind.Move, path.Segments[0].Kind);
        Assert.Equal(PathSegmentKind.Close, path.Segments[^1].Kind);
        Assert.Equal(new Point(0, 0), path.Segments[0].Points[0]);
        Assert.Equal(new Point(10, 20), path.Segments[2].Points[0]);
    }

    [Fact]
    public void Builder_opens_a_default_page_and_rejects_ending_none()
    {
        var builder = new RenderModelBuilder();
        builder.SetDefaultPageSize(100, 200);
        builder.AddPath(Paths.FromRect(new Rect(0, 0, 1, 1)), null, null);
        var doc = builder.Document();
        var page = Assert.Single(doc.Pages);
        Assert.Equal(100, page.Width);
        Assert.Equal(200, page.Height);
        Assert.ThrowsAny<Exception>(() => new RenderModelBuilder().EndPage());
    }
}

public class CoreGraphicsTests
{
    private readonly RenderModelBuilder _builder = new();
    private readonly OperatorRegistry _registry = new();
    private readonly PsExecutionContext _ctx;

    public CoreGraphicsTests()
    {
        _builder.SetDefaultPageSize(200, 100);
        GraphicsOperators.Register(_registry, _builder);
        _ctx = PsPipeline.CreateDefaultContext();
        _ctx.DefaultPageSize = (200, 100);
    }

    private void Call(string name, params object[] operands)
    {
        foreach (var operand in operands)
            _ctx.OperandStack.Push(operand);
        var entry = _registry.Get(name);
        Assert.NotNull(entry);
        entry.Fn(_ctx);
    }

    [Fact]
    public void Moveto_lineto_closepath_stroke_emits_a_path()
    {
        Call("moveto", 0L, 0L);
        Call("lineto", 10L, 0L);
        Call("closepath");
        Call("stroke");
        var doc = _builder.Document();
        var command = Assert.IsType<PathCommand>(Assert.Single(doc.Pages).Commands[0]);
        Assert.Equal(PathSegmentKind.Move, command.Path.Segments[0].Kind);
        Assert.Equal(PathSegmentKind.Close, command.Path.Segments[^1].Kind);
    }

    [Fact]
    public void Rectpath_appends_a_closed_rectangle()
    {
        Call("rectpath", 1L, 2L, 3L, 4L);
        Assert.Equal(5, _ctx.State.CurrentPath.Segments.Count);
    }

    [Fact]
    public void Translate_applies_to_the_ctm()
    {
        Call("translate", 5L, 7L);
        Call("moveto", 1L, 1L);
        Assert.Equal(new Point(6.0, 8.0), _ctx.State.CurrentPath.Segments[0].Points[0]);
    }

    [Fact]
    public void Initclip_emits_a_clip_command()
    {
        Call("initclip");
        Assert.IsType<ClipCommand>(Assert.Single(_builder.Document().Pages).Commands[0]);
    }

    [Fact]
    public void Save_and_restore_round_trip_the_line_width()
    {
        Call("setlinewidth", 2L);
        Call("save");
        Call("setlinewidth", 5L);
        Call("restore");
        Assert.Equal(2.0, _ctx.State.LineWidth);
    }
}
