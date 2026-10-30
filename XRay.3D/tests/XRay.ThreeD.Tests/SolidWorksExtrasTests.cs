using System.IO.Compression;
using System.Numerics;
using System.Text;
using XRay.ThreeD.Formats.SolidWorks;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Tests;

/// <summary>Drawings, metadata without inflating the display lists, external assembly components, and the bitmap decoder behind the preview.</summary>
public class SolidWorksExtrasTests
{
    private static readonly string[] Files =
    [
        "sldprt/servo-mount.sldprt", "sldprt/muon-detector-case.sldprt", "sldprt/nist-ctc-02-part.sldprt", "sldprt/nist-ftc-10-part.sldprt",
        "sldasm/nist-mtc-assembly.sldasm", "slddrw/rocket-fin-set.slddrw",
    ];

    [Fact]
    public void Header_only_open_finds_the_entries_the_checked_open_does()
    {
        foreach (var file in Files)
        {
            var data = File.ReadAllBytes(DatasetPaths.File(file));
            var checkedOpen = SwContainer.TryOpen(data, TestContext.Current.CancellationToken)!;
            var lazy = SwContainer.TryOpen(data, TestContext.Current.CancellationToken, verify: false)!;

            Assert.Equal(checkedOpen.Names.Order(StringComparer.Ordinal), lazy.Names.Order(StringComparer.Ordinal));
            foreach (var name in checkedOpen.Names)
            {
                Assert.Equal(checkedOpen.RangeOf(name), lazy.RangeOf(name));
            }
        }
    }

    [Fact]
    public void Metadata_alone_never_inflates_the_display_lists()
    {
        var path = DatasetPaths.File("sldprt/servo-mount.sldprt");
        var data = File.ReadAllBytes(path);
        var range = SwContainer.TryOpen(data, TestContext.Current.CancellationToken)!.RangeOf("Contents/DisplayLists")!.Value;
        var expected = ModelLoader.ReadMetadata(data, "servo-mount.sldprt");

        // Damage the middle of the stream: its compressed bytes no longer inflate, and nothing notices.
        var damaged = (byte[])data.Clone();
        for (var i = range.Length / 3; i < range.Length * 2 / 3; i++)
        {
            damaged[range.Start + i] ^= 0xA5;
        }

        var meta = ModelLoader.ReadMetadata(damaged, "servo-mount.sldprt");
        Assert.True(meta.HasGeometry);
        Assert.Null(meta.Statistics);
        Assert.Equal(expected.Properties.Count, meta.Properties.Count);
        Assert.Equal(expected.Title, meta.Title);
        Assert.Equal("solidworks", ModelLoader.Detect(damaged, "x.sldprt").Id);
    }

    [Fact]
    public void Drawing_sheets_become_images_at_paper_size_and_write_as_svg()
    {
        var model = ModelLoader.Load(DatasetPaths.File("slddrw/rocket-fin-set.slddrw"));

        Assert.True(model.Is2D);
        var image = Assert.Single(model.Images);
        Assert.Equal("Sheet1", image.Name);
        Assert.Equal(0.5588, image.Width, 4);
        Assert.Equal(0.4318, image.Height, 4);
        Assert.Equal("Sheet1", model.Layers[image.Layer].Name);
        var sheet = Assert.Single(model.Metadata.DrawingSheets);
        Assert.Equal(("Sheet1", 0.5588, 0.4318), (sheet.Name, Math.Round(sheet.WidthMeters, 4), Math.Round(sheet.HeightMeters, 4)));
        Assert.Contains("558.8 x 431.8 mm", model.Metadata.GetProperty("sheet size: Sheet1"));

        // The first sheet uses the larger Preview bitmap with its letterbox bars trimmed (the saved sheet image is 640 x 480).
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], image.Data.Take(4));
        var width = (image.Data[16] << 24) | (image.Data[17] << 16) | (image.Data[18] << 8) | image.Data[19];
        Assert.True(width > 1500, $"image width {width}");

        var svg = Encoding.UTF8.GetString(ModelLoader.ConvertToSvg(File.ReadAllBytes(DatasetPaths.File("slddrw/rocket-fin-set.slddrw")), "rocket-fin-set.slddrw"));
        Assert.Contains("<image", svg, StringComparison.Ordinal);
        Assert.Contains("data:image/png;base64,iVBOR", svg, StringComparison.Ordinal);
        Assert.Contains("viewBox=\"", svg, StringComparison.Ordinal);
        Assert.Contains("Sheet1", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void Dib_decoder_reads_rle8_and_truecolour_and_trims_bars()
    {
        // 4 x 2 RLE8: bottom row (stored first) = palette 1 x4; top row = 2 x2, then absolute run of 0, 1.
        var palette = new byte[1024];
        palette[4] = 255; // index 1 = blue (BGRA)
        palette[8 + 2] = 255; // index 2 = red
        var rle = new byte[] { 4, 1, 0, 0, 2, 2, 0, 2, 0, 1, 0, 0, 0, 1 };
        var header = new byte[40];
        BitConverter.GetBytes(40).CopyTo(header, 0);
        BitConverter.GetBytes(4).CopyTo(header, 4);
        BitConverter.GetBytes(2).CopyTo(header, 8);
        BitConverter.GetBytes((short)1).CopyTo(header, 12);
        BitConverter.GetBytes((short)8).CopyTo(header, 14);
        BitConverter.GetBytes(1).CopyTo(header, 16);
        var dib = header.Concat(palette).Concat(rle).ToArray();

        var rgb = DibImage.DecodeRgb(dib, out var w, out var h, TestContext.Current.CancellationToken)!;
        Assert.Equal((4, 2), (w, h));
        Assert.Equal([255, 0, 0], rgb.Take(3));      // top row first: red
        Assert.Equal([0, 0, 0], rgb.Skip(6).Take(3)); // absolute run: palette 0 (black)
        Assert.Equal([0, 0, 255], rgb.Skip(12).Take(3)); // bottom row: blue

        // 24-bit, bottom-up, rows padded to four bytes: 1 x 2, bottom pixel (BGR 1,2,3), top pixel (4,5,6).
        var h24 = new byte[40];
        BitConverter.GetBytes(40).CopyTo(h24, 0);
        BitConverter.GetBytes(1).CopyTo(h24, 4);
        BitConverter.GetBytes(2).CopyTo(h24, 8);
        BitConverter.GetBytes((short)1).CopyTo(h24, 12);
        BitConverter.GetBytes((short)24).CopyTo(h24, 14);
        var truecolour = DibImage.DecodeRgb(h24.Concat(new byte[] { 1, 2, 3, 0, 4, 5, 6, 0 }).ToArray(), out w, out h, TestContext.Current.CancellationToken)!;
        Assert.Equal([6, 5, 4, 3, 2, 1], truecolour);

        // Bars of the corner colour are trimmed from all sides.
        var barred = new byte[5 * 3 * 3];
        Array.Fill(barred, (byte)255);
        barred[(1 * 5 + 2) * 3] = 0; // one dark pixel at (2, 1)
        int bw = 5, bh = 3;
        var trimmed = DibImage.Trim(barred, ref bw, ref bh);
        Assert.Equal((1, 1), (bw, bh));
        Assert.Equal(0, trimmed[0]);
        Assert.Null(DibImage.DecodeRgb(new byte[100], out _, out _, TestContext.Current.CancellationToken));
        var png = DibImage.EncodePng(trimmed, bw, bh);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], png.Take(4));
    }

    // ---- an assembly whose components are external ----------------------------------------------------------------------

    private static byte[] SwString(string text) => [0xFF, 0xFE, 0xFF, (byte)text.Length, .. Encoding.Unicode.GetBytes(text)];

    private static byte[] Instance(string name, double x, double y, double z) =>
        [.. SwString(name), 0, .. BitConverter.GetBytes(x), .. BitConverter.GetBytes(y), .. BitConverter.GetBytes(z), .. BitConverter.GetBytes(1.0)];

    /// <summary>A SolidWorks-style container: entries with nibble-swapped names and raw deflate, a decoy first.</summary>
    private static byte[] Container(params (string Name, byte[] Data)[] entries)
    {
        using var output = new MemoryStream();
        void Write(string name, byte[] data, bool decoy)
        {
            using var packed = new MemoryStream();
            using (var deflate = new DeflateStream(packed, CompressionLevel.Optimal, leaveOpen: true))
            {
                deflate.Write(data);
            }

            var compressed = packed.ToArray();
            var swapped = Encoding.UTF8.GetBytes(name).Select(b => (byte)((b >> 4) | (b << 4))).ToArray();
            output.Write([0x53, 0x57, 0x58, 0x31, 0x14, 0x00, 0x06, 0x00, 0x08, 0x00, 0, 0, 0, 0]);
            output.Write(BitConverter.GetBytes(Crc32.Update(0, data)));
            output.Write(BitConverter.GetBytes(decoy ? compressed.Length + 4000 : compressed.Length));
            output.Write(BitConverter.GetBytes(data.Length));
            output.Write(BitConverter.GetBytes((ushort)swapped.Length));
            output.Write(BitConverter.GetBytes((ushort)0));
            output.Write(swapped);
            output.Write(compressed);
            if (decoy)
            {
                output.Write(new byte[4000]);
            }
        }

        Write("PreviewPNG", [1, 2, 3], decoy: true);
        foreach (var (name, data) in entries)
        {
            Write(name, data, decoy: false);
        }

        return output.ToArray();
    }

    private static byte[] ExternalAssembly(params byte[][] instances)
    {
        const string tree = """<?xml version="1.0"?><swSolidWorks><swHeader><swFile id="3" swDocType="ASSEMBLY" swPath="C:\a\frame.SLDASM"/><swFile id="6" swDocType="PART" swPath="C:\a\servo-mount.SLDPRT"/><swFile id="9" swDocType="PART" swPath="C:\a\gone.SLDPRT"/></swHeader><swModelList><swModel id="5" swName="servo-mount" swFileRef="6"/></swModelList></swSolidWorks>""";
        const string app = """<?xml version="1.0"?><Properties><Application>SOLIDWORKS</Application><AppVersion>23.0</AppVersion></Properties>""";
        return Container(
            ("docProps/app.xml", Encoding.UTF8.GetBytes(app)),
            ("swXmlContents/COMPINSTANCETREE", Encoding.UTF8.GetBytes(tree)),
            ("swXmlContents/KeyWords", Encoding.UTF8.GetBytes("""<?xml version="1.0"?><KeyWords Name="frame"/>""")),
            ("Contents/DisplayLists", instances.SelectMany(i => i).Concat(new byte[16]).ToArray()));
    }

    [Fact]
    public void External_components_are_merged_under_the_instance_transforms()
    {
        var assembly = ExternalAssembly(Instance("servo-mount-1@frame", 1, 2, 3), Instance("servo-mount-2@frame", 0.5, 0, 0), Instance("gone-1@frame", 0, 0, 0));
        var part = File.ReadAllBytes(DatasetPaths.File("sldprt/servo-mount.sldprt"));
        var asked = new List<string>();
        var options = new LoadOptions
        {
            ResolveCompanion = name =>
            {
                asked.Add(name);
                return name == "servo-mount.SLDPRT" ? part : null;
            },
        };

        Assert.Equal("solidworks", ModelLoader.Detect(assembly, "frame.sldasm").Id);
        var alone = ModelLoader.Load(assembly, "frame.sldasm");
        Assert.Empty(alone.Meshes);
        Assert.Contains("servo-mount.SLDPRT", alone.Metadata.ComponentFiles);

        var model = ModelLoader.Load(assembly, "frame.sldasm", options);
        var single = ModelLoader.Load(part, "servo-mount.sldprt");

        var root = Assert.Single(model.Roots);
        Assert.Equal(2, root.Children.Count);
        Assert.Equal(["servo-mount-1", "servo-mount-2"], root.Children.Select(c => c.Name));
        Assert.Equal(new Vector3(1, 2, 3), root.Children[0].Transform.Translation);
        Assert.Equal(new Vector3(0.5f, 0, 0), root.Children[1].Transform.Translation);
        // One part loaded once, drawn twice.
        Assert.Single(model.Meshes.Distinct());
        Assert.Equal(single.Meshes.Count, model.Meshes.Count);
        Assert.Equal(2 * single.ComputeStatistics().Triangles, model.ComputeStatistics().Triangles);
        Assert.Equal(single.ComputeBounds()!.Value.Size, model.ComputeBounds()!.Value.Size - new Vector3(0.5f, 2, 3), new VectorComparer());
        Assert.Contains(model.Warnings, w => w.Contains("gone.SLDPRT", StringComparison.Ordinal));
        Assert.Equal(1, asked.Count(a => a == "servo-mount.SLDPRT"));
        Assert.True(model.Metadata.HasGeometry);
        GlbValidation.Validate(GlbWriter.ToArray(model));
    }

    [Fact]
    public void Damaged_external_assemblies_and_cancellation_fail_cleanly()
    {
        var assembly = ExternalAssembly(Instance("servo-mount-1@frame", 1, 2, 3));
        var part = File.ReadAllBytes(DatasetPaths.File("sldprt/servo-mount.sldprt"));
        var options = new LoadOptions { ResolveCompanion = _ => part };
        for (var length = 0; length < assembly.Length; length += Math.Max(1, assembly.Length / 80))
        {
            try
            {
                ModelLoader.Load(assembly.AsMemory(0, length), "x.sldasm", options);
            }
            catch (ModelFormatException)
            {
            }
        }

        // A companion that is not a model at all is a warning-free miss, not a crash; a cancelled token stops the merge.
        var junk = new LoadOptions { ResolveCompanion = _ => new byte[] { 1, 2, 3 } };
        try
        {
            ModelLoader.Load(assembly, "x.sldasm", junk);
        }
        catch (ModelFormatException)
        {
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ModelLoader.Load(assembly, "x.sldasm", new LoadOptions { ResolveCompanion = _ => part, Cancellation = cts.Token }));
    }

    private sealed class VectorComparer : IEqualityComparer<Vector3>
    {
        public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 1e-3f;

        public int GetHashCode(Vector3 v) => 0;
    }
}
