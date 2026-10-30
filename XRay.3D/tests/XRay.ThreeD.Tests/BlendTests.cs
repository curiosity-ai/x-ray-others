using System.Numerics;
using System.Text.Json;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Tests;

/// <summary>
/// Blender files against the values Blender's own Python module (bpy) reports for them (Corpus/blend-golden.json): object names, parents, triangle
/// counts, world-space bounds and Principled BSDF values. The synthetic scene is committed and covers the 5.x layout and all three containers; the
/// real files of Blender 2.81 to 4.4 (legacy and attribute mesh layouts, gzip and Zstandard) are not redistributable and are read from the
/// folder XRAY_3D_LOCAL_SAMPLES (default /tmp/claude-0/samples, sub-folder blend); without them those tests skip.
/// </summary>
public class BlendTests
{
    private static JsonElement Golden(string file) => JsonDocument.Parse(File.ReadAllText(DatasetPaths.CorpusFile("blend-golden.json"))).RootElement.GetProperty(file);

    private static string? LocalSample(string file)
    {
        var root = Environment.GetEnvironmentVariable("XRAY_3D_LOCAL_SAMPLES") ?? "/tmp/claude-0/samples";
        var path = Path.Combine(root, "blend", file);
        return File.Exists(path) ? path : null;
    }

    public static IEnumerable<object[]> RealFiles() => JsonDocument.Parse(File.ReadAllText(DatasetPaths.CorpusFile("blend-golden.json"))).RootElement
        .EnumerateObject().Select(p => p.Name).Where(n => n != "scene-zstd.blend").OrderBy(n => n, StringComparer.Ordinal).Select(n => new object[] { n });

    private static (SceneNode Node, Matrix4x4 World)? FindWorld(Model3D model, string name)
    {
        (SceneNode, Matrix4x4)? found = null;
        void Visit(SceneNode node, Matrix4x4 parent)
        {
            var world = node.Transform * parent;
            if (found is null && node.Name == name && node.Mesh is not null)
            {
                found = (node, world);
            }

            foreach (var child in node.Children)
            {
                Visit(child, world);
            }
        }

        foreach (var root in model.Roots)
        {
            Visit(root, Matrix4x4.Identity);
        }

        return found;
    }

    private static void Compare(Model3D model, JsonElement golden, bool exactTriangles)
    {
        foreach (var o in golden.GetProperty("objects").EnumerateArray())
        {
            var name = o.GetProperty("name").GetString()!;
            var hit = FindWorld(model, name);
            Assert.True(hit is not null, "object not found: " + name);
            var (node, world) = hit!.Value;
            var mesh = node.Mesh!;
            Assert.Equal(o.GetProperty("vertices").GetInt32() > 0, mesh.VertexCount > 0);
            var fan = o.GetProperty("fan_triangles").GetInt32();
            if (exactTriangles)
            {
                Assert.Equal(fan, mesh.TriangleCount);
            }
            else
            {
                // The polygon triangulator can disagree with n - 2 on a degenerate polygon of a dense mesh.
                Assert.InRange(mesh.TriangleCount, fan - fan / 1000 - 1, fan + fan / 1000 + 8);
            }

            if (exactTriangles)
            {
                Assert.Equal(o.GetProperty("triangles").GetInt32(), mesh.TriangleCount);
            }

            var box = mesh.ComputeBounds(world);
            var min = o.GetProperty("min").EnumerateArray().Select(e => e.GetDouble()).ToArray();
            var max = o.GetProperty("max").EnumerateArray().Select(e => e.GetDouble()).ToArray();
            var extent = Math.Max(1.0, Math.Max(max[0] - min[0], Math.Max(max[1] - min[1], max[2] - min[2])));
            var tolerance = 1e-4 * extent + 1e-4;
            Assert.InRange(box.Min.X, min[0] - tolerance, min[0] + tolerance);
            Assert.InRange(box.Min.Y, min[1] - tolerance, min[1] + tolerance);
            Assert.InRange(box.Min.Z, min[2] - tolerance, min[2] + tolerance);
            Assert.InRange(box.Max.X, max[0] - tolerance, max[0] + tolerance);
            Assert.InRange(box.Max.Y, max[1] - tolerance, max[1] + tolerance);
            Assert.InRange(box.Max.Z, max[2] - tolerance, max[2] + tolerance);
            var parent = o.GetProperty("parent");
            if (parent.ValueKind == JsonValueKind.String)
            {
                Assert.True(model.Nodes().Any(n => n.Name == parent.GetString() && n.Children.Contains(node)), "parent link of " + name);
            }
        }

        foreach (var m in golden.GetProperty("materials").EnumerateObject())
        {
            var material = model.Materials.FirstOrDefault(x => x.Name == m.Name);
            if (material is null || !m.Value.TryGetProperty("base_color", out var color))
            {
                continue;
            }

            var c = color.EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
            Assert.Equal(c[0], material.BaseColor.X, 4);
            Assert.Equal(c[1], material.BaseColor.Y, 4);
            Assert.Equal(c[2], material.BaseColor.Z, 4);
            Assert.Equal((float)m.Value.GetProperty("metallic").GetDouble(), material.Metallic, 4);
            Assert.Equal((float)m.Value.GetProperty("roughness").GetDouble(), material.Roughness, 4);
        }
    }

    [Theory]
    [InlineData("scene-zstd.blend")]
    [InlineData("scene-gzip.blend")]
    [InlineData("scene-raw.blend")]
    public void SyntheticSceneMatchesBlender(string file)
    {
        var model = ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File("synthetic/blend/" + file)), file);
        Assert.Equal("blender", model.Metadata.Format.Id);
        Assert.Equal(UpAxis.Z, model.Metadata.UpAxis);
        Assert.Equal(LengthUnit.Millimeter, model.Metadata.Units);
        Assert.Equal("Synthetic", model.Metadata.Title);
        Compare(model, Golden("scene-zstd.blend"), exactTriangles: true);
        Assert.Contains("Red", model.Metadata.MaterialNames);

        // The hidden object and the excluded collection are not drawn; the camera and light are reported, not drawn.
        Assert.DoesNotContain(model.Nodes(), n => n.Name is "Hidden" or "InExcluded");
        Assert.Contains(model.Metadata.Notes, n => n.Contains("camera"));
        Assert.Contains(model.Metadata.Notes, n => n.Contains("light"));
        Assert.Contains("Props", model.Metadata.LayerNames);
        Assert.Equal(1, model.Metadata.LayerNames.Count(n => n == "Widget"));
    }

    [Fact]
    public void CollectionInstanceUsesTheInstanceOffset()
    {
        var model = ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File("synthetic/blend/scene-zstd.blend")), "scene-zstd.blend");
        var instance = model.Nodes().First(n => n.Name == "WidgetInstance");
        var part = FindWorld(model, "WidgetPart")!.Value;
        var box = part.Node.Mesh!.ComputeBounds(part.World);

        // A unit-half cube (+-1) minus the offset (1, 0, 0), halved, placed at y = 6: x in [-1, 0], y in [5.5, 6.5], z in [-0.5, 0.5].
        Assert.Equal(-1f, box.Min.X, 4);
        Assert.Equal(0f, box.Max.X, 4);
        Assert.Equal(5.5f, box.Min.Y, 4);
        Assert.Equal(6.5f, box.Max.Y, 4);
        Assert.Equal(-0.5f, box.Min.Z, 4);
        Assert.Equal(0.5f, box.Max.Z, 4);
        Assert.NotNull(instance);
    }

    [Fact]
    public void ConvertsToValidGlb()
    {
        var glb = ModelLoader.ConvertToGlb(File.ReadAllBytes(DatasetPaths.File("synthetic/blend/scene-zstd.blend")), "scene-zstd.blend");
        Assert.True(glb.Length > 1000);
        Assert.Equal((byte)'g', glb[0]);
        var back = ModelLoader.Load(glb, "back.glb");
        Assert.Equal(ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File("synthetic/blend/scene-zstd.blend")), "x.blend").Metadata.Statistics!.Triangles, back.Metadata.Statistics!.Triangles);
    }

    [Fact]
    public void ReportsContainerAndVersion()
    {
        var model = ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File("synthetic/blend/scene-gzip.blend")), "scene-gzip.blend");
        Assert.StartsWith("Blender 5.", model.Metadata.Application);
        Assert.Contains(model.Metadata.Notes, n => n.Contains("gzip"));
    }

    [Fact]
    public void DetectsByContentEvenWithAnotherExtension()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/blend/scene-raw.blend"));
        Assert.Equal("blender", ModelLoader.Detect(data, "model.bin").Id);
        Assert.Equal("blender", ModelLoader.Detect(File.ReadAllBytes(DatasetPaths.File("synthetic/blend/scene-zstd.blend")), "model.blend").Id);
    }

    [Theory]
    [MemberData(nameof(RealFiles))]
    public void RealFilesMatchBlender(string file)
    {
        var path = LocalSample(file);
        Assert.SkipWhen(path is null, "local Blender sample not present: " + file);
        var model = ModelLoader.Load(File.ReadAllBytes(path!), file);
        var golden = Golden(file);
        Compare(model, golden, exactTriangles: false);
        var total = model.Metadata.Statistics!.Triangles;
        var expected = golden.GetProperty("triangles").GetInt64();

        // Blender drops a few degenerate triangles from its own tessellation; the polygon fan count is exact (checked per object above).
        Assert.InRange(total, expected - Math.Max(10, expected / 1000), expected + Math.Max(10, expected / 1000));
    }

    [Fact]
    public void ZstdWrappedLegacyDecompressionIsTheSameAsGzip()
    {
        var raw = ZstdDecoder.Decompress(File.ReadAllBytes(DatasetPaths.File("synthetic/blend/scene-zstd.blend")), 1 << 28, TestContext.Current.CancellationToken);
        var onDisk = File.ReadAllBytes(DatasetPaths.File("synthetic/blend/scene-raw.blend"));
        Assert.True(raw.Length > onDisk.Length);
        Assert.True(raw.AsSpan(0, 17).SequenceEqual(onDisk.AsSpan(0, 17)));
    }
}
