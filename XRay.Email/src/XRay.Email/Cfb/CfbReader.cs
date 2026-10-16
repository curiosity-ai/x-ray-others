using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;

namespace XRay.Email.Cfb;

public sealed class CfbReader : IDisposable
{
    private static readonly IReadOnlyDictionary<string, LayoutField> HeaderLayout = new Dictionary<string, LayoutField>(StringComparer.Ordinal)
    {
        ["header_signature"] = new(0x0000, 8),
        ["header_clsid"] = new(0x0008, 16),
        ["minor_version"] = new(0x0018, 2),
        ["major_version"] = new(0x001A, 2),
        ["byte_order"] = new(0x001C, 2),
        ["sector_shift"] = new(0x001E, 2),
        ["mini_sector_shift"] = new(0x0020, 2),
        ["reserved"] = new(0x0022, 6),
        ["number_of_directory_sectors"] = new(0x0028, 4),
        ["number_of_fat_sectors"] = new(0x002C, 4),
        ["first_directory_sector_location"] = new(0x0030, 4),
        ["transaction_signature_number"] = new(0x0034, 4),
        ["mini_stream_cutoff_size"] = new(0x0038, 4),
        ["first_mini_fat_sector_location"] = new(0x003C, 4),
        ["number_of_mini_fat_sectors"] = new(0x0040, 4),
        ["first_difat_sector_location"] = new(0x0044, 4),
        ["number_of_difat_sectors"] = new(0x0048, 4),
        ["difat"] = new(0x004C, 436),
    };

    private static readonly IReadOnlyDictionary<string, LayoutField> DirectoryEntryLayout = new Dictionary<string, LayoutField>(StringComparer.Ordinal)
    {
        ["directory_entry_name"] = new(0, 64),
        ["directory_entry_name_length"] = new(64, 2),
        ["object_type"] = new(66, 1),
        ["color_flag"] = new(67, 1),
        ["left_sibling_id"] = new(68, 4),
        ["right_sibling_id"] = new(72, 4),
        ["child_id"] = new(76, 4),
        ["clsid"] = new(80, 16),
        ["state_bits"] = new(96, 4),
        ["creation_time"] = new(100, 8),
        ["modified_time"] = new(108, 8),
        ["starting_sector_location"] = new(116, 4),
        ["stream_size"] = new(120, 8),
    };

    private readonly byte[] _data;
    private readonly Dictionary<uint, byte[]> _streamData = [];

    public CfbReader(byte[] data)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        Header = ParseHeader();
        ValidateHeader();
        Difat = BuildDifat().AsReadOnly();
        Fat = BuildFat().AsReadOnly();
        MiniFat = BuildMiniFat().AsReadOnly();
        DirectoryEntries = BuildDirectoryEntries().AsReadOnly();
        RootEntry = GetRootEntry();
        MiniStream = RootEntry.StreamSize > 0 ? ReadStreamBytes(RootEntry) : [];
        _streamData[RootEntry.StreamId] = MiniStream;
    }

    public Header Header { get; }

    public ReadOnlyCollection<uint> Difat { get; }

    public ReadOnlyCollection<uint> Fat { get; }

    public ReadOnlyCollection<uint> MiniFat { get; }

    public ReadOnlyCollection<DirectoryEntry> DirectoryEntries { get; }

    public DirectoryEntry RootEntry { get; }

    public byte[] MiniStream { get; }

    public int MajorVersion => Header.MajorVersion;

    public int SectorSize => Header.SectorSize;

    public int MiniSectorSize => Header.MiniSectorSize;

    public int DirectoryEntryCount => DirectoryEntries.Count;

    public int MaterializedStreamCount => _streamData.Count;

    public int FileSize => _data.Length;

    public static CfbReader FromFile(string path) => new(File.ReadAllBytes(path));

    public static CfbReader FromStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return new CfbReader(buffer.ToArray());
    }

    public void Dispose()
    {
    }

    public DirectoryEntry GetEntry(uint streamId)
    {
        if (streamId >= DirectoryEntries.Count)
        {
            throw new CfbException($"Invalid stream id: {streamId}");
        }

        return DirectoryEntries[(int)streamId];
    }

    public byte[] GetStreamData(uint streamId)
    {
        if (_streamData.TryGetValue(streamId, out var existing))
        {
            return existing;
        }

        var entry = GetEntry(streamId);
        if (!entry.IsStream() && !entry.IsRoot())
        {
            throw new CfbException($"Stream data is not available for non-stream entry stream_id={streamId}");
        }

        var payload = ReadStreamBytes(entry);
        _streamData[streamId] = payload;
        return payload;
    }

    public IEnumerable<DirectoryEntry> IterStorages()
    {
        foreach (var entry in DirectoryEntries)
        {
            if (entry.IsStorage())
            {
                yield return entry;
            }
        }
    }

    public IEnumerable<DirectoryEntry> IterStreams()
    {
        foreach (var entry in DirectoryEntries)
        {
            if (entry.IsStream())
            {
                yield return entry;
            }
        }
    }

    public IEnumerable<DirectoryEntry> IterChildren(uint storageStreamId)
    {
        var storage = GetEntry(storageStreamId);
        if (!storage.IsStorage() || storage.ChildId == CfbConstants.NOSTREAM)
        {
            yield break;
        }

        foreach (var streamId in CollectSiblingsInOrder(storage.ChildId))
        {
            yield return DirectoryEntries[(int)streamId];
        }
    }

    public IEnumerable<(int Depth, DirectoryEntry Entry)> IterTree(uint startStreamId = 0)
    {
        var stack = new Stack<(int Depth, uint StreamId)>();
        var visited = new HashSet<uint>();
        stack.Push((0, startStreamId));
        while (stack.Count > 0)
        {
            var (depth, streamId) = stack.Pop();
            if (!visited.Add(streamId))
            {
                throw new CfbException($"Directory entry {streamId} is reachable more than once; the directory tree is cyclic.");
            }

            var entry = GetEntry(streamId);
            yield return (depth, entry);

            if (entry.IsStorage() && entry.ChildId != CfbConstants.NOSTREAM)
            {
                var childIds = CollectSiblingsInOrder(entry.ChildId);
                for (var i = childIds.Count - 1; i >= 0; i--)
                {
                    stack.Push((depth + 1, childIds[i]));
                }
            }
        }
    }

    public DirectoryEntry? FindChildByName(uint storageStreamId, string name)
    {
        return IterChildren(storageStreamId).FirstOrDefault(child => child.Name == name);
    }

    public DirectoryEntry? ResolvePath(IEnumerable<string> names, uint startStreamId = 0)
    {
        var current = GetEntry(startStreamId);
        var currentId = startStreamId;
        foreach (var part in names)
        {
            if (!current.IsStorage())
            {
                return null;
            }

            var child = FindChildByName(currentId, part);
            if (child is null)
            {
                return null;
            }

            current = child;
            currentId = child.StreamId;
        }

        return current;
    }

    private Header ParseHeader()
    {
        if (_data.Length < 512)
        {
            throw new CfbException("File is too small to contain a Compound File Binary (CFB) header.");
        }

        if (!_data.AsSpan(0, 8).SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }))
        {
            throw new CfbException("Invalid Compound File Binary (CFB) signature.");
        }

        var headerSignature = ReadBytes(GetLayout(HeaderLayout, "header_signature"));
        var headerClsid = ReadBytes(GetLayout(HeaderLayout, "header_clsid"));
        var minorVersion = ReadUInt16(GetLayout(HeaderLayout, "minor_version").Offset);
        var majorVersion = ReadUInt16(GetLayout(HeaderLayout, "major_version").Offset);
        var byteOrder = ReadUInt16(GetLayout(HeaderLayout, "byte_order").Offset);
        var sectorShift = ReadUInt16(GetLayout(HeaderLayout, "sector_shift").Offset);
        var miniSectorShift = ReadUInt16(GetLayout(HeaderLayout, "mini_sector_shift").Offset);
        var reserved = ReadBytes(GetLayout(HeaderLayout, "reserved"));
        var numberOfDirectorySectors = ReadUInt32(GetLayout(HeaderLayout, "number_of_directory_sectors").Offset);
        var numberOfFatSectors = ReadUInt32(GetLayout(HeaderLayout, "number_of_fat_sectors").Offset);
        var firstDirectorySectorLocation = ReadUInt32(GetLayout(HeaderLayout, "first_directory_sector_location").Offset);
        var transactionSignatureNumber = ReadUInt32(GetLayout(HeaderLayout, "transaction_signature_number").Offset);
        var miniStreamCutoffSize = ReadUInt32(GetLayout(HeaderLayout, "mini_stream_cutoff_size").Offset);
        var firstMiniFatSectorLocation = ReadUInt32(GetLayout(HeaderLayout, "first_mini_fat_sector_location").Offset);
        var numberOfMiniFatSectors = ReadUInt32(GetLayout(HeaderLayout, "number_of_mini_fat_sectors").Offset);
        var firstDifatSectorLocation = ReadUInt32(GetLayout(HeaderLayout, "first_difat_sector_location").Offset);
        var numberOfDifatSectors = ReadUInt32(GetLayout(HeaderLayout, "number_of_difat_sectors").Offset);
        var difatLayout = GetLayout(HeaderLayout, "difat");
        var difat = new uint[109];
        for (var i = 0; i < difat.Length; i++)
        {
            difat[i] = ReadUInt32(difatLayout.Offset + (i * 4));
        }

        if (byteOrder != CfbConstants.ByteOrderLittleEndian)
        {
            throw new CfbException("Unsupported byte order; expected little-endian marker 0xFFFE.");
        }

        return new Header(
            headerSignature,
            headerClsid,
            minorVersion,
            majorVersion,
            byteOrder,
            sectorShift,
            miniSectorShift,
            reserved,
            numberOfDirectorySectors,
            numberOfFatSectors,
            firstDirectorySectorLocation,
            transactionSignatureNumber,
            miniStreamCutoffSize,
            firstMiniFatSectorLocation,
            numberOfMiniFatSectors,
            firstDifatSectorLocation,
            numberOfDifatSectors,
            difat);
    }

    private void ValidateHeader()
    {
        if (Header.MajorVersion is not (3 or 4))
        {
            throw new CfbException($"Unsupported major version: {Header.MajorVersion}");
        }

        var expectedShift = Header.MajorVersion == 3 ? (ushort)9 : (ushort)12;
        if (Header.SectorShift != expectedShift)
        {
            throw new CfbException($"Invalid sector shift {Header.SectorShift} for version {Header.MajorVersion}.");
        }

        if (Header.MiniSectorShift != 6)
        {
            throw new CfbException($"Invalid mini sector shift: {Header.MiniSectorShift}");
        }

        if (Header.MiniStreamCutoffSize != CfbConstants.MiniStreamCutoffSize)
        {
            throw new CfbException($"Invalid mini stream cutoff size: {Header.MiniStreamCutoffSize}");
        }

        if (Header.HeaderClsid.Any(value => value != 0))
        {
            throw new CfbException("Invalid header CLSID; expected all zeroes.");
        }

        if (Header.MajorVersion == 3 && Header.NumberOfDirectorySectors != 0)
        {
            throw new CfbException("For version 3, number_of_directory_sectors must be zero.");
        }
    }

    private int SectorOffset(uint sectorNumber)
    {
        if (sectorNumber > CfbConstants.MAXREGSECT)
        {
            throw new CfbException($"Invalid sector number: 0x{sectorNumber:X8}");
        }

        var offset = ((long)sectorNumber + 1) * Header.SectorSize;
        if (offset > int.MaxValue)
        {
            throw new CfbException($"Sector {sectorNumber} points past end of file.");
        }

        return (int)offset;
    }

    private byte[] ReadSector(uint sectorNumber)
    {
        var offset = SectorOffset(sectorNumber);
        var end = offset + Header.SectorSize;
        if (end > _data.Length)
        {
            throw new CfbException($"Sector {sectorNumber} points past end of file.");
        }

        return _data[offset..end];
    }

    private List<uint> BuildDifat()
    {
        var difatEntries = Header.Difat.Where(value => value != (uint)SectorMarker.FREESECT).ToList();
        var nextDifatSector = Header.FirstDifatSectorLocation;
        var visited = new HashSet<uint>();

        for (var i = 0; i < Header.NumberOfDifatSectors; i++)
        {
            if (nextDifatSector is (uint)SectorMarker.ENDOFCHAIN or (uint)SectorMarker.FREESECT)
            {
                break;
            }

            if (!visited.Add(nextDifatSector))
            {
                throw new CfbException("Cycle detected in DIFAT chain.");
            }

            var sectorData = ReadSector(nextDifatSector);
            var entriesPerSector = (Header.SectorSize / 4) - 1;
            for (var entryIndex = 0; entryIndex < entriesPerSector; entryIndex++)
            {
                var entry = BinaryPrimitives.ReadUInt32LittleEndian(sectorData.AsSpan(entryIndex * 4, 4));
                if (entry != (uint)SectorMarker.FREESECT)
                {
                    difatEntries.Add(entry);
                }
            }

            nextDifatSector = BinaryPrimitives.ReadUInt32LittleEndian(sectorData.AsSpan(Header.SectorSize - 4, 4));
        }

        return difatEntries.Take((int)Header.NumberOfFatSectors).ToList();
    }

    private List<uint> BuildFat()
    {
        var fatEntries = new List<uint>();
        var entriesPerSector = Header.SectorSize / 4;
        foreach (var fatSector in Difat)
        {
            var sectorData = ReadSector(fatSector);
            for (var i = 0; i < entriesPerSector; i++)
            {
                fatEntries.Add(BinaryPrimitives.ReadUInt32LittleEndian(sectorData.AsSpan(i * 4, 4)));
            }
        }

        return fatEntries;
    }

    private List<uint> IterChain(uint startSector)
    {
        if (startSector is (uint)SectorMarker.ENDOFCHAIN or (uint)SectorMarker.FREESECT)
        {
            return [];
        }

        var chain = new List<uint>();
        var seen = new HashSet<uint>();
        var sector = startSector;
        while (sector != (uint)SectorMarker.ENDOFCHAIN)
        {
            if (!seen.Add(sector))
            {
                throw new CfbException("Cycle detected in sector chain.");
            }

            if (sector >= Fat.Count)
            {
                throw new CfbException($"Sector {sector} is outside FAT range.");
            }

            chain.Add(sector);
            var next = Fat[(int)sector];
            if (next is (uint)SectorMarker.FATSECT or (uint)SectorMarker.DIFSECT)
            {
                throw new CfbException("Invalid chain link to FAT/DIFAT sector marker.");
            }

            sector = next;
        }

        return chain;
    }

    private List<uint> BuildMiniFat()
    {
        if (Header.NumberOfMiniFatSectors == 0)
        {
            return [];
        }

        var miniFat = new List<uint>();
        var chain = IterChain(Header.FirstMiniFatSectorLocation);
        var entriesPerSector = Header.SectorSize / 4;
        foreach (var sector in chain)
        {
            var sectorData = ReadSector(sector);
            for (var i = 0; i < entriesPerSector; i++)
            {
                miniFat.Add(BinaryPrimitives.ReadUInt32LittleEndian(sectorData.AsSpan(i * 4, 4)));
            }
        }

        return miniFat;
    }

    private List<DirectoryEntry> BuildDirectoryEntries()
    {
        var chain = IterChain(Header.FirstDirectorySectorLocation);
        var raw = new List<byte>(chain.Count * Header.SectorSize);
        foreach (var sector in chain)
        {
            raw.AddRange(ReadSector(sector));
        }

        var entries = new List<DirectoryEntry>();
        for (var offset = 0; offset + 128 <= raw.Count; offset += 128)
        {
            var chunk = CollectionsMarshal.AsSpan(raw).Slice(offset, 128);
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(chunk.Slice(GetLayout(DirectoryEntryLayout, "directory_entry_name_length").Offset, 2));
            var objectTypeRaw = chunk[GetLayout(DirectoryEntryLayout, "object_type").Offset];
            var colorFlagRaw = chunk[GetLayout(DirectoryEntryLayout, "color_flag").Offset];
            if (!Enum.IsDefined(typeof(DirectoryObjectType), objectTypeRaw))
            {
                throw new CfbException($"Invalid directory entry object_type value: {objectTypeRaw}");
            }

            if (!Enum.IsDefined(typeof(DirectoryColorFlag), colorFlagRaw))
            {
                throw new CfbException($"Invalid directory entry color_flag value: {colorFlagRaw}");
            }

            var leftSiblingId = BinaryPrimitives.ReadUInt32LittleEndian(chunk.Slice(GetLayout(DirectoryEntryLayout, "left_sibling_id").Offset, 4));
            var rightSiblingId = BinaryPrimitives.ReadUInt32LittleEndian(chunk.Slice(GetLayout(DirectoryEntryLayout, "right_sibling_id").Offset, 4));
            var childId = BinaryPrimitives.ReadUInt32LittleEndian(chunk.Slice(GetLayout(DirectoryEntryLayout, "child_id").Offset, 4));
            var clsidLayout = GetLayout(DirectoryEntryLayout, "clsid");
            var clsid = chunk.Slice(clsidLayout.Offset, clsidLayout.Size).ToArray();
            var stateBits = BinaryPrimitives.ReadUInt32LittleEndian(chunk.Slice(GetLayout(DirectoryEntryLayout, "state_bits").Offset, 4));
            var creationTime = BinaryPrimitives.ReadUInt64LittleEndian(chunk.Slice(GetLayout(DirectoryEntryLayout, "creation_time").Offset, 8));
            var modifiedTime = BinaryPrimitives.ReadUInt64LittleEndian(chunk.Slice(GetLayout(DirectoryEntryLayout, "modified_time").Offset, 8));
            var startingSectorLocation = BinaryPrimitives.ReadUInt32LittleEndian(chunk.Slice(GetLayout(DirectoryEntryLayout, "starting_sector_location").Offset, 4));
            var streamSize = BinaryPrimitives.ReadUInt64LittleEndian(chunk.Slice(GetLayout(DirectoryEntryLayout, "stream_size").Offset, 8));

            if (Header.MajorVersion == 3)
            {
                streamSize &= 0xFFFFFFFF;
            }

            string name;
            if (nameLength >= 2)
            {
                // The name field is 64 bytes including the terminator; a damaged length must not
                // read past it (MS-CFB 2.6.1).
                var byteCount = Math.Min(nameLength - 2, 62) & ~1;
                name = System.Text.Encoding.Unicode.GetString(chunk[..byteCount]);
            }
            else
            {
                name = string.Empty;
            }

            entries.Add(new DirectoryEntry(
                (uint)entries.Count,
                name,
                nameLength,
                (DirectoryObjectType)objectTypeRaw,
                (DirectoryColorFlag)colorFlagRaw,
                leftSiblingId,
                rightSiblingId,
                childId,
                clsid,
                stateBits,
                creationTime,
                modifiedTime,
                startingSectorLocation,
                streamSize));
        }

        return entries;
    }

    private DirectoryEntry GetRootEntry()
    {
        if (DirectoryEntries.Count == 0)
        {
            throw new CfbException("No directory entries found.");
        }

        var root = DirectoryEntries[0];
        if (!root.IsRoot())
        {
            throw new CfbException("Directory entry 0 is not root storage.");
        }

        if (root.StreamId != CfbConstants.RootStreamId)
        {
            throw new CfbException("Invalid root stream id.");
        }

        return root;
    }

    private byte[] ReadStreamBytes(DirectoryEntry entry)
    {
        if (entry.StreamSize == 0)
        {
            return [];
        }

        if (entry.IsRoot() || entry.StreamSize < Header.MiniStreamCutoffSize)
        {
            if (entry.IsRoot())
            {
                var chain = IterChain(entry.StartingSectorLocation);
                var payload = new List<byte>(chain.Count * Header.SectorSize);
                foreach (var sector in chain)
                {
                    payload.AddRange(ReadSector(sector));
                }

                return payload.Take((int)entry.StreamSize).ToArray();
            }

            if (MiniStream.Length == 0)
            {
                return [];
            }

            var output = new List<byte>();
            var seen = new HashSet<uint>();
            var miniSector = entry.StartingSectorLocation;
            while (miniSector != (uint)SectorMarker.ENDOFCHAIN)
            {
                if (!seen.Add(miniSector))
                {
                    throw new CfbException("Cycle detected in mini FAT chain.");
                }

                if (miniSector >= MiniFat.Count)
                {
                    throw new CfbException($"Mini sector {miniSector} is outside mini FAT range.");
                }

                var longOffset = (long)miniSector * Header.MiniSectorSize;
                if (longOffset > int.MaxValue)
                {
                    throw new CfbException("Mini sector points past mini-stream length.");
                }

                var offset = (int)longOffset;
                var end = offset + Header.MiniSectorSize;
                if (end > MiniStream.Length)
                {
                    throw new CfbException("Mini sector points past mini-stream length.");
                }

                output.AddRange(MiniStream[offset..end]);
                miniSector = MiniFat[(int)miniSector];
            }

            return output.Take((int)entry.StreamSize).ToArray();
        }

        var regularChain = IterChain(entry.StartingSectorLocation);
        var regularPayload = new List<byte>(regularChain.Count * Header.SectorSize);
        foreach (var sector in regularChain)
        {
            regularPayload.AddRange(ReadSector(sector));
        }

        return regularPayload.Take((int)entry.StreamSize).ToArray();
    }

    private List<uint> CollectSiblingsInOrder(uint startId)
    {
        var result = new List<uint>();
        var stack = new Stack<uint>();
        var active = new HashSet<uint>();
        var emitted = new HashSet<uint>();
        var current = startId;

        while (current != CfbConstants.NOSTREAM || stack.Count > 0)
        {
            while (current != CfbConstants.NOSTREAM)
            {
                if (current >= DirectoryEntries.Count)
                {
                    throw new CfbException($"Invalid stream id in sibling tree: {current}");
                }

                if (!active.Add(current))
                {
                    throw new CfbException("Cycle detected in directory sibling tree.");
                }

                stack.Push(current);
                current = DirectoryEntries[(int)current].LeftSiblingId;
            }

            var nodeId = stack.Pop();
            active.Remove(nodeId);
            if (!emitted.Add(nodeId))
            {
                throw new CfbException("Duplicate node encountered in directory sibling tree.");
            }

            result.Add(nodeId);
            current = DirectoryEntries[(int)nodeId].RightSiblingId;
        }

        return result;
    }

    private ushort ReadUInt16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(offset, 2));

    private uint ReadUInt32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(offset, 4));

    private byte[] ReadBytes(LayoutField layout) => _data.AsSpan(layout.Offset, layout.Size).ToArray();

    private static LayoutField GetLayout(IReadOnlyDictionary<string, LayoutField> map, string name) => map[name];

    private readonly record struct LayoutField(int Offset, int Size);
}
