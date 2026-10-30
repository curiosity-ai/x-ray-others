namespace XRay.ThreeD.Tests;

/// <summary>Parasolid transmit files: header metadata only, and a clear refusal for geometry.</summary>
public class ParasolidTests
{
    [Fact]
    public void A_text_header_gives_modeller_schema_application_and_dates()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/header.x_t"));
        var meta = ModelLoader.ReadMetadata(data, "header.x_t");

        Assert.Equal("parasolid", meta.Format.Id);
        Assert.Equal(FormatSupport.Metadata, meta.Format.Support);
        Assert.Equal("modeller version 2900166", meta.FormatVersion);
        Assert.Equal("Hand-written header fixture", meta.Application);
        Assert.Equal("fixture author", meta.Author);
        Assert.Equal("bracket", meta.Title);
        Assert.Equal(new DateTimeOffset(2024, 3, 5, 10, 20, 30, TimeSpan.Zero), meta.Created);
        Assert.Equal("SCH_2900166_28101", meta.GetProperty("Schema"));
        Assert.Equal("2900166", meta.GetProperty("Kernel (from schema key)"));
        Assert.Equal("text (x_t)", meta.GetProperty("Encoding"));
        Assert.Equal("XRAY-TEST", meta.GetProperty("MC"));
        Assert.Equal("transmit", meta.GetProperty("GUISE"));
        Assert.False(meta.HasGeometry);
    }

    [Theory]
    [InlineData("synthetic/header.x_b", "modeller version 3000000", "SCH_3000000_30000", "binary (x_b)")]
    [InlineData("synthetic/bare.x_t", "modeller version 300000017", "SCH_3000000_300000", "text (x_t)")]
    public void Binary_and_bare_headers_give_version_and_schema(string relative, string version, string schema, string encoding)
    {
        var meta = ModelLoader.ReadMetadata(File.ReadAllBytes(DatasetPaths.File(relative)), Path.GetFileName(relative));
        Assert.Equal(version, meta.FormatVersion);
        Assert.Equal(schema, meta.GetProperty("Schema"));
        Assert.Equal(encoding, meta.GetProperty("Encoding"));
    }

    [Fact]
    public void Geometry_is_reported_as_unsupported_with_the_version_in_the_message()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/header.x_t"));
        var ex = Assert.Throws<UnsupportedModelFormatException>(() => ModelLoader.Load(data, "header.x_t"));
        Assert.Equal("parasolid", ex.Format.Id);
        Assert.Contains("2900166", ex.Message, StringComparison.Ordinal);
        Assert.Equal("parasolid", ModelLoader.Detect(File.ReadAllBytes(DatasetPaths.File("synthetic/bare.x_t")), "noextension").Id);
    }
}
