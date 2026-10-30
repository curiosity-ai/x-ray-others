using System.Buffers.Binary;

namespace XRay.ThreeD.Internal;

/// <summary>
/// An .xz stream decoder, written from the public format descriptions (the .xz file format and the LZMA2 / LZMA coding it carries). It reads streams made of
/// blocks with one LZMA2 filter (what JT 10 segments use); check fields are skipped, not verified. The output is bounded by the caller.
/// </summary>
internal static class XzDecoder
{
    private static readonly byte[] Magic = [0xFD, 0x37, 0x7A, 0x58, 0x5A, 0x00];

    public static bool StartsWithMagic(ReadOnlySpan<byte> data) => data.StartsWith(Magic);

    /// <summary>Decodes one or more concatenated streams (zero padding between them is skipped).</summary>
    public static byte[] DecodeConcatenated(ReadOnlySpan<byte> data, long limit, CancellationToken token)
    {
        var output = new MemoryStream();
        var pos = 0;
        while (pos < data.Length)
        {
            while (pos < data.Length && data[pos] == 0)
            {
                pos++;
            }

            if (pos >= data.Length)
            {
                break;
            }

            if (!data[pos..].StartsWith(Magic))
            {
                throw new ModelFormatException("An xz stream does not start with its signature.");
            }

            pos = DecodeStream(data, pos, output, limit, token);
        }

        return output.ToArray();
    }

    private static int DecodeStream(ReadOnlySpan<byte> d, int pos, MemoryStream output, long limit, CancellationToken token)
    {
        if (pos + 12 > d.Length)
        {
            throw new ModelFormatException("The xz stream header is cut off.");
        }

        var checkSize = (d[pos + 7] & 0x0F) switch
        {
            0 => 0,
            1 => 4,
            4 => 8,
            10 => 32,
            _ => throw new ModelFormatException("The xz check type is not supported."),
        };

        pos += 12;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (pos >= d.Length)
            {
                throw new ModelFormatException("The xz stream is cut off.");
            }

            if (d[pos] == 0)
            {
                // The index: skip it and the footer (the index is padded to four bytes and ends with a CRC-32, then the 12 byte footer).
                pos++;
                var records = ReadVarInt(d, ref pos);
                for (long i = 0; i < records * 2; i++)
                {
                    ReadVarInt(d, ref pos);
                }

                while (pos % 4 != 0 && pos < d.Length)
                {
                    pos++;
                }

                pos += 4 + 12;
                return Math.Min(pos, d.Length);
            }

            var headerSize = (d[pos] + 1) * 4;
            if (pos + headerSize > d.Length)
            {
                throw new ModelFormatException("An xz block header is cut off.");
            }

            var headerEnd = pos + headerSize - 4;
            var flags = d[pos + 1];
            var p = pos + 2;
            if ((flags & 0x40) != 0)
            {
                ReadVarInt(d, ref p);
            }

            if ((flags & 0x80) != 0)
            {
                ReadVarInt(d, ref p);
            }

            var filters = (flags & 3) + 1;
            if (filters != 1)
            {
                throw new ModelFormatException("An xz block with a filter chain is not supported.");
            }

            var id = ReadVarInt(d, ref p);
            var propsSize = ReadVarInt(d, ref p);
            if (id != 0x21 || propsSize != 1 || p >= headerEnd)
            {
                throw new ModelFormatException("An xz block that is not LZMA2 is not supported.");
            }

            pos += headerSize;
            pos = DecodeLzma2(d, pos, output, limit, token);
            // Block padding to four bytes counts compressed data, header excluded; the check follows.
            var consumed = pos - (headerEnd + 4);
            pos += (4 - consumed % 4) % 4;
            pos += checkSize;
        }
    }

    private static long ReadVarInt(ReadOnlySpan<byte> d, ref int pos)
    {
        long v = 0;
        for (var shift = 0; shift < 63; shift += 7)
        {
            if (pos >= d.Length)
            {
                throw new ModelFormatException("An xz number is cut off.");
            }

            var b = d[pos++];
            v |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return v;
            }
        }

        throw new ModelFormatException("An xz number is too long.");
    }

    private sealed class Lzma
    {
        public int Lc, Lp, Pb;
        public ushort[] Literal = [];
        public readonly ushort[] IsMatch = new ushort[12 << 4];
        public readonly ushort[] IsRep = new ushort[12];
        public readonly ushort[] IsRepG0 = new ushort[12];
        public readonly ushort[] IsRepG1 = new ushort[12];
        public readonly ushort[] IsRepG2 = new ushort[12];
        public readonly ushort[] IsRep0Long = new ushort[12 << 4];
        public readonly ushort[] PosSlot = new ushort[4 << 6];
        public readonly ushort[] PosSpecial = new ushort[115];
        public readonly ushort[] Align = new ushort[16];
        public readonly LenCoder Len = new();
        public readonly LenCoder RepLen = new();
        public int State;
        public uint Rep0, Rep1, Rep2, Rep3;

        public void SetProps(int props)
        {
            if (props >= 9 * 5 * 5)
            {
                throw new ModelFormatException("The LZMA properties are invalid.");
            }

            Lc = props % 9;
            props /= 9;
            Lp = props % 5;
            Pb = props / 5;
            if (Lc + Lp > 4)
            {
                throw new ModelFormatException("The LZMA properties are not valid in LZMA2.");
            }

            Literal = new ushort[0x300 << (Lc + Lp)];
        }

        public void ResetState()
        {
            Fill(Literal);
            Fill(IsMatch);
            Fill(IsRep);
            Fill(IsRepG0);
            Fill(IsRepG1);
            Fill(IsRepG2);
            Fill(IsRep0Long);
            Fill(PosSlot);
            Fill(PosSpecial);
            Fill(Align);
            Len.Reset();
            RepLen.Reset();
            State = 0;
            Rep0 = Rep1 = Rep2 = Rep3 = 0;
        }

        private static void Fill(ushort[] a) => Array.Fill(a, (ushort)1024);
    }

    private sealed class LenCoder
    {
        public readonly ushort[] Choice = new ushort[2];
        public readonly ushort[] Low = new ushort[16 << 3];
        public readonly ushort[] Mid = new ushort[16 << 3];
        public readonly ushort[] High = new ushort[256];

        public void Reset()
        {
            Array.Fill(Choice, (ushort)1024);
            Array.Fill(Low, (ushort)1024);
            Array.Fill(Mid, (ushort)1024);
            Array.Fill(High, (ushort)1024);
        }
    }

    private ref struct RangeDecoder
    {
        private readonly ReadOnlySpan<byte> _d;
        private int _pos;
        private readonly int _end;
        private uint _range;
        private uint _code;

        public RangeDecoder(ReadOnlySpan<byte> d, int start, int end)
        {
            _d = d;
            _pos = start;
            _end = end;
            _range = 0xFFFFFFFF;
            if (end - start < 5 || d[start] != 0)
            {
                throw new ModelFormatException("The LZMA range coder start is invalid.");
            }

            _code = BinaryPrimitives.ReadUInt32BigEndian(d[(start + 1)..]);
            _pos = start + 5;
        }

        public readonly int Position => _pos;

        private byte Next()
        {
            if (_pos >= _end)
            {
                throw new ModelFormatException("The LZMA data is cut off.");
            }

            return _d[_pos++];
        }

        public int Bit(ushort[] probs, int index)
        {
            var p = probs[index];
            var bound = (_range >> 11) * p;
            int bit;
            if (_code < bound)
            {
                _range = bound;
                probs[index] = (ushort)(p + ((2048 - p) >> 5));
                bit = 0;
            }
            else
            {
                _range -= bound;
                _code -= bound;
                probs[index] = (ushort)(p - (p >> 5));
                bit = 1;
            }

            if (_range < 0x01000000)
            {
                _range <<= 8;
                _code = (_code << 8) | Next();
            }

            return bit;
        }

        public uint Direct(int count)
        {
            uint result = 0;
            for (var i = 0; i < count; i++)
            {
                _range >>= 1;
                _code -= _range;
                var t = 0u - (_code >> 31);
                _code += _range & t;
                result = (result << 1) + (t + 1);
                if (_range < 0x01000000)
                {
                    _range <<= 8;
                    _code = (_code << 8) | Next();
                }
            }

            return result;
        }

        public int Tree(ushort[] probs, int offset, int bits)
        {
            var m = 1;
            for (var i = 0; i < bits; i++)
            {
                m = (m << 1) | Bit(probs, offset + m);
            }

            return m - (1 << bits);
        }

        public int ReverseTree(ushort[] probs, int offset, int bits)
        {
            var m = 1;
            var symbol = 0;
            for (var i = 0; i < bits; i++)
            {
                var bit = Bit(probs, offset + m);
                m = (m << 1) | bit;
                symbol |= bit << i;
            }

            return symbol;
        }
    }

    private static int DecodeLen(ref RangeDecoder rc, LenCoder lc, int posState)
    {
        if (rc.Bit(lc.Choice, 0) == 0)
        {
            return rc.Tree(lc.Low, posState << 3, 3);
        }

        if (rc.Bit(lc.Choice, 1) == 0)
        {
            return 8 + rc.Tree(lc.Mid, posState << 3, 3);
        }

        return 16 + rc.Tree(lc.High, 0, 8);
    }

    private static int DecodeLzma2(ReadOnlySpan<byte> d, int pos, MemoryStream output, long limit, CancellationToken token)
    {
        var lz = new Lzma();
        var needProps = true;
        // The dictionary is everything decoded so far in this block; matches reach back at most to the last dictionary reset.
        var dictStart = 0;
        var all = new GrowBuffer();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (pos >= d.Length)
            {
                throw new ModelFormatException("The LZMA2 data is cut off.");
            }

            var control = d[pos++];
            if (control == 0)
            {
                output.Write(all.Data, 0, all.Length);
                return pos;
            }

            if (control < 0x80)
            {
                if (control > 2 || pos + 2 > d.Length)
                {
                    throw new ModelFormatException("An LZMA2 chunk header is invalid.");
                }

                var size = ((d[pos] << 8) | d[pos + 1]) + 1;
                pos += 2;
                if (control == 1)
                {
                    dictStart = all.Length;
                }

                if (pos + size > d.Length)
                {
                    throw new ModelFormatException("An LZMA2 chunk is cut off.");
                }

                CheckLimit(all.Length + size, limit);
                all.Append(d.Slice(pos, size));
                pos += size;
                continue;
            }

            if (pos + 4 > d.Length)
            {
                throw new ModelFormatException("An LZMA2 chunk header is cut off.");
            }

            var unpacked = (((control & 0x1F) << 16) | (d[pos] << 8) | d[pos + 1]) + 1;
            var packed = ((d[pos + 2] << 8) | d[pos + 3]) + 1;
            pos += 4;
            var mode = (control >> 5) & 3;
            if (mode == 3)
            {
                dictStart = all.Length;
            }

            if (mode >= 2)
            {
                if (pos >= d.Length)
                {
                    throw new ModelFormatException("An LZMA2 chunk header is cut off.");
                }

                lz.SetProps(d[pos++]);
                needProps = false;
            }

            if (needProps)
            {
                throw new ModelFormatException("An LZMA2 stream starts without properties.");
            }

            if (mode >= 1)
            {
                lz.ResetState();
            }

            if (pos + packed > d.Length)
            {
                throw new ModelFormatException("An LZMA2 chunk is cut off.");
            }

            CheckLimit(all.Length + unpacked, limit);
            DecodeChunk(d, pos, pos + packed, lz, all, unpacked, dictStart, token);
            pos += packed;
        }
    }

    private static void CheckLimit(long length, long limit)
    {
        if (length > limit)
        {
            throw new ModelFormatException("The compressed data expands past the size limit.");
        }
    }

    private sealed class GrowBuffer
    {
        public byte[] Data = new byte[1 << 16];
        public int Length;

        public void Ensure(int extra)
        {
            if (Length + extra > Data.Length)
            {
                Array.Resize(ref Data, (int)Math.Min(int.MaxValue / 2, Math.Max((long)Data.Length * 2, (long)Length + extra)));
            }
        }

        public void Append(ReadOnlySpan<byte> bytes)
        {
            Ensure(bytes.Length);
            bytes.CopyTo(Data.AsSpan(Length));
            Length += bytes.Length;
        }
    }

    private static void DecodeChunk(ReadOnlySpan<byte> d, int start, int end, Lzma lz, GrowBuffer o, int unpacked, int dictStart, CancellationToken token)
    {
        var rc = new RangeDecoder(d, start, end);
        var target = o.Length + unpacked;
        o.Ensure(unpacked);
        var pbMask = (1 << lz.Pb) - 1;
        var lpMask = (1 << lz.Lp) - 1;
        var tick = 0;
        while (o.Length < target)
        {
            if ((++tick & 0xFFFF) == 0)
            {
                token.ThrowIfCancellationRequested();
            }

            var pos = o.Length - dictStart;
            var posState = pos & pbMask;
            var state = lz.State;
            if (rc.Bit(lz.IsMatch, (state << 4) + posState) == 0)
            {
                var prev = pos > 0 ? o.Data[o.Length - 1] : 0;
                var litState = ((pos & lpMask) << lz.Lc) + (prev >> (8 - lz.Lc));
                var offset = 0x300 * litState;
                var symbol = 1;
                if (state >= 7)
                {
                    int matchByte = o.Data[o.Length - (int)lz.Rep0 - 1];
                    var matchBit = 0x100;
                    do
                    {
                        matchByte <<= 1;
                        var mb = matchByte & matchBit;
                        var bit = rc.Bit(lz.Literal, offset + matchBit + mb + symbol);
                        symbol = (symbol << 1) | bit;
                        matchBit &= bit != 0 ? mb : ~mb;
                    }
                    while (symbol < 0x100);
                }
                else
                {
                    while (symbol < 0x100)
                    {
                        symbol = (symbol << 1) | rc.Bit(lz.Literal, offset + symbol);
                    }
                }

                o.Data[o.Length++] = (byte)symbol;
                lz.State = state < 4 ? 0 : state < 10 ? state - 3 : state - 6;
                continue;
            }

            int len;
            if (rc.Bit(lz.IsRep, state) != 0)
            {
                if (pos == 0)
                {
                    throw new ModelFormatException("An LZMA repeat has no data to repeat.");
                }

                if (rc.Bit(lz.IsRepG0, state) == 0)
                {
                    if (rc.Bit(lz.IsRep0Long, (state << 4) + posState) == 0)
                    {
                        lz.State = state < 7 ? 9 : 11;
                        CopyByte(o, (int)lz.Rep0, dictStart);
                        continue;
                    }
                }
                else
                {
                    uint dist;
                    if (rc.Bit(lz.IsRepG1, state) == 0)
                    {
                        dist = lz.Rep1;
                    }
                    else
                    {
                        if (rc.Bit(lz.IsRepG2, state) == 0)
                        {
                            dist = lz.Rep2;
                        }
                        else
                        {
                            dist = lz.Rep3;
                            lz.Rep3 = lz.Rep2;
                        }

                        lz.Rep2 = lz.Rep1;
                    }

                    lz.Rep1 = lz.Rep0;
                    lz.Rep0 = dist;
                }

                len = DecodeLen(ref rc, lz.RepLen, posState);
                lz.State = state < 7 ? 8 : 11;
            }
            else
            {
                lz.Rep3 = lz.Rep2;
                lz.Rep2 = lz.Rep1;
                lz.Rep1 = lz.Rep0;
                len = DecodeLen(ref rc, lz.Len, posState);
                lz.State = state < 7 ? 7 : 10;
                var lenState = Math.Min(len, 3);
                var slot = rc.Tree(lz.PosSlot, lenState << 6, 6);
                if (slot < 4)
                {
                    lz.Rep0 = (uint)slot;
                }
                else
                {
                    var direct = (slot >> 1) - 1;
                    var dist = (uint)((2 | (slot & 1)) << direct);
                    if (slot < 14)
                    {
                        dist += (uint)rc.ReverseTree(lz.PosSpecial, (int)dist - slot - 1, direct);
                    }
                    else
                    {
                        dist += rc.Direct(direct - 4) << 4;
                        dist += (uint)rc.ReverseTree(lz.Align, 0, 4);
                    }

                    lz.Rep0 = dist;
                    if (dist == 0xFFFFFFFF)
                    {
                        throw new ModelFormatException("An LZMA end marker appeared inside an LZMA2 chunk.");
                    }
                }
            }

            len += 2;
            if (lz.Rep0 >= (uint)(o.Length - dictStart))
            {
                throw new ModelFormatException("An LZMA match reaches before the start of the data.");
            }

            if (len > target - o.Length)
            {
                throw new ModelFormatException("An LZMA match runs past the end of its chunk.");
            }

            var from = o.Length - (int)lz.Rep0 - 1;
            for (var i = 0; i < len; i++)
            {
                o.Data[o.Length++] = o.Data[from + i];
            }
        }
    }

    private static void CopyByte(GrowBuffer o, int rep0, int dictStart)
    {
        if (rep0 >= o.Length - dictStart)
        {
            throw new ModelFormatException("An LZMA short repeat reaches before the start of the data.");
        }

        o.Data[o.Length] = o.Data[o.Length - rep0 - 1];
        o.Length++;
    }
}
