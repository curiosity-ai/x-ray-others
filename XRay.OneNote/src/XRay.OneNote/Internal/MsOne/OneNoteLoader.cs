using System.Text.RegularExpressions;
using XRay.OneNote.Internal.OneStore;
using XRay.OneNote.Internal.Text;
using J = XRay.OneNote.Internal.OneStore.JcidNames;
using P = XRay.OneNote.Internal.OneStore.PropertyNames;

namespace XRay.OneNote.Internal.MsOne;

/// <summary>The result of loading a section: its name, its current pages and each page's history.</summary>
internal sealed record LoadedOneNoteDocument(string? DisplayName, List<Page> Pages, List<List<Page>> PageHistories);

/// <summary>A node of the MS-ONE object graph: an object with its structural children and metadata objects.</summary>
internal sealed class LogicalNode
{
    public required string Kind { get; init; }

    public required ExtendedGuid Oid { get; init; }

    public List<LogicalNode> Children { get; init; } = [];

    public List<LogicalNode> Metadata { get; init; } = [];
}

/// <summary>
/// Interprets the MS-ONE object model stored in a revision store and builds the public document tree:
/// picks the section and page object spaces, resolves the current revision of each page, and maps
/// page, outline, rich text, image, table, attachment and note tag objects to nodes.
/// </summary>
internal sealed partial class OneNoteLoader
{
    private const uint RootRoleDefaultContent = 0x00000001;

    // Nodes deeper than this, or more logical nodes than the budget, only occur in damaged files.
    private const int MaxDepth = 256;
    private const int NodeBudget = 2_000_000;

    private static readonly HashSet<string> StructureProperties =
    [
        P.ElementChildNodes, P.ContentChildNodes, P.StructureElementChildNodes,
        P.ChildGraphSpaceElementNodes, P.ListNodes, P.TextRunDataObject,
    ];

    private static readonly HashSet<string> MetadataProperties = [P.MetaDataObjectsAboveGraphSpace];

    private static readonly HashSet<string> PublicContainerKinds =
    [
        J.SectionNode, J.PageSeriesNode, J.OutlineGroup, J.PageManifestNode, J.VersionHistoryContent, J.VersionProxy,
    ];

    private static readonly HashSet<string> ContentKinds =
    [
        J.PageNode, J.OutlineNode, J.RichTextOENode, J.ImageNode, J.TableNode, J.EmbeddedFileNode,
    ];

    private static readonly HashSet<string> PageKinds = [J.PageManifestNode, J.PageNode];

    private static readonly HashSet<string> TagReferenceProperties = [P.NoteTagDefinitionOid, P.MetaDataObjectsAboveGraphSpace, P.NoteTagStates];

    private readonly OneStoreFile _parsed;
    private readonly bool _includePageHistory;
    private readonly Dictionary<ObjectSpaceState, Dictionary<int, OrderedDictionary<ExtendedGuid, OneStoreObject>>> _snapshotCache = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ObjectSpaceState, Dictionary<ExtendedGuid, int>> _revisionIndexCache = new(ReferenceEqualityComparer.Instance);
    private readonly ObjectSpaceState? _sectionSpace;
    private readonly List<ObjectSpaceState> _contentSpaces;
    private readonly ObjectSpaceState? _contentSpace;

    private ObjectSpaceState? _space;
    private int? _currentRevisionIndex;
    private OrderedDictionary<ExtendedGuid, OneStoreObject> _objects;
    private int _nodeCount;

    private OneNoteLoader(OneStoreFile parsed, bool includePageHistory)
    {
        _parsed = parsed;
        _includePageHistory = includePageHistory;
        _sectionSpace = SelectSectionSpace(parsed);
        _contentSpaces = SelectContentSpaces(parsed);
        _contentSpace = _contentSpaces.Count > 0 ? _contentSpaces[0] : SelectContentSpace(parsed);
        _space = _contentSpace ?? _sectionSpace;
        _objects = _space?.Objects ?? new OrderedDictionary<ExtendedGuid, OneStoreObject>();
        _currentRevisionIndex = SelectCurrentRevisionIndex(_space);
        _objects = GetSnapshotObjects(_space, _currentRevisionIndex);
    }

    public static LoadedOneNoteDocument Load(byte[] data, string? displayName, bool includePageHistory)
    {
        var parsed = OneStoreParser.Parse(data, displayName);
        return new OneNoteLoader(parsed, includePageHistory).Build();
    }

    private LoadedOneNoteDocument Build()
    {
        var entries = BuildCurrentPageEntries();
        var pages = entries.Select(entry => entry.Page).ToList();
        var histories = entries.Select(entry => entry.History).ToList();

        string? displayName;
        if (!string.IsNullOrEmpty(_sectionSpace?.SectionDisplayName))
        {
            displayName = _sectionSpace.SectionDisplayName;
        }
        else if (!string.IsNullOrEmpty(_contentSpace?.SectionDisplayName))
        {
            displayName = _contentSpace.SectionDisplayName;
        }
        else
        {
            displayName = _parsed.DisplayName;
        }

        if (displayName is null && pages.Count > 0)
        {
            displayName = pages[0].Title?.TitleText?.Text;
        }

        return new LoadedOneNoteDocument(displayName, pages, histories);
    }

    // ------------------------------------------------------------------ object space and revision selection

    private T RunInSpace<T>(ObjectSpaceState? space, int? revisionIndex, Func<T> callback)
    {
        var savedObjects = _objects;
        var savedSpace = _space;
        var savedRevisionIndex = _currentRevisionIndex;
        try
        {
            _space = space;
            _currentRevisionIndex = revisionIndex;
            _objects = GetSnapshotObjects(space, revisionIndex);
            return callback();
        }
        finally
        {
            _objects = savedObjects;
            _space = savedSpace;
            _currentRevisionIndex = savedRevisionIndex;
        }
    }

    private int? SelectCurrentRevisionIndex(ObjectSpaceState? space)
    {
        if (space is null || space.Revisions.Count == 0)
        {
            return null;
        }

        var preferred = space.GetLatestRevisionIndex(RootRoleDefaultContent);
        if (preferred is not null && IsPageRevision(space, preferred))
        {
            return preferred;
        }

        for (var index = space.Revisions.Count - 1; index >= 0; index--)
        {
            if (IsPageRevision(space, index) && space.Revisions[index].ContextId is null)
            {
                return index;
            }
        }

        for (var index = space.Revisions.Count - 1; index >= 0; index--)
        {
            if (IsPageRevision(space, index))
            {
                return index;
            }
        }

        return space.Revisions.Count - 1;
    }

    private Dictionary<ExtendedGuid, int> GetRevisionIndexMap(ObjectSpaceState space)
    {
        if (!_revisionIndexCache.TryGetValue(space, out var map))
        {
            map = [];
            for (var index = 0; index < space.Revisions.Count; index++)
            {
                map[space.Revisions[index].Rid] = index;
            }

            _revisionIndexCache[space] = map;
        }

        return map;
    }

    private int? GetParentRevisionIndex(ObjectSpaceState? space, int revisionIndex)
    {
        if (space is null || revisionIndex < 0 || revisionIndex >= space.Revisions.Count)
        {
            return null;
        }

        var dependent = space.Revisions[revisionIndex].RidDependent;
        if (dependent is not { } rid || rid.IsZero)
        {
            return null;
        }

        return GetRevisionIndexMap(space).TryGetValue(rid, out var parent) ? parent : null;
    }

    private bool IsPageRevision(ObjectSpaceState? space, int? revisionIndex)
    {
        if (space is null || revisionIndex is not { } index || index < 0 || index >= space.Revisions.Count)
        {
            return false;
        }

        var root = GetSnapshotRoot(space, index);
        var objects = GetSnapshotObjects(space, index);
        return root is { } oid && objects.TryGetValue(oid, out var record) && PageKinds.Contains(record.JcidName);
    }

    private static ulong? RevisionLastModifiedTimestamp(ObjectSpaceState? space, int revisionIndex)
    {
        if (space is null || revisionIndex < 0 || revisionIndex >= space.Revisions.Count)
        {
            return null;
        }

        foreach (var record in space.Revisions[revisionIndex].Objects.Values)
        {
            if (record.JcidName != J.RevisionMetaData)
            {
                continue;
            }

            var value = record.Properties.Get(P.LastModifiedTimeStamp);
            if (value.AsInteger() is { } timestamp)
            {
                return timestamp;
            }
        }

        return null;
    }

    private OrderedDictionary<ExtendedGuid, OneStoreObject> GetSnapshotObjects(ObjectSpaceState? space, int? revisionIndex)
    {
        if (space is null)
        {
            return new OrderedDictionary<ExtendedGuid, OneStoreObject>();
        }

        if (revisionIndex is not { } index || index < 0 || index >= space.Revisions.Count)
        {
            return space.Objects;
        }

        if (!_snapshotCache.TryGetValue(space, out var spaceCache))
        {
            spaceCache = [];
            _snapshotCache[space] = spaceCache;
        }

        if (spaceCache.TryGetValue(index, out var cached))
        {
            return cached;
        }

        // Walk up the dependency chain (iteratively: a damaged chain may be long or cyclic), then
        // build snapshots from the oldest uncached ancestor down.
        var chain = new List<int>();
        var seen = new HashSet<int>();
        int? current = index;
        OrderedDictionary<ExtendedGuid, OneStoreObject>? baseSnapshot = null;
        while (current is { } at && seen.Add(at))
        {
            if (spaceCache.TryGetValue(at, out var hit))
            {
                baseSnapshot = hit;
                break;
            }

            chain.Add(at);
            current = GetParentRevisionIndex(space, at);
        }

        for (var position = chain.Count - 1; position >= 0; position--)
        {
            var revision = chain[position];
            var merged = baseSnapshot is null
                ? new OrderedDictionary<ExtendedGuid, OneStoreObject>()
                : new OrderedDictionary<ExtendedGuid, OneStoreObject>(baseSnapshot);
            foreach (var (oid, obj) in space.Revisions[revision].Objects)
            {
                merged[oid] = obj;
            }

            spaceCache[revision] = merged;
            baseSnapshot = merged;
        }

        return spaceCache[index];
    }

    private ExtendedGuid? GetSnapshotRoot(ObjectSpaceState? space, int? revisionIndex)
    {
        if (space is null)
        {
            return null;
        }

        if (revisionIndex is { } index && index >= 0 && index < space.Revisions.Count)
        {
            int? current = index;
            var seen = new HashSet<int>();
            while (current is { } at && seen.Add(at))
            {
                if (space.Revisions[at].RootRoles.TryGetValue(RootRoleDefaultContent, out var root))
                {
                    return root;
                }

                current = GetParentRevisionIndex(space, at);
            }
        }

        return space.GetLatestRoot(RootRoleDefaultContent);
    }

    private ObjectSpaceState? SelectSectionSpace(OneStoreFile parsed)
    {
        foreach (var space in parsed.ObjectSpaces)
        {
            if (space.GetLatestRoot(RootRoleDefaultContent) is not { } root)
            {
                continue;
            }

            if (space.Objects.TryGetValue(root, out var record) && record.JcidName == J.SectionNode)
            {
                return space;
            }
        }

        return null;
    }

    private static int ContentScore(ObjectSpaceState space)
    {
        var best = Count(space.Objects);
        foreach (var revision in space.Revisions)
        {
            best = Math.Max(best, Count(revision.Objects));
        }

        return best;

        static int Count(OrderedDictionary<ExtendedGuid, OneStoreObject> snapshot) =>
            snapshot.Values.Count(obj => ContentKinds.Contains(obj.JcidName));
    }

    private ObjectSpaceState? SelectContentSpace(OneStoreFile parsed)
    {
        ObjectSpaceState? bestSpace = null;
        var bestScore = -1;
        foreach (var space in parsed.ObjectSpaces)
        {
            var score = ContentScore(space);
            if (score > bestScore)
            {
                bestSpace = space;
                bestScore = score;
            }
        }

        return bestSpace ?? SelectSectionSpace(parsed);
    }

    private List<ObjectSpaceState> SelectContentSpaces(OneStoreFile parsed)
    {
        var candidates = new List<(int Index, int Score, ObjectSpaceState Space)>();
        for (var index = 0; index < parsed.ObjectSpaces.Count; index++)
        {
            var space = parsed.ObjectSpaces[index];
            if (ReferenceEquals(space, _sectionSpace))
            {
                continue;
            }

            var revisionIndex = SelectCurrentRevisionIndex(space);
            if (!IsPageRevision(space, revisionIndex))
            {
                continue;
            }

            candidates.Add((index, ContentScore(space), space));
        }

        return candidates.OrderBy(c => -c.Score).ThenBy(c => c.Index).Select(c => c.Space).ToList();
    }

    // ------------------------------------------------------------------ logical graph

    private List<ExtendedGuid> OrderedPropertyRefs(OneStoreObject record, HashSet<string> allowed)
    {
        var refs = new List<ExtendedGuid>();
        foreach (var (name, value) in record.Properties.Raw)
        {
            if (allowed.Contains(name))
            {
                FlattenRefs(value, refs);
            }
        }

        return refs;
    }

    private static void FlattenRefs(PropertyValue value, List<ExtendedGuid> into, int depth = 0)
    {
        if (depth > MaxDepth)
        {
            return;
        }

        switch (value.Kind)
        {
            case PropertyKind.ObjectId:
                into.Add(value.ObjectIdValue);
                break;
            case PropertyKind.List:
                foreach (var item in value.Items)
                {
                    FlattenRefs(item, into, depth + 1);
                }

                break;
            case PropertyKind.PropertySet:
                foreach (var item in value.SetValue!.Values)
                {
                    FlattenRefs(item, into, depth + 1);
                }

                break;
        }
    }

    private static List<ExtendedGuid> FlattenRefs(PropertyValue value)
    {
        var refs = new List<ExtendedGuid>();
        FlattenRefs(value, refs);
        return refs;
    }

    private LogicalNode? BuildLogicalNode(ExtendedGuid oid, HashSet<ExtendedGuid> stack)
    {
        if (!_objects.TryGetValue(oid, out var record))
        {
            return null;
        }

        if (++_nodeCount > NodeBudget)
        {
            throw new FileCorruptedException("The object graph is too large to be a valid section");
        }

        if (stack.Contains(oid) || stack.Count >= MaxDepth)
        {
            return new LogicalNode { Kind = record.JcidName, Oid = oid };
        }

        stack.Add(oid);
        try
        {
            var children = new List<LogicalNode>();
            foreach (var reference in OrderedPropertyRefs(record, StructureProperties))
            {
                if (BuildLogicalNode(reference, stack) is { } child)
                {
                    children.Add(child);
                }
            }

            var metadata = new List<LogicalNode>();
            foreach (var reference in OrderedPropertyRefs(record, MetadataProperties))
            {
                if (BuildLogicalNode(reference, stack) is { } child)
                {
                    metadata.Add(child);
                }
            }

            return new LogicalNode { Kind = record.JcidName, Oid = oid, Children = children, Metadata = metadata };
        }
        finally
        {
            stack.Remove(oid);
        }
    }

    private OneStoreObject? FindMetadataRecord(LogicalNode? node, string kind)
    {
        if (node is null)
        {
            return null;
        }

        foreach (var metadata in node.Metadata)
        {
            if (_objects.TryGetValue(metadata.Oid, out var record) && record.JcidName == kind)
            {
                return record;
            }
        }

        return null;
    }

    private static List<LogicalNode> Descendants(LogicalNode? node)
    {
        var result = new List<LogicalNode>();
        if (node is null)
        {
            return result;
        }

        var stack = new Stack<LogicalNode>();
        stack.Push(node);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            result.Add(current);
            for (var index = current.Children.Count - 1; index >= 0; index--)
            {
                stack.Push(current.Children[index]);
            }
        }

        return result;
    }

    private LogicalNode? BuildLogicalRoot(ObjectSpaceState? space, int? revisionIndex)
    {
        var root = GetSnapshotRoot(space, revisionIndex);
        return root is { } oid ? BuildLogicalNode(oid, []) : null;
    }

    // ------------------------------------------------------------------ pages

    private sealed record BuiltPageEntry(
        ExtendedGuid Oid,
        Page Page,
        List<Page> History,
        string? NotebookGuid,
        long? TopologyTimestamp,
        int? SectionOrder,
        int SpaceOrder,
        int PageOrder);

    private List<(ExtendedGuid Oid, LogicalNode Node, Page Page)> BuildPageInfos(LogicalNode? root)
    {
        var pages = new List<(ExtendedGuid, LogicalNode, Page)>();
        foreach (var node in Descendants(root))
        {
            if (node.Kind == J.PageNode)
            {
                pages.Add((node.Oid, node, BuildPage(node)));
            }
        }

        if (pages.Count > 0)
        {
            return pages;
        }

        var pageOids = _objects.Values.Where(obj => obj.JcidName == J.PageNode).Select(obj => obj.Oid).ToList();
        foreach (var oid in pageOids)
        {
            if (BuildLogicalNode(oid, []) is { } logicalPage)
            {
                pages.Add((oid, logicalPage, BuildPage(logicalPage)));
            }
        }

        return pages;
    }

    private List<(ExtendedGuid Oid, LogicalNode Node, Page Page)> BuildPageInfosForSpace(ObjectSpaceState space, int? revisionIndex) =>
        RunInSpace(space, revisionIndex, () => BuildPageInfos(BuildLogicalRoot(space, revisionIndex)));

    private List<(ExtendedGuid Oid, Page Page)> BuildPagePairsForRevision(ObjectSpaceState space, int revisionIndex) =>
        RunInSpace(space, revisionIndex, () => BuildPageInfos(BuildLogicalRoot(space, revisionIndex)).Select(info => (info.Oid, info.Page)).ToList());

    private List<OneStoreObject> PageMetadataCandidates() =>
        _objects.Values.Where(record => record.JcidName == J.PageMetaData).ToList();

    private OneStoreObject? PageMetadataFromNode(LogicalNode node)
    {
        if (FindMetadataRecord(node, J.PageMetaData) is { } metadata)
        {
            return metadata;
        }

        var candidates = PageMetadataCandidates();
        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        if (_objects.TryGetValue(node.Oid, out var record)
            && record.Properties.Get(P.NotebookManagementEntityGuid).AsNonEmptyBytes() is { } notebookGuid)
        {
            foreach (var candidate in candidates)
            {
                if (candidate.Properties.Get(P.NotebookManagementEntityGuid) is { Kind: PropertyKind.Bytes } value
                    && value.BytesValue.AsSpan().SequenceEqual(notebookGuid))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private string? PageNotebookGuid(LogicalNode node)
    {
        if (PageMetadataFromNode(node) is { } metadata
            && metadata.Properties.Get(P.NotebookManagementEntityGuid).AsNonEmptyBytes() is { } fromMetadata)
        {
            return Convert.ToHexString(fromMetadata);
        }

        if (_objects.TryGetValue(node.Oid, out var record)
            && record.Properties.Get(P.NotebookManagementEntityGuid).AsNonEmptyBytes() is { } fromRecord)
        {
            return Convert.ToHexString(fromRecord);
        }

        return null;
    }

    private long? PageTopologyTimestamp(LogicalNode node)
    {
        var value = PageMetadataFromNode(node)?.Properties.Get(P.TopologyCreationTimeStamp).AsInteger();
        return value is { } v ? (long)Math.Min(v, long.MaxValue) : null;
    }

    private bool ShouldSynthesizePageTitle(LogicalNode node) =>
        FindMetadataRecord(node, J.PageMetaData) is not null || SectionPageOrders().Count > 1;

    private Dictionary<string, int> SectionPageOrders()
    {
        if (_sectionSpace is null)
        {
            return [];
        }

        var sectionRevisionIndex = SelectCurrentRevisionIndex(_sectionSpace);
        return RunInSpace(_sectionSpace, sectionRevisionIndex, () =>
        {
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            var nextOrder = 0;
            foreach (var node in Descendants(BuildLogicalRoot(_sectionSpace, sectionRevisionIndex)))
            {
                if (node.Kind != J.PageSeriesNode)
                {
                    continue;
                }

                foreach (var metadata in node.Metadata)
                {
                    if (!_objects.TryGetValue(metadata.Oid, out var record) || record.JcidName != J.PageMetaData)
                    {
                        continue;
                    }

                    if (record.Properties.Get(P.NotebookManagementEntityGuid).AsNonEmptyBytes() is not { } notebookGuid)
                    {
                        continue;
                    }

                    var key = Convert.ToHexString(notebookGuid);
                    if (result.TryAdd(key, nextOrder))
                    {
                        nextOrder++;
                    }
                }
            }

            return result;
        });
    }

    private List<BuiltPageEntry> BuildCurrentPageEntries()
    {
        var sectionOrders = SectionPageOrders();
        var candidateSpaces = _contentSpaces.Count > 0
            ? _contentSpaces
            : (_contentSpace is not null ? [_contentSpace] : new List<ObjectSpaceState>());
        var entries = new List<BuiltPageEntry>();

        for (var spaceOrder = 0; spaceOrder < candidateSpaces.Count; spaceOrder++)
        {
            var space = candidateSpaces[spaceOrder];
            var revisionIndex = SelectCurrentRevisionIndex(space);
            var pageInfos = BuildPageInfosForSpace(space, revisionIndex);
            if (pageInfos.Count == 0)
            {
                continue;
            }

            var pagePairs = pageInfos.Select(info => (info.Oid, info.Page)).ToList();
            var histories = RunInSpace(space, revisionIndex, () => BuildPageHistories(pagePairs));
            var count = Math.Min(pageInfos.Count, histories.Count);
            for (var pageOrder = 0; pageOrder < count; pageOrder++)
            {
                var (oid, logicalPage, page) = pageInfos[pageOrder];
                // Resolved against the primary content snapshot, not this space's: the reference reader
                // does the same, and page order depends on it.
                var notebookGuid = PageNotebookGuid(logicalPage);
                var topology = PageTopologyTimestamp(logicalPage);
                entries.Add(new BuiltPageEntry(
                    oid,
                    page,
                    histories[pageOrder],
                    notebookGuid,
                    topology,
                    notebookGuid is not null && sectionOrders.TryGetValue(notebookGuid, out var order) ? order : null,
                    spaceOrder,
                    pageOrder));
            }
        }

        return entries
            .OrderBy(e => e.SectionOrder is null)
            .ThenBy(e => e.SectionOrder ?? 0)
            .ThenBy(e => e.TopologyTimestamp is null)
            .ThenBy(e => e.TopologyTimestamp ?? 0)
            .ThenBy(e => e.SpaceOrder)
            .ThenBy(e => e.PageOrder)
            .ToList();
    }

    // ------------------------------------------------------------------ page history

    /// <summary>A page identity: its object id, or the notebook management GUID it carries.</summary>
    private readonly record struct PageKey(ExtendedGuid? Oid, string? NotebookGuid);

    private List<PageKey> PageIdentityKeys(ExtendedGuid oid, OrderedDictionary<ExtendedGuid, OneStoreObject> objects)
    {
        var keys = new List<PageKey> { new(oid, null) };
        if (objects.TryGetValue(oid, out var record)
            && record.Properties.Get(P.NotebookManagementEntityGuid).AsNonEmptyBytes() is { } notebookGuid)
        {
            keys.Add(new PageKey(null, Convert.ToHexString(notebookGuid)));
        }

        return keys;
    }

    private static string PageTextSignature(Page page) =>
        string.Join("\n", page.GetChildNodes<RichText>().Select(rt => PyText.Strip(rt.Text)).Where(text => text.Length > 0));

    private List<int> PublishedPageRevisionIndices()
    {
        if (_space is null)
        {
            return [];
        }

        var candidates = new List<(ulong? Timestamp, int RevisionIndex)>();
        var seen = new HashSet<int>();
        foreach (var (contextId, root) in _space.IterLatestRoots(RootRoleDefaultContent))
        {
            var revisionIndex = _space.GetLatestRevisionIndex(RootRoleDefaultContent, contextId);
            if (revisionIndex is not { } index || seen.Contains(index))
            {
                continue;
            }

            var revisionObjects = GetSnapshotObjects(_space, index);
            if (!revisionObjects.TryGetValue(root, out var record) || !PageKinds.Contains(record.JcidName))
            {
                continue;
            }

            seen.Add(index);
            candidates.Add((RevisionLastModifiedTimestamp(_space, index), index));
        }

        return candidates
            .OrderBy(c => c.Timestamp is null)
            .ThenBy(c => c.Timestamp ?? 0)
            .ThenBy(c => c.RevisionIndex)
            .Select(c => c.RevisionIndex)
            .ToList();
    }

    private List<List<Page>> BuildPageHistories(List<(ExtendedGuid Oid, Page Page)> currentPagePairs)
    {
        if (!_includePageHistory || _space is null || currentPagePairs.Count == 0
            || _space.Revisions.Count == 0 || _currentRevisionIndex is null)
        {
            return currentPagePairs.Select(pair => new List<Page> { pair.Page }).ToList();
        }

        var historiesByPage = new Dictionary<Page, List<(ulong? Timestamp, int RevisionIndex, Page Page)>>(ReferenceEqualityComparer.Instance);
        var currentPagesByKey = new Dictionary<PageKey, Page>();
        foreach (var (oid, page) in currentPagePairs)
        {
            historiesByPage[page] = [];
            foreach (var key in PageIdentityKeys(oid, _objects))
            {
                currentPagesByKey.TryAdd(key, page);
            }
        }

        foreach (var revisionIndex in PublishedPageRevisionIndices())
        {
            var pagePairs = BuildPagePairsForRevision(_space, revisionIndex);
            var timestamp = RevisionLastModifiedTimestamp(_space, revisionIndex);
            var revisionObjects = GetSnapshotObjects(_space, revisionIndex);
            foreach (var (oid, page) in pagePairs)
            {
                Page? owner = null;
                foreach (var key in PageIdentityKeys(oid, revisionObjects))
                {
                    if (currentPagesByKey.TryGetValue(key, out owner))
                    {
                        break;
                    }
                }

                if (owner is not null)
                {
                    historiesByPage[owner].Add((timestamp, revisionIndex, page));
                }
            }
        }

        var result = new List<List<Page>>();
        foreach (var (_, currentPage) in currentPagePairs)
        {
            var revisions = historiesByPage[currentPage];
            if (revisions.Count == 0)
            {
                result.Add([currentPage]);
                continue;
            }

            var ordered = revisions
                .OrderBy(r => r.Timestamp is null)
                .ThenBy(r => r.Timestamp ?? 0)
                .ThenBy(r => r.RevisionIndex)
                .Select(r => r.Page);
            var unique = new List<Page>();
            string? lastSignature = null;
            foreach (var page in ordered)
            {
                var signature = PageTextSignature(page);
                if (lastSignature is null || signature != lastSignature)
                {
                    unique.Add(page);
                    lastSignature = signature;
                }
            }

            if (unique.Count > 0)
            {
                unique[^1] = currentPage;
                result.Add(unique);
            }
            else
            {
                result.Add([currentPage]);
            }
        }

        return result;
    }

    // ------------------------------------------------------------------ node builders

    private Page BuildPage(LogicalNode node)
    {
        var page = new Page();
        _objects.TryGetValue(node.Oid, out var record);
        var metadata = PageMetadataFromNode(node);
        if (record is not null)
        {
            var properties = record.Properties;
            page.Author = DecodeUtf16(properties.Get(P.Author));
            page.CreationTime = Time32ToDateTime(properties.Get(P.CreationTimeStamp));
            page.LastModifiedTime = FileTimeToDateTime(properties.Get(P.LastModifiedTimeStamp))
                ?? Time32ToDateTime(properties.Get(P.LastModifiedTime));
            page.Level = properties.Get(P.PageLevel).AsInt32();
        }

        if (metadata is not null)
        {
            var properties = metadata.Properties;
            page.Author ??= DecodeUtf16(properties.Get(P.Author));
            page.CreationTime ??= Time32ToDateTime(properties.Get(P.CreationTimeStamp));
            page.LastModifiedTime ??= FileTimeToDateTime(properties.Get(P.LastModifiedTimeStamp))
                ?? Time32ToDateTime(properties.Get(P.LastModifiedTime));
            page.Level ??= properties.Get(P.PageLevel).AsInt32();
        }

        var titleNode = node.Children.FirstOrDefault(child => child.Kind == J.TitleNode);
        if (titleNode is not null)
        {
            page.Title = BuildTitle(titleNode);
            page.AppendChildLast(page.Title);
        }

        foreach (var child in node.Children)
        {
            if (child.Kind == J.TitleNode)
            {
                continue;
            }

            if (BuildPublicNode(child) is { } publicChild)
            {
                page.AppendChildLast(publicChild);
            }
        }

        if (page.Title is null && ShouldSynthesizePageTitle(node))
        {
            string? titleText = null;
            if (metadata is not null)
            {
                titleText = DecodeUtf16(metadata.Properties.Get(P.CachedTitleString))
                    ?? DecodeUtf16(metadata.Properties.Get(P.CachedTitleStringFromPage));
            }

            if (!string.IsNullOrEmpty(titleText))
            {
                var title = new Title { TitleText = new RichText(titleText, [new TextRun(titleText)]) };
                page.Title = title;
                page.AppendChildFirst(title);
            }
        }

        return page;
    }

    private Title BuildTitle(LogicalNode node)
    {
        var title = new Title();
        var richTexts = Descendants(node).Where(candidate => candidate.Kind == J.RichTextOENode).ToList();
        if (richTexts.Count > 0)
        {
            title.TitleText = BuildRichText(richTexts[0]);
        }

        if (richTexts.Count > 1)
        {
            title.TitleDate = BuildRichText(richTexts[1]);
        }

        if (richTexts.Count > 2)
        {
            title.TitleTime = BuildRichText(richTexts[2]);
        }

        return title;
    }

    private Node? BuildPublicNode(LogicalNode node)
    {
        if (PublicContainerKinds.Contains(node.Kind))
        {
            return null;
        }

        return node.Kind switch
        {
            J.OutlineNode => BuildOutline(node),
            J.OutlineElementNode => BuildOutlineElement(node),
            J.RichTextOENode => BuildRichText(node),
            J.ImageNode => BuildImage(node),
            J.EmbeddedFileNode => BuildAttachedFile(node),
            J.TableNode => BuildTable(node),
            J.TableRowNode => BuildTableRow(node),
            J.TableCellNode => BuildTableCell(node),
            _ => null,
        };
    }

    private void AppendPublicChildren(CompositeNode parent, LogicalNode node)
    {
        foreach (var child in node.Children)
        {
            if (BuildPublicNode(child) is { } publicChild)
            {
                parent.AppendChildLast(publicChild);
            }
            else if (PublicContainerKinds.Contains(child.Kind))
            {
                AppendPublicChildren(parent, child);
            }
        }
    }

    private OneStoreObject Record(LogicalNode node) => _objects[node.Oid];

    private Outline BuildOutline(LogicalNode node)
    {
        var properties = Record(node).Properties;
        var outline = new Outline
        {
            HorizontalOffset = U32ToFloat(properties.Get(P.OffsetFromParentHoriz)),
            VerticalOffset = U32ToFloat(properties.Get(P.OffsetFromParentVert)),
            MaxWidth = U32ToFloat(properties.Get(P.LayoutMaxWidth)),
        };
        AppendPublicChildren(outline, node);
        return outline;
    }

    private OutlineElement BuildOutlineElement(LogicalNode node)
    {
        var record = Record(node);
        var outlineElement = new OutlineElement();
        var outlineTags = CollectTags(record);

        var listNode = node.Children.FirstOrDefault(child => child.Kind == J.NumberListNode);
        if (listNode is not null)
        {
            outlineElement.NumberList = BuildNumberList(listNode);
        }

        var ordered = node.Children.Where(child => child.Kind is not (J.NumberListNode or J.OutlineElementNode)).ToList();
        ordered.AddRange(node.Children.Where(child => child.Kind == J.OutlineElementNode));

        foreach (var child in ordered)
        {
            if (BuildPublicNode(child) is not { } publicChild)
            {
                continue;
            }

            if (publicChild is RichText richText && richText.Tags.Count == 0 && outlineTags.Count > 0)
            {
                richText.Tags.AddRange(outlineTags);
            }

            outlineElement.AppendChildLast(publicChild);
        }

        return outlineElement;
    }

    private PropertySet ResolveStyleProperties(PropertyValue value) => value.Kind switch
    {
        PropertyKind.PropertySet => value.SetValue!,
        PropertyKind.ObjectId when _objects.TryGetValue(value.ObjectIdValue, out var target) => target.Properties,
        _ => PropertySet.Empty,
    };

    private static double? FontSizeFrom(PropertySet properties) =>
        properties.Get(P.FontSize).AsInteger() is { } halfPoints ? halfPoints / 2.0 : null;

    private static ParagraphStyle ParagraphStyleFrom(PropertySet resolved) => new()
    {
        FontName = DecodeUtf16(resolved.Get(P.Font)),
        FontSize = FontSizeFrom(resolved),
        FontColor = resolved.Get(P.FontColor).AsUInt32(),
        Highlight = resolved.Get(P.Highlight).AsUInt32(),
        IsBold = resolved.Get(P.Bold).IsTruthy,
        IsItalic = resolved.Get(P.Italic).IsTruthy,
        IsUnderline = resolved.Get(P.Underline).IsTruthy,
        IsStrikethrough = resolved.Get(P.Strikethrough).IsTruthy,
        IsSuperscript = resolved.Get(P.Superscript).IsTruthy,
        IsSubscript = resolved.Get(P.Subscript).IsTruthy,
    };

    private static TextStyle TextStyleFrom(PropertySet resolved) => new()
    {
        IsHyperlink = resolved.Get(P.Hyperlink).IsTruthy || resolved.Get(P.WzHyperlinkUrl).IsTruthy,
        HyperlinkAddress = DecodeUtf16(resolved.Get(P.WzHyperlinkUrl)),
        FontName = DecodeUtf16(resolved.Get(P.Font)),
        FontSize = FontSizeFrom(resolved),
        FontColor = resolved.Get(P.FontColor).AsUInt32(),
        Highlight = resolved.Get(P.Highlight).AsUInt32(),
        Language = resolved.Get(P.LanguageId).AsInt32(),
        IsBold = resolved.Get(P.Bold).IsTruthy,
        IsItalic = resolved.Get(P.Italic).IsTruthy,
        IsUnderline = resolved.Get(P.Underline).IsTruthy,
        IsStrikethrough = resolved.Get(P.Strikethrough).IsTruthy,
        IsSuperscript = resolved.Get(P.Superscript).IsTruthy,
        IsSubscript = resolved.Get(P.Subscript).IsTruthy,
    };

    /// <summary>
    /// Decodes TextRunIndex: the end offset of every run but the last, stored as 32-bit or 16-bit
    /// integers. Falls back to zeros when no width yields a plausible ascending sequence.
    /// </summary>
    internal static List<long> DecodeRunBoundaries(PropertyValue raw, int runCount, int textLength)
    {
        var zeros = Enumerable.Repeat(0L, Math.Max(runCount, 0)).ToList();
        if (raw.Kind != PropertyKind.Bytes || runCount <= 0)
        {
            return zeros;
        }

        var bytes = raw.BytesValue;
        foreach (var width in (int[])[4, 2])
        {
            if (bytes.Length % width != 0)
            {
                continue;
            }

            var values = new List<long>(bytes.Length / width);
            for (var offset = 0; offset < bytes.Length; offset += width)
            {
                values.Add(width == 4
                    ? System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset))
                    : System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset)));
            }

            if (values.Count != runCount - 1 && values.Count != runCount)
            {
                continue;
            }

            var ascending = true;
            for (var index = 1; index < values.Count; index++)
            {
                ascending &= values[index - 1] <= values[index];
            }

            if (values.Count > 0 && ascending && values.All(v => v >= 0 && v <= textLength))
            {
                return values.Take(runCount).ToList();
            }
        }

        return zeros;
    }

    private RichText BuildRichText(LogicalNode node)
    {
        var record = Record(node);
        var properties = record.Properties;
        var text = DecodeUtf16(properties.Get(P.RichEditTextUnicode)) is { Length: > 0 } unicode
            ? unicode
            : DecodeExtendedAscii(properties.Get(P.TextExtendedAscii)) ?? string.Empty;
        var baseStyle = TextStyleFrom(properties);
        var richText = new RichText(
            text,
            text.Length > 0 ? [new TextRun(text, baseStyle)] : null,
            ParagraphStyleFrom(properties),
            DecodeParagraphAlignment(properties.Get(P.ParagraphAlignment)),
            CollectTags(record));

        var runStyles = properties.Get(P.TextRunFormatting);
        if (runStyles.Kind == PropertyKind.List && runStyles.Items.Count > 0)
        {
            var styleItems = runStyles.Items;
            var boundaries = DecodeRunBoundaries(properties.Get(P.TextRunIndex), styleItems.Count, text.Length);
            if (boundaries.Count == 0 || boundaries[0] != 0)
            {
                boundaries.Insert(0, 0);
            }

            boundaries = boundaries.Take(styleItems.Count).Append(text.Length).Order().ToList();
            var runs = new List<TextRun>(styleItems.Count);
            for (var index = 0; index < styleItems.Count; index++)
            {
                var start = index < boundaries.Count ? boundaries[index] : 0;
                var end = index + 1 < boundaries.Count ? boundaries[index + 1] : text.Length;
                var from = (int)Math.Min(start, text.Length);
                var to = (int)Math.Clamp(end, from, text.Length);
                runs.Add(new TextRun(text[from..to], TextStyleFrom(ResolveStyleProperties(styleItems[index]))));
            }

            richText.ReplaceTextRuns(runs);
        }

        return richText;
    }

    [GeneratedRegex("([0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})")]
    private static partial Regex GuidPattern();

    private byte[] ResolveFileBytes(OneStoreObject record, string propertyName)
    {
        if (record.Properties.Get(propertyName).AsObjectId() is not { } reference
            || !_objects.TryGetValue(reference, out var target))
        {
            return [];
        }

        var fileReference = target.FileDataReference ?? string.Empty;
        if (fileReference.StartsWith("<ifndf>", StringComparison.OrdinalIgnoreCase))
        {
            var match = GuidPattern().Match(fileReference);
            if (match.Success)
            {
                return _parsed.FileDataStore.GetValueOrDefault(match.Groups[1].Value.ToLowerInvariant(), []);
            }
        }

        return [];
    }

    private Image BuildImage(LogicalNode node)
    {
        var record = Record(node);
        var properties = record.Properties;
        var width = U32ToFloat(properties.Get(P.PictureWidth));
        var height = U32ToFloat(properties.Get(P.PictureHeight));
        var bytes = ResolveFileBytes(record, P.PictureContainer);
        if (bytes.Length == 0)
        {
            bytes = ResolveFileBytes(record, P.WebPictureContainer14);
        }

        var image = new Image
        {
            FileName = DecodeUtf16(properties.Get(P.ImageFilename)),
            Bytes = bytes,
            Width = width,
            Height = height,
            OriginalWidth = width,
            OriginalHeight = height,
            Alignment = DecodeLayoutAlignment(properties.Get(P.LayoutAlignmentSelf))
                ?? DecodeLayoutAlignment(properties.Get(P.LayoutAlignmentInParent)),
            AlternativeTextDescription = DecodeUtf16(properties.Get(P.ImageAltText)),
            HyperlinkUrl = DecodeUtf16(properties.Get(P.WzHyperlinkUrl)),
        };
        image.Tags.AddRange(CollectTags(record));
        return image;
    }

    private AttachedFile BuildAttachedFile(LogicalNode node)
    {
        var record = Record(node);
        var file = new AttachedFile
        {
            FileName = DecodeUtf16(record.Properties.Get(P.EmbeddedFileName)) ?? DecodeUtf16(record.Properties.Get(P.SourceFilepath)),
            Bytes = ResolveFileBytes(record, P.EmbeddedFileContainer),
        };
        file.Tags.AddRange(CollectTags(record));
        return file;
    }

    private Table BuildTable(LogicalNode node)
    {
        var record = Record(node);
        var bordersVisible = record.Properties.Contains(P.TableBordersVisible)
            ? record.Properties.Get(P.TableBordersVisible).IsTruthy
            : true;
        var table = new Table { IsBordersVisible = bordersVisible };
        table.Tags.AddRange(CollectTags(record));
        foreach (var width in BytesToFloatList(record.Properties.Get(P.TableColumnWidths)))
        {
            table.Columns.Add(new TableColumn { Width = width });
        }

        foreach (var child in node.Children)
        {
            if (child.Kind == J.TableRowNode)
            {
                table.AppendChildLast(BuildTableRow(child));
            }
        }

        return table;
    }

    private TableRow BuildTableRow(LogicalNode node)
    {
        var row = new TableRow();
        foreach (var child in node.Children)
        {
            if (child.Kind == J.TableCellNode)
            {
                row.AppendChildLast(BuildTableCell(child));
            }
        }

        return row;
    }

    private TableCell BuildTableCell(LogicalNode node)
    {
        var cell = new TableCell();
        AppendPublicChildren(cell, node);
        return cell;
    }

    private NumberList BuildNumberList(LogicalNode node)
    {
        var properties = Record(node).Properties;
        var format = DecodeNumberListFormat(properties.Get(P.NumberListFormat));
        return new NumberList
        {
            Format = format,
            NumberFormat = format,
            Restart = properties.Get(P.ListRestart).AsInt32(),
        };
    }

    // ------------------------------------------------------------------ note tags

    private static NoteTag? TagFromProperties(PropertySet properties)
    {
        var tag = new NoteTag(
            DecodeUtf16(properties.Get(P.NoteTagLabel)),
            properties.Get(P.NoteTagShape).AsInt32(),
            null,
            properties.Get(P.NoteTagHighlightColor).AsUInt32(),
            TimestampToDateTime(properties.Get(P.NoteTagCreated)),
            TimestampToDateTime(properties.Get(P.NoteTagCompleted)),
            properties.Get(P.NoteTagTextColor).AsUInt32());
        return !string.IsNullOrEmpty(tag.Label) || tag.Icon is not null ? tag : null;
    }

    private static void TagFromRecord(OneStoreObject? record, List<NoteTag> into)
    {
        if (record is not null && TagFromProperties(record.Properties) is { } tag)
        {
            into.Add(tag);
        }
    }

    private void TagFromState(PropertyValue state, List<NoteTag> into)
    {
        if (state.Kind != PropertyKind.PropertySet)
        {
            return;
        }

        var properties = state.SetValue!;
        if (properties.Get(P.NoteTagDefinitionOid).AsObjectId() is { } definition)
        {
            TagFromRecord(_objects.GetValueOrDefault(definition), into);
        }

        if (TagFromProperties(properties) is { } inline)
        {
            into.Add(inline);
        }
    }

    private List<NoteTag> CollectTags(OneStoreObject record)
    {
        var tags = new List<NoteTag>();
        TagFromRecord(record, tags);
        var states = record.Properties.Get(P.NoteTagStates);
        if (states.Kind == PropertyKind.List)
        {
            foreach (var state in states.Items)
            {
                TagFromState(state, tags);
            }
        }

        foreach (var (name, value) in record.Properties.Raw)
        {
            if (!TagReferenceProperties.Contains(name))
            {
                continue;
            }

            foreach (var reference in FlattenRefs(value))
            {
                if (!_objects.TryGetValue(reference, out var target))
                {
                    continue;
                }

                TagFromRecord(target, tags);
                if (target.Properties.Get(P.NoteTagDefinitionOid).AsObjectId() is { } nested)
                {
                    TagFromRecord(_objects.GetValueOrDefault(nested), tags);
                }
            }
        }

        return UniqueTags(tags);
    }

    private static List<NoteTag> UniqueTags(List<NoteTag> tags)
    {
        var seen = new HashSet<(string?, int?, uint?, uint?, DateTime?, DateTime?)>();
        var result = new List<NoteTag>();
        foreach (var tag in tags)
        {
            if (seen.Add((tag.Label, tag.Icon, tag.FontColor, tag.Highlight, tag.CreationTime, tag.CompletedTime)))
            {
                result.Add(tag);
            }
        }

        return result;
    }

    // ------------------------------------------------------------------ value decoding

    internal static string? DecodeUtf16(PropertyValue value) => value.Kind switch
    {
        PropertyKind.Bytes => TextDecoding.DecodeUtf16(value.BytesValue) is { Length: > 0 } text ? text : null,
        PropertyKind.Integer => value.IntegerValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PropertyKind.Bool => value.BoolValue ? "True" : "False",
        _ => null,
    };

    internal static string? DecodeExtendedAscii(PropertyValue value) => value.Kind switch
    {
        PropertyKind.Bytes => TextDecoding.DecodeExtendedAscii(value.BytesValue) is { Length: > 0 } text ? text : null,
        PropertyKind.Integer => value.IntegerValue.ToString(System.Globalization.CultureInfo.InvariantCulture),
        PropertyKind.Bool => value.BoolValue ? "True" : "False",
        _ => null,
    };

    private static string? DecodeNumberListFormat(PropertyValue value)
    {
        if (DecodeUtf16(value) is { Length: > 0 } text)
        {
            return text;
        }

        return value.Kind == PropertyKind.Bytes ? Convert.ToHexString(value.BytesValue).ToLowerInvariant() : null;
    }

    /// <summary>Reinterprets a 32-bit property value as an IEEE single (MS-ONE stores float sizes this way).</summary>
    internal static double? U32ToFloat(PropertyValue value) =>
        value.AsUInt32() is { } bits ? BitConverter.Int32BitsToSingle(unchecked((int)bits)) : null;

    private static List<double> BytesToFloatList(PropertyValue value)
    {
        var result = new List<double>();
        if (value.Kind != PropertyKind.Bytes)
        {
            return result;
        }

        var bytes = value.BytesValue;
        for (var offset = 0; offset + 4 <= bytes.Length; offset += 4)
        {
            result.Add(System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(offset)));
        }

        return result;
    }

    private static HorizontalAlignment? DecodeParagraphAlignment(PropertyValue value) => value.AsInteger() switch
    {
        0 => HorizontalAlignment.Left,
        1 => HorizontalAlignment.Center,
        2 => HorizontalAlignment.Right,
        _ => null,
    };

    private static HorizontalAlignment? DecodeLayoutAlignment(PropertyValue value)
    {
        if (value.AsInteger() is not { } raw || raw == 0)
        {
            return null;
        }

        return (raw & 0x7) switch
        {
            1 => HorizontalAlignment.Left,
            2 => HorizontalAlignment.Center,
            3 => HorizontalAlignment.Right,
            4 => HorizontalAlignment.Left,
            5 => HorizontalAlignment.Right,
            _ => null,
        };
    }

    private static readonly DateTime Time32Epoch = new(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A Time32 (seconds since 1980-01-01 UTC).</summary>
    internal static DateTime? Time32ToDateTime(PropertyValue value) =>
        value.AsInteger() is { } seconds ? Time32ToDateTime(seconds) : null;

    internal static DateTime? Time32ToDateTime(ulong seconds) =>
        seconds <= (ulong)(DateTime.MaxValue - Time32Epoch).TotalSeconds ? Time32Epoch.AddSeconds(seconds) : null;

    /// <summary>A FILETIME (100 ns intervals since 1601-01-01 UTC).</summary>
    internal static DateTime? FileTimeToDateTime(PropertyValue value) =>
        value.AsInteger() is { } ticks ? FileTimeToDateTime(ticks) : null;

    internal static DateTime? FileTimeToDateTime(ulong fileTime) =>
        fileTime <= (ulong)(DateTime.MaxValue.Ticks - DateTime.FromFileTimeUtc(0).Ticks)
            ? DateTime.FromFileTimeUtc((long)fileTime)
            : null;

    /// <summary>A note tag timestamp: a FILETIME when it exceeds 32 bits, otherwise a Time32.</summary>
    internal static DateTime? TimestampToDateTime(PropertyValue value)
    {
        if (value.AsInteger() is not { } raw)
        {
            return null;
        }

        return raw > 0xFFFFFFFF ? FileTimeToDateTime(raw) : Time32ToDateTime(raw);
    }
}
