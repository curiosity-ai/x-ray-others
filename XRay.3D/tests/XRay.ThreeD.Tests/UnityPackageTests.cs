using System.Numerics;

namespace XRay.ThreeD.Tests;

public class UnityPackageTests
{
    private static Model3D Load(string file) => ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File(file)), file);

    [Fact]
    public void Text_mesh_assets_match_the_bounds_and_counts_Unity_wrote_into_them()
    {
        var model = Load("unitypackage/opengameart-key-collection.unitypackage");

        Assert.Equal("unity", model.Metadata.Format.Id);
        Assert.Equal("Key Collection", model.Metadata.Title);
        Assert.Contains("Assets/Art/Keys/Key Collection/Prefabs/Key1.prefab", model.Metadata.ComponentFiles);
        Assert.Equal(6, model.Metadata.PartNames.Count);

        // Key3_Mesh: 9527 vertices, 37182 indices; m_LocalAABB centre/extent in the file (X is mirrored on load).
        var mesh = model.Meshes.Single(m => m.Name == "Key3_Mesh");
        Assert.Equal(9527, mesh.VertexCount);
        Assert.Equal((9534 + 27648) / 3, mesh.TriangleCount);
        var box = mesh.ComputeBounds();
        var center = new Vector3(0.056079745f, 0.015304282f, 0.11427373f);
        var extent = new Vector3(3.2629347f, 0.1300865f, 1.1925812f);
        Assert.True(Vector3.Distance(box.Min, new Vector3(-(center.X + extent.X), center.Y - extent.Y, center.Z - extent.Z)) < 1e-3f, box.Min.ToString());
        Assert.True(Vector3.Distance(box.Max, new Vector3(-(center.X - extent.X), center.Y + extent.Y, center.Z + extent.Z)) < 1e-3f, box.Max.ToString());
        Assert.NotEmpty(GlbWriter.ToArray(model));
    }

    [Fact]
    public void Prefabs_with_model_files_load_through_the_model_readers()
    {
        var model = Load("unitypackage/opengameart-forest-arena.unitypackage");

        Assert.True(model.Metadata.HasGeometry);
        Assert.True(model.Metadata.Statistics!.Triangles > 1000);
        Assert.Contains(model.Metadata.PartNames, n => n == "ForestBarrel");
        Assert.NotEmpty(GlbWriter.ToArray(model));
    }
}
