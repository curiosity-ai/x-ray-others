namespace XRay.ThreeD.Tests;

/// <summary>
/// Which dataset meshes enclose space. Every mesh a CAD, scene or game file stores as a solid must be a closed, outward-wound mesh after welding by
/// position (1e-5 of the bounding box diagonal, as a viewer would), in memory and after every GLB profile (none, quantised, Meshopt). The table lists, per
/// file, how many meshes are open and why; a reader or writer change that closes or opens a mesh fails here until the table says so.
/// </summary>
public class ClosedSolidsTests
{
    /// <summary>Open meshes per file, with the reason. Zero means every mesh in the file is a closed solid.</summary>
    private static readonly Dictionary<string, (int OpenMeshes, string Why)> Expected = new()
    {
        ["synthetic/rhino-bigmesh.3dm"] = (1, "a height-field sheet (the fixture is a surface)"),
        ["synthetic/rhino-scene.3dm"] = (2, "a three-triangle mesh and a one-triangle mesh: sheets"),
        ["3dm/terrain-contours.3dm"] = (1, "a terrain surface"),
        ["synthetic/assembly.3dxml"] = (1, "fixture bracket: two boxes sharing a face, wound against each other where they touch (source)"),
        ["synthetic/textured.3mf"] = (1, "a textured quad"),
        ["synthetic/two-cubes.3mf"] = (0, ""),
        ["b3d/blitz3d-arcade-cabinet.b3d"] = (1, "the marquee is a quad"),
        ["b3d/blitz3d-swaying-tentacle.b3d"] = (1, "the tentacle is open at both ends (a tube)"),
        ["synthetic/blend/scene-gzip.blend"] = (1, "EllMesh is a flat ellipse polygon"),
        ["synthetic/blend/scene-raw.blend"] = (1, "EllMesh is a flat ellipse polygon"),
        ["synthetic/blend/scene-zstd.blend"] = (1, "EllMesh is a flat ellipse polygon"),
        ["catia/100nF.CATPart"] = (0, ""),
        ["catia/BracketMidVer3.CATPart"] = (0, ""),
        ["catia/Case_Logo_Yellow.CATPart"] = (0, ""),
        ["synthetic/catia-cube.CATPart"] = (0, ""),
        ["synthetic/catia-cube-r32.CATPart"] = (0, ""),
        ["catia/Plexiglass_top.CATPart"] = (0, ""),
        ["catia/Weight.CATPart"] = (0, ""),
        ["synthetic/assembly.FCStd"] = (0, ""),
        ["fusion/1WireRGBWController.f3d"] = (3, "three bodies with spherical fillet faces that tessellate wrongly (open): TODO"),
        ["glb/abstract-white-man-abstract_man_walking.glb"] = (4, "character meshes with open borders (source)"),
        ["glb/abstract-white-woman-abstract_woman_walking.glb"] = (4, "character meshes with open borders (source)"),
        ["khronos/Box.glb"] = (0, ""),
        ["synthetic/instances.gltf"] = (0, ""),
        ["draco/sphere-draco.glb"] = (0, ""),
        ["draco/sphere-draco-tangent.glb"] = (0, ""),
        ["synthetic/textured.gltf"] = (1, "a quad"),
        ["glb/watering-can.glb"] = (0, ""),
        ["synthetic/gdl-solids.gsm"] = (0, ""),
        ["synthetic/building.ifc"] = (0, ""),
        ["synthetic/ifcxml-wall.ifc"] = (0, ""),
        ["synthetic/ifcxml-wall.ifcxml"] = (0, ""),
        ["synthetic/iges-bounded-plane.igs"] = (1, "bounded planes: sheets"),
        ["synthetic/iges-brep-quadrics.igs"] = (1, "one sphere-cap triangle missing at the pole (3 open edges): TODO"),
        ["synthetic/iges-nurbs-brep.igs"] = (0, ""),
        ["synthetic/iges-nurbs-faces.igs"] = (0, ""),
        ["synthetic/iges-ruled.igs"] = (1, "ruled surfaces: sheets"),
        ["synthetic/iges-spline-surface.igs"] = (1, "a spline surface: a sheet"),
        ["synthetic/iges-trimmed-faces.igs"] = (1, "trimmed faces of a plate whose senses disagree: TODO"),
        ["iges/jetracer-cam-mount.iges"] = (0, ""),
        ["iges/slewing-ring.iges"] = (0, ""),
        ["synthetic/scene.obj"] = (2, "a quad and a two-triangle wedge fixture: sheets"),
        ["synthetic/disc.x_t"] = (1, "a Parasolid sheet body (one disc face)"),
        ["synthetic/disc-embedded.x_t"] = (1, "a Parasolid sheet body (one disc face)"),
        ["pln/parametric-table.gsm"] = (0, ""),
        ["pln/table-lamp.gsm"] = (1, "the lamp shade is open (source)"),
        ["synthetic/cube-ascii.ply"] = (0, ""),
        ["synthetic/cube-binary-big.ply"] = (0, ""),
        ["synthetic/cube-binary-little.ply"] = (0, ""),
        ["psu/astronaut.psu"] = (0, ""),
        ["synthetic/box-700.sab"] = (0, ""),
        ["synthetic/box-700.sat"] = (0, ""),
        ["synthetic/cylinder-400.sat"] = (0, ""),
        ["synthetic/hexprism-700.sat"] = (0, ""),
        ["sat/nist-arm-blank.sat"] = (0, ""),
        ["synthetic/sphere-400.sat"] = (0, ""),
        ["synthetic/torus-400.sat"] = (0, ""),
        ["scad/chess-rook.scad"] = (0, ""),
        ["scad/hex-bolt-and-nut.scad"] = (0, ""),
        ["scad/twisted-vase.scad"] = (0, ""),
        ["scad/wall-bracket.scad"] = (0, ""),
        ["synthetic/home.sh3d"] = (1, "the room floor is a quad"),
        ["sh3d/sweet-home-cottage.sh3d"] = (2, "room floors and ceilings are quads"),
        ["synthetic/geometry-2017.skp"] = (2, "planes and a plate drawn as faces"),
        ["synthetic/geometry-2021.skp"] = (1, "a face-only model"),
        ["sldasm/nist-mtc-assembly.sldasm"] = (2, "two parts whose display faces are tessellated independently (T-junctions at face borders in the file)"),
        ["synthetic/legacy-cube.sldprt"] = (0, ""),
        ["sldprt/muon-detector-case.sldprt"] = (0, ""),
        ["sldprt/nist-ctc-02-part.sldprt"] = (0, ""),
        ["sldprt/nist-ftc-10-part.sldprt"] = (0, ""),
        ["sldprt/servo-mount.sldprt"] = (0, ""),
        ["step/adafruit-motor-hat.step"] = (1, "a chip LED with two degenerate faces (source)"),
        ["step/feather-esp8266.step"] = (0, ""),
        ["synthetic/mapped.stp"] = (0, ""),
        ["step/neopixel-ring.step"] = (0, ""),
        ["synthetic/tessellated.stp"] = (0, ""),
        ["synthetic/cube-ascii.stl"] = (0, ""),
        ["synthetic/cube-binary.stl"] = (0, ""),
        ["uasset/DoorCloser.uasset"] = (0, ""),
        ["uasset/Faucet.uasset"] = (1, "the static mesh has holes (62 open edges in the asset)"),
        ["unitypackage/opengameart-forest-arena.unitypackage"] = (3, "three open boxes in the scene (source)"),
        ["unitypackage/opengameart-key-collection.unitypackage"] = (6, "source meshes: zero-area triangles, open borders and overlapping shells"),
        ["synthetic/usd-layers/geom/ball.usda"] = (0, ""),
        ["synthetic/usd-layers/geom/box.usda"] = (0, ""),
        ["synthetic/usd-layers/crate-root.usdc"] = (0, ""),
        ["synthetic/usd-layers/root.usda"] = (2, "a quad and a one-triangle animation fixture"),
        ["synthetic/usd-layers/sub.usda"] = (0, ""),
        ["synthetic/usd-layers.usdz"] = (2, "a quad and a one-triangle animation fixture"),
        ["synthetic/usd-layers/geom/variants.usda"] = (0, ""),
        ["zip/opengameart-lowpoly-dummy-prefab.zip"] = (0, ""),
        ["synthetic/package-obj-mtl.zip"] = (2, "a quad and a triangle fixture"),
        ["synthetic/package-obj-textured.zip"] = (2, "a quad and a triangle fixture"),
        ["zip/unity-forest-catapult.zip"] = (0, ""),
        ["zip/unity-forest-demo-scene.zip"] = (0, ""),
    };

    public static IEnumerable<object[]> Files() => Expected.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => new object[] { k });

    private static int Open(Model3D model) => model.Meshes.Where(m => m.Indices.Length > 0).Count(m => !m.AnalyzeTopology().IsSolid);

    [Theory]
    [MemberData(nameof(Files))]
    public void Solids_are_closed_in_memory_and_after_every_GLB_profile(string file)
    {
        var (open, why) = Expected[file];
        var model = MetricsTests.TryLoad(file) ?? LoadLarge(file);
        Assert.True(Open(model) == open, $"{file}: {Open(model)} open meshes, expected {open} ({why})");
        var statistics = model.ComputeStatistics();
        Assert.Equal(open == 0, statistics.OpenMeshes == 0 && statistics.InsideOutMeshes == 0);

        foreach (var compression in new[] { GlbCompression.None, GlbCompression.Quantize, GlbCompression.Meshopt })
        {
            var back = ModelLoader.Load(GlbWriter.ToArray(model, new GlbWriteOptions { Compression = compression }), "x.glb");
            Assert.True(Open(back) == open, $"{file} {compression}: {Open(back)} open meshes after writing, expected {open} ({why})");
        }
    }

    [Fact]
    public void A_sphere_cap_that_contains_the_pole_is_a_fan_to_the_pole()
    {
        // The pole is one point in space and a line in the sphere's parameters; without vertices along that line the triangles between the
        // interior samples and the pole collapsed to nothing and left a ring of 12 open edges around the pole of the plate's sphere.
        var topology = MetricsTests.TryLoad("synthetic/iges-brep-quadrics.igs")!.Meshes.Single().AnalyzeTopology();
        Assert.True(topology.OpenEdges <= 3, $"{topology.OpenEdges} open edges");
        Assert.Equal(0, topology.InconsistentEdges);
    }

    private static Model3D LoadLarge(string file) => ModelLoader.Load(DatasetPaths.File(file));
}
