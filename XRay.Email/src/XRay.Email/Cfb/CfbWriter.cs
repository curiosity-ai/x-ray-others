using System.Buffers.Binary;

namespace XRay.Email.Cfb;

public static class CfbWriter
{
    public static byte[] ToBytes(CfbDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new Serializer(document).Serialize();
    }

    public static void WriteFile(CfbDocument document, string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        File.WriteAllBytes(path, ToBytes(document));
    }

    private sealed class Serializer
    {
        private readonly CfbDocument _document;
        private readonly ushort _majorVersion;
        private readonly ushort _minorVersion;
        private readonly int _sectorSize;
        private readonly int _miniSectorSize;
        private readonly int _entriesPerFatSector;
        private readonly int _entriesPerDifatSector;
        private readonly int _directoryEntriesPerSector;

        public Serializer(CfbDocument document)
        {
            _document = document;
            _majorVersion = document.MajorVersion;
            if (_majorVersion is not (3 or 4))
            {
                throw new CfbException($"Unsupported CFB writer major version: {_majorVersion}");
            }

            _minorVersion = document.MinorVersion;
            _sectorSize = _majorVersion == 3 ? 512 : 4096;
            _miniSectorSize = 64;
            _entriesPerFatSector = _sectorSize / 4;
            _entriesPerDifatSector = _entriesPerFatSector - 1;
            _directoryEntriesPerSector = _sectorSize / 128;
        }

        public byte[] Serialize()
        {
            if (_document.Root.Name != CfbConstants.RootEntryName)
            {
                throw new CfbException($"Root storage name must be '{CfbConstants.RootEntryName}'.");
            }

            var records = BuildEntryRecords(_document.Root);
            AssignSiblingTrees(records);

            var (miniStreamPayload, miniFatEntries) = PlanMiniStream(records);
            var regularStreamRecords = records.Where(record => record.IsStream && record.StreamSize >= CfbConstants.MiniStreamCutoffSize).ToArray();

            var regularChains = regularStreamRecords.ToDictionary(
                record => record.StreamId,
                record => new Chain(record.StreamData),
                EqualityComparer<uint>.Default);
            var rootChain = new Chain(miniStreamPayload);

            var directoryPayload = PackDirectoryEntries(records);
            var directoryChain = new Chain(directoryPayload);
            var miniFatPayload = PackUInt32Array(miniFatEntries);
            var miniFatChain = new Chain(miniFatPayload);

            var contentSectorCount =
                regularChains.Values.Sum(chain => SectorSpanForPayload(chain.Payload)) +
                SectorSpanForPayload(rootChain.Payload) +
                SectorSpanForPayload(directoryChain.Payload) +
                SectorSpanForPayload(miniFatChain.Payload);

            var (fatSectorCount, difatSectorCount) = SolveFatAndDifatSectorCounts(contentSectorCount);

            var nextSector = 0;
            foreach (var record in regularStreamRecords)
            {
                var chain = regularChains[record.StreamId];
                chain.Sectors.AddRange(AllocateSectors(ref nextSector, SectorSpanForPayload(chain.Payload)));
                record.StartingSectorLocation = chain.FirstSector;
            }

            rootChain.Sectors.AddRange(AllocateSectors(ref nextSector, SectorSpanForPayload(rootChain.Payload)));
            directoryChain.Sectors.AddRange(AllocateSectors(ref nextSector, SectorSpanForPayload(directoryChain.Payload)));
            miniFatChain.Sectors.AddRange(AllocateSectors(ref nextSector, SectorSpanForPayload(miniFatChain.Payload)));

            var fatSectorNumbers = AllocateSectors(ref nextSector, fatSectorCount);
            var difatSectorNumbers = AllocateSectors(ref nextSector, difatSectorCount);
            var totalSectorCount = nextSector;

            var rootRecord = records[0];
            rootRecord.StartingSectorLocation = rootChain.FirstSector;
            rootRecord.StreamSize = (ulong)rootChain.Payload.Length;
            if (rootRecord.StreamSize == 0)
            {
                rootRecord.StartingSectorLocation = (uint)SectorMarker.ENDOFCHAIN;
            }

            if (_majorVersion == 3 && rootRecord.StreamSize > 0x80000000)
            {
                throw new CfbException("Version 3 CFB writer cannot emit mini streams larger than 2 GB.");
            }

            directoryChain.Payload = PackDirectoryEntries(records);

            var fatEntries = Enumerable.Repeat((uint)SectorMarker.FREESECT, fatSectorCount * _entriesPerFatSector).ToArray();
            foreach (var chain in regularChains.Values)
            {
                MarkSectorChain(fatEntries, chain.Sectors);
            }

            MarkSectorChain(fatEntries, rootChain.Sectors);
            MarkSectorChain(fatEntries, directoryChain.Sectors);
            MarkSectorChain(fatEntries, miniFatChain.Sectors);
            foreach (var sector in fatSectorNumbers)
            {
                fatEntries[sector] = (uint)SectorMarker.FATSECT;
            }

            foreach (var sector in difatSectorNumbers)
            {
                fatEntries[sector] = (uint)SectorMarker.DIFSECT;
            }

            var fatPayload = PackUInt32Array(fatEntries);
            var fatSectorMap = SplitPayload(fatPayload, fatSectorNumbers);

            var difatEntries = fatSectorNumbers.ToList();
            var headerDifat = difatEntries.Take(109).ToList();
            while (headerDifat.Count < 109)
            {
                headerDifat.Add((uint)SectorMarker.FREESECT);
            }

            var difatPayloadMap = BuildDifatSectors(difatEntries.Skip(109).ToArray(), difatSectorNumbers);

            var sectorPayloads = new Dictionary<uint, byte[]>();
            MergePayloads(sectorPayloads, SplitPayload(rootChain.Payload, rootChain.Sectors));
            MergePayloads(sectorPayloads, SplitPayload(directoryChain.Payload, directoryChain.Sectors));
            MergePayloads(sectorPayloads, SplitPayload(miniFatChain.Payload, miniFatChain.Sectors));
            foreach (var chain in regularChains.Values)
            {
                MergePayloads(sectorPayloads, SplitPayload(chain.Payload, chain.Sectors));
            }

            MergePayloads(sectorPayloads, fatSectorMap);
            MergePayloads(sectorPayloads, difatPayloadMap);

            var header = BuildHeader(
                numberOfDirectorySectors: _majorVersion == 3 ? 0u : (uint)directoryChain.Sectors.Count,
                numberOfFatSectors: (uint)fatSectorCount,
                firstDirectorySectorLocation: directoryChain.FirstSector,
                firstMiniFatSectorLocation: miniFatChain.Sectors.Count == 0 ? (uint)SectorMarker.ENDOFCHAIN : miniFatChain.FirstSector,
                numberOfMiniFatSectors: (uint)miniFatChain.Sectors.Count,
                firstDifatSectorLocation: difatSectorNumbers.Count == 0 ? (uint)SectorMarker.ENDOFCHAIN : difatSectorNumbers[0],
                numberOfDifatSectors: (uint)difatSectorNumbers.Count,
                difat: headerDifat);

            var output = new byte[_sectorSize + (totalSectorCount * _sectorSize)];
            header.CopyTo(output, 0);
            for (var sectorNumber = 0; sectorNumber < totalSectorCount; sectorNumber++)
            {
                if (sectorPayloads.TryGetValue((uint)sectorNumber, out var payload))
                {
                    Buffer.BlockCopy(payload, 0, output, _sectorSize + (sectorNumber * _sectorSize), payload.Length);
                }
            }

            return output;
        }

        private List<EntryRecord> BuildEntryRecords(CfbStorage root)
        {
            var records = new List<EntryRecord>();
            AddStorage(root, true);
            if (records[0].Name != CfbConstants.RootEntryName)
            {
                throw new CfbException($"Root storage name must be '{CfbConstants.RootEntryName}'.");
            }

            return records;

            EntryRecord AddStorage(CfbStorage storage, bool isRoot)
            {
                ValidateName(storage.Name);
                var record = new EntryRecord(
                    (uint)records.Count,
                    storage.Name,
                    isRoot ? DirectoryObjectType.RootStorageObject : DirectoryObjectType.StorageObject,
                    ValidateClsid(storage.Clsid),
                    storage.StateBits,
                    storage.CreationTime,
                    storage.ModifiedTime);
                records.Add(record);

                foreach (var child in SortedChildren(storage.Children))
                {
                    switch (child)
                    {
                        case CfbStorage childStorage:
                            record.Children.Add(AddStorage(childStorage, false));
                            break;
                        case CfbStream childStream:
                            ValidateName(childStream.Name);
                            var childRecord = new EntryRecord(
                                (uint)records.Count,
                                childStream.Name,
                                DirectoryObjectType.StreamObject,
                                ValidateClsid(childStream.Clsid),
                                childStream.StateBits,
                                childStream.CreationTime,
                                childStream.ModifiedTime)
                            {
                                StreamData = childStream.Data.ToArray(),
                                StreamSize = (ulong)childStream.Data.Length,
                            };
                            records.Add(childRecord);
                            record.Children.Add(childRecord);
                            break;
                        default:
                            throw new CfbException($"Unsupported CFB node type: {child.GetType().FullName}");
                    }
                }

                return record;
            }
        }

        private static void AssignSiblingTrees(IEnumerable<EntryRecord> records)
        {
            foreach (var record in records)
            {
                record.ColorFlag = DirectoryColorFlag.Black;
                record.LeftSiblingId = CfbConstants.NOSTREAM;
                record.RightSiblingId = CfbConstants.NOSTREAM;
                record.ChildId = CfbConstants.NOSTREAM;
            }

            foreach (var record in records.Where(record => record.IsStorage && record.Children.Count > 0))
            {
                record.ChildId = BuildBalancedSiblingTree(record.Children);
            }
        }

        private static uint BuildBalancedSiblingTree(IReadOnlyList<EntryRecord> children)
        {
            return Build(children);

            static uint Build(IReadOnlyList<EntryRecord> items)
            {
                if (items.Count == 0)
                {
                    return CfbConstants.NOSTREAM;
                }

                var mid = items.Count / 2;
                var record = items[mid];
                record.LeftSiblingId = Build(items.Take(mid).ToArray());
                record.RightSiblingId = Build(items.Skip(mid + 1).ToArray());
                record.ColorFlag = DirectoryColorFlag.Black;
                return record.StreamId;
            }
        }

        private (byte[] MiniStreamPayload, List<uint> MiniFatEntries) PlanMiniStream(IEnumerable<EntryRecord> records)
        {
            var miniStream = new List<byte>();
            var miniFatEntries = new List<uint>();
            uint nextMiniSector = 0;

            foreach (var record in records)
            {
                if (!record.IsStream)
                {
                    continue;
                }

                if (record.StreamSize == 0)
                {
                    record.StartingSectorLocation = (uint)SectorMarker.ENDOFCHAIN;
                    continue;
                }

                if (record.StreamSize >= CfbConstants.MiniStreamCutoffSize)
                {
                    continue;
                }

                var miniSectorCount = MiniSectorSpanForSize((int)record.StreamSize);
                record.StartingSectorLocation = nextMiniSector;
                for (var offset = 0; offset < miniSectorCount; offset++)
                {
                    var current = nextMiniSector + (uint)offset;
                    var nextValue = offset + 1 < miniSectorCount ? current + 1 : (uint)SectorMarker.ENDOFCHAIN;
                    miniFatEntries.Add(nextValue);
                }

                nextMiniSector += (uint)miniSectorCount;
                miniStream.AddRange(PadBytes(record.StreamData, miniSectorCount * _miniSectorSize));
            }

            return (miniStream.ToArray(), miniFatEntries);
        }

        private byte[] BuildHeader(
            uint numberOfDirectorySectors,
            uint numberOfFatSectors,
            uint firstDirectorySectorLocation,
            uint firstMiniFatSectorLocation,
            uint numberOfMiniFatSectors,
            uint firstDifatSectorLocation,
            uint numberOfDifatSectors,
            IReadOnlyList<uint> difat)
        {
            var header = new byte[_sectorSize];
            new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }.CopyTo(header, 0);
            WriteUInt16(header, 24, _minorVersion);
            WriteUInt16(header, 26, _majorVersion);
            WriteUInt16(header, 28, (ushort)CfbConstants.ByteOrderLittleEndian);
            WriteUInt16(header, 30, _majorVersion == 3 ? (ushort)9 : (ushort)12);
            WriteUInt16(header, 32, 6);
            WriteUInt32(header, 40, numberOfDirectorySectors);
            WriteUInt32(header, 44, numberOfFatSectors);
            WriteUInt32(header, 48, firstDirectorySectorLocation);
            WriteUInt32(header, 52, _document.TransactionSignatureNumber);
            WriteUInt32(header, 56, CfbConstants.MiniStreamCutoffSize);
            WriteUInt32(header, 60, firstMiniFatSectorLocation);
            WriteUInt32(header, 64, numberOfMiniFatSectors);
            WriteUInt32(header, 68, firstDifatSectorLocation);
            WriteUInt32(header, 72, numberOfDifatSectors);
            for (var i = 0; i < difat.Count; i++)
            {
                WriteUInt32(header, 76 + (i * 4), difat[i]);
            }

            return header;
        }

        private byte[] PackDirectoryEntries(IReadOnlyList<EntryRecord> records)
        {
            var totalSlots = RoundUp(records.Count, _directoryEntriesPerSector);
            var payload = new byte[totalSlots * 128];
            for (var index = 0; index < records.Count; index++)
            {
                PackDirectoryEntry(records[index]).CopyTo(payload, index * 128);
            }

            for (var index = records.Count; index < totalSlots; index++)
            {
                PackUnallocatedDirectoryEntry().CopyTo(payload, index * 128);
            }

            return payload;
        }

        private byte[] PackDirectoryEntry(EntryRecord record)
        {
            var data = new byte[128];
            var nameBytes = System.Text.Encoding.Unicode.GetBytes(record.Name + "\0");
            if (nameBytes.Length > 64)
            {
                throw new CfbException($"Directory entry name is too long: '{record.Name}'.");
            }

            Buffer.BlockCopy(nameBytes, 0, data, 0, nameBytes.Length);
            WriteUInt16(data, 64, (ushort)nameBytes.Length);
            data[66] = (byte)record.ObjectType;
            data[67] = (byte)record.ColorFlag;
            WriteUInt32(data, 68, record.LeftSiblingId);
            WriteUInt32(data, 72, record.RightSiblingId);
            WriteUInt32(data, 76, record.ChildId);
            Buffer.BlockCopy(record.Clsid, 0, data, 80, 16);
            WriteUInt32(data, 96, record.StateBits);
            WriteUInt64(data, 100, record.CreationTime);
            WriteUInt64(data, 108, record.ModifiedTime);

            if (record.ObjectType == DirectoryObjectType.StorageObject)
            {
                WriteUInt32(data, 116, 0);
                WriteUInt64(data, 120, 0);
                return data;
            }

            var startingSector = record.StartingSectorLocation;
            if (record.StreamSize == 0 && record.IsStream)
            {
                startingSector = (uint)SectorMarker.ENDOFCHAIN;
            }

            WriteUInt32(data, 116, startingSector);
            if (_majorVersion == 3 && record.StreamSize > 0x80000000)
            {
                throw new CfbException("Version 3 CFB writer cannot emit streams larger than 2 GB.");
            }

            WriteUInt64(data, 120, record.StreamSize);
            return data;
        }

        private static byte[] PackUnallocatedDirectoryEntry()
        {
            var data = new byte[128];
            WriteUInt32(data, 68, CfbConstants.NOSTREAM);
            WriteUInt32(data, 72, CfbConstants.NOSTREAM);
            WriteUInt32(data, 76, CfbConstants.NOSTREAM);
            return data;
        }

        private Dictionary<uint, byte[]> BuildDifatSectors(IReadOnlyList<uint> remainingFatEntries, IReadOnlyList<uint> difatSectorNumbers)
        {
            if (difatSectorNumbers.Count == 0)
            {
                return [];
            }

            var result = new Dictionary<uint, byte[]>();
            for (var index = 0; index < difatSectorNumbers.Count; index++)
            {
                var start = index * _entriesPerDifatSector;
                var end = Math.Min(start + _entriesPerDifatSector, remainingFatEntries.Count);
                var payload = new byte[_sectorSize];
                for (var entryIndex = 0; entryIndex < _entriesPerDifatSector; entryIndex++)
                {
                    var value = start + entryIndex < end ? remainingFatEntries[start + entryIndex] : (uint)SectorMarker.FREESECT;
                    WriteUInt32(payload, entryIndex * 4, value);
                }

                var nextSector = index + 1 < difatSectorNumbers.Count ? difatSectorNumbers[index + 1] : (uint)SectorMarker.ENDOFCHAIN;
                WriteUInt32(payload, _sectorSize - 4, nextSector);
                result[difatSectorNumbers[index]] = payload;
            }

            return result;
        }

        private Dictionary<uint, byte[]> SplitPayload(byte[] payload, IReadOnlyList<uint> sectors)
        {
            var result = new Dictionary<uint, byte[]>();
            for (var index = 0; index < sectors.Count; index++)
            {
                var chunk = new byte[_sectorSize];
                var count = Math.Min(_sectorSize, payload.Length - (index * _sectorSize));
                if (count > 0)
                {
                    Buffer.BlockCopy(payload, index * _sectorSize, chunk, 0, count);
                }

                result[sectors[index]] = chunk;
            }

            return result;
        }

        private byte[] PackUInt32Array(IReadOnlyList<uint> values)
        {
            if (values.Count == 0)
            {
                return [];
            }

            var paddedLength = RoundUp(values.Count, _entriesPerFatSector);
            var payload = new byte[paddedLength * 4];
            for (var index = 0; index < paddedLength; index++)
            {
                var value = index < values.Count ? values[index] : (uint)SectorMarker.FREESECT;
                WriteUInt32(payload, index * 4, value);
            }

            return payload;
        }

        private static void MarkSectorChain(uint[] fatEntries, IReadOnlyList<uint> sectors)
        {
            if (sectors.Count == 0)
            {
                return;
            }

            for (var index = 0; index + 1 < sectors.Count; index++)
            {
                fatEntries[sectors[index]] = sectors[index + 1];
            }

            fatEntries[sectors[^1]] = (uint)SectorMarker.ENDOFCHAIN;
        }

        private (int FatSectorCount, int DifatSectorCount) SolveFatAndDifatSectorCounts(int contentSectorCount)
        {
            var fatSectorCount = 0;
            var difatSectorCount = 0;
            while (true)
            {
                var totalSectorCount = contentSectorCount + fatSectorCount + difatSectorCount;
                var newFatSectorCount = CeilDiv(totalSectorCount, _entriesPerFatSector);
                var newDifatSectorCount = newFatSectorCount <= 109
                    ? 0
                    : CeilDiv(newFatSectorCount - 109, _entriesPerDifatSector);
                if (newFatSectorCount == fatSectorCount && newDifatSectorCount == difatSectorCount)
                {
                    return (fatSectorCount, difatSectorCount);
                }

                fatSectorCount = newFatSectorCount;
                difatSectorCount = newDifatSectorCount;
            }
        }

        private static IEnumerable<CfbNode> SortedChildren(IEnumerable<CfbNode> children)
        {
            var ordered = children.OrderBy(child => child, Comparer<CfbNode>.Create((left, right) => DirectoryEntryNameComparer.Compare(left.Name, right.Name))).ToArray();
            for (var index = 0; index + 1 < ordered.Length; index++)
            {
                if (DirectoryEntryNameComparer.Compare(ordered[index].Name, ordered[index + 1].Name) == 0)
                {
                    throw new CfbException($"Duplicate sibling name under CFB comparison rules: '{ordered[index].Name}' vs '{ordered[index + 1].Name}'.");
                }
            }

            return ordered;
        }

        private static void ValidateName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new CfbException("CFB writer does not support empty directory entry names.");
            }

            if (name.Contains('\0'))
            {
                throw new CfbException("CFB directory entry names must not contain embedded null characters.");
            }

            if (DirectoryEntryNameComparer.GetDirectoryEntryNameLength(name) > 64)
            {
                throw new CfbException($"CFB directory entry name exceeds 64-byte field: '{name}'.");
            }
        }

        private static byte[] ValidateClsid(byte[] clsid)
        {
            ArgumentNullException.ThrowIfNull(clsid);
            if (clsid.Length != 16)
            {
                throw new CfbException($"CLSID must be exactly 16 bytes, got {clsid.Length}.");
            }

            return clsid.ToArray();
        }

        private int SectorSpanForPayload(byte[] payload) => CeilDiv(payload.Length, _sectorSize);

        private int MiniSectorSpanForSize(int size) => CeilDiv(size, _miniSectorSize);

        private static int CeilDiv(int value, int divisor) => value <= 0 ? 0 : (value + divisor - 1) / divisor;

        private static int RoundUp(int value, int alignment) => value == 0 ? 0 : ((value + alignment - 1) / alignment) * alignment;

        private static List<uint> AllocateSectors(ref int nextSector, int count)
        {
            var sectors = new List<uint>(count);
            for (var index = 0; index < count; index++)
            {
                sectors.Add((uint)nextSector);
                nextSector++;
            }

            return sectors;
        }

        private static byte[] PadBytes(byte[] value, int targetLength)
        {
            if (value.Length == targetLength)
            {
                return value.ToArray();
            }

            var padded = new byte[targetLength];
            Buffer.BlockCopy(value, 0, padded, 0, value.Length);
            return padded;
        }

        private static void MergePayloads(IDictionary<uint, byte[]> destination, IReadOnlyDictionary<uint, byte[]> source)
        {
            foreach (var pair in source)
            {
                destination[pair.Key] = pair.Value;
            }
        }

        private static void WriteUInt16(Span<byte> destination, int offset, ushort value) =>
            BinaryPrimitives.WriteUInt16LittleEndian(destination.Slice(offset, 2), value);

        private static void WriteUInt32(Span<byte> destination, int offset, uint value) =>
            BinaryPrimitives.WriteUInt32LittleEndian(destination.Slice(offset, 4), value);

        private static void WriteUInt64(Span<byte> destination, int offset, ulong value) =>
            BinaryPrimitives.WriteUInt64LittleEndian(destination.Slice(offset, 8), value);
    }

    private sealed class EntryRecord
    {
        public EntryRecord(uint streamId, string name, DirectoryObjectType objectType, byte[] clsid, uint stateBits, ulong creationTime, ulong modifiedTime)
        {
            StreamId = streamId;
            Name = name;
            ObjectType = objectType;
            Clsid = clsid;
            StateBits = stateBits;
            CreationTime = creationTime;
            ModifiedTime = modifiedTime;
        }

        public uint StreamId { get; }

        public string Name { get; }

        public DirectoryObjectType ObjectType { get; }

        public byte[] Clsid { get; }

        public uint StateBits { get; }

        public ulong CreationTime { get; }

        public ulong ModifiedTime { get; }

        public byte[] StreamData { get; set; } = [];

        public List<EntryRecord> Children { get; } = [];

        public DirectoryColorFlag ColorFlag { get; set; } = DirectoryColorFlag.Black;

        public uint LeftSiblingId { get; set; } = CfbConstants.NOSTREAM;

        public uint RightSiblingId { get; set; } = CfbConstants.NOSTREAM;

        public uint ChildId { get; set; } = CfbConstants.NOSTREAM;

        public uint StartingSectorLocation { get; set; } = CfbConstants.NOSTREAM;

        public ulong StreamSize { get; set; }

        public bool IsRoot => ObjectType == DirectoryObjectType.RootStorageObject;

        public bool IsStorage => ObjectType is DirectoryObjectType.StorageObject or DirectoryObjectType.RootStorageObject;

        public bool IsStream => ObjectType == DirectoryObjectType.StreamObject;
    }

    private sealed class Chain
    {
        public Chain(byte[] payload)
        {
            Payload = payload;
        }

        public byte[] Payload { get; set; }

        public List<uint> Sectors { get; } = [];

        public uint FirstSector => Sectors.Count == 0 ? (uint)SectorMarker.ENDOFCHAIN : Sectors[0];
    }
}
