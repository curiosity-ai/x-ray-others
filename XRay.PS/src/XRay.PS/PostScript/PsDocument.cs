// Ported from ps/document.py, ps/editing.py, ps/serializer.py and ps/xmp.py.

using System.Globalization;
using System.Text;
using XRay.PS.Internal;

namespace XRay.PS.PostScript;

/// <summary>A loaded or editable PostScript or EPS document.</summary>
public sealed class PsDocument
{
    private PsDocument(byte[] data, bool isEps)
    {
        Data = data;
        IsEps = isEps;
    }

    /// <summary>The document bytes (the source bytes until the document is edited and saved).</summary>
    public byte[] Data { get; set; }

    public bool IsEps { get; set; }

    /// <summary>The DSC comments of the source, or null for a new document.</summary>
    public DscMetadata? Dsc { get; set; }

    public string? SourcePath { get; set; }

    public List<PsPage> Pages { get; set; } = [];

    /// <summary>Lines before the first page.</summary>
    public List<string> Prolog { get; set; } = [];

    /// <summary>Lines from <c>%%Trailer</c> on.</summary>
    public List<string> Trailer { get; set; } = [];

    /// <summary>The first line (<c>%!PS-Adobe-...</c>).</summary>
    public string? Header { get; set; }

    /// <summary>True when pages were added, removed or edited since the last save.</summary>
    public bool Dirty { get; set; }

    /// <summary>Creates a new document with a single empty page.</summary>
    public static PsDocument Create(bool isEps = false, double width = 612.0, double height = 792.0) => new([], isEps)
    {
        Pages = [new PsPage(width, height)],
        Header = isEps ? "%!PS-Adobe-3.0 EPSF-3.0" : "%!PS-Adobe-3.0",
    };

    /// <summary>Loads a document from bytes and reads its DSC metadata.</summary>
    public static PsDocument FromBytes(byte[] data)
    {
        bool isEps = DetectEps(data);
        var dsc = DscMetadata.Parse(data);
        var (header, lines) = ExtractHeader(data);
        var (pages, prolog, trailer) = ParsePages(lines, dsc, isEps);
        return new PsDocument(data, isEps)
        {
            Dsc = dsc,
            Pages = pages,
            Prolog = prolog,
            Trailer = trailer,
            Header = header,
        };
    }

    /// <summary>Loads a document from a file.</summary>
    public static PsDocument FromFile(string path)
    {
        var doc = FromBytes(File.ReadAllBytes(path));
        doc.SourcePath = path;
        return doc;
    }

    /// <summary>Loads a document from a stream.</summary>
    public static PsDocument FromStream(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return FromBytes(ms.ToArray());
    }

    public PsPage AddPage(double? width = null, double? height = null)
    {
        var (w, h) = width is not null && height is not null ? (width.Value, height.Value) : DefaultPageSize();
        var page = new PsPage(w, h);
        Pages.Add(page);
        Dirty = true;
        return page;
    }

    public PsPage InsertPage(int index, double? width = null, double? height = null)
    {
        if (index < 0 || index > Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(index), "page index out of range");
        var (w, h) = width is not null && height is not null ? (width.Value, height.Value) : DefaultPageSize();
        var page = new PsPage(w, h);
        Pages.Insert(index, page);
        Dirty = true;
        return page;
    }

    public void RemovePage(int index)
    {
        if (index < 0 || index >= Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(index), "page index out of range");
        Pages.RemoveAt(index);
        Dirty = true;
    }

    public PsPage GetPage(int index)
    {
        if (index < 0 || index >= Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(index), "page index out of range");
        return Pages[index];
    }

    private bool NeedsSerialization => Pages.Count > 0 && (Dirty || Pages.Any(p => p.Dirty) || Data.Length == 0);

    /// <summary>Serializes the document (keeping its XMP packet) and optionally writes it.</summary>
    public byte[] Save(string? path = null)
    {
        byte[] data;
        if (NeedsSerialization)
        {
            data = Serialize();
            string? xmp = Xmp.Extract(Data);
            if (xmp is not null)
                data = Xmp.Replace(data, xmp);
            Data = data;
            Dirty = false;
            foreach (var page in Pages)
                page.Dirty = false;
        }
        else
        {
            data = Data;
        }
        if (!string.IsNullOrEmpty(path))
            File.WriteAllBytes(path, data);
        return data;
    }

    /// <summary>The document bytes, serializing pending edits without saving them.</summary>
    public byte[] AsBytes()
    {
        if (NeedsSerialization)
        {
            byte[] data = Serialize();
            string? xmp = Xmp.Extract(Data);
            if (xmp is not null)
                data = Xmp.Replace(data, xmp);
            return data;
        }
        return Data;
    }

    /// <summary>The XMP metadata packet, if present.</summary>
    public string? GetXmp() => Xmp.Extract(Data.Length > 0 ? Data : AsBytes());

    /// <summary>Sets the XMP metadata packet (EPS only).</summary>
    public void SetXmp(string xmpXml)
    {
        if (!IsEps && !DetectEps(Data))
            throw new InvalidOperationException("EPS document required");
        Data = Xmp.Replace(Data.Length > 0 ? Data : AsBytes(), xmpXml);
    }

    /// <summary>Removes the XMP metadata packet (EPS only).</summary>
    public void RemoveXmp()
    {
        if (!IsEps && !DetectEps(Data))
            throw new InvalidOperationException("EPS document required");
        Data = Xmp.Remove(Data.Length > 0 ? Data : AsBytes());
    }

    /// <summary>Converts the document to PDF.</summary>
    public byte[] ToPdf(PdfSaveOptions? options = null) => PsConverter.ToPdf(this, options);

    /// <summary>Renders the first page to a raster image.</summary>
    public byte[] ToImage(ImageSaveOptions options) => PsConverter.ToImage(this, options);

    /// <summary>Interprets the document and returns its render model.</summary>
    public Rendering.RenderDocument BuildRenderModel(string? additionalFontsFolder = null) =>
        PsConverter.BuildRenderModel(this, additionalFontsFolder).Document;

    internal static bool DetectEps(byte[] data)
    {
        if (data.Length == 0)
            return false;
        string header = Py.Latin1String(data.AsSpan(0, Math.Min(2048, data.Length)));
        var lines = Py.SplitLines(header);
        string firstLine = lines.Count > 0 ? lines[0] : "";
        if (firstLine.StartsWith("%!PS-Adobe-", StringComparison.Ordinal) && firstLine.Contains("EPSF", StringComparison.Ordinal))
            return true;
        return header.Contains("%%BoundingBox", StringComparison.Ordinal);
    }

    private static (string Header, List<string> Lines) ExtractHeader(byte[] data)
    {
        var lines = Py.SplitLines(Py.Latin1String(data));
        if (lines.Count > 0 && lines[0].StartsWith("%!", StringComparison.Ordinal))
            return (lines[0], lines.GetRange(1, lines.Count - 1));
        return ("%!PS-Adobe-3.0", lines);
    }

    private static (List<PsPage>, List<string>, List<string>) ParsePages(List<string> lines, DscMetadata? dsc, bool isEps)
    {
        var (width, height) = PageGeometry.PageSizeFromDsc(dsc);
        var pages = new List<PsPage>();
        var prolog = new List<string>();
        var trailer = new List<string>();
        var current = new List<string>();
        bool inPage = false;
        bool inTrailer = false;
        foreach (string line in lines)
        {
            if (line.StartsWith("%%Page:", StringComparison.Ordinal) && !inTrailer)
            {
                if (inPage)
                {
                    pages.Add(new PsPage(width, height, current));
                    current = [];
                }
                inPage = true;
                continue;
            }
            if (line.StartsWith("%%Trailer", StringComparison.Ordinal))
            {
                if (inPage)
                {
                    pages.Add(new PsPage(width, height, current));
                    current = [];
                    inPage = false;
                }
                inTrailer = true;
                trailer.Add(line);
                continue;
            }
            if (inTrailer)
                trailer.Add(line);
            else if (inPage)
                current.Add(line);
            else
                prolog.Add(line);
        }
        if (inPage)
            pages.Add(new PsPage(width, height, current));
        if (pages.Count == 0)
        {
            int split = lines.FindIndex(l => l.StartsWith("%%EndComments", StringComparison.Ordinal));
            List<string> content;
            if (split >= 0)
            {
                prolog = lines.GetRange(0, split + 1);
                content = lines.GetRange(split + 1, lines.Count - split - 1);
            }
            else
            {
                prolog = [];
                content = lines;
            }
            pages.Add(new PsPage(width, height, content));
            trailer = [];
        }
        var filteredProlog = prolog.Where(l => !l.StartsWith("%%Pages:", StringComparison.Ordinal) && !l.StartsWith("%%Page:", StringComparison.Ordinal)
            && !(isEps && (l.StartsWith("%%BoundingBox:", StringComparison.Ordinal) || l.StartsWith("%%HiResBoundingBox:", StringComparison.Ordinal)))).ToList();
        var filteredTrailer = trailer.Where(l => !l.StartsWith("%%Pages:", StringComparison.Ordinal)).ToList();
        return (pages, filteredProlog, filteredTrailer);
    }

    private (double, double) DefaultPageSize() =>
        Pages.Count > 0 ? (Pages[0].Width, Pages[0].Height) : PageGeometry.PageSizeFromDsc(Dsc);

    /// <summary>Serializes the page model back to PostScript (serialize_document).</summary>
    internal byte[] Serialize()
    {
        var lines = new List<string> { Header ?? (IsEps ? "%!PS-Adobe-3.0 EPSF-3.0" : "%!PS-Adobe-3.0") };
        if (IsEps)
        {
            var bbox = Pages.Count == 0 ? (0L, 0L, 612L, 792L) : (0L, 0L, Py.Round(Pages[0].Width), Py.Round(Pages[0].Height));
            lines.Add($"%%BoundingBox: {bbox.Item1} {bbox.Item2} {bbox.Item3} {bbox.Item4}");
        }
        lines.Add($"%%Pages: {Pages.Count}");
        lines.AddRange(Prolog);
        for (int i = 0; i < Pages.Count; i++)
        {
            var page = Pages[i];
            lines.Add($"%%Page: {i + 1} {i + 1}");
            lines.AddRange(page.Content);
            if (!IsEps && !page.Content.Any(l => Py.Strip(l) == "showpage"))
                lines.Add("showpage");
        }
        lines.AddRange(Trailer);
        string output = string.Join("\n", lines) + "\n";
        foreach (char c in output)
        {
            if (c > '\xff')
                throw new EncoderFallbackException($"'latin-1' codec can't encode character '\\u{(int)c:x4}'");
        }
        return Py.Latin1.GetBytes(output);
    }
}

/// <summary>Raster image data for embedding in a PS/EPS page.</summary>
public sealed record PsImage(int Width, int Height, int BitsPerComponent, string ColorSpace, byte[] Data, string? Filter = null, double[]? Decode = null);

/// <summary>An editable PS/EPS page: its size and PostScript content lines.</summary>
public sealed class PsPage
{
    public PsPage(double width, double height, List<string>? content = null)
    {
        Width = width;
        Height = height;
        Content = content ?? [];
        Canvas = new PsCanvas(this);
    }

    public double Width { get; set; }
    public double Height { get; set; }
    public List<string> Content { get; set; }
    public bool Dirty { get; set; }

    /// <summary>A canvas that appends PostScript operators to this page.</summary>
    public PsCanvas Canvas { get; }
}

/// <summary>Appends drawing operators to a <see cref="PsPage"/>.</summary>
public sealed class PsCanvas(PsPage page)
{
    public void MoveTo(double x, double y) => Append($"{Fmt(x)} {Fmt(y)} moveto");

    public void LineTo(double x, double y) => Append($"{Fmt(x)} {Fmt(y)} lineto");

    public void CurveTo(double x1, double y1, double x2, double y2, double x3, double y3) =>
        Append($"{Fmt(x1)} {Fmt(y1)} {Fmt(x2)} {Fmt(y2)} {Fmt(x3)} {Fmt(y3)} curveto");

    public void ClosePath() => Append("closepath");

    public void Rect(double x, double y, double w, double h)
    {
        Append("newpath");
        MoveTo(x, y);
        LineTo(x + w, y);
        LineTo(x + w, y + h);
        LineTo(x, y + h);
        ClosePath();
    }

    public void Ellipse(double x, double y, double w, double h)
    {
        double cx = x + w / 2.0;
        double cy = y + h / 2.0;
        double rx = w / 2.0;
        double ry = h / 2.0;
        Append("matrix currentmatrix");
        Append($"{Fmt(cx)} {Fmt(cy)} translate");
        Append($"{Fmt(rx)} {Fmt(ry)} scale");
        Append("0 0 1 0 360 arc");
        Append("setmatrix");
    }

    public void Stroke() => Append("stroke");

    public void Fill() => Append("fill");

    public void FillStroke()
    {
        Append("gsave");
        Append("fill");
        Append("grestore");
        Append("stroke");
    }

    public void SetStrokeColor(params double[] color) => SetColor(color);

    public void SetFillColor(params double[] color) => SetColor(color);

    public void SetLineWidth(double width) => Append($"{Fmt(width)} setlinewidth");

    public void SetLineCap(int cap) => Append($"{cap} setlinecap");

    public void SetLineJoin(int join) => Append($"{join} setlinejoin");

    public void SetMiterLimit(double limit) => Append($"{Fmt(limit)} setmiterlimit");

    public void Concat(params double[] matrix)
    {
        if (matrix.Length != 6)
            throw new ArgumentException("concat matrix must have 6 elements");
        Append($"[{string.Join(" ", matrix.Select(Fmt))}] concat");
    }

    public void SaveState() => Append("gsave");

    public void RestoreState() => Append("grestore");

    public void Clip()
    {
        Append("clip");
        Append("newpath");
    }

    public void InitClip() => Append("initclip");

    public void DrawText(string text, double x, double y, string fontName, double size)
    {
        string escaped = text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal);
        Append($"/{fontName} findfont {Fmt(size)} scalefont setfont");
        Append($"{Fmt(x)} {Fmt(y)} moveto");
        Append($"({escaped}) show");
    }

    public void DrawImage(PsImage image, double x, double y, double w, double h)
    {
        int components = image.ColorSpace switch
        {
            "DeviceGray" => 1,
            "DeviceRGB" => 3,
            "DeviceCMYK" => 4,
            _ => throw new ArgumentException("unsupported color space"),
        };
        if (image.BitsPerComponent % 8 != 0)
            throw new ArgumentException("bits_per_component must be a multiple of 8");
        long expected = (long)image.Width * image.Height * components * (image.BitsPerComponent / 8);
        if (image.Data.Length != expected)
            throw new ArgumentException("image data size mismatch");
        Append("gsave");
        Append($"{Fmt(x)} {Fmt(y)} translate");
        Append($"{Fmt(w)} {Fmt(h)} scale");
        Append($"{image.Width} {image.Height} {image.BitsPerComponent} [{image.Width} 0 0 -{image.Height} 0 {image.Height}]");
        string hex = Convert.ToHexStringLower(image.Data);
        Append(components == 1 ? $"{{<{hex}>}} image" : $"{{<{hex}>}} false {components} colorimage");
        Append("grestore");
    }

    private void SetColor(double[] color)
    {
        switch (color.Length)
        {
            case 1:
                Append($"{Fmt(color[0])} setgray");
                break;
            case 3:
                Append($"{Fmt(color[0])} {Fmt(color[1])} {Fmt(color[2])} setrgbcolor");
                break;
            case 4:
                Append($"{Fmt(color[0])} {Fmt(color[1])} {Fmt(color[2])} {Fmt(color[3])} setcmykcolor");
                break;
            default:
                throw new ArgumentException("unsupported color tuple length");
        }
    }

    private void Append(string line)
    {
        page.Dirty = true;
        page.Content.Add(line);
    }

    internal static string Fmt(double value)
    {
        long asInt = Py.Int(value);
        if ((double)asInt == value)
            return asInt.ToString(CultureInfo.InvariantCulture);
        return Py.FormatF(value, 6).TrimEnd('0').TrimEnd('.');
    }
}

/// <summary>XMP metadata packets in EPS files.</summary>
public static class Xmp
{
    public static string? Extract(byte[] data)
    {
        var packet = ExtractBetweenMarkers(data, "%%BeginXMP"u8, "%%EndXMP"u8);
        if (packet is not null)
            return Py.DecodeUtf8OrLatin1(packet);
        var (start, end) = FindXmpMetaBounds(data);
        if (start == -1 || end == -1)
            return null;
        return Py.DecodeUtf8OrLatin1(data.AsSpan(start, end - start));
    }

    public static byte[] Replace(byte[] data, string xmpXml)
    {
        EnsureXml(xmpXml);
        byte[] packet = BuildPacket(xmpXml);
        int begin = data.AsSpan().IndexOf("%%BeginXMP"u8);
        if (begin != -1)
        {
            int end = IndexOf(data, "%%EndXMP"u8, begin);
            if (end != -1)
            {
                int blockEnd = LineEnd(data, end);
                return [.. data.AsSpan(0, begin), .. packet, .. data.AsSpan(blockEnd)];
            }
        }
        var (xmpStart, xmpEnd) = FindXmpMetaBounds(data);
        if (xmpStart != -1 && xmpEnd != -1)
            return [.. data.AsSpan(0, xmpStart), .. Encoding.UTF8.GetBytes(xmpXml), .. data.AsSpan(xmpEnd)];
        int insertAt = FindInsertionIndex(data);
        return [.. data.AsSpan(0, insertAt), .. packet, .. data.AsSpan(insertAt)];
    }

    public static byte[] Remove(byte[] data)
    {
        int begin = data.AsSpan().IndexOf("%%BeginXMP"u8);
        if (begin != -1)
        {
            int end = IndexOf(data, "%%EndXMP"u8, begin);
            if (end != -1)
            {
                int blockEnd = LineEnd(data, end);
                return [.. data.AsSpan(0, begin), .. data.AsSpan(blockEnd)];
            }
        }
        var (xmpStart, xmpEnd) = FindXmpMetaBounds(data);
        if (xmpStart != -1 && xmpEnd != -1)
            return [.. data.AsSpan(0, xmpStart), .. data.AsSpan(xmpEnd)];
        return data;
    }

    private static void EnsureXml(string xml)
    {
        try
        {
            var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null };
            using var reader = System.Xml.XmlReader.Create(new StringReader(xml), settings);
            while (reader.Read())
            {
            }
        }
        catch (System.Xml.XmlException ex)
        {
            throw new ArgumentException("invalid XMP XML", ex);
        }
    }

    private static byte[] BuildPacket(string xml) => Encoding.UTF8.GetBytes("%%BeginXMP\n" + Py.Strip(xml) + "\n%%EndXMP\n");

    private static byte[]? ExtractBetweenMarkers(byte[] data, ReadOnlySpan<byte> startMarker, ReadOnlySpan<byte> endMarker)
    {
        int start = data.AsSpan().IndexOf(startMarker);
        if (start == -1)
            return null;
        int end = IndexOf(data, endMarker, start);
        if (end == -1)
            return null;
        int contentStart = Array.IndexOf(data, (byte)'\n', start);
        contentStart = contentStart == -1 ? start + startMarker.Length : contentStart + 1;
        if (contentStart > end)
            return [];
        return Py.StripBytes(data.AsSpan(contentStart, end - contentStart)).ToArray();
    }

    private static (int Start, int End) FindXmpMetaBounds(byte[] data)
    {
        int start = data.AsSpan().IndexOf("<x:xmpmeta"u8);
        if (start == -1)
            return (-1, -1);
        int end = IndexOf(data, "</x:xmpmeta>"u8, start);
        if (end == -1)
            return (-1, -1);
        return (start, end + "</x:xmpmeta>".Length);
    }

    private static int FindInsertionIndex(byte[] data)
    {
        var lines = Py.SplitLinesKeepEnds(Py.Latin1String(data));
        if (lines.Count == 0)
            return 0;
        int offset = lines[0].Length;
        int? insertAt = null;
        for (int i = 1; i < lines.Count; i++)
        {
            string line = lines[i];
            if (line.StartsWith("%%EndComments", StringComparison.Ordinal))
            {
                insertAt = offset + line.Length;
                break;
            }
            if (line.StartsWith("%%Page:", StringComparison.Ordinal))
            {
                insertAt = offset;
                break;
            }
            offset += line.Length;
        }
        return insertAt ?? offset;
    }

    private static int LineEnd(byte[] data, int pos)
    {
        int end = Array.IndexOf(data, (byte)'\n', pos);
        return end == -1 ? data.Length : end + 1;
    }

    private static int IndexOf(byte[] data, ReadOnlySpan<byte> needle, int start)
    {
        int idx = data.AsSpan(start).IndexOf(needle);
        return idx < 0 ? -1 : idx + start;
    }
}
