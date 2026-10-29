using System.IO.Compression;
using System.Numerics;

namespace XRay.ThreeD.Tests;

public class IfcTests
{
    private static Model3D Load() => ModelLoader.Load(DatasetPaths.File("synthetic/building.ifc"));

    private static SceneNode Find(Model3D model, string name) => model.Nodes().First(n => n.Name == name);

    /// <summary>The node's mesh under its full world transform: (volume, bounds).</summary>
    internal static (double Volume, BoundingBox Bounds) Measure(Model3D model, SceneNode target)
    {
        (double, BoundingBox)? result = null;
        void Visit(SceneNode node, Matrix4x4 parent)
        {
            var world = node.Transform * parent;
            if (node == target && node.Mesh is { } mesh)
            {
                double volume = 0;
                var p = new Vector3[mesh.VertexCount];
                for (var i = 0; i < p.Length; i++)
                {
                    p[i] = Vector3.Transform(new Vector3(mesh.Positions[3 * i], mesh.Positions[3 * i + 1], mesh.Positions[3 * i + 2]), world);
                }

                for (var t = 0; t + 2 < mesh.Indices.Length; t += 3)
                {
                    volume += Vector3.Dot(p[mesh.Indices[t]], Vector3.Cross(p[mesh.Indices[t + 1]], p[mesh.Indices[t + 2]])) / 6.0;
                }

                result = (volume, mesh.ComputeBounds(world));
            }

            foreach (var child in node.Children)
            {
                Visit(child, world);
            }
        }

        foreach (var root in model.Roots)
        {
            Visit(root, Matrix4x4.Identity);
        }

        return result ?? throw new InvalidOperationException("node has no mesh");
    }

    [Fact]
    public void Spatial_tree_names_ids_and_header()
    {
        var model = Load();
        var meta = model.Metadata;

        Assert.Equal("ifc", meta.Format.Id);
        Assert.Equal("IFC4", meta.FormatVersion);
        Assert.Equal("Fixture house", meta.Title);
        Assert.Equal("Fixture Author", meta.Author);
        Assert.Equal("Fixture Org", meta.Company);
        Assert.Equal(LengthUnit.Millimeter, meta.Units);
        Assert.Equal(UpAxis.Z, meta.UpAxis);
        Assert.Contains("Fixture Modeller 1.0", meta.Application);

        var project = Assert.Single(model.Roots);
        Assert.Equal("Fixture house", project.Name);
        Assert.Equal("IFCPROJECT", project.Properties["IfcType"]);
        var site = Assert.Single(project.Children);
        Assert.Equal("Plot", site.Name);
        var building = Assert.Single(site.Children);
        Assert.Equal("House", building.Name);
        Assert.Equal("0Fixture0000000000B001", building.Properties["GlobalId"]);
        var storey = Assert.Single(building.Children);
        Assert.Equal("Level 1", storey.Name);
        Assert.Equal("3000", storey.Properties["Elevation"]);
        Assert.Equal(10, storey.Children.Count);
        Assert.Equal(["Plot", "House", "Level 1"], meta.Features.Select(f => f.Name));
        Assert.Contains("Exterior wall", meta.PartNames);
        Assert.Contains("Brick", meta.MaterialNames);
    }

    [Fact]
    public void Property_sets_quantities_and_material_go_to_the_node_extras()
    {
        var wall = Find(Load(), "Exterior wall");

        Assert.Equal("2HR", wall.Properties["Pset_WallCommon.FireRating"]);
        Assert.Equal("true", wall.Properties["Pset_WallCommon.IsExternal"]);
        Assert.Equal("0.35", wall.Properties["Pset_WallCommon.ThermalTransmittance"]);
        Assert.Equal("4000", wall.Properties["Qto_WallBaseQuantities.Length"]);
        Assert.Equal("12", wall.Properties["Qto_WallBaseQuantities.NetSideArea"]);
        Assert.Equal("Brick", wall.Properties["Material"]);
        Assert.Equal("W-01", wall.Properties["Tag"]);
        Assert.Equal("Basic Wall:Exterior", wall.Properties["ObjectType"]);
        Assert.Equal("0Fixture00000000WALL01", wall.Properties["GlobalId"]);
    }

    [Fact]
    public void Extruded_profiles_are_placed_along_the_placement_chain_and_wind_outwards()
    {
        var model = Load();

        // Storey origin in world coordinates: building (1000, 2000, 0) + storey (0, 0, 3000).
        var (wallVolume, wall) = Measure(model, Find(model, "Exterior wall"));
        Assert.Equal(4000.0 * 200 * 3000, wallVolume, 1e-3 * wallVolume);
        Assert.Equal(new Vector3(1000, 2000, 3000), wall.Min);
        Assert.Equal(new Vector3(5000, 2200, 6000), wall.Max);

        // The slab is extruded downwards: positive volume, hanging below the storey level.
        var (slabVolume, slab) = Measure(model, Find(model, "Floor slab"));
        Assert.Equal(24e6 * 250, slabVolume, 1e-3 * slabVolume);
        Assert.Equal(3000 - 250, slab.Min.Z);
        Assert.Equal(3000, slab.Max.Z);

        var (columnVolume, column) = Measure(model, Find(model, "Column C1"));
        Assert.InRange(columnVolume / (Math.PI * 150 * 150 * 2800), 0.98, 1.0);
        Assert.Equal(1000 + 5800, (column.Min.X + column.Max.X) / 2, 1.0);
    }

    [Fact]
    public void Face_sets_breps_and_mapped_items()
    {
        var model = Load();

        Assert.Equal(500.0 * 500 * 500, Measure(model, Find(model, "Crate")).Volume, 1000.0);
        Assert.Equal(600.0 * 600 * 600 / 6, Measure(model, Find(model, "Tetrahedron")).Volume, 1000.0);
        Assert.Equal(400.0 * 400 * 300 / 3, Measure(model, Find(model, "Pyramid")).Volume, 1000.0);

        var (volume, post) = Measure(model, Find(model, "Post 2"));
        Assert.Equal(300.0 * 300 * 500, volume, 1000.0);
        Assert.Equal(new Vector3(1000 + 5200, 2000 + 4000, 3000), (post.Min + post.Max) / 2 - new Vector3(0, 0, 250));
    }

    [Fact]
    public void Unsupported_representations_keep_the_node_and_warn()
    {
        var model = Load();
        var pipe = Find(model, "Pipe");

        Assert.Null(pipe.Mesh);
        Assert.StartsWith("unsupported: IFCSWEPTDISKSOLID", pipe.Properties["Geometry"], StringComparison.Ordinal);
        Assert.Contains(model.Warnings, w => w.Contains("IFCSWEPTDISKSOLID", StringComparison.Ordinal));
        Assert.Null(Find(model, "Placeholder").Mesh);
        Assert.Contains(model.Metadata.Notes, n => n.Contains("no drawable geometry", StringComparison.Ordinal));
    }

    [Fact]
    public void Surface_style_becomes_a_material()
    {
        var model = Load();
        var wall = Find(model, "Exterior wall");

        var group = Assert.Single(wall.Mesh!.Groups);
        var material = model.Materials[group.Material];
        Assert.Equal("Brick red", material.Name);
        Assert.Equal(new Vector4(0.8f, 0.3f, 0.2f, 1f), material.BaseColor);
    }

    [Fact]
    public void Metadata_only_read_matches_the_full_read_and_converts_to_glb()
    {
        var path = DatasetPaths.File("synthetic/building.ifc");
        var bytes = File.ReadAllBytes(path);
        var light = ModelLoader.ReadMetadata(bytes, "building.ifc");
        var full = Load().Metadata;

        Assert.Equal(full.Title, light.Title);
        Assert.Equal(full.Units, light.Units);
        Assert.Equal(full.PartNames, light.PartNames);
        Assert.True(light.HasGeometry);
        Assert.Null(light.Statistics);
        Assert.NotNull(full.Statistics);

        var glb = ModelLoader.ConvertToGlb(bytes, "building.ifc");
        GlbValidation.Validate(glb);
    }

    [Fact]
    public void Zipped_ifc_is_detected_and_read()
    {
        var bytes = File.ReadAllBytes(DatasetPaths.File("synthetic/building.ifc"));
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var stream = zip.CreateEntry("model.ifc").Open();
            stream.Write(bytes);
        }

        var packed = output.ToArray();
        Assert.Equal("ifc", ModelLoader.Detect(packed, "building.ifczip").Id);
        Assert.Equal("Fixture house", ModelLoader.Load(packed, "building.ifczip").Metadata.Title);
    }

    [Fact]
    public void Triangle_limit_is_enforced()
    {
        Assert.Throws<ModelFormatException>(() => ModelLoader.Load(DatasetPaths.File("synthetic/building.ifc"), new LoadOptions { MaxTriangles = 10 }));
    }
}
