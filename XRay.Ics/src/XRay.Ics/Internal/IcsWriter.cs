using System.Text;

namespace XRay.Ics.Internal;

/// <summary>Writes the raw tree as RFC 5545 content lines: CRLF, UTF-8, folded at 75 octets.</summary>
internal static class IcsWriter
{
    private const int FoldLimit = 75;
    private static readonly System.Buffers.SearchValues<char> QuoteTriggers = System.Buffers.SearchValues.Create(";:, \"^\n\r");

    public static void Write(IcsComponent root, Stream destination, CancellationToken cancellationToken)
    {
        using var writer = new StreamWriter(destination, new UTF8Encoding(false), 32 * 1024, leaveOpen: true) { NewLine = "\r\n" };
        var counter = 0;
        WriteComponent(root, writer, cancellationToken, ref counter);
        writer.Flush();
    }

    private static void WriteComponent(IcsComponent component, StreamWriter writer, CancellationToken cancellationToken, ref int counter)
    {
        WriteLine(writer, "BEGIN:" + component.Name);
        foreach (var property in component.Properties)
        {
            if ((++counter & 255) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            WriteProperty(property, writer);
        }

        foreach (var child in component.Children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WriteComponent(child, writer, cancellationToken, ref counter);
        }

        WriteLine(writer, "END:" + component.Name);
    }

    private static void WriteProperty(IcsProperty property, StreamWriter writer)
    {
        var builder = new StringBuilder();
        if (!string.IsNullOrEmpty(property.Group))
        {
            builder.Append(property.Group).Append('.');
        }

        builder.Append(property.Name);
        var value = property.Value;
        var decodedQuotedPrintable = string.Equals(property.GetParameter("ENCODING"), "QUOTED-PRINTABLE", StringComparison.OrdinalIgnoreCase);
        foreach (var parameter in property.Parameters)
        {
            if (decodedQuotedPrintable && (parameter.Name == "ENCODING" || parameter.Name == "CHARSET"))
            {
                continue;
            }

            if (parameter.Values.Count == 0)
            {
                continue;
            }

            builder.Append(';').Append(parameter.Name).Append('=');
            for (var i = 0; i < parameter.Values.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                AppendParameterValue(builder, parameter.Values[i]);
            }
        }

        if (decodedQuotedPrintable && property.Name != "CATEGORIES")
        {
            value = IcsText.Escape(IcsText.Unescape(value));
        }

        builder.Append(':');
        AppendValue(builder, value);
        WriteLine(writer, builder.ToString());
    }

    private static void AppendValue(StringBuilder builder, string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < value.Length && value[i + 1] == '\n')
                {
                    i++;
                }

                builder.Append("\\n");
            }
            else if (c >= ' ' || c == '\t')
            {
                builder.Append(c);
            }
        }
    }

    private static void AppendParameterValue(StringBuilder builder, string value)
    {
        var needsQuotes = value.AsSpan().IndexOfAny(QuoteTriggers) >= 0 || value.Length == 0;
        if (!needsQuotes)
        {
            builder.Append(value);
            return;
        }

        builder.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    builder.Append("^'");
                    break;
                case '^':
                    builder.Append("^^");
                    break;
                case '\n':
                    builder.Append("^n");
                    break;
                case '\r':
                    break;
                default:
                    if (c >= ' ' || c == '\t')
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        builder.Append('"');
    }

    /// <summary>Writes one logical line folded so no physical line exceeds 75 octets (UTF-8), never splitting a character.</summary>
    internal static void WriteLine(StreamWriter writer, string line)
    {
        var octets = 0;
        var limit = FoldLimit;
        var start = 0;
        var i = 0;
        while (i < line.Length)
        {
            var c = line[i];
            int width;
            int chars = 1;
            if (c < 0x80)
            {
                width = 1;
            }
            else if (c < 0x800)
            {
                width = 2;
            }
            else if (char.IsHighSurrogate(c) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1]))
            {
                width = 4;
                chars = 2;
            }
            else
            {
                width = 3;
            }

            if (octets + width > limit)
            {
                writer.Write(line.AsSpan(start, i - start));
                writer.Write("\r\n ");
                start = i;
                octets = 0;
                limit = FoldLimit - 1;
            }

            octets += width;
            i += chars;
        }

        writer.Write(line.AsSpan(start));
        writer.Write("\r\n");
    }
}
