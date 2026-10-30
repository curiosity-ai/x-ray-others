using System.Numerics;

namespace XRay.ThreeD.Tests;

/// <summary>The library's own verdict on whether a mesh encloses space: welded by position, edges tallied by direction.</summary>
public class MeshTopologyTests
{
    private static MeshData Cube() => MetricsTests.Cube();

    /// <summary>The cube with every face's corners stored separately, so no two triangles of different faces share a vertex index.</summary>
    private static MeshData UnweldedCube()
    {
        var cube = Cube();
        var positions = new List<float>();
        var indices = new List<uint>();
        foreach (var index in cube.Indices)
        {
            indices.Add((uint)(positions.Count / 3));
            positions.Add(cube.Positions[3 * index]);
            positions.Add(cube.Positions[3 * index + 1]);
            positions.Add(cube.Positions[3 * index + 2]);
        }

        return new MeshData { Positions = positions.ToArray(), Indices = indices.ToArray() };
    }

    [Fact]
    public void A_cube_is_a_closed_outward_solid_whether_or_not_its_vertices_are_shared()
    {
        foreach (var mesh in new[] { Cube(), UnweldedCube() })
        {
            var topology = mesh.AnalyzeTopology();
            Assert.True(topology.IsSolid);
            Assert.Equal(0, topology.OpenEdges);
            Assert.Equal(0, topology.InconsistentEdges);
            Assert.Equal(1.0, topology.SignedVolume, 9);
        }
    }

    [Fact]
    public void A_vertex_that_is_off_by_round_off_still_welds()
    {
        var mesh = UnweldedCube();
        for (var i = 0; i < mesh.Positions.Length; i++)
        {
            mesh.Positions[i] += (i % 7 - 3) * 1e-8f;
        }

        Assert.True(mesh.AnalyzeTopology().IsClosed);
    }

    [Fact]
    public void A_sheet_has_open_edges_and_an_inside_out_solid_is_flagged()
    {
        var quad = new MeshData { Positions = [0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0], Indices = [0, 1, 2, 0, 2, 3] };
        var sheet = quad.AnalyzeTopology();
        Assert.False(sheet.IsClosed);
        Assert.Equal(4, sheet.OpenEdges);

        var inside = Cube();
        for (var t = 0; t < inside.Indices.Length; t += 3)
        {
            (inside.Indices[t + 1], inside.Indices[t + 2]) = (inside.Indices[t + 2], inside.Indices[t + 1]);
        }

        var topology = inside.AnalyzeTopology();
        Assert.True(topology.IsClosed);
        Assert.True(topology.IsInsideOut);
        Assert.False(topology.IsSolid);
    }

    [Fact]
    public void One_flipped_triangle_is_an_inconsistent_edge_and_a_hole_is_an_open_edge()
    {
        var flipped = Cube();
        (flipped.Indices[1], flipped.Indices[2]) = (flipped.Indices[2], flipped.Indices[1]);
        var a = flipped.AnalyzeTopology();
        Assert.False(a.IsClosed);
        Assert.True(a.InconsistentEdges >= 2);

        var holed = Cube();
        holed.Indices = holed.Indices.Take(holed.Indices.Length - 3).ToArray();
        var b = holed.AnalyzeTopology();
        Assert.False(b.IsClosed);
        Assert.Equal(3, b.OpenEdges);
    }

    [Fact]
    public void Two_cubes_that_touch_along_an_edge_are_closed_with_a_non_manifold_edge()
    {
        var one = Cube();
        var positions = one.Positions.ToList();
        var indices = one.Indices.ToList();
        // A second cube whose corner edge coincides with the first cube's edge (1,1,z): offset (1, 1, 0) shares the edge x=1,y=1.
        for (var i = 0; i < one.Positions.Length; i += 3)
        {
            positions.Add(one.Positions[i] + 1);
            positions.Add(one.Positions[i + 1] + 1);
            positions.Add(one.Positions[i + 2]);
        }

        indices.AddRange(one.Indices.Select(x => x + 8));
        var topology = new MeshData { Positions = positions.ToArray(), Indices = indices.ToArray() }.AnalyzeTopology();
        Assert.True(topology.IsSolid);
        Assert.True(topology.NonManifoldEdges >= 1);
        Assert.Equal(2.0, topology.SignedVolume, 9);
    }

    [Fact]
    public void Statistics_count_only_closed_meshes_in_the_volume_and_say_so()
    {
        var model = new Model3D();
        var cube = Cube();
        var sheet = new MeshData { Positions = [0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0], Indices = [0, 1, 2, 0, 2, 3] };
        model.Meshes.Add(cube);
        model.Meshes.Add(sheet);
        model.Roots.Add(new SceneNode("cube") { Mesh = cube });
        model.Roots.Add(new SceneNode("sheet") { Mesh = sheet, Transform = Matrix4x4.CreateTranslation(0, 0, 3) });
        var stats = model.ComputeStatistics();
        Assert.Equal(1.0, stats.Volume, 9);
        Assert.Equal(1, stats.ClosedMeshes);
        Assert.Equal(1, stats.OpenMeshes);
        Assert.Equal(4, stats.OpenEdges);
        Assert.False(stats.VolumeIsComplete);
        Assert.Equal(7.0, stats.SurfaceArea, 9);

        model.Roots.RemoveAt(1);
        Assert.True(model.ComputeStatistics().VolumeIsComplete);
    }

    [Fact]
    public void The_GLB_carries_the_verdict_per_mesh_and_a_two_material_solid_is_still_one_closed_mesh()
    {
        var cube = Cube();
        cube.Groups.Add(new MeshGroup(0, 18, 0));
        cube.Groups.Add(new MeshGroup(18, 18, 1));
        var model = new Model3D();
        model.Materials.Add(new ModelMaterial());
        model.Materials.Add(new ModelMaterial());
        model.Meshes.Add(cube);
        model.Roots.Add(new SceneNode("two materials") { Mesh = cube });
        foreach (var compression in new[] { GlbCompression.None, GlbCompression.Quantize, GlbCompression.Meshopt })
        {
            var glb = GlbWriter.ToArray(model, new GlbWriteOptions { Compression = compression });
            var json = ConversionPolicyTests.Json(glb);
            var mesh = json.GetProperty("meshes")[0];
            Assert.Equal(2, mesh.GetProperty("primitives").GetArrayLength());
            var extras = mesh.GetProperty("extras");
            Assert.True(extras.GetProperty("closed").GetBoolean());
            Assert.Equal(0, extras.GetProperty("openEdges").GetInt32());
            Assert.False(extras.GetProperty("insideOut").GetBoolean());
        }
    }
}
