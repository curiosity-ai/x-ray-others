using XRay.OneNote.Internal.OneStore;

namespace XRay.OneNote;

/// <summary>
/// One entry of a notebook table of contents: a section, or a folder of the notebook's directory.
/// </summary>
public sealed class NotebookTocEntry
{
    private static readonly IReadOnlyList<NotebookTocEntry> NoChildren = [];

    internal NotebookTocEntry(string? fileName, Guid? guid, uint? orderingId, uint? color)
    {
        FileName = fileName;
        Guid = guid;
        OrderingId = orderingId;
        Color = color;
    }

    /// <summary>The section's file name (<c>FolderChildFilename</c>, for example <c>Notes.one</c>), or the folder's name. The file on disk takes precedence when it exists.</summary>
    public string? FileName { get; }

    /// <summary>The section's identity (<c>FileIdentityGuid</c>): equals <see cref="Document.Guid"/> of the section file. Null when the entry names none.</summary>
    public Guid? Guid { get; }

    /// <summary>The position among the notebook's entries (<c>NotebookElementOrderingID</c>), when stored.</summary>
    public uint? OrderingId { get; }

    /// <summary>The color as a COLORREF (0x00BBGGRR); null when none or undefined (0xFFFFFFFF).</summary>
    public uint? Color { get; }

    /// <summary>
    /// True for an entry that is a directory (a section group) rather than a section file: its name does not
    /// end in <c>.one</c> (an entry without a name and without a section identity counts as one too).
    /// </summary>
    public bool IsFolder => FileName is { } name
        ? !name.EndsWith(".one", StringComparison.OrdinalIgnoreCase)
        : Guid is null;

    /// <summary>
    /// For a folder read through <see cref="NotebookToc.Load(string, CancellationToken)"/>: the entries of the table
    /// of contents file found in that directory, in order. Empty for a section, for a folder whose directory or
    /// table of contents is absent, and for <c>OneNote_RecycleBin</c>, which is never entered.
    /// </summary>
    public IReadOnlyList<NotebookTocEntry> Children { get; private set; } = NoChildren;

    internal void SetChildren(IReadOnlyList<NotebookTocEntry> children) => Children = children;
}

/// <summary>
/// A notebook table of contents (.onetoc2, MS-ONETOC2): the notebook's identity, color, and its ordered
/// sections and section groups. The file uses the MS-ONESTORE container (as a file-node-list file, as a
/// FSSHTTPB package, or as a header with the package embedded after the transaction log); its root object is a
/// TocContainer (JCID 0x00020001) whose <c>0x1CF6</c> property lists child TocContainers, each holding the
/// file name (<c>0x1D6B</c>), ordering id (<c>0x1CB9</c>), color (<c>0x1CBE</c>) and identity (<c>0x1D94</c>) of one
/// section or section group. The ids were established by reading files written by OneNote desktop and
/// OneNote online; MS-ONETOC2 itself was not available.
/// </summary>
public sealed class NotebookToc
{
    private const string ColorProperty = PropertyNames.SectionColor;
    private const string EnableHistoryProperty = "0x1e1e";
    private const string ChildrenProperty = "0x1cf6";
    private const string FileNameProperty = "0x1d6b";
    private const string OrderProperty = "0x1cb9";
    private const string IdentityProperty = "0x1d94";

    // Section groups nest a few levels in real notebooks; deeper nesting is a cycle or a damaged file.
    private const int MaxFolderDepth = 16;
    private const int MaxChildren = 200_000;
    private const string RecycleBinName = "OneNote_RecycleBin";

    private NotebookToc(Guid guid, string? displayName, uint? color, bool? enableHistory, List<NotebookTocEntry> entries)
    {
        Guid = guid;
        DisplayName = displayName;
        Color = color;
        EnableHistory = enableHistory;
        Entries = entries;
    }

    /// <summary>The notebook's identity: the file GUID of the table of contents, which section files name as their <see cref="Document.NotebookGuid"/>.</summary>
    public Guid Guid { get; }

    /// <summary>The notebook name: the file name without extension when loaded from a path, else null.</summary>
    public string? DisplayName { get; }

    /// <summary>The notebook color as a COLORREF, when set.</summary>
    public uint? Color { get; }

    /// <summary>Whether version history pages can be associated with the notebook, when stored.</summary>
    public bool? EnableHistory { get; }

    /// <summary>
    /// The entries in notebook order: by ordering id, then by position in the file. A file name that the file
    /// refers to more than once (OneNote keeps older orderings in the same file) appears once, as its last
    /// reference states it.
    /// </summary>
    public IReadOnlyList<NotebookTocEntry> Entries { get; }

    /// <summary>The section entries (the entries that are not folders), in order.</summary>
    public IEnumerable<NotebookTocEntry> Sections => Entries.Where(entry => !entry.IsFolder);

    /// <summary>
    /// Reads a table of contents from <paramref name="path"/>. The entries that are folders (section groups) get
    /// their <see cref="NotebookTocEntry.Children"/> from the table of contents file in that directory beside
    /// <paramref name="path"/> (<c>.onetoc2</c>, else the first <c>*.onetoc2</c>); a folder without one has none.
    /// </summary>
    /// <exception cref="FileCorruptedException">The file is truncated or damaged.</exception>
    /// <exception cref="UnsupportedFileFormatException">The file is not a table of contents.</exception>
    public static NotebookToc Load(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        cancellationToken.ThrowIfCancellationRequested();
        var toc = Parse(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path), cancellationToken);
        var visited = new HashSet<string>(StringComparer.Ordinal) { Path.GetFullPath(path) };
        LoadFolders(toc.Entries, Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", visited, 1, cancellationToken);
        return toc;
    }

    /// <summary>Reads a table of contents from the remaining contents of <paramref name="stream"/>.</summary>
    /// <exception cref="FileCorruptedException">The data is truncated or damaged.</exception>
    /// <exception cref="UnsupportedFileFormatException">The data is not a table of contents.</exception>
    public static NotebookToc Load(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer, 81920);
        return Parse(buffer.ToArray(), null, cancellationToken);
    }

    private static void LoadFolders(IReadOnlyList<NotebookTocEntry> entries, string directory, HashSet<string> visited, int depth, CancellationToken ct)
    {
        if (depth > MaxFolderDepth)
        {
            return;
        }

        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!entry.IsFolder || entry.FileName is not { Length: > 0 } name
                || string.Equals(name, RecycleBinName, StringComparison.OrdinalIgnoreCase)
                || name.IndexOfAny(['/', '\\']) >= 0 || name is "." or "..")
            {
                continue;
            }

            var folder = Path.Combine(directory, name);
            if (!Directory.Exists(folder))
            {
                continue;
            }

            var file = FindTocFile(folder);
            if (file is null || !visited.Add(Path.GetFullPath(file)))
            {
                continue;
            }

            try
            {
                var nested = Parse(File.ReadAllBytes(file), Path.GetFileNameWithoutExtension(file), ct);
                LoadFolders(nested.Entries, folder, visited, depth + 1, ct);
                entry.SetChildren(nested.Entries);
            }
            catch (OneNoteException)
            {
                // A damaged nested table leaves that folder empty; the notebook's own table was read.
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string? FindTocFile(string folder)
    {
        var plain = Path.Combine(folder, ".onetoc2");
        if (File.Exists(plain))
        {
            return plain;
        }

        return Directory.EnumerateFiles(folder, "*.onetoc2").Order(StringComparer.Ordinal).FirstOrDefault();
    }

    private static NotebookToc Parse(byte[] data, string? displayName, CancellationToken ct)
    {
        OneStoreFile file;
        try
        {
            file = OneStoreParser.ParseToc(data, displayName, ct);
        }
        catch (Exception exception) when (exception is IndexOutOfRangeException or ArgumentException or OverflowException or InvalidOperationException or InvalidCastException)
        {
            throw new FileCorruptedException("The table of contents is damaged");
        }

        // The data root is the first object space whose default-content root is a table of contents object.
        var found = new List<(string Name, NotebookTocEntry Entry)>();
        uint? color = null;
        bool? history = null;
        foreach (var space in file.ObjectSpaces)
        {
            ct.ThrowIfCancellationRequested();
            var objects = space.Objects;
            if (space.GetLatestRoot(1) is not { } rootId || !objects.TryGetValue(rootId, out var root))
            {
                continue;
            }

            color = ColorOf(root.Properties.Get(ColorProperty));
            history = root.Properties.Get(EnableHistoryProperty) is { Kind: PropertyKind.Bool } flag ? flag.BoolValue : null;
            var visited = new HashSet<ExtendedGuid>();
            var budget = 0;
            Collect(root, objects, found, visited, 0, ref budget, ct);
            break;
        }

        // Later references to a file name are authoritative; then order by ordering id, then file position.
        var latest = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < found.Count; index++)
        {
            latest[found[index].Name] = index;
        }

        var ordered = latest.Values
            .Select(index => (Index: index, found[index].Entry))
            .OrderBy(item => item.Entry.OrderingId is null)
            .ThenBy(item => item.Entry.OrderingId ?? 0)
            .ThenBy(item => item.Index)
            .Select(item => item.Entry)
            .ToList();
        return new NotebookToc(file.FileGuid, displayName, color, history, ordered);
    }

    private static void Collect(
        OneStoreObject node,
        IReadOnlyDictionary<ExtendedGuid, OneStoreObject> objects,
        List<(string, NotebookTocEntry)> into,
        HashSet<ExtendedGuid> visited,
        int depth,
        ref int budget,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (depth > MaxFolderDepth || ++budget > MaxChildren || !visited.Add(node.Oid))
        {
            return;
        }

        var name = NameOf(node.Properties.Get(FileNameProperty));
        var children = node.Properties.Get(ChildrenProperty);

        // The entry for the table of contents file itself names it (for example "Notes.onetoc2"); it is not a section.
        if (name is not null && !name.EndsWith(".onetoc2", StringComparison.OrdinalIgnoreCase))
        {
            into.Add((name, EntryOf(node, name)));
        }

        if (children.Kind != PropertyKind.List)
        {
            return;
        }

        foreach (var item in children.Items)
        {
            if (item.AsObjectId() is { } id && objects.TryGetValue(id, out var child))
            {
                Collect(child, objects, into, visited, depth + 1, ref budget, ct);
            }
        }
    }

    /// <summary>The stored name: UTF-16 text in which <c>^M</c> stands for <c>+</c> and <c>^J</c> for <c>,</c>.</summary>
    private static string? NameOf(PropertyValue value)
    {
        if (value.Kind != PropertyKind.Bytes || value.BytesValue.Length == 0)
        {
            return null;
        }

        var text = TextDecoding.DecodeUtf16(value.BytesValue).Replace("^M", "+", StringComparison.Ordinal).Replace("^J", ",", StringComparison.Ordinal);
        return text.Length > 0 ? text : null;
    }

    private static NotebookTocEntry EntryOf(OneStoreObject node, string name)
    {
        var identity = node.Properties.Get(IdentityProperty).AsNonEmptyBytes() is { Length: 16 } bytes ? new Guid(bytes) : (Guid?)null;
        return new NotebookTocEntry(
            name,
            identity == Guid.Empty ? null : identity,
            node.Properties.Get(OrderProperty).AsUInt32(),
            ColorOf(node.Properties.Get(ColorProperty)));
    }

    /// <summary>A color property: R, G, B and an inverted alpha byte; 0xFFFFFFFF means undefined.</summary>
    private static uint? ColorOf(PropertyValue value) =>
        value.AsUInt32() is { } color && color != 0xFFFFFFFF ? color & 0x00FFFFFF : null;
}
