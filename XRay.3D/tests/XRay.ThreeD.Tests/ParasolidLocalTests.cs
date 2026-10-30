namespace XRay.ThreeD.Tests;

/// <summary>A real Parasolid text transmit file (a SolidWorks 98Plus export, Parasolid 10) from XRAY_3D_LOCAL_SAMPLES/parasolid; skipped when absent. Only the header is decoded.</summary>
public class ParasolidLocalTests
{
    [Fact]
    public void The_header_of_a_real_v10_transmit_file_is_read_and_geometry_is_refused_with_the_reason()
    {
        var root = Environment.GetEnvironmentVariable("XRAY_3D_LOCAL_SAMPLES") ?? "/tmp/claude-0/samples";
        var path = Path.Combine(root, "parasolid", "LONGBAR.x_t");
        if (!File.Exists(path))
        {
            return;
        }

        var bytes = File.ReadAllBytes(path);
        Assert.Equal("parasolid", ModelLoader.Detect(bytes, "LONGBAR.x_t").Id);
        var meta = ModelLoader.ReadMetadata(bytes, "LONGBAR.x_t");
        Assert.Contains("SCH_1000230_10004", string.Join("|", meta.Properties.Select(p => p.Value)) + meta.FormatVersion, StringComparison.Ordinal);
        Assert.Throws<UnsupportedModelFormatException>(() => ModelLoader.Load(bytes, "LONGBAR.x_t"));
    }
}
