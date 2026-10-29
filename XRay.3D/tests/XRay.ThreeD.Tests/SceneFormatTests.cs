using System.Numerics;

namespace XRay.ThreeD.Tests;

public class SceneFormatTests
{
    private static Model3D Load(string relative) => ModelLoader.Load(DatasetPaths.File(relative));

    private static void AssertBounds(Model3D model, Vector3 min, Vector3 max, float tolerance = 1e-3f)
    {
        var b = model.ComputeBounds()!.Value;
        Assert.True(Vector3.Distance(b.Min, min) <= tolerance, $"min {b.Min} expected {min}");
        Assert.True(Vector3.Distance(b.Max, max) <= tolerance, $"max {b.Max} expected {max}");
    }

    [Fact]
    public void Off_quads_become_twelve_triangles()
    {
        var model = Load("synthetic/cube.off");
        var mesh = Assert.Single(model.Meshes);

        Assert.Equal("off", model.Metadata.Format.Id);
        Assert.Equal(8, mesh.VertexCount);
        Assert.Equal(12, mesh.TriangleCount);
        AssertBounds(model, Vector3.Zero, Vector3.One);
    }

    [Fact]
    public void Coff_carries_vertex_colours_and_face_colours()
    {
        var vertexColours = Load("synthetic/cube-colored.off").Meshes[0];
        Assert.Equal(32, vertexColours.Colors.Length);
        Assert.Equal(0f, vertexColours.Colors[0], 3);
        Assert.Equal(1f, vertexColours.Colors[1], 3);
        Assert.Equal(128 / 255f, vertexColours.Colors[2], 3);
        Assert.Equal(40 / 255f, vertexColours.Colors[4], 3);

        var faces = Load("synthetic/cube-facecolors.off");
        var mesh = faces.Meshes[0];
        Assert.Equal(2, mesh.Groups.Count);
        Assert.All(mesh.Groups, g => Assert.Equal(6, g.Count / 3));
        Assert.Equal(new Vector4(1, 0, 0, 1), faces.Materials[mesh.Groups[0].Material].BaseColor);
        Assert.Equal(new Vector4(0, 0, 1, 1), faces.Materials[mesh.Groups[1].Material].BaseColor);
    }

    [Theory]
    [InlineData("synthetic/cube.amf")]
    [InlineData("synthetic/cube-zipped.amf")]
    public void Amf_volumes_materials_and_constellation(string file)
    {
        var model = Load(file);

        Assert.Equal("amf", model.Metadata.Format.Id);
        Assert.Equal("fixture amf", model.Metadata.GetProperty("name"));
        Assert.Equal(LengthUnit.Millimeter, model.Metadata.Units);
        Assert.Equal(["Red", "Blue"], model.Metadata.MaterialNames);
        Assert.Equal(2, model.Roots.Count);
        Assert.Same(model.Roots[0].Mesh, model.Roots[1].Mesh);
        var mesh = model.Meshes[0];
        Assert.Equal(12, mesh.TriangleCount);
        Assert.Equal(2, mesh.Groups.Count);
        Assert.Equal(new Vector4(1, 0, 0, 1), model.Materials[mesh.Groups[0].Material].BaseColor);
        Assert.Equal(24, model.ComputeStatistics().Triangles);
        AssertBounds(model, Vector3.Zero, new Vector3(5, 1, 1));
    }

    [Fact]
    public void Max3ds_objects_materials_and_face_assignments()
    {
        var model = Load("synthetic/scene.3ds");

        Assert.Equal("3ds", model.Metadata.Format.Id);
        Assert.Equal(["cube", "pyramid"], model.Roots.Select(r => r.Name));
        Assert.Equal(["red", "green"], model.Metadata.MaterialNames);
        var cube = model.Roots[0].Mesh!;
        Assert.Equal(12, cube.TriangleCount);
        Assert.Equal(2, cube.Groups.Count);
        Assert.Equal(new Vector4(1, 0, 0, 1), model.Materials[cube.Groups[0].Material].BaseColor);
        Assert.Equal(new Vector4(0, 1, 0, 1), model.Materials[cube.Groups[1].Material].BaseColor);
        Assert.Equal(6, model.Roots[1].Mesh!.TriangleCount);
        AssertBounds(model, Vector3.Zero, new Vector3(7, 2, 3));
    }

    [Fact]
    public void Collada_hierarchy_transforms_materials_units_and_axis()
    {
        var model = Load("synthetic/scene.dae");
        var meta = model.Metadata;

        Assert.Equal("collada", meta.Format.Id);
        Assert.Equal(LengthUnit.Centimeter, meta.Units);
        Assert.Equal(UpAxis.Z, meta.UpAxis);
        Assert.Equal("fixture script", meta.Application);
        Assert.Equal("root", Assert.Single(model.Roots).Name);
        Assert.Equal(["cube-node", "pyr-node", "matrix-node"], model.Roots[0].Children.Select(c => c.Name));
        Assert.Equal(30, model.ComputeStatistics().Triangles);        // the cube is drawn twice
        Assert.Equal(2, model.Meshes.Count);
        Assert.Same(model.Roots[0].Children[0].Mesh, model.Roots[0].Children[2].Mesh);
        Assert.Equal(["Red", "Blue"], meta.MaterialNames);
        AssertBounds(model, new Vector3(1, 2, 3), new Vector3(11, 23, 9)); // checked against pycollada
        Assert.Equal(new Vector4(1, 0, 0, 1), model.Materials[model.Roots[0].Children[0].Mesh!.Groups[0].Material].BaseColor);
    }

    [Theory]
    [InlineData("synthetic/scene.x3d", "x3d")]
    [InlineData("synthetic/scene.wrl", "vrml")]
    public void X3d_and_vrml_build_the_same_scene(string file, string format)
    {
        var model = Load(file);

        Assert.Equal(format, model.Metadata.Format.Id);
        Assert.Equal(LengthUnit.Meter, model.Metadata.Units);
        Assert.Equal(3, model.Roots.Count);

        // DEF/USE: the second use of cubeShape is the same mesh.
        var outer = model.Roots[0];
        Assert.Equal("outer", outer.Name);
        var first = outer.Children[0].Mesh;
        var second = outer.Children[1].Children[0].Mesh;
        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Equal(12, first!.TriangleCount);

        Assert.Equal(new Vector4(1, 0, 0, 1), model.Materials[first.Groups[0].Material].BaseColor);
        var box = model.Roots[1].Mesh!;
        Assert.Equal(12, box.TriangleCount);
        Assert.Equal(0.75f, model.Materials[box.Groups[0].Material].BaseColor.W, 3);

        var lines = model.Roots[2].Mesh!;
        Assert.Empty(lines.Indices);
        Assert.Equal(3, lines.EdgeIndices.Length / 2); // 0-1, 1-2, 4-5

        AssertBounds(model, new Vector3(-1, -1, -1.5f), new Vector3(2, 7, 2));
    }

    [Fact]
    public void Vrml_one_is_refused_and_the_scenes_write_valid_glb()
    {
        Assert.Throws<UnsupportedModelFormatException>(() => ModelLoader.Load("#VRML V1.0 ascii\nSeparator { }\n"u8.ToArray(), "x.wrl"));

        foreach (var file in new[] { "synthetic/cube.off", "synthetic/cube-facecolors.off", "synthetic/cube.amf", "synthetic/scene.3ds", "synthetic/scene.dae", "synthetic/scene.x3d", "synthetic/scene.wrl" })
        {
            var glb = ModelLoader.ConvertToGlb(File.ReadAllBytes(DatasetPaths.File(file)), Path.GetFileName(file));
            GlbValidation.Validate(glb);
        }
    }
}
