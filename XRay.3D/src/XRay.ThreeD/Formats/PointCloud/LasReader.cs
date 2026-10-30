using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace XRay.ThreeD.Formats.PointCloud;

/// <summary>
/// ASPRS LAS 1.0 to 1.4, point formats 0 to 10: scaled and offset coordinates, RGB when the format carries it (else a height ramp),
/// header metadata (version, software, date, bounds, point count) and the coordinate reference system from the GeoTIFF keys or
/// the WKT record. Compressed LAZ is recognised and refused.
/// </summary>
internal static class LasReader
{
    private static readonly int[] RecordSizes = [20, 28, 26, 34, 57, 63, 30, 36, 38, 59, 67];

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var s = data.Span;
        if (s.Length < 227 || !s[..4].SequenceEqual("LASF"u8))
        {
            throw new ModelFormatException("Not a LAS file.");
        }

        var major = s[24];
        var minor = s[25];
        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(s[94..]);
        var pointOffset = BinaryPrimitives.ReadUInt32LittleEndian(s[96..]);
        var vlrCount = BinaryPrimitives.ReadUInt32LittleEndian(s[100..]);
        var formatByte = s[104];
        var format = formatByte & 0x3F;
        var compressed = (formatByte & 0x80) != 0;
        var recordLength = BinaryPrimitives.ReadUInt16LittleEndian(s[105..]);
        long count = BinaryPrimitives.ReadUInt32LittleEndian(s[107..]);
        if (major == 1 && minor >= 4 && s.Length >= 375)
        {
            var big = (long)BinaryPrimitives.ReadUInt64LittleEndian(s[247..]);
            if (big > 0)
            {
                count = big;
            }
        }

        double sx = D(s, 131), sy = D(s, 139), sz = D(s, 147);
        double ox = D(s, 155), oy = D(s, 163), oz = D(s, 171);

        var model = new Model3D();
        var meta = model.Metadata;
        meta.Format = ModelFormats.Las;
        meta.DocumentKind = "pointcloud";
        meta.FormatVersion = $"{major}.{minor}";
        meta.Application = Text(s.Slice(58, 32));
        meta.Properties.Add(new ModelProperty("System identifier", Text(s.Slice(26, 32)), "las"));
        meta.Properties.Add(new ModelProperty("Point data format", format.ToString(CultureInfo.InvariantCulture), "las"));
        var day = BinaryPrimitives.ReadUInt16LittleEndian(s[90..]);
        var year = BinaryPrimitives.ReadUInt16LittleEndian(s[92..]);
        if (year is > 1980 and < 2200 && day is >= 1 and <= 366)
        {
            meta.Created = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(day - 1);
        }

        ReadVlrs(s, headerSize, vlrCount, meta);

        if (compressed || meta.Properties.Any(p => p is { Name: "VLR", Value: "laszip encoded" }))
        {
            throw new UnsupportedModelFormatException(ModelFormats.Las, "Compressed LAZ point data is not supported yet; the header metadata is available through ReadMetadata.");
        }

        if (format >= RecordSizes.Length || recordLength < RecordSizes[format])
        {
            throw new ModelFormatException("The LAS point data format or record length is invalid.");
        }

        if (count <= 0 || count > 4_000_000_000L || pointOffset + count * recordLength > s.Length)
        {
            // Truncated files still hold the points that are present.
            var available = (s.Length - pointOffset) / Math.Max(1, (int)recordLength);
            if (available <= 0)
            {
                throw new ModelFormatException("The LAS file holds no point records.");
            }

            count = Math.Min(count > 0 ? count : available, available);
        }

        var hasRgb = format is 2 or 3 or 5 or 7 or 8 or 10;
        var rgbOffset = format switch { 2 => 20, 3 => 28, 5 => 28, _ => 30 };
        var stride = options.MaxPoints > 0 && count > options.MaxPoints ? (int)Math.Ceiling(count / (double)options.MaxPoints) : 1;
        var kept = (int)((count + stride - 1) / stride);
        var xyz = new double[kept * 3];
        var colors = hasRgb ? new float[kept * 4] : null;
        var sixteenBitColours = false;
        var o = 0;
        for (long i = 0; i < count; i += stride)
        {
            var record = s.Slice((int)(pointOffset + i * recordLength), recordLength);
            xyz[3 * o] = BinaryPrimitives.ReadInt32LittleEndian(record) * sx + ox;
            xyz[3 * o + 1] = BinaryPrimitives.ReadInt32LittleEndian(record[4..]) * sy + oy;
            xyz[3 * o + 2] = BinaryPrimitives.ReadInt32LittleEndian(record[8..]) * sz + oz;
            if (colors is not null)
            {
                int r = BinaryPrimitives.ReadUInt16LittleEndian(record[rgbOffset..]);
                int g = BinaryPrimitives.ReadUInt16LittleEndian(record[(rgbOffset + 2)..]);
                int b = BinaryPrimitives.ReadUInt16LittleEndian(record[(rgbOffset + 4)..]);
                sixteenBitColours |= r > 255 || g > 255 || b > 255;
                colors[4 * o] = r;
                colors[4 * o + 1] = g;
                colors[4 * o + 2] = b;
                colors[4 * o + 3] = 1;
            }

            o++;
        }

        if (colors is not null)
        {
            // The standard stores 16-bit channels, but many writers put 8-bit values there.
            var divisor = sixteenBitColours ? 65535f : 255f;
            for (var i = 0; i < kept; i++)
            {
                colors[4 * i] /= divisor;
                colors[4 * i + 1] /= divisor;
                colors[4 * i + 2] /= divisor;
            }
        }

        var built = PointCloudCommon.Build(ModelFormats.Las, xyz, colors ?? PointCloudCommon.HeightRamp(xyz), options);
        built.Metadata.FormatVersion = meta.FormatVersion;
        built.Metadata.Application = meta.Application;
        built.Metadata.Created = meta.Created;
        built.Metadata.Properties.InsertRange(0, meta.Properties);
        built.Metadata.Properties.RemoveAll(p => p is { Name: "points", Group: "cloud" });
        built.Metadata.Properties.Add(new ModelProperty("points", count.ToString(CultureInfo.InvariantCulture), "cloud"));
        if (stride > 1)
        {
            built.Metadata.Notes.Add($"The cloud was thinned to every {stride}. point ({kept:N0} of {count:N0}).");
            built.Warnings.Add(built.Metadata.Notes[^1]);
        }

        built.Metadata.Units = LengthUnit.Meter; // nominal; the CRS property says what the units really are
        return built;
    }

    /// <summary>Header metadata only, without reading any point.</summary>
    public static ModelMetadata ReadMetadata(ReadOnlyMemory<byte> data)
    {
        var s = data.Span;
        if (s.Length < 227 || !s[..4].SequenceEqual("LASF"u8))
        {
            throw new ModelFormatException("Not a LAS file.");
        }

        var meta = new ModelMetadata { Format = ModelFormats.Las, DocumentKind = "pointcloud", FormatVersion = $"{s[24]}.{s[25]}", Application = Text(s.Slice(58, 32)), HasGeometry = true };
        long count = BinaryPrimitives.ReadUInt32LittleEndian(s[107..]);
        if (s[25] >= 4 && s.Length >= 375 && (long)BinaryPrimitives.ReadUInt64LittleEndian(s[247..]) is > 0 and var big)
        {
            count = big;
        }

        meta.Properties.Add(new ModelProperty("points", count.ToString(CultureInfo.InvariantCulture), "cloud"));
        meta.Properties.Add(new ModelProperty("Point data format", (s[104] & 0x3F).ToString(CultureInfo.InvariantCulture), "las"));
        ReadVlrs(s, BinaryPrimitives.ReadUInt16LittleEndian(s[94..]), BinaryPrimitives.ReadUInt32LittleEndian(s[100..]), meta);
        meta.Bounds = new BoundingBox(new System.Numerics.Vector3((float)D(s, 187), (float)D(s, 203), (float)D(s, 219)), new System.Numerics.Vector3((float)D(s, 179), (float)D(s, 195), (float)D(s, 211)));
        meta.Notes.Add("Bounds are in the file's coordinates.");
        if ((s[104] & 0x80) != 0)
        {
            meta.Notes.Add("The point data is LAZ compressed.");
        }

        return meta;
    }

    private static double D(ReadOnlySpan<byte> s, int offset) => BinaryPrimitives.ReadDoubleLittleEndian(s[offset..]);

    private static string Text(ReadOnlySpan<byte> bytes) => Encoding.ASCII.GetString(bytes).TrimEnd('\0', ' ');

    private static void ReadVlrs(ReadOnlySpan<byte> s, int headerSize, uint count, ModelMetadata meta)
    {
        var position = headerSize;
        for (uint i = 0; i < count && position + 54 <= s.Length; i++)
        {
            var user = Text(s.Slice(position + 2, 16));
            var id = BinaryPrimitives.ReadUInt16LittleEndian(s[(position + 18)..]);
            int length = BinaryPrimitives.ReadUInt16LittleEndian(s[(position + 20)..]);
            var body = position + 54;
            if (body + length > s.Length)
            {
                break;
            }

            var record = s.Slice(body, length);
            if (user == "LASF_Projection" && id == 34735 && length >= 8)
            {
                // GeoKeyDirectory: header of four shorts, then (key, location, count, value) entries.
                var keys = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
                for (var k = 0; k < keys && 8 + 8 * k + 8 <= length; k++)
                {
                    var key = BinaryPrimitives.ReadUInt16LittleEndian(record[(8 + 8 * k)..]);
                    var value = BinaryPrimitives.ReadUInt16LittleEndian(record[(8 + 8 * k + 6)..]);
                    if (key == 3072)
                    {
                        meta.Properties.Add(new ModelProperty("CRS", "EPSG:" + value.ToString(CultureInfo.InvariantCulture), "las"));
                    }
                    else if (key == 2048)
                    {
                        meta.Properties.Add(new ModelProperty("CRS (geographic)", "EPSG:" + value.ToString(CultureInfo.InvariantCulture), "las"));
                    }
                }
            }
            else if (user == "LASF_Projection" && id is 2111 or 2112)
            {
                meta.Properties.Add(new ModelProperty("CRS WKT", Text(record), "las"));
            }
            else if (user == "laszip encoded")
            {
                meta.Properties.Add(new ModelProperty("VLR", "laszip encoded", "las"));
            }

            position = body + length;
        }
    }
}
