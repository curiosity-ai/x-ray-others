using System.Buffers.Binary;

namespace XRay.ThreeD.Tests;

/// <summary>DWG: version, header, sections, layers (AC1015, against the same drawing as DXF) and previews.</summary>
public class DwgTests
{
    private static readonly byte[] PreviewSentinel = [0x1F, 0x25, 0x6D, 0x07, 0xD4, 0x36, 0x28, 0x28, 0x9D, 0x57, 0xCA, 0x3F, 0x9D, 0x44, 0x10, 0x2B];

    [Fact]
    public void Header_sections_and_layers_of_an_R2000_drawing_match_its_dxf_twin()
    {
        var meta = ModelLoader.ReadMetadata(File.ReadAllBytes(DatasetPaths.File("dwg/flange-drawing.dwg")), "flange-drawing.dwg");

        Assert.Equal("dwg", meta.Format.Id);
        Assert.Equal("AC1015", meta.FormatVersion);
        Assert.Equal("AutoCAD 2000", meta.GetProperty("Release"));
        Assert.Equal("30", meta.GetProperty("Code page"));
        Assert.Equal("298", meta.GetProperty("Objects"));
        Assert.Contains("object map (1591 bytes)", meta.GetProperty("Sections"));
        Assert.True(meta.HasGeometry);

        // The same drawing exported to DXF lists these layers (read with ezdxf).
        Assert.Equal(["0", "Defpoints", "OUTLINE", "CENTER", "HIDDEN", "DIMS", "HATCH", "TITLE"], meta.LayerNames);
        var plan = ModelLoader.ReadMetadata(File.ReadAllBytes(DatasetPaths.File("dwg/studio-floor-plan.dwg")), "x.dwg");
        Assert.Equal(["0", "Defpoints", "WALLS", "DOORS", "WINDOWS", "FURNITURE", "LIGHTING", "TEXT", "HATCH", "DIMS"], plan.LayerNames);
    }

    [Fact]
    public void Releases_without_a_decoder_refuse_geometry_with_the_reason_and_files_without_preview_have_none()
    {
        var header = new byte[0x200];
        "AC1032"u8.CopyTo(header);
        var ex = Assert.Throws<UnsupportedModelFormatException>(() => ModelLoader.Load(header, "x.dwg"));
        Assert.Equal("dwg", ex.Format.Id);
        Assert.Contains("not decoded", ex.Message, StringComparison.Ordinal);
        Assert.Null(ModelLoader.ReadPreview(File.ReadAllBytes(DatasetPaths.File("dwg/flange-drawing.dwg")), "x.dwg"));
    }

    private static byte[] WithPreview(byte[] original, params (int Type, byte[] Data)[] images)
    {
        var file = original.ToList();
        var seeker = file.Count;
        var body = new List<byte>();
        var tableLength = 16 + 4 + 1 + images.Length * 9 + 16;
        var start = seeker + tableLength;
        var entries = new List<byte>();
        foreach (var (type, data) in images)
        {
            entries.Add((byte)type);
            entries.AddRange(BitConverter.GetBytes(start + body.Count));
            entries.AddRange(BitConverter.GetBytes(data.Length));
            body.AddRange(data);
        }

        file.AddRange(PreviewSentinel);
        file.AddRange(BitConverter.GetBytes(tableLength + body.Count));
        file.Add((byte)images.Length);
        file.AddRange(entries);
        file.AddRange(new byte[16]);
        file.AddRange(body);
        var bytes = file.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x0D), (uint)seeker);
        return bytes;
    }

    [Fact]
    public void Preview_images_are_read_as_png_wmf_or_dib()
    {
        var original = File.ReadAllBytes(DatasetPaths.File("dwg/flange-drawing.dwg"));
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

        var direct = ModelLoader.ReadPreview(WithPreview(original, (4, png)), "x.dwg");
        Assert.Equal("image/png", direct!.MediaType);
        Assert.Equal(png, direct.Data);

        // A 2 x 1 pixel 24-bit DIB (no file header), rows padded to four bytes.
        var dib = new List<byte>();
        dib.AddRange(BitConverter.GetBytes(40));
        dib.AddRange(BitConverter.GetBytes(2));
        dib.AddRange(BitConverter.GetBytes(1));
        dib.AddRange(new byte[] { 1, 0, 24, 0 });
        dib.AddRange(new byte[24]);
        dib.AddRange(new byte[] { 0, 0, 255, 0, 255, 0, 0, 0 });
        var converted = ModelLoader.ReadPreview(WithPreview(original, (1, new byte[10]), (2, dib.ToArray())), "x.dwg");
        Assert.Equal("image/png", converted!.MediaType);
        Assert.Equal(0x89, converted.Data[0]);

        var wmf = new byte[] { 0xD7, 0xCD, 0xC6, 0x9A, 0, 0, 0, 0 };
        Assert.Equal("image/wmf", ModelLoader.ReadPreview(WithPreview(original, (3, wmf)), "x.dwg")!.MediaType);
        Assert.True(ModelLoader.ReadMetadata(WithPreview(original, (4, png)), "x.dwg").HasPreview);
    }

    [Fact]
    public void Later_releases_report_their_version_and_say_what_is_not_decoded()
    {
        var header = new byte[0x200];
        "AC1032"u8.CopyTo(header);
        var meta = ModelLoader.ReadMetadata(header, "x.dwg");

        Assert.Equal("AC1032", meta.FormatVersion);
        Assert.Equal("AutoCAD 2018", meta.GetProperty("Release"));
        Assert.Contains(meta.Notes, n => n.Contains("compressed, encrypted", StringComparison.Ordinal));
        Assert.Empty(meta.LayerNames);
    }
}
