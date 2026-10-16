using XRay.OneNote.Internal.OneStore;

namespace XRay.OneNote;

/// <summary>
/// One entry of a notebook table of contents: a section, or a folder of the notebook's directory.
/// </summary>
public sealed class NotebookTocEntry
{
    internal NotebookTocEntry(string? fileName, Guid? guid, uint? orderingId, uint? color)
    {
        FileName = fileName;
        Guid = guid;
        OrderingId = orderingId;
        Color = color;
    }

    /// <summary>The section's file name (<c>FolderChildFilename</c>), or the folder's name. The file on disk takes precedence when it exists.</summary>
    public string? FileName { get; }

    /// <summary>The section's identity (<c>FileIdentityGuid</c>): equals <see cref="Document.Guid"/> of the section file. Null for a folder.</summary>
    public Guid? Guid { get; }

    /// <summary>The position among the notebook's entries (<c>NotebookElementOrderingID</c>), when stored.</summary>
    public uint? OrderingId { get; }

    /// <summary>The color as a COLORREF (0x00BBGGRR); null when none or undefined (0xFFFFFFFF).</summary>
    public uint? Color { get; }

    /// <summary>True for an entry without a section identity: a subdirectory, which holds a nested notebook (group).</summary>
    public bool IsFolder => Guid is null;
}

/// <summary>
/// A notebook table of contents (.onetoc2, MS-ONETOC2): the notebook's identity, color, and its ordered
/// sections. <b>Best effort:</b> the file uses the MS-ONESTORE container, which is shared with sections and
/// validated, but the property ids of its TOC-specific property sets are not published in the specs this
/// package was built from, so the entries are recognised by the kind of each property (a list of object
/// references, a 16-byte GUID, a UTF-16 string, integers) and by the one known id (the color, 0x1CBE). It has
/// not been checked against a file written by OneNote; a file that does not match yields no entries rather than an error.
/// </summary>
public sealed class NotebookToc
{
    private const string ColorProperty = "0x1cbe";
    private const string EnableHistoryProperty = "0x1e1e";

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

    /// <summary>The entries in notebook order (by ordering id, then file order).</summary>
    public IReadOnlyList<NotebookTocEntry> Entries { get; }

    /// <summary>The section entries, in order.</summary>
    public IEnumerable<NotebookTocEntry> Sections => Entries.Where(entry => !entry.IsFolder);

    /// <summary>Reads a table of contents from <paramref name="path"/>.</summary>
    /// <exception cref="FileCorruptedException">The file is truncated or damaged.</exception>
    /// <exception cref="UnsupportedFileFormatException">The file is not a table of contents.</exception>
    public static NotebookToc Load(string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        cancellationToken.ThrowIfCancellationRequested();
        return Parse(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path), cancellationToken);
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

        var entries = new List<(int Order, NotebookTocEntry Entry)>();
        uint? color = null;
        bool? history = null;
        foreach (var space in file.ObjectSpaces)
        {
            ct.ThrowIfCancellationRequested();
            var objects = space.Objects;
            var root = space.GetLatestRoot(1) is { } id && objects.TryGetValue(id, out var record) ? record : null;
            root ??= objects.Values.FirstOrDefault(o => o.Jcid.Index == 1 && HasObjectList(o));
            if (root is null)
            {
                continue;
            }

            foreach (var (name, value) in root.Properties.Raw)
            {
                if (name == ColorProperty)
                {
                    color = ColorOf(value);
                }
                else if (name == EnableHistoryProperty && value.Kind == PropertyKind.Bool)
                {
                    history = value.BoolValue;
                }
                else if (value.Kind == PropertyKind.List && value.Items.Count > 0 && value.Items.All(item => item.Kind == PropertyKind.ObjectId))
                {
                    foreach (var item in value.Items)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (objects.TryGetValue(item.ObjectIdValue, out var child))
                        {
                            entries.Add((entries.Count, ReadEntry(child)));
                        }
                    }
                }
            }
        }

        var ordered = entries
            .OrderBy(e => e.Entry.OrderingId is null)
            .ThenBy(e => e.Entry.OrderingId ?? 0)
            .ThenBy(e => e.Order)
            .Select(e => e.Entry)
            .ToList();
        return new NotebookToc(file.FileGuid, displayName, color, history, ordered);
    }

    private static bool HasObjectList(OneStoreObject record) =>
        record.Properties.Raw.Any(p => p.Value.Kind == PropertyKind.List && p.Value.Items.Count > 0 && p.Value.Items.All(i => i.Kind == PropertyKind.ObjectId));

    private static NotebookTocEntry ReadEntry(OneStoreObject record)
    {
        Guid? guid = null;
        string? fileName = null;
        uint? ordering = null;
        uint? color = null;
        foreach (var (name, value) in record.Properties.Raw)
        {
            if (name == ColorProperty)
            {
                color = ColorOf(value);
            }
            else if (value.Kind == PropertyKind.Bytes)
            {
                if (value.BytesValue.Length == 16 && guid is null)
                {
                    guid = new Guid(value.BytesValue);
                }
                else if (value.BytesValue.Length > 0 && fileName is null)
                {
                    var text = TextDecoding.DecodeUtf16(value.BytesValue);
                    if (text.Length > 0)
                    {
                        fileName = text;
                    }
                }
            }
            else if (value.Kind == PropertyKind.Integer && ordering is null && value.AsUInt32() is { } number)
            {
                ordering = number;
            }
        }

        return new NotebookTocEntry(fileName, guid == Guid.Empty ? null : guid, ordering, color);
    }

    private static uint? ColorOf(PropertyValue value) =>
        value.AsUInt32() is { } color && color != 0xFFFFFFFF ? color : null;
}
