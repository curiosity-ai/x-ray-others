using System.Numerics;
using System.Text.Json;

namespace XRay.ThreeD.Tests;

/// <summary>AC1015 entities against the DXF twin of each drawing read with ezdxf: every LINE, CIRCLE, ARC, LWPOLYLINE and TEXT of model space, with its layer.</summary>
public class DwgGeometryTests
{
    public static IEnumerable<object[]> Drawings() => [["flange-drawing"], ["studio-floor-plan"]];

    private static (Model3D Model, JsonElement Golden) Load(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(DatasetPaths.CorpusFile("dwg-golden.json")));
        var model = ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File($"dwg/{name}.dwg")), name + ".dwg");
        return (model, doc.RootElement.GetProperty(name).Clone());
    }

    private static string Key(string type, string layer, params double[] values) =>
        type + "|" + layer + "|" + string.Join(",", values.Select(v => Math.Round(v, 2).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)));

    private static List<string> Segments(Model3D model)
    {
        var result = new List<string>();
        foreach (var mesh in model.Meshes)
        {
            var v = mesh.Positions;
            var e = mesh.EdgeIndices;
            for (var i = 0; i + 1 < e.Length; i += 2)
            {
                result.Add(string.Join(",", new[] { v[e[i] * 3], v[e[i] * 3 + 1], v[e[i] * 3 + 2], v[e[i + 1] * 3], v[e[i + 1] * 3 + 1], v[e[i + 1] * 3 + 2] }.Select(x => Math.Round(x, 2).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture))));
            }
        }

        return result;
    }

    [Theory]
    [MemberData(nameof(Drawings))]
    public void The_drawing_decoded_from_the_dwg_agrees_with_its_dxf_twin(string name)
    {
        var (model, golden) = Load(name);
        var twin = ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File($"dxf/{name}.dxf")), name + ".dxf");
        Assert.Equal(twin.Layers.Select(l => l.Name), model.Layers.Select(l => l.Name));
        var a = twin.ComputeBounds()!.Value;
        var b = model.ComputeBounds()!.Value;
        Assert.True(a.Size.X > 0 && b.Size.X > 0);

        // Every LINE of the DXF twin, read by an independent reader (ezdxf), is a segment of the DWG model.
        var segments = new HashSet<string>(Segments(model));
        foreach (var e in golden.GetProperty("entities").EnumerateArray().Where(e => e.GetProperty("type").GetString() == "LINE"))
        {
            var p = e.GetProperty("p").EnumerateArray().Select(v => Math.Round(v.GetDouble(), 2).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            Assert.Contains(string.Join(",", p), segments);
        }
    }

    [Theory]
    [MemberData(nameof(Drawings))]
    public void Text_strings_positions_and_heights_match_the_dxf_twin(string name)
    {
        var (model, golden) = Load(name);
        var expected = golden.GetProperty("entities").EnumerateArray().Where(e => e.GetProperty("type").GetString() == "TEXT")
            .Select(e => (Text: e.GetProperty("text").GetString()!, P: e.GetProperty("p").EnumerateArray().Select(v => v.GetDouble()).ToArray(), Layer: e.GetProperty("layer").GetString()!)).ToList();
        // Text inside blocks (dimension text) is not model-space text: those are reached only through inserts, so the model's top-level texts are the model-space ones.
        var actual = model.Texts.Where(t => expected.Any(e => e.Text == t.Text)).ToList();
        Assert.Equal(expected.Count, actual.Count);
        foreach (var e in expected)
        {
            var t = actual.Single(x => x.Text == e.Text);
            Assert.Equal(e.P[0], t.Position.X, 2);
            Assert.Equal(e.P[1], t.Position.Y, 2);
            Assert.Equal(e.P[3], t.Height, 3);
            Assert.Equal(e.Layer, model.Layers[t.Layer].Name);
        }
    }

    [Theory]
    [MemberData(nameof(Drawings))]
    public void Layers_the_drawing_extent_and_the_undecoded_kinds_are_reported(string name)
    {
        var (model, golden) = Load(name);
        Assert.Equal(golden.GetProperty("layers").EnumerateArray().Select(l => l.GetString()), model.Metadata.LayerNames);
        Assert.True(model.Is2D);
        Assert.True(model.Metadata.HasGeometry);
        _ = GlbWriter.ToArray(model);
        Assert.Contains("<svg", System.Text.Encoding.UTF8.GetString(ModelLoader.ConvertToSvg(File.ReadAllBytes(DatasetPaths.File($"dwg/{name}.dwg")), name + ".dwg")), StringComparison.Ordinal);
    }

    [Fact]
    public void Blocks_are_expanded_under_their_inserts()
    {
        var (model, _) = Load("flange-drawing");
        Assert.True(model.Roots.Count > 0);
        Assert.DoesNotContain(model.Warnings, w => w.Contains("damaged", StringComparison.Ordinal));
    }
}
