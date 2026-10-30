using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.Dwf;

/// <summary>
/// Autodesk DWF 6 (a ZIP package behind a "(DWF V06.00)" header) and DWFx (an XPS package). Reads the manifest and page descriptors (title, paper,
/// units, extents, transforms), the preview image when the package has one, and the 2D graphics: W2D streams in their text form (colours,
/// polylines, lines, polygons, text) and XPS pages (paths and glyphs). Binary W2D streams and W3D (3D) sections are not decoded and are counted
/// in a warning.
/// </summary>
internal static class DwfReader
{
    private const long EntryLimit = 512L * 1024 * 1024;

    public static bool Sniff(ReadOnlySpan<byte> head) => head.Length >= 8 && Encoding.ASCII.GetString(head[..5]) == "(DWF " && Encoding.ASCII.GetString(head[..Math.Min(head.Length, 12)]).StartsWith("(DWF V", StringComparison.Ordinal);

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var cancel = options.Cancellation;
        var model = new Model3D();
        var meta = model.Metadata;
        meta.Format = ModelFormats.Dwf;
        meta.DocumentKind = "drawing";
        meta.UpAxis = UpAxis.Z;
        var head = Encoding.ASCII.GetString(data.Span[..Math.Min(data.Length, 12)]);
        if (head.StartsWith("(DWF V", StringComparison.Ordinal))
        {
            meta.FormatVersion = "DWF " + head.TrimStart('(').Replace("DWF V", "", StringComparison.Ordinal).TrimEnd(')');
        }

        using var zip = OpenPackage(data);
        var entries = zip.Entries.ToDictionary(e => e.FullName, e => e, StringComparer.OrdinalIgnoreCase);
        string? Text(string name) => entries.TryGetValue(name, out var e) ? Encoding.UTF8.GetString(ZipEntries.Read(e, 64 * 1024 * 1024, cancel)) : null;

        // DWFx carries a DWF manifest next to the XPS parts.
        var isXps = entries.Keys.Any(k => k.EndsWith(".fpage", StringComparison.OrdinalIgnoreCase));
        if (isXps)
        {
            meta.FormatVersion = "DWFx (XPS package)";
        }

        var sheets = new List<(string Name, string? Descriptor)>();
        if (Text("manifest.xml") is { } manifest)
        {
            var doc = LoadXml(manifest);
            foreach (var section in doc.Descendants().Where(e => e.Name.LocalName == "Section"))
            {
                var type = section.Attribute("type")?.Value ?? "";
                var title = section.Attribute("title")?.Value ?? section.Attribute("name")?.Value ?? "";
                var href = section.Attribute("href")?.Value;
                if (type.EndsWith("ePlot", StringComparison.Ordinal))
                {
                    sheets.Add((title, href));
                }
                else if (type.Contains("eModel", StringComparison.Ordinal) || type.Contains("3D", StringComparison.Ordinal))
                {
                    model.Warnings.Add($"The 3D section '{title}' (W3D) is not decoded.");
                    meta.PartNames.Add(title);
                }
                else if (type.Length > 0)
                {
                    meta.Notes.Add($"Section {title} ({type}) is not read.");
                }
            }
        }

        if (meta.Title is null && sheets.Count > 0)
        {
            meta.Title = sheets[0].Name;
        }

        // Preview image: a PNG or JPEG named like a thumbnail or preview.
        foreach (var e in zip.Entries)
        {
            var name = e.FullName.ToLowerInvariant();
            if ((name.EndsWith(".png", StringComparison.Ordinal) || name.EndsWith(".jpg", StringComparison.Ordinal) || name.EndsWith(".jpeg", StringComparison.Ordinal)) && (name.Contains("thumbnail", StringComparison.Ordinal) || name.Contains("preview", StringComparison.Ordinal)))
            {
                model.Preview = new PreviewImage(name.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : "image/jpeg", ZipEntries.Read(e, 16 * 1024 * 1024, cancel));
                meta.HasPreview = true;
                break;
            }
        }

        var builder = new DrawingBuilder(model, options);
        var offset = 0.0;
        if (isXps)
        {
            var pages = entries.Values.Where(e => e.FullName.EndsWith(".fpage", StringComparison.OrdinalIgnoreCase)).OrderBy(e => e.FullName, StringComparer.Ordinal).ToList();
            var index = 0;
            foreach (var page in pages)
            {
                cancel.ThrowIfCancellationRequested();
                var xml = LoadXml(Encoding.UTF8.GetString(ZipEntries.Read(page, 64 * 1024 * 1024, cancel)));
                var width = ParseDouble(xml.Attribute("Width")?.Value, 816) / 96.0;
                var height = ParseDouble(xml.Attribute("Height")?.Value, 1056) / 96.0;
                var name = "Page " + (++index).ToString(CultureInfo.InvariantCulture);
                meta.Sheets.Add(name);
                meta.DrawingSheets.Add(new DrawingSheet(name, width * 0.0254, height * 0.0254));
                builder.Page(xml, offset, height);
                offset += width * 1.1;
            }

            meta.Units = LengthUnit.Inch;
        }
        else
        {
            var index = 0;
            foreach (var (title, href) in sheets)
            {
                cancel.ThrowIfCancellationRequested();
                var dir = href is null ? "" : href[..(href.LastIndexOf('/') + 1)];
                var descriptor = href is null ? null : Text(href);
                if (descriptor is null)
                {
                    continue;
                }

                var page = LoadXml(descriptor);
                var paper = page.Descendants().FirstOrDefault(e => e.Name.LocalName == "Paper");
                var width = ParseDouble(paper?.Attribute("width")?.Value, 0);
                var height = ParseDouble(paper?.Attribute("height")?.Value, 0);
                var units = paper?.Attribute("units")?.Value ?? "in";
                var unitScale = units switch { "mm" => 1e-3, "cm" => 1e-2, "m" => 1.0, _ => 0.0254 };
                meta.Sheets.Add(page.Attribute("name")?.Value ?? title);
                if (width > 0 && height > 0)
                {
                    meta.DrawingSheets.Add(new DrawingSheet(page.Attribute("name")?.Value ?? title, width * unitScale, height * unitScale));
                }

                meta.Units = units switch { "mm" => LengthUnit.Millimeter, "cm" => LengthUnit.Centimeter, "m" => LengthUnit.Meter, _ => LengthUnit.Inch };
                foreach (var resource in page.Descendants().Where(e => e.Name.LocalName == "GraphicResource"))
                {
                    var mime = resource.Attribute("mime")?.Value ?? "";
                    var path = resource.Attribute("href")?.Value;
                    if (path is null || !entries.TryGetValue(path, out var entry))
                    {
                        continue;
                    }

                    if (!mime.Contains("w2d", StringComparison.OrdinalIgnoreCase))
                    {
                        model.Warnings.Add($"Graphic resource '{path}' ({mime}) is not decoded.");
                        continue;
                    }

                    var scale = 1.0;
                    var transform = (resource.Attribute("transform")?.Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (transform.Length >= 1 && double.TryParse(transform[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0)
                    {
                        scale = s;
                    }

                    var w2d = ZipEntries.Read(entry, EntryLimit, cancel);
                    builder.W2d(Encoding.Latin1.GetString(w2d), scale, offset, resource.Attribute("title")?.Value ?? title);
                }

                offset += (width > 0 ? width : 1) * 1.1;
                index++;
            }
        }

        builder.Finish();
        return model;
    }

    private static ZipArchive OpenPackage(ReadOnlyMemory<byte> data)
    {
        // A DWF 6 file has twelve bytes of text before the ZIP stream; offsets in its directory count from the start of that stream.
        try
        {
            return ZipEntries.Open(data);
        }
        catch (ModelFormatException) when (Sniff(data.Span))
        {
            return ZipEntries.Open(data[12..]);
        }
    }

    private static XElement LoadXml(string text)
    {
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 256 * 1024 * 1024 });
        return XElement.Load(reader);
    }

    private static double ParseDouble(string? s, double fallback) =>
        s is not null && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : fallback;

    // ------------------------------------------------------------------------------------------------------------

    private sealed class DrawingBuilder
    {
        private readonly Model3D _model;
        private readonly LoadOptions _options;
        private readonly Dictionary<int, (List<float> Positions, List<uint> Segments)> _byColour = new();
        private readonly Dictionary<int, int> _materials = new();
        private int _unknown;
        private long _tick;

        public DrawingBuilder(Model3D model, LoadOptions options)
        {
            _model = model;
            _options = options;
        }

        private (List<float> Positions, List<uint> Segments) Bucket(int rgb)
        {
            if (!_byColour.TryGetValue(rgb, out var b))
            {
                _byColour[rgb] = b = (new List<float>(), new List<uint>());
            }

            return b;
        }

        private void Polyline(IReadOnlyList<(double X, double Y)> points, int rgb, double scale, double dx, double dy, bool flipY = false)
        {
            if (points.Count < 2)
            {
                return;
            }

            var (positions, segments) = Bucket(rgb);
            var first = (uint)(positions.Count / 3);
            foreach (var (x, y) in points)
            {
                positions.Add((float)(x * scale + dx));
                positions.Add((float)((flipY ? -y : y) * scale + dy));
                positions.Add(0f);
            }

            for (var i = 0; i + 1 < points.Count; i++)
            {
                segments.Add(first + (uint)i);
                segments.Add(first + (uint)i + 1);
            }
        }

        /// <summary>The text form of a W2D stream: parenthesised commands and one-letter drawing commands.</summary>
        public void W2d(string text, double scale, double dx, string sheet)
        {
            if (!text.StartsWith("(W2D", StringComparison.Ordinal))
            {
                _model.Warnings.Add($"The graphics of '{sheet}' are a binary W2D stream, which is not decoded.");
                return;
            }

            var fontHeight = 1.0;
            var fontRotation = 0.0;
            var colour = 0x000000;
            var line = 0;
            foreach (var raw in text.AsSpan().EnumerateLines())
            {
                Cancel.Every(_options.Cancellation, _tick++);
                line++;
                var s = raw.Trim().ToString();
                if (s.Length == 0)
                {
                    continue;
                }

                if (s[0] == '(')
                {
                    var name = s[1..].Split(' ', ')', '(')[0];
                    var body = s.Length > name.Length + 1 ? s[(name.Length + 1)..].Trim().TrimEnd(')').Trim() : "";
                    switch (name)
                    {
                        case "Color":
                        {
                            var c = body.Split(',', StringSplitOptions.RemoveEmptyEntries);
                            if (c.Length >= 3 && int.TryParse(c[0], out var r) && int.TryParse(c[1], out var g) && int.TryParse(c[2], out var b))
                            {
                                colour = Math.Clamp(r, 0, 255) << 16 | Math.Clamp(g, 0, 255) << 8 | Math.Clamp(b, 0, 255);
                            }

                            break;
                        }

                        case "Font":
                        {
                            var h = ExtractNumber(body, "Height");
                            var r = ExtractNumber(body, "Rotation");
                            if (h is { } hh)
                            {
                                fontHeight = hh;
                            }

                            fontRotation = (r ?? 0) * 360.0 / 65536.0;
                            break;
                        }

                        case "Text":
                        {
                            var q = body.IndexOf('\'');
                            var q2 = body.LastIndexOf('\'');
                            var pos = (q > 0 ? body[..q] : body).Trim().Split(',');
                            if (pos.Length == 2 && q >= 0 && q2 > q && double.TryParse(pos[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && double.TryParse(pos[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
                            {
                                _model.Texts.Add(new ModelText
                                {
                                    Text = body.Substring(q + 1, q2 - q - 1),
                                    Position = new Vector3((float)(x * scale + dx), (float)(y * scale), 0),
                                    Height = (float)(fontHeight * scale),
                                    Rotation = (float)fontRotation,
                                    Rgb = colour,
                                });
                            }

                            break;
                        }

                        case "W2D" or "EndOfDWF":
                            break;
                        default:
                            _unknown++;
                            break;
                    }

                    continue;
                }

                var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts[0] == "P" && parts.Length >= 3 && int.TryParse(parts[1], out var count) && count >= 1 && count == parts.Length - 2)
                {
                    var points = new List<(double, double)>(count);
                    var ok = true;
                    for (var i = 2; i < parts.Length && ok; i++)
                    {
                        var xy = parts[i].Split(',');
                        ok = xy.Length == 2 && double.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out _) && double.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out _);
                        if (ok)
                        {
                            points.Add((double.Parse(xy[0], CultureInfo.InvariantCulture), double.Parse(xy[1], CultureInfo.InvariantCulture)));
                        }
                    }

                    if (ok)
                    {
                        Polyline(points, colour, scale, dx, 0);
                    }
                    else
                    {
                        _unknown++;
                    }
                }
                else
                {
                    _unknown++;
                }
            }
        }

        private static double? ExtractNumber(string body, string key)
        {
            var i = body.IndexOf("(" + key, StringComparison.Ordinal);
            if (i < 0)
            {
                return null;
            }

            var rest = body[(i + key.Length + 1)..].TrimStart();
            var end = 0;
            while (end < rest.Length && (char.IsDigit(rest[end]) || rest[end] is '.' or '-' or '+'))
            {
                end++;
            }

            return double.TryParse(rest.AsSpan(0, end), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
        }

        /// <summary>One XPS page: paths (strokes and fills) and glyph runs, with canvases and render transforms.</summary>
        public void Page(XElement page, double dx, double height)
        {
            Visit(page, Matrix3x2.Identity, dx, height, 0);
        }

        private void Visit(XElement element, Matrix3x2 parent, double dx, double height, int depth)
        {
            if (depth > 64)
            {
                return;
            }

            foreach (var child in element.Elements())
            {
                Cancel.Every(_options.Cancellation, _tick++);
                var local = parent;
                if (child.Attribute("RenderTransform")?.Value is { } rt && ParseMatrix(rt) is { } m)
                {
                    local = m * parent;
                }

                switch (child.Name.LocalName)
                {
                    case "Canvas":
                        Visit(child, local, dx, height, depth + 1);
                        break;
                    case "Path":
                        PathElement(child, local, dx, height);
                        break;
                    case "Glyphs":
                    {
                        var text = child.Attribute("UnicodeString")?.Value;
                        if (text is null)
                        {
                            break;
                        }

                        var x = ParseDouble(child.Attribute("OriginX")?.Value, 0);
                        var y = ParseDouble(child.Attribute("OriginY")?.Value, 0);
                        var size = ParseDouble(child.Attribute("FontRenderingEmSize")?.Value, 12);
                        var p = Vector2.Transform(new Vector2((float)x, (float)y), local);
                        _model.Texts.Add(new ModelText
                        {
                            Text = text,
                            Position = new Vector3((float)(p.X / 96.0 + dx), (float)((height * 96 - p.Y) / 96.0), 0),
                            Height = (float)(size / 96.0 * Math.Sqrt(local.M11 * local.M11 + local.M12 * local.M12)),
                            Rotation = (float)(-Math.Atan2(local.M12, local.M11) * 180 / Math.PI),
                            Rgb = ParseColour(child.Attribute("Fill")?.Value) ?? 0,
                        });
                        break;
                    }

                    default:
                        // Path.Data and Canvas.Resources property elements and anything else are skipped.
                        break;
                }
            }
        }

        private void PathElement(XElement path, Matrix3x2 transform, double dx, double height)
        {
            if (path.Attribute("Data")?.Value is not { } data)
            {
                return;
            }

            var stroke = ParseColour(path.Attribute("Stroke")?.Value);
            var fill = ParseColour(path.Attribute("Fill")?.Value);
            var loops = ParsePathData(data, _options.Cancellation);
            foreach (var (points, closed) in loops)
            {
                var transformed = points.Select(p => Vector2.Transform(new Vector2((float)p.X, (float)p.Y), transform)).ToList();
                if (fill is { } f && closed && transformed.Count >= 3)
                {
                    var fillShape = new ModelFill { Rgb = f };
                    fillShape.Loops.Add(transformed.Select(p => new Vector3((float)(p.X / 96.0 + dx), (float)((height * 96 - p.Y) / 96.0), 0)).ToArray());
                    _model.Fills.Add(fillShape);
                }

                if (stroke is { } s || fill is null)
                {
                    var list = transformed.Select(p => (X: (double)p.X / 96.0 + dx, Y: (height * 96 - p.Y) / 96.0)).ToList();
                    if (closed && list.Count > 2)
                    {
                        list.Add(list[0]);
                    }

                    Polyline(list, stroke ?? 0, 1, 0, 0);
                }
            }
        }

        public void Finish()
        {
            foreach (var (rgb, (positions, segments)) in _byColour.OrderBy(k => k.Key))
            {
                if (segments.Count == 0)
                {
                    continue;
                }

                var material = _model.Materials.Count;
                _model.Materials.Add(new ModelMaterial { BaseColor = new Vector4(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1) });
                _materials[rgb] = material;
                var mesh = new MeshData { Name = "#" + rgb.ToString("x6", CultureInfo.InvariantCulture), Positions = positions.ToArray(), EdgeIndices = segments.ToArray(), EdgeMaterial = material };
                _model.Meshes.Add(mesh);
                _model.Roots.Add(new SceneNode(null) { Mesh = mesh });
            }

            if (_unknown > 0)
            {
                _model.Warnings.Add($"{_unknown} drawing command(s) of the W2D stream are not decoded.");
            }

            _model.Metadata.HasGeometry = _model.Meshes.Count > 0 || _model.Texts.Count > 0 || _model.Fills.Count > 0;
            _model.Is2D = true;
            _model.Metadata.Statistics = _model.ComputeStatistics();
            _model.Metadata.Bounds = _model.ComputeBounds();
        }
    }

    private static Matrix3x2? ParseMatrix(string text)
    {
        var p = text.Split(',', ' ');
        if (p.Length != 6)
        {
            return null;
        }

        var v = new float[6];
        for (var i = 0; i < 6; i++)
        {
            if (!float.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]) || !float.IsFinite(v[i]))
            {
                return null;
            }
        }

        return new Matrix3x2(v[0], v[1], v[2], v[3], v[4], v[5]);
    }

    private static int? ParseColour(string? text)
    {
        if (text is null || !text.StartsWith('#'))
        {
            return null;
        }

        var hex = text[1..];
        if (hex.Length == 8 && int.TryParse(hex[..2], NumberStyles.HexNumber, null, out var alpha) && alpha == 0)
        {
            return null; // transparent
        }

        if (hex.Length is 6 or 8 && int.TryParse(hex[^6..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return rgb;
        }

        return null;
    }

    /// <summary>The XPS path mini-language: M L H V C Q S T Z (upper and lower case); curves are flattened, arcs drawn as chords.</summary>
    internal static List<(List<(double X, double Y)> Points, bool Closed)> ParsePathData(string data, CancellationToken cancel)
    {
        var result = new List<(List<(double, double)>, bool)>();
        var tokens = new List<string>();
        var i = 0;
        while (i < data.Length)
        {
            var c = data[i];
            if (char.IsLetter(c))
            {
                tokens.Add(c.ToString());
                i++;
            }
            else if (char.IsDigit(c) || c is '-' or '+' or '.')
            {
                var start = i++;
                while (i < data.Length && (char.IsDigit(data[i]) || data[i] == '.' || ((data[i] is 'e' or 'E') && i + 1 < data.Length)))
                {
                    if (data[i] is 'e' or 'E')
                    {
                        i++;
                        if (i < data.Length && data[i] is '-' or '+')
                        {
                            i++;
                        }

                        continue;
                    }

                    i++;
                }

                tokens.Add(data[start..i]);
            }
            else
            {
                i++;
            }

            if (tokens.Count > 5_000_000)
            {
                throw new ModelFormatException("An XPS path is too long.");
            }
        }

        double x = 0, y = 0, sx = 0, sy = 0;
        List<(double, double)>? current = null;
        var k = 0;
        var command = 'M';
        double Next() => k < tokens.Count && double.TryParse(tokens[k++], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : 0;
        long tick = 0;
        while (k < tokens.Count)
        {
            Cancel.Every(cancel, tick++);
            if (char.IsLetter(tokens[k][0]))
            {
                command = tokens[k++][0];
            }

            var rel = char.IsLower(command);
            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                {
                    double nx = Next(), ny = Next();
                    x = rel ? x + nx : nx;
                    y = rel ? y + ny : ny;
                    sx = x;
                    sy = y;
                    current = new List<(double, double)> { (x, y) };
                    result.Add((current, false));
                    command = rel ? 'l' : 'L';
                    break;
                }

                case 'L':
                {
                    double nx = Next(), ny = Next();
                    x = rel ? x + nx : nx;
                    y = rel ? y + ny : ny;
                    (current ??= StartAt(result, x, y)).Add((x, y));
                    break;
                }

                case 'H':
                    x = rel ? x + Next() : Next();
                    (current ??= StartAt(result, x, y)).Add((x, y));
                    break;
                case 'V':
                    y = rel ? y + Next() : Next();
                    (current ??= StartAt(result, x, y)).Add((x, y));
                    break;
                case 'C':
                {
                    var a = (X: Next(), Y: Next());
                    var b = (X: Next(), Y: Next());
                    var e = (X: Next(), Y: Next());
                    if (rel)
                    {
                        a = (x + a.X, y + a.Y);
                        b = (x + b.X, y + b.Y);
                        e = (x + e.X, y + e.Y);
                    }

                    var list = current ??= StartAt(result, x, y);
                    for (var s = 1; s <= 12; s++)
                    {
                        var t = s / 12.0;
                        var u = 1 - t;
                        list.Add((u * u * u * x + 3 * u * u * t * a.X + 3 * u * t * t * b.X + t * t * t * e.X, u * u * u * y + 3 * u * u * t * a.Y + 3 * u * t * t * b.Y + t * t * t * e.Y));
                    }

                    x = e.X;
                    y = e.Y;
                    break;
                }

                case 'Q':
                {
                    var a = (X: Next(), Y: Next());
                    var e = (X: Next(), Y: Next());
                    if (rel)
                    {
                        a = (x + a.X, y + a.Y);
                        e = (x + e.X, y + e.Y);
                    }

                    var list = current ??= StartAt(result, x, y);
                    for (var s = 1; s <= 10; s++)
                    {
                        var t = s / 10.0;
                        var u = 1 - t;
                        list.Add((u * u * x + 2 * u * t * a.X + t * t * e.X, u * u * y + 2 * u * t * a.Y + t * t * e.Y));
                    }

                    x = e.X;
                    y = e.Y;
                    break;
                }

                case 'A':
                {
                    // Elliptical arc: size, rotation, large-arc flag, sweep flag, end point; drawn as a chord.
                    for (var s = 0; s < 5; s++)
                    {
                        Next();
                    }

                    double nx = Next(), ny = Next();
                    x = rel ? x + nx : nx;
                    y = rel ? y + ny : ny;
                    (current ??= StartAt(result, x, y)).Add((x, y));
                    break;
                }

                case 'Z':
                    if (current is not null)
                    {
                        result[^1] = (current, true);
                        x = sx;
                        y = sy;
                        current = null;
                    }

                    command = 'M';
                    break;
                default:
                    k++;
                    break;
            }
        }

        return result;

        static List<(double, double)> StartAt(List<(List<(double, double)>, bool)> list, double px, double py)
        {
            var l = new List<(double, double)> { (px, py) };
            list.Add((l, false));
            return l;
        }
    }
}
