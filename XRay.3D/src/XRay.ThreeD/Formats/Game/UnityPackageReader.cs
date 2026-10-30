using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace XRay.ThreeD.Formats.Game;

using XRay.ThreeD.Internal;
using Doc = Internal.UnityYaml.Document;

/// <summary>
/// A <c>.unitypackage</c>: a gzipped tar of one folder per asset (<c>&lt;guid&gt;/asset</c>, <c>asset.meta</c>, <c>pathname</c>, optional
/// <c>preview.png</c>). The asset list, kinds and paths go to the metadata. Geometry comes from prefabs and scenes saved as text YAML (GameObject
/// and Transform hierarchy, MeshFilter, MeshRenderer, nested prefab instances with transform overrides, materials with colour and main texture),
/// from text-serialised Mesh assets, and from model files in the package (FBX, OBJ, COLLADA, glTF, 3DS, STL, PLY, X3D), which are loaded by the
/// readers of this library. Unity is left-handed with +Y up and metres: transforms and vertices are mirrored on X. A model file referenced by a
/// MeshFilter is placed whole (the sub-mesh the file id picks is not selected). Binary-serialised assets, compressed meshes, skinned meshes and
/// terrain are not read; the model carries a warning for each kind skipped.
/// </summary>
internal static class UnityPackageReader
{
    private const int MaxRoots = 300;
    private const int MaxNodes = 300_000;
    private static readonly Matrix4x4 MirrorX = Matrix4x4.CreateScale(-1, 1, 1);
    private static readonly string[] ModelExtensions = [".fbx", ".obj", ".dae", ".gltf", ".glb", ".3ds", ".stl", ".ply", ".x3d", ".wrl", ".off", ".3mf"];

    private sealed class Asset
    {
        public string Guid = "";
        public string Path = "";
        public byte[]? Data;
        public bool HasMeta;
        public long Preview;
        public string Ext => System.IO.Path.GetExtension(Path).ToLowerInvariant();
    }

    private sealed class Context
    {
        public Model3D Model = null!;
        public LoadOptions Options = null!;
        public Dictionary<string, Asset> ByGuid = new(StringComparer.Ordinal);
        public Dictionary<string, MeshData?> Meshes = new(StringComparer.Ordinal);
        public Dictionary<string, int> Materials = new(StringComparer.Ordinal);
        public Dictionary<string, int> Textures = new(StringComparer.Ordinal);
        public Dictionary<string, SceneNode?> Models = new(StringComparer.Ordinal);
        public Dictionary<string, List<Doc>> Yaml = new(StringComparer.Ordinal);
        public HashSet<string> Warned = new(StringComparer.Ordinal);
        public int Nodes;
        public int Depth;

        public void Warn(string message)
        {
            if (Warned.Add(message) && Model.Warnings.Count < 200)
            {
                Model.Warnings.Add(message);
            }
        }
    }

    public static bool Sniff(ReadOnlySpan<byte> d) => d.Length > 2 && d[0] == 0x1F && d[1] == 0x8B;

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var model = new Model3D();
        var meta = model.Metadata;
        meta.Format = ModelFormats.Unity;
        meta.DocumentKind = "scene";
        meta.Units = LengthUnit.Meter;
        meta.UpAxis = UpAxis.Y;
        var assets = ReadTar(data, options);
        var ctx = new Context { Model = model, Options = options };
        foreach (var a in assets)
        {
            ctx.ByGuid[a.Guid] = a;
        }

        Describe(model, assets);

        var roots = new List<SceneNode>();
        var scenes = assets.Where(a => a.Ext == ".unity" && a.Data is not null).ToList();
        var prefabs = assets.Where(a => a.Ext == ".prefab" && a.Data is not null).ToList();
        var skipped = 0;
        void Add(SceneNode? node)
        {
            if (node is null)
            {
                return;
            }

            if (roots.Count >= MaxRoots)
            {
                skipped++;
                return;
            }

            roots.Add(node);
        }

        foreach (var scene in scenes)
        {
            options.Cancellation.ThrowIfCancellationRequested();
            Add(Guarded(ctx, scene, () => BuildYamlAsset(ctx, scene)));
        }

        foreach (var prefab in prefabs)
        {
            options.Cancellation.ThrowIfCancellationRequested();
            Add(Guarded(ctx, prefab, () => BuildYamlAsset(ctx, prefab)));
        }

        if (roots.Count == 0)
        {
            foreach (var asset in assets)
            {
                options.Cancellation.ThrowIfCancellationRequested();
                if (asset.Data is null)
                {
                    continue;
                }

                var wrapper = asset.Ext == ".asset" && IsText(asset.Data) && StartsWithMesh(asset.Data)
                    ? Guarded(ctx, asset, () => MeshAssetNode(ctx, asset))
                    : ModelExtensions.Contains(asset.Ext) ? Guarded(ctx, asset, () => CloneModel(ctx, asset)) : null;
                Add(wrapper);
            }
        }

        if (skipped > 0)
        {
            ctx.Warn($"{skipped} more top-level objects were not loaded (limit {MaxRoots}).");
        }

        Spread(model, roots, scenes.Count == 0);
        meta.HasGeometry = model.Meshes.Count > 0;
        meta.Statistics = model.ComputeStatistics();
        meta.Bounds = model.ComputeBounds();
        if (!meta.HasGeometry)
        {
            meta.Notes.Add("The package holds no geometry this library reads (prefabs and meshes saved as text, or model files).");
        }

        return model;
    }

    private static SceneNode? Guarded(Context ctx, Asset asset, Func<SceneNode?> body)
    {
        try
        {
            return body();
        }
        catch (ModelFormatException ex)
        {
            ctx.Warn($"{asset.Path}: {ex.Message}");
            return null;
        }
    }

    // ---- the tar ----------------------------------------------------------------------------------------------------

    private static List<Asset> ReadTar(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        byte[] tar;
        try
        {
            using var input = new MemoryStream(data.ToArray(), writable: false);
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            var output = new MemoryStream();
            var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(81920);
            try
            {
                int read;
                while ((read = gzip.Read(buffer, 0, buffer.Length)) > 0)
                {
                    options.Cancellation.ThrowIfCancellationRequested();
                    if (output.Length + read > options.MaxInputBytes)
                    {
                        throw new ModelFormatException("The unpacked Unity package is larger than the byte limit.");
                    }

                    output.Write(buffer, 0, read);
                }
            }
            finally
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            }

            tar = output.ToArray();
        }
        catch (InvalidDataException ex)
        {
            throw new ModelFormatException("The Unity package is not a valid gzip stream: " + ex.Message, ex);
        }

        var byGuid = new Dictionary<string, Asset>(StringComparer.Ordinal);
        var p = 0;
        string? longName = null;
        long tick = 0;
        while (p + 512 <= tar.Length)
        {
            Cancel.Every(options.Cancellation, tick++);
            var header = tar.AsSpan(p, 512);
            if (header.IndexOfAnyExcept((byte)0) < 0)
            {
                break;
            }

            var name = Encoding.UTF8.GetString(header[..100]).TrimEnd('\0');
            var prefix = Encoding.UTF8.GetString(header.Slice(345, 155)).TrimEnd('\0');
            if (prefix.Length > 0)
            {
                name = prefix + "/" + name;
            }

            var sizeText = Encoding.ASCII.GetString(header.Slice(124, 12)).Trim('\0', ' ');
            long size;
            try
            {
                size = sizeText.Length == 0 ? 0 : Convert.ToInt64(sizeText, 8);
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException)
            {
                throw new ModelFormatException("A tar header in the Unity package has an invalid size.");
            }

            var type = (char)header[156];
            var bodyStart = p + 512;
            if (size < 0 || size > tar.Length - bodyStart)
            {
                throw new ModelFormatException("The Unity package's tar stream is truncated.");
            }

            var body = tar.AsSpan(bodyStart, (int)size);
            p = bodyStart + (int)((size + 511) / 512 * 512);
            if (type == 'L')
            {
                longName = Encoding.UTF8.GetString(body).TrimEnd('\0');
                continue;
            }

            if (longName is not null)
            {
                name = longName;
                longName = null;
            }

            if (type is not ('0' or '\0'))
            {
                continue;
            }

            var parts = name.TrimStart('.', '/').Split('/', 2);
            if (parts.Length != 2 || parts[0].Length == 0 || parts[0].Length > 64 || parts[1].Contains('/'))
            {
                continue;
            }

            if (!byGuid.TryGetValue(parts[0], out var asset))
            {
                if (byGuid.Count >= 200_000)
                {
                    throw new ModelFormatException("The Unity package has too many assets.");
                }

                byGuid[parts[0]] = asset = new Asset { Guid = parts[0] };
            }

            switch (parts[1])
            {
                case "pathname": asset.Path = Encoding.UTF8.GetString(body).Split('\n')[0].Trim('\r', '\0', ' '); break;
                case "asset": asset.Data = body.ToArray(); break;
                case "asset.meta": asset.HasMeta = true; break;
                case "preview.png": asset.Preview = size; break;
            }
        }

        return byGuid.Values.Where(a => a.Path.Length > 0).OrderBy(a => a.Path, StringComparer.Ordinal).ToList();
    }

    private static void Describe(Model3D model, List<Asset> assets)
    {
        var meta = model.Metadata;
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        string[]? common = null;
        foreach (var a in assets.Where(a => a.Data is not null))
        {
            var ext = a.Ext.Length > 0 ? a.Ext : "(none)";
            counts[ext] = counts.GetValueOrDefault(ext) + 1;
            if (meta.ComponentFiles.Count < 5000)
            {
                meta.ComponentFiles.Add(a.Path);
            }

            if (ModelExtensions.Contains(a.Ext) || a.Ext is ".prefab" or ".unity")
            {
                if (meta.PartNames.Count < 5000)
                {
                    meta.PartNames.Add(System.IO.Path.GetFileNameWithoutExtension(a.Path));
                }
            }

            var dir = a.Path.Split('/')[..^1];
            common = common is null ? dir : common.TakeWhile((s, i) => i < dir.Length && dir[i] == s).ToArray();
        }

        meta.Properties.Add(new ModelProperty("container", "tar.gz", "package"));
        meta.Properties.Add(new ModelProperty("assets", assets.Count(a => a.Data is not null).ToString(CultureInfo.InvariantCulture), "package"));
        foreach (var (ext, n) in counts)
        {
            meta.Properties.Add(new ModelProperty(ext, n.ToString(CultureInfo.InvariantCulture), "asset kinds"));
        }

        if (common is { Length: > 0 })
        {
            meta.Title = common[^1] == "Assets" ? null : common[^1];
        }
    }

    // ---- Unity YAML assets ------------------------------------------------------------------------------------------

    private static bool IsText(byte[] d) => d.Length > 5 && d[0] == '%' && d[1] == 'Y' && d[2] == 'A' && d[3] == 'M' && d[4] == 'L';

    private static bool StartsWithMesh(byte[] d)
    {
        var head = Encoding.ASCII.GetString(d, 0, Math.Min(d.Length, 200));
        return head.Contains("--- !u!43 ", StringComparison.Ordinal);
    }

    private static List<Doc>? Yaml(Context ctx, Asset asset)
    {
        if (asset.Data is null || !IsText(asset.Data))
        {
            ctx.Warn($"{asset.Path} is not saved as text YAML (binary serialisation is not read).");
            return null;
        }

        if (!ctx.Yaml.TryGetValue(asset.Guid, out var docs))
        {
            docs = UnityYaml.Parse(Encoding.UTF8.GetString(asset.Data), ctx.Options.Cancellation);
            ctx.Yaml[asset.Guid] = docs;
        }

        return docs;
    }

    private static SceneNode? BuildYamlAsset(Context ctx, Asset asset)
    {
        var docs = Yaml(ctx, asset);
        if (docs is null)
        {
            return null;
        }

        var root = new SceneNode(System.IO.Path.GetFileNameWithoutExtension(asset.Path));
        root.Properties["unity.asset"] = asset.Path;
        BuildInto(ctx, docs, root, out _);
        return root.Children.Count == 0 && root.Mesh is null ? null : root;
    }

    /// <summary>Builds the Transform hierarchy of <paramref name="docs"/> under <paramref name="parent"/>; returns the file id of the first root transform.</summary>
    private static void BuildInto(Context ctx, List<Doc> docs, SceneNode parent, out long rootTransformId)
    {
        rootTransformId = 0;
        var byId = new Dictionary<long, Doc>();
        foreach (var d in docs)
        {
            byId[d.FileId] = d;
        }

        var nodes = new Dictionary<long, SceneNode>();
        var transforms = docs.Where(d => d.ClassId is 4 or 224 && !d.Stripped).ToList();
        foreach (var t in transforms)
        {
            Cancel.Every(ctx.Options.Cancellation, nodes.Count);
            if (++ctx.Nodes > MaxNodes)
            {
                throw new ModelFormatException("The Unity scene has too many objects.");
            }

            var goId = UnityYaml.Ref(t.Body, "m_GameObject").FileId;
            var name = byId.TryGetValue(goId, out var go) ? UnityYaml.Str(go.Body, "m_Name") : null;
            var node = new SceneNode(string.IsNullOrEmpty(name) ? null : name) { Transform = LocalMatrix(t.Body) };
            if (go is not null)
            {
                var tag = UnityYaml.Str(go.Body, "m_TagString");
                if (!string.IsNullOrEmpty(tag) && tag != "Untagged")
                {
                    node.Properties["unity.tag"] = tag;
                }

                if (UnityYaml.Str(go.Body, "m_IsActive") == "0")
                {
                    node.Properties["unity.inactive"] = "true";
                }
            }

            nodes[t.FileId] = node;
        }

        // Components: mesh filters and renderers by game object.
        var filters = new Dictionary<long, Doc>();
        var renderers = new Dictionary<long, Doc>();
        foreach (var d in docs)
        {
            if (d.ClassId == 33)
            {
                filters[UnityYaml.Ref(d.Body, "m_GameObject").FileId] = d;
            }
            else if (d.ClassId == 23)
            {
                renderers[UnityYaml.Ref(d.Body, "m_GameObject").FileId] = d;
            }
        }

        foreach (var t in transforms)
        {
            var node = nodes[t.FileId];
            var goId = UnityYaml.Ref(t.Body, "m_GameObject").FileId;
            if (filters.TryGetValue(goId, out var filter))
            {
                var (fileId, guid) = UnityYaml.Ref(filter.Body, "m_Mesh");
                if (guid is not null && ctx.ByGuid.TryGetValue(guid, out var meshAsset))
                {
                    var materials = renderers.TryGetValue(goId, out var renderer) ? MaterialRefs(ctx, renderer) : [];
                    AttachMesh(ctx, node, meshAsset, fileId, materials);
                }
            }

            var father = UnityYaml.Ref(t.Body, "m_Father").FileId;
            if (father != 0 && nodes.TryGetValue(father, out var parentNode))
            {
                parentNode.Children.Add(node);
            }
            else if (father == 0 || !byId.ContainsKey(father))
            {
                parent.Children.Add(node);
                if (rootTransformId == 0)
                {
                    rootTransformId = t.FileId;
                }
            }
        }

        // Instances of other prefabs or models.
        foreach (var inst in docs.Where(d => d.ClassId == 1001 && d.Type == "PrefabInstance"))
        {
            Cancel.Every(ctx.Options.Cancellation, nodes.Count);
            var node = Instance(ctx, inst, byId, nodes);
            if (node is not null)
            {
                parent.Children.Add(node);
            }
        }
    }

    private static SceneNode? Instance(Context ctx, Doc inst, Dictionary<long, Doc> byId, Dictionary<long, SceneNode> nodes)
    {
        var (_, guid) = UnityYaml.Ref(inst.Body, "m_SourcePrefab");
        if (guid is null || !ctx.ByGuid.TryGetValue(guid, out var source) || source.Data is null)
        {
            ctx.Warn("A prefab instance refers to an asset that is not in the package.");
            return null;
        }

        if (++ctx.Nodes > MaxNodes || ++ctx.Depth > 16)
        {
            ctx.Depth--;
            throw new ModelFormatException("Prefab instances are nested too deeply or there are too many.");
        }

        try
        {
            var holder = new SceneNode(System.IO.Path.GetFileNameWithoutExtension(source.Path));
            long rootId = 0;
            if (source.Ext == ".prefab")
            {
                var docs = Yaml(ctx, source);
                if (docs is null)
                {
                    return null;
                }

                BuildInto(ctx, docs, holder, out rootId);
            }
            else if (ModelExtensions.Contains(source.Ext))
            {
                var m = CloneModel(ctx, source);
                if (m is null)
                {
                    return null;
                }

                holder = m;
            }
            else
            {
                return null;
            }

            // Overrides of the root transform and the name.
            var mods = UnityYaml.Map(inst.Body, "m_Modification");
            var list = mods is null ? null : UnityYaml.List(mods, "m_Modifications");
            if (list is not null)
            {
                var transformTargets = new HashSet<long>();
                foreach (var item in list.OfType<Dictionary<string, object>>())
                {
                    var path = UnityYaml.Str(item, "propertyPath") ?? "";
                    if (path.StartsWith("m_LocalPosition.", StringComparison.Ordinal) || path.StartsWith("m_LocalRotation.", StringComparison.Ordinal) || path.StartsWith("m_LocalScale.", StringComparison.Ordinal))
                    {
                        transformTargets.Add(UnityYaml.Ref(item, "target").FileId);
                    }
                }

                var rootTarget = source.Ext == ".prefab" ? rootId : transformTargets.Count == 1 ? transformTargets.First() : 0;
                var local = Matrix4x4.Identity;
                var position = Vector3.Zero;
                var rotation = Quaternion.Identity;
                var scale = Vector3.One;
                var any = false;
                string? name = null;
                foreach (var item in list.OfType<Dictionary<string, object>>())
                {
                    var path = UnityYaml.Str(item, "propertyPath") ?? "";
                    var value = UnityYaml.Num(item, "value");
                    var target = UnityYaml.Ref(item, "target").FileId;
                    if (path == "m_Name" && name is null)
                    {
                        name = UnityYaml.Str(item, "value");
                        continue;
                    }

                    if (rootTarget == 0 || target != rootTarget)
                    {
                        continue;
                    }

                    any = true;
                    switch (path)
                    {
                        case "m_LocalPosition.x": position.X = (float)value; break;
                        case "m_LocalPosition.y": position.Y = (float)value; break;
                        case "m_LocalPosition.z": position.Z = (float)value; break;
                        case "m_LocalRotation.x": rotation.X = (float)value; break;
                        case "m_LocalRotation.y": rotation.Y = (float)value; break;
                        case "m_LocalRotation.z": rotation.Z = (float)value; break;
                        case "m_LocalRotation.w": rotation.W = (float)value; break;
                        case "m_LocalScale.x": scale.X = (float)value; break;
                        case "m_LocalScale.y": scale.Y = (float)value; break;
                        case "m_LocalScale.z": scale.Z = (float)value; break;
                    }
                }

                if (any)
                {
                    rotation = rotation.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(rotation);
                    local = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(position);
                    holder.Transform = holder.Transform * (MirrorX * local * MirrorX);
                }

                if (name is not null)
                {
                    holder.Name = name;
                }
            }

            return holder;
        }
        finally
        {
            ctx.Depth--;
        }
    }

    private static Matrix4x4 LocalMatrix(Dictionary<string, object> body)
    {
        var p = UnityYaml.Map(body, "m_LocalPosition");
        var r = UnityYaml.Map(body, "m_LocalRotation");
        var s = UnityYaml.Map(body, "m_LocalScale");
        var q = new Quaternion((float)UnityYaml.Num(r, "x"), (float)UnityYaml.Num(r, "y"), (float)UnityYaml.Num(r, "z"), (float)UnityYaml.Num(r, "w", 1));
        q = q.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(q);
        var local = Matrix4x4.CreateScale((float)UnityYaml.Num(s, "x", 1), (float)UnityYaml.Num(s, "y", 1), (float)UnityYaml.Num(s, "z", 1))
            * Matrix4x4.CreateFromQuaternion(q)
            * Matrix4x4.CreateTranslation((float)UnityYaml.Num(p, "x"), (float)UnityYaml.Num(p, "y"), (float)UnityYaml.Num(p, "z"));
        return MirrorX * local * MirrorX;
    }

    private static List<string?> MaterialRefs(Context ctx, Doc renderer)
    {
        var result = new List<string?>();
        if (UnityYaml.List(renderer.Body, "m_Materials") is { } list)
        {
            foreach (var item in list.OfType<Dictionary<string, object>>())
            {
                result.Add(UnityYaml.Str(item, "guid"));
                if (result.Count >= 256)
                {
                    break;
                }
            }
        }

        return result;
    }

    private static void AttachMesh(Context ctx, SceneNode node, Asset meshAsset, long fileId, List<string?> materials)
    {
        if (meshAsset.Data is null)
        {
            return;
        }

        try
        {
            if (meshAsset.Ext == ".asset" || meshAsset.Ext == ".mesh")
            {
                if (!ctx.Meshes.TryGetValue(meshAsset.Guid, out var mesh))
                {
                    mesh = LoadMeshAsset(ctx, meshAsset);
                    ctx.Meshes[meshAsset.Guid] = mesh;
                    if (mesh is not null)
                    {
                        ctx.Model.Meshes.Add(mesh);
                    }
                }

                if (mesh is null)
                {
                    return;
                }

                // Groups hold material indices: a shared mesh keeps one material assignment (the first renderer's).
                if (mesh.Groups.Count > 0 && mesh.Groups.All(g => g.Material == -2))
                {
                    var groups = mesh.Groups.ToArray();
                    mesh.Groups.Clear();
                    for (var i = 0; i < groups.Length; i++)
                    {
                        var guid = materials.Count == 0 ? null : materials[Math.Min(i, materials.Count - 1)];
                        mesh.Groups.Add(groups[i] with { Material = guid is null ? -1 : MaterialFor(ctx, guid) });
                    }
                }

                node.Mesh = mesh;
            }
            else if (ModelExtensions.Contains(meshAsset.Ext))
            {
                if (CloneModel(ctx, meshAsset) is { } placed)
                {
                    node.Children.Add(placed);
                }
            }
        }
        catch (ModelFormatException ex)
        {
            ctx.Warn($"{meshAsset.Path}: {ex.Message}");
        }
    }

    private static SceneNode? MeshAssetNode(Context ctx, Asset asset)
    {
        var node = new SceneNode(System.IO.Path.GetFileNameWithoutExtension(asset.Path));
        AttachMesh(ctx, node, asset, 0, []);
        return node.Mesh is null ? null : node;
    }

    // ---- materials ------------------------------------------------------------------------------------------------------

    private static int MaterialFor(Context ctx, string guid)
    {
        if (ctx.Materials.TryGetValue(guid, out var cached))
        {
            return cached;
        }

        var index = -1;
        if (ctx.ByGuid.TryGetValue(guid, out var asset) && asset.Ext == ".mat" && asset.Data is not null && IsText(asset.Data))
        {
            var docs = Yaml(ctx, asset);
            var mat = docs?.FirstOrDefault(d => d.ClassId == 21);
            if (mat is not null)
            {
                var material = new ModelMaterial { Name = UnityYaml.Str(mat.Body, "m_Name") };
                var saved = UnityYaml.Map(mat.Body, "m_SavedProperties");
                if (saved is not null)
                {
                    var color = FindColor(saved, "_BaseColor") ?? FindColor(saved, "_Color");
                    if (color is { } c)
                    {
                        material.BaseColor = c;
                    }

                    var floats = UnityYaml.List(saved, "m_Floats");
                    material.Metallic = (float)Math.Clamp(FindFloat(floats, "_Metallic") ?? 0, 0, 1);
                    if ((FindFloat(floats, "_Glossiness") ?? FindFloat(floats, "_Smoothness")) is { } gloss)
                    {
                        material.Roughness = (float)Math.Clamp(1 - gloss, 0, 1);
                    }

                    var tex = FindTexture(saved, "_MainTex") ?? FindTexture(saved, "_BaseMap");
                    if (tex is not null && ctx.ByGuid.TryGetValue(tex, out var texAsset) && texAsset.Data is not null)
                    {
                        if (!ctx.Textures.TryGetValue(tex, out var slot))
                        {
                            slot = -1;
                            if (TextureImages.Create(texAsset.Path, texAsset.Data, out var problem, ctx.Options.Cancellation) is { } texture)
                            {
                                slot = ctx.Model.Textures.Count;
                                ctx.Model.Textures.Add(texture);
                            }
                            else if (problem is not null)
                            {
                                ctx.Warn(problem);
                            }

                            ctx.Textures[tex] = slot;
                        }

                        if (slot >= 0)
                        {
                            material.BaseColorTexture = new ModelTextureRef(slot);
                        }
                    }
                }

                index = ctx.Model.Materials.Count;
                ctx.Model.Materials.Add(material);
                ctx.Model.Metadata.MaterialNames.Add(material.Name ?? "Material");
            }
        }

        ctx.Materials[guid] = index;
        return index;
    }

    private static Vector4? FindColor(Dictionary<string, object> saved, string name)
    {
        if (UnityYaml.List(saved, "m_Colors") is { } list)
        {
            foreach (var item in list.OfType<Dictionary<string, object>>())
            {
                if (UnityYaml.Map(item, name) is { } c)
                {
                    return new Vector4((float)UnityYaml.Num(c, "r", 1), (float)UnityYaml.Num(c, "g", 1), (float)UnityYaml.Num(c, "b", 1), (float)UnityYaml.Num(c, "a", 1));
                }
            }
        }

        return null;
    }

    private static double? FindFloat(List<object>? list, string name)
    {
        if (list is null)
        {
            return null;
        }

        foreach (var item in list.OfType<Dictionary<string, object>>())
        {
            if (item.ContainsKey(name))
            {
                return UnityYaml.Num(item, name);
            }
        }

        return null;
    }

    private static string? FindTexture(Dictionary<string, object> saved, string name)
    {
        if (UnityYaml.List(saved, "m_TexEnvs") is { } list)
        {
            foreach (var item in list.OfType<Dictionary<string, object>>())
            {
                if (UnityYaml.Map(item, name) is { } env)
                {
                    return UnityYaml.Ref(env, "m_Texture").Guid;
                }
            }
        }

        return null;
    }

    // ---- model files ------------------------------------------------------------------------------------------------

    /// <summary>The model file loaded once and returned as a fresh copy of its tree, scaled to metres, ready to hang under any node.</summary>
    private static SceneNode? CloneModel(Context ctx, Asset asset)
    {
        if (!ctx.Models.TryGetValue(asset.Guid, out var template))
        {
            template = null;
            try
            {
                var inner = ctx.Options.Clone();
                inner.ArchiveDepth = ctx.Options.ArchiveDepth + 1;
                var directory = asset.Path.Contains('/') ? asset.Path[..asset.Path.LastIndexOf('/')] : "";
                inner.ResolveCompanion = name => FindByName(ctx, directory, name)?.Data;
                if (ctx.Options.ArchiveDepth >= 3)
                {
                    throw new ModelFormatException("Packages are nested too deeply.");
                }

                var loaded = ModelLoader.Load(asset.Data!, System.IO.Path.GetFileName(asset.Path), inner);
                var wrapper = new SceneNode(System.IO.Path.GetFileNameWithoutExtension(asset.Path));
                if (loaded.Metadata.Units is { } unit && unit != LengthUnit.Meter)
                {
                    var s = (float)unit.ToMeters();
                    wrapper.Transform = Matrix4x4.CreateScale(s);
                }

                ModelMerge.Append(ctx.Model, wrapper, loaded, asset.Path + ": ");
                template = wrapper;
            }
            catch (ModelFormatException ex)
            {
                ctx.Warn($"{asset.Path}: {ex.Message}");
            }

            ctx.Models[asset.Guid] = template;
            return template is null ? null : ModelMerge.CloneTree(template);
        }

        return template is null ? null : ModelMerge.CloneTree(template);
    }

    private static Asset? FindByName(Context ctx, string directory, string name)
    {
        var clean = name.Replace('\\', '/').Trim();
        var file = clean[(clean.LastIndexOf('/') + 1)..];
        Asset? best = null;
        foreach (var a in ctx.ByGuid.Values)
        {
            if (a.Data is null || !a.Path.EndsWith("/" + file, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (a.Path.StartsWith(directory + "/", StringComparison.Ordinal))
            {
                return a;
            }

            best ??= a;
        }

        return best;
    }

    // ---- Mesh assets ------------------------------------------------------------------------------------------------

    private static MeshData? LoadMeshAsset(Context ctx, Asset asset)
    {
        var docs = Yaml(ctx, asset);
        var doc = docs?.FirstOrDefault(d => d.ClassId == 43);
        if (doc is null)
        {
            return null;
        }

        var b = doc.Body;
        if (UnityYaml.Long(b, "m_MeshCompression") != 0)
        {
            ctx.Warn($"{asset.Path}: compressed meshes are not read.");
            return null;
        }

        var vd = UnityYaml.Map(b, "m_VertexData");
        var channels = vd is null ? null : UnityYaml.List(vd, "m_Channels");
        var subMeshes = UnityYaml.List(b, "m_SubMeshes");
        if (vd is null || channels is null || subMeshes is null || UnityYaml.Str(vd, "_typelessdata") is not { } typeless || UnityYaml.Str(b, "m_IndexBuffer") is not { } indexHex)
        {
            ctx.Warn($"{asset.Path}: only meshes with a vertex-data block are read (Unity 5 and later).");
            return null;
        }

        var count = UnityYaml.Long(vd, "m_VertexCount");
        if (count <= 0 || count > 50_000_000)
        {
            return null;
        }

        var vertexBytes = Hex(typeless);
        var indexBytes = Hex(indexHex);
        var chan = channels.OfType<Dictionary<string, object>>().Take(16).Select(c => (Stream: (int)UnityYaml.Long(c, "stream"), Offset: (int)UnityYaml.Long(c, "offset"), Format: (int)UnityYaml.Long(c, "format"), Dim: (int)UnityYaml.Long(c, "dimension") & 15)).ToArray();

        // Stream layout: stream k starts after the (16-byte aligned) streams before it; its stride is the end of its furthest channel.
        var streams = new int[4];
        foreach (var c in chan)
        {
            if (c.Dim > 0 && (uint)c.Stream < 4)
            {
                var size = FormatSize(c.Format);
                if (size == 0)
                {
                    continue;
                }

                streams[c.Stream] = Math.Max(streams[c.Stream], c.Offset + c.Dim * size);
            }
        }

        var starts = new long[4];
        long cursor = 0;
        for (var s = 0; s < 4; s++)
        {
            starts[s] = cursor;
            cursor += (streams[s] * count + 15) / 16 * 16;
        }

        if (cursor > vertexBytes.Length + 15)
        {
            throw new ModelFormatException("The mesh's vertex buffer is shorter than its channels declare.");
        }

        float[] Channel(int index, int want)
        {
            if (index >= chan.Length || chan[index].Dim < want || chan[index].Stream >= 4)
            {
                return [];
            }

            var c = chan[index];
            var size = FormatSize(c.Format);
            if (size == 0)
            {
                return [];
            }

            var result = new float[count * want];
            for (var v = 0; v < count; v++)
            {
                Cancel.Every(ctx.Options.Cancellation, v);
                var at = starts[c.Stream] + v * streams[c.Stream] + c.Offset;
                if (at + c.Dim * size > vertexBytes.Length)
                {
                    throw new ModelFormatException("The mesh's vertex buffer is truncated.");
                }

                for (var k = 0; k < want; k++)
                {
                    result[v * want + k] = ReadComponent(vertexBytes, (int)at + k * size, c.Format);
                }
            }

            return result;
        }

        var mesh = new MeshData { Name = UnityYaml.Str(b, "m_Name") };
        mesh.Positions = Channel(0, 3);
        if (mesh.Positions.Length == 0)
        {
            return null;
        }

        for (var i = 0; i < mesh.Positions.Length; i += 3)
        {
            mesh.Positions[i] = -mesh.Positions[i];
        }

        mesh.Normals = Channel(1, 3);
        for (var i = 0; i < mesh.Normals.Length; i += 3)
        {
            mesh.Normals[i] = -mesh.Normals[i];
        }

        mesh.Colors = ChannelColor(chan, 3, Channel);
        mesh.TexCoords = Channel(4, 2);
        var wide = UnityYaml.Long(b, "m_IndexFormat") == 1;
        var indices = new List<uint>();
        var tick = 0L;
        foreach (var sub in subMeshes.OfType<Dictionary<string, object>>().Take(1024))
        {
            if (UnityYaml.Long(sub, "topology") != 0)
            {
                ctx.Warn($"{asset.Path}: a sub-mesh that is not a triangle list was skipped.");
                continue;
            }

            var first = UnityYaml.Long(sub, "firstByte");
            var n = UnityYaml.Long(sub, "indexCount");
            var baseVertex = UnityYaml.Long(sub, "baseVertex");
            var width = wide ? 4 : 2;
            if (first < 0 || n < 0 || n % 3 != 0 || first + n * width > indexBytes.Length)
            {
                throw new ModelFormatException("A Unity sub-mesh's index range lies outside its buffer.");
            }

            var start = indices.Count;
            for (long i = 0; i < n; i += 3)
            {
                Cancel.Every(ctx.Options.Cancellation, tick++);
                var a = ReadIndex(indexBytes, first + i * width, wide) + baseVertex;
                var bb = ReadIndex(indexBytes, first + (i + 1) * width, wide) + baseVertex;
                var c = ReadIndex(indexBytes, first + (i + 2) * width, wide) + baseVertex;
                if (a < 0 || bb < 0 || c < 0 || a >= count || bb >= count || c >= count)
                {
                    throw new ModelFormatException("A Unity triangle refers to a vertex that does not exist.");
                }

                // Mirrored on X: reverse the winding.
                indices.Add((uint)a);
                indices.Add((uint)c);
                indices.Add((uint)bb);
            }

            mesh.Groups.Add(new MeshGroup(start, indices.Count - start, -2));
        }

        if (indices.Count == 0)
        {
            return null;
        }

        mesh.Indices = indices.ToArray();
        return mesh;
    }

    private static float[] ChannelColor((int Stream, int Offset, int Format, int Dim)[] chan, int index, Func<int, int, float[]> read)
    {
        if (index >= chan.Length || chan[index].Dim < 4)
        {
            return [];
        }

        return read(index, 4);
    }

    private static int FormatSize(int format) => format switch { 0 => 4, 1 => 2, 2 or 3 => 1, 4 or 5 => 2, _ => 0 };

    private static float ReadComponent(byte[] d, int at, int format) => format switch
    {
        0 => BitConverter.ToSingle(d, at),
        1 => (float)BitConverter.ToHalf(d, at),
        2 => d[at] / 255f,
        3 => Math.Max((sbyte)d[at] / 127f, -1f),
        4 => BitConverter.ToUInt16(d, at) / 65535f,
        _ => Math.Max(BitConverter.ToInt16(d, at) / 32767f, -1f),
    };

    private static long ReadIndex(byte[] d, long at, bool wide) => wide ? BitConverter.ToUInt32(d, (int)at) : BitConverter.ToUInt16(d, (int)at);

    private static byte[] Hex(string text)
    {
        try
        {
            return Convert.FromHexString(text);
        }
        catch (FormatException)
        {
            throw new ModelFormatException("A Unity mesh buffer is not valid hexadecimal.");
        }
    }

    // ---- layout ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Top-level prefabs and meshes each sit at the origin of their own file; shown together they would overlap, so each is placed in a row. Scenes
    /// keep their own coordinates (<paramref name="spreadAll"/> false when there are scenes).
    /// </summary>
    private static void Spread(Model3D model, List<SceneNode> roots, bool spreadAll)
    {
        if (roots.Count == 0)
        {
            return;
        }

        if (!spreadAll || roots.Count == 1)
        {
            model.Roots.AddRange(roots);
            return;
        }

        var x = 0f;
        foreach (var root in roots)
        {
            var box = Bounds(root, Matrix4x4.Identity);
            if (box.IsEmpty)
            {
                model.Roots.Add(root);
                continue;
            }

            var wrapper = new SceneNode(root.Name)
            {
                Transform = Matrix4x4.CreateTranslation(x - box.Min.X, -box.Min.Y, -box.Center.Z),
            };
            wrapper.Children.Add(root);
            foreach (var key in root.Properties)
            {
                wrapper.Properties[key.Key] = key.Value;
            }

            model.Roots.Add(wrapper);
            x += box.Size.X + Math.Max(box.Size.X * 0.2f, 0.1f);
        }
    }

    private static BoundingBox Bounds(SceneNode node, Matrix4x4 parent)
    {
        var world = node.Transform * parent;
        var box = BoundingBox.Empty;
        if (node.Mesh is { } mesh && mesh.Positions.Length >= 3)
        {
            box = mesh.ComputeBounds(world);
        }

        foreach (var child in node.Children)
        {
            box = box.Union(Bounds(child, world));
        }

        return box;
    }
}
