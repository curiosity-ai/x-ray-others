using System.Text.Json;

namespace XRay.ThreeD.Tests;

/// <summary>OLE compound files (Inventor, Revit): properties as olefile reads them, previews, and a hostile container.</summary>
public class CompoundDocumentTests
{
    private static readonly JsonElement Golden = JsonDocument.Parse(File.ReadAllText(DatasetPaths.CorpusFile("cfb-golden.json"))).RootElement;

    [Fact]
    public void Streams_of_both_sectors_sizes_are_read_through_the_fat_and_mini_fat()
    {
        foreach (var name in new[] { "part.ipt", "model.rvt" })
        {
            var file = new Internal.CompoundFile(File.ReadAllBytes(DatasetPaths.File("synthetic/" + name)), default);
            var expected = Golden.GetProperty(name).GetProperty("streams").EnumerateArray().Select(e => e.GetString()!).Order(StringComparer.Ordinal);
            Assert.Equal(expected, file.StreamNames.Order(StringComparer.Ordinal));
        }

        var part = new Internal.CompoundFile(File.ReadAllBytes(DatasetPaths.File("synthetic/part.ipt")), default);
        var big = part.Read("RSeStorageData", default)!;
        Assert.Equal(9000, big.Length);
        Assert.Equal(Golden.GetProperty("part.ipt").GetProperty("big_sum").GetInt64(), big.Sum(b => (long)b));
        Assert.Equal("tiny stream", System.Text.Encoding.ASCII.GetString(part.Read("Small", default)!));
        Assert.Equal(5000, new Internal.CompoundFile(File.ReadAllBytes(DatasetPaths.File("synthetic/model.rvt")), default).Read("Formats/Latest", default)!.Length);
    }

    [Fact]
    public void Inventor_properties_match_olefile()
    {
        var expected = Golden.GetProperty("part.ipt");
        var meta = ModelLoader.ReadMetadata(File.ReadAllBytes(DatasetPaths.File("synthetic/part.ipt")), "part.ipt");

        Assert.Equal("inventor", meta.Format.Id);
        Assert.Equal(expected.GetProperty("title").GetString(), meta.Title);
        Assert.Equal(expected.GetProperty("author").GetString(), meta.Author);
        Assert.Equal(expected.GetProperty("last_saved_by").GetString(), meta.LastModifiedBy);
        Assert.Equal(expected.GetProperty("application").GetString(), meta.Application);
        Assert.Equal(expected.GetProperty("company").GetString(), meta.Company);
        Assert.Equal(expected.GetProperty("subject").GetString(), meta.GetProperty("Subject"));
        Assert.Equal(expected.GetProperty("keywords").GetString(), meta.GetProperty("Keywords"));
        Assert.Equal(expected.GetProperty("comments").GetString(), meta.GetProperty("Comments"));
        Assert.Equal(expected.GetProperty("category").GetString(), meta.GetProperty("Category"));
        Assert.Equal(DateTimeOffset.Parse(expected.GetProperty("created").GetString()!), meta.Created);
        Assert.Equal(DateTimeOffset.Parse(expected.GetProperty("saved").GetString()!), meta.Modified);
        Assert.True(meta.HasPreview);
        Assert.False(meta.HasGeometry);
    }

    [Fact]
    public void Thumbnails_are_returned_as_png_and_geometry_is_unsupported()
    {
        var ipt = ModelLoader.ReadPreview(File.ReadAllBytes(DatasetPaths.File("synthetic/part.ipt")), "part.ipt");
        var rvt = ModelLoader.ReadPreview(File.ReadAllBytes(DatasetPaths.File("synthetic/model.rvt")), "model.rvt");

        Assert.Equal("image/png", ipt!.MediaType);
        Assert.Equal("image/png", rvt!.MediaType);
        Assert.Equal(0x89, rvt.Data[0]);
        Assert.Equal(ipt.Data, rvt.Data);
        var ex = Assert.Throws<UnsupportedModelFormatException>(() => ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File("synthetic/model.rvt")), "model.rvt"));
        Assert.Equal("revit", ex.Format.Id);
    }

    [Fact]
    public void Revit_basic_file_info_gives_build_format_and_user()
    {
        var meta = ModelLoader.ReadMetadata(File.ReadAllBytes(DatasetPaths.File("synthetic/model.rvt")), "model.rvt");

        Assert.Equal("revit", meta.Format.Id);
        Assert.Equal("2024", meta.FormatVersion);
        Assert.StartsWith("Autodesk Revit 2024 (Build: ", meta.Application, StringComparison.Ordinal);
        Assert.Equal("jdoe", meta.GetProperty("Username"));
        Assert.Equal("Not enabled", meta.GetProperty("Worksharing"));
        Assert.Equal("ENU", meta.GetProperty("Locale"));
        Assert.True(meta.HasPreview || ModelLoader.ReadPreview(File.ReadAllBytes(DatasetPaths.File("synthetic/model.rvt")), "x.rvt") is not null);
    }

    [Fact]
    public void A_directory_cycle_or_chain_loop_is_refused()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/part.ipt"));
        var first = BitConverter.ToUInt32(data, 0x30);
        // Point the first directory sector's FAT entry back at itself.
        var fatSector = BitConverter.ToUInt32(data, 0x4C);
        var entryOffset = (int)((fatSector + 1) * 512 + first * 4);
        BitConverter.GetBytes(first).CopyTo(data, entryOffset);

        Assert.Throws<ModelFormatException>(() => ModelLoader.ReadMetadata(data, "part.ipt"));
    }
}
