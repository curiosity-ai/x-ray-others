using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace XRay.OneNote.Tests.Support;

/// <summary>What a page of a PDF shows: its text in drawing order, link targets and image count.</summary>
internal sealed record PdfPageSummary(string Text, List<string> Links, int ImageCount);

/// <summary>
/// A minimal reader for the PDFs this package writes (uncompressed object syntax, Flate streams,
/// standard WinAnsi fonts and Type0 fonts with ToUnicode maps). It is the test-side counterpart of
/// the pypdf manifests in Goldens/pdf, used to check page count, text, links and images.
/// </summary>
internal sealed partial class PdfInspector
{
    private readonly Dictionary<int, string> _objects = [];
    private readonly Dictionary<int, byte[]> _streams = [];

    public PdfInspector(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var position = 0;
        while (true)
        {
            var header = ObjectHeaderPattern().Match(text, position);
            if (!header.Success)
            {
                break;
            }

            var id = int.Parse(header.Groups[1].Value, CultureInfo.InvariantCulture);
            var bodyStart = header.Index + header.Length;
            var endObject = text.IndexOf("\nendobj", bodyStart, StringComparison.Ordinal);
            var streamAt = text.IndexOf("stream\n", bodyStart, StringComparison.Ordinal);
            if (streamAt >= 0 && streamAt < endObject)
            {
                var dictionary = text[bodyStart..streamAt];
                var length = int.Parse(LengthPattern().Match(dictionary).Groups[1].Value, CultureInfo.InvariantCulture);
                var start = streamAt + "stream\n".Length;
                var raw = pdf.AsSpan(start, length).ToArray();
                _streams[id] = dictionary.Contains("/FlateDecode", StringComparison.Ordinal) ? Inflate(raw) : raw;
                _objects[id] = dictionary;
                position = text.IndexOf("\nendobj", start + length, StringComparison.Ordinal) + 7;
            }
            else
            {
                _objects[id] = text[bodyStart..endObject];
                position = endObject + 7;
            }
        }

        var root = Reference(Trailer(text), "Root");
        var pagesId = Reference(_objects[root], "Pages");
        Pages = [.. Kids(_objects[pagesId]).Select(ReadPage)];
    }

    public List<PdfPageSummary> Pages { get; }

    /// <summary>Text with every white space character removed, for layout-independent comparison.</summary>
    public static string Squash(string text) => WhiteSpacePattern().Replace(text, string.Empty);

    private static string Trailer(string text) => text[text.LastIndexOf("trailer", StringComparison.Ordinal)..];

    private static byte[] Inflate(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
    }

    private static int Reference(string dictionary, string key) =>
        int.Parse(Regex.Match(dictionary, $"/{key} (\\d+) 0 R").Groups[1].Value, CultureInfo.InvariantCulture);

    private static IEnumerable<int> Kids(string pages)
    {
        var kids = Regex.Match(pages, @"/Kids \[([^\]]*)\]").Groups[1].Value;
        return ReferencePattern().Matches(kids).Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    private PdfPageSummary ReadPage(int pageId)
    {
        var page = _objects[pageId];
        var fonts = new Dictionary<string, Func<byte[], string>>(StringComparer.Ordinal);
        var fontDictionary = Regex.Match(page, @"/Font <<([^>]*)>>").Groups[1].Value;
        foreach (Match font in Regex.Matches(fontDictionary, @"/(F\d+) (\d+) 0 R"))
        {
            fonts[font.Groups[1].Value] = Decoder(int.Parse(font.Groups[2].Value, CultureInfo.InvariantCulture));
        }

        var images = Regex.Match(page, @"/XObject <<([^>]*)>>").Groups[1].Value;
        var imageCount = ReferencePattern().Matches(images).Count(m => _objects[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)].Contains("/Subtype /Image", StringComparison.Ordinal));

        var links = new SortedSet<string>(StringComparer.Ordinal);
        var annotations = Regex.Match(page, @"/Annots \[([^\]]*)\]").Groups[1].Value;
        foreach (Match annotation in ReferencePattern().Matches(annotations))
        {
            var body = _objects[int.Parse(annotation.Groups[1].Value, CultureInfo.InvariantCulture)];
            var uri = Regex.Match(body, @"/URI (\((?:\\.|[^\\)])*\)|<[0-9A-Fa-f]*>)");
            if (uri.Success)
            {
                links.Add(DecodeTextString(uri.Groups[1].Value).Trim());
            }
        }

        var content = Encoding.Latin1.GetString(_streams[Reference(page, "Contents")]);
        var text = new StringBuilder();
        var current = fonts.Values.FirstOrDefault();
        double? lastY = null;
        foreach (var line in content.Split('\n'))
        {
            var fontMatch = Regex.Match(line, @"/(F\d+) [\d.]+ Tf");
            if (fontMatch.Success)
            {
                current = fonts[fontMatch.Groups[1].Value];
            }

            var draw = Regex.Match(line, @"Tm (\((?:\\.|[^\\)])*\)|<[0-9A-Fa-f]*>) Tj");
            if (!draw.Success || current is null)
            {
                continue;
            }

            var y = double.Parse(Regex.Match(line, @"1 0 0 1 [-\d.]+ ([-\d.]+) Tm").Groups[1].Value, CultureInfo.InvariantCulture);
            if (lastY is { } previous && Math.Abs(previous - y) > 0.5)
            {
                text.Append('\n');
            }

            lastY = y;
            text.Append(current(DecodeString(draw.Groups[1].Value)));
        }

        return new PdfPageSummary(text.ToString(), [.. links], imageCount);
    }

    private Func<byte[], string> Decoder(int fontId)
    {
        var font = _objects[fontId];
        if (!font.Contains("/Type0", StringComparison.Ordinal))
        {
            return bytes => new string([.. bytes.Select(b => WinAnsi(b))]);
        }

        var map = new Dictionary<int, string>();
        var cmap = Encoding.ASCII.GetString(_streams[Reference(font, "ToUnicode")]);
        foreach (Match entry in Regex.Matches(cmap, @"<([0-9A-Fa-f]{4})> <([0-9A-Fa-f]+)>"))
        {
            map[Convert.ToInt32(entry.Groups[1].Value, 16)] = Encoding.BigEndianUnicode.GetString(Convert.FromHexString(entry.Groups[2].Value));
        }

        return bytes =>
        {
            var builder = new StringBuilder();
            for (var i = 0; i + 1 < bytes.Length; i += 2)
            {
                builder.Append(map.GetValueOrDefault((bytes[i] << 8) | bytes[i + 1], "�"));
            }

            return builder.ToString();
        };
    }

    private static char WinAnsi(byte b)
    {
        const string high = "€\u0081‚ƒ„…†‡ˆ‰Š‹Œ\u008DŽ\u008F\u0090‘’“”•–—˜™š›œ\u009DžŸ";
        return b is >= 0x80 and <= 0x9F ? high[b - 0x80] : (char)b;
    }

    private static byte[] DecodeString(string token)
    {
        if (token.StartsWith('<'))
        {
            return Convert.FromHexString(token[1..^1]);
        }

        var bytes = new List<byte>();
        for (var i = 1; i < token.Length - 1; i++)
        {
            var c = token[i];
            if (c != '\\')
            {
                bytes.Add((byte)c);
                continue;
            }

            var next = token[++i];
            if (next is >= '0' and <= '7')
            {
                var octal = token.Substring(i, 3);
                bytes.Add(Convert.ToByte(octal, 8));
                i += 2;
            }
            else
            {
                bytes.Add((byte)next);
            }
        }

        return [.. bytes];
    }

    private static string DecodeTextString(string token)
    {
        var bytes = DecodeString(token);
        return bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF
            ? Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2)
            : Encoding.Latin1.GetString(bytes);
    }

    [GeneratedRegex(@"(\d+) 0 obj\n")]
    private static partial Regex ObjectHeaderPattern();

    [GeneratedRegex(@"/Length (\d+)")]
    private static partial Regex LengthPattern();

    [GeneratedRegex(@"(\d+) 0 R")]
    private static partial Regex ReferencePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhiteSpacePattern();
}
