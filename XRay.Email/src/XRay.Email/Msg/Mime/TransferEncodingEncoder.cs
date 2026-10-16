using System.Globalization;
using System.Text;

namespace XRay.Email.Msg.Mime;

internal static class TransferEncodingEncoder
{
    private const int MaxEncodedWordLength = 75;
    private const int MaxLineLength = 76;

    public static (string TransferEncoding, byte[] EncodedBody) EncodeBody(string mediaType, byte[] payload)
    {
        if (payload.Length == 0)
        {
            return ("7bit", []);
        }

        if (mediaType.StartsWith("message/", StringComparison.OrdinalIgnoreCase))
        {
            // RFC 2046 5.2.1: message/rfc822 must not be base64 or quoted-printable encoded.
            return (IsSevenBit(payload) ? "7bit" : "8bit", payload.ToArray());
        }

        if (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            if (IsSevenBit(payload) && !HasLongLine(payload))
            {
                return ("7bit", NormalizeLineEndings(payload));
            }

            return ("quoted-printable", EncodeQuotedPrintable(payload));
        }

        return ("base64", EncodeBase64(payload));
    }

    /// <summary>Encodes a body for a part whose Content-Transfer-Encoding is already decided.</summary>
    public static byte[] EncodeBodyAs(string? transferEncoding, byte[] payload)
    {
        return transferEncoding?.Trim().ToLowerInvariant() switch
        {
            "base64" => EncodeBase64(payload),
            "quoted-printable" => EncodeQuotedPrintable(payload),
            _ => payload.ToArray(),
        };
    }

    /// <summary>
    /// Encodes an unstructured header value (such as Subject). ASCII text is returned unchanged; any
    /// other text becomes a run of UTF-8 B-encoded words of at most 75 characters each, split on
    /// character boundaries.
    /// </summary>
    public static string EncodeUnstructured(string value)
    {
        if (string.IsNullOrEmpty(value) || (value.All(ch => ch is >= ' ' and <= '~' or '\t') && !value.Contains("=?", StringComparison.Ordinal)))
        {
            return value;
        }

        return EncodeWords(value);
    }

    /// <summary>Encodes text as space-separated UTF-8 B-encoded words (RFC 2047).</summary>
    public static string EncodeWords(string value)
    {
        const string prefix = "=?utf-8?B?";
        const string suffix = "?=";
        var maxBytes = (MaxEncodedWordLength - prefix.Length - suffix.Length) / 4 * 3;
        var words = new List<string>();
        var chunk = new List<byte>(maxBytes);
        var enumerator = StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            var bytes = Encoding.UTF8.GetBytes(enumerator.GetTextElement());
            if (chunk.Count > 0 && chunk.Count + bytes.Length > maxBytes)
            {
                words.Add(prefix + Convert.ToBase64String(chunk.ToArray()) + suffix);
                chunk.Clear();
            }

            chunk.AddRange(bytes);
        }

        if (chunk.Count > 0)
        {
            words.Add(prefix + Convert.ToBase64String(chunk.ToArray()) + suffix);
        }

        return string.Join(' ', words);
    }

    /// <summary>
    /// Formats one header parameter. ASCII values are quoted; other values use RFC 2231 extended
    /// syntax (<c>name*0*=utf-8''...</c>), split into sections so no line grows unbounded.
    /// </summary>
    public static string FormatParameter(string name, string value)
    {
        if (value.All(ch => ch is >= ' ' and <= '~'))
        {
            return $"{name}=\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
        }

        var encoded = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            if (b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' || b is (byte)'.' or (byte)'-' or (byte)'_' or (byte)'~')
            {
                encoded.Append((char)b);
            }
            else
            {
                encoded.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        var text = encoded.ToString();
        const int sectionLength = 60;
        if (text.Length <= sectionLength)
        {
            return $"{name}*=utf-8''{text}";
        }

        var sections = new List<string>();
        var position = 0;
        while (position < text.Length)
        {
            var length = Math.Min(sectionLength, text.Length - position);

            // Never split a %XX escape.
            if (position + length < text.Length)
            {
                if (text[position + length - 1] == '%')
                {
                    length -= 1;
                }
                else if (length >= 2 && text[position + length - 2] == '%')
                {
                    length -= 2;
                }
            }

            var piece = text.Substring(position, length);
            sections.Add(sections.Count == 0 ? $"{name}*0*=utf-8''{piece}" : $"{name}*{sections.Count}*={piece}");
            position += length;
        }

        return string.Join("; ", sections);
    }

    /// <summary>
    /// Folds a header line at whitespace so that lines stay within 78 characters where the value
    /// allows it (RFC 5322 2.2.3). Encoded words and other tokens are never split.
    /// </summary>
    public static string FoldHeader(string name, string value)
    {
        var line = $"{name}: {value}";
        if (line.Length <= 78)
        {
            return line;
        }

        var builder = new StringBuilder(line.Length + 16);
        var lineLength = 0;
        var tokens = SplitKeepingLeadingSpace(line);
        foreach (var token in tokens)
        {
            if (lineLength > 0 && lineLength + token.Length > 78 && token[0] == ' ')
            {
                builder.Append("\r\n");
                lineLength = 0;
            }

            builder.Append(token);
            lineLength += token.Length;
        }

        return builder.ToString();
    }

    private static List<string> SplitKeepingLeadingSpace(string line)
    {
        var tokens = new List<string>();
        var start = 0;
        for (var index = 1; index < line.Length; index++)
        {
            if (line[index] == ' ' && line[index - 1] != ' ')
            {
                tokens.Add(line[start..index]);
                start = index;
            }
        }

        tokens.Add(line[start..]);
        return tokens;
    }

    private static bool IsSevenBit(byte[] payload)
    {
        foreach (var b in payload)
        {
            if (b == 9 || b == 10 || b == 13)
            {
                continue;
            }

            if (b < 32 || b > 126)
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasLongLine(byte[] payload)
    {
        var length = 0;
        foreach (var b in payload)
        {
            length = b is (byte)'\n' or (byte)'\r' ? 0 : length + 1;
            if (length > 998)
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] NormalizeLineEndings(byte[] payload)
    {
        var text = Encoding.Latin1.GetString(payload).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        return Encoding.Latin1.GetBytes(text.Replace("\n", "\r\n", StringComparison.Ordinal));
    }

    private static byte[] EncodeBase64(byte[] payload)
    {
        var base64 = Convert.ToBase64String(payload);
        var builder = new StringBuilder(base64.Length + ((base64.Length / MaxLineLength) + 1) * 2);
        for (var offset = 0; offset < base64.Length; offset += MaxLineLength)
        {
            builder.Append(base64, offset, Math.Min(MaxLineLength, base64.Length - offset));
            builder.Append("\r\n");
        }

        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static byte[] EncodeQuotedPrintable(byte[] payload)
    {
        var builder = new StringBuilder(payload.Length * 3);
        var lineLength = 0;
        for (var index = 0; index < payload.Length; index++)
        {
            var b = payload[index];
            if (b == (byte)'\r' && index + 1 < payload.Length && payload[index + 1] == (byte)'\n')
            {
                continue;
            }

            if (b is (byte)'\n' or (byte)'\r')
            {
                builder.Append("\r\n");
                lineLength = 0;
                continue;
            }

            // Whitespace right before a line break (or the end) must be encoded, or transports strip it.
            var atLineEnd = index + 1 == payload.Length || payload[index + 1] is (byte)'\r' or (byte)'\n';
            var literal = IsPrintableQuotedPrintableByte(b) && !(atLineEnd && b is (byte)' ' or (byte)'\t');
            var token = literal ? ((char)b).ToString() : $"={b:X2}";
            if (lineLength + token.Length > MaxLineLength - 1)
            {
                builder.Append("=\r\n");
                lineLength = 0;
            }

            builder.Append(token);
            lineLength += token.Length;
        }

        return Encoding.ASCII.GetBytes(builder.ToString());
    }

    private static bool IsPrintableQuotedPrintableByte(byte value)
    {
        return value is >= 33 and <= 60 or >= 62 and <= 126 || value == 9 || value == 32;
    }
}
