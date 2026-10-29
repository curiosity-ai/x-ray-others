using System.Globalization;
using System.Numerics;
using System.Xml;
using System.Xml.Linq;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.Bim;

/// <summary>
/// Sweet Home 3D (.sh3d, a ZIP whose Home.xml describes the home; Sweet Home 3D 5.3 and later): name, levels, walls, rooms and the furniture
/// list (catalogue ids, sizes, prices) as nodes with extras. Furniture is drawn from its embedded OBJ model fitted to the piece's width, depth
/// and height, or as a box when the model cannot be read; walls are boxes and rooms flat floors. Plan coordinates are centimetres with y down;
/// the scene is Z-up with the plan mirrored in Y so it keeps its handedness.
/// </summary>
internal static class Sh3dReader
{
    private const double DefaultWallHeight = 250;

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        using var zip = ZipEntries.Open(data);
        var entry = ZipEntries.Find(zip, "Home.xml");
        if (entry is null)
        {
            throw new UnsupportedModelFormatException(
                ModelFormats.Sh3d,
                "This Sweet Home 3D file stores the home as a serialised Java object; only the XML form (Home.xml, Sweet Home 3D 5.3 and later) is read.");
        }

        var limit = Math.Min(options.MaxInputBytes, 1L << 28);
        XDocument document;
        using (var stream = new MemoryStream(ZipEntries.Read(entry, limit), writable: false))
        using (var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null }))
        {
            document = XDocument.Load(reader);
        }

        var home = document.Root;
        if (home is null || home.Name.LocalName != "home")
        {
            throw new ModelFormatException("Home.xml is not a Sweet Home 3D home.");
        }

        return new Builder(home, zip, options, limit).Build();
    }

    private sealed class Builder
    {
        private readonly XElement _home;
        private readonly System.IO.Compression.ZipArchive _zip;
        private readonly LoadOptions _options;
        private readonly long _limit;
        private readonly Model3D _model = new();
        private readonly Dictionary<string, (SceneNode Node, double Elevation)> _levels = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (Model3D Source, BoundingBox Box)?> _models = new(StringComparer.Ordinal);
        private readonly Dictionary<int, MeshData> _boxes = new();
        private long _triangles;
        private int _furniture;
        private int _walls;
        private int _rooms;
        private int _boxFallbacks;

        public Builder(XElement home, System.IO.Compression.ZipArchive zip, LoadOptions options, long limit)
        {
            _home = home;
            _zip = zip;
            _options = options;
            _limit = limit;
        }

        public Model3D Build()
        {
            var meta = _model.Metadata;
            meta.Format = ModelFormats.Sh3d;
            meta.DocumentKind = "building";
            meta.Units = LengthUnit.Centimeter;
            meta.UpAxis = UpAxis.Z;
            var version = (string?)_home.Attribute("version");
            if (version is not null)
            {
                meta.FormatVersion = version;
                if (int.TryParse(version, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1000)
                {
                    meta.Application = $"Sweet Home 3D {number / 1000}.{number % 1000 / 100}";
                }
            }

            if ((string?)_home.Attribute("name") is { Length: > 0 } name)
            {
                meta.Title = name.EndsWith(".sh3d", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name;
            }

            var wallHeight = Number(_home.Attribute("wallHeight")) ?? DefaultWallHeight;
            meta.Properties.Add(new ModelProperty("wall height", Format(wallHeight), "home"));
            foreach (var property in _home.Elements("property"))
            {
                if ((string?)property.Attribute("name") is { Length: > 0 } key && (string?)property.Attribute("value") is { } value)
                {
                    meta.Properties.Add(new ModelProperty(key, value, "property"));
                }
            }

            var root = new SceneNode(meta.Title ?? "home");
            _model.Roots.Add(root);
            foreach (var level in _home.Elements("level").OrderBy(l => Number(l.Attribute("elevation")) ?? 0))
            {
                var id = (string?)level.Attribute("id") ?? "";
                var elevation = Number(level.Attribute("elevation")) ?? 0;
                var levelName = (string?)level.Attribute("name") ?? "Level";
                var layer = _model.Layers.Count;
                _model.Layers.Add(new ModelLayer(levelName) { Visible = (string?)level.Attribute("visible") != "false" });
                meta.LayerNames.Add(levelName);
                var node = new SceneNode(levelName) { Layer = layer, Transform = Matrix4x4.CreateTranslation(0, 0, (float)elevation) };
                node.Properties["Elevation"] = Format(elevation);
                AddIfPresent(node, "Height", level.Attribute("height"));
                AddIfPresent(node, "FloorThickness", level.Attribute("floorThickness"));
                root.Children.Add(node);
                if (id.Length > 0)
                {
                    _levels[id] = (node, elevation);
                }

                meta.Features.Add(new ModelFeature(levelName, "level"));
            }

            foreach (var element in _home.Elements())
            {
                _options.Cancellation.ThrowIfCancellationRequested();
                switch (element.Name.LocalName)
                {
                    case "wall":
                        AddWall(element, root, wallHeight);
                        break;
                    case "room":
                        AddRoom(element, root);
                        break;
                    case "pieceOfFurniture" or "doorOrWindow" or "light" or "furnitureGroup":
                        AddPiece(element, Parent(element, root), 0);
                        break;
                }
            }

            meta.Properties.Add(new ModelProperty("walls", _walls.ToString(CultureInfo.InvariantCulture), "home"));
            meta.Properties.Add(new ModelProperty("rooms", _rooms.ToString(CultureInfo.InvariantCulture), "home"));
            meta.Properties.Add(new ModelProperty("furniture", _furniture.ToString(CultureInfo.InvariantCulture), "home"));
            if (_boxFallbacks > 0)
            {
                _model.Warnings.Add($"{_boxFallbacks} piece(s) of furniture have no readable model and are drawn as boxes.");
            }

            meta.HasGeometry = _model.Meshes.Count > 0;
            meta.Statistics = _model.ComputeStatistics();
            meta.Bounds = _model.ComputeBounds();
            return _model;
        }

        private static double? Number(XAttribute? attribute) =>
            attribute is not null && double.TryParse(attribute.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : null;

        private static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);

        private static void AddIfPresent(SceneNode node, string key, XAttribute? attribute)
        {
            if (attribute is { Value.Length: > 0 })
            {
                node.Properties[key] = attribute.Value;
            }
        }

        private SceneNode Parent(XElement element, SceneNode fallback) =>
            (string?)element.Attribute("level") is { } id && _levels.TryGetValue(id, out var level) ? level.Node : fallback;

        /// <summary>#AARRGGBB or AARRGGBB (hex) to opaque RGB, or null.</summary>
        private static int? Colour(XAttribute? attribute)
        {
            if (attribute is null)
            {
                return null;
            }

            var text = attribute.Value.TrimStart('#');
            return text.Length >= 6 && int.TryParse(text.AsSpan(text.Length - 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb) ? rgb : null;
        }

        private int MaterialFor(int? rgb)
        {
            var colour = rgb ?? 0xC8C8C8;
            var name = "#" + colour.ToString("X6", CultureInfo.InvariantCulture);
            var index = _model.Materials.FindIndex(m => m.Name == name);
            if (index < 0)
            {
                index = _model.Materials.Count;
                _model.Materials.Add(new ModelMaterial { Name = name, BaseColor = new Vector4(((colour >> 16) & 255) / 255f, ((colour >> 8) & 255) / 255f, (colour & 255) / 255f, 1) });
            }

            return index;
        }

        /// <summary>A unit box (x and z centred, y from 0 to 1: the model convention of a piece) in the given colour, shared by every piece of that colour.</summary>
        private MeshData Box(int? rgb)
        {
            var key = rgb ?? -1;
            if (!_boxes.TryGetValue(key, out var mesh))
            {
                var acc = new MeshAccumulator(1000);
                acc.AddBox(new Vector3(-0.5f, 0, -0.5f), new Vector3(0.5f, 1, 0.5f), Matrix4x4.Identity);
                mesh = acc.ToMesh("box", MaterialFor(rgb))!;
                _model.Meshes.Add(mesh);
                _boxes[key] = mesh;
            }

            return mesh;
        }

        /// <summary>Places a model whose bounds are <paramref name="box"/> (Y up) so it fills width x height x depth centred at (x, y), turned by angle.</summary>
        private static Matrix4x4 Placement(BoundingBox box, double width, double depth, double height, double x, double y, double elevation, double angle, bool mirrored)
        {
            var size = box.Size;
            var sx = size.X > 1e-9f ? (float)width / size.X : 1f;
            var sy = size.Y > 1e-9f ? (float)height / size.Y : 1f;
            var sz = size.Z > 1e-9f ? (float)depth / size.Z : 1f;
            var centre = Matrix4x4.CreateTranslation(-(box.Min.X + box.Max.X) / 2, -box.Min.Y, -(box.Min.Z + box.Max.Z) / 2);
            var scale = Matrix4x4.CreateScale(mirrored ? -sx : sx, sy, sz);
            // Y-up model axes to the Z-up scene: x stays, model z becomes -y, model y becomes z.
            var upright = new Matrix4x4(1, 0, 0, 0, 0, 0, 1, 0, 0, -1, 0, 0, 0, 0, 0, 1);
            return centre * scale * upright * Matrix4x4.CreateRotationZ((float)-angle) * Matrix4x4.CreateTranslation((float)x, (float)-y, (float)elevation);
        }

        private void Count(MeshData mesh)
        {
            _triangles += mesh.TriangleCount;
            if (_triangles > _options.MaxTriangles)
            {
                throw new ModelFormatException("The home has more triangles than the configured limit.");
            }
        }

        private void AddWall(XElement wall, SceneNode root, double homeWallHeight)
        {
            double x1 = Number(wall.Attribute("xStart")) ?? 0, y1 = Number(wall.Attribute("yStart")) ?? 0;
            double x2 = Number(wall.Attribute("xEnd")) ?? 0, y2 = Number(wall.Attribute("yEnd")) ?? 0;
            var length = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
            var thickness = Number(wall.Attribute("thickness")) ?? 7.5;
            var height = Number(wall.Attribute("height")) ?? homeWallHeight;
            if (!(length > 1e-6) || !(height > 0))
            {
                return;
            }

            _walls++;
            var mesh = Box(Colour(wall.Attribute("leftSideColor")) ?? Colour(wall.Attribute("topColor")));
            Count(mesh);
            var node = new SceneNode("Wall") { Mesh = mesh };
            node.Properties["Id"] = (string?)wall.Attribute("id") ?? "";
            node.Properties["Length"] = Format(length);
            node.Properties["Thickness"] = Format(thickness);
            node.Properties["Height"] = Format(height);
            node.Transform = Placement(mesh.ComputeBounds(), length, thickness, height, (x1 + x2) / 2, (y1 + y2) / 2, 0, Math.Atan2(y2 - y1, x2 - x1), mirrored: false);
            Parent(wall, root).Children.Add(node);
        }

        private void AddRoom(XElement room, SceneNode root)
        {
            var points = room.Elements("point")
                .Select(p => (X: Number(p.Attribute("x")), Y: Number(p.Attribute("y"))))
                .Where(p => p.X is not null && p.Y is not null)
                .Select(p => new Vector3((float)p.X!.Value, (float)-p.Y!.Value, 0))
                .ToList();
            if (points.Count < 3)
            {
                return;
            }

            _rooms++;
            double area = 0;
            for (var i = 0; i < points.Count; i++)
            {
                var a = points[i];
                var b = points[(i + 1) % points.Count];
                area += (double)a.X * b.Y - (double)b.X * a.Y;
            }

            if (area < 0)
            {
                points.Reverse();
            }

            var acc = new MeshAccumulator(_options.MaxTriangles - _triangles);
            acc.AddPolygon(points);
            var name = (string?)room.Attribute("name");
            var node = new SceneNode(string.IsNullOrEmpty(name) ? "Room" : name);
            node.Properties["Id"] = (string?)room.Attribute("id") ?? "";
            node.Properties["Area"] = Format(Math.Abs(area) / 2);
            AddIfPresent(node, "FloorColor", room.Attribute("floorColor"));
            if (acc.ToMesh(node.Name, MaterialFor(Colour(room.Attribute("floorColor")) ?? 0xD0D0D0)) is { } mesh)
            {
                Count(mesh);
                _model.Meshes.Add(mesh);
                node.Mesh = mesh;
            }

            Parent(room, root).Children.Add(node);
            _model.Metadata.Features.Add(new ModelFeature(node.Name!, "room"));
        }

        private static readonly string[] PieceKinds = ["pieceOfFurniture", "doorOrWindow", "light", "furnitureGroup"];

        private void AddPiece(XElement piece, SceneNode parent, int depth)
        {
            var name = (string?)piece.Attribute("name");
            var node = new SceneNode(string.IsNullOrEmpty(name) ? piece.Name.LocalName : name);
            if (piece.Name.LocalName != "furnitureGroup")
            {
                Place(piece, node, parent);
                return;
            }

            node.Properties["Id"] = (string?)piece.Attribute("id") ?? "";
            parent.Children.Add(node);
            if (depth < 16)
            {
                foreach (var child in piece.Elements().Where(e => PieceKinds.Contains(e.Name.LocalName)))
                {
                    AddPiece(child, node, depth + 1);
                }
            }
        }

        private void Place(XElement piece, SceneNode node, SceneNode parent)
        {
            _furniture++;
            double x = Number(piece.Attribute("x")) ?? 0, y = Number(piece.Attribute("y")) ?? 0;
            var elevation = Number(piece.Attribute("elevation")) ?? 0;
            var angle = Number(piece.Attribute("angle")) ?? 0;
            double width = Math.Max(Number(piece.Attribute("width")) ?? 1, 0), depth = Math.Max(Number(piece.Attribute("depth")) ?? 1, 0), height = Math.Max(Number(piece.Attribute("height")) ?? 1, 0);
            var mirrored = (string?)piece.Attribute("modelMirrored") == "true";
            foreach (var key in new[] { "id", "catalogId", "model", "description", "information", "creator", "price", "color" })
            {
                AddIfPresent(node, key switch { "id" => "Id", "catalogId" => "CatalogId", _ => char.ToUpperInvariant(key[0]) + key[1..] }, piece.Attribute(key));
            }

            node.Properties["Width"] = Format(width);
            node.Properties["Depth"] = Format(depth);
            node.Properties["Height"] = Format(height);
            node.Properties["X"] = Format(x);
            node.Properties["Y"] = Format(y);
            node.Properties["Elevation"] = Format(elevation);
            node.Properties["Angle"] = Format(angle);
            node.Properties["Kind"] = piece.Name.LocalName;
            if ((string?)piece.Attribute("visible") == "false")
            {
                node.Properties["Visible"] = "false";
            }

            if (!_model.Metadata.PartNames.Contains(node.Name!))
            {
                _model.Metadata.PartNames.Add(node.Name!);
            }

            var modelPath = (string?)piece.Attribute("model");
            var loaded = modelPath is null ? null : LoadModel(modelPath);
            if (loaded is { } m)
            {
                node.Transform = Placement(m.Box, width, depth, height, x, y, elevation, angle, mirrored);
                foreach (var root in m.Source.Roots)
                {
                    var clone = ModelMerge.CloneTree(root);
                    node.Children.Add(clone);
                    foreach (var n in new[] { clone }.SelectMany(Descend))
                    {
                        if (n.Mesh is { } mesh)
                        {
                            Count(mesh);
                        }
                    }
                }
            }
            else
            {
                _boxFallbacks++;
                var mesh = Box(Colour(piece.Attribute("color")));
                Count(mesh);
                node.Mesh = mesh;
                node.Transform = Placement(mesh.ComputeBounds(), width, depth, height, x, y, elevation, angle, mirrored);
            }

            parent.Children.Add(node);
        }

        private static IEnumerable<SceneNode> Descend(SceneNode node)
        {
            var stack = new Stack<SceneNode>([node]);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                yield return n;
                foreach (var c in n.Children)
                {
                    stack.Push(c);
                }
            }
        }

        /// <summary>Loads an embedded model once (its meshes join the home); null when it is missing or unreadable.</summary>
        private (Model3D Source, BoundingBox Box)? LoadModel(string path)
        {
            if (_models.TryGetValue(path, out var cached))
            {
                return cached;
            }

            (Model3D, BoundingBox)? result = null;
            try
            {
                if (ZipEntries.Find(_zip, path) is { } entry && ZipEntries.Read(entry, _limit) is { } bytes)
                {
                    var directory = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
                    var inner = _options.Clone();
                    inner.ResolveCompanion = name =>
                    {
                        var full = directory.Length == 0 ? name : directory + "/" + name;
                        return ZipEntries.Find(_zip, full) is { } e ? ZipEntries.Read(e, _limit) : null;
                    };
                    var source = ModelLoader.Load(bytes, entry.Name, inner);
                    if (source.ComputeBounds() is { IsEmpty: false } box)
                    {
                        ModelMerge.AdoptResources(_model, source);
                        result = (source, box);
                    }
                }
            }
            catch (ModelFormatException)
            {
                // drawn as a box
            }

            _models[path] = result;
            return result;
        }
    }
}
