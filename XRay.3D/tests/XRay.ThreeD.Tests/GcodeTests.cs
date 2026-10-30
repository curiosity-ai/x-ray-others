using System.Numerics;
using System.Text;

namespace XRay.ThreeD.Tests;

public class GcodeTests
{
    private static Model3D Print() => ModelLoader.Load(DatasetPaths.File("synthetic/print.gcode"));

    private static MeshData Mesh(Model3D model, string name) => model.Meshes.Single(m => m.Name == name);

    [Fact]
    public void Print_header_layers_and_units()
    {
        var meta = Print().Metadata;

        Assert.Equal("gcode", meta.Format.Id);
        Assert.Equal("toolpath", meta.DocumentKind);
        Assert.Equal(LengthUnit.Millimeter, meta.Units);
        Assert.Equal(UpAxis.Z, meta.UpAxis);
        Assert.Equal("PrusaSlicer 2.7.1", meta.Application);
        Assert.Equal("3", meta.GetProperty("layers"));
        Assert.Equal("0.2", meta.GetProperty("first layer height"));
        Assert.Equal("0.2", meta.GetProperty("layer_height"));
        Assert.Equal("0.4", meta.GetProperty("nozzle_diameter"));
        Assert.Equal("PLA", meta.GetProperty("filament_type"));
        Assert.Equal("1m 2s", meta.GetProperty("estimated printing time (normal mode)"));
        Assert.Equal("12.0", meta.GetProperty("filament used [mm]"));
    }

    [Fact]
    public void Extrusion_and_travel_are_separate_line_meshes_with_bounds()
    {
        var model = Print();
        var extrusion = Mesh(model, "extrusion");
        var travel = Mesh(model, "travel");

        Assert.Empty(extrusion.Indices);
        // Layers one and two are four segments each; the third is a half circle (32 segments at 0.1 rad) and a straight line back.
        Assert.Equal(4 + 4 + 32 + 1, extrusion.EdgeIndices.Length / 2);
        var box = extrusion.ComputeBounds();
        Assert.True(Vector3.Distance(box.Min, new Vector3(10, 10, 0.2f)) < 1e-4f, box.Min.ToString());
        Assert.True(Vector3.Distance(box.Max, new Vector3(30, 30, 0.6f)) < 1e-4f, box.Max.ToString());
        Assert.NotEmpty(travel.EdgeIndices);
        Assert.NotEqual(model.Materials[extrusion.EdgeMaterial].BaseColor, model.Materials[travel.EdgeMaterial].BaseColor);
        GlbValidation.Validate(GlbWriter.ToArray(model));
    }

    [Fact]
    public void Retraction_is_not_drawn_as_extrusion()
    {
        var gcode = "G21\nG90\nM82\nG92 E0\nG1 X10 E1\nG1 E0\nG1 X20 E0\nG1 X30 E1\n";
        var model = ModelLoader.Load(Encoding.ASCII.GetBytes(gcode), "a.gcode");

        Assert.Equal(2, Mesh(model, "extrusion").EdgeIndices.Length / 2); // X0-10 and X20-30
        Assert.Equal(1, Mesh(model, "travel").EdgeIndices.Length / 2);    // X10-20 with E unchanged
    }

    [Fact]
    public void Relative_positioning_and_extrusion()
    {
        var gcode = "G21\nG91\nM83\nG1 X10 E1\nG1 Y10 E1\nG1 X-10 E1\n";
        var model = ModelLoader.Load(Encoding.ASCII.GetBytes(gcode), "a.gcode");

        var box = Mesh(model, "extrusion").ComputeBounds();
        Assert.Equal(new Vector3(0, 0, 0), box.Min);
        Assert.Equal(new Vector3(10, 10, 0), box.Max);
        Assert.Equal("relative", model.Metadata.GetProperty("positioning"));
    }

    [Fact]
    public void Cnc_profile_in_inches_with_centre_and_radius_arcs()
    {
        var model = ModelLoader.Load(DatasetPaths.File("synthetic/profile.nc"));
        var meta = model.Metadata;

        Assert.Equal(LengthUnit.Inch, meta.Units);
        Assert.Equal("2", meta.GetProperty("arcs"));
        var cut = Mesh(model, "cut");
        var rapid = Mesh(model, "rapid");
        var box = cut.ComputeBounds();
        Assert.True(Vector3.Distance(box.Min, new Vector3(0, 0, -0.1f)) < 1e-4f, box.Min.ToString());
        Assert.True(Vector3.Distance(box.Max, new Vector3(3, 3, 0.5f)) < 1e-4f, box.Max.ToString());
        // The R1 half circle from (3,3) to (1,3), clockwise, dips to y = 2: no vertex of the cut leaves the 0..3 square.
        Assert.Contains(Enumerable.Range(0, cut.VertexCount), i => Math.Abs(cut.Positions[3 * i] - 2) < 0.05 && Math.Abs(cut.Positions[3 * i + 1] - 2) < 0.01);
        Assert.NotEmpty(rapid.EdgeIndices);
    }

    [Fact]
    public void Mixed_units_are_converted_to_the_first_and_reported()
    {
        var model = ModelLoader.Load(Encoding.ASCII.GetBytes("G21\nG1 X10\nG20\nG1 X1\n"), "a.gcode");

        Assert.Equal(LengthUnit.Millimeter, model.Metadata.Units);
        Assert.Contains(model.Warnings, w => w.Contains("G20", StringComparison.Ordinal));
        Assert.Equal(25.4f, model.ComputeBounds()!.Value.Max.X, 1e-3f);
    }

    [Fact]
    public void Detection_by_content_and_extension()
    {
        var bytes = File.ReadAllBytes(DatasetPaths.File("synthetic/print.gcode"));

        Assert.Equal("gcode", ModelLoader.Detect(bytes, "x.gcode").Id);
        Assert.Equal("gcode", ModelLoader.Detect(bytes, null).Id);
        Assert.Equal("gcode", ModelLoader.Detect(bytes, "x.bin").Id);
        // A binary .nc file (NetCDF) is not G-code.
        Assert.True(ModelLoader.Detect(new byte[] { (byte)'C', (byte)'D', (byte)'F', 1, 0, 0, 0, 0, 255, 254 }, "x.nc").IsUnknown);
    }

    [Fact]
    public void Empty_and_move_free_files_are_refused()
    {
        Assert.Throws<ModelFormatException>(() => ModelLoader.Load(Encoding.ASCII.GetBytes("; nothing here\nM104 S200\n"), "a.gcode"));
        Assert.Throws<ModelFormatException>(() => ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File("synthetic/print.gcode")), "a.gcode", new LoadOptions { MaxTriangles = 5 }));
    }

    [Fact]
    public void Unevaluated_expressions_do_not_crash()
    {
        var model = ModelLoader.Load(Encoding.ASCII.GetBytes("G1 X#100 Y[1+2]\nG1 X5 Y5\n"), "a.nc");

        Assert.Contains(model.Warnings, w => w.Contains("expressions", StringComparison.Ordinal));
    }
}
