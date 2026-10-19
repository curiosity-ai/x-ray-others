using XRay.OneNote.Internal.OneStore;

namespace XRay.OneNote.Internal.Fsshttpb;

/// <summary>
/// Reads a revision store that is carried as an FSSHTTPB package (MS-ONESTORE 2.7 and 2.8, MS-FSSHTTPB) and
/// presents it as the same <see cref="OneStoreFile"/> the file-node-list reader produces: one object space per
/// cell, one revision per revision manifest, and objects whose properties are decoded the same way.
/// </summary>
internal static class FsshttpbStore
{
    public static readonly Guid SectionSchema = new("1F937CB4-B26F-445F-B9F8-17E20160E461");
    public static readonly Guid TocSchema = new("E4DBFD38-E5C7-408B-A8A1-0E7B421E1F5F");

    private static readonly Guid DefaultContextGuid = new("84DEFAB9-AAA3-4A0D-A3A8-520C77AC7073");
    private static readonly Guid HeaderCellRootGuid = new("1A5A319C-C26B-41AA-B9C5-9BD8C44E07D4");
    private static readonly Guid RevisionRootGuid = new("4A3717F8-1C14-49E7-9526-81D942DE1741");

    private const int MaxRevisionChain = 100_000;

    // Objects built across the whole package: a real package builds each object a few times (once per revision that
    // declares it); a hostile one could name the same large group from every revision.
    private const long MaxObjectBuilds = 20_000_000;

    /// <summary>Parses the package at <paramref name="offset"/> of <paramref name="data"/>.</summary>
    public static OneStoreFile Parse(byte[] data, int offset, string? displayName, Guid expectedSchema, CancellationToken ct)
    {
        var package = FsshttpbPackage.Parse(data, offset, ct);
        var manifest = package.StorageManifests.FirstOrDefault()
            ?? throw new FileCorruptedException("The package has no storage manifest");
        if (manifest.Schema != expectedSchema)
        {
            throw new UnsupportedFileFormatException("Unsupported file format", manifest.Schema.ToString("D"));
        }

        var index = (package.Elements.GetValueOrDefault(package.StorageIndexId) as StorageIndexElement)
            ?? package.StorageIndexes.FirstOrDefault()
            ?? throw new FileCorruptedException("The package has no storage index");

        var builder = new Builder(package, index, ct);
        var headerCell = manifest.Roots.TryGetValue(new ExtendedGuid(HeaderCellRootGuid, 1), out var headerId) ? headerId : (CellId?)null;
        var (fileGuid, ancestorGuid) = headerCell is { } header ? builder.ReadHeader(header) : (package.FileGuid, Guid.Empty);

        var spaces = new List<ObjectSpaceState>();
        var byId = new Dictionary<ExtendedGuid, ObjectSpaceState>();
        var cells = index.CellMappings.Keys.Where(cell => headerCell is not { } h || cell != h).ToList();
        if (manifest.Roots.TryGetValue(new ExtendedGuid(DefaultContextGuid, 2), out var rootCell) && cells.Remove(rootCell))
        {
            cells.Insert(0, rootCell);
        }

        // Within an object space the default context comes first, so its revisions are the baseline.
        foreach (var cell in cells.OrderBy(c => IsDefaultContext(c) ? 0 : 1))
        {
            ct.ThrowIfCancellationRequested();
            if (!byId.TryGetValue(cell.Space, out var state))
            {
                state = new ObjectSpaceState(cell.Space);
                byId[cell.Space] = state;
            }

            builder.AddCell(state, cell);
        }

        // Keep the root cell's space first, then the others in the storage index's order.
        foreach (var cell in cells)
        {
            if (byId.TryGetValue(cell.Space, out var state) && !spaces.Contains(state))
            {
                spaces.Add(state);
            }
        }

        foreach (var state in spaces)
        {
            Finish(state, ct);
        }

        return new OneStoreFile
        {
            ObjectSpaces = spaces,
            FileDataStore = builder.FileData,
            DisplayName = displayName,
            IsPackaged = true,
            FileGuid = fileGuid,
            AncestorGuid = ancestorGuid,
        };
    }

    private static bool IsDefaultContext(CellId cell) => cell.Context.Guid == DefaultContextGuid || cell.Context.IsZero;

    private static void Finish(ObjectSpaceState state, CancellationToken ct)
    {
        RevisionChains.MaterializeLatestRoots(state, ct);
        var preferred = state.GetLatestRevisionIndex(1);
        if (preferred is null && state.Revisions.Count > 0)
        {
            preferred = state.Revisions.Count - 1;
        }

        if (preferred is { } chosen)
        {
            state.Objects = RevisionChains.BuildSnapshot(state, state.Revisions[chosen], ct);
        }
    }

    private sealed class Builder
    {
        private readonly FsshttpbPackage _package;
        private readonly StorageIndexElement _index;
        private readonly CancellationToken _ct;
        private int _orderIndex;
        private long _objectBuilds;

        public Builder(FsshttpbPackage package, StorageIndexElement index, CancellationToken ct)
        {
            _package = package;
            _index = index;
            _ct = ct;
        }

        public Dictionary<string, byte[]> FileData { get; } = new(StringComparer.Ordinal);

        /// <summary>The identity and ancestor GUIDs the header cell's root object carries (MS-ONESTORE 2.7.2).</summary>
        public (Guid File, Guid Ancestor) ReadHeader(CellId cell)
        {
            var file = _package.FileGuid;
            var ancestor = Guid.Empty;
            var foundFile = false;
            var foundAncestor = false;

            // The current revision is first: an older revision's identity only fills what it lacks.
            foreach (var revision in RevisionChain(cell))
            {
                foreach (var groupId in revision.ObjectGroups)
                {
                    if (_package.Elements.GetValueOrDefault(groupId) is not ObjectGroupElement group)
                    {
                        continue;
                    }

                    // The header's root object has only its property set (partition 1), no metadata object.
                    for (var i = 0; i < Math.Min(group.Declarations.Count, group.Data.Count); i++)
                    {
                        if (group.Declarations[i].PartitionId != Partitions.ObjectData || group.Data[i].Data is not { } blob)
                        {
                            continue;
                        }

                        var properties = PropertySetParser.ParseObjectBlobWithReferences(blob, group.Data[i].ObjectReferences, [], []);
                        if (!foundFile && properties.Get("0x1d94").AsNonEmptyBytes() is { Length: 16 } identity)
                        {
                            file = new Guid(identity);
                            foundFile = true;
                        }

                        if (!foundAncestor && properties.Get("0x1d95").AsNonEmptyBytes() is { Length: 16 } parent)
                        {
                            ancestor = new Guid(parent);
                            foundAncestor = true;
                        }
                    }
                }
            }

            return (file, ancestor);
        }

        /// <summary>The revision manifests of a cell, the current one first, following base revisions.</summary>
        private List<RevisionManifestElement> RevisionChain(CellId cell)
        {
            var chain = new List<RevisionManifestElement>();
            if (!_index.CellMappings.TryGetValue(cell, out var mapping))
            {
                return chain;
            }

            ExtendedGuid? manifestId = null;
            if (_package.Elements.GetValueOrDefault(mapping.Mapping) is CellManifestElement cellManifest
                && _index.RevisionMappings.TryGetValue(cellManifest.CurrentRevision, out var revisionMapping))
            {
                manifestId = revisionMapping.Mapping;
            }

            if (manifestId is null && mapping.Serial.Guid != Guid.Empty)
            {
                foreach (var candidate in _index.RevisionMappings.Values)
                {
                    if (candidate.Serial == mapping.Serial)
                    {
                        manifestId = candidate.Mapping;
                        break;
                    }
                }
            }

            var seen = new HashSet<ExtendedGuid>();
            while (manifestId is { } id && seen.Add(id) && chain.Count < MaxRevisionChain)
            {
                _ct.ThrowIfCancellationRequested();
                if (_package.Elements.GetValueOrDefault(id) is not RevisionManifestElement revision)
                {
                    break;
                }

                chain.Add(revision);
                manifestId = !revision.BaseRevisionId.IsZero && _index.RevisionMappings.TryGetValue(revision.BaseRevisionId, out var baseMapping)
                    ? baseMapping.Mapping
                    : null;
            }

            return chain;
        }

        public void AddCell(ObjectSpaceState state, CellId cell)
        {
            ExtendedGuid? context = IsDefaultContext(cell) ? null : cell.Context;
            var known = new HashSet<ExtendedGuid>(state.Revisions.Select(r => r.Rid));

            // Oldest first, so a revision's dependency is known before it.
            foreach (var revision in RevisionChain(cell).AsEnumerable().Reverse())
            {
                _ct.ThrowIfCancellationRequested();
                if (!known.Add(revision.RevisionId))
                {
                    continue;
                }

                var record = new RevisionManifestRecord
                {
                    Rid = revision.RevisionId,
                    RidDependent = revision.BaseRevisionId.IsZero ? null : revision.BaseRevisionId,
                    ContextId = context,
                    OrderIndex = _orderIndex++,
                };
                foreach (var (root, obj) in revision.RootDeclarations)
                {
                    if (root.Guid == RevisionRootGuid)
                    {
                        record.RootRoles[root.N] = obj;
                    }
                }

                record.Objects = BuildObjects(revision, cell.Space);
                foreach (var obj in record.Objects.Values)
                {
                    if (OneStoreParser.SectionNameOf(obj) is { } name)
                    {
                        record.SectionDisplayName = name;
                    }
                }

                state.Revisions.Add(record);
                // Oldest first, so the newest revision's name is the one that stays.
                if (!string.IsNullOrEmpty(record.SectionDisplayName))
                {
                    state.SectionDisplayName = record.SectionDisplayName;
                }
            }
        }

        private OrderedDictionary<ExtendedGuid, OneStoreObject> BuildObjects(RevisionManifestElement revision, ExtendedGuid spaceId)
        {
            var objects = new OrderedDictionary<ExtendedGuid, OneStoreObject>();
            foreach (var groupId in revision.ObjectGroups)
            {
                _ct.ThrowIfCancellationRequested();
                if (_package.Elements.GetValueOrDefault(groupId) is not ObjectGroupElement group)
                {
                    continue;
                }

                var count = Math.Min(group.Declarations.Count, group.Data.Count);
                if ((_objectBuilds += count) > MaxObjectBuilds)
                {
                    throw new FileCorruptedException("The package declares too many objects");
                }

                var items = new Dictionary<(ExtendedGuid, ulong), ObjectDataItem>();
                var order = new List<ExtendedGuid>();
                var ordered = new HashSet<ExtendedGuid>();
                for (var i = 0; i < count; i++)
                {
                    var declaration = group.Declarations[i];
                    items.TryAdd((declaration.ObjectId, declaration.PartitionId), group.Data[i]);
                    if (ordered.Add(declaration.ObjectId))
                    {
                        order.Add(declaration.ObjectId);
                    }
                }

                foreach (var objectId in order)
                {
                    _ct.ThrowIfCancellationRequested();
                    if (BuildObject(objectId, items, spaceId) is { } built)
                    {
                        objects[objectId] = built;
                    }
                }
            }

            return objects;
        }

        private OneStoreObject? BuildObject(ExtendedGuid objectId, Dictionary<(ExtendedGuid, ulong), ObjectDataItem> items, ExtendedGuid spaceId)
        {
            if (!items.TryGetValue((objectId, Partitions.Metadata), out var metadata) || metadata.Data is not { Length: >= 4 } jcidBytes
                || !items.TryGetValue((objectId, Partitions.ObjectData), out var data) || data.Data is null)
            {
                return null;
            }

            var jcid = Jcid.FromRaw(BitConverter.ToUInt32(jcidBytes, 0));
            var objectRefs = data.ObjectReferences;
            var spaceRefs = new List<ExtendedGuid>();
            var contextRefs = new List<ExtendedGuid>();
            foreach (var cell in data.CellReferences)
            {
                // A reference to a cell of this object space is a context; any other is an object space.
                if (cell.Space == spaceId)
                {
                    contextRefs.Add(cell.Context);
                }
                else
                {
                    spaceRefs.Add(cell.Space);
                }
            }

            var properties = PropertySetParser.ParseObjectBlobWithReferences(data.Data, objectRefs, spaceRefs, contextRefs);
            string? reference = null;
            string? extension = null;
            if (jcid.IsFileData)
            {
                extension = TextDecoding.DecodeUtf16(properties.Get("0x3424").AsNonEmptyBytes() ?? []);
                if (items.TryGetValue((objectId, Partitions.FileData), out var blobItem)
                    && blobItem.IsBlobReference
                    && _package.Elements.GetValueOrDefault(blobItem.BlobId) is BlobElement blob)
                {
                    var key = BlobKey(blobItem.BlobId);
                    FileData[key] = blob.Data;
                    reference = "<ifndf>{" + key + "}";
                }
                else
                {
                    reference = "<invfdo>";
                }
            }

            return new OneStoreObject
            {
                Oid = objectId,
                Jcid = jcid,
                JcidName = JcidNames.For(jcid.Index),
                Properties = properties,
                FileDataReference = reference,
                FileExtension = extension,
            };
        }

        /// <summary>A stable GUID text standing for a BLOB: its extended GUID's GUID with the number folded in.</summary>
        private static string BlobKey(ExtendedGuid blob)
        {
            var bytes = blob.Guid.ToByteArray();
            bytes[0] ^= (byte)blob.N;
            bytes[1] ^= (byte)(blob.N >> 8);
            bytes[2] ^= (byte)(blob.N >> 16);
            bytes[3] ^= (byte)(blob.N >> 24);
            return new Guid(bytes).ToString("D");
        }
    }
}
