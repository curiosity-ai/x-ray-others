using System.Diagnostics;

namespace XRay.ThreeD.Tests;

/// <summary>
/// A timing harness for large inputs, without a benchmarking library. Skipped unless <c>XRAY_3D_BENCH</c> is set, so the normal run stays
/// quick and free of timing assertions: <c>XRAY_3D_BENCH=1 dotnet test --project XRay.3D/tests/XRay.ThreeD.Tests -c Release -- --filter-class "*BenchmarkTests"</c>.
/// Each case loads (and converts to GLB) the file several times and prints best and median wall time, bytes allocated per iteration and the
/// GC counts; <c>XRAY_3D_BENCH_ROUNDS</c> sets the iteration count (default 5). Results are also appended to <c>XRAY_3D_BENCH_OUT</c> when set.
/// </summary>
public class BenchmarkTests
{
    public static IEnumerable<object[]> Cases() =>
    [
        ["sldasm/nist-mtc-assembly.sldasm"],
        ["step/feather-esp8266.step"],
        ["step/adafruit-motor-hat.step"],
        ["glb/watering-can.glb"],
        ["ply/gsplat-measuring-tape.ply"],
        ["dxf/godown-warehouse-30-x-20-m-with-loading-dock-plan-drawing.dxf"],
        ["synthetic/laz-f3.laz"],
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public void Load_and_convert_timing(string relative)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("XRAY_3D_BENCH") is { Length: > 0 }, "Set XRAY_3D_BENCH=1 to run the timing harness.");
        var data = File.ReadAllBytes(DatasetPaths.File(relative));
        var name = Path.GetFileName(relative);
        var rounds = int.TryParse(Environment.GetEnvironmentVariable("XRAY_3D_BENCH_ROUNDS"), out var n) && n > 0 ? n : 5;
        var times = new List<double>();
        long allocated = 0;
        var gen0 = GC.CollectionCount(0);
        var gen2 = GC.CollectionCount(2);
        ModelLoader.ConvertToGlb(data, name); // warm-up: JIT and caches
        for (var i = 0; i < rounds; i++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            var glb = ModelLoader.ConvertToGlb(data, name);
            watch.Stop();
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
            times.Add(watch.Elapsed.TotalMilliseconds);
            Assert.True(glb.Length > 0);
        }

        times.Sort();
        var line = $"{relative,-70} {data.Length / 1024,8} KiB  best {times[0],9:F1} ms  median {times[times.Count / 2],9:F1} ms  alloc {allocated / rounds / 1024,10:N0} KiB/iter  gc0 {GC.CollectionCount(0) - gen0}  gc2 {GC.CollectionCount(2) - gen2}";
        Console.WriteLine(line);
        if (Environment.GetEnvironmentVariable("XRAY_3D_BENCH_OUT") is { Length: > 0 } output)
        {
            File.AppendAllText(output, line + Environment.NewLine);
        }
    }
}
