using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using XRay.Email.Cfb;
using XRay.Email.Msg.Mime;

namespace XRay.Email.Msg;

public sealed partial class MapiMessage : IDisposable
{
    private static readonly Regex StreamPropertyPattern = new($"^{Regex.Escape(MsgConstants.PropertyValueStreamPrefix)}([0-9A-Fa-f]{{4}})([0-9A-Fa-f]{{4}})$", RegexOptions.Compiled);
    private static readonly Regex IndexedStreamPropertyPattern = new($"^{Regex.Escape(MsgConstants.PropertyValueStreamPrefix)}([0-9A-Fa-f]{{4}})([0-9A-Fa-f]{{4}})-([0-9A-Fa-f]{{8}})$", RegexOptions.Compiled);

    private const ushort RecipientTypePropertyId = 0x0C15;
    private const ushort RowIdPropertyId = 0x3000;
    private const ushort DisplayNamePropertyId = 0x3001;
    private const ushort AddressTypePropertyId = 0x3002;
    private const ushort EmailAddressPropertyId = 0x3003;
    private const ushort SearchKeyPropertyId = 0x300B;
    private const ushort SmtpAddressPropertyId = 0x39FE;
    private const ushort AttachLongPathnamePropertyId = 0x370D;
    private const ushort AttachExtensionPropertyId = 0x3703;
    private const ushort AttachSizePropertyId = 0x0E20;
    private const ushort AttachNumPropertyId = 0x0E21;
    private const ushort RtfCompressedPropertyId = 0x1009;
    private const ushort MessageCodepagePropertyId = 0x3FFD;
    private const ushort SenderSmtpAddressPropertyId = 0x5D01;
    private const ushort SentRepresentingSmtpAddressPropertyId = 0x5D02;
    private const uint StoreUnicodeOk = 0x00040000;
    private const string DefaultMessageClass = "IPM.Note";

    private static readonly Dictionary<ushort, int> FixedInlineSizes = new()
    {
        [(ushort)PropertyTypeCode.PtypInteger16] = 2,
        [(ushort)PropertyTypeCode.PtypInteger32] = 4,
        [(ushort)PropertyTypeCode.PtypFloating32] = 4,
        [(ushort)PropertyTypeCode.PtypFloating64] = 8,
        [(ushort)PropertyTypeCode.PtypCurrency] = 8,
        [(ushort)PropertyTypeCode.PtypFloatingTime] = 8,
        [(ushort)PropertyTypeCode.PtypErrorCode] = 4,
        [(ushort)PropertyTypeCode.PtypBoolean] = 2,
        [(ushort)PropertyTypeCode.PtypInteger64] = 8,
        [(ushort)PropertyTypeCode.PtypTime] = 8,
    };

    static MapiMessage()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public const uint DefaultPropertyFlags = MsgConstants.PROPATTR_READABLE | MsgConstants.PROPATTR_WRITABLE;
    public const int RecipientTypeTo = 1;
    public const int RecipientTypeCc = 2;
    public const int RecipientTypeBcc = 3;
    public const int AttachMethodByValue = 1;
    public const int AttachMethodEmbedded = 5;
    public const int AttachMethodStorage = 6;

    public bool UnicodeStrings { get; set; } = true;

    public MapiPropertyCollection Properties { get; } = new();

    public IList<MapiRecipient> Recipients { get; } = [];

    public IList<MapiAttachment> Attachments { get; } = [];

    public ushort MajorVersion { get; set; } = 3;

    public ushort MinorVersion { get; set; } = 0x003E;

    public uint TransactionSignatureNumber { get; set; }

    public IReadOnlyList<string> ValidationIssues { get; private set; } = Array.Empty<string>();

    public string? Subject
    {
        get => GetStringProperty((ushort)CommonMessagePropertyId.Subject);
        set => SetStringProperty((ushort)CommonMessagePropertyId.Subject, value);
    }

    public string? Body
    {
        get => GetStringProperty((ushort)CommonMessagePropertyId.Body);
        set => SetStringProperty((ushort)CommonMessagePropertyId.Body, value);
    }

    /// <summary>
    /// The HTML body (PR_HTML). Outlook usually stores it as binary in the code page given by
    /// PR_INTERNET_CPID; that form is decoded here. Setting it stores a string property.
    /// </summary>
    public string? HtmlBody
    {
        get => Properties.Get((ushort)CommonMessagePropertyId.BodyHtml)?.Value switch
        {
            string text => text,
            byte[] bytes => DecodeHtmlBytes(bytes),
            _ => null,
        };
        set => SetStringProperty((ushort)CommonMessagePropertyId.BodyHtml, value);
    }

    /// <summary>
    /// The RTF body, decompressed from PR_RTF_COMPRESSED (MS-OXRTFCP), or null when the message has
    /// none or it cannot be decompressed. The RTF is returned with each byte mapped to one character
    /// (ISO-8859-1), since RTF carries its own code page declarations.
    /// </summary>
    public string? RtfBody
    {
        get
        {
            if (Properties.Get(RtfCompressedPropertyId, (ushort)PropertyTypeCode.PtypBinary)?.Value is not byte[] compressed)
            {
                return null;
            }

            return CompressedRtf.TryDecompress(compressed) is { } rtf ? Encoding.Latin1.GetString(rtf) : null;
        }
    }

    /// <summary>The transport message headers (PR_TRANSPORT_MESSAGE_HEADERS), as received over SMTP.</summary>
    public string? TransportMessageHeaders
    {
        get => GetStringProperty((ushort)CommonMessagePropertyId.TransportMessageHeaders);
        set => SetStringProperty((ushort)CommonMessagePropertyId.TransportMessageHeaders, value);
    }

    public string? MessageClass
    {
        get => GetStringProperty((ushort)CommonMessagePropertyId.MessageClass);
        set => SetStringProperty((ushort)CommonMessagePropertyId.MessageClass, value);
    }

    public string? SenderName
    {
        get => GetStringProperty((ushort)CommonMessagePropertyId.SenderName);
        set => SetStringProperty((ushort)CommonMessagePropertyId.SenderName, value);
    }

    public string? SenderEmailAddress
    {
        get => GetStringProperty((ushort)CommonMessagePropertyId.SenderEmailAddress);
        set => SetStringProperty((ushort)CommonMessagePropertyId.SenderEmailAddress, value);
    }

    public string? SenderAddressType
    {
        get => GetStringProperty((ushort)CommonMessagePropertyId.SenderAddressType);
        set => SetStringProperty((ushort)CommonMessagePropertyId.SenderAddressType, value);
    }

    /// <summary>
    /// The sender's SMTP address. For an Exchange sender, <see cref="SenderEmailAddress"/> holds an
    /// X.500 distinguished name; this falls back to PR_SENDER_SMTP_ADDRESS, then
    /// PR_SENT_REPRESENTING_SMTP_ADDRESS, then the From header in the transport headers.
    /// </summary>
    public string? SenderSmtpAddress
    {
        get
        {
            var address = SenderEmailAddress;
            var addressType = SenderAddressType;
            if (!string.IsNullOrWhiteSpace(address) && (string.IsNullOrEmpty(addressType) || string.Equals(addressType, "SMTP", StringComparison.OrdinalIgnoreCase)) && address.Contains('@'))
            {
                return address;
            }

            foreach (var propertyId in new[] { SenderSmtpAddressPropertyId, SentRepresentingSmtpAddressPropertyId })
            {
                if (GetStringProperty(propertyId) is { Length: > 0 } smtp)
                {
                    return smtp;
                }
            }

            if (TransportMessageHeaders is { Length: > 0 } headers)
            {
                var from = HeaderParser.ParseHeaders(Encoding.UTF8.GetBytes(headers)).FirstOrDefault(header => header.NormalizedName == "from");
                if (from is not null && AddressParser.ParseList(from.RawValue).FirstOrDefault() is { Address.Length: > 0 } mailbox && mailbox.Address.Contains('@'))
                {
                    return mailbox.Address;
                }
            }

            return address is not null && address.Contains('@') ? address : null;
        }
    }

    public string? InternetMessageId
    {
        get => GetStringProperty((ushort)CommonMessagePropertyId.InternetMessageId);
        set => SetStringProperty((ushort)CommonMessagePropertyId.InternetMessageId, value);
    }

    public DateTime? MessageDeliveryTime
    {
        get => Properties.Get((ushort)CommonMessagePropertyId.MessageDeliveryTime, (ushort)PropertyTypeCode.PtypTime)?.Value as DateTime?;
        set
        {
            if (value is null)
            {
                Properties.Remove((ushort)CommonMessagePropertyId.MessageDeliveryTime);
                return;
            }

            SetProperty((ushort)CommonMessagePropertyId.MessageDeliveryTime, (ushort)PropertyTypeCode.PtypTime, value.Value);
        }
    }

    internal IList<MsgStream> ExtraStreams { get; } = [];

    internal IList<MsgStorage> ExtraStorages { get; } = [];

    internal MsgStorage? NameIdStorage { get; set; }

    internal byte[] HeaderReserved0 { get; set; } = new byte[8];

    internal byte[] HeaderReserved1 { get; set; } = new byte[8];

    internal bool EmitStoreSupportMask { get; set; } = true;

    internal bool PreserveDeclaredHeaderCounts { get; set; }

    internal int? DeclaredNextRecipientId { get; set; }

    internal int? DeclaredNextAttachmentId { get; set; }

    internal int? DeclaredRecipientCount { get; set; }

    internal int? DeclaredAttachmentCount { get; set; }

    internal Encoding? InheritedString8Encoding { get; set; }

    public static MapiMessage Create(string subject = "", string body = "", bool unicodeStrings = true)
    {
        var message = new MapiMessage { UnicodeStrings = unicodeStrings };
        if (!string.IsNullOrEmpty(subject))
        {
            message.Subject = subject;
        }

        if (!string.IsNullOrEmpty(body))
        {
            message.Body = body;
        }

        return message;
    }

    public static MapiMessage FromFile(string path, CancellationToken cancellationToken) => FromFile(path, false, cancellationToken);

    public static MapiMessage FromStream(Stream stream, CancellationToken cancellationToken) => FromStream(stream, false, cancellationToken);

    public static MapiMessage FromFile(string path, bool strict = false) => FromFile(path, strict, CancellationToken.None);

    /// <summary>
    /// Loads an MSG file. The file is read sector by sector on demand and is never held whole in
    /// memory; only the parsed message (its properties and attachment bytes) is.
    /// </summary>
    public static MapiMessage FromFile(string path, bool strict, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var reader = MsgReader.FromFile(path, strict, cancellationToken);
        return FromReader(reader);
    }

    public static MapiMessage FromStream(Stream stream, bool strict = false) => FromStream(stream, strict, CancellationToken.None);

    /// <summary>
    /// Loads an MSG from a stream. A seekable stream is read in place from its current position (no
    /// copy); a non-seekable one is spooled to a temporary file, not to memory. The stream is not
    /// disposed.
    /// </summary>
    public static MapiMessage FromStream(Stream stream, bool strict, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = MsgReader.FromStream(stream, strict, cancellationToken);
        return FromReader(reader);
    }

    private static MapiMessage FromReader(MsgReader reader)
    {
        var message = FromMsgDocument(MsgDocument.FromReader(reader));
        message.ValidationIssues = reader.ValidationIssues.ToArray();
        return message;
    }

    /// <summary>Loads a message from a file, detecting MSG (a compound file) or EML from its first bytes.</summary>
    public static MapiMessage Load(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
        return Load(file, cancellationToken);
    }

    /// <summary>
    /// Loads a message from a stream, detecting MSG (compound file signature) or EML. A non-seekable
    /// stream is handled without buffering the part already inspected.
    /// </summary>
    public static MapiMessage Load(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var signature = new byte[8];
        if (stream.CanSeek)
        {
            var start = stream.Position;
            var read = stream.ReadAtLeast(signature, signature.Length, throwOnEndOfStream: false);
            stream.Position = start;
            return IsCompoundFile(signature.AsSpan(0, read)) ? FromStream(stream, false, cancellationToken) : LoadFromEml(stream, cancellationToken);
        }

        var peeked = stream.ReadAtLeast(signature, signature.Length, throwOnEndOfStream: false);
        using var rejoined = new PrefixedStream(signature.AsSpan(0, peeked).ToArray(), stream);
        return IsCompoundFile(signature.AsSpan(0, peeked)) ? FromStream(rejoined, false, cancellationToken) : LoadFromEml(rejoined, cancellationToken);
    }

    private static bool IsCompoundFile(ReadOnlySpan<byte> head) =>
        head.Length >= 8 && head.StartsWith((ReadOnlySpan<byte>)[0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1]);

    public static MapiMessage FromMsgDocument(MsgDocument document) => FromMsgDocument(document, null);

    private static MapiMessage FromMsgDocument(MsgDocument document, Encoding? inheritedString8Encoding)
    {
        ArgumentNullException.ThrowIfNull(document);
        var root = document.Root;
        EnsurePropertyEntries(root, root.Role == MsgStorageRole.EmbeddedMessage ? MsgStorageRole.EmbeddedMessage : MsgStorageRole.Message);
        var unicodeStrings = DetectUnicodeState(root);
        var string8Encoding = ResolveString8Encoding(root.FixedLengthProperties, inheritedString8Encoding);
        var (properties, extraStreams, header0, header1) = ParsePropertyStorage(root, root.Role == MsgStorageRole.EmbeddedMessage ? MsgStorageRole.EmbeddedMessage : MsgStorageRole.Message, string8Encoding);

        var message = new MapiMessage
        {
            UnicodeStrings = unicodeStrings,
            InheritedString8Encoding = inheritedString8Encoding,
            MajorVersion = document.MajorVersion,
            MinorVersion = document.MinorVersion,
            TransactionSignatureNumber = document.TransactionSignatureNumber,
            SourceFormat = MessageSourceFormat.Msg,
            HeaderReserved0 = header0,
            HeaderReserved1 = header1,
            EmitStoreSupportMask = properties.Get((ushort)CommonMessagePropertyId.StoreSupportMask, (ushort)PropertyTypeCode.PtypInteger32) is not null,
            NameIdStorage = root.FindStorage(MsgConstants.NamedPropertyMappingStorageName) is { } nameId ? CloneStorage(nameId) : null,
        };

        foreach (var property in properties.IterProperties())
        {
            message.Properties.Set(property);
        }

        foreach (var stream in extraStreams)
        {
            message.ExtraStreams.Add(CloneStream(stream));
        }

        foreach (var storage in root.Storages.Where(storage =>
                     storage.Role is not MsgStorageRole.NamedPropertyMapping and not MsgStorageRole.Recipient and not MsgStorageRole.Attachment))
        {
            message.ExtraStorages.Add(CloneStorage(storage));
        }

        if (root.PropertyStreamHeader is PropertyStreamHeaderTopLevel header)
        {
            message.PreserveDeclaredHeaderCounts = true;
            message.DeclaredNextRecipientId = (int)header.NextRecipientId;
            message.DeclaredNextAttachmentId = (int)header.NextAttachmentId;
            message.DeclaredRecipientCount = (int)header.RecipientCount;
            message.DeclaredAttachmentCount = (int)header.AttachmentCount;
        }

        foreach (var storage in root.Storages)
        {
            if (storage.Role == MsgStorageRole.Recipient)
            {
                message.Recipients.Add(RecipientFromStorage(storage, string8Encoding));
            }
            else if (storage.Role == MsgStorageRole.Attachment)
            {
                message.Attachments.Add(AttachmentFromStorage(storage, string8Encoding));
            }
        }

        return message;
    }

    public static MapiMessage LoadFromEml(string path) => LoadFromEml(path, CancellationToken.None);

    /// <summary>
    /// Loads an EML file. MIME parsing works on the message as one block (decoded parts are kept in
    /// memory regardless), so the file is read once into an array of exactly its size; there is no
    /// intermediate copy.
    /// </summary>
    public static MapiMessage LoadFromEml(string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        var model = new MimeReader(cancellationToken).ReadFile(path);
        return FinishEml(model, cancellationToken);
    }

    public static MapiMessage LoadFromEml(Stream stream) => LoadFromEml(stream, CancellationToken.None);

    /// <summary>Loads an EML from the rest of a stream (not disposed). A seekable stream is read into an exactly sized array.</summary>
    public static MapiMessage LoadFromEml(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var model = new MimeReader(cancellationToken).Read(stream);
        return FinishEml(model, cancellationToken);
    }

    public static MapiMessage LoadFromEml(byte[] data) => LoadFromEml(data, CancellationToken.None);

    public static MapiMessage LoadFromEml(byte[] data, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);
        var model = new MimeReader(cancellationToken).Read(data);
        return FinishEml(model, cancellationToken);
    }

    private static MapiMessage FinishEml(MimeMessageModel model, CancellationToken cancellationToken)
    {
        return EmlMessageMapper.ToMapiMessage(model, cancellationToken);
    }

    public void Dispose()
    {
    }

    public MapiRecipient AddRecipient(string emailAddress, string? displayName = null, int recipientType = RecipientTypeTo)
    {
        var recipient = new MapiRecipient
        {
            EmailAddress = emailAddress,
            DisplayName = displayName ?? emailAddress,
            RecipientType = recipientType,
        };
        Recipients.Add(recipient);
        return recipient;
    }

    public MapiAttachment AddAttachment(string filename, byte[] data, string? mimeType = null, string? contentId = null)
    {
        var attachment = MapiAttachment.FromBytes(filename, data, mimeType, contentId);
        Attachments.Add(attachment);
        return attachment;
    }

    public MapiAttachment AddAttachment(string filename, Stream stream, string? mimeType = null, string? contentId = null)
    {
        var attachment = MapiAttachment.FromStream(filename, stream, mimeType, contentId);
        Attachments.Add(attachment);
        return attachment;
    }

    public MapiAttachment AddEmbeddedMessageAttachment(MapiMessage message, string? filename = null, string? mimeType = null)
    {
        var attachment = new MapiAttachment
        {
            Filename = filename ?? $"{message.Subject ?? "message"}.msg",
            MimeType = mimeType ?? "message/rfc822",
            AttachMethod = AttachMethodEmbedded,
            EmbeddedMessage = message,
        };
        Attachments.Add(attachment);
        return attachment;
    }

    /// <summary>The format this message was loaded from; <see cref="MessageSourceFormat.Created"/> for one built in code.</summary>
    public MessageSourceFormat SourceFormat { get; internal set; } = MessageSourceFormat.Created;

    public MapiProperty SetProperty(ushort propertyId, ushort propertyType, object? value, uint flags = DefaultPropertyFlags)
    {
        return Properties.Add(propertyId, propertyType, value, flags);
    }

    public object? GetPropertyValue(ushort propertyId, ushort? propertyType = null, bool decode = true)
    {
        var property = Properties.Get(propertyId, propertyType);
        if (property is null)
        {
            return null;
        }

        if (decode)
        {
            return property.Value;
        }

        if (property.RawDirectStream is not null)
        {
            return property.RawDirectStream.ToArray();
        }

        if (property.RawInlineValue is not null)
        {
            return property.RawInlineValue.ToArray();
        }

        return property.Value;
    }

    public IEnumerable<(ushort PropertyId, ushort PropertyType)> IterPropertyKeys()
    {
        foreach (var property in Properties.IterProperties())
        {
            yield return (property.PropertyId, property.PropertyType);
        }
    }

    public IEnumerable<MapiProperty> IterProperties() => Properties.IterProperties();

    public IEnumerable<MapiAttachment> IterAttachmentsInfo() => Attachments;

    public MsgDocument ToMsgDocument() => ToMsgDocument(embedded: false);

    private MsgDocument ToMsgDocument(bool embedded)
    {
        EnsureCoreDefaults();
        var string8Encoding = String8Encoding();

        var root = new MsgStorage(CfbConstants.RootEntryName, MsgStorageRole.Message);
        var recipientCount = Recipients.Count;
        var attachmentCount = Attachments.Count;
        var nextRecipientId = NextStorageId(Recipients.Select(item => item.StorageId));
        var nextAttachmentId = NextStorageId(Attachments.Select(item => item.StorageId));
        if (PreserveDeclaredHeaderCounts)
        {
            recipientCount = DeclaredRecipientCount ?? recipientCount;
            attachmentCount = DeclaredAttachmentCount ?? attachmentCount;
            nextRecipientId = DeclaredNextRecipientId ?? nextRecipientId;
            nextAttachmentId = DeclaredNextAttachmentId ?? nextAttachmentId;
        }

        var (propertyStream, valueStreams) = SerializePropertyBag(
            Properties,
            embedded ? MsgStorageRole.EmbeddedMessage : MsgStorageRole.Message,
            HeaderReserved0,
            HeaderReserved1,
            null,
            recipientCount,
            attachmentCount,
            nextRecipientId,
            nextAttachmentId,
            string8Encoding);

        root.Streams.Add(propertyStream);
        foreach (var stream in valueStreams)
        {
            root.Streams.Add(stream);
        }

        foreach (var stream in ExtraStreams.Select(CloneStream))
        {
            root.Streams.Add(stream);
        }

        root.Storages.Add(NameIdStorage is null ? CreateEmptyNamedPropertyMapping() : CloneStorage(NameIdStorage));

        foreach (var storage in ExtraStorages.Select(CloneStorage))
        {
            root.Storages.Add(storage);
        }

        for (var index = 0; index < Recipients.Count; index++)
        {
            root.Storages.Add(RecipientToStorage(Recipients[index], index, UnicodeStrings, string8Encoding));
        }

        for (var index = 0; index < Attachments.Count; index++)
        {
            root.Storages.Add(AttachmentToStorage(Attachments[index], index, UnicodeStrings, string8Encoding));
        }

        return new MsgDocument(root, MajorVersion, MinorVersion, TransactionSignatureNumber, false);
    }

    /// <summary>
    /// An empty named property mapping storage. MS-OXMSG 2.2.3 requires its GUID, entry and string
    /// streams to exist even when no named property is defined; readers reject the file otherwise.
    /// </summary>
    private static MsgStorage CreateEmptyNamedPropertyMapping()
    {
        var storage = new MsgStorage(MsgConstants.NamedPropertyMappingStorageName, MsgStorageRole.NamedPropertyMapping);
        storage.Streams.Add(new MsgStream("__substg1.0_00020102"));
        storage.Streams.Add(new MsgStream("__substg1.0_00030102"));
        storage.Streams.Add(new MsgStream("__substg1.0_00040102"));
        return storage;
    }

    public byte[] Save() => Save(CancellationToken.None);

    public byte[] Save(CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        Save(memory, cancellationToken);
        return memory.ToArray();
    }

    public void Save(string path) => Save(path, CancellationToken.None);

    public void Save(string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        MsgWriter.WriteFile(ToMsgDocument(), path, cancellationToken);
    }

    public void Save(Stream stream) => Save(stream, CancellationToken.None);

    /// <summary>Writes the message as an MSG file to <paramref name="stream"/>, one sector at a time (the file is never assembled in memory).</summary>
    public void Save(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        MsgWriter.Write(ToMsgDocument(), stream, cancellationToken);
    }

    public byte[] SaveToEml() => SaveToEml(CancellationToken.None);

    public byte[] SaveToEml(CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        SaveToEml(memory, cancellationToken);
        return memory.ToArray();
    }

    public void SaveToEml(string path) => SaveToEml(path, CancellationToken.None);

    public void SaveToEml(string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920);
        SaveToEml(file, cancellationToken);
    }

    public void SaveToEml(Stream stream) => SaveToEml(stream, CancellationToken.None);

    /// <summary>
    /// Writes the message as MIME to <paramref name="stream"/>. Attachments are base64-encoded block by
    /// block as they are written, and embedded messages are written in place, so no encoded copy of
    /// the message is held in memory.
    /// </summary>
    public void SaveToEml(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var model = EmlMessageMapper.ToMimeMessage(this, cancellationToken);
        new MimeWriter().Write(stream, model, cancellationToken);
    }
}
