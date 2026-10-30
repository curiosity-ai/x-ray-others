using System.Numerics;

namespace XRay.ThreeD.Tests;

public class IgesTests
{
    [Fact]
    public void Synthetic_file_metadata()
    {
        var model = ModelLoader.Load(DatasetPaths.File("synthetic/part.igs"));
        var meta = model.Metadata;

        Assert.Equal("iges", meta.Format.Id);
        Assert.Equal("fixture-part", meta.Title);
        Assert.Equal(LengthUnit.Millimeter, meta.Units);
        Assert.Equal("IGES 5.3", meta.FormatVersion);
        Assert.Equal("Author Doe", meta.Author);
        Assert.Equal("Org Corp", meta.Company);
        Assert.Equal("XRay fixture / v1.0.0", meta.Application);
        Assert.Equal(new DateTimeOffset(2025, 3, 4, 10, 20, 30, TimeSpan.Zero), meta.Created);
        Assert.Equal(new DateTimeOffset(2025, 3, 5, 0, 0, 0, TimeSpan.Zero), meta.Modified);
        Assert.Contains("IGES fixture", meta.GetProperty("description"));
        Assert.Equal("3", meta.Properties.Single(p => p.Name == "110 Line").Value);
        Assert.Equal("1", meta.Properties.Single(p => p.Name == "126 Rational B-spline curve").Value);
        Assert.Equal(["0", "1", "2", "3"], meta.LayerNames);
    }

    [Fact]
    public void Curves_are_sampled_placed_and_grouped_by_level_and_colour()
    {
        var model = ModelLoader.Load(DatasetPaths.File("synthetic/part.igs"));

        Assert.Equal(4, model.Meshes.Count);
        Assert.All(model.Meshes, m => Assert.Empty(m.Indices));
        Assert.All(model.Meshes, m => Assert.NotEmpty(m.EdgeIndices));

        // The 2D parametric line (50,50)-(60,60) is not drawn; the circle spans -10..10 and the moved line reaches x = 110.
        var bounds = model.ComputeBounds()!.Value;
        Assert.True(Vector3.Distance(new Vector3(-10, -10, 0), bounds.Min) < 0.1f, bounds.Min.ToString());
        Assert.True(Vector3.Distance(new Vector3(110, 10, 5), bounds.Max) < 0.1f, bounds.Max.ToString());

        var red = model.Meshes.Single(m => model.Materials[m.EdgeMaterial].BaseColor == new Vector4(1, 0, 0, 1));
        Assert.Equal([100f, 0f, 0f, 110f, 0f, 0f], red.Positions);

        var circle = model.Meshes.Single(m => m.Name == "level 0");
        for (var i = 0; i < circle.VertexCount; i++)
        {
            var (x, y, z) = (circle.Positions[3 * i], circle.Positions[3 * i + 1], circle.Positions[3 * i + 2]);
            if (z == 5 && !(x == 5 && y == 0))
            {
                Assert.Equal(10.0, Math.Sqrt(x * x + y * y), 1e-3);
            }
        }

        var spline = model.Meshes.Single(m => m.Name == "level 3");
        Assert.True(spline.VertexCount >= 16);
        for (var i = 0; i < spline.VertexCount; i++)
        {
            var (x, y) = (spline.Positions[3 * i], spline.Positions[3 * i + 1]);
            Assert.Equal(1.0, Math.Sqrt(x * x + y * y), 1e-4);
        }

        GlbValidation.Validate(GlbWriter.ToArray(model));
    }

    [Theory]
    [InlineData("slewing-ring.iges", "Right Arm 2", 27)]
    [InlineData("jetracer-cam-mount.iges", "jetracer_cam_mount v2", 92)]
    public void Dataset_files(string file, string title, int surfaces)
    {
        var path = DatasetPaths.File("iges/" + file);
        var model = ModelLoader.Load(path);
        var meta = model.Metadata;

        Assert.Equal(title, meta.Title);
        Assert.Equal(LengthUnit.Millimeter, meta.Units);
        Assert.Equal("IGES 5.3", meta.FormatVersion);
        Assert.NotNull(meta.Created);
        Assert.Equal(surfaces.ToString(), meta.Properties.Single(p => p.Name.StartsWith("128 ", StringComparison.Ordinal)).Value);
        Assert.Equal("1", meta.Properties.Single(p => p.Name.StartsWith("186 ", StringComparison.Ordinal)).Value);
        Assert.Contains(model.Warnings, w => w.Contains("not tessellated", StringComparison.Ordinal));
        Assert.True(meta.HasGeometry);
        var size = meta.Bounds!.Value.Size;
        Assert.True(size.X > 10 && size.X < 5000 && size.Y > 5 && size.Z > 5);

        var light = ModelLoader.ReadMetadata(File.ReadAllBytes(path), file);
        Assert.Equal(title, light.Title);
        Assert.Null(light.Statistics);
        GlbValidation.Validate(ModelLoader.ConvertToGlb(File.ReadAllBytes(path), file));
    }

    [Fact]
    public void Dataset_dates()
    {
        var meta = ModelLoader.ReadMetadata(DatasetPaths.File("iges/jetracer-cam-mount.iges"));
        Assert.Equal(new DateTimeOffset(2019, 10, 16, 6, 4, 2, TimeSpan.Zero), meta.Created);
        Assert.Equal(new DateTimeOffset(2019, 10, 8, 15, 23, 51, TimeSpan.Zero), meta.Modified);
    }
}
