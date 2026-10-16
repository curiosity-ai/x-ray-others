using System.Buffers.Binary;
using System.Collections;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using XRay.Email.Cfb;

namespace XRay.Email.Msg;

public sealed class MsgReader : IDisposable
{
    private static readonly Regex RecipientNamePattern = new("^__recip_version1\\.0_#[0-9A-Fa-f]{8}$", RegexOptions.Compiled);
    private static readonly Regex AttachmentNamePattern = new("^__attach_version1\\.0_#[0-9A-Fa-f]{8}$", RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<string, LayoutField> TopLevelPropertyStreamLayout = new Dictionary<string, LayoutField>(StringComparer.Ordinal)
    {
        ["reserved_0"] = new(0, 8),
        ["next_recipient_id"] = new(8, 4),
        ["next_attachment_id"] = new(12, 4),
        ["recipient_count"] = new(16, 4),
        ["attachment_count"] = new(20, 4),
        ["reserved_1"] = new(24, 8),
    };

    private static readonly IReadOnlyDictionary<string, LayoutField> SubobjectPropertyStreamLayout = new Dictionary<string, LayoutField>(StringComparer.Ordinal)
    {
        ["reserved_0"] = new(0, 8),
    };

    private static readonly IReadOnlyDictionary<string, LayoutField> FixedLengthEntryLayout = new Dictionary<string, LayoutField>(StringComparer.Ordinal)
    {
        ["property_tag"] = new(0, 4),
        ["flags"] = new(4, 4),
        ["value"] = new(8, 8),
    };

    private readonly CfbReader _cfbReader;
    private readonly bool _strict;
    private bool _closed;
    private readonly List<string> _validationIssues = [];
    private readonly StorageLayout _storageLayout;
    private readonly IReadOnlyList<PropertyEntryFixedLength> _topLevelFixedEntries;

    public IReadOnlyList<string> ValidationIssues => new ReadOnlyCollection<string>(_validationIssues);

    public static MsgReader FromFile(string path, CancellationToken cancellationToken) => FromFile(path, false, cancellationToken);

    public static MsgReader FromStream(Stream stream, CancellationToken cancellationToken) => FromStream(stream, false, cancellationToken);

    public static MsgReader FromFile(string path, bool strict = false) => FromFile(path, strict, CancellationToken.None);

    /// <summary>Opens a file and reads it on demand rather than loading it whole.</summary>
    public static MsgReader FromFile(string path, bool strict, CancellationToken cancellationToken) => Open(Cfb.CfbReader.FromFile(path, cancellationToken), strict);

    public static MsgReader FromStream(Stream stream, bool strict = false) => FromStream(stream, strict, CancellationToken.None);

    /// <summary>Reads a seekable stream in place; a non-seekable stream is spooled to a temporary file.</summary>
    public static MsgReader FromStream(Stream stream, bool strict, CancellationToken cancellationToken) => Open(Cfb.CfbReader.FromStream(stream, cancellationToken), strict);

    private static MsgReader Open(CfbReader cfb, bool strict)
    {
        try
        {
            return new MsgReader(cfb, strict);
        }
        catch
        {
            cfb.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        _cfbReader.Dispose();
        _closed = true;
    }

    internal MsgReader(CfbReader cfbReader, bool strict = false)
    {
        _cfbReader = cfbReader ?? throw new ArgumentNullException(nameof(cfbReader));
        _strict = strict;
        _storageLayout = BuildStorageLayout();
        var topLevelData = _cfbReader.GetStreamData(_storageLayout.TopLevelPropertyStream.StreamId);
        (TopLevelHeader, _topLevelFixedEntries) = ParseMessagePropertyStreamData(topLevelData, TopLevelHeaderSize, "Top-level", strict ? null : _validationIssues);
        ValidateTopLevelCounts();
        ValidateEmbeddedMessageRules();
    }

    internal CfbReader CfbReader => !_closed ? _cfbReader : throw new MsgException("MsgReader is closed.");

    internal bool Strict => _strict;

    internal StorageLayout StorageLayout => _storageLayout;

    internal PropertyStreamHeaderTopLevel TopLevelHeader { get; }

    internal IEnumerable<PropertyEntryFixedLength> IterTopLevelFixedLengthProperties()
    {
        foreach (var entry in _topLevelFixedEntries)
        {
            yield return entry;
        }
    }

    internal IEnumerable<DirectoryEntry> IterRecipientStorages()
    {
        foreach (var entry in _storageLayout.RecipientStorages)
        {
            yield return entry;
        }
    }

    internal IEnumerable<DirectoryEntry> IterAttachmentStorages()
    {
        foreach (var entry in _storageLayout.AttachmentStorages)
        {
            yield return entry;
        }
    }

    internal (PropertyStreamHeaderTopLevel Header, IReadOnlyList<PropertyEntryFixedLength> Entries) ParseMessagePropertyStream(uint storageStreamId = CfbConstants.RootStreamId, bool embedded = false)
    {
        if (storageStreamId == CfbConstants.RootStreamId)
        {
            // Parsed once in the constructor; parsing again would report its issues twice.
            return (TopLevelHeader, _topLevelFixedEntries);
        }

        var data = _cfbReader.GetStreamData(GetSinglePropertyStreamEntry(storageStreamId).StreamId);
        var issues = _strict ? null : _validationIssues;
        return embedded
            ? ParseEmbeddedPropertyStreamData(data, issues)
            : ParseMessagePropertyStreamData(data, TopLevelHeaderSize, "Top-level", issues);
    }

    internal (PropertyStreamHeaderSubobject Header, IReadOnlyList<PropertyEntryFixedLength> Entries) ParseSubobjectPropertyStream(uint storageStreamId)
    {
        var propertyStream = GetSinglePropertyStreamEntry(storageStreamId);
        return ParseSubobjectPropertyStreamData(_cfbReader.GetStreamData(propertyStream.StreamId), _strict ? null : _validationIssues);
    }

    /// <summary>Size of the property stream header of the top-level message storage (MS-OXMSG 2.4.1.1).</summary>
    internal const int TopLevelHeaderSize = 32;

    /// <summary>Size of the property stream header of an embedded message storage (MS-OXMSG 2.4.1.2).</summary>
    internal const int EmbeddedHeaderSize = 24;

    /// <summary>Size of the property stream header of recipient and attachment storages (MS-OXMSG 2.4.1.3).</summary>
    internal const int SubobjectHeaderSize = 8;

    internal static (PropertyStreamHeaderTopLevel Header, IReadOnlyList<PropertyEntryFixedLength> Entries) ParseTopLevelPropertyStream(byte[] data)
        => ParseMessagePropertyStreamData(data, TopLevelHeaderSize, "Top-level", null);

    /// <summary>
    /// Parses the property stream of an embedded message. The specification gives it a 24-byte
    /// header; files written by earlier versions of this library used the 32-byte top-level header,
    /// so that layout is accepted when it is the only one the stream length fits.
    /// </summary>
    internal static (PropertyStreamHeaderTopLevel Header, IReadOnlyList<PropertyEntryFixedLength> Entries) ParseEmbeddedPropertyStreamData(byte[] data, ICollection<string>? issues)
    {
        var headerSize = data.Length >= TopLevelHeaderSize && (data.Length - EmbeddedHeaderSize) % 16 != 0 && (data.Length - TopLevelHeaderSize) % 16 == 0
            ? TopLevelHeaderSize
            : EmbeddedHeaderSize;
        return ParseMessagePropertyStreamData(data, headerSize, "Embedded message", issues);
    }

    /// <summary>
    /// Parses a message property stream. When <paramref name="issues"/> is null the parse is strict;
    /// otherwise a trailing partial entry is dropped and reported there.
    /// </summary>
    internal static (PropertyStreamHeaderTopLevel Header, IReadOnlyList<PropertyEntryFixedLength> Entries) ParseMessagePropertyStreamData(byte[] data, int headerSize, string context, ICollection<string>? issues)
    {
        if (data.Length < headerSize)
        {
            throw new MsgException($"{context} property stream is smaller than {headerSize}-byte header.");
        }

        var payloadLength = data.Length - headerSize;
        if (payloadLength % 16 != 0)
        {
            var message = $"{context} property payload length {payloadLength} is not divisible by 16 with {headerSize}-byte header.";
            if (issues is null)
            {
                throw new MsgException(message);
            }

            issues.Add(message);
            payloadLength -= payloadLength % 16;
        }

        var header = new PropertyStreamHeaderTopLevel(
            data.AsSpan(GetLayout(TopLevelPropertyStreamLayout, "reserved_0").Offset, 8).ToArray(),
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(GetLayout(TopLevelPropertyStreamLayout, "next_recipient_id").Offset, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(GetLayout(TopLevelPropertyStreamLayout, "next_attachment_id").Offset, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(GetLayout(TopLevelPropertyStreamLayout, "recipient_count").Offset, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(GetLayout(TopLevelPropertyStreamLayout, "attachment_count").Offset, 4)),
            headerSize >= TopLevelHeaderSize ? data.AsSpan(GetLayout(TopLevelPropertyStreamLayout, "reserved_1").Offset, 8).ToArray() : new byte[8]);

        return (header, ParseFixedLengthEntries(data.AsSpan(headerSize, payloadLength).ToArray(), context.ToLowerInvariant()));
    }

    internal static (PropertyStreamHeaderSubobject Header, IReadOnlyList<PropertyEntryFixedLength> Entries) ParseSubobjectPropertyStreamData(byte[] data, ICollection<string>? issues = null)
    {
        if (data.Length < SubobjectHeaderSize)
        {
            throw new MsgException("Subobject property stream is smaller than 8-byte header.");
        }

        var payloadLength = data.Length - SubobjectHeaderSize;
        if (payloadLength % 16 != 0)
        {
            var message = $"subobject property payload length {payloadLength} is not divisible by 16.";
            if (issues is null)
            {
                throw new MsgException(message);
            }

            issues.Add(message);
            payloadLength -= payloadLength % 16;
        }

        var header = new PropertyStreamHeaderSubobject(data.AsSpan(GetLayout(SubobjectPropertyStreamLayout, "reserved_0").Offset, 8).ToArray());
        return (header, ParseFixedLengthEntries(data.AsSpan(SubobjectHeaderSize, payloadLength).ToArray(), "subobject"));
    }

    private static IReadOnlyList<PropertyEntryFixedLength> ParseFixedLengthEntries(byte[] payload, string context)
    {
        if (payload.Length % 16 != 0)
        {
            throw new MsgException($"{context} property payload length {payload.Length} is not divisible by 16.");
        }

        var entries = new List<PropertyEntryFixedLength>(payload.Length / 16);
        for (var offset = 0; offset < payload.Length; offset += 16)
        {
            entries.Add(new PropertyEntryFixedLength(
                BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset + GetLayout(FixedLengthEntryLayout, "property_tag").Offset, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset + GetLayout(FixedLengthEntryLayout, "flags").Offset, 4)),
                payload.AsSpan(offset + GetLayout(FixedLengthEntryLayout, "value").Offset, 8).ToArray()));
        }

        return entries;
    }

    private StorageLayout BuildStorageLayout()
    {
        var children = _cfbReader.IterChildren(CfbConstants.RootStreamId).ToArray();
        var namedPropertyMappingStorages = children.Where(child => child.IsStorage() && child.Name == MsgConstants.NamedPropertyMappingStorageName).ToArray();
        if (namedPropertyMappingStorages.Length != 1)
        {
            var message = $"Expected exactly one top-level named property mapping storage, found {namedPropertyMappingStorages.Length}";
            if (_strict || namedPropertyMappingStorages.Length > 1)
            {
                throw new MsgException(message);
            }

            _validationIssues.Add(message);
        }

        var topLevelPropertyStreams = children.Where(child => child.IsStream() && child.Name == MsgConstants.PropertyStreamName).ToArray();
        if (topLevelPropertyStreams.Length != 1)
        {
            throw new MsgException($"Expected exactly one top-level property stream, found {topLevelPropertyStreams.Length}");
        }

        var recipientStorages = children.Where(child => child.IsStorage() && RecipientNamePattern.IsMatch(child.Name)).ToArray();
        var attachmentStorages = children.Where(child => child.IsStorage() && AttachmentNamePattern.IsMatch(child.Name)).ToArray();

        foreach (var child in children)
        {
            if (child.IsStorage() && child.Name.StartsWith("__recip_version1.0_#", StringComparison.Ordinal) && !RecipientNamePattern.IsMatch(child.Name))
            {
                throw new MsgException($"Invalid recipient storage naming pattern: {child.Name}");
            }

            if (child.IsStorage() && child.Name.StartsWith("__attach_version1.0_#", StringComparison.Ordinal) && !AttachmentNamePattern.IsMatch(child.Name))
            {
                throw new MsgException($"Invalid attachment storage naming pattern: {child.Name}");
            }
        }

        if ((uint)recipientStorages.Length > MsgConstants.MaxRecipientStorages)
        {
            throw new MsgException($"Recipient storage count exceeds {MsgConstants.MaxRecipientStorages}.");
        }

        if ((uint)attachmentStorages.Length > MsgConstants.MaxAttachmentStorages)
        {
            throw new MsgException($"Attachment storage count exceeds {MsgConstants.MaxAttachmentStorages}.");
        }

        return new StorageLayout(recipientStorages, attachmentStorages, namedPropertyMappingStorages.FirstOrDefault(), topLevelPropertyStreams[0]);
    }

    private void ValidateTopLevelCounts()
    {
        if ((uint)_storageLayout.RecipientStorages.Count != TopLevelHeader.RecipientCount)
        {
            var message = $"Recipient storage count mismatch: {_storageLayout.RecipientStorages.Count} != {TopLevelHeader.RecipientCount}";
            if (_strict)
            {
                throw new MsgException(message);
            }

            _validationIssues.Add(message);
        }

        if ((uint)_storageLayout.AttachmentStorages.Count != TopLevelHeader.AttachmentCount)
        {
            var message = $"Attachment storage count mismatch: {_storageLayout.AttachmentStorages.Count} != {TopLevelHeader.AttachmentCount}";
            if (_strict)
            {
                throw new MsgException(message);
            }

            _validationIssues.Add(message);
        }
    }

    private void ValidateEmbeddedMessageRules()
    {
        foreach (var attachment in _storageLayout.AttachmentStorages)
        {
            var embeddedStorage = _cfbReader.FindChildByName(attachment.StreamId, MsgConstants.EmbeddedMessageStorageName);
            if (embeddedStorage is null)
            {
                continue;
            }

            if (!embeddedStorage.IsStorage())
            {
                throw new MsgException("Embedded message entry exists but is not a storage object.");
            }

            var embeddedNameId = _cfbReader.FindChildByName(embeddedStorage.StreamId, MsgConstants.NamedPropertyMappingStorageName);
            if (embeddedNameId is not null && !embeddedNameId.IsStorage())
            {
                throw new MsgException("Embedded named property mapping entry exists but is not a storage object.");
            }
        }
    }

    private DirectoryEntry GetSinglePropertyStreamEntry(uint storageStreamId)
    {
        var propertyStreams = _cfbReader.IterChildren(storageStreamId)
            .Where(child => child.IsStream() && child.Name == MsgConstants.PropertyStreamName)
            .ToArray();
        if (propertyStreams.Length != 1)
        {
            throw new MsgException($"Expected one property stream in storage sid={storageStreamId}, found {propertyStreams.Length}");
        }

        return propertyStreams[0];
    }

    private static LayoutField GetLayout(IReadOnlyDictionary<string, LayoutField> map, string name) => map[name];

    private readonly record struct LayoutField(int Offset, int Size);
}
