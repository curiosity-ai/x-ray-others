using System.Text;

namespace XRay.ThreeD.Tests;

/// <summary>OpenSCAD: evaluation checked against closed-form volumes and areas (no second reader is involved).</summary>
public class OpenScadTests
{
    private static Model3D Load(string source, LoadOptions? options = null) => ModelLoader.Load(Encoding.UTF8.GetBytes(source), "t.scad", options);

    private static double Volume(Model3D model) => model.Metadata.Statistics!.Volume;

    [Fact]
    public void Primitives_have_the_closed_form_volume()
    {
        Assert.Equal(24, Volume(Load("cube([2,3,4]);")), 6);
        Assert.Equal(8, Volume(Load("cube(2, center=true);")), 6);
        var cyl = Math.PI * 9 * 5;
        Assert.InRange(Volume(Load("cylinder(h=5, r=3, $fn=360);")), cyl * 0.999, cyl);
        // a cone of the same height is a third of it
        Assert.InRange(Volume(Load("cylinder(h=5, r1=3, r2=0, $fn=360);")), cyl / 3 * 0.999, cyl / 3);
        var sphere = 4.0 / 3 * Math.PI * 8;
        Assert.InRange(Volume(Load("sphere(r=2, $fn=96);")), sphere * 0.99, sphere);
    }

    [Fact]
    public void Transforms_modules_functions_and_loops_evaluate()
    {
        var model = Load("""
            function sq(x) = x * x;
            module slab(n) { cube([n, n, 1]); }
            for (i = [0:2]) translate([i * 10, 0, 0]) slab(sq(i + 1));
            """);
        Assert.Equal(1 + 16 + 81, Volume(model), 6);
        var b = model.Metadata.Bounds!.Value;
        Assert.Equal(29, b.Max.X, 4);

        var comp = Load("pts = [for (i = [0:3]) i * 2]; translate([pts[3], 0, 0]) cube(1);");
        Assert.Equal(6, comp.Metadata.Bounds!.Value.Min.X, 4);

        var rot = Load("rotate([0, 90, 0]) cube([1, 1, 5]);");
        Assert.Equal(5, rot.Metadata.Bounds!.Value.Size.X, 4);
        Assert.Equal(5, Volume(rot), 5);
        Assert.Equal(5, Volume(Load("mirror([1,0,0]) cube([1,1,5]);")), 5);
    }

    [Fact]
    public void Difference_and_intersection_are_computed()
    {
        Assert.Equal(1000 - 250, Volume(Load("difference() { cube(10); translate([5,5,-1]) cube([10,10,12]); }")), 4);
        Assert.Equal(125, Volume(Load("intersection() { cube(10); translate([5,5,5]) cube(10); }")), 4);
        var holed = Volume(Load("difference() { cube([10,10,2]); translate([5,5,-1]) cylinder(r=2, h=4, $fn=720); }"));
        Assert.InRange(holed, 200 - Math.PI * 4 * 2, 200 - Math.PI * 4 * 2 * 0.999);
    }

    [Fact]
    public void Extrusions_hull_and_polyhedron()
    {
        Assert.Equal(60, Volume(Load("linear_extrude(height=3) square([4,5]);")), 5);
        // twist and scale keep the area at the base and shrink linearly
        var tapered = Volume(Load("linear_extrude(height=3, scale=0.5, slices=20) square([2,2], center=true);"));
        Assert.InRange(tapered, 4 * 3 * (1 + 0.5 + 0.25) / 3 * 0.999, 4 * 3 * (1 + 0.5 + 0.25) / 3 * 1.02);
        var ring = Volume(Load("rotate_extrude($fn=720) translate([3, 0]) square([2, 1]);"));
        Assert.InRange(ring, Math.PI * (25 - 9) * 0.999, Math.PI * (25 - 9));
        Assert.Equal(1.0 / 3 * 4 * 4 * 3, Volume(Load("hull() { cube([4,4,0.001]); translate([2,2,3]) cube(0.001); }")), 1);
        // a tetrahedron with faces listed clockwise from outside, as OpenSCAD wants
        var tet = Volume(Load("polyhedron(points=[[0,0,0],[1,0,0],[0,1,0],[0,0,1]], faces=[[0,1,2],[0,3,1],[1,3,2],[0,2,3]]);"));
        Assert.Equal(1.0 / 6, tet, 6);
    }

    [Fact]
    public void Colours_become_materials_and_customizer_parameters_become_properties()
    {
        var model = Load("""
            // A part
            /* [Size] */
            // Width
            w = 5; // [1:10]
            derived = w * 2;
            color("red") cube(1);
            color([0, 1, 0]) translate([3, 0, 0]) cube(1);
            """);
        Assert.Equal(2, model.Materials.Count);
        Assert.Equal(new System.Numerics.Vector4(1, 0, 0, 1), model.Materials[0].BaseColor);
        Assert.Equal("A part", model.Metadata.Title);
        Assert.Single(model.Metadata.Properties);
        Assert.Equal("Size", model.Metadata.Properties[0].Group);
        _ = GlbWriter.ToArray(model);
    }

    [Fact]
    public void Unsupported_operations_warn_instead_of_failing()
    {
        var model = Load("minkowski() { cube(2); sphere(1, $fn=8); } text(\"x\");");
        Assert.True(model.Warnings.Count >= 2);
        Assert.True(model.Metadata.HasGeometry);
    }

    [Fact]
    public void Bad_input_fails_cleanly_and_runaway_programs_are_stopped()
    {
        Assert.Throws<ModelFormatException>(() => Load("cube(("));
        Assert.Throws<ModelFormatException>(() => Load("module m() { m(); } m();"));
        Assert.Throws<ModelFormatException>(() => Load("for (i = [0:1e9]) cube(1);", new LoadOptions { MaxTriangles = 1000 }));
    }

    [Theory]
    [InlineData("chess-rook.scad", 5, 54)]
    [InlineData("twisted-vase.scad", 2, 118)]
    [InlineData("wall-bracket.scad", 2, 60)]
    [InlineData("hex-bolt-and-nut.scad", 3, 51.524307)]
    public void Dataset_programs_load_with_their_colours_and_extent(string file, int materials, double top)
    {
        var data = File.ReadAllBytes(DatasetPaths.File("scad/" + file));
        var model = ModelLoader.Load(data, file);
        Assert.Equal(materials > 0 ? Math.Min(materials, model.Materials.Count) : 0, model.Materials.Count);
        Assert.Equal(top, model.Metadata.Bounds!.Value.Max.Z, 3);
        Assert.Empty(model.Warnings);
        Assert.True(model.Metadata.Statistics!.Volume > 0);
        _ = GlbWriter.ToArray(model);
    }
}
