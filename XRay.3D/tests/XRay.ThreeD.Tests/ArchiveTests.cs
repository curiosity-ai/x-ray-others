using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace XRay.ThreeD.Tests;

public class ArchiveTests
{
    private static byte[] Zip(params (string Name, byte[] Data)[] entries)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, data) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(data);
            }
        }

        return output.ToArray();
    }

    private static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);

    private const string Obj = "mtllib model.mtl\nv 0 0 0\nv 1 0 0\nv 0 1 0\nusemtl Red\nf 1 2 3\n";

    [Fact]
    public void Obj_package_loads_the_primary_model_with_its_mtl_colours()
    {
        var path = DatasetPaths.File("synthetic/package-obj-mtl.zip");
        var bytes = File.ReadAllBytes(path);
        Assert.Equal("archive", ModelLoader.Detect(bytes, "package-obj-mtl.zip").Id);

        var model = ModelLoader.Load(bytes, "package-obj-mtl.zip");
        var meta = model.Metadata;

        Assert.Equal("obj", meta.Format.Id);
        Assert.Equal("model", meta.Title);
        Assert.Equal("zip", meta.Properties.Single(p => p.Name == "container").Value);
        Assert.Equal("box/model.obj", meta.Properties.Single(p => p.Name == "entry").Value);
        Assert.Equal(["other/cube.stl"], meta.ComponentFiles);
        Assert.Equal(["plate", "wedge"], model.Meshes.Select(m => m.Name));

        var red = model.Materials.Single(m => m.Name == "Red");
        var blue = model.Materials.Single(m => m.Name == "Blue");
        Assert.Equal(new Vector4(1, 0, 0, 1), red.BaseColor);
        Assert.Equal(0.5f, red.Roughness, 1e-4f);
        Assert.Equal(new Vector4(0, 0, 1, 0.5f), blue.BaseColor);
        Assert.Empty(model.Warnings);

        Assert.Equal("obj", ModelLoader.ReadMetadata(bytes, "package-obj-mtl.zip").Format.Id);
        GlbValidation.Validate(ModelLoader.ConvertToGlb(bytes, "package-obj-mtl.zip"));
    }

    [Fact]
    public void Companions_are_found_by_relative_path_then_by_file_name()
    {
        var mtl = Text("newmtl Red\nKd 0.5 0.25 0\n");
        var nested = Zip(("models/a/part.obj", Text(Obj.Replace("model.mtl", "../shared/model.mtl", StringComparison.Ordinal))), ("models/shared/model.mtl", mtl));
        var elsewhere = Zip(("models/a/part.obj", Text(Obj)), ("library/MODEL.MTL", mtl));

        Assert.Equal(new Vector4(0.5f, 0.25f, 0, 1), ModelLoader.Load(nested, "x.zip").Materials.Single().BaseColor);
        Assert.Equal(new Vector4(0.5f, 0.25f, 0, 1), ModelLoader.Load(elsewhere, "x.zip").Materials.Single().BaseColor);
    }

    [Fact]
    public void A_missing_mtl_warns_and_the_model_still_loads()
    {
        var model = ModelLoader.Load(Zip(("part.obj", Text(Obj))), "x.zip");

        Assert.Single(model.Meshes);
        Assert.Contains(model.Warnings, w => w.Contains("model.mtl", StringComparison.Ordinal));
        Assert.Equal("Red", model.Materials.Single().Name);
    }

    [Fact]
    public void Loose_obj_takes_its_mtl_from_the_resolver()
    {
        var requested = new List<string>();
        var options = new LoadOptions
        {
            ResolveCompanion = name =>
            {
                requested.Add(name);
                return Text("newmtl Red\nKd 0 1 0\nTr 0.25\n");
            },
        };

        var model = ModelLoader.Load(Text(Obj), "part.obj", options);

        Assert.Equal(["model.mtl"], requested);
        Assert.Equal(new Vector4(0, 1, 0, 0.75f), model.Materials.Single().BaseColor);
    }

    [Fact]
    public void Extension_priority_decides_and_the_largest_of_a_kind_wins()
    {
        var cube = File.ReadAllBytes(DatasetPaths.File("synthetic/cube-ascii.stl"));
        var scene = File.ReadAllBytes(DatasetPaths.File("synthetic/scene.obj"));

        var objOverStl = ModelLoader.Load(Zip(("a.stl", cube), ("b.obj", scene)), "x.zip");
        Assert.Equal("obj", objOverStl.Metadata.Format.Id);
        Assert.Equal(["a.stl"], objOverStl.Metadata.ComponentFiles);

        var largest = ModelLoader.Load(Zip(("small.stl", cube), ("big.stl", [.. cube, .. Text("\n\n\n\n\n\n\n\n\n\n")])), "x.zip");
        Assert.Equal("big.stl", largest.Metadata.Properties.Single(p => p.Name == "entry").Value);
    }

    [Fact]
    public void A_broken_primary_falls_through_to_the_next_model()
    {
        var cube = File.ReadAllBytes(DatasetPaths.File("synthetic/cube-ascii.stl"));
        var model = ModelLoader.Load(Zip(("broken.glb", Text("glTF this is not a glb")), ("cube.stl", cube)), "x.zip");

        Assert.Equal("stl", model.Metadata.Format.Id);
        Assert.Contains(model.Warnings, w => w.StartsWith("broken.glb:", StringComparison.Ordinal));
    }

    [Fact]
    public void An_archive_without_a_model_is_unsupported()
    {
        var ex = Assert.Throws<UnsupportedModelFormatException>(() => ModelLoader.Load(Zip(("readme.txt", Text("hello")), ("a.gbr", Text("G04*"))), "x.zip"));

        Assert.Equal("archive", ex.Format.Id);
        Assert.Contains("no 3D model", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Entry_size_limit_applies()
    {
        var cube = File.ReadAllBytes(DatasetPaths.File("synthetic/cube-ascii.stl"));
        var packed = Zip(("cube.stl", cube));

        Assert.Throws<ModelFormatException>(() => ModelLoader.Load(packed, "x.zip", new LoadOptions { MaxInputBytes = cube.Length - 1 }));
    }

    [Fact]
    public void Dataset_archives_load_or_say_there_is_no_model()
    {
        var loaded = new List<string>();
        foreach (var file in DatasetPaths.Files("zip", "*.zip"))
        {
            var bytes = File.ReadAllBytes(file);
            try
            {
                var model = ModelLoader.Load(bytes, Path.GetFileName(file));
                loaded.Add(Path.GetFileName(file) + " -> " + model.Metadata.Format.Id);
                GlbValidation.Validate(GlbWriter.ToArray(model));
            }
            catch (UnsupportedModelFormatException)
            {
            }
        }

        Assert.Contains(loaded, l => l.StartsWith("opengameart-lowpoly-dummy-prefab.zip -> fbx", StringComparison.Ordinal));
    }
}
