using System.Numerics;
using System.Text.Json;

namespace XRay.ThreeD.Tests;

/// <summary>Rhino 3DM against rhino3dm (the openNURBS bindings): layers, materials, objects, meshes, curves, blocks and units.</summary>
public class RhinoTests
{
    private static JsonElement Golden(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(DatasetPaths.CorpusFile("rhino-golden.json")));
        return doc.RootElement.GetProperty(file).Clone();
    }

    private static Model3D Load(string relative, out string file)
    {
        file = Path.GetFileName(relative);
        return ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File(relative)), file);
    }

    private static LengthUnit? Units(string text) => text switch
    {
        "UnitSystem.Meters" => LengthUnit.Meter,
        "UnitSystem.Millimeters" => LengthUnit.Millimeter,
        "UnitSystem.Inches" => LengthUnit.Inch,
        _ => null,
    };

    public static IEnumerable<object[]> Files() =>
    [
        ["3dm/terrain-contours.3dm"],
        ["synthetic/rhino-scene.3dm"],
        ["synthetic/rhino-bigmesh.3dm"],
        ["synthetic/rhino-layers.3dm"],
    ];

    [Theory]
    [MemberData(nameof(Files))]
    public void Units_layers_and_properties_match_rhino3dm(string relative)
    {
        var model = Load(relative, out var file);
        var golden = Golden(file);
        var meta = model.Metadata;

        Assert.Equal("rhino", meta.Format.Id);
        Assert.Equal(Units(golden.GetProperty("units").GetString()!), meta.Units);
        Assert.Equal(UpAxis.Z, meta.UpAxis);
        Assert.Equal(golden.GetProperty("absoluteTolerance").GetDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture), meta.GetProperty("Absolute tolerance"));
        var createdBy = golden.GetProperty("createdBy").GetString();
        Assert.Equal(string.IsNullOrEmpty(createdBy) ? null : createdBy, meta.Author);

        var layers = golden.GetProperty("layers").EnumerateArray().ToList();
        Assert.Equal(layers.Count, model.Layers.Count);
        var byId = layers.ToDictionary(l => l.GetProperty("id").GetString()!, l => l);
        for (var i = 0; i < layers.Count; i++)
        {
            var expected = layers[i];
            var path = expected.GetProperty("name").GetString()!;
            var visible = expected.GetProperty("visible").GetBoolean();
            for (var parent = expected.GetProperty("parent").GetString()!; byId.TryGetValue(parent, out var p); parent = p.GetProperty("parent").GetString()!)
            {
                path = p.GetProperty("name").GetString() + "::" + path;
                visible &= p.GetProperty("visible").GetBoolean();
            }

            Assert.Equal(path, model.Layers[i].Name);
            Assert.Equal(visible, model.Layers[i].Visible);
        }

        Assert.Equal(model.Layers.Select(l => l.Name), meta.LayerNames);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Objects_match_rhino3dm_in_order_with_exact_counts_and_extents(string relative)
    {
        var model = Load(relative, out var file);
        var golden = Golden(file);
        var objects = golden.GetProperty("objects").EnumerateArray()
            .Where(o => o.GetProperty("mode").GetString() != "ObjectMode.InstanceDefinitionObject" && o.GetProperty("visible").GetBoolean() && o.GetProperty("mode").GetString() != "ObjectMode.Hidden")
            .ToList();
        // Kinds that have no geometry here (nurbs curves are sampled, so they are checked against analytic extents below).
        Assert.Equal(objects.Count, model.Roots.Count);
        for (var i = 0; i < objects.Count; i++)
        {
            var expected = objects[i];
            var node = model.Roots[i];
            var kind = expected.GetProperty("kind").GetString();
            var name = expected.GetProperty("name").GetString();
            Assert.Equal(string.IsNullOrEmpty(name) ? (kind == "InstanceReference" ? "Block1" : null) : name, node.Name);
            Assert.Equal(expected.GetProperty("layer").GetInt32(), node.Layer);
            if (kind is "Mesh")
            {
                Assert.Equal(expected.GetProperty("vertices").GetInt32(), node.Mesh!.VertexCount);
                Assert.Equal(expected.GetProperty("triangles").GetInt32(), node.Mesh.TriangleCount);
                Assert.Equal(expected.GetProperty("colors").GetInt32() > 0, node.Mesh.Colors.Length > 0);
                Assert.Equal(expected.GetProperty("normals").GetInt32() > 0, node.Mesh.Normals.Length > 0);
            }

            if (kind is "Mesh" or "PolylineCurve" or "LineCurve" or "Point" or "PointCloud")
            {
                Assert.Equal(expected.GetProperty("box").EnumerateArray().Select(v => v.GetDouble()).ToArray(), BoxOf(node.Mesh!), new BoxComparer());
                if (kind is "PolylineCurve" or "LineCurve")
                {
                    Assert.Equal(expected.GetProperty("points").GetInt32(), node.Mesh!.VertexCount);
                    Assert.Equal(2 * (node.Mesh.VertexCount - 1), node.Mesh.EdgeIndices.Length);
                }
            }

            if (kind == "InstanceReference")
            {
                var m = expected.GetProperty("xform").EnumerateArray().Select(v => (float)v.GetDouble()).ToArray();
                var t = node.Transform;
                // The file's matrix acts on column vectors; the node's transform is its transpose.
                Assert.Equal(m[0], t.M11, 4);
                Assert.Equal(m[1], t.M21, 4);
                Assert.Equal(m[3], t.M41, 4);
                Assert.Equal(m[4], t.M12, 4);
                Assert.Equal(m[5], t.M22, 4);
                Assert.Equal(m[7], t.M42, 4);
                Assert.Equal(2, node.Children.Count);
            }
        }
    }

    private sealed class BoxComparer : IEqualityComparer<double[]>
    {
        public bool Equals(double[]? x, double[]? y) => x is not null && y is not null && x.Length == y.Length && x.Zip(y).All(p => Math.Abs(p.First - p.Second) < 2e-3);

        public int GetHashCode(double[] obj) => obj.Length;
    }

    private static double[] BoxOf(MeshData mesh)
    {
        var b = mesh.ComputeBounds();
        return [b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z];
    }

    [Fact]
    public void Terrain_contours_keep_their_layer_colours_and_the_terrain_mesh_its_extent()
    {
        var model = Load("3dm/terrain-contours.3dm", out _);
        Assert.Equal(["Contours 25 m", "Contours 5 m", "Terrain surface", "Site boundary"], model.Metadata.LayerNames);
        Assert.Equal(4, model.Materials.Count);
        // Layer colours as the file states them (checked with rhino3dm above): contours brown, terrain green.
        Assert.Contains(model.Materials, m => m.BaseColor == new Vector4(120 / 255f, 60 / 255f, 20 / 255f, 1));
        Assert.Contains(model.Materials, m => m.BaseColor == new Vector4(150 / 255f, 175 / 255f, 120 / 255f, 1));
        var bounds = model.Metadata.Bounds!.Value;
        Assert.Equal(400, bounds.Max.X, 3);
        Assert.Equal(59.95, bounds.Max.Z, 3);
        Assert.Equal(-0.05, bounds.Min.Z, 3);
        var terrain = model.Roots.Single(n => n.Mesh!.TriangleCount > 0).Mesh!;
        Assert.Equal(16384, terrain.VertexCount);
        Assert.Equal(32258, terrain.TriangleCount);
        Assert.Equal(16384 * 3, terrain.Normals.Length);
        _ = GlbWriter.ToArray(model);
    }

    [Fact]
    public void Scene_curves_are_sampled_materials_and_user_strings_are_read()
    {
        var model = Load("synthetic/rhino-scene.3dm", out var file);
        var golden = Golden(file);
        var circle = model.Roots.Single(n => n.Name == "circle").Mesh!.ComputeBounds();
        Assert.Equal(-1, circle.Min.X, 2);
        Assert.Equal(-1, circle.Min.Y, 2);
        Assert.Equal(3, circle.Max.X, 2);
        Assert.Equal(3, circle.Max.Y, 2);
        var arc = model.Roots.Single(n => n.Name == "arc").Mesh!.ComputeBounds();
        Assert.Equal(1, arc.Max.X, 2);
        Assert.Equal(Math.Sin(1.5), arc.Max.Y, 2);
        var curve = model.Roots.Single(n => n.Name == "nurbs").Mesh!.ComputeBounds();
        Assert.Equal(0, curve.Min.X, 3);
        Assert.Equal(3, curve.Max.X, 3);
        Assert.Equal(1.5, curve.Max.Y, 2); // a cubic Bezier with control heights 0, 2, 2, 0 peaks at 1.5

        var steel = golden.GetProperty("materials")[0];
        Assert.Equal("Steel", steel.GetProperty("name").GetString());
        Assert.Contains("Steel", model.Metadata.MaterialNames);
        var material = model.Materials.Single(m => m.Name == "Steel");
        Assert.Equal(200 / 255f, material.BaseColor.X, 3);
        Assert.Equal(100 / 255f, material.BaseColor.Y, 3);
        Assert.Equal(50 / 255f, material.BaseColor.Z, 3);
        Assert.Equal(1 - steel.GetProperty("transparency").GetDouble(), material.BaseColor.W, 3);
        var mesh = model.Roots.Single(n => n.Name == "mesh");
        Assert.Equal("A-100", mesh.Properties["part-number"]);
        Assert.Equal("two strings", mesh.Properties["note"]);

        // The polyline has its own colour, the cloud takes its layer's.
        var polyline = model.Roots.Single(n => n.Name == "poly").Mesh!;
        Assert.Equal(new Vector4(10 / 255f, 20 / 255f, 30 / 255f, 1), model.Materials[polyline.EdgeMaterial].BaseColor);
    }

    [Fact]
    public void Blocks_become_shared_meshes_under_the_instance_transforms()
    {
        var model = Load("synthetic/rhino-scene.3dm", out _);
        Assert.Equal(["Block1"], model.Metadata.PartNames);
        var one = model.Roots.Single(n => n.Name == "ref1");
        var two = model.Roots.Single(n => n.Name == "ref2");
        Assert.Same(one.Children[0].Mesh, two.Children[0].Mesh);
        Assert.Same(one.Children[1].Mesh, two.Children[1].Mesh);
        Assert.Equal(10, one.Transform.M41, 4);
        // ref2 is a quarter turn about z followed by a move to (20, 5): the block's x axis points along +y.
        var moved = Vector3.Transform(new Vector3(2, 0, 0), two.Transform);
        Assert.Equal(20, moved.X, 3);
        Assert.Equal(7, moved.Y, 3);
        // Members are not also drawn at the top level.
        Assert.DoesNotContain(model.Roots, n => n.Name is "bm" or "bl");
        var bounds = model.Metadata.Bounds!.Value;
        // The block's line ends at (3, 3, 3): moved to (13, 3, 3) by ref1 and, quarter-turned, to (17, 8, 3) by ref2.
        Assert.Equal(20, bounds.Max.X, 3);
        Assert.Equal(8, bounds.Max.Y, 3);
        Assert.Equal(3, bounds.Max.Z, 3);
    }

    [Fact]
    public void Hidden_objects_and_hidden_layers_are_reported_not_dropped_silently()
    {
        var model = Load("synthetic/rhino-layers.3dm", out _);
        Assert.Equal(4, model.Roots.Count);
        Assert.Contains(model.Warnings, w => w.Contains("hidden", StringComparison.Ordinal));
        Assert.False(model.Layers.Single(l => l.Name == "Off").Visible);
        Assert.False(model.Layers.Single(l => l.Name == "Off::Child of off").Visible);
        Assert.True(model.Layers.Single(l => l.Name == "Locked").Visible);
    }

    [Fact]
    public void A_mesh_beyond_16_bit_indices_with_deflated_buffers_and_colours()
    {
        var model = Load("synthetic/rhino-bigmesh.3dm", out _);
        var mesh = model.Roots.Single().Mesh!;
        Assert.Equal(257 * 257, mesh.VertexCount);
        Assert.Equal(2 * 256 * 256, mesh.TriangleCount);
        Assert.Equal(mesh.VertexCount * 4, mesh.Colors.Length);
        Assert.Equal(1 / 255f * 0, mesh.Colors[0]);
        Assert.Equal(128 / 255f, mesh.Colors[2], 3);
        Assert.Equal(1 / 255f, mesh.Colors[4], 3);
    }

    [Fact]
    public void Old_archives_and_other_damage_are_refused_with_a_reason()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/rhino-layers.3dm"));
        var old = (byte[])data.Clone();
        "       4"u8.CopyTo(old.AsSpan(24));
        var ex = Assert.Throws<UnsupportedModelFormatException>(() => ModelLoader.Load(old, "old.3dm"));
        Assert.Contains("version 4", ex.Message, StringComparison.Ordinal);
        Assert.Throws<ModelFormatException>(() => ModelLoader.Load(data[..40], "cut.3dm"));
    }
}

/// <summary>
/// Real Rhino 7 and 8 files kept outside the repository (their licence is not ours to redistribute): set XRAY_3D_LOCAL_SAMPLES, or leave
/// the default /tmp/claude-0/samples, and put them under rhino/. The expectations are counts and extents read with rhino3dm.
/// </summary>
public class RhinoLocalSampleTests
{
    private static string? Sample(string name)
    {
        var root = Environment.GetEnvironmentVariable("XRAY_3D_LOCAL_SAMPLES") ?? "/tmp/claude-0/samples";
        var path = Path.Combine(root, "rhino", name);
        return File.Exists(path) ? path : null;
    }

    public static IEnumerable<object[]> Names()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(DatasetPaths.CorpusFile("rhino-local-golden.json")));
        return doc.RootElement.EnumerateObject().Select(p => new object[] { p.Name }).ToList();
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Real_files_load_with_the_geometry_rhino3dm_reports(string name)
    {
        var path = Sample(name);
        if (path is null)
        {
            Assert.Skip("local sample not present: " + name);
            return;
        }

        using var doc = JsonDocument.Parse(File.ReadAllBytes(DatasetPaths.CorpusFile("rhino-local-golden.json")));
        var golden = doc.RootElement.GetProperty(name);
        var model = ModelLoader.Load(File.ReadAllBytes(path), name);
        Assert.Equal(Units(golden.GetProperty("units").GetString()!) ?? LengthUnit.Millimeter, model.Metadata.Units);
        Assert.Equal(golden.GetProperty("layers").GetArrayLength(), model.Layers.Count);

        var objects = golden.GetProperty("objects").EnumerateArray()
            .Where(o => o.GetProperty("mode").GetString() != "ObjectMode.InstanceDefinitionObject").ToList();
        long triangles = 0;
        var direct = 0;
        foreach (var o in objects)
        {
            switch (o.GetProperty("kind").GetString())
            {
                case "Mesh":
                    triangles += o.GetProperty("triangles").GetInt64();
                    direct++;
                    break;
                case "Brep" when o.GetProperty("renderTriangles").GetInt64() > 0:
                    triangles += o.GetProperty("renderTriangles").GetInt64();
                    direct++;
                    break;
                case "Point" or "PointCloud" or "PolylineCurve" or "LineCurve" or "ArcCurve" or "NurbsCurve":
                    direct++;
                    break;
            }
        }

        // Instances add nodes for the members of their block; direct objects are one root each.
        Assert.True(model.Roots.Count >= direct, $"{model.Roots.Count} roots, expected at least {direct}");
        Assert.Equal(triangles, model.Meshes.Where(m => m.Indices.Length > 0).Sum(m => (long)m.TriangleCount));
        _ = GlbWriter.ToArray(model);
    }

    private static LengthUnit? Units(string text) => text switch
    {
        "UnitSystem.Meters" => LengthUnit.Meter,
        "UnitSystem.Millimeters" => LengthUnit.Millimeter,
        "UnitSystem.Inches" => LengthUnit.Inch,
        _ => null,
    };

    [Fact]
    public void Arcs_and_curves_of_a_real_file_match_the_extents_rhino3dm_reports()
    {
        var path = Sample("file3dm_stuff.3dm");
        if (path is null)
        {
            Assert.Skip("local sample not present");
            return;
        }

        using var doc = JsonDocument.Parse(File.ReadAllBytes(DatasetPaths.CorpusFile("rhino-local-golden.json")));
        var arc = doc.RootElement.GetProperty("file3dm_stuff.3dm").GetProperty("objects").EnumerateArray().First(o => o.GetProperty("kind").GetString() == "ArcCurve");
        var expected = arc.GetProperty("box").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        var model = ModelLoader.Load(File.ReadAllBytes(path), "file3dm_stuff.3dm");
        // The circle is sampled as a polygon: its extents sit inside the exact ones within the sagitta of one segment.
        var found = model.Roots.Where(n => n.Mesh is { EdgeIndices.Length: > 0, VertexCount: > 8 }).Select(n => n.Mesh!.ComputeBounds())
            .Any(b => Math.Abs(b.Min.X - expected[0]) < 0.3 && Math.Abs(b.Max.X - expected[3]) < 0.3 && Math.Abs(b.Min.Y - expected[1]) < 0.3 && Math.Abs(b.Max.Y - expected[4]) < 0.3);
        Assert.True(found);
    }
}
