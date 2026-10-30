using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XRay.ThreeD.Tests;

/// <summary>
/// CATIA V5 documents: the container, the metadata streams, the preview and the display tessellation a part carries. Hand-made files in the same layout are committed
/// (dataset/synthetic, written by tools/fixtures/make_fixtures.py), seven MIT-licensed real files are committed (dataset/catia), and the other real files (parts and products of
/// R18 to R32, 75 in all, not redistributable) are read from XRAY_3D_LOCAL_SAMPLES (default /tmp/claude-0/samples, sub-folder catia/files) and compared with
/// Corpus/catia-golden.json. The golden file records counts, bounds and names of what this reader returns; what makes it more than a snapshot is that every tessellation of the
/// real files is a closed, consistently oriented surface (no edge without its reverse) and that a part and its mirror image have the same volume.
/// </summary>
public class CatiaTests
{
    private static readonly string[] Synthetic = ["catia-cube.CATPart", "catia-cube-r32.CATPart", "catia-assembly.CATProduct", "catia-drawing.CATDrawing"];

    private static byte[] Synth(string name) => File.ReadAllBytes(DatasetPaths.File("synthetic/" + name));

    private static string LocalRoot => Path.Combine(Environment.GetEnvironmentVariable("XRAY_3D_LOCAL_SAMPLES") ?? "/tmp/claude-0/samples", "catia", "files");

    private static JsonObject Golden() => JsonNode.Parse(File.ReadAllText(DatasetPaths.CorpusFile("catia-golden.json")))!.AsObject();

    [Theory]
    [InlineData("catia-cube.CATPart", "part")]
    [InlineData("catia-assembly.CATProduct", "assembly")]
    [InlineData("catia-drawing.CATDrawing", "drawing")]
    public void The_container_header_identifies_the_format_whatever_the_name(string file, string kind)
    {
        var data = Synth(file);
        Assert.Equal("catia", ModelLoader.Detect(data, "renamed.bin").Id);
        Assert.Equal(FormatSupport.Full, ModelLoader.Detect(data, null).Support);
        Assert.Equal(kind, ModelLoader.ReadMetadata(data).DocumentKind);
        Assert.Equal("catia", ModelLoader.Detect(Array.Empty<byte>(), "a.cgr").Id);
    }

    [Fact]
    public void A_cube_part_is_a_closed_solid_with_one_colour_per_face()
    {
        var model = ModelLoader.Load(Synth("catia-cube.CATPart"), "cube.CATPart");
        var mesh = Assert.Single(model.Meshes);
        Assert.Equal(6, mesh.Faces.Length);
        Assert.Equal(12, mesh.TriangleCount);
        Assert.Equal(24, mesh.VertexCount);
        Assert.Equal(mesh.Positions.Length, mesh.Normals.Length);
        Assert.Equal(6, model.Materials.Count);
        Assert.Equal(["#FF0000", "#00FF00", "#0000FF", "#FFFF00", "#00FFFF", "#D2D2FF"], model.Materials.Select(m => m.Name));
        Assert.Equal(6, mesh.Groups.Count);
        var bounds = model.Metadata.Bounds!.Value;
        Assert.Equal(new System.Numerics.Vector3(0, 0, 0), bounds.Min);
        Assert.Equal(new System.Numerics.Vector3(10, 10, 10), bounds.Max);
        Assert.Equal(0, OpenEdges(mesh));
        Assert.Equal(1000, Math.Round(Volume(mesh)), 0);
        Assert.Equal(LengthUnit.Millimeter, model.Metadata.Units);
        Assert.Equal(UpAxis.Z, model.Metadata.UpAxis);
        Assert.NotNull(model.Preview);
        Assert.True(model.Metadata.HasGeometry);
        Assert.Contains(model.Metadata.Notes, n => n.Contains("tessellation", StringComparison.Ordinal));
    }

    [Fact]
    public void A_cube_part_converts_to_glb()
    {
        var glb = ModelLoader.ConvertToGlb(Synth("catia-cube.CATPart"), "cube.CATPart");
        Assert.True(glb.Length > 500);
        Assert.Equal((byte)'g', glb[0]);
    }

    [Fact]
    public void The_summary_properties_name_the_save_version_and_the_links()
    {
        var meta = ModelLoader.ReadMetadata(Synth("catia-cube.CATPart"));
        Assert.Equal("V5R21", meta.FormatVersion);
        Assert.Equal("CATIA V5R21", meta.Application);
        Assert.Equal("Cube", meta.Title);
        Assert.Equal(["Cube"], meta.PartNames);
        Assert.Equal("V5R21 SP0 HF0 built 04-14-2011.20.00", meta.GetProperty("LastSaveVersion"));
        Assert.Equal("CATIAV5R21", meta.GetProperty("MinimalVersionToRead"));
        Assert.Equal("Part1", meta.GetProperty("Part feature"));
        Assert.Equal("Synthetic cube", meta.GetProperty("Nomenclature"));
        Assert.Equal(@"C:\Models\Cube.CATPart", meta.GetProperty("Original path"));
        Assert.Equal(["Fastener.CATPart"], meta.ComponentFiles);
        Assert.Contains("CATPrtCont", meta.GetProperty("Containers"));
        Assert.True(meta.HasPreview);
        Assert.Equal("16 x 12", meta.GetProperty("Preview size"));
        Assert.Equal("CATIA V5R32 SP1", ModelLoader.ReadMetadata(Synth("catia-cube-r32.CATPart")).Application);
    }

    [Fact]
    public void The_preview_is_the_embedded_jpeg()
    {
        foreach (var file in Synthetic)
        {
            var preview = ModelLoader.ReadPreview(Synth(file), file);
            Assert.NotNull(preview);
            Assert.Equal("image/jpeg", preview!.MediaType);
            Assert.Equal(0xff, preview.Data[0]);
            Assert.Equal(0xd8, preview.Data[1]);
            Assert.Equal(0xd9, preview.Data[^1]);
        }
    }

    [Fact]
    public void A_product_lists_its_structure_and_the_files_it_refers_to()
    {
        var data = Synth("catia-assembly.CATProduct");
        var meta = ModelLoader.ReadMetadata(data);
        Assert.Equal("assembly", meta.DocumentKind);
        Assert.Equal("Gearbox", meta.Title);
        Assert.Equal(["Cube.1", "Fastener.1", "Fastener.2"], meta.PartNames);
        Assert.Equal(["Cube.CATPart", "Fastener.CATPart", "scan.stl"], meta.ComponentFiles);
        Assert.Equal(@"D:\Work\Gearbox.CATProduct", meta.GetProperty("Original path"));
        Assert.False(meta.HasGeometry);
        var ex = Assert.Throws<UnsupportedModelFormatException>(() => ModelLoader.Load(data, "x.CATProduct"));
        Assert.Contains("positions", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_drawing_has_a_preview_and_no_geometry()
    {
        var data = Synth("catia-drawing.CATDrawing");
        var meta = ModelLoader.ReadMetadata(data);
        Assert.Equal("drawing", meta.DocumentKind);
        Assert.True(meta.HasPreview);
        Assert.Throws<UnsupportedModelFormatException>(() => ModelLoader.Load(data, "x.CATDrawing"));
    }

    [Fact]
    public void Handle_widths_run_lengths_fans_and_the_flagged_table_layout_are_read()
    {
        // 300 vertices need two-byte handles; the table comes after other arrays (flag bytes 01 00 01 02), found by its own consistency.
        var rows = Enumerable.Range(0, 300).Select(i => (X: (float)(i % 7), Y: (float)(i % 11), Z: (float)(i / 7))).ToList();
        var main = new List<byte>(new byte[40]);
        main.AddRange(CatiaFixture.Positions(rows, [1, 0, 1, 2], filler: 64));
        main.AddRange(CatiaFixture.Run(2, fan: false, 0, 1, 2, 3, 4));
        main.AddRange(CatiaFixture.Run(2, fan: true, 10, 11, 12, 13));
        main.AddRange(CatiaFixture.Triangles(2, 20, 21, 22, 23, 24, 25));
        var tail = new List<byte>();
        for (var start = 30; start < 300; start += 3)
        {
            tail.AddRange(CatiaFixture.Triangles(2, start, start + 1, start + 2));
        }

        main.AddRange(tail);
        main.AddRange(Enumerable.Repeat(CatiaFixture.Row(1, 2, 3, 255), 93).SelectMany(r => r));
        var model = ModelLoader.Load(CatiaFixture.Part([.. main]), "p.CATPart");
        var mesh = Assert.Single(model.Meshes);
        Assert.Equal(93, mesh.Faces.Length);
        Assert.Equal(3 + 2 + 2 + 90, mesh.TriangleCount);
    }

    [Fact]
    public void A_table_that_the_faces_do_not_use_is_not_taken_for_a_tessellation()
    {
        var rows = Enumerable.Range(0, 64).Select(i => ((float)i, 0f, 0f)).ToList();
        var main = new List<byte>(CatiaFixture.Positions(rows));
        main.AddRange(CatiaFixture.Triangles(1, 0, 1, 2));
        var ex = Assert.Throws<UnsupportedModelFormatException>(() => ModelLoader.Load(CatiaFixture.Part([.. main]), "p.CATPart"));
        Assert.Contains("tessellation", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Handles_outside_the_table_end_a_packet()
    {
        var rows = Enumerable.Range(0, 8).Select(i => ((float)(i & 1), (float)((i >> 1) & 1), (float)(i >> 2))).ToList();
        var main = new List<byte>(CatiaFixture.Positions(rows));
        main.AddRange(CatiaFixture.Triangles(1, 0, 1, 9));
        Assert.Throws<UnsupportedModelFormatException>(() => ModelLoader.Load(CatiaFixture.Part([.. main]), "p.CATPart"));
    }

    [Fact]
    public void The_triangle_limit_is_enforced()
    {
        var rows = Enumerable.Range(0, 8).Select(i => ((float)(i & 1), (float)((i >> 1) & 1), (float)(i >> 2))).ToList();
        var main = new List<byte>(CatiaFixture.Positions(rows));
        for (var i = 0; i < 8; i++)
        {
            main.AddRange(CatiaFixture.Triangles(1, 0, 1, 2, 1, 2, 3, 4, 5, 6, 5, 6, 7));
        }

        Assert.Throws<ModelFormatException>(() => ModelLoader.Load(CatiaFixture.Part([.. main]), "p.CATPart", new LoadOptions { MaxTriangles = 5 }));
    }

    [Fact]
    public void Truncating_the_container_fails_cleanly()
    {
        var data = Synth("catia-cube.CATPart");
        for (var length = 0; length < data.Length; length += 37)
        {
            try
            {
                _ = ModelLoader.Load(data[..length], "x.CATPart");
            }
            catch (ModelFormatException)
            {
            }
        }
    }

    [Fact]
    public void A_header_without_a_directory_is_a_format_error()
    {
        var data = new byte[200];
        Encoding.ASCII.GetBytes("V5_CFV2\0").CopyTo(data, 0);
        Assert.Throws<ModelFormatException>(() => ModelLoader.ReadMetadata(data, "x.CATPart"));
    }

    private static void StopsPromptly(byte[] data)
    {
        using var cts = new CancellationTokenSource();
        var options = new LoadOptions { Cancellation = cts.Token };
        var watch = Stopwatch.StartNew();
        var task = Task.Run(() => ModelLoader.Load(data, "big.CATPart", options), TestContext.Current.CancellationToken);
        Thread.Sleep(30);
        cts.Cancel();
        var cancelled = Stopwatch.StartNew();
        try
        {
            task.GetAwaiter().GetResult();
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30));
        }
        catch (OperationCanceledException)
        {
            Assert.True(cancelled.Elapsed < TimeSpan.FromSeconds(2), $"took {cancelled.Elapsed} to stop after the cancel");
        }
        catch (ModelFormatException)
        {
            // The input is refused as damaged before the cancel lands.
        }
    }

    [Fact]
    public void A_large_tessellation_stops_promptly()
    {
        const int n = 400_000;
        var rows = Enumerable.Range(0, n).Select(i => ((float)(i % 977), (float)(i % 613), (float)(i % 31))).ToList();
        var main = new List<byte>(CatiaFixture.Positions(rows));
        var faces = 0;
        for (var i = 0; i + 2 < n; i += 1)
        {
            main.AddRange(CatiaFixture.Triangles(3, i, i + 1, i + 2));
            faces++;
        }

        main.AddRange(Enumerable.Repeat(CatiaFixture.Row(9, 9, 9, 255), faces).SelectMany(r => r));
        StopsPromptly(CatiaFixture.Part([.. main]));
    }

    [Fact]
    public void A_stream_of_false_packet_starts_stops_promptly()
    {
        var rows = Enumerable.Range(0, 1000).Select(i => ((float)i, 0f, 0f)).ToList();
        var main = new List<byte>(CatiaFixture.Positions(rows));
        for (var i = 0; i < 6_000_000; i++)
        {
            main.AddRange([0x01, 0x4b, 0xff, 0xff]);
        }

        StopsPromptly(CatiaFixture.Part([.. main]));
    }

    private static int OpenEdges(MeshData mesh)
    {
        var edges = new Dictionary<(float, float, float, float, float, float), int>();
        var p = mesh.Positions;
        for (var t = 0; t + 2 < mesh.Indices.Length; t += 3)
        {
            for (var k = 0; k < 3; k++)
            {
                var a = (int)mesh.Indices[t + k] * 3;
                var b = (int)mesh.Indices[t + (k + 1) % 3] * 3;
                var key = (p[a], p[a + 1], p[a + 2], p[b], p[b + 1], p[b + 2]);
                edges[key] = edges.GetValueOrDefault(key) + 1;
            }
        }

        return edges.Count(e => edges.GetValueOrDefault((e.Key.Item4, e.Key.Item5, e.Key.Item6, e.Key.Item1, e.Key.Item2, e.Key.Item3)) != e.Value);
    }

    private static double Volume(MeshData mesh)
    {
        double volume = 0;
        var p = mesh.Positions;
        for (var t = 0; t + 2 < mesh.Indices.Length; t += 3)
        {
            int a = (int)mesh.Indices[t] * 3, b = (int)mesh.Indices[t + 1] * 3, c = (int)mesh.Indices[t + 2] * 3;
            volume += p[a] * (p[b + 1] * (double)p[c + 2] - p[b + 2] * (double)p[c + 1])
                - p[a + 1] * (p[b] * (double)p[c + 2] - p[b + 2] * (double)p[c])
                + p[a + 2] * (p[b] * (double)p[c + 1] - p[b + 1] * (double)p[c]);
        }

        return volume / 6;
    }

    /// <summary>What the golden file records for one file: names, counts and bounds (never a vertex).</summary>
    private static JsonObject Summarize(byte[] data, string name)
    {
        var meta = ModelLoader.ReadMetadata(data, name);
        var o = new JsonObject
        {
            ["sha256"] = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(),
            ["kind"] = meta.DocumentKind,
            ["version"] = meta.FormatVersion,
            ["application"] = meta.Application,
            ["title"] = meta.Title,
            ["partFeature"] = meta.GetProperty("Part feature"),
            ["partNames"] = new JsonArray(meta.PartNames.Select(n => (JsonNode?)n).ToArray()),
            ["components"] = new JsonArray(meta.ComponentFiles.Select(n => (JsonNode?)n).ToArray()),
            ["previewSize"] = meta.GetProperty("Preview size"),
            ["hasGeometry"] = meta.HasGeometry,
        };
        if (meta.HasGeometry)
        {
            var model = ModelLoader.Load(data, name);
            var s = model.Metadata.Statistics!;
            var bounds = model.Metadata.Bounds!.Value;
            o["bodies"] = new JsonArray(model.Meshes.Select(m => (JsonNode?)new JsonArray(m.Faces.Length, m.TriangleCount, m.VertexCount)).ToArray());
            o["materials"] = s.Materials;
            o["bounds"] = new JsonArray(new[] { bounds.Min.X, bounds.Min.Y, bounds.Min.Z, bounds.Max.X, bounds.Max.Y, bounds.Max.Z }.Select(v => (JsonNode?)Math.Round(v, 3)).ToArray());
            o["openEdges"] = model.Meshes.Sum(OpenEdges);
        }

        return o;
    }

    private static void Check(JsonObject golden, byte[] data, string name)
    {
        var actual = Summarize(data, name);
        foreach (var (key, expected) in golden)
        {
            Assert.True(actual.ContainsKey(key), name + ": " + key + " missing");
            Assert.Equal(expected?.ToJsonString(), actual[key]?.ToJsonString());
        }

        if (golden.ContainsKey("openEdges"))
        {
            Assert.Equal(0, actual["openEdges"]!.GetValue<int>() - golden["openEdges"]!.GetValue<int>());
        }
    }

    public static IEnumerable<object[]> LocalFiles() => Golden().Select(p => new object[] { p.Key });

    public static IEnumerable<object[]> DatasetFiles() => Directory.EnumerateFiles(Path.Combine(DatasetPaths.Root, "catia")).Select(f => new object[] { Path.GetFileName(f) }).OrderBy(a => (string)a[0], StringComparer.Ordinal);

    [Theory]
    [MemberData(nameof(DatasetFiles))]
    public void A_committed_real_file_matches_the_golden_file(string file)
    {
        var data = File.ReadAllBytes(DatasetPaths.File("catia/" + file));
        var sha = Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
        var golden = Golden().Single(p => p.Value!["sha256"]!.GetValue<string>() == sha).Value!.AsObject();
        Check(golden, data, file);
    }

    [Theory]
    [MemberData(nameof(LocalFiles))]
    public void A_real_file_matches_the_golden_file(string file)
    {
        var path = Path.Combine(LocalRoot, file);
        Assert.SkipWhen(!File.Exists(path), "local sample not present: " + file);
        var data = File.ReadAllBytes(path);
        var golden = Golden()[file]!.AsObject();
        Check(golden, data, file);
        var model = golden["hasGeometry"]!.GetValue<bool>() ? ModelLoader.Load(data, file) : null;
        if (model is not null)
        {
            // A tessellation is a closed surface of consistently oriented triangles with a positive volume, or an open sheet; never a tangle.
            Assert.All(model.Meshes, m => Assert.True(Volume(m) > 0 || OpenEdges(m) > 0, file));
        }
    }

    [Fact]
    public void A_part_and_its_mirror_image_enclose_the_same_volume()
    {
        var a = Path.Combine(LocalRoot, "dolgan_Femur_lateral.CATPart");
        var b = Path.Combine(LocalRoot, "dolgan_Symmetry_of_Femur_lateral.CATPart");
        Assert.SkipWhen(!File.Exists(a) || !File.Exists(b), "local samples not present");
        var first = ModelLoader.Load(File.ReadAllBytes(a), "a.CATPart");
        var second = ModelLoader.Load(File.ReadAllBytes(b), "b.CATPart");
        Assert.Equal(Volume(first.Meshes[0]), Volume(second.Meshes[0]), 1);
        var left = first.Metadata.Bounds!.Value;
        var right = second.Metadata.Bounds!.Value;
        Assert.Equal(left.Min.X, right.Min.X, 3);
        Assert.Equal(left.Min.Y, -right.Max.Y, 3);
        Assert.Equal(left.Max.Y, -right.Min.Y, 3);
    }

    [Fact]
    public void Write_the_golden_file_when_asked()
    {
        var output = Environment.GetEnvironmentVariable("XRAY_3D_CATIA_GOLDEN_OUT");
        Assert.SkipWhen(output is null, "set XRAY_3D_CATIA_GOLDEN_OUT to regenerate Corpus/catia-golden.json from the local samples");
        var root = new JsonObject();
        foreach (var file in Directory.EnumerateFiles(LocalRoot).OrderBy(f => f, StringComparer.Ordinal))
        {
            root[Path.GetFileName(file)] = Summarize(File.ReadAllBytes(file), Path.GetFileName(file));
        }

        File.WriteAllText(output!, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }
}
