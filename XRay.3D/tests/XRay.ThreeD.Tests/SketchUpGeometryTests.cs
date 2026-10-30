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

    public static IEnumerable<object[]> LegacyFiles() => JsonDocument.Parse(File.ReadAllText(DatasetPaths.CorpusFile("skp-legacy-geometry-golden.json"))).RootElement
        .EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).Select(n => new object[] { n });

    /// <summary>The 30 real SketchUp 2013 to 2020 files (MFC object streams): counts and bounds against an independent walk (Corpus/skp-legacy-geometry-golden.json).</summary>
    [Theory]
    [MemberData(nameof(LegacyFiles))]
    public void LegacyRealFileMatchesTheIndependentWalk(string file)
    {
        var root = Environment.GetEnvironmentVariable("XRAY_3D_LOCAL_SAMPLES") ?? "/tmp/claude-0/samples";
        var path = Path.Combine(root, "sketchup", file);
        Assert.SkipWhen(!File.Exists(path), "local sample not present: " + file);
        var golden = JsonDocument.Parse(File.ReadAllText(DatasetPaths.CorpusFile("skp-legacy-geometry-golden.json"))).RootElement.GetProperty(file);
        var data = File.ReadAllBytes(path);
        if (golden.GetProperty("tris").GetInt64() == 0)
        {
            Assert.False(ModelLoader.Load(data, file).Metadata.HasGeometry);
            return;
        }

        var model = ModelLoader.Load(data, file);
        var stats = model.ComputeStatistics();
        // The oracle counts vertices + 2 * holes - 2 per face; a face whose loops touch or revisit a vertex (two of the real files have a few) is
        // triangulated into a slightly different number, so the triangle count may differ by a handful there. Vertices and bounds are exact.
        Assert.InRange(stats.Triangles, golden.GetProperty("tris").GetInt64() - 4, golden.GetProperty("tris").GetInt64() + 4);
        Assert.Equal(golden.GetProperty("verts").GetInt64(), stats.Vertices);
        var b = golden.GetProperty("bounds").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
        var bounds = model.ComputeBounds()!.Value;
        Assert.True(Vector3.Distance(bounds.Min, new Vector3(b[0], b[1], b[2])) < 1e-2f, $"min {bounds.Min}");
        Assert.True(Vector3.Distance(bounds.Max, new Vector3(b[3], b[4], b[5])) < 1e-2f, $"max {bounds.Max}");
        Assert.NotEmpty(ModelLoader.ConvertToGlb(data, file));
    }

    [Fact]
    public void SyntheticLegacyScene()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/geometry-2017.skp"));
        var model = ModelLoader.Load(data, "geometry-2017.skp");
        Assert.True(model.Metadata.HasGeometry);
        var root = Assert.Single(model.Roots);

        // The model's own triangle (painted Oak), and two placements of the plate (a square with a hole: eight triangles); the hidden third is skipped.
        Assert.Equal(1, root.Mesh!.TriangleCount);
        Assert.Equal(["Plate A", "Plate B"], root.Children.Select(c => c.Name));
        Assert.Same(root.Children[0].Mesh, root.Children[1].Mesh);
        Assert.Equal(8, root.Children[0].Mesh!.TriangleCount);
        Assert.Equal(17, model.ComputeStatistics().Triangles);
        Assert.Equal("Oak", model.Materials[0].Name);
        Assert.Equal(new Vector4(160 / 255f, 110 / 255f, 60 / 255f, 1), model.Materials[0].BaseColor);
        Assert.Equal(["Walls"], model.Layers.Select(l => l.Name));
        var bounds = model.ComputeBounds()!.Value;
        Assert.Equal(new Vector3(-8, 0, 0), bounds.Min);
        Assert.Equal(new Vector3(14, 18, 0), bounds.Max);
    }

    [Fact]
    public void SyntheticLegacySceneFailsCleanlyWhenCut()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/geometry-2017.skp"));
        for (var length = 0; length < data.Length; length += 7)
        {
            try
            {
                _ = ModelLoader.Load(data[..length], "geometry-2017.skp");
            }
            catch (ModelFormatException)
            {
            }
        }
    }

    /// <summary>Textured materials and texture coordinates in both generations (local samples): the image is carried, every mesh with a textured group has one UV pair per vertex, all finite, spanning a few repeats at most.</summary>
    [Theory]
    [InlineData("allan__Workshop_table.skp")]
    [InlineData("allan__Sofa.skp")]
    [InlineData("corynth__coffee_tables.skp")]
    public void TexturesAndUvsOfRealFiles(string file)
    {
        var root = Environment.GetEnvironmentVariable("XRAY_3D_LOCAL_SAMPLES") ?? "/tmp/claude-0/samples";
        var path = Path.Combine(root, "sketchup", file);
        Assert.SkipWhen(!File.Exists(path), "local sample not present: " + file);
        var model = ModelLoader.Load(File.ReadAllBytes(path), file);
        Assert.NotEmpty(model.Textures);
        Assert.All(model.Textures, t => Assert.Equal("image/jpeg", t.MediaType));
        var textured = model.Materials.Count(m => m.BaseColorTexture is not null);
        Assert.True(textured > 0);
        var checkedMeshes = 0;
        foreach (var mesh in model.Meshes.Where(m => m.TexCoords.Length > 0))
        {
            Assert.Equal(mesh.VertexCount * 2, mesh.TexCoords.Length);
            Assert.All(mesh.TexCoords, c => Assert.True(float.IsFinite(c) && Math.Abs(c) < 200, "uv " + c));
            checkedMeshes++;
        }

        Assert.True(checkedMeshes > 0);
        Assert.NotEmpty(ModelLoader.ConvertToGlb(File.ReadAllBytes(path), file));
    }
}
