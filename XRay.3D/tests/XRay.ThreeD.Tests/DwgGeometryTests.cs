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

    [Theory]
    [MemberData(nameof(Drawings))]
    public void Lines_circles_arcs_and_polylines_match_the_dxf_twin(string name)
    {
        var (model, golden) = Load(name);
        var layerOf = (SceneNode n) => n.Layer >= 0 ? model.Layers[n.Layer].Name : "?";

        var expected = new List<string>();
        foreach (var e in golden.GetProperty("entities").EnumerateArray())
        {
            var type = e.GetProperty("type").GetString()!;
            var layer = e.GetProperty("layer").GetString()!;
            var p = e.GetProperty("p");
            switch (type)
            {
                case "LINE":
                    expected.Add(Key(type, layer, p.EnumerateArray().Select(v => v.GetDouble()).ToArray()));
                    break;
                case "CIRCLE":
                    expected.Add(Key(type, layer, p.EnumerateArray().Select(v => v.GetDouble()).ToArray()));
                    break;
                case "ARC":
                {
                    var v = p.EnumerateArray().Select(x => x.GetDouble()).ToArray();
                    expected.Add(Key(type, layer, v[0], v[1], v[2], v[3]));
                    break;
                }

                case "LWPOLYLINE":
                {
                    var pts = p.EnumerateArray().Select(q => q.EnumerateArray().Select(x => x.GetDouble()).ToArray()).ToList();
                    expected.Add(Key(type, layer, pts[0][0], pts[0][1], e.GetProperty("closed").GetBoolean() ? 1 : 0));
                    break;
                }
            }
        }

        var actual = new List<string>();
        foreach (var node in model.Roots.Where(n => n.Name is "LINE" or "CIRCLE" or "ARC" or "LWPOLYLINE"))
        {
            var m = node.Mesh!;
            var v = m.Positions;
            var layer = layerOf(node);
            switch (node.Name)
            {
                case "LINE":
                    actual.Add(Key("LINE", layer, v[0], v[1], v[2], v[3], v[4], v[5]));
                    break;
                case "CIRCLE":
                {
                    var b = m.ComputeBounds();
                    actual.Add(Key("CIRCLE", layer, b.Center.X, b.Center.Y, b.Center.Z, b.Size.X / 2));
                    break;
                }

                case "ARC":
                {
                    // Centre and radius from the start, middle and end of the sampled arc: the centre is the point equidistant from them.
                    int n = m.VertexCount - 1;
                    Vector2 P(int i) => new(v[i * 3], v[i * 3 + 1]);
                    Vector2 a = P(0), c = P(n / 2), d = P(n);
                    var det = 2 * (a.X * (c.Y - d.Y) + c.X * (d.Y - a.Y) + d.X * (a.Y - c.Y));
                    var cx = ((a.X * a.X + a.Y * a.Y) * (c.Y - d.Y) + (c.X * c.X + c.Y * c.Y) * (d.Y - a.Y) + (d.X * d.X + d.Y * d.Y) * (a.Y - c.Y)) / det;
                    var cy = ((a.X * a.X + a.Y * a.Y) * (d.X - c.X) + (c.X * c.X + c.Y * c.Y) * (a.X - d.X) + (d.X * d.X + d.Y * d.Y) * (c.X - a.X)) / det;
                    actual.Add(Key("ARC", layer, cx, cy, 0, Vector2.Distance(new Vector2(cx, cy), a)));
                    break;
                }

                case "LWPOLYLINE":
                {
                    var closed = m.VertexCount > 2 && Math.Abs(v[0] - v[(m.VertexCount - 1) * 3]) < 1e-4 && Math.Abs(v[1] - v[(m.VertexCount - 1) * 3 + 1]) < 1e-4;
                    actual.Add(Key("LWPOLYLINE", layer, v[0], v[1], closed ? 1 : 0));
                    break;
                }
            }
        }

        // ARC keys above carry (centre, radius); the golden holds the same four numbers.
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
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
        foreach (var other in golden.GetProperty("other").EnumerateObject().Where(o => o.Name == "HATCH"))
        {
            Assert.Contains(model.Warnings, w => w.Contains("HATCH", StringComparison.Ordinal) && w.StartsWith(other.Value.GetInt32().ToString(), StringComparison.Ordinal));
        }

        _ = GlbWriter.ToArray(model);
        Assert.Contains("<svg", System.Text.Encoding.UTF8.GetString(ModelLoader.ConvertToSvg(File.ReadAllBytes(DatasetPaths.File($"dwg/{name}.dwg")), name + ".dwg")), StringComparison.Ordinal);
    }

    [Fact]
    public void Blocks_become_shared_meshes_under_insert_transforms()
    {
        var (model, _) = Load("flange-drawing");
        var inserts = model.Roots.Where(n => n.Name == "INSERT").ToList();
        Assert.NotEmpty(inserts);
        Assert.All(inserts, n => Assert.NotEmpty(n.Children));
        // The same block is drawn by more than one insert with the very same meshes.
        var meshes = inserts.SelectMany(n => n.Children).Select(c => c.Mesh).Where(m => m is not null).GroupBy(m => m).Count();
        Assert.True(inserts.SelectMany(n => n.Children).Count(c => c.Mesh is not null) > meshes);
    }
}
