namespace XRay.Email.Msg;

public sealed class MapiRecipient
{
    public string? DisplayName { get; set; }

    public string? EmailAddress { get; set; }

    public int RecipientType { get; set; } = MapiMessage.RecipientTypeTo;

    public string AddressType { get; set; } = "SMTP";

    public MapiPropertyCollection Properties { get; } = new();

    /// <summary>
    /// The recipient's SMTP address. For an Exchange recipient, <see cref="EmailAddress"/> holds an
    /// X.500 distinguished name and the SMTP address comes from PR_SMTP_ADDRESS.
    /// </summary>
    public string? SmtpAddress
    {
        get
        {
            if (string.Equals(AddressType, "SMTP", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(EmailAddress))
            {
                return EmailAddress;
            }

            if (Properties.Get(0x39FE)?.Value is string { Length: > 0 } smtp)
            {
                return smtp;
            }

            return EmailAddress is not null && EmailAddress.Contains('@') ? EmailAddress : null;
        }
    }

    internal int? StorageId { get; set; }

    internal IList<MsgStream> ExtraStreams { get; } = [];

    internal IList<MsgStorage> ExtraStorages { get; } = [];

    internal byte[] HeaderReserved0 { get; set; } = new byte[8];
}
