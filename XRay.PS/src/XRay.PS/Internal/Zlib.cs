using System.IO.Compression;

namespace XRay.PS.Internal;

/// <summary>zlib/deflate helpers. Decompression is a managed inflater so that failures match
/// Python's <c>zlib.decompress</c>: a bad header, a truncated stream and a checksum mismatch are
/// errors, data after the end of the stream is ignored.</summary>
internal static class Zlib
{
    /// <summary>Python <c>zlib.decompress(data)</c>.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
            throw Error("Error -5 while decompressing data: incomplete or truncated stream");
        int cmf = data[0];
        int flg = data[1];
        if ((cmf & 0x0F) != 8 || ((cmf << 8) | flg) % 31 != 0 || (cmf >> 4) > 7)
            throw Error("Error -3 while decompressing data: incorrect header check");
        if ((flg & 0x20) != 0)
            throw Error("Error -3 while decompressing data: need dictionary");
        var inflater = new Inflater(data[2..]);
        var output = inflater.Inflate(out bool complete, out int consumed);
        if (!complete)
            throw Error("Error -5 while decompressing data: incomplete or truncated stream");
        int pos = 2 + consumed;
        if (pos + 4 > data.Length)
            throw Error("Error -5 while decompressing data: incomplete or truncated stream");
        uint expected = (uint)((data[pos] << 24) | (data[pos + 1] << 16) | (data[pos + 2] << 8) | data[pos + 3]);
        if (Adler32(output) != expected)
            throw Error("Error -3 while decompressing data: incorrect data check");
        return output;
    }

    /// <summary>Decompresses a zlib stream, returning whatever could be decoded (never throws).</summary>
    public static byte[] DecompressLenient(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
            return [];
        try
        {
            var inflater = new Inflater(data[2..]);
            return inflater.Inflate(out _, out _);
        }
        catch (HostErrorException)
        {
            return [];
        }
    }

    /// <summary>Inflates raw deflate data; null when it is malformed or truncated.</summary>
    public static byte[]? InflateRaw(ReadOnlySpan<byte> data)
    {
        try
        {
            var output = new Inflater(data).Inflate(out bool complete, out _);
            return complete ? output : null;
        }
        catch (HostErrorException)
        {
            return null;
        }
    }

    /// <summary>zlib compression (used for PDF streams and PNG data).</summary>
    public static byte[] Compress(ReadOnlySpan<byte> data, CompressionLevel level = CompressionLevel.Optimal)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, level, leaveOpen: true))
            z.Write(data);
        return ms.ToArray();
    }

    public static uint Adler32(ReadOnlySpan<byte> data)
    {
        const uint mod = 65521;
        uint a = 1, b = 0;
        int i = 0;
        while (i < data.Length)
        {
            int n = Math.Min(5552, data.Length - i);
            for (int k = 0; k < n; k++)
            {
                a += data[i + k];
                b += a;
            }
            a %= mod;
            b %= mod;
            i += n;
        }
        return (b << 16) | a;
    }

    private static HostErrorException Error(string message) => new("zlib.error", message);

    private ref struct Inflater
    {
        private readonly ReadOnlySpan<byte> _input;
        private int _pos;
        private uint _bitBuf;
        private int _bitCount;
        private List<byte> _out;
        private int _polls;

        public Inflater(ReadOnlySpan<byte> input)
        {
            _input = input;
            _pos = 0;
            _bitBuf = 0;
            _bitCount = 0;
            _out = new List<byte>(Math.Max(64, input.Length * 4));
        }

        private bool Need(int count)
        {
            while (_bitCount < count)
            {
                if (_pos >= _input.Length)
                    return false;
                _bitBuf |= (uint)_input[_pos++] << _bitCount;
                _bitCount += 8;
            }
            return true;
        }

        private int Bits(int count)
        {
            if (!Need(count))
                throw new EndOfInput();
            int value = (int)(_bitBuf & ((1u << count) - 1));
            _bitBuf >>= count;
            _bitCount -= count;
            return value;
        }

        private sealed class EndOfInput : Exception;

        public byte[] Inflate(out bool complete, out int consumed)
        {
            complete = false;
            try
            {
                bool last;
                do
                {
                    last = Bits(1) == 1;
                    int type = Bits(2);
                    switch (type)
                    {
                        case 0:
                            Stored();
                            break;
                        case 1:
                            Huffman(FixedLit, FixedDist);
                            break;
                        case 2:
                            var (lit, dist) = DynamicTables();
                            Huffman(lit, dist);
                            break;
                        default:
                            throw Error("Error -3 while decompressing data: invalid block type");
                    }
                }
                while (!last);
                complete = true;
            }
            catch (EndOfInput)
            {
            }
            // Return the unused whole bytes of the bit buffer.
            consumed = _pos - _bitCount / 8;
            return [.. _out];
        }

        private void Stored()
        {
            _bitBuf = 0;
            _bitCount = 0;
            if (_pos + 4 > _input.Length)
                throw new EndOfInput();
            int len = _input[_pos] | (_input[_pos + 1] << 8);
            int nlen = _input[_pos + 2] | (_input[_pos + 3] << 8);
            _pos += 4;
            if ((len ^ 0xFFFF) != nlen)
                throw Error("Error -3 while decompressing data: invalid stored block lengths");
            int available = Math.Min(len, _input.Length - _pos);
            Guard.Check();
            for (int i = 0; i < available; i++)
                _out.Add(_input[_pos + i]);
            _pos += available;
            if (available < len)
                throw new EndOfInput();
        }

        private static readonly Huff FixedLit = BuildFixedLit();
        private static readonly Huff FixedDist = BuildFixedDist();

        private static Huff BuildFixedLit()
        {
            var lengths = new byte[288];
            for (int i = 0; i < 144; i++)
                lengths[i] = 8;
            for (int i = 144; i < 256; i++)
                lengths[i] = 9;
            for (int i = 256; i < 280; i++)
                lengths[i] = 7;
            for (int i = 280; i < 288; i++)
                lengths[i] = 8;
            return new Huff(lengths);
        }

        private static Huff BuildFixedDist()
        {
            var lengths = new byte[30];
            Array.Fill(lengths, (byte)5);
            return new Huff(lengths);
        }

        private static readonly int[] LengthBase = [3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258];
        private static readonly int[] LengthExtra = [0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0];
        private static readonly int[] DistBase = [1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577];
        private static readonly int[] DistExtra = [0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13];
        private static readonly int[] CodeLengthOrder = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];

        private (Huff Lit, Huff Dist) DynamicTables()
        {
            int hlit = Bits(5) + 257;
            int hdist = Bits(5) + 1;
            int hclen = Bits(4) + 4;
            var clLengths = new byte[19];
            for (int i = 0; i < hclen; i++)
                clLengths[CodeLengthOrder[i]] = (byte)Bits(3);
            var clHuff = new Huff(clLengths);
            var lengths = new byte[hlit + hdist];
            int n = 0;
            while (n < hlit + hdist)
            {
                int sym = Decode(clHuff);
                if (sym < 16)
                {
                    lengths[n++] = (byte)sym;
                }
                else if (sym == 16)
                {
                    if (n == 0)
                        throw Error("Error -3 while decompressing data: invalid bit length repeat");
                    byte prev = lengths[n - 1];
                    int repeat = 3 + Bits(2);
                    if (n + repeat > lengths.Length)
                        throw Error("Error -3 while decompressing data: invalid bit length repeat");
                    for (int i = 0; i < repeat; i++)
                        lengths[n++] = prev;
                }
                else
                {
                    int repeat = sym == 17 ? 3 + Bits(3) : 11 + Bits(7);
                    if (n + repeat > lengths.Length)
                        throw Error("Error -3 while decompressing data: invalid bit length repeat");
                    n += repeat;
                }
            }
            if (lengths[256] == 0)
                throw Error("Error -3 while decompressing data: invalid code -- missing end-of-block");
            return (new Huff(lengths.AsSpan(0, hlit).ToArray()), new Huff(lengths.AsSpan(hlit, hdist).ToArray()));
        }

        private int Decode(Huff h)
        {
            int code = 0;
            int first = 0;
            int index = 0;
            for (int len = 1; len <= 15; len++)
            {
                code |= Bits(1);
                int count = h.Counts[len];
                if (code - count < first)
                    return h.Symbols[index + (code - first)];
                index += count;
                first += count;
                first <<= 1;
                code <<= 1;
            }
            throw Error("Error -3 while decompressing data: invalid code");
        }

        private void Huffman(Huff lit, Huff dist)
        {
            while (true)
            {
                if ((++_polls & 0xFFFF) == 0)
                {
                    Guard.Check();
                    Guard.Charge(_out.Count, "inflated data");
                }
                int sym = Decode(lit);
                if (sym < 256)
                {
                    _out.Add((byte)sym);
                }
                else if (sym == 256)
                {
                    return;
                }
                else
                {
                    sym -= 257;
                    if (sym >= 29)
                        throw Error("Error -3 while decompressing data: invalid literal/length code");
                    int length = LengthBase[sym] + Bits(LengthExtra[sym]);
                    int dsym = Decode(dist);
                    if (dsym >= 30)
                        throw Error("Error -3 while decompressing data: invalid distance code");
                    int distance = DistBase[dsym] + Bits(DistExtra[dsym]);
                    if (distance > _out.Count)
                        throw Error("Error -3 while decompressing data: invalid distance too far back");
                    int start = _out.Count - distance;
                    for (int i = 0; i < length; i++)
                        _out.Add(_out[start + i]);
                }
            }
        }
    }

    private sealed class Huff
    {
        public readonly short[] Counts = new short[16];
        public readonly short[] Symbols;

        public Huff(byte[] lengths)
        {
            Symbols = new short[lengths.Length];
            foreach (byte l in lengths)
                Counts[l]++;
            Counts[0] = 0;
            var offs = new short[16];
            for (int i = 1; i < 16; i++)
                offs[i] = (short)(offs[i - 1] + Counts[i - 1]);
            for (int s = 0; s < lengths.Length; s++)
            {
                if (lengths[s] != 0)
                    Symbols[offs[lengths[s]]++] = (short)s;
            }
        }
    }
}
