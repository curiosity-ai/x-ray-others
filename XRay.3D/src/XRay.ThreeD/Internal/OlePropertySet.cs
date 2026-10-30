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

    public static OlePropertySet Parse(ReadOnlySpan<byte> s, CancellationToken token)
    {
        var set = new OlePropertySet();
        if (s.Length < 48 || BinaryPrimitives.ReadUInt16LittleEndian(s) != 0xFFFE)
        {
            return set;
        }

        var sets = BinaryPrimitives.ReadUInt32LittleEndian(s[24..]);
        if (sets is 0 or > 2)
        {
            return set;
        }

        // Only the first property set is read: both streams of interest keep their values there.
        var offset = (long)BinaryPrimitives.ReadUInt32LittleEndian(s[44..]);
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

        for (var i = 0; i < count; i++)
        {
            Cancel.Every(token, i);
            var id = BinaryPrimitives.ReadUInt32LittleEndian(block[(8 + 8 * i)..]);
            var at = (long)BinaryPrimitives.ReadUInt32LittleEndian(block[(12 + 8 * i)..]);
            if (id == 0 || at < 0 || at + 4 > block.Length)
            {
                continue;
            }

            var value = ReadValue(block[(int)at..], codePage);
            if (value is null)
            {
                continue;
            }

            if (id == 1 && value is short cp)
            {
                codePage = cp;
                continue;
            }

            set.Values[id] = value;
        }

        return set;
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
