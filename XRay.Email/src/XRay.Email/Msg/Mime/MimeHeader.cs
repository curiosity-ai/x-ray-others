namespace XRay.Email.Msg.Mime;

/// <summary>One header field. <see cref="RawValue"/> is the unfolded wire form; <see cref="Value"/> has its RFC 2047 encoded words decoded.</summary>
internal sealed class MimeHeader
{
    public MimeHeader(string name, string value)
        : this(name, value, value)
    {
    }

    public MimeHeader(string name, string value, string rawValue)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        NormalizedName = name.Trim().ToLowerInvariant();
        Value = value ?? string.Empty;
        RawValue = rawValue ?? string.Empty;
    }

    /// <summary>Creates a header from its wire form, decoding encoded words for <see cref="Value"/>.</summary>
    public static MimeHeader FromWire(string name, string rawValue) => new(name, HeaderParser.DecodeEncodedWords(rawValue), rawValue);

    public string Name { get; }

    public string NormalizedName { get; }

    public string Value { get; }

    public string RawValue { get; }
}
