using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;

namespace XRay.ThreeD.Tests;

public class GlbWriterTests
{
    private static (JsonElement Json, byte[] Bin) Parse(byte[] glb)
    {
        Assert.Equal(0x46546C67u, BinaryPrimitives.ReadUInt32LittleEndian(glb));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(4)));
        Assert.Equal((uint)glb.Length, BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(8)));
        var jsonLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12));
        Assert.Equal(0x4E4F534Au, BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(16)));
        Assert.Equal(0, jsonLength % 4);
        var json = JsonDocument.Parse(glb.AsMemory(20, jsonLength)).RootElement;
        var binStart = 20 + jsonLength;
        var bin = binStart < glb.Length ? glb[(binStart + 8)..] : [];
        return (json, bin);
    }

    [Fact]
    public void Assembly_instances_share_meshes_and_carry_extras()
    {
        var model = ModelLoader.Load(DatasetPaths.File("sldasm/nist-mtc-assembly.sldasm"));
        var (json, bin) = Parse(GlbWriter.ToArray(model));

        Assert.Equal(8, json.GetProperty("meshes").GetArrayLength());
        var nodes = json.GetProperty("nodes");
        Assert.Equal(model.Nodes().Count(), nodes.GetArrayLength());
        Assert.Equal(36, nodes.EnumerateArray().Count(n => n.TryGetProperty("mesh", out _)));
        Assert.Equal("nist_mtc_crada_box_rev-D-1", nodes[1].GetProperty("name").GetString());
        Assert.Equal("nist_mtc_crada_box_rev-D.SLDPRT", nodes[1].GetProperty("extras").GetProperty("file").GetString());
        Assert.Equal(16, nodes[2].GetProperty("matrix").GetArrayLength());

        var extras = json.GetProperty("asset").GetProperty("extras");
        Assert.Equal("solidworks", extras.GetProperty("format").GetString());
        Assert.Equal("assembly", extras.GetProperty("documentKind").GetString());
        Assert.Equal("meter", extras.GetProperty("sourceUnits").GetString());
        Assert.Equal(8, extras.GetProperty("componentFiles").GetArrayLength());
        Assert.Contains(extras.GetProperty("properties").EnumerateArray(), p => p.GetProperty("name").GetString() == "Part Number" && p.GetProperty("value").GetString() == "NIST-MTC-005");
        Assert.Equal(json.GetProperty("buffers")[0].GetProperty("byteLength").GetInt32(), bin.Length);
    }

    [Fact]
    public void Round_trip_keeps_the_scene()
    {
        var original = ModelLoader.Load(DatasetPaths.File("synthetic/two-cubes.3mf"));
        var glb = GlbWriter.ToArray(original);
        var back = ModelLoader.Load(glb, "x.glb");

        Assert.Equal(original.Meshes.Sum(m => m.TriangleCount), back.Meshes.Sum(m => m.TriangleCount));
        Assert.Equal(original.Nodes().Select(n => n.Name), back.Nodes().Where(n => n.Name != "root").Select(n => n.Name));
        Assert.Equal(original.ComputeStatistics().Triangles, back.ComputeStatistics().Triangles);
        var o = original.ComputeBounds()!.Value;
        var b = back.ComputeBounds()!.Value;
        // 3MF is in millimeters and Z-up; the GLB is in meters and Y-up.
        Assert.Equal(o.Size.X / 1000, b.Size.X, 5);
        Assert.Equal(o.Size.Z / 1000, b.Size.Y, 5);
        Assert.Equal(o.Size.Y / 1000, b.Size.Z, 5);
    }

    [Fact]
    public void Units_and_up_axis_can_be_left_alone()
    {
        var model = ModelLoader.Load(DatasetPaths.File("synthetic/two-cubes.3mf"));
        var (converted, _) = Parse(GlbWriter.ToArray(model));
        var (raw, _) = Parse(GlbWriter.ToArray(model, new GlbWriteOptions { ConvertToMeters = false, ConvertToYUp = false }));

        Assert.Equal("root", converted.GetProperty("nodes").EnumerateArray().Last().GetProperty("name").GetString());
        Assert.Equal(0.001, converted.GetProperty("nodes").EnumerateArray().Last().GetProperty("scale")[0].GetDouble(), 6);
        Assert.NotEqual("root", raw.GetProperty("nodes").EnumerateArray().Last().GetProperty("name").GetString());
    }

    [Fact]
    public void Material_groups_become_primitives_and_vertex_colours_are_written()
    {
        var obj = ModelLoader.Load(DatasetPaths.File("synthetic/scene.obj"));
        var (json, _) = Parse(GlbWriter.ToArray(obj));
        Assert.Equal(2, json.GetProperty("meshes")[1].GetProperty("primitives").GetArrayLength());

        var ply = ModelLoader.Load(DatasetPaths.File("synthetic/cube-binary-little.ply"));
        var (plyJson, _) = Parse(GlbWriter.ToArray(ply));
        Assert.True(plyJson.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("attributes").TryGetProperty("COLOR_0", out _));
    }

    [Fact]
    public void Point_clouds_are_written_as_points()
    {
        var (json, _) = Parse(GlbWriter.ToArray(ModelLoader.Load(DatasetPaths.File("synthetic/points.ply"))));

        Assert.Equal(0, json.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("mode").GetInt32());
    }

    [Fact]
    public void Meshes_over_65535_vertices_use_32_bit_indices()
    {
        var model = new Model3D();
        var mesh = new MeshData { Positions = new float[3 * 70_000], Indices = [0, 1, 69_999] };
        model.Meshes.Add(mesh);
        model.Roots.Add(new SceneNode("big") { Mesh = mesh });
        var (json, _) = Parse(GlbWriter.ToArray(model));

        var indexAccessor = json.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("indices").GetInt32();
        Assert.Equal(5125, json.GetProperty("accessors")[indexAccessor].GetProperty("componentType").GetInt32());

        var small = ModelLoader.Load(DatasetPaths.File("sldprt/servo-mount.sldprt"));
        var (smallJson, _) = Parse(GlbWriter.ToArray(small));
        var smallAccessor = smallJson.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("indices").GetInt32();
        Assert.Equal(5123, smallJson.GetProperty("accessors")[smallAccessor].GetProperty("componentType").GetInt32());
    }

    [Fact]
    public void Convert_to_glb_works_for_every_loadable_dataset_file()
    {
        foreach (var file in new[] { "sldasm/nist-mtc-assembly.sldasm", "sldprt/servo-mount.sldprt", "synthetic/cube-ascii.stl", "synthetic/scene.obj", "khronos/Box.glb" })
        {
            var bytes = ModelLoader.ConvertToGlb(File.ReadAllBytes(DatasetPaths.File(file)), Path.GetFileName(file));
            Assert.True(bytes.Length > 100, file);
            _ = Parse(bytes);
        }
    }

    [Fact]
    public void Empty_model_writes_a_valid_empty_scene()
    {
        var (json, bin) = Parse(GlbWriter.ToArray(new Model3D()));

        Assert.Equal(0, json.GetProperty("scenes")[0].GetProperty("nodes").GetArrayLength());
        Assert.Empty(bin);
    }
}
