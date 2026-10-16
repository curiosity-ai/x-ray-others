using System.Collections;
using XRay.OneNote.Internal.Text;

namespace XRay.OneNote;

/// <summary>
/// A paragraph of text made of formatted <see cref="TextRuns"/>. <see cref="Text"/> is the
/// concatenation of the runs.
/// </summary>
public sealed class RichText : Node
{
    private string _text;
    private List<TextRun> _textRuns;
    private ParagraphStyle _paragraphStyle;
    private TextStyle _defaultTextStyle;

    /// <summary>
    /// Creates a paragraph. With <paramref name="textRuns"/> the text is the concatenation of the runs
    /// (unless <paramref name="text"/> is also given); with only <paramref name="text"/> a single run
    /// is created in the paragraph style's formatting.
    /// </summary>
    public RichText(
        string? text = null,
        IEnumerable<TextRun>? textRuns = null,
        ParagraphStyle? paragraphStyle = null,
        HorizontalAlignment? alignment = null,
        IEnumerable<NoteTag>? tags = null)
    {
        text ??= string.Empty;
        _paragraphStyle = paragraphStyle ?? new ParagraphStyle();
        Alignment = alignment;
        _defaultTextStyle = TextStyle.FromParagraphStyle(_paragraphStyle);
        Tags = tags is null ? [] : [.. tags];
        _textRuns = textRuns is null ? [] : [.. textRuns];
        if (_textRuns.Count > 0)
        {
            _defaultTextStyle = _textRuns[0].Style;
        }

        _text = textRuns is null ? text : (text.Length > 0 ? text : JoinRuns(_textRuns));
        if (_textRuns.Count == 0 && _text.Length > 0)
        {
            _textRuns = [new TextRun(_text, _defaultTextStyle)];
        }
    }

    /// <summary>The paragraph text. Setting it replaces the runs with one run in the current style.</summary>
    public string Text
    {
        get => _textRuns.Count > 0 ? JoinRuns(_textRuns) : _text;
        set
        {
            _text = value ?? string.Empty;
            if (_text.Length > 0)
            {
                var style = _textRuns.Count > 0 ? _textRuns[0].Style : _defaultTextStyle;
                _defaultTextStyle = style;
                _textRuns = [new TextRun(_text, style)];
            }
            else
            {
                _textRuns = [];
            }
        }
    }

    /// <summary>The formatted runs, in order. The list is live: changes are reflected in <see cref="Text"/>.</summary>
    public List<TextRun> TextRuns => _textRuns;

    /// <summary>The paragraph's default formatting.</summary>
    public ParagraphStyle ParagraphStyle
    {
        get => _paragraphStyle;
        set
        {
            _paragraphStyle = value ?? new ParagraphStyle();
            _defaultTextStyle = TextStyle.FromParagraphStyle(_paragraphStyle);
        }
    }

    /// <summary>The note tags on this paragraph.</summary>
    public List<NoteTag> Tags { get; private set; }

    /// <summary>The number of characters (UTF-16 code units) in <see cref="Text"/>.</summary>
    public int Length => Text.Length;

    /// <summary>The paragraph alignment, or null when not specified.</summary>
    public HorizontalAlignment? Alignment { get; set; }

    /// <summary>When the paragraph was last modified, when known.</summary>
    public DateTime? LastModifiedTime { get; set; }

    /// <summary>Space before the paragraph in points, when known.</summary>
    public double? SpaceBefore { get; set; }

    /// <summary>Space after the paragraph in points, when known.</summary>
    public double? SpaceAfter { get; set; }

    /// <summary>The line spacing, when known.</summary>
    public double? LineSpacing { get; set; }

    /// <summary>Whether this paragraph is the text of its page title.</summary>
    public bool IsTitleText => ParentNode is Title title && ReferenceEquals(title.TitleText, this);

    /// <summary>Whether this paragraph is the date of its page title.</summary>
    public bool IsTitleDate => ParentNode is Title title && ReferenceEquals(title.TitleDate, this);

    /// <summary>Whether this paragraph is the time of its page title.</summary>
    public bool IsTitleTime => ParentNode is Title title && ReferenceEquals(title.TitleTime, this);

    /// <summary>Appends a run; without a style it uses the style of the last appended text.</summary>
    public RichText Append(string text, TextStyle? style = null)
    {
        text ??= string.Empty;
        var effective = style ?? _defaultTextStyle;
        _defaultTextStyle = effective;
        if (_textRuns.Count > 0)
        {
            _textRuns.Add(new TextRun(text, effective));
            _text = JoinRuns(_textRuns);
            return this;
        }

        var combined = Text + text;
        _text = combined;
        _textRuns = combined.Length > 0 ? [new TextRun(combined, effective)] : [];
        return this;
    }

    /// <summary>Inserts text at the start of the paragraph.</summary>
    public RichText AppendFront(string text, TextStyle? style = null) => Insert(0, text, style);

    /// <summary>Removes all text and runs.</summary>
    public RichText Clear()
    {
        _text = string.Empty;
        _textRuns = [];
        return this;
    }

    /// <summary>Enumerates the characters of <see cref="Text"/>.</summary>
    public IEnumerator<char> GetEnumerator() => ((IEnumerable<char>)Text).GetEnumerator();

    /// <summary>
    /// The index of <paramref name="value"/> in <see cref="Text"/> within the clamped range
    /// [<paramref name="startIndex"/>, <paramref name="startIndex"/> + <paramref name="count"/>), or -1.
    /// </summary>
    public int IndexOf(string value, int startIndex = 0, int? count = null, StringComparison comparisonType = StringComparison.Ordinal)
    {
        ArgumentNullException.ThrowIfNull(value);
        var haystack = Text;
        var start = Math.Clamp(startIndex, 0, haystack.Length);
        var end = count is null ? haystack.Length : Math.Max(start, Math.Min(start + count.Value, haystack.Length));
        return haystack.IndexOf(value, start, end - start, comparisonType);
    }

    /// <summary>Inserts text at <paramref name="index"/> (clamped). The paragraph becomes a single run.</summary>
    public RichText Insert(int index, string text, TextStyle? style = null)
    {
        text ??= string.Empty;
        var current = Text;
        index = Math.Clamp(index, 0, current.Length);
        _text = string.Concat(current.AsSpan(0, index), text, current.AsSpan(index));
        var effective = style ?? (_textRuns.Count > 0 ? _textRuns[0].Style : _defaultTextStyle);
        _defaultTextStyle = effective;
        _textRuns = _text.Length > 0 ? [new TextRun(_text, effective)] : [];
        return this;
    }

    /// <summary>Removes <paramref name="count"/> characters (or the rest) from <paramref name="start"/>.</summary>
    public RichText Remove(int start, int? count = null)
    {
        var current = Text;
        start = Math.Clamp(start, 0, current.Length);
        var end = count is null ? current.Length : Math.Max(start, Math.Min(start + count.Value, current.Length));
        _text = string.Concat(current.AsSpan(0, start), current.AsSpan(end));
        SingleRunInCurrentStyle();
        return this;
    }

    /// <summary>Replaces every occurrence of <paramref name="oldValue"/>. The paragraph becomes a single run.</summary>
    public RichText Replace(string oldValue, string newValue, TextStyle? style = null)
    {
        ArgumentNullException.ThrowIfNull(oldValue);
        ArgumentNullException.ThrowIfNull(newValue);
        if (oldValue.Length == 0)
        {
            throw new ArgumentException("The value to replace must not be empty.", nameof(oldValue));
        }

        _text = Text.Replace(oldValue, newValue, StringComparison.Ordinal);
        if (_text.Length > 0)
        {
            var effective = style ?? (_textRuns.Count > 0 ? _textRuns[0].Style : _defaultTextStyle);
            _defaultTextStyle = effective;
            _textRuns = [new TextRun(_text, effective)];
        }
        else
        {
            _textRuns = [];
        }

        return this;
    }

    /// <summary>Removes leading and trailing white space.</summary>
    public RichText Trim() => TrimWith(PyText.Strip);

    /// <summary>Removes leading white space.</summary>
    public RichText TrimStart() => TrimWith(PyText.StripStart);

    /// <summary>Removes trailing white space.</summary>
    public RichText TrimEnd() => TrimWith(PyText.StripEnd);

    internal void ReplaceTextRuns(List<TextRun> runs)
    {
        _textRuns = runs;
        if (_textRuns.Count > 0)
        {
            _defaultTextStyle = _textRuns[0].Style;
        }

        _text = JoinRuns(_textRuns);
    }

    private protected override void AcceptCore(DocumentVisitor visitor)
    {
        visitor.VisitRichTextStart(this);
        visitor.VisitRichTextEnd(this);
    }

    internal override Node CloneCore(CloneContext context)
    {
        var copy = new RichText();
        context.Register(this, copy);
        copy._text = _text;
        copy._paragraphStyle = context.Copy(_paragraphStyle, s => s.Clone());
        copy._defaultTextStyle = context.Copy(_defaultTextStyle, s => s.Clone());
        copy._textRuns = [.. _textRuns.Select(run => context.Copy(run, r => new TextRun(r.Text, context.Copy(r.Style, s => s.Clone()))))];
        copy.Tags = [.. Tags.Select(tag => context.Copy(tag, t => t.Clone()))];
        copy.Alignment = Alignment;
        copy.LastModifiedTime = LastModifiedTime;
        copy.SpaceBefore = SpaceBefore;
        copy.SpaceAfter = SpaceAfter;
        copy.LineSpacing = LineSpacing;
        return copy;
    }

    private static string JoinRuns(List<TextRun> runs) => string.Concat(runs.Select(run => run.Text));

    private void SingleRunInCurrentStyle()
    {
        if (_text.Length > 0)
        {
            var style = _textRuns.Count > 0 ? _textRuns[0].Style : _defaultTextStyle;
            _defaultTextStyle = style;
            _textRuns = [new TextRun(_text, style)];
        }
        else
        {
            _textRuns = [];
        }
    }

    private RichText TrimWith(Func<string, string> trim)
    {
        var current = Text;
        var trimmed = trim(current);
        if (trimmed != current)
        {
            Text = trimmed;
        }

        return this;
    }
}

/// <summary>The title block of a page: up to three paragraphs for the title text, date and time.</summary>
public sealed class Title : Node, IEnumerable<Node>
{
    private RichText? _titleText;
    private RichText? _titleDate;
    private RichText? _titleTime;

    /// <summary>Creates a title from its parts.</summary>
    public Title(RichText? titleText = null, RichText? titleDate = null, RichText? titleTime = null)
    {
        TitleText = titleText;
        TitleDate = titleDate;
        TitleTime = titleTime;
    }

    /// <summary>The title text.</summary>
    public RichText? TitleText
    {
        get => _titleText;
        set => SetPart(ref _titleText, value);
    }

    /// <summary>The title date line.</summary>
    public RichText? TitleDate
    {
        get => _titleDate;
        set => SetPart(ref _titleDate, value);
    }

    /// <summary>The title time line.</summary>
    public RichText? TitleTime
    {
        get => _titleTime;
        set => SetPart(ref _titleTime, value);
    }

    internal override IEnumerable<Node> TraversalChildren
    {
        get
        {
            if (_titleText is not null)
            {
                yield return _titleText;
            }

            if (_titleDate is not null)
            {
                yield return _titleDate;
            }

            if (_titleTime is not null)
            {
                yield return _titleTime;
            }
        }
    }

    /// <summary>Every descendant of type <typeparamref name="T"/>, in order: text, date, time.</summary>
    public List<T> GetChildNodes<T>()
        where T : Node => CollectDescendants<T>(TraversalChildren);

    /// <inheritdoc/>
    public IEnumerator<Node> GetEnumerator() => TraversalChildren.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private protected override void AcceptCore(DocumentVisitor visitor)
    {
        visitor.VisitTitleStart(this);
        foreach (var child in TraversalChildren)
        {
            child.Accept(visitor);
        }

        visitor.VisitTitleEnd(this);
    }

    internal override Node CloneCore(CloneContext context)
    {
        var copy = new Title();
        context.Register(this, copy);
        copy.TitleText = _titleText is null ? null : context.Clone(_titleText);
        copy.TitleDate = _titleDate is null ? null : context.Clone(_titleDate);
        copy.TitleTime = _titleTime is null ? null : context.Clone(_titleTime);
        return copy;
    }

    private void SetPart(ref RichText? slot, RichText? value)
    {
        if (ReferenceEquals(slot, value))
        {
            return;
        }

        if (slot is not null && ReferenceEquals(slot.ParentNode, this))
        {
            slot.SetParent(null);
        }

        value?.SetParent(this);
        slot = value;
    }
}
