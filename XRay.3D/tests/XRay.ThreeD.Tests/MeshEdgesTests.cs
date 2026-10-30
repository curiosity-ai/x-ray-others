using System.Diagnostics;
using XRay.ThreeD;

namespace XRay.ThreeD.Tests;

/// <summary>
/// Feature-edge extraction on large strip meshes. The edge table was keyed by a packed pair of vertex ids whose default hash
/// (<c>low ^ high</c>) is nearly constant along a strip, so a million-triangle part (a 50 MB SolidWorks file) took minutes.
/// </summary>
public class MeshEdgesTests
{
    private static MeshData Strip(int triangles)
    {
        var vertices = triangles + 2;
        var positions = new float[vertices * 3];
        for (var v = 0; v < vertices; v++)
        {
            positions[3 * v] = v * 0.001f;
            positions[3 * v + 1] = (v & 1) * 0.001f;
            positions[3 * v + 2] = (v % 7) * 1e-5f;
        }

        var indices = new uint[triangles * 3];
        for (var t = 0; t < triangles; t++)
        {
            var a = (uint)t;
            indices[3 * t] = (t & 1) == 0 ? a : a + 1;
            indices[3 * t + 1] = (t & 1) == 0 ? a + 1 : a;
            indices[3 * t + 2] = a + 2;
        }

        return new MeshData { Positions = positions, Indices = indices, Faces = [new MeshFace(0, indices.Length / 2 / 3 * 3), new MeshFace(indices.Length / 2 / 3 * 3, indices.Length - indices.Length / 2 / 3 * 3)] };
    }

    [Fact]
    public void MillionTriangleStripExtractsQuickly()
    {
        var mesh = Strip(1_000_000);
        var watch = Stopwatch.StartNew();
        var edges = MeshEdges.Extract(mesh);
        watch.Stop();
        Assert.NotEmpty(edges);
        Assert.Equal(0, edges.Length % 2);
        // A linear pass takes about a second; the quadratic table needed minutes.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"took {watch.Elapsed}");
    }

    [Fact]
    public void ExtractionStopsWhenCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => MeshEdges.Extract(Strip(100_000), cancellation: cts.Token));
    }

    [Fact]
    public void LocalLargeSolidWorksPartsConvert()
    {
        var root = Environment.GetEnvironmentVariable("XRAY_3D_LOCAL_SAMPLES");
        var folder = root is null ? null : Path.Combine(root, "solidworks-large");
        if (folder is null || !Directory.Exists(folder))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(folder, "*.sldprt"))
        {
            var watch = Stopwatch.StartNew();
            var glb = ModelLoader.ConvertToGlb(File.ReadAllBytes(file), file);
            Assert.True(glb.Length > 1000);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), $"{file} took {watch.Elapsed}");
        }
    }
}
