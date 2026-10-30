namespace XRay.ThreeD.Tests;

/// <summary>
/// A real Parasolid text transmit file (a SolidWorks 98Plus export, Parasolid 10, schema SCH_1000230_10004) from XRAY_3D_LOCAL_SAMPLES/parasolid; skipped
/// when absent. The file holds an assembly of four square bars (2 x 2 cross section; lengths 3, 10, 5 and 2) stacked along z by the transforms of its four
/// instances, so its bounds and volume are known in closed form (x and y 0 to 2, z 0 to 20; volume 80).
/// </summary>
public class ParasolidLocalTests
{
    private static byte[]? Sample()
    {
        var root = Environment.GetEnvironmentVariable("XRAY_3D_LOCAL_SAMPLES") ?? "/tmp/claude-0/samples";
        var path = Path.Combine(root, "parasolid", "LONGBAR.x_t");
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
}
