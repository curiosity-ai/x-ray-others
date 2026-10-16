using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace XRay.OneNote.Internal.Pdf;

/// <summary>An image loaded for drawing: its pixel size and the canvas-specific handle.</summary>
internal sealed class PdfImageSource
{
    public required int PixelWidth { get; init; }

    public required int PixelHeight { get; init; }

    public object? Handle { get; init; }
}

/// <summary>
/// The drawing surface the exporter renders onto, in PDF user space (points, origin bottom left).
/// It mirrors the small set of canvas operations the reference writer uses, so tests can record them.
/// </summary>
internal interface IPdfCanvas
{
    public double PageWidth { get; }

    public double PageHeight { get; }

    public PdfFontRegistry Fonts { get; }

    public void SetFont(string name, double size);

    public void DrawString(double x, double y, string text);

    public void SetFillColor(double r, double g, double b);

    public void SetStrokeColor(double r, double g, double b);

    public void SetLineWidth(double width);

    /// <summary>Saves the graphics state (colors, line width, current font).</summary>
    public void SaveState();

    /// <summary>Restores the state saved by the matching <see cref="SaveState"/>.</summary>
    public void RestoreState();

    public void Rect(double x, double y, double width, double height, bool stroke, bool fill);

    public void Line(double x1, double y1, double x2, double y2);

    public void Circle(double centerX, double centerY, double radius, bool stroke, bool fill);

    public void Polygon(IReadOnlyList<(double X, double Y)> points, bool stroke, bool fill);

    /// <summary>Decodes an image for drawing; null when the data is not a supported image.</summary>
    public PdfImageSource? LoadImage(byte[] data);

    /// <summary>Draws an image into a box, scaled to fit and centered when <paramref name="preserveAspectRatio"/> is set.</summary>
    public void DrawImage(PdfImageSource image, double x, double y, double width, double height, bool preserveAspectRatio);

    public void LinkUrl(string url, double x1, double y1, double x2, double y2);

    /// <summary>Ends the current page and starts a new one.</summary>
    public void ShowPage();

    public double StringWidth(string text, string fontName, double size);

    /// <summary>Finishes the document. A trailing page that received no drawing is dropped.</summary>
    public byte[] Save();
}

/// <summary>Writes PDF 1.4 output: one content stream per page, standard and embedded TrueType fonts, images, URI links.</summary>
internal sealed class PdfCanvas : IPdfCanvas
{
    /// <summary>A4 in points, the reference writer's page size.</summary>
    public const double A4Width = 595.2755905511812;
    public const double A4Height = 841.8897637795277;

    private readonly List<PageData> _pages = [];
    private readonly Dictionary<string, ImageXObject> _imagesByHash = new(StringComparer.Ordinal);
    private readonly List<ImageXObject> _images = [];
    private readonly Dictionary<string, string> _fontResourceNames = new(StringComparer.Ordinal);
    private readonly Stack<(string Name, double Size)> _savedFonts = new();
    private PageData _current = new();
    private string _fontName = "Helvetica";
    private double _fontSize = 12;

    public double PageWidth => A4Width;

    public double PageHeight => A4Height;

    public PdfFontRegistry Fonts { get; } = new();

    public void SetFont(string name, double size)
    {
        _fontName = name;
        _fontSize = size;
        _current.Touched = true;
    }

    public void DrawString(double x, double y, string text)
    {
        var font = Fonts.Get(_fontName);
        var resource = FontResource(font);
        _current.FontsUsed.Add(font.Name);
        var encoded = font switch
        {
            EmbeddedTrueTypeFont embedded => PdfSyntax.HexString(embedded.Encode(text)),
            _ => PdfSyntax.LiteralString(StandardFont.Encode(text)),
        };
        Append($"BT {resource} {N(_fontSize)} Tf 1 0 0 1 {N(x)} {N(y)} Tm {encoded} Tj ET");
    }

    public void SetFillColor(double r, double g, double b) => Append($"{N(r)} {N(g)} {N(b)} rg");

    public void SetStrokeColor(double r, double g, double b) => Append($"{N(r)} {N(g)} {N(b)} RG");

    public void SetLineWidth(double width) => Append($"{N(width)} w");

    public void SaveState()
    {
        _savedFonts.Push((_fontName, _fontSize));
        Append("q");
    }

    public void RestoreState()
    {
        if (_savedFonts.Count == 0)
        {
            return;
        }

        (_fontName, _fontSize) = _savedFonts.Pop();
        Append("Q");
    }

    public void Rect(double x, double y, double width, double height, bool stroke, bool fill)
    {
        if (!stroke && !fill)
        {
            return;
        }

        Append($"{N(x)} {N(y)} {N(width)} {N(height)} re {PaintOperator(stroke, fill)}");
    }

    public void Line(double x1, double y1, double x2, double y2) => Append($"{N(x1)} {N(y1)} m {N(x2)} {N(y2)} l S");

    public void Circle(double centerX, double centerY, double radius, bool stroke, bool fill)
    {
        if (!stroke && !fill)
        {
            return;
        }

        // Four cubic Bezier arcs.
        const double k = 0.5522847498;
        var c = radius * k;
        var builder = new StringBuilder();
        builder.Append($"{N(centerX + radius)} {N(centerY)} m ");
        builder.Append($"{N(centerX + radius)} {N(centerY + c)} {N(centerX + c)} {N(centerY + radius)} {N(centerX)} {N(centerY + radius)} c ");
        builder.Append($"{N(centerX - c)} {N(centerY + radius)} {N(centerX - radius)} {N(centerY + c)} {N(centerX - radius)} {N(centerY)} c ");
        builder.Append($"{N(centerX - radius)} {N(centerY - c)} {N(centerX - c)} {N(centerY - radius)} {N(centerX)} {N(centerY - radius)} c ");
        builder.Append($"{N(centerX + c)} {N(centerY - radius)} {N(centerX + radius)} {N(centerY - c)} {N(centerX + radius)} {N(centerY)} c h ");
        builder.Append(PaintOperator(stroke, fill));
        Append(builder.ToString());
    }

    public void Polygon(IReadOnlyList<(double X, double Y)> points, bool stroke, bool fill)
    {
        if (points.Count == 0 || (!stroke && !fill))
        {
            return;
        }

        var builder = new StringBuilder();
        builder.Append($"{N(points[0].X)} {N(points[0].Y)} m");
        for (var i = 1; i < points.Count; i++)
        {
            builder.Append($" {N(points[i].X)} {N(points[i].Y)} l");
        }

        builder.Append(" h ").Append(PaintOperator(stroke, fill));
        Append(builder.ToString());
    }

    /// <summary>Cancels image decoding and saving.</summary>
    public CancellationToken CancellationToken { get; init; }

    public PdfImageSource? LoadImage(byte[] data)
    {
        var hash = Convert.ToHexString(SHA256.HashData(data));
        if (!_imagesByHash.TryGetValue(hash, out var xobject))
        {
            var decoded = ImageDecoder.Decode(data, CancellationToken);
            if (decoded is null)
            {
                return null;
            }

            xobject = new ImageXObject($"Im{_images.Count + 1}", decoded);
            _imagesByHash[hash] = xobject;
            _images.Add(xobject);
        }

        return new PdfImageSource { PixelWidth = xobject.Image.Width, PixelHeight = xobject.Image.Height, Handle = xobject };
    }

    public void DrawImage(PdfImageSource image, double x, double y, double width, double height, bool preserveAspectRatio)
    {
        if (image.Handle is not ImageXObject xobject)
        {
            return;
        }

        if (preserveAspectRatio && image.PixelWidth > 0 && image.PixelHeight > 0)
        {
            var scale = Math.Min(width / image.PixelWidth, height / image.PixelHeight);
            var fittedWidth = scale * image.PixelWidth;
            var fittedHeight = scale * image.PixelHeight;
            x += (width - fittedWidth) / 2;
            y += (height - fittedHeight) / 2;
            width = fittedWidth;
            height = fittedHeight;
        }

        _current.ImagesUsed.Add(xobject);
        Append($"q {N(width)} 0 0 {N(height)} {N(x)} {N(y)} cm /{xobject.Name} Do Q");
    }

    public void LinkUrl(string url, double x1, double y1, double x2, double y2) =>
        _current.Links.Add((url, Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2)));

    public void ShowPage()
    {
        _pages.Add(_current);
        _current = new PageData();
    }

    public double StringWidth(string text, string fontName, double size) => Fonts.Get(fontName).StringWidth(text, size);

    public byte[] Save()
    {
        using var buffer = new MemoryStream();
        Save(buffer);
        return buffer.ToArray();
    }

    /// <summary>Writes the finished file to <paramref name="output"/> without buffering it a second time.</summary>
    public void Save(Stream output, CancellationToken ct = default)
    {
        if (_current.Touched || _current.Content.Length > 0)
        {
            ShowPage();
        }

        new PdfFileWriter(this, ct == default ? CancellationToken : ct).Write(output);
    }

    private static string N(double value) => PdfSyntax.Number(value);

    private static string PaintOperator(bool stroke, bool fill) => (stroke, fill) switch
    {
        (true, true) => "B",
        (true, false) => "S",
        _ => "f",
    };

    private string FontResource(PdfFont font)
    {
        if (!_fontResourceNames.TryGetValue(font.Name, out var resource))
        {
            resource = $"/F{_fontResourceNames.Count + 1}";
            _fontResourceNames[font.Name] = resource;
        }

        return resource;
    }

    private void Append(string operation)
    {
        _current.Content.Append(operation).Append('\n');
        _current.Touched = true;
    }

    private sealed class PageData
    {
        public StringBuilder Content { get; } = new();

        public bool Touched { get; set; }

        public HashSet<string> FontsUsed { get; } = new(StringComparer.Ordinal);

        public List<ImageXObject> ImagesUsed { get; } = [];

        public List<(string Url, double X1, double Y1, double X2, double Y2)> Links { get; } = [];
    }

    private sealed record ImageXObject(string Name, DecodedImage Image);

    /// <summary>Serializes the pages, fonts and images into a PDF file with a cross-reference table.</summary>
    private sealed class PdfFileWriter
    {
        private readonly PdfCanvas _canvas;
        private readonly List<byte[]> _objects = [];
        private readonly CancellationToken _ct;

        public PdfFileWriter(PdfCanvas canvas, CancellationToken ct)
        {
            _canvas = canvas;
            _ct = ct;
        }

        public void Write(Stream output)
        {
            var catalog = Reserve();
            var pagesRoot = Reserve();
            var info = Add(Encoding.ASCII.GetBytes("<< /Producer (X-Ray.OneNote) >>"));

            var fontObjects = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (fontName, _) in _canvas._fontResourceNames)
            {
                fontObjects[fontName] = WriteFont(_canvas.Fonts.Get(fontName));
            }

            var imageObjects = new Dictionary<ImageXObject, int>(ReferenceEqualityComparer.Instance);
            foreach (var image in _canvas._images.Where(image => _canvas._pages.Any(page => page.ImagesUsed.Contains(image))))
            {
                imageObjects[image] = WriteImage(image.Image);
            }

            var pageIds = new List<int>();
            foreach (var page in _canvas._pages)
            {
                _ct.ThrowIfCancellationRequested();
                var content = WriteStream(Encoding.Latin1.GetBytes(page.Content.ToString()), compress: true, "");
                var resources = new StringBuilder("<< /ProcSet [/PDF /Text /ImageB /ImageC /ImageI]");
                if (page.FontsUsed.Count > 0)
                {
                    resources.Append(" /Font <<");
                    foreach (var fontName in page.FontsUsed.Order(StringComparer.Ordinal))
                    {
                        resources.Append(' ').Append(_canvas._fontResourceNames[fontName]).Append(' ').Append(fontObjects[fontName]).Append(" 0 R");
                    }

                    resources.Append(" >>");
                }

                var images = page.ImagesUsed.Distinct().ToList();
                if (images.Count > 0)
                {
                    resources.Append(" /XObject <<");
                    foreach (var image in images)
                    {
                        resources.Append(" /").Append(image.Name).Append(' ').Append(imageObjects[image]).Append(" 0 R");
                    }

                    resources.Append(" >>");
                }

                resources.Append(" >>");

                var annotations = new List<int>();
                foreach (var (url, x1, y1, x2, y2) in page.Links)
                {
                    annotations.Add(Add(Encoding.ASCII.GetBytes(
                        $"<< /Type /Annot /Subtype /Link /Rect [{N(x1)} {N(y1)} {N(x2)} {N(y2)}] /Border [0 0 0] /A << /S /URI /URI {PdfSyntax.TextString(url)} >> >>")));
                }

                var pageDictionary = new StringBuilder();
                pageDictionary.Append($"<< /Type /Page /Parent {pagesRoot} 0 R /MediaBox [0 0 {N(A4Width)} {N(A4Height)}] /Resources {resources} /Contents {content} 0 R");
                if (annotations.Count > 0)
                {
                    pageDictionary.Append(" /Annots [").AppendJoin(' ', annotations.Select(a => $"{a} 0 R")).Append(']');
                }

                pageDictionary.Append(" >>");
                pageIds.Add(Add(Encoding.ASCII.GetBytes(pageDictionary.ToString())));
            }

            Set(pagesRoot, Encoding.ASCII.GetBytes($"<< /Type /Pages /Kids [{string.Join(' ', pageIds.Select(p => $"{p} 0 R"))}] /Count {pageIds.Count} >>"));
            Set(catalog, Encoding.ASCII.GetBytes($"<< /Type /Catalog /Pages {pagesRoot} 0 R >>"));
            Serialize(output, catalog, info);
        }

        private int Reserve()
        {
            _objects.Add([]);
            return _objects.Count;
        }

        private int Add(byte[] body)
        {
            _objects.Add(body);
            return _objects.Count;
        }

        private void Set(int id, byte[] body) => _objects[id - 1] = body;

        private int WriteStream(byte[] data, bool compress, string dictionaryEntries)
        {
            var payload = data;
            var filter = string.Empty;
            if (compress)
            {
                using var buffer = new MemoryStream();
                using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
                {
                    zlib.Write(data);
                }

                payload = buffer.ToArray();
                filter = " /Filter /FlateDecode";
            }

            return AddStream(payload, $"{dictionaryEntries}{filter}");
        }

        private int AddStream(byte[] payload, string dictionaryEntries)
        {
            var header = Encoding.ASCII.GetBytes($"<<{dictionaryEntries} /Length {payload.Length} >>\nstream\n");
            var footer = "\nendstream"u8.ToArray();
            return Add([.. header, .. payload, .. footer]);
        }

        private int WriteFont(PdfFont font)
        {
            if (font is StandardFont)
            {
                return Add(Encoding.ASCII.GetBytes($"<< /Type /Font /Subtype /Type1 /BaseFont /{font.Name} /Encoding /WinAnsiEncoding >>"));
            }

            var embedded = (EmbeddedTrueTypeFont)font;
            var ttf = embedded.Font;
            var glyphs = embedded.UsedGlyphs;
            var tag = SubsetTag(glyphs.Keys);
            var baseFont = $"{tag}+{ttf.PostScriptName}";

            var program = ttf.Subset(glyphs.Keys);
            var fontFile = WriteStream(program, compress: true, $" /Length1 {program.Length}");

            var flags = 4 | (ttf.IsFixedPitch ? 1 : 0) | (ttf.ItalicAngle != 0 ? 64 : 0);
            var stemV = 50 + (int)Math.Pow(ttf.WeightClass / 65.0, 2);
            var bbox = ttf.BoundingBox;
            var descriptor = Add(Encoding.ASCII.GetBytes(
                $"<< /Type /FontDescriptor /FontName /{baseFont} /Flags {flags} /FontBBox [{bbox[0]} {bbox[1]} {bbox[2]} {bbox[3]}] " +
                $"/ItalicAngle {N(ttf.ItalicAngle)} /Ascent {N(ttf.Ascent)} /Descent {N(ttf.Descent)} /CapHeight {N(ttf.CapHeight)} " +
                $"/StemV {stemV} /FontFile2 {fontFile} 0 R >>"));

            var widths = new StringBuilder();
            foreach (var glyph in glyphs.Keys)
            {
                widths.Append(glyph).Append(" [").Append(N(ttf.GlyphWidth(glyph))).Append("] ");
            }

            var cidFont = Add(Encoding.ASCII.GetBytes(
                $"<< /Type /Font /Subtype /CIDFontType2 /BaseFont /{baseFont} " +
                "/CIDSystemInfo << /Registry (Adobe) /Ordering (Identity) /Supplement 0 >> " +
                $"/FontDescriptor {descriptor} 0 R /DW {N(ttf.DefaultWidth)} /W [{widths.ToString().TrimEnd()}] /CIDToGIDMap /Identity >>"));

            var toUnicode = WriteStream(Encoding.ASCII.GetBytes(ToUnicodeCMap(glyphs)), compress: true, "");
            return Add(Encoding.ASCII.GetBytes(
                $"<< /Type /Font /Subtype /Type0 /BaseFont /{baseFont} /Encoding /Identity-H " +
                $"/DescendantFonts [{cidFont} 0 R] /ToUnicode {toUnicode} 0 R >>"));
        }

        private static string SubsetTag(IEnumerable<ushort> glyphs)
        {
            var hash = SHA256.HashData([.. glyphs.SelectMany(g => (byte[])[(byte)(g >> 8), (byte)g])]);
            var tag = new char[6];
            for (var i = 0; i < 6; i++)
            {
                tag[i] = (char)('A' + (hash[i] % 26));
            }

            return new string(tag);
        }

        private static string ToUnicodeCMap(SortedDictionary<ushort, string> glyphs)
        {
            var builder = new StringBuilder();
            builder.Append("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n");
            builder.Append("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n");
            builder.Append("/CMapName /Adobe-Identity-UCS def\n/CMapType 2 def\n");
            builder.Append("1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
            var entries = glyphs.ToList();
            for (var start = 0; start < entries.Count; start += 100)
            {
                var chunk = entries.Skip(start).Take(100).ToList();
                builder.Append(chunk.Count.ToString(CultureInfo.InvariantCulture)).Append(" beginbfchar\n");
                foreach (var (glyph, text) in chunk)
                {
                    builder.Append('<').Append(glyph.ToString("X4", CultureInfo.InvariantCulture)).Append("> <")
                        .Append(Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(text))).Append(">\n");
                }

                builder.Append("endbfchar\n");
            }

            builder.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
            return builder.ToString();
        }

        private int WriteImage(DecodedImage image)
        {
            if (image.Jpeg is { } jpeg)
            {
                var colorSpace = image.Components switch { 1 => "/DeviceGray", 4 => "/DeviceCMYK", _ => "/DeviceRGB" };
                var decode = image.InvertedCmyk ? " /Decode [1 0 1 0 1 0 1 0]" : string.Empty;
                return AddStream(jpeg, $" /Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace {colorSpace} /BitsPerComponent 8{decode} /Filter /DCTDecode");
            }

            var smask = string.Empty;
            if (image.Alpha is { } alpha)
            {
                var mask = WriteStream(alpha, compress: true, $" /Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace /DeviceGray /BitsPerComponent 8");
                smask = $" /SMask {mask} 0 R";
            }

            var space = image.Components == 1 ? "/DeviceGray" : "/DeviceRGB";
            return WriteStream(image.Pixels!, compress: true, $" /Type /XObject /Subtype /Image /Width {image.Width} /Height {image.Height} /ColorSpace {space} /BitsPerComponent 8{smask}");
        }

        private void Serialize(Stream target, int catalog, int info)
        {
            // The trailer carries a hash of everything before it, so the bytes are hashed as they go out.
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long position = 0;
            void Emit(ReadOnlySpan<byte> bytes)
            {
                target.Write(bytes);
                hash.AppendData(bytes);
                position += bytes.Length;
            }

            void Write(string text) => Emit(Encoding.ASCII.GetBytes(text));
            Write("%PDF-1.4\n");
            Emit([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);
            var offsets = new long[_objects.Count];
            for (var i = 0; i < _objects.Count; i++)
            {
                _ct.ThrowIfCancellationRequested();
                offsets[i] = position;
                Write($"{i + 1} 0 obj\n");
                Emit(_objects[i]);
                Write("\nendobj\n");
            }

            var xref = position;
            Write($"xref\n0 {_objects.Count + 1}\n0000000000 65535 f \n");
            foreach (var offset in offsets)
            {
                Write($"{offset.ToString("D10", CultureInfo.InvariantCulture)} 00000 n \n");
            }

            var id = Convert.ToHexString(hash.GetCurrentHash().AsSpan(0, 16));
            Write($"trailer\n<< /Size {_objects.Count + 1} /Root {catalog} 0 R /Info {info} 0 R /ID [<{id}> <{id}>] >>\nstartxref\n{xref}\n%%EOF\n");
        }
    }
}
