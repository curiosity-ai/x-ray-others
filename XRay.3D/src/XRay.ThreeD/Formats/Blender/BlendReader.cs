using System.Globalization;
using System.Numerics;

namespace XRay.ThreeD.Formats.Blender;

using XRay.ThreeD.Internal;

/// <summary>
/// Blender <c>.blend</c> files, from the file's own SDNA (see <see cref="BlendFile"/>): the first scene's collection tree with its objects, parent
/// hierarchy and transforms (location, rotation in every mode, scale, parent inverse, collection instances), and each mesh object's geometry with
/// UVs, per-material groups, flat and smooth shading and Principled BSDF colour, metallic and roughness. Both mesh layouts are read: the legacy one
/// (vertex, loop and polygon arrays, Blender 2.8 to 3.4) and the attribute one (named position, corner and face-offset layers, 3.5 and later).
/// Not applied: modifiers, geometry nodes, shape keys, armatures and animation (the mesh is the base mesh in rest pose), constraints, textures.
/// Blender is Z up, and its unit is the metre times the scene's unit scale.
/// </summary>
internal static class BlendReader
{
    private const int MaxDepth = 64;

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var token = options.Cancellation;
        var limit = Math.Min(0x7FFFFFC7L, Math.Max(options.MaxInputBytes, 1L << 20) * 8);
        var file = BlendFile.Open(data, limit, token);
        var ctx = new Context(file, options);
        ctx.Run();
        return ctx.Model;
    }

    private sealed class Layer
    {
        public string Name = "";
        public ulong Data;
        public string? StructName;
        public long Type;

        /// <summary>Blender 5 attributes: one value for every element, stored once.</summary>
        public bool Single;
    }

    private sealed class Context
    {
        private readonly BlendFile _f;
        private readonly LoadOptions _o;
        private Dictionary<ulong, SceneNode> _objects = new();
        private readonly Dictionary<ulong, MeshData?> _meshes = new();
        private readonly Dictionary<ulong, int> _materials = new();
        private readonly Dictionary<ulong, int> _layers = new();
        private readonly Dictionary<ulong, SceneNode?> _collections = new();
        private readonly HashSet<ulong> _building = new();
        private readonly HashSet<ulong> _excluded = new();
        private readonly Dictionary<string, int> _skipped = new(StringComparer.Ordinal);
        private long _triangles;
        private long _tick;
        private int _hidden;
        private bool _modifiers;
        private bool _objectMaterials;

        public Context(BlendFile file, LoadOptions options)
        {
            _f = file;
            _o = options;
        }

        public Model3D Model { get; } = new();

        /// <summary>The size of one Blender unit in metres (the scene's unit scale).</summary>
        private double _metersPerUnit = 1;

        private void Tick() => Cancel.Every(_o.Cancellation, _tick++);

        private void Skip(string what)
        {
            _skipped[what] = _skipped.GetValueOrDefault(what) + 1;
        }

        public void Run()
        {
            var md = Model.Metadata;
            md.Format = ModelFormats.Blender;
            md.DocumentKind = "scene";
            md.UpAxis = UpAxis.Z;
            md.Units = LengthUnit.Meter;
            md.FormatVersion = (_f.Version / 100).ToString(CultureInfo.InvariantCulture) + "." + (_f.Version % 100).ToString(CultureInfo.InvariantCulture);
            md.Application = "Blender " + (_f.Version / 100).ToString(CultureInfo.InvariantCulture) + "." + (_f.Version % 100).ToString(CultureInfo.InvariantCulture);
            if (_f.Compression != "none")
            {
                md.Notes.Add("The file is " + _f.Compression + "-compressed.");
            }

            var sceneBlock = _f.Blocks.Where(b => b.Code == BlendFile.Code("SC")).Select(b => (BlendFile.Block?)b).FirstOrDefault();
            var scene = sceneBlock is { } sb ? _f.At("Scene", sb.Pos) : default;
            var roots = new List<SceneNode>();
            if (!scene.IsNull)
            {
                md.Title = scene.IdName();
                ReadUnits(scene);
                ReadExclusions(scene);
                var master = _f.Deref(scene.Ptr("master_collection"), "Collection");
                if (!master.IsNull)
                {
                    var holder = new SceneNode("scene");
                    BuildCollection(master, holder, master: true, 0);
                    roots.AddRange(holder.Children);
                }
            }

            if (roots.Count == 0)
            {
                // No scene (an asset file) or an empty one: every object block, parents nested.
                var holder = new SceneNode("objects");
                foreach (var block in _f.Blocks)
                {
                    Tick();
                    if (block.Code == BlendFile.Code("OB"))
                    {
                        ObjectNode(block.Old, holder, -1, 0);
                    }
                }

                roots.AddRange(holder.Children);
            }

            foreach (var r in roots)
            {
                Prune(r);
            }

            foreach (var r in roots)
            {
                if (HasMesh(r))
                {
                    Model.Roots.Add(r);
                }
            }

            foreach (var n in Model.Nodes())
            {
                if (n.Mesh is not null && !string.IsNullOrEmpty(n.Name) && !md.PartNames.Contains(n.Name!))
                {
                    md.PartNames.Add(n.Name!);
                }
            }

            foreach (var m in Model.Materials)
            {
                if (!string.IsNullOrEmpty(m.Name))
                {
                    md.MaterialNames.Add(m.Name!);
                }
            }

            foreach (var l in Model.Layers)
            {
                md.LayerNames.Add(l.Name);
            }

            if (_hidden > 0)
            {
                Model.Warnings.Add(_hidden.ToString(CultureInfo.InvariantCulture) + " object(s) hidden in the viewport were left out.");
            }

            if (_modifiers)
            {
                Model.Warnings.Add("Modifiers and geometry nodes are not applied: meshes are the base meshes.");
            }

            if (_objectMaterials)
            {
                Model.Warnings.Add("Materials linked to objects rather than meshes use the mesh's slots.");
            }

            foreach (var (what, count) in _skipped.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                md.Notes.Add(count.ToString(CultureInfo.InvariantCulture) + " " + what + " not exported.");
            }

            md.HasGeometry = Model.Meshes.Count > 0;
            UnitScaling.Apply(Model, _metersPerUnit);
            md.Statistics = Model.ComputeStatistics();
            md.Bounds = Model.ComputeBounds();
        }

        private void ReadUnits(Rec scene)
        {
            var unit = scene.Sub("unit");
            if (unit.IsNull)
            {
                return;
            }

            // One Blender unit is scale_length metres (the length unit shown in the interface, millimetres or inches, only changes how numbers are
            // displayed). With the unit system set to None the scale is not used and a unit is a metre.
            var scale = unit.Float("scale_length", 0, 1f);
            if (!(scale > 0) || !float.IsFinite(scale) || unit.Int("system", 1) == 0)
            {
                return;
            }

            _metersPerUnit = scale;
        }

        private void ReadExclusions(Rec scene)
        {
            foreach (var viewLayer in scene.List("view_layers", "ViewLayer").Take(1))
            {
                var stack = new Stack<(Rec Rec, int Depth)>();
                foreach (var lc in viewLayer.List("layer_collections", "LayerCollection"))
                {
                    stack.Push((lc, 0));
                }

                while (stack.Count > 0)
                {
                    Tick();
                    var (lc, depth) = stack.Pop();
                    if ((lc.Int("flag") & (16 | 128)) != 0)
                    {
                        _excluded.Add(lc.Ptr("collection"));
                    }

                    if (depth < MaxDepth)
                    {
                        foreach (var child in lc.List("layer_collections", "LayerCollection"))
                        {
                            stack.Push((child, depth + 1));
                        }
                    }
                }
            }
        }

        private SceneNode? BuildCollection(Rec collection, SceneNode parent, bool master, int depth)
        {
            Tick();
            var ptr = (ulong)collection.Pos;
            if (depth > MaxDepth || !_building.Add(ptr))
            {
                return null;
            }

            try
            {
                var node = master ? parent : new SceneNode(collection.IdName());
                var layer = -1;
                if (!master && !_layers.TryGetValue(ptr, out layer))
                {
                    layer = Model.Layers.Count;
                    Model.Layers.Add(new ModelLayer(collection.IdName()));
                    _layers[ptr] = layer;
                }

                foreach (var co in collection.List("gobject", "CollectionObject"))
                {
                    Tick();
                    ObjectNode(co.Ptr("ob"), node, layer, 0);
                }

                foreach (var child in collection.List("children", "CollectionChild"))
                {
                    var cptr = child.Ptr("collection");
                    var c = _f.Deref(cptr, "Collection");
                    if (c.IsNull || _excluded.Contains(cptr) || (c.Int("flag") & 1) != 0)
                    {
                        continue;
                    }

                    var sub = BuildCollection(c, node, master: false, depth + 1);
                    if (sub is not null)
                    {
                        node.Children.Add(sub);
                    }
                }

                return node;
            }
            finally
            {
                _building.Remove(ptr);
            }
        }

        /// <summary>The node of an object (made on first use, under its parent's node when it has one, else under <paramref name="home"/>).</summary>
        private SceneNode? ObjectNode(ulong ptr, SceneNode home, int layer, int depth)
        {
            if (_objects.TryGetValue(ptr, out var existing))
            {
                return existing;
            }

            var ob = _f.Deref(ptr, "Object");
            if (ob.IsNull || depth > MaxDepth)
            {
                return null;
            }

            _f.CurrentScope = _f.ScopeOf(ob.Pos);

            var node = new SceneNode(ob.IdName());
            _objects[ptr] = node;
            var scope = _f.CurrentScope;
            var parentPtr = ob.Ptr("parent");
            SceneNode? parentNode = null;
            if (parentPtr != 0 && parentPtr != ptr)
            {
                parentNode = ObjectNode(parentPtr, home, layer, depth + 1);
                _f.CurrentScope = scope;
            }

            var local = LocalMatrix(ob);
            if (parentNode is not null)
            {
                node.Transform = local * ob.Matrix("parentinv");
                parentNode.Children.Add(node);
                var partype = ob.Int("partype") & 15;
                if (partype is 5 or 6 or 7)
                {
                    Skip("vertex or bone parent link(s) (placed as object parents)");
                }
            }
            else
            {
                node.Transform = local;
                home.Children.Add(node);
            }

            node.Layer = layer;
            var type = (int)ob.Int("type");
            var restrict = ob.Int(ob.Pick("restrictflag", "visibility_flag"));
            if ((restrict & 1) != 0 && type != 0)
            {
                _hidden++;
                return node;
            }

            if (ob.List("modifiers", "ModifierData").Any())
            {
                _modifiers = true;
            }

            switch (type)
            {
                case 1:
                    node.Properties["blender.type"] = "Mesh";
                    var mesh = MeshFor(ob, ob.Ptr("data"));
                    if (mesh is not null)
                    {
                        node.Mesh = mesh;
                    }

                    break;
                case 0:
                    var instance = ob.Ptr(ob.Pick("instance_collection", "dup_group"));
                    if (instance != 0 && (ob.Int("transflag") & (1 << 8)) != 0)
                    {
                        var c = _f.Deref(instance, "Collection");
                        if (!c.IsNull && !_building.Contains(instance))
                        {
                            // The instance gets its own copy of the collection's nodes (the same objects may also be placed directly).
                            var saved = _objects;
                            _objects = new Dictionary<ulong, SceneNode>();
                            SceneNode? inner;
                            try
                            {
                                inner = BuildCollection(c, new SceneNode(), master: false, depth: 1);
                            }
                            finally
                            {
                                _objects = saved;
                            }

                            if (inner is not null)
                            {
                                var field = c.Pick("instance_offset", "dupli_ofs");
                                var offset = c.Has(field) ? new Vector3(c.Float(field, 0), c.Float(field, 1), c.Float(field, 2)) : Vector3.Zero;
                                inner.Transform = Matrix4x4.CreateTranslation(-offset);
                                node.Children.Add(inner);
                            }
                        }
                    }

                    break;
                case 11:
                    Skip("camera(s)");
                    break;
                case 10:
                    Skip("light(s)");
                    break;
                case 25:
                    Skip("armature(s)");
                    break;
                case 4 or 5 or 6 or 7 or 26 or 27 or 28 or 29:
                    Skip("curve, text, surface, metaball or volume object(s) (no mesh data)");
                    break;
                default:
                    Skip("object(s) of type " + type.ToString(CultureInfo.InvariantCulture));
                    break;
            }

            return node;
        }

        private static Matrix4x4 LocalMatrix(Rec ob)
        {
            Vector3 V(string name) => new(ob.Float(name, 0), ob.Float(name, 1), ob.Float(name, 2));
            var loc = V("loc") + V("dloc");
            var scale = V("size");
            if (ob.Has("dscale"))
            {
                var d = V("dscale");
                if (d != Vector3.Zero)
                {
                    scale *= d;
                }
            }

            var mode = (int)ob.Int("rotmode");
            Matrix4x4 r;
            if (mode >= 1 && mode <= 6)
            {
                var e = V("rot") + V("drot");
                string[] orders = ["", "XYZ", "XZY", "YXZ", "YZX", "ZXY", "ZYX"];
                r = Matrix4x4.Identity;
                foreach (var axis in orders[mode])
                {
                    r *= axis switch
                    {
                        'X' => Matrix4x4.CreateRotationX(e.X),
                        'Y' => Matrix4x4.CreateRotationY(e.Y),
                        _ => Matrix4x4.CreateRotationZ(e.Z),
                    };
                }
            }
            else if (mode == -1)
            {
                var axis = V("rotAxis");
                r = axis.LengthSquared() > 1e-12f ? Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(axis), ob.Float("rotAngle")) : Matrix4x4.Identity;
            }
            else
            {
                var q = new Quaternion(ob.Float("quat", 1), ob.Float("quat", 2), ob.Float("quat", 3), ob.Float("quat", 0));
                r = q.LengthSquared() > 1e-12f ? Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(q)) : Matrix4x4.Identity;
            }

            return Matrix4x4.CreateScale(scale) * r * Matrix4x4.CreateTranslation(loc);
        }

        private static bool Prune(SceneNode node)
        {
            for (var i = node.Children.Count - 1; i >= 0; i--)
            {
                if (!Prune(node.Children[i]))
                {
                    node.Children.RemoveAt(i);
                }
            }

            return node.Mesh is not null || node.Children.Count > 0;
        }

        private static bool HasMesh(SceneNode node) => node.Mesh is not null || node.Children.Any(HasMesh);

        // ---- meshes ----

        private MeshData? MeshFor(Rec ob, ulong meshPtr)
        {
            if (_meshes.TryGetValue(meshPtr, out var cached))
            {
                return cached;
            }

            var me = _f.Deref(meshPtr, "Mesh");
            _f.CurrentScope = me.IsNull ? -1 : _f.ScopeOf(me.Pos);
            MeshData? mesh = null;
            if (!me.IsNull)
            {
                mesh = BuildMesh(me);
                if (mesh is not null)
                {
                    Model.Meshes.Add(mesh);
                }
            }

            _meshes[meshPtr] = mesh;
            if (ob.Ptr("matbits") != 0 && ob.Int("totcol") > 0 && _f.TryRaw(ob.Ptr("matbits"), (int)Math.Min(ob.Int("totcol"), 4096), out var bits) && bits.IndexOfAnyExcept((byte)0) >= 0)
            {
                _objectMaterials = true;
            }

            return mesh;
        }

        /// <summary>The layers of one domain: the classic CustomData layers, and (Blender 5) the attributes of the mesh's attribute storage.</summary>
        private List<Layer> Layers(Rec me, string customDataField, int domain)
        {
            var result = new List<Layer>();
            var customData = me.Sub(customDataField);
            if (!customData.IsNull)
            {
                var count = (int)Math.Min(customData.Int("totlayer"), 4096);
                var ptr = customData.Ptr("layers");
                if (count > 0 && ptr != 0 && _f.Sdna.ByName.TryGetValue("CustomDataLayer", out var ls)
                    && _f.TryResolve(ptr, out var pos, out var remaining) && remaining >= (long)count * ls.Size)
                {
                    for (var i = 0; i < count; i++)
                    {
                        var rec = _f.At("CustomDataLayer", pos + (long)i * ls.Size);
                        if (rec.IsNull)
                        {
                            break;
                        }

                        var data = rec.Ptr("data");
                        var block = _f.BlockAt(data);
                        result.Add(new Layer { Name = rec.Str("name"), Data = data, Type = rec.Int("type"), StructName = block is { } b ? _f.StructNameOf(b) : null });
                    }
                }
            }

            var storage = me.Sub("attribute_storage");
            if (!storage.IsNull && _f.Sdna.ByName.TryGetValue("Attribute", out var attr))
            {
                var count = (int)Math.Min(storage.Int("dna_attributes_num"), 4096);
                var ptr = storage.Ptr("dna_attributes");
                if (count > 0 && _f.TryResolve(ptr, out var pos, out var remaining))
                {
                    // The stored count can exceed what the block holds (it also counts attributes that were never written).
                    count = (int)Math.Min(count, remaining / attr.Size);
                    for (var i = 0; i < count; i++)
                    {
                        var rec = _f.At("Attribute", pos + (long)i * attr.Size);
                        if (rec.IsNull || rec.Int("domain") != domain)
                        {
                            continue;
                        }

                        var single = rec.Int("storage_type") == 1;
                        var holder = _f.Deref(rec.Ptr("data"), single ? "AttributeSingle" : "AttributeArray");
                        if (holder.IsNull)
                        {
                            continue;
                        }

                        var name = _f.TryResolve(rec.Ptr("name"), out var np, out var nr) ? ReadCString(np, nr) : "";
                        var data = holder.Ptr("data");
                        var block = _f.BlockAt(data);
                        result.Add(new Layer { Name = name, Data = data, Single = single, Type = rec.Int("data_type"), StructName = block is { } b ? _f.StructNameOf(b) : null });
                    }
                }
            }

            return result;
        }

        private string ReadCString(long pos, long remaining)
        {
            var span = _f.Data.AsSpan((int)pos, (int)Math.Min(remaining, 256));
            var z = span.IndexOf((byte)0);
            return System.Text.Encoding.UTF8.GetString(z < 0 ? span : span[..z]);
        }

        /// <summary>An int32 per element of the layer (a single-valued attribute is spread over all elements); null when the data is not there.</summary>
        private int[]? ReadInts(Layer layer, int count)
        {
            if (layer.Single)
            {
                if (!_f.TryRaw(layer.Data, 4, out _))
                {
                    return null;
                }

                var v = (int)_f.U32(RawPos(layer.Data));
                var all = new int[count];
                Array.Fill(all, v);
                return all;
            }

            if (!_f.TryRaw(layer.Data, count * 4L, out _))
            {
                return null;
            }

            var result = new int[count];
            var at = RawPos(layer.Data);
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = (int)_f.U32(at + 4L * i);
            }

            return result;
        }

        private bool[]? ReadBools(Layer layer, int count)
        {
            if (layer.Single)
            {
                if (!_f.TryRaw(layer.Data, 1, out _))
                {
                    return null;
                }

                var all = new bool[count];
                Array.Fill(all, _f.Data[RawPos(layer.Data)] != 0);
                return all;
            }

            if (!_f.TryRaw(layer.Data, count, out _))
            {
                return null;
            }

            var result = new bool[count];
            var at = RawPos(layer.Data);
            for (var i = 0; i < result.Length; i++)
            {
                result[i] = _f.Data[at + i] != 0;
            }

            return result;
        }

        private Layer? Find(List<Layer> layers, params string[] names)
        {
            foreach (var name in names)
            {
                var hit = layers.FirstOrDefault(l => l.Name == name && l.Data != 0);
                if (hit is not null)
                {
                    return hit;
                }
            }

            return null;
        }

        private Layer? FindStruct(List<Layer> layers, string structName) => layers.FirstOrDefault(l => l.StructName == structName && l.Data != 0);

        private MeshData? BuildMesh(Rec me)
        {
            var lv = me.Int(me.Pick("totvert", "verts_num"));
            var lp = me.Int(me.Pick("totpoly", "faces_num"));
            var ll = me.Int(me.Pick("totloop", "corners_num"));
            if (lv <= 0 || lp <= 0 || ll < 3 || lv > 1 << 28 || lp > 1 << 28 || ll > 1 << 29)
            {
                return null;
            }

            var nv = (int)lv;
            var np = (int)lp;
            var nl = (int)ll;

            var vdata = Layers(me, "vdata", 0);
            var pdata = Layers(me, "pdata", 2);
            var ldata = Layers(me, "ldata", 3);

            // Positions.
            var positions = new float[nv * 3];
            if (Find(vdata, "position") is { Single: false } posLayer && _f.TryRaw(posLayer.Data, nv * 12L, out _))
            {
                for (var i = 0; i < positions.Length; i++)
                {
                    positions[i] = _f.F32(RawPos(posLayer.Data) + 4L * i);
                }

            }
            else
            {
                var ptr = FindStruct(vdata, "MVert")?.Data ?? me.Ptr("mvert");
                if (!ReadStructArray(ptr, "MVert", "co", nv, 3, positions))
                {
                    throw new ModelFormatException("A Blender mesh has no readable vertex positions.");
                }
            }

            // Loop vertices.
            var corners = new int[nl];
            if (Find(ldata, ".corner_vert") is { } cv && ReadInts(cv, nl) is { } cornerVerts)
            {
                corners = cornerVerts;
            }
            else
            {
                var ptr = FindStruct(ldata, "MLoop")?.Data ?? me.Ptr("mloop");
                if (!ReadStructInts(ptr, "MLoop", "v", nl, corners))
                {
                    throw new ModelFormatException("A Blender mesh has no readable face corners.");
                }
            }

            // Polygons: where each one starts and ends in the corner list.
            var begins = new int[np];
            var ends = new int[np];
            var materialOf = new int[np];
            var smooth = new bool[np];
            var offsetsPtr = me.Ptr(me.Pick("poly_offset_indices", "face_offset_indices"));
            if (offsetsPtr != 0 && _f.TryRaw(offsetsPtr, (np + 1) * 4L, out _))
            {
                var at = RawPos(offsetsPtr);
                var previous = (int)_f.U32(at);
                for (var i = 0; i < np; i++)
                {
                    begins[i] = previous;
                    previous = (int)_f.U32(at + 4L * (i + 1));
                    ends[i] = previous;
                }

                Array.Fill(smooth, true);
                if (Find(pdata, "sharp_face", ".sharp_face") is { } sharp && ReadBools(sharp, np) is { } sharpFlags)
                {
                    for (var i = 0; i < np; i++)
                    {
                        smooth[i] = !sharpFlags[i];
                    }
                }

                if (Find(pdata, "material_index") is { } mi && ReadInts(mi, np) is { } indices)
                {
                    materialOf = indices;
                }
            }
            else
            {
                var ptr = FindStruct(pdata, "MPoly")?.Data ?? me.Ptr("mpoly");
                if (!_f.Sdna.ByName.TryGetValue("MPoly", out var ps) || !_f.TryResolve(ptr, out var pos, out var remaining) || remaining < (long)np * ps.Size
                    || !ps.ByName.TryGetValue("loopstart", out var startField) || !ps.ByName.TryGetValue("totloop", out var totalField))
                {
                    throw new ModelFormatException("A Blender mesh has no readable faces.");
                }

                var matOff = ps.ByName.TryGetValue("mat_nr", out var mf) ? mf.Offset : -1;
                var flagOff = ps.ByName.TryGetValue("flag", out var ff) ? ff.Offset : -1;
                for (var i = 0; i < np; i++)
                {
                    Tick();
                    var rec = pos + (long)i * ps.Size;
                    begins[i] = (int)_f.U32(rec + startField.Offset);
                    ends[i] = begins[i] + (int)_f.U32(rec + totalField.Offset);
                    if (matOff >= 0)
                    {
                        materialOf[i] = (short)_f.U16(rec + matOff);
                    }

                    smooth[i] = flagOff >= 0 && (_f.Data[rec + flagOff] & 1) != 0;
                }
            }

            // UVs.
            float[]? uvs = null;
            var uvLayer = ldata.FirstOrDefault(l => l.Data != 0 && !l.Name.StartsWith('.') && l.StructName is "vec2f" or "MLoopUV" or "float2");
            if (uvLayer is not null)
            {
                uvs = new float[nl * 2];
                var sname = uvLayer.StructName == "MLoopUV" ? "MLoopUV" : null;
                if (sname is not null)
                {
                    if (!ReadStructArray(uvLayer.Data, "MLoopUV", "uv", nl, 2, uvs))
                    {
                        uvs = null;
                    }
                }
                else if (_f.TryRaw(uvLayer.Data, nl * 8L, out _))
                {
                    var at = RawPos(uvLayer.Data);
                    for (var i = 0; i < uvs.Length; i++)
                    {
                        uvs[i] = _f.F32(at + 4L * i);
                    }
                }
                else
                {
                    uvs = null;
                }
            }

            return Assemble(me, positions, corners, begins, ends, materialOf, smooth, uvs);
        }

        private long RawPos(ulong ptr) => _f.TryResolve(ptr, out var pos, out _) ? pos : 0;

        private bool ReadStructArray(ulong ptr, string structName, string field, int count, int components, float[] result)
        {
            if (!_f.Sdna.ByName.TryGetValue(structName, out var s) || !s.ByName.TryGetValue(field, out var f) || !_f.TryResolve(ptr, out var pos, out var remaining) || remaining < (long)count * s.Size)
            {
                return false;
            }

            for (var i = 0; i < count; i++)
            {
                Tick();
                var at = pos + (long)i * s.Size + f.Offset;
                for (var c = 0; c < components; c++)
                {
                    result[i * components + c] = _f.F32(at + 4L * c);
                }
            }

            return true;
        }

        private bool ReadStructInts(ulong ptr, string structName, string field, int count, int[] result)
        {
            if (!_f.Sdna.ByName.TryGetValue(structName, out var s) || !s.ByName.TryGetValue(field, out var f) || !_f.TryResolve(ptr, out var pos, out var remaining) || remaining < (long)count * s.Size)
            {
                return false;
            }

            for (var i = 0; i < count; i++)
            {
                Tick();
                result[i] = (int)_f.U32(pos + (long)i * s.Size + f.Offset);
            }

            return true;
        }

        private MeshData? Assemble(Rec me, float[] positions, int[] corners, int[] starts, int[] ends, int[] materialOf, bool[] smooth, float[]? uvs)
        {
            var nv = positions.Length / 3;
            var np = materialOf.Length;
            var nl = corners.Length;
            int Start(int p) => starts[p];
            int End(int p) => ends[p];

            // Validate every polygon once.
            for (var p = 0; p < np; p++)
            {
                Tick();
                var s = Start(p);
                var e = End(p);
                if (s < 0 || e < s || e > nl)
                {
                    throw new ModelFormatException("A Blender face points outside the corner list.");
                }
            }

            for (var i = 0; i < nl; i++)
            {
                if ((uint)corners[i] >= (uint)nv)
                {
                    throw new ModelFormatException("A Blender face corner points outside the vertex list.");
                }
            }

            // Smooth vertex normals: the sum of the (area weighted) normals of the smooth faces around each vertex.
            var vertexNormals = new float[nv * 3];
            var faceNormals = new Vector3[np];
            for (var p = 0; p < np; p++)
            {
                Tick();
                var s = Start(p);
                var n = End(p) - s;
                if (n < 3)
                {
                    continue;
                }

                double nx = 0, ny = 0, nz = 0;
                for (var i = 0; i < n; i++)
                {
                    var a = corners[s + i] * 3;
                    var b = corners[s + (i + 1) % n] * 3;
                    nx += (positions[a + 1] - positions[b + 1]) * (positions[a + 2] + positions[b + 2]);
                    ny += (positions[a + 2] - positions[b + 2]) * (positions[a] + positions[b]);
                    nz += (positions[a] - positions[b]) * (positions[a + 1] + positions[b + 1]);
                }

                faceNormals[p] = new Vector3((float)nx, (float)ny, (float)nz);
                if (smooth[p])
                {
                    for (var i = 0; i < n; i++)
                    {
                        var v = corners[s + i] * 3;
                        vertexNormals[v] += (float)nx;
                        vertexNormals[v + 1] += (float)ny;
                        vertexNormals[v + 2] += (float)nz;
                    }
                }
            }

            var slots = MaterialSlots(me);
            var outPos = new List<float>(Math.Min(nl, 1 << 20) * 3);
            var outNormals = new List<float>(Math.Min(nl, 1 << 20) * 3);
            var outUvs = uvs is null ? null : new List<float>(Math.Min(nl, 1 << 20) * 2);
            var lookup = new Dictionary<(int V, int Flat, uint U, uint W), uint>();
            var byMaterial = new SortedDictionary<int, List<uint>>();
            var corner = new uint[64];
            var points = new List<Vector3>();

            uint Vertex(int p, int cornerIndex, bool flat)
            {
                var v = corners[cornerIndex];
                var u = uvs is null ? 0u : BitConverter.SingleToUInt32Bits(uvs[2 * cornerIndex]);
                var w = uvs is null ? 0u : BitConverter.SingleToUInt32Bits(uvs[2 * cornerIndex + 1]);
                var key = (v, flat ? p : -1, u, w);
                if (lookup.TryGetValue(key, out var index))
                {
                    return index;
                }

                index = (uint)(outPos.Count / 3);
                lookup[key] = index;
                outPos.Add(positions[v * 3]);
                outPos.Add(positions[v * 3 + 1]);
                outPos.Add(positions[v * 3 + 2]);
                Vector3 normal = flat ? faceNormals[p] : new Vector3(vertexNormals[v * 3], vertexNormals[v * 3 + 1], vertexNormals[v * 3 + 2]);
                if (!(normal.LengthSquared() > 1e-20f))
                {
                    normal = faceNormals[p].LengthSquared() > 1e-20f ? faceNormals[p] : Vector3.UnitZ;
                }

                normal = Vector3.Normalize(normal);
                outNormals.Add(normal.X);
                outNormals.Add(normal.Y);
                outNormals.Add(normal.Z);
                if (outUvs is not null)
                {
                    outUvs.Add(uvs![2 * cornerIndex]);
                    outUvs.Add(1f - uvs[2 * cornerIndex + 1]);
                }

                return index;
            }

            for (var p = 0; p < np; p++)
            {
                Tick();
                var s = Start(p);
                var n = End(p) - s;
                if (n < 3)
                {
                    continue;
                }

                var slot = materialOf[p];
                if (slots.Length == 0 || slot < 0 || slot >= slots.Length)
                {
                    slot = slots.Length == 0 ? -1 : Math.Clamp(slot, 0, slots.Length - 1);
                }

                if (!byMaterial.TryGetValue(slot, out var list))
                {
                    list = new List<uint>();
                    byMaterial[slot] = list;
                }

                _triangles += n - 2;
                if (_triangles > _o.MaxTriangles)
                {
                    throw new ModelFormatException("The Blender file has more triangles than the allowed " + _o.MaxTriangles.ToString(CultureInfo.InvariantCulture) + ".");
                }

                var flat = !smooth[p];
                if (corner.Length < n)
                {
                    corner = new uint[Math.Min(n, 1 << 20)];
                    if (corner.Length < n)
                    {
                        continue;
                    }
                }

                for (var i = 0; i < n; i++)
                {
                    corner[i] = Vertex(p, s + i, flat);
                }

                if (n == 3)
                {
                    list.Add(corner[0]);
                    list.Add(corner[1]);
                    list.Add(corner[2]);
                    continue;
                }

                if (n == 4)
                {
                    var fn = faceNormals[p];
                    Vector3 P(int i) => new(positions[corners[s + i] * 3], positions[corners[s + i] * 3 + 1], positions[corners[s + i] * 3 + 2]);
                    bool Good(int a, int b, int c) => Vector3.Dot(Vector3.Cross(P(b) - P(a), P(c) - P(a)), fn) > 0;
                    if (Good(0, 1, 2) && Good(0, 2, 3))
                    {
                        list.AddRange([corner[0], corner[1], corner[2], corner[0], corner[2], corner[3]]);
                    }
                    else
                    {
                        list.AddRange([corner[1], corner[2], corner[3], corner[1], corner[3], corner[0]]);
                    }

                    continue;
                }

                points.Clear();
                for (var i = 0; i < n; i++)
                {
                    var v = corners[s + i] * 3;
                    points.Add(new Vector3(positions[v], positions[v + 1], positions[v + 2]));
                }

                foreach (var (a, b, c) in PolygonTriangulation.Triangulate(points))
                {
                    list.Add(corner[a]);
                    list.Add(corner[b]);
                    list.Add(corner[c]);
                }
            }

            var indices = new List<uint>();
            var mesh = new MeshData { Name = me.IdName() };
            foreach (var (slot, list) in byMaterial)
            {
                if (list.Count == 0)
                {
                    continue;
                }

                var material = slot >= 0 && slot < slots.Length ? slots[slot] : -1;
                mesh.Groups.Add(new MeshGroup(indices.Count, list.Count, material));
                indices.AddRange(list);
            }

            if (indices.Count == 0)
            {
                return null;
            }

            mesh.Positions = [.. outPos];
            mesh.Normals = [.. outNormals];
            if (outUvs is not null)
            {
                mesh.TexCoords = [.. outUvs];
            }

            mesh.Indices = [.. indices];
            return mesh;
        }

        // ---- materials ----

        private int[] MaterialSlots(Rec me)
        {
            var count = (int)Math.Clamp(me.Int("totcol"), 0, 4096);
            var ptr = me.Ptr("mat");
            if (count == 0 || ptr == 0 || !_f.TryRaw(ptr, (long)count * _f.PtrSize, out _))
            {
                return [];
            }

            var slots = new int[count];
            var at = RawPos(ptr);
            for (var i = 0; i < count; i++)
            {
                var mp = _f.Ptr(at + (long)i * _f.PtrSize);
                slots[i] = MaterialIndex(mp);
            }

            return slots;
        }

        private int MaterialIndex(ulong ptr)
        {
            if (ptr == 0)
            {
                return -1;
            }

            if (_materials.TryGetValue(ptr, out var index))
            {
                return index;
            }

            var ma = _f.Deref(ptr, "Material");
            if (ma.IsNull)
            {
                return _materials[ptr] = -1;
            }

            var previousScope = _f.CurrentScope;
            _f.CurrentScope = _f.ScopeOf(ma.Pos);

            var material = new ModelMaterial
            {
                Name = ma.IdName(),
                BaseColor = new Vector4(ma.Float("r", 0, 0.8f), ma.Float("g", 0, 0.8f), ma.Float("b", 0, 0.8f), ma.Has("a") ? ma.Float("a", 0, 1f) : 1f),
                Metallic = ma.Float("metallic", 0, 0f),
                Roughness = ma.Float("roughness", 0, 0.5f),
            };
            if (ma.Int("use_nodes") != 0)
            {
                ReadPrincipled(ma, material);
            }

            _f.CurrentScope = previousScope;
            index = Model.Materials.Count;
            Model.Materials.Add(material);
            return _materials[ptr] = index;
        }

        private void ReadPrincipled(Rec ma, ModelMaterial material)
        {
            var tree = _f.Deref(ma.Ptr("nodetree"), "bNodeTree");
            if (tree.IsNull)
            {
                return;
            }

            foreach (var node in tree.List("nodes", "bNode"))
            {
                Tick();
                if (node.Str("idname") != "ShaderNodeBsdfPrincipled")
                {
                    continue;
                }

                foreach (var socket in node.List("inputs", "bNodeSocket"))
                {
                    var name = socket.Str("name");
                    var dv = socket.Ptr("default_value");
                    if (dv == 0)
                    {
                        continue;
                    }

                    // bNodeSocketValueFloat starts with a subtype int in some versions: the field offset comes from the block's own struct.
                    var valueOffset = name == "Base Color" ? 0L : 4L;
                    if (_f.BlockAt(dv) is { } valueBlock && _f.StructNameOf(valueBlock) is { } valueStruct
                        && _f.Sdna.ByName.TryGetValue(valueStruct, out var vs) && vs.ByName.TryGetValue("value", out var vf))
                    {
                        valueOffset = vf.Offset;
                    }

                    float Value(int i = 0) => _f.TryResolve(dv, out var p, out var rem) && rem >= valueOffset + 4L * (i + 1) ? _f.F32(p + valueOffset + 4L * i) : float.NaN;
                    switch (name)
                    {
                        case "Base Color":
                            var r = Value(0);
                            var g = Value(1);
                            var b = Value(2);
                            if (float.IsFinite(r + g + b))
                            {
                                material.BaseColor = new Vector4(r, g, b, material.BaseColor.W);
                            }

                            break;
                        case "Metallic":
                            if (float.IsFinite(Value()))
                            {
                                material.Metallic = Math.Clamp(Value(), 0f, 1f);
                            }

                            break;
                        case "Roughness":
                            if (float.IsFinite(Value()))
                            {
                                material.Roughness = Math.Clamp(Value(), 0f, 1f);
                            }

                            break;
                        case "Alpha":
                            if (float.IsFinite(Value()))
                            {
                                material.BaseColor = new Vector4(material.BaseColor.X, material.BaseColor.Y, material.BaseColor.Z, Math.Clamp(Value(), 0f, 1f));
                            }

                            break;
                    }
                }

                return;
            }
        }
    }
}
