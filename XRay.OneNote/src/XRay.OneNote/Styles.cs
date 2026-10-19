namespace XRay.OneNote;

/// <summary>
/// The default character formatting of a paragraph. Text runs inherit it when they are created
/// without a style of their own.
/// </summary>
public sealed class ParagraphStyle
{
    /// <summary>The font family name, or null for the default font.</summary>
    public string? FontName { get; set; }

    /// <summary>The font size in points, or null for the default size.</summary>
    public double? FontSize { get; set; }

    /// <summary>The text color as a COLORREF (0x00BBGGRR), or null for the default color.</summary>
    public uint? FontColor { get; set; }

    /// <summary>The highlight color as a COLORREF (0x00BBGGRR), or null for no highlight.</summary>
    public uint? Highlight { get; set; }

    /// <summary>Whether the text is bold.</summary>
    public bool IsBold { get; set; }

    /// <summary>Whether the text is italic.</summary>
    public bool IsItalic { get; set; }

    /// <summary>Whether the text is underlined.</summary>
    public bool IsUnderline { get; set; }

    /// <summary>Whether the text is struck through.</summary>
    public bool IsStrikethrough { get; set; }

    /// <summary>Whether the text is superscript.</summary>
    public bool IsSuperscript { get; set; }

    /// <summary>Whether the text is subscript.</summary>
    public bool IsSubscript { get; set; }

    /// <summary>
    /// The identifier of the named paragraph style (<c>ParagraphStyleId</c>): <c>p</c>, <c>h1</c> to <c>h6</c>,
    /// <c>code</c>, <c>blockquote</c>, <c>cite</c>, and <c>PageTitle</c>/<c>PageDateTime</c> for the title; null when the
    /// paragraph names none.
    /// </summary>
    public string? StyleId { get; set; }

    /// <summary>The style flags as a bit set: 1 bold, 2 italic, 4 underline, 8 strikethrough.</summary>
    public int FontStyle => StyleFlags.Compute(IsBold, IsItalic, IsUnderline, IsStrikethrough);

    /// <summary>A new paragraph style with every property at its default.</summary>
    public static ParagraphStyle Default => new();

    internal ParagraphStyle Clone() => (ParagraphStyle)MemberwiseClone();
}

/// <summary>The character formatting of a <see cref="TextRun"/>.</summary>
public sealed class TextStyle
{
    /// <summary>Whether the run is a hyperlink.</summary>
    public bool IsHyperlink { get; set; }

    /// <summary>
    /// The hyperlink target, when the run is a hyperlink with an address. A hyperlink written as a field code in the
    /// text (a <c>HYPERLINK "address"</c> run, introduced by U+FDDF, before the displayed text) gives its address
    /// to the hyperlink runs it governs; the field code itself stays in <see cref="RichText.Text"/>.
    /// </summary>
    public string? HyperlinkAddress { get; set; }

    /// <summary>The font family name, or null for the paragraph default.</summary>
    public string? FontName { get; set; }

    /// <summary>The font size in points, or null for the paragraph default.</summary>
    public double? FontSize { get; set; }

    /// <summary>The text color as a COLORREF (0x00BBGGRR), or null for the default color.</summary>
    public uint? FontColor { get; set; }

    /// <summary>The highlight color as a COLORREF (0x00BBGGRR), or null for no highlight.</summary>
    public uint? Highlight { get; set; }

    /// <summary>The language as a Windows LCID (for example 1033 for en-US), or null when unspecified.</summary>
    public int? Language { get; set; }

    /// <summary>Whether the text is bold.</summary>
    public bool IsBold { get; set; }

    /// <summary>Whether the text is italic.</summary>
    public bool IsItalic { get; set; }

    /// <summary>Whether the text is underlined.</summary>
    public bool IsUnderline { get; set; }

    /// <summary>Whether the text is struck through.</summary>
    public bool IsStrikethrough { get; set; }

    /// <summary>Whether the text is superscript.</summary>
    public bool IsSuperscript { get; set; }

    /// <summary>Whether the text is subscript.</summary>
    public bool IsSubscript { get; set; }

    /// <summary>Whether the text is hidden.</summary>
    public bool IsHidden { get; set; }

    /// <summary>Whether the text uses math formatting.</summary>
    public bool IsMathFormatting { get; set; }

    /// <summary>The style flags as a bit set: 1 bold, 2 italic, 4 underline, 8 strikethrough.</summary>
    public int FontStyle => StyleFlags.Compute(IsBold, IsItalic, IsUnderline, IsStrikethrough);

    /// <summary>A new default style (language en-US).</summary>
    public static TextStyle Default => new() { Language = 1033 };

    /// <summary>A new style matching OneNote's default page title text: 20 pt bold.</summary>
    public static TextStyle DefaultMsOneNoteTitleTextStyle => new() { FontSize = 20.0, IsBold = true, Language = 1033 };

    /// <summary>A new style matching OneNote's default page title date: 11 pt.</summary>
    public static TextStyle DefaultMsOneNoteTitleDateStyle => new() { FontSize = 11.0, Language = 1033 };

    /// <summary>A new style matching OneNote's default page title time: 11 pt.</summary>
    public static TextStyle DefaultMsOneNoteTitleTimeStyle => new() { FontSize = 11.0, Language = 1033 };

    internal static TextStyle FromParagraphStyle(ParagraphStyle? style)
    {
        style ??= new ParagraphStyle();
        return new TextStyle
        {
            FontName = style.FontName,
            FontSize = style.FontSize,
            FontColor = style.FontColor,
            Highlight = style.Highlight,
            IsBold = style.IsBold,
            IsItalic = style.IsItalic,
            IsUnderline = style.IsUnderline,
            IsStrikethrough = style.IsStrikethrough,
            IsSuperscript = style.IsSuperscript,
            IsSubscript = style.IsSubscript,
        };
    }

    internal TextStyle Clone() => (TextStyle)MemberwiseClone();
}

/// <summary>A span of text with uniform formatting.</summary>
public sealed class TextRun
{
    /// <summary>Creates a run.</summary>
    public TextRun(string text = "", TextStyle? style = null)
    {
        Text = text ?? string.Empty;
        Style = style ?? new TextStyle();
    }

    /// <summary>The text of the run.</summary>
    public string Text { get; set; }

    /// <summary>The formatting of the run.</summary>
    public TextStyle Style { get; set; }
}

/// <summary>The numbering or bullet of a list item (an <see cref="OutlineElement"/>).</summary>
public sealed class NumberList
{
    /// <summary>
    /// The list format string as stored in the file. For numbered lists it holds a U+FFFD marker
    /// followed by a character selecting the numbering style; for bullets it is the bullet text.
    /// </summary>
    public string? Format { get; set; }

    /// <summary>The numbering format; the same value as <see cref="Format"/>.</summary>
    public string? NumberFormat { get; set; }

    /// <summary>The font of the list marker, when known.</summary>
    public string? Font { get; set; }

    /// <summary>The font size of the list marker, when known.</summary>
    public double? FontSize { get; set; }

    /// <summary>The color of the list marker, when known.</summary>
    public uint? FontColor { get; set; }

    /// <summary>Whether the list marker is bold.</summary>
    public bool IsBold { get; set; }

    /// <summary>Whether the list marker is italic.</summary>
    public bool IsItalic { get; set; }

    /// <summary>When the list was last modified, when known.</summary>
    public DateTime? LastModifiedTime { get; set; }

    /// <summary>The number this item restarts the list at, or null when it continues the list.</summary>
    public int? Restart { get; set; }

    /// <summary>The header for item <paramref name="number"/>: the format with "{0}" replaced by the number.</summary>
    public string GetNumberedListHeader(int number)
    {
        if (Format is null)
        {
            return string.Empty;
        }

        return Format.Contains("{0}", StringComparison.Ordinal)
            ? Format.Replace("{0}", number.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            : Format;
    }

    internal NumberList Clone() => (NumberList)MemberwiseClone();
}

/// <summary>A note tag (to-do box, star, question mark, ...) attached to text, an image, a table or a file.</summary>
public sealed class NoteTag
{
    private readonly TagStatus? _status;

    internal NoteTag(
        string? label = null,
        int? icon = null,
        TagStatus? status = null,
        uint? highlight = null,
        DateTime? creationTime = null,
        DateTime? completedTime = null,
        uint? fontColor = null)
    {
        Label = label;
        Icon = icon;
        _status = status;
        Highlight = highlight;
        CreationTime = creationTime;
        CompletedTime = completedTime;
        FontColor = fontColor;
    }

    /// <summary>The tag's label, for example "Important".</summary>
    public string? Label { get; set; }

    /// <summary>The tag's shape (MS-ONE NoteTagShape), or null when unknown.</summary>
    public int? Icon { get; set; }

    /// <summary>Whether the tag is open or completed.</summary>
    public TagStatus Status => _status ?? (CompletedTime is not null ? TagStatus.Completed : TagStatus.Open);

    /// <summary>The tag's highlight color as a COLORREF (0x00BBGGRR), when set.</summary>
    public uint? Highlight { get; set; }

    /// <summary>When the tag was created (UTC), when known.</summary>
    public DateTime? CreationTime { get; set; }

    /// <summary>When the tag was completed (UTC), when it has been.</summary>
    public DateTime? CompletedTime { get; }

    /// <summary>The tag's text color as a COLORREF (0x00BBGGRR), when set.</summary>
    public uint? FontColor { get; set; }

    /// <summary>A yellow star tag (shape 13).</summary>
    public static NoteTag CreateYellowStar(string? label = null) => new(string.IsNullOrEmpty(label) ? "Yellow Star" : label, 13);

    /// <summary>A question mark tag (shape 15).</summary>
    public static NoteTag CreateQuestionMark(string? label = null) => new(string.IsNullOrEmpty(label) ? "Question Mark" : label, 15);

    /// <summary>A musical note tag (shape 121).</summary>
    public static NoteTag CreateMusicalNote(string? label = null) => new(string.IsNullOrEmpty(label) ? "Musical Note" : label, 121);

    internal NoteTag Clone() => new(Label, Icon, _status, Highlight, CreationTime, CompletedTime, FontColor);
}

internal static class StyleFlags
{
    public static int Compute(bool bold, bool italic, bool underline, bool strikethrough) =>
        (bold ? 1 : 0) | (italic ? 2 : 0) | (underline ? 4 : 0) | (strikethrough ? 8 : 0);
}
