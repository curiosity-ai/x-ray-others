using XRay.OneNote.Internal.Text;

namespace XRay.OneNote.Internal.Pdf;

/// <summary>
/// A block laid out top to bottom: it is measured with <see cref="Wrap"/> for an available size,
/// then drawn with its bottom-left corner at a position. The model follows the ReportLab platypus
/// flowables the reference writer builds its tables from.
/// </summary>
internal abstract class Flowable
{
    /// <summary>Horizontal alignment inside a frame: LEFT, CENTER or RIGHT.</summary>
    public string HAlign { get; set; } = "LEFT";

    public double Width { get; protected set; }

    public double Height { get; protected set; }

    public abstract (double Width, double Height) Wrap(IPdfCanvas canvas, double availableWidth, double availableHeight);

    /// <summary>Draws with the bottom-left corner at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public abstract void Draw(IPdfCanvas canvas, double x, double y);

    /// <summary>Splits into parts that fit <paramref name="availableHeight"/>; empty when it cannot be split.</summary>
    public virtual List<Flowable> Split(IPdfCanvas canvas, double availableWidth, double availableHeight) => [];
}

/// <summary>Empty space.</summary>
internal sealed class Spacer : Flowable
{
    public Spacer(double width, double height)
    {
        Width = width;
        Height = height;
    }

    public override (double Width, double Height) Wrap(IPdfCanvas canvas, double availableWidth, double availableHeight) => (Width, Height);

    public override void Draw(IPdfCanvas canvas, double x, double y)
    {
    }
}

/// <summary>Plain text in one font, wrapped to the available width; explicit newlines break lines.</summary>
internal sealed class Paragraph : Flowable
{
    private readonly string _text;
    private readonly string _fontName;
    private readonly double _fontSize;
    private readonly double _leading;
    private readonly int _alignment;
    private readonly double _leftIndent;
    private List<string> _lines = [];

    /// <summary>Creates a paragraph; <paramref name="alignment"/> is 0 left, 1 center, 2 right.</summary>
    public Paragraph(string text, string fontName, double fontSize, double leading, int alignment = 0, double leftIndent = 0)
    {
        _text = text;
        _fontName = fontName;
        _fontSize = fontSize;
        _leading = leading;
        _alignment = alignment;
        _leftIndent = Math.Max(leftIndent, 0);
    }

    public override (double Width, double Height) Wrap(IPdfCanvas canvas, double availableWidth, double availableHeight)
    {
        var lineWidth = Math.Max(availableWidth - _leftIndent, 1);
        _lines = [];
        foreach (var hardLine in _text.Split('\n'))
        {
            BreakLine(canvas, hardLine, lineWidth, _lines);
        }

        Width = availableWidth;
        Height = _lines.Count * _leading;
        return (Width, Height);
    }

    public override void Draw(IPdfCanvas canvas, double x, double y)
    {
        if (_lines.Count == 0)
        {
            return;
        }

        canvas.SetFont(_fontName, _fontSize);
        canvas.SetFillColor(0, 0, 0);
        var baseline = y + Height - _fontSize;
        foreach (var line in _lines)
        {
            var lineX = x + _leftIndent;
            if (_alignment != 0)
            {
                var free = Width - _leftIndent - canvas.StringWidth(line, _fontName, _fontSize);
                lineX += _alignment == 1 ? free / 2 : free;
            }

            if (line.Length > 0)
            {
                canvas.DrawString(lineX, baseline, line);
            }

            baseline -= _leading;
        }
    }

    /// <summary>Greedy word wrap; a word wider than the line is broken between characters.</summary>
    private void BreakLine(IPdfCanvas canvas, string text, double width, List<string> lines)
    {
        var words = text.Split(' ');
        var current = string.Empty;
        foreach (var word in words)
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (canvas.StringWidth(candidate, _fontName, _fontSize) <= width)
            {
                current = candidate;
                continue;
            }

            if (current.Length > 0)
            {
                lines.Add(current);
            }

            current = word;
            while (current.Length > 1 && canvas.StringWidth(current, _fontName, _fontSize) > width)
            {
                var fit = 1;
                while (fit < current.Length && canvas.StringWidth(current[..(fit + 1)], _fontName, _fontSize) <= width)
                {
                    fit++;
                }

                lines.Add(current[..fit]);
                current = current[fit..];
            }
        }

        lines.Add(current);
    }
}

/// <summary>A picture drawn stretched to its box.</summary>
internal sealed class ImageFlowable : Flowable
{
    private readonly PdfImageSource _image;

    public ImageFlowable(PdfImageSource image, double width, double height)
    {
        _image = image;
        Width = width;
        Height = height;
    }

    public override (double Width, double Height) Wrap(IPdfCanvas canvas, double availableWidth, double availableHeight) => (Width, Height);

    public override void Draw(IPdfCanvas canvas, double x, double y) => canvas.DrawImage(_image, x, y, Width, Height, preserveAspectRatio: false);
}

/// <summary>A flowable that draws itself through a callback.</summary>
internal sealed class CustomFlowable : Flowable
{
    private readonly Action<IPdfCanvas, double, double> _draw;
    private readonly bool _clampHeight;

    public CustomFlowable(double width, double height, Action<IPdfCanvas, double, double> draw, bool clampHeight = false)
    {
        Width = width;
        Height = height;
        _draw = draw;
        _clampHeight = clampHeight;
    }

    public override (double Width, double Height) Wrap(IPdfCanvas canvas, double availableWidth, double availableHeight)
    {
        var height = _clampHeight ? Math.Min(Height, availableHeight > 0 ? availableHeight : Height) : Height;
        return (Math.Min(Width, availableWidth), height);
    }

    public override void Draw(IPdfCanvas canvas, double x, double y) => _draw(canvas, x, y);
}

/// <summary>The cell styling a <see cref="TableFlowable"/> applies to every cell.</summary>
internal sealed record TableStyle(
    double LeftPadding,
    double RightPadding,
    double TopPadding,
    double BottomPadding,
    string VAlign,
    double? GridWidth = null,
    (double R, double G, double B) GridColor = default,
    string? FontName = null,
    double FontSize = 10,
    double Leading = 12);

/// <summary>
/// A grid of cells, each a list of flowables (or empty). Row heights fit the tallest cell; a table
/// that does not fit is split between rows.
/// </summary>
internal sealed class TableFlowable : Flowable
{
    private readonly List<List<List<Flowable>?>> _rows;
    private readonly double[] _columnWidths;
    private readonly TableStyle _style;
    private readonly Dictionary<(int Column, int Row), string> _alignments;
    private double[] _rowHeights = [];

    public TableFlowable(List<List<List<Flowable>?>> rows, double[] columnWidths, TableStyle style, Dictionary<(int, int), string>? alignments = null)
    {
        _rows = rows;
        _columnWidths = columnWidths;
        _style = style;
        _alignments = alignments ?? [];
    }

    public override (double Width, double Height) Wrap(IPdfCanvas canvas, double availableWidth, double availableHeight)
    {
        _rowHeights = new double[_rows.Count];
        for (var row = 0; row < _rows.Count; row++)
        {
            double tallest = 0;
            for (var column = 0; column < _rows[row].Count && column < _columnWidths.Length; column++)
            {
                var innerWidth = _columnWidths[column] - _style.LeftPadding - _style.RightPadding;
                var innerHeight = availableHeight - _style.TopPadding - _style.BottomPadding;
                tallest = Math.Max(tallest, CellHeight(canvas, _rows[row][column], innerWidth, innerHeight));
            }

            _rowHeights[row] = tallest + _style.TopPadding + _style.BottomPadding;
        }

        Width = _columnWidths.Sum();
        Height = _rowHeights.Sum();
        return (Width, Height);
    }

    public override List<Flowable> Split(IPdfCanvas canvas, double availableWidth, double availableHeight)
    {
        Wrap(canvas, availableWidth, availableHeight);
        double used = 0;
        var fitting = 0;
        while (fitting < _rowHeights.Length && used + _rowHeights[fitting] <= availableHeight)
        {
            used += _rowHeights[fitting];
            fitting++;
        }

        if (fitting == 0 || fitting == _rows.Count)
        {
            return [];
        }

        return
        [
            new TableFlowable(_rows[..fitting], _columnWidths, _style, Shift(0, fitting)),
            new TableFlowable(_rows[fitting..], _columnWidths, _style, Shift(fitting, _rows.Count)),
        ];
    }

    public override void Draw(IPdfCanvas canvas, double x, double y)
    {
        canvas.SaveState();
        var top = y + Height;
        var rowTop = top;
        for (var row = 0; row < _rows.Count; row++)
        {
            var rowBottom = rowTop - _rowHeights[row];
            var columnLeft = x;
            for (var column = 0; column < _columnWidths.Length; column++)
            {
                var cell = column < _rows[row].Count ? _rows[row][column] : null;
                if (cell is { Count: > 0 })
                {
                    DrawCell(canvas, cell, column, row, columnLeft, rowBottom, _columnWidths[column], _rowHeights[row]);
                }

                columnLeft += _columnWidths[column];
            }

            rowTop = rowBottom;
        }

        if (_style.GridWidth is { } gridWidth)
        {
            canvas.SetStrokeColor(_style.GridColor.R, _style.GridColor.G, _style.GridColor.B);
            canvas.SetLineWidth(gridWidth);
            var right = x + Width;
            var lineY = top;
            canvas.Line(x, lineY, right, lineY);
            foreach (var height in _rowHeights)
            {
                lineY -= height;
                canvas.Line(x, lineY, right, lineY);
            }

            var lineX = x;
            canvas.Line(lineX, top, lineX, y);
            foreach (var width in _columnWidths)
            {
                lineX += width;
                canvas.Line(lineX, top, lineX, y);
            }
        }

        canvas.RestoreState();
    }

    private double CellHeight(IPdfCanvas canvas, List<Flowable>? cell, double innerWidth, double innerHeight)
    {
        if (cell is null)
        {
            // An empty text cell still takes one line.
            return _style.Leading;
        }

        double total = 0;
        foreach (var flowable in cell)
        {
            total += flowable.Wrap(canvas, innerWidth, innerHeight).Height;
        }

        return total;
    }

    private void DrawCell(IPdfCanvas canvas, List<Flowable> cell, int column, int row, double left, double bottom, double width, double height)
    {
        var sizes = cell.Select(flowable => flowable.Wrap(canvas, width - _style.LeftPadding - _style.RightPadding, height - _style.TopPadding - _style.BottomPadding)).ToList();
        var content = sizes.Sum(size => size.Height);
        var y = _style.VAlign switch
        {
            "TOP" => bottom + height - _style.TopPadding,
            "BOTTOM" => bottom + _style.BottomPadding + content,
            _ => bottom + ((height + _style.BottomPadding - _style.TopPadding + content) / 2.0),
        };
        var just = _alignments.GetValueOrDefault((column, row), "LEFT");
        for (var index = 0; index < cell.Count; index++)
        {
            var (flowableWidth, flowableHeight) = sizes[index];
            var x = just switch
            {
                "RIGHT" => left + width - _style.RightPadding - flowableWidth,
                "CENTER" => left + ((width + _style.LeftPadding - _style.RightPadding - flowableWidth) / 2.0),
                _ => left + _style.LeftPadding,
            };
            y -= flowableHeight;
            cell[index].Draw(canvas, x, y);
        }
    }

    private Dictionary<(int, int), string> Shift(int start, int end)
    {
        var result = new Dictionary<(int, int), string>();
        foreach (var ((column, row), value) in _alignments)
        {
            if (row >= start && row < end)
            {
                result[(column, row - start)] = value;
            }
        }

        return result;
    }
}

/// <summary>Helpers shared by the exporter and the flowables.</summary>
internal static class PdfText
{
    /// <summary>The text of a run without OneNote's internal hyperlink field codes.</summary>
    public static string Sanitize(string text)
    {
        if (text.IndexOf('﷟') < 0)
        {
            return text;
        }

        return HyperlinkField().Replace(text, string.Empty).Replace("﷟", string.Empty, StringComparison.Ordinal);
    }

    public static string PlainTextFromRichText(RichText richText) => PyText.Strip(Sanitize(richText.Text));

    public static System.Text.RegularExpressions.Regex HyperlinkField() => HyperlinkFieldPattern;

    public static System.Text.RegularExpressions.Regex HyperlinkTarget() => HyperlinkTargetPattern;

    private static readonly System.Text.RegularExpressions.Regex HyperlinkFieldPattern = new("﷟HYPERLINK\\s+\"[^\"]*\"", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex HyperlinkTargetPattern = new("﷟HYPERLINK\\s+\"([^\"]+)\"", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
}
