using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace XRay.ThreeD.Formats.Cad;

using XRay.ThreeD.Internal;

/// <summary>
/// Formats that keep their document in an OLE compound file and whose geometry is proprietary: Autodesk Inventor (.ipt, .iam, .idw) and
/// Revit (.rvt, .rfa, .rft). Only what the container documents is read: the standard SummaryInformation and DocumentSummaryInformation
/// property sets (title, author, dates, application, company, thumbnail) and, for Revit, the text of the BasicFileInfo stream and the
/// PNG inside its preview stream. Geometry is not decoded, and <see cref="Read"/> says so.
/// </summary>
internal static partial class CompoundDocumentReader
{
    private const string SummaryStream = "\u0005SummaryInformation";
    private const string DocumentSummaryStream = "\u0005DocumentSummaryInformation";

    public static Model3D Read(FormatInfo format, ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var meta = ReadMetadata(format, data, options);
        throw new UnsupportedModelFormatException(format, $"{format.Name} geometry is proprietary and not decoded; the document properties ({meta.Properties.Count} found) and the preview are available through ReadMetadata and ReadPreview.");
    }

    public static ModelMetadata ReadMetadata(FormatInfo format, ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var token = options.Cancellation;
        var file = new CompoundFile(data, token);
        var meta = new ModelMetadata { Format = format, DocumentKind = format.Id == "revit" ? "bim" : "cad", HasGeometry = false };

        if (file.Read(SummaryStream, token, 1 << 24) is { } summaryBytes)
        {
            var summary = OlePropertySet.Parse(summaryBytes, token);
            meta.Title = Clean(summary.Text(2));
            Add(meta, "Subject", summary.Text(3));
            meta.Author = Clean(summary.Text(4));
            Add(meta, "Keywords", summary.Text(5));
            Add(meta, "Comments", summary.Text(6));
            meta.LastModifiedBy = Clean(summary.Text(8));
            meta.Created = summary.Time(12);
            meta.Modified = summary.Time(13);
            meta.Application = Clean(summary.Text(18)) ?? meta.Application;
            meta.HasPreview = summary.Blob(17) is { } blob && Sniff(blob, token) is not null;
        }

        if (file.Read(DocumentSummaryStream, token, 1 << 24) is { } documentBytes)
        {
            var document = OlePropertySet.Parse(documentBytes, token);
            meta.Company = Clean(document.Text(15));
            Add(meta, "Category", document.Text(2));
            Add(meta, "Manager", document.Text(14));
        }

        if (format.Id == "revit")
        {
            ReadRevitInfo(file, meta, token);
        }

        meta.Notes.Add($"{format.Name} geometry is proprietary and is not decoded; only the document properties and the preview are read.");
        return meta;
    }

    public static PreviewImage? ReadPreview(FormatInfo format, ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var token = options.Cancellation;
        var file = new CompoundFile(data, token);
        if (format.Id == "revit")
        {
            foreach (var name in file.StreamNames.Where(n => n.StartsWith("RevitPreview", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
            {
                if (file.Read(name, token, 1 << 24) is { } bytes && FindPng(bytes) is { } png)
                {
                    return new PreviewImage("image/png", png);
                }
            }
        }

        return file.Read(SummaryStream, token, 1 << 24) is { } summaryBytes && OlePropertySet.Parse(summaryBytes, token).Blob(17) is { } blob ? Sniff(blob, token) : null;
    }

    private static void ReadRevitInfo(CompoundFile file, ModelMetadata meta, CancellationToken token)
    {
        if (file.Read("BasicFileInfo", token, 1 << 22) is not { Length: > 8 } bytes)
        {
            return;
        }

        // The stream is UTF-16 text of "Key: value" lines after a short binary header; control characters become line breaks.
        var chars = Encoding.Unicode.GetString(bytes, 0, bytes.Length & ~1).Select(c => c < ' ' && c is not ('\r' or '\n') ? '\n' : c).ToArray();
        var text = new string(chars);
        foreach (Match m in RevitLine().Matches(text))
        {
            token.ThrowIfCancellationRequested();
            var key = m.Groups[1].Value.Trim();
            var value = m.Groups[2].Value.Trim();
            switch (key)
            {
                case "Revit Build":
                    meta.Application = value;
                    break;
                case "Format":
                    meta.FormatVersion = value;
                    break;
                case "Username" or "Worksharing" or "Central Model Path" or "Last Save Path" or "Locale" or "Open Workset Default":
                    Add(meta, key, value);
                    break;
            }
        }
    }

    [GeneratedRegex(@"(Revit Build|Format|Username|Worksharing|Central Model Path|Last Save Path|Locale|Open Workset Default):[ \t]*([^\r\n]*)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 2000)]
    private static partial Regex RevitLine();

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void Add(ModelMetadata meta, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            meta.Properties.Add(new ModelProperty(name, value.Trim(), "document"));
        }
    }

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>The PNG that starts at the first signature in <paramref name="bytes"/>, ending after its IEND chunk.</summary>
    private static byte[]? FindPng(ReadOnlySpan<byte> bytes)
    {
        var start = bytes.IndexOf(PngSignature);
        if (start < 0)
        {
            return null;
        }

        var p = start + 8;
        for (var chunks = 0; chunks < 100_000 && p + 12 <= bytes.Length; chunks++)
        {
            var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes[p..]);
            if (length > bytes.Length - p - 12)
            {
                return null;
            }

            var isEnd = bytes.Slice(p + 4, 4).SequenceEqual("IEND"u8);
            p += 12 + (int)length;
            if (isEnd)
            {
                return bytes[start..p].ToArray();
            }
        }

        return null;
    }

    /// <summary>Clipboard thumbnail data as a PNG: stored PNG as is, a DIB (with or without a file header) converted.</summary>
    private static PreviewImage? Sniff(byte[] blob, CancellationToken token)
    {
        if (FindPng(blob) is { } png && blob.AsSpan().StartsWith(PngSignature))
        {
            return new PreviewImage("image/png", png);
        }

        if (blob.Length > 2 && blob[0] == 0xFF && blob[1] == 0xD8)
        {
            return new PreviewImage("image/jpeg", blob);
        }

        return DibImage.DecodeRgb(blob, out var w, out var h, token) is { } rgb ? new PreviewImage("image/png", DibImage.EncodePng(rgb, w, h)) : null;
    }
}
