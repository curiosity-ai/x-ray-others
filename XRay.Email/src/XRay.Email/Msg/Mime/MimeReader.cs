namespace XRay.Email.Msg.Mime;

internal sealed class MimeReader
{
    /// <summary>Multipart nesting deeper than this is kept as one opaque part instead of recursing.</summary>
    internal const int MaxDepth = 64;

    public MimeMessageModel Read(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            bytes = bytes[3..]; // a UTF-8 byte order mark written by an editor
        }

        var (headerBytes, bodyBytes) = HeaderParser.SplitHeaderAndBody(bytes);
        var headers = HeaderParser.ParseHeaders(headerBytes);
        var rootEntity = ParseEntity(headers, bodyBytes, 0);
        return new MimeMessageModel(headers, rootEntity, CharsetDecoder.DecodeUnknown(headerBytes));
    }

    public MimeMessageModel Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return Read(memory.ToArray());
    }

    public MimeMessageModel ReadFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Read(File.ReadAllBytes(path));
    }

    private static MimeEntity ParseEntity(IReadOnlyList<MimeHeader> headers, byte[] bodyBytes, int depth)
    {
        var (contentType, parameters) = HeaderParser.ParseStructuredHeaderValue(
            headers.FirstOrDefault(header => header.NormalizedName == "content-type")?.RawValue,
            "text/plain");
        if (depth < MaxDepth &&
            contentType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) &&
            parameters.TryGetValue("boundary", out var boundary) &&
            !string.IsNullOrWhiteSpace(boundary))
        {
            var children = MultipartParser.SplitParts(bodyBytes, boundary)
                .Select(part => ParseRawEntity(part, depth + 1))
                .ToArray();
            return new MimeMultipart(headers, boundary, children);
        }

        var decodedBody = TransferEncodingDecoder.Decode(
            headers.FirstOrDefault(header => header.NormalizedName == "content-transfer-encoding")?.Value,
            bodyBytes);
        return new MimePart(headers, bodyBytes, decodedBody);
    }

    private static MimeEntity ParseRawEntity(byte[] bytes, int depth)
    {
        var (headerBytes, bodyBytes) = HeaderParser.SplitHeaderAndBody(bytes);
        var headers = HeaderParser.ParseHeaders(headerBytes);
        return ParseEntity(headers, bodyBytes, depth);
    }
}
