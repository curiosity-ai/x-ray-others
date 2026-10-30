using System.Text.Json;

namespace XRay.ThreeD.Tests;

/// <summary>DWF 6 (W2D text streams) against a second parse of the same streams, and a hand-written DWFx (XPS) page.</summary>
public class DwfTests
{
    public static IEnumerable<object[]> Files() => DatasetPaths.Files("dwf", "*.dwf").Select(f => new object[] { "dwf/" + Path.GetFileName(f) });

    [Theory]
    [MemberData(nameof(Files))]
    public void W2d_geometry_text_and_paper_match_the_independent_parse(string relative)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(DatasetPaths.CorpusFile("dwf-golden.json")));
        var golden = doc.RootElement.GetProperty(Path.GetFileName(relative));
        var model = ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File(relative)), Path.GetFileName(relative));
        var scale = golden.GetProperty("scale").GetDouble();

        Assert.Equal("dwf", model.Metadata.Format.Id);
        Assert.True(model.Is2D);
        Assert.Equal(LengthUnit.Inch, model.Metadata.Units);
        var sheet = Assert.Single(model.Metadata.DrawingSheets);
        Assert.Equal(golden.GetProperty("width").GetDouble() * 0.0254, sheet.WidthMeters, 6);
        Assert.Equal(golden.GetProperty("height").GetDouble() * 0.0254, sheet.HeightMeters, 6);

        var points = model.Meshes.Sum(m => (long)m.VertexCount);
        var segments = model.Meshes.Sum(m => (long)m.EdgeIndices.Length / 2);
        Assert.Equal(golden.GetProperty("points").GetInt64(), points);
        Assert.Equal(golden.GetProperty("points").GetInt64() - golden.GetProperty("polylines").GetInt64(), segments);
        Assert.Equal(golden.GetProperty("texts").GetInt32(), model.Texts.Count);
        Assert.Equal(golden.GetProperty("firstText").GetString(), model.Texts[0].Text);
        Assert.Equal(golden.GetProperty("lastText").GetString(), model.Texts[^1].Text);
        Assert.Equal(golden.GetProperty("fontHeights").EnumerateArray().Select(h => Math.Round(h.GetDouble() * scale, 4)).OrderBy(h => h), model.Texts.Select(t => Math.Round(t.Height, 4)).Distinct().OrderBy(h => h));
        Assert.True(model.Materials.Count <= golden.GetProperty("colours").GetInt32() && model.Materials.Count >= 1);

        var min = golden.GetProperty("min").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        var max = golden.GetProperty("max").EnumerateArray().Select(v => v.GetDouble()).ToArray();
        var box = model.Metadata.Bounds!.Value;
        Assert.Equal(min[0], box.Min.X, 3);
        Assert.Equal(min[1], box.Min.Y, 3);
        Assert.Equal(max[0], box.Max.X, 3);
        Assert.Equal(max[1], box.Max.Y, 3);

        // A drawing can be written as SVG.
        Assert.Contains("<svg", System.Text.Encoding.UTF8.GetString(ModelLoader.ConvertToSvg(File.ReadAllBytes(DatasetPaths.File(relative)), Path.GetFileName(relative))), StringComparison.Ordinal);
    }

    [Fact]
    public void A_dwfx_page_gives_paths_curves_fills_and_glyphs_in_page_units()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/drawing.dwfx"));
        var model = ModelLoader.Load(data, "drawing.dwfx");

        Assert.Equal("dwf", model.Metadata.Format.Id);
        Assert.Equal("DWFx (XPS package)", model.Metadata.FormatVersion);
        Assert.Equal("Synthetic sheet", model.Metadata.Title);
        var sheet = Assert.Single(model.Metadata.DrawingSheets);
        Assert.Equal(2 * 0.0254, sheet.WidthMeters, 6);
        Assert.Equal(1 * 0.0254, sheet.HeightMeters, 6);

        // The rectangle is stroked blue (closed: five points) and the curve is black (a start point and twelve flattened steps).
        var blue = model.Meshes.Single(m => model.Materials[m.EdgeMaterial].BaseColor == new System.Numerics.Vector4(0, 0, 1, 1));
        Assert.Equal(5, blue.VertexCount);
        var rect = blue.ComputeBounds();
        Assert.Equal(0, rect.Min.X, 4);
        Assert.Equal(1, rect.Max.X, 4);
        Assert.Equal(0.5, rect.Min.Y, 4); // page space is flipped: the rectangle hangs from the top edge of the 1 inch page
        Assert.Equal(1, rect.Max.Y, 4);
        var black = model.Meshes.Single(m => model.Materials[m.EdgeMaterial].BaseColor == new System.Numerics.Vector4(0, 0, 0, 1));
        Assert.Equal(13, black.VertexCount);
        Assert.Equal(1.0, black.ComputeBounds().Min.X, 4); // the canvas moved it right by 96 units
        Assert.Equal(1.3125, black.ComputeBounds().Max.X, 4);

        var fill = Assert.Single(model.Fills);
        Assert.Equal(3, fill.Loops[0].Length);
        Assert.Equal(0x00AA00, fill.Rgb);
        Assert.Equal(1.5, fill.Loops[0].Max(p => p.X), 4);

        var text = Assert.Single(model.Texts);
        Assert.Equal("Hello DWFx", text.Text);
        Assert.Equal(100 / 96.0, text.Position.X, 4);
        Assert.Equal(6 / 96.0, text.Position.Y, 4);
        Assert.Equal(12 / 96.0, text.Height, 4);
        Assert.True(model.Is2D);
        Assert.Contains("Hello DWFx", System.Text.Encoding.UTF8.GetString(ModelLoader.ConvertToSvg(data, "drawing.dwfx")), StringComparison.Ordinal);
    }

    [Fact]
    public void Binary_streams_and_3d_sections_are_reported_not_misread()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("dwf/usfs-rtr2-tent-platform.dwf"));
        // Rewrite the stream header so that it is no longer the text form: the reader must say so instead of drawing garbage.
        using var input = new MemoryStream(data[12..]);
        using var output = new MemoryStream();
        output.Write(data, 0, 12);
        using (var zip = new System.IO.Compression.ZipArchive(output, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        using (var source = new System.IO.Compression.ZipArchive(input))
        {
            foreach (var entry in source.Entries)
            {
                using var read = entry.Open();
                using var buffer = new MemoryStream();
                read.CopyTo(buffer);
                var bytes = buffer.ToArray();
                if (entry.FullName.EndsWith(".w2d", StringComparison.Ordinal))
                {
                    bytes = "{W2D V06.00}"u8.ToArray(); // the binary form starts with a brace
                }

                var created = zip.CreateEntry(entry.FullName);
                using var write = created.Open();
                write.Write(bytes);
            }
        }

        var model = ModelLoader.Load(output.ToArray(), "x.dwf");
        Assert.Contains(model.Warnings, w => w.Contains("binary W2D", StringComparison.Ordinal));
        Assert.Empty(model.Meshes);
    }
}
