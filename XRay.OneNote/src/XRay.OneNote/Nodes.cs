namespace XRay.OneNote;

/// <summary>A positioned block of content on a page (a OneNote "note container").</summary>
public sealed class Outline : CompositeNode
{
    /// <summary>The horizontal offset from the page origin in half-inches, when known.</summary>
    public double? HorizontalOffset { get; set; }

    /// <summary>The vertical offset from the page origin in half-inches, when known.</summary>
    public double? VerticalOffset { get; set; }

    /// <summary>The maximum width in half-inches, when known.</summary>
    public double? MaxWidth { get; set; }

    /// <summary>The maximum height in half-inches, when known.</summary>
    public double? MaxHeight { get; set; }

    /// <summary>The minimum width in half-inches, when known.</summary>
    public double? MinWidth { get; set; }

    /// <summary>The reserved width in half-inches, when known.</summary>
    public double? ReservedWidth { get; set; }

    /// <summary>The indent position, when known.</summary>
    public double? IndentPosition { get; set; }

    /// <summary>Whether descendants are locked in place.</summary>
    public bool DescendantsCannotBeMoved { get; set; }

    /// <summary>When the outline was last modified, when known.</summary>
    public DateTime? LastModifiedTime { get; set; }

    private protected override void AcceptCore(DocumentVisitor visitor)
    {
        visitor.VisitOutlineStart(this);
        AcceptChildren(visitor);
        visitor.VisitOutlineEnd(this);
    }

    internal override Node CloneCore(CloneContext context)
    {
        var copy = new Outline
        {
            HorizontalOffset = HorizontalOffset,
            VerticalOffset = VerticalOffset,
            MaxWidth = MaxWidth,
            MaxHeight = MaxHeight,
            MinWidth = MinWidth,
            ReservedWidth = ReservedWidth,
            IndentPosition = IndentPosition,
            DescendantsCannotBeMoved = DescendantsCannotBeMoved,
            LastModifiedTime = LastModifiedTime,
        };
        context.Register(this, copy);
        CloneChildrenInto(copy, context);
        return copy;
    }
}

/// <summary>One paragraph-level element of an outline; nested elements form indented levels and lists.</summary>
public sealed class OutlineElement : CompositeNode
{
    /// <summary>The list numbering or bullet of this element, when it is a list item.</summary>
    public NumberList? NumberList { get; set; }

    private protected override void AcceptCore(DocumentVisitor visitor)
    {
        visitor.VisitOutlineElementStart(this);
        AcceptChildren(visitor);
        visitor.VisitOutlineElementEnd(this);
    }

    internal override Node CloneCore(CloneContext context)
    {
        var copy = new OutlineElement { NumberList = context.CopyNullable(NumberList, n => n.Clone()) };
        context.Register(this, copy);
        CloneChildrenInto(copy, context);
        return copy;
    }
}

/// <summary>An embedded picture.</summary>
public sealed class Image : CompositeNode
{
    private string? _fileName;
    private string? _filePath;
    private string? _format;
    private byte[] _bytes = [];
    private double? _originalWidth;
    private double? _originalHeight;

    /// <summary>The picture's original file name, when stored.</summary>
    public string? FileName { get => _fileName; init => _fileName = value; }

    /// <summary>The picture's original path, when stored.</summary>
    public string? FilePath { get => _filePath; init => _filePath = value; }

    /// <summary>The picture's format, when known.</summary>
    public string? Format { get => _format; init => _format = value; }

    /// <summary>The encoded picture bytes (PNG, JPEG, ...), empty when the data is not in the section.</summary>
    public byte[] Bytes { get => _bytes; init => _bytes = value ?? []; }

    /// <summary>The displayed width in half-inches, when known.</summary>
    public double? Width { get; set; }

    /// <summary>The displayed height in half-inches, when known.</summary>
    public double? Height { get; set; }

    /// <summary>The original width, when known.</summary>
    public double? OriginalWidth { get => _originalWidth; init => _originalWidth = value; }

    /// <summary>The original height, when known.</summary>
    public double? OriginalHeight { get => _originalHeight; init => _originalHeight = value; }

    /// <summary>The horizontal offset, when known.</summary>
    public double? HorizontalOffset { get; set; }

    /// <summary>The vertical offset, when known.</summary>
    public double? VerticalOffset { get; set; }

    /// <summary>The alignment of the picture in its container, when specified.</summary>
    public HorizontalAlignment? Alignment { get; set; }

    /// <summary>Whether the picture is a page background.</summary>
    public bool IsBackground { get; set; }

    /// <summary>The widest the picture is laid out in its outline, in half-inches (<c>LayoutMaxWidth</c>), when stored.</summary>
    public double? LayoutMaxWidth { get; set; }

    /// <summary>The tallest the picture is laid out, in half-inches (<c>LayoutMaxHeight</c>), when stored.</summary>
    public double? LayoutMaxHeight { get; set; }

    /// <summary>When the picture was last modified, when known.</summary>
    public DateTime? LastModifiedTime { get; set; }

    /// <summary>The alternative text title, when stored.</summary>
    public string? AlternativeTextTitle { get; set; }

    /// <summary>The alternative text description, when stored.</summary>
    public string? AlternativeTextDescription { get; set; }

    /// <summary>The hyperlink the picture points to, when it has one.</summary>
    public string? HyperlinkUrl { get; set; }

    /// <summary>The note tags on this picture.</summary>
    public List<NoteTag> Tags { get; private set; } = [];

    /// <summary>Replaces this picture's data and properties with those of <paramref name="image"/>.</summary>
    public void Replace(Image image)
    {
        ArgumentNullException.ThrowIfNull(image);
        _fileName = image.FileName;
        _filePath = image.FilePath;
        _format = image.Format;
        _bytes = image.Bytes;
        Width = image.Width;
        Height = image.Height;
        _originalWidth = image.OriginalWidth;
        _originalHeight = image.OriginalHeight;
        HorizontalOffset = image.HorizontalOffset;
        VerticalOffset = image.VerticalOffset;
        Alignment = image.Alignment;
        IsBackground = image.IsBackground;
        LayoutMaxWidth = image.LayoutMaxWidth;
        LayoutMaxHeight = image.LayoutMaxHeight;
        LastModifiedTime = image.LastModifiedTime;
        AlternativeTextTitle = image.AlternativeTextTitle;
        AlternativeTextDescription = image.AlternativeTextDescription;
        HyperlinkUrl = image.HyperlinkUrl;
        Tags = [.. image.Tags];
    }

    private protected override void AcceptCore(DocumentVisitor visitor)
    {
        visitor.VisitImageStart(this);
        AcceptChildren(visitor);
        visitor.VisitImageEnd(this);
    }

    internal override Node CloneCore(CloneContext context)
    {
        var copy = new Image();
        context.Register(this, copy);
        copy.Replace(this);
        copy.Tags = [.. Tags.Select(tag => context.Copy(tag, t => t.Clone()))];
        CloneChildrenInto(copy, context);
        return copy;
    }
}

/// <summary>A file embedded in a page.</summary>
public sealed class AttachedFile : Node
{
    /// <summary>The attached file's name.</summary>
    public string? FileName { get; init; }

    /// <summary>The attached file's contents, empty when the data is not in the section.</summary>
    public byte[] Bytes { get; init; } = [];

    /// <summary>The note tags on this attachment.</summary>
    public List<NoteTag> Tags { get; private set; } = [];

    internal override Node CloneCore(CloneContext context)
    {
        var copy = new AttachedFile { FileName = FileName, Bytes = Bytes };
        context.Register(this, copy);
        copy.Tags = [.. Tags.Select(tag => context.Copy(tag, t => t.Clone()))];
        return copy;
    }
}

/// <summary>A table: rows of cells, each holding outline content.</summary>
public sealed class Table : CompositeNode
{
    /// <summary>The note tags on this table.</summary>
    public List<NoteTag> Tags { get; private set; } = [];

    /// <summary>The column definitions, in order.</summary>
    public List<TableColumn> Columns { get; private set; } = [];

    /// <summary>Whether the table borders are shown.</summary>
    public bool IsBordersVisible { get; set; } = true;

    /// <summary>When the table was last modified, when known.</summary>
    public DateTime? LastModifiedTime { get; set; }

    internal override Node CloneCore(CloneContext context)
    {
        var copy = new Table { IsBordersVisible = IsBordersVisible, LastModifiedTime = LastModifiedTime };
        context.Register(this, copy);
        copy.Tags = [.. Tags.Select(tag => context.Copy(tag, t => t.Clone()))];
        copy.Columns = [.. Columns.Select(column => context.Copy(column, c => new TableColumn { Width = c.Width, LockedWidth = c.LockedWidth }))];
        CloneChildrenInto(copy, context);
        return copy;
    }
}

/// <summary>A row of a <see cref="Table"/>.</summary>
public sealed class TableRow : CompositeNode
{
    internal override Node CloneCore(CloneContext context)
    {
        var copy = new TableRow();
        context.Register(this, copy);
        CloneChildrenInto(copy, context);
        return copy;
    }
}

/// <summary>A cell of a <see cref="TableRow"/>.</summary>
public sealed class TableCell : CompositeNode
{
    /// <summary>The cell's background color as a COLORREF (0x00BBGGRR), when it has one.</summary>
    public uint? BackgroundColor { get; set; }

    internal override Node CloneCore(CloneContext context)
    {
        var copy = new TableCell { BackgroundColor = BackgroundColor };
        context.Register(this, copy);
        CloneChildrenInto(copy, context);
        return copy;
    }
}

/// <summary>A column of a <see cref="Table"/>.</summary>
public sealed class TableColumn
{
    /// <summary>The column width in half-inches, when known.</summary>
    public double? Width { get; set; }

    /// <summary>Whether the width is locked.</summary>
    public bool LockedWidth { get; set; }
}
