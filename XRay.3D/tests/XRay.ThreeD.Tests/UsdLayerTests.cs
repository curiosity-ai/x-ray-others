using System.Numerics;
using System.Text.Json;

namespace XRay.ThreeD.Tests;

/// <summary>
/// USD composed from several layers against pxr: a sublayer, a reference and a payload to geometry layers, a variant selection authored on the
/// referencing prim, an animated mesh (the first time sample is used) and a UsdUVTexture material, as loose files (through the companion resolver)
/// and packed into a USDZ. See tools/fixtures/make_usd_layers.py.
/// </summary>
public class UsdLayerTests
{
    private static readonly JsonElement Golden = JsonDocument.Parse(File.ReadAllText(DatasetPaths.CorpusFile("usd-layers-golden.json"))).RootElement;

    private static void Check(Model3D model, JsonElement? expected = null)
    {
        var golden = expected ?? Golden;
        var stats = model.Metadata.Statistics!;
        Assert.Equal(golden.GetProperty("triangles").GetInt32(), stats.Triangles);
        Assert.True(Math.Abs(stats.SurfaceArea / golden.GetProperty("area").GetDouble() - 1) < 1e-4, $"area {stats.SurfaceArea}");
        var bounds = model.ComputeBounds()!.Value;
        var min = golden.GetProperty("min").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
        var max = golden.GetProperty("max").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
        Assert.True(Vector3.Distance(bounds.Min, new Vector3(min[0], min[1], min[2])) < 1e-3f, $"min {bounds.Min}");
        Assert.True(Vector3.Distance(bounds.Max, new Vector3(max[0], max[1], max[2])) < 1e-3f, $"max {bounds.Max}");
        Assert.Empty(model.Warnings);
    }

    private static void CheckTexture(Model3D model)
    {
        var texture = Assert.Single(model.Textures);
        Assert.Equal("image/png", texture.MediaType);
        Assert.Equal("checker.png", texture.Name);
        var material = Assert.Single(model.Materials, m => m.BaseColorTexture is not null);
        Assert.Equal(0, material.BaseColorTexture!.Texture);
        var quad = Assert.Single(model.Meshes, m => m.TexCoords.Length > 0);
        Assert.Equal(4, quad.VertexCount);
        Assert.Equal(1f, quad.TexCoords[1]);
    }

    [Fact]
    public void Loose_layers_compose_like_pxr()
    {
        var folder = Path.Combine(DatasetPaths.Root, "synthetic", "usd-layers");
        var options = new LoadOptions { ResolveCompanion = name => File.Exists(Path.Combine(folder, name)) ? File.ReadAllBytes(Path.Combine(folder, name)) : null };
        var model = ModelLoader.Load(File.ReadAllBytes(Path.Combine(folder, "root.usda")), "root.usda", options);
        Check(model);
        CheckTexture(model);
        Assert.NotEmpty(GlbWriter.ToArray(model));
    }

    [Fact]
    public void A_crate_root_layer_composes_its_sublayer_and_reference_like_pxr()
    {
        var folder = Path.Combine(DatasetPaths.Root, "synthetic", "usd-layers");
        var options = new LoadOptions { ResolveCompanion = name => File.Exists(Path.Combine(folder, name)) ? File.ReadAllBytes(Path.Combine(folder, name)) : null };
        var model = ModelLoader.Load(File.ReadAllBytes(Path.Combine(folder, "crate-root.usdc")), "crate-root.usdc", options);
        Check(model, Golden.GetProperty("crate"));
    }

    [Fact]
    public void Usdz_members_compose_like_pxr()
    {
        var model = ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File("synthetic/usd-layers.usdz")), "usd-layers.usdz");
        Check(model);
        CheckTexture(model);
    }

    [Fact]
    public void Missing_layers_are_reported_not_fatal()
    {
        var model = ModelLoader.Load(File.ReadAllBytes(Path.Combine(DatasetPaths.Root, "synthetic", "usd-layers", "root.usda")), "root.usda");
        Assert.Contains(model.Warnings, w => w.Contains("geom/box.usda", StringComparison.Ordinal) || w.Contains("box.usda", StringComparison.Ordinal));
        Assert.NotEmpty(model.Meshes);
    }
}
