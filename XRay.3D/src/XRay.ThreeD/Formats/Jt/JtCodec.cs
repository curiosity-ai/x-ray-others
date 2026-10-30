using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace XRay.ThreeD.Formats.Jt;

using XRay.ThreeD.Internal;

/// <summary>A byte-order aware cursor over a JT buffer (every value is stored in the byte order the file header declares).</summary>
internal sealed class JtCursor
{
    private readonly byte[] _data;
    private readonly int _end;

    public JtCursor(byte[] data, bool bigEndian, int major, int start = 0)
    {
        _data = data;
        _end = data.Length;
        Pos = start;
        BigEndian = bigEndian;
        Major = major;
    }

    public int Pos { get; set; }

    public bool BigEndian { get; }

    public int Major { get; }

    public int Remaining => _end - Pos;

    public ModelFormatException Fail(string message) => new(message + " (at byte " + Pos.ToString(System.Globalization.CultureInfo.InvariantCulture) + " of a JT segment)");

    private int Take(int n)
    {
        if (n < 0 || n > _end - Pos)
        {
            throw Fail("The JT data is cut off");
        }

        var at = Pos;
        Pos += n;
        return at;
    }

    public ReadOnlySpan<byte> Bytes(int n) => _data.AsSpan(Take(n), n);

    public void Skip(long n)
    {
        if (n < 0 || n > _end - Pos)
        {
            throw Fail("The JT data is cut off");
        }

        Pos += (int)n;
    }

    public byte U8() => _data[Take(1)];

    public ushort U16()
    {
        var s = _data.AsSpan(Take(2), 2);
        return BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);
    }

    public short I16() => (short)U16();

    public uint U32()
    {
        var s = _data.AsSpan(Take(4), 4);
        return BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
    }

    public int I32() => (int)U32();

    public ulong U64()
    {
        var s = _data.AsSpan(Take(8), 8);
        return BigEndian ? BinaryPrimitives.ReadUInt64BigEndian(s) : BinaryPrimitives.ReadUInt64LittleEndian(s);
    }

    public float F32() => BitConverter.UInt32BitsToSingle(U32());

    public double F64() => BitConverter.UInt64BitsToDouble(U64());

    /// <summary>A GUID: one 32-bit and two 16-bit numbers in the file's byte order, then eight bytes; kept in logical (big-endian) order.</summary>
    public Guid16 ReadGuid()
    {
        var word = U32();
        var a = U16();
        var b = U16();
        var tail = Bytes(8);
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, word);
        BinaryPrimitives.WriteUInt16BigEndian(bytes[4..], a);
        BinaryPrimitives.WriteUInt16BigEndian(bytes[6..], b);
        tail.CopyTo(bytes[8..]);
        return new Guid16(bytes);
    }

    public int ReadVersion() => Major >= 10 ? U8() : I16();

    /// <summary>A count that must fit in what is left of the buffer.</summary>
    public int ReadLen()
    {
        var raw = I32();
        if (raw < 0 || raw > Remaining)
        {
            throw Fail("A JT length is out of range");
        }

        return raw;
    }

    public float[] ReadVecF32()
    {
        var n = ReadLen();
        if ((long)n * 4 > Remaining)
        {
            throw Fail("The JT data is cut off");
        }

        var v = new float[n];
        for (var i = 0; i < n; i++)
        {
            v[i] = F32();
        }

        return v;
    }

    public string ReadMbString()
    {
        var count = ReadLen();
        var bytes = Bytes(count * 2 > Remaining ? throw Fail("The JT data is cut off") : count * 2);
        var chars = new char[count];
        for (var i = 0; i < count; i++)
        {
            chars[i] = (char)(BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(bytes[(i * 2)..]) : BinaryPrimitives.ReadUInt16LittleEndian(bytes[(i * 2)..]));
        }

        var end = Array.IndexOf(chars, '\0');
        return new string(chars, 0, end < 0 ? count : end);
    }
}

/// <summary>A 16 byte JT type or segment identifier.</summary>
internal readonly struct Guid16 : IEquatable<Guid16>
{
    private readonly ulong _hi;
    private readonly ulong _lo;

    public Guid16(ReadOnlySpan<byte> b)
    {
        _hi = BinaryPrimitives.ReadUInt64BigEndian(b);
        _lo = BinaryPrimitives.ReadUInt64BigEndian(b[8..]);
    }

    public Guid16(ulong hi, ulong lo)
    {
        _hi = hi;
        _lo = lo;
    }

    public static Guid16 EndOfElement => new(ulong.MaxValue, ulong.MaxValue);

    public bool Equals(Guid16 other) => _hi == other._hi && _lo == other._lo;

    public override bool Equals(object? obj) => obj is Guid16 g && Equals(g);

    public override int GetHashCode() => HashCode.Combine(_hi, _lo);

    public override string ToString() => _hi.ToString("x16", System.Globalization.CultureInfo.InvariantCulture) + _lo.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);

    public static bool operator ==(Guid16 a, Guid16 b) => a.Equals(b);

    public static bool operator !=(Guid16 a, Guid16 b) => !a.Equals(b);
}

/// <summary>The bit reader of the JT integer codecs: most significant bit first, refilled from a byte slice or straight from the segment cursor.</summary>
internal sealed class JtBitReader
{
    private readonly byte[]? _data;
    private int _dataPos;
    private readonly JtCursor? _cursor;
    private readonly bool _swap;
    private byte[] _buffer = [];
    private int _bufferLength;
    private uint _bitsBuf;
    private int _bitsLoaded;
    private int _wordPos;

    public JtBitReader(byte[] data, bool littleEndianWords, CancellationToken token)
    {
        _data = data;
        _swap = littleEndianWords;
        Token = token;
    }

    public JtBitReader(JtCursor cursor, CancellationToken token)
    {
        _cursor = cursor;
        Token = token;
    }

    public CancellationToken Token { get; }

    private static ModelFormatException Corrupt(string message) => new("A JT codec stream is damaged: " + message);

    private static uint ShiftLeft(uint v, int bits) => bits >= 32 ? 0 : v << bits;

    private void Fill(long len)
    {
        if (len < 0 || len > int.MaxValue)
        {
            throw Corrupt("length overflow");
        }

        var n = (int)len;
        if (_data is not null)
        {
            if (n > _data.Length - _dataPos)
            {
                throw Corrupt("the bit stream is truncated");
            }

            if (_buffer.Length < n)
            {
                _buffer = new byte[n];
            }

            Buffer.BlockCopy(_data, _dataPos, _buffer, 0, n);
            _dataPos += n;
        }
        else
        {
            var bytes = _cursor!.Bytes(n);
            if (_buffer.Length < n)
            {
                _buffer = new byte[n];
            }

            bytes.CopyTo(_buffer);
        }

        _bufferLength = n;
    }

    private uint Word()
    {
        if (_wordPos + 4 > _bufferLength)
        {
            throw Corrupt("the bit stream is exhausted");
        }

        var s = _buffer.AsSpan(_wordPos, 4);
        _wordPos += 4;
        return _swap ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);
    }

    public void Load(long bits)
    {
        if (bits < _bitsLoaded)
        {
            throw Corrupt("asked for fewer bits than held");
        }

        var newBytes = (bits - _bitsLoaded + 7) / 8;
        if (newBytes == 0)
        {
            return;
        }

        var preload = (int)(newBytes % 4);
        if (_bitsLoaded + preload * 8 > 32)
        {
            throw Corrupt("bit buffer overflow");
        }

        Fill(newBytes);
        uint first = 0;
        for (var i = 0; i < preload; i++)
        {
            first |= (uint)_buffer[i] << (24 - i * 8);
        }

        if (_bitsLoaded != 0)
        {
            _bitsBuf |= ShiftRight(first, _bitsLoaded);
            _bitsLoaded += preload * 8;
        }
        else
        {
            _bitsBuf = first;
            _bitsLoaded = preload * 8;
        }

        _wordPos = preload;
    }

    private static uint ShiftRight(uint v, int bits) => bits >= 32 ? 0 : v >> bits;

    public void LoadWords(long words)
    {
        Fill(words * 4);
        _bitsBuf = 0;
        _bitsLoaded = 0;
        _wordPos = 0;
    }

    public uint ReadBit()
    {
        if (_bitsLoaded == 0)
        {
            _bitsBuf = Word();
            _bitsLoaded = 31;
        }
        else
        {
            _bitsLoaded--;
        }

        var result = _bitsBuf >> 31;
        _bitsBuf <<= 1;
        return result;
    }

    private uint BitBuffer(int bits)
    {
        if (bits > 32)
        {
            throw Corrupt("a field is wider than 32 bits");
        }

        if (_bitsLoaded >= bits)
        {
            var r = _bitsBuf;
            _bitsBuf = ShiftLeft(_bitsBuf, bits);
            _bitsLoaded -= bits;
            return r;
        }

        if (_bitsLoaded == 0)
        {
            _bitsBuf = Word();
            var r = _bitsBuf;
            _bitsBuf = ShiftLeft(_bitsBuf, bits);
            _bitsLoaded = 32 - bits;
            return r;
        }

        var result = _bitsBuf;
        var head = _bitsLoaded;
        var tail = bits - head;
        _bitsBuf = Word();
        result |= _bitsBuf >> head;
        _bitsBuf = ShiftLeft(_bitsBuf, tail);
        _bitsLoaded = 32 - tail;
        return result;
    }

    public uint ReadU32(int bits)
    {
        if (bits <= 0)
        {
            throw Corrupt("a field width is zero");
        }

        return BitBuffer(bits) >> (32 - bits);
    }

    public int ReadI32(int bits)
    {
        if (bits <= 0)
        {
            throw Corrupt("a field width is zero");
        }

        return (int)BitBuffer(bits) >> (32 - bits);
    }

    public uint ReadU32Or0(int bits) => bits == 0 ? 0 : ReadU32(bits);

    public int ReadI32Or0(int bits) => bits == 0 ? 0 : ReadI32(bits);
}

internal struct ProbEntry
{
    public int Symbol;
    public uint Occurrences;
    public int Value;
    public uint NextContext;
}

/// <summary>A probability table of the Huffman and arithmetic codecs.</summary>
internal sealed class ProbContext
{
    public const int Escape = -2;

    public ProbEntry[] Entries = [];
    public uint Total;

    public bool HasEscape => Entries.Any(e => e.Symbol == Escape);

    public void ReadMark1First(JtBitReader r) => Read(r, 32, 6, 6, 6, 6, 32);

    public void ReadMark1Next(JtBitReader r, ProbContext first)
    {
        Read(r, 32, 6, 6, 0, 6, 0);
        for (var i = 0; i < Entries.Length; i++)
        {
            for (var j = first.Entries.Length - 1; j >= 0; j--)
            {
                if (first.Entries[j].Symbol == Entries[i].Symbol)
                {
                    Entries[i].Value = first.Entries[j].Value;
                    break;
                }
            }
        }
    }

    public void ReadMark2(JtBitReader r) => Read(r, 16, 6, 6, 6, 0, 32);

    public void ReadMark3(JtBitReader r)
    {
        r.Load(16 + 6 + 7 + 32);
        var count = (int)Math.Min(r.ReadU32(16), int.MaxValue);
        var occBits = (int)r.ReadU32(6);
        var valueBits = (int)r.ReadU32(7);
        var min = r.ReadI32(32);
        if (occBits > 32 || valueBits > 32)
        {
            throw new ModelFormatException("A JT probability table has fields wider than 32 bits.");
        }

        r.Load((long)count * (1 + occBits + valueBits));
        Entries = new ProbEntry[count];
        Total = 0;
        for (var i = 0; i < count; i++)
        {
            Cancel.Every(r.Token, i);
            var symbol = r.ReadU32(1) != 0 ? Escape : 0;
            var occ = r.ReadU32Or0(occBits);
            var value = unchecked((int)r.ReadU32Or0(valueBits) + min);
            Entries[i] = new ProbEntry { Symbol = symbol, Occurrences = occ, Value = value };
            Total = unchecked(Total + occ);
        }
    }

    private void Read(JtBitReader r, int countW, int symW, int occW, int valW, int nextW, int minW)
    {
        r.Load(countW + symW + occW + valW + nextW + minW);
        var count = (long)r.ReadU32Or0(countW);
        var symbolBits = (int)r.ReadU32Or0(symW);
        var occBits = (int)r.ReadU32Or0(occW);
        var valueBits = (int)r.ReadU32Or0(valW);
        var nextBits = (int)r.ReadU32Or0(nextW);
        var min = r.ReadI32Or0(minW);
        if (symbolBits > 32 || occBits > 32 || valueBits > 32 || nextBits > 32)
        {
            throw new ModelFormatException("A JT probability table has fields wider than 32 bits.");
        }

        r.Load(count * (symbolBits + occBits + valueBits + nextBits));
        if (count > 1 << 24)
        {
            throw new ModelFormatException("A JT probability table is too large.");
        }

        Entries = new ProbEntry[count];
        Total = 0;
        for (var i = 0; i < count; i++)
        {
            Cancel.Every(r.Token, i);
            var symbol = unchecked((int)r.ReadU32Or0(symbolBits) - 2);
            var occ = r.ReadU32Or0(occBits);
            var value = unchecked((int)r.ReadU32Or0(valueBits) + min);
            var next = r.ReadU32Or0(nextBits);
            Entries[i] = new ProbEntry { Symbol = symbol, Occurrences = occ, Value = value, NextContext = next };
            Total = unchecked(Total + occ);
        }
    }
}

internal enum UnpackKind
{
    Null,
    Lag1,
    Lag2,
    Xor1,
    Xor2,
    Ramp,
    Stride1,
    Stride2,
    StripIdx,
}

/// <summary>The integer packages of JT: a codec choice, its tables and its code text; decoded on demand.</summary>
internal sealed class JtPackage
{
    private enum Kind
    {
        Empty,
        Null,
        Bitlength,
        Bitlength2,
        Huffman,
        Arithmetic,
        Chopper,
        Dictionary,
    }

    private Kind _kind;
    private uint[] _words = [];
    private bool _v10;
    private List<ProbContext> _contexts = new();
    private JtPackage? _oob;
    private int _outCount;
    private byte[] _codeText = [];
    private JtPackage? _msb;
    private JtPackage? _lsb;
    private int _shift;
    private int _bias;

    public static JtPackage Empty() => new();

    public int OutValCount => _kind switch
    {
        Kind.Empty => 0,
        Kind.Null => _words.Length,
        Kind.Chopper => _msb!.OutValCount,
        Kind.Dictionary => _lsb!.OutValCount,
        _ => _outCount,
    };

    private static JtPackage NullPackage(uint[] words) => new() { _kind = Kind.Null, _words = words };

    private static uint[] ReadU32s(JtCursor c, int count)
    {
        if ((long)count * 4 > c.Remaining)
        {
            throw c.Fail("The JT data is cut off");
        }

        var words = new uint[count];
        for (var i = 0; i < count; i++)
        {
            words[i] = c.U32();
        }

        return words;
    }

    private static byte[] ReadWords(JtCursor c, long words)
    {
        var len = words * 4;
        if (len > c.Remaining)
        {
            throw c.Fail("The JT data is cut off");
        }

        return c.Bytes((int)len).ToArray();
    }

    public static JtPackage Load1(JtCursor c, CancellationToken token, int depth = 0)
    {
        if (depth > 8)
        {
            throw c.Fail("JT integer packages nest too deeply");
        }

        var codec = c.U8();
        if (codec == 0)
        {
            return NullPackage(ReadU32s(c, c.ReadLen()));
        }

        var kind = codec switch
        {
            1 => Kind.Bitlength,
            2 => Kind.Huffman,
            3 => Kind.Arithmetic,
            _ => throw c.Fail("Unknown JT mark 1 codec " + codec),
        };
        var p = new JtPackage { _kind = kind };
        var contextCount = 0;
        if (codec > 1)
        {
            contextCount = c.U8();
            var reader = new JtBitReader(c, token);
            var first = new ProbContext();
            first.ReadMark1First(reader);
            p._contexts.Add(first);
            for (var i = 1; i < contextCount; i++)
            {
                var next = new ProbContext();
                next.ReadMark1Next(reader, first);
                p._contexts.Add(next);
            }

            var oobCount = c.I32();
            if (oobCount > 0)
            {
                p._oob = Load1(c, token, depth + 1);
            }
        }

        c.I32();
        var outCount = c.I32();
        if (outCount < 0)
        {
            throw c.Fail("A JT package has a negative value count");
        }

        p._outCount = outCount;
        if (contextCount > 1)
        {
            c.I32();
        }

        var wordCount = c.I32();
        if (wordCount < 0)
        {
            throw c.Fail("A JT package has a negative code text length");
        }

        p._codeText = ReadWords(c, wordCount);
        return p;
    }

    public static JtPackage Load2(JtCursor c, CancellationToken token, int depth = 0)
    {
        if (depth > 8)
        {
            throw c.Fail("JT integer packages nest too deeply");
        }

        var valueCount = c.I32();
        if (valueCount == 0)
        {
            return NullPackage([]);
        }

        if (valueCount < 0)
        {
            throw c.Fail("A JT package has a negative value count");
        }

        var codec = c.U8();
        if (codec == 4)
        {
            var chop = c.U8();
            if (chop == 0)
            {
                return Load2(c, token, depth + 1);
            }

            var bias = c.I32();
            var span = c.U8();
            var msb = Load2(c, token, depth + 1);
            var lsb = Load2(c, token, depth + 1);
            if (msb.OutValCount != valueCount || lsb.OutValCount != valueCount)
            {
                throw c.Fail("JT chopped data sizes do not match");
            }

            return new JtPackage { _kind = Kind.Chopper, _msb = msb, _lsb = lsb, _shift = (byte)(span - chop), _bias = bias };
        }

        if (codec == 5)
        {
            // JT 10.5 dictionary codec: a package of the distinct values, then a package of valueCount indices into it.
            var table = Load2(c, token, depth + 1);
            var indices = Load2(c, token, depth + 1);
            if (indices.OutValCount != valueCount)
            {
                throw c.Fail("JT dictionary data sizes do not match");
            }

            return new JtPackage { _kind = Kind.Dictionary, _msb = table, _lsb = indices };
        }

        if (codec is not (0 or 1 or 3))
        {
            throw c.Fail("Unknown JT mark 2 codec " + codec);
        }

        var codeTextLength = c.I32();
        if (codeTextLength <= 0)
        {
            throw c.Fail("A JT package has an invalid code text length");
        }

        var words = (codeTextLength + 31) / 32;
        if (codec == 0)
        {
            return NullPackage(ReadU32s(c, words));
        }

        var p = new JtPackage { _kind = codec == 1 ? Kind.Bitlength2 : Kind.Arithmetic, _v10 = c.Major >= 10, _outCount = valueCount };
        p._codeText = ReadWords(c, words);
        if (codec == 3)
        {
            var reader = new JtBitReader(c, token);
            var ctx = new ProbContext();
            if (p._v10)
            {
                ctx.ReadMark3(reader);
            }
            else
            {
                ctx.ReadMark2(reader);
            }

            p._contexts.Add(ctx);
            if (!p._v10 || ctx.HasEscape)
            {
                p._oob = Load2(c, token, depth + 1);
            }
        }

        return p;
    }

    public int[] Decode(bool bigEndian, UnpackKind unpack, CancellationToken token)
    {
        int[] values;
        switch (_kind)
        {
            case Kind.Empty:
                return [];
            case Kind.Null:
                values = new int[_words.Length];
                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = (int)_words[i];
                }

                break;
            case Kind.Chopper:
            {
                var hi = _msb!.Decode(bigEndian, UnpackKind.Null, token);
                var lo = _lsb!.Decode(bigEndian, UnpackKind.Null, token);
                values = new int[Math.Min(hi.Length, lo.Length)];
                for (var i = 0; i < values.Length; i++)
                {
                    values[i] = unchecked(((hi[i] << (_shift & 31)) | lo[i]) + _bias);
                }

                break;
            }

            case Kind.Dictionary:
            {
                var table = _msb!.Decode(bigEndian, UnpackKind.Null, token);
                var index = _lsb!.Decode(bigEndian, UnpackKind.Null, token);
                values = new int[index.Length];

                // An index of -1 introduces the next table value; any other index names a value by its place in a
                // move-to-front list of the values seen so far.
                var recent = new List<int>();
                var next = 0;
                for (var i = 0; i < values.Length; i++)
                {
                    Cancel.Every(token, i);
                    var k = index[i];
                    int v;
                    if (k == -1)
                    {
                        if (next >= table.Length)
                        {
                            throw new ModelFormatException("A JT dictionary has too few values.");
                        }

                        v = table[next++];
                        recent.Insert(0, v);
                    }
                    else
                    {
                        if ((uint)k >= (uint)recent.Count)
                        {
                            throw new ModelFormatException("A JT dictionary index is out of range.");
                        }

                        v = recent[k];
                        recent.RemoveAt(k);
                        recent.Insert(0, v);
                    }

                    values[i] = v;
                }

                break;
            }

            default:
            {
                var oob = _oob is null ? [] : _oob.Decode(bigEndian, UnpackKind.Null, token);
                var reader = new JtBitReader(_codeText, !bigEndian, token);
                reader.LoadWords(_codeText.Length / 4);
                values = _kind switch
                {
                    Kind.Bitlength => JtCodecs.Bitlength(reader, _outCount),
                    Kind.Bitlength2 => JtCodecs.Bitlength2(reader, _outCount, _v10),
                    Kind.Huffman => JtCodecs.Huffman(reader, _outCount, _contexts.Count > 0 ? _contexts[0] : throw new ModelFormatException("A JT Huffman package has no probability table."), oob),
                    _ => JtCodecs.Arithmetic(reader, _outCount, _contexts, oob),
                };
                break;
            }
        }

        _kind = Kind.Empty;
        Apply(unpack, values);
        return values;
    }

    private static void Apply(UnpackKind kind, int[] values)
    {
        if (kind == UnpackKind.Null)
        {
            return;
        }

        unchecked
        {
            for (var i = 4; i < values.Length; i++)
            {
                var v1 = values[i - 1];
                var v2 = values[i - 2];
                var v4 = values[i - 4];
                var residual = values[i];
                switch (kind)
                {
                    case UnpackKind.Lag1: values[i] = v1 + residual; break;
                    case UnpackKind.Lag2: values[i] = v2 + residual; break;
                    case UnpackKind.Xor1: values[i] = v1 ^ residual; break;
                    case UnpackKind.Xor2: values[i] = v2 ^ residual; break;
                    case UnpackKind.Ramp: values[i] = i + residual; break;
                    case UnpackKind.Stride1: values[i] = v1 + (v1 - v2) + residual; break;
                    case UnpackKind.Stride2: values[i] = v2 + (v2 - v4) + residual; break;
                    case UnpackKind.StripIdx:
                    {
                        var delta = v2 - v4;
                        values[i] = v2 + (delta > -8 && delta < 8 ? delta : 2) + residual;
                        break;
                    }
                }
            }
        }
    }
}

internal static class JtCodecs
{
    private const int StepSize = 2;

    private static int Width(int value)
    {
        if (value < 0)
        {
            throw new ModelFormatException("A JT codec field width went negative.");
        }

        return value;
    }

    public static int[] Bitlength(JtBitReader r, int count)
    {
        var output = new int[count];
        var n = 0;
        var fieldWidth = 0;
        while (n < count)
        {
            Cancel.Every(r.Token, n);
            if (r.ReadBit() != 0)
            {
                var first = r.ReadBit();
                var step = first != 0 ? StepSize : -StepSize;
                do
                {
                    fieldWidth += step;
                }
                while (r.ReadBit() == first);
            }

            output[n++] = r.ReadI32Or0(Width(fieldWidth));
        }

        return output;
    }

    public static int[] Bitlength2(JtBitReader r, int count, bool v10)
    {
        if (v10)
        {
            return Bitlength2V10(r, count);
        }

        return r.ReadBit() != 0 ? Bitlength2Variable(r, count) : Bitlength2Fixed(r, count);
    }

    private static int Nibbler(JtBitReader r)
    {
        var result = 0;
        var nibbles = 0;
        while (true)
        {
            var part = r.ReadU32(4);
            result |= (int)(part << (nibbles * 4));
            var more = r.ReadU32(1);
            nibbles++;
            if (more == 0 || nibbles >= 8)
            {
                break;
            }
        }

        var width = nibbles * 4;
        if (width < 32)
        {
            var shift = 32 - width;
            result = (int)((uint)result << shift) >> shift;
        }

        return result;
    }

    private static int[] Bitlength2V10(JtBitReader r, int count)
    {
        var output = new int[count];
        var n = 0;
        if (r.ReadBit() == 0)
        {
            var min = Nibbler(r);
            var max = Nibbler(r);
            var span = unchecked((uint)(max - min));
            var width = 0;
            while (span != 0)
            {
                width++;
                span >>= 1;
            }

            while (n < count)
            {
                Cancel.Every(r.Token, n);
                var residual = width == 0 ? 0u : r.ReadU32(width);
                output[n++] = unchecked((int)residual + min);
            }

            return output;
        }

        var mean = Nibbler(r);
        var fieldWidth = 0;
        while (n < count)
        {
            Cancel.Every(r.Token, n);
            int change;
            do
            {
                change = r.ReadI32(4);
                fieldWidth += change;
            }
            while (change == -8 || change == 7);

            var runLen = (int)r.ReadU32(4);
            var runEnd = Math.Min(n + runLen, count);
            var width = Width(fieldWidth);
            while (n < runEnd)
            {
                var residual = width == 0 ? 0 : r.ReadI32(width);
                output[n++] = unchecked(residual + mean);
            }
        }

        return output;
    }

    private static int[] Bitlength2Fixed(JtBitReader r, int count)
    {
        var minBits = (int)r.ReadU32(6);
        var maxBits = (int)r.ReadU32(6);
        if (minBits > 32 || maxBits > 32)
        {
            throw new ModelFormatException("A JT bitlength package has symbol widths over 32 bits.");
        }

        var min = r.ReadI32Or0(minBits);
        var max = r.ReadI32Or0(maxBits);
        var range = unchecked(max - min);
        var output = new int[count];
        if (range <= 0)
        {
            Array.Fill(output, min);
            return output;
        }

        var fieldWidth = 1;
        var rest = range >> 1;
        while (rest != 0)
        {
            fieldWidth++;
            rest >>= 1;
        }

        for (var n = 0; n < count; n++)
        {
            Cancel.Every(r.Token, n);
            output[n] = unchecked((int)(r.ReadU32(fieldWidth) + (uint)min));
        }

        return output;
    }

    private static int[] Bitlength2Variable(JtBitReader r, int count)
    {
        var mean = r.ReadI32(32);
        var changeBits = (int)r.ReadU32(3);
        var runBits = (int)r.ReadU32(3);
        if (changeBits == 0 || runBits == 0)
        {
            throw new ModelFormatException("A JT bitlength package has zero control widths.");
        }
        var half = 1 << (changeBits - 1);
        var maxDecrease = -half;
        var maxIncrease = half - 1;
        var fieldWidth = 0;
        var output = new int[count];
        var n = 0;
        while (n < count)
        {
            Cancel.Every(r.Token, n);
            int change;
            do
            {
                change = r.ReadI32(changeBits);
                fieldWidth += change;
            }
            while (change == maxDecrease || change == maxIncrease);

            var runLen = (int)r.ReadU32(runBits);
            var runEnd = n + runLen;
            var width = Width(fieldWidth);
            while (n < runEnd && n < count)
            {
                output[n++] = width == 0 ? mean : unchecked(r.ReadI32(width) + mean);
            }
        }

        return output;
    }

    private struct HNode
    {
        public int Occurrences;
        public int Left, Right;
        public int Symbol;
        public int Value;

        public readonly bool IsLeaf => Left < 0 && Right < 0;
    }

    private static void HeapAdd(List<HNode> nodes, List<int> heap, int index)
    {
        heap.Add(index);
        var i = heap.Count;
        while (i != 1 && nodes[heap[i / 2 - 1]].Occurrences > nodes[index].Occurrences)
        {
            heap[i - 1] = heap[i / 2 - 1];
            i /= 2;
        }

        heap[i - 1] = index;
    }

    private static void HeapRemove(List<HNode> nodes, List<int> heap)
    {
        if (heap.Count == 0)
        {
            return;
        }

        var size = heap.Count;
        var y = heap[size - 1];
        var i = 1;
        var child = 2;
        size--;
        while (child <= size)
        {
            if (child < size && nodes[heap[child - 1]].Occurrences > nodes[heap[child]].Occurrences)
            {
                child++;
            }

            if (nodes[y].Occurrences < nodes[heap[child - 1]].Occurrences)
            {
                break;
            }

            heap[i - 1] = heap[child - 1];
            i = child;
            child *= 2;
        }

        heap[i - 1] = y;
        heap.RemoveAt(heap.Count - 1);
    }

    private static int HeapTop(List<HNode> nodes, List<int> heap)
    {
        var index = heap[0];
        HeapRemove(nodes, heap);
        return index;
    }

    public static int[] Huffman(JtBitReader r, int count, ProbContext context, int[] oob)
    {
        var nodes = new List<HNode>(context.Entries.Length * 2);
        var heap = new List<int>(context.Entries.Length);
        foreach (var e in context.Entries)
        {
            nodes.Add(new HNode { Occurrences = (int)e.Occurrences, Left = -1, Right = -1, Symbol = e.Symbol, Value = e.Value });
            HeapAdd(nodes, heap, nodes.Count - 1);
        }

        if (nodes.Count < 2)
        {
            throw new ModelFormatException("A JT Huffman table has fewer than two symbols.");
        }

        while (heap.Count > 1)
        {
            var first = HeapTop(nodes, heap);
            var second = HeapTop(nodes, heap);
            nodes.Add(new HNode { Occurrences = unchecked(nodes[first].Occurrences + nodes[second].Occurrences), Left = first, Right = second, Symbol = unchecked((int)0xdeadbeef) });
            HeapAdd(nodes, heap, nodes.Count - 1);
        }

        var root = HeapTop(nodes, heap);
        var output = new int[count];
        var oobPos = 0;
        for (var n = 0; n < count; n++)
        {
            Cancel.Every(r.Token, n);
            var index = root;
            do
            {
                var next = r.ReadBit() != 0 ? nodes[index].Left : nodes[index].Right;
                if (next < 0)
                {
                    throw new ModelFormatException("A JT Huffman code does not match its tree.");
                }

                index = next;
            }
            while (!nodes[index].IsLeaf);

            if (nodes[index].Symbol != ProbContext.Escape)
            {
                output[n] = nodes[index].Value;
            }
            else
            {
                if (oobPos >= oob.Length)
                {
                    throw new ModelFormatException("A JT Huffman package ran out of out-of-band values.");
                }

                output[n] = oob[oobPos++];
            }
        }

        return output;
    }

    public static int[] Arithmetic(JtBitReader r, int count, List<ProbContext> contexts, int[] oob)
    {
        if (contexts.Count == 0)
        {
            throw new ModelFormatException("A JT arithmetic package has no probability table.");
        }

        var current = 0;
        ushort low = 0;
        ushort high = 0xFFFF;
        var code = (ushort)r.ReadU32(16);
        var output = new List<int>(Math.Min(count, 1 << 20));
        var oobPos = 0;
        unchecked
        {
            while (output.Count < count)
            {
                Cancel.Every(r.Token, output.Count);
                if (current >= contexts.Count)
                {
                    throw new ModelFormatException("A JT arithmetic package refers to an unknown context.");
                }

                var ctx = contexts[current];
                var entries = ctx.Entries;
                if (entries.Length == 0 || ctx.Total == 0)
                {
                    throw new ModelFormatException("A JT arithmetic probability table is empty.");
                }

                var total = ctx.Total;
                var range = (uint)(high - low + 1);
                if (range == 0)
                {
                    throw new ModelFormatException("A JT arithmetic range is empty.");
                }

                var target = ((uint)(code - low + 1) * total - 1) / range;
                var position = 0;
                var symbolHigh = entries[0].Occurrences;
                while (symbolHigh <= target)
                {
                    position++;
                    if (position >= entries.Length)
                    {
                        throw new ModelFormatException("A JT arithmetic symbol lookup left the table.");
                    }

                    symbolHigh += entries[position].Occurrences;
                }

                var entry = entries[position];
                var symbolLow = symbolHigh - entry.Occurrences;
                if (entry.Symbol != ProbContext.Escape)
                {
                    output.Add(entry.Value);
                }
                else if (current == 0)
                {
                    if (oobPos >= oob.Length)
                    {
                        throw new ModelFormatException("A JT arithmetic package ran out of out-of-band values.");
                    }

                    output.Add(oob[oobPos++]);
                }

                current = (int)Math.Min(entry.NextContext, int.MaxValue);
                high = (ushort)(low + (ushort)(range * symbolHigh / total) - 1);
                low = (ushort)(low + (ushort)(range * symbolLow / total));

                var mask = (ushort)(high ^ low);
                if ((mask & 0x8000) == 0)
                {
                    var shift = 1;
                    var probe = (ushort)~mask;
                    while ((probe & 0x4000) != 0)
                    {
                        probe <<= 1;
                        shift++;
                    }

                    low = (ushort)(low << (shift & 15));
                    high = (ushort)~(((uint)(ushort)~high) << shift);
                    code = (ushort)(code << (shift & 15));
                    code |= (ushort)r.ReadU32(shift);
                }

                if ((high & 0x4000) == 0 && (low & 0x4000) != 0)
                {
                    var shift = 1;
                    ushort probe = 0x2000;
                    while ((high & probe) == 0 && (low & probe) != 0)
                    {
                        probe >>= 1;
                        shift++;
                    }

                    low = (ushort)(low << (shift & 15));
                    high = (ushort)~(((uint)(ushort)~high) << shift);
                    code = (ushort)(code << (shift & 15));
                    code |= (ushort)r.ReadU32(shift);
                    low &= 0x7FFF;
                    high |= 0x8000;
                    code ^= 0x8000;
                }
            }
        }

        return output.ToArray();
    }
}
