using System.Numerics;
using System.Text.Json;

namespace XRay.ThreeD.Tests;

public class StepTests
{
    private static readonly JsonElement Golden = JsonDocument.Parse(File.ReadAllText(DatasetPaths.CorpusFile("step-golden.json"))).RootElement;

    public static IEnumerable<object[]> Files() => Golden.EnumerateObject().Select(p => new object[] { p.Name });

    /// <summary>Surface area and enclosed volume of everything the model draws, in its own units.</summary>
    internal static (double Area, double Volume) Measure(Model3D model)
    {
        double area = 0;
        double volume = 0;
        void Visit(SceneNode node, Matrix4x4 parent)
        {
            var world = node.Transform * parent;
            if (node.Mesh is { } mesh)
            {
                var p = new Vector3[mesh.VertexCount];
                for (var i = 0; i < p.Length; i++)
                {
                    p[i] = Vector3.Transform(new Vector3(mesh.Positions[3 * i], mesh.Positions[3 * i + 1], mesh.Positions[3 * i + 2]), world);
                }

                for (var t = 0; t + 2 < mesh.Indices.Length; t += 3)
                {
                    Vector3 a = p[mesh.Indices[t]], b = p[mesh.Indices[t + 1]], c = p[mesh.Indices[t + 2]];
                    area += 0.5 * Vector3.Cross(b - a, c - a).Length();
                    volume += Vector3.Dot(a, Vector3.Cross(b, c)) / 6.0;
                }
            }

            foreach (var child in node.Children)
            {
                Visit(child, world);
            }
        }

        foreach (var root in model.Roots)
        {
            Visit(root, Matrix4x4.Identity);
        }

        return (area, volume);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Tessellation_matches_opencascade(string file)
    {
        var expected = Golden.GetProperty(file);
        var model = ModelLoader.Load(DatasetPaths.File("step/" + file));
        var (area, volume) = Measure(model);

        Assert.Equal("step", model.Metadata.Format.Id);
        Assert.Equal(LengthUnit.Millimeter, model.Metadata.Units);
        var expectedArea = expected.GetProperty("area").GetDouble();
        var expectedVolume = expected.GetProperty("volume").GetDouble();
        Assert.True(Math.Abs(area / expectedArea - 1) < 0.005, $"{file}: area {area:F2} vs exact {expectedArea:F2}");
        Assert.True(Math.Abs(volume / expectedVolume - 1) < 0.005, $"{file}: volume {volume:F2} vs exact {expectedVolume:F2}");

        var bounds = model.ComputeBounds()!.Value;
        var min = expected.GetProperty("min").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
        var max = expected.GetProperty("max").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
        // The mesh sits just inside the exact boundary of curved parts (chordal error), never outside it by more than rounding.
        var tolerance = (float)Math.Max(0.1, 0.002 * (bounds.Max - bounds.Min).Length());
        Assert.True(Vector3.Distance(bounds.Min, new Vector3(min[0], min[1], min[2])) < tolerance, $"{file}: min {bounds.Min} vs {string.Join(',', min)}");
        Assert.True(Vector3.Distance(bounds.Max, new Vector3(max[0], max[1], max[2])) < tolerance, $"{file}: max {bounds.Max} vs {string.Join(',', max)}");
        Assert.DoesNotContain(model.Warnings, w => w.Contains("skipped", StringComparison.Ordinal));
    }

    [Fact]
    public void Assembly_structure_names_properties_and_colours()
    {
        var model = ModelLoader.Load(DatasetPaths.File("step/feather-esp8266.step"));
        var meta = model.Metadata;

        Assert.Contains("Autodesk Translation Framework", meta.Application);
        Assert.Equal("AP214", meta.GetProperty("application protocol"));
        var root = Assert.Single(model.Roots);
        Assert.Equal("Adafruit ESP8266 Feather v4", root.Name);
        var pcb = Assert.Single(root.Children);
        Assert.Equal("PCB Component", pcb.Name);
        Assert.Equal(29, pcb.Children.Count);

        // Four 10uF capacitors are four placements of one part: one mesh, four nodes, four different positions.
        var caps = pcb.Children.Where(c => c.Name == "0805-NO").ToList();
        Assert.Equal(4, caps.Count);
        Assert.Single(caps.Select(c => c.Mesh).Distinct());
        Assert.Equal(4, caps.Select(c => c.Transform.Translation).Distinct().Count());
        Assert.Equal("10uF", caps[0].Properties["productId"]);
        Assert.Equal("Steel", caps[0].Properties["material"]);

        Assert.Contains("Board", meta.PartNames);
        Assert.True(model.Materials.Count >= 10);
        Assert.All(model.Meshes, m => Assert.Equal(m.Indices.Length, m.Faces.Sum(f => f.IndexCount)));
        Assert.True(model.Meshes.Sum(m => m.Faces.Length) > 1000);
    }

    [Fact]
    public void Metadata_alone_needs_no_tessellation()
    {
        var meta = ModelLoader.ReadMetadata(DatasetPaths.File("step/adafruit-motor-hat.step"));

        Assert.Equal("step", meta.Format.Id);
        Assert.Equal(LengthUnit.Millimeter, meta.Units);
        Assert.True(meta.HasGeometry);
        Assert.NotEmpty(meta.PartNames);
        Assert.Null(meta.Statistics);
    }

    [Fact]
    public void Output_is_valid_glb_and_compresses()
    {
        var model = ModelLoader.Load(DatasetPaths.File("step/neopixel-ring.step"));
        var plain = GlbWriter.ToArray(model);
        GlbValidation.Validate(plain);
        var packed = GlbWriter.ToArray(model, new GlbWriteOptions { Compression = GlbCompression.Meshopt });

        Assert.True(packed.Length < plain.Length / 2);
        Assert.Equal(model.ComputeStatistics().Triangles, ModelLoader.Load(packed, "x.glb").ComputeStatistics().Triangles);
    }

    [Fact]
    public void Coarser_tolerance_gives_fewer_triangles_with_the_same_bounds()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("step/neopixel-ring.step"));
        var fine = ModelLoader.Load(data, "x.step", new LoadOptions { LinearDeflection = 0.0002, AngularDeflection = 0.1 });
        var coarse = ModelLoader.Load(data, "x.step", new LoadOptions { LinearDeflection = 0.01, AngularDeflection = 0.8 });

        Assert.True(fine.ComputeStatistics().Triangles > 2 * coarse.ComputeStatistics().Triangles);
        Assert.True(Math.Abs(Measure(fine).Volume / 1896.0979 - 1) < 0.001);
    }

    [Fact]
    public void Edge_cases_in_the_file_syntax_are_parsed()
    {
        var text = "ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('a'),'2;1');\nFILE_NAME('n \\X2\\00E9\\X0\\','2020-01-01T00:00:00',('me','you'),(''),'pp','sys','');\nFILE_SCHEMA(('CONFIG_CONTROL_DESIGN'));\nENDSEC;\nDATA;\n/* comment; with semicolon */\n#1=CARTESIAN_POINT('it''s;',(0.,0.,0.));\nENDSEC;\nEND-ISO-10303-21;\n";
        var ex = Assert.Throws<ModelFormatException>(() => ModelLoader.Load(System.Text.Encoding.Latin1.GetBytes(text), "x.step"));

        Assert.Contains("no solid geometry", ex.Message, StringComparison.Ordinal);
        var meta = ModelLoader.ReadMetadata(System.Text.Encoding.Latin1.GetBytes(text), "x.step");
        Assert.Equal("n \u00e9", meta.Title);
        Assert.Equal("me, you", meta.Author);
        Assert.Equal("AP203", meta.GetProperty("application protocol"));
    }
}
