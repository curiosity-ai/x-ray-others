using System.Globalization;
using System.Numerics;
using System.Text;
using System.Xml;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD;

/// <summary>What <see cref="SvgWriter"/> does with a model.</summary>
public sealed class SvgWriteOptions
{
    /// <summary>Draw a three-dimensional model as its top view (Z dropped). Off by default: only drawings (<see cref="Model3D.Is2D"/>) are written.</summary>
    public bool ProjectFlat { get; set; }

    /// <summary>Stroke width in pixels (strokes do not scale with the zoom).</summary>
    public double StrokeWidth { get; set; } = 1;

    /// <summary>Write the model's metadata into a <c>&lt;metadata&gt;</c> element and <c>data-*</c> attributes.</summary>
    public bool IncludeMetadata { get; set; } = true;

    /// <summary>Share meshes that several nodes use (blocks, instanced parts) through <c>&lt;defs&gt;</c> and <c>&lt;use&gt;</c>.</summary>
    public bool ReuseBlocks { get; set; } = true;

    /// <summary>Checked between nodes and every few thousand segments; a cancelled write throws <see cref="OperationCanceledException"/>.</summary>
    public CancellationToken Cancellation { get; set; }

    internal SvgWriteOptions Clone() => (SvgWriteOptions)MemberwiseClone();
}

/// <summary>
/// Writes a drawing as SVG: one <c>&lt;g&gt;</c> per layer (id, name, visibility), strokes with the layer or entity colour, real
/// <c>&lt;text&gt;</c>, filled areas, and shared blocks through <c>&lt;use&gt;</c>. Y is flipped to screen space and the viewBox is the drawing's bounds.
/// </summary>
public static class SvgWriter
{
    private const string Ns = "http://www.w3.org/2000/svg";
    private const string XRayNs = "urn:xray3d:svg";

    public static string ToString(Model3D model, SvgWriteOptions? options = null)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        Write(model, writer, options);
        return writer.ToString();
    }

    public static void Write(Model3D model, TextWriter output, SvgWriteOptions? options = null)
    {
        options ??= new SvgWriteOptions();
        if (!model.Is2D && !options.ProjectFlat)
        {
            throw new ModelFormatException("The model is three-dimensional. Write it with GlbWriter, or set SvgWriteOptions.ProjectFlat to draw its top view.");
        }

        options.Cancellation.ThrowIfCancellationRequested();
        using var xml = XmlWriter.Create(output, new XmlWriterSettings { Indent = false, OmitXmlDeclaration = false, CloseOutput = false, NewLineHandling = NewLineHandling.None });
        new Run(model, options, xml).Write();
    }

    private sealed class Run
    {
        private readonly Model3D _model;
        private readonly SvgWriteOptions _o;
        private readonly XmlWriter _w;
        private readonly List<(SceneNode Node, Matrix4x4 World)> _placed = new();
        private readonly Dictionary<MeshData, int> _uses = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<MeshData, int> _meshIds = new(ReferenceEqualityComparer.Instance);
        private long _tick;

        public Run(Model3D model, SvgWriteOptions options, XmlWriter writer)
        {
            _model = model;
            _o = options;
            _w = writer;
        }

        public void Write()
        {
            Place();
            var box = Bounds();
            var pad = Math.Max(box.Width, box.Height) * 0.02;
            if (!(pad > 0))
            {
                pad = 1;
            }

            _w.WriteStartElement("svg", Ns);
            _w.WriteAttributeString("viewBox", $"{N(box.MinX - pad)} {N(-box.MaxY - pad)} {N(box.Width + 2 * pad)} {N(box.Height + 2 * pad)}");
            _w.WriteAttributeString("preserveAspectRatio", "xMidYMid meet");
            _w.WriteAttributeString("fill", "none");
            _w.WriteAttributeString("stroke", "#000000");
            _w.WriteAttributeString("stroke-linecap", "round");
            _w.WriteAttributeString("stroke-linejoin", "round");
            var meta = _model.Metadata;
            if (_o.IncludeMetadata)
            {
                _w.WriteAttributeString("data-format", meta.Format.Id);
                if (meta.Units is { } unit)
                {
                    _w.WriteAttributeString("data-units", unit.ToString().ToLowerInvariant());
                    _w.WriteAttributeString("data-unit-to-meters", N(unit.ToMeters()));
                }

                if (meta.Title is { Length: > 0 } title)
                {
                    _w.WriteElementString("title", Ns, Clean(title));
                }

                WriteMetadata();
            }

            WriteDefs();
            var layerCount = _model.Layers.Count;
            for (var layer = -1; layer < layerCount; layer++)
            {
                WriteLayer(layer);
            }

            _w.WriteEndElement();
            _w.Flush();
        }

        private void Place()
        {
            var stack = new Stack<(SceneNode, Matrix4x4)>();
            foreach (var root in Enumerable.Reverse(_model.Roots))
            {
                stack.Push((root, Matrix4x4.Identity));
            }

            while (stack.Count > 0)
            {
                Cancel.Every(_o.Cancellation, _tick++, 255);
                var (node, parent) = stack.Pop();
                var world = node.Transform * parent;
                if (node.Mesh is { } mesh && mesh.VertexCount > 0)
                {
                    _placed.Add((node, world));
                    _uses[mesh] = _uses.GetValueOrDefault(mesh) + 1;
                }

                for (var i = node.Children.Count - 1; i >= 0; i--)
                {
                    stack.Push((node.Children[i], world));
                }
            }
        }

        private (double MinX, double MaxX, double MinY, double MaxY, double Width, double Height) Bounds()
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            void Include(Vector3 p)
            {
                minX = Math.Min(minX, p.X);
                maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y);
                maxY = Math.Max(maxY, p.Y);
            }

            foreach (var (node, world) in _placed)
            {
                var positions = node.Mesh!.Positions;
                for (var i = 0; i + 2 < positions.Length; i += 3)
                {
                    Cancel.Every(_o.Cancellation, _tick++, 4095);
                    Include(Vector3.Transform(new Vector3(positions[i], positions[i + 1], positions[i + 2]), world));
                }
            }

            foreach (var fill in _model.Fills)
            {
                foreach (var loop in fill.Loops)
                {
                    foreach (var p in loop)
                    {
                        Include(p);
                    }
                }
            }

            foreach (var text in _model.Texts)
            {
                Include(text.Position);
            }

            foreach (var image in _model.Images)
            {
                Include(image.Position);
                Include(image.Position + new Vector3(image.Width, image.Height, 0));
            }

            if (minX > maxX)
            {
                return (0, 1, 0, 1, 1, 1);
            }

            return (minX, maxX, minY, maxY, maxX - minX, maxY - minY);
        }

        private void WriteMetadata()
        {
            var meta = _model.Metadata;
            _w.WriteStartElement("metadata", Ns);
            _w.WriteStartElement("xray", "model", XRayNs);
            _w.WriteAttributeString("format", meta.Format.Id);
            WriteAttr("formatName", meta.Format.Name);
            WriteAttr("documentKind", meta.DocumentKind);
            WriteAttr("application", meta.Application);
            WriteAttr("author", meta.Author);
            WriteAttr("units", meta.Units?.ToString().ToLowerInvariant());
            WriteAttr("version", meta.FormatVersion);
            foreach (var layer in _model.Layers)
            {
                _w.WriteStartElement("xray", "layer", XRayNs);
                _w.WriteAttributeString("name", Clean(layer.Name));
                _w.WriteAttributeString("visible", layer.Visible ? "true" : "false");
                _w.WriteEndElement();
            }

            foreach (var p in meta.Properties.Take(500))
            {
                _w.WriteStartElement("xray", "property", XRayNs);
                _w.WriteAttributeString("name", Clean(p.Name));
                _w.WriteAttributeString("value", Clean(p.Value));
                if (p.Group is not null)
                {
                    _w.WriteAttributeString("group", Clean(p.Group));
                }

                _w.WriteEndElement();
            }

            _w.WriteEndElement();
            _w.WriteEndElement();
        }

        private void WriteAttr(string name, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                _w.WriteAttributeString(name, Clean(value));
            }
        }

        private void WriteDefs()
        {
            if (!_o.ReuseBlocks)
            {
                return;
            }

            var shared = _uses.Where(u => u.Value >= 2).Select(u => u.Key).ToList();
            if (shared.Count == 0)
            {
                return;
            }

            _w.WriteStartElement("defs", Ns);
            foreach (var mesh in shared)
            {
                var id = _meshIds.Count;
                _meshIds[mesh] = id;
                _w.WriteStartElement("g", Ns);
                _w.WriteAttributeString("id", "block-" + id.ToString(CultureInfo.InvariantCulture));
                if (!string.IsNullOrEmpty(mesh.Name))
                {
                    _w.WriteAttributeString("data-name", Clean(mesh.Name));
                }

                WriteMesh(mesh, Matrix4x4.Identity, flip: false);
                _w.WriteEndElement();
            }

            _w.WriteEndElement();
        }

        private void WriteLayer(int layer)
        {
            var fills = _model.Fills.Where(f => f.Layer == layer).ToList();
            var texts = _model.Texts.Where(t => t.Layer == layer).ToList();
            var nodes = _placed.Where(p => p.Node.Layer == layer).ToList();
            var images = _model.Images.Where(i => i.Layer == layer).ToList();
            if (fills.Count == 0 && texts.Count == 0 && nodes.Count == 0 && images.Count == 0)
            {
                return;
            }

            _w.WriteStartElement("g", Ns);
            _w.WriteAttributeString("id", layer < 0 ? "layer-none" : "layer-" + layer.ToString(CultureInfo.InvariantCulture));
            if (layer >= 0)
            {
                _w.WriteAttributeString("data-name", Clean(_model.Layers[layer].Name));
                if (!_model.Layers[layer].Visible)
                {
                    _w.WriteAttributeString("display", "none");
                }
            }

            foreach (var image in images)
            {
                // Y is flipped like everything else: the rectangle's top edge is at -(y + height).
                _w.WriteStartElement("image", Ns);
                _w.WriteAttributeString("x", N(image.Position.X));
                _w.WriteAttributeString("y", N(-(image.Position.Y + image.Height)));
                _w.WriteAttributeString("width", N(image.Width));
                _w.WriteAttributeString("height", N(image.Height));
                _w.WriteAttributeString("preserveAspectRatio", "none");
                _w.WriteAttributeString("href", "data:" + image.MediaType + ";base64," + Convert.ToBase64String(image.Data));
                if (!string.IsNullOrEmpty(image.Name))
                {
                    _w.WriteAttributeString("data-name", Clean(image.Name));
                }

                _w.WriteEndElement();
            }

            foreach (var fill in fills)
            {
                WriteFill(fill);
            }

            foreach (var (node, world) in nodes)
            {
                Cancel.Every(_o.Cancellation, _tick++, 255);
                var mesh = node.Mesh!;
                if (_meshIds.TryGetValue(mesh, out var id))
                {
                    _w.WriteStartElement("use", Ns);
                    _w.WriteAttributeString("href", "#block-" + id.ToString(CultureInfo.InvariantCulture));
                    _w.WriteAttributeString("transform", MatrixOf(world));
                    if (!string.IsNullOrEmpty(node.Name))
                    {
                        _w.WriteAttributeString("data-name", Clean(node.Name));
                    }

                    _w.WriteEndElement();
                }
                else
                {
                    WriteMesh(mesh, world, flip: true);
                }
            }

            foreach (var text in texts)
            {
                WriteText(text);
            }

            _w.WriteEndElement();
        }

        /// <summary>The 2D part of <paramref name="world"/> followed by the Y flip, as an SVG matrix.</summary>
        private static string MatrixOf(Matrix4x4 world)
        {
            // Row vectors: x' = x*M11 + y*M21 + M41, y' = x*M12 + y*M22 + M42; the flip negates the y' column.
            return $"matrix({N(world.M11)} {N(-world.M12)} {N(world.M21)} {N(-world.M22)} {N(world.M41)} {N(-world.M42)})";
        }

        private string ColorOf(int material, string fallback)
        {
            if (material >= 0 && material < _model.Materials.Count)
            {
                var c = _model.Materials[material].BaseColor;
                return Hex(c.X, c.Y, c.Z);
            }

            return fallback;
        }

        private static string Hex(float r, float g, float b) =>
            "#" + ((int)Math.Round(Math.Clamp(r, 0, 1) * 255)).ToString("x2", CultureInfo.InvariantCulture)
            + ((int)Math.Round(Math.Clamp(g, 0, 1) * 255)).ToString("x2", CultureInfo.InvariantCulture)
            + ((int)Math.Round(Math.Clamp(b, 0, 1) * 255)).ToString("x2", CultureInfo.InvariantCulture);

        private static string HexOf(int rgb) => "#" + (rgb & 0xFFFFFF).ToString("x6", CultureInfo.InvariantCulture);

        private void WriteMesh(MeshData mesh, Matrix4x4 world, bool flip)
        {
            var p = mesh.Positions;
            var sb = new StringBuilder();

            void Point(uint index)
            {
                var v = new Vector3(p[3 * index], p[3 * index + 1], p[3 * index + 2]);
                if (flip)
                {
                    v = Vector3.Transform(v, world);
                    sb.Append(N(v.X)).Append(' ').Append(N(-v.Y));
                }
                else
                {
                    sb.Append(N(v.X)).Append(' ').Append(N(v.Y));
                }
            }

            if (mesh.Indices.Length > 0 && _model.Fills.Count == 0)
            {
                // Triangles of a drawing without filled areas of its own (3D faces, solids): one path per material group.
                var groups = mesh.Groups.Count > 0 ? mesh.Groups : [new MeshGroup(0, mesh.Indices.Length, -1)];
                foreach (var group in groups)
                {
                    sb.Clear();
                    for (var i = group.Start; i + 2 < group.Start + group.Count && i + 2 < mesh.Indices.Length; i += 3)
                    {
                        Cancel.Every(_o.Cancellation, _tick++, 4095);
                        sb.Append('M');
                        Point(mesh.Indices[i]);
                        sb.Append('L');
                        Point(mesh.Indices[i + 1]);
                        sb.Append('L');
                        Point(mesh.Indices[i + 2]);
                        sb.Append('Z');
                    }

                    if (sb.Length > 0)
                    {
                        _w.WriteStartElement("path", Ns);
                        _w.WriteAttributeString("d", sb.ToString());
                        _w.WriteAttributeString("fill", ColorOf(group.Material, "#999999"));
                        _w.WriteAttributeString("stroke", "none");
                        _w.WriteEndElement();
                    }
                }
            }

            if (mesh.EdgeIndices.Length > 0)
            {
                sb.Clear();
                var color = ColorOf(mesh.EdgeMaterial, "#000000");
                long last = -1;
                for (var i = 0; i + 1 < mesh.EdgeIndices.Length; i += 2)
                {
                    Cancel.Every(_o.Cancellation, _tick++, 4095);
                    if (mesh.EdgeIndices[i] != last)
                    {
                        sb.Append('M');
                        Point(mesh.EdgeIndices[i]);
                    }

                    sb.Append('L');
                    Point(mesh.EdgeIndices[i + 1]);
                    last = mesh.EdgeIndices[i + 1];
                }

                WriteStroke(sb.ToString(), color, 1);
            }
            else if (mesh.Indices.Length == 0)
            {
                sb.Clear();
                for (uint i = 0; i < mesh.VertexCount; i++)
                {
                    Cancel.Every(_o.Cancellation, _tick++, 4095);
                    sb.Append('M');
                    Point(i);
                    sb.Append("h0");
                }

                WriteStroke(sb.ToString(), "#000000", 3);
            }
        }

        private void WriteStroke(string d, string color, double widthFactor)
        {
            if (d.Length == 0)
            {
                return;
            }

            _w.WriteStartElement("path", Ns);
            _w.WriteAttributeString("d", d);
            _w.WriteAttributeString("stroke", color);
            _w.WriteAttributeString("stroke-width", N(_o.StrokeWidth * widthFactor));
            _w.WriteAttributeString("vector-effect", "non-scaling-stroke");
            _w.WriteEndElement();
        }

        private void WriteFill(ModelFill fill)
        {
            var sb = new StringBuilder();
            foreach (var loop in fill.Loops)
            {
                if (loop.Length < 3)
                {
                    continue;
                }

                for (var i = 0; i < loop.Length; i++)
                {
                    Cancel.Every(_o.Cancellation, _tick++, 4095);
                    sb.Append(i == 0 ? 'M' : 'L').Append(N(loop[i].X)).Append(' ').Append(N(-loop[i].Y));
                }

                sb.Append('Z');
            }

            if (sb.Length == 0)
            {
                return;
            }

            _w.WriteStartElement("path", Ns);
            _w.WriteAttributeString("d", sb.ToString());
            _w.WriteAttributeString("fill", fill.Rgb >= 0 ? HexOf(fill.Rgb) : "#000000");
            _w.WriteAttributeString("fill-rule", "evenodd");
            _w.WriteAttributeString("stroke", "none");
            _w.WriteEndElement();
        }

        private void WriteText(ModelText text)
        {
            if (string.IsNullOrEmpty(text.Text))
            {
                return;
            }

            var x = N(text.Position.X);
            var y = N(-text.Position.Y);
            _w.WriteStartElement("text", Ns);
            _w.WriteAttributeString("x", x);
            _w.WriteAttributeString("y", y);
            _w.WriteAttributeString("font-size", N(text.Height));
            _w.WriteAttributeString("font-family", "sans-serif");
            _w.WriteAttributeString("fill", text.Rgb >= 0 ? HexOf(text.Rgb) : "#000000");
            _w.WriteAttributeString("stroke", "none");
            if (text.Anchor is "middle" or "end")
            {
                _w.WriteAttributeString("text-anchor", text.Anchor);
            }

            if (text.Rotation != 0)
            {
                _w.WriteAttributeString("transform", $"rotate({N(-text.Rotation)} {x} {y})");
            }

            var lines = text.Text.Split('\n');
            if (lines.Length == 1)
            {
                _w.WriteString(Clean(text.Text));
            }
            else
            {
                for (var i = 0; i < lines.Length; i++)
                {
                    _w.WriteStartElement("tspan", Ns);
                    _w.WriteAttributeString("x", x);
                    _w.WriteAttributeString("dy", i == 0 ? "0" : N(text.Height * 1.2));
                    _w.WriteString(Clean(lines[i]));
                    _w.WriteEndElement();
                }
            }

            _w.WriteEndElement();
        }
    }

    private static string N(double v)
    {
        if (!double.IsFinite(v))
        {
            return "0";
        }

        var s = v.ToString("0.#####", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }

    /// <summary>XML 1.0 cannot carry most control characters or lone surrogates; they become '?'.</summary>
    private static string Clean(string value)
    {
        var needs = false;
        for (var i = 0; i < value.Length; i++)
        {
            if (!XmlConvert.IsXmlChar(value[i]) && !(char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) && !char.IsLowSurrogate(value[i]))
            {
                needs = true;
                break;
            }
        }

        if (!needs)
        {
            return value;
        }

        var sb = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                sb.Append(c).Append(value[++i]);
            }
            else
            {
                sb.Append(XmlConvert.IsXmlChar(c) ? c : '?');
            }
        }

        return sb.ToString();
    }
}
