using System.Numerics;
using System.Text.Json;

namespace XRay.ThreeD.Tests;

/// <summary>The rendering profile of the GLB output: faces, materials, edges.</summary>
public class ProfileTests
{
    private static JsonElement Json(byte[] glb)
    {
        var length = BitConverter.ToInt32(glb, 12);
        return JsonDocument.Parse(glb.AsMemory(20, length)).RootElement;
    }

    [Fact]
    public void Cube_has_twelve_feature_edges()
    {
        var mesh = ModelLoader.Load(DatasetPaths.File("synthetic/cube-binary.stl")).Meshes[0];

        var edges = MeshEdges.Extract(mesh, cancellation: TestContext.Current.CancellationToken);

        Assert.Equal(12, edges.Length / 2); // the diagonals of the flat sides are not creases
    }

    [Fact]
    public void Open_boundaries_and_creases_are_edges_but_flat_interiors_are_not()
    {
        // Two triangles forming a flat square, then a third folded up at 90 degrees.
        var mesh = new MeshData
        {
            Positions = [0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 1, 0, 1, 1, 1, 1],
            Indices = [0, 1, 2, 0, 2, 3, 1, 4, 5],
        };
        var edges = MeshEdges.Extract(mesh, cancellation: TestContext.Current.CancellationToken);
        var pairs = Enumerable.Range(0, edges.Length / 2).Select(i => (Math.Min(edges[2 * i], edges[2 * i + 1]), Math.Max(edges[2 * i], edges[2 * i + 1]))).ToHashSet();

        Assert.DoesNotContain((0u, 2u), pairs);      // the shared diagonal of the flat square
        Assert.Contains((0u, 1u), pairs);            // an open border
        Assert.Contains((1u, 4u), pairs);            // the folded triangle's own borders
        Assert.Equal(7, pairs.Count);                // 4 square borders + 3 of the folded triangle
    }

    [Fact]
    public void Faces_of_a_part_are_one_range_each_and_cover_all_indices()
    {
        var model = ModelLoader.Load(DatasetPaths.File("sldprt/nist-ftc-10-part.sldprt"));
        var mesh = Assert.Single(model.Meshes);

        Assert.Equal(206, mesh.Faces.Length);
        Assert.Equal(mesh.Indices.Length, mesh.Faces.Sum(f => f.IndexCount));
        var ordered = mesh.Faces.OrderBy(f => f.FirstIndex).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            Assert.Equal(ordered[i - 1].FirstIndex + ordered[i - 1].IndexCount, ordered[i].FirstIndex);
        }

        // Material groups tile the index range with no gaps.
        Assert.Equal(mesh.Indices.Length, mesh.Groups.Sum(g => g.Count));
        Assert.Equal(3, model.Materials.Count);
    }

    [Fact]
    public void Glb_carries_face_ranges_edges_and_materials_for_cad()
    {
        var model = ModelLoader.Load(DatasetPaths.File("sldprt/nist-ftc-10-part.sldprt"));
        var glb = GlbWriter.ToArray(model);
        GlbValidation.Validate(glb);
        var json = Json(glb);

        var mesh = json.GetProperty("meshes")[0];
        Assert.Equal(206, mesh.GetProperty("extras").GetProperty("faceCount").GetInt32());
        Assert.Equal(412, mesh.GetProperty("extras").GetProperty("faceRanges").GetArrayLength());
        var primitives = mesh.GetProperty("primitives").EnumerateArray().ToList();
        Assert.Equal(3, primitives.Count(p => !p.TryGetProperty("mode", out _)));
        var edges = Assert.Single(primitives, p => p.TryGetProperty("mode", out var m) && m.GetInt32() == 1);
        Assert.Equal("edges", edges.GetProperty("extras").GetProperty("role").GetString());
        Assert.Equal(3, json.GetProperty("materials").GetArrayLength());
    }

    [Fact]
    public void Edges_can_be_turned_off_or_forced()
    {
        var cad = ModelLoader.Load(DatasetPaths.File("sldprt/servo-mount.sldprt"));
        var stl = ModelLoader.Load(DatasetPaths.File("synthetic/cube-binary.stl"));

        bool HasEdges(Model3D m, EdgeMode mode) =>
            Json(GlbWriter.ToArray(m, new GlbWriteOptions { Edges = mode })).GetProperty("meshes")[0].GetProperty("primitives").EnumerateArray()
                .Any(p => p.TryGetProperty("mode", out var x) && x.GetInt32() == 1);

        Assert.True(HasEdges(cad, EdgeMode.Auto));
        Assert.False(HasEdges(cad, EdgeMode.None));
        Assert.False(HasEdges(stl, EdgeMode.Auto));
        Assert.True(HasEdges(stl, EdgeMode.All));
    }

    [Fact]
    public void Solidworks_materials_become_pbr_with_metals_detected()
    {
        var model = ModelLoader.Load(DatasetPaths.File("sldasm/nist-mtc-assembly.sldasm"));

        Assert.Equal(4, model.Materials.Count);
        var aluminium = Assert.Single(model.Materials, m => m.Name == "satinfinishaluminum");
        Assert.Equal(0.9f, aluminium.Metallic);
        Assert.Equal(221 / 255f, aluminium.BaseColor.X, 4);
        Assert.Contains("satinfinishaluminum", model.Metadata.MaterialNames);
    }

    [Theory]
    [InlineData("sldasm/nist-mtc-assembly.sldasm")]
    [InlineData("sldprt/nist-ctc-02-part.sldprt")]
    [InlineData("synthetic/cube-ascii.stl")]
    [InlineData("synthetic/scene.obj")]
    [InlineData("synthetic/cube-binary-big.ply")]
    [InlineData("synthetic/points.ply")]
    [InlineData("synthetic/two-cubes.3mf")]
    [InlineData("synthetic/instances.gltf")]
    [InlineData("khronos/Box.glb")]
    [InlineData("glb/watering-can.glb")]
    public void Every_output_passes_the_structural_validation(string relative)
    {
        GlbValidation.Validate(ModelLoader.ConvertToGlb(File.ReadAllBytes(DatasetPaths.File(relative)), Path.GetFileName(relative)));
    }

    [Fact]
    public void Zero_length_normals_are_dropped_and_others_normalised()
    {
        var obj = "v 0 0 0\nv 1 0 0\nv 0 1 0\nvn 0 0 5\nf 1//1 2//1 3//1\n"u8.ToArray();
        var model = ModelLoader.Load(obj, "a.obj");

        Assert.Equal(1f, model.Meshes[0].Normals[2]);
        Assert.Equal(new Vector3(0, 0, 1), new Vector3(model.Meshes[0].Normals[0], model.Meshes[0].Normals[1], model.Meshes[0].Normals[2]));

        var zero = "v 0 0 0\nv 1 0 0\nv 0 1 0\nvn 0 0 0\nf 1//1 2//1 3//1\n"u8.ToArray();
        var loaded = ModelLoader.Load(zero, "a.obj");
        Assert.Empty(loaded.Meshes[0].Normals);
        Assert.NotEmpty(loaded.Warnings);
    }
}
