using System.Buffers.Binary;
using System.Text;

namespace XRay.ThreeD.Internal;

/// <summary>
/// An OLE property set stream ([MS-OLEPS]): the SummaryInformation (title, author, dates, application, thumbnail) and
/// DocumentSummaryInformation (company, category) streams that Office and Autodesk documents keep. Unknown value types are skipped.
/// </summary>
internal sealed class OlePropertySet
{
    public readonly Dictionary<uint, object> Values = new();

    public string? Text(uint id) => Values.TryGetValue(id, out var v) ? v as string : null;

    public DateTimeOffset? Time(uint id) => Values.TryGetValue(id, out var v) && v is DateTimeOffset t ? t : null;

    /// <summary>Thumbnail bytes (clipboard data) of a VT_CF property.</summary>
    public byte[]? Blob(uint id) => Values.TryGetValue(id, out var v) ? v as byte[] : null;

    /// <summary>The format id of the set (which kind of properties it holds); empty for a set read with <see cref="Parse"/> from a malformed stream.</summary>
    public Guid Fmtid { get; private set; }

    /// <summary>Names of the properties, from the set's dictionary (user-defined properties are named there; standard sets have no dictionary).</summary>
    public readonly Dictionary<uint, string> Names = new();

    public double? Number(uint id) => Values.TryGetValue(id, out var v) ? v switch { double d => d, int i => i, uint u => u, short sh => sh, _ => null } : null;

    /// <summary>The first property set of the stream (the SummaryInformation and DocumentSummaryInformation streams keep their values there).</summary>
    public static OlePropertySet Parse(ReadOnlySpan<byte> s, CancellationToken token)
    {
        if (s.Length < 48 || BinaryPrimitives.ReadUInt16LittleEndian(s) != 0xFFFE)
        {
            return new OlePropertySet();
        }

        var sets = BinaryPrimitives.ReadUInt32LittleEndian(s[24..]);
        return sets is 0 or > 2 ? new OlePropertySet() : ParseAt(s, 0, token);
    }

    /// <summary>Every property set of the stream (at most four), each with its format id: Inventor keeps one set per stream, Office two in the document summary.</summary>
    public static List<OlePropertySet> ParseAll(ReadOnlySpan<byte> s, CancellationToken token)
    {
        var result = new List<OlePropertySet>();
        if (s.Length < 48 || BinaryPrimitives.ReadUInt16LittleEndian(s) != 0xFFFE)
        {
            return result;
        }

        var sets = BinaryPrimitives.ReadUInt32LittleEndian(s[24..]);
        for (var i = 0; i < Math.Min(sets, 4u); i++)
        {
            if (28 + 20 * (i + 1) > s.Length)
            {
                break;
            }

            result.Add(ParseAt(s, (int)i, token));
        }

        return result;
    }

    private static OlePropertySet ParseAt(ReadOnlySpan<byte> s, int index, CancellationToken token)
    {
        var set = new OlePropertySet();
        var entry = 28 + 20 * index;
        if (entry + 20 > s.Length)
        {
            return set;
        }

        set.Fmtid = new Guid(s.Slice(entry, 16));
        var offset = (long)BinaryPrimitives.ReadUInt32LittleEndian(s[(entry + 16)..]);
        if (offset < 0 || offset + 8 > s.Length)
        {
            return set;
        }

        var block = s[(int)offset..];
        var count = BinaryPrimitives.ReadUInt32LittleEndian(block[4..]);
        var codePage = 1252;
        if (count > 4096 || 8 + (long)count * 8 > block.Length)
        {
            return set;
        }

        // The code page (property 1) decides how names and strings are read, so it goes first.
        for (var i = 0; i < count; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(block[(8 + 8 * i)..]) == 1)
            {
                var at1 = (long)BinaryPrimitives.ReadUInt32LittleEndian(block[(12 + 8 * i)..]);
                if (at1 >= 0 && at1 + 4 <= block.Length && ReadValue(block[(int)at1..], codePage) is short cp1)
                {
                    codePage = (ushort)cp1;
                }
            }
        }

        for (var i = 0; i < count; i++)
        {
            Cancel.Every(token, i);
            var id = BinaryPrimitives.ReadUInt32LittleEndian(block[(8 + 8 * i)..]);
            var at = (long)BinaryPrimitives.ReadUInt32LittleEndian(block[(12 + 8 * i)..]);
            if (at < 0 || at + 4 > block.Length)
            {
                continue;
            }

            if (id == 0)
            {
                ReadDictionary(block[(int)at..], codePage, set.Names, token);
                continue;
            }

            if (id == 1)
            {
                continue;
            }

            var value = ReadValue(block[(int)at..], codePage);
            if (value is not null)
            {
                set.Values[id] = value;
            }
        }

        return set;
    }

    private static void ReadDictionary(ReadOnlySpan<byte> d, int codePage, Dictionary<uint, string> names, CancellationToken token)
    {
        var entries = BinaryPrimitives.ReadUInt32LittleEndian(d);
        var p = 4;
        for (var i = 0; i < entries && i < 4096; i++)
        {
            Cancel.Every(token, i);
            if (p + 8 > d.Length)
            {
                return;
            }

            var id = BinaryPrimitives.ReadUInt32LittleEndian(d[p..]);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(d[(p + 4)..]);
            p += 8;
            var bytes = codePage == 1200 ? (long)length * 2 : length;
            if (bytes > d.Length - p)
            {
                return;
            }

            var raw = d.Slice(p, (int)bytes);
            names[id] = (codePage == 1200 ? Encoding.Unicode.GetString(raw) : Encoding.Latin1.GetString(raw)).TrimEnd('\0');
            p += (int)bytes;
            if (codePage == 1200)
            {
                p = (p + 3) & ~3;
            }
        }
    }

    private static object? ReadValue(ReadOnlySpan<byte> v, int codePage)
    {
        if (v.Length < 4)
        {
            return null;
        }

        var type = BinaryPrimitives.ReadUInt16LittleEndian(v);
        var body = v[4..];
        switch (type)
        {
            case 2 when body.Length >= 2:
                return BinaryPrimitives.ReadInt16LittleEndian(body);
            case 5 when body.Length >= 8:
                return BinaryPrimitives.ReadDoubleLittleEndian(body);
            case 0x13 when body.Length >= 4:
                return BinaryPrimitives.ReadUInt32LittleEndian(body);
            case 3 when body.Length >= 4:
                return BinaryPrimitives.ReadInt32LittleEndian(body);
            case 0x1E when body.Length >= 4:
            {
                var length = BinaryPrimitives.ReadUInt32LittleEndian(body);
                if (length > body.Length - 4)
                {
                    return null;
                }

                var bytes = body.Slice(4, (int)length);
                var end = bytes.IndexOf((byte)0);
                bytes = end < 0 ? bytes : bytes[..end];
                try
                {
                    return (codePage == 65001 ? Encoding.UTF8 : Encoding.Latin1).GetString(bytes);
                }
                catch (ArgumentException)
                {
                    return Encoding.Latin1.GetString(bytes);
                }
            }

            case 0x1F when body.Length >= 4:
            {
                var chars = BinaryPrimitives.ReadUInt32LittleEndian(body);
                if (chars > (body.Length - 4) / 2)
                {
                    return null;
                }

                return Encoding.Unicode.GetString(body.Slice(4, (int)chars * 2)).TrimEnd('\0');
            }

            case 0x40 when body.Length >= 8:
            {
                var ticks = BinaryPrimitives.ReadInt64LittleEndian(body);
                if (ticks <= 0 || ticks > 2_650_467_743_999_999_999L)
                {
                    return null;
                }

                return new DateTimeOffset(DateTime.FromFileTimeUtc(ticks), TimeSpan.Zero);
            }

            case 0x47 when body.Length >= 8:
            {
                // VT_CF: byte count, then a clipboard format (negative values are tagged, the next four bytes name it) and the data.
                var total = BinaryPrimitives.ReadUInt32LittleEndian(body);
                if (total < 4 || total > body.Length - 4)
                {
                    return null;
                }

                var data = body.Slice(4, (int)total);
                var format = BinaryPrimitives.ReadInt32LittleEndian(data);
                var skip = format is -1 or -2 or -3 ? 8 : format > 0 ? 4 : 4;
                return skip <= data.Length ? data[skip..].ToArray() : null;
            }

            default:
                return null;
        }
    }
}
