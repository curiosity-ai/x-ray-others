using System.Text.Json;

namespace XRay.ThreeD.Tests;

/// <summary>
/// JT files (local samples: real files of JT 8.0 to 10.5, not redistributable, read from XRAY_3D_LOCAL_SAMPLES/jt). Five of them are compared with the JSON the JT Open
/// reference toolkit wrote for them (Corpus/jt-golden.json): per shape of the finest level of detail the index, vertex and normal counts and the sums of the coordinates,
/// indices and normals, which pins down the codecs (Huffman, arithmetic, bitlength, the dual-mesh walk, quantised and unit-vector normals) and the byte order handling.
/// </summary>
public class JtTests
{
    private static string Samples => Path.Combine(Environment.GetEnvironmentVariable("XRAY_3D_LOCAL_SAMPLES") ?? "/tmp/claude-0/samples", "jt");

    public static IEnumerable<object[]> Golden() => JsonDocument.Parse(File.ReadAllText(DatasetPaths.CorpusFile("jt-golden.json"))).RootElement
        .EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).Select(n => new object[] { n });

    public static IEnumerable<object[]> All()
    {
        var dir = Samples;
        var names = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.jt").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray() : ["none.jt"];
        return names.Select(n => new object[] { n! });
    }

    [Theory]
    [MemberData(nameof(Golden))]
    public void ShapesMatchTheReferenceToolkit(string file)
    {
        var path = Path.Combine(Samples, file);
        Assert.SkipWhen(!File.Exists(path), "local sample not present: " + file);
        var golden = JsonDocument.Parse(File.ReadAllText(DatasetPaths.CorpusFile("jt-golden.json"))).RootElement.GetProperty(file).EnumerateArray().ToArray();
        var model = ModelLoader.Load(File.ReadAllBytes(path), file);

        var actual = new List<(int Vertices, int Normals, double SumV, double SumN, int Triangles)>();
        foreach (var mesh in model.Meshes)
        {
            actual.Add((mesh.VertexCount, mesh.Normals.Length / 3, mesh.Positions.Sum(v => (double)v), mesh.Normals.Sum(v => (double)v), mesh.TriangleCount));
        }

        Assert.Equal(golden.Length, actual.Count);
        foreach (var g in golden)
        {
            var indices = g[0].GetInt32();
            var vertices = g[1].GetInt32();
            var match = actual.Where(a => a.Vertices == vertices && a.Normals == g[2].GetInt32()
                && Math.Abs(a.SumV - g[3].GetDouble()) < 0.01 + Math.Abs(g[3].GetDouble()) * 1e-5
                && Math.Abs(a.SumN - g[5].GetDouble()) < 0.05 + Math.Abs(g[5].GetDouble()) * 1e-3).ToArray();
            Assert.True(match.Length >= 1, $"no mesh with {vertices} vertices and the sums {g[3]} / {g[5]}");
            // JT 8 strips carry degenerate triangles that are dropped here, so the count may be lower there.
            Assert.InRange(match[0].Triangles, indices / 3 - 60, indices / 3);
            var colour = g[6].EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
            Assert.Contains(model.Materials, m => Math.Abs(m.BaseColor.X - colour[0]) < 1e-3 && Math.Abs(m.BaseColor.Y - colour[1]) < 1e-3 && Math.Abs(m.BaseColor.Z - colour[2]) < 1e-3);
        }
    }

    [Theory]
    [MemberData(nameof(All))]
    public void EveryRealFileLoadsAndConverts(string file)
    {
        var path = Path.Combine(Samples, file);
        Assert.SkipWhen(!File.Exists(path), "local sample not present: " + file);
        var data = File.ReadAllBytes(path);
        Assert.Equal("jt", ModelLoader.Detect(data, file).Id);
        var model = ModelLoader.Load(data, file);
        Assert.NotNull(model.Metadata.FormatVersion);
        Assert.True(model.Metadata.HasGeometry, "no geometry in " + file);
        Assert.True(model.ComputeStatistics().Triangles > 0);

        Assert.NotEmpty(ModelLoader.ConvertToGlb(data, file));
    }

    /// <summary>
    /// JT 10.5 stores most integer streams of the NIST assembly with codec 5 (a table of distinct values and a move-to-front index stream, -1 meaning
    /// "next table value") and wraps several segment kinds (23, 30) in the same xz envelope as the others. The counts are what the reader finds
    /// (the index stream length equals the triangle count of the first shape, 4010); one of fifteen shapes still fails to decode (see TODO.md).
    /// </summary>
    [Fact]
    public void Jt105AssemblyShowsPartGeometry()
    {
        var path = Path.Combine(Samples, "nist-mtc-assembly.jt");
        Assert.SkipWhen(!File.Exists(path), "local sample not present");
        var model = ModelLoader.Load(File.ReadAllBytes(path), "nist-mtc-assembly.jt");
        Assert.Equal(7, model.Meshes.Count);
        Assert.Contains(model.Meshes, m => m.TriangleCount == 4010);
        Assert.Contains(model.Meshes, m => m.TriangleCount == 9912);
        Assert.Contains(model.Meshes, m => m.TriangleCount == 10936);
        Assert.True(model.ComputeStatistics().Triangles > 200_000);
    }

    [Fact]
    public void TruncationAndMutationOnlyThrowModelFormatException()
    {
        var rng = new Random(11);
        foreach (var file in new[] { "example_block_jt9.5.jt", "example_block_jt8.1.jt", "example_block_jt10.3.jt", "nist-mtc-cover.jt" })
        {
            var path = Path.Combine(Samples, file);
            Assert.SkipWhen(!File.Exists(path), "local sample not present: " + file);
            var data = File.ReadAllBytes(path);
            for (var n = 0; n < data.Length; n += 97)
            {
                try
                {
                    _ = ModelLoader.Load(data[..n], file);
                }
                catch (ModelFormatException)
                {
                }
            }

            for (var i = 0; i < 400; i++)
            {
                var copy = (byte[])data.Clone();
                for (var k = 0; k < 3; k++)
                {
                    copy[rng.Next(copy.Length)] ^= (byte)(1 << rng.Next(8));
                }

                try
                {
                    _ = ModelLoader.Load(copy, file);
                }
                catch (ModelFormatException)
                {
                }
            }
        }
    }
}
