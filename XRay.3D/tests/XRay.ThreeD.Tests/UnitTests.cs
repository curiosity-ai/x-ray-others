using System.Text;

namespace XRay.ThreeD.Tests;

/// <summary>
/// What a file's length unit comes to in the GLB: the scene is scaled to metres exactly once. Each theory takes a fixture whose raw extent is known,
/// states the unit in the way the format does, and asserts the size of the converted scene in metres.
/// </summary>
public class UnitTests
{
    /// <summary>Largest side of the scene's bounding box after converting to GLB and reading the GLB back (glTF is metres).</summary>
    private static double GlbExtent(byte[] data, string name, LoadOptions? options = null)
    {
        var model = ModelLoader.Load(data, name, options);
        var glb = GlbWriter.ToArray(model);
        var size = ModelLoader.Load(glb, "x.glb").ComputeBounds()!.Value.Size;
        return Math.Max(size.X, Math.Max(size.Y, size.Z));
    }

    private static byte[] Text(string relative, params (string From, string To)[] edits)
    {
        var text = Encoding.UTF8.GetString(File.ReadAllBytes(DatasetPaths.File(relative)));
        foreach (var (from, to) in edits)
        {
            Assert.Contains(from, text);
            text = text.Replace(from, to, StringComparison.Ordinal);
        }

        return Encoding.UTF8.GetBytes(text);
    }

    private static void Near(double expected, double actual) => Assert.True(Math.Abs(expected - actual) <= 1e-4 * expected, $"expected {expected:G6} m, got {actual:G6} m");

    [Theory]
    [InlineData("0.01", 0.01)]
    [InlineData("1", 1)]
    [InlineData("0.001", 0.001)]
    [InlineData("0.0254", 0.0254)]
    [InlineData("0.5", 0.5)] // not a named unit: the scene is scaled by its root node
    public void Collada_unit_meter_scales_the_scene_once(string meter, double factor)
    {
        var baseline = GlbExtent(Text("synthetic/scene.dae"), "a.dae");
        var extent = GlbExtent(Text("synthetic/scene.dae", ("meter=\"0.01\"", $"meter=\"{meter}\"")), "a.dae");
        Near(baseline / 0.01 * factor, extent);
    }

    [Theory]
    [InlineData("100", 1.0)]
    [InlineData("1", 0.01)]
    [InlineData("0.1", 0.001)]
    [InlineData("2.54", 0.0254)]
    [InlineData("0.5", 0.005)] // UnitScaleFactor is centimetres per unit; 0.5 is no named unit
    public void Fbx_UnitScaleFactor_is_centimetres_per_unit(string unitScale, double metersPerUnit)
    {
        var baseline = GlbExtent(Text("synthetic/scene-ascii.fbx"), "a.fbx");
        var extent = GlbExtent(Text("synthetic/scene-ascii.fbx", ("\"Number\", \"\", 100", $"\"Number\", \"\", {unitScale}")), "a.fbx");
        Near(baseline * metersPerUnit, extent);
    }

    [Theory]
    [InlineData("metersPerUnit = 1", 1.0)]
    [InlineData("metersPerUnit = 0.01", 0.01)]
    [InlineData("metersPerUnit = 0.001", 0.001)]
    [InlineData("metersPerUnit = 0.0254", 0.0254)]
    [InlineData("metersPerUnit = 0.5", 0.5)]
    [InlineData("", 0.01)] // the USD schema's fallback for an unauthored metersPerUnit is centimetres
    public void Usd_metersPerUnit_scales_the_scene_once(string statement, double metersPerUnit)
    {
        var baseline = GlbExtent(Text("synthetic/scene.usda"), "a.usda");
        var extent = GlbExtent(Text("synthetic/scene.usda", ("metersPerUnit = 0.01", statement)), "a.usda");
        Near(baseline / 0.01 * metersPerUnit, extent);
    }

    [Theory]
    [InlineData("millimeter", 0.001)]
    [InlineData("inch", 0.0254)]
    [InlineData("feet", 0.3048)]
    [InlineData("meter", 1.0)]
    [InlineData("micrometer", 1e-6)]
    public void Amf_unit_attribute_is_read_for_every_unit_the_specification_names(string unit, double metersPerUnit)
    {
        var baseline = GlbExtent(Text("synthetic/cube.amf"), "a.amf");
        var extent = GlbExtent(Text("synthetic/cube.amf", ("unit=\"millimeter\"", $"unit=\"{unit}\"")), "a.amf");
        Near(baseline / 0.001 * metersPerUnit, extent);
    }

    [Fact]
    public void X3D_unit_elements_and_UNIT_statements_scale_to_metres()
    {
        var baseline = GlbExtent(Text("synthetic/scene.x3d"), "a.x3d");
        var xml = GlbExtent(Text("synthetic/scene.x3d", ("<head>", "<head><unit category=\"length\" name=\"mm\" conversionFactor=\"0.001\"/>")), "a.x3d");
        Near(baseline * 0.001, xml);
        var classicBaseline = GlbExtent(Text("synthetic/scene.x3dv"), "a.x3dv");
        var classic = GlbExtent(Text("synthetic/scene.x3dv", ("PROFILE Interchange", "PROFILE Interchange\nUNIT length mm 0.001")), "a.x3dv");
        Near(classicBaseline * 0.001, classic);
    }

    [Theory]
    [InlineData(1, 0.0254)]
    [InlineData(2, 0.001)]
    [InlineData(4, 0.3048)]
    [InlineData(6, 1.0)]
    [InlineData(7, 1000.0)]
    public void Iges_unit_flag_is_read(int flag, double metersPerUnit)
    {
        // Global section field 14 (the unit flag) and field 15 (its name) of the fixture: 2,2HMM.
        var baseline = GlbExtent(Text("synthetic/part.igs"), "a.igs");
        var extent = GlbExtent(Text("synthetic/part.igs", ("1.,2,2HMM,", $"1.,{flag},2HMM,")), "a.igs");
        Near(baseline / 0.001 * metersPerUnit, extent);
    }

    [Fact]
    public void Step_length_units_cover_prefixes_and_conversion_based_inches()
    {
        var baseline = GlbExtent(Text("synthetic/tessellated.stp"), "a.stp");
        Near(baseline * 1000, GlbExtent(Text("synthetic/tessellated.stp", ("SI_UNIT(.MILLI.,.METRE.)", "SI_UNIT($,.METRE.)")), "a.stp"));
        Near(baseline * 10, GlbExtent(Text("synthetic/tessellated.stp", ("SI_UNIT(.MILLI.,.METRE.)", "SI_UNIT(.CENTI.,.METRE.)")), "a.stp"));
        var inch = Text(
            "synthetic/tessellated.stp",
            ("#9001 = ( LENGTH_UNIT() NAMED_UNIT(*) SI_UNIT(.MILLI.,.METRE.) );", "#9001 = ( CONVERSION_BASED_UNIT('INCH',#9007) LENGTH_UNIT() NAMED_UNIT(#9008) );\n#9007 = LENGTH_MEASURE_WITH_UNIT(LENGTH_MEASURE(25.4),#9009);\n#9008 = DIMENSIONAL_EXPONENTS(1.,0.,0.,0.,0.,0.,0.);\n#9009 = ( LENGTH_UNIT() NAMED_UNIT(*) SI_UNIT(.MILLI.,.METRE.) );"));
        Near(baseline * 25.4, GlbExtent(inch, "a.stp"));
    }

    [Fact]
    public void Ifc_si_prefixes_scale_to_metres()
    {
        var baseline = GlbExtent(Text("synthetic/building.ifc"), "a.ifc");
        Near(baseline * 1000, GlbExtent(Text("synthetic/building.ifc", ("IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.)", "IFCSIUNIT(*,.LENGTHUNIT.,$,.METRE.)")), "a.ifc"));
        Near(baseline * 10, GlbExtent(Text("synthetic/building.ifc", ("IFCSIUNIT(*,.LENGTHUNIT.,.MILLI.,.METRE.)", "IFCSIUNIT(*,.LENGTHUNIT.,.CENTI.,.METRE.)")), "a.ifc"));
    }

    [Theory]
    [InlineData(1, 0.0254)]
    [InlineData(4, 0.001)]
    [InlineData(5, 0.01)]
    [InlineData(6, 1.0)]
    [InlineData(2, 0.3048)]
    public void Dxf_INSUNITS_follow_the_AutoCAD_table(int code, double metersPerUnit)
    {
        var baseline = GlbOrSvgExtent(Text("synthetic/drawing.dxf"));
        var data = Text("synthetic/drawing.dxf", ("$INSUNITS\n70\n4", $"$INSUNITS\n70\n{code}"));
        Near(baseline / 0.001 * metersPerUnit, GlbOrSvgExtent(data));
    }

    private static double GlbOrSvgExtent(byte[] dxf)
    {
        var model = ModelLoader.Load(dxf, "a.dxf");
        var size = model.ComputeBounds()!.Value.Size;
        return Math.Max(size.X, size.Y) * (model.Metadata.Units?.ToMeters() ?? 1);
    }

    [Theory]
    [InlineData(1, LengthUnit.Millimeter)]
    [InlineData(0, LengthUnit.Inch)]
    public void A_DXF_without_an_insertion_unit_takes_the_measurement_system(int measurement, LengthUnit expected)
    {
        var dxf = Encoding.UTF8.GetBytes($"0\nSECTION\n2\nHEADER\n9\n$MEASUREMENT\n70\n{measurement}\n9\n$INSUNITS\n70\n0\n0\nENDSEC\n0\nSECTION\n2\nENTITIES\n0\nLINE\n8\n0\n10\n0\n20\n0\n11\n10\n21\n0\n0\nENDSEC\n0\nEOF\n");
        Assert.Equal(expected, ModelLoader.Load(dxf, "a.dxf").Metadata.Units);
        var stated = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(dxf).Replace("$INSUNITS\n70\n0", "$INSUNITS\n70\n6", StringComparison.Ordinal));
        Assert.Equal(LengthUnit.Meter, ModelLoader.Load(stated, "a.dxf").Metadata.Units);
    }

    [Fact]
    public void AssumedUnits_fill_in_unitless_formats_and_never_override_a_stated_unit()
    {
        var stl = File.ReadAllBytes(DatasetPaths.File("synthetic/cube-ascii.stl"));
        Near(0.0254, GlbExtent(stl, "a.stl", new LoadOptions { AssumedUnits = LengthUnit.Inch }));
        Near(1.0, GlbExtent(stl, "a.stl"));

        // An old ACIS save has no unit line: the hint applies. An ACIS save that states millimetres keeps them.
        var arm = File.ReadAllBytes(DatasetPaths.File("sat/nist-arm-blank.sat"));
        Assert.Null(ModelLoader.Load(arm, "a.sat").Metadata.Units);
        Assert.Equal(LengthUnit.Inch, ModelLoader.Load(arm, "a.sat", new LoadOptions { AssumedUnits = LengthUnit.Inch }).Metadata.Units);
        var box = File.ReadAllBytes(DatasetPaths.File("synthetic/box-700.sat"));
        Assert.Equal(LengthUnit.Millimeter, ModelLoader.Load(box, "a.sat", new LoadOptions { AssumedUnits = LengthUnit.Inch }).Metadata.Units);
    }
}
