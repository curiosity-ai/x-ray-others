namespace XRay.OneNote.Internal.Text;

/// <summary>
/// String helpers with the white space definition of the reference implementation (Python's
/// <c>str.isspace</c>), which also counts the information separators U+001C to U+001F.
/// </summary>
internal static class PyText
{
    public static bool IsWhiteSpace(char c) => char.IsWhiteSpace(c) || c is >= '\u001C' and <= '\u001F';

    /// <summary>True when the string is non-empty and every character is white space.</summary>
    public static bool IsSpace(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!IsWhiteSpace(c))
            {
                return false;
            }
        }

        return true;
    }

    public static string Strip(string text) => StripEnd(StripStart(text));

    public static string StripStart(string text)
    {
        var start = 0;
        while (start < text.Length && IsWhiteSpace(text[start]))
        {
            start++;
        }

        return start == 0 ? text : text[start..];
    }

    public static string StripEnd(string text)
    {
        var end = text.Length;
        while (end > 0 && IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        return end == text.Length ? text : text[..end];
    }

    /// <summary>Collapses runs of white space to one space and trims, like <c>re.sub(r"\s+", " ", s).strip()</c>.</summary>
    public static string CollapseWhiteSpace(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var c in text)
        {
            if (IsWhiteSpace(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
