namespace XRay.ThreeD.Tests;

public class FormatTests
{
    [Theory]
    [InlineData("sldprt", "solidworks")]
    [InlineData("sldasm", "solidworks")]
    [InlineData("slddrw", "solidworks")]
    [InlineData("glb", "gltf")]
    [InlineData("ply", "ply")]
    [InlineData("step", "step")]
    [InlineData("iges", "iges")]
    [InlineData("dxf", "dxf")]
    [InlineData("dwg", "dwg")]
    [InlineData("3dm", "rhino")]
    [InlineData("fcstd", "freecad")]
    [InlineData("scad", "openscad")]
    [InlineData("dwf", "dwf")]
    [InlineData("sh3d", "sh3d")]
    [InlineData("zip", "archive")]
    public void Dataset_files_are_detected(string folder, string expected)
    {
        var files = DatasetPaths.Files(folder).ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var format = ModelLoader.Detect(File.ReadAllBytes(file), Path.GetFileName(file));
            Assert.True(format.Id == expected, $"{file}: detected {format.Id}, expected {expected}");
        }
    }

    [Fact]
    public void Content_wins_over_a_wrong_extension()
    {
        var glb = File.ReadAllBytes(DatasetPaths.File("khronos/Box.glb"));
        Assert.Equal("gltf", ModelLoader.Detect(glb, "model.stl").Id);
        Assert.Equal("stl", ModelLoader.Detect(File.ReadAllBytes(DatasetPaths.File("synthetic/cube-binary.stl")), null).Id);
        Assert.Equal("stl", ModelLoader.Detect(File.ReadAllBytes(DatasetPaths.File("synthetic/cube-ascii.stl")), null).Id);
        Assert.Equal("3mf", ModelLoader.Detect(File.ReadAllBytes(DatasetPaths.File("synthetic/two-cubes.3mf")), "x.bin").Id);
        Assert.True(ModelLoader.Detect(new byte[] { 1, 2, 3 }, null).IsUnknown);
    }

    [Fact]
    public void Recognised_but_unread_formats_throw_the_unsupported_exception()
    {
        // AC1015 drawings load now; a later release is recognised but its pages are not decoded.
        var header = new byte[0x200];
        "AC1032"u8.CopyTo(header);
        var ex = Assert.Throws<UnsupportedModelFormatException>(() => ModelLoader.Load(header, "x.dwg"));
        Assert.Equal("dwg", ex.Format.Id);
        Assert.Equal(FormatSupport.Metadata, ex.Format.Support);
    }

    [Fact]
    public void Every_supported_format_has_extensions_and_a_unique_id()
    {
        var all = ModelFormats.All.ToList();
        Assert.Equal(all.Count, all.Select(f => f.Id).Distinct().Count());
        Assert.All(all, f => Assert.NotEmpty(f.Extensions));
    }
}
