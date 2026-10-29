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

    public List<ModelLayer> Layers { get; } = new();

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
    public ModelStatistics ComputeStatistics()
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

        return new ModelStatistics(nodes, Meshes.Count, vertices, triangles, Materials.Count, Meshes.Sum(m => (long)m.VertexCount), Meshes.Sum(m => (long)m.TriangleCount));
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

    /// <summary>r,g,b,a per vertex (0..1, sRGB), or empty when the file has none.</summary>
    public float[] Colors { get; set; } = [];

    /// <summary>Three indices per triangle. Empty for a point cloud (the vertices are drawn as points).</summary>
    public uint[] Indices { get; set; } = [];

    /// <summary>
    /// The source faces (a CAD face is a patch of triangles), as index ranges, in the order of the source's face list.
    /// Empty when the format has no faces. A renderer maps a picked triangle to its face by searching these ranges.
    /// </summary>
    public MeshFace[] Faces { get; set; } = [];

    /// <summary>Feature edges as index pairs into <see cref="Positions"/>, when precomputed (see <see cref="MeshEdges"/>); empty otherwise.</summary>
    public uint[] EdgeIndices { get; set; } = [];

    /// <summary>Contiguous index ranges that share a material. Empty means one group over all indices.</summary>
    public List<MeshGroup> Groups { get; } = new();

    public int VertexCount => Positions.Length / 3;

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
}

public sealed class ModelLayer
{
    public ModelLayer(string name)
    {
        Name = name;
    }

    public string Name { get; set; }

    public bool Visible { get; set; } = true;
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
