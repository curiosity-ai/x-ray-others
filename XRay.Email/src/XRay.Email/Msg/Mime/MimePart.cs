namespace XRay.Email.Msg.Mime;

internal sealed class MimePart : MimeEntity
{
    public MimePart(IEnumerable<MimeHeader> headers, byte[] rawBody, byte[] decodedBody)
        : base(headers)
    {
        RawBody = rawBody ?? throw new ArgumentNullException(nameof(rawBody));
        DecodedBody = decodedBody ?? throw new ArgumentNullException(nameof(decodedBody));
    }

    /// <summary>
    /// A part whose body is encoded while it is written, so a large attachment is never held a second
    /// time as base64 text. <see cref="RawBody"/> is empty for such a part.
    /// </summary>
    public static MimePart Deferred(IEnumerable<MimeHeader> headers, byte[] decodedBody) => new(headers, [], decodedBody) { DeferEncoding = true };

    public bool DeferEncoding { get; private init; }

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
