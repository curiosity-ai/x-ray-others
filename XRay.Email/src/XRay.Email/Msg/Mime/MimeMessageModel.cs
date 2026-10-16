namespace XRay.Email.Msg.Mime;

internal sealed class MimeMessageModel
{
    public MimeMessageModel(IEnumerable<MimeHeader> headers, MimeEntity rootEntity, string? rawHeaderText = null)
    {
        Headers = headers?.ToArray() ?? throw new ArgumentNullException(nameof(headers));
        RootEntity = rootEntity ?? throw new ArgumentNullException(nameof(rootEntity));
        RawHeaderText = rawHeaderText;
    }

    /// <summary>The header block as read from the wire, when the model was parsed.</summary>
    public string? RawHeaderText { get; }

    public IReadOnlyList<MimeHeader> Headers { get; }

    public MimeEntity RootEntity { get; }

    public string? Subject => GetHeaderValue("subject");

    public IReadOnlyList<MimeMailbox> From => AddressParser.ParseList(GetAddressHeader("from"));

    public IReadOnlyList<MimeMailbox> Sender => AddressParser.ParseList(GetAddressHeader("sender"));

    public IReadOnlyList<MimeMailbox> To => AddressParser.ParseList(GetAddressHeader("to"));

    public IReadOnlyList<MimeMailbox> Cc => AddressParser.ParseList(GetAddressHeader("cc"));

    public IReadOnlyList<MimeMailbox> Bcc => AddressParser.ParseList(GetAddressHeader("bcc"));

    public string? MessageId => NormalizeMessageId(GetHeaderValue("message-id"));

    public DateTimeOffset? Date => HeaderParser.ParseDateHeader(GetHeaderValue("date"));

    public string? GetHeaderValue(string normalizedName)
    {
        return Headers.FirstOrDefault(header => header.NormalizedName == normalizedName)?.Value;
    }

    /// <summary>Joins every occurrence of an address header, in wire form; some mailers repeat To or Cc.</summary>
    private string? GetAddressHeader(string normalizedName)
    {
        var values = Headers.Where(header => header.NormalizedName == normalizedName).Select(header => header.RawValue).ToArray();
        return values.Length == 0 ? null : string.Join(", ", values);
    }

    private static string? NormalizeMessageId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = HeaderParser.StripComments(value).Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
