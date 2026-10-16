using System.Buffers.Binary;
using XRay.Email.Cfb;

namespace XRay.Email.Msg;

public sealed class MsgDocument
{
    public MsgDocument(MsgStorage root, ushort majorVersion = 3, ushort minorVersion = 0x003E, uint transactionSignatureNumber = 0, bool strict = false)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));
        MajorVersion = majorVersion;
        MinorVersion = minorVersion;
        TransactionSignatureNumber = transactionSignatureNumber;
        Strict = strict;
    }

    private const int MaxStorageDepth = 256;

    public MsgStorage Root { get; }

    public ushort MajorVersion { get; set; }

    public ushort MinorVersion { get; set; }

    public uint TransactionSignatureNumber { get; set; }

    public bool Strict { get; set; }

    internal static MsgDocument FromReader(MsgReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var cfb = reader.CfbReader;
        var rootEntry = cfb.GetEntry(CfbConstants.RootStreamId);
        return new MsgDocument(
            BuildStorage(reader, rootEntry, MsgStorageRole.Message, []),
            cfb.Header.MajorVersion,
            cfb.Header.MinorVersion,
            cfb.Header.TransactionSignatureNumber,
            reader.Strict);
    }

    public static MsgDocument FromFile(string path, CancellationToken cancellationToken) => FromFile(path, false, cancellationToken);

    public static MsgDocument FromStream(Stream stream, CancellationToken cancellationToken) => FromStream(stream, false, cancellationToken);

    public static MsgDocument FromFile(string path, bool strict = false) => FromFile(path, strict, CancellationToken.None);

    public static MsgDocument FromFile(string path, bool strict, CancellationToken cancellationToken)
    {
        using var reader = MsgReader.FromFile(path, strict, cancellationToken);
        return FromReader(reader);
    }

    public static MsgDocument FromStream(Stream stream, bool strict = false) => FromStream(stream, strict, CancellationToken.None);

    public static MsgDocument FromStream(Stream stream, bool strict, CancellationToken cancellationToken)
    {
        using var reader = MsgReader.FromStream(stream, strict, cancellationToken);
        return FromReader(reader);
    }

    public CfbDocument ToCfbDocument()
    {
        ValidateStorage(Root, true, Strict);
        return new CfbDocument(ToCfbStorage(Root), MajorVersion, MinorVersion, TransactionSignatureNumber);
    }

    private static MsgStorage BuildStorage(MsgReader reader, DirectoryEntry entry, MsgStorageRole role, HashSet<uint> ancestors)
    {
        // A damaged directory can link a storage back to one of its ancestors.
        if (!ancestors.Add(entry.StreamId) || ancestors.Count > MaxStorageDepth)
        {
            throw new MsgException($"Storage tree is cyclic or deeper than {MaxStorageDepth} levels at stream id {entry.StreamId}.");
        }

        var cfb = reader.CfbReader;
        cfb.CancellationToken.ThrowIfCancellationRequested();
        var storage = new MsgStorage(entry.Name, role)
        {
            Clsid = entry.Clsid.ToArray(),
            StateBits = entry.StateBits,
            CreationTime = entry.CreationTime,
            ModifiedTime = entry.ModifiedTime,
        };

        if (role is MsgStorageRole.Message or MsgStorageRole.EmbeddedMessage or MsgStorageRole.Recipient or MsgStorageRole.Attachment)
        {
            try
            {
                if (role is MsgStorageRole.Message or MsgStorageRole.EmbeddedMessage)
                {
                    var (header, entries) = reader.ParseMessagePropertyStream(entry.StreamId, embedded: role == MsgStorageRole.EmbeddedMessage);
                    storage.PropertyHeaderKind = "message";
                    storage.PropertyStreamHeader = header;
                    storage.FixedLengthProperties = entries;
                }
                else
                {
                    var (header, entries) = reader.ParseSubobjectPropertyStream(entry.StreamId);
                    storage.PropertyHeaderKind = "subobject";
                    storage.PropertyStreamHeader = header;
                    storage.FixedLengthProperties = entries;
                }
            }
            catch (MsgException ex)
            {
                if (role == MsgStorageRole.Message && entry.StreamId == CfbConstants.RootStreamId)
                {
                    throw;
                }

                storage.PropertyStreamParseError = ex.Message;
            }
        }

        var attachMethod = role == MsgStorageRole.Attachment ? ExtractAttachMethod(storage.FixedLengthProperties) : null;
        foreach (var child in cfb.IterChildren(entry.StreamId))
        {
            if (child.IsStream())
            {
                storage.Streams.Add(new MsgStream(child.Name, cfb.GetStreamData(child.StreamId))
                {
                    Clsid = child.Clsid.ToArray(),
                    StateBits = child.StateBits,
                    CreationTime = child.CreationTime,
                    ModifiedTime = child.ModifiedTime,
                });
                continue;
            }

            var childRole = ClassifyStorageRole(cfb, role, child, attachMethod);
            storage.Storages.Add(BuildStorage(reader, child, childRole, ancestors));
        }

        ancestors.Remove(entry.StreamId);
        return storage;
    }

    private static uint? ExtractAttachMethod(IReadOnlyList<PropertyEntryFixedLength> entries)
    {
        var propertyTag = ((uint)CommonMessagePropertyId.AttachMethod << 16) | (ushort)PropertyTypeCode.PtypInteger32;
        foreach (var entry in entries)
        {
            if (entry.PropertyTag == propertyTag)
            {
                return BinaryPrimitives.ReadUInt32LittleEndian(entry.Value.AsSpan(0, 4));
            }
        }

        return null;
    }

    private static MsgStorageRole ClassifyStorageRole(CfbReader cfbReader, MsgStorageRole parentRole, DirectoryEntry child, uint? attachMethod)
    {
        if (parentRole is MsgStorageRole.Message or MsgStorageRole.EmbeddedMessage)
        {
            if (child.Name == MsgConstants.NamedPropertyMappingStorageName)
            {
                return MsgStorageRole.NamedPropertyMapping;
            }

            if (RegexHolder.RecipientNamePattern.IsMatch(child.Name))
            {
                return MsgStorageRole.Recipient;
            }

            if (RegexHolder.AttachmentNamePattern.IsMatch(child.Name))
            {
                return MsgStorageRole.Attachment;
            }

            return MsgStorageRole.Generic;
        }

        if (parentRole == MsgStorageRole.Attachment && child.Name == MsgConstants.EmbeddedMessageStorageName)
        {
            if (attachMethod == 5)
            {
                return MsgStorageRole.EmbeddedMessage;
            }

            if (attachMethod == 6)
            {
                return MsgStorageRole.CustomAttachment;
            }

            return cfbReader.FindChildByName(child.StreamId, MsgConstants.PropertyStreamName) is not null
                ? MsgStorageRole.EmbeddedMessage
                : MsgStorageRole.CustomAttachment;
        }

        return MsgStorageRole.Generic;
    }

    private static void ValidateStorage(MsgStorage storage, bool isRoot, bool strict)
    {
        var propertyStreamCount = storage.Streams.Count(stream => stream.Name == MsgConstants.PropertyStreamName);
        if (storage.Role is MsgStorageRole.Message or MsgStorageRole.EmbeddedMessage or MsgStorageRole.Recipient or MsgStorageRole.Attachment)
        {
            if (propertyStreamCount != 1)
            {
                throw new MsgException($"Storage '{storage.Name}' with role '{storage.Role}' must contain exactly one property stream; found {propertyStreamCount}");
            }
        }

        if (storage.Role == MsgStorageRole.Message && isRoot)
        {
            var namedMappingCount = storage.Storages.Count(child => child.Name == MsgConstants.NamedPropertyMappingStorageName);
            if (namedMappingCount != 1)
            {
                throw new MsgException($"Top-level MSG storage must contain exactly one named property mapping storage; found {namedMappingCount}");
            }
        }

        if (storage.Role == MsgStorageRole.Recipient && !RegexHolder.RecipientNamePattern.IsMatch(storage.Name))
        {
            throw new MsgException($"Invalid recipient storage name: {storage.Name}");
        }

        if (storage.Role == MsgStorageRole.Attachment && !RegexHolder.AttachmentNamePattern.IsMatch(storage.Name))
        {
            throw new MsgException($"Invalid attachment storage name: {storage.Name}");
        }

        if (strict && storage.Role == MsgStorageRole.EmbeddedMessage)
        {
            var namedMappingCount = storage.Storages.Count(child => child.Name == MsgConstants.NamedPropertyMappingStorageName);
            if (namedMappingCount != 0)
            {
                throw new MsgException("Embedded message storage must not contain a named property mapping storage in strict mode.");
            }
        }

        foreach (var child in storage.Storages)
        {
            ValidateStorage(child, false, strict);
        }
    }

    private static CfbStorage ToCfbStorage(MsgStorage storage)
    {
        var cfbStorage = new CfbStorage(storage.Name)
        {
            Clsid = storage.Clsid.ToArray(),
            StateBits = storage.StateBits,
            CreationTime = storage.CreationTime,
            ModifiedTime = storage.ModifiedTime,
        };

        foreach (var stream in storage.Streams)
        {
            cfbStorage.AddStream(new CfbStream(stream.Name, stream.Data)
            {
                Clsid = stream.Clsid.ToArray(),
                StateBits = stream.StateBits,
                CreationTime = stream.CreationTime,
                ModifiedTime = stream.ModifiedTime,
            });
        }

        foreach (var child in storage.Storages)
        {
            cfbStorage.AddStorage(ToCfbStorage(child));
        }

        return cfbStorage;
    }

    private static class RegexHolder
    {
        public static readonly System.Text.RegularExpressions.Regex RecipientNamePattern = new("^__recip_version1\\.0_#[0-9A-Fa-f]{8}$", System.Text.RegularExpressions.RegexOptions.Compiled);
        public static readonly System.Text.RegularExpressions.Regex AttachmentNamePattern = new("^__attach_version1\\.0_#[0-9A-Fa-f]{8}$", System.Text.RegularExpressions.RegexOptions.Compiled);
    }
}
