using XRay.ThreeD.Formats.Jt;
using XRay.ThreeD.Formats.Parasolid;

namespace XRay.ThreeD.Tests;

/// <summary>Parasolid transmit streams inside JT files (the XT B-rep elements) and NX part files, from XRAY_3D_LOCAL_SAMPLES/jt and /nx; skipped when absent.</summary>
public class ParasolidRoutingLocalTests
{
    private static byte[]? Jt(string name)
    {
        var root = Environment.GetEnvironmentVariable("XRAY_3D_LOCAL_SAMPLES") ?? "/tmp/claude-0/samples";
        var path = Path.Combine(root, "jt", name);
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    [Theory]
    [InlineData("example_block_jt8.1.jt", false)]   // tessellated shapes only
    [InlineData("example_block_jt9.5.jt", true)]    // schema 25001
    [InlineData("example_block_jt10.3.jt", true)]   // schema 31100
    public void The_xt_element_of_a_jt_block_is_a_box_of_closed_form_volume(string name, bool hasXt)
    {
        if (Jt(name) is not { } bytes)
        {
            return;
        }

        var streams = JtReader.FindXtStreams(bytes, TestContext.Current.CancellationToken);
        Assert.Equal(hasXt, streams.Count > 0);
        if (!hasXt)
        {
            return;
        }

        var model = ParasolidReader.ReadEmbedded(streams, new LoadOptions { Cancellation = TestContext.Current.CancellationToken })!;
        Assert.Equal(6, int.Parse(model.Metadata.GetProperty("Faces")!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(0.1 * 0.08 * 0.06, model.Metadata.Statistics!.Volume, 9);
        var box = model.Metadata.Bounds!.Value;
        Assert.Equal(0.1, box.Max.X, 6);
        Assert.Equal(0.08, box.Max.Y, 6);
        Assert.Equal(0.06, box.Max.Z, 6);
    }

    [Fact]
    public void The_same_part_stored_by_JT_8_and_9_has_the_same_geometry_under_two_schemas()
    {
        if (Jt("opening_protection_plate1_jt8.0.jt") is not { } v8 || Jt("opening_protection_plate1_jt9.5.jt") is not { } v9)
        {
            return;
        }

        var token = TestContext.Current.CancellationToken;
        var a = ParasolidReader.ReadEmbedded(JtReader.FindXtStreams(v8, token), new LoadOptions { Cancellation = token })!;
        var b = ParasolidReader.ReadEmbedded(JtReader.FindXtStreams(v9, token), new LoadOptions { Cancellation = token })!;
        Assert.Equal("SCH_1700284_16100_13006", a.Metadata.GetProperty("Schema"));
        Assert.Equal("SCH_2500176_25001_13006", b.Metadata.GetProperty("Schema"));
        Assert.Equal(a.Metadata.Statistics!.Triangles, b.Metadata.Statistics!.Triangles);
        Assert.Equal(a.Metadata.Statistics.SignedVolume, b.Metadata.Statistics.SignedVolume, 12);
        Assert.Equal(a.Metadata.Bounds, b.Metadata.Bounds);
    }

    [Fact]
    public void An_nx_part_gives_its_largest_transmit_stream_and_agrees_with_the_same_part_in_jt()
    {
        var root = Environment.GetEnvironmentVariable("XRAY_3D_LOCAL_SAMPLES") ?? "/tmp/claude-0/samples";
        var path = Path.Combine(root, "nx", "nist-mtc-box-nx.prt");
        if (!File.Exists(path))
        {
            return;
        }

        var bytes = File.ReadAllBytes(path);
        Assert.Equal("nx", ModelLoader.Detect(bytes, "x.prt").Id);
        var model = ModelLoader.Load(bytes, "nist-mtc-box-nx.prt");
        Assert.Equal("nx", model.Metadata.Format.Id);
        Assert.Equal(348, int.Parse(model.Metadata.GetProperty("Faces")!, System.Globalization.CultureInfo.InvariantCulture));
        var box = model.Metadata.Bounds!.Value;

        // The bounds of the NIST MTC box part as the NX file stores it (the same part as the first body of the JT assembly of the same schema).
        Assert.Equal(-0.004, box.Min.X, 4);
        Assert.Equal(-0.004, box.Min.Y, 4);
        Assert.Equal(-0.003, box.Min.Z, 4);
        Assert.Equal(0.1484, box.Max.X, 4);
        Assert.Equal(0.0976, box.Max.Y, 4);
        Assert.Equal(0.07, box.Max.Z, 4);
        Assert.InRange(model.Metadata.Statistics!.SignedVolume, 3.0e-4, 3.6e-4);
    }
}
