using System.Numerics;
using System.Text;
using System.Text.Json;

namespace XRay.ThreeD.Tests;

public class TextureTests
{
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47];

    private static JsonElement Json(byte[] glb)
    {
        var length = (int)BitConverter.ToUInt32(glb, 12);
        return JsonDocument.Parse(glb.AsMemory(20, length)).RootElement.Clone();
    }

    [Fact]
    public void Obj_package_loads_textures_from_the_mtl_with_transform_and_converts_bmp()
    {
        var model = ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File("synthetic/package-obj-textured.zip")), "package-obj-textured.zip");

        Assert.Equal(2, model.Textures.Count);
        Assert.Equal(2, model.Materials.Count);
        var checker = model.Materials.Single(m => m.Name == "Checker");
        Assert.Equal(TextureWrap.ClampToEdge, model.Textures[checker.BaseColorTexture!.Texture].WrapU);
        Assert.Equal(new Vector2(2, 2), checker.BaseColorTexture.Scale);
        // OBJ v points up: the offset's v is conjugated by the flip, 1 - 2 - 0.
        Assert.Equal(new Vector2(0.25f, -1f), checker.BaseColorTexture.Offset);
        var pink = model.Textures[model.Materials.Single(m => m.Name == "Pink").BaseColorTexture!.Texture];
        Assert.Equal("image/png", pink.MediaType);
        Assert.Equal(PngMagic, pink.Data[..4]);

        var quad = model.Meshes[0];
        Assert.Equal(quad.VertexCount * 2, quad.TexCoords.Length);
        // The first corner has uv (0,0) in OBJ, which is (0,1) once v is flipped.
        Assert.Equal(new[] { 0f, 1f }, quad.TexCoords[..2]);
        Assert.Empty(model.Warnings);
    }

    [Fact]
    public void Missing_texture_is_a_warning_and_the_colour_stays()
    {
        var model = ModelLoader.Load(Encoding.UTF8.GetBytes("mtllib a.mtl\nv 0 0 0\nv 1 0 0\nv 0 1 0\nvt 0 0\nvt 1 0\nvt 0 1\nusemtl M\nf 1/1 2/2 3/3\n"), "a.obj",
            new LoadOptions { ResolveCompanion = n => n == "a.mtl" ? Encoding.UTF8.GetBytes("newmtl M\nKd 1 0 0\nmap_Kd nowhere.png\n") : null });

        Assert.Empty(model.Textures);
        Assert.Null(model.Materials[0].BaseColorTexture);
        Assert.Contains(model.Warnings, w => w.Contains("nowhere.png"));
    }

    [Fact]
    public void Obj_package_converts_to_glb_with_images_samplers_texcoords_and_transform()
    {
        var model = ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File("synthetic/package-obj-textured.zip")), "package-obj-textured.zip");
        var glb = GlbWriter.ToArray(model);
        GlbValidation.Validate(glb);
        var json = Json(glb);

        Assert.Equal(2, json.GetProperty("images").GetArrayLength());
        Assert.All(json.GetProperty("images").EnumerateArray(), i => Assert.Equal("image/png", i.GetProperty("mimeType").GetString()));
        Assert.Equal(2, json.GetProperty("textures").GetArrayLength());
        Assert.Equal(33071, json.GetProperty("samplers")[0].GetProperty("wrapS").GetInt32());
        Assert.Contains("KHR_texture_transform", json.GetProperty("extensionsUsed").EnumerateArray().Select(e => e.GetString()));
        Assert.True(json.GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("attributes").TryGetProperty("TEXCOORD_0", out _));
        var info = json.GetProperty("materials")[0].GetProperty("pbrMetallicRoughness").GetProperty("baseColorTexture");
        Assert.Equal(2f, info.GetProperty("extensions").GetProperty("KHR_texture_transform").GetProperty("scale")[0].GetSingle());

        // And it reads back.
        var back = ModelLoader.Load(glb, "back.glb");
        Assert.Equal(2, back.Textures.Count);
        Assert.Equal(model.Meshes[0].TexCoords, back.Meshes[0].TexCoords);
        Assert.Equal(new Vector2(0.25f, -1f), back.Materials[0].BaseColorTexture!.Offset);
    }

    [Theory]
    [InlineData(GlbCompression.Quantize)]
    [InlineData(GlbCompression.Meshopt)]
    public void Compressed_output_keeps_texcoords_and_textures(GlbCompression compression)
    {
        var model = ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File("synthetic/textured.gltf")), "textured.gltf");
        var glb = GlbWriter.ToArray(model, new GlbWriteOptions { Compression = compression });
        GlbValidation.Validate(glb, allowCompressed: true);
        var back = ModelLoader.Load(glb, "back.glb");

        Assert.Single(back.Textures);
        Assert.Equal(model.Meshes[0].TexCoords.Length, back.Meshes[0].TexCoords.Length);
        // Vertices are renumbered by first use and positions are quantised, so compare the coordinates as a set.
        string Key(MeshData m, int v) => $"{m.TexCoords[2 * v]:0.###},{m.TexCoords[2 * v + 1]:0.###}";
        Assert.Equal(
            Enumerable.Range(0, model.Meshes[0].VertexCount).Select(v => Key(model.Meshes[0], v)).Order(),
            Enumerable.Range(0, back.Meshes[0].VertexCount).Select(v => Key(back.Meshes[0], v)).Order());
    }

    [Fact]
    public void Gltf_textures_sampler_transform_and_uvs_are_read()
    {
        var model = ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File("synthetic/textured.gltf")), "textured.gltf");

        var texture = Assert.Single(model.Textures);
        Assert.Equal("image/png", texture.MediaType);
        Assert.True(texture.Nearest);
        Assert.Equal(TextureWrap.ClampToEdge, texture.WrapU);
        Assert.Equal(TextureWrap.MirroredRepeat, texture.WrapV);
        var reference = model.Materials[0].BaseColorTexture!;
        Assert.Equal(new Vector2(0.25f, 0.5f), reference.Offset);
        Assert.Equal(new Vector2(2, 3), reference.Scale);
        Assert.Equal(0.5f, reference.Rotation);
        Assert.Equal(new[] { 0f, 1f, 1f, 1f, 1f, 0f, 0f, 0f }, model.Meshes[0].TexCoords);
    }

    [Fact]
    public void Glb_input_with_embedded_buffer_view_images_round_trips()
    {
        var model = ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File("synthetic/textured.gltf")), "textured.gltf");
        var once = GlbWriter.ToArray(model);
        var twice = GlbWriter.ToArray(ModelLoader.Load(once, "once.glb"));
        Assert.Equal(model.Textures[0].Data, ModelLoader.Load(twice, "twice.glb").Textures[0].Data);
    }

    [Fact]
    public void ThreeMf_texture2d_groups_split_vertices_per_uv_and_flip_v()
    {
        var model = ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File("synthetic/textured.3mf")), "textured.3mf");

        var texture = Assert.Single(model.Textures);
        Assert.Equal(TextureWrap.ClampToEdge, texture.WrapU);
        Assert.Equal(TextureWrap.MirroredRepeat, texture.WrapV);
        var mesh = model.Meshes[0];
        // Four corners, each used with one coordinate: four textured vertices (the shared originals stay unused).
        Assert.Equal(8, mesh.VertexCount);
        Assert.Equal(mesh.VertexCount * 2, mesh.TexCoords.Length);
        Assert.Equal(0, model.Materials[0].BaseColorTexture!.Texture);
        var corner = (int)mesh.Indices[1]; // second corner of the first triangle: (10,0,0) with uv (1,0) -> (1,1)
        Assert.Equal(10f, mesh.Positions[3 * corner]);
        Assert.Equal(new[] { 1f, 1f }, mesh.TexCoords[(2 * corner)..(2 * corner + 2)]);
        GlbValidation.Validate(GlbWriter.ToArray(model));
    }

    [Fact]
    public void Bmp_and_tga_textures_are_converted_to_png_and_garbage_is_refused()
    {
        string Obj(string file) => "mtllib a.mtl\nv 0 0 0\nv 1 0 0\nv 0 1 0\nvt 0 0\nvt 1 0\nvt 0 1\nusemtl M\nf 1/1 2/2 3/3\n";
        Model3D Load(string name, byte[] image) => ModelLoader.Load(Encoding.UTF8.GetBytes(Obj(name)), "a.obj", new LoadOptions
        {
            ResolveCompanion = n => n == "a.mtl" ? Encoding.UTF8.GetBytes($"newmtl M\nmap_Kd {name}\n") : n == name ? image : null,
        });

        // 2x1 TGA, 24 bit, bottom-left origin: pixels red, blue.
        byte[] tga = [0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 1, 0, 24, 0, 0, 0, 255, 255, 0, 0];
        var fromTga = Load("t.tga", tga);
        Assert.Equal("image/png", fromTga.Textures[0].MediaType);

        var garbage = Load("g.tiff", [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20]);
        Assert.Empty(garbage.Textures);
        Assert.Contains(garbage.Warnings, w => w.Contains("g.tiff"));

        // A BMP claiming a huge size must be refused, not allocated.
        var bmp = new byte[60];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(54).CopyTo(bmp, 10);
        BitConverter.GetBytes(40).CopyTo(bmp, 14);
        BitConverter.GetBytes(int.MaxValue).CopyTo(bmp, 18);
        BitConverter.GetBytes(int.MaxValue).CopyTo(bmp, 22);
        bmp[26] = 1;
        bmp[28] = 24;
        var huge = Load("h.bmp", bmp);
        Assert.Empty(huge.Textures);
        Assert.Contains(huge.Warnings, w => w.Contains("h.bmp"));
    }

    [Fact]
    public void Assembly_package_carries_textures_into_part_files()
    {
        var model = ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File("synthetic/textured.gltf")), "textured.gltf");
        var package = AssemblyPackage.Create(model);
        var reloaded = AssemblyPackage.Load(p => package.Files.GetValueOrDefault(p));

        Assert.Single(reloaded.Textures);
        Assert.NotNull(reloaded.Materials[0].BaseColorTexture);
        Assert.Equal(model.Meshes[0].TexCoords.Length, reloaded.Meshes[0].TexCoords.Length);
    }
}
