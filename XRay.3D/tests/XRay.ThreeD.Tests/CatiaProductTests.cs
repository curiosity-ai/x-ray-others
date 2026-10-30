using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using XRay.ThreeD.Formats.Catia;

namespace XRay.ThreeD.Tests;

/// <summary>
/// CATIA V5 products: the instances (reference, name, placement) a product stores and the assembly built from them and the component files. Two MIT-licensed products are committed
/// (dataset/catia); the real assemblies with their parts (public repositories, not redistributable) are read from XRAY_3D_LOCAL_SAMPLES/catia/src and compared with
/// Corpus/catia-assemblies-golden.json (counts and bounds only).
/// </summary>
public class CatiaProductTests
{
    private static string SrcRoot => Path.Combine(Environment.GetEnvironmentVariable("XRAY_3D_LOCAL_SAMPLES") ?? "/tmp/claude-0/samples", "catia", "src");

    private static List<CatiaProduct.Instance> Instances(byte[] file)
    {
        var container = CatiaContainer.TryOpen(file, CancellationToken.None)!;
        var data = container.Read(container.Named("Data", nested: false).First(), 1 << 28)!;
        return CatiaProduct.Decode(data, CancellationToken.None);
    }

    /// <summary>Finds a component by its bare name anywhere under the root (the first one wins).</summary>
    private static Func<string, byte[]?> Finder(string root)
    {
        var index = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
            index.TryAdd(Path.GetFileName(path), path);
        }

        return name => index.TryGetValue(name, out var path) ? File.ReadAllBytes(path) : null;
    }

    [Fact]
    public void A_committed_product_stores_the_reference_name_and_placement_of_each_instance()
    {
        var instances = Instances(File.ReadAllBytes(DatasetPaths.File("catia/Product1.CATProduct")));
        Assert.Equal(["Part001.1", "Part0024.1", "Part001.2"], instances.Select(i => i.Name));
        Assert.Equal(["Part001", "Part0024", "Part001"], instances.Select(i => i.Reference));
        Assert.All(instances, i => Assert.True(i.Placed && !i.Undecoded));
        Assert.Equal(new Vector3(-5.411f, 0, 99.201f), Round(instances[0].Placement.Translation));
        Assert.Equal(Matrix4x4.Identity.M11, instances[0].Placement.M11);
        // 90 degrees about Y, then about Z: the axis vectors are the rows (the images of x, y, z), the origin the last row.
        var m = instances[1].Placement;
        Assert.Equal(new Vector3(0, 0, -1), Round(new Vector3(m.M11, m.M12, m.M13)));
        Assert.Equal(new Vector3(-1, 0, 0), Round(new Vector3(m.M21, m.M22, m.M23)));
        Assert.Equal(new Vector3(0, 1, 0), Round(new Vector3(m.M31, m.M32, m.M33)));
        Assert.Equal(new Vector3(0.038f, -255.5f, 113.496f), Round(m.Translation));
        Assert.Equal(1f, m.GetDeterminant(), 3);
        Assert.Equal(new Vector3(-5.362f, 0, 99.201f), Round(instances[2].Placement.Translation));
    }

    [Fact]
    public void A_committed_product_without_its_components_says_what_is_missing()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("catia/POWER_BOARD.CATProduct"));
        Assert.NotEmpty(Instances(data));
        Assert.Throws<UnsupportedModelFormatException>(() => ModelLoader.Load(data, "POWER_BOARD.CATProduct"));
        var options = new LoadOptions { ResolveCompanion = _ => null };
        Assert.Throws<UnsupportedModelFormatException>(() => ModelLoader.Load(data, "POWER_BOARD.CATProduct", options));
    }

    [Fact]
    public void The_stored_axes_are_rows_so_that_mating_parts_touch()
    {
        // Rod, cylinder and piston of the piston-cylinder assembly mate; with the placements read as columns the rod and the cylinder end up 45 mm apart.
        var root = Path.Combine(SrcRoot, "Guilherme115_Motor-V6", "02_Pistao_Biela");
        var path = Path.Combine(root, "piston cylinder assembly.CATProduct");
        Assert.SkipWhen(!File.Exists(path) || new FileInfo(path).Length < 1000, "local sample not present");
        var model = ModelLoader.Load(File.ReadAllBytes(path), "piston cylinder assembly.CATProduct", new LoadOptions { ResolveCompanion = Finder(Path.GetDirectoryName(root)!) });
        var top = model.Roots[0].Children;
        Assert.Equal(3, top.Count);
        var boxes = top.Select(n => WorldBox(model, n, Matrix4x4.Identity)).ToList();
        Assert.True(Overlaps(boxes[0], boxes[1]), "connecting rod and cylinder touch");
        Assert.True(Overlaps(boxes[1], boxes[2]), "cylinder and piston touch");
    }

    [Fact]
    public void Real_assemblies_match_the_golden_file()
    {
        var root = Path.Combine(SrcRoot, "Guilherme115_Motor-V6");
        Assert.SkipWhen(!Directory.Exists(root), "local samples not present");
        var golden = JsonNode.Parse(File.ReadAllText(DatasetPaths.CorpusFile("catia-assemblies-golden.json")))!.AsObject();
        var find = Finder(root);
        foreach (var (name, expected) in golden)
        {
            var path = Path.Combine(root, name);
            Assert.SkipWhen(!File.Exists(path), "local sample not present: " + name);
            var actual = Summarize(path, find);
            Assert.Equal(expected!.ToJsonString(), actual.ToJsonString());
        }
    }

    [Fact]
    public void Write_the_assemblies_golden_file_when_asked()
    {
        var output = Environment.GetEnvironmentVariable("XRAY_3D_CATIA_ASM_GOLDEN_OUT");
        Assert.SkipWhen(output is null, "set XRAY_3D_CATIA_ASM_GOLDEN_OUT to regenerate Corpus/catia-assemblies-golden.json from the local samples");
        var root = Path.Combine(SrcRoot, "Guilherme115_Motor-V6");
        var find = Finder(root);
        var result = new JsonObject();
        foreach (var path in Directory.EnumerateFiles(root, "*.CATProduct", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
            result[Path.GetRelativePath(root, path).Replace('\\', '/')] = Summarize(path, find);
        }

        File.WriteAllText(output!, result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private static JsonObject Summarize(string path, Func<string, byte[]?> find)
    {
        var bytes = File.ReadAllBytes(path);
        var instances = Instances(bytes);
        var node = new JsonObject { ["instances"] = instances.Count, ["placed"] = instances.Count(i => i.Placed), ["undecoded"] = instances.Count(i => i.Undecoded) };
        try
        {
            var model = ModelLoader.Load(bytes, Path.GetFileName(path), new LoadOptions { ResolveCompanion = find });
            var b = model.Metadata.Bounds!.Value;
            node["drawn"] = model.Roots[0].Children.Count;
            node["triangles"] = model.Metadata.Statistics!.Triangles;
            node["bounds"] = new JsonArray(new[] { b.Min.X, b.Min.Y, b.Min.Z, b.Max.X, b.Max.Y, b.Max.Z }.Select(v => (JsonNode)Math.Round(v, 2)).ToArray());
            node["warnings"] = model.Warnings.Count;
        }
        catch (UnsupportedModelFormatException)
        {
            node["drawn"] = 0;
        }

        return node;
    }

    [Fact]
    public void Part_tessellations_match_their_exports_from_the_same_model()
    {
        var root = SrcRoot;
        Assert.SkipWhen(!Directory.Exists(root), "local samples not present");
        var golden = JsonNode.Parse(File.ReadAllText(DatasetPaths.CorpusFile("catia-oracle-golden.json")))!.AsObject();
        foreach (var pair in golden["pairs"]!.AsArray())
        {
            var part = Path.Combine(root, pair!["part"]!.GetValue<string>());
            var reference = Path.Combine(root, pair["reference"]!.GetValue<string>());
            Assert.SkipWhen(!File.Exists(part) || !File.Exists(reference) || new FileInfo(part).Length < 1000, "local sample not present: " + part);
            var a = ModelLoader.Load(File.ReadAllBytes(part), Path.GetFileName(part));
            var b = ModelLoader.Load(File.ReadAllBytes(reference), Path.GetFileName(reference));
            var tolerance = pair["tolerance"]!.GetValue<double>();
            var expected = pair["volume"]!.GetValue<double>();
            Assert.Equal(expected, b.Metadata.Statistics!.Volume, expected * 1e-4);
            Assert.InRange(a.Metadata.Statistics!.Volume / expected, 1 - tolerance, 1 + tolerance);
            Assert.InRange(a.Metadata.Statistics.SurfaceArea / pair["area"]!.GetValue<double>(), 0.95, 1.05);
            var bounds = pair["bounds"]!.AsArray().Select(v => v!.GetValue<double>()).ToArray();
            var box = a.Metadata.Bounds!.Value;
            float[] actual = [box.Min.X, box.Min.Y, box.Min.Z, box.Max.X, box.Max.Y, box.Max.Z];
            for (var i = 0; i < 6; i++)
            {
                Assert.Equal(bounds[i], actual[i], 0.01);
            }
        }
    }

    private static Vector3 Round(Vector3 v) => new((float)Math.Round(v.X, 3), (float)Math.Round(v.Y, 3), (float)Math.Round(v.Z, 3));

    private static (Vector3 Min, Vector3 Max) WorldBox(Model3D model, SceneNode node, Matrix4x4 parent)
    {
        var world = node.Transform * parent;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        void Walk(SceneNode n, Matrix4x4 m)
        {
            var w = n.Transform * m;
            if (n.Mesh is { } mesh)
            {
                for (var i = 0; i + 2 < mesh.Positions.Length; i += 3)
                {
                    var p = Vector3.Transform(new Vector3(mesh.Positions[i], mesh.Positions[i + 1], mesh.Positions[i + 2]), w);
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
            }

            foreach (var c in n.Children)
            {
                Walk(c, w);
            }
        }

        foreach (var c in node.Children)
        {
            Walk(c, world);
        }

        return (min, max);
    }

    private static bool Overlaps((Vector3 Min, Vector3 Max) a, (Vector3 Min, Vector3 Max) b) =>
        a.Min.X <= b.Max.X && b.Min.X <= a.Max.X && a.Min.Y <= b.Max.Y && b.Min.Y <= a.Max.Y && a.Min.Z <= b.Max.Z && b.Min.Z <= a.Max.Z;
}
