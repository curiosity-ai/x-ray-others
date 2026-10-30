using System.Numerics;

namespace XRay.ThreeD.Tests;

/// <summary>What the GLB profiles (quantised, meshopt, recentred) must keep: the drawn solid's area and volume, and every node's place in the scene.</summary>
public class GlbMetricsTests
{
    private static IEnumerable<string> ModelFiles() => MetricsTests.ModelFiles();

    private static Model3D? TryLoad(string relative) => MetricsTests.TryLoad(relative);

    private static (double Area, double Volume, long Triangles) World(Model3D model) => MetricsTests.World(model);

    private static MeshData Cube() => MetricsTests.Cube();

    private static bool Close(double expected, double actual, double relative, double absolute = 0) => MetricsTests.Close(expected, actual, relative, absolute);

    [Fact]
    public void Every_GLB_profile_keeps_the_area_and_volume_of_every_dataset_model_within_half_a_percent()
    {
        var failures = new List<string>();
        var checkedModels = 0;
        foreach (var relative in ModelFiles())
        {
            if (TryLoad(relative) is not { } model || model.Metadata.Statistics is not { SurfaceArea: > 0 } stats)
            {
                continue;
            }

            checkedModels++;
            var toMeters = model.Metadata.Units?.ToMeters() ?? 1;
            foreach (var compression in new[] { GlbCompression.None, GlbCompression.Quantize, GlbCompression.Meshopt })
            {
                var glb = GlbWriter.ToArray(model, new GlbWriteOptions { Compression = compression });
                var (area, volume, _) = World(ModelLoader.Load(glb, "x.glb"));
                var expectedArea = stats.SurfaceArea * toMeters * toMeters;
                var expectedVolume = stats.Volume * toMeters * toMeters * toMeters;
                if (!Close(expectedArea, area, 0.005))
                {
                    failures.Add($"{relative} {compression}: area {area:G6} m2, expected {expectedArea:G6}");
                }

                // An open surface's volume is not a quantity of the model; compare only where the figure is the solid's.
                if (!Close(expectedVolume, volume, 0.005, 1e-4 * expectedArea * Math.Sqrt(expectedArea)))
                {
                    failures.Add($"{relative} {compression}: volume {volume:G6} m3, expected {expectedVolume:G6}");
                }
            }
        }

        Assert.True(checkedModels > 60, $"only {checkedModels} models were measured");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void A_node_with_a_mesh_and_children_does_not_pass_its_quantisation_or_recentring_frame_to_the_children()
    {
        // The parent's mesh is stored in its own frame (dequantised, and recentred when far from the origin); the frame cannot live in the
        // parent's matrix, which the children inherit.
        foreach (var offset in new[] { 0f, 1_000_000f })
        {
            var parentMesh = Cube();
            var childMesh = Cube();
            parentMesh.Positions = parentMesh.Positions.Select((v, i) => i % 3 == 0 ? v * 3 + offset : v * 3).ToArray();
            var model = new Model3D();
            model.Meshes.Add(parentMesh);
            model.Meshes.Add(childMesh);
            var parent = new SceneNode("parent") { Mesh = parentMesh, Transform = Matrix4x4.CreateTranslation(0, 0, 0) };
            parent.Children.Add(new SceneNode("child") { Mesh = childMesh, Transform = Matrix4x4.CreateTranslation(5, 5, 5) * Matrix4x4.CreateScale(2) });
            model.Roots.Add(parent);
            var expected = World(model);
            Assert.Equal(27.0 + 8, expected.Volume, 3);

            foreach (var compression in new[] { GlbCompression.None, GlbCompression.Quantize, GlbCompression.Meshopt })
            {
                var glb = GlbWriter.ToArray(model, new GlbWriteOptions { Compression = compression, ConvertToMeters = false, ConvertToYUp = false, Edges = EdgeMode.None });
                var back = ModelLoader.Load(glb, "x.glb");
                var actual = World(back);
                Assert.Equal(expected.Volume, actual.Volume, 2);
                Assert.Equal(expected.Area, actual.Area, 2);
                var bounds = back.ComputeBounds()!.Value;
                Assert.Equal(model.ComputeBounds()!.Value.Max.X, bounds.Max.X, 1);
                Assert.Equal(model.ComputeBounds()!.Value.Max.Y, bounds.Max.Y, 1);
            }
        }
    }
}
