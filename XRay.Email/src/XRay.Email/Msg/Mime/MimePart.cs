namespace XRay.Email.Msg.Mime;

internal sealed class MimePart : MimeEntity
{
    public MimePart(IEnumerable<MimeHeader> headers, byte[] rawBody, byte[] decodedBody)
        : base(headers)
    {
        RawBody = rawBody ?? throw new ArgumentNullException(nameof(rawBody));
        DecodedBody = decodedBody ?? throw new ArgumentNullException(nameof(decodedBody));
    }

    public byte[] RawBody { get; }

    public byte[] DecodedBody { get; }

    public bool IsTextPart => MediaType.StartsWith("text/", StringComparison.Ordinal);

    public bool IsAttachment =>
        string.Equals(ContentDisposition, "attachment", StringComparison.OrdinalIgnoreCase) ||
        !string.IsNullOrEmpty(FileName);

    public bool IsInline =>
        string.Equals(ContentDisposition, "inline", StringComparison.OrdinalIgnoreCase) ||
        !string.IsNullOrEmpty(ContentId);

    public string GetText() => CharsetDecoder.DecodeText(DecodedBody, Charset);
}
