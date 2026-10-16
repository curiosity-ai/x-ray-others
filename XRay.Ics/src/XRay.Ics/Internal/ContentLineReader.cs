using System.Text;

namespace XRay.Ics.Internal;

/// <summary>
/// Splits calendar text into logical content lines: CRLF, LF or CR line ends, RFC 5545 folding
/// (a physical line starting with space or tab continues the previous one), and the vCalendar 1.0
/// quoted-printable soft break (a line ending in <c>=</c> continues on the next line).
/// </summary>
internal sealed class ContentLineReader
{
    private readonly string text;
    private readonly int maxLineLength;
    private int position;
    private int lineNumber;

    public ContentLineReader(string text, int maxLineLength)
    {
        this.text = text;
        this.maxLineLength = maxLineLength;
    }

    public int LastLineNumber { get; private set; }

    /// <summary>True when the last line returned exceeded the length limit and was emptied.</summary>
    public bool LastLineOverflowed { get; private set; }

    public bool TryRead(out string line)
    {
        line = string.Empty;
        LastLineOverflowed = false;
        while (position < text.Length)
        {
            var first = ReadPhysical(out var startLine);
            if (first.Length == 0)
            {
                continue;
            }

            LastLineNumber = startLine;
            var quotedPrintable = IsQuotedPrintable(first);
            var simple = first.Length <= maxLineLength && !(quotedPrintable && first[^1] == '=') && (position >= text.Length || text[position] is not (' ' or '\t' or '\r' or '\n'));
            if (simple)
            {
                // The common case: one physical line, nothing to join.
                line = first;
                return true;
            }

            var builder = new StringBuilder(first.Length);
            var overflow = false;
            Append(builder, first, ref overflow);
            while (true)
            {
                if (quotedPrintable && !overflow && builder.Length > 0 && builder[^1] == '=' && position < text.Length)
                {
                    builder.Length--;
                    var next = ReadPhysical(out _);
                    Append(builder, next, ref overflow);
                    continue;
                }

                if (position < text.Length && (text[position] == ' ' || text[position] == '\t' || BlankLinesThenContinuation()))
                {
                    // Blank lines between a line and its continuation are skipped, as most readers do.
                    while (position < text.Length && text[position] != ' ' && text[position] != '\t')
                    {
                        ReadPhysical(out _);
                    }

                    var next = ReadPhysical(out _);
                    Append(builder, next.AsSpan(1), ref overflow);
                    continue;
                }

                break;
            }

            if (overflow)
            {
                LastLineOverflowed = true;
                line = string.Empty;
                return true;
            }

            line = builder.ToString();
            return true;
        }

        return false;
    }

    /// <summary>True when only empty lines separate the current position from a line that starts with white space.</summary>
    private bool BlankLinesThenContinuation()
    {
        var index = position;
        var skipped = false;
        while (index < text.Length && (text[index] == '\r' || text[index] == '\n'))
        {
            skipped = true;
            index++;
        }

        return skipped && index < text.Length && (text[index] == ' ' || text[index] == '\t');
    }

    private void Append(StringBuilder builder, ReadOnlySpan<char> value, ref bool overflow)
    {
        if (overflow)
        {
            return;
        }

        if (builder.Length + value.Length > maxLineLength)
        {
            overflow = true;
            builder.Clear();
            return;
        }

        builder.Append(value);
    }

    private string ReadPhysical(out int startLine)
    {
        lineNumber++;
        startLine = lineNumber;
        var start = position;
        var end = position;
        while (end < text.Length && text[end] != '\r' && text[end] != '\n')
        {
            end++;
        }

        position = end;
        if (position < text.Length)
        {
            if (text[position] == '\r' && position + 1 < text.Length && text[position + 1] == '\n')
            {
                position += 2;
            }
            else
            {
                position++;
            }
        }

        return text.Substring(start, end - start);
    }

    private static bool IsQuotedPrintable(string firstPhysicalLine)
    {
        var colon = firstPhysicalLine.IndexOf(':');
        var header = colon < 0 ? firstPhysicalLine : firstPhysicalLine[..colon];
        return header.Contains("QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase);
    }
}
