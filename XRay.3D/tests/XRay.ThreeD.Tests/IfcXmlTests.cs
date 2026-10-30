namespace XRay.ThreeD.Tests;

/// <summary>
/// ifcXML against the same model written as a STEP physical file: a small hand-written pair in the dataset, and real files (a buildingSMART
/// sample with its STEP twin, a wall with an opening and a window) that are not redistributable and are read from XRAY_3D_LOCAL_SAMPLES
/// (default /tmp/claude-0/samples, sub-folder ifcxml); without them those tests skip.
/// </summary>
public class IfcXmlTests
{
    private static string? Local(string file)
    {
        var root = Environment.GetEnvironmentVariable("XRAY_3D_LOCAL_SAMPLES") ?? "/tmp/claude-0/samples";
        var path = Path.Combine(root, "ifcxml", file);
        return File.Exists(path) ? path : null;
    }

    private static void AssertSameModel(Model3D xml, Model3D step)
    {
        Assert.Equal(step.Metadata.Statistics, xml.Metadata.Statistics);
        Assert.Equal(step.ComputeBounds(), xml.ComputeBounds());
        Assert.Equal(step.Nodes().Select(n => n.Name), xml.Nodes().Select(n => n.Name));
        Assert.Equal(step.Nodes().Select(n => n.Properties.GetValueOrDefault("GlobalId")), xml.Nodes().Select(n => n.Properties.GetValueOrDefault("GlobalId")));
        Assert.Equal(step.Metadata.Units, xml.Metadata.Units);
    }

    [Fact]
    public void Hand_written_ifcxml_gives_the_model_of_its_step_twin()
    {
        var xmlBytes = File.ReadAllBytes(DatasetPaths.File("synthetic/ifcxml-wall.ifcxml"));
        Assert.Equal("ifc", ModelLoader.Detect(xmlBytes, "any.bin").Id);
        var xml = ModelLoader.Load(xmlBytes, "ifcxml-wall.ifcxml");
        var step = ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File("synthetic/ifcxml-wall.ifc")), "ifcxml-wall.ifc");

        AssertSameModel(xml, step);
        Assert.Equal(12, xml.Metadata.Statistics!.Triangles);
        // The wall: 4000 x 200 x 2800 section placed at (2000, 500, 0), its profile centred on (2000, 100): x 2000..6000, y 500..700 after placement.
        var bounds = xml.ComputeBounds()!.Value;
        Assert.Equal(2000f, bounds.Min.X, 1);
        Assert.Equal(6000f, bounds.Max.X, 1);
        Assert.Equal(2800f, bounds.Max.Z, 1);
        var wall = xml.Nodes().First(n => n.Name == "Wall A");
        Assert.Equal("EI60", wall.Properties.First(p => p.Key.EndsWith("FireRating", StringComparison.Ordinal)).Value);
        Assert.NotEmpty(GlbWriter.ToArray(xml));
    }

    [Fact]
    public void Metadata_only_read_works()
    {
        var meta = ModelLoader.ReadMetadata(File.ReadAllBytes(DatasetPaths.File("synthetic/ifcxml-wall.ifcxml")), "ifcxml-wall.ifcxml");
        Assert.Equal("ifc", meta.Format.Id);
        Assert.Contains("Wall A", meta.PartNames);
    }

    [Fact]
    public void A_real_buildingsmart_sample_matches_its_step_twin()
    {
        var xml = Local("extruded-solid.ifcxml");
        var twin = Local("extruded-solid.twin.ifc");
        if (xml is null || twin is null)
        {
            Assert.Skip("ifcXML sample not available");
            return;
        }

        AssertSameModel(ModelLoader.Load(File.ReadAllBytes(xml), "x.ifcxml"), ModelLoader.Load(File.ReadAllBytes(twin), "x.ifc"));
    }

    [Fact]
    public void A_real_wall_with_opening_and_window_loads()
    {
        var path = Local("wall-window.ifcxml");
        if (path is null)
        {
            Assert.Skip("ifcXML sample not available");
            return;
        }

        var model = ModelLoader.Load(File.ReadAllBytes(path), "wall.ifcxml");
        Assert.Contains(model.Nodes(), n => n.Name == "Wall for Test Example" && n.Mesh is not null);
        Assert.Contains(model.Nodes(), n => n.Name == "Window for Test Example" && n.Mesh is not null);
        var bounds = model.ComputeBounds()!.Value;
        Assert.Equal(3000f, bounds.Max.X, 1);
        Assert.Equal(2000f, bounds.Max.Z, 1);
    }

    [Fact]
    public void Damaged_or_hostile_xml_fails_cleanly()
    {
        Assert.Throws<ModelFormatException>(() => ModelLoader.Load("<ifcXML xmlns=\"http://www.buildingsmart-tech.org/ifcXML/IFC4/Add2\"><IfcProject"u8.ToArray(), "x.ifcxml"));
        Assert.Throws<ModelFormatException>(() => ModelLoader.Load("<!DOCTYPE x [<!ENTITY a \"b\">]><ifcXML/>"u8.ToArray(), "x.ifcxml"));
        Assert.Throws<ModelFormatException>(() => ModelLoader.Load("<ifcXML xmlns=\"http://www.buildingsmart-tech.org/ifcXML/IFC4/Add2\"/>"u8.ToArray(), "x.ifcxml"));
    }
}
