namespace XRay.ThreeD.Tests;

public class PartLayerTests
{
    [Fact]
    public void Assemblies_without_layers_get_one_synthesised_layer_per_named_part()
    {
        var model = ModelLoader.Load(DatasetPaths.File("synthetic/two-cubes.3mf"));
        var meshNodes = model.Nodes().Where(n => n.Mesh is not null).ToList();

        Assert.True(meshNodes.Count >= 2);
        Assert.NotEmpty(model.Layers);
        Assert.All(model.Layers, l => Assert.True(l.Synthesized));
        Assert.All(meshNodes, n => Assert.InRange(n.Layer, 0, model.Layers.Count - 1));
        Assert.Equal(meshNodes.Select(n => n.Name ?? n.Mesh!.Name ?? "").Where(s => s.Length > 0).Distinct().Count(), model.Layers.Count(l => !l.Name.StartsWith("Part ", StringComparison.Ordinal)));
    }

    [Fact]
    public void Instances_of_one_part_share_its_layer_and_the_option_turns_it_off()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/instances.gltf"));
        var on = ModelLoader.Load(data, "instances.gltf");
        var off = ModelLoader.Load(data, "instances.gltf", new LoadOptions { SynthesizePartLayers = false });

        Assert.Empty(off.Layers);
        var nodes = on.Nodes().Where(n => n.Mesh is not null).ToList();
        foreach (var group in nodes.GroupBy(n => n.Name ?? n.Mesh!.Name))
        {
            Assert.Single(group.Select(n => n.Layer).Distinct());
        }
    }

    [Fact]
    public void Layers_defined_by_the_file_are_left_alone()
    {
        var model = ModelLoader.Load(DatasetPaths.File("synthetic/entities.dxf"));

        Assert.NotEmpty(model.Layers);
        Assert.All(model.Layers, l => Assert.False(l.Synthesized));
    }

    [Fact]
    public void Meshopt_codecs_honour_cancellation()
    {
        var indices = Enumerable.Range(0, 3 * 5000).Select(i => (uint)(i / 3 + i % 3)).ToArray();
        var token = TestContext.Current.CancellationToken;
        var encoded = Internal.MeshoptEncoder.EncodeTriangles(indices, token);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Equal(indices.Length, Internal.MeshoptDecoder.DecodeTriangles(encoded, indices.Length, token).Length);
        Assert.ThrowsAny<OperationCanceledException>(() => Internal.MeshoptDecoder.DecodeTriangles(encoded, indices.Length, cts.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => Internal.MeshoptEncoder.EncodeTriangles(indices, cts.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => Internal.MeshoptEncoder.EncodeIndexSequence(indices, cts.Token));
        var vertices = new byte[4000 * 12];
        Assert.ThrowsAny<OperationCanceledException>(() => Internal.MeshoptEncoder.EncodeVertices(vertices, 4000, 12, cts.Token));
        var packed = Internal.MeshoptEncoder.EncodeVertices(vertices, 4000, 12, token);
        Assert.Equal(vertices, Internal.MeshoptDecoder.DecodeVertices(packed, 4000, 12, token));
        Assert.ThrowsAny<OperationCanceledException>(() => Internal.MeshoptDecoder.DecodeVertices(packed, 4000, 12, cts.Token));
    }
}
