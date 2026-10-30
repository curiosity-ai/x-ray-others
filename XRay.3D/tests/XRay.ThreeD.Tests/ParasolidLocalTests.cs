using XRay.ThreeD.Formats.Parasolid;

namespace XRay.ThreeD.Tests;

/// <summary>
/// A real Parasolid text transmit file (a SolidWorks 98Plus export, Parasolid 10, schema SCH_1000230_10004) from XRAY_3D_LOCAL_SAMPLES/parasolid; skipped
/// when absent. The file holds an assembly of four square bars (2 x 2 cross section; lengths 3, 10, 5 and 2) stacked along z by the transforms of its four
/// instances, so its bounds and volume are known in closed form (x and y 0 to 2, z 0 to 20; volume 80).
/// </summary>
public class ParasolidLocalTests
{
    private static byte[]? Sample(string name = "LONGBAR.x_t")
    {
        var root = Environment.GetEnvironmentVariable("XRAY_3D_LOCAL_SAMPLES") ?? "/tmp/claude-0/samples";
        var path = Path.Combine(root, "parasolid", name);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    [Fact]
    public void The_header_of_a_real_v10_transmit_file_is_read()
    {
        if (Sample() is not { } bytes)
        {
            return;
        }

        Assert.Equal("parasolid", ModelLoader.Detect(bytes, "LONGBAR.x_t").Id);
        var meta = ModelLoader.ReadMetadata(bytes, "LONGBAR.x_t");
        Assert.Contains("SCH_1000230_10004", string.Join("|", meta.Properties.Select(p => p.Value)) + meta.FormatVersion, StringComparison.Ordinal);
    }

    [Fact]
    public void A_real_v10_assembly_is_decoded_with_its_instance_transforms_and_matches_the_closed_form_bounds_and_volume()
    {
        if (Sample() is not { } bytes)
        {
            return;
        }

        var model = ModelLoader.Load(bytes, "LONGBAR.x_t");
        Assert.Equal(LengthUnit.Meter, model.Metadata.Units);
        var assembly = Assert.Single(model.Roots);
        Assert.Equal(4, assembly.Children.Count);
        Assert.All(assembly.Children, c => Assert.NotNull(c.Mesh));
        Assert.Equal(24, model.Meshes.Sum(m => m.Faces.Length));
        var stats = model.Metadata.Statistics!;
        Assert.Equal(80.0, stats.Volume, 3);
        var box = model.Metadata.Bounds!.Value;
        Assert.Equal(0, box.Min.X, 4);
        Assert.Equal(0, box.Min.Y, 4);
        Assert.Equal(0, box.Min.Z, 4);
        Assert.Equal(2, box.Max.X, 4);
        Assert.Equal(2, box.Max.Y, 4);
        Assert.Equal(20, box.Max.Z, 4);
        _ = GlbWriter.ToArray(model);
    }

    [Fact]
    public void A_file_that_embeds_its_schema_is_read_with_the_edits_of_its_own_schema_section()
    {
        // An Ansys Electronics Desktop export of schema 35102 on the 13006 base: a gingerbread man, one 4.8 mm thick body with planar faces whose boundaries
        // are lines and linear surface curves (every type of the base is edited in the file: the BODY carries 36 fields instead of 23).
        if (Sample("gingerbread.x_t") is not { } bytes)
        {
            return;
        }

        var nodes = ParasolidReader.ReadNodes(bytes, null, 5_000_000, TestContext.Current.CancellationToken, out var key);
        Assert.Equal("SCH_3501210_35102_13006", key);
        Assert.Equal(18046, nodes.Count);
        Assert.Equal(36, nodes.Values.First(n => n.Def.Type == 12).Def.Fields.Length);
        Assert.Equal(682, nodes.Values.Count(n => n.Def.Type == 14));

        var model = ModelLoader.Load(bytes, "gingerbread.x_t");
        Assert.Equal(682, int.Parse(model.Metadata.GetProperty("Faces")!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(1, int.Parse(model.Metadata.GetProperty("Bodies")!, System.Globalization.CultureInfo.InvariantCulture));
        var box = model.Metadata.Bounds!.Value;
        Assert.Equal(0.12056, box.Min.X, 4);
        Assert.Equal(0.16251, box.Min.Y, 4);
        Assert.Equal(0.04445, box.Min.Z, 5);
        Assert.Equal(0.24074, box.Max.X, 4);
        Assert.Equal(0.31975, box.Max.Y, 4);
        Assert.Equal(0.0492506, box.Max.Z, 5);
        Assert.InRange(model.Metadata.Statistics!.Volume, 3.5e-5, 4.2e-5);
        _ = GlbWriter.ToArray(model);
    }

    [Fact]
    public void A_plain_schema_32001_file_of_three_sheet_bodies_is_read_through_its_part_block()
    {
        // An Ansys logo, three planar sheet bodies; the root node is a block listing the three parts. The sheets lie in z = 0.
        if (Sample("Ansys_logo_2D.x_t") is not { } bytes)
        {
            return;
        }

        var model = ModelLoader.Load(bytes, "Ansys_logo_2D.x_t");
        Assert.Equal(3, int.Parse(model.Metadata.GetProperty("Bodies")!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(3, int.Parse(model.Metadata.GetProperty("Faces")!, System.Globalization.CultureInfo.InvariantCulture));
        var box = model.Metadata.Bounds!.Value;
        Assert.Equal(-0.0008636, box.Min.X, 6);
        Assert.Equal(-0.0002794, box.Min.Y, 6);
        Assert.Equal(0.0008128, box.Max.X, 6);
        Assert.Equal(0.0002794, box.Max.Y, 6);
        Assert.Equal(0, box.Min.Z, 9);
        Assert.Equal(0, box.Max.Z, 9);
    }

    [Theory]
    [InlineData("LONGBAR.x_t")]
    [InlineData("Ansys_logo_2D.x_t")]
    [InlineData("gingerbread.x_t")]
    public void The_binary_encoding_of_a_real_files_nodes_gives_the_same_model_as_the_text(string name)
    {
        if (Sample(name) is not { } text)
        {
            return;
        }

        var binary = ParasolidBinaryWriter.FromText(text);
        Assert.Equal("binary (x_b)", ModelLoader.ReadMetadata(binary, "x.x_b").GetProperty("Encoding"));
        var a = ModelLoader.Load(text, name);
        var b = ModelLoader.Load(binary, "x.x_b");
        Assert.Equal(a.Metadata.Statistics!.Triangles, b.Metadata.Statistics!.Triangles);
        Assert.Equal(a.Metadata.Statistics.Volume, b.Metadata.Statistics.Volume, 12);
        Assert.Equal(a.Metadata.Bounds!.Value.Min, b.Metadata.Bounds!.Value.Min);
        Assert.Equal(a.Metadata.Bounds!.Value.Max, b.Metadata.Bounds!.Value.Max);
        Assert.Equal(a.Metadata.GetProperty("Faces"), b.Metadata.GetProperty("Faces"));
    }
}
