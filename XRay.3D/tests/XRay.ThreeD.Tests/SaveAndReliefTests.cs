namespace XRay.ThreeD.Tests;

public class SaveAndReliefTests
{
    // Counts and bounds from an independent parse of the icon (a separate script: 888 vertices, int16 extents /4096).
    [Fact]
    public void Psu_icon_matches_an_independent_parse()
    {
        var file = "psu/astronaut.psu";
        var model = ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File(file)), file);
        Assert.Equal("psu", model.Metadata.Format.Id);
        Assert.Equal("Innerscene Sample Astronaut", model.Metadata.Title);
        var stats = model.Metadata.Statistics!;
        Assert.Equal(888, stats.UniqueVertices);
        Assert.Equal(296, stats.UniqueTriangles);
        var b = model.Metadata.Bounds!.Value;
        Assert.Equal(-4152 / 4096.0, b.Min.X, 4);
        Assert.Equal(4152 / 4096.0, b.Max.X, 4);
        Assert.Equal(0, b.Min.Y, 4);
        Assert.Equal(13107 / 4096.0, b.Max.Y, 4);
        Assert.Equal(3212 / 4096.0, b.Max.Z, 4);
        Assert.Single(model.Textures);
        Assert.True(stats.Volume > 0);
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.FromHours(9)), model.Metadata.Created);
        Assert.NotEmpty(GlbWriter.ToArray(model));
    }

    [Theory]
    [InlineData("rlf/scallop-shell.rlf", 720, 680, "180.00", "170.00", "20.815")]
    public void Rlf_banner_is_read_and_geometry_is_reported_as_missing(string file, int w, int h, string mmW, string mmH, string max)
    {
        var data = File.ReadAllBytes(DatasetPaths.File(file));
        var meta = ModelLoader.ReadMetadata(data, file);
        Assert.Equal("rlf", meta.Format.Id);
        Assert.Equal("SCALLOP_SHELL", meta.Title);
        Assert.Equal(w.ToString(), meta.GetProperty("Width (pixels)"));
        Assert.Equal(h.ToString(), meta.GetProperty("Height (pixels)"));
        Assert.Equal(mmW, meta.GetProperty("Width (mm)"));
        Assert.Equal(mmH, meta.GetProperty("Depth (mm)"));
        Assert.Equal(max, meta.GetProperty("Maximum height (mm)"));
        Assert.False(meta.HasGeometry);
        Assert.Throws<UnsupportedModelFormatException>(() => ModelLoader.Load(data, file));
    }

    [Theory]
    [InlineData("rlf/horse-statue-relief.rlf")]
    [InlineData("rlf/lion-head-relief.rlf")]
    [InlineData("rlf/compass-rose.rlf")]
    public void Every_rlf_sample_has_dimensions(string file)
    {
        var meta = ModelLoader.ReadMetadata(File.ReadAllBytes(DatasetPaths.File(file)), file);
        Assert.NotNull(meta.GetProperty("Width (pixels)"));
        Assert.NotNull(meta.GetProperty("Pixel size (mm)"));
    }
}
