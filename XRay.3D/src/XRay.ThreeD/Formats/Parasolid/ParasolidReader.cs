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

    /// <summary>Where the stream starts after an optional text preamble (ending in **END_OF_HEADER), and whether it is binary (PS) rather than text (T).</summary>
    internal static (int Start, bool Binary) Payload(ReadOnlySpan<byte> span)
    {
        var start = 0;
        if (span.Length > 2 && span[0] == (byte)'*' && span[1] == (byte)'*')
        {
            var marker = span[..Math.Min(span.Length, 64 * 1024)].IndexOf("**END_OF_HEADER"u8);
            if (marker >= 0)
            {
                start = marker + "**END_OF_HEADER".Length;
                while (start < span.Length && span[start] == (byte)'*')
                {
                    start++;
                }

                if (start < span.Length && span[start] == (byte)'\r')
                {
                    start++;
                }

                if (start < span.Length && span[start] == (byte)'\n')
                {
                    start++;
                }
            }
        }

        var binary = span.Length > start + 4 && span[start] == (byte)'P' && span[start + 1] == (byte)'S' && span[start + 2] == 0;
        return (start, binary);
    }

    public static ModelMetadata ReadMetadata(ReadOnlyMemory<byte> data, CancellationToken cancel)
    {
        var meta = new ModelMetadata { Format = ModelFormats.Parasolid, DocumentKind = "model", UpAxis = UpAxis.Z };
        var span = data.Span;
        var binary = Payload(span).Binary;
        meta.Properties.Add(new ModelProperty("Encoding", binary ? "binary (x_b)" : "text (x_t)"));

        // The first kilobytes hold every header form.
        var text = Encoding.Latin1.GetString(span[..Math.Min(span.Length, 16 * 1024)]);
        cancel.ThrowIfCancellationRequested();
        var endHeader = text.IndexOf("**END_OF_HEADER", StringComparison.Ordinal);
        if (endHeader > 0)
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
            while (end < text.Length && (char.IsAsciiLetterOrDigit(text[end]) || text[end] == '_'))
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

        string? streamKey;
        if (binary)
        {
            var (payload, _) = Payload(span);
            streamKey = BinaryHeader(span[..Math.Min(span.Length, payload + 4096)].ToArray(), payload).Key;
        }
        else
        {
            streamKey = StreamHeader(XtStream.Unwrap(text)).Key;
        }

        meta.Notes.Add(DescribeSupport(streamKey));
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

    /// <summary>The header of a binary stream: PS and two zero flag bytes, the modeller version (16 bit length), the schema key (32 bit length), for an embedded schema the highest node type (16 bit) and the user field size (32 bit).</summary>
    private static (string? Key, int UserFields, int MaxType, int Start) BinaryHeader(byte[] data, int start)
    {
        try
        {
            var p = start + 4;
            int U16() => System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p, 2));
            int I32() => System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(p, 4));
            var versionLength = U16();
            p += 2 + versionLength;
            var keyLength = I32();
            p += 4;
            if (keyLength is < 8 or > 64 || p + keyLength > data.Length)
            {
                return (null, 0, 0, 0);
            }

            var key = Encoding.Latin1.GetString(data, p, keyLength);
            p += keyLength;
            var maxType = 0;
            if (XtKey.Parse(key) is { Embedded: true })
            {
                maxType = U16();
                p += 2;
            }

            var userFields = I32();
            p += 4;
            return userFields is < 0 or > 16 ? (null, 0, 0, 0) : (key, userFields, maxType, p);
        }
        catch (ArgumentOutOfRangeException)
        {
            return (null, 0, 0, 0);
        }
    }

    private static string DescribeSupport(string? key)
    {
        var candidates = XtLayouts.Candidates(XtKey.Parse(key), false, out var reason);
        return candidates.Count == 0
            ? $"The node stream is not decoded: {reason}."
            : "Bodies, faces, edges and the analytic and B-spline geometry of the node stream are decoded by Load.";
    }

    /// <summary>Reads the node stream with the layouts of its schema (text or binary); of several candidate layouts the one that leaves no pointer dangling wins.</summary>
    internal static Dictionary<int, XtNode> ReadNodes(ReadOnlyMemory<byte> data, string? version, long maxNodes, CancellationToken cancel, out string? key)
    {
        var (payload, binary) = Payload(data.Span);
        string? body = null;
        byte[]? bytes = null;
        (string? Key, int UserFields, int MaxType, int Start) header;
        if (binary)
        {
            bytes = data.ToArray();
            header = BinaryHeader(bytes, payload);
        }
        else
        {
            var text = XtStream.Unwrap(Encoding.Latin1.GetString(data.Span));
            header = StreamHeader(text);
            body = text[Math.Min(header.Start, text.Length)..];
        }

        key = header.Key;
        var candidates = XtLayouts.Candidates(XtKey.Parse(header.Key), binary, out var reason);
        if (candidates.Count == 0)
        {
            throw new UnsupportedModelFormatException(ModelFormats.Parasolid, $"Parasolid transmit file ({version ?? "unknown version"}): {reason}; only the header is read (use ReadMetadata).");
        }

        Dictionary<int, XtNode>? nodes = null;
        var bestDangling = int.MaxValue;
        ModelFormatException? failure = null;
        foreach (var candidate in candidates)
        {
            try
            {
                XtStream reader = binary ? new XtBinaryStream(bytes!, header.Start, cancel) : new XtTextStream(body!, 0, cancel);
                var parsedNodes = XtStream.Parse(reader, header.UserFields, candidate, header.MaxType, maxNodes, cancel, out var dangling);
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
            throw failure is UnsupportedModelFormatException ? failure : new ModelFormatException("The Parasolid node stream could not be read with the layouts of schema " + header.Key + ": " + failure?.Message);
        }

        if (nodes.Count == 0)
        {
            throw new ModelFormatException("The Parasolid transmit file has no nodes.");
        }

        return nodes;
    }

    /// <summary>
    /// Splits a buffer that holds binary transmit files one after another (the geometry streams of a SolidWorks part) at each header: PS, two zero bytes,
    /// a 16 bit length and a version text that contains "TRANSMIT FILE". A buffer with one header is returned whole.
    /// </summary>
    internal static List<byte[]> SplitStreams(byte[] data)
    {
        var starts = new List<int>();
        for (var i = 0; i + 12 < data.Length; i++)
        {
            if (data[i] == (byte)'P' && data[i + 1] == (byte)'S' && data[i + 2] == 0 && data[i + 3] == 0 && data[i + 4] == 0 && data[i + 5] is > 10 and < 120
                && data.AsSpan(i + 6, Math.Min(data.Length - i - 6, 20)).IndexOf(": TRANSMIT FILE"u8) == 0)
            {
                starts.Add(i);
            }
        }

        var result = new List<byte[]>(starts.Count);
        for (var k = 0; k < starts.Count; k++)
        {
            var end = k + 1 < starts.Count ? starts[k + 1] : data.Length;
            result.Add(data.AsSpan(starts[k], end - starts[k]).ToArray());
        }

        return result;
    }

    /// <summary>
    /// The binary transmit streams a container file holds: those stored as they are (a PS header), and those inside zlib streams, found by trying every
    /// zlib header and looking at the first bytes it inflates to. A compound file (OLE) is searched stream by stream. NX part files keep their B-rep this way.
    /// </summary>
    internal static List<byte[]> FindEmbedded(ReadOnlyMemory<byte> data, CancellationToken cancel)
    {
        var result = new List<byte[]>();
        if (Internal.CompoundFile.HasSignature(data.Span))
        {
            var file = new Internal.CompoundFile(data, cancel);
            foreach (var name in file.StreamNames.Order(StringComparer.Ordinal))
            {
                cancel.ThrowIfCancellationRequested();
                if (file.Read(name, cancel, 1L << 28) is { } bytes)
                {
                    ScanForStreams(bytes, result, cancel);
                }
            }

            return result;
        }

        ScanForStreams(data.ToArray(), result, cancel);
        return result;
    }

    private static void ScanForStreams(byte[] buffer, List<byte[]> result, CancellationToken cancel)
    {
        result.AddRange(SplitStreams(buffer));
        long inflatedTotal = 0;
        var attempts = 0;
        for (var i = 0; i + 8 < buffer.Length && attempts < 100_000 && inflatedTotal < (1L << 29); i++)
        {
            if (buffer[i] != 0x78 || buffer[i + 1] is not (0x01 or 0x5E or 0x9C or 0xDA))
            {
                continue;
            }

            Internal.Cancel.Every(cancel, attempts++, 0);
            try
            {
                using var probe = new System.IO.Compression.ZLibStream(new MemoryStream(buffer, i, buffer.Length - i, writable: false), System.IO.Compression.CompressionMode.Decompress);
                var head = new byte[32];
                var got = 0;
                while (got < head.Length)
                {
                    var n = probe.Read(head, got, head.Length - got);
                    if (n <= 0)
                    {
                        break;
                    }

                    got += n;
                }

                if (got < 16 || !(head[0] == (byte)'P' && head[1] == (byte)'S' && head[2] == 0 && head[3] == 0))
                {
                    continue;
                }

                using var full = new System.IO.Compression.ZLibStream(new MemoryStream(buffer, i, buffer.Length - i, writable: false), System.IO.Compression.CompressionMode.Decompress);
                var output = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = full.Read(chunk, 0, chunk.Length)) > 0)
                {
                    output.Write(chunk, 0, read);
                    cancel.ThrowIfCancellationRequested();
                    if (output.Length > (1L << 28))
                    {
                        break;
                    }
                }

                inflatedTotal += output.Length;
                result.AddRange(SplitStreams(output.ToArray()));
            }
            catch (InvalidDataException)
            {
            }
        }
    }

    /// <summary>
    /// Loads the Parasolid B-rep out of a container format whose geometry is a transmit stream inside it (Siemens NX, Solid Edge). The file holds several
    /// streams (the part, and the tool bodies and deltas of its features, some of them several times over): the largest one that gives geometry is the part.
    /// </summary>
    internal static Model3D ReadContainer(FormatInfo format, ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var streams = FindEmbedded(data, options.Cancellation);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Model3D? model = null;
        foreach (var stream in streams.OrderByDescending(x => x.Length))
        {
            if (!seen.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream))))
            {
                continue;
            }

            if (ReadEmbedded([stream], options) is { } found)
            {
                model = found;
                break;
            }
        }

        if (model is null)
        {
            throw new UnsupportedModelFormatException(format, $"{format.Name}: no Parasolid transmit stream that this reader understands was found in the file ({streams.Count} found).");
        }

        model.Metadata.Format = format;
        model.Metadata.Notes.Add($"The geometry is the largest of the {streams.Count} Parasolid transmit stream(s) stored in the file; the others (feature tool bodies, deltas) are not read.");
        return model;
    }

    /// <summary>
    /// Reads transmit streams found inside another format (a SolidWorks partition, the XT B-rep elements of a JT file) and merges what they hold into one
    /// model. A stream of a schema or node type that is not understood, or without a body (a ghost partition, a delta), is left out; null when no stream gave geometry.
    /// </summary>
    internal static Model3D? ReadEmbedded(IEnumerable<byte[]> streams, LoadOptions options)
    {
        Model3D? merged = null;
        foreach (var stream in streams)
        {
            options.Cancellation.ThrowIfCancellationRequested();
            Model3D part;
            try
            {
                part = Read(stream, options);
            }
            catch (ModelFormatException)
            {
                continue;
            }

            if (part.Meshes.Count == 0)
            {
                continue;
            }

            if (merged is null)
            {
                merged = part;
                continue;
            }

            var shift = merged.Materials.Count;
            merged.Materials.AddRange(part.Materials);
            foreach (var mesh in part.Meshes)
            {
                if (shift > 0)
                {
                    for (var i = 0; i < mesh.Groups.Count; i++)
                    {
                        var g = mesh.Groups[i];
                        mesh.Groups[i] = g with { Material = g.Material >= 0 ? g.Material + shift : g.Material };
                    }
                }

                merged.Meshes.Add(mesh);
            }

            merged.Roots.AddRange(part.Roots);
            merged.Warnings.AddRange(part.Warnings);
        }

        return merged;
    }

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var meta = ReadMetadata(data, options.Cancellation);
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
