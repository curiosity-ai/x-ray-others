using System.Text.Json;

namespace XRay.ThreeD.Tests;

/// <summary>
/// E57 against pye57 (libE57Format). The synthetic two-scan file is in the dataset; real files (the pye57 test data, a Cartesian scan with a
/// pose, a spherical scanner export with an embedded image) are not redistributable and are read from XRAY_3D_LOCAL_SAMPLES (default
/// /tmp/claude-0/samples, sub-folder e57); without them those tests skip.
/// </summary>
public class E57Tests
{
    private static string? Local(string file)
    {
        var root = Environment.GetEnvironmentVariable("XRAY_3D_LOCAL_SAMPLES") ?? "/tmp/claude-0/samples";
        var path = Path.Combine(root, "e57", file);
        return File.Exists(path) ? path : null;
    }

    private static double[] GlobalSum(Model3D model)
    {
        var mesh = model.Meshes[0];
        var origin = model.Metadata.Origin ?? [0, 0, 0];
        var sum = new double[3];
        for (var i = 0; i < mesh.VertexCount; i++)
        {
            for (var a = 0; a < 3; a++)
            {
                sum[a] += mesh.Positions[3 * i + a] + origin[a];
            }
        }

        return sum;
    }

    [Fact]
    public void Two_scans_with_colour_and_pose_match_libe57()
    {
        var file = "synthetic/e57-two-scans.e57";
        var golden = JsonDocument.Parse(File.ReadAllText(DatasetPaths.CorpusFile("e57-golden.json"))).RootElement.GetProperty("scans");
        var data = File.ReadAllBytes(DatasetPaths.File(file));
        Assert.Equal("e57", ModelLoader.Detect(data, "x.bin").Id);
        var model = ModelLoader.Load(data, file);
        Assert.Equal("e57", model.Metadata.Format.Id);
        Assert.Equal(2400, model.Meshes[0].VertexCount);
        var sum = GlobalSum(model);
        var expected = new double[3];
        foreach (var scan in golden.EnumerateArray())
        {
            expected[0] += scan.GetProperty("globalSumX").GetDouble();
            expected[1] += scan.GetProperty("globalSumY").GetDouble();
            expected[2] += scan.GetProperty("globalSumZ").GetDouble();
        }

        for (var a = 0; a < 3; a++)
        {
            Assert.InRange(Math.Abs(sum[a] - expected[a]), 0, 0.05);
        }

        var color = golden[0].GetProperty("firstColor");
        Assert.Equal(color[0].GetInt32() / 255f, model.Meshes[0].Colors[0], 3);
        Assert.Equal(color[2].GetInt32() / 255f, model.Meshes[0].Colors[2], 3);
        Assert.Equal(new[] { "scan 0", "scan 1" }, model.Metadata.PartNames);
        Assert.NotEmpty(GlbWriter.ToArray(model));
    }

    [Fact]
    public void Metadata_lists_scans_without_decoding_points()
    {
        var meta = ModelLoader.ReadMetadata(File.ReadAllBytes(DatasetPaths.File("synthetic/e57-two-scans.e57")), "x.e57");
        Assert.Equal("2400", meta.Properties.First(p => p is { Name: "points", Group: "cloud" }).Value);
        Assert.Equal("2", meta.GetProperty("scans"));
        Assert.Empty(meta.Notes.Where(n => n.Contains("CRC", StringComparison.Ordinal)));
    }

    [Fact]
    public void A_damaged_page_is_reported_not_fatal_for_the_metadata()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/e57-two-scans.e57"));
        data[2048 + 100] ^= 0xFF;
        var meta = ModelLoader.ReadMetadata(data, "x.e57");
        Assert.Contains(meta.Notes, n => n.Contains("CRC", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("test.e57")]
    [InlineData("testSpherical.e57")]
    [InlineData("pumpAVisualReferenceImage.e57")]
    public void Real_files_match_pye57(string file)
    {
        var path = Local(file);
        if (path is null)
        {
            Assert.Skip("E57 sample not available: " + file);
            return;
        }

        var golden = JsonDocument.Parse(File.ReadAllText(DatasetPaths.CorpusFile("e57-local-golden.json"))).RootElement.GetProperty(file);
        var model = ModelLoader.Load(File.ReadAllBytes(path), file, new LoadOptions { MaxPoints = 0 });
        long points = 0;
        var expected = new double[3];
        foreach (var scan in golden.EnumerateArray())
        {
            points += scan.GetProperty("points").GetInt64();
            for (var a = 0; a < 3; a++)
            {
                expected[a] += scan.GetProperty("sum")[a].GetDouble();
            }
        }

        Assert.Equal(points, model.Meshes[0].VertexCount);
        var bounds = model.Metadata.Bounds!.Value;
        var origin = model.Metadata.Origin!;
        for (var a = 0; a < 3; a++)
        {
            var low = golden.EnumerateArray().Min(sc => sc.GetProperty("min")[a].GetDouble());
            var high = golden.EnumerateArray().Max(sc => sc.GetProperty("max")[a].GetDouble());
            Assert.InRange(Math.Abs(bounds.Min[a] + origin[a] - low), 0, 0.01);
            Assert.InRange(Math.Abs(bounds.Max[a] + origin[a] - high), 0, 0.01);
        }

        var sum = GlobalSum(model);
        for (var a = 0; a < 3; a++)
        {
            // Positions are float32 around the centre: allow a rounding error per point.
            Assert.InRange(Math.Abs(sum[a] - expected[a]), 0, Math.Max(1.0, points * 2e-6 * Math.Max(1, Math.Abs(expected[a] / points) * 1e-3)));
        }
    }

    [Fact]
    public void The_embedded_image_is_the_preview()
    {
        var path = Local("pumpAVisualReferenceImage.e57");
        if (path is null)
        {
            Assert.Skip("E57 sample not available");
            return;
        }

        var preview = ModelLoader.ReadPreview(File.ReadAllBytes(path), "pump.e57");
        Assert.NotNull(preview);
        Assert.Equal("image/jpeg", preview.MediaType);
        Assert.Equal(0xFF, preview.Data[0]);
        Assert.Equal(0xD8, preview.Data[1]);
    }
}
