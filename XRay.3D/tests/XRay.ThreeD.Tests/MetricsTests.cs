using System.Numerics;
using System.Text.Json;

namespace XRay.ThreeD.Tests;

/// <summary>
/// Area and volume, checked three ways: against a brute-force sum over the world-space triangles (what a viewer computes from the drawn
/// scene), through every GLB profile (a quantised or recentred mesh must land where the source mesh is), and against a mirrored node.
/// </summary>
public class MetricsTests
{
    /// <summary>The drawn solid: every triangle under its node's world transform, a mirroring transform reading its winding the other way (glTF's rule).</summary>
    internal static (double Area, double Volume, long Triangles) World(Model3D model)
    {
        double area = 0, volume = 0;
        long triangles = 0;
        void Visit(SceneNode node, Matrix4x4 parent)
        {
            var world = node.Transform * parent;
            if (node.Mesh is { } mesh)
            {
                var flip = ((double)world.M11 * ((double)world.M22 * world.M33 - (double)world.M23 * world.M32)
                    - (double)world.M12 * ((double)world.M21 * world.M33 - (double)world.M23 * world.M31)
                    + (double)world.M13 * ((double)world.M21 * world.M32 - (double)world.M22 * world.M31)) < 0;
                var p = mesh.Positions;
                // Double precision throughout: a float world position loses the volume of a small part far from the origin.
                (double X, double Y, double Z) At(uint i)
                {
                    double x = p[i * 3], y = p[i * 3 + 1], z = p[i * 3 + 2];
                    return (x * world.M11 + y * world.M21 + z * world.M31 + world.M41, x * world.M12 + y * world.M22 + z * world.M32 + world.M42, x * world.M13 + y * world.M23 + z * world.M33 + world.M43);
                }

                for (var t = 0; t + 2 < mesh.Indices.Length; t += 3)
                {
                    var a = At(mesh.Indices[t]);
                    var b = At(mesh.Indices[t + (flip ? 2 : 1)]);
                    var c = At(mesh.Indices[t + (flip ? 1 : 2)]);
                    double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z, vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
                    double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                    area += 0.5 * Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    volume += (a.X * (b.Y * c.Z - b.Z * c.Y) - a.Y * (b.X * c.Z - b.Z * c.X) + a.Z * (b.X * c.Y - b.Y * c.X)) / 6.0;
                    triangles++;
                }
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

        return (area, volume, triangles);
    }

    internal static IEnumerable<string> ModelFiles()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(DatasetPaths.File("manifest.json")));
        foreach (var entry in doc.RootElement.EnumerateArray())
        {
            var file = entry.GetProperty("file").GetString()!;
            if (!file.EndsWith(".json", StringComparison.Ordinal) && !file.EndsWith(".drc", StringComparison.Ordinal))
            {
                yield return file;
            }
        }
    }

    internal static Model3D? TryLoad(string relative)
    {
        var path = DatasetPaths.File(relative);
        var root = Path.GetFullPath(Path.GetDirectoryName(path)!);
        var options = new LoadOptions
        {
            ResolveCompanion = name =>
            {
                var full = Path.GetFullPath(Path.Combine(root, name.Replace('\\', '/')));
                return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) && System.IO.File.Exists(full) ? System.IO.File.ReadAllBytes(full) : null;
            },
        };
        try
        {
            var model = ModelLoader.Load(path, options);
            return model.Is2D || model.Meshes.Count == 0 || model.Meshes.Sum(m => (long)m.TriangleCount) > 150_000 ? null : model;
        }
        catch (ModelFormatException)
        {
            return null;
        }
    }

    internal static bool Close(double expected, double actual, double relative, double absolute = 0) =>
        Math.Abs(expected - actual) <= relative * Math.Max(Math.Abs(expected), Math.Abs(actual)) + absolute;

    [Fact]
    public void Statistics_equal_the_triangle_sums_of_the_drawn_scene_on_every_dataset_model()
    {
        var failures = new List<string>();
        var checkedModels = 0;
        foreach (var relative in ModelFiles())
        {
            if (TryLoad(relative) is not { } model || model.Metadata.Statistics is not { } stats)
            {
                continue;
            }

            checkedModels++;
            var (area, volume, _) = World(model);
            if (!Close(area, stats.SurfaceArea, 1e-4) || !Close(volume, stats.SignedVolume, 1e-4, 1e-9 * area))
            {
                failures.Add($"{relative}: area {stats.SurfaceArea:G6} vs {area:G6}, volume {stats.SignedVolume:G6} vs {volume:G6}");
            }
        }

        Assert.True(checkedModels > 60, $"only {checkedModels} models were measured");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void A_closed_consistently_wound_mesh_of_the_dataset_encloses_a_positive_volume()
    {
        var failures = new List<string>();
        foreach (var relative in ModelFiles())
        {
            if (TryLoad(relative) is not { } model)
            {
                continue;
            }

            foreach (var mesh in model.Meshes)
            {
                var (closed, volume) = Closure(mesh);
                if (closed && volume <= 0)
                {
                    failures.Add($"{relative}: closed mesh of {mesh.TriangleCount} triangles is wound inside out (volume {volume:G5})");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    /// <summary>Whether every edge is used once in each direction (after welding by position), and the signed volume.</summary>
    internal static (bool Closed, double Volume) Closure(MeshData mesh)
    {
        var p = mesh.Positions;
        var ids = new Dictionary<(long, long, long), int>();
        int Id(uint i)
        {
            var key = ((long)Math.Round(p[i * 3] * 1e4), (long)Math.Round(p[i * 3 + 1] * 1e4), (long)Math.Round(p[i * 3 + 2] * 1e4));
            if (!ids.TryGetValue(key, out var id))
            {
                ids[key] = id = ids.Count;
            }

            return id;
        }

        var directed = new Dictionary<(int, int), int>();
        double volume = 0;
        for (var t = 0; t + 2 < mesh.Indices.Length; t += 3)
        {
            int a = Id(mesh.Indices[t]), b = Id(mesh.Indices[t + 1]), c = Id(mesh.Indices[t + 2]);
            if (a == b || b == c || a == c)
            {
                continue;
            }

            foreach (var edge in new[] { (a, b), (b, c), (c, a) })
            {
                directed[edge] = directed.GetValueOrDefault(edge) + 1;
            }

            Vector3 A = new(p[mesh.Indices[t] * 3], p[mesh.Indices[t] * 3 + 1], p[mesh.Indices[t] * 3 + 2]);
            Vector3 B = new(p[mesh.Indices[t + 1] * 3], p[mesh.Indices[t + 1] * 3 + 1], p[mesh.Indices[t + 1] * 3 + 2]);
            Vector3 C = new(p[mesh.Indices[t + 2] * 3], p[mesh.Indices[t + 2] * 3 + 1], p[mesh.Indices[t + 2] * 3 + 2]);
            volume += Vector3.Dot(A, Vector3.Cross(B, C)) / 6.0;
        }

        var closed = directed.Count > 0 && directed.All(e => e.Value == 1 && directed.GetValueOrDefault((e.Key.Item2, e.Key.Item1)) == 1);
        return (closed, volume);
    }

    internal static MeshData Cube()
    {
        float[] pos = [0, 0, 0, 1, 0, 0, 1, 1, 0, 0, 1, 0, 0, 0, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1];
        uint[] idx = [0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4, 3, 7, 6, 3, 6, 2, 0, 4, 7, 0, 7, 3, 1, 2, 6, 1, 6, 5];
        return new MeshData { Positions = pos, Indices = idx };
    }

    [Fact]
    public void A_mirrored_instance_is_a_positive_solid_and_the_GLB_leaves_the_winding_to_the_node_determinant()
    {
        var cube = Cube();
        Assert.True(Closure(cube) is { Closed: true, Volume: > 0.99 });

        var model = new Model3D();
        model.Meshes.Add(cube);
        model.Roots.Add(new SceneNode("plain") { Mesh = cube });
        model.Roots.Add(new SceneNode("mirrored") { Mesh = cube, Transform = Matrix4x4.CreateScale(-2, 2, 2) * Matrix4x4.CreateTranslation(10, 0, 0) });
        model.Roots.Add(new SceneNode("mirrored twice") { Mesh = cube, Transform = Matrix4x4.CreateScale(-1, -1, 1) });
        model.Metadata.Statistics = model.ComputeStatistics();

        // 1 + 8 + 1 cubes, all drawn right way round.
        Assert.Equal(10.0, model.Metadata.Statistics.Volume, 6);
        Assert.Equal(6.0 + 6 * 4 + 6, model.Metadata.Statistics.SurfaceArea, 6);

        foreach (var compression in new[] { GlbCompression.None, GlbCompression.Quantize, GlbCompression.Meshopt })
        {
            var glb = GlbWriter.ToArray(model, new GlbWriteOptions { Compression = compression, ConvertToMeters = false, ConvertToYUp = false, Edges = EdgeMode.None });
            var back = ModelLoader.Load(glb, "x.glb");
            var (area, volume, triangles) = World(back);
            Assert.Equal(36, triangles);
            Assert.Equal(10.0, volume, 4);
            Assert.Equal(36.0, area, 4);

            // glTF: the mirrored node's determinant is negative, which makes a renderer read the (unchanged, counter-clockwise) triangles as clockwise.
            var mirrored = back.Nodes().Single(n => n.Name == "mirrored");
            Assert.True(mirrored.Transform.GetDeterminant() < 0);
            Assert.Equal(0, back.Nodes().Count(n => n.Mesh is { } m && Closure(m) is { Closed: true, Volume: < 0 }));
        }
    }

    [Fact]
    public void Non_uniform_scale_changes_each_triangles_area_by_its_own_factor()
    {
        var model = new Model3D();
        var cube = Cube();
        model.Meshes.Add(cube);
        model.Roots.Add(new SceneNode("slab") { Mesh = cube, Transform = Matrix4x4.CreateScale(4, 1, 0.5f) * Matrix4x4.CreateTranslation(3, 3, 3) });
        var stats = model.ComputeStatistics();
        Assert.Equal(2 * (4 * 1 + 4 * 0.5 + 1 * 0.5), stats.SurfaceArea, 6);
        Assert.Equal(2.0, stats.Volume, 6);
    }

    [Fact]
    public void A_surface_that_is_not_closed_keeps_its_position_in_the_volume()
    {
        var model = new Model3D();
        var quad = new MeshData { Positions = [0, 0, 1, 1, 0, 1, 1, 1, 1, 0, 1, 1], Indices = [0, 1, 2, 0, 2, 3] };
        model.Meshes.Add(quad);
        model.Roots.Add(new SceneNode("quad") { Mesh = quad, Transform = Matrix4x4.CreateTranslation(0, 0, 5) });
        var (_, volume, _) = World(model);
        var stats = model.ComputeStatistics();
        Assert.Equal(volume, stats.SignedVolume, 9);

        // The library does not call that a volume: the quad encloses nothing.
        Assert.Equal(0, stats.Volume);
        Assert.Equal(1, stats.OpenMeshes);
        Assert.Equal(4, stats.OpenEdges);
        Assert.False(stats.VolumeIsComplete);
    }
}
