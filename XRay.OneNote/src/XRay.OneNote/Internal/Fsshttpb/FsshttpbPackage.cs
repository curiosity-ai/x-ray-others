using XRay.OneNote.Internal.OneStore;

namespace XRay.OneNote.Internal.Fsshttpb;

/// <summary>The partition ids of the objects of a revision store in an object group (MS-ONESTORE 2.7.6).</summary>
internal static class Partitions
{
    public const ulong ObjectData = 1;
    public const ulong FileData = 2;
    public const ulong Metadata = 4;
}

/// <summary>A declaration in an object group: an object, or an object whose data is a blob.</summary>
internal sealed record ObjectDeclaration(ExtendedGuid ObjectId, ulong PartitionId, ExtendedGuid BlobId, bool IsBlob);

/// <summary>The data of one declaration: inline bytes with the references they use, or a reference to a blob.</summary>
internal sealed record ObjectDataItem(List<ExtendedGuid> ObjectReferences, List<CellId> CellReferences, byte[]? Data, ExtendedGuid BlobId, bool IsBlobReference);

internal abstract record DataElement(ExtendedGuid Id, SerialNumber Serial);

internal sealed record StorageIndexElement(
    ExtendedGuid Id,
    SerialNumber Serial,
    Dictionary<CellId, (ExtendedGuid Mapping, SerialNumber Serial)> CellMappings,
    Dictionary<ExtendedGuid, (ExtendedGuid Mapping, SerialNumber Serial)> RevisionMappings) : DataElement(Id, Serial);

internal sealed record StorageManifestElement(
    ExtendedGuid Id,
    SerialNumber Serial,
    Guid Schema,
    Dictionary<ExtendedGuid, CellId> Roots) : DataElement(Id, Serial);

internal sealed record CellManifestElement(ExtendedGuid Id, SerialNumber Serial, ExtendedGuid CurrentRevision) : DataElement(Id, Serial);

internal sealed record RevisionManifestElement(
    ExtendedGuid Id,
    SerialNumber Serial,
    ExtendedGuid RevisionId,
    ExtendedGuid BaseRevisionId,
    List<(ExtendedGuid Root, ExtendedGuid Object)> RootDeclarations,
    List<ExtendedGuid> ObjectGroups) : DataElement(Id, Serial);

internal sealed record ObjectGroupElement(
    ExtendedGuid Id,
    SerialNumber Serial,
    List<ObjectDeclaration> Declarations,
    List<ObjectDataItem> Data) : DataElement(Id, Serial);

internal sealed record BlobElement(ExtendedGuid Id, SerialNumber Serial, byte[] Data) : DataElement(Id, Serial);

/// <summary>
/// A parsed MS-ONESTORE alternative packaging (section 2.8): the file's identity and the data elements of
/// its data element package (MS-FSSHTTPB 2.2.1.12), keyed by element id.
/// </summary>
internal sealed class FsshttpbPackage
{
    // Stream object types (MS-FSSHTTPB 2.2.1.5).
    private const uint DataElementType = 0x01;
    private const uint ObjectDataBlobHeader = 0x02;
    private const uint ObjectExcludedData = 0x03;
    private const uint ObjectBlobDeclaration = 0x05;
    private const uint DataElementHash = 0x06;
    private const uint StorageManifestRootDeclare = 0x07;
    private const uint RevisionManifestRootDeclare = 0x0A;
    private const uint CellManifestCurrentRevision = 0x0B;
    private const uint StorageManifestSchemaGuid = 0x0C;
    private const uint StorageIndexRevisionMapping = 0x0D;
    private const uint StorageIndexCellMapping = 0x0E;
    private const uint StorageIndexManifestMapping = 0x11;
    private const uint DataElementPackageType = 0x15;
    private const uint ObjectData = 0x16;
    private const uint ObjectDeclarationType = 0x18;
    private const uint RevisionManifestGroupReference = 0x19;
    private const uint RevisionManifestType = 0x1A;
    private const uint ObjectBlobReference = 0x1C;
    private const uint ObjectGroupDeclarations = 0x1D;
    private const uint ObjectGroupData = 0x1E;
    private const uint PackagingType = 0x7A;

    // Data element types (MS-FSSHTTPB 2.2.1.12.1).
    private const ulong ElementStorageIndex = 0x01;
    private const ulong ElementStorageManifest = 0x02;
    private const ulong ElementCellManifest = 0x03;
    private const ulong ElementRevisionManifest = 0x04;
    private const ulong ElementObjectGroup = 0x05;
    private const ulong ElementFragment = 0x06;
    private const ulong ElementBlob = 0x0A;

    // Bounds for what a damaged or hostile package may ask for.
    private const int MaxElements = 4_000_000;

    private FsshttpbPackage()
    {
    }

    public static readonly Guid PackageFormat = new("638DE92F-A6D4-4BC1-9A36-B3FC2511A5B7");

    public Guid FileType { get; private init; }

    public Guid FileGuid { get; private init; }

    public Guid CellSchema { get; private init; }

    public ExtendedGuid StorageIndexId { get; private init; }

    public Dictionary<ExtendedGuid, DataElement> Elements { get; } = new();

    public List<StorageIndexElement> StorageIndexes { get; } = [];

    public List<StorageManifestElement> StorageManifests { get; } = [];

    /// <summary>Whether <paramref name="data"/> has the packaging header at <paramref name="offset"/>.</summary>
    public static bool IsPackagingAt(byte[] data, long offset)
    {
        if (offset < 0 || offset > data.Length - 68)
        {
            return false;
        }

        return new Guid(data.AsSpan((int)offset + 48, 16)) == PackageFormat;
    }

    /// <summary>Parses the packaging (2.8.1) that starts at <paramref name="offset"/>.</summary>
    public static FsshttpbPackage Parse(byte[] data, int offset, CancellationToken ct)
    {
        var reader = new FsshttpbReader(data, offset, data.Length);
        var fileType = reader.ReadGuid();
        var file = reader.ReadGuid();
        reader.ReadGuid();
        var format = reader.ReadGuid();
        if (format != PackageFormat)
        {
            throw new UnsupportedFileFormatException("Unsupported file format", format.ToString("D"));
        }

        reader.Skip(4);
        var start = reader.ReadHeader();
        if (start.Kind != HeaderKind.Start32 || start.Type != PackagingType)
        {
            throw new FileCorruptedException("The packaging structure does not start with its stream object header");
        }

        var storageIndexId = reader.ReadExtendedGuid();
        var cellSchema = reader.ReadGuid();

        var package = new FsshttpbPackage { FileType = fileType, FileGuid = file, CellSchema = cellSchema, StorageIndexId = storageIndexId };
        package.ReadDataElementPackage(reader, ct);
        return package;
    }

    private void ReadDataElementPackage(FsshttpbReader reader, CancellationToken ct)
    {
        var header = reader.ReadHeader();
        if (header.Kind != HeaderKind.Start16 || header.Type != DataElementPackageType)
        {
            throw new FileCorruptedException("The data element package is missing");
        }

        reader.ReadByte();
        var count = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (reader.AtEnd)
            {
                // A package cut off before its end markers: keep the elements read so far.
                break;
            }

            var next = reader.PeekHeader();
            if (next.Kind == HeaderKind.End8 && next.Type == DataElementPackageType)
            {
                break;
            }

            if (++count > MaxElements)
            {
                throw new FileCorruptedException("The package holds too many data elements");
            }

            ReadDataElement(reader);
        }
    }

    private void ReadDataElement(FsshttpbReader reader)
    {
        var header = reader.ReadHeader();
        if (header.Kind != HeaderKind.Start16 || header.Type != DataElementType)
        {
            throw new FileCorruptedException($"Expected a data element, found stream object type 0x{header.Type:X}");
        }

        var id = reader.ReadExtendedGuid();
        var serial = reader.ReadSerialNumber();
        var type = reader.ReadCompactUInt();

        DataElement? element = type switch
        {
            ElementStorageIndex => ReadStorageIndex(reader, id, serial),
            ElementStorageManifest => ReadStorageManifest(reader, id, serial),
            ElementCellManifest => ReadCellManifest(reader, id, serial),
            ElementRevisionManifest => ReadRevisionManifest(reader, id, serial),
            ElementObjectGroup => ReadObjectGroup(reader, id, serial),
            ElementBlob => ReadBlob(reader, id, serial),
            ElementFragment => SkipFragment(reader),
            _ => SkipToElementEnd(reader),
        };

        ExpectEnd(reader, DataElementType);
        if (element is null)
        {
            return;
        }

        Elements.TryAdd(id, element);
        if (element is StorageIndexElement index)
        {
            StorageIndexes.Add(index);
        }
        else if (element is StorageManifestElement manifest)
        {
            StorageManifests.Add(manifest);
        }
    }

    private static void ExpectEnd(FsshttpbReader reader, uint type)
    {
        var end = reader.ReadHeader();
        if (end.Kind != HeaderKind.End8 || end.Type != type)
        {
            throw new FileCorruptedException($"Expected the end of stream object 0x{type:X}, found type 0x{end.Type:X}");
        }
    }

    /// <summary>Skips what remains of a data element: headers and their data up to the element's end marker.</summary>
    private static DataElement? SkipToElementEnd(FsshttpbReader reader)
    {
        var depth = 0;
        while (true)
        {
            var header = reader.PeekHeader();
            if (header.Kind == HeaderKind.End8 && header.Type == DataElementType && depth == 0)
            {
                return null;
            }

            reader.ReadHeader();
            if (header.IsStart)
            {
                reader.Skip(header.Length);
                if (header.Compound)
                {
                    depth++;
                }
            }
            else if (depth > 0)
            {
                depth--;
            }
        }
    }

    /// <summary>Data element fragments (2.2.1.12.7) are not used by the packaging this reader supports; they are skipped.</summary>
    private static DataElement? SkipFragment(FsshttpbReader reader) => SkipToElementEnd(reader);

    private static StorageIndexElement ReadStorageIndex(FsshttpbReader reader, ExtendedGuid id, SerialNumber serial)
    {
        var cells = new Dictionary<CellId, (ExtendedGuid, SerialNumber)>();
        var revisions = new Dictionary<ExtendedGuid, (ExtendedGuid, SerialNumber)>();
        while (!(reader.PeekHeader() is { Kind: HeaderKind.End8, Type: DataElementType }))
        {
            var header = reader.ReadHeader();
            var end = reader.Position + (int)Math.Min(header.Length, int.MaxValue);
            switch (header.Type)
            {
                case StorageIndexCellMapping:
                {
                    var cell = reader.ReadCellId();
                    var mapping = reader.ReadExtendedGuid();
                    cells.TryAdd(cell, (mapping, reader.ReadSerialNumber()));
                    break;
                }

                case StorageIndexRevisionMapping:
                {
                    var revision = reader.ReadExtendedGuid();
                    var mapping = reader.ReadExtendedGuid();
                    revisions.TryAdd(revision, (mapping, reader.ReadSerialNumber()));
                    break;
                }

                default:
                    reader.Position = end;
                    break;
            }
        }

        return new StorageIndexElement(id, serial, cells, revisions);
    }

    private static StorageManifestElement ReadStorageManifest(FsshttpbReader reader, ExtendedGuid id, SerialNumber serial)
    {
        var schema = Guid.Empty;
        var roots = new Dictionary<ExtendedGuid, CellId>();
        while (!(reader.PeekHeader() is { Kind: HeaderKind.End8, Type: DataElementType }))
        {
            var header = reader.ReadHeader();
            var end = reader.Position + (int)Math.Min(header.Length, int.MaxValue);
            switch (header.Type)
            {
                case StorageManifestSchemaGuid:
                    schema = reader.ReadGuid();
                    break;
                case StorageManifestRootDeclare:
                {
                    var root = reader.ReadExtendedGuid();
                    roots.TryAdd(root, reader.ReadCellId());
                    break;
                }

                default:
                    reader.Position = end;
                    break;
            }
        }

        return new StorageManifestElement(id, serial, schema, roots);
    }

    private static CellManifestElement ReadCellManifest(FsshttpbReader reader, ExtendedGuid id, SerialNumber serial)
    {
        var current = default(ExtendedGuid);
        while (!(reader.PeekHeader() is { Kind: HeaderKind.End8, Type: DataElementType }))
        {
            var header = reader.ReadHeader();
            var end = reader.Position + (int)Math.Min(header.Length, int.MaxValue);
            if (header.Type == CellManifestCurrentRevision)
            {
                current = reader.ReadExtendedGuid();
            }
            else
            {
                reader.Position = end;
            }
        }

        return new CellManifestElement(id, serial, current);
    }

    private static RevisionManifestElement ReadRevisionManifest(FsshttpbReader reader, ExtendedGuid id, SerialNumber serial)
    {
        ExtendedGuid revisionId = default;
        ExtendedGuid baseRevision = default;
        var roots = new List<(ExtendedGuid, ExtendedGuid)>();
        var groups = new List<ExtendedGuid>();
        while (!(reader.PeekHeader() is { Kind: HeaderKind.End8, Type: DataElementType }))
        {
            var header = reader.ReadHeader();
            var end = reader.Position + (int)Math.Min(header.Length, int.MaxValue);
            switch (header.Type)
            {
                case RevisionManifestType:
                    revisionId = reader.ReadExtendedGuid();
                    baseRevision = reader.ReadExtendedGuid();
                    break;
                case RevisionManifestRootDeclare:
                {
                    var root = reader.ReadExtendedGuid();
                    roots.Add((root, reader.ReadExtendedGuid()));
                    break;
                }

                case RevisionManifestGroupReference:
                    groups.Add(reader.ReadExtendedGuid());
                    break;
                default:
                    reader.Position = end;
                    break;
            }
        }

        return new RevisionManifestElement(id, serial, revisionId, baseRevision, roots, groups);
    }

    private static ObjectGroupElement ReadObjectGroup(FsshttpbReader reader, ExtendedGuid id, SerialNumber serial)
    {
        var declarations = new List<ObjectDeclaration>();
        var data = new List<ObjectDataItem>();
        while (!(reader.PeekHeader() is { Kind: HeaderKind.End8, Type: DataElementType }))
        {
            var header = reader.ReadHeader();
            var end = reader.Position + (int)Math.Min(header.Length, int.MaxValue);
            switch (header.Type)
            {
                case ObjectGroupDeclarations when header.IsStart:
                    ReadDeclarations(reader, declarations);
                    break;
                case ObjectGroupData when header.IsStart:
                    ReadGroupData(reader, data);
                    break;
                case DataElementHash:
                    reader.Position = end;
                    break;
                default:
                    // Object metadata declarations (type 0x79) and anything unknown: skip the start and
                    // whatever its compound body holds is handled by the loop's own header handling.
                    reader.Position = end;
                    break;
            }
        }

        return new ObjectGroupElement(id, serial, declarations, data);
    }

    private static void ReadDeclarations(FsshttpbReader reader, List<ObjectDeclaration> declarations)
    {
        while (true)
        {
            var header = reader.ReadHeader();
            if (header.Kind == HeaderKind.End8 && header.Type == ObjectGroupDeclarations)
            {
                return;
            }

            var end = reader.Position + (int)Math.Min(header.Length, int.MaxValue);
            if (header.Type == ObjectDeclarationType)
            {
                var objectId = reader.ReadExtendedGuid();
                var partition = reader.ReadCompactUInt();
                declarations.Add(new ObjectDeclaration(objectId, partition, default, false));
            }
            else if (header.Type == ObjectBlobDeclaration)
            {
                var objectId = reader.ReadExtendedGuid();
                var blobId = reader.ReadExtendedGuid();
                var partition = reader.ReadCompactUInt();
                declarations.Add(new ObjectDeclaration(objectId, partition, blobId, true));
            }
            else if (!header.IsStart)
            {
                continue;
            }

            if (end > reader.Position && end <= reader.End)
            {
                reader.Position = end;
            }
            else if (end > reader.End)
            {
                throw new FileCorruptedException("An object declaration runs past the end of the data");
            }
        }
    }

    private static void ReadGroupData(FsshttpbReader reader, List<ObjectDataItem> data)
    {
        while (true)
        {
            var header = reader.ReadHeader();
            if (header.Kind == HeaderKind.End8 && header.Type == ObjectGroupData)
            {
                return;
            }

            if (!header.IsStart)
            {
                continue;
            }

            var end = reader.Position + header.Length;
            if (end > reader.End)
            {
                throw new FileCorruptedException("An object data item runs past the end of the data");
            }

            var inner = new FsshttpbReader(reader.PeekAll(), reader.Position, (int)end);
            switch (header.Type)
            {
                case ObjectData:
                {
                    var objects = inner.ReadExtendedGuidArray();
                    var cells = inner.ReadCellIdArray();
                    data.Add(new ObjectDataItem(objects, cells, inner.ReadBinaryItem(), default, false));
                    break;
                }

                case ObjectExcludedData:
                {
                    var objects = inner.ReadExtendedGuidArray();
                    var cells = inner.ReadCellIdArray();
                    data.Add(new ObjectDataItem(objects, cells, null, default, false));
                    break;
                }

                case ObjectBlobReference:
                {
                    var objects = inner.ReadExtendedGuidArray();
                    var cells = inner.ReadCellIdArray();
                    data.Add(new ObjectDataItem(objects, cells, null, inner.ReadExtendedGuid(), true));
                    break;
                }
            }

            reader.Position = (int)end;
        }
    }

    private static BlobElement ReadBlob(FsshttpbReader reader, ExtendedGuid id, SerialNumber serial)
    {
        var header = reader.ReadHeader();
        if (!header.IsStart || header.Type != ObjectDataBlobHeader)
        {
            throw new FileCorruptedException("An object data BLOB element does not hold a BLOB");
        }

        // The data is a binary item: a compact length, then the bytes (2.2.1.12.8).
        return new BlobElement(id, serial, reader.ReadBinaryItem());
    }
}
