using System.Collections;

namespace XRay.OneNote;

/// <summary>A page of a section. Its children are the <see cref="Title"/> (when present) and the page content.</summary>
public sealed class Page : CompositeNode
{
    /// <summary>The page title, which is also the first child when present.</summary>
    public Title? Title { get; set; }

    /// <summary>The page author, when stored.</summary>
    public string? Author { get; set; }

    /// <summary>When the page was created (UTC), when known.</summary>
    public DateTime? CreationTime { get; set; }

    /// <summary>When the page was last modified (UTC), when known.</summary>
    public DateTime? LastModifiedTime { get; set; }

    /// <summary>The page level: 1 for a page, 2 and deeper for subpages.</summary>
    public int? Level { get; set; }

    /// <summary>The page background color, when known.</summary>
    public uint? BackgroundColor { get; set; }

    /// <summary>Whether this is a conflict page.</summary>
    public bool IsConflictPage { get; set; }

    /// <summary>
    /// A deep copy of the page and its content. The copy is detached (it has no parent).
    /// <paramref name="cloneHistory"/> is accepted for API compatibility; history is kept per document,
    /// so it is not copied.
    /// </summary>
    public Page Clone(bool cloneHistory = false)
    {
        _ = cloneHistory;
        return new CloneContext().Clone(this);
    }

    private protected override void AcceptCore(DocumentVisitor visitor)
    {
        visitor.VisitPageStart(this);
        AcceptChildren(visitor);
        visitor.VisitPageEnd(this);
    }

    internal override Node CloneCore(CloneContext context)
    {
        var copy = new Page
        {
            Author = Author,
            CreationTime = CreationTime,
            LastModifiedTime = LastModifiedTime,
            Level = Level,
            BackgroundColor = BackgroundColor,
            IsConflictPage = IsConflictPage,
        };
        context.Register(this, copy);
        CloneChildrenInto(copy, context);
        copy.Title = Title is null ? null : context.Clone(Title);
        return copy;
    }
}

/// <summary>
/// The earlier versions of a page, oldest first, and the current version. Loaded when
/// <see cref="LoadOptions.LoadHistory"/> is set.
/// </summary>
public sealed class PageHistory : IList<Page>
{
    private readonly List<Page> _versions = [];

    /// <summary>Creates an empty history for <paramref name="current"/>.</summary>
    public PageHistory(Page current)
    {
        ArgumentNullException.ThrowIfNull(current);
        Current = current;
    }

    /// <summary>The current version of the page.</summary>
    public Page Current { get; }

    /// <summary>The number of earlier versions.</summary>
    public int Count => _versions.Count;

    /// <inheritdoc/>
    public bool IsReadOnly => false;

    /// <inheritdoc/>
    public Page this[int index]
    {
        get => _versions[index];
        set => _versions[index] = value;
    }

    /// <inheritdoc/>
    public void Add(Page item) => _versions.Add(item);

    /// <summary>Appends several versions.</summary>
    public void AddRange(IEnumerable<Page> pages) => _versions.AddRange(pages);

    /// <inheritdoc/>
    public void Clear() => _versions.Clear();

    /// <inheritdoc/>
    public bool Contains(Page item) => _versions.Contains(item);

    /// <inheritdoc/>
    public void CopyTo(Page[] array, int arrayIndex) => _versions.CopyTo(array, arrayIndex);

    /// <inheritdoc/>
    public int IndexOf(Page item) => _versions.IndexOf(item);

    /// <inheritdoc/>
    public void Insert(int index, Page item) => _versions.Insert(index, item);

    /// <inheritdoc/>
    public bool Remove(Page item) => _versions.Remove(item);

    /// <inheritdoc/>
    public void RemoveAt(int index) => _versions.RemoveAt(index);

    /// <summary>Removes <paramref name="count"/> versions starting at <paramref name="index"/>.</summary>
    public void RemoveRange(int index, int count) => _versions.RemoveRange(index, count);

    /// <inheritdoc/>
    public IEnumerator<Page> GetEnumerator() => _versions.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
