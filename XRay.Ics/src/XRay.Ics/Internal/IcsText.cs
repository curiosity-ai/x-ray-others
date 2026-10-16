using System.Text;

namespace XRay.Ics.Internal;

/// <summary>TEXT escaping, quoted-printable and base64 helpers.</summary>
internal static class IcsText
{
    public static string Unescape(string value)
    {
        if (value.IndexOf('\\') < 0 && value.IndexOf('\r') < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\\' && i + 1 < value.Length)
            {
                var next = value[i + 1];
                switch (next)
                {
                    case 'n':
                    case 'N':
                        builder.Append('\n');
                        i++;
                        continue;
                    case '\\':
                    case ',':
                    case ';':
                    case ':':
                        builder.Append(next);
                        i++;
                        continue;
                    default:
                        // Not an escape RFC 5545 defines (a Windows path, say): keep it literally.
                        builder.Append(c);
                        continue;
                }
            }

            if (c == '\r')
            {
                if (i + 1 < value.Length && value[i + 1] == '\n')
                {
                    i++;
                }

                builder.Append('\n');
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    public static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;
                case ';':
                    builder.Append("\\;");
                    break;
                case ',':
                    builder.Append("\\,");
                    break;
                case '\r':
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }

                    builder.Append("\\n");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                default:
                    if (c >= ' ' || c == '\t')
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>Splits on a separator that is not preceded by a backslash and unescapes each part.</summary>
    public static List<string> SplitUnescaped(string value, char separator)
    {
        var parts = new List<string>();
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\')
            {
                i++;
            }
            else if (value[i] == separator)
            {
                parts.Add(Unescape(value[start..i]));
                start = i + 1;
            }
        }

        parts.Add(Unescape(value[start..]));
        return parts;
    }

    /// <summary>Splits without unescaping (for structured values such as RRULE parts).</summary>
    public static List<string> SplitRaw(string value, char separator)
    {
        var parts = new List<string>();
        var start = 0;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\')
            {
                i++;
            }
            else if (value[i] == separator)
            {
                parts.Add(value[start..i]);
                start = i + 1;
            }
        }

        parts.Add(value[start..]);
        return parts;
    }

    /// <summary>
    /// Decodes quoted-printable text to bytes. Input characters are expected to be ASCII;
    /// anything else is passed through as its Latin-1 byte. A malformed escape stays literal.
    /// </summary>
    public static byte[] DecodeQuotedPrintable(string value)
    {
        var bytes = new List<byte>(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '=' && i + 2 < value.Length + 0 && Uri.IsHexDigit(value[i + 1]) && Uri.IsHexDigit(value[i + 2]))
            {
                bytes.Add(Convert.ToByte(value.Substring(i + 1, 2), 16));
                i += 2;
            }
            else if (c == '=' && i + 1 < value.Length && (value[i + 1] == '\r' || value[i + 1] == '\n'))
            {
                // A soft break that survived: drop it with its line end.
                i++;
                if (value[i] == '\r' && i + 1 < value.Length && value[i + 1] == '\n')
                {
                    i++;
                }
            }
            else if (c < 256)
            {
                bytes.Add((byte)c);
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(c.ToString()));
            }
        }

        return [.. bytes];
    }

    /// <summary>Tolerant base64: whitespace and stray characters are skipped, padding is optional.</summary>
    public static byte[] DecodeBase64(string value)
    {
        using var memory = new MemoryStream();
        DecodeBase64To(value, memory, CancellationToken.None);
        return memory.ToArray();
    }

    /// <summary>Streams the decoded bytes of tolerant base64 text into <paramref name="destination"/> without a second full copy.</summary>
    public static void DecodeBase64To(string value, Stream destination, CancellationToken cancellationToken)
    {
        const int Chunk = 8192;
        var buffer = new char[Chunk];
        var length = 0;
        var processed = 0;
        foreach (var c in value)
        {
            char mapped;
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/')
            {
                mapped = c;
            }
            else if (c == '-')
            {
                mapped = '+';
            }
            else if (c == '_')
            {
                mapped = '/';
            }
            else
            {
                continue;
            }

            buffer[length++] = mapped;
            if (length == Chunk)
            {
                Flush(buffer, length, destination);
                length = 0;
                if ((++processed & 63) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }

        var remainder = length % 4;
        if (remainder == 1)
        {
            length--;
        }
        else if (remainder is 2 or 3)
        {
            for (var i = remainder; i < 4; i++)
            {
                buffer[length++] = '=';
            }
        }

        Flush(buffer, length, destination);
    }

    private static void Flush(char[] buffer, int length, Stream destination)
    {
        if (length == 0)
        {
            return;
        }

        try
        {
            var bytes = Convert.FromBase64CharArray(buffer, 0, length);
            destination.Write(bytes, 0, bytes.Length);
        }
        catch (FormatException)
        {
            // Damaged data: keep what came before and drop this block.
        }
    }
}
