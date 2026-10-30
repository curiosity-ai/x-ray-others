using System.Text.Json;

namespace XRay.ThreeD.Tests;

/// <summary>
/// Archicad library parts (.gsm): the container, the metadata and the geometry the GDL interpreter draws. The lamp (a BASE / VERT / EDGE / PGON polyhedron)
/// is checked against a separate Python parse of its script and the table against closed forms (Corpus/gsm-golden.json, tools/oracle/make_gsm_golden.py);
/// the synthetic part against closed forms worked out in the test. Archicad project files (.pln, .pla) are a proprietary container that is not opened.
/// </summary>
public class ArchicadTests
{
    private static JsonElement Golden(string name) => JsonDocument.Parse(File.ReadAllText(DatasetPaths.CorpusFile("gsm-golden.json"))).RootElement.GetProperty(name);

    [Fact]
    public void LampMatchesTheSeparateParse()
    {
        var golden = Golden("table-lamp.gsm");
        var model = ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File("pln/table-lamp.gsm")), "table-lamp.gsm");
        var stats = model.Metadata.Statistics!;
        Assert.Equal(golden.GetProperty("triangles").GetInt32(), stats.Triangles);
        Assert.Equal(golden.GetProperty("area").GetDouble(), stats.SurfaceArea, 6);
        Assert.Equal(golden.GetProperty("volume").GetDouble(), stats.Volume, 6);
        Assert.Equal(golden.GetProperty("materials").GetInt32(), model.Materials.Count);
        var box = model.Metadata.Bounds!.Value;
        var min = golden.GetProperty("min").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
        var max = golden.GetProperty("max").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
        Assert.Equal(min[0], box.Min.X, 5);
        Assert.Equal(max[2], box.Max.Z, 5);
        Assert.Equal(max[0], box.Max.X, 5);
        Assert.Contains(model.Materials, m => m.Name == "Lamp shade" && Math.Abs(m.BaseColor.X - 0.95f) < 1e-6f);
        Assert.Equal(UpAxis.Z, model.Metadata.UpAxis);
        Assert.Equal(LengthUnit.Meter, model.Metadata.Units);
        Assert.NotNull(model.Preview);
        Assert.Equal("image/png", model.Preview!.MediaType);
        Assert.Equal("Innerscene", model.Metadata.Author);
        Assert.Equal("CC0", model.Metadata.GetProperty("License"));
        Assert.Equal("0.3", model.Metadata.GetProperty("A")![..3]);
        Assert.Contains("BODY", model.Metadata.GetProperty("3D script")!);
    }

    [Fact]
    public void TableMatchesClosedForms()
    {
        var golden = Golden("parametric-table.gsm");
        var model = ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File("pln/parametric-table.gsm")), "parametric-table.gsm");
        var expected = golden.GetProperty("volume").GetDouble();
        Assert.Equal(expected, model.Metadata.Statistics!.Volume, expected * 0.003);
        var box = model.Metadata.Bounds!.Value;
        Assert.Equal(0f, box.Min.X, 5);
        Assert.Equal(1.4f, box.Max.X, 5);
        Assert.Equal(0.8f, box.Max.Y, 5);
        Assert.Equal(0.75f, box.Max.Z, 5);
        Assert.Equal(2, model.Materials.Count);
        Assert.Equal(["Table steel", "Table oak"], model.Materials.Select(m => m.Name).Order(StringComparer.Ordinal).Reverse());
        Assert.Equal("1.4", model.Metadata.GetProperty("A"));
        Assert.Empty(model.Warnings);
    }

    [Fact]
    public void SyntheticPartExercisesTheInterpreter()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/gdl-solids.gsm"));
        Assert.Equal("archicad", ModelLoader.Detect(data, "part.bin").Id);
        var model = ModelLoader.Load(data, "gdl-solids.gsm");
        var stats = model.Metadata.Statistics!;

        // Four steps (0.25 x 0.5 x 0.1 i), two UV spheres of radius 0.25 (24 x 12 facets: 0.0636024), a 0.1 cube, a 24-sided frustum and a prism with one arc.
        const double expected = 0.125 + 2 * 0.06360238063781531 + 0.001 + 0.13458917 + 0.04555290;
        Assert.Equal(expected, stats.Volume, 4);
        var box = model.Metadata.Bounds!.Value;
        Assert.Equal(-0.3f, box.Min.X, 4);
        Assert.Equal(2.6f, box.Max.X, 4);
        Assert.Equal(-1f, box.Min.Y, 4);
        Assert.Equal(4.3f, box.Max.Y, 4);
        Assert.Equal(-0.25f, box.Min.Z, 4);
        Assert.Equal(1f, box.Max.Z, 4);
        var oak = Assert.Single(model.Materials);
        Assert.Equal("Oak", oak.Name);
        Assert.Equal(0.7f, oak.BaseColor.X, 5);
        Assert.Equal(0.75f, oak.BaseColor.W, 5);
        Assert.Equal("6c1d8e52-3b6a-4f0e-9d27-5a8e0b7c4d11", model.Metadata.GetProperty("GUID"));
        Assert.Equal("Examples", model.Metadata.Author);
        Assert.Equal("1.0", model.Metadata.GetProperty("License version"));
        Assert.Equal("4", model.Metadata.GetProperty("steps"));
        Assert.Equal("Test object", model.Metadata.GetProperty("Comment"));
        Assert.Equal(8, (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(model.Preview!.Data.AsSpan(16)));
        Assert.True(ModelLoader.ConvertToGlb(data, "gdl-solids.gsm").Length > 500);
    }

    [Fact]
    public void ProjectFilesAreNotOpened()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("pln/planogram-drinks-aisle.pln"));
        var e = Assert.Throws<UnsupportedModelFormatException>(() => ModelLoader.Load(data, "planogram-drinks-aisle.pln"));
        Assert.Contains("project files", e.Message);
    }
}
