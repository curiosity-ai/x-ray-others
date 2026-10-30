using System.Text;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.OpenScad;

/// <summary>
/// OpenSCAD source (.scad): parsed and evaluated into a mesh. Primitives, transforms, loops, modules, functions, list comprehensions,
/// linear and rotate extrusion, offset and hull are computed; difference and intersection use a BSP-tree boolean; union is a plain
/// combination of its operands. Not computed (warned about, operands drawn or skipped): minkowski, projection, text, import, 2D
/// intersections and 2D cuts that cross an outline, rounded offsets.
/// </summary>
internal static class ScadReader
{
    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var text = Encoding.UTF8.GetString(data.Span);
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        var program = ScadParser.Parse(text, options.Cancellation);
        var eval = new ScadEval(options, name => options.ResolveCompanion?.Invoke(name) is { } bytes ? Encoding.UTF8.GetString(bytes) : null);
        var geos = eval.Run(program);

        var model = new Model3D();
        model.Metadata.Format = ModelFormats.OpenScad;
        model.Metadata.DocumentKind = "program";
        model.Metadata.UpAxis = UpAxis.Z;
        model.Metadata.Units = LengthUnit.Millimeter;
        model.Metadata.Application = "OpenSCAD";
        foreach (var m in eval.Materials)
        {
            model.Materials.Add(m);
            if (m.Name is not null)
            {
                model.Metadata.MaterialNames.Add(m.Name);
            }
        }

        model.Warnings.AddRange(eval.Warnings);
        ReadCustomizer(text, model.Metadata, options.Cancellation);
        model.Metadata.Title = Header(text);

        var solids = geos.OfType<Solid>().ToList();
        var shapes = geos.OfType<Shape>().ToList();
        if (solids.Count > 0 && shapes.Count > 0)
        {
            model.Warnings.Add("The program draws both 2D shapes and 3D solids; only the solids are loaded.");
        }

        if (solids.Count > 0)
        {
            var mesh = BuildMesh(solids.SelectMany(s => s.Polys).ToList(), model.Materials.Count, options);
            mesh.Name = "scad";
            model.Meshes.Add(mesh);
            model.Roots.Add(new SceneNode(null) { Mesh = mesh });
        }
        else if (shapes.Count > 0)
        {
            foreach (var region in shapes.SelectMany(s => s.Regions))
            {
                var fill = new ModelFill();
                foreach (var loop in region)
                {
                    fill.Loops.Add(loop.Select(p => new System.Numerics.Vector3((float)p.X, (float)p.Y, 0)).ToArray());
                }

                model.Fills.Add(fill);
            }

            model.Is2D = true;
        }

        model.Metadata.HasGeometry = model.Meshes.Count > 0 || model.Fills.Count > 0;
        model.Metadata.Statistics = model.ComputeStatistics();
        model.Metadata.Bounds = model.ComputeBounds();
        return model;
    }

    private static MeshData BuildMesh(List<Poly> polys, int materialCount, LoadOptions options)
    {
        var positions = new List<float>();
        var weld = new Dictionary<(float, float, float), uint>();
        var byMaterial = new SortedDictionary<int, List<uint>>();
        long tick = 0;
        if (polys.Count > options.MaxTriangles)
        {
            throw new ModelFormatException("The program produces more geometry than the configured limit.");
        }

        uint Vertex(D3 v)
        {
            var key = ((float)v.X, (float)v.Y, (float)v.Z);
            if (!weld.TryGetValue(key, out var id))
            {
                id = (uint)(positions.Count / 3);
                weld[key] = id;
                positions.Add(key.Item1);
                positions.Add(key.Item2);
                positions.Add(key.Item3);
            }

            return id;
        }

        foreach (var p in polys)
        {
            Cancel.Every(options.Cancellation, tick++);
            if (!byMaterial.TryGetValue(p.Color, out var list))
            {
                byMaterial[p.Color] = list = new List<uint>();
            }

            var first = Vertex(p.Vertices[0]);
            var prev = Vertex(p.Vertices[1]);
            for (var i = 2; i < p.Vertices.Length; i++)
            {
                var next = Vertex(p.Vertices[i]);
                if (first != prev && prev != next && first != next)
                {
                    list.Add(first);
                    list.Add(prev);
                    list.Add(next);
                }

                prev = next;
            }
        }

        var mesh = new MeshData { Positions = positions.ToArray() };
        var indices = new List<uint>();
        foreach (var (material, list) in byMaterial)
        {
            mesh.Groups.Add(new MeshGroup(indices.Count, list.Count, material));
            indices.AddRange(list);
        }

        mesh.Indices = indices.ToArray();

        // The BSP booleans split polygons where they cross, which leaves vertices on the edges of unsplit neighbours: close those cracks so the
        // solid is closed topologically as well as geometrically.
        var repaired = TJunctionRepair.Repair(mesh.Positions, mesh.Indices, mesh.Groups.Select(g => (g.Start, g.Count)).ToList(), out var ranges, options.Cancellation);
        if (!ReferenceEquals(repaired, mesh.Indices))
        {
            var materials = mesh.Groups.Select(g => g.Material).ToList();
            mesh.Indices = repaired;
            mesh.Groups.Clear();
            for (var g = 0; g < ranges.Length; g++)
            {
                mesh.Groups.Add(new MeshGroup(ranges[g].Start, ranges[g].Count, materials[g]));
            }
        }

        if (mesh.Groups.Count == 1 && mesh.Groups[0].Material < 0)
        {
            mesh.Groups.Clear();
        }

        _ = materialCount;
        return mesh;
    }

    private static bool IsLiteral(string value) =>
        value is "true" or "false"
        || double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)
        || (value.Length >= 2 && value[0] == '"' && value[^1] == '"' && !value.AsSpan(1, value.Length - 2).Contains('"'));

    /// <summary>The first comment line of the file, as a title.</summary>
    private static string? Header(string text)
    {
        foreach (var raw in text.AsSpan().EnumerateLines())
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            return line.StartsWith("//", StringComparison.Ordinal) ? line[2..].Trim().ToString() is { Length: > 0 } t ? t : null : null;
        }

        return null;
    }

    /// <summary>
    /// The Customizer parameters: top-level assignments in the leading block, grouped by "/* [Group] */" comments, described by the
    /// comment line above them, and followed by "// [range]" hints. Reported as properties.
    /// </summary>
    private static void ReadCustomizer(string text, ModelMetadata meta, CancellationToken cancel)
    {
        string? group = null;
        string? description = null;
        var depth = 0;
        long tick = 0;
        foreach (var raw in text.AsSpan().EnumerateLines())
        {
            Cancel.Every(cancel, tick++);
            var line = raw.Trim().ToString();
            if (line.StartsWith("/*", StringComparison.Ordinal) && line.EndsWith("*/", StringComparison.Ordinal))
            {
                var inner = line[2..^2].Trim();
                if (inner.StartsWith('[') && inner.EndsWith(']'))
                {
                    group = inner[1..^1];
                    if (group is "Hidden" or "hidden")
                    {
                        group = null;
                    }
                }

                continue;
            }

            if (depth == 0 && line.StartsWith("//", StringComparison.Ordinal))
            {
                description = line[2..].Trim();
                continue;
            }

            if (depth == 0 && line.Length > 0 && (char.IsLetter(line[0]) || line[0] == '_' || line[0] == '$'))
            {
                var eq = line.IndexOf('=');
                var semi = line.IndexOf(';');
                if (eq > 0 && semi > eq && !line.StartsWith("module ", StringComparison.Ordinal) && !line.StartsWith("function ", StringComparison.Ordinal))
                {
                    var name = line[..eq].Trim();
                    if (name.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '$'))
                    {
                        var value = line[(eq + 1)..semi].Trim();
                        if (IsLiteral(value))
                        {
                            var hint = line[(semi + 1)..].Trim();
                            var detail = description is not null && !description.Contains('=') ? description : null;
                            meta.Properties.Add(new ModelProperty(name, value.Trim('"'), group is not null ? group : detail is not null ? "Parameters" : null));
                            if (detail is not null && hint.Length == 0)
                            {
                                // keep the description as part of the name list through a second property would be noise: skip.
                            }
                        }
                    }
                }
            }

            if (line.Length > 0 && !line.StartsWith("//", StringComparison.Ordinal))
            {
                description = null;
                foreach (var c in line)
                {
                    depth += c == '{' ? 1 : c == '}' ? -1 : 0;
                }

                depth = Math.Max(depth, 0);
            }
        }
    }
}
