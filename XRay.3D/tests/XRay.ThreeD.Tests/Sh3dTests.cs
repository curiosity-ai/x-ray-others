using System.Numerics;

namespace XRay.ThreeD.Tests;

public class Sh3dTests
{
    /// <summary>Bounds of everything under a node, in scene coordinates.</summary>
    private static BoundingBox Bounds(Model3D model, SceneNode target)
    {
        var box = BoundingBox.Empty;
        void Visit(SceneNode node, Matrix4x4 parent, bool inside)
        {
            var world = node.Transform * parent;
            inside |= node == target;
            if (inside && node.Mesh is { } mesh)
            {
                box = box.Union(mesh.ComputeBounds(world));
            }

            foreach (var child in node.Children)
            {
                Visit(child, world, inside);
            }
        }

        foreach (var root in model.Roots)
        {
            Visit(root, Matrix4x4.Identity, false);
        }

        return box;
    }

    private static void AssertBox(BoundingBox box, Vector3 min, Vector3 max)
    {
        Assert.True(Vector3.Distance(box.Min, min) < 0.01f && Vector3.Distance(box.Max, max) < 0.01f, $"{box.Min}..{box.Max} expected {min}..{max}");
    }

    private static Model3D Home() => ModelLoader.Load(DatasetPaths.File("synthetic/home.sh3d"));

    private static SceneNode Find(Model3D model, string name) => model.Nodes().First(n => n.Name == name);

    [Fact]
    public void Synthetic_home_metadata_levels_and_counts()
    {
        var model = Home();
        var meta = model.Metadata;

        Assert.Equal("sh3d", meta.Format.Id);
        Assert.Equal("fixture-home", meta.Title);
        Assert.Equal("Sweet Home 3D 7.2", meta.Application);
        Assert.Equal("7200", meta.FormatVersion);
        Assert.Equal(LengthUnit.Centimeter, meta.Units);
        Assert.Equal(["Ground", "First floor"], meta.LayerNames);
        Assert.Equal(["Ground", "First floor", "Hall"], meta.Features.Select(f => f.Name));
        Assert.Equal(["level", "level", "room"], meta.Features.Select(f => f.Type));
        Assert.Equal("Fixture Author", meta.GetProperty("com.eteks.sweethome3d.SweetHome3D.Author"));
        Assert.Equal("2", meta.GetProperty("walls"));
        Assert.Equal("1", meta.GetProperty("rooms"));
        Assert.Equal("3", meta.GetProperty("furniture"));
        Assert.Equal(["Block", "Missing model", "Lamp"], meta.PartNames);

        var root = Assert.Single(model.Roots);
        Assert.Equal(["Ground", "First floor"], root.Children.Select(c => c.Name));
        Assert.Equal("262", root.Children[1].Properties["Elevation"]);
    }

    [Fact]
    public void Models_are_fitted_to_the_piece_and_turned_in_the_plan()
    {
        var model = Home();
        var block = Find(model, "Block");

        Assert.Equal("fixture#block", block.Properties["CatalogId"]);
        Assert.Equal("12.5", block.Properties["Price"]);
        // 60 x 20 x 40 at (100, 100), a quarter turn: the 60 runs along plan y; y is mirrored, the elevation is 10.
        AssertBox(Bounds(model, block), new Vector3(90, -130, 10), new Vector3(110, -70, 50));
        var material = model.Materials.Single(m => m.Name == "M");
        Assert.Equal(new Vector4(0.2f, 0.4f, 0.6f, 1f), material.BaseColor);
    }

    [Fact]
    public void Walls_rooms_and_groups_follow_their_level()
    {
        var model = Home();

        var wall = model.Nodes().First(n => n.Name == "Wall" && n.Properties["Id"] == "w1");
        AssertBox(Bounds(model, wall), new Vector3(0, -10, 0), new Vector3(400, 10, 250));
        Assert.Equal("#FF0000", model.Materials[wall.Mesh!.Groups[0].Material].Name);

        var upper = model.Nodes().First(n => n.Name == "Wall" && n.Properties["Id"] == "w2");
        AssertBox(Bounds(model, upper), new Vector3(-5, -300, 262), new Vector3(5, 0, 512));

        var hall = Find(model, "Hall");
        Assert.Equal("120000", hall.Properties["Area"]);
        var floor = hall.Mesh!;
        for (var t = 0; t + 2 < floor.Indices.Length; t += 3)
        {
            Vector3 P(int k) => new(floor.Positions[3 * floor.Indices[t + k]], floor.Positions[3 * floor.Indices[t + k] + 1], floor.Positions[3 * floor.Indices[t + k] + 2]);
            Assert.True(Vector3.Cross(P(1) - P(0), P(2) - P(0)).Z > 0, "floor faces up");
        }

        var group = Find(model, "Set");
        Assert.Equal(2, group.Children.Count);
        AssertBox(Bounds(model, Find(model, "Missing model")), new Vector3(175, -175, 262), new Vector3(225, -125, 312));
        AssertBox(Bounds(model, Find(model, "Lamp")), new Vector3(245, -155, 362), new Vector3(255, -145, 372));
    }

    [Fact]
    public void A_piece_without_a_readable_model_is_a_coloured_box_and_warns()
    {
        var model = Home();
        var missing = Find(model, "Missing model");

        Assert.NotNull(missing.Mesh);
        Assert.Equal(12, missing.Mesh!.TriangleCount);
        Assert.Equal("#0000FF", model.Materials[missing.Mesh.Groups[0].Material].Name);
        Assert.Contains(model.Warnings, w => w.Contains("1 piece(s)", StringComparison.Ordinal));
    }

    [Fact]
    public void Instances_share_one_mesh()
    {
        var model = Home();
        var block = Find(model, "Block");
        var lamp = Find(model, "Lamp");

        Assert.Same(block.Children.Single().Mesh, lamp.Children.Single().Mesh);
        GlbValidation.Validate(GlbWriter.ToArray(model));
    }

    [Fact]
    public void Cottage_dataset_file()
    {
        var path = DatasetPaths.File("sh3d/sweet-home-cottage.sh3d");
        var model = ModelLoader.Load(path);
        var meta = model.Metadata;

        Assert.Equal("sweet-home-cottage", meta.Title);
        Assert.Equal("5", meta.GetProperty("walls"));
        Assert.Equal("2", meta.GetProperty("rooms"));
        Assert.Equal("17", meta.GetProperty("furniture"));
        Assert.Equal(["Door", "Window", "Kitchen units", "Dining table", "Chair", "Sofa", "Bookcase", "Plant", "Double bed", "Floor lamp"], meta.PartNames);
        Assert.Contains("Living and kitchen", meta.Features.Select(f => f.Name));
        Assert.DoesNotContain(model.Warnings, w => w.Contains("boxes", StringComparison.Ordinal));

        // Four chairs, one mesh set; the dining table is 160 x 90 x 76 at (200, 250).
        var chairs = model.Nodes().Where(n => n.Name == "Chair").ToList();
        Assert.Equal(4, chairs.Count);
        Assert.Same(chairs[0].Children.Single().Mesh ?? chairs[0].Children.Single().Children.First().Mesh, chairs[3].Children.Single().Mesh ?? chairs[3].Children.Single().Children.First().Mesh);
        AssertBox(Bounds(model, Find(model, "Dining table")), new Vector3(120, -295, 0), new Vector3(280, -205, 76));
        // The bookcase is turned a quarter circle: 80 along plan y, 35 along x.
        AssertBox(Bounds(model, Find(model, "Bookcase")), new Vector3(12.5f, -490, 0), new Vector3(47.5f, -410, 200));

        var size = meta.Bounds!.Value.Size;
        Assert.True(size.X is > 900 and < 930 && size.Y is > 600 and < 630 && size.Z is > 245 and < 260, size.ToString());
        Assert.Equal(meta.Title, ModelLoader.ReadMetadata(File.ReadAllBytes(path), "x.sh3d").Title);
        GlbValidation.Validate(GlbWriter.ToArray(model));
    }

    [Fact]
    public void A_serialised_java_home_is_reported_as_unsupported()
    {
        using var output = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            using var stream = zip.CreateEntry("Home").Open();
            stream.Write([0xAC, 0xED, 0, 5]);
        }

        var ex = Assert.Throws<UnsupportedModelFormatException>(() => ModelLoader.Load(output.ToArray(), "old.sh3d"));
        Assert.Equal("sh3d", ex.Format.Id);
    }
}
