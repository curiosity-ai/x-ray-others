using System.Buffers.Binary;
using System.Text;

namespace XRay.ThreeD.Formats.Rhino;

/// <summary>One chunk of a 3dm archive: a type code and either a short value or a length-prefixed body (with a trailing CRC when bit 0x8000 is set).</summary>
internal readonly record struct RhinoChunk(uint Type, bool IsShort, long Value, int DataStart, int DataEnd, int End)
{
    public int Length => DataEnd - DataStart;
}

/// <summary>Bounds-checked little-endian cursor over a region of a 3dm archive.</summary>
internal sealed class RhinoReaderCursor
{
    private readonly ReadOnlyMemory<byte> _data;

    public RhinoReaderCursor(ReadOnlyMemory<byte> data, int start, int end, bool wideLengths)
    {
        _data = data;
        Pos = start;
        End = Math.Min(end, data.Length);
        WideLengths = wideLengths;
    }

    public int Pos { get; set; }

    public int End { get; }

    public bool WideLengths { get; }

    public int Remaining => End - Pos;

    public ReadOnlySpan<byte> Span => _data.Span;

    public ReadOnlyMemory<byte> Memory => _data;

    private ReadOnlySpan<byte> Take(int n)
    {
        if (n < 0 || Pos < 0 || (long)Pos + n > End)
        {
            throw new ModelFormatException("The 3dm file is truncated.");
        }

        var s = _data.Span.Slice(Pos, n);
        Pos += n;
        return s;
    }

    public byte Byte() => Take(1)[0];

    public int Int() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    public uint UInt() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

    public double Double() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));

    public float Float() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

    public Guid Guid() => new(Take(16));

    public void Skip(int n) => Take(n);

    /// <summary>A UTF-16 string stored as a count of code units (terminator included) and the units.</summary>
    public string String()
    {
        var count = Int();
        if (count < 0 || count > 1 << 22)
        {
            throw new ModelFormatException("A string in the 3dm file has an impossible length.");
        }

        if (count == 0)
        {
            return "";
        }

        var bytes = Take(count * 2);
        var chars = count;
        if (bytes[(count - 1) * 2] == 0 && bytes[(count - 1) * 2 + 1] == 0)
        {
            chars--;
        }

        return Encoding.Unicode.GetString(bytes[..(chars * 2)]);
    }

    /// <summary>Reads the chunk header at the cursor and moves past the whole chunk (a short chunk has no body).</summary>
    public RhinoChunk Chunk()
    {
        var start = Pos;
        var type = UInt();
        var value = WideLengths ? BinaryPrimitives.ReadInt64LittleEndian(Take(8)) : Int();
        if ((type & 0x80000000) != 0)
        {
            return new RhinoChunk(type, true, value, Pos, Pos, Pos);
        }

        if (value < 0 || value > End - Pos)
        {
            throw new ModelFormatException($"A chunk at offset {start} of the 3dm file overruns its container.");
        }

        var dataStart = Pos;
        var end = Pos + (int)value;
        var dataEnd = (type & 0x8000) != 0 && value >= 4 ? end - 4 : end;
        Pos = end;
        return new RhinoChunk(type, false, value, dataStart, dataEnd, end);
    }

    public RhinoReaderCursor Sub(RhinoChunk chunk) => new(_data, chunk.DataStart, chunk.DataEnd, WideLengths);

    public RhinoReaderCursor SubRange(int start, int end) => new(_data, start, end, WideLengths);
}
