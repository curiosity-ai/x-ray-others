using System.Text;

namespace XRay.Ics.Internal;

/// <summary>Parses one logical content line into an <see cref="IcsProperty"/>.</summary>
internal static class ContentLineParser
{
    private static readonly string[] BareEncodings = ["QUOTED-PRINTABLE", "BASE64", "8BIT", "7BIT", "B", "Q"];

    public static IcsProperty? Parse(string line, int lineNumber, bool legacyText, Encoding fallback)
    {
        var index = 0;
        var nameEnd = line.AsSpan().IndexOfAny(';', ':');
        if (nameEnd <= 0)
        {
            return null;
        }

        var fullName = line[..nameEnd].Trim();
        string? group = null;
        var dot = fullName.IndexOf('.');
        if (dot > 0)
        {
            group = fullName[..dot];
            fullName = fullName[(dot + 1)..];
        }

        if (fullName.Length == 0 || fullName.AsSpan().IndexOfAny(' ', '"', '\t') >= 0)
        {
            return null;
        }

        var property = new IcsProperty(fullName) { Group = group, LineNumber = lineNumber };
        index = nameEnd;
        var inQuotes = false;
        while (index < line.Length && line[index] == ';')
        {
            index++;
            var paramStart = index;
            var equals = -1;
            while (index < line.Length)
            {
                var c = line[index];
                if (!inQuotes && c == '\\' && index + 1 < line.Length && IsParameterEscape(line[index + 1]))
                {
                    // Some producers backslash-escape separators in unquoted parameter values (CN=Doe\, John).
                    index += 2;
                    continue;
                }

                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (!inQuotes && (c == ';' || c == ':'))
                {
                    break;
                }
                else if (!inQuotes && c == '=' && equals < 0)
                {
                    equals = index;
                }

                index++;
            }

            ReadParameter(property, line, paramStart, index, equals);
        }

        if (index >= line.Length || line[index] != ':')
        {
            // An unterminated quote swallowed the colon: take the text after the first colon instead.
            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                return null;
            }

            property.Parameters.Clear();
            property.Value = line[(colon + 1)..];
        }
        else
        {
            property.Value = line[(index + 1)..];
        }

        DecodeValue(property, legacyText, fallback);
        return property;
    }

    private static void ReadParameter(IcsProperty property, string line, int start, int end, int equals)
    {
        if (end <= start)
        {
            return;
        }

        if (equals < 0)
        {
            // vCalendar 1.0 / vCard 2.1 bare parameter: ;QUOTED-PRINTABLE, ;WORK.
            var token = line[start..end].Trim();
            if (token.Length == 0)
            {
                return;
            }

            var isEncoding = Array.Exists(BareEncodings, e => string.Equals(e, token, StringComparison.OrdinalIgnoreCase));
            property.Parameters.Add(new IcsParameter(isEncoding ? "ENCODING" : "TYPE", token.ToUpperInvariant() == "B" ? "BASE64" : token));
            return;
        }

        var name = line[start..equals].Trim();
        if (name.Length == 0)
        {
            return;
        }

        var parameter = new IcsParameter(name);
        var i = equals + 1;
        var valueBuilder = new StringBuilder();
        var quoted = false;
        var wasQuoted = false;
        while (i <= end)
        {
            if (i == end)
            {
                parameter.Values.Add(Caret(valueBuilder.ToString(), wasQuoted));
                break;
            }

            var c = line[i];
            if (!quoted && c == '\\' && i + 1 < end && IsParameterEscape(line[i + 1]))
            {
                valueBuilder.Append(line[i + 1]);
                i += 2;
                continue;
            }

            if (c == '"')
            {
                quoted = !quoted;
                wasQuoted = true;
            }
            else if (c == ',' && !quoted)
            {
                parameter.Values.Add(Caret(valueBuilder.ToString(), wasQuoted));
                valueBuilder.Clear();
                wasQuoted = false;
            }
            else
            {
                valueBuilder.Append(c);
            }

            i++;
        }

        if (string.Equals(parameter.Name, "ENCODING", StringComparison.OrdinalIgnoreCase) && parameter.Values.Count == 1 && string.Equals(parameter.Values[0], "B", StringComparison.OrdinalIgnoreCase))
        {
            parameter.Values[0] = "BASE64";
        }

        property.Parameters.Add(parameter);
    }

    private static bool IsParameterEscape(char c) => c is ',' or ';' or ':' or '\\';

    /// <summary>RFC 6868: ^n newline, ^^ caret, ^' double quote.</summary>
    private static string Caret(string value, bool wasQuoted)
    {
        if (value.IndexOf('^') < 0)
        {
            return wasQuoted ? value : value.Trim();
        }

        var builder = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '^' && i + 1 < value.Length)
            {
                switch (value[i + 1])
                {
                    case 'n':
                        builder.Append('\n');
                        i++;
                        continue;
                    case '^':
                        builder.Append('^');
                        i++;
                        continue;
                    case '\'':
                        builder.Append('"');
                        i++;
                        continue;
                }
            }

            builder.Append(value[i]);
        }

        return builder.ToString();
    }

    private static void DecodeValue(IcsProperty property, bool legacyText, Encoding fallback)
    {
        var encoding = property.GetParameter("ENCODING");
        var charset = property.GetParameter("CHARSET");
        var isQuotedPrintable = string.Equals(encoding, "QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase);
        var isBinary = string.Equals(encoding, "BASE64", StringComparison.OrdinalIgnoreCase)
            || string.Equals(property.GetParameter("VALUE"), "BINARY", StringComparison.OrdinalIgnoreCase);
        if (isBinary)
        {
            return;
        }

        if (isQuotedPrintable)
        {
            var bytes = IcsText.DecodeQuotedPrintable(property.Value);
            var target = TextDecoder.TryGetEncoding(charset);
            property.Value = DecodeBytes(bytes, target, legacyText ? fallback : null);
            return;
        }

        if (legacyText)
        {
            var target = TextDecoder.TryGetEncoding(charset) ?? fallback;
            var bytes = Encoding.Latin1.GetBytes(property.Value);
            property.Value = target.GetString(bytes);
            // Parameter values were read as Latin-1 too.
            foreach (var parameter in property.Parameters)
            {
                for (var i = 0; i < parameter.Values.Count; i++)
                {
                    parameter.Values[i] = target.GetString(Encoding.Latin1.GetBytes(parameter.Values[i]));
                }
            }
        }
    }

    private static string DecodeBytes(byte[] bytes, Encoding? declared, Encoding? fallback)
    {
        if (declared is not null)
        {
            return declared.GetString(bytes);
        }

        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return (fallback ?? TextDecoder.Windows1252()).GetString(bytes);
        }
    }
}
