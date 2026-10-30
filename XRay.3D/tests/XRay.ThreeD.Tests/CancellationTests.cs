using System.Diagnostics;
using System.Text;

namespace XRay.ThreeD.Tests;

/// <summary>A cancelled token reaches every reader and writer: the result is OperationCanceledException, never ModelFormatException.</summary>
public class CancellationTests
{
    public static IEnumerable<object[]> Files() => RobustnessTests.Fixtures.Select(f => new object[] { f });

    private static LoadOptions Cancelled()
    {
        var cts = new CancellationTokenSource();
        cts.Cancel();
        return new LoadOptions { Cancellation = cts.Token };
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void An_already_cancelled_token_stops_load_metadata_and_conversion(string relative)
    {
        var data = File.ReadAllBytes(DatasetPaths.File(relative));
        var name = Path.GetFileName(relative);
        var options = Cancelled();

        Assert.ThrowsAny<OperationCanceledException>(() => ModelLoader.Load(data, name, options));
        Assert.ThrowsAny<OperationCanceledException>(() => ModelLoader.ReadMetadata(data, name, options));
        Assert.ThrowsAny<OperationCanceledException>(() => ModelLoader.ConvertToGlb(data, name, null, options));
    }

    [Fact]
    public void Writers_stop_on_a_cancelled_token()
    {
        var model = ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File("synthetic/scene.obj")), "scene.obj");
        var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => GlbWriter.ToArray(model, new GlbWriteOptions { Cancellation = cts.Token }));
        Assert.ThrowsAny<OperationCanceledException>(() => AssemblyPackage.Create(model, new AssemblyPackageOptions { Cancellation = cts.Token }));
        Assert.Equal("archive", ModelLoader.Detect(File.ReadAllBytes(DatasetPaths.File("synthetic/package-obj-mtl.zip")), "x.zip", new LoadOptions()).Id);
    }

    private static void StopsPromptly(byte[] data, string name)
    {
        using var cts = new CancellationTokenSource();
        var options = new LoadOptions { Cancellation = cts.Token };
        var cancelled = Stopwatch.StartNew();
        var watch = Stopwatch.StartNew();
        var task = Task.Run(() => ModelLoader.Load(data, name, options));
        Thread.Sleep(30);
        cts.Cancel();
        cancelled.Restart();
        try
        {
            task.GetAwaiter().GetResult();
            // The load finished before the cancel could land (a fast machine): nothing to prove, but it must not have been slow.
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30));
        }
        catch (OperationCanceledException)
        {
            Assert.True(cancelled.Elapsed < TimeSpan.FromSeconds(2), $"took {cancelled.Elapsed} to stop after the cancel");
        }
    }

    [Fact]
    public void A_large_obj_stops_promptly()
    {
        var sb = new StringBuilder(80_000_000);
        for (var i = 0; i < 1_500_000; i++)
        {
            sb.Append("v ").Append(i % 977).Append(' ').Append(i % 613).Append(' ').Append(i % 31).Append('\n');
        }

        for (var i = 1; i + 2 <= 1_500_000; i += 1)
        {
            sb.Append("f ").Append(i).Append(' ').Append(i + 1).Append(' ').Append(i + 2).Append('\n');
        }

        StopsPromptly(Encoding.ASCII.GetBytes(sb.ToString()), "big.obj");
    }

    [Fact]
    public void A_large_ascii_ply_stops_promptly()
    {
        const int n = 1_500_000;
        var sb = new StringBuilder(60_000_000);
        sb.Append("ply\nformat ascii 1.0\nelement vertex ").Append(n).Append("\nproperty float x\nproperty float y\nproperty float z\nelement face ").Append(n - 2)
            .Append("\nproperty list uchar int vertex_indices\nend_header\n");
        for (var i = 0; i < n; i++)
        {
            sb.Append(i % 977).Append(' ').Append(i % 613).Append(' ').Append(i % 31).Append('\n');
        }

        for (var i = 0; i + 2 < n; i++)
        {
            sb.Append("3 ").Append(i).Append(' ').Append(i + 1).Append(' ').Append(i + 2).Append('\n');
        }

        StopsPromptly(Encoding.ASCII.GetBytes(sb.ToString()), "big.ply");
    }

    [Fact]
    public void A_large_step_file_stops_promptly()
    {
        var sb = new StringBuilder(80_000_000);
        sb.Append("ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('x'),'2;1');\nFILE_NAME('x','2025-01-01T00:00:00',(''),(''),'','','');\nFILE_SCHEMA(('AUTOMOTIVE_DESIGN'));\nENDSEC;\nDATA;\n");
        for (var i = 1; i <= 1_200_000; i++)
        {
            sb.Append('#').Append(i).Append("=CARTESIAN_POINT('',(").Append(i % 977).Append(".,").Append(i % 613).Append(".,").Append(i % 31).Append(".));\n");
        }

        sb.Append("ENDSEC;\nEND-ISO-10303-21;\n");
        StopsPromptly(Encoding.ASCII.GetBytes(sb.ToString()), "big.step");
    }

    [Fact]
    public void A_large_tessellated_step_face_stops_promptly()
    {
        const int n = 400_000;
        var sb = new StringBuilder(60_000_000);
        sb.Append("ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('x'),'2;1');\nFILE_NAME('x','2025-01-01T00:00:00',(''),(''),'','','');\nFILE_SCHEMA(('AP242_MANAGED_MODEL_BASED_3D_ENGINEERING_MIM_LF'));\nENDSEC;\nDATA;\n");
        sb.Append("#1=COORDINATES_LIST('',").Append(n).Append(",(");
        for (var i = 0; i < n; i++)
        {
            sb.Append(i == 0 ? "" : ",").Append('(').Append(i % 977).Append(".,").Append(i % 613).Append(".,").Append(i % 31).Append(".)");
        }

        sb.Append("));\n#2=TRIANGULATED_FACE('',#1,").Append(n).Append(",(),$,(),(");
        for (var i = 1; i + 2 <= n; i++)
        {
            sb.Append(i == 1 ? "" : ",").Append('(').Append(i).Append(',').Append(i + 1).Append(',').Append(i + 2).Append(')');
        }

        sb.Append("));\n#3=TESSELLATED_SOLID('',(#2),$);\nENDSEC;\nEND-ISO-10303-21;\n");
        StopsPromptly(Encoding.ASCII.GetBytes(sb.ToString()), "big-tessellated.step");
    }

    [Fact]
    public void A_solidworks_assembly_stops_promptly()
    {
        StopsPromptly(File.ReadAllBytes(DatasetPaths.File("sldasm/nist-mtc-assembly.sldasm")), "nist-mtc-assembly.sldasm");
    }
}
