using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace XRay.ThreeD.Formats.PointCloud;

/// <summary>Point Cloud Library .pcd: ASCII, binary and binary_compressed (LZF); x y z, packed rgb/rgba, normals are ignored.</summary>
internal static class PcdReader
{
    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var span = data.Span;
        var fields = new List<string>();
        var sizes = new List<int>();
        var types = new List<char>();
        var counts = new List<int>();
        long points = 0;
        string dataKind = "";
        string? version = null;
        var position = 0;
        while (true)
        {
            var end = span[position..].IndexOf((byte)'\n');
            if (end < 0 || position > 1 << 16)
            {
                throw new ModelFormatException("The PCD header is not terminated.");
            }

            var line = Encoding.ASCII.GetString(span.Slice(position, end)).Trim('\r', ' ');
            position += end + 1;
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0].ToUpperInvariant())
            {
                case "VERSION": version = parts.ElementAtOrDefault(1); break;
                case "FIELDS" or "COLUMNS": fields = parts.Skip(1).ToList(); break;
                case "SIZE": sizes = parts.Skip(1).Select(int.Parse).ToList(); break;
                case "TYPE": types = parts.Skip(1).Select(t => t[0]).ToList(); break;
                case "COUNT": counts = parts.Skip(1).Select(int.Parse).ToList(); break;
                case "POINTS": points = long.Parse(parts[1], CultureInfo.InvariantCulture); break;
                case "DATA": dataKind = parts[1].ToLowerInvariant(); break;
            }

            if (dataKind.Length > 0)
            {
                break;
            }
        }

        if (counts.Count == 0)
        {
            counts = fields.Select(_ => 1).ToList();
        }

        if (fields.Count == 0 || sizes.Count != fields.Count || types.Count != fields.Count || counts.Count != fields.Count)
        {
            throw new ModelFormatException("The PCD header fields are inconsistent.");
        }

        if (points <= 0 || points > 500_000_000)
        {
            throw new ModelFormatException("The PCD declares an impossible point count.");
        }

        var rowSize = 0;
        var offsets = new int[fields.Count];
        for (var f = 0; f < fields.Count; f++)
        {
            offsets[f] = rowSize;
            rowSize += sizes[f] * counts[f];
        }

        var xi = fields.IndexOf("x");
        var yi = fields.IndexOf("y");
        var zi = fields.IndexOf("z");
        if (xi < 0 || yi < 0 || zi < 0)
        {
            throw new ModelFormatException("The PCD has no x, y, z fields.");
        }

        var rgbIndex = fields.IndexOf("rgb") >= 0 ? fields.IndexOf("rgb") : fields.IndexOf("rgba");
        var n = (int)points;
        var xyz = new double[n * 3];
        var colors = rgbIndex >= 0 ? new float[n * 4] : null;
        var kept = 0;

        double Read(ReadOnlySpan<byte> row, int field) => sizes[field] switch
        {
            4 when types[field] == 'F' => BinaryPrimitives.ReadSingleLittleEndian(row[offsets[field]..]),
            8 when types[field] == 'F' => BinaryPrimitives.ReadDoubleLittleEndian(row[offsets[field]..]),
            4 when types[field] == 'I' => BinaryPrimitives.ReadInt32LittleEndian(row[offsets[field]..]),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(row[offsets[field]..]),
            2 when types[field] == 'I' => BinaryPrimitives.ReadInt16LittleEndian(row[offsets[field]..]),
            2 => BinaryPrimitives.ReadUInt16LittleEndian(row[offsets[field]..]),
            1 when types[field] == 'I' => (sbyte)row[offsets[field]],
            1 => row[offsets[field]],
            _ => throw new ModelFormatException("The PCD uses an unsupported field size."),
        };

        void SetColor(int index, uint packed)
        {
            colors![4 * index] = ((packed >> 16) & 255) / 255f;
            colors[4 * index + 1] = ((packed >> 8) & 255) / 255f;
            colors[4 * index + 2] = (packed & 255) / 255f;
            colors[4 * index + 3] = 1;
        }

        if (dataKind == "ascii")
        {
            var text = Encoding.ASCII.GetString(span[position..]);
            foreach (var raw in text.AsSpan().EnumerateLines())
            {
                if (kept >= n)
                {
                    break;
                }

                var tokens = raw.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < fields.Count)
                {
                    continue;
                }

                // Column of each field: counts expand to several tokens.
                var column = 0;
                var columns = new int[fields.Count];
                for (var f = 0; f < fields.Count; f++)
                {
                    columns[f] = column;
                    column += counts[f];
                }

                xyz[3 * kept] = double.Parse(tokens[columns[xi]], CultureInfo.InvariantCulture);
                xyz[3 * kept + 1] = double.Parse(tokens[columns[yi]], CultureInfo.InvariantCulture);
                xyz[3 * kept + 2] = double.Parse(tokens[columns[zi]], CultureInfo.InvariantCulture);
                if (colors is not null)
                {
                    var token = tokens[columns[rgbIndex]];
                    var packed = types[rgbIndex] == 'F' ? BitConverter.SingleToUInt32Bits(float.Parse(token, CultureInfo.InvariantCulture)) : (uint)double.Parse(token, CultureInfo.InvariantCulture);
                    SetColor(kept, packed);
                }

                kept++;
            }
        }
        else if (dataKind is "binary" or "binary_compressed")
        {
            byte[] body;
            if (dataKind == "binary_compressed")
            {
                var compressed = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(position, 4));
                var uncompressed = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(position + 4, 4));
                if (uncompressed != (long)rowSize * n || compressed > span.Length - position - 8)
                {
                    throw new ModelFormatException("The PCD compressed block sizes are inconsistent.");
                }

                var planar = Lzf.Decompress(span.Slice(position + 8, (int)compressed), (int)uncompressed);
                // Planar (structure of arrays) to rows.
                body = new byte[planar.Length];
                var planeStart = 0;
                for (var f = 0; f < fields.Count; f++)
                {
                    var width = sizes[f] * counts[f];
                    for (var i = 0; i < n; i++)
                    {
                        Array.Copy(planar, planeStart + i * width, body, i * rowSize + offsets[f], width);
                    }

                    planeStart += width * n;
                }
            }
            else
            {
                if ((long)rowSize * n > span.Length - position)
                {
                    throw new ModelFormatException("The PCD body is shorter than its header says.");
                }

                body = span.Slice(position, rowSize * n).ToArray();
            }

            for (var i = 0; i < n; i++)
            {
                var row = body.AsSpan(i * rowSize, rowSize);
                xyz[3 * kept] = Read(row, xi);
                xyz[3 * kept + 1] = Read(row, yi);
                xyz[3 * kept + 2] = Read(row, zi);
                if (colors is not null)
                {
                    SetColor(kept, types[rgbIndex] == 'F' && sizes[rgbIndex] == 4 ? BitConverter.SingleToUInt32Bits((float)Read(row, rgbIndex)) : (uint)Read(row, rgbIndex));
                }

                kept++;
            }
        }
        else
        {
            throw new ModelFormatException($"Unknown PCD data kind '{dataKind}'.");
        }

        if (kept == 0)
        {
            throw new ModelFormatException("The PCD holds no points.");
        }

        Array.Resize(ref xyz, kept * 3);
        if (colors is not null)
        {
            Array.Resize(ref colors, kept * 4);
        }

        colors ??= PointCloudCommon.HeightRamp(xyz);
        var model = PointCloudCommon.Build(ModelFormats.Pcd, xyz, colors, options);
        model.Metadata.FormatVersion = version;
        model.Metadata.Properties.Add(new ModelProperty("fields", string.Join(' ', fields), "cloud"));
        return model;
    }
}

/// <summary>LZF decompression (the scheme of libLZF as used by PCL's binary_compressed files).</summary>
internal static class Lzf
{
    public static byte[] Decompress(ReadOnlySpan<byte> input, int outputLength)
    {
        var output = new byte[outputLength];
        var i = 0;
        var o = 0;
        while (i < input.Length)
        {
            int control = input[i++];
            if (control < 32)
            {
                var length = control + 1;
                if (i + length > input.Length || o + length > output.Length)
                {
                    throw new ModelFormatException("A compressed LZF literal run is out of range.");
                }

                input.Slice(i, length).CopyTo(output.AsSpan(o));
                i += length;
                o += length;
            }
            else
            {
                var length = control >> 5;
                if (length == 7)
                {
                    if (i >= input.Length)
                    {
                        throw new ModelFormatException("An LZF back reference is truncated.");
                    }

                    length += input[i++];
                }

                if (i >= input.Length)
                {
                    throw new ModelFormatException("An LZF back reference is truncated.");
                }

                var reference = o - ((control & 0x1F) << 8) - 1 - input[i++];
                length += 2;
                if (reference < 0 || o + length > output.Length)
                {
                    throw new ModelFormatException("An LZF back reference is out of range.");
                }

                for (var k = 0; k < length; k++)
                {
                    output[o++] = output[reference++];
                }
            }
        }

        if (o != outputLength)
        {
            throw new ModelFormatException("The LZF block decompressed to the wrong size.");
        }

        return output;
    }
}
