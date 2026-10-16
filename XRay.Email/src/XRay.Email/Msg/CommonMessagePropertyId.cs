namespace XRay.Email.Msg;

/// <summary>Common MAPI property identifiers used by the MSG reader and writer for core message semantics, body fields, transport headers, and attachments.</summary>
public enum CommonMessagePropertyId : ushort
{
    /// <summary>Property id for the message class string, such as IPM.Note.</summary>
    MessageClass = (ushort)26,
    /// <summary>Property id for transport header block string used to project RFC-style headers.</summary>
    TransportMessageHeaders = (ushort)125,
    /// <summary>Property id for message subject text.</summary>
    Subject = (ushort)55,
    /// <summary>Property id for display-form To recipients string.</summary>
    DisplayTo = (ushort)3588,
    /// <summary>Property id for display-form Cc recipients string.</summary>
    DisplayCc = (ushort)3587,
    /// <summary>Property id for display-form Bcc recipients string.</summary>
    DisplayBcc = (ushort)3586,
    /// <summary>Property id for Internet Message-ID string.</summary>
    InternetMessageId = (ushort)4149,
    /// <summary>Property id for the Message object status flags value.</summary>
    MessageFlags = (ushort)3591,
    /// <summary>Property id for the code page used for the body or HTML body.</summary>
    InternetCodepage = (ushort)16350,
    /// <summary>Property id for sender display name.</summary>
    SenderName = (ushort)3098,
    /// <summary>Property id for sender address type string, typically SMTP.</summary>
    SenderAddressType = (ushort)3102,
    /// <summary>Property id for sender SMTP-style email address.</summary>
    SenderEmailAddress = (ushort)3103,
    /// <summary>Property id for message delivery FILETIME value.</summary>
    MessageDeliveryTime = (ushort)3590,
    /// <summary>Property id for store support mask that controls Unicode string expectations in MSG files.</summary>
    StoreSupportMask = (ushort)13325,
    /// <summary>Property id for plain-text body value.</summary>
    Body = (ushort)4096,
    /// <summary>Property id for HTML body value.</summary>
    BodyHtml = (ushort)4115,
    /// <summary>Property id for generic display name fields used in several MSG object contexts.</summary>
    DisplayName = (ushort)12289,
    /// <summary>Property id for attachment method classification (for example regular file versus embedded message).</summary>
    AttachMethod = (ushort)14085,
    /// <summary>Property id for binary attachment payload stream.</summary>
    AttachDataBinary = (ushort)14081,
    /// <summary>Property id for short attachment file name.</summary>
    AttachFilename = (ushort)14084,
    /// <summary>Property id for long attachment file name.</summary>
    AttachLongFilename = (ushort)14087,
    /// <summary>Property id for attachment MIME type tag string.</summary>
    AttachMimeTag = (ushort)14094,
    /// <summary>Property id for attachment content-id string.</summary>
    AttachContentId = (ushort)14098,
    /// <summary>Property id referenced by MSG variable-length multi-valued stream naming example (__substg1.0_6844101F).</summary>
    ScheduleInfoDelegateNames = (ushort)26692,
    /// <summary>Property id referenced by MSG fixed-length multi-valued stream naming example (__substg1.0_68531003).</summary>
    ScheduleInfoMonthsBusy = (ushort)26707,
    /// <summary>Property id referenced by MSG variable-length stream naming example (__substg1.0_100A0102).</summary>
    ExampleTag100A = (ushort)4106,
    /// <summary>Property id referenced by MSG variable-length stream naming example (__substg1.0_101D0102).</summary>
    ExampleTag101D = (ushort)4125
}
