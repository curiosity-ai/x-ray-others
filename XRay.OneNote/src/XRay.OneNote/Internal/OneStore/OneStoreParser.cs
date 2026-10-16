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

    // FileChunkReference field sizes for the StpFormat and CbFormat values of a FileNode header (2.4.3).
    private static readonly int[] StpSizes = [8, 4, 2, 4];
    private static readonly int[] CbSizes = [4, 8, 1, 2];

    public static OneStoreFile Parse(byte[] data, string? displayName)
    {
        if (data.Length < 1024)
        {
            throw new FileCorruptedException("File is too small to be a OneNote section");
        }

        var span = data.AsSpan();
        var fileType = Bytes.Guid(span, 0);
        if (fileType != SectionFileType)
        {
            throw new UnsupportedFileFormatException("Unsupported file format", fileType.ToString("D"));
        }

        var rootOffset = Bytes.U64(span, 172);
        var rootSize = Bytes.U32(span, 180);
        var rootNodes = ParseFileNodeList(data, rootOffset, rootSize);

        var fileDataStore = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var objectSpaces = new List<ObjectSpaceState>();

        foreach (var node in rootNodes)
        {
            if (node.FileNodeId == 0x090)
            {
                var (listOffset, listSize, _) = DecodeChunkReference(node, 0);
                ProcessFileDataStoreList(data, fileDataStore, listOffset, listSize);
            }
            else if (node.FileNodeId == 0x008)
            {
                var (listOffset, listSize, refSize) = DecodeChunkReference(node, 0);
                var gosid = Bytes.ExtendedGuid(node.Payload.Span, refSize);
                objectSpaces.Add(ParseObjectSpace(data, gosid, listOffset, listSize));
            }
        }

        return new OneStoreFile { ObjectSpaces = objectSpaces, FileDataStore = fileDataStore, DisplayName = displayName };
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
    internal static List<FileNode> ParseFileNodeList(byte[] data, ulong startOffset, ulong size)
    {
        var result = new List<FileNode>();
        var currentOffset = startOffset;
        var currentSize = size;
        var visited = new HashSet<ulong>();
        var memory = new ReadOnlyMemory<byte>(data);

        while (currentSize != 0)
        {
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

            var offset = 16;
            while (offset + 4 <= span.Length - 20)
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
            }

            if (Bytes.U64(span, span.Length - 8) != FileNodeListFooterMagic)
            {
                break;
            }

            currentOffset = Bytes.LenientUInt(span, span.Length - 20, 8);
            currentSize = Bytes.LenientUInt(span, span.Length - 12, 4);
        }

        return result;
    }

    private static ObjectSpaceState ParseObjectSpace(byte[] data, ExtendedGuid gosid, ulong listOffset, ulong listSize)
    {
        var state = new ObjectSpaceState(gosid);
        foreach (var node in ParseFileNodeList(data, listOffset, listSize))
        {
            if (node.FileNodeId != 0x010)
            {
                continue;
            }

            var (revisionOffset, revisionSize, _) = DecodeChunkReference(node, 0);
            var revisionNodes = ParseFileNodeList(data, revisionOffset, revisionSize);
            ProcessRevisionManifestList(data, state, revisionNodes);
        }

        return state;
    }

    private static void ProcessFileDataStoreList(byte[] data, Dictionary<string, byte[]> store, ulong offset, ulong size)
    {
        var memory = new ReadOnlyMemory<byte>(data);
        foreach (var node in ParseFileNodeList(data, offset, size))
        {
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

    private static void ProcessRevisionManifestList(byte[] data, ObjectSpaceState state, List<FileNode> nodes)
    {
        List<FileNode>? currentManifest = null;
        var orderIndex = 0;

        foreach (var node in nodes)
        {
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
                    var revision = ProcessRevisionManifest(data, state.Gosid, currentManifest, orderIndex);
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

        RevisionChains.MaterializeLatestRoots(state);
        var preferred = state.GetLatestRevisionIndex(1);
        if (preferred is null && state.Revisions.Count > 0)
        {
            preferred = state.Revisions.Count - 1;
        }

        if (preferred is { } index)
        {
            state.Objects = RevisionChains.BuildSnapshot(state, state.Revisions[index]);
        }
    }

    private static RevisionManifestRecord? ProcessRevisionManifest(byte[] data, ExtendedGuid gosid, List<FileNode> nodes, int orderIndex)
    {
        var gidTable = new Dictionary<uint, Guid>();
        var objects = new OrderedDictionary<ExtendedGuid, OneStoreObject>();
        string? sectionDisplayName = null;
        RevisionManifestRecord? revision = null;

        foreach (var node in nodes)
        {
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
                    break;
                }

                case 0x024:
                    gidTable[Bytes.U32(payload, 0)] = Bytes.Guid(payload, 4);
                    break;
                case 0x025:
                {
                    var source = Bytes.U32(payload, 0);
                    var target = Bytes.U32(payload, 4);
                    if (gidTable.TryGetValue(source, out var guid))
                    {
                        gidTable[target] = guid;
                    }

                    break;
                }

                case 0x026:
                    CopyGidRange(gidTable, payload);
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
                        ProcessGroup(data, objects, ref sectionDisplayName, groupOffset, groupSize, gidTable);
                    }

                    break;
            }
        }

        if (revision is null)
        {
            return null;
        }

        revision.Objects = objects;
        revision.SectionDisplayName = sectionDisplayName;
        return revision;
    }

    private static void CopyGidRange(Dictionary<uint, Guid> gidTable, ReadOnlySpan<byte> payload)
    {
        var sourceStart = Bytes.U32(payload, 0);
        var count = Bytes.U32(payload, 4);
        var targetStart = Bytes.U32(payload, 8);

        // A well-formed range only copies entries the table already holds, so a count far beyond the
        // table size can only come from a damaged node; bounding it keeps such a node from looping.
        var limit = Math.Min(count, (uint)gidTable.Count + 4096u);
        for (uint index = 0; index < limit; index++)
        {
            if (gidTable.TryGetValue(unchecked(sourceStart + index), out var guid))
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
        Dictionary<uint, Guid> gidTable)
    {
        var localGidTable = new Dictionary<uint, Guid>(gidTable);
        foreach (var node in ParseFileNodeList(data, offset, size))
        {
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
            if (parsed.JcidName == JcidNames.SectionMetaData
                && parsed.Properties.Contains(PropertyNames.SectionDisplayName)
                && parsed.Properties.Get(PropertyNames.SectionDisplayName) is { Kind: PropertyKind.Bytes } nameValue)
            {
                var name = TextDecoding.DecodeUtf16(nameValue.BytesValue);
                if (name.Length > 0)
                {
                    sectionDisplayName = name;
                }
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

    public static OrderedDictionary<ExtendedGuid, OneStoreObject> BuildSnapshot(ObjectSpaceState state, RevisionManifestRecord revision)
    {
        var snapshot = new OrderedDictionary<ExtendedGuid, OneStoreObject>();
        foreach (var item in IterChain(state, revision))
        {
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
    public static void MaterializeLatestRoots(ObjectSpaceState state)
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
