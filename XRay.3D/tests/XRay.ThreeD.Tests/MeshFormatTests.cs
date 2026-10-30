using System.Numerics;

namespace XRay.ThreeD.Tests;

public class MeshFormatTests
{
    private static Model3D Load(string relative) => ModelLoader.Load(DatasetPaths.File(relative));

    [Theory]
    [InlineData("synthetic/cube-ascii.stl")]
    [InlineData("synthetic/cube-binary.stl")]
    public void Stl_cube_is_welded_to_eight_vertices(string file)
    {
        var model = Load(file);
        var mesh = Assert.Single(model.Meshes);

        Assert.Equal("stl", model.Metadata.Format.Id);
        Assert.Equal(8, mesh.VertexCount);
        Assert.Equal(12, mesh.TriangleCount);
        Assert.Equal(new Vector3(1, 1, 1), model.ComputeBounds()!.Value.Size);
    }

    [Fact]
    public void Stl_ascii_keeps_the_solid_name()
    {
        Assert.Equal("unit cube", Load("synthetic/cube-ascii.stl").Metadata.Title);
        Assert.Equal("unit cube (binary)", Load("synthetic/cube-binary.stl").Metadata.Title);
    }

    [Fact]
    public void Obj_objects_become_nodes_with_material_groups_quads_and_negative_indices()
    {
        var model = Load("synthetic/scene.obj");
        var root = Assert.Single(model.Roots);

        Assert.Equal(["plate", "wedge"], root.Children.Select(c => c.Name));
        Assert.Equal(["steel", "paint"], model.Metadata.MaterialNames);
        Assert.Equal("scene.mtl", model.Metadata.GetProperty("mtllib"));

        var plate = root.Children[0].Mesh!;
        Assert.Equal(2, plate.TriangleCount); // a quad
        Assert.Equal(4, plate.VertexCount);
        Assert.Equal(12, plate.Normals.Length);

        var wedge = root.Children[1].Mesh!;
        Assert.Equal(2, wedge.TriangleCount); // one with 1-based, one with -3 -2 -1
        Assert.Equal(2, wedge.Groups.Count);
        Assert.NotEqual(wedge.Groups[0].Material, wedge.Groups[1].Material);
    }

    [Theory]
    [InlineData("synthetic/cube-ascii.ply", "ascii 1.0")]
    [InlineData("synthetic/cube-binary-little.ply", "binary_little_endian 1.0")]
    [InlineData("synthetic/cube-binary-big.ply", "binary_big_endian 1.0")]
    public void Ply_encodings_decode_to_the_same_cube_with_colours(string file, string format)
    {
        var model = Load(file);
        var mesh = Assert.Single(model.Meshes);

        Assert.Equal(format, model.Metadata.FormatVersion);
        Assert.Equal(8, mesh.VertexCount);
        Assert.Equal(12, mesh.TriangleCount);
        Assert.Equal(32, mesh.Colors.Length);
        Assert.Equal(0f, mesh.Colors[0]);
        Assert.Equal(1f, mesh.Colors[3]);
        Assert.Equal(32 / 255f, mesh.Colors[4], 4);
        Assert.Equal(new Vector3(1, 1, 1), model.ComputeBounds()!.Value.Size);
    }

    [Fact]
    public void Ply_without_faces_is_a_point_cloud()
    {
        var model = Load("synthetic/points.ply");

        Assert.Equal("pointcloud", model.Metadata.DocumentKind);
        Assert.Equal(3, model.Meshes[0].VertexCount);
        Assert.Empty(model.Meshes[0].Indices);
    }

    [Fact]
    public void ThreeMf_components_transforms_materials_and_metadata()
    {
        var model = Load("synthetic/two-cubes.3mf");
        var meta = model.Metadata;

        Assert.Equal("Two cubes", meta.Title);
        Assert.Equal("fixture script", meta.Author);
        Assert.Equal(LengthUnit.Millimeter, meta.Units);
        Assert.Equal(["Red", "Blue"], meta.MaterialNames);

        var pair = Assert.Single(model.Roots);
        Assert.Equal("pair", pair.Name);
        Assert.Equal("PAIR-1", pair.Properties["partNumber"]);
        Assert.Equal(new Vector3(0, 5, 0), pair.Transform.Translation);
        Assert.Equal(2, pair.Children.Count);
        Assert.Equal(new Vector3(2, 0, 0), pair.Children[1].Transform.Translation);
        Assert.Equal(12, pair.Children[0].Mesh!.TriangleCount);
        Assert.Equal(new Vector4(1, 0, 0, 1), model.Materials[0].BaseColor);
        Assert.Equal(new Vector3(3, 1, 1), model.ComputeBounds()!.Value.Size);
    }

    [Fact]
    public void Gltf_nodes_instances_extras_and_materials()
    {
        var model = Load("synthetic/instances.gltf");

        var rack = Assert.Single(model.Roots);
        Assert.Equal("rack", rack.Name);
        Assert.Equal("RACK-1", rack.Properties["partNumber"]);
        Assert.Equal(["cube-left", "cube-right"], rack.Children.Select(c => c.Name));
        Assert.Same(rack.Children[0].Mesh, rack.Children[1].Mesh);
        Assert.Single(model.Meshes);
        Assert.Equal(new Vector3(3, 0, 0), rack.Children[1].Transform.Translation);
        Assert.Equal(2f, rack.Children[1].Transform.M11);
        Assert.Equal("orange", model.Materials[0].Name);
        Assert.Equal(1f, model.Materials[0].BaseColor.X, 4);
        Assert.Equal(["cube-left", "cube-right"], model.Metadata.PartNames);
        Assert.Equal("Cubes", model.Metadata.Title);
    }

    [Fact]
    public void Glb_from_a_third_party_exporter_loads()
    {
        var model = Load("khronos/Box.glb");
        var mesh = Assert.Single(model.Meshes);

        Assert.Equal(24, mesh.VertexCount);
        Assert.Equal(12, mesh.TriangleCount);
        Assert.Equal(72, mesh.Normals.Length);
        Assert.NotNull(model.Metadata.Application);
    }

    [Fact]
    public void Real_world_glb_files_load()
    {
        foreach (var file in DatasetPaths.Files("glb"))
        {
            var model = ModelLoader.Load(file);
            Assert.True(model.Metadata.HasGeometry, file);
            Assert.True(model.ComputeStatistics().Triangles > 1000, file);
        }
    }

    [Fact]
    public void Gaussian_splat_ply_loads_as_points()
    {
        var model = Load("ply/gsplat-measuring-tape.ply");

        Assert.True(model.Meshes[0].VertexCount > 1000);
        Assert.Empty(model.Meshes[0].Indices);
    }
}
