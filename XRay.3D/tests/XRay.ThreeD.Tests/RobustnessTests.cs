namespace XRay.ThreeD.Tests;

/// <summary>Every reader takes hostile files: damaged input may fail, but only with <see cref="ModelFormatException"/>.</summary>
public class RobustnessTests
{
    internal static readonly string[] Fixtures =
    [
        "sldprt/servo-mount.sldprt",
        "slddrw/rocket-fin-set.slddrw",
        "synthetic/cube-ascii.stl",
        "synthetic/cube-binary.stl",
        "synthetic/scene.obj",
        "synthetic/cube-ascii.ply",
        "synthetic/cube-binary-little.ply",
        "synthetic/cube-binary-big.ply",
        "synthetic/two-cubes.3mf",
        "synthetic/instances.gltf",
        "khronos/Box.glb",
        "synthetic/entities.dxf",
        "dxf/flange-drawing.dxf",
        "synthetic/cloud-f3.las",
        "synthetic/part.ipt",
        "synthetic/model.rvt",
        "dwg/flange-drawing.dwg",
        "dwg/studio-floor-plan.dwg",
        "synthetic/laz-f0.laz",
        "synthetic/laz-f3.laz",
        "synthetic/cube-facecolors.off",
        "synthetic/cube.amf",
        "synthetic/cube-zipped.amf",
        "synthetic/scene.3ds",
        "synthetic/scene.dae",
        "synthetic/scene.x3d",
        "synthetic/scene.wrl",
        "synthetic/scene.x3dv",
        "synthetic/scene-7400.fbx",
        "synthetic/scene-7700.fbx",
        "synthetic/scene-ascii.fbx",
        "synthetic/scene.usda",
        "step/neopixel-ring.step",
        "synthetic/tessellated.stp",
        "synthetic/mapped.stp",
        "synthetic/scene.usdz",
        "synthetic/scene.usdc",
        "synthetic/rich.usdc",
        "synthetic/rich.usdz",
        "synthetic/cloud-binary.pcd",
        "synthetic/cloud-compressed.pcd",
        "synthetic/cloud-ascii.pcd",
        "synthetic/cloud.pts",
        "synthetic/building.ifc",
        "synthetic/part.igs",
        "iges/slewing-ring.iges",
        "synthetic/iges-brep-quadrics.igs",
        "synthetic/iges-trimmed-faces.igs",
        "synthetic/iges-nurbs-brep.igs",
        "synthetic/iges-nurbs-faces.igs",
        "synthetic/assembly.FCStd",
        "synthetic/home.sh3d",
        "synthetic/blend/scene-raw.blend",
        "synthetic/blend/scene-zstd.blend",
        "synthetic/inventor-hashed.iam",
        "synthetic/revit-2021.rvt",
        "synthetic/legacy-cube.sldprt",
        "synthetic/gdl-solids.gsm",
        "pln/table-lamp.gsm",
        "sh3d/sweet-home-cottage.sh3d",
        "synthetic/package-obj-mtl.zip",
        "synthetic/package-obj-textured.zip",
        "synthetic/textured.gltf",
        "synthetic/textured.3mf",
        "synthetic/drawing.dxf",
        "draco/sphere-draco.glb",
        "draco/sphere-draco-tangent.glb",
        "synthetic/print.gcode",
        "synthetic/profile.nc",
        "fcstd/butt-hinge.FCStd",
        "scad/chess-rook.scad",
        "scad/wall-bracket.scad",
        "3dm/terrain-contours.3dm",
        "synthetic/header.x_t",
        "synthetic/header.x_b",
        "dwf/usfs-rtr2-tent-platform.dwf",
        "dwf/usfs-rwfc1-fish-cleaning-station.dwf",
        "synthetic/drawing.dwfx",
        "sat/nist-arm-blank.sat",
        "synthetic/box-700.sat",
        "synthetic/box-700.sab",
        "synthetic/cylinder-400.sat",
        "synthetic/sphere-400.sat",
        "synthetic/rhino-scene.3dm",
        "synthetic/rhino-layers.3dm",
        "b3d/blitz3d-arcade-cabinet.b3d",
        "b3d/blitz3d-swaying-tentacle.b3d",
        "b3d/bazis-wall-cabinet.b3d",
        "unitypackage/opengameart-key-collection.unitypackage",
        "psu/astronaut.psu",
        "rlf/scallop-shell.rlf",
        "uasset/Faucet.uasset",
        "uasset/DoorCloser.uasset",
        "synthetic/las14-f10.las",
        "synthetic/laz14-f10.laz",
        "synthetic/laz14-f8.laz",
        "synthetic/e57-two-scans.e57",
        "synthetic/usd-layers.usdz",
        "synthetic/usd-layers/crate-root.usdc",
        "synthetic/iges-spline-surface.igs",
        "synthetic/iges-ruled.igs",
        "synthetic/iges-bounded-plane.igs",
        "synthetic/iges-spline-curve.igs",
        "uasset/Faucet.uasset",
        "uasset/DoorCloser.uasset",
        "synthetic/laz14c-f7.laz",
        "synthetic/laz-pointwise-f3.laz",
    ];

    public static IEnumerable<object[]> Files() => Fixtures.Select(f => new object[] { f });

    private static void MustFailCleanly(byte[] data, string name)
    {
        try
        {
            var model = ModelLoader.Load(data, name);
            // A load that succeeds must also write.
            _ = GlbWriter.ToArray(model);
            _ = ModelLoader.ReadMetadata(data, name);
        }
        catch (ModelFormatException)
        {
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Truncated_files_fail_cleanly(string relative)
    {
        var data = File.ReadAllBytes(DatasetPaths.File(relative));
        var name = Path.GetFileName(relative);
        var step = Math.Max(1, data.Length / 60);
        for (var length = 0; length < data.Length; length += step)
        {
            MustFailCleanly(data[..length], name);
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Mutated_files_fail_cleanly(string relative)
    {
        var original = File.ReadAllBytes(DatasetPaths.File(relative));
        var name = Path.GetFileName(relative);
        var rounds = int.TryParse(Environment.GetEnvironmentVariable("XRAY_3D_MUTATIONS"), out var n) ? n : 40;
        var seed = int.TryParse(Environment.GetEnvironmentVariable("XRAY_3D_SEED"), out var configured) ? configured : 12345;
        var random = new Random(relative.Aggregate(seed, (h, c) => unchecked(h * 31 + c)));
        for (var round = 0; round < rounds; round++)
        {
            var data = (byte[])original.Clone();
            for (var k = 0; k < 1 + random.Next(8); k++)
            {
                data[random.Next(data.Length)] = (byte)random.Next(256);
            }

            try
            {
                MustFailCleanly(data, name);
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                throw new InvalidOperationException($"{relative} seed {seed} round {round}: {ex.GetType().Name}: {ex.Message}", ex);
            }
        }
    }

    [Fact]
    public void Garbage_and_empty_input_fail_cleanly()
    {
        var random = new Random(7);
        foreach (var length in new[] { 0, 1, 3, 12, 84, 1000, 100_000 })
        {
            var data = new byte[length];
            random.NextBytes(data);
            foreach (var name in new[] { "a.stl", "a.obj", "a.ply", "a.glb", "a.3mf", "a.sldprt", "a.bin" })
            {
                MustFailCleanly(data, name);
            }
        }
    }

    [Fact]
    public void Input_over_the_size_limit_is_refused()
    {
        var data = File.ReadAllBytes(DatasetPaths.File("synthetic/cube-binary.stl"));

        Assert.Throws<ModelFormatException>(() => ModelLoader.Load(data, "x.stl", new LoadOptions { MaxInputBytes = 100 }));
        Assert.Throws<ModelFormatException>(() => ModelLoader.Load(data, "x.stl", new LoadOptions { MaxTriangles = 5 }));
    }

    [Fact]
    public void Cancellation_is_honoured()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var data = File.ReadAllBytes(DatasetPaths.File("sldasm/nist-mtc-assembly.sldasm"));

        Assert.ThrowsAny<OperationCanceledException>(() => ModelLoader.Load(data, "x.sldasm", new LoadOptions { Cancellation = cts.Token }));
    }
}
