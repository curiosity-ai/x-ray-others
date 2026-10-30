using System.Globalization;
using System.Text;
using XRay.ThreeD.Internal;

namespace XRay.ThreeD.Formats.Parasolid;

/// <summary>
/// Parasolid transmit files (text .x_t/.xmt_txt and binary .x_b/.xmt_bin): the header only. The file's modeller version, schema key, the
/// originating system and application lines of the text header, user-field size and the first words of the node stream are read. The node
/// stream itself cannot be interpreted without the field layouts of the file's schema (a proprietary table per schema version), so geometry is
/// not decoded and a load says so.
/// </summary>
internal static class ParasolidReader
{
    public static bool Sniff(ReadOnlySpan<byte> head)
    {
        var text = Encoding.Latin1.GetString(head[..Math.Min(head.Length, 200)]);
        return text.StartsWith("**ABCDEFGHIJKLMNOPQRSTUVWXYZ", StringComparison.Ordinal)
            || text.StartsWith("**PART1", StringComparison.Ordinal)
            || (text.Length > 6 && text[0] == 'T' && char.IsDigit(text[1]) && text.Contains(": TRANSMIT FILE", StringComparison.Ordinal))
            || (head.Length > 8 && head[0] == (byte)'P' && head[1] == (byte)'S' && head[2] == 0 && head[3] == 0 && text.Contains("TRANSMIT FILE", StringComparison.Ordinal));
    }

    public static ModelMetadata ReadMetadata(ReadOnlyMemory<byte> data, CancellationToken cancel)
    {
        var meta = new ModelMetadata { Format = ModelFormats.Parasolid, DocumentKind = "model", UpAxis = UpAxis.Z };
        var span = data.Span;
        var binary = span.Length > 4 && span[0] == (byte)'P' && span[1] == (byte)'S' && span[2] == 0;
        meta.Properties.Add(new ModelProperty("Encoding", binary ? "binary (x_b)" : "text (x_t)"));

        // The first kilobytes hold every header form.
        var text = Encoding.Latin1.GetString(span[..Math.Min(span.Length, 16 * 1024)]);
        cancel.ThrowIfCancellationRequested();
        var endHeader = text.IndexOf("**END_OF_HEADER", StringComparison.Ordinal);
        if (!binary && endHeader > 0)
        {
            string? created = null;
            foreach (var raw in text[..endHeader].Split('\n'))
            {
                var line = raw.Trim().TrimEnd(';');
                var eq = line.IndexOf('=');
                if (line.StartsWith("**", StringComparison.Ordinal) || eq <= 0)
                {
                    continue;
                }

                var key = line[..eq].Trim();
                var value = line[(eq + 1)..].Trim();
                if (value.Length == 0)
                {
                    continue;
                }

                switch (key)
                {
                    case "APPL":
                        meta.Application = value;
                        break;
                    case "USER":
                        meta.Author = value;
                        break;
                    case "DATE":
                        created = value;
                        break;
                    case "FILE":
                        meta.Title = value;
                        break;
                    case "SCH":
                        meta.Properties.Add(new ModelProperty("Schema", value));
                        break;
                    default:
                        meta.Properties.Add(new ModelProperty(key, value, "Header"));
                        break;
                }
            }

            if (created is not null && DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            {
                meta.Created = date;
            }
            else if (created is not null)
            {
                meta.Properties.Add(new ModelProperty("DATE", created, "Header"));
            }
        }

        var marker = text.IndexOf("TRANSMIT FILE", StringComparison.Ordinal);
        if (marker < 0)
        {
            throw new ModelFormatException("The Parasolid transmit file has no TRANSMIT FILE line.");
        }

        var version = text.IndexOf("modeller version", marker, StringComparison.Ordinal);
        if (version > 0)
        {
            var start = version + "modeller version".Length;
            var digits = 0;
            while (start + digits + 1 < text.Length && (text[start + 1 + digits] is >= '0' and <= '9'))
            {
                digits++;
            }

            if (digits > 0)
            {
                meta.FormatVersion = "modeller version " + text.Substring(start + 1, digits);
            }
        }

        var schema = text.IndexOf("SCH_", marker, StringComparison.Ordinal);
        if (schema > 0)
        {
            var end = schema;
            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_'))
            {
                end++;
            }

            var key = text[schema..end];
            if (!meta.Properties.Any(p => p.Name == "Schema"))
            {
                meta.Properties.Add(new ModelProperty("Schema", key));
            }

            // SCH_<kernel version>_<schema number>[_<base schema>]: the digits as the file spells them.
            var parts = key.Split('_');
            if (parts.Length >= 3)
            {
                meta.Properties.Add(new ModelProperty("Kernel (from schema key)", parts[1]));
            }
        }

        meta.Notes.Add("The node stream of a transmit file is laid out by a per-version schema that is not available; geometry is not decoded.");
        return meta;
    }

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var meta = ReadMetadata(data, options.Cancellation);
        throw new UnsupportedModelFormatException(ModelFormats.Parasolid, $"Parasolid transmit file ({meta.FormatVersion ?? "unknown version"}): geometry is not decoded, only the header is read (use ReadMetadata).");
    }
}
