using System.Collections;

namespace XRay.OneNote;

/// <summary>Base type of every node in the document tree.</summary>
public abstract class Node
{
    private protected Node()
    {
    }

    /// <summary>The node that contains this one, or null for a root or detached node.</summary>
    public Node? ParentNode { get; private set; }

    /// <summary>The document this node belongs to, or null when it is not attached to one.</summary>
    public Document? Document
    {
        get
        {
            for (var current = this; current is not null; current = current.ParentNode)
            {
                if (current is Document document)
                {
                    return document;
                }
            }

            return null;
        }
    }

    /// <summary>Calls the visitor for this node and, for container nodes, for every descendant.</summary>
    public void Accept(DocumentVisitor visitor)
    {
        ArgumentNullException.ThrowIfNull(visitor);
        AcceptCore(visitor);
    }

    internal void SetParent(Node? parent) => ParentNode = parent;

    /// <summary>The child nodes used for traversal; empty for leaf nodes.</summary>
    internal virtual IEnumerable<Node> TraversalChildren => [];

    private protected virtual void AcceptCore(DocumentVisitor visitor)
    {
    }

    /// <summary>Deep-copies this node (and its descendants) through <paramref name="context"/>.</summary>
    internal abstract Node CloneCore(CloneContext context);

    /// <summary>Collects every descendant of type <typeparamref name="T"/> in document order.</summary>
    internal static List<T> CollectDescendants<T>(IEnumerable<Node> children)
        where T : Node
    {
        var found = new List<T>();
        foreach (var child in children)
        {
            if (child is T match)
            {
                found.Add(match);
            }

            found.AddRange(CollectDescendants<T>(child.TraversalChildren));
        }

        return found;
    }
}

/// <summary>A node that holds an ordered list of child nodes.</summary>
public abstract class CompositeNode : Node, IEnumerable<Node>
{
    private readonly List<Node> _children = [];

    private protected CompositeNode()
    {
    }

    /// <summary>The first child, or null when there are none.</summary>
    public Node? FirstChild => _children.Count > 0 ? _children[0] : null;

    /// <summary>The last child, or null when there are none.</summary>
    public Node? LastChild => _children.Count > 0 ? _children[^1] : null;

    internal override IEnumerable<Node> TraversalChildren => _children;

    /// <summary>
    /// Containers without visitor callbacks of their own (tables, rows, cells) pass the visitor on to
    /// their children, so content inside them is visited too.
    /// </summary>
    private protected override void AcceptCore(DocumentVisitor visitor) => AcceptChildren(visitor);

    /// <summary>Appends <paramref name="node"/> after the existing children and returns it.</summary>
    public T AppendChildLast<T>(T node)
        where T : Node
    {
        ArgumentNullException.ThrowIfNull(node);
        node.SetParent(this);
        _children.Add(node);
        return node;
    }

    /// <summary>Inserts <paramref name="node"/> before the existing children and returns it.</summary>
    public T AppendChildFirst<T>(T node)
        where T : Node
    {
        ArgumentNullException.ThrowIfNull(node);
        node.SetParent(this);
        _children.Insert(0, node);
        return node;
    }

    /// <summary>Inserts <paramref name="node"/> at <paramref name="index"/> and returns it.</summary>
    public T InsertChild<T>(int index, T node)
        where T : Node
    {
        ArgumentNullException.ThrowIfNull(node);
        node.SetParent(this);
        _children.Insert(index, node);
        return node;
    }

    /// <summary>Removes <paramref name="node"/> from the children and detaches it.</summary>
    public void RemoveChild(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!_children.Remove(node))
        {
            throw new ArgumentException("The node is not a child of this node.", nameof(node));
        }

        node.SetParent(null);
    }

    /// <summary>Every descendant of type <typeparamref name="T"/>, depth first, in document order.</summary>
    public List<T> GetChildNodes<T>()
        where T : Node => CollectDescendants<T>(_children);

    /// <inheritdoc/>
    public IEnumerator<Node> GetEnumerator() => _children.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private protected void AcceptChildren(DocumentVisitor visitor)
    {
        foreach (var child in _children)
        {
            child.Accept(visitor);
        }
    }

    private protected void CloneChildrenInto(CompositeNode target, CloneContext context)
    {
        foreach (var child in _children)
        {
            target.AppendChildLast(context.Clone(child));
        }
    }
}

/// <summary>
/// Visits the nodes of a document. Override the methods for the node types of interest; the defaults
/// do nothing.
/// </summary>
public abstract class DocumentVisitor
{
    /// <summary>Called before the pages of a document.</summary>
    public virtual void VisitDocumentStart(Document document)
    {
    }

    /// <summary>Called after the pages of a document.</summary>
    public virtual void VisitDocumentEnd(Document document)
    {
    }

    /// <summary>Called before the children of a page.</summary>
    public virtual void VisitPageStart(Page page)
    {
    }

    /// <summary>Called after the children of a page.</summary>
    public virtual void VisitPageEnd(Page page)
    {
    }

    /// <summary>Called before the parts of a page title.</summary>
    public virtual void VisitTitleStart(Title title)
    {
    }

    /// <summary>Called after the parts of a page title.</summary>
    public virtual void VisitTitleEnd(Title title)
    {
    }

    /// <summary>Called before the children of an outline.</summary>
    public virtual void VisitOutlineStart(Outline outline)
    {
    }

    /// <summary>Called after the children of an outline.</summary>
    public virtual void VisitOutlineEnd(Outline outline)
    {
    }

    /// <summary>Called before the children of an outline element.</summary>
    public virtual void VisitOutlineElementStart(OutlineElement outlineElement)
    {
    }

    /// <summary>Called after the children of an outline element.</summary>
    public virtual void VisitOutlineElementEnd(OutlineElement outlineElement)
    {
    }

    /// <summary>Called for a rich text node.</summary>
    public virtual void VisitRichTextStart(RichText richText)
    {
    }

    /// <summary>Called after <see cref="VisitRichTextStart"/> for a rich text node.</summary>
    public virtual void VisitRichTextEnd(RichText richText)
    {
    }

    /// <summary>Called before the children of an image.</summary>
    public virtual void VisitImageStart(Image image)
    {
    }

    /// <summary>Called after the children of an image.</summary>
    public virtual void VisitImageEnd(Image image)
    {
    }
}

/// <summary>Options for loading a document.</summary>
public sealed class LoadOptions
{
    /// <summary>The password of an encrypted section. Encrypted sections are not supported, so setting it fails the load.</summary>
    public string? DocumentPassword { get; set; }

    /// <summary>Whether to reconstruct the earlier versions of every page (see <see cref="Document.GetPageHistory"/>).</summary>
    public bool LoadHistory { get; set; }
}

/// <summary>Deep-copy state: maps each original object to its copy so shared references stay shared.</summary>
internal sealed class CloneContext
{
    private readonly Dictionary<object, object> _copies = new(ReferenceEqualityComparer.Instance);

    public T Clone<T>(T node)
        where T : Node
    {
        if (_copies.TryGetValue(node, out var existing))
        {
            return (T)existing;
        }

        var copy = (T)node.CloneCore(this);
        _copies[node] = copy;
        return copy;
    }

    /// <summary>Copies a non-node value once per original instance.</summary>
    public T Copy<T>(T value, Func<T, T> factory)
        where T : class
    {
        if (_copies.TryGetValue(value, out var existing))
        {
            return (T)existing;
        }

        var copy = factory(value);
        _copies[value] = copy;
        return copy;
    }

    public T? CopyNullable<T>(T? value, Func<T, T> factory)
        where T : class => value is null ? null : Copy(value, factory);

    /// <summary>Records <paramref name="copy"/> as the copy of <paramref name="original"/> before its children are copied.</summary>
    public void Register(object original, object copy) => _copies[original] = copy;
}
