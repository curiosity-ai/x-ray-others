using System.Numerics;
using System.Text.Json;

namespace XRay.ThreeD.Tests;

/// <summary>
/// IGES surfaces and B-rep against OpenCASCADE's IGES reader: area, volume (solids) and bounds of what each file draws. The synthetic files were
/// written by OpenCASCADE's writer in its two modes (B-rep with quadrics or B-splines, and trimmed surfaces); the others are real exports.
/// </summary>
public class IgesSurfaceTests
{
    private static readonly JsonElement Golden = JsonDocument.Parse(File.ReadAllText(DatasetPaths.CorpusFile("iges-golden.json"))).RootElement;

    public static IEnumerable<object[]> Files() => Golden.EnumerateObject().Select(p => new object[] { p.Name });

    [Theory]
    [MemberData(nameof(Files))]
    public void Surfaces_match_opencascade(string file)
    {
        var expected = Golden.GetProperty(file);
        var model = ModelLoader.Load(DatasetPaths.File(file));
        var (area, volume) = StepTests.Measure(model);

        // Triangles inscribe the surface (angular deflection 0.25 rad by default), so the mesh is a little under the exact area and volume.
        var expectedArea = expected.GetProperty("area").GetDouble();
        Assert.True(Math.Abs(area / expectedArea - 1) < 0.006, $"{file}: area {area:F3} vs exact {expectedArea:F3}");
        if (expected.TryGetProperty("volume", out var v))
        {
            Assert.True(Math.Abs(volume / v.GetDouble() - 1) < 0.012, $"{file}: volume {volume:F3} vs exact {v.GetDouble():F3}");
        }

        var bounds = model.ComputeBounds()!.Value;
        var min = expected.GetProperty("min").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
        var max = expected.GetProperty("max").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
        var tolerance = (float)Math.Max(0.05, 0.003 * (bounds.Max - bounds.Min).Length());
        Assert.True(Vector3.Distance(bounds.Min, new Vector3(min[0], min[1], min[2])) < tolerance, $"{file}: min {bounds.Min}");
        Assert.True(Vector3.Distance(bounds.Max, new Vector3(max[0], max[1], max[2])) < tolerance, $"{file}: max {bounds.Max}");
        Assert.DoesNotContain(model.Warnings, w => w.Contains("could not be tessellated", StringComparison.Ordinal) || w.Contains("skipped", StringComparison.Ordinal));
    }

    [Fact]
    public void Brep_faces_keep_their_ranges_and_the_solid_is_one_mesh_on_its_level()
    {
        var model = ModelLoader.Load(DatasetPaths.File("synthetic/iges-brep-quadrics.igs"));

        var mesh = Assert.Single(model.Meshes);
        Assert.Equal(mesh.Indices.Length, mesh.Faces.Sum(f => f.IndexCount));
        Assert.Equal(mesh.Positions.Length, mesh.Normals.Length);
        // Plate: 4 sides, bottom, top with its hole, cylinder, sphere cap, cone, cone top: about a dozen faces.
        Assert.InRange(mesh.Faces.Length, 8, 20);
        Assert.Equal("iges", model.Metadata.Format.Id);
        Assert.True(model.Metadata.HasGeometry);
        // The edge curves of the B-rep are the faces' boundaries, not extra drawing.
        Assert.All(model.Meshes, m => Assert.NotEmpty(m.Indices));
        GlbValidation.Validate(GlbWriter.ToArray(model));
    }

    [Fact]
    public void Solids_are_not_inside_out()
    {
        foreach (var file in new[] { "synthetic/iges-brep-quadrics.igs", "synthetic/iges-nurbs-brep.igs", "iges/slewing-ring.iges", "iges/jetracer-cam-mount.iges" })
        {
            var (_, volume) = StepTests.Measure(ModelLoader.Load(DatasetPaths.File(file)));
            Assert.True(volume > 0, $"{file}: signed volume {volume}");
        }
    }

    [Fact]
    public void Dataset_files_carry_node_layers_and_colours()
    {
        var model = ModelLoader.Load(DatasetPaths.File("iges/jetracer-cam-mount.iges"));

        var root = Assert.Single(model.Roots);
        Assert.NotEmpty(root.Children);
        Assert.All(root.Children, c => Assert.True(c.Layer >= 0));
        Assert.Equal(92, model.Meshes.Sum(m => m.Faces.Length));
    }

    [Fact]
    public void Metadata_alone_needs_no_tessellation()
    {
        var meta = ModelLoader.ReadMetadata(File.ReadAllBytes(DatasetPaths.File("synthetic/iges-nurbs-brep.igs")), "x.igs");

        Assert.True(meta.HasGeometry);
        Assert.Null(meta.Statistics);
        Assert.Contains(meta.Properties, p => p.Name.StartsWith("128 ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_coarser_tolerance_gives_fewer_triangles()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/iges-brep-quadrics.igs"));
        var fine = ModelLoader.Load(data, "x.igs", new LoadOptions { AngularDeflection = 0.08 }).ComputeStatistics().Triangles;
        var coarse = ModelLoader.Load(data, "x.igs", new LoadOptions { AngularDeflection = 0.6 }).ComputeStatistics().Triangles;

        Assert.True(fine > coarse);
    }

    [Fact]
    public void Triangle_limit_is_honoured()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("iges/jetracer-cam-mount.iges"));

        Assert.Throws<ModelFormatException>(() => ModelLoader.Load(data, "x.igs", new LoadOptions { MaxTriangles = 100 }));
    }
}
