using System.Buffers.Binary;
using System.Text;

namespace XRay.Email.Msg;

public sealed partial class MapiMessage
{
    private void EnsureCoreDefaults()
    {
        if (string.IsNullOrEmpty(MessageClass))
        {
            MessageClass = DefaultMessageClass;
        }

        if (EmitStoreSupportMask && UnicodeStrings && Properties.Get((ushort)CommonMessagePropertyId.StoreSupportMask, (ushort)PropertyTypeCode.PtypInteger32) is null)
        {
            SetProperty((ushort)CommonMessagePropertyId.StoreSupportMask, (ushort)PropertyTypeCode.PtypInteger32, (int)StoreUnicodeOk);
        }

        SyncDisplayRecipientProperties();
    }

    private void SyncDisplayRecipientProperties()
    {
        SetHeaderProperty((ushort)CommonMessagePropertyId.DisplayTo, FormatRecipientHeader(RecipientTypeTo));
        SetHeaderProperty((ushort)CommonMessagePropertyId.DisplayCc, FormatRecipientHeader(RecipientTypeCc));
        SetHeaderProperty((ushort)CommonMessagePropertyId.DisplayBcc, FormatRecipientHeader(RecipientTypeBcc));
    }

    private void SetHeaderProperty(ushort propertyId, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            Properties.Remove(propertyId);
            return;
        }

        SetStringProperty(propertyId, value);
    }

    private string? FormatRecipientHeader(int recipientType)
    {
        var values = Recipients
            .Where(recipient => recipient.RecipientType == recipientType && !string.IsNullOrEmpty(recipient.EmailAddress))
            .Select(recipient => string.IsNullOrEmpty(recipient.DisplayName)
                ? recipient.EmailAddress!
                : $"{recipient.DisplayName} <{recipient.EmailAddress}>")
            .ToArray();
        return values.Length == 0 ? null : string.Join(", ", values);
    }

    private string? GetStringProperty(ushort propertyId)
    {
        var property = Properties.Get(propertyId, (ushort)PropertyTypeCode.PtypString)
            ?? Properties.Get(propertyId, (ushort)PropertyTypeCode.PtypString8);
        return property?.Value as string;
    }

    private void SetStringProperty(ushort propertyId, string? value)
    {
        // Drop every existing representation (PtypString8, PtypString, PtypBinary) first; otherwise a
        // loaded PtypString8 value would shadow the new one and both would be written back.
        Properties.Remove(propertyId);
        if (value is null)
        {
            return;
        }

        var propertyType = UnicodeStrings ? (ushort)PropertyTypeCode.PtypString : (ushort)PropertyTypeCode.PtypString8;
        Properties.Set(new MapiProperty(propertyId, propertyType, value));
    }

    private string DecodeHtmlBytes(byte[] bytes)
    {
        Encoding? encoding = null;
        foreach (var propertyId in new[] { (ushort)CommonMessagePropertyId.InternetCodepage, MessageCodepagePropertyId })
        {
            if (Properties.Get(propertyId, (ushort)PropertyTypeCode.PtypInteger32)?.Value is int codePage && TryGetCodePageEncoding(codePage) is { } candidate)
            {
                encoding = candidate;
                break;
            }
        }

        if (encoding is null)
        {
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes).TrimEnd('\0');
            }
            catch (DecoderFallbackException)
            {
                encoding = DefaultString8Encoding();
            }
        }

        var text = encoding.GetString(bytes);
        return (text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text).TrimEnd('\0');
    }

    private static bool DetectUnicodeState(MsgStorage storage)
    {
        if (storage.FindStream(MsgConstants.PropertyStreamName) is null)
        {
            return true;
        }

        var property = storage.FixedLengthProperties.FirstOrDefault(entry => entry.PropertyTag == (((uint)CommonMessagePropertyId.StoreSupportMask << 16) | (ushort)PropertyTypeCode.PtypInteger32));
        if (property is not null)
        {
            return (BinaryPrimitives.ReadUInt32LittleEndian(property.Value.AsSpan(0, 4)) & StoreUnicodeOk) != 0;
        }

        return storage.Streams.Any(stream =>
        {
            var match = StreamPropertyPattern.Match(stream.Name);
            return match.Success && Convert.ToUInt16(match.Groups[2].Value, 16) == (ushort)PropertyTypeCode.PtypString;
        });
    }

    private static MapiRecipient RecipientFromStorage(MsgStorage storage, Encoding string8Encoding)
    {
        var (properties, extraStreams, reserved0, _) = ParsePropertyStorage(storage, MsgStorageRole.Recipient, string8Encoding);
        var recipient = new MapiRecipient
        {
            StorageId = StorageIdFromName(storage.Name),
            HeaderReserved0 = reserved0,
            DisplayName = properties.Get(DisplayNamePropertyId)?.Value as string,
            EmailAddress = properties.Get(EmailAddressPropertyId)?.Value as string ?? properties.Get(SmtpAddressPropertyId)?.Value as string,
            AddressType = properties.Get(AddressTypePropertyId)?.Value as string ?? "SMTP",
            RecipientType = properties.Get(RecipientTypePropertyId, (ushort)PropertyTypeCode.PtypInteger32)?.Value is int value ? value : RecipientTypeTo,
        };

        foreach (var property in properties.IterProperties())
        {
            recipient.Properties.Set(property);
        }

        foreach (var stream in extraStreams.Select(CloneStream))
        {
            recipient.ExtraStreams.Add(stream);
        }

        foreach (var child in storage.Storages.Select(CloneStorage))
        {
            recipient.ExtraStorages.Add(child);
        }

        return recipient;
    }

    private static MapiAttachment AttachmentFromStorage(MsgStorage storage, Encoding string8Encoding)
    {
        var (properties, extraStreams, reserved0, _) = ParsePropertyStorage(storage, MsgStorageRole.Attachment, string8Encoding);
        var attachMethod = properties.Get((ushort)CommonMessagePropertyId.AttachMethod, (ushort)PropertyTypeCode.PtypInteger32)?.Value is int attachMethodValue
            ? attachMethodValue
            : AttachMethodByValue;

        var dataProperty = properties.Get((ushort)CommonMessagePropertyId.AttachDataBinary, (ushort)PropertyTypeCode.PtypBinary);
        var attachment = new MapiAttachment
        {
            StorageId = StorageIdFromName(storage.Name),
            HeaderReserved0 = reserved0,
            AttachMethod = attachMethod,
            Filename = properties.Get((ushort)CommonMessagePropertyId.AttachLongFilename)?.Value as string
                ?? properties.Get((ushort)CommonMessagePropertyId.AttachFilename)?.Value as string,
            MimeType = properties.Get((ushort)CommonMessagePropertyId.AttachMimeTag)?.Value as string,
            ContentId = properties.Get((ushort)CommonMessagePropertyId.AttachContentId)?.Value as string,
            Data = dataProperty?.Value as byte[] ?? [],
        };

        foreach (var property in properties.IterProperties())
        {
            attachment.Properties.Set(property);
        }

        foreach (var stream in extraStreams.Select(CloneStream))
        {
            attachment.ExtraStreams.Add(stream);
        }

        foreach (var child in storage.Storages)
        {
            if (child.Name != MsgConstants.EmbeddedMessageStorageName)
            {
                continue;
            }

            if (child.Role == MsgStorageRole.EmbeddedMessage)
            {
                try
                {
                    attachment.EmbeddedMessage = FromMsgDocument(new MsgDocument(CloneStorage(child)), string8Encoding);
                }
                catch (Exception)
                {
                    attachment.CustomStorage = CloneStorage(child);
                }
            }
            else
            {
                attachment.CustomStorage = CloneStorage(child);
            }
        }

        return attachment;
    }

    private static MsgStorage RecipientToStorage(MapiRecipient recipient, int index, bool unicodeStrings, Encoding string8Encoding)
    {
        var storageId = recipient.StorageId ?? index;
        SyncPropertyValue(recipient.Properties, RecipientTypePropertyId, (ushort)PropertyTypeCode.PtypInteger32, recipient.RecipientType);
        SyncPropertyValue(recipient.Properties, RowIdPropertyId, (ushort)PropertyTypeCode.PtypInteger32, index);
        if (!string.IsNullOrEmpty(recipient.DisplayName))
        {
            SetObjectStringProperty(recipient.Properties, DisplayNamePropertyId, recipient.DisplayName!, unicodeStrings);
        }

        if (!string.IsNullOrEmpty(recipient.AddressType))
        {
            SetObjectStringProperty(recipient.Properties, AddressTypePropertyId, recipient.AddressType, unicodeStrings);
        }

        if (!string.IsNullOrEmpty(recipient.EmailAddress))
        {
            SetObjectStringProperty(recipient.Properties, EmailAddressPropertyId, recipient.EmailAddress!, unicodeStrings);
            if (recipient.Properties.Get(SmtpAddressPropertyId) is null)
            {
                SetObjectStringProperty(recipient.Properties, SmtpAddressPropertyId, recipient.EmailAddress!, unicodeStrings);
            }

            if (recipient.Properties.Get(SearchKeyPropertyId, (ushort)PropertyTypeCode.PtypBinary) is null)
            {
                recipient.Properties.Add(SearchKeyPropertyId, (ushort)PropertyTypeCode.PtypBinary, Encoding.ASCII.GetBytes($"SMTP:{recipient.EmailAddress}\0"));
            }
        }

        var storage = new MsgStorage($"{MsgConstants.RecipientStoragePrefix}{storageId:X8}", MsgStorageRole.Recipient);
        var (propertyStream, valueStreams) = SerializePropertyBag(recipient.Properties, MsgStorageRole.Recipient, recipient.HeaderReserved0, [], null, 0, 0, 0, 0, string8Encoding);
        storage.Streams.Add(propertyStream);
        foreach (var stream in valueStreams)
        {
            storage.Streams.Add(stream);
        }

        foreach (var stream in recipient.ExtraStreams.Select(CloneStream))
        {
            storage.Streams.Add(stream);
        }

        foreach (var child in recipient.ExtraStorages.Select(CloneStorage))
        {
            storage.Storages.Add(child);
        }

        return storage;
    }

    private static MsgStorage AttachmentToStorage(MapiAttachment attachment, int index, bool unicodeStrings, Encoding string8Encoding)
    {
        var storageId = attachment.StorageId ?? index;
        if (attachment.AttachMethod == AttachMethodEmbedded)
        {
            SyncPropertyValue(attachment.Properties, (ushort)CommonMessagePropertyId.AttachMethod, (ushort)PropertyTypeCode.PtypInteger32, AttachMethodEmbedded);
            SyncPropertyValue(attachment.Properties, (ushort)CommonMessagePropertyId.AttachDataBinary, (ushort)PropertyTypeCode.PtypObject, null);
        }
        else if (attachment.AttachMethod == AttachMethodStorage)
        {
            SyncPropertyValue(attachment.Properties, (ushort)CommonMessagePropertyId.AttachMethod, (ushort)PropertyTypeCode.PtypInteger32, AttachMethodStorage);
            SyncPropertyValue(attachment.Properties, (ushort)CommonMessagePropertyId.AttachDataBinary, (ushort)PropertyTypeCode.PtypObject, null);
        }
        else
        {
            SyncPropertyValue(attachment.Properties, (ushort)CommonMessagePropertyId.AttachMethod, (ushort)PropertyTypeCode.PtypInteger32, AttachMethodByValue);
            SyncPropertyValue(attachment.Properties, (ushort)CommonMessagePropertyId.AttachDataBinary, (ushort)PropertyTypeCode.PtypBinary, attachment.Data.ToArray());
        }

        if (!string.IsNullOrEmpty(attachment.Filename))
        {
            SetObjectStringProperty(attachment.Properties, (ushort)CommonMessagePropertyId.AttachFilename, ShortFilename(attachment.Filename!), unicodeStrings);
            SetObjectStringProperty(attachment.Properties, (ushort)CommonMessagePropertyId.AttachLongFilename, attachment.Filename!, unicodeStrings);
            SetObjectStringProperty(attachment.Properties, AttachLongPathnamePropertyId, attachment.Filename!, unicodeStrings);
            var extension = Path.GetExtension(attachment.Filename!);
            if (!string.IsNullOrEmpty(extension))
            {
                SetObjectStringProperty(attachment.Properties, AttachExtensionPropertyId, extension, unicodeStrings);
            }
        }

        if (!string.IsNullOrEmpty(attachment.MimeType))
        {
            SetObjectStringProperty(attachment.Properties, (ushort)CommonMessagePropertyId.AttachMimeTag, attachment.MimeType!, unicodeStrings);
        }

        if (!string.IsNullOrEmpty(attachment.ContentId))
        {
            SetObjectStringProperty(attachment.Properties, (ushort)CommonMessagePropertyId.AttachContentId, attachment.ContentId!, unicodeStrings);
        }

        SyncPropertyValue(attachment.Properties, AttachNumPropertyId, (ushort)PropertyTypeCode.PtypInteger32, storageId);
        SyncPropertyValue(attachment.Properties, AttachSizePropertyId, (ushort)PropertyTypeCode.PtypInteger32, attachment.Data.Length);

        var storage = new MsgStorage($"{MsgConstants.AttachmentStoragePrefix}{storageId:X8}", MsgStorageRole.Attachment);
        var (propertyStream, valueStreams) = SerializePropertyBag(attachment.Properties, MsgStorageRole.Attachment, attachment.HeaderReserved0, [], attachment.AttachMethod, 0, 0, 0, 0, string8Encoding);
        storage.Streams.Add(propertyStream);
        foreach (var stream in valueStreams)
        {
            storage.Streams.Add(stream);
        }

        foreach (var stream in attachment.ExtraStreams.Select(CloneStream))
        {
            storage.Streams.Add(stream);
        }

        if (attachment.EmbeddedMessage is not null)
        {
            var embeddedDocument = attachment.EmbeddedMessage.ToMsgDocument(embedded: true);
            var embeddedRoot = CloneStorage(embeddedDocument.Root);
            embeddedRoot.Name = MsgConstants.EmbeddedMessageStorageName;
            embeddedRoot.Role = MsgStorageRole.EmbeddedMessage;
            foreach (var child in embeddedRoot.Storages.Where(child => child.Name == MsgConstants.NamedPropertyMappingStorageName).ToArray())
            {
                embeddedRoot.Storages.Remove(child);
            }

            storage.Storages.Add(embeddedRoot);
        }
        else if (attachment.CustomStorage is not null)
        {
            var custom = CloneStorage(attachment.CustomStorage);
            custom.Name = MsgConstants.EmbeddedMessageStorageName;
            custom.Role = MsgStorageRole.CustomAttachment;
            storage.Storages.Add(custom);
        }

        return storage;
    }

    private static void SetObjectStringProperty(MapiPropertyCollection properties, ushort propertyId, string value, bool unicodeStrings)
    {
        SyncPropertyValue(properties, propertyId, unicodeStrings ? (ushort)PropertyTypeCode.PtypString : (ushort)PropertyTypeCode.PtypString8, value);
    }

    private static MapiProperty SyncPropertyValue(MapiPropertyCollection properties, ushort propertyId, ushort propertyType, object? value)
    {
        var existing = properties.Get(propertyId, propertyType);
        if (existing is not null && Equals(existing.Value, value))
        {
            return existing;
        }

        return properties.Set(new MapiProperty(propertyId, propertyType, value));
    }

    private static MsgStorage CloneStorage(MsgStorage storage)
    {
        var clone = new MsgStorage(storage.Name, storage.Role)
        {
            Clsid = storage.Clsid.ToArray(),
            StateBits = storage.StateBits,
            CreationTime = storage.CreationTime,
            ModifiedTime = storage.ModifiedTime,
            PropertyHeaderKind = storage.PropertyHeaderKind,
            PropertyStreamHeader = storage.PropertyStreamHeader,
            FixedLengthProperties = storage.FixedLengthProperties.ToArray(),
            PropertyStreamParseError = storage.PropertyStreamParseError,
        };

        foreach (var stream in storage.Streams.Select(CloneStream))
        {
            clone.Streams.Add(stream);
        }

        foreach (var child in storage.Storages.Select(CloneStorage))
        {
            clone.Storages.Add(child);
        }

        return clone;
    }

    private static MsgStream CloneStream(MsgStream stream) => new(stream.Name, stream.Data.ToArray())
    {
        Clsid = stream.Clsid.ToArray(),
        StateBits = stream.StateBits,
        CreationTime = stream.CreationTime,
        ModifiedTime = stream.ModifiedTime,
    };
}
