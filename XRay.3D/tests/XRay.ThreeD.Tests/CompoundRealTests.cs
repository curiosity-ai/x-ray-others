using System.Buffers.Binary;
using System.Text.Json;

namespace XRay.ThreeD.Tests;

/// <summary>
/// Real Revit (2015, 2017, 2021) and Inventor (2021 to 2023) files against an independent reader (Corpus/cfb-real-golden.json, made by
/// tools/oracle/make_cfb_real_golden.py with olefile, zipfile and zlib). The files are not redistributable: they are read from
/// XRAY_3D_LOCAL_SAMPLES (default /tmp/claude-0/samples, sub-folders revit and inventor) and the tests skip without them.
/// </summary>
public class CompoundRealTests
{
    public static IEnumerable<object[]> Files() => JsonDocument.Parse(File.ReadAllText(DatasetPaths.CorpusFile("cfb-real-golden.json"))).RootElement
        .EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).Select(n => new object[] { n });

    private static string? Find(string file)
    {
        var root = Environment.GetEnvironmentVariable("XRAY_3D_LOCAL_SAMPLES") ?? "/tmp/claude-0/samples";
        foreach (var folder in new[] { "revit", "inventor" })
        {
            var path = Path.Combine(root, folder, file);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void MatchesTheIndependentReader(string file)
    {
        var path = Find(file);
        Assert.SkipWhen(path is null, "local sample not present: " + file);
        var golden = JsonDocument.Parse(File.ReadAllText(DatasetPaths.CorpusFile("cfb-real-golden.json"))).RootElement.GetProperty(file);
        var data = File.ReadAllBytes(path!);
        var meta = ModelLoader.ReadMetadata(data, file);
        var preview = ModelLoader.ReadPreview(data, file);
        Assert.NotNull(preview);
        Assert.True(meta.HasPreview);
        Assert.Equal("image/png", preview!.MediaType);
        var expected = golden.GetProperty("preview");
        Assert.Equal(expected.GetProperty("width").GetInt32(), (int)BinaryPrimitives.ReadUInt32BigEndian(preview.Data.AsSpan(16)));
        Assert.Equal(expected.GetProperty("height").GetInt32(), (int)BinaryPrimitives.ReadUInt32BigEndian(preview.Data.AsSpan(20)));

        if (meta.Format.Id == "revit")
        {
            Assert.Equal(golden.GetProperty("application").GetString(), meta.Application);
            Assert.Equal(golden.GetProperty("title").GetString(), meta.Title);
            Assert.Equal(golden.GetProperty("guid").GetString(), meta.GetProperty("Unique Document GUID"));
            Assert.Equal(golden.GetProperty("last_save_path").GetString(), meta.GetProperty("Last Save Path"));
            Assert.Equal(DateTimeOffset.Parse(golden.GetProperty("modified").GetString()!, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal), meta.Modified);
            Assert.Equal(golden.GetProperty("links").EnumerateArray().Select(e => e.GetString()), meta.ComponentFiles);
            Assert.Equal(golden.GetProperty("history").EnumerateArray().Select(e => e.GetString()).Order(StringComparer.Ordinal), meta.Properties.Where(p => p.Group == "history").Select(p => p.Value).Order(StringComparer.Ordinal));
            foreach (var p in golden.GetProperty("project").EnumerateObject())
            {
                Assert.Equal(p.Value.GetString(), meta.GetProperty(p.Name));
            }
        }
        else
        {
            var design = golden.GetProperty("design");
            Assert.Equal(design.GetProperty("part_number").GetString(), meta.GetProperty("Part Number"));
            Assert.Equal("Autodesk Inventor " + design.GetProperty("build").GetString(), meta.Application);
            Assert.Equal(design.GetProperty("designer").GetString(), meta.GetProperty("Designer"));
            Assert.Equal(design.GetProperty("subtype").GetString(), meta.GetProperty("Document Subtype"));
            Assert.Equal(design.GetProperty("subtype").GetString() == "Assembly" ? "assembly" : "part", meta.DocumentKind);
            foreach (var (key, name) in new[] { ("description", "Description"), ("material", "Material"), ("appearance", "Appearance") })
            {
                Assert.Equal(design.TryGetProperty(key, out var v) ? v.GetString() : null, meta.GetProperty(name));
            }

            foreach (var p in golden.GetProperty("custom").EnumerateObject())
            {
                Assert.Equal(p.Value.GetString(), meta.GetProperty(p.Name));
            }

            if (golden.TryGetProperty("references", out var references))
            {
                Assert.Equal(references.EnumerateArray().Select(e => e.GetString()).Order(StringComparer.Ordinal), meta.ComponentFiles.Order(StringComparer.Ordinal));
            }
        }
    }
}

/// <summary>The layouts seen in real Inventor 2021+ and Revit 2019+ files, as committed synthetic files (tools/fixtures/make_cfb_real_layout.py).</summary>
public class CompoundLayoutTests
{
    [Fact]
    public void InventorPropertySetsAreFoundByFormatIdNotByName()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/inventor-hashed.iam"));
        var meta = ModelLoader.ReadMetadata(data, "inventor-hashed.iam");
        Assert.Equal("inventor", meta.Format.Id);
        Assert.Equal("Pump housing", meta.Title);
        Assert.Equal("A. Designer", meta.Author);
        Assert.Equal("Example Works", meta.Company);
        Assert.Equal("Autodesk Inventor 2022.2 (Build 262287010, 287A)", meta.Application);
        Assert.Equal("assembly", meta.DocumentKind);
        Assert.Equal(new DateTimeOffset(2022, 6, 1, 8, 0, 0, TimeSpan.Zero), meta.Created);
        Assert.Equal(new DateTimeOffset(2023, 2, 3, 17, 30, 0, TimeSpan.Zero), meta.Modified);
        Assert.Equal("B. Reviewer", meta.LastModifiedBy);
        Assert.Equal("PH-100", meta.GetProperty("Part Number"));
        Assert.Equal("Aluminum 6061", meta.GetProperty("Material"));
        Assert.Equal("Aluminum - Satin", meta.GetProperty("Appearance"));
        Assert.Equal("6.350 mm", meta.GetProperty("plate_thickness"));
        Assert.Equal("12.000 mm", meta.GetProperty("hole_pitch"));
        Assert.DoesNotContain(meta.Properties, p => p.Name == "Property Set Name");
        Assert.Equal(["housing.ipt", "lid.ipt", "M4_screw.ipt"], meta.ComponentFiles);
        Assert.True(meta.HasPreview);
        var preview = ModelLoader.ReadPreview(data, "inventor-hashed.iam");
        Assert.Equal("image/png", preview!.MediaType);
        Assert.Equal(12, (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(preview.Data.AsSpan(16)));
    }

    [Fact]
    public void RevitTextOnAnOddOffsetProjectInformationLinksAndHistory()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/revit-2021.rvt"));
        var meta = ModelLoader.ReadMetadata(data, "revit-2021.rvt");
        Assert.Equal("Autodesk Revit 2021 (Build: 20200220_1100(x64))", meta.Application);
        Assert.Equal("2021", meta.FormatVersion);
        Assert.Equal("Annex", meta.Title);
        Assert.Equal(new DateTimeOffset(2023, 4, 5, 6, 7, 8, TimeSpan.Zero), meta.Modified);
        Assert.Equal("C:\\models\\annex.rvt", meta.GetProperty("Last Save Path"));
        Assert.Equal("ENG", meta.GetProperty("Locale"));
        Assert.Equal("0a1b2c3d-1111-2222-3333-444455556666", meta.GetProperty("Unique Document GUID"));
        Assert.Equal("Annex", meta.GetProperty("Project Name"));
        Assert.Equal("Example Works", meta.GetProperty("Client Name"));
        Assert.Equal(["Keynotes.txt", "frame.rvt"], meta.ComponentFiles);
        Assert.Equal(["Revit 2020 2020 (2020.000) : 20190410_1515(x64)", "Revit 2021 2021 (2021.000) : 20200220_1100(x64)"],
            meta.Properties.Where(p => p.Group == "history").Select(p => p.Value).Order(StringComparer.Ordinal));
        Assert.True(meta.HasPreview);
    }
}
