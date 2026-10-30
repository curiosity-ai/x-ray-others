using System.Numerics;

namespace XRay.ThreeD.Tests;

public class FreecadTests
{
    [Fact]
    public void Synthetic_document_metadata_tree_and_embedded_mesh()
    {
        var model = ModelLoader.Load(DatasetPaths.File("synthetic/assembly.FCStd"));
        var meta = model.Metadata;

        Assert.Equal("freecad", meta.Format.Id);
        Assert.Equal("Fixture assembly", meta.Title);
        Assert.Equal("Fixture Author", meta.Author);
        Assert.Equal("Fixture Org", meta.Company);
        Assert.Equal("Reviewer", meta.LastModifiedBy);
        Assert.Equal(new DateTimeOffset(2025, 3, 4, 10, 20, 30, TimeSpan.Zero), meta.Created);
        Assert.Equal("FreeCAD 1.0.2R12345 (Git)", meta.Application);
        Assert.Equal(LengthUnit.Millimeter, meta.Units);

        var part = Assert.Single(model.Roots);
        Assert.Equal("Fixture assembly", part.Name);
        Assert.Equal("App::Part", part.Properties["Type"]);
        Assert.Equal(["Cube mesh", "Cylinder", "Origin"], part.Children.Select(c => c.Name));
        Assert.Equal(["Fixture assembly", "Cube mesh", "Cylinder"], meta.Features.Select(f => f.Name));
        Assert.Equal(["Cylinder"], meta.PartNames);

        // Part placement (100, 0, 0) turned 90 degrees about Z, then the mesh object's own (0, 20, 0).
        var bounds = model.ComputeBounds()!.Value;
        Assert.True(Vector3.Distance(bounds.Min, new Vector3(70, 0, 0)) < 1e-3f, bounds.Min.ToString());
        Assert.True(Vector3.Distance(bounds.Max, new Vector3(80, 10, 10)) < 1e-3f, bounds.Max.ToString());
        Assert.True(meta.HasGeometry);
        Assert.Equal(12, meta.Statistics!.Triangles);
    }

    [Fact]
    public void Brep_shapes_are_listed_colours_and_thumbnail_are_read()
    {
        var model = ModelLoader.Load(DatasetPaths.File("synthetic/assembly.FCStd"));
        var cylinder = model.Nodes().Single(n => n.Name == "Cylinder");

        Assert.Equal("#cc9e47", cylinder.Properties["Color"]);
        Assert.Contains("B-rep", cylinder.Properties["Geometry"], StringComparison.Ordinal);
        Assert.Null(cylinder.Mesh);
        Assert.Contains(model.Warnings, w => w.Contains(".brp", StringComparison.Ordinal));
        Assert.True(model.Metadata.HasPreview);
        Assert.Equal("image/png", model.Preview!.MediaType);
        Assert.Equal(0x89, model.Preview.Data[0]);
        GlbValidation.Validate(GlbWriter.ToArray(model));
    }

    [Theory]
    [InlineData("butt-hinge.FCStd", "Butt hinge", new[] { "Leaf 1", "Leaf 2", "Pin" })]
    [InlineData("swivel-caster-wheel.FCStd", null, null)]
    public void Dataset_files(string file, string? title, string[]? parts)
    {
        var path = DatasetPaths.File("fcstd/" + file);
        var model = ModelLoader.Load(path);
        var meta = model.Metadata;

        Assert.Equal("freecad", meta.Format.Id);
        Assert.StartsWith("FreeCAD 1.0", meta.Application, StringComparison.Ordinal);
        Assert.NotEmpty(meta.PartNames);
        Assert.False(meta.HasGeometry);
        Assert.Contains(model.Warnings, w => w.Contains("B-rep", StringComparison.Ordinal));
        if (title is not null)
        {
            Assert.Equal(title, meta.Title);
            Assert.Equal(parts, meta.PartNames);
        }

        // Every object is a node; the origin helpers hang under their part.
        var root = Assert.Single(model.Roots);
        Assert.Equal("App::Part", root.Properties["Type"]);
        Assert.Contains(root.Children, c => c.Properties["Type"] == "App::Origin");
        Assert.All(model.Nodes().Where(n => n.Properties["Type"] == "Part::Feature"), n => Assert.Matches("^#[0-9a-f]{6}$", n.Properties["Color"]));

        var light = ModelLoader.ReadMetadata(File.ReadAllBytes(path), file);
        Assert.Equal(meta.Title, light.Title);
        GlbValidation.Validate(GlbWriter.ToArray(model));
    }
}
