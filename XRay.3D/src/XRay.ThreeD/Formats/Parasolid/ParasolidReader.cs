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

    /// <summary>
    /// The stream header of a text file: T, the length of the modeller version text and the text itself, the length of the schema key and the key, for an
    /// embedded schema the highest node type, then the user field size; the nodes follow.
    /// </summary>
    private static (string? Key, int UserFields, int MaxType, int Start) StreamHeader(string text)
    {
        var marker = text.IndexOf(": TRANSMIT FILE", StringComparison.Ordinal);
        if (marker < 0)
        {
            return (null, 0, 0, 0);
        }

        var digitsEnd = marker - 1;
        var t = digitsEnd;
        while (t > 0 && char.IsAsciiDigit(text[t - 1]))
        {
            t--;
        }

        if (digitsEnd < 1 || text[digitsEnd] != ' ' || t == digitsEnd || t == 0 || text[t - 1] != 'T' || !int.TryParse(text.AsSpan(t, digitsEnd - t), NumberStyles.None, CultureInfo.InvariantCulture, out var versionLength))
        {
            return (null, 0, 0, 0);
        }

        // The version text starts after the single space that follows its length (here the colon of ": TRANSMIT FILE" is its first character).
        var p = marker + versionLength;
        if (p >= text.Length)
        {
            return (null, 0, 0, 0);
        }

        int Number(ref int at)
        {
            while (at < text.Length && text[at] == ' ')
            {
                at++;
            }

            var from = at;
            while (at < text.Length && char.IsAsciiDigit(text[at]) && at - from < 9)
            {
                at++;
            }

            return at > from ? int.Parse(text.AsSpan(from, at - from), NumberStyles.None, CultureInfo.InvariantCulture) : -1;
        }

        var keyLength = Number(ref p);
        if (p < text.Length && text[p] == ' ')
        {
            p++;
        }

        if (keyLength < 8 || keyLength > 64 || p + keyLength > text.Length)
        {
            return (null, 0, 0, 0);
        }

        // The version text is followed by the key's length, a space and the key.
        var key = text.Substring(p, keyLength);
        p += keyLength;
        var parsed = XtKey.Parse(key);
        var maxType = 0;
        if (parsed is { Embedded: true })
        {
            maxType = Number(ref p);
        }

        var userFields = Number(ref p);
        return (key, Math.Max(userFields, 0), maxType, p);
    }

    private static string DescribeSupport(string text, bool binary)
    {
        if (binary)
        {
            return "Binary transmit files (x_b) are not decoded; geometry is not read.";
        }

        var candidates = XtLayouts.Candidates(XtKey.Parse(StreamHeader(XtStream.Unwrap(text)).Key), out var reason);
        return candidates.Count == 0
            ? $"The node stream is not decoded: {reason}."
            : "Bodies, faces, edges and the analytic and B-spline geometry of the node stream are decoded by Load.";
    }

    /// <summary>Reads the node stream of a text file with the layouts of its schema; of several candidate layouts the one that leaves no pointer dangling wins.</summary>
    internal static Dictionary<int, XtNode> ReadNodes(ReadOnlyMemory<byte> data, string? version, long maxNodes, CancellationToken cancel, out string? key)
    {
        var text = XtStream.Unwrap(Encoding.Latin1.GetString(data.Span));
        var (schemaKey, userFields, maxType, start) = StreamHeader(text);
        key = schemaKey;
        var candidates = XtLayouts.Candidates(XtKey.Parse(schemaKey), out var reason);
        if (candidates.Count == 0)
        {
            throw new UnsupportedModelFormatException(ModelFormats.Parasolid, $"Parasolid transmit file ({version ?? "unknown version"}): {reason}; only the header is read (use ReadMetadata).");
        }

        var body = text[Math.Min(start, text.Length)..];
        Dictionary<int, XtNode>? nodes = null;
        var bestDangling = int.MaxValue;
        ModelFormatException? failure = null;
        foreach (var candidate in candidates)
        {
            try
            {
                var parsedNodes = XtStream.Parse(body, 0, userFields, candidate, maxType, maxNodes, cancel, out var dangling);
                if (dangling < bestDangling)
                {
                    nodes = parsedNodes;
                    bestDangling = dangling;
                }

                if (dangling == 0)
                {
                    break;
                }
            }
            catch (ModelFormatException e)
            {
                failure ??= e;
            }
        }

        if (nodes is null)
        {
            throw failure is UnsupportedModelFormatException ? failure : new ModelFormatException("The Parasolid node stream could not be read with the layouts of schema " + schemaKey + ": " + failure?.Message);
        }

        if (nodes.Count == 0)
        {
            throw new ModelFormatException("The Parasolid transmit file has no nodes.");
        }

        return nodes;
    }

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var meta = ReadMetadata(data, options.Cancellation);
        var span = data.Span;
        if (span.Length > 4 && span[0] == (byte)'P' && span[1] == (byte)'S' && span[2] == 0)
        {
            throw new UnsupportedModelFormatException(ModelFormats.Parasolid, $"Parasolid binary transmit file ({meta.FormatVersion ?? "unknown version"}): the node stream is not decoded, only the header is read (use ReadMetadata).");
        }

        var nodes = ReadNodes(data, meta.FormatVersion, Math.Max(1_000_000, options.MaxTriangles), options.Cancellation, out var key);

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
