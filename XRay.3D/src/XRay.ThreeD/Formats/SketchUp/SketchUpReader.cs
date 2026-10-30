using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace XRay.ThreeD.Formats.SketchUp;

using XRay.ThreeD.Internal;

/// <summary>
/// SketchUp models (<c>.skp</c>): the version and the thumbnail, and for SketchUp 2021 and later the package structure (materials, styles, textures).
/// Every file starts with two UTF-16 strings, each introduced by <c>FF FE FF</c> and a character count: <c>SketchUp Model</c> and the version in braces
/// (<c>{23.2.0}</c>; major 13 and up is the release year minus 2000). From 2021 the rest is a ZIP package behind that prefix (a <c>VFF</c> marker follows
/// the version): <c>meta/model_thumbnail.png</c>, <c>materials/&lt;name&gt;/material.xml</c> with the colour, opacity and texture of each material, the
/// textures, <c>styles/&lt;name&gt;/style.xml</c> and <c>model.dat</c>, the model itself. Earlier files (2013 to 2020 and the older ones) are one
/// serialised object stream with the thumbnail PNG inside it. The geometry in <c>model.dat</c> and in the older stream is proprietary and is not decoded.
/// </summary>
internal static class SketchUpReader
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static bool Sniff(ReadOnlySpan<byte> d) =>
        d.Length > 40 && d[0] == 0xFF && d[1] == 0xFE && d[2] == 0xFF && d[3] == 14 && d.Slice(4, 28).SequenceEqual("SketchUp Model"u8.ToArray().SelectMany(b => new byte[] { b, 0 }).ToArray());

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var meta = ReadMetadata(data, options);
        throw new UnsupportedModelFormatException(ModelFormats.SketchUp, "SketchUp geometry is proprietary and not decoded (" + meta.Application + "); the version, thumbnail, materials and styles are available through ReadMetadata and ReadPreview.");
    }

    public static PreviewImage? ReadPreview(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var png = Thumbnail(data, options.Cancellation, out _);
        return png is null ? null : new PreviewImage("image/png", png);
    }

    public static ModelMetadata ReadMetadata(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var token = options.Cancellation;
        var d = data.Span;
        if (!Sniff(d))
        {
            throw new ModelFormatException("Not a SketchUp model: the header is missing.");
        }

        var meta = new ModelMetadata { Format = ModelFormats.SketchUp, DocumentKind = "model", HasGeometry = false, Units = LengthUnit.Inch };
        var p = 4 + 28;
        if (d.Length < p + 4 || d[p] != 0xFF || d[p + 1] != 0xFE || d[p + 2] != 0xFF)
        {
            throw new ModelFormatException("The SketchUp header has no version.");
        }

        var chars = d[p + 3];
        if (p + 4 + 2 * chars > d.Length)
        {
            throw new ModelFormatException("The SketchUp version string is cut off.");
        }

        var version = Encoding.Unicode.GetString(d.Slice(p + 4, 2 * chars)).Trim('{', '}');
        var rest = d[(p + 4 + 2 * chars)..];
        var vff = rest.Length >= 3 && rest[..3].SequenceEqual("VFF"u8);
        meta.FormatVersion = version;
        var major = int.TryParse(version.AsSpan(0, Math.Max(0, version.IndexOf('.'))), NumberStyles.None, CultureInfo.InvariantCulture, out var m) ? m : 0;
        meta.Application = major >= 13 ? "SketchUp 20" + major.ToString("00", CultureInfo.InvariantCulture) + " (" + version + ")" : "SketchUp " + major.ToString(CultureInfo.InvariantCulture) + " (" + version + ")";
        meta.Notes.Add(vff ? "ZIP-based package (SketchUp 2021 and later)." : "Serialised object stream (SketchUp 2020 and earlier).");

        if (vff)
        {
            ReadPackage(data, meta, token);
        }
        else
        {
            meta.HasPreview = Thumbnail(data, token, out _) is not null;
        }

        meta.Notes.Add("SketchUp geometry is proprietary and is not decoded; only the version and preview" + (vff ? ", materials and styles" : "") + " are read. Lengths are stored in inches.");
        return meta;
    }

    /// <summary>The thumbnail PNG: <c>meta/model_thumbnail.png</c> of a package, or the first PNG in the first 256 KiB of an older stream.</summary>
    private static byte[]? Thumbnail(ReadOnlyMemory<byte> data, CancellationToken token, out string? source)
    {
        source = null;
        var d = data.Span;
        var p = 4 + 28 + 4 + 2 * d[4 + 28 + 3];
        if (p + 3 <= d.Length && d.Slice(p, 3).SequenceEqual("VFF"u8))
        {
            if (OpenPackage(data) is { } zip)
            {
                using (zip)
                {
                    foreach (var name in new[] { "meta/model_thumbnail.png", "meta/preview_thumbnail.png" })
                    {
                        if (ZipEntries.Find(zip, name) is { } entry && ZipEntries.Read(entry, 1 << 24, token) is { Length: > 8 } png && png.AsSpan().StartsWith(PngSignature))
                        {
                            source = name;
                            return png;
                        }
                    }
                }
            }

            return null;
        }

        var head = d[..Math.Min(d.Length, 1 << 18)];
        return FindPng(head) is { } found ? found : null;
    }

    private static byte[]? FindPng(ReadOnlySpan<byte> bytes)
    {
        var start = bytes.IndexOf(PngSignature);
        if (start < 0)
        {
            return null;
        }

        var p = start + 8;
        for (var chunks = 0; chunks < 10_000 && p + 12 <= bytes.Length; chunks++)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes[p..]);
            if (length > bytes.Length - p - 12)
            {
                return null;
            }

            var end = bytes.Slice(p + 4, 4).SequenceEqual("IEND"u8);
            p += 12 + (int)length;
            if (end)
            {
                return bytes[start..p].ToArray();
            }
        }

        return null;
    }

    /// <summary>Opens the ZIP package that follows the prefix: central-directory offsets count from the start of the package, not of the file.</summary>
    private static ZipArchive? OpenPackage(ReadOnlyMemory<byte> data)
    {
        var d = data.Span;
        var from = Math.Max(0, d.Length - 65557);
        var eocd = d[from..].LastIndexOf(new byte[] { 0x50, 0x4B, 0x05, 0x06 });
        if (eocd < 0 || from + eocd + 22 > d.Length)
        {
            return null;
        }

        eocd += from;
        var size = BinaryPrimitives.ReadUInt32LittleEndian(d[(eocd + 12)..]);
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(d[(eocd + 16)..]);
        var start = (long)eocd - size - offset;
        if (start < 0 || start > eocd)
        {
            return null;
        }

        try
        {
            var zip = new ZipArchive(new MemoryStream(data.Slice((int)start).ToArray(), writable: false), ZipArchiveMode.Read);
            if (zip.Entries.Count > ZipEntries.MaxEntries)
            {
                zip.Dispose();
                return null;
            }

            return zip;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static void ReadPackage(ReadOnlyMemory<byte> data, ModelMetadata meta, CancellationToken token)
    {
        using var zip = OpenPackage(data);
        if (zip is null)
        {
            meta.Notes.Add("The package structure could not be read.");
            return;
        }

        meta.HasPreview = ZipEntries.Find(zip, "meta/model_thumbnail.png") is not null || ZipEntries.Find(zip, "meta/preview_thumbnail.png") is not null;
        var textures = 0;
        var styles = new HashSet<string>(StringComparer.Ordinal);
        var tick = 0L;
        foreach (var entry in zip.Entries)
        {
            Cancel.Every(token, tick++);
            var name = entry.FullName;
            if (name.StartsWith("materials/", StringComparison.Ordinal) && name.EndsWith("/material.xml", StringComparison.Ordinal) && entry.Length < (1 << 20))
            {
                ReadMaterial(entry, meta, token);
            }
            else if (name.StartsWith("materials/", StringComparison.Ordinal) && !name.EndsWith(".xml", StringComparison.Ordinal) && !name.EndsWith('/'))
            {
                textures++;
            }
            else if (name.StartsWith("styles/", StringComparison.Ordinal) && name.EndsWith("/style.xml", StringComparison.Ordinal))
            {
                var style = name["styles/".Length..^"/style.xml".Length];
                var underscore = style.LastIndexOf('_');
                if (underscore > 0 && style[(underscore + 1)..].All(char.IsAsciiDigit))
                {
                    style = style[..underscore];
                }

                if (styles.Add(style))
                {
                    meta.Properties.Add(new ModelProperty("Style", style, "styles"));
                }
            }
        }

        if (textures > 0)
        {
            meta.Properties.Add(new ModelProperty("Texture images", textures.ToString(CultureInfo.InvariantCulture), "materials"));
        }

        if (ZipEntries.Find(zip, "model.dat") is { } model)
        {
            meta.Properties.Add(new ModelProperty("Model data", model.Length.ToString(CultureInfo.InvariantCulture) + " bytes", "package"));
        }
    }

    private static void ReadMaterial(ZipArchiveEntry entry, ModelMetadata meta, CancellationToken token)
    {
        XDocument? doc;
        try
        {
            using var stream = new MemoryStream(ZipEntries.Read(entry, 1 << 20, token), writable: false);
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1 << 20 };
            using var reader = XmlReader.Create(stream, settings);
            doc = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return;
        }

        foreach (var material in doc.Descendants().Where(e => e.Name.LocalName == "material" && e.Attribute("name") is not null).Take(64))
        {
            var name = (string)material.Attribute("name")!;
            if (name.StartsWith("Layer_", StringComparison.Ordinal))
            {
                // A tag (layer) is stored as a material that carries its colour.
                if (!meta.LayerNames.Contains(name[6..]))
                {
                    meta.LayerNames.Add(name[6..]);
                }

                continue;
            }

            if (meta.MaterialNames.Contains(name))
            {
                continue;
            }

            meta.MaterialNames.Add(name);
            int Channel(string attribute) => int.TryParse((string?)material.Attribute(attribute), NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, 0, 255) : 0;
            var text = "#" + Channel("colorRed").ToString("X2", CultureInfo.InvariantCulture) + Channel("colorGreen").ToString("X2", CultureInfo.InvariantCulture) + Channel("colorBlue").ToString("X2", CultureInfo.InvariantCulture);
            if ((string?)material.Attribute("useTrans") == "1" && double.TryParse((string?)material.Attribute("trans"), NumberStyles.Float, CultureInfo.InvariantCulture, out var transparency))
            {
                text += ", opacity " + (1 - transparency).ToString("0.##", CultureInfo.InvariantCulture);
            }

            if ((string?)material.Attribute("hasTexture") == "1")
            {
                text += ", textured";
            }

            meta.Properties.Add(new ModelProperty(name, text, "materials"));
        }
    }
}
