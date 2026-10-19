using System.Text;

namespace XRay.OneNote.Internal.OneStore;

/// <summary>
/// Reads the MS-ONESTORE revision store of a OneNote section file: the header, the root file node
/// list, the object spaces with their revision manifests, and the file data store.
/// </summary>
internal static class OneStoreParser
{
    private const ulong FileNodeListMagic = 0xA4567AB1F5F7F4C4;
    private const ulong FileNodeListFooterMagic = 0x8BC215C38233BA4B;
    private static readonly Guid SectionFileType = new("7b5c52e4-d88c-4da7-aeb1-5378d02996d3");
    private static readonly Guid TocFileType = new("43ff2fa1-efd9-4c76-9ee2-10ea5722765f");

    // FileChunkReference field sizes for the StpFormat and CbFormat values of a FileNode header (2.4.3).
    private static readonly int[] StpSizes = [8, 4, 2, 4];
    private static readonly int[] CbSizes = [4, 8, 1, 2];

    public static OneStoreFile Parse(byte[] data, string? displayName, CancellationToken ct = default) =>
        ParseCore(data, displayName, SectionFileType, ct);

    /// <summary>Parses a table of contents (.onetoc2) file: the same container as a section, with another file type.</summary>
    public static OneStoreFile ParseToc(byte[] data, string? displayName, CancellationToken ct = default) =>
        ParseCore(data, displayName, TocFileType, ct);

    private static OneStoreFile ParseCore(byte[] data, string? displayName, Guid expectedType, CancellationToken ct)
    {
        // A file that is an FSSHTTPB package from its first byte (OneNote online, SharePoint), or a revision
        // store header whose package follows the transaction log (a OneDrive download).
        var schema = expectedType == TocFileType ? Fsshttpb.FsshttpbStore.TocSchema : Fsshttpb.FsshttpbStore.SectionSchema;
        if (Fsshttpb.FsshttpbPackage.IsPackagingAt(data, 0))
        {
            return Fsshttpb.FsshttpbStore.Parse(data, 0, displayName, schema, ct);
        }

        if (data.Length >= 172 && Bytes.Guid(data, 32) == Guid.Empty)
        {
            var embedded = EmbeddedPackagingOffset(data);
            if (embedded is { } offset)
            {
                return Fsshttpb.FsshttpbStore.Parse(data, offset, displayName, schema, ct);
            }
        }

        if (data.Length < 1024)
        {
            throw new FileCorruptedException("File is too small to be a OneNote section");
        }

        var span = data.AsSpan();
        var fileType = Bytes.Guid(span, 0);
        if (fileType != expectedType)
        {
            throw new UnsupportedFileFormatException("Unsupported file format", fileType.ToString("D"));
        }

        var rootOffset = Bytes.U64(span, 172);
        var rootSize = Bytes.U32(span, 180);
        var limits = ReadCommittedCounts(data, ct);
        var rootNodes = ParseFileNodeList(data, rootOffset, rootSize, ct, limits);

        var fileDataStore = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var objectSpaces = new List<ObjectSpaceState>();

        foreach (var node in rootNodes)
        {
            if (node.FileNodeId == 0x090)
            {
                var (listOffset, listSize, _) = DecodeChunkReference(node, 0);
                ProcessFileDataStoreList(data, fileDataStore, listOffset, listSize, ct, limits);
            }
            else if (node.FileNodeId == 0x008)
            {
                var (listOffset, listSize, refSize) = DecodeChunkReference(node, 0);
                var gosid = Bytes.ExtendedGuid(node.Payload.Span, refSize);
                objectSpaces.Add(ParseObjectSpace(data, gosid, listOffset, listSize, ct, limits));
            }
        }

        return new OneStoreFile { ObjectSpaces = objectSpaces, FileDataStore = fileDataStore, DisplayName = displayName, FileGuid = Bytes.Guid(span, 16), AncestorGuid = Bytes.Guid(span, 128) };
    }

    /// <summary>
    /// Where an FSSHTTPB package starts inside a file that has a revision store header with no legacy
    /// version: right after the transaction log. Null when no packaging header is there.
    /// </summary>
    private static int? EmbeddedPackagingOffset(byte[] data)
    {
        var start = Bytes.U64(data, 160);
        var size = Bytes.U32(data, 168);
        var end = start + size;
        return end < (ulong)data.Length && Fsshttpb.FsshttpbPackage.IsPackagingAt(data, (long)end) ? (int)end : null;
    }

    /// <summary>
    /// Reads the transaction log (2.3.3): for every file node list, how many nodes the committed transactions put in
    /// it. Null when the log cannot be read, in which case no list is cut short. A transaction counts only once
    /// Header.cTransactionsInLog reaches its number; entries of later transactions are ignored.
    /// </summary>
    internal static CommittedCounts? ReadCommittedCounts(byte[] data, CancellationToken ct)
    {
        if (data.Length < 172)
        {
            return null;
        }

        var committedTransactions = Bytes.U32(data, 96);
        var offset = Bytes.U64(data, 160);
        var size = Bytes.U32(data, 168);
        if (committedTransactions == 0 || size < 20 || offset >= (ulong)data.Length)
        {
            return null;
        }

        var counts = new Dictionary<uint, long>();
        var pending = new List<(uint List, long Count)>();
        uint transactions = 0;
        var visited = new HashSet<ulong>();
        ulong at = offset;
        ulong length = size;
        while (length >= 20 && visited.Add(at) && transactions < committedTransactions)
        {
            ct.ThrowIfCancellationRequested();
            if (at + length > (ulong)data.Length)
            {
                return null;
            }

            var fragment = data.AsSpan((int)at, (int)length);
            var entries = (fragment.Length - 12) / 8;
            for (var index = 0; index < entries && transactions < committedTransactions; index++)
            {
                var source = Bytes.U32(fragment, index * 8L);
                var value = Bytes.U32(fragment, (index * 8L) + 4);
                if (source == 1)
                {
                    // The sentinel closes a transaction: its entries are committed.
                    foreach (var (list, count) in pending)
                    {
                        counts[list] = count;
                    }

                    pending.Clear();
                    transactions++;
                }
                else
                {
                    pending.Add((source, value));
                }
            }

            at = Bytes.U64(fragment, fragment.Length - 12);
            length = Bytes.U32(fragment, fragment.Length - 4);
        }

        return transactions == committedTransactions ? new CommittedCounts(counts) : null;
    }

    /// <summary>Decodes a FileChunkReference whose field sizes come from the node header.</summary>
    internal static (ulong Stp, ulong Cb, int Size) DecodeChunkReference(FileNode node, int offset)
    {
        var payload = node.Payload.Span;
        var stpSize = StpSizes[node.StpFormat];
        var cbSize = CbSizes[node.CbFormat];
        var stpRaw = Bytes.LenientUInt(payload, offset, stpSize);
        var cbRaw = Bytes.LenientUInt(payload, offset + stpSize, cbSize);

        // Compressed formats store the value divided by 8.
        var stp = node.StpFormat >= 2 ? unchecked(stpRaw * 8) : stpRaw;
        var cb = node.CbFormat >= 2 ? unchecked(cbRaw * 8) : cbRaw;
        return (stp, cb, stpSize + cbSize);
    }

    /// <summary>Walks a file node list (2.4.1) across its fragments.</summary>
    internal static List<FileNode> ParseFileNodeList(byte[] data, ulong startOffset, ulong size, CancellationToken ct = default, CommittedCounts? limits = null)
    {
        var result = new List<FileNode>();
        var currentOffset = startOffset;
        var currentSize = size;
        var visited = new HashSet<ulong>();
        var memory = new ReadOnlyMemory<byte>(data);
        long? remaining = null;
        var listChecked = false;

        while (currentSize != 0)
        {
            ct.ThrowIfCancellationRequested();

            // A fragment chain that loops back on itself would never end.
            if (!visited.Add(currentOffset))
            {
                break;
            }

            var fragment = Bytes.Slice(memory, currentOffset, currentSize);
            if (fragment.Length < 24)
            {
                break;
            }

            var span = fragment.Span;
            if (Bytes.U64(span, 0) != FileNodeListMagic)
            {
                throw new FileCorruptedException("Invalid FileNodeListFragment magic");
            }

            if (!listChecked)
            {
                // The transaction log says how many nodes of this list were committed (2.3.3.2); the rest are not part of the file.
                listChecked = true;
                remaining = limits?.For(Bytes.U32(span, 8));
            }

            var offset = 16;
            while (offset + 4 <= span.Length - 20 && remaining is not 0)
            {
                var header = Bytes.U32(span, offset);
                var fileNodeId = header & 0x3FF;
                var nodeSize = (int)((header >> 10) & 0x1FFF);
                var stpFormat = (int)((header >> 23) & 0x3);
                var cbFormat = (int)((header >> 25) & 0x3);
                var baseType = (int)((header >> 27) & 0xF);
                if (nodeSize < 4 || offset + nodeSize > span.Length - 8)
                {
                    break;
                }

                result.Add(new FileNode(fileNodeId, fragment.Slice(offset + 4, nodeSize - 4), stpFormat, cbFormat, baseType));
                offset += nodeSize;
                if (fileNodeId == 0x0FF)
                {
                    break;
                }

                // A chunk terminator is not one of the list's nodes; every other node counts against the committed number.
                if (remaining is { } left)
                {
                    remaining = left - 1;
                }
            }

            if (Bytes.U64(span, span.Length - 8) != FileNodeListFooterMagic || remaining is 0)
            {
                break;
            }

            currentOffset = Bytes.LenientUInt(span, span.Length - 20, 8);
            currentSize = Bytes.LenientUInt(span, span.Length - 12, 4);
        }

        return result;
    }

    /// <summary>The section name a section metadata object carries (its SectionDisplayName property), when it has one.</summary>
    internal static string? SectionNameOf(OneStoreObject obj)
    {
        if (obj.JcidName == JcidNames.SectionMetaData
            && obj.Properties.Contains(PropertyNames.SectionDisplayName)
            && obj.Properties.Get(PropertyNames.SectionDisplayName) is { Kind: PropertyKind.Bytes } nameValue)
        {
            var name = TextDecoding.DecodeUtf16(nameValue.BytesValue);
            return name.Length > 0 ? name : null;
        }

        return null;
    }

    private static ObjectSpaceState ParseObjectSpace(byte[] data, ExtendedGuid gosid, ulong listOffset, ulong listSize, CancellationToken ct, CommittedCounts? limits)
    {
        var state = new ObjectSpaceState(gosid);
        foreach (var node in ParseFileNodeList(data, listOffset, listSize, ct, limits))
        {
            ct.ThrowIfCancellationRequested();
            if (node.FileNodeId != 0x010)
            {
                continue;
            }

            var (revisionOffset, revisionSize, _) = DecodeChunkReference(node, 0);
            var revisionNodes = ParseFileNodeList(data, revisionOffset, revisionSize, ct, limits);
            ProcessRevisionManifestList(data, state, revisionNodes, ct, limits);
        }

        return state;
    }

    private static void ProcessFileDataStoreList(byte[] data, Dictionary<string, byte[]> store, ulong offset, ulong size, CancellationToken ct, CommittedCounts? limits)
    {
        var memory = new ReadOnlyMemory<byte>(data);
        foreach (var node in ParseFileNodeList(data, offset, size, ct, limits))
        {
            ct.ThrowIfCancellationRequested();
            if (node.FileNodeId != 0x094)
            {
                continue;
            }

            var (blobOffset, blobSize, refSize) = DecodeChunkReference(node, 0);
            var reference = Bytes.Guid(node.Payload.Span, refSize).ToString("D");
            store[reference] = ParseFileDataStoreObject(Bytes.Slice(memory, blobOffset, blobSize).Span);
        }
    }

    /// <summary>Extracts the payload of a FileDataStoreObject (2.6.13): a 16-byte header GUID, the length, the data.</summary>
    private static byte[] ParseFileDataStoreObject(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < 52)
        {
            return [];
        }

        var length = Bytes.U64(blob, 16);
        const int dataOffset = 36;
        var available = (ulong)(blob.Length - dataOffset);
        return blob.Slice(dataOffset, (int)Math.Min(length, available)).ToArray();
    }

    private static void ProcessRevisionManifestList(byte[] data, ObjectSpaceState state, List<FileNode> nodes, CancellationToken ct, CommittedCounts? limits)
    {
        List<FileNode>? currentManifest = null;
        var orderIndex = 0;

        foreach (var node in nodes)
        {
            ct.ThrowIfCancellationRequested();
            var id = node.FileNodeId;
            if (id is 0x01B or 0x01E or 0x01F)
            {
                currentManifest = [node];
                continue;
            }

            if (currentManifest is not null)
            {
                currentManifest.Add(node);
                if (id == 0x01C)
                {
                    var revision = ProcessRevisionManifest(data, state, currentManifest, orderIndex, ct, limits);
                    if (revision is not null)
                    {
                        state.Revisions.Add(revision);
                        if (state.SectionDisplayName is null && !string.IsNullOrEmpty(revision.SectionDisplayName))
                        {
                            state.SectionDisplayName = revision.SectionDisplayName;
                        }
                    }

                    orderIndex++;
                    currentManifest = null;
                }

                continue;
            }

            if (id == 0x05C)
            {
                var payload = node.Payload.Span;
                var rid = Bytes.ExtendedGuid(payload, 0);
                var role = Bytes.U32(payload, 20);
                state.RoleBindings.Add(new RevisionRoleBinding(rid, null, role, orderIndex));
                orderIndex++;
            }
            else if (id == 0x05D)
            {
                var payload = node.Payload.Span;
                var rid = Bytes.ExtendedGuid(payload, 0);
                var role = Bytes.U32(payload, 20);
                var contextId = Bytes.ExtendedGuid(payload, 24);
                state.RoleBindings.Add(new RevisionRoleBinding(rid, contextId, role, orderIndex));
                orderIndex++;
            }
        }

        RevisionChains.MaterializeLatestRoots(state, ct);
        var preferred = state.GetLatestRevisionIndex(1);
        if (preferred is null && state.Revisions.Count > 0)
        {
            preferred = state.Revisions.Count - 1;
        }

        if (preferred is { } index)
        {
            state.Objects = RevisionChains.BuildSnapshot(state, state.Revisions[index], ct);
        }
    }

    private static RevisionManifestRecord? ProcessRevisionManifest(byte[] data, ObjectSpaceState state, List<FileNode> nodes, int orderIndex, CancellationToken ct, CommittedCounts? limits)
    {
        var gidTable = new Dictionary<uint, Guid>();
        var objects = new OrderedDictionary<ExtendedGuid, OneStoreObject>();
        string? sectionDisplayName = null;
        RevisionManifestRecord? revision = null;

        // GlobalIdTableEntry2 and Entry3 define entries from the dependency revision's table (2.5.11, 2.5.12).
        Dictionary<uint, Guid>? dependencyTable = null;

        foreach (var node in nodes)
        {
            ct.ThrowIfCancellationRequested();
            var payload = node.Payload.Span;
            switch (node.FileNodeId)
            {
                case 0x01B or 0x01E or 0x01F:
                {
                    var rid = Bytes.ExtendedGuid(payload, 0);
                    ExtendedGuid? ridDependent = payload.Length >= 40 ? Bytes.ExtendedGuid(payload, 20) : null;
                    ExtendedGuid? contextId = node.FileNodeId == 0x01F && payload.Length >= 60 ? Bytes.ExtendedGuid(payload, 40) : null;
                    revision = new RevisionManifestRecord
                    {
                        Rid = rid,
                        RidDependent = ridDependent,
                        ContextId = contextId,
                        OrderIndex = orderIndex,
                    };
                    dependencyTable = ridDependent is { } dependent && !dependent.IsZero
                        ? state.Revisions.LastOrDefault(item => item.Rid == dependent)?.GidTable
                        : null;
                    break;
                }

                case 0x024:
                    gidTable[Bytes.U32(payload, 0)] = Bytes.Guid(payload, 4);
                    break;
                case 0x025:
                {
                    var source = Bytes.U32(payload, 0);
                    var target = Bytes.U32(payload, 4);
                    if ((dependencyTable ?? gidTable).TryGetValue(source, out var guid) || gidTable.TryGetValue(source, out guid))
                    {
                        gidTable[target] = guid;
                    }

                    break;
                }

                case 0x026:
                    CopyGidRange(gidTable, payload, dependencyTable);
                    break;
                case 0x05A:
                    if (revision is not null)
                    {
                        var root = Bytes.ExtendedGuid(payload, 0);
                        var role = Bytes.U32(payload, 20);
                        revision.RootRoles[role] = root;
                    }

                    break;
                case 0x059:
                    if (revision is not null)
                    {
                        var root = PropertySetParser.ResolveObjectId(Bytes.U32(payload, 0), gidTable);
                        var role = Bytes.U32(payload, 4);
                        if (root is { } resolved)
                        {
                            revision.RootRoles[role] = resolved;
                        }
                    }

                    break;
                case 0x0B0:
                    if (revision is not null)
                    {
                        var (groupOffset, groupSize, _) = DecodeChunkReference(node, 0);
                        ProcessGroup(data, objects, ref sectionDisplayName, groupOffset, groupSize, gidTable, ct, limits);
                    }

                    break;
                case 0x02D or 0x02E or 0x041 or 0x042:
                    // Declarations and revisions of objects directly in the manifest: what table of
                    // contents files (.onetoc2) use instead of object groups (2.5.13, 2.5.14, 2.5.23, 2.5.24).
                    if (revision is not null && ParseRefCountObject(node, gidTable, data, state, objects) is { } declared)
                    {
                        objects[declared.Oid] = declared;
                    }

                    break;
            }
        }

        if (revision is null)
        {
            return null;
        }

        revision.Objects = objects;
        revision.GidTable = gidTable;
        revision.SectionDisplayName = sectionDisplayName;
        return revision;
    }

    /// <summary>
    /// Reads an ObjectDeclarationWithRefCount (0x02D, 0x02E) or ObjectRevisionWithRefCount (0x041, 0x042) node: the
    /// object's property set is at the chunk reference; a declaration names the JCID, a revision keeps the JCID the
    /// object already has in this manifest or an earlier revision.
    /// </summary>
    private static OneStoreObject? ParseRefCountObject(
        FileNode node,
        Dictionary<uint, Guid> gidTable,
        byte[] data,
        ObjectSpaceState state,
        OrderedDictionary<ExtendedGuid, OneStoreObject> declaredHere)
    {
        var payload = node.Payload.Span;
        var (blobOffset, blobSize, refSize) = DecodeChunkReference(node, 0);
        var oid = PropertySetParser.ResolveObjectId(Bytes.U32(payload, refSize), gidTable);
        if (oid is null)
        {
            return null;
        }

        Jcid jcid;
        if (node.FileNodeId is 0x02D or 0x02E)
        {
            // ObjectDeclarationWithRefCountBody (2.6.15): the JCID index is the low 10 bits.
            var packed = Bytes.U32(payload, refSize + 4);
            jcid = new Jcid((ushort)(packed & 0x3FF), false, true, false, false, false);
        }
        else
        {
            if (declaredHere.TryGetValue(oid.Value, out var current))
            {
                jcid = current.Jcid;
            }
            else
            {
                OneStoreObject? earlier = null;
                for (var index = state.Revisions.Count - 1; index >= 0 && earlier is null; index--)
                {
                    state.Revisions[index].Objects.TryGetValue(oid.Value, out earlier);
                }

                if (earlier is null)
                {
                    return null;
                }

                jcid = earlier.Jcid;
            }
        }

        var blob = Bytes.Slice(data, blobOffset, blobSize);
        return new OneStoreObject
        {
            Oid = oid.Value,
            Jcid = jcid,
            JcidName = JcidNames.For(jcid.Index),
            Properties = PropertySetParser.ParseObjectBlob(blob.Span, gidTable),
        };
    }

    private static void CopyGidRange(Dictionary<uint, Guid> gidTable, ReadOnlySpan<byte> payload, Dictionary<uint, Guid>? source = null)
    {
        source ??= gidTable;
        var sourceStart = Bytes.U32(payload, 0);
        var count = Bytes.U32(payload, 4);
        var targetStart = Bytes.U32(payload, 8);

        // A well-formed range only copies entries the table already holds, so a count far beyond the
        // table size can only come from a damaged node; bounding it keeps such a node from looping.
        var limit = Math.Min(count, (uint)source.Count + 4096u);
        for (uint index = 0; index < limit; index++)
        {
            if (source.TryGetValue(unchecked(sourceStart + index), out var guid))
            {
                gidTable[unchecked(targetStart + index)] = guid;
            }
        }
    }

    /// <summary>Processes an object group (2.1.13): its global id table entries and object declarations.</summary>
    private static void ProcessGroup(
        byte[] data,
        OrderedDictionary<ExtendedGuid, OneStoreObject> objects,
        ref string? sectionDisplayName,
        ulong offset,
        ulong size,
        Dictionary<uint, Guid> gidTable,
        CancellationToken ct,
        CommittedCounts? limits)
    {
        var localGidTable = new Dictionary<uint, Guid>(gidTable);
        foreach (var node in ParseFileNodeList(data, offset, size, ct, limits))
        {
            ct.ThrowIfCancellationRequested();
            var payload = node.Payload.Span;
            switch (node.FileNodeId)
            {
                case 0x024:
                    localGidTable[Bytes.U32(payload, 0)] = Bytes.Guid(payload, 4);
                    continue;
                case 0x025:
                {
                    var source = Bytes.U32(payload, 0);
                    var target = Bytes.U32(payload, 4);
                    if (localGidTable.TryGetValue(source, out var guid))
                    {
                        localGidTable[target] = guid;
                    }

                    continue;
                }

                case 0x026:
                    CopyGidRange(localGidTable, payload);
                    continue;
                case 0x0A4 or 0x0A5 or 0x0C4 or 0x0C5 or 0x072 or 0x073:
                    break;
                default:
                    continue;
            }

            var parsed = ParseObjectDeclaration(node, localGidTable, data);
            if (parsed is null)
            {
                continue;
            }

            objects[parsed.Oid] = parsed;
            if (SectionNameOf(parsed) is { } name)
            {
                sectionDisplayName = name;
            }
        }
    }

    private static OneStoreObject? ParseObjectDeclaration(FileNode node, Dictionary<uint, Guid> gidTable, byte[] data)
    {
        var payload = node.Payload.Span;
        if (node.FileNodeId is 0x0A4 or 0x0A5 or 0x0C4 or 0x0C5)
        {
            var (blobOffset, blobSize, refSize) = DecodeChunkReference(node, 0);
            var oid = PropertySetParser.ResolveObjectId(Bytes.U32(payload, refSize), gidTable);
            if (oid is null)
            {
                return null;
            }

            var jcid = Jcid.FromRaw(Bytes.U32(payload, refSize + 4));
            var blob = Bytes.Slice(data, blobOffset, blobSize);
            var properties = PropertySetParser.ParseObjectBlob(blob.Span, gidTable);
            return new OneStoreObject
            {
                Oid = oid.Value,
                Jcid = jcid,
                JcidName = JcidNames.For(jcid.Index),
                Properties = properties,
            };
        }

        if (node.FileNodeId is 0x072 or 0x073)
        {
            long offset = 0;
            var oid = PropertySetParser.ResolveObjectId(Bytes.U32(payload, offset), gidTable);
            if (oid is null)
            {
                return null;
            }

            offset += 4;
            var jcid = Jcid.FromRaw(Bytes.U32(payload, offset));
            offset += 4;
            offset += node.FileNodeId == 0x073 ? 4 : 1;
            var fileDataReference = DecodeStorageString(payload, ref offset);
            var fileExtension = DecodeStorageString(payload, ref offset);
            return new OneStoreObject
            {
                Oid = oid.Value,
                Jcid = jcid,
                JcidName = JcidNames.For(jcid.Index),
                Properties = new PropertySet(),
                FileDataReference = fileDataReference,
                FileExtension = fileExtension,
            };
        }

        return null;
    }

    /// <summary>Decodes a StringInStorageBuffer (2.2.3): a character count, then UTF-16LE text.</summary>
    private static string DecodeStorageString(ReadOnlySpan<byte> data, ref long offset)
    {
        var charCount = (long)Bytes.U32(data, offset);
        offset += 4;
        var byteCount = charCount * 2;
        if (offset + byteCount > data.Length)
        {
            byteCount = charCount;
        }

        var start = Math.Min(offset, data.Length);
        var end = Math.Min(offset + byteCount, data.Length);
        var text = TextDecoding.DecodeUtf16(data[(int)start..(int)end]);
        offset += byteCount;
        return text;
    }
}

/// <summary>The number of nodes the committed transactions put in each file node list, by list id (MS-ONESTORE 2.3.3).</summary>
internal sealed class CommittedCounts
{
    private readonly Dictionary<uint, long> _counts;

    public int ListCount => _counts.Count;

    public long Total => _counts.Values.Sum();

    public CommittedCounts(Dictionary<uint, long> counts)
    {
        _counts = counts;
    }

    /// <summary>The committed node count of the list, or null when the log does not mention it.</summary>
    public long? For(uint listId) => _counts.TryGetValue(listId, out var count) ? count : null;
}

/// <summary>Revision dependency chains: effective roots and object snapshots of a revision (2.1.8).</summary>
internal static class RevisionChains
{
    /// <summary>The revision and the revisions it depends on, oldest first.</summary>
    public static List<RevisionManifestRecord> IterChain(ObjectSpaceState state, RevisionManifestRecord revision)
    {
        var byRid = new Dictionary<ExtendedGuid, RevisionManifestRecord>();
        foreach (var item in state.Revisions)
        {
            byRid[item.Rid] = item;
        }

        var chain = new List<RevisionManifestRecord>();
        var seen = new HashSet<ExtendedGuid>();
        var current = revision;
        while (current is not null && seen.Add(current.Rid))
        {
            chain.Add(current);
            if (current.RidDependent is not { } dependent || dependent.IsZero)
            {
                break;
            }

            current = byRid.GetValueOrDefault(dependent);
        }

        chain.Reverse();
        return chain;
    }

    public static OrderedDictionary<uint, ExtendedGuid> EffectiveRoots(ObjectSpaceState state, RevisionManifestRecord revision)
    {
        var roots = new OrderedDictionary<uint, ExtendedGuid>();
        foreach (var item in IterChain(state, revision))
        {
            foreach (var (role, root) in item.RootRoles)
            {
                roots[role] = root;
            }
        }

        return roots;
    }

    public static ExtendedGuid? ResolveRoot(ObjectSpaceState state, RevisionManifestRecord revision, uint role) =>
        EffectiveRoots(state, revision).TryGetValue(role, out var root) ? root : null;

    public static OrderedDictionary<ExtendedGuid, OneStoreObject> BuildSnapshot(ObjectSpaceState state, RevisionManifestRecord revision, CancellationToken ct = default)
    {
        var snapshot = new OrderedDictionary<ExtendedGuid, OneStoreObject>();
        foreach (var item in IterChain(state, revision))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var (oid, obj) in item.Objects)
            {
                snapshot[oid] = obj;
            }
        }

        return snapshot;
    }

    /// <summary>
    /// Replays revision manifests and role declarations in file order to find the latest root of every
    /// (context, role) pair and the revision that supplied it.
    /// </summary>
    public static void MaterializeLatestRoots(ObjectSpaceState state, CancellationToken ct = default)
    {
        var latest = new OrderedDictionary<ContextRole, ExtendedGuid>();
        var latestIndices = new OrderedDictionary<ContextRole, int>();
        var byRid = new Dictionary<ExtendedGuid, RevisionManifestRecord>();
        var indexByRid = new Dictionary<ExtendedGuid, int>();
        for (var index = 0; index < state.Revisions.Count; index++)
        {
            byRid[state.Revisions[index].Rid] = state.Revisions[index];
            indexByRid[state.Revisions[index].Rid] = index;
        }

        var events = new List<(int Order, int Sequence, RevisionManifestRecord? Revision, RevisionRoleBinding? Binding)>();
        foreach (var revision in state.Revisions)
        {
            events.Add((revision.OrderIndex, events.Count, revision, null));
        }

        foreach (var binding in state.RoleBindings)
        {
            events.Add((binding.OrderIndex, events.Count, null, binding));
        }

        foreach (var item in events.OrderBy(e => e.Order).ThenBy(e => e.Sequence))
        {
            ct.ThrowIfCancellationRequested();
            if (item.Revision is { } revision)
            {
                var revisionIndex = indexByRid[revision.Rid];
                foreach (var (role, root) in EffectiveRoots(state, revision))
                {
                    var key = new ContextRole(revision.ContextId, role);
                    latest[key] = root;
                    latestIndices[key] = revisionIndex;
                }

                continue;
            }

            var binding = item.Binding!;
            if (!byRid.TryGetValue(binding.Rid, out var bound))
            {
                continue;
            }

            if (ResolveRoot(state, bound, binding.Role) is { } boundRoot)
            {
                var key = new ContextRole(binding.ContextId, binding.Role);
                latest[key] = boundRoot;
                latestIndices[key] = indexByRid[bound.Rid];
            }
        }

        state.LatestRootsByContextRole = latest;
        state.LatestRevisionIndicesByContextRole = latestIndices;
        var rootRoles = new OrderedDictionary<uint, ExtendedGuid>();
        foreach (var (key, root) in latest)
        {
            if (key.ContextId is null)
            {
                rootRoles[key.Role] = root;
            }
        }

        state.RootRoles = rootRoles;
    }
}

/// <summary>Text decodings used by the format, matching the reference reader's error handling.</summary>
internal static class TextDecoding
{
    // Windows-1252 code points for bytes 0x80-0x9F; 0 marks the five undefined bytes, which are dropped.
    private static readonly char[] Cp1252High =
    [
        '€', '\0', '‚', 'ƒ', '„', '…', '†', '‡',
        'ˆ', '‰', 'Š', '‹', 'Œ', '\0', 'Ž', '\0',
        '\0', '‘', '’', '“', '”', '•', '–', '—',
        '˜', '™', 'š', '›', 'œ', '\0', 'ž', 'Ÿ',
    ];

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Decodes UTF-16LE, dropping a trailing odd byte and unpaired surrogates, and strips trailing NULs.
    /// </summary>
    public static string DecodeUtf16(ReadOnlySpan<byte> bytes)
    {
        var count = bytes.Length / 2;
        var builder = new StringBuilder(count);
        for (var index = 0; index < count; index++)
        {
            var unit = (char)(bytes[2 * index] | (bytes[(2 * index) + 1] << 8));
            if (char.IsHighSurrogate(unit))
            {
                if (index + 1 < count)
                {
                    var next = (char)(bytes[2 * (index + 1)] | (bytes[(2 * (index + 1)) + 1] << 8));
                    if (char.IsLowSurrogate(next))
                    {
                        builder.Append(unit).Append(next);
                        index++;
                    }
                }

                continue;
            }

            if (char.IsLowSurrogate(unit))
            {
                continue;
            }

            builder.Append(unit);
        }

        var length = builder.Length;
        while (length > 0 && builder[length - 1] == '\0')
        {
            length--;
        }

        return builder.ToString(0, length);
    }

    /// <summary>
    /// Decodes single-byte text (TextExtendedAscii): strict UTF-8 when the bytes are valid UTF-8,
    /// otherwise Windows-1252 with undefined bytes dropped. Trailing NULs are stripped.
    /// </summary>
    public static string DecodeExtendedAscii(ReadOnlySpan<byte> bytes)
    {
        string text;
        if (System.Text.Unicode.Utf8.IsValid(bytes))
        {
            text = StrictUtf8.GetString(bytes);
        }
        else
        {
            var builder = new StringBuilder(bytes.Length);
            foreach (var b in bytes)
            {
                if (b is >= 0x80 and <= 0x9F)
                {
                    var c = Cp1252High[b - 0x80];
                    if (c != '\0')
                    {
                        builder.Append(c);
                    }
                }
                else
                {
                    builder.Append((char)b);
                }
            }

            text = builder.ToString();
        }

        return text.TrimEnd('\0');
    }
}
