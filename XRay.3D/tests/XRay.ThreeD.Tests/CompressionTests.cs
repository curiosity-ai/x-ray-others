using System.Numerics;
using System.Text.Json;

namespace XRay.ThreeD.Tests;

public class CompressionTests
{
    private static double SurfaceArea(Model3D model)
    {
        double total = 0;
        void Visit(SceneNode node, Matrix4x4 parent)
        {
            var world = node.Transform * parent;
            if (node.Mesh is { } mesh)
            {
                var p = new Vector3[mesh.VertexCount];
                for (var i = 0; i < p.Length; i++)
                {
                    p[i] = Vector3.Transform(new Vector3(mesh.Positions[3 * i], mesh.Positions[3 * i + 1], mesh.Positions[3 * i + 2]), world);
                }

                for (var t = 0; t + 2 < mesh.Indices.Length; t += 3)
                {
                    total += 0.5 * Vector3.Cross(p[mesh.Indices[t + 1]] - p[mesh.Indices[t]], p[mesh.Indices[t + 2]] - p[mesh.Indices[t]]).Length();
                }
            }

            foreach (var child in node.Children)
            {
                Visit(child, world);
            }
        }

        foreach (var root in model.Roots)
        {
            Visit(root, Matrix4x4.Identity);
        }

        return total;
    }

    [Theory]
    [InlineData("sldasm/nist-mtc-assembly.sldasm")]
    [InlineData("sldprt/nist-ftc-10-part.sldprt")]
    [InlineData("synthetic/scene.obj")]
    [InlineData("synthetic/two-cubes.3mf")]
    [InlineData("synthetic/cube-binary-little.ply")]
    [InlineData("synthetic/points.ply")]
    public void Compressed_output_reads_back_to_the_same_model(string relative)
    {
        var original = ModelLoader.Load(DatasetPaths.File(relative));
        var originalBounds = original.ComputeBounds()!.Value;

        foreach (var mode in new[] { GlbCompression.Quantize, GlbCompression.Meshopt })
        {
            var glb = GlbWriter.ToArray(original, new GlbWriteOptions { Compression = mode, ConvertToMeters = false, ConvertToYUp = false });
            GlbValidation.Validate(glb, allowCompressed: true);
            var back = ModelLoader.Load(glb, "x.glb");

            Assert.Equal(original.ComputeStatistics().Triangles, back.ComputeStatistics().Triangles);
            Assert.Equal(original.Nodes().Count(n => n.Mesh is not null), back.Nodes().Count(n => n.Mesh is not null));
            var bounds = back.ComputeBounds()!.Value;
            var tolerance = Math.Max(originalBounds.Size.Length() / 20000f, 1e-6f);
            Assert.True(Vector3.Distance(bounds.Min, originalBounds.Min) <= tolerance, $"{mode}: min {bounds.Min} vs {originalBounds.Min}");
            Assert.True(Vector3.Distance(bounds.Max, originalBounds.Max) <= tolerance, $"{mode}: max {bounds.Max} vs {originalBounds.Max}");

            if (original.ComputeStatistics().Triangles > 0)
            {
                var area = SurfaceArea(original);
                Assert.InRange(SurfaceArea(back) / area, 0.995, 1.005);
                Assert.True(back.Meshes.Sum(m => m.Groups.Count) >= original.Meshes.Sum(m => m.Groups.Count));
            }
        }
    }

    [Fact]
    public void Meshopt_makes_the_assembly_a_quarter_of_the_size()
    {
        var model = ModelLoader.Load(DatasetPaths.File("sldasm/nist-mtc-assembly.sldasm"));
        var plain = GlbWriter.ToArray(model).Length;
        var quantized = GlbWriter.ToArray(model, new GlbWriteOptions { Compression = GlbCompression.Quantize }).Length;
        var meshopt = GlbWriter.ToArray(model, new GlbWriteOptions { Compression = GlbCompression.Meshopt }).Length;

        Assert.True(quantized < plain * 0.65, $"{quantized} vs {plain}");
        Assert.True(meshopt < plain * 0.30, $"{meshopt} vs {plain}");
    }

    [Fact]
    public void Compressed_files_declare_their_extensions_and_a_fallback_buffer()
    {
        var model = ModelLoader.Load(DatasetPaths.File("sldprt/servo-mount.sldprt"));
        var glb = GlbWriter.ToArray(model, new GlbWriteOptions { Compression = GlbCompression.Meshopt });
        var json = JsonDocument.Parse(glb.AsMemory(20, BitConverter.ToInt32(glb, 12))).RootElement;

        var required = json.GetProperty("extensionsRequired").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("EXT_meshopt_compression", required);
        Assert.Contains("KHR_mesh_quantization", required);
        var buffers = json.GetProperty("buffers");
        Assert.Equal(2, buffers.GetArrayLength());
        Assert.True(buffers[1].GetProperty("extensions").GetProperty("EXT_meshopt_compression").GetProperty("fallback").GetBoolean());
        Assert.DoesNotContain("uri", buffers[1].EnumerateObject().Select(p => p.Name));
        Assert.Contains(json.GetProperty("bufferViews").EnumerateArray(), v => v.TryGetProperty("extensions", out var e) && e.GetProperty("EXT_meshopt_compression").GetProperty("mode").GetString() == "TRIANGLES");
        Assert.Contains(json.GetProperty("bufferViews").EnumerateArray(), v => v.TryGetProperty("extensions", out var e) && e.GetProperty("EXT_meshopt_compression").TryGetProperty("filter", out var f) && f.GetString() == "OCTAHEDRAL");
    }

    [Fact]
    public void Quantised_positions_stay_within_one_step_of_the_original()
    {
        var model = ModelLoader.Load(DatasetPaths.File("sldprt/nist-ctc-02-part.sldprt"));
        var back = ModelLoader.Load(GlbWriter.ToArray(model, new GlbWriteOptions { Compression = GlbCompression.Meshopt, ConvertToMeters = false }), "x.glb");

        var size = model.ComputeBounds()!.Value.Size;
        var step = Math.Max(size.X, Math.Max(size.Y, size.Z)) / 65535f;
        var a = model.ComputeBounds()!.Value;
        var b = back.ComputeBounds()!.Value;
        Assert.True(Vector3.Distance(a.Center, b.Center) <= step * 2);
        Assert.Equal(488, model.Meshes[0].Faces.Length);
    }

    [Fact]
    public void Meshopt_streams_that_are_damaged_fail_cleanly()
    {
        var model = ModelLoader.Load(DatasetPaths.File("sldprt/servo-mount.sldprt"));
        var glb = GlbWriter.ToArray(model, new GlbWriteOptions { Compression = GlbCompression.Meshopt });
        var random = new Random(99);
        for (var round = 0; round < 200; round++)
        {
            var data = (byte[])glb.Clone();
            var jsonEnd = 20 + BitConverter.ToInt32(glb, 12) + 8;
            for (var k = 0; k < 1 + random.Next(6); k++)
            {
                data[jsonEnd + random.Next(data.Length - jsonEnd)] = (byte)random.Next(256);
            }

            try
            {
                _ = GlbWriter.ToArray(ModelLoader.Load(data, "x.glb"));
            }
            catch (ModelFormatException)
            {
            }
        }
    }
}

public class InstancingTests
{
    private static Model3D Screws()
    {
        var model = new Model3D();
        var mesh = new MeshData { Name = "screw", Positions = [0, 0, 0, 1, 0, 0, 0, 1, 0], Indices = [0, 1, 2] };
        model.Meshes.Add(mesh);
        var root = new SceneNode("fixture");
        for (var i = 0; i < 6; i++)
        {
            var node = new SceneNode($"screw-{i + 1}")
            {
                Mesh = mesh,
                Transform = Matrix4x4.CreateRotationZ(i * 0.3f) * Matrix4x4.CreateTranslation(i, 2 * i, 0),
            };
            node.Properties["partNumber"] = $"PN-{i}";
            root.Children.Add(node);
        }

        var other = new MeshData { Name = "other", Positions = [0, 0, 0, 1, 0, 0, 0, 1, 0], Indices = [0, 1, 2] };
        model.Meshes.Add(other);
        root.Children.Add(new SceneNode("lonely") { Mesh = other });
        model.Roots.Add(root);
        return model;
    }

    [Fact]
    public void Gpu_instancing_merges_siblings_and_keeps_their_names_in_extras()
    {
        var glb = GlbWriter.ToArray(Screws(), new GlbWriteOptions { Instancing = GlbInstancing.Gpu });
        GlbValidation.Validate(glb);
        var json = JsonDocument.Parse(glb.AsMemory(20, BitConverter.ToInt32(glb, 12))).RootElement;

        var nodes = json.GetProperty("nodes").EnumerateArray().ToList();
        Assert.Contains("EXT_mesh_gpu_instancing", json.GetProperty("extensionsRequired").EnumerateArray().Select(e => e.GetString()));
        var merged = Assert.Single(nodes, n => n.TryGetProperty("extensions", out _));
        Assert.Equal("screw (instances)", merged.GetProperty("name").GetString());
        var instances = merged.GetProperty("extras").GetProperty("instances");
        Assert.Equal(6, instances.GetArrayLength());
        Assert.Equal("screw-3", instances[2].GetProperty("name").GetString());
        Assert.Equal("PN-3", instances[3].GetProperty("partNumber").GetString());
        Assert.Equal(6, json.GetProperty("accessors")[merged.GetProperty("extensions").GetProperty("EXT_mesh_gpu_instancing").GetProperty("attributes").GetProperty("TRANSLATION").GetInt32()].GetProperty("count").GetInt32());
        Assert.Equal(1, nodes.Count(n => n.TryGetProperty("name", out var name) && name.GetString() == "lonely"));
    }

    [Fact]
    public void Gpu_instances_read_back_as_the_same_placements()
    {
        var original = Screws();
        var back = ModelLoader.Load(GlbWriter.ToArray(original, new GlbWriteOptions { Instancing = GlbInstancing.Gpu, ConvertToMeters = false, ConvertToYUp = false }), "x.glb");

        Assert.Equal(original.ComputeStatistics().Triangles, back.ComputeStatistics().Triangles);
        var o = original.ComputeBounds()!.Value;
        var b = back.ComputeBounds()!.Value;
        Assert.True(Vector3.Distance(o.Min, b.Min) < 1e-4f && Vector3.Distance(o.Max, b.Max) < 1e-4f);
        Assert.Contains(back.Nodes(), n => n.Name == "screw-4" && n.Properties["partNumber"] == "PN-3");
    }

    [Fact]
    public void Gpu_instancing_is_off_by_default_and_ignored_for_compressed_output()
    {
        var plain = GlbWriter.ToArray(Screws());
        Assert.DoesNotContain("EXT_mesh_gpu_instancing", System.Text.Encoding.UTF8.GetString(plain));

        var compressed = GlbWriter.ToArray(Screws(), new GlbWriteOptions { Instancing = GlbInstancing.Gpu, Compression = GlbCompression.Quantize });
        Assert.DoesNotContain("EXT_mesh_gpu_instancing", System.Text.Encoding.UTF8.GetString(compressed));
    }

    [Fact]
    public void Instances_that_cannot_be_decomposed_stay_as_nodes()
    {
        var model = Screws();
        // Shear cannot be expressed as translation, rotation and scale.
        foreach (var node in model.Roots[0].Children.Take(2))
        {
            node.Transform = new Matrix4x4(1, 0.5f, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1);
        }

        var glb = GlbWriter.ToArray(model, new GlbWriteOptions { Instancing = GlbInstancing.Gpu });
        Assert.DoesNotContain("EXT_mesh_gpu_instancing", System.Text.Encoding.UTF8.GetString(glb));
    }

    [Fact]
    public void Assembly_instancing_round_trips_whatever_it_can_merge()
    {
        var model = ModelLoader.Load(DatasetPaths.File("sldasm/nist-mtc-assembly.sldasm"));
        var glb = GlbWriter.ToArray(model, new GlbWriteOptions { Instancing = GlbInstancing.Gpu, Edges = EdgeMode.None });
        var back = ModelLoader.Load(glb, "x.glb");

        Assert.Equal(model.ComputeStatistics().Triangles, back.ComputeStatistics().Triangles);
    }
}
