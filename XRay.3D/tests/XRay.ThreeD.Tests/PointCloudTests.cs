using System.Text.Json;

namespace XRay.ThreeD.Tests;

public class PointCloudTests
{
    // The generator's cloud, rebuilt: x = 500000 + (i * 0.25 % 10), y = 4000000 + (i * 7 % 13) * 0.5, z = 100 + (i % 5) * 1.5.
    private static (double X, double Y, double Z) Expected(int i) => (500000.0 + i * 0.25 % 10, 4000000.0 + (i * 7) % 13 * 0.5, 100.0 + (i % 5) * 1.5);

    private static (double X, double Y, double Z) Absolute(Model3D model, int i)
    {
        var mesh = model.Meshes[0];
        var origin = model.Metadata.Origin!;
        return (mesh.Positions[3 * i] + origin[0], mesh.Positions[3 * i + 1] + origin[1], mesh.Positions[3 * i + 2] + origin[2]);
    }

    [Theory]
    [InlineData("synthetic/cloud-f0.las")]
    [InlineData("synthetic/cloud-f3.las")]
    public void Las_points_round_trip_through_the_recentred_origin(string file)
    {
        var model = ModelLoader.Load(DatasetPaths.File(file));
        var mesh = Assert.Single(model.Meshes);

        Assert.Equal(200, mesh.VertexCount);
        Assert.Empty(mesh.Indices);
        Assert.Equal("1.2", model.Metadata.FormatVersion);
        Assert.Equal("xray3d fixtures", model.Metadata.Application);
        Assert.Equal(new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero), model.Metadata.Created);
        Assert.Equal("EPSG:32632", model.Metadata.GetProperty("CRS"));
        for (var i = 0; i < 200; i++)
        {
            var (x, y, z) = Absolute(model, i);
            var e = Expected(i);
            // The file stores millimetres; float32 around a recentred origin keeps well under a millimetre.
            Assert.InRange(Math.Abs(x - e.X), 0, 0.002);
            Assert.InRange(Math.Abs(y - e.Y), 0, 0.002);
            Assert.InRange(Math.Abs(z - e.Z), 0, 0.002);
        }

        // Positions are small numbers near the origin, not survey coordinates.
        Assert.True(model.ComputeBounds()!.Value.Max.Length() < 20);
    }

    [Fact]
    public void Las_rgb_is_normalised_from_16_bits_and_other_formats_get_a_height_ramp()
    {
        var rgb = ModelLoader.Load(DatasetPaths.File("synthetic/cloud-f3.las")).Meshes[0];
        Assert.Equal(0f, rgb.Colors[0], 3);
        Assert.Equal(1f, rgb.Colors[1], 3);   // colour[0] = (0, 255, 0)
        Assert.Equal(0f, rgb.Colors[2], 3);
        Assert.Equal(5 / 255f, rgb.Colors[4], 3);

        var ramp = ModelLoader.Load(DatasetPaths.File("synthetic/cloud-f0.las")).Meshes[0];
        Assert.Equal(800, ramp.Colors.Length);
        Assert.NotEqual(ramp.Colors[0], ramp.Colors[4 * 4]); // different heights, different colours
    }

    [Fact]
    public void Las_metadata_can_be_read_without_the_points()
    {
        var meta = ModelLoader.ReadMetadata(DatasetPaths.File("synthetic/cloud-f3.las"));

        Assert.Equal("200", meta.GetProperty("points"));
        Assert.Equal("EPSG:32632", meta.GetProperty("CRS"));
        Assert.Equal(500009.75f, meta.Bounds!.Value.Max.X, 1);
        Assert.Equal("las", meta.Format.Id);
    }

    [Fact]
    public void Compression_flag_without_a_laszip_record_is_a_damaged_file()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/cloud-f0.las"));
        data[104] |= 0x80;

        Assert.Equal("laz", ModelLoader.Detect(data, "x.las").Id);
        Assert.Throws<ModelFormatException>(() => ModelLoader.Load(data, "x.las"));
    }

    [Fact]
    public void Large_clouds_are_thinned_evenly()
    {
        var model = ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File("synthetic/cloud-f3.las")), "x.las", new LoadOptions { MaxPoints = 50 });

        Assert.Equal(50, model.Meshes[0].VertexCount);
        Assert.Equal(50 * 4, model.Meshes[0].Colors.Length);
        Assert.Contains(model.Warnings, w => w.Contains("thinned", StringComparison.Ordinal));
        Assert.Equal("200", model.Metadata.GetProperty("points"));
    }

    [Theory]
    [InlineData("synthetic/cloud-ascii.pcd")]
    [InlineData("synthetic/cloud-binary.pcd")]
    [InlineData("synthetic/cloud-compressed.pcd")]
    public void Pcd_encodings_agree(string file)
    {
        var model = ModelLoader.Load(DatasetPaths.File(file));
        var mesh = Assert.Single(model.Meshes);

        Assert.Equal(100, mesh.VertexCount);
        Assert.Equal("0.7", model.Metadata.FormatVersion);
        Assert.Contains("x y z rgb", model.Metadata.GetProperty("fields"));
        for (var i = 0; i < 100; i += 7)
        {
            var origin = model.Metadata.Origin!;
            Assert.Equal(Math.Cos(i / 10.0) * 2, mesh.Positions[3 * i] + origin[0], 4);
            Assert.Equal(Math.Sin(i / 10.0) * 2, mesh.Positions[3 * i + 1] + origin[1], 4);
            Assert.Equal(i / 50.0, mesh.Positions[3 * i + 2] + origin[2], 4);
        }

        // colour[i] = (i * 5 % 256, 255 - i * 3 % 256, i * 11 % 256)
        Assert.Equal(5 / 255f, mesh.Colors[4], 3);
        Assert.Equal((255 - 3) / 255f, mesh.Colors[5], 3);
        Assert.Equal(11 / 255f, mesh.Colors[6], 3);
    }

    [Theory]
    [InlineData("synthetic/cloud.xyz")]
    [InlineData("synthetic/cloud.pts")]
    public void Text_clouds_read_with_colours(string file)
    {
        var model = ModelLoader.Load(DatasetPaths.File(file));
        var mesh = Assert.Single(model.Meshes);

        Assert.Equal(100, mesh.VertexCount);
        Assert.Equal(5 / 255f, mesh.Colors[4], 3);
        Assert.Equal(ModelFormats.FromExtension(file)!.Id, model.Metadata.Format.Id);
    }

    [Fact]
    public void Clouds_are_written_as_points_with_colours_and_the_origin_is_recorded()
    {
        var model = ModelLoader.Load(DatasetPaths.File("synthetic/cloud-f3.las"));
        var glb = GlbWriter.ToArray(model);
        GlbValidation.Validate(glb);
        var json = JsonDocument.Parse(glb.AsMemory(20, BitConverter.ToInt32(glb, 12))).RootElement;

        var primitive = json.GetProperty("meshes")[0].GetProperty("primitives")[0];
        Assert.Equal(0, primitive.GetProperty("mode").GetInt32());
        Assert.True(primitive.GetProperty("attributes").TryGetProperty("COLOR_0", out _));
        var origin = json.GetProperty("asset").GetProperty("extras").GetProperty("origin");
        Assert.InRange(origin[0].GetDouble(), 500000, 500010);
        Assert.InRange(origin[1].GetDouble(), 4000000, 4000007);

        foreach (var mode in new[] { GlbCompression.Quantize, GlbCompression.Meshopt })
        {
            var packed = GlbWriter.ToArray(model, new GlbWriteOptions { Compression = mode });
            var back = ModelLoader.Load(packed, "x.glb");
            Assert.Equal(200, back.ComputeStatistics().Vertices);
        }
    }

    [Fact]
    public void Bad_clouds_fail_cleanly()
    {
        Assert.Throws<ModelFormatException>(() => ModelLoader.Load("no numbers here\nnor here\n"u8.ToArray(), "x.xyz"));
        Assert.Throws<ModelFormatException>(() => ModelLoader.Load("# .PCD v0.7\nVERSION 0.7\nFIELDS x y z\nSIZE 4 4 4\nTYPE F F F\nCOUNT 1 1 1\nPOINTS 5\nDATA binary\n"u8.ToArray(), "x.pcd"));
        Assert.Throws<ModelFormatException>(() => ModelLoader.Load(new byte[300], "x.las"));
    }
}

/// <summary>LAZ (LASzip) point formats 0 to 3 in several chunks, against laspy with the lazrs back end.</summary>
public class LazTests
{
    private static readonly JsonElement Golden = JsonDocument.Parse(File.ReadAllText(DatasetPaths.CorpusFile("laz-golden.json"))).RootElement;

    [Theory]
    [InlineData("laz-f0.laz")]
    [InlineData("laz-f1.laz")]
    [InlineData("laz-f2.laz")]
    [InlineData("laz-f3.laz")]
    public void Decoded_records_are_byte_identical_to_the_independent_decoder(string name)
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/" + name));
        var expected = Golden.GetProperty(name);
        var info = Formats.PointCloud.LazDecoder.ReadInfo(data)!;
        var recordLength = expected.GetProperty("recordLength").GetInt32();
        var count = expected.GetProperty("points").GetInt32();
        var pointOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(96));

        var (records, kept) = Formats.PointCloud.LazDecoder.Decode(data, info, pointOffset, count, recordLength, 1, new LoadOptions());

        Assert.Equal(count, kept);
        Assert.Equal(400u, info.ChunkSize);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(records)).ToLowerInvariant();
        Assert.Equal(expected.GetProperty("recordsSha256").GetString(), hash);
    }

    [Theory]
    [InlineData("laz-f0.laz")]
    [InlineData("laz-f3.laz")]
    public void Loaded_model_has_the_points_and_colours(string name)
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/" + name));
        var expected = Golden.GetProperty(name);
        var model = ModelLoader.Load(data, name);
        var mesh = Assert.Single(model.Meshes);
        var origin = model.Metadata.Origin!;

        Assert.Equal("laz", model.Metadata.Format.Id);
        Assert.Equal(1500, mesh.VertexCount);
        var indexes = new[] { 0, 1, 399, 400, 401, 799, 800, 1499 };
        for (var k = 0; k < indexes.Length; k++)
        {
            var i = indexes[k];
            Assert.InRange(Math.Abs(expected.GetProperty("x")[k].GetDouble() - (mesh.Positions[3 * i] + origin[0])), 0, 0.002);
            Assert.InRange(Math.Abs(expected.GetProperty("y")[k].GetDouble() - (mesh.Positions[3 * i + 1] + origin[1])), 0, 0.002);
            Assert.InRange(Math.Abs(expected.GetProperty("z")[k].GetDouble() - (mesh.Positions[3 * i + 2] + origin[2])), 0, 0.002);
        }

        if (expected.GetProperty("rgb").ValueKind == JsonValueKind.Array)
        {
            var rgb = expected.GetProperty("rgb")[0];
            // The cloud uses the full 16 bits somewhere, so the reader scales by 65535.
            Assert.Equal(rgb[0].GetInt32() / 65535f, mesh.Colors[0], 3);
            Assert.Equal(rgb[2].GetInt32() / 65535f, mesh.Colors[2], 3);
        }
    }

    [Fact]
    public void Thinning_and_cancellation_apply_to_compressed_clouds()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/laz-f1.laz"));

        Assert.Equal(150, ModelLoader.Load(data, "x.laz", new LoadOptions { MaxPoints = 150 }).Meshes[0].VertexCount);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ModelLoader.Load(data, "x.laz", new LoadOptions { Cancellation = cts.Token }));
    }
}

/// <summary>Uncompressed LAS 1.4 point formats 6 to 10 against laspy's values (positions, colours) and the committed point records.</summary>
public class Las14Tests
{
    private static readonly JsonElement Golden = JsonDocument.Parse(File.ReadAllText(DatasetPaths.CorpusFile("las14-golden.json"))).RootElement;

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void Points_and_colours_match_laspy(int format)
    {
        var name = $"las14-f{format}";
        var expected = Golden.GetProperty(name);
        var data = File.ReadAllBytes(DatasetPaths.File($"synthetic/{name}.las"));
        var model = ModelLoader.Load(data, name + ".las");
        var mesh = Assert.Single(model.Meshes);
        var origin = model.Metadata.Origin!;

        Assert.Equal("las", model.Metadata.Format.Id);
        Assert.Equal("1.4", model.Metadata.FormatVersion);
        Assert.Equal(900, mesh.VertexCount);
        var indexes = new[] { 0, 1, 299, 300, 301, 599, 600, 899 };
        for (var k = 0; k < indexes.Length; k++)
        {
            var i = indexes[k];
            Assert.InRange(Math.Abs(expected.GetProperty("x")[k].GetDouble() - (mesh.Positions[3 * i] + origin[0])), 0, 0.002);
            Assert.InRange(Math.Abs(expected.GetProperty("y")[k].GetDouble() - (mesh.Positions[3 * i + 1] + origin[1])), 0, 0.002);
            Assert.InRange(Math.Abs(expected.GetProperty("z")[k].GetDouble() - (mesh.Positions[3 * i + 2] + origin[2])), 0, 0.002);
        }

        if (expected.GetProperty("rgb").ValueKind == JsonValueKind.Array)
        {
            var rgb = expected.GetProperty("rgb")[0];
            Assert.Equal(rgb[0].GetInt32() / 65535f, mesh.Colors[0], 3);
            Assert.Equal(rgb[2].GetInt32() / 65535f, mesh.Colors[2], 3);
        }

        Assert.Equal(format.ToString(), ModelLoader.ReadMetadata(data, name + ".las").GetProperty("Point data format"));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void Layered_laz_records_are_byte_identical_to_the_independent_decoder(int format)
    {
        var name = $"las14-f{format}";
        var expected = Golden.GetProperty(name);
        var data = File.ReadAllBytes(DatasetPaths.File($"synthetic/laz14-f{format}.laz"));
        var info = Formats.PointCloud.LazDecoder.ReadInfo(data)!;
        var recordLength = expected.GetProperty("recordLength").GetInt32();
        var count = expected.GetProperty("points").GetInt32();
        var pointOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(96));

        var (records, kept) = Formats.PointCloud.LazDecoder.Decode(data, info, pointOffset, count, recordLength, 1, new LoadOptions());

        Assert.Equal(3, info.Compressor);
        Assert.Equal(count, kept);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(records)).ToLowerInvariant();
        Assert.Equal(expected.GetProperty("recordsSha256").GetString(), hash);
    }

    [Theory]
    [InlineData("laz14c-f6.laz", "las14-f6", 3)]
    [InlineData("laz14c-f7.laz", "las14-f7", 3)]
    [InlineData("laz14c-f8.laz", "las14-f8", 3)]
    [InlineData("laz-pointwise-f3.laz", "laz-pointwise-f3", 1)]
    public void Chunked_by_the_rust_writer_and_unchunked_files_are_byte_identical(string file, string key, int compressor)
    {
        var expected = Golden.GetProperty(key);
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/" + file));
        var info = Formats.PointCloud.LazDecoder.ReadInfo(data)!;
        var recordLength = expected.GetProperty("recordLength").GetInt32();
        var count = expected.GetProperty("points").GetInt32();
        var pointOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(96));

        var (records, kept) = Formats.PointCloud.LazDecoder.Decode(data, info, pointOffset, count, recordLength, 1, new LoadOptions());

        Assert.Equal(compressor, info.Compressor == 3 ? 3 : info.Compressor);
        Assert.Equal(count, kept);
        if (compressor == 3)
        {
            Assert.Equal(300u, info.ChunkSize);
        }

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(records)).ToLowerInvariant();
        Assert.Equal(expected.GetProperty("recordsSha256").GetString(), hash);
    }

    [Fact]
    public void Layered_laz_loads_as_a_model()
    {
        var model = ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File("synthetic/laz14-f8.laz")), "x.laz");
        Assert.Equal("laz", model.Metadata.Format.Id);
        Assert.Equal(900, model.Meshes[0].VertexCount);
    }
}
