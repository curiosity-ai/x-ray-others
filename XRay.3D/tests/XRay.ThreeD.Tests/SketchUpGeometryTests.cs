using System.Numerics;
using System.Text.Json;

namespace XRay.ThreeD.Tests;

/// <summary>
/// SketchUp 2021 and later geometry (model.dat). The synthetic file is committed; the 24 real public files (SketchUp 2021 to 2026, not redistributable, read
/// from XRAY_3D_LOCAL_SAMPLES) are compared with counts and bounds from an independent record walk written in Python (Corpus/skp-geometry-golden.json:
/// the triangle count of a face with holes is the vertex count plus twice the holes minus two).
/// </summary>
public class SketchUpGeometryTests
{
    public static IEnumerable<object[]> Files() => JsonDocument.Parse(File.ReadAllText(DatasetPaths.CorpusFile("skp-geometry-golden.json"))).RootElement
        .EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(Files))]
    public void RealFileMatchesTheIndependentWalk(string file)
    {
        var root = Environment.GetEnvironmentVariable("XRAY_3D_LOCAL_SAMPLES") ?? "/tmp/claude-0/samples";
        var path = Path.Combine(root, "sketchup", file);
        Assert.SkipWhen(!File.Exists(path), "local sample not present: " + file);
        var golden = JsonDocument.Parse(File.ReadAllText(DatasetPaths.CorpusFile("skp-geometry-golden.json"))).RootElement.GetProperty(file);
        var model = ModelLoader.Load(File.ReadAllBytes(path), file);
        var stats = model.ComputeStatistics();
        Assert.Equal(golden.GetProperty("tris").GetInt64(), stats.Triangles);
        Assert.Equal(golden.GetProperty("verts").GetInt64(), stats.Vertices);
        var b = golden.GetProperty("bounds").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
        var bounds = model.ComputeBounds()!.Value;
        Assert.True(Vector3.Distance(bounds.Min, new Vector3(b[0], b[1], b[2])) < 1e-2f, $"min {bounds.Min}");
        Assert.True(Vector3.Distance(bounds.Max, new Vector3(b[3], b[4], b[5])) < 1e-2f, $"max {bounds.Max}");
        Assert.Equal(LengthUnit.Inch, model.Metadata.Units);
        Assert.NotEmpty(ModelLoader.ConvertToGlb(File.ReadAllBytes(path), file));
    }

    [Fact]
    public void SyntheticScene()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/geometry-2021.skp"));
        var model = ModelLoader.Load(data, "geometry-2021.skp");
        Assert.True(model.Metadata.HasGeometry);
        var root = Assert.Single(model.Roots);

        // The plate: a square with a hole is eight triangles; the cube is shared by both placements.
        Assert.Equal(2, model.Meshes.Count);
        Assert.Equal(8, root.Mesh!.TriangleCount);
        Assert.Equal(["Cube A", "Cube"], root.Children.Select(c => c.Name));
        Assert.Same(root.Children[0].Mesh, root.Children[1].Mesh);
        Assert.Equal(12, root.Children[0].Mesh!.TriangleCount);
        Assert.Equal(32, model.ComputeStatistics().Triangles);

        Assert.Equal(["Walls"], model.Layers.Select(l => l.Name));
        Assert.Equal(-1, root.Children[0].Layer);
        Assert.Equal(0, root.Children[1].Layer);
        Assert.Equal("Oak", model.Materials.Single().Name);
        Assert.Equal(new Vector4(160 / 255f, 110 / 255f, 60 / 255f, 1), model.Materials[0].BaseColor);

        var bounds = model.ComputeBounds()!.Value;
        Assert.Equal(new Vector3(-4, 0, 0), bounds.Min);
        Assert.Equal(new Vector3(12, 14, 4), bounds.Max);

        // Every triangle of the plate faces +Z, and the hole is empty.
        var plate = root.Mesh;
        double area = 0;
        for (var i = 0; i < plate.Indices.Length; i += 3)
        {
            Vector3 P(int k) => new(plate.Positions[plate.Indices[i + k] * 3], plate.Positions[plate.Indices[i + k] * 3 + 1], plate.Positions[plate.Indices[i + k] * 3 + 2]);
            var n = Vector3.Cross(P(1) - P(0), P(2) - P(0));
            Assert.True(n.Z > 0);
            area += n.Z / 2;
        }

        Assert.Equal(12.0, area, 3);
        Assert.Equal(LengthUnit.Inch, model.Metadata.Units);
    }

    [Fact]
    public void SyntheticSceneFailsCleanlyWhenCut()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/geometry-2021.skp"));
        for (var length = 0; length < data.Length; length += 37)
        {
            try
            {
                _ = ModelLoader.Load(data[..length], "geometry-2021.skp");
            }
            catch (ModelFormatException)
            {
            }
        }
    }
}
