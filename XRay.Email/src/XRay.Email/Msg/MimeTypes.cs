namespace XRay.Email.Msg;

/// <summary>A small extension to media type table for attachments that arrive without a type.</summary>
internal static class MimeTypes
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "text/plain", [".log"] = "text/plain", [".csv"] = "text/csv", [".tsv"] = "text/tab-separated-values",
        [".html"] = "text/html", [".htm"] = "text/html", [".css"] = "text/css", [".xml"] = "application/xml",
        [".json"] = "application/json", [".md"] = "text/markdown", [".ics"] = "text/calendar", [".vcf"] = "text/vcard",
        [".rtf"] = "application/rtf", [".pdf"] = "application/pdf",
        [".doc"] = "application/msword", [".dot"] = "application/msword",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xls"] = "application/vnd.ms-excel",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        [".odt"] = "application/vnd.oasis.opendocument.text", [".ods"] = "application/vnd.oasis.opendocument.spreadsheet",
        [".odp"] = "application/vnd.oasis.opendocument.presentation",
        [".zip"] = "application/zip", [".gz"] = "application/gzip", [".tar"] = "application/x-tar",
        [".7z"] = "application/x-7z-compressed", [".rar"] = "application/vnd.rar",
        [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".jpe"] = "image/jpeg", [".png"] = "image/png",
        [".gif"] = "image/gif", [".bmp"] = "image/bmp", [".tif"] = "image/tiff", [".tiff"] = "image/tiff",
        [".webp"] = "image/webp", [".svg"] = "image/svg+xml", [".ico"] = "image/x-icon", [".psd"] = "image/vnd.adobe.photoshop",
        [".mp3"] = "audio/mpeg", [".wav"] = "audio/wav", [".mp4"] = "video/mp4", [".mov"] = "video/quicktime",
        [".eml"] = "message/rfc822", [".msg"] = "application/vnd.ms-outlook",
    };

    public static string FromFileName(string? fileName)
    {
        var extension = string.IsNullOrEmpty(fileName) ? null : Path.GetExtension(fileName);
        return extension is { Length: > 0 } && ByExtension.TryGetValue(extension, out var type) ? type : "application/octet-stream";
    }

    public static string? ExtensionFor(string? mediaType)
    {
        if (string.IsNullOrEmpty(mediaType))
        {
            return null;
        }

        var bare = mediaType.Split(';')[0].Trim();
        foreach (var pair in ByExtension)
        {
            if (string.Equals(pair.Value, bare, StringComparison.OrdinalIgnoreCase) && pair.Key is not (".jpe" or ".tif" or ".htm" or ".log"))
            {
                return pair.Key;
            }
        }

        return null;
    }

    /// <summary>The image type implied by the first bytes, or null.</summary>
    public static string? SniffImage(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
        {
            return "image/png";
        }

        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (data.Length >= 6 && (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8)))
        {
            return "image/gif";
        }

        if (data.Length >= 2 && data.StartsWith("BM"u8))
        {
            return "image/bmp";
        }

        if (data.Length >= 12 && data.StartsWith("RIFF"u8) && data[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }

        return null;
    }
}
