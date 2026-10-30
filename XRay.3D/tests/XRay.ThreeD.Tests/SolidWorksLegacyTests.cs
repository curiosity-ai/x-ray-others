using System.Text.Json;

namespace XRay.ThreeD.Tests;

/// <summary>
/// SolidWorks 2014 and earlier: an OLE compound file whose streams are zlib chunks behind the <c>__ZLB</c> signature. Real files (public repositories,
/// not redistributable) are read from XRAY_3D_LOCAL_SAMPLES (default /tmp/claude-0/samples, sub-folder solidworks-old) and compared with an
/// independent scan (olefile, zlib; Corpus/sw-legacy-golden.json). The committed synthetic file covers the same container without them.
/// </summary>
public class SolidWorksLegacyTests
{
    public static IEnumerable<object[]> Files() => JsonDocument.Parse(File.ReadAllText(DatasetPaths.CorpusFile("sw-legacy-golden.json"))).RootElement
        .EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(Files))]
    public void RealFileMatchesTheIndependentScan(string file)
    {
        var root = Environment.GetEnvironmentVariable("XRAY_3D_LOCAL_SAMPLES") ?? "/tmp/claude-0/samples";
        var path = Path.Combine(root, "solidworks-old", file);
        Assert.SkipWhen(!File.Exists(path), "local sample not present: " + file);
        var golden = JsonDocument.Parse(File.ReadAllText(DatasetPaths.CorpusFile("sw-legacy-golden.json"))).RootElement.GetProperty(file);
        var data = File.ReadAllBytes(path);
        Assert.Equal("solidworks", ModelLoader.Detect(data, file).Id);
        var meta = ModelLoader.ReadMetadata(data, file);
        Assert.Equal("SolidWorks (2014 or earlier)", meta.Application);
        if (golden.TryGetProperty("configurations", out var configurations))
        {
            Assert.Equal(configurations.EnumerateArray().Select(e => e.GetString()), meta.Configurations);
        }

        if (golden.TryGetProperty("component_files", out var components))
        {
            Assert.Equal(components.EnumerateArray().Select(e => e.GetString()).Order(StringComparer.OrdinalIgnoreCase), meta.ComponentFiles.Order(StringComparer.OrdinalIgnoreCase));
        }

        var model = ModelLoader.Load(data, file, new LoadOptions { Cancellation = TestContext.Current.CancellationToken });
        if (golden.GetProperty("faces").GetInt32() == 0)
        {
            // A part with no drawn faces (the display list holds none): nothing to build.
            Assert.Empty(model.Meshes);
            return;
        }

        Assert.True(model.Meshes.Count > 0);
        Assert.Equal(golden.GetProperty("vertices").GetInt64(), model.Metadata.Statistics!.UniqueVertices);
        Assert.True(model.Metadata.HasPreview);
        if (meta.DocumentKind == "part")
        {
            var box = model.ComputeBounds()!.Value;
            var min = golden.GetProperty("min").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
            var max = golden.GetProperty("max").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
            Assert.Equal(min[0], box.Min.X, 6);
            Assert.Equal(min[1], box.Min.Y, 6);
            Assert.Equal(min[2], box.Min.Z, 6);
            Assert.Equal(max[0], box.Max.X, 6);
            Assert.Equal(max[1], box.Max.Y, 6);
            Assert.Equal(max[2], box.Max.Z, 6);
        }
    }

    [Fact]
    public void SyntheticCompoundPartLoadsWithChunkedAndEmptyStreams()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/legacy-cube.sldprt"));
        Assert.Equal("solidworks", ModelLoader.Detect(data, "model.bin").Id);
        var model = ModelLoader.Load(data, "legacy-cube.sldprt");
        var meta = model.Metadata;
        Assert.Equal("part", meta.DocumentKind);
        Assert.Equal("Cube20", meta.Title);
        Assert.Equal("A. Drafter", meta.Author);
        Assert.Equal("Drafter", meta.LastModifiedBy);
        Assert.Equal(new DateTimeOffset(2013, 5, 6, 7, 8, 9, TimeSpan.Zero), meta.Created);
        Assert.Equal(new DateTimeOffset(2014, 2, 12, 16, 41, 57, TimeSpan.Zero), meta.Modified);
        Assert.Equal("7000", meta.FormatVersion);
        Assert.Equal(["Default"], meta.Configurations);
        Assert.Equal("LC-001", meta.GetProperty("Part Number"));
        Assert.Equal("legacy-cube", meta.GetProperty("SW-File Name"));
        Assert.Equal(UpAxis.Y, meta.UpAxis);
        Assert.Equal(LengthUnit.Meter, meta.Units);
        Assert.Equal(12, meta.Statistics!.Triangles);
        Assert.Equal(24, meta.Statistics.UniqueVertices);
        Assert.Equal(0.0024, meta.Statistics.SurfaceArea, 6);
        var box = meta.Bounds!.Value;
        Assert.Equal(-0.01f, box.Min.X, 6);
        Assert.Equal(0.01f, box.Max.Z, 6);
        Assert.NotNull(model.Preview);
        Assert.True(ModelLoader.ReadMetadata(data, "legacy-cube.sldprt").HasGeometry);
    }

    [Fact]
    public void OtherCompoundFilesAreNotSolidWorks()
    {
        Assert.NotEqual("solidworks", ModelLoader.Detect(File.ReadAllBytes(DatasetPaths.File("synthetic/part.ipt")), null).Id);
    }
}
