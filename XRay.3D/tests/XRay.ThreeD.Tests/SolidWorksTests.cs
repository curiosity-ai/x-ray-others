using System.Numerics;
using System.Text.Json;

namespace XRay.ThreeD.Tests;

public class SolidWorksTests
{
    private static readonly JsonElement Golden = JsonDocument.Parse(File.ReadAllText(DatasetPaths.CorpusFile("solidworks-golden.json"))).RootElement;

    public static IEnumerable<object[]> Files() =>
        Golden.EnumerateObject().Select(p => new object[] { p.Name });

    private static string PathOf(string file) =>
        DatasetPaths.File($"{Path.GetExtension(file).TrimStart('.').ToLowerInvariant()}/{file}");

    [Theory]
    [MemberData(nameof(Files))]
    public void Matches_the_reference_reader(string file)
    {
        var expected = Golden.GetProperty(file);
        var model = ModelLoader.Load(PathOf(file));
        var meta = model.Metadata;

        Assert.Equal("solidworks", meta.Format.Id);
        Assert.Equal(expected.GetProperty("kind").GetString(), meta.DocumentKind);
        Assert.Equal(
            expected.GetProperty("componentFiles").EnumerateArray().Select(e => e.GetString()!),
            meta.ComponentFiles);

        if (!expected.TryGetProperty("vertices", out _))
        {
            Assert.False(meta.HasGeometry);
            return;
        }

        var stats = model.ComputeStatistics();
        Assert.Equal(expected.GetProperty("vertices").GetInt64(), stats.Vertices);
        Assert.Equal(expected.GetProperty("triangles").GetInt64(), stats.Triangles);

        // World coordinates of every drawn vertex, summed per axis, against the reference reader's sums.
        var sum = new double[3];
        long count = 0;
        void Visit(SceneNode node, Matrix4x4 parent)
        {
            var world = node.Transform * parent;
            if (node.Mesh is { } mesh)
            {
                for (var i = 0; i < mesh.Positions.Length; i += 3)
                {
                    var p = Vector3.Transform(new Vector3(mesh.Positions[i], mesh.Positions[i + 1], mesh.Positions[i + 2]), world);
                    sum[0] += p.X;
                    sum[1] += p.Y;
                    sum[2] += p.Z;
                    count++;
                }
            }

            foreach (var child in node.Children)
            {
                Visit(child, world);
            }
        }

        foreach (var root in model.Roots)
        {
            Visit(root, Matrix4x4.Identity);
        }

        Assert.Equal(expected.GetProperty("vertices").GetInt64(), count);
        var expectedSum = expected.GetProperty("sum").EnumerateArray().Select(e => e.GetDouble()).ToArray();
        for (var axis = 0; axis < 3; axis++)
        {
            Assert.InRange(sum[axis] - expectedSum[axis], -0.05, 0.05);
        }

        // Face colours: one entry per drawn face, instances counted per use.
        var histogram = new Dictionary<string, int>();
        foreach (var node in model.Nodes().Where(n => n.Mesh is not null))
        {
            var mesh = node.Mesh!;
            Assert.Equal(mesh.Faces.Sum(f => f.IndexCount), mesh.Indices.Length);
            foreach (var face in mesh.Faces)
            {
                var group = mesh.Groups.First(g => face.FirstIndex >= g.Start && face.FirstIndex < g.Start + g.Count);
                var key = group.Material < 0
                    ? "none"
                    : ((int)Math.Round(model.Materials[group.Material].BaseColor.X * 255) << 16 | (int)Math.Round(model.Materials[group.Material].BaseColor.Y * 255) << 8 | (int)Math.Round(model.Materials[group.Material].BaseColor.Z * 255)).ToString("x6");
                histogram[key] = histogram.GetValueOrDefault(key) + 1;
            }
        }

        var expectedColors = expected.GetProperty("faceColors").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32());
        Assert.Equal(expectedColors.OrderBy(k => k.Key), histogram.OrderBy(k => k.Key));
        Assert.Equal(expected.GetProperty("faces").GetInt32(), histogram.Values.Sum());

        var bounds = model.ComputeBounds()!.Value;
        var min = expected.GetProperty("min").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
        var max = expected.GetProperty("max").EnumerateArray().Select(e => (float)e.GetDouble()).ToArray();
        Assert.Equal(min[0], bounds.Min.X, 4);
        Assert.Equal(min[1], bounds.Min.Y, 4);
        Assert.Equal(min[2], bounds.Min.Z, 4);
        Assert.Equal(max[0], bounds.Max.X, 4);
        Assert.Equal(max[1], bounds.Max.Y, 4);
        Assert.Equal(max[2], bounds.Max.Z, 4);
    }

    [Fact]
    public void Assembly_exposes_instances_names_and_properties()
    {
        var model = ModelLoader.Load(DatasetPaths.File("sldasm/nist-mtc-assembly.sldasm"));
        var meta = model.Metadata;

        Assert.Equal("assembly", meta.DocumentKind);
        Assert.Equal(LengthUnit.Meter, meta.Units);
        Assert.StartsWith("SOLIDWORKS 2015", meta.Application);
        Assert.Equal("nist_mtc_crada_assembly_rev-D", meta.Title);
        Assert.Equal("NIST-MTC-005", meta.GetProperty("Part Number"));
        Assert.Equal("tdh1", meta.LastModifiedBy);
        Assert.Equal(8, meta.ComponentFiles.Count);
        Assert.Contains("nist_mtc_crada_box_rev-D", meta.PartNames);
        Assert.Contains("Default", meta.Configurations);

        var root = Assert.Single(model.Roots);
        Assert.Equal(36, root.Children.Count);
        Assert.Equal("nist_mtc_crada_box_rev-D-1", root.Children[0].Name);
        Assert.Equal("nist_mtc_crada_box_rev-D-1@nist_mtc_crada_assembly_rev-D", root.Children[0].Properties["instancePath"]);
        Assert.Equal("nist_mtc_crada_box_rev-D.SLDPRT", root.Children[0].Properties["file"]);

        // Four instances of one screw share one mesh.
        var screws = root.Children.Where(c => c.Name!.StartsWith("91274A141", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, screws.Count);
        Assert.Single(screws.Select(s => s.Mesh).Distinct());
        Assert.Equal(8, model.Meshes.Count);
        Assert.NotNull(model.Preview);
        Assert.Equal("image/png", model.Preview!.MediaType);
    }

    [Fact]
    public void Metadata_only_read_agrees_with_full_load()
    {
        var path = DatasetPaths.File("sldasm/nist-mtc-assembly.sldasm");
        var quick = ModelLoader.ReadMetadata(path);
        var full = ModelLoader.Load(path).Metadata;

        // The display lists are not inflated for metadata alone, so there are no counts; everything else is the same.
        Assert.Null(quick.Statistics);
        Assert.Equal(full.PartNames, quick.PartNames);
        Assert.Equal(full.Properties.Count, quick.Properties.Count);
        Assert.True(quick.HasGeometry);
    }

    [Fact]
    public void Drawing_has_sheets_and_no_geometry()
    {
        var model = ModelLoader.Load(DatasetPaths.File("slddrw/rocket-fin-set.slddrw"));

        Assert.Equal("drawing", model.Metadata.DocumentKind);
        Assert.False(model.Metadata.HasGeometry);
        Assert.Contains("Sheet1", model.Metadata.Sheets);
        Assert.Empty(model.Roots);
    }

    [Fact]
    public void Rejects_files_that_are_not_solidworks_documents()
    {
        var ex = Assert.Throws<UnsupportedModelFormatException>(() => ModelLoader.Load(new byte[2048], "part.sldprt"));
        Assert.Equal("solidworks", ex.Format.Id);
    }
}
