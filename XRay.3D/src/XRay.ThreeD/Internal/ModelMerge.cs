namespace XRay.ThreeD.Internal;

/// <summary>Moves what one model holds (nodes, meshes, materials, layers) into another.</summary>
internal static class ModelMerge
{
    /// <summary>
    /// Appends the roots of <paramref name="source"/> as children of <paramref name="parent"/> (or as roots of <paramref name="target"/> when
    /// null), rebasing material and layer indices. Warnings are carried over with <paramref name="prefix"/>. The source must not be used afterwards.
    /// </summary>
    public static void Append(Model3D target, SceneNode? parent, Model3D source, string? prefix = null)
    {
        AdoptResources(target, source);
        foreach (var root in source.Roots)
        {
            if (parent is null)
            {
                target.Roots.Add(root);
            }
            else
            {
                parent.Children.Add(root);
            }
        }

        foreach (var warning in source.Warnings)
        {
            target.Warnings.Add(prefix is null ? warning : prefix + warning);
        }
    }

    /// <summary>
    /// Adds the meshes, materials and layers of <paramref name="source"/> to <paramref name="target"/>, rebasing the indices the meshes and nodes
    /// hold. The nodes stay with the source; a caller that places them several times clones them (see <see cref="CloneTree"/>).
    /// </summary>
    public static void AdoptResources(Model3D target, Model3D source)
    {
        var materialOffset = target.Materials.Count;
        var layerOffset = target.Layers.Count;
        target.Materials.AddRange(source.Materials);
        target.Layers.AddRange(source.Layers);
        target.Metadata.LayerNames.AddRange(source.Layers.Select(l => l.Name));
        target.Meshes.AddRange(source.Meshes);
        foreach (var mesh in source.Meshes)
        {
            if (materialOffset > 0)
            {
                var groups = mesh.Groups.Select(g => g with { Material = g.Material >= 0 ? g.Material + materialOffset : g.Material }).ToList();
                mesh.Groups.Clear();
                mesh.Groups.AddRange(groups);
                if (mesh.EdgeMaterial >= 0)
                {
                    mesh.EdgeMaterial += materialOffset;
                }
            }
        }

        foreach (var node in source.Nodes())
        {
            if (node.Layer >= 0)
            {
                node.Layer += layerOffset;
            }
        }
    }

    /// <summary>A copy of a node and its descendants that shares the meshes (properties are copied, the layer is kept).</summary>
    public static SceneNode CloneTree(SceneNode node)
    {
        var copy = new SceneNode(node.Name) { Transform = node.Transform, Mesh = node.Mesh, Layer = node.Layer };
        foreach (var (key, value) in node.Properties)
        {
            copy.Properties[key] = value;
        }

        foreach (var child in node.Children)
        {
            copy.Children.Add(CloneTree(child));
        }

        return copy;
    }
}
