using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace XRay.ThreeD.Tests;

/// <summary>
/// SketchUp files: the version, the thumbnail, and for 2021 and later the materials, tags and styles of the ZIP package behind the header. Public files of
/// SketchUp 3 to 26 (55 of them, not redistributable here) are read from XRAY_3D_LOCAL_SAMPLES (default /tmp/claude-0/samples, sub-folder sketchup) and
/// compared with an independent reader (Corpus/skp-golden.json); synthetic files in the same layouts are committed. Geometry is proprietary and not decoded.
/// </summary>
public class SketchUpTests
{
    public static IEnumerable<object[]> Files() => JsonDocument.Parse(File.ReadAllText(DatasetPaths.CorpusFile("skp-golden.json"))).RootElement
        .EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(Files))]
    public void RealFileMatchesTheIndependentReader(string file)
    {
        var root = Environment.GetEnvironmentVariable("XRAY_3D_LOCAL_SAMPLES") ?? "/tmp/claude-0/samples";
        var path = Path.Combine(root, "sketchup", file);
        Assert.SkipWhen(!File.Exists(path), "local sample not present: " + file);
        var golden = JsonDocument.Parse(File.ReadAllText(DatasetPaths.CorpusFile("skp-golden.json"))).RootElement.GetProperty(file);
        var data = File.ReadAllBytes(path);
        Assert.Equal("sketchup", ModelLoader.Detect(data, null).Id);
        var meta = ModelLoader.ReadMetadata(data, file);
        Assert.Equal(golden.GetProperty("version").GetString(), meta.FormatVersion);
        Assert.StartsWith("SketchUp ", meta.Application);
        Assert.False(meta.HasGeometry);
        var thumbnail = golden.GetProperty("thumbnail");
        var preview = ModelLoader.ReadPreview(data, file);
        if (thumbnail.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(preview);
        }
        else
        {
            Assert.NotNull(preview);
            Assert.True(meta.HasPreview);
            Assert.Equal(thumbnail.GetProperty("sha256").GetString(), Convert.ToHexString(SHA256.HashData(preview!.Data)).ToLowerInvariant());
        }

        if (golden.GetProperty("vff").GetBoolean())
        {
            Assert.Equal(golden.GetProperty("materials").EnumerateArray().Select(e => e.GetString()), meta.MaterialNames);
            Assert.Equal(golden.GetProperty("layers").EnumerateArray().Select(e => e.GetString()), meta.LayerNames);
            Assert.Equal(golden.GetProperty("styles").EnumerateArray().Select(e => e.GetString()), meta.Properties.Where(p => p.Group == "styles").Select(p => p.Value));
            Assert.Equal(golden.GetProperty("model_dat").GetInt64() + " bytes", meta.GetProperty("Model data"));
        }
    }

    [Fact]
    public void SyntheticPackage()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/model-2021.skp"));
        Assert.Equal("sketchup", ModelLoader.Detect(data, "x.bin").Id);
        var meta = ModelLoader.ReadMetadata(data, "model-2021.skp");
        Assert.Equal("SketchUp 2021 (21.1.299)", meta.Application);
        Assert.Equal("21.1.299", meta.FormatVersion);
        Assert.Equal(["Oak", "Glass"], meta.MaterialNames);
        Assert.Equal(["Walls"], meta.LayerNames);
        Assert.Equal("#A06E3C, textured", meta.GetProperty("Oak"));
        Assert.Equal("#C8DCE6, opacity 0.4", meta.GetProperty("Glass"));
        Assert.Equal("[Simple Style]", meta.GetProperty("Style"));
        Assert.Equal("1", meta.GetProperty("Texture images"));
        Assert.Equal(LengthUnit.Inch, meta.Units);
        var preview = ModelLoader.ReadPreview(data, "model-2021.skp");
        Assert.Equal(20, (int)BinaryPrimitives.ReadUInt32BigEndian(preview!.Data.AsSpan(16)));
        Assert.Throws<UnsupportedModelFormatException>(() => ModelLoader.Load(data, "model-2021.skp"));
    }

    [Fact]
    public void SyntheticSerialisedStream()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/model-2016.skp"));
        var meta = ModelLoader.ReadMetadata(data, "model-2016.skp");
        Assert.Equal("SketchUp 2016 (16.1.1449)", meta.Application);
        Assert.Contains(meta.Notes, n => n.Contains("Serialised object stream"));
        var preview = ModelLoader.ReadPreview(data, "model-2016.skp");
        Assert.Equal(24, (int)BinaryPrimitives.ReadUInt32BigEndian(preview!.Data.AsSpan(16)));
        Assert.Equal(18, (int)BinaryPrimitives.ReadUInt32BigEndian(preview.Data.AsSpan(20)));
    }
}
