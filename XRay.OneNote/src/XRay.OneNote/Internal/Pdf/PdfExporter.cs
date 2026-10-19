using XRay.OneNote.Internal.Text;
using XRay.OneNote.Saving;

namespace XRay.OneNote.Internal.Pdf;

/// <summary>
/// Renders a document to PDF: page titles, outlines with nested numbered lists, rich text runs with
/// their formatting and hyperlinks, note tag glyphs, images, attached file names and tables. The
/// layout rules follow the Python reference writer so that page breaks and text order match it.
/// </summary>
internal sealed class PdfExporter
{
    private const double LeftMargin = 40;
    private const double TopMargin = 40;
    private const double BottomMargin = 80;
    private const double RightMargin = 40;
    private const double DefaultPageMarginPoints = 36.0;
    private const double PointsPerHalfInch = 36.0;
    private const double PointsPerImageCm = 28.35;
    private const double TagIconSize = 10.0;
    private const double TagIconGap = 2.0;
    private const double OutlineLevelIndent = 18.0;
    private const double OutlineMarkerGap = 6.0;
    private const double OutlineMarkerMinWidth = 12.0;
    private const string DefaultFont = "Arial";
    private static readonly (double R, double G, double B) DefaultHyperlinkColor = (0.0, 0.2, 0.65);
    private static readonly (double R, double G, double B) Black = (0, 0, 0);

    private static readonly HashSet<int> CheckboxShapes = TagShapes.Checkbox;
    private static readonly HashSet<int> StarShapes = TagShapes.Star;
    private static readonly HashSet<int> FlagShapes = TagShapes.Flag;
    private static readonly HashSet<int> QuestionShapes = TagShapes.Question;
    private static readonly HashSet<int> ArrowRightShapes = TagShapes.ArrowRight;
    private static readonly HashSet<int> ArrowLeftShapes = TagShapes.ArrowLeft;
    private static readonly HashSet<int> ArrowUpShapes = TagShapes.ArrowUp;
    private static readonly HashSet<int> ArrowDownShapes = TagShapes.ArrowDown;
    private static readonly HashSet<int> ExclamationShapes = TagShapes.Exclamation;
    private static readonly HashSet<int> ContactShapes = TagShapes.Contact;
    private static readonly HashSet<int> MusicShapes = TagShapes.Music;
    private static readonly HashSet<int> CalendarShapes = TagShapes.Calendar;
    private static readonly HashSet<int> TimeShapes = TagShapes.Time;
    private static readonly HashSet<int> CheckmarkShapes = TagShapes.Checkmark;
    private static readonly HashSet<int> GreenShapes = TagShapes.Green;
    private static readonly HashSet<int> BlueShapes = TagShapes.Blue;
    private static readonly HashSet<int> RedShapes = TagShapes.Red;

    private readonly IPdfCanvas _pdf;
    private readonly FontResolver _fonts;

    private readonly CancellationToken _ct;

    private PdfExporter(IPdfCanvas canvas, PdfSaveOptions options, CancellationToken ct)
    {
        _ct = ct;
        _pdf = canvas;
        _fonts = new FontResolver(canvas.Fonts, options.FontsSubsystem);
    }

    public static byte[] Export(Document document, PdfSaveOptions options)
    {
        using var buffer = new MemoryStream();
        Export(document, options, buffer, default);
        return buffer.ToArray();
    }

    /// <summary>Renders the document and writes the PDF to <paramref name="output"/>.</summary>
    public static void Export(Document document, PdfSaveOptions options, Stream output, CancellationToken ct)
    {
        var canvas = new PdfCanvas { CancellationToken = ct };
        Render(document, options, canvas, ct);
        canvas.Save(output, ct);
    }

    /// <summary>Renders onto <paramref name="canvas"/> without saving it.</summary>
    internal static void Render(Document document, PdfSaveOptions options, IPdfCanvas canvas, CancellationToken ct = default) =>
        new PdfExporter(canvas, options, ct).WritePdf(document, options);

    /// <summary>The fill color of a note tag glyph for a shape.</summary>
    internal static (double R, double G, double B) TagColor(int? shape)
    {
        if (shape is { } s)
        {
            if (QuestionShapes.Contains(s))
            {
                return (0.64, 0.32, 0.82);
            }

            if (GreenShapes.Contains(s))
            {
                return (0.20, 0.58, 0.25);
            }

            if (BlueShapes.Contains(s))
            {
                return (0.13, 0.43, 0.82);
            }

            if (RedShapes.Contains(s))
            {
                return (0.85, 0.18, 0.16);
            }
        }

        return (0.92, 0.55, 0.18);
    }

    // ------------------------------------------------------------------ document

    private void WritePdf(Document document, PdfSaveOptions options)
    {
        var width = _pdf.PageWidth;
        var height = _pdf.PageHeight;
        var pages = document.Pages.ToList();
        long start = Math.Max(options.PageIndex, 0);
        var end = options.PageCount is { } count ? start + count : pages.Count;
        var selected = new List<Page?>();
        for (var index = start; index < Math.Min(end, pages.Count); index++)
        {
            selected.Add(pages[(int)index]);
        }

        if (selected.Count == 0)
        {
            selected.Add(null);
        }

        for (var index = 0; index < selected.Count; index++)
        {
            _ct.ThrowIfCancellationRequested();
            var cursorY = height - TopMargin;
            var page = selected[index];
            if (page is null)
            {
                var emptyText = document.DisplayName ?? "Empty document";
                _pdf.SetFont(_fonts.Register("sans", false, false, FontResolver.RequiresUnicodeFont(emptyText)), 12);
                _pdf.DrawString(LeftMargin, cursorY, emptyText);
                continue;
            }

            var titleNodes = new HashSet<RichText>(ReferenceEqualityComparer.Instance);
            if (page.Title is { } title)
            {
                if (title.TitleText is { } titleText)
                {
                    titleNodes.Add(titleText);
                    cursorY = RenderRichText(titleText, cursorY, width, height, LeftMargin, DefaultFont, 14.0);
                }

                var metaNodes = new List<RichText>();
                if (title.TitleDate is { } date)
                {
                    titleNodes.Add(date);
                    metaNodes.Add(date);
                }

                if (title.TitleTime is { } time)
                {
                    titleNodes.Add(time);
                    metaNodes.Add(time);
                }

                if (metaNodes.Count > 0)
                {
                    cursorY = RenderInlineRichTexts(metaNodes, cursorY, width, height, LeftMargin, 18.0, DefaultFont, 9.0);
                }

                cursorY -= 14;
            }

            var contentOriginY = cursorY;
            var renderedOutlineTexts = new HashSet<RichText>(ReferenceEqualityComparer.Instance);
            var nextOutlineY = contentOriginY;
            foreach (var outline in page.GetChildNodes<Outline>())
            {
                _ct.ThrowIfCancellationRequested();
                // The reference writer positions outlines at a fixed origin: it reads offset and width
                // attributes the outline model does not have, so they are always zero.
                var outlineCursorY = Math.Min(contentOriginY, nextOutlineY);
                var outlineStartX = DefaultPageMarginPoints;
                foreach (var richText in outline.GetChildNodes<RichText>())
                {
                    renderedOutlineTexts.Add(richText);
                }

                outlineCursorY = RenderOutlineContent(outline, outlineCursorY, width, height, outlineStartX, null, [], 0);
                nextOutlineY = outlineCursorY - 8;
            }

            cursorY = Math.Min(cursorY, nextOutlineY);

            foreach (var richText in page.GetChildNodes<RichText>())
            {
                if (titleNodes.Contains(richText) || renderedOutlineTexts.Contains(richText)
                    || HasAncestor<TableCell>(richText) || HasAncestor<Outline>(richText))
                {
                    continue;
                }

                if (PdfText.PlainTextFromRichText(richText).Length == 0)
                {
                    continue;
                }

                cursorY = RenderRichText(richText, cursorY, width, height, LeftMargin, DefaultFont, 11.0);
            }

            foreach (var table in page.GetChildNodes<Table>())
            {
                if (!HasAncestor<Outline>(table))
                {
                    cursorY = RenderTable(table, cursorY, width, height, LeftMargin, null);
                }
            }

            foreach (var image in page.GetChildNodes<Image>())
            {
                if (!HasAncestor<Outline>(image) && image.Bytes.Length > 0)
                {
                    cursorY = RenderImage(image, cursorY, width, height, LeftMargin, null);
                }
            }

            foreach (var attachedFile in page.GetChildNodes<AttachedFile>())
            {
                if (!HasAncestor<Outline>(attachedFile) && !HasAncestor<TableCell>(attachedFile))
                {
                    cursorY = RenderAttachedFile(attachedFile, cursorY, width, height, LeftMargin, null);
                }
            }

            if (index != selected.Count - 1)
            {
                _pdf.ShowPage();
            }
        }
    }

    private static bool HasAncestor<T>(Node node)
        where T : Node
    {
        for (var current = node.ParentNode; current is not null; current = current.ParentNode)
        {
            if (current is T)
            {
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------ text runs

    private string FontNameForStyle(TextStyle style, string defaultFont, string text, bool defaultBold = false)
    {
        var family = string.IsNullOrEmpty(style.FontName) ? defaultFont : style.FontName;
        return _fonts.Register(FontResolver.GroupForFamily(family), defaultBold || style.IsBold, style.IsItalic, FontResolver.RequiresUnicodeFont(text));
    }

    private static (double R, double G, double B) RgbComponents(uint? color, (double R, double G, double B) fallback)
    {
        if (color is not { } value)
        {
            return fallback;
        }

        var clamped = Math.Min(value, 0xFFFFFFu);
        return ((clamped & 0xFF) / 255.0, ((clamped >> 8) & 0xFF) / 255.0, ((clamped >> 16) & 0xFF) / 255.0);
    }

    private static (double R, double G, double B) EffectiveTextColor(TextStyle style)
    {
        if (style.FontColor is not null)
        {
            return RgbComponents(style.FontColor, Black);
        }

        return style.IsHyperlink || !string.IsNullOrEmpty(style.HyperlinkAddress) ? DefaultHyperlinkColor : Black;
    }

    private static bool ShouldUnderline(TextStyle style) => style.IsUnderline || style.IsHyperlink || !string.IsNullOrEmpty(style.HyperlinkAddress);

    private static double FontSizeOf(TextStyle style, double defaultSize) =>
        Math.Max(style.FontSize is { } size && size != 0 ? size : defaultSize, 1.0);

    private double ShowPage(double pageHeight)
    {
        _pdf.ShowPage();
        _pdf.SetFillColor(0, 0, 0);
        _pdf.SetStrokeColor(0, 0, 0);
        return pageHeight - TopMargin;
    }

    private void SetFill((double R, double G, double B) color) => _pdf.SetFillColor(color.R, color.G, color.B);

    private void SetStroke((double R, double G, double B) color) => _pdf.SetStrokeColor(color.R, color.G, color.B);

    private int FitPrefixLength(string text, string fontName, double fontSize, double maxWidth)
    {
        if (text.Length == 0)
        {
            return 0;
        }

        if (_pdf.StringWidth(text, fontName, fontSize) <= maxWidth)
        {
            return text.Length;
        }

        var low = 1;
        var high = text.Length;
        var best = 0;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (_pdf.StringWidth(text[..middle], fontName, fontSize) <= maxWidth)
            {
                best = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        if (best == 0)
        {
            return char.IsHighSurrogate(text[0]) && text.Length > 1 ? 2 : 1;
        }

        // Never split a surrogate pair.
        if (best < text.Length && char.IsHighSurrogate(text[best - 1]) && best > 1)
        {
            best--;
        }

        var breakAt = Math.Max(text.LastIndexOf(' ', best - 1), text.LastIndexOf('\t', best - 1));
        if (breakAt > 0 && breakAt < text.Length)
        {
            return breakAt + 1;
        }

        return best;
    }

    private static readonly System.Text.RegularExpressions.Regex SplitFieldCode = new("^\\s*HYPERLINK\\s+\"[^\"]+\"\\s*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The address of a hyperlink field code that OneNote Online splits over two runs: a run ending in U+FDDF, then a
    /// run holding exactly <c>HYPERLINK "address"</c>. Null when <paramref name="text"/> is not the second half.
    /// </summary>
    internal static string? ExtractSplitHyperlinkTarget(string? previousText, string text)
    {
        if (previousText is not { Length: > 0 } || previousText[^1] != '\uFDDF' || !SplitFieldCode.IsMatch(text))
        {
            return null;
        }

        return ExtractHyperlinkTarget('\uFDDF' + text);
    }

    internal static List<TextRun> IterRuns(RichText richText)
    {
        if (richText.TextRuns.Count > 0)
        {
            // A field code split over two runs is rejoined, so the code is recognised (and hidden) as one.
            var runs = new List<TextRun>(richText.TextRuns.Count);
            foreach (var run in richText.TextRuns)
            {
                if (runs.Count > 0 && ExtractSplitHyperlinkTarget(runs[^1].Text, run.Text) is not null)
                {
                    var marker = runs[^1];
                    runs[^1] = new TextRun(marker.Text[..^1], marker.Style);
                    runs.Add(new TextRun('\uFDDF' + run.Text, run.Style));
                    continue;
                }

                runs.Add(run);
            }

            return runs;
        }

        var text = richText.Text;
        if (text.Length == 0)
        {
            return [];
        }

        var paragraph = richText.ParagraphStyle;
        return
        [
            new TextRun(text, new TextStyle
            {
                FontSize = paragraph.FontSize,
                FontColor = paragraph.FontColor,
                Highlight = paragraph.Highlight,
                IsBold = paragraph.IsBold,
                IsItalic = paragraph.IsItalic,
                IsUnderline = paragraph.IsUnderline,
                IsStrikethrough = paragraph.IsStrikethrough,
                IsSuperscript = paragraph.IsSuperscript,
                IsSubscript = paragraph.IsSubscript,
            }),
        ];
    }

    internal static string? ExtractHyperlinkTarget(string text)
    {
        var match = PdfText.HyperlinkTarget().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var target = PyText.Strip(match.Groups[1].Value);
        return target.Length > 0 ? target : null;
    }

    internal static string? NormalizeHyperlinkTarget(string? target)
    {
        if (string.IsNullOrEmpty(target))
        {
            return null;
        }

        // A URL scheme: a letter, then letters, digits, '+', '-' or '.', then ':'.
        var colon = target.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && char.IsAsciiLetter(target[0]))
        {
            var isScheme = true;
            for (var i = 0; i < colon && isScheme; i++)
            {
                isScheme = char.IsAsciiLetterOrDigit(target[i]) || target[i] is '+' or '-' or '.';
            }

            if (isScheme)
            {
                return target;
            }
        }

        if (target.Contains('@', StringComparison.Ordinal) && !target.Contains(' ', StringComparison.Ordinal))
        {
            return "mailto:" + target;
        }

        return "https://" + target;
    }

    private double DrawSegment(double x, double y, string text, TextStyle style, string defaultFont, double defaultSize, bool defaultBold = false)
    {
        if (text.Length == 0)
        {
            return x;
        }

        var fontSize = FontSizeOf(style, defaultSize);
        var fontName = FontNameForStyle(style, defaultFont, text, defaultBold);
        var textColor = EffectiveTextColor(style);
        var baselineShift = style.IsSuperscript ? 0.35 * fontSize : (style.IsSubscript ? -0.2 * fontSize : 0.0);
        var width = _pdf.StringWidth(text, fontName, fontSize);

        if (style.Highlight is not null)
        {
            SetFill(RgbComponents(style.Highlight, Black));
            _pdf.Rect(x, y - (fontSize * 0.25) + baselineShift, width, fontSize * 1.15, stroke: false, fill: true);
        }

        _pdf.SetFont(fontName, fontSize);
        SetFill(textColor);
        _pdf.DrawString(x, y + baselineShift, text);

        if (ShouldUnderline(style))
        {
            SetStroke(textColor);
            _pdf.Line(x, y - (fontSize * 0.15) + baselineShift, x + width, y - (fontSize * 0.15) + baselineShift);
        }

        if (style.IsStrikethrough)
        {
            SetStroke(textColor);
            _pdf.Line(x, y + (fontSize * 0.3) + baselineShift, x + width, y + (fontSize * 0.3) + baselineShift);
        }

        return x + width;
    }

    private (double X, double Y) RenderRuns(
        List<TextRun> runs,
        double startX,
        double cursorY,
        double pageWidth,
        double pageHeight,
        string defaultFont = DefaultFont,
        double defaultSize = 11.0,
        bool defaultBold = false,
        bool finishWithNewline = true)
    {
        if (runs.Count == 0)
        {
            return (startX, cursorY);
        }

        var x = startX;
        var baseLineHeight = defaultSize * 1.35;
        var currentLineHeight = baseLineHeight;
        var renderedAny = false;
        string? pendingHyperlinkTarget = null;

        foreach (var run in runs)
        {
            _ct.ThrowIfCancellationRequested();
            var style = run.Style;
            var fontSize = FontSizeOf(style, defaultSize);
            var rawText = run.Text;
            var fontName = FontNameForStyle(style, defaultFont, rawText, defaultBold);
            if (ExtractHyperlinkTarget(rawText) is { } extracted)
            {
                pendingHyperlinkTarget = NormalizeHyperlinkTarget(extracted);
            }

            var text = PdfText.Sanitize(rawText);
            var runHyperlinkTarget = NormalizeHyperlinkTarget(style.HyperlinkAddress);
            if (runHyperlinkTarget is null && style.IsHyperlink)
            {
                runHyperlinkTarget = pendingHyperlinkTarget;
            }

            var position = 0;
            while (position < text.Length)
            {
                _ct.ThrowIfCancellationRequested();
                if (cursorY < BottomMargin + currentLineHeight)
                {
                    cursorY = ShowPage(pageHeight);
                    x = startX;
                    currentLineHeight = baseLineHeight;
                }

                var newlineAt = text.IndexOf('\n', position);
                var chunkEnd = newlineAt != -1 ? newlineAt : text.Length;
                var chunk = text[position..chunkEnd];

                while (chunk.Length > 0)
                {
                    _ct.ThrowIfCancellationRequested();
                    // The reference writer only breaks pages at explicit newlines, so a long wrapped
                    // paragraph ran off the bottom of the page; break at the start of every line.
                    if (x == startX && cursorY < BottomMargin + currentLineHeight)
                    {
                        cursorY = ShowPage(pageHeight);
                        currentLineHeight = baseLineHeight;
                    }

                    currentLineHeight = Math.Max(currentLineHeight, fontSize * 1.35);
                    var availableWidth = Math.Max(pageWidth - RightMargin - x, fontSize);
                    var pieceLength = FitPrefixLength(chunk, fontName, fontSize, availableWidth);
                    var piece = chunk[..pieceLength];
                    if (x == startX && PyText.IsSpace(piece))
                    {
                        piece = string.Empty;
                        pieceLength = chunk.Length;
                    }

                    if (piece.Length > 0)
                    {
                        var pieceStartX = x;
                        x = DrawSegment(x, cursorY, piece, style, defaultFont, defaultSize, defaultBold);
                        if (runHyperlinkTarget is not null)
                        {
                            _pdf.LinkUrl(runHyperlinkTarget, pieceStartX, cursorY - (fontSize * 0.25), x, cursorY + (fontSize * 0.95));
                        }

                        renderedAny = true;
                    }

                    chunk = chunk[pieceLength..];
                    if (chunk.Length > 0)
                    {
                        cursorY -= currentLineHeight;
                        x = startX;
                        currentLineHeight = baseLineHeight;
                    }
                }

                if (newlineAt == -1)
                {
                    position = text.Length;
                }
                else
                {
                    position = newlineAt + 1;
                    cursorY -= currentLineHeight;
                    x = startX;
                    currentLineHeight = baseLineHeight;
                }
            }

            if (text.Length > 0 && runHyperlinkTarget is not null)
            {
                pendingHyperlinkTarget = null;
            }
        }

        if (renderedAny && finishWithNewline)
        {
            cursorY -= currentLineHeight;
            x = startX;
        }

        return (x, cursorY);
    }

    private double RenderRichText(RichText richText, double cursorY, double pageWidth, double pageHeight, double startX, string defaultFont, double defaultSize, bool defaultBold = false)
    {
        var runs = IterRuns(richText);
        if (runs.Count == 0)
        {
            return cursorY;
        }

        var tagOffset = RenderNoteTags(TagsForNode(richText), startX, cursorY);
        (_, cursorY) = RenderRuns(runs, startX + tagOffset, cursorY, pageWidth, pageHeight, defaultFont, defaultSize, defaultBold);
        return cursorY;
    }

    private double RenderInlineRichTexts(List<RichText> richTexts, double cursorY, double pageWidth, double pageHeight, double startX, double gap, string defaultFont, double defaultSize)
    {
        var x = startX;
        var rendered = false;
        foreach (var richText in richTexts)
        {
            var runs = IterRuns(richText);
            if (runs.Count == 0)
            {
                continue;
            }

            (x, cursorY) = RenderRuns(runs, x, cursorY, pageWidth, pageHeight, defaultFont, defaultSize, finishWithNewline: false);
            x += gap;
            rendered = true;
        }

        if (rendered)
        {
            cursorY -= defaultSize * 1.6;
        }

        return cursorY;
    }

    // ------------------------------------------------------------------ note tags

    internal static List<NoteTag> DedupeTags(IEnumerable<NoteTag>? tags)
    {
        var seen = new HashSet<(int?, string?, uint?, uint?, DateTime?, DateTime?)>();
        var result = new List<NoteTag>();
        foreach (var tag in tags ?? [])
        {
            if (seen.Add((tag.Icon, tag.Label, tag.FontColor, tag.Highlight, tag.CreationTime, tag.CompletedTime)))
            {
                result.Add(tag);
            }
        }

        return result;
    }

    private static List<NoteTag> TagsForNode(Node node) => node switch
    {
        RichText richText => DedupeTags(richText.Tags),
        Image image => DedupeTags(image.Tags),
        Table table => DedupeTags(table.Tags),
        AttachedFile file => DedupeTags(file.Tags),
        _ => [],
    };

    private static double TagBlockWidth(List<NoteTag> tags)
    {
        var deduped = DedupeTags(tags);
        return deduped.Count == 0 ? 0.0 : (deduped.Count * TagIconSize) + (deduped.Count * TagIconGap);
    }

    /// <summary>Draws the tags right to left from <paramref name="x"/> (the last tag first) and returns the width used.</summary>
    private double RenderNoteTags(List<NoteTag> tags, double x, double baselineY) => RenderNoteTags(_pdf, tags, x, baselineY);

    private static double RenderNoteTags(IPdfCanvas pdf, List<NoteTag> tags, double x, double baselineY)
    {
        var deduped = DedupeTags(tags);
        if (deduped.Count == 0)
        {
            return 0.0;
        }

        var consumed = 0.0;
        for (var index = deduped.Count - 1; index >= 0; index--)
        {
            var shape = deduped[index].Icon;
            consumed += DrawTagGlyph(pdf, x + consumed, baselineY, shape, TagColor(shape), TagIconSize, deduped[index].Status == TagStatus.Completed);
            consumed += TagIconGap;
        }

        return consumed;
    }

    private static double CenteredTagBaseline(double topY, double blockHeight, double iconSize) => topY - (blockHeight / 2.0) - (iconSize * 0.42);

    private static double DrawTagGlyph(IPdfCanvas pdf, double x, double baselineY, int? shape, (double R, double G, double B) color, double iconSize, bool completed = false)
    {
        var glyphY = baselineY - (iconSize * 0.08);
        pdf.SetStrokeColor(color.R, color.G, color.B);
        pdf.SetFillColor(color.R, color.G, color.B);
        var s = shape ?? int.MinValue;
        if (CheckboxShapes.Contains(s))
        {
            pdf.Rect(x, glyphY - (iconSize * 0.10), iconSize, iconSize, stroke: true, fill: false);
            if (completed)
            {
                // A ticked box: a check mark across the whole box, over the shape's own decoration.
                pdf.Line(x + (iconSize * 0.15), glyphY + (iconSize * 0.38), x + (iconSize * 0.42), glyphY + (iconSize * 0.12));
                pdf.Line(x + (iconSize * 0.42), glyphY + (iconSize * 0.12), x + (iconSize * 0.90), glyphY + (iconSize * 0.80));
            }

            var innerX = x + (iconSize * 0.08);
            var innerY = glyphY;
            var innerSize = iconSize * 0.84;
            if (s is 4 or 5 or 6)
            {
                DrawStar(pdf, innerX, innerY, innerSize);
            }
            else if (s is 7 or 8 or 9)
            {
                DrawQuestion(pdf, innerX, innerY, innerSize);
            }
            else if (s is 10 or 11 or 12)
            {
                DrawArrow(pdf, innerX, innerY, innerSize, "right");
            }
            else if (s is 89 or 90 or 91 or 92 or 93 or 97 or 98 or 99)
            {
                DrawFlag(pdf, innerX, innerY, innerSize);
            }
            else if (s is 94 or 95 or 96)
            {
                DrawContact(pdf, innerX, innerY, innerSize);
            }
            else
            {
                DrawCheckmark(pdf, innerX, innerY, innerSize);
            }

            return iconSize;
        }

        if (StarShapes.Contains(s))
        {
            DrawStar(pdf, x, glyphY, iconSize);
        }
        else if (QuestionShapes.Contains(s))
        {
            DrawQuestion(pdf, x, glyphY, iconSize);
        }
        else if (ExclamationShapes.Contains(s))
        {
            pdf.Line(x + (iconSize * 0.50), glyphY + (iconSize * 0.22), x + (iconSize * 0.50), glyphY + (iconSize * 0.78));
            pdf.Rect(x + (iconSize * 0.45), glyphY + (iconSize * 0.06), iconSize * 0.10, iconSize * 0.10, stroke: true, fill: true);
        }
        else if (ArrowRightShapes.Contains(s))
        {
            DrawArrow(pdf, x, glyphY, iconSize, "right");
        }
        else if (ArrowLeftShapes.Contains(s))
        {
            DrawArrow(pdf, x, glyphY, iconSize, "left");
        }
        else if (ArrowUpShapes.Contains(s))
        {
            DrawArrow(pdf, x, glyphY, iconSize, "up");
        }
        else if (ArrowDownShapes.Contains(s))
        {
            DrawArrow(pdf, x, glyphY, iconSize, "down");
        }
        else if (FlagShapes.Contains(s))
        {
            DrawFlag(pdf, x, glyphY, iconSize);
        }
        else if (CheckmarkShapes.Contains(s))
        {
            DrawCheckmark(pdf, x, glyphY, iconSize);
        }
        else if (ContactShapes.Contains(s))
        {
            DrawContact(pdf, x, glyphY, iconSize);
        }
        else if (MusicShapes.Contains(s))
        {
            DrawMusic(pdf, x, glyphY, iconSize);
        }
        else if (CalendarShapes.Contains(s))
        {
            DrawCalendar(pdf, x, glyphY, iconSize);
        }
        else if (TimeShapes.Contains(s))
        {
            DrawClock(pdf, x, glyphY, iconSize);
        }
        else
        {
            DrawGenericBadge(pdf, x, glyphY, iconSize, shape);
        }

        return iconSize;
    }

    private static void L(IPdfCanvas pdf, double x, double y, double size, double ax, double ay, double bx, double by) =>
        pdf.Line(x + (size * ax), y + (size * ay), x + (size * bx), y + (size * by));

    private static void DrawCheckmark(IPdfCanvas pdf, double x, double y, double size)
    {
        L(pdf, x, y, size, 0.18, 0.35, 0.40, 0.12);
        L(pdf, x, y, size, 0.40, 0.12, 0.78, 0.72);
    }

    private static void DrawStar(IPdfCanvas pdf, double x, double y, double size)
    {
        (double, double)[] points =
        [
            (0.50, 0.84), (0.62, 0.56), (0.90, 0.56), (0.68, 0.38), (0.76, 0.10),
            (0.50, 0.26), (0.24, 0.10), (0.32, 0.38), (0.10, 0.56), (0.38, 0.56),
        ];
        pdf.Polygon([.. points.Select(p => (x + (p.Item1 * size), y + (p.Item2 * size)))], stroke: false, fill: true);
    }

    private static void DrawArrow(IPdfCanvas pdf, double x, double y, double size, string direction)
    {
        switch (direction)
        {
            case "left":
                L(pdf, x, y, size, 0.78, 0.48, 0.22, 0.48);
                L(pdf, x, y, size, 0.22, 0.48, 0.44, 0.70);
                L(pdf, x, y, size, 0.22, 0.48, 0.44, 0.26);
                return;
            case "up":
                L(pdf, x, y, size, 0.50, 0.18, 0.50, 0.76);
                L(pdf, x, y, size, 0.50, 0.18, 0.28, 0.40);
                L(pdf, x, y, size, 0.50, 0.18, 0.72, 0.40);
                return;
            case "down":
                L(pdf, x, y, size, 0.50, 0.18, 0.50, 0.76);
                L(pdf, x, y, size, 0.50, 0.76, 0.28, 0.54);
                L(pdf, x, y, size, 0.50, 0.76, 0.72, 0.54);
                return;
            default:
                L(pdf, x, y, size, 0.18, 0.48, 0.78, 0.48);
                L(pdf, x, y, size, 0.78, 0.48, 0.56, 0.70);
                L(pdf, x, y, size, 0.78, 0.48, 0.56, 0.26);
                return;
        }
    }

    private static void DrawQuestion(IPdfCanvas pdf, double x, double y, double size)
    {
        L(pdf, x, y, size, 0.26, 0.66, 0.40, 0.82);
        L(pdf, x, y, size, 0.40, 0.82, 0.62, 0.82);
        L(pdf, x, y, size, 0.62, 0.82, 0.74, 0.64);
        L(pdf, x, y, size, 0.74, 0.64, 0.50, 0.46);
        L(pdf, x, y, size, 0.50, 0.46, 0.50, 0.28);
        pdf.Rect(x + (size * 0.45), y + (size * 0.08), size * 0.10, size * 0.10, stroke: true, fill: true);
    }

    private static void DrawFlag(IPdfCanvas pdf, double x, double y, double size)
    {
        L(pdf, x, y, size, 0.24, 0.12, 0.24, 0.82);
        L(pdf, x, y, size, 0.24, 0.78, 0.72, 0.66);
        L(pdf, x, y, size, 0.72, 0.66, 0.24, 0.52);
    }

    private static void DrawContact(IPdfCanvas pdf, double x, double y, double size)
    {
        pdf.Circle(x + (size * 0.50), y + (size * 0.68), size * 0.16, stroke: true, fill: false);
        L(pdf, x, y, size, 0.24, 0.26, 0.76, 0.26);
        L(pdf, x, y, size, 0.24, 0.26, 0.36, 0.44);
        L(pdf, x, y, size, 0.76, 0.26, 0.64, 0.44);
    }

    private static void DrawMusic(IPdfCanvas pdf, double x, double y, double size)
    {
        L(pdf, x, y, size, 0.58, 0.78, 0.58, 0.30);
        L(pdf, x, y, size, 0.58, 0.78, 0.80, 0.72);
        pdf.Rect(x + (size * 0.22), y + (size * 0.10), size * 0.18, size * 0.16, stroke: true, fill: false);
        pdf.Rect(x + (size * 0.52), y + (size * 0.16), size * 0.18, size * 0.16, stroke: true, fill: false);
    }

    private static void DrawCalendar(IPdfCanvas pdf, double x, double y, double size)
    {
        pdf.Rect(x + (size * 0.14), y + (size * 0.14), size * 0.72, size * 0.66, stroke: true, fill: false);
        L(pdf, x, y, size, 0.14, 0.62, 0.86, 0.62);
        L(pdf, x, y, size, 0.30, 0.86, 0.30, 0.68);
        L(pdf, x, y, size, 0.70, 0.86, 0.70, 0.68);
    }

    private static void DrawClock(IPdfCanvas pdf, double x, double y, double size)
    {
        pdf.Circle(x + (size * 0.50), y + (size * 0.50), size * 0.34, stroke: true, fill: false);
        L(pdf, x, y, size, 0.50, 0.50, 0.50, 0.66);
        L(pdf, x, y, size, 0.50, 0.50, 0.64, 0.40);
    }

    private static void DrawGenericBadge(IPdfCanvas pdf, double x, double y, double size, int? shape)
    {
        pdf.Rect(x + (size * 0.12), y + (size * 0.12), size * 0.76, size * 0.76, stroke: true, fill: false);
        var remainder = shape is { } s ? ((s % 3) + 3) % 3 : -1;
        if (remainder == 0)
        {
            L(pdf, x, y, size, 0.24, 0.24, 0.76, 0.76);
        }
        else if (remainder == 1)
        {
            L(pdf, x, y, size, 0.24, 0.50, 0.76, 0.50);
            L(pdf, x, y, size, 0.50, 0.24, 0.50, 0.76);
        }
        else
        {
            L(pdf, x, y, size, 0.24, 0.76, 0.76, 0.24);
        }
    }

    // ------------------------------------------------------------------ numbered lists

    private static string NumberListPayload(string? format)
    {
        if (string.IsNullOrEmpty(format))
        {
            return string.Empty;
        }

        // A leading length character (the count of the characters after it) is not part of the text.
        return format.Length > 1 && format[0] == format.Length - 1 ? format[1..] : format;
    }

    internal static string ToRoman(int value)
    {
        if (value <= 0)
        {
            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        (int Amount, string Numeral)[] numerals =
        [
            (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"),
            (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I"),
        ];
        var remaining = value;
        var builder = new System.Text.StringBuilder();
        foreach (var (amount, numeral) in numerals)
        {
            while (remaining >= amount)
            {
                builder.Append(numeral);
                remaining -= amount;
            }
        }

        return builder.ToString();
    }

    internal static string ToAlpha(int value)
    {
        if (value <= 0)
        {
            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var chars = new List<char>();
        var current = value;
        while (current > 0)
        {
            current--;
            chars.Add((char)('a' + (current % 26)));
            current /= 26;
        }

        chars.Reverse();
        return new string([.. chars]);
    }

    private static string FormatListNumber(int value, int numberFormat) => numberFormat switch
    {
        4 => ToAlpha(value),
        3 => ToAlpha(value).ToUpperInvariant(),
        2 => ToRoman(value).ToLowerInvariant(),
        1 => ToRoman(value),
        _ => value.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// The marker text of a list item: the format with its U+FFFD placeholder replaced by the item
    /// number (counted per depth and format, or the list's restart value), in the style the
    /// placeholder's next character selects.
    /// </summary>
    internal static string ResolveListMarkerText(NumberList numberList, int depth, Dictionary<(int, string), int> listState)
    {
        var payload = NumberListPayload(numberList.Format);
        if (payload.Length == 0)
        {
            return string.Empty;
        }

        var markerIndex = payload.IndexOf('�', StringComparison.Ordinal);
        if (markerIndex == -1)
        {
            return payload;
        }

        var numberFormat = markerIndex + 1 < payload.Length ? payload[markerIndex + 1] : '\0';
        var key = (depth, payload);
        int current;
        if (numberList.Restart is { } restart && restart > 0)
        {
            current = restart;
        }
        else
        {
            current = listState.GetValueOrDefault(key) + 1;
        }

        listState[key] = current;
        var prefix = payload[..markerIndex];
        var suffix = markerIndex + 2 < payload.Length ? payload[(markerIndex + 2)..] : string.Empty;
        return prefix + FormatListNumber(current, numberFormat) + suffix;
    }

    // ------------------------------------------------------------------ outlines

    private (double CursorY, double TextStartX) RenderOutlineListItem(
        OutlineElement outlineElement,
        RichText richText,
        double cursorY,
        double pageWidth,
        double pageHeight,
        double startX,
        int depth,
        Dictionary<(int, string), int> listState)
    {
        var itemStartX = startX + (depth * OutlineLevelIndent);
        var tags = TagsForNode(richText);
        var tagOffset = TagBlockWidth(tags);
        if (tagOffset != 0)
        {
            RenderNoteTags(tags, itemStartX, cursorY);
        }

        var markerText = outlineElement.NumberList is { } numberList ? ResolveListMarkerText(numberList, depth, listState) : string.Empty;
        var markerWidth = 0.0;
        var markerX = itemStartX + tagOffset;
        if (markerText.Length > 0)
        {
            // The reference writer always uses a standard font here, which cannot draw bullets outside
            // Latin-1; choosing by the marker text keeps such bullets visible.
            var markerFont = _fonts.Register("sans", false, false, FontResolver.RequiresUnicodeFont(markerText));
            markerWidth = Math.Max(_pdf.StringWidth(markerText, markerFont, 11.0), OutlineMarkerMinWidth);
            _pdf.SetFont(markerFont, 11.0);
            _pdf.SetFillColor(0, 0, 0);
            _pdf.DrawString(markerX, cursorY, markerText);
        }

        var textStartX = markerX + (markerWidth != 0 ? markerWidth + OutlineMarkerGap : 0.0);
        (_, cursorY) = RenderRuns(IterRuns(richText), textStartX, cursorY, pageWidth, pageHeight, DefaultFont, 11.0);
        return (cursorY, textStartX);
    }

    private double RenderOutlineElement(
        OutlineElement outlineElement,
        double cursorY,
        double pageWidth,
        double pageHeight,
        double startX,
        double? maxWidth,
        int depth,
        Dictionary<(int, string), int> listState)
    {
        var textStartX = startX + ((depth + 1) * OutlineLevelIndent);
        var renderedRichText = false;

        foreach (var child in outlineElement)
        {
            if (child is RichText richText)
            {
                if (PdfText.PlainTextFromRichText(richText).Length == 0)
                {
                    continue;
                }

                (cursorY, textStartX) = RenderOutlineListItem(outlineElement, richText, cursorY, pageWidth, pageHeight, startX, depth, listState);
                renderedRichText = true;
                continue;
            }

            var childStartX = renderedRichText ? textStartX : startX + (depth * OutlineLevelIndent);
            double? childMaxWidth = maxWidth is { } mw ? Math.Max(mw - (childStartX - startX), 1.0) : null;
            var nextDepth = child is OutlineElement ? depth + 1 : depth;
            cursorY = RenderOutlineContent(
                child,
                cursorY,
                pageWidth,
                pageHeight,
                nextDepth == depth ? childStartX : startX,
                nextDepth == depth ? childMaxWidth : maxWidth,
                listState,
                nextDepth);
        }

        return cursorY;
    }

    private double RenderOutlineContent(
        Node node,
        double cursorY,
        double pageWidth,
        double pageHeight,
        double startX,
        double? maxWidth,
        Dictionary<(int, string), int> listState,
        int outlineDepth)
    {
        _ct.ThrowIfCancellationRequested();
        switch (node)
        {
            case OutlineElement outlineElement:
                return RenderOutlineElement(outlineElement, cursorY, pageWidth, pageHeight, startX, maxWidth, outlineDepth, listState);
            case RichText richText:
            {
                if (HasAncestor<Table>(richText) || PdfText.PlainTextFromRichText(richText).Length == 0)
                {
                    return cursorY;
                }

                var runs = IterRuns(richText);
                var alignment = richText.Alignment;
                var tags = TagsForNode(richText);
                var tagOffset = TagBlockWidth(tags);
                var text = string.Concat(runs.Select(run => PdfText.Sanitize(run.Text)));
                var renderStartX = startX + tagOffset;
                double? renderMaxWidth = maxWidth is { } mw && tagOffset != 0 ? Math.Max(mw - tagOffset, 1.0) : maxWidth;
                if (tagOffset != 0)
                {
                    RenderNoteTags(tags, startX, cursorY);
                }

                var containerWidth = ContainerWidth(pageWidth, renderStartX, renderMaxWidth);
                if (tagOffset == 0 && alignment is HorizontalAlignment.Center or HorizontalAlignment.Right && !text.Contains('\n', StringComparison.Ordinal))
                {
                    return RenderAlignedSingleLineRichText(runs, cursorY, renderStartX, containerWidth, alignment.Value, DefaultFont, 11.0);
                }

                (_, cursorY) = RenderRuns(runs, renderStartX, cursorY, pageWidth, pageHeight, DefaultFont, 11.0);
                return cursorY;
            }

            case Table table:
                return RenderTable(table, cursorY, pageWidth, pageHeight, startX, maxWidth);
            case Image image:
                return RenderImage(image, cursorY, pageWidth, pageHeight, startX, maxWidth);
            case AttachedFile attachedFile:
                return RenderAttachedFile(attachedFile, cursorY, pageWidth, pageHeight, startX, maxWidth);
        }

        foreach (var child in node.TraversalChildren)
        {
            cursorY = RenderOutlineContent(child, cursorY, pageWidth, pageHeight, startX, maxWidth, listState, outlineDepth);
        }

        return cursorY;
    }

    private static double ContainerWidth(double pageWidth, double startX, double? maxWidth = null)
    {
        var availableWidth = Math.Max(pageWidth - RightMargin - startX, 1.0);
        if (maxWidth is not { } width || width <= 0)
        {
            return availableWidth;
        }

        return Math.Max(Math.Min(width, availableWidth), 1.0);
    }

    private static double AlignedX(double startX, double contentWidth, double containerWidth, HorizontalAlignment? alignment) => alignment switch
    {
        HorizontalAlignment.Center => startX + Math.Max((containerWidth - contentWidth) / 2.0, 0.0),
        HorizontalAlignment.Right => startX + Math.Max(containerWidth - contentWidth, 0.0),
        _ => startX,
    };

    private double MeasureRunsWidth(List<TextRun> runs, string defaultFont, double defaultSize, bool defaultBold = false)
    {
        var total = 0.0;
        foreach (var run in runs)
        {
            var text = PdfText.Sanitize(run.Text);
            if (text.Length == 0)
            {
                continue;
            }

            var fontSize = FontSizeOf(run.Style, defaultSize);
            total += _pdf.StringWidth(text, FontNameForStyle(run.Style, defaultFont, text, defaultBold), fontSize);
        }

        return total;
    }

    private double RenderAlignedSingleLineRichText(List<TextRun> runs, double cursorY, double startX, double containerWidth, HorizontalAlignment alignment, string defaultFont, double defaultSize)
    {
        var lineWidth = MeasureRunsWidth(runs, defaultFont, defaultSize);
        var x = AlignedX(startX, lineWidth, containerWidth, alignment);
        foreach (var run in runs)
        {
            var text = PdfText.Sanitize(run.Text);
            if (text.Length > 0)
            {
                x = DrawSegment(x, cursorY, text, run.Style, defaultFont, defaultSize);
            }
        }

        return cursorY - (defaultSize * 1.35);
    }

    // ------------------------------------------------------------------ images and attachments

    private static (double Width, double Height) ScaleDimensionsToFit(double width, double height, double maxWidth, double maxHeight)
    {
        if (width <= 0 || height <= 0)
        {
            return (width, height);
        }

        var widthScale = maxWidth > 0 ? maxWidth / width : 1.0;
        var heightScale = maxHeight > 0 ? maxHeight / height : 1.0;
        var scale = Math.Min(Math.Min(widthScale, heightScale), 1.0);
        return (width * scale, height * scale);
    }

    /// <summary>
    /// The drawn size of an image in points. The stored width and height are scaled by 28.35 points
    /// per unit, as the reference writer does; with neither, the pixel size at 0.75 points per pixel.
    /// </summary>
    private static (double Width, double Height) ResolveImageDrawSize(Image image, PdfImageSource source, double pageWidth, double cursorY, double startX, double? maxWidth)
    {
        double pixelWidth = source.PixelWidth;
        double pixelHeight = source.PixelHeight;
        var aspectRatio = pixelWidth != 0 && pixelHeight != 0 ? pixelWidth / pixelHeight : 1.0;
        var widthCm = Math.Max(image.Width ?? 0.0, 0.0);
        var heightCm = Math.Max(image.Height ?? 0.0, 0.0);
        double drawWidth, drawHeight;
        if (widthCm > 0 && heightCm > 0)
        {
            drawWidth = widthCm * PointsPerImageCm;
            drawHeight = heightCm * PointsPerImageCm;
        }
        else if (widthCm > 0)
        {
            drawWidth = widthCm * PointsPerImageCm;
            drawHeight = aspectRatio > 0 ? drawWidth / aspectRatio : drawWidth;
        }
        else if (heightCm > 0)
        {
            drawHeight = heightCm * PointsPerImageCm;
            drawWidth = drawHeight * aspectRatio;
        }
        else
        {
            drawWidth = pixelWidth != 0 ? pixelWidth * 0.75 : 220.0;
            drawHeight = pixelHeight != 0 ? pixelHeight * 0.75 : 160.0;
        }

        var availableWidth = ContainerWidth(pageWidth, startX, maxWidth);
        var maxHeight = Math.Max(cursorY - BottomMargin, 1.0);
        return ScaleDimensionsToFit(drawWidth, drawHeight, availableWidth, maxHeight);
    }

    private double RenderImage(Image image, double cursorY, double pageWidth, double pageHeight, double startX, double? maxWidth)
    {
        if (image.Bytes.Length == 0)
        {
            return cursorY;
        }

        if (cursorY < 180)
        {
            cursorY = ShowPage(pageHeight);
        }

        var imageTags = TagsForNode(image);
        var tagOffset = TagBlockWidth(imageTags);
        var tagX = startX;
        if (tagOffset != 0)
        {
            startX += tagOffset;
            if (maxWidth is { } mw)
            {
                maxWidth = Math.Max(mw - tagOffset, 1.0);
            }
        }

        var source = _pdf.LoadImage(image.Bytes);
        if (source is null)
        {
            // Not a picture format this writer can embed: show its name instead.
            _pdf.SetFont(_fonts.Register("sans", false, true), 10);
            _pdf.DrawString(startX, cursorY, image.FileName ?? "[image]");
            return cursorY - 14;
        }

        var (drawWidth, drawHeight) = ResolveImageDrawSize(image, source, pageWidth, cursorY, startX, maxWidth);
        var containerWidth = ContainerWidth(pageWidth, startX, maxWidth);
        var drawX = AlignedX(startX, drawWidth, containerWidth, image.Alignment);
        var drawY = Math.Max(BottomMargin, cursorY - drawHeight);
        _pdf.DrawImage(source, drawX, drawY, drawWidth, drawHeight, preserveAspectRatio: true);
        if (tagOffset != 0)
        {
            RenderNoteTags(imageTags, tagX, CenteredTagBaseline(cursorY, drawHeight, TagIconSize));
        }

        if (!string.IsNullOrEmpty(image.HyperlinkUrl))
        {
            _pdf.LinkUrl(image.HyperlinkUrl, drawX, drawY, drawX + drawWidth, drawY + drawHeight);
        }

        return cursorY - drawHeight - 12;
    }

    private static string AttachmentDisplayName(AttachedFile attachedFile)
    {
        var name = PyText.Strip(attachedFile.FileName ?? string.Empty);
        return name.Length > 0 ? name : "attachment.bin";
    }

    private double RenderAttachedFile(AttachedFile attachedFile, double cursorY, double pageWidth, double pageHeight, double startX, double? maxWidth)
    {
        if (cursorY < 96)
        {
            cursorY = ShowPage(pageHeight);
        }

        var tags = TagsForNode(attachedFile);
        var tagOffset = TagBlockWidth(tags);
        if (tagOffset != 0)
        {
            RenderNoteTags(tags, startX, cursorY);
        }

        var run = new TextRun(AttachmentDisplayName(attachedFile), new TextStyle { FontSize = 11.0 });
        (_, cursorY) = RenderRuns([run], startX + tagOffset, cursorY, pageWidth, pageHeight, DefaultFont, 11.0);
        return cursorY;
    }

    // ------------------------------------------------------------------ tables

    private static IEnumerable<Node> RenderableDescendants(Node node)
    {
        foreach (var child in node.TraversalChildren)
        {
            if (child is AttachedFile or Image or RichText)
            {
                yield return child;
                continue;
            }

            foreach (var descendant in RenderableDescendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static HorizontalAlignment? TableCellAlignment(TableCell cell)
    {
        foreach (var node in RenderableDescendants(cell))
        {
            if (node is Image { Alignment: { } imageAlignment })
            {
                return imageAlignment;
            }

            if (node is RichText { Alignment: { } textAlignment })
            {
                return textAlignment;
            }
        }

        return null;
    }

    private static int ParagraphAlignmentValue(HorizontalAlignment? alignment) => alignment switch
    {
        HorizontalAlignment.Center => 1,
        HorizontalAlignment.Right => 2,
        _ => 0,
    };

    private static string FlowableAlignmentValue(HorizontalAlignment? alignment) => alignment switch
    {
        HorizontalAlignment.Center => "CENTER",
        HorizontalAlignment.Right => "RIGHT",
        _ => "LEFT",
    };

    private Paragraph ParagraphForTableText(string text, HorizontalAlignment? defaultAlignment = null, double fontSize = 11.0, double leading = 13.0) =>
        new(text, _fonts.Register("sans", false, false, FontResolver.RequiresUnicodeFont(text)), fontSize, leading, ParagraphAlignmentValue(defaultAlignment));

    private double TableOutlinePrefixWidth(List<NoteTag> tags, string markerText, double fontSize)
    {
        var width = TagBlockWidth(tags);
        if (markerText.Length > 0)
        {
            var markerFont = _fonts.Register("sans", false, false, FontResolver.RequiresUnicodeFont(markerText));
            var markerWidth = _pdf.StringWidth(markerText, markerFont, fontSize);
            width += Math.Max(markerWidth, OutlineMarkerMinWidth - 2.0) + 3.0;
        }

        return width;
    }

    private Flowable TableOutlineItemFlowable(string text, List<NoteTag> tags, string markerText, double maxWidth, double indent, HorizontalAlignment? defaultAlignment, double fontSize, double leading)
    {
        var prefixWidth = indent + TableOutlinePrefixWidth(tags, markerText, fontSize);
        var prefixHeight = Math.Max(leading, TagIconSize + 2.0);
        var markerFont = markerText.Length > 0 ? _fonts.Register("sans", false, false, FontResolver.RequiresUnicodeFont(markerText)) : null;
        var prefix = new CustomFlowable(prefixWidth, prefixHeight, (canvas, x, y) =>
        {
            var drawX = x + indent;
            var baseline = y + Math.Max(fontSize * 0.15, TagIconSize * 0.18);
            if (tags.Count > 0)
            {
                RenderNoteTags(canvas, tags, drawX, baseline);
                drawX += TagBlockWidth(tags);
            }

            if (markerFont is not null)
            {
                canvas.SetFont(markerFont, fontSize);
                canvas.SetFillColor(0, 0, 0);
                canvas.DrawString(drawX, baseline, markerText);
            }
        });

        var textWidth = Math.Max(maxWidth - prefixWidth, 1.0);
        var paragraph = ParagraphForTableText(text, defaultAlignment, fontSize, leading);
        return new TableFlowable(
            [[[prefix], [paragraph]]],
            [prefixWidth, textWidth],
            new TableStyle(0, 0, 0, 0, "TOP"));
    }

    private List<Flowable> TableOutlineFlowables(Node node, double maxWidth, HorizontalAlignment? defaultAlignment, int depth, Dictionary<(int, string), int> listState)
    {
        var flowables = new List<Flowable>();
        foreach (var child in node.TraversalChildren)
        {
            switch (child)
            {
                case OutlineElement:
                {
                    foreach (var childFlowable in TableOutlineFlowables(child, maxWidth, defaultAlignment, depth + 1, listState))
                    {
                        if (flowables.Count > 0 && childFlowable is not Spacer)
                        {
                            flowables.Add(new Spacer(1, 2));
                        }

                        flowables.Add(childFlowable);
                    }

                    continue;
                }

                case RichText richText:
                {
                    var text = PdfText.PlainTextFromRichText(richText);
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    var tags = TagsForNode(richText);
                    var markerText = richText.ParentNode is OutlineElement { NumberList: { } numberList }
                        ? ResolveListMarkerText(numberList, Math.Max(depth - 1, 0), listState)
                        : string.Empty;
                    var item = TableOutlineItemFlowable(
                        text,
                        tags,
                        markerText,
                        Math.Max(maxWidth, 1.0),
                        Math.Max(depth - 1, 0) * (OutlineLevelIndent * 0.8),
                        defaultAlignment ?? richText.Alignment,
                        9.0,
                        11.0);
                    if (flowables.Count > 0)
                    {
                        flowables.Add(new Spacer(1, 2));
                    }

                    flowables.Add(item);
                    continue;
                }

                case Image image:
                {
                    if (ImageToFlowable(image, Math.Max(maxWidth, 1.0)) is { } flowable)
                    {
                        if (flowables.Count > 0)
                        {
                            flowables.Add(new Spacer(1, 4));
                        }

                        flowables.Add(flowable);
                    }

                    continue;
                }

                case AttachedFile attachedFile:
                {
                    var name = AttachmentDisplayName(attachedFile);
                    var nameParagraph = new Paragraph(name, _fonts.Register("sans", false, false, FontResolver.RequiresUnicodeFont(name)), 10, 12, ParagraphAlignmentValue(defaultAlignment));
                    foreach (var attachmentFlowable in (Flowable?[])[nameParagraph, AttachedFileTagFlowable(attachedFile, Math.Max(maxWidth, 1.0), defaultAlignment)])
                    {
                        if (attachmentFlowable is null)
                        {
                            continue;
                        }

                        if (flowables.Count > 0)
                        {
                            flowables.Add(new Spacer(1, 4));
                        }

                        flowables.Add(attachmentFlowable);
                    }

                    continue;
                }
            }

            foreach (var nested in TableOutlineFlowables(child, maxWidth, defaultAlignment, depth, listState))
            {
                if (flowables.Count > 0 && nested is not Spacer)
                {
                    flowables.Add(new Spacer(1, 2));
                }

                flowables.Add(nested);
            }
        }

        return flowables;
    }

    private Flowable? AttachedFileTagFlowable(AttachedFile attachedFile, double maxWidth, HorizontalAlignment? defaultAlignment)
    {
        var tags = TagsForNode(attachedFile);
        if (tags.Count == 0)
        {
            return null;
        }

        return new CustomFlowable(Math.Min(TagBlockWidth(tags), maxWidth), TagIconSize, (canvas, x, y) => RenderNoteTags(canvas, tags, x, y + (TagIconSize * 0.18)), clampHeight: true)
        {
            HAlign = FlowableAlignmentValue(defaultAlignment),
        };
    }

    private Flowable? ImageToFlowable(Image image, double maxWidth)
    {
        if (image.Bytes.Length == 0 || _pdf.LoadImage(image.Bytes) is not { } source)
        {
            return null;
        }

        var (drawWidth, drawHeight) = ResolveImageDrawSize(image, source, maxWidth, 10_000.0, 0.0, maxWidth);
        return new ImageFlowable(source, drawWidth, drawHeight) { HAlign = FlowableAlignmentValue(image.Alignment) };
    }

    private List<Flowable> TableCellFlowables(TableCell cell, double maxWidth, HorizontalAlignment? defaultAlignment)
    {
        var flowables = TableOutlineFlowables(cell, maxWidth, defaultAlignment, 0, []);
        if (flowables.Count > 0)
        {
            return flowables;
        }

        var fallback = new List<Flowable>();
        foreach (var node in RenderableDescendants(cell))
        {
            if (node is RichText { Text.Length: > 0 } richText)
            {
                fallback.Add(ParagraphForTableText(PdfText.PlainTextFromRichText(richText), defaultAlignment));
            }
        }

        return fallback.Count > 0 ? fallback : [new Spacer(1, 1)];
    }

    private static double[] ResolveTableColumnWidths(Table table, int columnCount, double availableWidth)
    {
        if (columnCount <= 0)
        {
            return [];
        }

        var rawWidths = table.Columns.Where(c => c.Width is { } w && double.IsFinite(w) && w > 0).Select(c => c.Width!.Value).ToList();
        if (rawWidths.Count >= columnCount)
        {
            var total = rawWidths.Take(columnCount).Sum();
            if (total > 0)
            {
                var scale = availableWidth / total;
                return [.. rawWidths.Take(columnCount).Select(width => Math.Max(width * scale, 36.0))];
            }
        }

        var equalWidth = Math.Max(availableWidth / columnCount, 36.0);
        return [.. Enumerable.Repeat(equalWidth, columnCount)];
    }

    private double RenderTable(Table table, double cursorY, double pageWidth, double pageHeight, double startX, double? maxWidth)
    {
        var renderedRows = table.Select(row => row is CompositeNode composite ? composite.ToList() : []).ToList();
        var columnCount = renderedRows.Count == 0 ? 0 : renderedRows.Max(row => row.Count);
        if (renderedRows.Count == 0 || columnCount == 0)
        {
            return cursorY;
        }

        var tableTags = TagsForNode(table);
        var tagOffset = TagBlockWidth(tableTags);
        var tagX = startX;
        if (tagOffset != 0)
        {
            startX += tagOffset;
            if (maxWidth is { } mw)
            {
                maxWidth = Math.Max(mw - tagOffset, 1.0);
            }
        }

        var availableWidth = Math.Max(ContainerWidth(pageWidth, startX, maxWidth), 72.0);
        var columnWidths = ResolveTableColumnWidths(table, columnCount, availableWidth);
        var alignments = new Dictionary<(int, int), string>();
        var rows = new List<List<List<Flowable>?>>();
        for (var rowIndex = 0; rowIndex < renderedRows.Count; rowIndex++)
        {
            var row = new List<List<Flowable>?>();
            for (var index = 0; index < renderedRows[rowIndex].Count; index++)
            {
                var cellWidth = index < columnWidths.Length ? columnWidths[index] : availableWidth / Math.Max(columnCount, 1);
                if (renderedRows[rowIndex][index] is TableCell cell)
                {
                    var cellAlignment = TableCellAlignment(cell);
                    row.Add(TableCellFlowables(cell, Math.Max(cellWidth - 12.0, 1.0), cellAlignment));
                    alignments[(index, rowIndex)] = FlowableAlignmentValue(cellAlignment);
                }
                else
                {
                    row.Add(null);
                }
            }

            while (row.Count < columnCount)
            {
                row.Add(null);
            }

            rows.Add(row);
        }

        _fonts.Register("sans", false, false);
        var style = new TableStyle(6, 6, 4, 4, "MIDDLE", 0.5, (0.55, 0.55, 0.55), Leading: 13, FontSize: 11);
        var pending = new List<Flowable> { new TableFlowable(rows, columnWidths, style, alignments) };
        var drewTableTags = false;
        var freshPage = false;
        while (pending.Count > 0)
        {
            _ct.ThrowIfCancellationRequested();
            var current = pending[0];
            pending.RemoveAt(0);
            var availableHeight = Math.Max(cursorY - BottomMargin, 1.0);
            var (_, heightUsed) = current.Wrap(_pdf, availableWidth, availableHeight);

            var fits = heightUsed <= availableHeight;
            if (!fits)
            {
                var parts = current.Split(_pdf, availableWidth, availableHeight);
                if (parts.Count > 0)
                {
                    pending.InsertRange(0, parts);
                    continue;
                }

                if (!freshPage)
                {
                    pending.Insert(0, current);
                    cursorY = ShowPage(pageHeight);
                    freshPage = true;
                    continue;
                }

                // Too tall for an empty page and not splittable: draw it anyway, as waiting for room
                // would never end.
            }

            current.Draw(_pdf, startX, cursorY - heightUsed);
            if (tagOffset != 0 && !drewTableTags)
            {
                RenderNoteTags(tableTags, tagX, CenteredTagBaseline(cursorY, heightUsed, TagIconSize));
                drewTableTags = true;
            }

            cursorY -= heightUsed;
            freshPage = false;
            if (pending.Count > 0)
            {
                cursorY = ShowPage(pageHeight);
                freshPage = true;
            }
        }

        return cursorY - 12;
    }
}
