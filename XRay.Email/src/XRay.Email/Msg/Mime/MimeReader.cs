namespace XRay.Email.Msg.Mime;

internal sealed class MimeReader
{
    /// <summary>Multipart nesting deeper than this is kept as one opaque part instead of recursing.</summary>
    internal const int MaxDepth = 64;

    private readonly CancellationToken _cancellationToken;

    public MimeReader()
        : this(CancellationToken.None)
    {
    }

    public MimeReader(CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
    }

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
        return Read(ReadAll(stream, _cancellationToken));
    }

    public MimeMessageModel ReadFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
        return Read(ReadAll(file, _cancellationToken));
    }

    /// <summary>
    /// Reads the rest of a stream into one array. A seekable stream is read into an array of exactly
    /// the remaining size (no growth copies); other streams grow geometrically.
    /// </summary>
    internal static byte[] ReadAll(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream.CanSeek)
        {
            var remaining = stream.Length - stream.Position;
            if (remaining > Array.MaxLength)
            {
                throw new InvalidDataException("The message is too large to load.");
            }

            var exact = new byte[remaining];
            var filled = 0;
            while (filled < exact.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = stream.Read(exact, filled, (int)Math.Min(exact.Length - filled, 1 << 20));
                if (read == 0)
                {
                    return exact.AsSpan(0, filled).ToArray();
                }

                filled += read;
            }

            return exact;
        }

        var buffer = new byte[81920];
        var length = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (length == buffer.Length)
            {
                Array.Resize(ref buffer, checked(buffer.Length * 2));
            }

            var read = stream.Read(buffer, length, buffer.Length - length);
            if (read == 0)
            {
                break;
            }

            length += read;
        }

        Array.Resize(ref buffer, length);
        return buffer;
    }

    private MimeEntity ParseEntity(IReadOnlyList<MimeHeader> headers, byte[] bodyBytes, int depth)
    {
        _cancellationToken.ThrowIfCancellationRequested();
        var (contentType, parameters) = HeaderParser.ParseStructuredHeaderValue(
            headers.FirstOrDefault(header => header.NormalizedName == "content-type")?.RawValue,
            "text/plain");
        if (depth < MaxDepth &&
            contentType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase) &&
            parameters.TryGetValue("boundary", out var boundary) &&
            !string.IsNullOrWhiteSpace(boundary))
        {
            var children = MultipartParser.SplitParts(bodyBytes, boundary, _cancellationToken)
                .Select(part => ParseRawEntity(part, depth + 1))
                .ToArray();
            return new MimeMultipart(headers, boundary, children);
        }

        var decodedBody = TransferEncodingDecoder.Decode(
            headers.FirstOrDefault(header => header.NormalizedName == "content-transfer-encoding")?.Value,
            bodyBytes);
        return new MimePart(headers, bodyBytes, decodedBody);
    }

    private MimeEntity ParseRawEntity(byte[] bytes, int depth)
    {
        var (headerBytes, bodyBytes) = HeaderParser.SplitHeaderAndBody(bytes);
        var headers = HeaderParser.ParseHeaders(headerBytes);
        return ParseEntity(headers, bodyBytes, depth);
    }
}
