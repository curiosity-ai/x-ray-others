using XRay.ThreeD.Formats.SolidWorks;

namespace XRay.ThreeD.Tests;

/// <summary>The Parasolid B-rep that a SolidWorks part stores, tessellated by the Parasolid reader, against the display lists of the same file.</summary>
public class SolidWorksBrepTests
{
    [Theory]
    [InlineData("sldprt/nist-ftc-10-part.sldprt")]
    [InlineData("sldprt/nist-ctc-02-part.sldprt")]
    [InlineData("sldprt/servo-mount.sldprt")]
    [InlineData("sldprt/muon-detector-case.sldprt")]
    public void The_stored_partition_has_the_bounds_of_the_display_geometry(string relative)
    {
        var data = File.ReadAllBytes(DatasetPaths.File(relative));
        var display = ModelLoader.Load(data, "part.sldprt");
        var container = SwContainer.TryOpen(data, TestContext.Current.CancellationToken)!;
        var brep = SolidWorksReader.ReadBrep(container, new LoadOptions { Cancellation = TestContext.Current.CancellationToken });
        Assert.NotNull(brep);
        Assert.NotEmpty(brep.Meshes);

        // Display lists are in metres, like the Parasolid partition; a B-rep tessellation is coarser, so the boxes agree to a small fraction of the part.
        var a = display.Metadata.Bounds!.Value;
        var b = brep.ComputeBounds()!.Value;
        var size = (a.Max - a.Min).Length();
        Assert.True((a.Min - b.Min).Length() < 0.02 * size, $"min {a.Min} vs {b.Min}");
        Assert.True((a.Max - b.Max).Length() < 0.02 * size, $"max {a.Max} vs {b.Max}");
        _ = GlbWriter.ToArray(brep);
    }
}
