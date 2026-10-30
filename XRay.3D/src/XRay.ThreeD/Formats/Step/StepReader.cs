using System.Globalization;
using System.Numerics;
using System.Text;

namespace XRay.ThreeD.Formats.Step;

/// <summary>
/// STEP (ISO 10303-21, application protocols 203, 214 and 242): header metadata, units, the product structure (products, assemblies and
/// the placements of their components), shape representations as B-rep solids (planes, cylinders, cones, spheres, tori, B-spline,
/// revolution and extrusion surfaces; faceted B-reps), colours, layers and product properties. Geometry is tessellated with
/// <see cref="LoadOptions.LinearDeflection"/> and <see cref="LoadOptions.AngularDeflection"/>.
/// </summary>
internal static class StepReader
{
    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var text = Encoding.Latin1.GetString(data.Span);
        var file = StepFile.Parse(text, options);
        Trace("parse", clock);
        var model = new Builder(file, options).Build();
        Trace("build", clock);
        return model;
    }

    private static void Trace(string phase, System.Diagnostics.Stopwatch clock)
    {
        if (Environment.GetEnvironmentVariable("XRAY3D_TRACE") is { Length: > 0 })
        {
            Console.Error.WriteLine($"step {phase}: {clock.ElapsedMilliseconds} ms");
        }
    }

    public static ModelMetadata ReadMetadata(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var text = Encoding.Latin1.GetString(data.Span);
        var file = StepFile.Parse(text, options);
        var builder = new Builder(file, options);
        return builder.BuildMetadataOnly();
    }

    private sealed class Product
    {
        public int DefinitionId;
        public string? Id;
        public string? Name;
        public string? Description;
        public string? Version;
        public readonly List<Usage> Children = new();
        public readonly List<ModelProperty> Properties = new();
        public int ShapeRep = -1;
    }

    private sealed record Usage(int Id, string? Name, int ChildDefinition, Matrix4x4 Transform);

    private sealed class Builder
    {
        private readonly StepFile _f;
        private readonly LoadOptions _options;
        private readonly StepEntities _geo;
        private readonly Model3D _model = new();
        private readonly Dictionary<int, Product> _products = new();
        private readonly Dictionary<int, (int Rgb, bool Has)> _colors = new();
        private readonly Dictionary<int, int> _layerOf = new();
        private readonly Dictionary<int, MeshData?> _repMeshes = new();
        private readonly Dictionary<int, int> _materialByRgb = new();
        private StepTessellator? _tessellator;
        private double _unitScale = 1;

        public Builder(StepFile file, LoadOptions options)
        {
            _f = file;
            _options = options;
            _geo = new StepEntities(file);
        }

        public ModelMetadata BuildMetadataOnly()
        {
            var meta = _model.Metadata;
            ReadHeader(meta);
            ReadUnits(meta);
            ReadProducts();
            foreach (var p in _products.Values)
            {
                if (!string.IsNullOrEmpty(p.Name) && !meta.PartNames.Contains(p.Name!))
                {
                    meta.PartNames.Add(p.Name!);
                }
            }

            ReadLayers();
            meta.HasGeometry = _f.OfType("MANIFOLD_SOLID_BREP").Count + _f.OfType("FACETED_BREP").Count + _f.OfType("BREP_WITH_VOIDS").Count > 0;
            return meta;
        }

        public Model3D Build()
        {
            var meta = _model.Metadata;
            ReadHeader(meta);
            ReadUnits(meta);
            ReadProducts();
            ReadColors();
            ReadLayers();

            var size = ModelSize();
            _tessellator = new StepTessellator(_f, _geo, Math.Max(size * _options.LinearDeflection, 1e-9), _options.AngularDeflection) { Cancel = _options.Cancellation };

            // Roots: product definitions no assembly uses as a component. Without any product structure, all solids form one part.
            var childIds = _products.Values.SelectMany(p => p.Children.Select(c => c.ChildDefinition)).ToHashSet();
            var rootProducts = _products.Values.Where(p => !childIds.Contains(p.DefinitionId)).ToList();
            var withGeometryOrChildren = rootProducts.Where(p => p.Children.Count > 0 || p.ShapeRep >= 0).ToList();
            foreach (var product in withGeometryOrChildren)
            {
                if (BuildProductNode(product, 0, new HashSet<int>()) is { } node)
                {
                    _model.Roots.Add(node);
                }
            }

            if (_model.Roots.Count == 0)
            {
                // A plain geometry file: every solid, no products.
                var mesh = BuildMeshForSolids(AllSolids(), "geometry");
                if (mesh is not null)
                {
                    _model.Meshes.Add(mesh);
                    _model.Roots.Add(new SceneNode(null) { Mesh = mesh });
                }
            }

            if (_model.Roots.Count == 0 || _model.Meshes.Count == 0)
            {
                throw new ModelFormatException("The STEP file holds no solid geometry this reader can tessellate.");
            }

            foreach (var warning in _tessellator.Warnings.Distinct().Take(20))
            {
                _model.Warnings.Add(warning);
            }

            if (_tessellator.Warnings.Count > 20)
            {
                _model.Warnings.Add($"{_tessellator.Warnings.Count - 20} more tessellation warnings were suppressed.");
            }

            meta.PartNames.Clear();
            meta.PartNames.AddRange(_model.Nodes().Where(n => n.Mesh is not null && !string.IsNullOrEmpty(n.Name)).Select(n => n.Name!).Distinct());
            meta.MaterialNames.AddRange(_model.Materials.Select(m => m.Name!).Where(n => !string.IsNullOrEmpty(n)));
            meta.HasGeometry = true;
            meta.Statistics = _model.ComputeStatistics();
            meta.Bounds = _model.ComputeBounds();
            return _model;
        }

        // ---- header, units ---------------------------------------------------------------------------------------

        private void ReadHeader(ModelMetadata meta)
        {
            meta.Format = ModelFormats.Step;
            meta.DocumentKind = "scene";
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

                var system = new[] { name[5].Str, name[4].Str }.Where(s => s.Length > 0);
                meta.Application = string.Join(" / ", system);
            }

            if (_f.Header.TryGetValue("FILE_DESCRIPTION", out var description) && description.Count > 0)
            {
                var text = string.Join(" ", description[0].Items.Select(i => i.Str).Where(s => s.Length > 0));
                if (text.Length > 0)
                {
                    meta.Properties.Add(new ModelProperty("description", text, "step"));
                }
            }

            if (_f.Header.TryGetValue("FILE_SCHEMA", out var schema) && schema.Count > 0)
            {
                var names = schema[0].Items.Select(i => i.Str).Where(s => s.Length > 0).ToList();
                meta.FormatVersion = string.Join(", ", names);
                var upper = string.Join(" ", names).ToUpperInvariant();
                meta.Properties.Add(new ModelProperty("application protocol", upper.Contains("AP242") || upper.Contains("MANAGED_MODEL_BASED") ? "AP242" : upper.Contains("AUTOMOTIVE_DESIGN") ? "AP214" : upper.Contains("CONFIG_CONTROL") || upper.Contains("AP203") ? "AP203" : names.FirstOrDefault() ?? "?", "step"));
            }
        }

        private void ReadUnits(ModelMetadata meta)
        {
            meta.UpAxis = UpAxis.Z;
            foreach (var context in _f.OfType("GLOBAL_UNIT_ASSIGNED_CONTEXT"))
            {
                var units = context.ArgsOf("GLOBAL_UNIT_ASSIGNED_CONTEXT").FirstOrDefault()?.Items ?? [];
                foreach (var unitRef in units.Where(u => u.IsRef))
                {
                    var unit = _f.Get(unitRef);
                    if (unit is null || !unit.Is("LENGTH_UNIT"))
                    {
                        continue;
                    }

                    var (lengthUnit, scale) = LengthOf(unit);
                    meta.Units = lengthUnit;
                    _unitScale = scale;
                    return;
                }
            }

            meta.Units = LengthUnit.Millimeter;
            meta.Notes.Add("The file states no length unit; millimeters are assumed.");
        }

        private (LengthUnit? Unit, double Scale) LengthOf(StepEntity unit)
        {
            if (unit.Is("SI_UNIT"))
            {
                var args = unit.ArgsOf("SI_UNIT");
                var prefix = args.Count > 0 ? args[0].Str : "";
                return prefix.ToUpperInvariant() switch
                {
                    "MILLI" => (LengthUnit.Millimeter, 0.001),
                    "CENTI" => (LengthUnit.Centimeter, 0.01),
                    "KILO" => (LengthUnit.Kilometer, 1000),
                    "MICRO" => (LengthUnit.Micrometer, 1e-6),
                    "DECI" => (LengthUnit.Decimeter, 0.1),
                    "NANO" => (LengthUnit.Nanometer, 1e-9),
                    _ => (LengthUnit.Meter, 1),
                };
            }

            if (unit.Is("CONVERSION_BASED_UNIT"))
            {
                var name = unit.ArgsOf("CONVERSION_BASED_UNIT").FirstOrDefault()?.Str.ToUpperInvariant() ?? "";
                return name switch
                {
                    "INCH" or "IN" => (LengthUnit.Inch, 0.0254),
                    "FOOT" or "FT" => (LengthUnit.Foot, 0.3048),
                    "YARD" => (LengthUnit.Yard, 0.9144),
                    "MILE" => (LengthUnit.Mile, 1609.344),
                    _ => (null, 1),
                };
            }

            return (null, 1);
        }

        // ---- products --------------------------------------------------------------------------------------------

        private void ReadProducts()
        {
            var productById = new Dictionary<int, StepEntity>();
            foreach (var p in _f.OfType("PRODUCT"))
            {
                productById[p.Id] = p;
            }

            var formation = new Dictionary<int, StepEntity>();
            foreach (var pf in _f.OfType("PRODUCT_DEFINITION_FORMATION").Concat(_f.OfType("PRODUCT_DEFINITION_FORMATION_WITH_SPECIFIED_SOURCE")))
            {
                formation[pf.Id] = pf;
            }

            foreach (var pd in _f.OfType("PRODUCT_DEFINITION"))
            {
                var product = new Product { DefinitionId = pd.Id };
                if (formation.TryGetValue(pd.Arg(2).ToRef, out var pf))
                {
                    product.Version = pf.Arg(0).Str;
                    if (productById.TryGetValue(pf.Arg(2).ToRef, out var prod))
                    {
                        product.Id = prod.Arg(0).Str;
                        product.Name = prod.Arg(1).Str is { Length: > 0 } n ? n : prod.Arg(0).Str;
                        product.Description = prod.Arg(2).Str;
                    }
                }

                _products[pd.Id] = product;
            }

            // Shape representations of product definitions: PRODUCT_DEFINITION_SHAPE -> SHAPE_DEFINITION_REPRESENTATION.
            var shapeOwner = new Dictionary<int, int>(); // product_definition_shape id -> definition id (a product definition or a usage)
            foreach (var pds in _f.OfType("PRODUCT_DEFINITION_SHAPE"))
            {
                shapeOwner[pds.Id] = pds.Arg(2).ToRef;
            }

            var repOf = new Dictionary<int, int>();
            foreach (var sdr in _f.OfType("SHAPE_DEFINITION_REPRESENTATION"))
            {
                if (shapeOwner.TryGetValue(sdr.Arg(0).ToRef, out var owner) && _products.TryGetValue(owner, out var product))
                {
                    var rep = sdr.Arg(1).ToRef;
                    if (product.ShapeRep < 0 || RepHasSolids(rep) && !RepHasSolids(product.ShapeRep))
                    {
                        product.ShapeRep = rep;
                    }
                }

                repOf[sdr.Arg(1).ToRef] = sdr.Arg(0).ToRef;
            }

            // Representations tied together by SHAPE_REPRESENTATION_RELATIONSHIP stand for the same shape.
            var related = new Dictionary<int, int>();
            foreach (var rel in _f.OfType("SHAPE_REPRESENTATION_RELATIONSHIP"))
            {
                if (!rel.Is("REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION"))
                {
                    var args = rel.IsComplex ? rel.ArgsOf("REPRESENTATION_RELATIONSHIP") : rel.Args;
                    var o = rel.IsComplex ? 0 : 0;
                    if (args.Count >= 4 + o - (rel.IsComplex ? 0 : 0))
                    {
                        related[args[2].ToRef] = args[3].ToRef;
                        related[args[3].ToRef] = args[2].ToRef;
                    }
                }
            }

            foreach (var product in _products.Values.Where(p => p.ShapeRep >= 0 && !RepHasSolids(p.ShapeRep)))
            {
                if (related.TryGetValue(product.ShapeRep, out var other) && RepHasSolids(other))
                {
                    product.ShapeRep = other;
                }
            }

            // Assembly structure.
            var usageShapes = new Dictionary<int, int>(); // usage id -> product_definition_shape id
            foreach (var pds in _f.OfType("PRODUCT_DEFINITION_SHAPE"))
            {
                usageShapes[pds.Arg(2).ToRef] = pds.Id;
            }

            var transformOfPds = new Dictionary<int, (int Rep1, int Rep2, Matrix4x4 Transform)>();
            foreach (var cdsr in _f.OfType("CONTEXT_DEPENDENT_SHAPE_REPRESENTATION"))
            {
                var relation = _f.Get(cdsr.Arg(0).ToRef);
                if (relation is null)
                {
                    continue;
                }

                var relationArgs = relation.IsComplex ? relation.ArgsOf("REPRESENTATION_RELATIONSHIP") : relation.Args;
                if (relationArgs.Count < 4)
                {
                    continue;
                }

                var rep1 = relationArgs[2].ToRef;
                var rep2 = relationArgs[3].ToRef;
                var transformation = relation.IsComplex ? relation.ArgsOf("REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION").FirstOrDefault() : (relation.Type == "REPRESENTATION_RELATIONSHIP_WITH_TRANSFORMATION" ? relation.Arg(4) : null);
                var matrix = Matrix4x4.Identity;
                if (transformation is not null && _f.Get(transformation) is { Type: "ITEM_DEFINED_TRANSFORMATION" } idt)
                {
                    var a1 = _geo.Axis2(idt.Arg(2).ToRef);
                    var a2 = _geo.Axis2(idt.Arg(3).ToRef);
                    // Points in the first representation's frame map to the second's: undo the first placement, apply the second.
                    Matrix4x4.Invert(a1.ToMatrix(), out var inverse);
                    matrix = inverse * a2.ToMatrix();
                }

                transformOfPds[cdsr.Arg(1).ToRef] = (rep1, rep2, matrix);
            }

            foreach (var nauo in _f.OfType("NEXT_ASSEMBLY_USAGE_OCCURRENCE").Concat(_f.OfType("PRODUCT_DEFINITION_USAGE")).Concat(_f.OfType("ASSEMBLY_COMPONENT_USAGE")))
            {
                var parentId = nauo.Arg(3).ToRef;
                var childId = nauo.Arg(4).ToRef;
                if (!_products.TryGetValue(parentId, out var parent) || !_products.TryGetValue(childId, out var child))
                {
                    continue;
                }

                var matrix = Matrix4x4.Identity;
                if (usageShapes.TryGetValue(nauo.Id, out var pds) && transformOfPds.TryGetValue(pds, out var t))
                {
                    // Which representation belongs to the child decides the direction of the transformation.
                    matrix = t.Rep1 == child.ShapeRep || related.GetValueOrDefault(t.Rep1) == child.ShapeRep || t.Rep2 == parent.ShapeRep && t.Rep1 != parent.ShapeRep
                        ? t.Transform
                        : (Matrix4x4.Invert(t.Transform, out var inverse) ? inverse : t.Transform);
                }

                var name = nauo.Arg(1).Str is { Length: > 0 } nn ? nn : child.Name;
                parent.Children.Add(new Usage(nauo.Id, name, childId, matrix));
            }

            // Properties.
            var definitions = new Dictionary<int, StepEntity>();
            foreach (var pd in _f.OfType("PROPERTY_DEFINITION"))
            {
                definitions[pd.Id] = pd;
            }

            foreach (var pdr in _f.OfType("PROPERTY_DEFINITION_REPRESENTATION"))
            {
                if (!definitions.TryGetValue(pdr.Arg(0).ToRef, out var pd) || _f.Get(pdr.Arg(1).ToRef) is not { } rep)
                {
                    continue;
                }

                var target = pd.Arg(2).ToRef;
                var definitionId = shapeOwner.TryGetValue(target, out var owner) ? owner : target;
                var list = _products.TryGetValue(definitionId, out var product) ? product.Properties : _model.Metadata.Properties;
                foreach (var item in rep.Arg(1).Refs.Select(r => _f.Get(r)).Where(e => e is not null))
                {
                    var value = item!.Type switch
                    {
                        "DESCRIPTIVE_REPRESENTATION_ITEM" => item.Arg(1).Str,
                        "MEASURE_REPRESENTATION_ITEM" or "VALUE_REPRESENTATION_ITEM" => item.Arg(1).Type == StepArg.Kind.Typed ? item.Arg(1).Num.ToString("R", CultureInfo.InvariantCulture) + " " + item.Arg(1).Text.ToLowerInvariant().Replace("_measure", "", StringComparison.Ordinal) : item.Arg(1).Num.ToString("R", CultureInfo.InvariantCulture),
                        _ => null,
                    };
                    if (value is { Length: > 0 })
                    {
                        var label = (item.Arg(0).Str is { Length: > 0 } itemName ? itemName : pd.Arg(0).Str).Replace(" measure", string.Empty, StringComparison.OrdinalIgnoreCase);
                        var group = pd.Arg(0).Str;
                        // "material property" holding an item "Steel" with value "Steel" is the property "material" = "Steel".
                        var propertyName = label == value || label.Length == 0 ? group : group.Length > 0 && group != label && !group.Equals("material property", StringComparison.OrdinalIgnoreCase) ? group + ": " + label : label;
                        list.Add(new ModelProperty(propertyName.Replace(" property", string.Empty, StringComparison.OrdinalIgnoreCase), value, "step"));
                    }
                }
            }
        }

        private readonly Dictionary<int, bool> _hasSolids = new();

        private bool RepHasSolids(int rep)
        {
            if (_hasSolids.TryGetValue(rep, out var cached))
            {
                return cached;
            }

            var e = _f.Get(rep);
            var result = e is not null && e.Arg(1).Refs.Any(r => _f.Get(r) is { } item && IsSolid(item));
            _hasSolids[rep] = result;
            return result;
        }

        private static bool IsSolid(StepEntity e) => e.Type is "MANIFOLD_SOLID_BREP" or "BREP_WITH_VOIDS" or "FACETED_BREP" or "SHELL_BASED_SURFACE_MODEL" or "MANIFOLD_SURFACE_SHAPE_REPRESENTATION" or "MAPPED_ITEM"
            || e.Is("MANIFOLD_SOLID_BREP") || e.Is("FACETED_BREP");

        // ---- colours and layers ----------------------------------------------------------------------------------

        private void ReadColors()
        {
            foreach (var styled in _f.OfType("STYLED_ITEM").Concat(_f.OfType("OVER_RIDING_STYLED_ITEM")))
            {
                var item = styled.Arg(2).ToRef;
                if (item < 0 || _colors.ContainsKey(item))
                {
                    continue;
                }

                foreach (var style in styled.Arg(1).Refs)
                {
                    if (ColorOfStyle(style) is { } rgb)
                    {
                        _colors[item] = (rgb, true);
                        break;
                    }
                }
            }
        }

        private int? ColorOfStyle(int styleRef)
        {
            // PRESENTATION_STYLE_ASSIGNMENT -> SURFACE_STYLE_USAGE -> SURFACE_SIDE_STYLE -> SURFACE_STYLE_FILL_AREA -> FILL_AREA_STYLE
            // -> FILL_AREA_STYLE_COLOUR -> COLOUR_RGB. Each level lists its children; take the first colour found.
            var seen = new HashSet<int>();
            var stack = new Stack<int>();
            stack.Push(styleRef);
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (!seen.Add(id) || _f.Get(id) is not { } e)
                {
                    continue;
                }

                if (e.Type == "COLOUR_RGB")
                {
                    return ToRgb(e.Arg(1).Num, e.Arg(2).Num, e.Arg(3).Num);
                }

                if (e.Type == "DRAUGHTING_PRE_DEFINED_COLOUR")
                {
                    return e.Arg(0).Str.ToLowerInvariant() switch
                    {
                        "red" => 0xFF0000,
                        "green" => 0x00FF00,
                        "blue" => 0x0000FF,
                        "yellow" => 0xFFFF00,
                        "cyan" => 0x00FFFF,
                        "magenta" => 0xFF00FF,
                        "black" => 0,
                        "white" => 0xFFFFFF,
                        _ => 0x808080,
                    };
                }

                // Skip curve/point styles; follow everything else that leads to a surface fill colour.
                if (e.Type is "CURVE_STYLE" or "POINT_STYLE")
                {
                    continue;
                }

                foreach (var arg in e.Args)
                {
                    foreach (var r in arg.Type == StepArg.Kind.Ref ? [arg.Ref] : arg.Items.Where(i => i.IsRef).Select(i => i.Ref))
                    {
                        stack.Push(r);
                    }
                }
            }

            return null;
        }

        private static int ToRgb(double r, double g, double b) =>
            ((int)Math.Round(Math.Clamp(r, 0, 1) * 255) << 16) | ((int)Math.Round(Math.Clamp(g, 0, 1) * 255) << 8) | (int)Math.Round(Math.Clamp(b, 0, 1) * 255);

        private void ReadLayers()
        {
            foreach (var layer in _f.OfType("PRESENTATION_LAYER_ASSIGNMENT"))
            {
                var name = layer.Arg(0).Str;
                if (name.Length == 0)
                {
                    continue;
                }

                var index = _model.Layers.FindIndex(l => l.Name == name);
                if (index < 0)
                {
                    index = _model.Layers.Count;
                    _model.Layers.Add(new ModelLayer(name));
                    _model.Metadata.LayerNames.Add(name);
                }

                foreach (var item in layer.Arg(2).Refs)
                {
                    _layerOf[item] = index;
                }
            }
        }

        // ---- scene -----------------------------------------------------------------------------------------------

        private double ModelSize()
        {
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            var count = 0;
            foreach (var point in _f.OfType("CARTESIAN_POINT"))
            {
                var c = point.Arg(1).Items;
                if (c.Count < 3)
                {
                    continue;
                }

                minX = Math.Min(minX, c[0].Num);
                maxX = Math.Max(maxX, c[0].Num);
                minY = Math.Min(minY, c[1].Num);
                maxY = Math.Max(maxY, c[1].Num);
                minZ = Math.Min(minZ, c[2].Num);
                maxZ = Math.Max(maxZ, c[2].Num);
                if (++count > 2_000_000)
                {
                    break;
                }
            }

            var size = count == 0 ? 1 : Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY) + (maxZ - minZ) * (maxZ - minZ));
            return Math.Max(size, 1e-6);
        }

        private SceneNode? BuildProductNode(Product product, int depth, HashSet<int> path)
        {
            if (depth > 64 || !path.Add(product.DefinitionId))
            {
                return null;
            }

            try
            {
                _options.Cancellation.ThrowIfCancellationRequested();
                var node = new SceneNode(product.Name);
                if (product.Id is { Length: > 0 } id)
                {
                    node.Properties["productId"] = id;
                }

                if (product.Description is { Length: > 0 } description)
                {
                    node.Properties["description"] = description;
                }

                if (product.Version is { Length: > 0 } version)
                {
                    node.Properties["version"] = version;
                }

                foreach (var p in product.Properties)
                {
                    node.Properties[p.Name] = p.Value;
                }

                if (product.ShapeRep >= 0)
                {
                    var mesh = MeshOfRepresentation(product.ShapeRep, product.Name);
                    if (mesh is not null)
                    {
                        node.Mesh = mesh;
                    }
                }

                foreach (var usage in product.Children)
                {
                    if (_products.TryGetValue(usage.ChildDefinition, out var child) && BuildProductNode(child, depth + 1, path) is { } childNode)
                    {
                        childNode.Transform = usage.Transform * childNode.Transform;
                        if (!string.IsNullOrEmpty(usage.Name))
                        {
                            childNode.Properties["usage"] = usage.Name!;
                        }

                        node.Children.Add(childNode);
                    }
                }

                return node.Mesh is null && node.Children.Count == 0 ? null : node;
            }
            finally
            {
                path.Remove(product.DefinitionId);
            }
        }

        private MeshData? MeshOfRepresentation(int rep, string? name)
        {
            if (_repMeshes.TryGetValue(rep, out var cached))
            {
                return cached;
            }

            var solids = new List<StepEntity>();
            if (_f.Get(rep) is { } representation)
            {
                foreach (var item in representation.Arg(1).Refs.Select(r => _f.Get(r)).Where(e => e is not null && IsSolid(e!) && e!.Type != "MAPPED_ITEM"))
                {
                    solids.Add(item!);
                }
            }

            var mesh = BuildMeshForSolids(solids, name);
            if (mesh is not null)
            {
                _model.Meshes.Add(mesh);
            }

            _repMeshes[rep] = mesh;
            return mesh;
        }

        private IEnumerable<StepEntity> AllSolids() =>
            _f.OfType("MANIFOLD_SOLID_BREP").Concat(_f.OfType("BREP_WITH_VOIDS")).Concat(_f.OfType("FACETED_BREP")).Concat(_f.OfType("SHELL_BASED_SURFACE_MODEL")).Distinct();

        private MeshData? BuildMeshForSolids(IEnumerable<StepEntity> solids, string? name)
        {
            var positions = new List<float>();
            var normals = new List<float>();
            var indices = new List<uint>();
            var faces = new List<MeshFace>();
            // Per face colour -> material, with index runs sorted by material at the end.
            var faceData = new List<(int Start, int Count, int Material)>();
            var scale = (float)(1.0);
            foreach (var solid in solids)
            {
                var solidColor = _colors.TryGetValue(solid.Id, out var sc) ? sc.Rgb : -1;
                foreach (var face in FacesOf(solid))
                {
                    _options.Cancellation.ThrowIfCancellationRequested();
                    var mesh = _tessellator!.TessellateFace(face);
                    if (mesh is null)
                    {
                        continue;
                    }

                    var rgb = _colors.TryGetValue(face.Id, out var fc) ? fc.Rgb : solidColor;
                    var material = rgb >= 0 ? MaterialFor(rgb) : -1;
                    var vertexBase = positions.Count / 3;
                    foreach (var p in mesh.Positions)
                    {
                        positions.Add((float)p.X * scale);
                        positions.Add((float)p.Y * scale);
                        positions.Add((float)p.Z * scale);
                    }

                    foreach (var n in mesh.Normals)
                    {
                        normals.Add((float)n.X);
                        normals.Add((float)n.Y);
                        normals.Add((float)n.Z);
                    }

                    var start = indices.Count;
                    foreach (var i in mesh.Indices)
                    {
                        indices.Add((uint)(vertexBase + i));
                    }

                    faceData.Add((start, indices.Count - start, material));
                    if (indices.Count / 3 > _options.MaxTriangles)
                    {
                        throw new ModelFormatException("The STEP file produces more triangles than the configured limit.");
                    }
                }
            }

            if (indices.Count == 0)
            {
                return null;
            }

            // Order faces by material so that each material is one contiguous range; keep each face's own range.
            var order = faceData.Select((d, i) => (d, i)).OrderBy(x => x.d.Material).ThenBy(x => x.i).ToList();
            var sorted = new uint[indices.Count];
            var meshFaces = new MeshFace[faceData.Count];
            var cursor = 0;
            var mesh2 = new MeshData { Name = name, Positions = positions.ToArray(), Normals = normals.ToArray() };
            var groupStart = 0;
            for (var k = 0; k < order.Count; k++)
            {
                var (d, original) = order[k];
                for (var t = 0; t < d.Count; t++)
                {
                    sorted[cursor + t] = indices[d.Start + t];
                }

                meshFaces[original] = new MeshFace(cursor, d.Count);
                cursor += d.Count;
                if (k + 1 == order.Count || order[k + 1].d.Material != d.Material)
                {
                    mesh2.Groups.Add(new MeshGroup(groupStart, cursor - groupStart, d.Material));
                    groupStart = cursor;
                }
            }

            mesh2.Indices = sorted;
            mesh2.Faces = meshFaces;
            if (mesh2.Groups.Count == 1 && mesh2.Groups[0].Material < 0)
            {
                mesh2.Groups.Clear();
            }

            return mesh2;
        }

        private int MaterialFor(int rgb)
        {
            if (!_materialByRgb.TryGetValue(rgb, out var index))
            {
                index = _model.Materials.Count;
                _materialByRgb[rgb] = index;
                _model.Materials.Add(new ModelMaterial
                {
                    Name = "#" + rgb.ToString("X6", CultureInfo.InvariantCulture),
                    BaseColor = new Vector4(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1f),
                });
            }

            return index;
        }

        /// <summary>The ADVANCED_FACE / FACE_SURFACE / FACE entities of a solid's shells.</summary>
        private IEnumerable<StepEntity> FacesOf(StepEntity solid)
        {
            var shells = new List<int>();
            switch (solid.Type)
            {
                case "MANIFOLD_SOLID_BREP":
                case "FACETED_BREP":
                    shells.Add(solid.Arg(1).ToRef);
                    break;
                case "BREP_WITH_VOIDS":
                    shells.Add(solid.Arg(1).ToRef);
                    shells.AddRange(solid.Arg(2).Refs);
                    break;
                case "SHELL_BASED_SURFACE_MODEL":
                    shells.AddRange(solid.Arg(1).Refs);
                    break;
                default:
                    if (solid.Is("MANIFOLD_SOLID_BREP"))
                    {
                        shells.Add(solid.ArgsOf("MANIFOLD_SOLID_BREP").Count > 1 ? solid.ArgsOf("MANIFOLD_SOLID_BREP")[1].ToRef : -1);
                    }

                    break;
            }

            foreach (var shellId in shells)
            {
                var shell = _f.Get(shellId);
                if (shell is null)
                {
                    continue;
                }

                // CLOSED_SHELL / OPEN_SHELL(name, faces); ORIENTED_CLOSED_SHELL wraps another shell.
                var faceRefs = shell.Type is "CLOSED_SHELL" or "OPEN_SHELL" ? shell.Arg(1).Refs : shell.Type == "ORIENTED_CLOSED_SHELL" && _f.Get(shell.Arg(2).ToRef) is { } inner ? inner.Arg(1).Refs : [];
                foreach (var faceId in faceRefs)
                {
                    if (_f.Get(faceId) is { } face && face.Type is "ADVANCED_FACE" or "FACE_SURFACE" or "FACE")
                    {
                        yield return face;
                    }
                }
            }
        }
    }
}
