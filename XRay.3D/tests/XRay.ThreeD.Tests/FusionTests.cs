namespace XRay.ThreeD.Tests;

/// <summary>
/// Fusion design files: a real CC0 .f3d (Zstandard-compressed entries, written by Fusion 360; from the cadmpeg corpus, see the dataset manifest), a synthetic
/// part package in the Manifest.dat grammar of the public description, and a synthetic multi-document .f3z. The expected values of the real file were read with
/// an independent decoder (python zipfile with the zstandard package) while writing tools/oracle/make_f3d_golden.py. Geometry is not decoded.
/// </summary>
public class FusionTests
{
    [Fact]
    public void RealPackageManifestAndInventory()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("fusion/1WireRGBWController.f3d"));
        Assert.Equal("fusion", ModelLoader.Detect(data, null).Id);
        var meta = ModelLoader.ReadMetadata(data, "1WireRGBWController.f3d");
        Assert.Equal("3-2-0-0", meta.FormatVersion);
        Assert.Equal("Fusion Document", meta.Title);
        Assert.Equal("A Fusion Document", meta.GetProperty("Description"));
        Assert.Equal("cd835aea-6a53-4b62-bd79-c62fd95e8881", meta.GetProperty("Document GUID"));
        Assert.Equal("c451872d-07d5-46d0-aad1-9ef0e9511dcf", meta.GetProperty("Document asset GUID"));
        Assert.Equal("6bae5e2a-9786-4a53-a76e-3e2adc6b17b6", meta.GetProperty("Active asset GUID"));
        Assert.Equal("FusionAssetName", meta.GetProperty("Asset folders"));
        Assert.Equal("FusionAssetType", meta.GetProperty("Asset type"));
        Assert.Equal("20", meta.GetProperty("Asset revision"));
        Assert.Equal("Application, ParaMesh, Server, VolField", meta.GetProperty("Capabilities"));
        Assert.Equal("2 (6144287 bytes)", meta.GetProperty("B-rep streams"));
        Assert.False(meta.HasGeometry);
        Assert.Throws<UnsupportedModelFormatException>(() => ModelLoader.Load(data, "x.f3d"));
    }

    [Fact]
    public void SyntheticPartPackage()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/part-docstruct.f3d"));
        Assert.Equal("fusion", ModelLoader.Detect(data, "a.bin").Id);
        var meta = ModelLoader.ReadMetadata(data, "part-docstruct.f3d");
        Assert.Equal("Bracket", meta.Title);
        Assert.Equal("part", meta.DocumentKind);
        Assert.Equal("part-design / part-sheetmetal", meta.GetProperty("Document structure"));
        Assert.Equal(["Small", "Large"], meta.Configurations);
        Assert.Equal("Bracket shown", meta.GetProperty("Display name"));
        Assert.Equal("urn:adsk.wipprod:dm.lineage:ABCDEF", meta.GetProperty("Lineage URN"));
        Assert.Equal("Application, Server", meta.GetProperty("Capabilities"));
        Assert.Equal("1 (1000 bytes)", meta.GetProperty("B-rep streams"));
    }

    [Fact]
    public void SyntheticMultiDocumentArchive()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/design-description.f3z"));
        Assert.Equal("fusion", ModelLoader.Detect(data, "a.f3z").Id);
        var meta = ModelLoader.ReadMetadata(data, "design-description.f3z");
        Assert.Equal("Controller assembly", meta.Title);
        Assert.Equal(["Controller.f3d"], meta.ComponentFiles);
        Assert.Equal(["Controller", "Enclosure"], meta.PartNames);
        Assert.Equal(new DateTimeOffset(2025, 6, 1, 10, 20, 30, TimeSpan.Zero), meta.Created);
        Assert.Equal("Controller.f3d", meta.GetProperty("Root document"));
        Assert.Equal("A bracket", meta.Properties.First(p => p.Name == "Description" && p.Group == "root: manifest").Value);
        Assert.Equal("FusionAssetType", meta.Properties.First(p => p.Name == "Asset type").Value);
        Assert.Equal("part", meta.Properties.First(p => p.Name == "Root document type").Value);
    }
}
