using System.Numerics;
using System.Text;
using System.Text.Json;

namespace XRay.ThreeD.Tests;

/// <summary>AP242 tessellated entities and mapped items; the golden values are OpenCASCADE's exact area, volume and bounds of what each file draws.</summary>
public class StepAp242Tests
{
    private static readonly JsonElement Golden = JsonDocument.Parse(File.ReadAllText(DatasetPaths.CorpusFile("step-ap242-golden.json"))).RootElement;

    private static void AssertMatchesOracle(Model3D model, string file, double tolerance)
    {
        var expected = Golden.GetProperty(file);
        var (area, volume) = StepTests.Measure(model);
        var expectedArea = expected.GetProperty("area").GetDouble();
        var expectedVolume = expected.GetProperty("volume").GetDouble();
        Assert.True(Math.Abs(area / expectedArea - 1) < tolerance, $"{file}: area {area:F3} vs exact {expectedArea:F3}");
        Assert.True(Math.Abs(volume / expectedVolume - 1) < tolerance, $"{file}: volume {volume:F3} vs exact {expectedVolume:F3}");
        var bounds = model.ComputeBounds()!.Value;
        var min = expected.GetProperty("min").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
        var max = expected.GetProperty("max").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
        Assert.True(Vector3.Distance(bounds.Min, new Vector3(min[0], min[1], min[2])) < 0.01f, $"{file}: min {bounds.Min}");
        Assert.True(Vector3.Distance(bounds.Max, new Vector3(max[0], max[1], max[2])) < 0.01f, $"{file}: max {bounds.Max}");
    }

    [Fact]
    public void Tessellated_solid_draws_the_stored_triangles()
    {
        var model = ModelLoader.Load(DatasetPaths.File("synthetic/tessellated.stp"));

        // The stored triangles inscribe the true surface: the area is just under the exact one, never over by more than rounding.
        AssertMatchesOracle(model, "tessellated.stp", 0.002);
        var mesh = Assert.Single(model.Meshes);
        Assert.Equal(Golden.GetProperty("tessellated.stp").GetProperty("faces").GetInt32(), mesh.Faces.Length);
        Assert.Equal(mesh.Indices.Length, mesh.Faces.Sum(f => f.IndexCount));
        Assert.Equal(mesh.Positions.Length, mesh.Normals.Length);
        Assert.Empty(model.Warnings);
        Assert.Equal("AP242", model.Metadata.GetProperty("application protocol"));
    }

    [Fact]
    public void Tessellated_solid_keeps_layer_and_face_colour()
    {
        var model = ModelLoader.Load(DatasetPaths.File("synthetic/tessellated.stp"));

        var root = Assert.Single(model.Roots);
        Assert.Equal("Tessellated plate", root.Name);
        Assert.Equal("TOP", model.Layers[root.Layer].Name);
        Assert.Contains("TOP", model.Metadata.LayerNames);
        var mesh = Assert.Single(model.Meshes);
        Assert.Equal(2, mesh.Groups.Count);
        Assert.Single(model.Materials);
        Assert.Equal("#CC1A1A", model.Materials[0].Name);
    }

    [Fact]
    public void Strip_winding_alternates_so_every_triangle_faces_out()
    {
        var model = ModelLoader.Load(DatasetPaths.File("synthetic/tessellated.stp"));
        var mesh = Assert.Single(model.Meshes);

        // Every triangle agrees with the stored (or, for the plain faces, derived) vertex normals.
        for (var t = 0; t < mesh.Indices.Length; t += 3)
        {
            Vector3 P(int k) => new(mesh.Positions[3 * mesh.Indices[t + k]], mesh.Positions[3 * mesh.Indices[t + k] + 1], mesh.Positions[3 * mesh.Indices[t + k] + 2]);
            Vector3 N(int k) => new(mesh.Normals[3 * mesh.Indices[t + k]], mesh.Normals[3 * mesh.Indices[t + k] + 1], mesh.Normals[3 * mesh.Indices[t + k] + 2]);
            var geometric = Vector3.Cross(P(1) - P(0), P(2) - P(0));
            Assert.True(Vector3.Dot(geometric, N(0) + N(1) + N(2)) >= 0, $"triangle {t / 3} is wound against its normals");
        }
    }

    [Fact]
    public void Mapped_items_instance_one_mesh_under_the_composed_placements()
    {
        var model = ModelLoader.Load(DatasetPaths.File("synthetic/mapped.stp"));

        // Five boxes are drawn; one mesh is stored. Exact geometry: the area, volume and bounds are OpenCASCADE's.
        AssertMatchesOracle(model, "mapped.stp", 1e-4);
        var mesh = Assert.Single(model.Meshes);
        var instances = model.Nodes().Where(n => n.Mesh == mesh).ToList();
        Assert.Equal(Golden.GetProperty("mapped.stp").GetProperty("instances").GetInt32(), instances.Count);
        Assert.Equal(5, model.ComputeStatistics().Triangles / mesh.TriangleCount);
    }

    [Fact]
    public void Mapped_items_carry_their_layers_and_names()
    {
        var model = ModelLoader.Load(DatasetPaths.File("synthetic/mapped.stp"));

        var root = Assert.Single(model.Roots);
        Assert.Equal(["pair 1", "pair 2", "lone box"], root.Children.Select(c => c.Name));
        Assert.Equal(["PAIRS", "PAIRS", "LONE"], root.Children.Select(c => model.Layers[c.Layer].Name));
        Assert.Equal(2, root.Children[0].Children.Count);
        Assert.Equal(["boxA", "boxB"], root.Children[0].Children.Select(c => c.Name));
        Assert.Equal(["LONE", "PAIRS"], model.Metadata.LayerNames.Order(StringComparer.Ordinal));
        GlbValidation.Validate(GlbWriter.ToArray(model));
    }

    private static string Wrap(string data) =>
        "ISO-10303-21;\nHEADER;\nFILE_DESCRIPTION(('x'),'2;1');\nFILE_NAME('x','2025-01-01T00:00:00',(''),(''),'','','');\nFILE_SCHEMA(('AP242_MANAGED_MODEL_BASED_3D_ENGINEERING_MIM_LF'));\nENDSEC;\nDATA;\n" + data + "\nENDSEC;\nEND-ISO-10303-21;\n";

    private static Model3D LoadText(string data) => ModelLoader.Load(Encoding.Latin1.GetBytes(Wrap(data)), "x.step");

    [Fact]
    public void Surface_sets_without_a_solid_are_drawn_and_unit_normals_are_filled_in()
    {
        // A strip with no normals over a point index that skips a coordinate; the strip's second triangle is wound the other way round.
        var model = LoadText("#1=COORDINATES_LIST('',5,((0.,0.,0.),(9.,9.,9.),(1.,0.,0.),(1.,1.,0.),(0.,1.,0.)));\n"
            + "#2=COMPLEX_TRIANGULATED_SURFACE_SET('',#1,4,(),(1,3,5,4),((1,2,3,4)),());\n");

        var mesh = Assert.Single(model.Meshes);
        Assert.Equal(4, mesh.VertexCount);
        Assert.Equal(2, mesh.TriangleCount);
        Assert.Equal(1.0, StepTests.Measure(model).Area, 6);
        Assert.All(Enumerable.Range(0, mesh.VertexCount), v => Assert.Equal(1f, mesh.Normals[3 * v + 2], 5));
    }

    [Fact]
    public void Invalid_triangle_indices_are_skipped_with_a_warning()
    {
        var model = LoadText("#1=COORDINATES_LIST('',3,((0.,0.,0.),(1.,0.,0.),(0.,1.,0.)));\n"
            + "#2=TRIANGULATED_FACE('',#1,3,(),$,(),((1,2,3),(1,2,9),(0,1,2),(1,1,2)));\n#3=TESSELLATED_SHELL('',(#2),$);\n");

        Assert.Equal(1, Assert.Single(model.Meshes).TriangleCount);
        Assert.Contains(model.Warnings, w => w.Contains("invalid indices", StringComparison.Ordinal));
    }

    [Fact]
    public void A_triangle_wound_against_the_stored_normal_is_turned_round()
    {
        var model = LoadText("#1=COORDINATES_LIST('',3,((0.,0.,0.),(1.,0.,0.),(0.,1.,0.)));\n#2=TRIANGULATED_FACE('',#1,3,((0.,0.,-1.)),$,(),((1,2,3)));\n");

        var mesh = Assert.Single(model.Meshes);
        Assert.Equal(-1f, mesh.Normals[2]);
        var c = Vector3.Cross(new Vector3(mesh.Positions[3 * mesh.Indices[1]], mesh.Positions[3 * mesh.Indices[1] + 1], 0) - Vector3.Zero, new Vector3(mesh.Positions[3 * mesh.Indices[2]], mesh.Positions[3 * mesh.Indices[2] + 1], 0));
        Assert.True(c.Z < 0);
    }

    [Fact]
    public void A_map_origin_goes_to_the_target_frame_when_both_are_set()
    {
        // The standard's meaning: a point p of the mapped representation lands at target * inverse(origin) * p. Origin at (1,1,0), target at (50,20,5)
        // turned a quarter about z (x' = 50 - (y-1), y' = 20 + (x-1)): the 4x3x2 box spans x 48..51 (from y 3..0), y 19..23, z 5..7.
        var model = LoadText("#1=COORDINATES_LIST('',8,((0.,0.,0.),(4.,0.,0.),(4.,3.,0.),(0.,3.,0.),(0.,0.,2.),(4.,0.,2.),(4.,3.,2.),(0.,3.,2.)));\n"
            + "#2=TRIANGULATED_FACE('',#1,8,(),$,(),((1,3,2),(1,4,3),(5,6,7),(5,7,8),(1,2,6),(1,6,5),(2,3,7),(2,7,6),(3,4,8),(3,8,7),(4,1,5),(4,5,8)));\n"
            + "#3=CARTESIAN_POINT('',(1.,1.,0.));\n#4=DIRECTION('',(0.,0.,1.));\n#5=DIRECTION('',(1.,0.,0.));\n#6=AXIS2_PLACEMENT_3D('',#3,#4,#5);\n"
            + "#7=CARTESIAN_POINT('',(50.,20.,5.));\n#8=DIRECTION('',(0.,1.,0.));\n#9=AXIS2_PLACEMENT_3D('',#7,#4,#8);\n"
            + "#10=SHAPE_REPRESENTATION('part',(#6,#2),#9000);\n#11=REPRESENTATION_MAP(#6,#10);\n#12=MAPPED_ITEM('m',#11,#9);\n"
            + "#13=SHAPE_REPRESENTATION('top',(#6,#12),#9000);\n#14=PRODUCT_DEFINITION('d','',$,$);\n#15=PRODUCT_DEFINITION_SHAPE('','',#14);\n#16=SHAPE_DEFINITION_REPRESENTATION(#15,#13);\n");

        var bounds = model.ComputeBounds()!.Value;
        Assert.True(Vector3.Distance(bounds.Min, new Vector3(48, 19, 5)) < 1e-4f, bounds.Min.ToString());
        Assert.True(Vector3.Distance(bounds.Max, new Vector3(51, 23, 7)) < 1e-4f, bounds.Max.ToString());
        Assert.Equal(24.0, StepTests.Measure(model).Volume, 4);
    }

    [Fact]
    public void Self_referencing_maps_and_instance_explosions_are_bounded_and_the_mesh_is_shared()
    {
        // A representation that maps itself, and a ladder of maps that doubles the instances at every rung.
        var sb = new StringBuilder("#1=COORDINATES_LIST('',3,((0.,0.,0.),(1.,0.,0.),(0.,1.,0.)));\n#2=TRIANGULATED_FACE('',#1,3,(),$,(),((1,2,3)));\n");
        sb.Append("#3=CARTESIAN_POINT('',(0.,0.,0.));\n#4=AXIS2_PLACEMENT_3D('',#3,$,$);\n");
        sb.Append("#10=SHAPE_REPRESENTATION('r0',(#4,#2),#9000);\n#11=REPRESENTATION_MAP(#4,#10);\n");
        for (var i = 1; i <= 40; i++)
        {
            sb.Append('#').Append(1000 + 4 * i).Append("=MAPPED_ITEM('a',#").Append(10 * i + 1).Append(",#4);\n");
            sb.Append('#').Append(1001 + 4 * i).Append("=MAPPED_ITEM('b',#").Append(10 * i + 1).Append(",#4);\n");
            sb.Append('#').Append(10 * (i + 1)).Append("=SHAPE_REPRESENTATION('r',(#4,#").Append(1000 + 4 * i).Append(",#").Append(1001 + 4 * i).Append("),#9000);\n");
            sb.Append('#').Append(10 * (i + 1) + 1).Append("=REPRESENTATION_MAP(#4,#").Append(10 * (i + 1)).Append(");\n");
        }

        sb.Append("#600=SHAPE_REPRESENTATION('self',(#4,#601),#9000);\n#601=MAPPED_ITEM('me',#602,#4);\n#602=REPRESENTATION_MAP(#4,#600);\n");
        sb.Append("#700=PRODUCT_DEFINITION('d','',$,$);\n#701=PRODUCT_DEFINITION_SHAPE('','',#700);\n#702=SHAPE_DEFINITION_REPRESENTATION(#701,#410);\n");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var model = LoadText(sb.ToString());

        // Nesting stops at sixteen levels (about 130 thousand instances, one stored mesh) rather than 2^40; the self-map is not followed.
        Assert.Single(model.Meshes);
        Assert.InRange(model.Nodes().Count(), 1, 600_000);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
    }
}
