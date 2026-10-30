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

        meta.Notes.Add(DescribeSupport(text, binary));
        return meta;
    }

    /// <summary>The key of the schema the node stream is written in, the user field size and where the nodes start; the key is read from the TRANSMIT FILE line (the header's own SCH entry can differ).</summary>
    private static (string? Key, int UserFields, int Start) StreamHeader(string text)
    {
        var marker = text.IndexOf("TRANSMIT FILE", StringComparison.Ordinal);
        var schema = marker < 0 ? -1 : text.IndexOf("SCH_", marker, StringComparison.Ordinal);
        if (schema < 0)
        {
            return (null, 0, 0);
        }

        var userFields = 0;
        var usfld = text.IndexOf("USFLD_SIZE=", StringComparison.Ordinal);
        if (usfld >= 0 && usfld < marker)
        {
            var from = usfld + "USFLD_SIZE=".Length;
            var end = from;
            while (end < text.Length && char.IsAsciiDigit(text[end]))
            {
                end++;
            }

            _ = int.TryParse(text.AsSpan(from, end - from), NumberStyles.None, CultureInfo.InvariantCulture, out userFields);
        }

        var stop = schema;
        while (stop < text.Length && (char.IsAsciiLetterOrDigit(text[stop]) || text[stop] == '_'))
        {
            stop++;
        }

        // The key is followed, without a space, by the user field size.
        var digits = userFields.ToString(CultureInfo.InvariantCulture);
        var token = text[schema..stop];
        var keyEnd = token.EndsWith(digits, StringComparison.Ordinal) && token.Length > digits.Length ? token.Length - digits.Length : token.Length;
        return (token[..keyEnd], userFields, schema + keyEnd + digits.Length);
    }

    /// <summary>The layout of the node stream: legacy (Parasolid 10), current (12 and 13) or null for a schema that is not tabulated.</summary>
    private static bool? Layout(string? key, out string reason)
    {
        reason = "";
        var parts = key?.Split('_') ?? [];
        if (parts.Length != 3 || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var schema))
        {
            reason = parts.Length > 3 ? $"the file embeds its schema as differences to the base schema {parts[^1]} ({key})" : "the schema key is missing or malformed";
            return null;
        }

        if (schema is >= 10000 and < 11000)
        {
            return true;
        }

        if (schema is >= 12000 and <= 13006)
        {
            return false;
        }

        reason = $"the field layouts of schema {schema} are not tabulated (Parasolid 10, 12 and 13 are)";
        return null;
    }

    private static string DescribeSupport(string text, bool binary)
    {
        if (binary)
        {
            return "Binary transmit files (x_b) are not decoded; geometry is not read.";
        }

        var layout = Layout(StreamHeader(text).Key, out var reason);
        return layout is null
            ? $"The node stream is not decoded: {reason}."
            : "Bodies, faces, edges and the analytic and B-spline geometry of the node stream are decoded by Load.";
    }

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var meta = ReadMetadata(data, options.Cancellation);
        var span = data.Span;
        if (span.Length > 4 && span[0] == (byte)'P' && span[1] == (byte)'S' && span[2] == 0)
        {
            throw new UnsupportedModelFormatException(ModelFormats.Parasolid, $"Parasolid binary transmit file ({meta.FormatVersion ?? "unknown version"}): the node stream is not decoded, only the header is read (use ReadMetadata).");
        }

        var text = Encoding.Latin1.GetString(span);
        var (key, userFields, start) = StreamHeader(text);
        var legacy = Layout(key, out var reason) ?? throw new UnsupportedModelFormatException(ModelFormats.Parasolid, $"Parasolid transmit file ({meta.FormatVersion ?? "unknown version"}): {reason}; only the header is read (use ReadMetadata).");
        var body = XtStream.Unwrap(text[Math.Min(start, text.Length)..]);
        var nodes = XtStream.Parse(body, 0, userFields, legacy, Math.Max(1_000_000, options.MaxTriangles), options.Cancellation);
        if (nodes.Count == 0)
        {
            throw new ModelFormatException("The Parasolid transmit file has no nodes.");
        }

        var model = new Model3D();
        var metadata = model.Metadata;
        metadata.Format = ModelFormats.Parasolid;
        metadata.DocumentKind = "model";
        metadata.UpAxis = UpAxis.Z;
        metadata.Units = LengthUnit.Meter;
        metadata.FormatVersion = meta.FormatVersion;
        metadata.Application = meta.Application;
        metadata.Title = meta.Title;
        metadata.Author = meta.Author;
        metadata.Created = meta.Created;
        foreach (var p in meta.Properties)
        {
            metadata.Properties.Add(p);
        }

        var builder = new XtBuilder(nodes, options, model);
        builder.Build();
        metadata.HasGeometry = model.Meshes.Count > 0;
        metadata.Properties.Add(new ModelProperty("Nodes", nodes.Count.ToString(CultureInfo.InvariantCulture)));
        metadata.Properties.Add(new ModelProperty("Bodies", nodes.Values.Count(n => n.Def.Type == 12).ToString(CultureInfo.InvariantCulture)));
        metadata.Properties.Add(new ModelProperty("Faces", builder.FaceCount.ToString(CultureInfo.InvariantCulture)));
        metadata.Statistics = model.ComputeStatistics();
        metadata.Bounds = model.ComputeBounds();
        return model;
    }
}
