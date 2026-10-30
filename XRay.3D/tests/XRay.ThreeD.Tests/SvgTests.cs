using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace XRay.ThreeD.Tests;

public class SvgTests
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    private static readonly JsonElement Golden = JsonDocument.Parse(File.ReadAllText(DatasetPaths.CorpusFile("dxf-drawing-golden.json"))).RootElement;

    public static IEnumerable<object[]> Files() => Golden.EnumerateObject().Select(p => new object[] { p.Name });

    private static string PathOf(string file) => DatasetPaths.File((File.Exists(Path.Combine(DatasetPaths.Root, "dxf", file)) ? "dxf/" : "synthetic/") + file);

    private static XDocument Convert(string path, SvgWriteOptions? options = null) =>
        XDocument.Parse(Encoding.UTF8.GetString(ModelLoader.ConvertToSvg(File.ReadAllBytes(path), Path.GetFileName(path), options)));

    private static string Normalise(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [Theory]
    [MemberData(nameof(Files))]
    public void Drawn_text_and_fills_match_the_independent_reader(string file)
    {
        var expected = Golden.GetProperty(file);
        var model = ModelLoader.Load(PathOf(file));

        foreach (var text in expected.GetProperty("texts").EnumerateArray())
        {
            var wanted = Normalise(text.GetProperty("text").GetString()!);
            var x = text.GetProperty("x").GetSingle();
            var y = text.GetProperty("y").GetSingle();
            var drawn = model.Texts.Where(t => Normalise(t.Text) == wanted && Math.Abs(t.Position.X - x) < 1e-2f && Math.Abs(t.Position.Y - y) < 1e-2f).ToList();
            Assert.True(drawn.Count > 0, $"{file}: text '{wanted}' at {x},{y} was not drawn");
            Assert.Contains(drawn, t => Math.Abs(t.Height - text.GetProperty("height").GetSingle()) < 1e-2f);
            var rotation = text.GetProperty("rotation").GetSingle();
            Assert.Contains(drawn, t => Math.Abs((((t.Rotation - rotation) % 360) + 540) % 360 - 180) < 1e-2f);
        }

        Assert.True(model.Fills.Count >= expected.GetProperty("solids").GetInt32() + expected.GetProperty("solidHatches").GetInt32());
    }

    [Fact]
    public void A_drawing_becomes_well_formed_svg_with_layers_text_fills_and_blocks()
    {
        var path = DatasetPaths.File("synthetic/drawing.dxf");
        var model = ModelLoader.Load(path);
        Assert.True(model.Is2D);
        Assert.Equal(2, model.Fills.Count);
        Assert.Equal(2, model.Fills.Single(f => f.Loops.Count == 2).Loops.Count);

        var doc = Convert(path);
        var root = doc.Root!;
        Assert.Equal(Svg + "svg", root.Name);
        var box = root.Attribute("viewBox")!.Value.Split(' ').Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(4, box.Length);
        // Y is flipped: the drawing spans y 0..40, so the box starts at about -40.
        Assert.InRange(box[1], -42, -39);
        Assert.Equal("dxf", root.Attribute("data-format")!.Value);
        Assert.Equal("millimeter", root.Attribute("data-units")!.Value);

        var layers = root.Elements(Svg + "g").ToDictionary(g => g.Attribute("data-name")!.Value);
        Assert.Contains("WALLS", layers.Keys);
        Assert.Contains("NOTES", layers.Keys);
        Assert.Contains("FILLS", layers.Keys);

        var room = layers["NOTES"].Elements(Svg + "text").Single(t => t.Value == "Room 101");
        Assert.Contains("rotate(-90", room.Attribute("transform")!.Value);
        Assert.Equal("3", room.Attribute("font-size")!.Value);
        var multi = layers["NOTES"].Elements(Svg + "text").Single(t => t.Elements(Svg + "tspan").Any());
        Assert.Equal(["Line one", "Line two"], multi.Elements(Svg + "tspan").Select(t => t.Value));

        var fills = layers["FILLS"].Elements(Svg + "path").Where(p => p.Attribute("fill-rule") is not null).ToList();
        Assert.Equal(2, fills.Count);
        Assert.Contains(fills, p => p.Attribute("d")!.Value.Count(c => c == 'M') == 2 && p.Attribute("fill")!.Value == "#ff0000");

        // Metadata travels along.
        Assert.NotNull(root.Element(Svg + "metadata"));
        Assert.Contains(root.Descendants().Where(e => e.Name.LocalName == "layer"), e => e.Attribute("name")!.Value == "WALLS");
    }

    [Fact]
    public void Meshes_shared_by_several_nodes_are_defined_once_and_placed_with_use()
    {
        // A DXF reader expands its blocks into the layer's geometry; a scene that really instances a mesh (glTF) keeps it shared.
        var doc = Convert(DatasetPaths.File("synthetic/instances.gltf"), new SvgWriteOptions { ProjectFlat = true });
        var root = doc.Root!;
        var defs = root.Element(Svg + "defs");
        Assert.NotNull(defs);
        var ids = defs!.Elements(Svg + "g").Select(g => "#" + g.Attribute("id")!.Value).ToHashSet();
        var uses = root.Descendants(Svg + "use").ToList();
        Assert.True(uses.Count >= 2);
        Assert.All(uses, u => Assert.Contains(u.Attribute("href")!.Value, ids));
        Assert.All(uses, u => Assert.StartsWith("matrix(", u.Attribute("transform")!.Value));

        var inline = Convert(DatasetPaths.File("synthetic/instances.gltf"), new SvgWriteOptions { ProjectFlat = true, ReuseBlocks = false });
        Assert.Empty(inline.Descendants(Svg + "use"));
    }

    [Fact]
    public void Three_dimensional_models_are_refused_unless_projected()
    {
        var path = DatasetPaths.File("synthetic/entities.dxf");
        Assert.False(ModelLoader.Load(path).Is2D);
        Assert.Throws<ModelFormatException>(() => ModelLoader.ConvertToSvg(File.ReadAllBytes(path), "entities.dxf"));
        var projected = Convert(path, new SvgWriteOptions { ProjectFlat = true, IncludeMetadata = false });
        Assert.Null(projected.Root!.Element(Svg + "metadata"));
        Assert.Throws<ModelFormatException>(() => ModelLoader.ConvertToSvg(File.ReadAllBytes(DatasetPaths.File("step/neopixel-ring.step")), "x.step"));
    }

    [Theory]
    [InlineData("dxf/flange-drawing.dxf")]
    [InlineData("dxf/studio-floor-plan.dxf")]
    [InlineData("dxf/godown-warehouse-30-x-20-m-with-loading-dock-plan-drawing.dxf")]
    [InlineData("slddrw/rocket-fin-set.slddrw")]
    public void Real_drawings_are_flagged_and_write_well_formed_svg(string relative)
    {
        var path = DatasetPaths.File(relative);
        var model = ModelLoader.Load(path);
        Assert.True(model.Is2D, relative);
        var doc = Convert(path);
        Assert.Equal(Svg + "svg", doc.Root!.Name);
    }

    [Fact]
    public void Text_with_characters_xml_cannot_carry_is_still_well_formed()
    {
        var bytes = Encoding.UTF8.GetBytes(File.ReadAllText(DatasetPaths.File("synthetic/drawing.dxf")).Replace("Room 101", "Room\u0001 <101> & \"x\""));
        var doc = XDocument.Parse(Encoding.UTF8.GetString(ModelLoader.ConvertToSvg(bytes, "drawing.dxf")));
        Assert.Contains(doc.Descendants(Svg + "text"), t => t.Value.Contains("<101> & \"x\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Writing_stops_on_a_cancelled_token()
    {
        var model = ModelLoader.Load(DatasetPaths.File("synthetic/drawing.dxf"));
        var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => SvgWriter.ToString(model, new SvgWriteOptions { Cancellation = cts.Token }));
    }
}
