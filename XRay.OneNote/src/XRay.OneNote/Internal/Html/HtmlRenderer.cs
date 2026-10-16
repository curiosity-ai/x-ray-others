using System.Globalization;
using System.Text;
using XRay.OneNote.Internal.Pdf;
using XRay.OneNote.Internal.Text;
using XRay.OneNote.Saving;

namespace XRay.OneNote.Internal.Html;

/// <summary>
/// Writes a document or a page as HTML 5. Offsets and sizes in the document are half-inches; the
/// output uses CSS pixels (48 per half-inch). Nothing here depends on the clock, the culture or hash
/// order, so the output is deterministic.
/// </summary>
internal sealed class HtmlRenderer
{
    private const double PixelsPerHalfInch = 48.0;

    private static readonly string[] SafeSchemes = ["http", "https", "mailto", "ftp", "ftps", "tel", "onenote", "file"];

    private readonly HtmlSaveOptions _options;
    private readonly CancellationToken _ct;
    private TextWriter _w = TextWriter.Null;
    private int _globalImage;
    private int _pageIndex;
    private int _pageImage;
    private int _pageFile;
    private string _titleTag = "h1";

    public HtmlRenderer(HtmlSaveOptions options, CancellationToken cancellationToken)
    {
        _options = options;
        _ct = cancellationToken;
    }

    /// <summary>Saves the selected pages of <paramref name="document"/>.</summary>
    /// <param name="document">The document.</param>
    /// <param name="callerStream">A stream the caller passed in: never closed here.</param>
    /// <param name="streamFor">Creates the stream of a page when neither the callback nor <paramref name="callerStream"/> gave one (page index, number of pages saved).</param>
    public void SaveDocument(Document document, Stream? callerStream, Func<int, int, Stream>? streamFor)
    {
        var pages = document.Pages.ToList();
        var start = Math.Max(_options.PageIndex, 0);
        var end = _options.PageCount is { } count ? (long)start + Math.Max(count, 0) : pages.Count;
        var selected = new List<(int Index, Page Page)>();
        for (var index = start; index < Math.Min(end, pages.Count); index++)
        {
            selected.Add((index, pages[index]));
        }

        _ct.ThrowIfCancellationRequested();
        var perPage = _options.PerPage && selected.Count > 0;
        if (!perPage)
        {
            var args = new PageSavingArgs(-1, null, document.DisplayName, 0, callerStream);
            using var output = OpenOutput(args, callerStream, streamFor, 0, selected.Count);
            WriteDocument(output.Stream, document.DisplayName, selected, document, singleDocument: true);
            return;
        }

        if (selected.Count > 1 && _options.PageSavingCallback is null && streamFor is null)
        {
            throw new InvalidOperationException(
                "Saving several pages as separate HTML documents needs a PageSavingCallback or a file path; use PerPage = false for one document.");
        }

        foreach (var (index, page) in selected)
        {
            _ct.ThrowIfCancellationRequested();
            var args = new PageSavingArgs(index, page, page.Title?.TitleText?.Text, page.Level ?? 1, callerStream);
            using var output = OpenOutput(args, callerStream, streamFor, index, selected.Count);
            WriteDocument(output.Stream, page.Title?.TitleText?.Text ?? document.DisplayName, [(index, page)], document, singleDocument: false);
        }
    }

    /// <summary>Writes one page as a complete HTML document.</summary>
    public void SavePage(Page page, int pageIndex, Stream output)
    {
        WriteDocument(output, page.Title?.TitleText?.Text, [(pageIndex, page)], page.Document, singleDocument: false);
    }

    private PageOutput OpenOutput(PageSavingArgs args, Stream? callerStream, Func<int, int, Stream>? streamFor, int index, int count)
    {
        _options.PageSavingCallback?.Invoke(args);
        _ct.ThrowIfCancellationRequested();
        if (args.OutputStream is { } chosen)
        {
            return new PageOutput(chosen, owned: !ReferenceEquals(chosen, callerStream) && !args.KeepStreamOpen);
        }

        if (streamFor is not null)
        {
            return new PageOutput(streamFor(index, count), owned: true);
        }

        throw new InvalidOperationException("No output stream was provided for the HTML document: set PageSavingArgs.OutputStream or save to a stream or path.");
    }

    private readonly struct PageOutput : IDisposable
    {
        private readonly bool _owned;

        public PageOutput(Stream stream, bool owned)
        {
            Stream = stream;
            _owned = owned;
        }

        public Stream Stream { get; }

        public void Dispose()
        {
            Stream.Flush();
            if (_owned)
            {
                Stream.Dispose();
            }
        }
    }

    // ------------------------------------------------------------------ documents

    private void WriteDocument(Stream output, string? title, List<(int Index, Page Page)> pages, Document? document, bool singleDocument)
    {
        using var writer = new StreamWriter(output, new UTF8Encoding(false), 16 * 1024, leaveOpen: true) { NewLine = "\n" };
        _w = writer;
        _titleTag = singleDocument ? "h2" : "h1";
        var documentTitle = PyText.Strip(title ?? string.Empty);
        if (documentTitle.Length == 0)
        {
            documentTitle = PyText.Strip(document?.DisplayName ?? string.Empty);
        }

        _w.Write("<!DOCTYPE html>\n<html");
        if (!string.IsNullOrWhiteSpace(_options.Language))
        {
            _w.Write(" lang=\"");
            Esc(_options.Language);
            _w.Write('"');
        }

        _w.Write(">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n<meta name=\"generator\" content=\"X-Ray.OneNote\">\n");
        if (!singleDocument && pages.Count == 1)
        {
            var page = pages[0].Page;
            if (!string.IsNullOrWhiteSpace(page.Author))
            {
                _w.Write("<meta name=\"author\" content=\"");
                Esc(page.Author);
                _w.Write("\">\n");
            }

            WriteTimeMeta("created", page.CreationTime);
            WriteTimeMeta("modified", page.LastModifiedTime);
        }

        _w.Write("<title>");
        Esc(documentTitle.Length > 0 ? documentTitle : "Untitled");
        _w.Write("</title>\n");
        if (_options.EmbedCss)
        {
            _w.Write("<style>\n");
            _w.Write(HtmlStyle.Css);
            _w.Write("\n</style>\n");
        }
        else
        {
            _w.Write("<link rel=\"stylesheet\" href=\"");
            Esc(_options.StyleSheetUrl);
            _w.Write("\">\n");
        }

        _w.Write("</head>\n<body class=\"on-body\">\n");
        if (singleDocument)
        {
            _w.Write("<main class=\"on-section\">\n");
            if (documentTitle.Length > 0)
            {
                _w.Write("<h1 class=\"on-section-title\">");
                Esc(documentTitle);
                _w.Write("</h1>\n");
            }
        }

        foreach (var (index, page) in pages)
        {
            _ct.ThrowIfCancellationRequested();
            WritePage(page, index);
        }

        if (singleDocument)
        {
            _w.Write("</main>\n");
        }

        _w.Write("</body>\n</html>\n");
        _w.Flush();
    }

    private void WriteTimeMeta(string name, DateTime? time)
    {
        if (time is { } value)
        {
            _w.Write("<meta name=\"");
            _w.Write(name);
            _w.Write("\" content=\"");
            _w.Write(value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
            _w.Write("Z\">\n");
        }
    }

    // ------------------------------------------------------------------ pages

    private void WritePage(Page page, int pageIndex)
    {
        _pageIndex = pageIndex;
        _pageImage = 0;
        _pageFile = 0;
        var level = page.Level is { } l && l > 0 ? l : 1;
        _w.Write("<article class=\"on-page on-level-");
        _w.Write(level.ToString(CultureInfo.InvariantCulture));
        _w.Write("\" data-page-index=\"");
        _w.Write(pageIndex.ToString(CultureInfo.InvariantCulture));
        _w.Write("\" data-level=\"");
        _w.Write(level.ToString(CultureInfo.InvariantCulture));
        _w.Write('"');
        if (Color(page.BackgroundColor) is { } background)
        {
            _w.Write(" style=\"background-color:");
            _w.Write(background);
            _w.Write('"');
        }

        _w.Write(">\n");
        if (page.Title is { } title)
        {
            WriteTitle(title);
        }

        _w.Write("<div class=\"on-canvas\">\n");
        var listState = new Dictionary<(int, string), int>();
        foreach (var child in page)
        {
            _ct.ThrowIfCancellationRequested();
            switch (child)
            {
                case Title:
                    break;
                case Outline outline:
                    WriteOutline(outline);
                    break;
                case RichText or Table or Image or AttachedFile:
                    WriteContent(child, 0, listState);
                    break;
            }
        }

        _w.Write("</div>\n</article>\n");
    }

    private void WriteTitle(Title title)
    {
        var text = title.TitleText;
        var date = title.TitleDate;
        var time = title.TitleTime;
        var hasText = text is not null && HasText(text);
        var hasDate = date is not null && HasText(date);
        var hasTime = time is not null && HasText(time);
        if (!hasText && !hasDate && !hasTime)
        {
            return;
        }

        _w.Write("<header class=\"on-title\">\n");
        if (hasText)
        {
            _w.Write('<');
            _w.Write(_titleTag);
            _w.Write(" class=\"on-title-text\">");
            WriteRuns(text!);
            _w.Write("</");
            _w.Write(_titleTag);
            _w.Write(">\n");
        }

        if (hasDate || hasTime)
        {
            _w.Write("<p class=\"on-title-meta\">");
            if (hasDate)
            {
                _w.Write("<span class=\"on-title-date\">");
                WriteRuns(date!);
                _w.Write("</span>");
            }

            if (hasDate && hasTime)
            {
                _w.Write(' ');
            }

            if (hasTime)
            {
                _w.Write("<span class=\"on-title-time\">");
                WriteRuns(time!);
                _w.Write("</span>");
            }

            _w.Write("</p>\n");
        }

        _w.Write("</header>\n");
    }

    // ------------------------------------------------------------------ outlines

    private void WriteOutline(Outline outline)
    {
        var style = new List<string>();
        if (outline.HorizontalOffset is { } x && Px(x) is { } left && x > 0)
        {
            style.Add("margin-left:" + left + "px");
        }

        if (outline.MaxWidth is { } width && width > 0 && Px(width) is { } maxWidth)
        {
            style.Add("max-width:" + maxWidth + "px");
        }

        _w.Write("<div class=\"on-outline\"");
        if (outline.HorizontalOffset is { } dx && Px(dx) is { } attrX)
        {
            _w.Write(" data-x=\"" + attrX + "\"");
        }

        if (outline.VerticalOffset is { } dy && Px(dy) is { } attrY)
        {
            _w.Write(" data-y=\"" + attrY + "\"");
        }

        WriteStyleAttribute(style);
        _w.Write(">\n");
        var listState = new Dictionary<(int, string), int>();
        foreach (var child in outline)
        {
            WriteContent(child, 0, listState);
        }

        _w.Write("</div>\n");
    }

    private void WriteContent(Node node, int depth, Dictionary<(int, string), int> listState)
    {
        _ct.ThrowIfCancellationRequested();
        switch (node)
        {
            case OutlineElement outlineElement:
                WriteOutlineElement(outlineElement, depth, listState);
                break;
            case RichText richText:
                WriteParagraph(richText, null, depth, listState);
                break;
            case Table table:
                WriteTable(table);
                break;
            case Image image:
                WriteImage(image);
                break;
            case AttachedFile file:
                WriteFile(file);
                break;
            default:
                foreach (var child in node.TraversalChildren)
                {
                    WriteContent(child, depth, listState);
                }

                break;
        }
    }

    private void WriteOutlineElement(OutlineElement element, int depth, Dictionary<(int, string), int> listState)
    {
        var list = element.NumberList;
        _w.Write("<div class=\"on-oe");
        if (list is not null)
        {
            _w.Write(" on-oe-list");
        }

        _w.Write("\">\n");
        var markerPending = list is not null;
        List<OutlineElement>? nested = null;
        foreach (var child in element)
        {
            if (child is OutlineElement nestedElement)
            {
                (nested ??= []).Add(nestedElement);
            }
            else if (child is RichText richText)
            {
                if (WriteParagraph(richText, markerPending ? list : null, depth, listState))
                {
                    markerPending = false;
                }
            }
            else
            {
                WriteContent(child, depth, listState);
            }
        }

        if (nested is not null)
        {
            _w.Write("<div class=\"on-children\">\n");
            foreach (var child in nested)
            {
                WriteOutlineElement(child, depth + 1, listState);
            }

            _w.Write("</div>\n");
        }

        _w.Write("</div>\n");
    }

    // ------------------------------------------------------------------ text

    private static bool HasText(RichText richText) => PdfText.PlainTextFromRichText(richText).Length > 0;

    /// <summary>Writes a paragraph; false when it was empty and skipped.</summary>
    private bool WriteParagraph(RichText richText, NumberList? list, int depth, Dictionary<(int, string), int> listState)
    {
        var tags = PdfExporter.DedupeTags(richText.Tags);
        if (!HasText(richText) && tags.Count == 0)
        {
            return false;
        }

        var classes = new StringBuilder("on-p");
        AlignmentClass(classes, richText.Alignment);
        if (!_options.ShowHiddenText && AllRunsHidden(richText))
        {
            classes.Append(" on-all-hidden");
        }

        var style = new List<string>();
        if (richText.SpaceBefore is { } before && before > 0 && Num(before) is { } b)
        {
            style.Add("margin-top:" + b + "pt");
        }

        if (richText.SpaceAfter is { } after && after > 0 && Num(after) is { } a)
        {
            style.Add("margin-bottom:" + a + "pt");
        }

        if (richText.LineSpacing is { } spacing && spacing > 0 && Num(spacing) is { } s)
        {
            style.Add("line-height:" + s + "pt");
        }

        _w.Write("<p class=\"");
        _w.Write(classes.ToString());
        _w.Write('"');
        WriteStyleAttribute(style);
        _w.Write('>');
        WriteTags(tags);
        if (list is not null)
        {
            WriteMarker(list, depth, listState);
        }

        WriteRuns(richText);
        _w.Write("</p>\n");
        return true;
    }

    private static bool AllRunsHidden(RichText richText)
    {
        var any = false;
        foreach (var run in PdfExporter.IterRuns(richText))
        {
            if (PdfText.Sanitize(run.Text).Length == 0)
            {
                continue;
            }

            any = true;
            if (!run.Style.IsHidden)
            {
                return false;
            }
        }

        return any;
    }

    private void WriteMarker(NumberList list, int depth, Dictionary<(int, string), int> listState)
    {
        var text = PdfExporter.ResolveListMarkerText(list, depth, listState);
        if (text.Length == 0)
        {
            return;
        }

        var numbered = list.Format?.Contains('\uFFFD', StringComparison.Ordinal) == true;
        if (!numbered && text.All(c => c is >= '\uE000' and <= '\uF8FF' or ' '))
        {
            // A private-use symbol (Wingdings and the like) has no meaning outside its font.
            text = "•";
        }

        _w.Write(numbered ? "<span class=\"on-marker on-marker-number\">" : "<span class=\"on-marker on-marker-bullet\">");
        Esc(text);
        _w.Write("</span>");
    }

    private void WriteRuns(RichText richText)
    {
        string? pendingTarget = null;
        foreach (var run in PdfExporter.IterRuns(richText))
        {
            var style = run.Style;
            if (PdfExporter.ExtractHyperlinkTarget(run.Text) is { } extracted)
            {
                pendingTarget = PdfExporter.NormalizeHyperlinkTarget(extracted);
            }

            var text = PdfText.Sanitize(run.Text);
            var target = PdfExporter.NormalizeHyperlinkTarget(style.HyperlinkAddress);
            if (target is null && style.IsHyperlink)
            {
                target = pendingTarget;
            }

            if (text.Length > 0 && target is not null)
            {
                pendingTarget = null;
            }

            if (text.Length == 0)
            {
                continue;
            }

            WriteRun(text, style, SafeUrl(target));
        }
    }

    private void WriteRun(string text, TextStyle style, string? href)
    {
        if (href is not null)
        {
            _w.Write("<a class=\"on-link\" href=\"");
            Esc(href);
            _w.Write("\">");
        }

        var classes = new List<string>();
        if (style.IsHidden)
        {
            classes.Add("on-hidden");
            if (_options.ShowHiddenText)
            {
                classes.Add("on-hidden-shown");
            }
        }

        if (style.IsMathFormatting)
        {
            classes.Add("on-math");
        }

        var css = new List<string>();
        if (Color(style.FontColor) is { } color)
        {
            css.Add("color:" + color);
        }

        if (Color(style.Highlight) is { } highlight)
        {
            css.Add("background-color:" + highlight);
        }

        if (FontFamily(style.FontName) is { } family)
        {
            css.Add("font-family:'" + family + "'");
        }

        if (style.FontSize is { } size && size > 0 && Num(size) is { } pt)
        {
            css.Add("font-size:" + pt + "pt");
        }

        var span = classes.Count > 0 || css.Count > 0;
        if (span)
        {
            _w.Write("<span");
            if (classes.Count > 0)
            {
                _w.Write(" class=\"" + string.Join(' ', classes) + "\"");
            }

            WriteStyleAttribute(css);
            _w.Write('>');
        }

        if (style.IsBold)
        {
            _w.Write("<strong>");
        }

        if (style.IsItalic)
        {
            _w.Write("<em>");
        }

        if (style.IsUnderline)
        {
            _w.Write("<u>");
        }

        if (style.IsStrikethrough)
        {
            _w.Write("<s>");
        }

        if (style.IsSuperscript)
        {
            _w.Write("<sup>");
        }
        else if (style.IsSubscript)
        {
            _w.Write("<sub>");
        }

        WriteText(text);

        if (style.IsSuperscript)
        {
            _w.Write("</sup>");
        }
        else if (style.IsSubscript)
        {
            _w.Write("</sub>");
        }

        if (style.IsStrikethrough)
        {
            _w.Write("</s>");
        }

        if (style.IsUnderline)
        {
            _w.Write("</u>");
        }

        if (style.IsItalic)
        {
            _w.Write("</em>");
        }

        if (style.IsBold)
        {
            _w.Write("</strong>");
        }

        if (span)
        {
            _w.Write("</span>");
        }

        if (href is not null)
        {
            _w.Write("</a>");
        }
    }

    /// <summary>Writes text with line breaks as <c>&lt;br&gt;</c>.</summary>
    private void WriteText(string text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is not ('\n' or '\r' or '\v'))
            {
                continue;
            }

            Esc(text.AsSpan(start, i - start));
            _w.Write("<br>");
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                i++;
            }

            start = i + 1;
        }

        Esc(text.AsSpan(start));
    }

    // ------------------------------------------------------------------ tags

    private void WriteTags(List<NoteTag> tags)
    {
        foreach (var tag in tags)
        {
            var (kind, glyph) = DescribeTag(tag);
            var status = tag.Status switch
            {
                TagStatus.Completed => "completed",
                TagStatus.Disabled => "disabled",
                _ => "open",
            };
            if (kind == "todo")
            {
                glyph = tag.Status == TagStatus.Completed ? "☑" : "☐";
            }

            _w.Write("<span class=\"on-tag on-tag-" + kind + " on-tag-" + status + "\"");
            if (tag.Icon is { } icon)
            {
                _w.Write(" data-shape=\"" + icon.ToString(CultureInfo.InvariantCulture) + "\"");
            }

            _w.Write(" role=\"img\" aria-label=\"");
            Esc(string.IsNullOrWhiteSpace(tag.Label) ? kind : tag.Label);
            _w.Write('"');
            if (!string.IsNullOrWhiteSpace(tag.Label))
            {
                _w.Write(" title=\"");
                Esc(tag.Label);
                _w.Write('"');
            }

            var css = new List<string>();
            if (Color(tag.FontColor) is { } color)
            {
                css.Add("color:" + color);
            }

            if (Color(tag.Highlight) is { } highlight)
            {
                css.Add("background-color:" + highlight);
            }

            WriteStyleAttribute(css);
            _w.Write('>');
            _w.Write(glyph);
            _w.Write("</span>");
        }
    }

    private void WriteTagsBlock(List<NoteTag> tags)
    {
        if (tags.Count == 0)
        {
            return;
        }

        _w.Write("<span class=\"on-tags\">");
        WriteTags(tags);
        _w.Write("</span>");
    }

    private static (string Kind, string Glyph) DescribeTag(NoteTag tag)
    {
        var s = tag.Icon ?? int.MinValue;
        if (TagShapes.Checkbox.Contains(s))
        {
            return ("todo", "☐");
        }

        if (TagShapes.Star.Contains(s))
        {
            return ("star", "★");
        }

        if (TagShapes.Question.Contains(s))
        {
            return ("question", "?");
        }

        if (TagShapes.Exclamation.Contains(s))
        {
            return ("exclamation", "!");
        }

        if (TagShapes.ArrowRight.Contains(s))
        {
            return ("arrow", "→");
        }

        if (TagShapes.ArrowLeft.Contains(s))
        {
            return ("arrow", "←");
        }

        if (TagShapes.ArrowUp.Contains(s))
        {
            return ("arrow", "↑");
        }

        if (TagShapes.ArrowDown.Contains(s))
        {
            return ("arrow", "↓");
        }

        if (TagShapes.Flag.Contains(s))
        {
            return ("flag", "⚑");
        }

        if (TagShapes.Checkmark.Contains(s))
        {
            return ("check", "✓");
        }

        if (TagShapes.Contact.Contains(s))
        {
            return ("contact", "☺");
        }

        if (TagShapes.Music.Contains(s))
        {
            return ("music", "♪");
        }

        if (TagShapes.Calendar.Contains(s))
        {
            return ("calendar", "▦");
        }

        if (TagShapes.Time.Contains(s))
        {
            return ("time", "◷");
        }

        return ("other", "●");
    }

    // ------------------------------------------------------------------ tables

    private void WriteTable(Table table)
    {
        var rows = table.OfType<CompositeNode>().ToList();
        var tags = PdfExporter.DedupeTags(table.Tags);
        if (rows.Count == 0 && tags.Count == 0)
        {
            return;
        }

        if (tags.Count > 0)
        {
            _w.Write("<div class=\"on-tags-row\">");
            WriteTagsBlock(tags);
            _w.Write("</div>\n");
        }

        var columnCount = rows.Count == 0 ? 0 : rows.Max(row => row.OfType<TableCell>().Count());
        _w.Write(table.IsBordersVisible ? "<table class=\"on-table on-borders\">\n" : "<table class=\"on-table\">\n");
        var widths = table.Columns.Select(c => c.Width).ToList();
        if (columnCount > 0 && widths.Count >= columnCount && widths.Take(columnCount).All(w => w is { } v && double.IsFinite(v) && v > 0.1 && v < 200))
        {
            _w.Write("<colgroup>");
            foreach (var width in widths.Take(columnCount))
            {
                _w.Write("<col style=\"width:" + Px(width!.Value) + "px\">");
            }

            _w.Write("</colgroup>\n");
        }

        _w.Write("<tbody>\n");
        foreach (var row in rows)
        {
            _ct.ThrowIfCancellationRequested();
            _w.Write("<tr class=\"on-row\">");
            foreach (var cell in row.OfType<TableCell>())
            {
                _w.Write("<td class=\"on-cell\">");
                var any = false;
                var listState = new Dictionary<(int, string), int>();
                foreach (var child in cell)
                {
                    if (!any)
                    {
                        _w.Write('\n');
                        any = true;
                    }

                    WriteContent(child, 0, listState);
                }

                _w.Write("</td>");
            }

            _w.Write("</tr>\n");
        }

        _w.Write("</tbody>\n</table>\n");
    }

    // ------------------------------------------------------------------ images and files

    private void WriteImage(Image image)
    {
        var imageIndex = _pageImage++;
        var tags = PdfExporter.DedupeTags(image.Tags);
        var alt = FirstNonEmpty(image.AlternativeTextDescription, image.AlternativeTextTitle) ?? string.Empty;
        var alignment = new StringBuilder("on-image");
        AlignmentClass(alignment, image.Alignment);
        if (image.Bytes.Length == 0)
        {
            WriteImagePlaceholder("on-image-missing", alignment, tags, alt, image.FileName);
            return;
        }

        var format = ImageFormats.Detect(image.Bytes, image.FileName, image.Format);
        var globalIndex = _globalImage++;
        var args = new ImageSavingArgs(
            image,
            image.Bytes,
            format,
            "img-" + globalIndex.ToString(CultureInfo.InvariantCulture) + "." + ImageFormats.Extension(format),
            _pageIndex,
            imageIndex,
            globalIndex);
        _options.ImageSavingCallback?.Invoke(args);
        _ct.ThrowIfCancellationRequested();
        if (args.OutputStream is { } stream)
        {
            try
            {
                stream.Write(image.Bytes);
                stream.Flush();
            }
            finally
            {
                if (!args.KeepStreamOpen)
                {
                    stream.Dispose();
                }
            }
        }

        string src;
        if (!string.IsNullOrEmpty(args.Url))
        {
            src = args.Url;
        }
        else if (args.SkipEmbedding)
        {
            WriteImagePlaceholder("on-image-skipped", alignment, tags, alt, image.FileName);
            return;
        }
        else if (_options.EmbedImages)
        {
            src = "data:" + ImageFormats.MimeType(format) + ";base64," + Convert.ToBase64String(image.Bytes);
        }
        else
        {
            src = args.SuggestedFileName;
        }

        _w.Write("<div class=\"" + alignment + "\">");
        WriteTagsBlock(tags);
        var link = SafeUrl(PdfExporter.NormalizeHyperlinkTarget(image.HyperlinkUrl));
        if (link is not null)
        {
            _w.Write("<a href=\"");
            Esc(link);
            _w.Write("\">");
        }

        _w.Write("<img class=\"on-img\" src=\"");
        Esc(src);
        _w.Write("\" alt=\"");
        Esc(alt);
        _w.Write('"');
        if (!string.IsNullOrWhiteSpace(image.AlternativeTextTitle))
        {
            _w.Write(" title=\"");
            Esc(image.AlternativeTextTitle);
            _w.Write('"');
        }

        if (image.Width is { } width && width > 0 && Px(width) is { } w)
        {
            _w.Write(" width=\"" + Math.Round(double.Parse(w, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture) + "\"");
        }

        if (image.Height is { } height && height > 0 && Px(height) is { } h)
        {
            _w.Write(" height=\"" + Math.Round(double.Parse(h, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture) + "\"");
        }

        _w.Write('>');
        if (link is not null)
        {
            _w.Write("</a>");
        }

        _w.Write("</div>\n");
    }

    private void WriteImagePlaceholder(string kind, StringBuilder alignment, List<NoteTag> tags, string alt, string? fileName)
    {
        _w.Write("<div class=\"" + alignment + "\">");
        WriteTagsBlock(tags);
        _w.Write("<span class=\"" + kind + "\">[image");
        var label = FirstNonEmpty(alt, fileName);
        if (label is not null)
        {
            _w.Write(": ");
            Esc(label);
        }

        _w.Write("]</span></div>\n");
    }

    private void WriteFile(AttachedFile file)
    {
        var fileIndex = _pageFile++;
        var tags = PdfExporter.DedupeTags(file.Tags);
        var name = PyText.Strip(file.FileName ?? string.Empty);
        if (name.Length == 0)
        {
            name = "attachment-" + fileIndex.ToString(CultureInfo.InvariantCulture) + ".bin";
        }

        var args = new FileSavingArgs(file, name, _pageIndex, fileIndex);
        _options.FileSavingCallback?.Invoke(args);
        _ct.ThrowIfCancellationRequested();
        if (args.OutputStream is { } stream)
        {
            try
            {
                stream.Write(file.Bytes);
                stream.Flush();
            }
            finally
            {
                if (!args.KeepStreamOpen)
                {
                    stream.Dispose();
                }
            }
        }

        var url = args.Url;
        if (string.IsNullOrEmpty(url) && _options.EmbedAttachments && file.Bytes.Length > 0)
        {
            url = "data:application/octet-stream;base64," + Convert.ToBase64String(file.Bytes);
        }

        _w.Write("<div class=\"on-file\">");
        WriteTagsBlock(tags);
        if (!string.IsNullOrEmpty(url))
        {
            _w.Write("<a class=\"on-file-link\" href=\"");
            Esc(url);
            _w.Write("\" download=\"");
            Esc(name);
            _w.Write("\">");
            Esc(name);
            _w.Write("</a>");
        }
        else
        {
            _w.Write("<span class=\"on-file-name\">");
            Esc(name);
            _w.Write("</span>");
        }

        if (file.Bytes.Length > 0)
        {
            _w.Write(" <span class=\"on-file-size\">(");
            _w.Write(FormatSize(file.Bytes.Length));
            _w.Write(")</span>");
        }

        _w.Write("</div>\n");
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }

        if (bytes < 1024 * 1024)
        {
            return (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
        }

        return (bytes / (1024.0 * 1024.0)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
    }

    // ------------------------------------------------------------------ helpers

    private static void AlignmentClass(StringBuilder classes, HorizontalAlignment? alignment)
    {
        switch (alignment)
        {
            case HorizontalAlignment.Center:
                classes.Append(" on-align-center");
                break;
            case HorizontalAlignment.Right:
                classes.Append(" on-align-right");
                break;
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    /// <summary>A COLORREF as #rrggbb; null for no color or for OneNote's "automatic" (a non-zero top byte).</summary>
    private static string? Color(uint? colorRef)
    {
        if (colorRef is not { } value || (value >> 24) != 0)
        {
            return null;
        }

        return string.Create(CultureInfo.InvariantCulture, $"#{value & 0xFF:x2}{(value >> 8) & 0xFF:x2}{(value >> 16) & 0xFF:x2}");
    }

    private static string? FontFamily(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var builder = new StringBuilder();
        foreach (var c in name.Trim())
        {
            if (c is not ('\'' or '"' or '\\' or ';' or '<' or '>' or '{' or '}' or '&') && !char.IsControl(c))
            {
                builder.Append(c);
            }
        }

        return builder.Length > 0 ? builder.ToString() : null;
    }

    private static string? SafeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        url = url.Trim();
        var colon = url.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
        {
            return url.StartsWith('#') ? url : null;
        }

        var scheme = url[..colon];
        return SafeSchemes.Contains(scheme, StringComparer.OrdinalIgnoreCase) ? url : null;
    }

    private static string? Num(double value) =>
        double.IsFinite(value) ? Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture) : null;

    private static string? Px(double halfInches) => Num(halfInches * PixelsPerHalfInch);

    private void WriteStyleAttribute(List<string> declarations)
    {
        if (declarations.Count == 0)
        {
            return;
        }

        _w.Write(" style=\"");
        for (var i = 0; i < declarations.Count; i++)
        {
            if (i > 0)
            {
                _w.Write(';');
            }

            Esc(declarations[i]);
        }

        _w.Write('"');
    }

    private void Esc(string? text) => Esc(text.AsSpan());

    private void Esc(ReadOnlySpan<char> text)
    {
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            string? replacement = null;
            switch (c)
            {
                case '&':
                    replacement = "&amp;";
                    break;
                case '<':
                    replacement = "&lt;";
                    break;
                case '>':
                    replacement = "&gt;";
                    break;
                case '"':
                    replacement = "&quot;";
                    break;
                case '\t':
                    break;
                case '\n':
                case '\r':
                    break;
                default:
                    if (c < ' ' || (c >= '\u007F' && c <= '\u009F') || c is '\uFFFE' or '\uFFFF')
                    {
                        replacement = string.Empty;
                    }
                    else if (char.IsHighSurrogate(c))
                    {
                        if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                        {
                            i++;
                        }
                        else
                        {
                            replacement = "\uFFFD";
                        }
                    }
                    else if (char.IsLowSurrogate(c))
                    {
                        replacement = "\uFFFD";
                    }

                    break;
            }

            if (replacement is null)
            {
                continue;
            }

            _w.Write(text[start..i]);
            _w.Write(replacement);
            start = i + 1;
        }

        _w.Write(text[start..]);
    }
}

/// <summary>Picture format sniffing for the HTML writer.</summary>
internal static class ImageFormats
{
    public static string Detect(byte[] data, string? fileName, string? declared)
    {
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            return "png";
        }

        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "jpeg";
        }

        if (data.Length >= 4 && data[0] == 'G' && data[1] == 'I' && data[2] == 'F' && data[3] == '8')
        {
            return "gif";
        }

        if (data.Length >= 2 && data[0] == 'B' && data[1] == 'M')
        {
            return "bmp";
        }

        if (data.Length >= 4 && ((data[0] == 'I' && data[1] == 'I' && data[2] == 42 && data[3] == 0) || (data[0] == 'M' && data[1] == 'M' && data[2] == 0 && data[3] == 42)))
        {
            return "tiff";
        }

        if (data.Length >= 44 && data[0] == 1 && data[1] == 0 && data[2] == 0 && data[3] == 0 && data[40] == ' ' && data[41] == 'E' && data[42] == 'M' && data[43] == 'F')
        {
            return "emf";
        }

        if (data.Length >= 4 && ((data[0] == 0xD7 && data[1] == 0xCD && data[2] == 0xC6 && data[3] == 0x9A) || (data[0] == 1 && data[1] == 0 && data[2] == 9 && data[3] == 0)))
        {
            return "wmf";
        }

        foreach (var hint in new[] { declared, Path.GetExtension(fileName ?? string.Empty) })
        {
            switch (hint?.Trim().TrimStart('.').ToLowerInvariant())
            {
                case "png":
                    return "png";
                case "jpg" or "jpeg" or "jpe":
                    return "jpeg";
                case "gif":
                    return "gif";
                case "bmp":
                    return "bmp";
                case "tif" or "tiff":
                    return "tiff";
            }
        }

        return "bin";
    }

    public static string Extension(string format) => format switch
    {
        "jpeg" => "jpg",
        _ => format,
    };

    public static string MimeType(string format) => format switch
    {
        "png" => "image/png",
        "jpeg" => "image/jpeg",
        "gif" => "image/gif",
        "bmp" => "image/bmp",
        "tiff" => "image/tiff",
        "emf" => "image/emf",
        "wmf" => "image/wmf",
        _ => "application/octet-stream",
    };
}
