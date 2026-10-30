using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using XRay.ThreeD.Formats.Step;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.Bim;

/// <summary>
/// IFC (ISO 16739, STEP physical file; .ifc and .ifczip): the project, site, building and storey tree with every product as a node,
/// names, GlobalIds, property sets and quantities in each node's extras, units and header metadata, and geometry for face sets,
/// faceted B-reps and extruded solids placed by their local placement chains. Other representations leave a node without
/// geometry and a warning.
/// </summary>
internal static class IfcReader
{
    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options) => new Builder(Parse(data, options), options, geometry: true).Build();

    public static ModelMetadata ReadMetadata(ReadOnlyMemory<byte> data, LoadOptions options) => new Builder(Parse(data, options), options, geometry: false).Build().Metadata;

    private static StepFile Parse(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var span = data.Span;
        if (span.Length > 2 && span[0] == 'P' && span[1] == 'K')
        {
            using var zip = ZipEntries.Open(data);
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith(".ifc", StringComparison.OrdinalIgnoreCase))
                ?? throw new ModelFormatException("The zipped IFC has no .ifc entry.");
            data = ZipEntries.Read(entry, options.MaxInputBytes);
        }

        return StepFile.Parse(Encoding.Latin1.GetString(data.Span), options);
    }

    private static readonly string[] NotProducts =
    [
        "IFCREL", "IFCPROPERTY", "IFCQUANTITY", "IFCCARTESIAN", "IFCDIRECTION", "IFCPOLY", "IFCFACE", "IFCAXIS", "IFCLOCAL", "IFCSTYLED",
        "IFCSHAPE", "IFCPRODUCT", "IFCSURFACE", "IFCCOLOUR", "IFCPRESENTATION", "IFCMATERIAL", "IFCOWNER", "IFCPERSON", "IFCORGANIZATION",
        "IFCCLOSEDSHELL", "IFCOPENSHELL", "IFCTRIANGULATED", "IFCINDEXED", "IFCEXTRUDED", "IFCMAPPED", "IFCREPRESENTATION", "IFCRECTANGLE",
        "IFCCIRCLE", "IFCUNIT", "IFCSIUNIT", "IFCMEASURE", "IFCDIMENSIONAL", "IFCCONVERSION", "IFCGEOMETRIC", "IFCLINE", "IFCTRIMMED",
        "IFCELEMENTQUANTITY", "IFCCLASSIFICATION", "IFCAPPLICATION", "IFCACTOR", "IFCTELECOM", "IFCPOSTAL", "IFCDOCUMENT", "IFCBOOLEAN",
        "IFCCONNECTION", "IFCCURVE", "IFCBSPLINE", "IFCCOMPOSITE", "IFCARBITRARY", "IFCCOORDINATED", "IFCMAPCONVERSION", "IFCPROJECTED",
    ];

    private static readonly string[] SkippedRepresentations = ["Axis", "Box", "FootPrint", "Annotation", "Plan", "Profile", "Reference", "CoG", "Clearance", "Lighting", "Surface"];

    private sealed class Builder
    {
        private readonly StepFile _f;
        private readonly LoadOptions _options;
        private readonly bool _geometry;
        private readonly IfcGeometry _geo;
        private readonly Model3D _model = new();
        private readonly Dictionary<int, List<int>> _children = new();
        private readonly HashSet<int> _hasParent = new();
        private readonly Dictionary<int, List<int>> _definitions = new();
        private readonly Dictionary<int, List<int>> _materialsOf = new();
        private readonly Dictionary<int, int> _typeOf = new();
        private readonly Dictionary<(int, int, int, int), int> _materialIndex = new();
        private readonly Dictionary<string, int> _typeCounts = new(StringComparer.Ordinal);
        private long _triangles;
        private int _productsWithoutGeometry;

        public Builder(StepFile file, LoadOptions options, bool geometry)
        {
            _f = file;
            _options = options;
            _geometry = geometry;
            _geo = new IfcGeometry(file, options);
        }

        public Model3D Build()
        {
            var meta = _model.Metadata;
            ReadHeader(meta);
            var project = _f.OfType("IFCPROJECT").FirstOrDefault();
            if (project is null)
            {
                throw new ModelFormatException("The IFC file has no IfcProject.");
            }

            ReadUnits(meta, project);
            IndexRelations();
            if (_geometry)
            {
                ReadStyles();
            }

            BuildTree(project);

            meta.PartNames.AddRange(_model.Nodes().Where(n => n.Properties.ContainsKey("GlobalId") && n.Children.Count == 0 && !string.IsNullOrEmpty(n.Name)).Select(n => n.Name!).Distinct().Take(10_000));
            foreach (var (type, count) in _typeCounts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal))
            {
                meta.Properties.Add(new ModelProperty(type, count.ToString(CultureInfo.InvariantCulture), "entities"));
            }

            meta.MaterialNames.AddRange(_materialsOf.Values.SelectMany(v => v).Distinct().Select(id => MaterialName(id)).Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).Take(1000));
            if (_geometry)
            {
                foreach (var (type, count) in _geo.Unsupported.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    _model.Warnings.Add($"{count} representation item(s) of type {type} are not drawn (unsupported geometry).");
                }

                foreach (var note in _geo.Approximations.OrderBy(n => n, StringComparer.Ordinal))
                {
                    _model.Warnings.Add(note);
                }

                if (_f.OfType("IFCRELVOIDSELEMENT").Count > 0)
                {
                    _model.Warnings.Add("Openings (IfcRelVoidsElement) are not cut out of their elements.");
                }

                meta.HasGeometry = _model.Meshes.Count > 0;
                meta.Statistics = _model.ComputeStatistics();
                meta.Bounds = _model.ComputeBounds();
            }
            else
            {
                meta.HasGeometry = _f.OfType("IFCPRODUCTDEFINITIONSHAPE").Count > 0;
            }

            if (_productsWithoutGeometry > 0)
            {
                meta.Notes.Add($"{_productsWithoutGeometry} product(s) carry no drawable geometry.");
            }

            return _model;
        }

        // ---- header, units ---------------------------------------------------------------------------------------

        private void ReadHeader(ModelMetadata meta)
        {
            meta.Format = ModelFormats.Ifc;
            meta.DocumentKind = "building";
            meta.UpAxis = UpAxis.Z;
            if (_f.Header.TryGetValue("FILE_NAME", out var name) && name.Count >= 7)
            {
                if (name[0].Str.Length > 0)
                {
                    meta.Title = name[0].Str;
                }

                if (DateTimeOffset.TryParse(name[1].Str, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created))
                {
                    meta.Created = created;
                }

                var author = string.Join(", ", name[2].Items.Select(i => i.Str).Where(s => s.Length > 0));
                if (author.Length > 0)
                {
                    meta.Author = author;
                }

                var organisation = string.Join(", ", name[3].Items.Select(i => i.Str).Where(s => s.Length > 0));
                if (organisation.Length > 0)
                {
                    meta.Company = organisation;
                }

                meta.Application = string.Join(" / ", new[] { name[5].Str, name[4].Str }.Where(s => s.Length > 0));
            }

            if (_f.Header.TryGetValue("FILE_DESCRIPTION", out var description) && description.Count > 0)
            {
                var text = string.Join(" ", description[0].Items.Select(i => i.Str).Where(s => s.Length > 0));
                if (text.Length > 0)
                {
                    meta.Properties.Add(new ModelProperty("description", text, "ifc"));
                }
            }

            if (_f.Header.TryGetValue("FILE_SCHEMA", out var schema) && schema.Count > 0)
            {
                meta.FormatVersion = string.Join(", ", schema[0].Items.Select(i => i.Str).Where(s => s.Length > 0));
            }
        }

        private void ReadUnits(ModelMetadata meta, StepEntity project)
        {
            var context = _f.Get(project.Arg(8));
            if (context is not null)
            {
                foreach (var unitRef in context.Arg(0).Items.Where(u => u.IsRef))
                {
                    var unit = _f.Get(unitRef);
                    if (unit is null || !unit.Arg(1).Str.Equals("LENGTHUNIT", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    meta.Units = LengthOf(unit);
                    break;
                }
            }

            if (meta.Units is null)
            {
                meta.Units = LengthUnit.Meter;
                meta.Notes.Add("The file states no length unit; meters are assumed.");
            }
        }

        private LengthUnit? LengthOf(StepEntity unit)
        {
            if (unit.Type == "IFCSIUNIT")
            {
                return unit.Arg(2).Str.ToUpperInvariant() switch
                {
                    "MILLI" => LengthUnit.Millimeter,
                    "CENTI" => LengthUnit.Centimeter,
                    "KILO" => LengthUnit.Kilometer,
                    "MICRO" => LengthUnit.Micrometer,
                    "DECI" => LengthUnit.Decimeter,
                    "NANO" => LengthUnit.Nanometer,
                    _ => LengthUnit.Meter,
                };
            }

            if (unit.Type == "IFCCONVERSIONBASEDUNIT")
            {
                return unit.Arg(2).Str.ToUpperInvariant() switch
                {
                    "INCH" or "IN" => LengthUnit.Inch,
                    "FOOT" or "FT" => LengthUnit.Foot,
                    "YARD" => LengthUnit.Yard,
                    "MILE" => LengthUnit.Mile,
                    _ => null,
                };
            }

            return null;
        }

        // ---- relationships ---------------------------------------------------------------------------------------

        private void IndexRelations()
        {
            void Link(int parent, IEnumerable<int> kids)
            {
                if (!_children.TryGetValue(parent, out var list))
                {
                    _children[parent] = list = [];
                }

                foreach (var kid in kids)
                {
                    if (kid != parent && _hasParent.Add(kid))
                    {
                        list.Add(kid);
                    }
                }
            }

            foreach (var type in new[] { "IFCRELAGGREGATES", "IFCRELNESTS" })
            {
                foreach (var rel in _f.OfType(type))
                {
                    Link(rel.Arg(4).ToRef, rel.Arg(5).Refs);
                }
            }

            foreach (var rel in _f.OfType("IFCRELCONTAINEDINSPATIALSTRUCTURE"))
            {
                Link(rel.Arg(5).ToRef, rel.Arg(4).Refs);
            }

            foreach (var rel in _f.OfType("IFCRELDEFINESBYPROPERTIES"))
            {
                var definition = rel.Arg(5).ToRef;
                foreach (var o in rel.Arg(4).Refs)
                {
                    (_definitions.TryGetValue(o, out var list) ? list : _definitions[o] = []).Add(definition);
                }
            }

            foreach (var rel in _f.OfType("IFCRELDEFINESBYTYPE"))
            {
                foreach (var o in rel.Arg(4).Refs)
                {
                    _typeOf[o] = rel.Arg(5).ToRef;
                }
            }

            foreach (var rel in _f.OfType("IFCRELASSOCIATESMATERIAL"))
            {
                var material = rel.Arg(5).ToRef;
                foreach (var o in rel.Arg(4).Refs)
                {
                    (_materialsOf.TryGetValue(o, out var list) ? list : _materialsOf[o] = []).Add(material);
                }
            }
        }

        private string MaterialName(int id, int depth = 0)
        {
            var e = _f.Get(id);
            if (e is null || depth > 4)
            {
                return "";
            }

            return e.Type switch
            {
                "IFCMATERIAL" => e.Arg(0).Str,
                "IFCMATERIALLIST" => string.Join(", ", e.Arg(0).Refs.Select(r => MaterialName(r, depth + 1)).Where(s => s.Length > 0)),
                "IFCMATERIALLAYERSETUSAGE" or "IFCMATERIALPROFILESETUSAGE" => MaterialName(e.Arg(0).ToRef, depth + 1),
                "IFCMATERIALLAYERSET" or "IFCMATERIALPROFILESET" => string.Join(", ", e.Arg(e.Type == "IFCMATERIALLAYERSET" ? 0 : 2).Refs.Select(r => MaterialName(r, depth + 1)).Where(s => s.Length > 0)),
                "IFCMATERIALLAYER" => MaterialName(e.Arg(0).ToRef, depth + 1),
                "IFCMATERIALPROFILE" => MaterialName(e.Arg(2).ToRef, depth + 1),
                "IFCMATERIALCONSTITUENTSET" => string.Join(", ", e.Arg(2).Refs.Select(r => MaterialName(r, depth + 1)).Where(s => s.Length > 0)),
                "IFCMATERIALCONSTITUENT" => MaterialName(e.Arg(2).ToRef, depth + 1),
                _ => "",
            };
        }

        // ---- styles ----------------------------------------------------------------------------------------------

        private void ReadStyles()
        {
            foreach (var styled in _f.OfType("IFCSTYLEDITEM"))
            {
                if (!styled.Arg(0).IsRef)
                {
                    continue;
                }

                foreach (var style in styled.Arg(1).Items)
                {
                    if (ColourOf(style, 0) is { } colour)
                    {
                        _geo.ItemMaterials[styled.Arg(0).Ref] = Material(colour.R, colour.G, colour.B, colour.A, colour.Name);
                        break;
                    }
                }
            }
        }

        private (float R, float G, float B, float A, string? Name)? ColourOf(StepArg arg, int depth)
        {
            var e = _f.Get(arg);
            if (e is null || depth > 5)
            {
                return null;
            }

            switch (e.Type)
            {
                case "IFCPRESENTATIONSTYLEASSIGNMENT":
                    foreach (var s in e.Arg(0).Items)
                    {
                        if (ColourOf(s, depth + 1) is { } c)
                        {
                            return c;
                        }
                    }

                    return null;
                case "IFCSURFACESTYLE":
                    foreach (var s in e.Arg(2).Items)
                    {
                        if (ColourOf(s, depth + 1) is { } c)
                        {
                            return (c.R, c.G, c.B, c.A, e.Arg(0).Str is { Length: > 0 } name ? name : null);
                        }
                    }

                    return null;
                case "IFCSURFACESTYLERENDERING":
                case "IFCSURFACESTYLESHADING":
                    var rgb = _f.Get(e.Arg(0));
                    if (rgb is null || rgb.Type != "IFCCOLOURRGB")
                    {
                        return null;
                    }

                    var alpha = e.Type == "IFCSURFACESTYLERENDERING" || e.Args.Count > 1 ? 1 - (e.Arg(1).Type == StepArg.Kind.Number || e.Arg(1).Type == StepArg.Kind.Typed ? (float)e.Arg(1).Num : 0) : 1;
                    return (Clamp((float)rgb.Arg(1).Num), Clamp((float)rgb.Arg(2).Num), Clamp((float)rgb.Arg(3).Num), Clamp(alpha), null);
                default:
                    return null;
            }
        }

        private static float Clamp(float v) => float.IsFinite(v) ? Math.Clamp(v, 0f, 1f) : 0f;

        private int Material(float r, float g, float b, float a, string? name)
        {
            var key = ((int)(r * 255), (int)(g * 255), (int)(b * 255), (int)(a * 255));
            if (!_materialIndex.TryGetValue(key, out var index))
            {
                index = _model.Materials.Count;
                _materialIndex[key] = index;
                _model.Materials.Add(new ModelMaterial { Name = name ?? $"#{key.Item1:X2}{key.Item2:X2}{key.Item3:X2}", BaseColor = new Vector4(r, g, b, a) });
            }

            return index;
        }

        // ---- the tree --------------------------------------------------------------------------------------------

        private bool IsProductCandidate(StepEntity e)
        {
            if (!e.Type.StartsWith("IFC", StringComparison.Ordinal) || e.Type.Length < 8)
            {
                return false;
            }

            foreach (var prefix in NotProducts)
            {
                if (e.Type.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return e.Args.Count >= 7 && _f.Get(e.Arg(5)) is { Type: "IFCLOCALPLACEMENT" };
        }

        private void BuildTree(StepEntity project)
        {
            var orphans = new List<int>();
            foreach (var e in _f.InOrder)
            {
                if (e.Id != project.Id && !_hasParent.Contains(e.Id) && IsProductCandidate(e))
                {
                    orphans.Add(e.Id);
                }
            }

            var visited = new HashSet<int>();
            var stack = new Stack<(int Id, SceneNode? Parent, int ParentPlacement)>();
            stack.Push((project.Id, null, -1));
            var count = 0;
            var queuedOrphans = false;
            while (true)
            {
                if (stack.Count == 0)
                {
                    if (queuedOrphans || orphans.Count == 0)
                    {
                        break;
                    }

                    queuedOrphans = true;
                    foreach (var id in Enumerable.Reverse(orphans))
                    {
                        stack.Push((id, _model.Roots.FirstOrDefault(), -1));
                    }

                    continue;
                }

                var (entityId, parent, parentPlacement) = stack.Pop();
                var e = _f.Get(entityId);
                if (e is null || !visited.Add(entityId))
                {
                    continue;
                }

                if (++count % 256 == 0)
                {
                    _options.Cancellation.ThrowIfCancellationRequested();
                }

                var node = MakeNode(e, parentPlacement, out var placement);
                if (parent is null)
                {
                    _model.Roots.Add(node);
                }
                else
                {
                    parent.Children.Add(node);
                }

                if (_children.TryGetValue(entityId, out var kids))
                {
                    for (var i = kids.Count - 1; i >= 0; i--)
                    {
                        stack.Push((kids[i], node, placement));
                    }
                }
            }
        }

        private SceneNode MakeNode(StepEntity e, int parentPlacement, out int placement)
        {
            var type = e.Type;
            var name = e.Arg(2).Str;
            var node = new SceneNode(name.Length > 0 ? name : null);
            placement = parentPlacement;
            _typeCounts[type] = _typeCounts.GetValueOrDefault(type) + 1;
            if (e.Arg(0).Type == StepArg.Kind.String && e.Arg(0).Str.Length > 0)
            {
                node.Properties["GlobalId"] = e.Arg(0).Str;
            }

            node.Properties["IfcType"] = type;
            AddIfText(node, "Description", e.Arg(3));
            AddIfText(node, "ObjectType", e.Arg(4));
            if (type == "IFCPROJECT" || type == "IFCSITE" || type == "IFCBUILDING" || type == "IFCBUILDINGSTOREY" || type == "IFCSPACE")
            {
                AddIfText(node, "LongName", e.Arg(type == "IFCPROJECT" ? 5 : 7));
                if (type == "IFCPROJECT")
                {
                    AddIfText(node, "Phase", e.Arg(6));
                }
            }
            else
            {
                AddIfText(node, "Tag", e.Arg(7));
            }

            if (type == "IFCBUILDINGSTOREY" && e.Arg(9).Type == StepArg.Kind.Number)
            {
                node.Properties["Elevation"] = Number(e.Arg(9).Num);
            }

            var meta = _model.Metadata;
            if (type == "IFCPROJECT")
            {
                if (name.Length > 0)
                {
                    meta.Title = name;
                }

                if (e.Arg(5).Str.Length > 0)
                {
                    meta.Properties.Add(new ModelProperty("long name", e.Arg(5).Str, "ifc"));
                }

                if (e.Arg(3).Str.Length > 0)
                {
                    meta.Properties.Add(new ModelProperty("project description", e.Arg(3).Str, "ifc"));
                }
            }

            if (type is "IFCSITE" or "IFCBUILDING" or "IFCBUILDINGSTOREY")
            {
                meta.Features.Add(new ModelFeature(name.Length > 0 ? name : type, type));
            }

            node.Name ??= type;
            if (_typeOf.TryGetValue(e.Id, out var typeId) && _f.Get(typeId) is { } typeEntity && typeEntity.Arg(2).Str.Length > 0)
            {
                node.Properties["TypeName"] = typeEntity.Arg(2).Str;
            }

            if (_materialsOf.TryGetValue(e.Id, out var materials))
            {
                var names = materials.Select(m => MaterialName(m)).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToList();
                if (names.Count > 0)
                {
                    node.Properties["Material"] = string.Join(", ", names);
                }
            }

            if (_definitions.TryGetValue(e.Id, out var definitions))
            {
                foreach (var d in definitions.Take(200))
                {
                    AddPropertySet(node, d);
                }
            }

            // Placement relative to the tree parent.
            var placementId = type == "IFCPROJECT" ? -1 : e.Arg(5).ToRef;
            if (placementId >= 0 && _geo.Relative(placementId) is { } relative)
            {
                placement = placementId;
                if (_geo.RelativeTo(placementId) == parentPlacement)
                {
                    node.Transform = relative;
                }
                else
                {
                    var world = _geo.World(placementId);
                    var parentWorld = parentPlacement >= 0 ? _geo.World(parentPlacement) : Matrix4x4.Identity;
                    node.Transform = Matrix4x4.Invert(parentWorld, out var inverse) ? world * inverse : world;
                }
            }

            if (_geometry && e.Args.Count > 6 && e.Arg(6).IsRef && _f.Get(e.Arg(6)) is { Type: "IFCPRODUCTDEFINITIONSHAPE" } shape)
            {
                AddGeometry(node, e, shape);
            }

            return node;
        }

        private void AddGeometry(SceneNode node, StepEntity product, StepEntity shape)
        {
            if (product.Type is "IFCOPENINGELEMENT" or "IFCSPACE")
            {
                return;
            }

            var reps = shape.Arg(2).Items.Select(r => _f.Get(r)).Where(r => r is not null).Cast<StepEntity>().ToList();
            var bodies = reps.Where(r => r.Arg(1).Str.Equals("Body", StringComparison.OrdinalIgnoreCase)).ToList();
            if (bodies.Count == 0)
            {
                bodies = reps.Where(r => !SkippedRepresentations.Contains(r.Arg(1).Str, StringComparer.OrdinalIgnoreCase)).ToList();
            }

            var acc = new MeshAccumulator(Math.Max(1, _options.MaxTriangles - _triangles));
            _geo.Runs.Clear();
            var attempted = 0;
            var failed = new List<string>();
            foreach (var rep in bodies)
            {
                foreach (var item in rep.Arg(3).Items.Where(i => i.IsRef))
                {
                    attempted++;
                    var before = _geo.Unsupported.Sum(p => p.Value);
                    if (!_geo.AddItem(item.Ref, Matrix4x4.Identity, acc, 0) && _f.Get(item) is { } entity && _geo.Unsupported.Sum(p => p.Value) > before)
                    {
                        failed.Add(entity.Type);
                    }
                }
            }

            var mesh = acc.ToMesh(node.Name);
            if (mesh is null)
            {
                if (attempted > 0)
                {
                    node.Properties["Geometry"] = "unsupported: " + string.Join(", ", failed.Distinct());
                }

                _productsWithoutGeometry++;
                return;
            }

            _triangles += acc.TriangleCount;
            var groups = new List<MeshGroup>();
            foreach (var (start, count, material) in _geo.Runs.Where(r => r.Count > 0))
            {
                if (groups.Count > 0 && groups[^1].Material == material && groups[^1].Start + groups[^1].Count == start)
                {
                    groups[^1] = groups[^1] with { Count = groups[^1].Count + count };
                }
                else
                {
                    groups.Add(new MeshGroup(start, count, material));
                }
            }

            if (groups.Any(g => g.Material >= 0))
            {
                mesh.Groups.AddRange(groups);
            }

            if (failed.Count > 0)
            {
                node.Properties["Geometry"] = "partly unsupported: " + string.Join(", ", failed.Distinct());
            }

            _model.Meshes.Add(mesh);
            node.Mesh = mesh;
        }

        // ---- properties ------------------------------------------------------------------------------------------

        private static void AddIfText(SceneNode node, string key, StepArg arg)
        {
            if (arg.Type == StepArg.Kind.String && arg.Str.Length > 0)
            {
                node.Properties[key] = arg.Str;
            }
        }

        private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

        private static string Format(StepArg arg, int depth = 0)
        {
            switch (arg.Type)
            {
                case StepArg.Kind.Typed:
                    return arg.Items.Count > 0 && depth < 4 ? Format(arg.Items[0], depth + 1) : "";
                case StepArg.Kind.Number:
                    return Number(arg.Number);
                case StepArg.Kind.String:
                    return arg.Text;
                case StepArg.Kind.Enum:
                    return arg.Text.Equals("T", StringComparison.OrdinalIgnoreCase) ? "true" : arg.Text.Equals("F", StringComparison.OrdinalIgnoreCase) ? "false" : arg.Text;
                case StepArg.Kind.List:
                    return string.Join(", ", arg.Items.Take(100).Select(i => Format(i, depth + 1)));
                default:
                    return "";
            }
        }

        private void AddPropertySet(SceneNode node, int definitionId)
        {
            var definition = _f.Get(definitionId);
            if (definition is null)
            {
                return;
            }

            if (definition.Type == "IFCPROPERTYSET")
            {
                var setName = definition.Arg(2).Str;
                foreach (var propertyRef in definition.Arg(4).Refs.Take(500))
                {
                    var property = _f.Get(propertyRef);
                    if (property is null || property.Arg(0).Str.Length == 0)
                    {
                        continue;
                    }

                    var value = property.Type switch
                    {
                        "IFCPROPERTYSINGLEVALUE" => Format(property.Arg(2)),
                        "IFCPROPERTYENUMERATEDVALUE" => Format(property.Arg(2)),
                        "IFCPROPERTYLISTVALUE" => Format(property.Arg(2)),
                        "IFCPROPERTYBOUNDEDVALUE" => $"{Format(property.Arg(3))}..{Format(property.Arg(2))}",
                        _ => null,
                    };
                    if (value is not null && node.Properties.Count < 2000)
                    {
                        node.Properties[(setName.Length > 0 ? setName + "." : "") + property.Arg(0).Str] = value;
                    }
                }
            }
            else if (definition.Type == "IFCELEMENTQUANTITY")
            {
                var setName = definition.Arg(2).Str;
                foreach (var quantityRef in definition.Arg(5).Refs.Take(500))
                {
                    var quantity = _f.Get(quantityRef);
                    if (quantity is null || !quantity.Type.StartsWith("IFCQUANTITY", StringComparison.Ordinal) || quantity.Arg(0).Str.Length == 0)
                    {
                        continue;
                    }

                    if (node.Properties.Count < 2000)
                    {
                        node.Properties[(setName.Length > 0 ? setName + "." : "") + quantity.Arg(0).Str] = Format(quantity.Arg(3));
                    }
                }
            }
        }
    }
}
