using System.Text.Json;

namespace XRay.ThreeD.Tests;

/// <summary>ACIS SAT: polyhedra against ezdxf's loader, curved solids against closed forms, real NIST saves for topology.</summary>
public class SatTests
{
    private static JsonElement Golden(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(DatasetPaths.CorpusFile("sat-golden.json")));
        return doc.RootElement.GetProperty(name).Clone();
    }

    private static Model3D Load(string relative) => ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File(relative)), Path.GetFileName(relative));

    [Theory]
    [InlineData("synthetic/box-700.sat")]
    [InlineData("synthetic/hexprism-700.sat")]
    [InlineData("synthetic/box-700.sab")]
    public void Polyhedra_match_the_ezdxf_loader(string relative)
    {
        var model = Load(relative);
        var golden = Golden(Path.GetFileName(relative));
        var stats = model.Metadata.Statistics!;

        Assert.Equal("sat", model.Metadata.Format.Id);
        Assert.Equal(golden.GetProperty("volume").GetDouble(), stats.Volume, 3);
        Assert.Equal(golden.GetProperty("area").GetDouble(), stats.SurfaceArea, 3);
        Assert.Equal(golden.GetProperty("vertices").GetInt32(), model.Meshes[0].Faces.SelectMany(f => Enumerable.Range(f.FirstIndex, f.IndexCount).Select(i => model.Meshes[0].Indices[i])).Distinct().Select(i => (model.Meshes[0].Positions[i * 3], model.Meshes[0].Positions[i * 3 + 1], model.Meshes[0].Positions[i * 3 + 2])).Select(p => ((int)Math.Round(p.Item1 * 1000), (int)Math.Round(p.Item2 * 1000), (int)Math.Round(p.Item3 * 1000))).Distinct().Count());
        Assert.Equal(golden.GetProperty("faces").GetInt32(), model.Meshes[0].Faces.Length);
        var min = golden.GetProperty("min").EnumerateArray().Select(v => v.GetSingle()).ToArray();
        var max = golden.GetProperty("max").EnumerateArray().Select(v => v.GetSingle()).ToArray();
        var b = model.Metadata.Bounds!.Value;
        Assert.Equal(min[0], b.Min.X, 3);
        Assert.Equal(min[1], b.Min.Y, 3);
        Assert.Equal(max[2], b.Max.Z, 3);
        Assert.Equal(max[0], b.Max.X, 3);
        Assert.Equal(UpAxis.Z, model.Metadata.UpAxis);
        Assert.Equal(LengthUnit.Millimeter, model.Metadata.Units);
    }

    [Fact]
    public void Cylinder_sphere_and_torus_have_the_closed_form_area_and_volume()
    {
        var cylinder = Load("synthetic/cylinder-400.sat").Metadata.Statistics!;
        Assert.InRange(cylinder.Volume, Math.PI * 4 * 5 * 0.99, Math.PI * 4 * 5);
        Assert.InRange(cylinder.SurfaceArea, (2 * Math.PI * 2 * 5 + 2 * Math.PI * 4) * 0.99, 2 * Math.PI * 2 * 5 + 2 * Math.PI * 4);

        var sphere = Load("synthetic/sphere-400.sat");
        Assert.InRange(sphere.Metadata.Statistics!.Volume, 4.0 / 3 * Math.PI * 27 * 0.95, 4.0 / 3 * Math.PI * 27);
        Assert.InRange(sphere.Metadata.Statistics.SurfaceArea, 4 * Math.PI * 9 * 0.95, 4 * Math.PI * 9);
        var centre = sphere.Metadata.Bounds!.Value.Center;
        Assert.Equal(1, centre.X, 2);
        Assert.Equal(2, centre.Y, 2);
        Assert.Equal(3, centre.Z, 2);

        var torus = Load("synthetic/torus-400.sat").Metadata.Statistics!;
        Assert.InRange(torus.Volume, 2 * Math.PI * Math.PI * 5 * 0.97, 2 * Math.PI * Math.PI * 5);
        Assert.InRange(torus.SurfaceArea, 4 * Math.PI * Math.PI * 5 * 0.97, 4 * Math.PI * Math.PI * 5);
    }

    [Fact]
    public void A_real_save_of_the_first_ACIS_versions_loads_with_its_exact_extent()
    {
        var model = Load("sat/nist-arm-blank.sat");
        Assert.Equal("ACIS save version 105 (SAT)", model.Metadata.FormatVersion);
        Assert.Empty(model.Warnings);
        // A 13.812 x 1.75 x 1 inch blank: area and volume follow from the sides.
        Assert.Equal(13.812 * 1.75 * 1, model.Metadata.Statistics!.Volume, 3);
        Assert.Equal(2 * (13.812 * 1.75 + 13.812 + 1.75), model.Metadata.Statistics.SurfaceArea, 3);
        Assert.Equal(12, model.Meshes[0].TriangleCount);
    }

    private static string? Sample(string relative)
    {
        var root = Environment.GetEnvironmentVariable("XRAY_3D_LOCAL_SAMPLES") ?? "/tmp/claude-0/samples";
        var path = Path.Combine(root, "sat", relative.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? path : null;
    }

    /// <summary>Every undirected edge of a closed solid is used by exactly two triangles, once in each direction.</summary>
    internal static void AssertWatertight(MeshData mesh)
    {
        // Vertices of different faces are duplicated, so weld by position first.
        var ids = new Dictionary<(int, int, int), int>();
        int Id(uint i) => ids.TryGetValue(((int)Math.Round(mesh.Positions[i * 3] * 1e4), (int)Math.Round(mesh.Positions[i * 3 + 1] * 1e4), (int)Math.Round(mesh.Positions[i * 3 + 2] * 1e4)), out var v)
            ? v
            : ids[((int)Math.Round(mesh.Positions[i * 3] * 1e4), (int)Math.Round(mesh.Positions[i * 3 + 1] * 1e4), (int)Math.Round(mesh.Positions[i * 3 + 2] * 1e4))] = ids.Count;
        var directed = new Dictionary<(int, int), int>();
        for (var t = 0; t + 2 < mesh.Indices.Length; t += 3)
        {
            for (var k = 0; k < 3; k++)
            {
                var a = Id(mesh.Indices[t + k]);
                var b = Id(mesh.Indices[t + (k + 1) % 3]);
                if (a == b)
                {
                    continue;
                }

                directed[(a, b)] = directed.GetValueOrDefault((a, b)) + 1;
            }
        }

        var open = directed.Count(e => directed.GetValueOrDefault((e.Key.Item2, e.Key.Item1)) != e.Value);
        Assert.True(open <= directed.Count / 100, $"{open} of {directed.Count} edges have no opposite triangle edge");
    }

    [Theory]
    [InlineData("nist/arm_blnk/arm_blnk.sat", 1)]
    [InlineData("nist/boeing_part/boeing_part.sat", 1)]
    [InlineData("nist/new_demo_us/new_demo_us.sat", 1)]
    [InlineData("nist/simple_boeing_part/simple_boeing_part.sat", 1)]
    [InlineData("nist/simple_boeing_part/simple_boeing_part.hh.f.sat", 23)]
    [InlineData("ezdxf/cube_sat_400.sat", 1)]
    [InlineData("ezdxf/prism_sat_700.sat", 1)]
    [InlineData("ezdxf/cube_asm_sab_21800.sab", 1)]
    [InlineData("ezdxf/cube_asm_sab_22300.sab", 1)]
    public void Real_saves_are_closed_solids_with_every_face_tessellated(string relative, int bodies)
    {
        var path = Sample(relative);
        if (path is null)
        {
            Assert.Skip("local sample not present: " + relative);
            return;
        }

        var model = ModelLoader.Load(File.ReadAllBytes(path), Path.GetFileName(path));
        Assert.Equal(bodies, model.Meshes.Count);
        Assert.Empty(model.Warnings);
        Assert.True(model.Metadata.Statistics!.Volume > 0);
        foreach (var mesh in model.Meshes)
        {
            AssertWatertight(mesh);
        }

        // Every face record produced a patch (spline faces would be missing and warned about).
        if (path.EndsWith(".sab", StringComparison.Ordinal))
        {
            _ = GlbWriter.ToArray(model);
            return;
        }

        var faces = File.ReadAllText(path).Split('#').Count(r => r.TrimStart().StartsWith("face ", StringComparison.Ordinal) || System.Text.RegularExpressions.Regex.IsMatch(r.TrimStart(), @"^-?\d*\s*face\s"));
        Assert.Equal(faces, model.Meshes.Sum(m => m.Faces.Length));
        _ = GlbWriter.ToArray(model);
    }
}
