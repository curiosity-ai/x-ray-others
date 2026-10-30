namespace XRay.ThreeD.Tests;

public class UassetTests
{
    // Reference values from an independent parse of the same bytes (a separate script walking the tables and the raw mesh arrays).
    [Fact]
    public void Faucet_tables_and_raw_mesh_match_an_independent_parse()
    {
        var file = "uasset/Faucet.uasset";
        var model = ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File(file)), file);
        var meta = model.Metadata;
        Assert.Equal("unreal", meta.Format.Id);
        Assert.Equal("Unreal Engine 4.8.3-2634408", meta.Application);
        Assert.Equal("74", meta.GetProperty("Names"));
        Assert.Equal("10", meta.GetProperty("Imports"));
        Assert.Equal("5", meta.GetProperty("Exports"));
        Assert.Equal("1", meta.Properties.First(p => p.Name == "StaticMesh").Value);
        var stats = meta.Statistics!;
        Assert.Equal(1758, stats.UniqueTriangles);
        Assert.Equal(5274, stats.UniqueVertices);
        Assert.Equal(516.682, stats.Volume, 2);
        var b = meta.Bounds!.Value;
        Assert.Equal(-13.6106, b.Min.X, 3);
        Assert.Equal(13.5679, b.Max.X, 3);
        Assert.Equal(-3.3812, b.Min.Y, 3);
        Assert.Equal(16.9007, b.Max.Y, 3);
        Assert.Equal(39.7501, b.Max.Z, 3);
        Assert.Equal(LengthUnit.Centimeter, meta.Units);
        Assert.Equal("StaticMesh", model.Roots.First(n => n.Name == "Faucet").Properties["class"]);
        Assert.NotEmpty(GlbWriter.ToArray(model));
    }

    [Fact]
    public void DoorCloser_has_two_materials()
    {
        var file = "uasset/DoorCloser.uasset";
        var model = ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File(file)), file);
        Assert.Equal(948, model.Metadata.Statistics!.UniqueTriangles);
        Assert.Equal(2, model.Materials.Count);
        Assert.True(model.Metadata.Statistics!.Volume > 0);
    }

    [Fact]
    public void Metadata_only_read_works()
    {
        var meta = ModelLoader.ReadMetadata(File.ReadAllBytes(DatasetPaths.File("uasset/Faucet.uasset")), "Faucet.uasset");
        Assert.Contains("/Script/Engine", meta.ComponentFiles);
    }
}
