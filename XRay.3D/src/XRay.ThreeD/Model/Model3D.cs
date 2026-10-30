using System.Numerics;

namespace XRay.ThreeD;

/// <summary>A loaded 3D model: a scene graph over shared meshes, with materials, layers and metadata.</summary>
public sealed class Model3D
{
    public ModelMetadata Metadata { get; } = new();

    /// <summary>Top-level nodes. A model with one anonymous mesh has one root with no name.</summary>
    public List<SceneNode> Roots { get; } = new();

    /// <summary>Meshes shared by nodes. Instanced parts reference one mesh from many nodes.</summary>
    public List<MeshData> Meshes { get; } = new();

    public List<ModelMaterial> Materials { get; } = new();

    /// <summary>Images that materials refer to (<see cref="ModelMaterial.BaseColorTexture"/>), as PNG, JPEG or WebP bytes.</summary>
    public List<ModelTexture> Textures { get; } = new();

    public List<ModelLayer> Layers { get; } = new();

    /// <summary>
    /// True when the content is a drawing rather than a model: planar entities (a DXF with nothing out of its plane, a SolidWorks drawing,
    /// curve-only IGES). <see cref="SvgWriter"/> writes such a model as SVG; <see cref="GlbWriter"/> stays the output for 3D content.
    /// </summary>
    public bool Is2D { get; set; }

    /// <summary>Text drawn in the model's plane (drawings): annotations, dimension text, attributes. Positions are in world space.</summary>
    public List<ModelText> Texts { get; } = new();

    /// <summary>Filled areas (solid hatches, SOLID entities) of a drawing, in world space.</summary>
    public List<ModelFill> Fills { get; } = new();

    /// <summary>Raster images placed in the drawing's plane (the sheets of a drawing saved without vector content), drawn behind everything else.</summary>
    public List<ModelImage> Images { get; } = new();

    /// <summary>Sets <see cref="Is2D"/> when there is something to draw and none of it leaves the XY plane; returns the result.</summary>
    public bool DetectIs2D()
    {
        var any = Texts.Count > 0 || Fills.Count > 0 || Images.Count > 0 || Meshes.Any(m => m.VertexCount > 0);
        var box = ComputeBounds();
        var flat = box is not { } b || b.Size.Z <= 1e-6f * Math.Max(Math.Max(b.Size.X, b.Size.Y), 1e-9f);
        foreach (var fill in Fills)
        {
            flat &= fill.Loops.All(l => l.All(p => Math.Abs(p.Z) <= 1e-6f * Math.Max(box?.Size.X ?? 1, 1)));
        }

        Is2D = any && flat;
        return Is2D;
    }

    /// <summary>The preview image saved in the file (PNG, BMP or JPEG bytes), when it carries one.</summary>
    public PreviewImage? Preview { get; set; }

    /// <summary>Things the reader skipped or could not decode; the model is still usable.</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>Every node, depth first.</summary>
    public IEnumerable<SceneNode> Nodes()
    {
        var stack = new Stack<SceneNode>(Enumerable.Reverse(Roots));
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            for (var i = node.Children.Count - 1; i >= 0; i--)
            {
                stack.Push(node.Children[i]);
            }
        }
    }

    /// <summary>Counts nodes, meshes and drawn geometry (instances counted per use).</summary>
    public ModelStatistics ComputeStatistics() => ComputeStatisticsCore(CancellationToken.None);

    internal ModelStatistics ComputeStatisticsCore(CancellationToken cancellation)
    {
        long vertices = 0;
        long triangles = 0;
        var nodes = 0;
        foreach (var node in Nodes())
        {
            nodes++;
            if (node.Mesh is { } mesh)
            {
                vertices += mesh.VertexCount;
                triangles += mesh.TriangleCount;
            }
        }

        // Area and volume per unique mesh (in its own frame), then carried to each instance's world transform. A similarity transform (rotation,
        // uniform scale, translation) needs no more than the mesh's totals; anything else (a non-uniform scale, a shear: a furniture piece fitted
        // to a box, a node scaled along one axis) changes every triangle's area by its own factor, so those instances are measured triangle by
        // triangle (once per distinct matrix of a mesh). The volume of a closed mesh follows from the determinant alone; an open one also depends
        // on where it sits, which the vector-area term below carries.
        var perMesh = new Dictionary<MeshData, MeshTotals>(ReferenceEqualityComparer.Instance);
        var exact = new Dictionary<(MeshData, Matrix4x4), (double Area, double Volume)>();
        double totalArea = 0;
        double totalVolume = 0;
        double totalSigned = 0;
        int closedMeshes = 0, openMeshes = 0, insideOutMeshes = 0, openEdges = 0, nonManifoldEdges = 0, inconsistentEdges = 0;
        void Visit(SceneNode node, Matrix4x4 parent)
        {
            var world = node.Transform * parent;
            if (node.Mesh is { } mesh && mesh.Indices.Length > 0)
            {
                if (!perMesh.TryGetValue(mesh, out var m))
                {
                    m = Totals(mesh) with { Topology = MeshTopology.Analyze(mesh, MeshTopology.DefaultWeldTolerance, cancellation) };
                    perMesh[mesh] = m;
                    if (m.Topology.IsSolid)
                    {
                        closedMeshes++;
                    }
                    else if (m.Topology.IsInsideOut)
                    {
                        insideOutMeshes++;
                    }
                    else
                    {
                        openMeshes++;
                    }

                    openEdges += m.Topology.OpenEdges;
                    nonManifoldEdges += m.Topology.NonManifoldEdges;
                    inconsistentEdges += m.Topology.InconsistentEdges;
                }

                var (area, volume) = Carry(mesh, m, world, exact);
                totalArea += area;
                totalSigned += volume;

                // Only a closed, outward-facing mesh encloses space: the signed sum over an open surface depends on where the origin is.
                if (m.Topology.IsSolid)
                {
                    totalVolume += volume;
                }
            }

            foreach (var child in node.Children)
            {
                Visit(child, world);
            }
        }

        foreach (var root in Roots)
        {
            Visit(root, Matrix4x4.Identity);
        }

        return new ModelStatistics(nodes, Meshes.Count, vertices, triangles, Materials.Count, Meshes.Sum(m => (long)m.VertexCount), Meshes.Sum(m => (long)m.TriangleCount))
        {
            SurfaceArea = totalArea,
            Volume = totalVolume,
            SignedVolume = totalSigned,
            ClosedMeshes = closedMeshes,
            OpenMeshes = openMeshes,
            InsideOutMeshes = insideOutMeshes,
            OpenEdges = openEdges,
            NonManifoldEdges = nonManifoldEdges,
            InconsistentEdges = inconsistentEdges,
        };
    }

    /// <summary>Area, signed volume about the origin and the sum of the triangles' vector areas of a mesh in its own frame.</summary>
    private readonly record struct MeshTotals(double Area, double Volume, double Sx, double Sy, double Sz)
    {
        public MeshTopology Topology { get; init; }
    }

    private static MeshTotals Totals(MeshData mesh)
    {
        double area = 0, volume = 0, sx = 0, sy = 0, sz = 0;
        var p = mesh.Positions;
        for (var t = 0; t + 2 < mesh.Indices.Length; t += 3)
        {
            int a = (int)mesh.Indices[t] * 3, b = (int)mesh.Indices[t + 1] * 3, c = (int)mesh.Indices[t + 2] * 3;
            double ax = p[a], ay = p[a + 1], az = p[a + 2];
            double bx = p[b], by = p[b + 1], bz = p[b + 2];
            double cx = p[c], cy = p[c + 1], cz = p[c + 2];
            double ux = bx - ax, uy = by - ay, uz = bz - az, vx = cx - ax, vy = cy - ay, vz = cz - az;
            double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
            area += 0.5 * Math.Sqrt(nx * nx + ny * ny + nz * nz);
            sx += 0.5 * nx;
            sy += 0.5 * ny;
            sz += 0.5 * nz;
            volume += (ax * (by * cz - bz * cy) - ay * (bx * cz - bz * cx) + az * (bx * cy - by * cx)) / 6.0;
        }

        return new MeshTotals(area, volume, sx, sy, sz);
    }

    /// <summary>
    /// Area and volume of one drawn instance. A transform with a negative determinant mirrors the mesh; glTF (and every renderer) then reads the
    /// triangles' winding the other way, so the solid is drawn with the winding it was meant to have and its volume counts as positive.
    /// </summary>
    private static (double Area, double Volume) Carry(MeshData mesh, MeshTotals m, Matrix4x4 w, Dictionary<(MeshData, Matrix4x4), (double Area, double Volume)> exact)
    {
        // The linear part's determinant in double: the 4x4 determinant in float loses digits to a large translation.
        double det = (double)w.M11 * ((double)w.M22 * w.M33 - (double)w.M23 * w.M32)
            - (double)w.M12 * ((double)w.M21 * w.M33 - (double)w.M23 * w.M31)
            + (double)w.M13 * ((double)w.M21 * w.M32 - (double)w.M22 * w.M31);
        if (!double.IsFinite(det) || det == 0)
        {
            return (0, 0);
        }

        // Row-vector convention: the first three rows are the images of the axes. Similarity: they are orthogonal and equally long.
        double l1 = w.M11 * w.M11 + w.M12 * w.M12 + w.M13 * w.M13;
        double l2 = w.M21 * w.M21 + w.M22 * w.M22 + w.M23 * w.M23;
        double l3 = w.M31 * w.M31 + w.M32 * w.M32 + w.M33 * w.M33;
        double d12 = w.M11 * w.M21 + w.M12 * w.M22 + w.M13 * w.M23;
        double d13 = w.M11 * w.M31 + w.M12 * w.M32 + w.M13 * w.M33;
        double d23 = w.M21 * w.M31 + w.M22 * w.M32 + w.M23 * w.M33;
        var tol = 1e-5 * Math.Max(l1, Math.Max(l2, l3));
        var similarity = Math.Abs(l1 - l2) <= tol && Math.Abs(l1 - l3) <= tol && Math.Abs(d12) <= tol && Math.Abs(d13) <= tol && Math.Abs(d23) <= tol;
        var moved = w.M41 != 0 || w.M42 != 0 || w.M43 != 0;
        var openSurface = Math.Abs(m.Sx) + Math.Abs(m.Sy) + Math.Abs(m.Sz) > 1e-9 * m.Area;
        if (similarity && !(moved && openSurface))
        {
            return (m.Area * l1, Math.Abs(det) * m.Volume);
        }

        var key = (mesh, w);
        if (!exact.TryGetValue(key, out var result))
        {
            double area = 0, volume = 0;
            var p = mesh.Positions;
            for (var t = 0; t + 2 < mesh.Indices.Length; t += 3)
            {
                var a = Transform(p, (int)mesh.Indices[t] * 3, w);
                var b = Transform(p, (int)mesh.Indices[t + 1] * 3, w);
                var c = Transform(p, (int)mesh.Indices[t + 2] * 3, w);
                double ux = b.X - a.X, uy = b.Y - a.Y, uz = b.Z - a.Z, vx = c.X - a.X, vy = c.Y - a.Y, vz = c.Z - a.Z;
                double nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
                area += 0.5 * Math.Sqrt(nx * nx + ny * ny + nz * nz);
                volume += (a.X * (b.Y * c.Z - b.Z * c.Y) - a.Y * (b.X * c.Z - b.Z * c.X) + a.Z * (b.X * c.Y - b.Y * c.X)) / 6.0;
            }

            result = (area, det < 0 ? -volume : volume);
            exact[key] = result;
        }

        return result;
    }

    private static (double X, double Y, double Z) Transform(float[] p, int i, Matrix4x4 w)
    {
        double x = p[i], y = p[i + 1], z = p[i + 2];
        return (
            x * w.M11 + y * w.M21 + z * w.M31 + w.M41,
            x * w.M12 + y * w.M22 + z * w.M32 + w.M42,
            x * w.M13 + y * w.M23 + z * w.M33 + w.M43);
    }

    /// <summary>Axis-aligned bounds of every mesh under its node's world transform, or null for an empty model.</summary>
    public BoundingBox? ComputeBounds()
    {
        BoundingBox? total = null;
        void Visit(SceneNode node, Matrix4x4 parent)
        {
            var world = node.Transform * parent;
            if (node.Mesh is { } mesh && mesh.VertexCount > 0)
            {
                var box = mesh.ComputeBounds(world);
                total = total is null ? box : total.Value.Union(box);
            }

            foreach (var child in node.Children)
            {
                Visit(child, world);
            }
        }

        foreach (var root in Roots)
        {
            Visit(root, Matrix4x4.Identity);
        }

        return total;
    }
}

/// <summary>One node of the scene graph: a named part, assembly or group with a transform.</summary>
public sealed class SceneNode
{
    public SceneNode(string? name = null)
    {
        Name = name;
    }

    public string? Name { get; set; }

    /// <summary>Local transform in row-vector convention (System.Numerics): <c>world = local * parent</c>.</summary>
    public Matrix4x4 Transform { get; set; } = Matrix4x4.Identity;

    public MeshData? Mesh { get; set; }

    public List<SceneNode> Children { get; } = new();

    /// <summary>Per-node metadata, written to glTF <c>extras</c>.</summary>
    public Dictionary<string, string> Properties { get; } = new(StringComparer.Ordinal);

    /// <summary>Index into <see cref="Model3D.Layers"/>, or -1.</summary>
    public int Layer { get; set; } = -1;
}

/// <summary>Triangle mesh, split into material groups. Positions are in the model's units (see metadata).</summary>
public sealed class MeshData
{
    public string? Name { get; set; }

    /// <summary>x,y,z per vertex.</summary>
    public float[] Positions { get; set; } = [];

    /// <summary>x,y,z per vertex, or empty when the file has none.</summary>
    public float[] Normals { get; set; } = [];

    /// <summary>
    /// u,v per vertex in glTF's convention (the origin is the top left corner of the image, v grows downwards), or empty when the
    /// file has none. Readers of formats whose origin is the bottom left (OBJ, 3MF) flip v.
    /// </summary>
    public float[] TexCoords { get; set; } = [];

    /// <summary>r,g,b,a per vertex (0..1, sRGB), or empty when the file has none.</summary>
    public float[] Colors { get; set; } = [];

    /// <summary>For gaussian-splat clouds: the x,y,z extent of each splat's ellipsoid (linear, after the file's log encoding), or empty. Colours carry the splat's colour and opacity (alpha); glTF has no point sizes, so the writer does not use this.</summary>
    public float[] SplatScales { get; set; } = [];

    /// <summary>Three indices per triangle. Empty for a point cloud (the vertices are drawn as points).</summary>
    public uint[] Indices { get; set; } = [];

    /// <summary>
    /// The source faces (a CAD face is a patch of triangles), as index ranges, in the order of the source's face list.
    /// Empty when the format has no faces. A renderer maps a picked triangle to its face by searching these ranges.
    /// </summary>
    public MeshFace[] Faces { get; set; } = [];

    /// <summary>Feature edges as index pairs into <see cref="Positions"/>, when precomputed (see <see cref="MeshEdges"/>); empty otherwise.</summary>
    public uint[] EdgeIndices { get; set; } = [];

    /// <summary>Material for the edge lines of a line-only mesh (<see cref="Indices"/> empty, <see cref="EdgeIndices"/> set), or -1.</summary>
    public int EdgeMaterial { get; set; } = -1;

    /// <summary>Contiguous index ranges that share a material. Empty means one group over all indices.</summary>
    public List<MeshGroup> Groups { get; } = new();

    public int VertexCount => Positions.Length / 3;

    /// <summary>The edge structure of the triangles after welding vertices by position: whether the mesh is closed, how many edges are open or wound against a neighbour, and its signed volume.</summary>
    public MeshTopology AnalyzeTopology() => MeshTopology.Analyze(this, DefaultWeld, CancellationToken.None);

    private const double DefaultWeld = MeshTopology.DefaultWeldTolerance;

    public int TriangleCount => Indices.Length / 3;

    public BoundingBox ComputeBounds(Matrix4x4? transform = null)
    {
        var box = BoundingBox.Empty;
        var m = transform ?? Matrix4x4.Identity;
        for (var i = 0; i + 2 < Positions.Length; i += 3)
        {
            box = box.Include(Vector3.Transform(new Vector3(Positions[i], Positions[i + 1], Positions[i + 2]), m));
        }

        return box;
    }
}

/// <summary>A source face: <see cref="IndexCount"/> indices starting at <see cref="FirstIndex"/> in <see cref="MeshData.Indices"/>.</summary>
public readonly record struct MeshFace(int FirstIndex, int IndexCount);

/// <summary>A run of <see cref="Count"/> indices starting at <see cref="Start"/> drawn with one material.</summary>
public readonly record struct MeshGroup(int Start, int Count, int Material);

/// <summary>Surface colour. Component values are 0..1 in sRGB.</summary>
public sealed class ModelMaterial
{
    public string? Name { get; set; }

    public Vector4 BaseColor { get; set; } = new(0.8f, 0.8f, 0.8f, 1f);

    public float Metallic { get; set; }

    public float Roughness { get; set; } = 0.6f;

    /// <summary>The image multiplied with <see cref="BaseColor"/>, drawn with the mesh's <see cref="MeshData.TexCoords"/>; null for a plain colour.</summary>
    public ModelTextureRef? BaseColorTexture { get; set; }

    /// <summary>A copy that refers to the same texture index; a caller that moves it into another model rebases the index.</summary>
    public ModelMaterial Clone() => new()
    {
        Name = Name,
        BaseColor = BaseColor,
        Metallic = Metallic,
        Roughness = Roughness,
        BaseColorTexture = BaseColorTexture?.Clone(),
    };
}

/// <summary>How a material uses an image: which texture, and the UV transform of <c>KHR_texture_transform</c> (scale, then rotation, then offset).</summary>
public sealed class ModelTextureRef
{
    public ModelTextureRef(int texture)
    {
        Texture = texture;
    }

    /// <summary>Index into <see cref="Model3D.Textures"/>.</summary>
    public int Texture { get; set; }

    public Vector2 Offset { get; set; }

    /// <summary>Counter-clockwise rotation in radians.</summary>
    public float Rotation { get; set; }

    public Vector2 Scale { get; set; } = Vector2.One;

    public bool HasTransform => Offset != Vector2.Zero || Rotation != 0 || Scale != Vector2.One;

    public ModelTextureRef Clone() => new(Texture) { Offset = Offset, Rotation = Rotation, Scale = Scale };
}

/// <summary>How a texture repeats beyond the unit square.</summary>
public enum TextureWrap
{
    Repeat,
    ClampToEdge,
    MirroredRepeat,
}

/// <summary>An image with its sampling: PNG, JPEG or WebP bytes (other raster formats are converted to PNG on read).</summary>
public sealed class ModelTexture
{
    public string? Name { get; set; }

    /// <summary><c>image/png</c>, <c>image/jpeg</c> or <c>image/webp</c>.</summary>
    public string MediaType { get; set; } = "image/png";

    public byte[] Data { get; set; } = [];

    public TextureWrap WrapU { get; set; }

    public TextureWrap WrapV { get; set; }

    /// <summary>Nearest-neighbour instead of linear filtering.</summary>
    public bool Nearest { get; set; }
}

public sealed class ModelLayer
{
    public ModelLayer(string name)
    {
        Name = name;
    }

    public string Name { get; set; }

    public bool Visible { get; set; } = true;

    /// <summary>True for a layer the loader made up (one per named part, see <see cref="LoadOptions.SynthesizePartLayers"/>) rather than one the file defines.</summary>
    public bool Synthesized { get; init; }
}

/// <summary>A line of text in a drawing: <see cref="Height"/> in model units, <see cref="Rotation"/> in degrees counter-clockwise.</summary>
public sealed class ModelText
{
    public string Text { get; set; } = "";

    public Vector3 Position { get; set; }

    public float Height { get; set; } = 1;

    public float Rotation { get; set; }

    /// <summary>"start", "middle" or "end": which side of the text <see cref="Position"/> is on.</summary>
    public string Anchor { get; set; } = "start";

    /// <summary>Index into <see cref="Model3D.Layers"/>, or -1.</summary>
    public int Layer { get; set; } = -1;

    /// <summary>0xRRGGBB, or -1 for the default colour.</summary>
    public int Rgb { get; set; } = -1;
}

/// <summary>A filled area: one or more closed loops (even-odd rule, so inner loops are holes).</summary>
public sealed class ModelFill
{
    public List<Vector3[]> Loops { get; } = new();

    public int Layer { get; set; } = -1;

    public int Rgb { get; set; } = -1;
}

/// <summary>A raster image in a drawing: PNG, JPEG or WebP bytes filling the rectangle from <see cref="Position"/> (its lower left corner) by <see cref="Width"/> and <see cref="Height"/>, in model units.</summary>
public sealed class ModelImage
{
    public string? Name { get; set; }

    public string MediaType { get; set; } = "image/png";

    public byte[] Data { get; set; } = [];

    public Vector3 Position { get; set; }

    public float Width { get; set; } = 1;

    public float Height { get; set; } = 1;

    /// <summary>Index into <see cref="Model3D.Layers"/>, or -1.</summary>
    public int Layer { get; set; } = -1;
}

public sealed record PreviewImage(string MediaType, byte[] Data);

public readonly record struct BoundingBox(Vector3 Min, Vector3 Max)
{
    public static BoundingBox Empty { get; } = new(new Vector3(float.PositiveInfinity), new Vector3(float.NegativeInfinity));

    public bool IsEmpty => Min.X > Max.X;

    public Vector3 Size => IsEmpty ? Vector3.Zero : Max - Min;

    public Vector3 Center => IsEmpty ? Vector3.Zero : (Min + Max) / 2;

    public BoundingBox Include(Vector3 p) => new(Vector3.Min(Min, p), Vector3.Max(Max, p));

    public BoundingBox Union(BoundingBox other) => new(Vector3.Min(Min, other.Min), Vector3.Max(Max, other.Max));
}
