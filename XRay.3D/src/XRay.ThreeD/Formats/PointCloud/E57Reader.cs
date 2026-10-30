using System.Buffers.Binary;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace XRay.ThreeD.Formats.PointCloud;

using XRay.ThreeD.Internal;

/// <summary>
/// ASTM E57 3D imaging files: the 48-byte header, the paged layout (1020 payload bytes and a big-endian CRC-32C per 1024-byte page; logical
/// offsets skip the checksums), the XML section (scans in <c>data3D</c> with name, GUID, pose, bounds and limits; <c>images2D</c> with embedded
/// JPEG or PNG blobs), and the binary section of each scan's <c>points</c> CompressedVector: data packets holding one byte stream per prototype
/// field, coded with the default bit-pack codec (integers and scaled integers in the bit width of their range, least significant bit first;
/// floats as IEEE single or double). Cartesian or spherical coordinates, colours and intensity are decoded; scan poses are applied, so several
/// scans share one frame. Other codecs, other point attributes and point-grouping tables are not read.
/// </summary>
internal static class E57Reader
{
    private const int HeaderSize = 48;
    private const int MaxScans = 4096;

    public static bool Sniff(ReadOnlySpan<byte> d) => d.Length >= HeaderSize && d[..8].SequenceEqual("ASTM-E57"u8);

    // ---- paged file --------------------------------------------------------------------------------------------------------

    private sealed class Paged
    {
        private readonly ReadOnlyMemory<byte> _data;
        public readonly int PageSize;
        public readonly int Payload;

        public Paged(ReadOnlyMemory<byte> data, int pageSize)
        {
            _data = data;
            PageSize = pageSize;
            Payload = pageSize - 4;
        }

        public long LogicalLength => (_data.Length / PageSize) * (long)Payload + Math.Max(0, _data.Length % PageSize - 4);

        public long ToLogical(long physical)
        {
            var offset = physical % PageSize;
            if (offset >= Payload)
            {
                throw new ModelFormatException("An E57 offset points into a page checksum.");
            }

            return physical / PageSize * Payload + offset;
        }

        public byte[] Read(long logical, long count)
        {
            if (logical < 0 || count < 0 || count > int.MaxValue || logical + count > LogicalLength)
            {
                throw new ModelFormatException("An E57 section runs past the end of the file.");
            }

            var result = new byte[count];
            var span = _data.Span;
            long done = 0;
            while (done < count)
            {
                var position = logical + done;
                var page = position / Payload;
                var inPage = (int)(position % Payload);
                var take = (int)Math.Min(Payload - inPage, count - done);
                span.Slice((int)(page * PageSize + inPage), take).CopyTo(result.AsSpan((int)done));
                done += take;
            }

            return result;
        }

        /// <summary>Number of pages whose checksum does not match.</summary>
        public int CountBadPages(CancellationToken token)
        {
            var bad = 0;
            var span = _data.Span;
            var pages = span.Length / PageSize;
            for (var p = 0; p < pages; p++)
            {
                Cancel.Every(token, p, 63);
                var page = span.Slice(p * PageSize, PageSize);
                if (Crc32C(page[..Payload]) != BinaryPrimitives.ReadUInt32BigEndian(page[Payload..]))
                {
                    bad++;
                }
            }

            return bad;
        }
    }

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? (c >> 1) ^ 0x82F63B78u : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }

    internal static uint Crc32C(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        }

        return ~c;
    }

    // ---- scan description ---------------------------------------------------------------------------------------------------

    private enum Kind
    {
        Ignore, X, Y, Z, Range, Azimuth, Elevation, Red, Green, Blue, Intensity, CartesianInvalid, SphericalInvalid,
    }

    private sealed class Field
    {
        public Kind Kind;
        public string Type = "";
        public long Min, Max;
        public double Scale = 1, Offset;
        public int Bits;
        public bool Single;
        public double FMin, FMax;
        public byte[] Bytes = [];
        public int Length;
    }

    private sealed class Scan
    {
        public string Name = "";
        public string Guid = "";
        public string Description = "";
        public long Records;
        public long FileOffset = -1;
        public List<Field> Fields = new();
        public double[]? Quaternion;
        public double[]? Translation;
        public double[]? Bounds;
        public double ColorMin = 0, ColorMax = 255;
        public double IntensityMin = double.NaN, IntensityMax = double.NaN;
        public Dictionary<string, string> Info = new(StringComparer.Ordinal);
    }

    private static XElement? Child(XElement e, string local) => e.Elements().FirstOrDefault(c => c.Name.LocalName == local);

    private static double Num(XElement? e, double fallback = 0)
    {
        if (e is null)
        {
            return fallback;
        }

        var text = e.Value.Trim();
        if (text.Length == 0)
        {
            return string.Equals((string?)e.Attribute("type"), "Float", StringComparison.Ordinal) || string.Equals((string?)e.Attribute("type"), "Integer", StringComparison.Ordinal) ? 0 : fallback;
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    private static long Int(string? s, long fallback) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static double Dbl(string? s, double fallback) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private static XDocument ParseXml(byte[] xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1 << 26 };
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(xml, writable: false), settings);
            return XDocument.Load(reader, System.Xml.Linq.LoadOptions.None);
        }
        catch (XmlException e)
        {
            throw new ModelFormatException("The E57 XML section is damaged: " + e.Message);
        }
    }

    private static Kind KindOf(string name) => name switch
    {
        "cartesianX" => Kind.X,
        "cartesianY" => Kind.Y,
        "cartesianZ" => Kind.Z,
        "sphericalRange" => Kind.Range,
        "sphericalAzimuth" => Kind.Azimuth,
        "sphericalElevation" => Kind.Elevation,
        "colorRed" => Kind.Red,
        "colorGreen" => Kind.Green,
        "colorBlue" => Kind.Blue,
        "intensity" => Kind.Intensity,
        "cartesianInvalidState" => Kind.CartesianInvalid,
        "sphericalInvalidState" => Kind.SphericalInvalid,
        _ => Kind.Ignore,
    };

    private static int BitsFor(long min, long max)
    {
        var range = (ulong)(max - min);
        var bits = 0;
        while (bits < 64 && (range >> bits) != 0)
        {
            bits++;
        }

        return bits;
    }

    private static Scan ReadScan(XElement e)
    {
        var scan = new Scan
        {
            Name = Child(e, "name")?.Value ?? "",
            Guid = Child(e, "guid")?.Value ?? "",
            Description = Child(e, "description")?.Value ?? "",
        };
        foreach (var key in new[] { "sensorVendor", "sensorModel", "sensorSerialNumber", "sensorHardwareVersion", "sensorSoftwareVersion", "sensorFirmwareVersion", "temperature", "relativeHumidity", "atmosphericPressure" })
        {
            if (Child(e, key) is { } c && c.Value.Trim().Length > 0)
            {
                scan.Info[key] = c.Value.Trim();
            }
        }

        if (Child(e, "pose") is { } pose)
        {
            var q = Child(pose, "rotation");
            var t = Child(pose, "translation");
            if (q is not null)
            {
                scan.Quaternion = [Num(Child(q, "w"), 1), Num(Child(q, "x")), Num(Child(q, "y")), Num(Child(q, "z"))];
            }

            if (t is not null)
            {
                scan.Translation = [Num(Child(t, "x")), Num(Child(t, "y")), Num(Child(t, "z"))];
            }
        }

        if (Child(e, "cartesianBounds") is { } b)
        {
            scan.Bounds = new[] { "xMinimum", "xMaximum", "yMinimum", "yMaximum", "zMinimum", "zMaximum" }.Select(k => Num(Child(b, k), double.NaN)).ToArray();
        }

        if (Child(e, "colorLimits") is { } cl)
        {
            scan.ColorMin = Num(Child(cl, "colorRedMinimum"), 0);
            scan.ColorMax = Num(Child(cl, "colorRedMaximum"), 255);
        }

        if (Child(e, "intensityLimits") is { } il)
        {
            scan.IntensityMin = Num(Child(il, "intensityMinimum"), 0);
            scan.IntensityMax = Num(Child(il, "intensityMaximum"), 1);
            // Scaled integers state their limits as raw values with a scale: the element's own text is already scaled? No: it is the raw integer.
            if (Child(il, "intensityMaximum") is { } imax && (string?)imax.Attribute("type") == "ScaledInteger")
            {
                var scale = Dbl((string?)imax.Attribute("scale"), 1);
                var offset = Dbl((string?)imax.Attribute("offset"), 0);
                scan.IntensityMax = Num(imax) * scale + offset;
                scan.IntensityMin = Num(Child(il, "intensityMinimum")) * scale + offset;
            }
        }

        if (Child(e, "points") is { } points && (string?)points.Attribute("type") == "CompressedVector")
        {
            scan.Records = Int((string?)points.Attribute("recordCount"), 0);
            scan.FileOffset = Int((string?)points.Attribute("fileOffset"), -1);
            if (Child(points, "codecs")?.Elements().Any() == true)
            {
                throw new UnsupportedModelFormatException(ModelFormats.E57, "This E57 file names a codec for its points; only the default bit-pack codec is supported.");
            }

            if (Child(points, "prototype") is { } proto)
            {
                foreach (var f in proto.Elements())
                {
                    var type = (string?)f.Attribute("type") ?? "";
                    var field = new Field { Kind = KindOf(f.Name.LocalName), Type = type };
                    switch (type)
                    {
                        case "Integer":
                            field.Min = Int((string?)f.Attribute("minimum"), long.MinValue);
                            field.Max = Int((string?)f.Attribute("maximum"), long.MaxValue);
                            field.Bits = BitsFor(field.Min, field.Max);
                            break;
                        case "ScaledInteger":
                            field.Min = Int((string?)f.Attribute("minimum"), long.MinValue);
                            field.Max = Int((string?)f.Attribute("maximum"), long.MaxValue);
                            field.Scale = Dbl((string?)f.Attribute("scale"), 1);
                            field.Offset = Dbl((string?)f.Attribute("offset"), 0);
                            field.Bits = BitsFor(field.Min, field.Max);
                            break;
                        case "Float":
                            field.Single = string.Equals((string?)f.Attribute("precision"), "single", StringComparison.Ordinal);
                            field.Bits = field.Single ? 32 : 64;
                            field.FMin = Dbl((string?)f.Attribute("minimum"), double.NegativeInfinity);
                            field.FMax = Dbl((string?)f.Attribute("maximum"), double.PositiveInfinity);
                            break;
                        default:
                            throw new UnsupportedModelFormatException(ModelFormats.E57, $"An E57 point field of type {type} is not supported.");
                    }

                    scan.Fields.Add(field);
                }
            }
        }

        return scan;
    }

    // ---- file --------------------------------------------------------------------------------------------------------------

    private sealed class Parsed
    {
        public Paged File = null!;
        public XDocument Xml = null!;
        public XElement Root = null!;
        public List<Scan> Scans = new();
        public int BadPages;
    }

    private static Parsed Parse(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var s = data.Span;
        if (!Sniff(s))
        {
            throw new ModelFormatException("Not an E57 file.");
        }

        var major = BinaryPrimitives.ReadUInt32LittleEndian(s[8..]);
        var xmlOffset = BinaryPrimitives.ReadUInt64LittleEndian(s[24..]);
        var xmlLength = BinaryPrimitives.ReadUInt64LittleEndian(s[32..]);
        var pageSize = BinaryPrimitives.ReadUInt64LittleEndian(s[40..]);
        if (major != 1 || pageSize < 8 || pageSize > 1 << 20 || xmlLength == 0 || xmlLength > 1 << 26 || xmlOffset > (ulong)data.Length)
        {
            throw new ModelFormatException("The E57 header is invalid.");
        }

        var file = new Paged(data, (int)pageSize);
        var parsed = new Parsed { File = file, BadPages = file.CountBadPages(options.Cancellation) };
        var xml = file.Read(file.ToLogical((long)xmlOffset), (long)xmlLength);
        parsed.Xml = ParseXml(xml);
        parsed.Root = parsed.Xml.Root ?? throw new ModelFormatException("The E57 XML section is empty.");
        if (parsed.Root.Name.LocalName != "e57Root")
        {
            throw new ModelFormatException("The E57 XML section has no e57Root.");
        }

        if (Child(parsed.Root, "data3D") is { } scans)
        {
            foreach (var e in scans.Elements().Take(MaxScans))
            {
                Cancel.Every(options.Cancellation, parsed.Scans.Count, 15);
                parsed.Scans.Add(ReadScan(e));
            }
        }

        return parsed;
    }

    private static void FillMetadata(Parsed p, ModelMetadata meta)
    {
        meta.Format = ModelFormats.E57;
        meta.DocumentKind = "pointcloud";
        var root = p.Root;
        meta.FormatVersion = $"{(string?)Child(root, "versionMajor")?.Value.Trim().NullIfEmpty() ?? "1"}.{Child(root, "versionMinor")?.Value.Trim().NullIfEmpty() ?? "0"}";
        meta.Application = Child(root, "e57LibraryVersion")?.Value.Trim().NullIfEmpty();
        meta.Units = LengthUnit.Meter;
        if (Child(root, "guid")?.Value is { Length: > 0 } guid)
        {
            meta.Properties.Add(new ModelProperty("GUID", guid.Trim(), "e57"));
        }

        if (Child(root, "coordinateMetadata")?.Value.Trim() is { Length: > 0 } crs)
        {
            meta.Properties.Add(new ModelProperty("CRS", crs, "e57"));
        }

        if (Child(root, "creationDateTime") is { } created)
        {
            // GPS seconds since 1980-01-06 (leap seconds are not applied: the value is only used for display).
            var seconds = Num(Child(created, "dateTimeValue"), double.NaN);
            if (double.IsFinite(seconds) && seconds > 0 && seconds < 1e10)
            {
                meta.Created = new DateTimeOffset(1980, 1, 6, 0, 0, 0, TimeSpan.Zero).AddSeconds(seconds);
            }
        }

        long total = 0;
        var index = 0;
        foreach (var scan in p.Scans)
        {
            index++;
            total += scan.Records;
            var name = scan.Name.Length > 0 ? scan.Name : "Scan " + index.ToString(CultureInfo.InvariantCulture);
            meta.PartNames.Add(name);
            var group = "scan " + index.ToString(CultureInfo.InvariantCulture);
            meta.Properties.Add(new ModelProperty("name", name, group));
            meta.Properties.Add(new ModelProperty("points", scan.Records.ToString(CultureInfo.InvariantCulture), group));
            if (scan.Guid.Length > 0)
            {
                meta.Properties.Add(new ModelProperty("GUID", scan.Guid, group));
            }

            if (scan.Description.Length > 0)
            {
                meta.Properties.Add(new ModelProperty("description", scan.Description, group));
            }

            foreach (var (k, v) in scan.Info)
            {
                meta.Properties.Add(new ModelProperty(k, v, group));
            }

            var kinds = scan.Fields.Where(f => f.Kind != Kind.Ignore).Select(f => f.Kind.ToString().ToLowerInvariant());
            meta.Properties.Add(new ModelProperty("fields", string.Join(",", scan.Fields.Select(f => f.Kind == Kind.Ignore ? "other" : f.Kind.ToString().ToLowerInvariant())), group));
            _ = kinds;
            if (scan.Translation is { } t)
            {
                meta.Properties.Add(new ModelProperty("pose translation", string.Join(" ", t.Select(v => v.ToString("R", CultureInfo.InvariantCulture))), group));
            }

            if (scan.Quaternion is { } q)
            {
                meta.Properties.Add(new ModelProperty("pose rotation (w x y z)", string.Join(" ", q.Select(v => v.ToString("R", CultureInfo.InvariantCulture))), group));
            }
        }

        meta.Properties.Add(new ModelProperty("points", total.ToString(CultureInfo.InvariantCulture), "cloud"));
        meta.Properties.Add(new ModelProperty("scans", p.Scans.Count.ToString(CultureInfo.InvariantCulture), "cloud"));
        if (Child(root, "images2D")?.Elements().Count() is > 0 and var images)
        {
            meta.Properties.Add(new ModelProperty("images", images.ToString(CultureInfo.InvariantCulture), "e57"));
        }

        if (p.BadPages > 0)
        {
            meta.Notes.Add($"{p.BadPages} page(s) failed their CRC-32C check; the data may be damaged.");
        }

        var boxes = p.Scans.Where(sc => sc.Bounds is { } b && b.All(double.IsFinite)).ToList();
        if (boxes.Count > 0 && boxes.All(sc => sc.Translation is null && sc.Quaternion is null) && meta.Bounds is null)
        {
            var lo = new System.Numerics.Vector3((float)boxes.Min(sc => sc.Bounds![0]), (float)boxes.Min(sc => sc.Bounds![2]), (float)boxes.Min(sc => sc.Bounds![4]));
            var hi = new System.Numerics.Vector3((float)boxes.Max(sc => sc.Bounds![1]), (float)boxes.Max(sc => sc.Bounds![3]), (float)boxes.Max(sc => sc.Bounds![5]));
            meta.Bounds = new BoundingBox(lo, hi);
        }
    }

    private static string? NullIfEmpty(this string? s) => string.IsNullOrEmpty(s) ? null : s;

    public static ModelMetadata ReadMetadata(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var p = Parse(data, options);
        var meta = new ModelMetadata { HasGeometry = p.Scans.Any(s => s.Records > 0) };
        FillMetadata(p, meta);
        meta.Notes.Add("Bounds are in the scans' own coordinates, before their poses.");
        meta.HasPreview = FindImage(p) is not null;
        return meta;
    }

    private static PreviewImage? FindImage(Parsed p)
    {
        if (Child(p.Root, "images2D") is not { } images)
        {
            return null;
        }

        foreach (var image in images.Elements())
        {
            foreach (var representation in image.Elements())
            {
                foreach (var blobName in new[] { "jpegImage", "pngImage" })
                {
                    if (Child(representation, blobName) is { } blob && (string?)blob.Attribute("type") == "Blob")
                    {
                        var offset = Int((string?)blob.Attribute("fileOffset"), -1);
                        var length = Int((string?)blob.Attribute("length"), -1);
                        if (offset < 0 || length <= 0 || length > 1 << 26)
                        {
                            continue;
                        }

                        try
                        {
                            // The blob section starts with a 16-byte header (id, reserved, section length).
                            var bytes = p.File.Read(p.File.ToLogical(offset) + 16, length);
                            return new PreviewImage(blobName == "jpegImage" ? "image/jpeg" : "image/png", bytes);
                        }
                        catch (ModelFormatException)
                        {
                        }
                    }
                }
            }
        }

        return null;
    }

    public static PreviewImage? ReadPreview(ReadOnlyMemory<byte> data, LoadOptions options) => FindImage(Parse(data, options));

    // ---- binary section ----------------------------------------------------------------------------------------------------

    private static void ReadPackets(Paged file, Scan scan, LoadOptions options)
    {
        var token = options.Cancellation;
        var sectionStart = file.ToLogical(scan.FileOffset);
        var header = file.Read(sectionStart, 32);
        if (header[0] != 1)
        {
            throw new ModelFormatException("An E57 points section has the wrong section id.");
        }

        var sectionLength = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(8));
        var dataOffset = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(16));
        var position = file.ToLogical((long)dataOffset);
        var end = Math.Min(sectionStart + (long)Math.Min(sectionLength, long.MaxValue / 2), file.LogicalLength);
        var streams = scan.Fields.Count;
        var buffers = new MemoryStream[streams];
        for (var i = 0; i < streams; i++)
        {
            buffers[i] = new MemoryStream();
        }

        long tick = 0;
        while (position + 4 <= end)
        {
            Cancel.Every(token, tick++, 255);
            var head = file.Read(position, 4);
            var length = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(2)) + 1;
            if (position + length > end || length < 4)
            {
                break;
            }

            if (head[0] == 1)
            {
                var packet = file.Read(position, length);
                var count = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(4));
                if (count != streams || 6 + 2 * count > length)
                {
                    throw new ModelFormatException("An E57 data packet does not match the prototype.");
                }

                var at = 6 + 2 * count;
                for (var i = 0; i < count; i++)
                {
                    var size = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(6 + 2 * i));
                    if (at + size > length)
                    {
                        throw new ModelFormatException("An E57 byte stream runs past its packet.");
                    }

                    buffers[i].Write(packet, at, size);
                    at += size;
                }
            }
            else if (head[0] is not (0 or 2))
            {
                throw new ModelFormatException("An E57 packet has an unknown type.");
            }

            position += length;
        }

        for (var i = 0; i < streams; i++)
        {
            scan.Fields[i].Bytes = buffers[i].GetBuffer();
            scan.Fields[i].Length = (int)buffers[i].Length;
        }
    }

    private static ulong ReadBits(byte[] buffer, long bitPosition, int bits)
    {
        ulong value = 0;
        var got = 0;
        var position = bitPosition;
        while (got < bits)
        {
            var index = (int)(position >> 3);
            var offset = (int)(position & 7);
            var take = Math.Min(8 - offset, bits - got);
            var chunk = (ulong)((buffer[index] >> offset) & ((1 << take) - 1));
            value |= chunk << got;
            got += take;
            position += take;
        }

        return value;
    }

    private static double Value(Field f, long record)
    {
        var position = record * f.Bits;
        switch (f.Type)
        {
            case "Float":
                if (f.Single)
                {
                    return BitConverter.Int32BitsToSingle((int)ReadBits(f.Bytes, position, 32));
                }

                return BitConverter.Int64BitsToDouble((long)ReadBits(f.Bytes, position, 64));
            case "ScaledInteger":
                return (f.Min + (long)(f.Bits == 0 ? 0 : ReadBits(f.Bytes, position, f.Bits))) * f.Scale + f.Offset;
            default:
                return f.Min + (long)(f.Bits == 0 ? 0 : ReadBits(f.Bytes, position, f.Bits));
        }
    }

    public static Model3D Read(ReadOnlyMemory<byte> data, LoadOptions options)
    {
        var p = Parse(data, options);
        var scans = p.Scans.Where(s => s.Records > 0 && s.FileOffset >= 0 && s.Fields.Any(f => f.Kind is Kind.X or Kind.Range)).ToList();
        if (scans.Count == 0)
        {
            throw new UnsupportedModelFormatException(ModelFormats.E57, "The E57 file holds no scan with Cartesian or spherical points.");
        }

        long total = scans.Sum(s => s.Records);
        var stride = options.MaxPoints > 0 && total > options.MaxPoints ? (int)Math.Ceiling(total / (double)options.MaxPoints) : 1;
        var xyz = new List<double>();
        var colors = new List<float>();
        var anyColor = false;
        var anyIntensity = false;
        long globalIndex = 0;
        foreach (var scan in scans)
        {
            options.Cancellation.ThrowIfCancellationRequested();
            ReadPackets(p.File, scan, options);
            var fields = scan.Fields;
            Field? Find(Kind k) => fields.FirstOrDefault(f => f.Kind == k);
            var fx = Find(Kind.X);
            var fy = Find(Kind.Y);
            var fz = Find(Kind.Z);
            var fr = Find(Kind.Range);
            var fa = Find(Kind.Azimuth);
            var fe = Find(Kind.Elevation);
            var cartesian = fx is not null && fy is not null && fz is not null;
            if (!cartesian && (fr is null || fa is null || fe is null))
            {
                continue;
            }

            var red = Find(Kind.Red);
            var green = Find(Kind.Green);
            var blue = Find(Kind.Blue);
            var intensity = Find(Kind.Intensity);
            var invalidC = Find(Kind.CartesianInvalid);
            var invalidS = Find(Kind.SphericalInvalid);
            var used = cartesian ? new[] { fx!, fy!, fz! } : new[] { fr!, fa!, fe! };
            var available = long.MaxValue;
            foreach (var f in fields.Where(f => f.Kind != Kind.Ignore))
            {
                if (f.Bits > 0)
                {
                    available = Math.Min(available, (long)f.Length * 8 / f.Bits);
                }
            }

            var records = Math.Min(scan.Records, available == long.MaxValue ? scan.Records : available);
            var q = scan.Quaternion;
            var hasPose = scan.Translation is not null || q is not null;
            double qw = 1, qx = 0, qy = 0, qz = 0;
            if (q is not null)
            {
                var norm = Math.Sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
                if (norm > 0)
                {
                    (qw, qx, qy, qz) = (q[0] / norm, q[1] / norm, q[2] / norm, q[3] / norm);
                }
            }

            var t = scan.Translation ?? [0, 0, 0];
            var colorSpan = scan.ColorMax - scan.ColorMin;
            if (colorSpan <= 0)
            {
                colorSpan = 255;
            }

            var iMin = double.IsNaN(scan.IntensityMin) ? intensity is null ? 0 : intensity.Type == "Float" ? 0 : intensity.Min * intensity.Scale + intensity.Offset : scan.IntensityMin;
            var iMax = double.IsNaN(scan.IntensityMax) ? intensity is null ? 1 : intensity.Type == "Float" ? 1 : intensity.Max * intensity.Scale + intensity.Offset : scan.IntensityMax;
            var iSpan = iMax > iMin ? iMax - iMin : 1;
            for (long i = 0; i < records; i++, globalIndex++)
            {
                Cancel.Every(options.Cancellation, i);
                if (globalIndex % stride != 0)
                {
                    continue;
                }

                if (cartesian && invalidC is not null && Value(invalidC, i) != 0)
                {
                    continue;
                }

                if (!cartesian && invalidS is not null && Value(invalidS, i) != 0)
                {
                    continue;
                }

                double x, y, z;
                if (cartesian)
                {
                    (x, y, z) = (Value(used[0], i), Value(used[1], i), Value(used[2], i));
                }
                else
                {
                    var range = Value(used[0], i);
                    var az = Value(used[1], i);
                    var el = Value(used[2], i);
                    x = range * Math.Cos(el) * Math.Cos(az);
                    y = range * Math.Cos(el) * Math.Sin(az);
                    z = range * Math.Sin(el);
                }

                if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z))
                {
                    continue;
                }

                if (hasPose)
                {
                    // Rotate by the unit quaternion, then translate.
                    var tx = 2 * (qy * z - qz * y);
                    var ty = 2 * (qz * x - qx * z);
                    var tz = 2 * (qx * y - qy * x);
                    (x, y, z) = (x + qw * tx + (qy * tz - qz * ty) + t[0], y + qw * ty + (qz * tx - qx * tz) + t[1], z + qw * tz + (qx * ty - qy * tx) + t[2]);
                }

                xyz.Add(x);
                xyz.Add(y);
                xyz.Add(z);
                float r = 0, g = 0, b = 0;
                if (red is not null && green is not null && blue is not null)
                {
                    anyColor = true;
                    r = (float)Math.Clamp((Value(red, i) - scan.ColorMin) / colorSpan, 0, 1);
                    g = (float)Math.Clamp((Value(green, i) - scan.ColorMin) / colorSpan, 0, 1);
                    b = (float)Math.Clamp((Value(blue, i) - scan.ColorMin) / colorSpan, 0, 1);
                }
                else if (intensity is not null)
                {
                    anyIntensity = true;
                    r = g = b = (float)Math.Clamp((Value(intensity, i) - iMin) / iSpan, 0, 1);
                }

                colors.Add(r);
                colors.Add(g);
                colors.Add(b);
                colors.Add(1);
            }
        }

        if (xyz.Count == 0)
        {
            throw new ModelFormatException("The E57 file holds no valid points.");
        }

        var positions = xyz.ToArray();
        var built = PointCloudCommon.Build(ModelFormats.E57, positions, anyColor || anyIntensity ? colors.ToArray() : PointCloudCommon.HeightRamp(positions), options);
        var meta = new ModelMetadata();
        FillMetadata(p, meta);
        var pointsProperty = built.Metadata.Properties.FirstOrDefault(pr => pr is { Name: "points", Group: "cloud" });
        built.Metadata.FormatVersion = meta.FormatVersion;
        built.Metadata.Application = meta.Application;
        built.Metadata.Created = meta.Created;
        built.Metadata.Units = LengthUnit.Meter;
        built.Metadata.PartNames.AddRange(meta.PartNames);
        built.Metadata.Properties.RemoveAll(pr => pr is { Name: "points", Group: "cloud" });
        built.Metadata.Properties.InsertRange(0, meta.Properties);
        built.Metadata.Notes.AddRange(meta.Notes.Where(n => !n.StartsWith("Bounds are", StringComparison.Ordinal)));
        built.Preview = FindImage(p);
        built.Metadata.HasPreview = built.Preview is not null;
        _ = pointsProperty;
        if (p.BadPages > 0)
        {
            built.Warnings.Add(meta.Notes.First(n => n.Contains("CRC-32C", StringComparison.Ordinal)));
        }

        return built;
    }
}
