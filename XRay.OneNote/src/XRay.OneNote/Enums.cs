namespace XRay.OneNote;

/// <summary>Output formats supported by <see cref="Document.Save(string, SaveFormat)"/>.</summary>
public enum SaveFormat
{
    /// <summary>Portable Document Format.</summary>
    Pdf,
}

/// <summary>The OneNote file format a document was read from.</summary>
public enum FileFormat
{
    /// <summary>The format could not be determined.</summary>
    Unknown,

    /// <summary>OneNote 2010 and later section files (.one, MS-ONESTORE revision store).</summary>
    OneNote2010,

    /// <summary>OneNote Online (MS-FSSHTTPB packaging).</summary>
    OneNoteOnline,

    /// <summary>OneNote 2007 section files.</summary>
    OneNote2007,
}

/// <summary>Horizontal alignment of a paragraph or an image.</summary>
public enum HorizontalAlignment
{
    /// <summary>Aligned to the left edge.</summary>
    Left,

    /// <summary>Centered.</summary>
    Center,

    /// <summary>Aligned to the right edge.</summary>
    Right,
}

/// <summary>Completion state of a <see cref="NoteTag"/>.</summary>
public enum TagStatus
{
    /// <summary>The tag is open (not completed).</summary>
    Open = 0,

    /// <summary>The tag has been checked off.</summary>
    Completed = 1,

    /// <summary>The tag is disabled.</summary>
    Disabled = 2,
}

/// <summary>Kinds of document nodes.</summary>
public enum NodeType
{
    /// <summary>A <see cref="OneNote.Document"/>.</summary>
    Document,

    /// <summary>A <see cref="OneNote.Page"/>.</summary>
    Page,

    /// <summary>An <see cref="OneNote.Outline"/>.</summary>
    Outline,

    /// <summary>An <see cref="OneNote.OutlineElement"/>.</summary>
    OutlineElement,

    /// <summary>A <see cref="OneNote.RichText"/>.</summary>
    RichText,

    /// <summary>An <see cref="OneNote.Image"/>.</summary>
    Image,

    /// <summary>A <see cref="OneNote.Table"/>.</summary>
    Table,

    /// <summary>An <see cref="OneNote.AttachedFile"/>.</summary>
    AttachedFile,
}
