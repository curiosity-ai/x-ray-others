using System.Text.Json;

namespace XRay.ThreeD.Tests;

/// <summary>
/// Blender scenes in every unit setting, against Blender's own values (Corpus/blend-units-golden.json, written by bpy): one Blender unit is the scene's
/// scale_length metres, whatever length unit the interface shows, and a scene whose unit system is None does not scale at all. Sizes, areas and volumes
/// come out in metres once, in memory (through the unit and a scaling root where the scale is not a named unit) and after every GLB profile.
/// </summary>
public class BlendUnitsTests
{
    private static JsonElement Golden(string file) => JsonDocument.Parse(File.ReadAllText(DatasetPaths.CorpusFile("blend-units-golden.json"))).RootElement.GetProperty(file);

    private static double[] Vector(JsonElement e) => e.EnumerateArray().Select(x => x.GetDouble()).ToArray();

    /// <summary>The model as the GLB writer states it in metres: area, volume and bounds, through the profile (or straight from the model when it is null).</summary>
    private static (double Area, double Volume, double[] Size) InMetres(string file, GlbCompression? compression)
    {
        var model = ModelLoader.Load(DatasetPaths.File("synthetic/blend-units/" + file));
        if (compression is { } c)
        {
            model = ModelLoader.Load(GlbWriter.ToArray(model, new GlbWriteOptions { Compression = c }), "x.glb");
        }

        var stats = model.ComputeStatistics();
        var toMeters = model.Metadata.Units?.ToMeters() ?? 1;
        var box = model.ComputeBounds()!.Value;
        var size = new[] { (double)box.Size.X, box.Size.Y, box.Size.Z };
        if (compression is null)
        {
            // Blender is Z up; the GLB turns the scene to Y up, so compare the sorted sides.
            size = size.Select(s => s * toMeters).ToArray();
            return (stats.SurfaceArea * toMeters * toMeters, stats.Volume * toMeters * toMeters * toMeters, size.OrderBy(s => s).ToArray());
        }

        return (stats.SurfaceArea, stats.Volume, size.OrderBy(s => s).ToArray());
    }

    private static void Near(double expected, double actual, double relative = 0.002) => Assert.True(Math.Abs(expected - actual) <= relative * Math.Abs(expected), $"expected {expected:G6}, got {actual:G6}");

    [Theory]
    [InlineData("units-mm.blend", LengthUnit.Millimeter, 20, 0.001)]
    [InlineData("units-m.blend", LengthUnit.Meter, 2, 1)]
    [InlineData("units-cm.blend", LengthUnit.Centimeter, 30, 0.01)]
    [InlineData("units-inch.blend", LengthUnit.Inch, 10, 0.0254)]
    [InlineData("units-km.blend", LengthUnit.Kilometer, 0.5, 1000)]
    [InlineData("units-odd.blend", LengthUnit.Meter, 8, 0.0125)] // no named unit: metres, with the scale folded into a root node
    [InlineData("units-none.blend", LengthUnit.Meter, 2, 1)] // unit system None: scale_length (0.001) is not applied
    public void A_cube_has_the_size_the_unit_scale_gives_in_every_profile(string file, LengthUnit unit, double side, double metersPerUnit)
    {
        var model = ModelLoader.Load(DatasetPaths.File("synthetic/blend-units/" + file));
        Assert.Equal(unit, model.Metadata.Units);
        var expectedSide = side * metersPerUnit;
        var golden = Golden(file);
        Near(expectedSide, Vector(golden.GetProperty("max"))[0] - Vector(golden.GetProperty("min"))[0], 1e-5); // bpy agrees with the closed form
        foreach (GlbCompression? profile in new GlbCompression?[] { null, GlbCompression.None, GlbCompression.Quantize, GlbCompression.Meshopt })
        {
            var (area, volume, size) = InMetres(file, profile);
            Assert.All(size, s => Near(expectedSide, s));
            Near(6 * expectedSide * expectedSide, area);
            Near(expectedSide * expectedSide * expectedSide, volume);
            Near(golden.GetProperty("volume").GetDouble(), volume);
        }
    }

    [Theory]
    [InlineData("shapes.blend", 1.0)]
    [InlineData("shapes-mm.blend", 0.001)]
    public void The_shapes_scene_has_Blenders_own_area_volume_and_bounds_in_every_profile(string file, double metersPerUnit)
    {
        var golden = Golden(file);
        // Suzanne's eye sockets are open: the mesh is a surface, so the library leaves it out of the volume (and says so) while Blender's signed sum counts it.
        var suzanne = golden.GetProperty("objects").EnumerateArray().Single(o => o.GetProperty("name").GetString() == "Suzanne").GetProperty("volume").GetDouble();
        var goldenVolume = golden.GetProperty("volume").GetDouble() - suzanne;
        var goldenSize = Vector(golden.GetProperty("max")).Zip(Vector(golden.GetProperty("min")), (hi, lo) => hi - lo).OrderBy(s => s).ToArray();
        foreach (GlbCompression? profile in new GlbCompression?[] { null, GlbCompression.None, GlbCompression.Quantize, GlbCompression.Meshopt })
        {
            var (area, volume, size) = InMetres(file, profile);
            Near(golden.GetProperty("area").GetDouble(), area);
            Near(goldenVolume, volume);
            for (var i = 0; i < 3; i++)
            {
                Near(goldenSize[i], size[i]);
            }
        }

        // Closed forms for the parts, in the file's own units (so Blender's numbers are right too).
        double Volume(string name) => golden.GetProperty("objects").EnumerateArray().Where(o => o.GetProperty("name").GetString() == name).Sum(o => o.GetProperty("volume").GetDouble()) / Math.Pow(metersPerUnit, 3);
        Near(8, Volume("Cube"), 1e-4);
        Near(8, Volume("Mirrored"), 1e-4);
        Near(6, Volume("Sheared"), 1e-4); // 1 x 1 x 1 cube under the parent's (1, 2, 3) scale
        Near(3, Volume("Part"), 1e-4); // a unit cube in its own collection, and two instances of that collection
        Near(0.5 * 32 * Math.Sin(2 * Math.PI / 32) * 2, Volume("Cylinder"), 1e-4);
        Near(4.0 / 3 * Math.PI, Volume("Sphere"), 0.03);
        Near(2 * Math.PI * Math.PI * 1.5 * 0.25, Volume("Torus"), 0.05); // 32 x 16 facets: a few percent below the smooth torus
    }

    [Fact]
    public void Only_Suzanne_is_an_open_mesh_and_a_mirrored_instance_counts_positive()
    {
        var model = ModelLoader.Load(DatasetPaths.File("synthetic/blend-units/shapes.blend"));
        var stats = model.Metadata.Statistics!;
        Assert.Equal(1, stats.OpenMeshes);
        Assert.Equal(42, stats.OpenEdges);
        Assert.Equal(0, stats.InsideOutMeshes);
        Assert.False(stats.VolumeIsComplete);
        var suzanne = model.Meshes.Single(m => m.Name == "Suzanne");
        Assert.False(suzanne.AnalyzeTopology().IsClosed);
        Assert.All(model.Meshes.Where(m => m != suzanne), m => Assert.True(m.AnalyzeTopology().IsSolid, m.Name));

        // Blender's signed sum over the open mesh agrees with the library's SignedVolume.
        var golden = Golden("shapes.blend").GetProperty("objects").EnumerateArray().Single(o => o.GetProperty("name").GetString() == "Suzanne").GetProperty("volume").GetDouble();
        Near(golden, suzanne.AnalyzeTopology().SignedVolume, 0.005);
    }
}
