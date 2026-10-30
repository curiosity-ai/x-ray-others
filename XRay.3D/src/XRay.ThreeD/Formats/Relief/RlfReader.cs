using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace XRay.ThreeD.Formats.Relief;

/// <summary>
/// Relief height-map files (<c>.rlf</c>): a text banner of about 400 bytes ("NAME  RELIEF FILE  NAME  date", then sentences giving the size in
/// pixels and millimetres, the height range, the position, the vertical resolution and the pixel size), followed by length-prefixed decimal
/// strings and a compressed height raster. The banner is read completely; the raster uses a proprietary delta/run-length coding with no public
/// description, so it is not decoded: <see cref="Model3D.Metadata"/> carries the dimensions and <c>HasGeometry</c> is false.
/// </summary>
internal static class RlfReader
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);

    public static bool Sniff(ReadOnlySpan<byte> d)
    {
        if (d.Length < 64)
        {
            return false;
        }

        var head = Encoding.ASCII.GetString(d[..Math.Min(d.Length, 160)]);
        return head.Contains("RELIEF FILE", StringComparison.Ordinal);
    }

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var d = data.Span;
        if (!Sniff(d))
        {
            throw new ModelFormatException("The file is not a relief (.rlf) file.");
        }

        var textEnd = 0;
        while (textEnd < d.Length && textEnd < 4096 && (d[textEnd] is 9 or 10 or 13 or >= 32 and < 127))
        {
            textEnd++;
        }

        // The banner ends where the first non-text byte starts; the sentences themselves are all printable.
        var text = Encoding.ASCII.GetString(d[..Math.Min(textEnd, 2048)]);
        var model = new Model3D();
        var meta = model.Metadata;
        meta.Format = ModelFormats.Rlf;
        meta.DocumentKind = "heightmap";
        meta.Units = LengthUnit.Millimeter;
        var lines = text.Split(["\r\n", "\n"], StringSplitOptions.None);
        var banner = Regex.Match(lines[0], @"^\s*(?<n>.+?)\s{2,}RELIEF FILE\s+(?<m>.+?)\s{2,}(?<d>\d{1,2} [A-Z]{3} \d{4} \d{1,2}\.\d{2}\.\d{2})\s*$", RegexOptions.None, Timeout);
        if (banner.Success)
        {
            meta.Title = banner.Groups["n"].Value;
            if (DateTime.TryParseExact(banner.Groups["d"].Value, "d MMM yyyy H.mm.ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date))
            {
                meta.Created = new DateTimeOffset(date, TimeSpan.Zero);
            }
        }

        AddFirst(meta, text, @"relief is (\d+) pixels wide by (\d+) pixels high", "Width (pixels)", "Height (pixels)");
        AddFirst(meta, text, @"relief is ([\d.]+)mm wide by ([\d.]+)mm high", "Width (mm)", "Depth (mm)");
        AddFirst(meta, text, @"Maximum height is (-?[\d.]+)mm, minimum height is (-?[\d.]+)mm", "Maximum height (mm)", "Minimum height (mm)");
        AddFirst(meta, text, @"Position is X:(-?[\d.]+)mm Y:(-?[\d.]+)mm Z:(-?[\d.]+)mm", "Position X (mm)", "Position Y (mm)", "Position Z (mm)");
        AddFirst(meta, text, @"Vertical resolution is ([\d.]+)mm, pixel size ([\d.]+)mm", "Vertical resolution (mm)", "Pixel size (mm)");
        AddFirst(meta, text, @"range \+/- ([\d.]+)mm", "Representable height (+/- mm)");
        meta.Notes.Add("The relief raster is compressed with a proprietary coding that has no public description; the dimensions and height range are read, the heights are not decoded.");
        return model;
    }

    private static void AddFirst(ModelMetadata meta, string text, string pattern, params string[] names)
    {
        var m = Regex.Match(text, pattern, RegexOptions.IgnoreCase, Timeout);
        if (!m.Success)
        {
            return;
        }

        for (var i = 0; i < names.Length; i++)
        {
            meta.Properties.Add(new ModelProperty(names[i], m.Groups[i + 1].Value));
        }
    }
}
