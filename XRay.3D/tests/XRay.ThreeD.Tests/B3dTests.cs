namespace XRay.ThreeD.Tests;

public class B3dTests
{
    // Counts from an independent walk of the chunk tree (a separate script, not this reader).
    [Theory]
    [InlineData("b3d/blitz3d-arcade-cabinet.b3d", 4, 244, 122, 6)]
    [InlineData("b3d/blitz3d-swaying-tentacle.b3d", 6, 121, 160, 2)]
    public void Blitz3d_counts_match_an_independent_walk(string file, int nodes, int vertices, int triangles, int brushes)
    {
        var model = ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File(file)), file);
        Assert.Equal("b3d", model.Metadata.Format.Id);
        var stats = model.Metadata.Statistics!;
        Assert.Equal(nodes, stats.Nodes);
        Assert.Equal(vertices, stats.UniqueVertices);
        Assert.Equal(triangles, stats.UniqueTriangles);
        Assert.Equal(brushes, model.Materials.Count);
        Assert.NotEmpty(GlbWriter.ToArray(model));
    }

    [Fact]
    public void Bazis_container_gives_its_title_only()
    {
        var file = "b3d/bazis-wall-cabinet.b3d";
        var meta = ModelLoader.ReadMetadata(File.ReadAllBytes(DatasetPaths.File(file)), file);
        Assert.Equal("Wall cabinet 600", meta.Title);
        Assert.False(meta.HasGeometry);
    }
}
