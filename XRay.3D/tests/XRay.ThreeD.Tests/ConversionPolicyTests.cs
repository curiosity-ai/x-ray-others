using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace XRay.ThreeD.Tests;

public class ConversionPolicyTests
{
    internal static JsonElement Json(byte[] glb) => JsonDocument.Parse(glb.AsMemory(20, (int)BitConverter.ToUInt32(glb, 12))).RootElement.Clone();

    private static byte[] CubeStl(double offsetX, double offsetY, double offsetZ)
    {
        int[][] t = [[0, 2, 1], [0, 3, 2], [4, 5, 6], [4, 6, 7], [0, 1, 5], [0, 5, 4], [1, 2, 6], [1, 6, 5], [2, 3, 7], [2, 7, 6], [3, 0, 4], [3, 4, 7]];
        double[][] v = [[0, 0, 0], [1, 0, 0], [1, 1, 0], [0, 1, 0], [0, 0, 1], [1, 0, 1], [1, 1, 1], [0, 1, 1]];
        var sb = new StringBuilder("solid c\n");
        foreach (var tri in t)
        {
            sb.Append("facet normal 0 0 0\nouter loop\n");
            foreach (var i in tri)
            {
                sb.Append(CultureInfo.InvariantCulture, $"vertex {v[i][0] + offsetX:R} {v[i][1] + offsetY:R} {v[i][2] + offsetZ:R}\n");
            }

            sb.Append("endloop\nendfacet\n");
        }

        sb.Append("endsolid c\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    [Fact]
    public void Caller_hints_fill_in_units_and_up_axis_for_formats_that_do_not_say()
    {
        var stl = CubeStl(0, 0, 0);
        var options = new LoadOptions { AssumedUnits = LengthUnit.Millimeter, AssumedUpAxis = UpAxis.Z };

        var model = ModelLoader.Load(stl, "c.stl", options);
        Assert.Equal(LengthUnit.Millimeter, model.Metadata.Units);
        Assert.Equal(UpAxis.Z, model.Metadata.UpAxis);
        Assert.Equal(LengthUnit.Millimeter, ModelLoader.ReadMetadata(stl, "c.stl", options).Units);

        var root = Json(GlbWriter.ToArray(model)).GetProperty("nodes").EnumerateArray().Last();
        Assert.Equal(0.001f, root.GetProperty("scale")[0].GetSingle(), 6);
        Assert.True(root.TryGetProperty("rotation", out _)); // Z-up rotated to Y-up

        // A format that states its units is not overridden.
        var box = ModelLoader.Load(File.ReadAllBytes(DatasetPaths.File("khronos/Box.glb")), "Box.glb", new LoadOptions { AssumedUnits = LengthUnit.Inch, AssumedUpAxis = UpAxis.Z });
        Assert.Equal(LengthUnit.Meter, box.Metadata.Units);
        Assert.Equal(UpAxis.Y, box.Metadata.UpAxis);
    }

    [Fact]
    public void A_far_away_mesh_is_stored_around_its_own_centre_and_keeps_its_place()
    {
        var model = ModelLoader.Load(CubeStl(5_000_000, 6_000_000, 120), "far.stl");
        var before = model.ComputeBounds()!.Value;

        var glb = GlbWriter.ToArray(model);
        GlbValidation.Validate(glb);
        var json = Json(glb);
        var origin = json.GetProperty("meshes")[0].GetProperty("extras").GetProperty("origin").EnumerateArray().Select(e => e.GetSingle()).ToArray();
        Assert.Equal(5_000_000.5f, origin[0]);
        Assert.Equal(6_000_000.5f, origin[1]);
        var accessor = json.GetProperty("accessors")[0];
        Assert.InRange(accessor.GetProperty("max")[0].GetSingle(), 0.4f, 0.6f); // vertices are around zero

        // Up-axis conversion off, so the bounds are comparable with the source's.
        var back = ModelLoader.Load(GlbWriter.ToArray(model, new GlbWriteOptions { ConvertToYUp = false }), "far.glb");
        var after = back.ComputeBounds()!.Value;
        Assert.InRange(Vector3.Distance(before.Min, after.Min), 0, 0.2f);
        Assert.InRange(Vector3.Distance(before.Max, after.Max), 0, 0.2f);

        var plain = Json(GlbWriter.ToArray(model, new GlbWriteOptions { RecentreFarModels = false }));
        Assert.False(plain.GetProperty("meshes")[0].TryGetProperty("extras", out var plainExtras) && plainExtras.TryGetProperty("origin", out _));
        Assert.InRange(plain.GetProperty("accessors")[0].GetProperty("max")[0].GetSingle(), 4_999_999f, 5_000_002f);

        // A model near the origin is left alone.
        Assert.False(Json(GlbWriter.ToArray(ModelLoader.Load(CubeStl(2, 3, 4), "near.stl"))).GetProperty("meshes")[0].TryGetProperty("extras", out var nearExtras) && nearExtras.TryGetProperty("origin", out _));
    }

    [Fact]
    public void Smooth_and_flat_normals_are_derived_for_meshes_without_them()
    {
        var model = ModelLoader.Load(CubeStl(0, 0, 0), "c.stl");
        Assert.Empty(model.Meshes[0].Normals);
        Assert.False(Json(GlbWriter.ToArray(model)).GetProperty("meshes")[0].GetProperty("primitives")[0].GetProperty("attributes").TryGetProperty("NORMAL", out _));

        var smooth = GlbWriter.ToArray(model, new GlbWriteOptions { Normals = GlbNormals.Smooth });
        GlbValidation.Validate(smooth);
        var smoothMesh = ModelLoader.Load(smooth, "s.glb").Meshes[0];
        Assert.Equal(8, smoothMesh.VertexCount);
        Assert.Equal(smoothMesh.VertexCount * 3, smoothMesh.Normals.Length);
        // The corner (0,0,0) of the cube points away from the cube (all components negative); the diagonal split of the faces makes it uneven.
        var corner = Enumerable.Range(0, smoothMesh.VertexCount).Single(v => smoothMesh.Positions[3 * v] == 0 && smoothMesh.Positions[3 * v + 1] == 0 && smoothMesh.Positions[3 * v + 2] == 0);
        var normal = new Vector3(smoothMesh.Normals[3 * corner], smoothMesh.Normals[3 * corner + 1], smoothMesh.Normals[3 * corner + 2]);
        Assert.True(normal.X < 0 && normal.Y < 0 && normal.Z < 0);
        Assert.Equal(1f, normal.Length(), 3);

        var flat = GlbWriter.ToArray(model, new GlbWriteOptions { Normals = GlbNormals.Flat });
        GlbValidation.Validate(flat);
        var flatMesh = ModelLoader.Load(flat, "f.glb").Meshes[0];
        Assert.Equal(36, flatMesh.VertexCount);
        for (var t = 0; t < 12; t++)
        {
            var n0 = new Vector3(flatMesh.Normals[9 * t], flatMesh.Normals[9 * t + 1], flatMesh.Normals[9 * t + 2]);
            var n2 = new Vector3(flatMesh.Normals[9 * t + 6], flatMesh.Normals[9 * t + 7], flatMesh.Normals[9 * t + 8]);
            Assert.Equal(n0, n2);
            Assert.Equal(1f, MathF.Abs(n0.X) + MathF.Abs(n0.Y) + MathF.Abs(n0.Z), 3); // a cube face normal is an axis
        }

        // The model itself is not changed by writing.
        Assert.Empty(model.Meshes[0].Normals);
        Assert.Equal(8, model.Meshes[0].VertexCount);
    }

    [Fact]
    public void A_glb_converts_to_itself()
    {
        var bytes = File.ReadAllBytes(DatasetPaths.File("khronos/Box.glb"));
        Assert.Equal(bytes, ModelLoader.ConvertToGlb(bytes, "Box.glb"));
        // Explicit options still rewrite.
        Assert.NotEqual(bytes, ModelLoader.ConvertToGlb(bytes, "Box.glb", new GlbWriteOptions { Compression = GlbCompression.Quantize }));
        // And a damaged GLB is still refused, not passed on.
        Assert.Throws<ModelFormatException>(() => ModelLoader.ConvertToGlb(bytes[..40], "Box.glb"));
    }
}
