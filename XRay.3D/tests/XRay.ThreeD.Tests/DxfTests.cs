using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace XRay.ThreeD.Tests;

public class DxfTests
{
    private static readonly JsonElement Golden = JsonDocument.Parse(File.ReadAllText(DatasetPaths.CorpusFile("dxf-golden.json"))).RootElement;

    public static IEnumerable<object[]> Files() => Golden.EnumerateObject().Select(p => new object[] { p.Name });

    private static string PathOf(string file) => DatasetPaths.File((File.Exists(Path.Combine(DatasetPaths.Root, "dxf", file)) ? "dxf/" : "synthetic/") + file);

    [Theory]
    [MemberData(nameof(Files))]
    public void Matches_the_independent_reader(string file)
    {
        var expected = Golden.GetProperty(file);
        var model = ModelLoader.Load(PathOf(file));
        var meta = model.Metadata;

        Assert.StartsWith("AC10", meta.FormatVersion, StringComparison.Ordinal); // the reference reader upgrades old versions in memory, so only the family is compared
        foreach (var layer in expected.GetProperty("layers").EnumerateArray().Where(l => l.GetString() is not ("0" or "Defpoints")))
        {
            Assert.Contains(layer.GetString(), meta.LayerNames);
        }

        var units = expected.GetProperty("units").GetInt32();
        Assert.Equal(units == 4 ? LengthUnit.Millimeter : units == 6 ? LengthUnit.Meter : null, meta.Units);

        if (expected.GetProperty("entities").EnumerateObject().Any(e => e.Name is not ("TEXT" or "MTEXT")) && expected.GetProperty("extents").ValueKind == JsonValueKind.Object)
        {
            var bounds = model.ComputeBounds()!.Value;
            var min = expected.GetProperty("extents").GetProperty("min").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
            var max = expected.GetProperty("extents").GetProperty("max").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
            const float tolerance = 0.02f;

            // The reference extents include estimated text boxes, so they bound the drawn geometry from outside.
            Assert.True(bounds.Min.X >= min[0] - tolerance && bounds.Min.Y >= min[1] - tolerance, $"{file}: min {bounds.Min} vs {string.Join(',', min)}");
            Assert.True(bounds.Max.X <= max[0] + tolerance && bounds.Max.Y <= max[1] + tolerance, $"{file}: max {bounds.Max} vs {string.Join(',', max)}");
            if (expected.GetProperty("textFree").GetBoolean())
            {
                Assert.Equal(min[0], bounds.Min.X, 1);
                Assert.Equal(min[1], bounds.Min.Y, 1);
                Assert.Equal(max[0], bounds.Max.X, 1);
                Assert.Equal(max[1], bounds.Max.Y, 1);
            }
        }
    }

    [Fact]
    public void Dataset_drawings_are_two_dimensional_line_meshes_with_text_in_the_metadata()
    {
        var model = ModelLoader.Load(DatasetPaths.File("dxf/flange-drawing.dxf"));

        Assert.Equal("drawing", model.Metadata.DocumentKind);
        Assert.All(model.Meshes, m => Assert.True(m.Indices.Length == 0 || m.EdgeIndices.Length == 0));
        Assert.Contains(model.Metadata.Properties, p => p.Group == "text");
        Assert.Contains(model.Metadata.Properties, p => p.Group == "entities" && p.Name == "LINE");
        Assert.Equal("AutoCAD 2010 (AC1024)", model.Metadata.Application);
        Assert.NotNull(model.Metadata.Created);
        Assert.Equal(model.Layers.Count, model.Metadata.LayerNames.Count);
        Assert.Equal(model.Meshes.Count, model.Meshes.Count(m => m.EdgeIndices.Length > 0 || m.Indices.Length > 0 || m.VertexCount > 0));
    }

    [Fact]
    public void Layers_colours_and_visibility_come_from_the_table()
    {
        var model = ModelLoader.Load(DatasetPaths.File("synthetic/entities.dxf"));

        Assert.Equal(["L1", "L2", "HIDDEN"], model.Layers.Select(l => l.Name));
        Assert.False(model.Layers[2].Visible);
        Assert.True(model.Layers[0].Visible);
        var lineMaterials = model.Meshes.Where(m => m.EdgeIndices.Length > 0).Select(m => model.Materials[m.EdgeMaterial].Name).ToHashSet();
        Assert.Contains("#FF0000", lineMaterials); // L1 is ACI 1
        Assert.Contains("#0000FF", lineMaterials); // L2 is ACI 5
        Assert.Contains("BOX", model.Metadata.PartNames);
        Assert.Equal(LengthUnit.Meter, model.Metadata.Units);
        Assert.Contains(model.Metadata.Properties, p => p is { Group: "text", Value: "Hello DXF" });
    }

    private static Model3D Entities(string body) =>
        ModelLoader.Load(Encoding.UTF8.GetBytes("0\nSECTION\n2\nENTITIES\n" + body + "0\nENDSEC\n0\nEOF\n"), "x.dxf");

    private static string G(params object[] pairs)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < pairs.Length; i += 2)
        {
            sb.Append(((int)pairs[i]).ToString(CultureInfo.InvariantCulture)).Append('\n').Append(Convert.ToString(pairs[i + 1], CultureInfo.InvariantCulture)).Append('\n');
        }

        return sb.ToString();
    }

    private static BoundingBox Bounds(Model3D model) => model.ComputeBounds()!.Value;

    [Fact]
    public void A_bulge_of_one_is_a_half_circle_bulging_to_the_right_of_the_chord()
    {
        var model = Entities(G(0, "LWPOLYLINE", 8, "0", 90, 2, 70, 0, 10, 0.0, 20, 0.0, 42, 1.0, 10, 2.0, 20, 0.0));

        var b = Bounds(model);
        Assert.Equal(0f, b.Min.X, 3);
        Assert.Equal(2f, b.Max.X, 3);
        Assert.Equal(-1f, b.Min.Y, 3);
        Assert.Equal(0f, b.Max.Y, 3);

        var clockwise = Bounds(Entities(G(0, "LWPOLYLINE", 8, "0", 90, 2, 70, 0, 10, 0.0, 20, 0.0, 42, -1.0, 10, 2.0, 20, 0.0)));
        Assert.Equal(0f, clockwise.Min.Y, 3);
        Assert.Equal(1f, clockwise.Max.Y, 3);
    }

    [Fact]
    public void Circles_and_arcs_are_tessellated_in_the_right_quadrants()
    {
        var circle = Bounds(Entities(G(0, "CIRCLE", 8, "0", 10, 0.0, 20, 0.0, 30, 0.0, 40, 3.0)));
        Assert.Equal(-3f, circle.Min.X, 3);
        Assert.Equal(3f, circle.Max.Y, 3);

        var arc = Bounds(Entities(G(0, "ARC", 8, "0", 10, 0.0, 20, 0.0, 30, 0.0, 40, 6.0, 50, 0.0, 51, 90.0)));
        Assert.Equal(0f, arc.Min.X, 3);
        Assert.Equal(6f, arc.Max.X, 3);
        Assert.Equal(0f, arc.Min.Y, 3);
        Assert.Equal(6f, arc.Max.Y, 3);

        var wrapping = Bounds(Entities(G(0, "ARC", 8, "0", 10, 0.0, 20, 0.0, 30, 0.0, 40, 1.0, 50, 270.0, 51, 90.0)));
        Assert.Equal(1f, wrapping.Max.X, 3); // 270 to 90 degrees runs through 0
        Assert.Equal(0f, wrapping.Min.X, 1);
    }

    [Fact]
    public void Extrusion_direction_maps_the_object_coordinate_system()
    {
        var flipped = Bounds(Entities(G(0, "CIRCLE", 8, "0", 10, 5.0, 20, 0.0, 30, 0.0, 40, 1.0, 210, 0.0, 220, 0.0, 230, -1.0)));

        Assert.Equal(-5f, flipped.Center.X, 3);
    }

    [Fact]
    public void Inserts_apply_scale_rotation_translation_and_arrays()
    {
        var block = "0\nSECTION\n2\nBLOCKS\n" + G(0, "BLOCK", 8, "0", 2, "SQ", 70, 0, 10, 0.0, 20, 0.0, 30, 0.0)
            + G(0, "LINE", 8, "0", 10, 0.0, 20, 0.0, 30, 0.0, 11, 1.0, 21, 0.0, 31, 0.0)
            + G(0, "LINE", 8, "0", 10, 1.0, 20, 0.0, 30, 0.0, 11, 1.0, 21, 1.0, 31, 0.0)
            + G(0, "ENDBLK", 8, "0") + "0\nENDSEC\n";
        byte[] Make(string insert) => Encoding.UTF8.GetBytes(block + "0\nSECTION\n2\nENTITIES\n" + insert + "0\nENDSEC\n0\nEOF\n");

        var single = ModelLoader.Load(Make(G(0, "INSERT", 8, "0", 2, "SQ", 10, 10.0, 20, 0.0, 30, 0.0, 41, 2.0, 42, 2.0, 43, 1.0, 50, 90.0)), "x.dxf");
        var b = Bounds(single);
        Assert.Equal(8f, b.Min.X, 3);
        Assert.Equal(10f, b.Max.X, 3);
        Assert.Equal(0f, b.Min.Y, 3);
        Assert.Equal(2f, b.Max.Y, 3);

        var array = ModelLoader.Load(Make(G(0, "INSERT", 8, "0", 2, "SQ", 10, 0.0, 20, 0.0, 30, 0.0, 70, 3, 71, 2, 44, 5.0, 45, 5.0)), "x.dxf");
        Assert.Equal(11f, Bounds(array).Size.X, 3); // three columns 5 apart plus the block's width
        Assert.Equal(6f, Bounds(array).Size.Y, 3);
        Assert.Equal(6 * 2, array.Meshes.Sum(m => m.EdgeIndices.Length / 2)); // six copies of two lines
    }

    [Fact]
    public void Faces_polyface_meshes_and_mesh_entities_become_triangles()
    {
        var face = Entities(G(0, "3DFACE", 8, "0", 10, 0.0, 20, 0.0, 30, 0.0, 11, 1.0, 21, 0.0, 31, 0.0, 12, 1.0, 22, 1.0, 32, 1.0, 13, 0.0, 23, 1.0, 33, 1.0));
        Assert.Equal(2, face.Meshes.Sum(m => m.TriangleCount));
        Assert.Equal("model", face.Metadata.DocumentKind);

        var polyface = Entities(
            G(0, "POLYLINE", 8, "0", 66, 1, 70, 64, 71, 4, 72, 2)
            + G(0, "VERTEX", 8, "0", 10, 0.0, 20, 0.0, 30, 5.0, 70, 192)
            + G(0, "VERTEX", 8, "0", 10, 1.0, 20, 0.0, 30, 5.0, 70, 192)
            + G(0, "VERTEX", 8, "0", 10, 1.0, 20, 1.0, 30, 5.0, 70, 192)
            + G(0, "VERTEX", 8, "0", 10, 0.0, 20, 1.0, 30, 6.0, 70, 192)
            + G(0, "VERTEX", 8, "0", 10, 0.0, 20, 0.0, 30, 0.0, 70, 128, 71, 1, 72, 2, 73, 3, 74, 4)
            + G(0, "SEQEND", 8, "0"));
        Assert.Equal(2, polyface.Meshes.Sum(m => m.TriangleCount)); // one quad face
        Assert.Equal(4, polyface.Meshes.Sum(m => m.VertexCount));

        var mesh = Entities(G(0, "MESH", 8, "0", 71, 2, 72, 0, 92, 4, 10, 0.0, 20, 0.0, 30, 0.0, 10, 1.0, 20, 0.0, 30, 0.0, 10, 1.0, 20, 1.0, 30, 0.0, 10, 0.0, 20, 1.0, 30, 1.0, 93, 5, 90, 4, 90, 0, 90, 1, 90, 2, 90, 3, 94, 0));
        Assert.Equal(2, mesh.Meshes.Sum(m => m.TriangleCount));
    }

    [Fact]
    public void A_quadratic_spline_is_evaluated_not_connected_through_its_control_points()
    {
        var spline = Bounds(Entities(
            G(0, "SPLINE", 8, "0", 70, 8, 71, 2, 72, 6, 73, 3, 74, 0, 40, 0.0, 40, 0.0, 40, 0.0, 40, 1.0, 40, 1.0, 40, 1.0)
            + G(10, 0.0, 20, 30.0, 30, 0.0, 10, 5.0, 20, 40.0, 30, 0.0, 10, 10.0, 20, 30.0, 30, 0.0)));

        Assert.Equal(35f, spline.Max.Y, 1); // the apex of the Bezier curve, not the control point at 40
        Assert.Equal(10f, spline.Max.X, 2);
    }

    [Fact]
    public void Paper_space_entities_and_unknown_entities_are_reported_not_drawn()
    {
        var model = Entities(G(0, "LINE", 8, "0", 10, 0.0, 20, 0.0, 30, 0.0, 11, 1.0, 21, 0.0, 31, 0.0) + G(0, "LINE", 8, "0", 67, 1, 10, 0.0, 20, 0.0, 30, 0.0, 11, 100.0, 21, 0.0, 31, 0.0) + G(0, "3DSOLID", 8, "0"));

        Assert.Equal(1f, Bounds(model).Size.X, 3);
        Assert.Contains(model.Metadata.Notes, n => n.Contains("paper-space", StringComparison.Ordinal));
        Assert.Contains(model.Metadata.Notes, n => n.Contains("3DSOLID", StringComparison.Ordinal));
    }

    [Fact]
    public void Hatch_boundaries_are_drawn_as_outlines()
    {
        var model = Entities(G(0, "HATCH", 8, "0", 10, 0.0, 20, 0.0, 30, 0.0, 91, 1, 92, 3, 72, 0, 73, 1, 93, 4, 10, 0.0, 20, 0.0, 10, 4.0, 20, 0.0, 10, 4.0, 20, 4.0, 10, 0.0, 20, 4.0, 97, 0));

        Assert.Equal(4f, Bounds(model).Size.X, 3);
        Assert.Equal(4, model.Meshes.Sum(m => m.EdgeIndices.Length / 2)); // a closed square: four edges
    }

    [Fact]
    public void Aci_colours_follow_the_autocad_table()
    {
        Assert.Equal(0xFF0000, Formats.Cad.DxfReaderTestAccess.Aci(10));
        Assert.Equal(0xFF7F7F, Formats.Cad.DxfReaderTestAccess.Aci(11));
        Assert.Equal(0xCC0000, Formats.Cad.DxfReaderTestAccess.Aci(12));
        Assert.Equal(0x994C4C, Formats.Cad.DxfReaderTestAccess.Aci(15));
        Assert.Equal(0xFF7F00, Formats.Cad.DxfReaderTestAccess.Aci(30));
        Assert.Equal(0x00FF00, Formats.Cad.DxfReaderTestAccess.Aci(90));
        Assert.Equal(0x333333, Formats.Cad.DxfReaderTestAccess.Aci(250));
    }

    [Fact]
    public void Units_follow_the_insunits_table()
    {
        foreach (var (code, unit) in new (int, LengthUnit?)[] { (1, LengthUnit.Inch), (2, LengthUnit.Foot), (4, LengthUnit.Millimeter), (5, LengthUnit.Centimeter), (6, LengthUnit.Meter), (7, LengthUnit.Kilometer), (0, null) })
        {
            var data = Encoding.UTF8.GetBytes("0\nSECTION\n2\nHEADER\n9\n$INSUNITS\n70\n" + code + "\n0\nENDSEC\n0\nSECTION\n2\nENTITIES\n0\nLINE\n8\n0\n10\n0\n20\n0\n30\n0\n11\n1\n21\n0\n31\n0\n0\nENDSEC\n0\nEOF\n");
            Assert.Equal(unit, ModelLoader.Load(data, "x.dxf").Metadata.Units);
        }
    }

    [Fact]
    public void Output_is_valid_glb_with_line_primitives_that_read_back()
    {
        var model = ModelLoader.Load(DatasetPaths.File("dxf/flange-drawing.dxf"));
        var glb = GlbWriter.ToArray(model);
        GlbValidation.Validate(glb);
        var json = JsonDocument.Parse(glb.AsMemory(20, BitConverter.ToInt32(glb, 12))).RootElement;

        var primitives = json.GetProperty("meshes").EnumerateArray().SelectMany(m => m.GetProperty("primitives").EnumerateArray()).ToList();
        Assert.Contains(primitives, p => p.TryGetProperty("mode", out var mode) && mode.GetInt32() == 1);
        Assert.Contains(primitives, p => p.TryGetProperty("extras", out var x) && x.GetProperty("role").GetString() == "lines");

        var back = ModelLoader.Load(glb, "x.glb");
        Assert.Equal(model.Meshes.Sum(m => m.EdgeIndices.Length), back.Meshes.Sum(m => m.EdgeIndices.Length));
        Assert.NotEmpty(back.Materials);
        Assert.All(back.Meshes.Where(m => m.EdgeIndices.Length > 0), m => Assert.True(m.EdgeMaterial >= 0));
    }

    [Fact]
    public void Binary_dxf_is_unsupported_and_garbage_fails_cleanly()
    {
        var binary = Encoding.ASCII.GetBytes("AutoCAD Binary DXF\r\n\x1a\0" + new string('x', 100));
        Assert.Throws<UnsupportedModelFormatException>(() => ModelLoader.Load(binary, "x.dxf"));
        Assert.Throws<ModelFormatException>(() => ModelLoader.Load("0\nSECTION\n2\nENTITIES\nnot a code\n"u8.ToArray(), "x.dxf"));
        Assert.Throws<ModelFormatException>(() => ModelLoader.Load(Array.Empty<byte>(), "x.dxf"));
    }

    [Fact]
    public void Detected_from_content_without_an_extension()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("dxf/flange-drawing.dxf"));

        Assert.Equal("dxf", ModelLoader.Detect(data, null).Id);
    }
}
