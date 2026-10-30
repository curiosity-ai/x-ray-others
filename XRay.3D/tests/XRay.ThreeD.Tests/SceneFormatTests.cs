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
    [InlineData("synthetic/scene.x3dv", "x3dv")]
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

public class FbxTests
{
    [Theory]
    [InlineData("synthetic/scene-7400.fbx")]
    [InlineData("synthetic/scene-7700.fbx")]
    [InlineData("synthetic/scene-ascii.fbx")]
    public void Hierarchy_transforms_materials_and_settings_in_every_encoding(string file)
    {
        var model = ModelLoader.Load(DatasetPaths.File(file));
        var meta = model.Metadata;

        Assert.Equal("fbx", meta.Format.Id);
        Assert.Equal(LengthUnit.Meter, meta.Units);
        Assert.Equal(UpAxis.Z, meta.UpAxis);
        Assert.Equal(["Group", "Pyramid"], model.Roots.Select(r => r.Name));
        var cube = Assert.Single(model.Roots[0].Children);
        Assert.Equal("Cube", cube.Name);
        Assert.Equal(12, cube.Mesh!.TriangleCount);
        Assert.Equal(6, model.Roots[1].Mesh!.TriangleCount);
        Assert.Equal(["Red", "Green", "Blue"], meta.MaterialNames);

        // Cube faces 0..2 use Red, 3..5 use Green; the pyramid is Blue.
        Assert.Equal(2, cube.Mesh.Groups.Count);
        Assert.Equal(new Vector4(1, 0, 0, 1), model.Materials[cube.Mesh.Groups[0].Material].BaseColor);
        Assert.Equal(new Vector4(0, 1, 0, 1), model.Materials[cube.Mesh.Groups[1].Material].BaseColor);
        Assert.Equal(new Vector4(0, 0, 1, 1), model.Materials[model.Roots[1].Mesh!.Groups[0].Material].BaseColor);

        // Per-polygon-vertex normals survive as unit vectors.
        Assert.Equal(24, cube.Mesh.VertexCount);
        Assert.Equal(72, cube.Mesh.Normals.Length);

        // Group: translate (1,0,0), rotate 90 degrees about Z, scale 2. Pyramid at (5,0,0).
        var bounds = model.ComputeBounds()!.Value;
        Assert.True(Vector3.Distance(bounds.Min, new Vector3(-1, 0, 0)) < 1e-3f, bounds.Min.ToString());
        Assert.True(Vector3.Distance(bounds.Max, new Vector3(7, 2, 3)) < 1e-3f, bounds.Max.ToString());
    }

    [Fact]
    public void Header_metadata_is_read_from_the_binary_file()
    {
        var meta = ModelLoader.ReadMetadata(DatasetPaths.File("synthetic/scene-7700.fbx"));

        Assert.Equal("fixture script", meta.Application);
        Assert.Equal("fixture fbx", meta.Title);
        Assert.Equal("nobody", meta.Author);
        Assert.Equal("7700", meta.FormatVersion);
        Assert.Equal(["Cube", "Pyramid"], meta.PartNames);
    }

    [Fact]
    public void Written_as_glb_and_detected_by_content()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/scene-7400.fbx"));

        Assert.Equal("fbx", ModelLoader.Detect(data, null).Id);
        Assert.Equal("fbx", ModelLoader.Detect(File.ReadAllBytes(DatasetPaths.File("synthetic/scene-ascii.fbx")), null).Id);
        GlbValidation.Validate(ModelLoader.ConvertToGlb(data, "x.fbx"));
    }
}

public class UsdTests
{
    private static readonly System.Text.Json.JsonElement Golden =
        System.Text.Json.JsonDocument.Parse(File.ReadAllText(DatasetPaths.CorpusFile("usd-golden.json"))).RootElement.GetProperty("scene.usda");

    [Theory]
    [InlineData("synthetic/scene.usda")]
    [InlineData("synthetic/scene.usdz")]
    public void Stage_matches_the_reference_library(string file)
    {
        var model = ModelLoader.Load(DatasetPaths.File(file));
        var meta = model.Metadata;

        Assert.Equal("usd", meta.Format.Id);
        Assert.Equal(UpAxis.Z, meta.UpAxis);
        Assert.Equal(LengthUnit.Centimeter, meta.Units);
        Assert.Equal("World", meta.Title);

        // World bounds, in the file's units, against pxr's UsdGeomBBoxCache.
        var b = model.ComputeBounds()!.Value;
        var min = Golden.GetProperty("min").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
        var max = Golden.GetProperty("max").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
        Assert.True(Vector3.Distance(b.Min, new Vector3(min[0], min[1], min[2])) < 1e-3f, $"min {b.Min} vs {string.Join(',', min)}");
        Assert.True(Vector3.Distance(b.Max, new Vector3(max[0], max[1], max[2])) < 1e-3f, $"max {b.Max} vs {string.Join(',', max)}");
    }

    [Fact]
    public void Meshes_materials_references_and_primitives()
    {
        var model = ModelLoader.Load(DatasetPaths.File("synthetic/scene.usda"));
        var world = model.Roots.First(r => r.Name == "World");

        Assert.Equal(["Cube", "Pyramid", "Ball"], world.Children.Select(c => c.Name));
        var cube = world.Children[0].Mesh!;
        Assert.Equal(12, cube.TriangleCount);
        var pyramid = world.Children[1].Mesh!;
        Assert.Equal(6, pyramid.TriangleCount);          // one quad and four triangles
        Assert.Equal(16 * 3, pyramid.Normals.Length);     // faceVarying normals: one vertex per face corner (4 + 4 x 3)
        var steel = model.Materials[pyramid.Groups[0].Material];
        Assert.Equal("Steel", steel.Name);
        Assert.Equal(1f, steel.Metallic);
        Assert.Equal(0.25f, steel.Roughness);
        Assert.Equal(0.6f, steel.BaseColor.X, 3);
        Assert.NotEmpty(world.Children[2].Mesh!.Indices);   // the sphere primitive is tessellated
        Assert.Equal("Red", model.Materials[world.Children[2].Mesh!.Groups[0].Material].Name);

        // /Copy references /World/Cube: the same mesh, at its own place.
        var copy = model.Roots.First(r => r.Name == "Copy");
        Assert.Same(cube, copy.Mesh);
        Assert.Equal(new Vector3(0, -10, 0), copy.Transform.Translation);

        // displayColor with uniform interpolation: per-face colours become vertex colours.
        Assert.Equal(cube.VertexCount * 4, cube.Colors.Length);
    }

    [Fact]
    public void Crate_matches_the_text_layer_it_was_exported_from()
    {
        var text = ModelLoader.Load(DatasetPaths.File("synthetic/scene.usda"));
        var crate = ModelLoader.Load(DatasetPaths.File("synthetic/scene.usdc"));

        Assert.Equal("usd", ModelLoader.Detect(File.ReadAllBytes(DatasetPaths.File("synthetic/scene.usdc")), "x.usdc").Id);
        Assert.Equal(text.Nodes().Select(n => n.Name), crate.Nodes().Select(n => n.Name));
        Assert.Equal(text.Metadata.UpAxis, crate.Metadata.UpAxis);
        Assert.Equal(text.Metadata.Units, crate.Metadata.Units);
        Assert.Equal(text.Materials.Select(m => m.Name), crate.Materials.Select(m => m.Name));
        var a = text.ComputeBounds()!.Value;
        var b = crate.ComputeBounds()!.Value;
        Assert.True(Vector3.Distance(a.Min, b.Min) < 1e-4f && Vector3.Distance(a.Max, b.Max) < 1e-4f);
        Assert.Equal(text.ComputeStatistics().Triangles, crate.ComputeStatistics().Triangles);
    }

    [Fact]
    public void Output_is_valid_glb()
    {
        GlbValidation.Validate(ModelLoader.ConvertToGlb(File.ReadAllBytes(DatasetPaths.File("synthetic/scene.usda")), "scene.usda"));
    }
}

/// <summary>Crate (.usdc) files with compressed arrays, matrices, quaternions, geom subsets and materials, against pxr.</summary>
public class UsdcTests
{
    private static readonly System.Text.Json.JsonElement Golden =
        System.Text.Json.JsonDocument.Parse(File.ReadAllText(DatasetPaths.CorpusFile("usdc-golden.json"))).RootElement;

    private static IEnumerable<(SceneNode Node, Matrix4x4 World)> Walk(Model3D model)
    {
        var stack = new Stack<(SceneNode, Matrix4x4)>();
        foreach (var root in model.Roots)
        {
            stack.Push((root, Matrix4x4.Identity));
        }

        while (stack.Count > 0)
        {
            var (node, parent) = stack.Pop();
            var world = node.Transform * parent;
            yield return (node, world);
            foreach (var child in node.Children)
            {
                stack.Push((child, world));
            }
        }
    }

    [Theory]
    [InlineData("synthetic/rich.usdc")]
    [InlineData("synthetic/rich.usdz")]
    public void Meshes_transforms_and_materials_match_pxr(string file)
    {
        var model = ModelLoader.Load(DatasetPaths.File(file));

        Assert.Equal(UpAxis.Y, model.Metadata.UpAxis);
        Assert.Contains("Paint", model.Materials.Select(m => m.Name));
        Assert.Contains("Glass", model.Materials.Select(m => m.Name));
        foreach (var name in new[] { "Terrain", "Plate" })
        {
            var expected = Golden.GetProperty(name);
            var (node, world) = Walk(model).Single(x => x.Node.Name == name);
            var mesh = node.Mesh!;
            Assert.Equal(expected.GetProperty("triangles").GetInt32(), mesh.TriangleCount);
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            var sum = Vector3.Zero;
            for (var i = 0; i < mesh.Positions.Length; i += 3)
            {
                var p = Vector3.Transform(new Vector3(mesh.Positions[i], mesh.Positions[i + 1], mesh.Positions[i + 2]), world);
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }

            // Welded vertices repeat per attribute, so compare the sum over the original points through the index order instead.
            var seen = new HashSet<(int, int, int)>();
            for (var i = 0; i < mesh.Positions.Length; i += 3)
            {
                var p = Vector3.Transform(new Vector3(mesh.Positions[i], mesh.Positions[i + 1], mesh.Positions[i + 2]), world);
                if (seen.Add(((int)MathF.Round(p.X * 1e4f), (int)MathF.Round(p.Y * 1e4f), (int)MathF.Round(p.Z * 1e4f))))
                {
                    sum += p;
                }
            }

            var gmin = expected.GetProperty("min").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
            var gmax = expected.GetProperty("max").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
            var gsum = expected.GetProperty("sum").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
            Assert.True(Vector3.Distance(min, new Vector3(gmin[0], gmin[1], gmin[2])) < 1e-3f, $"{name} min {min} vs {string.Join(',', gmin)}");
            Assert.True(Vector3.Distance(max, new Vector3(gmax[0], gmax[1], gmax[2])) < 1e-3f, $"{name} max {max} vs {string.Join(',', gmax)}");
            Assert.True(Vector3.Distance(sum, new Vector3(gsum[0], gsum[1], gsum[2])) < 0.05f, $"{name} sum {sum} vs {string.Join(',', gsum)}");
        }

        // Constant display colour on one mesh, material subsets on the other.
        var terrain = model.Nodes().Single(n => n.Name == "Terrain").Mesh!;
        Assert.Equal(terrain.VertexCount * 4, terrain.Colors.Length);
        Assert.Equal(0.2f, terrain.Colors[0], 3);
        Assert.Equal(0.6f, terrain.Colors[1], 3);
        var plate = model.Nodes().Single(n => n.Name == "Plate").Mesh!;
        Assert.Equal(2, plate.Groups.Count);
        Assert.Contains("Pipe", model.Nodes().Select(n => n.Name));
    }

    [Fact]
    public void Output_is_valid_glb()
    {
        GlbValidation.Validate(ModelLoader.ConvertToGlb(File.ReadAllBytes(DatasetPaths.File("synthetic/rich.usdc")), "rich.usdc"));
    }
}
