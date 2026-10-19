using System.Buffers.Binary;
using XRay.OneNote.Internal.OneStore;

namespace XRay.OneNote.Internal.Fsshttpb;

/// <summary>A cell identifier (MS-FSSHTTPB 2.2.1.10): the context and the object space it holds.</summary>
internal readonly record struct CellId(ExtendedGuid Context, ExtendedGuid Space);

/// <summary>A serial number (MS-FSSHTTPB 2.2.1.9); the null serial number has an empty GUID and value 0.</summary>
internal readonly record struct SerialNumber(Guid Guid, ulong Value);

/// <summary>The kinds of stream object header (MS-FSSHTTPB 2.2.1.5).</summary>
internal enum HeaderKind
{
    Start16,
    Start32,
    End8,
    End16,
}

/// <summary>A stream object header: its kind, type, whether it opens a compound object, and the length of its attached data.</summary>
internal readonly record struct StreamHeader(HeaderKind Kind, uint Type, bool Compound, long Length)
{
    public bool IsStart => Kind is HeaderKind.Start16 or HeaderKind.Start32;
}

/// <summary>
/// A bounds-checked cursor over FSSHTTPB data (MS-FSSHTTPB 2.2.1). Every read that would pass the end
/// of the data raises <see cref="FileCorruptedException"/>.
/// </summary>
internal sealed class FsshttpbReader
{
    private readonly byte[] _data;

    public FsshttpbReader(byte[] data, int position, int end)
    {
        _data = data;
        Position = position;
        End = Math.Min(end, data.Length);
    }

    public int Position { get; set; }

    /// <summary>The underlying buffer, for creating a reader over a sub-range.</summary>
    public byte[] PeekAll() => _data;

    public int End { get; }

    public bool AtEnd => Position >= End;

    public int Remaining => Math.Max(End - Position, 0);

    public byte PeekByte()
    {
        Require(1);
        return _data[Position];
    }

    public byte ReadByte()
    {
        Require(1);
        return _data[Position++];
    }

    public ReadOnlySpan<byte> ReadBytes(long count)
    {
        if (count < 0 || count > Remaining)
        {
            throw Truncated((int)Math.Min(count, int.MaxValue));
        }

        var span = new ReadOnlySpan<byte>(_data, Position, (int)count);
        Position += (int)count;
        return span;
    }

    public byte[] ReadByteArray(long count) => ReadBytes(count).ToArray();

    public void Skip(long count)
    {
        if (count < 0 || count > Remaining)
        {
            throw Truncated((int)Math.Min(count, int.MaxValue));
        }

        Position += (int)count;
    }

    public Guid ReadGuid() => new(ReadBytes(16));

    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16LittleEndian(ReadBytes(2));

    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(4));

    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(8));

    /// <summary>Reads a compact unsigned 64-bit integer (2.2.1.1): the number of trailing zero bits of the first byte gives the width.</summary>
    public ulong ReadCompactUInt()
    {
        var first = ReadByte();
        if (first == 0)
        {
            return 0;
        }

        if (first == 0x80)
        {
            return ReadUInt64();
        }

        var extra = System.Numerics.BitOperations.TrailingZeroCount((uint)first);
        ulong value = first;
        for (var index = 1; index <= extra; index++)
        {
            value |= (ulong)ReadByte() << (8 * index);
        }

        return value >> (extra + 1);
    }

    /// <summary>Reads a compact integer that is used as a count or size, rejecting values the remaining data cannot hold.</summary>
    public int ReadCount(int bytesPerItem = 1)
    {
        var value = ReadCompactUInt();
        if (value > (ulong)Remaining / (ulong)Math.Max(bytesPerItem, 1))
        {
            throw new FileCorruptedException($"A count of {value} does not fit in the remaining {Remaining} bytes");
        }

        return (int)value;
    }

    /// <summary>Reads an extended GUID (2.2.1.7).</summary>
    public ExtendedGuid ReadExtendedGuid()
    {
        var first = ReadByte();
        if (first == 0)
        {
            return default;
        }

        uint value;
        if ((first & 0x07) == 4)
        {
            value = (uint)(first >> 3);
        }
        else if ((first & 0x3F) == 32)
        {
            value = (uint)((first | (ReadByte() << 8)) >> 6);
        }
        else if ((first & 0x7F) == 64)
        {
            var raw = first | (ReadByte() << 8) | (ReadByte() << 16);
            value = (uint)(raw >> 7);
        }
        else if (first == 0x80)
        {
            value = ReadUInt32();
        }
        else
        {
            throw new FileCorruptedException($"Invalid extended GUID type byte 0x{first:X2}");
        }

        return new ExtendedGuid(ReadGuid(), value);
    }

    public List<ExtendedGuid> ReadExtendedGuidArray()
    {
        var count = ReadCount();
        var result = new List<ExtendedGuid>(count);
        for (var index = 0; index < count; index++)
        {
            result.Add(ReadExtendedGuid());
        }

        return result;
    }

    public CellId ReadCellId()
    {
        var context = ReadExtendedGuid();
        return new CellId(context, ReadExtendedGuid());
    }

    public List<CellId> ReadCellIdArray()
    {
        var count = ReadCount();
        var result = new List<CellId>(count);
        for (var index = 0; index < count; index++)
        {
            result.Add(ReadCellId());
        }

        return result;
    }

    /// <summary>Reads a serial number (2.2.1.9).</summary>
    public SerialNumber ReadSerialNumber()
    {
        var kind = ReadByte();
        if (kind == 0)
        {
            return default;
        }

        if (kind != 0x80)
        {
            throw new FileCorruptedException($"Invalid serial number type byte 0x{kind:X2}");
        }

        var guid = ReadGuid();
        return new SerialNumber(guid, ReadUInt64());
    }

    /// <summary>Reads a binary item (2.2.1.3): a compact length and that many bytes.</summary>
    public byte[] ReadBinaryItem() => ReadByteArray(ReadCount());

    /// <summary>Reads a stream object header (2.2.1.5).</summary>
    public StreamHeader ReadHeader()
    {
        var first = PeekByte();
        switch (first & 3)
        {
            case 0:
            {
                var value = ReadUInt16();
                return new StreamHeader(HeaderKind.Start16, (uint)((value >> 3) & 0x3F), ((value >> 2) & 1) != 0, value >> 9);
            }

            case 2:
            {
                var value = ReadUInt32();
                long length = (value >> 17) & 0x7FFF;
                if (length == 0x7FFF)
                {
                    var large = ReadCompactUInt();
                    if (large > int.MaxValue)
                    {
                        throw new FileCorruptedException("A stream object is longer than 2 GiB");
                    }

                    length = (long)large;
                }

                return new StreamHeader(HeaderKind.Start32, (value >> 3) & 0x3FFF, ((value >> 2) & 1) != 0, length);
            }

            case 1:
                ReadByte();
                return new StreamHeader(HeaderKind.End8, (uint)(first >> 2), false, 0);
            default:
                return new StreamHeader(HeaderKind.End16, (uint)(ReadUInt16() >> 2), false, 0);
        }
    }

    public StreamHeader PeekHeader()
    {
        var saved = Position;
        try
        {
            return ReadHeader();
        }
        finally
        {
            Position = saved;
        }
    }

    private void Require(int count)
    {
        if (count > Remaining)
        {
            throw Truncated(count);
        }
    }

    private FileCorruptedException Truncated(int count) =>
        new($"Unexpected end of FSSHTTPB data reading {count} bytes at offset {Position}");
}
