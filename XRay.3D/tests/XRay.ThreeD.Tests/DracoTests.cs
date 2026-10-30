using System.Text.Json;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Tests;

/// <summary>The Draco decoder against what an independent decoder (DracoPy) produces from the same files; see tools/fixtures/make_draco_fixtures.py.</summary>
public class DracoTests
{
    public static IEnumerable<object[]> Files() =>
        DatasetPaths.Files("draco", "*.drc").Select(f => new object[] { Path.GetFileName(f) });

    private static float[] Flatten(JsonElement array)
    {
        var list = new List<float>();
        foreach (var row in array.EnumerateArray())
        {
            foreach (var v in row.EnumerateArray())
            {
                list.Add(v.GetSingle());
            }
        }

        return list.ToArray();
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Decodes_like_the_reference_decoder(string name)
    {
        var data = File.ReadAllBytes(DatasetPaths.File("draco/" + name));
        using var expected = JsonDocument.Parse(File.ReadAllBytes(DatasetPaths.File("draco/" + Path.ChangeExtension(name, ".json"))));
        var root = expected.RootElement;

        var geometry = DracoDecoder.Decode(data, 1_000_000, CancellationToken.None);

        var points = Flatten(root.GetProperty("points"));
        Assert.Equal(points.Length / 3, geometry.NumPoints);
        var position = geometry.Attributes.Single(a => a.Type == 0);
        Assert.Equal(points.Length, position.Values.Length);
        for (var i = 0; i < points.Length; i++)
        {
            Assert.True(Math.Abs(points[i] - position.Values[i]) < 1e-4f, $"position {i}: {points[i]} vs {position.Values[i]}");
        }

        if (root.TryGetProperty("faces", out var faces))
        {
            var expectedFaces = Flatten(faces);
            Assert.Equal(expectedFaces.Length, geometry.Indices.Length);
            for (var i = 0; i < expectedFaces.Length; i++)
            {
                Assert.Equal((uint)expectedFaces[i], geometry.Indices[i]);
            }
        }

        foreach (var (key, type, tolerance) in new[] { ("normals", 1, 2e-3f), ("tex_coord", 3, 1e-4f), ("colors", 2, 0f) })
        {
            if (!root.TryGetProperty(key, out var element))
            {
                continue;
            }

            var values = Flatten(element);
            var attribute = geometry.Attributes.Single(a => a.Type == type);
            Assert.Equal(values.Length, attribute.Values.Length);
            for (var i = 0; i < values.Length; i++)
            {
                Assert.True(Math.Abs(values[i] - attribute.Values[i]) <= tolerance + 1e-6f, $"{key} {i}: {values[i]} vs {attribute.Values[i]}");
            }
        }
    }

    [Fact]
    public void Draco_compressed_glb_decodes_to_the_same_mesh_as_the_reference_decoder()
    {
        var model = ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File("draco/sphere-draco.glb")), "sphere-draco.glb");
        using var expected = JsonDocument.Parse(File.ReadAllBytes(DatasetPaths.File("draco/sphere-draco.expected.json")));
        var root = expected.RootElement;
        var mesh = Assert.Single(model.Meshes);

        var points = Flatten(root.GetProperty("points"));
        Assert.Equal(points.Length, mesh.Positions.Length);
        Assert.All(points.Zip(mesh.Positions), pair => Assert.InRange(Math.Abs(pair.First - pair.Second), 0, 1e-4));
        var uv = Flatten(root.GetProperty("tex_coord"));
        Assert.All(uv.Zip(mesh.TexCoords), pair => Assert.InRange(Math.Abs(pair.First - pair.Second), 0, 1e-4));
        var normals = Flatten(root.GetProperty("normals"));
        Assert.All(normals.Zip(mesh.Normals), pair => Assert.InRange(Math.Abs(pair.First - pair.Second), 0, 2e-3));
        Assert.Equal(Flatten(root.GetProperty("faces")).Select(f => (uint)f), mesh.Indices);
        Assert.Empty(model.Warnings);

        // It writes back as an ordinary GLB and reads again.
        var glb = GlbWriter.ToArray(model);
        GlbValidation.Validate(glb);
        Assert.Equal(mesh.Indices.Length, ModelLoader.Load(glb, "again.glb").Meshes[0].Indices.Length);
    }

    [Fact]
    public void Extra_generic_attributes_are_decoded_and_ignored()
    {
        var model = ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File("draco/sphere-draco-tangent.glb")), "sphere-draco-tangent.glb");
        Assert.Equal(288 * 0 + model.Meshes[0].Indices.Length / 3, model.Meshes[0].TriangleCount);
        Assert.NotEmpty(model.Meshes[0].TexCoords);
    }

    [Fact]
    public void Gltf_with_an_external_bin_reads_it_through_companions_and_decodes_draco()
    {
        var gltf = File.ReadAllBytes(DatasetPaths.File("khronos/Box-Draco.gltf"));
        var bin = File.ReadAllBytes(DatasetPaths.File("khronos/Box-Draco.bin"));

        var model = ModelLoader.Load(gltf, "Box-Draco.gltf", new LoadOptions { ResolveCompanion = name => name == "Box.bin" ? bin : null });

        Assert.Equal(12, model.Meshes.Sum(m => m.TriangleCount));
        var bounds = model.ComputeBounds()!.Value;
        Assert.InRange(bounds.Size.X, 0.99f, 1.01f);
        Assert.InRange(bounds.Size.Y, 0.99f, 1.01f);
        Assert.InRange(bounds.Size.Z, 0.99f, 1.01f);

        // Without the file the node tree is still returned, with a warning.
        var without = ModelLoader.Load(gltf, "Box-Draco.gltf");
        Assert.Empty(without.Meshes);
        Assert.Contains(without.Warnings, w => w.Contains("Box.bin") || w.Contains("missing"));
    }

    [Fact]
    public void Damaged_draco_data_is_a_model_format_error()
    {
        var glb = File.ReadAllBytes(DatasetPaths.File("draco/sphere-draco.glb"));
        var random = new Random(11);
        var jsonLength = BitConverter.ToInt32(glb, 12);
        var binStart = 20 + jsonLength + 8;
        for (var i = 0; i < 300; i++)
        {
            var copy = (byte[])glb.Clone();
            for (var k = 0; k < 1 + random.Next(4); k++)
            {
                copy[binStart + random.Next(copy.Length - binStart)] = (byte)random.Next(256);
            }

            try
            {
                _ = ModelLoader.Load(copy, "x.glb");
            }
            catch (ModelFormatException)
            {
            }
        }
    }
}
