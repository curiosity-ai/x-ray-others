using System.Numerics;

namespace XRay.ThreeD.Tests;

/// <summary>
/// 3DXML: a synthetic package and product structure (committed), and the public CATIA example files (not committed: XRAY_3D_LOCAL_SAMPLES, sub-folder
/// 3dxml; vertex and triangle counts come from an independent parse of the XML).
/// </summary>
public class ThreeDxmlTests
{
    [Fact]
    public void PackageIsDetectedAndReadAsAnAssembly()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/assembly.3dxml"));
        Assert.Equal("3dxml", ModelLoader.Detect(data, "x.bin").Id);
        var model = ModelLoader.Load(data, "assembly.3dxml");
        var meta = model.Metadata;
        Assert.Equal("assembly", meta.DocumentKind);
        Assert.Equal("Rig", meta.Title);
        Assert.Equal("4.3", meta.FormatVersion);
        Assert.Equal(LengthUnit.Millimeter, meta.Units);
        Assert.True(meta.HasGeometry);

        var root = Assert.Single(model.Roots);
        Assert.Equal("Rig", root.Name);
        Assert.Equal("RIG-001", root.Properties["PLM_ExternalID"]);
        Assert.Equal(["Bracket.1", "Bracket.2", "Arm.1"], root.Children.Select(c => c.Name));

        // One representation, shared by every placement of the part.
        var mesh = Assert.Single(model.Meshes);
        Assert.Equal(8, mesh.VertexCount);
        Assert.Equal(14, mesh.TriangleCount);
        Assert.Equal(2, mesh.Groups.Count);
        Assert.Equal(0.5f, model.Materials[mesh.Groups[1].Material].BaseColor.W);
        var uses = model.Nodes().Where(n => n.Mesh is not null).ToList();
        Assert.Equal(3, uses.Count);
        Assert.All(uses, n => Assert.Same(mesh, n.Mesh));

        Assert.Equal(new Vector3(10, 0, 0), root.Children[0].Transform.Translation);
        var rotated = Vector3.Transform(new Vector3(1, 0, 0), root.Children[1].Transform);
        Assert.Equal(0f, rotated.X, 4);
        Assert.Equal(21f, rotated.Y, 4);
        Assert.Equal(new Vector3(5, 5, 35), Vector3.Transform(Vector3.Zero, root.Children[2].Children[0].Transform * root.Children[2].Transform));

        var bounds = model.ComputeBounds()!.Value;
        Assert.Equal(-1f, bounds.Min.X, 4);
        Assert.Equal(36f, bounds.Max.Z, 4);

        var glb = ModelLoader.ConvertToGlb(data, "assembly.3dxml");
        Assert.True(glb.Length > 100);
    }

    [Fact]
    public void StructureAloneTakesItsRepresentationsFromTheCompanionResolver()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/structure.3dxml"));
        Assert.Equal("3dxml", ModelLoader.Detect(data, "structure.bin").Id);

        var without = ModelLoader.Load(data, "structure.3dxml");
        Assert.False(without.Metadata.HasGeometry);
        Assert.Contains(without.Warnings, w => w.Contains("Bracket.3DRep", StringComparison.Ordinal));

        var rep = File.ReadAllBytes(DatasetPaths.File("synthetic/assembly.3dxml"));
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(rep), System.IO.Compression.ZipArchiveMode.Read);
        using var s = zip.GetEntry("Bracket.3DRep")!.Open();
        var bytes = new MemoryStream();
        s.CopyTo(bytes);
        var with = ModelLoader.Load(data, "structure.3dxml", new LoadOptions { ResolveCompanion = n => n == "Bracket.3DRep" ? bytes.ToArray() : null });
        Assert.True(with.Metadata.HasGeometry);
        Assert.Equal(14, with.Meshes.Single().TriangleCount);
    }

    [Fact]
    public void CancelledLoadIsNotAFormatError()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/assembly.3dxml"));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ModelLoader.Load(data, "assembly.3dxml", new LoadOptions { Cancellation = cts.Token }));
    }

    private static string? Samples()
    {
        var root = Environment.GetEnvironmentVariable("XRAY_3D_LOCAL_SAMPLES") ?? "/tmp/claude-0/samples";
        var dir = Path.Combine(root, "3dxml");
        return File.Exists(Path.Combine(dir, "quad.3dxml")) && File.Exists(Path.Combine(dir, "Chassis.3DRep")) ? dir : null;
    }

    [Fact]
    public void PublicQuadExampleMatchesTheIndependentCounts()
    {
        var dir = Samples();
        Assert.SkipWhen(dir is null, "local 3DXML samples not present");
        var data = File.ReadAllBytes(Path.Combine(dir!, "quad.3dxml"));
        var model = ModelLoader.Load(data, "quad.3dxml", new LoadOptions { ResolveCompanion = n => File.Exists(Path.Combine(dir!, n)) ? File.ReadAllBytes(Path.Combine(dir!, n)) : null });
        Assert.Equal("Quad", model.Roots.Single().Name);
        Assert.Equal("CATIA V5", model.Metadata.Application);
        Assert.True(model.Metadata.HasGeometry);

        var byName = model.Meshes.ToDictionary(m => m.Name!);
        Assert.Equal(10134 + 526, byName["Chassis_ReferenceRep"].VertexCount);
        Assert.Equal(1599, byName["Wheel_ReferenceRep"].VertexCount);
        // Degenerate triangles (strips and fans repeat indices) are dropped, so at most the raw counts.
        Assert.InRange(byName["Chassis_ReferenceRep"].TriangleCount, 16000, 16428 + 524);
        Assert.InRange(byName["Wheel_ReferenceRep"].TriangleCount, 2500, 2968);
        var wheels = model.Nodes().Count(n => ReferenceEquals(n.Mesh, byName["Wheel_ReferenceRep"]));
        Assert.Equal(4, wheels);
    }

    [Fact]
    public void HostileInputsFailCleanly()
    {
        var bad = "<?xml version=\"1.0\"?><Model_3dxml><ProductStructure root=\"1\"><Reference3D id=\"1\"/><Instance3D id=\"2\"><IsAggregatedBy>1</IsAggregatedBy><IsInstanceOf>1</IsInstanceOf></Instance3D></ProductStructure></Model_3dxml>";
        var ex = Assert.Throws<ModelFormatException>(() => ModelLoader.Load(System.Text.Encoding.UTF8.GetBytes(bad), "cycle.3dxml"));
        Assert.Contains("cycle", ex.Message, StringComparison.Ordinal);
    }
}
