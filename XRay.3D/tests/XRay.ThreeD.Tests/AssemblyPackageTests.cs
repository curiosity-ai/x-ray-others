using System.Text.Json;

namespace XRay.ThreeD.Tests;

public class AssemblyPackageTests
{
    private static Model3D Assembly() => ModelLoader.Load(DatasetPaths.File("sldasm/nist-mtc-assembly.sldasm"));

    [Fact]
    public void One_file_per_unique_part_and_a_small_manifest()
    {
        var package = AssemblyPackage.Create(Assembly());

        Assert.Equal(8, package.Files.Keys.Count(k => k.StartsWith("parts/", StringComparison.Ordinal)));
        Assert.Contains(AssemblyPackage.ManifestName, package.Files.Keys);
        Assert.True(package.Files[AssemblyPackage.ManifestName].Length < 40_000);

        using var manifest = JsonDocument.Parse(package.Manifest);
        var root = manifest.RootElement;
        Assert.Equal(AssemblyPackage.Schema, root.GetProperty("schema").GetString());
        Assert.Equal(8, root.GetProperty("parts").GetArrayLength());
        Assert.Equal(37, root.GetProperty("nodes").GetArrayLength());
        Assert.Equal("meter", root.GetProperty("extras").GetProperty("sourceUnits").GetString());
        var firstScrew = root.GetProperty("nodes").EnumerateArray().First(n => n.TryGetProperty("name", out var name) && name.GetString() == "91274A141_COATED ALLOY STEEL SOCKET HEAD CAP SCREW-1");
        var secondScrew = root.GetProperty("nodes").EnumerateArray().First(n => n.TryGetProperty("name", out var name) && name.GetString() == "91274A141_COATED ALLOY STEEL SOCKET HEAD CAP SCREW-2");
        Assert.Equal(firstScrew.GetProperty("part").GetString(), secondScrew.GetProperty("part").GetString());
        Assert.Equal("nist_mtc_crada_box_rev-D.SLDPRT", root.GetProperty("nodes")[1].GetProperty("properties").GetProperty("file").GetString());
    }

    [Fact]
    public void Each_part_file_is_a_standalone_glb()
    {
        var package = AssemblyPackage.Create(Assembly());

        foreach (var (name, data) in package.Files.Where(f => f.Key.EndsWith(".glb", StringComparison.Ordinal)))
        {
            GlbValidation.Validate(data);
            var part = ModelLoader.Load(data, name);
            Assert.Single(part.Meshes);
            Assert.True(part.Meshes[0].TriangleCount > 0);
        }
    }

    [Theory]
    [InlineData(GlbCompression.None)]
    [InlineData(GlbCompression.Meshopt)]
    public void Package_loads_back_to_the_same_assembly(GlbCompression compression)
    {
        var original = Assembly();
        var package = AssemblyPackage.Create(original, new AssemblyPackageOptions { Glb = new GlbWriteOptions { Compression = compression } });

        var back = AssemblyPackage.LoadZip(package.ToZip());

        Assert.Equal(original.ComputeStatistics().Triangles, back.ComputeStatistics().Triangles);
        Assert.Equal(original.Nodes().Select(n => n.Name), back.Nodes().Select(n => n.Name));
        Assert.Equal(original.Meshes.Count, back.Meshes.Count);
        Assert.Equal(original.Metadata.PartNames, back.Metadata.PartNames);
        Assert.Equal(original.Metadata.GetProperty("Part Number"), back.Metadata.GetProperty("Part Number"));
        Assert.Equal("solidworks", back.Metadata.Format.Id);
        var a = original.ComputeBounds()!.Value;
        var b = back.ComputeBounds()!.Value;
        Assert.True(System.Numerics.Vector3.Distance(a.Min, b.Min) < 1e-3f && System.Numerics.Vector3.Distance(a.Max, b.Max) < 1e-3f);
        Assert.True(back.Materials.Count >= original.Materials.Count);
    }

    [Fact]
    public void Directory_round_trip_and_a_manifest_with_a_part_outside_the_package_is_refused()
    {
        var package = AssemblyPackage.Create(Assembly());
        var directory = Path.Combine(Path.GetTempPath(), "xray3d-" + Guid.NewGuid().ToString("N"));
        try
        {
            package.WriteToDirectory(directory);
            var back = AssemblyPackage.Load(directory);
            Assert.Equal(37, back.Nodes().Count());

            var evil = package.Manifest.Replace("parts/p0.glb", "../p0.glb");
            Assert.Throws<ModelFormatException>(() => AssemblyPackage.Load(p => p == "assembly.json" ? System.Text.Encoding.UTF8.GetBytes(evil) : package.Files.GetValueOrDefault(p)));
            Assert.Throws<ModelFormatException>(() => AssemblyPackage.Load(_ => null));
            Assert.Throws<ModelFormatException>(() => AssemblyPackage.Load(p => p == "assembly.json" ? "{\"schema\":\"nope\"}"u8.ToArray() : null));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Scene_normalisation_is_in_the_manifest_root_not_in_the_parts()
    {
        var model = ModelLoader.Load(DatasetPaths.File("synthetic/two-cubes.3mf")); // millimeters, Z-up
        var package = AssemblyPackage.Create(model);
        using var manifest = JsonDocument.Parse(package.Manifest);

        var matrix = manifest.RootElement.GetProperty("root").GetProperty("matrix").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        Assert.Equal(0.001f, matrix[0], 6);
        var part = ModelLoader.Load(package.Files["parts/p0.glb"], "p0.glb");
        Assert.Equal(1f, part.ComputeBounds()!.Value.Size.X, 4); // still a 1 mm cube in the part's own units
    }

    [Fact]
    public void Damaged_packages_fail_cleanly()
    {
        var package = AssemblyPackage.Create(ModelLoader.Load(DatasetPaths.File("synthetic/two-cubes.3mf")));
        var random = new Random(5);
        for (var round = 0; round < 100; round++)
        {
            var manifest = (byte[])package.Files["assembly.json"].Clone();
            for (var k = 0; k < 1 + random.Next(4); k++)
            {
                manifest[random.Next(manifest.Length)] = (byte)random.Next(32, 127);
            }

            try
            {
                _ = AssemblyPackage.Load(p => p == "assembly.json" ? manifest : package.Files.GetValueOrDefault(p));
            }
            catch (ModelFormatException)
            {
            }
        }
    }
}
