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

    [Fact]
    public void A_parasolid_12_sheet_is_decoded_to_a_disc_with_its_colour()
    {
        var model = ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File("synthetic/disc.x_t")), "disc.x_t");
        Assert.Equal(LengthUnit.Meter, model.Metadata.Units);
        var node = Assert.Single(model.Roots);
        var mesh = Assert.IsType<MeshData>(node.Mesh);
        var material = Assert.Single(model.Materials);
        Assert.Equal(0.2f, material.BaseColor.X, 3);
        Assert.Equal(0.6f, material.BaseColor.Z, 3);

        // The ring edge has no vertices: the circle is walked from parameter zero. The polygon is inscribed in the circle of radius 2.
        double area = 0;
        for (var i = 0; i + 2 < mesh.Indices.Length; i += 3)
        {
            var a = new System.Numerics.Vector3(mesh.Positions[3 * mesh.Indices[i]], mesh.Positions[3 * mesh.Indices[i] + 1], mesh.Positions[3 * mesh.Indices[i] + 2]);
            var b = new System.Numerics.Vector3(mesh.Positions[3 * mesh.Indices[i + 1]], mesh.Positions[3 * mesh.Indices[i + 1] + 1], mesh.Positions[3 * mesh.Indices[i + 1] + 2]);
            var c = new System.Numerics.Vector3(mesh.Positions[3 * mesh.Indices[i + 2]], mesh.Positions[3 * mesh.Indices[i + 2] + 1], mesh.Positions[3 * mesh.Indices[i + 2] + 2]);
            area += System.Numerics.Vector3.Cross(b - a, c - a).Length() / 2;
        }

        Assert.InRange(area, Math.PI * 4 * 0.97, Math.PI * 4);
        Assert.Single(mesh.Faces);
    }

    [Fact]
    public void A_stream_that_names_a_node_type_outside_the_field_table_is_refused_cleanly()
    {
        var text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(DatasetPaths.File("synthetic/disc.x_t"))).Replace(" 83 3 15", " 77 3 15", StringComparison.Ordinal);
        Assert.ThrowsAny<ModelFormatException>(() => ModelLoader.Load(System.Text.Encoding.Latin1.GetBytes(text), "disc.x_t"));
    }
}
