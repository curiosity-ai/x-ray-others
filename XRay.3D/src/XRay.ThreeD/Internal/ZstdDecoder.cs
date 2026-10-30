using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;

namespace XRay.ThreeD.Internal;

/// <summary>
/// A Zstandard decoder written from RFC 8878: frames (skippable ones are skipped), raw, RLE and compressed blocks, Huffman-coded literals
/// (one and four streams, direct and FSE-compressed weights, treeless reuse), FSE sequence tables (predefined, RLE, compressed, repeat) and the
/// repeat-offset rules. Dictionaries are not supported, and the content checksum is not verified (the caller's format has its own structure).
/// The output size is capped by the caller; every loop whose bound comes from the input checks the cancellation token.
/// </summary>
internal static class ZstdDecoder
{
    private const int MaxBlockSize = 128 * 1024;

    private static readonly byte[] LlBits = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 3, 3, 4, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
    private static readonly uint[] LlBase = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24, 28, 32, 40, 48, 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65536];
    private static readonly byte[] MlBits = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 3, 3, 4, 4, 5, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
    private static readonly uint[] MlBase = BuildMlBase();

    private static readonly short[] LlDefault = [4, 3, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 1, 1, 2, 2, 2, 2, 2, 2, 2, 2, 2, 3, 2, 1, 1, 1, 1, 1, -1, -1, -1, -1];
    private static readonly short[] MlDefault = [1, 4, 3, 2, 2, 2, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1, -1, -1];
    private static readonly short[] OfDefault = [1, 1, 1, 1, 1, 1, 2, 2, 2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, -1, -1, -1, -1, -1];

    private static readonly Lazy<FseTable> LlTable = new(() => FseTable.Build(LlDefault, 6));
    private static readonly Lazy<FseTable> MlTable = new(() => FseTable.Build(MlDefault, 6));
    private static readonly Lazy<FseTable> OfTable = new(() => FseTable.Build(OfDefault, 5));

    private static uint[] BuildMlBase()
    {
        var b = new uint[53];
        for (var i = 0; i < 32; i++)
        {
            b[i] = (uint)i + 3;
        }

        uint[] high = [35, 37, 39, 41, 43, 47, 51, 59, 67, 83, 99, 131, 259, 515, 1027, 2051, 4099, 8195, 16387, 32771, 65539];
        high.CopyTo(b, 32);
        return b;
    }

    /// <summary>The Zstandard frame magic, or a skippable frame (which a file may start with).</summary>
    public static bool IsZstd(ReadOnlySpan<byte> d) =>
        d.Length >= 4 && ((d[0] == 0x28 && d[1] == 0xB5 && d[2] == 0x2F && d[3] == 0xFD) || ((d[0] & 0xF0) == 0x50 && d[1] == 0x2A && d[2] == 0x4D && d[3] == 0x18));

    /// <summary>Decodes every frame of <paramref name="src"/>; fails when the output would exceed <paramref name="maxOutput"/> bytes.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> src, long maxOutput, CancellationToken token)
    {
        var limit = (int)Math.Min(maxOutput, 0x7FFFFFC7);
        var state = new State(limit, token);
        try
        {
            var p = 0;
            var frames = 0;
            while (p < src.Length)
            {
                if (src.Length - p < 4)
                {
                    throw Bad("truncated frame header");
                }

                var magic = BinaryPrimitives.ReadUInt32LittleEndian(src[p..]);
                if ((magic & 0xFFFFFFF0u) == 0x184D2A50u)
                {
                    if (src.Length - p < 8)
                    {
                        throw Bad("truncated skippable frame");
                    }

                    var skip = BinaryPrimitives.ReadUInt32LittleEndian(src[(p + 4)..]);
                    if (skip > (uint)(src.Length - p - 8))
                    {
                        throw Bad("skippable frame past the end");
                    }

                    p += 8 + (int)skip;
                    continue;
                }

                if (magic != 0xFD2FB528u)
                {
                    throw Bad("bad frame magic");
                }

                token.ThrowIfCancellationRequested();
                p = DecodeFrame(src, p + 4, state);
                frames++;
            }

            if (frames == 0)
            {
                throw Bad("no frame");
            }

            return state.ToArray();
        }
        finally
        {
            state.Dispose();
        }
    }

    private static ModelFormatException Bad(string what) => new("Corrupt Zstandard data: " + what + ".");

    private sealed class State : IDisposable
    {
        public readonly int Limit;
        public readonly CancellationToken Token;
        public byte[] Buf = [];
        public int Len;
        public int FrameStart;
        public byte[]? Literals = ArrayPool<byte>.Shared.Rent(MaxBlockSize + 64);
        public HuffTable? Huff;
        public FseTable? PrevLl, PrevOf, PrevMl;
        public uint Rep1 = 1, Rep2 = 4, Rep3 = 8;

        public State(int limit, CancellationToken token)
        {
            Limit = limit;
            Token = token;
        }

        public void Reserve(int extra)
        {
            if (extra > Limit - Len)
            {
                throw new ModelFormatException("The decompressed data is larger than the allowed " + Limit.ToString(System.Globalization.CultureInfo.InvariantCulture) + " bytes.");
            }

            var need = Len + extra;
            if (need > Buf.Length)
            {
                var size = (int)Math.Min((long)Limit, Math.Max((long)need, Math.Max(65536L, Buf.Length * 2L)));
                Array.Resize(ref Buf, size);
            }
        }

        public byte[] ToArray() => Buf.AsSpan(0, Len).ToArray();

        public void Dispose()
        {
            if (Literals is { } l)
            {
                ArrayPool<byte>.Shared.Return(l);
                Literals = null;
            }
        }
    }

    private static int DecodeFrame(ReadOnlySpan<byte> src, int p, State st)
    {
        if (p >= src.Length)
        {
            throw Bad("truncated frame header");
        }

        var fhd = src[p++];
        var fcsFlag = fhd >> 6;
        var single = (fhd & 0x20) != 0;
        var checksum = (fhd & 0x04) != 0;
        var dictFlag = fhd & 3;
        if ((fhd & 0x08) != 0)
        {
            throw Bad("reserved bit set");
        }

        if (!single)
        {
            if (p >= src.Length)
            {
                throw Bad("truncated frame header");
            }

            var wd = src[p++];
            var exponent = (wd >> 3) + 10;
            if (exponent > 31)
            {
                throw Bad("window too large");
            }
        }

        var dictBytes = dictFlag switch { 0 => 0, 1 => 1, 2 => 2, _ => 4 };
        if (p + dictBytes > src.Length)
        {
            throw Bad("truncated frame header");
        }

        uint dictId = 0;
        for (var i = 0; i < dictBytes; i++)
        {
            dictId |= (uint)src[p + i] << (8 * i);
        }

        p += dictBytes;
        if (dictId != 0)
        {
            throw new UnsupportedModelFormatException(FormatInfo.Unknown, "The Zstandard data needs a dictionary, which is not supported.");
        }

        var fcsBytes = fcsFlag switch { 0 => single ? 1 : 0, 1 => 2, 2 => 4, _ => 8 };
        if (p + fcsBytes > src.Length)
        {
            throw Bad("truncated frame header");
        }

        ulong content = 0;
        var hasContent = fcsBytes > 0;
        for (var i = 0; i < fcsBytes; i++)
        {
            content |= (ulong)src[p + i] << (8 * i);
        }

        p += fcsBytes;
        if (fcsFlag == 1)
        {
            content += 256;
        }

        if (hasContent)
        {
            if (content > (ulong)(st.Limit - st.Len))
            {
                throw new ModelFormatException("The decompressed data is larger than the allowed " + st.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture) + " bytes.");
            }

            // Pre-size to what the frame says, never to more than the cap; a lying header only wastes up to the cap.
            st.Reserve((int)Math.Min(content, 1 << 28));
        }

        st.FrameStart = st.Len;
        st.Huff = null;
        st.PrevLl = st.PrevOf = st.PrevMl = null;
        st.Rep1 = 1;
        st.Rep2 = 4;
        st.Rep3 = 8;

        var last = false;
        var blocks = 0L;
        while (!last)
        {
            Cancel.Every(st.Token, blocks++, 63);
            if (p + 3 > src.Length)
            {
                throw Bad("truncated block header");
            }

            var header = src[p] | (src[p + 1] << 8) | (src[p + 2] << 16);
            p += 3;
            last = (header & 1) != 0;
            var type = (header >> 1) & 3;
            var size = header >> 3;
            switch (type)
            {
                case 0:
                    if (size > MaxBlockSize || size > src.Length - p)
                    {
                        throw Bad("raw block size");
                    }

                    st.Reserve(size);
                    src.Slice(p, size).CopyTo(st.Buf.AsSpan(st.Len));
                    st.Len += size;
                    p += size;
                    break;
                case 1:
                    if (size > MaxBlockSize || p >= src.Length)
                    {
                        throw Bad("RLE block size");
                    }

                    st.Reserve(size);
                    st.Buf.AsSpan(st.Len, size).Fill(src[p]);
                    st.Len += size;
                    p++;
                    break;
                case 2:
                    if (size > MaxBlockSize || size > src.Length - p)
                    {
                        throw Bad("compressed block size");
                    }

                    DecodeBlock(src.Slice(p, size), st);
                    p += size;
                    break;
                default:
                    throw Bad("reserved block type");
            }
        }

        if (hasContent && (ulong)(st.Len - st.FrameStart) != content)
        {
            throw Bad("content size mismatch");
        }

        if (checksum)
        {
            if (p + 4 > src.Length)
            {
                throw Bad("truncated checksum");
            }

            p += 4;
        }

        return p;
    }

    // ---- compressed block ----

    private static void DecodeBlock(ReadOnlySpan<byte> blk, State st)
    {
        var p = 0;
        var litLen = DecodeLiterals(blk, ref p, st);
        var literals = st.Literals!;
        var litPos = 0;

        if (p >= blk.Length)
        {
            throw Bad("missing sequences section");
        }

        int numSeq = blk[p++];
        if (numSeq >= 128)
        {
            if (numSeq == 255)
            {
                if (p + 2 > blk.Length)
                {
                    throw Bad("truncated sequence count");
                }

                numSeq = blk[p] + (blk[p + 1] << 8) + 0x7F00;
                p += 2;
            }
            else
            {
                if (p >= blk.Length)
                {
                    throw Bad("truncated sequence count");
                }

                numSeq = ((numSeq - 128) << 8) + blk[p++];
            }
        }

        var blockStart = st.Len;
        if (numSeq == 0)
        {
            st.Reserve(litLen);
            literals.AsSpan(0, litLen).CopyTo(st.Buf.AsSpan(st.Len));
            st.Len += litLen;
            return;
        }

        if (p >= blk.Length)
        {
            throw Bad("missing symbol modes");
        }

        var modes = blk[p++];
        if ((modes & 3) != 0)
        {
            throw Bad("reserved symbol mode bits");
        }

        var ll = ReadTable(blk, ref p, modes >> 6, 9, 35, LlTable, ref st.PrevLl);
        var of = ReadTable(blk, ref p, (modes >> 4) & 3, 8, 31, OfTable, ref st.PrevOf);
        var ml = ReadTable(blk, ref p, (modes >> 2) & 3, 9, 52, MlTable, ref st.PrevMl);

        var bits = new BackBits(blk[p..]);
        var llState = bits.Read(ll.Log);
        var ofState = bits.Read(of.Log);
        var mlState = bits.Read(ml.Log);

        for (var i = 0; i < numSeq; i++)
        {
            Cancel.Every(st.Token, i);
            int ofCode = of.Sym[ofState];
            int mlCode = ml.Sym[mlState];
            int llCode = ll.Sym[llState];
            if (ofCode > 31 || mlCode > 52 || llCode > 35)
            {
                throw Bad("symbol out of range");
            }

            uint offsetValue = (1u << ofCode) + (uint)bits.Read(ofCode);
            var matchLen = (int)(MlBase[mlCode] + (uint)bits.Read(MlBits[mlCode]));
            var litCount = (int)(LlBase[llCode] + (uint)bits.Read(LlBits[llCode]));

            uint offset;
            if (offsetValue > 3)
            {
                offset = offsetValue - 3;
                st.Rep3 = st.Rep2;
                st.Rep2 = st.Rep1;
                st.Rep1 = offset;
            }
            else
            {
                var idx = (int)offsetValue - 1 + (litCount == 0 ? 1 : 0);
                if (idx == 0)
                {
                    offset = st.Rep1;
                }
                else if (idx == 1)
                {
                    offset = st.Rep2;
                    st.Rep2 = st.Rep1;
                    st.Rep1 = offset;
                }
                else if (idx == 2)
                {
                    offset = st.Rep3;
                    st.Rep3 = st.Rep2;
                    st.Rep2 = st.Rep1;
                    st.Rep1 = offset;
                }
                else
                {
                    offset = st.Rep1 - 1;
                    if (offset == 0)
                    {
                        throw Bad("zero repeat offset");
                    }

                    st.Rep3 = st.Rep2;
                    st.Rep2 = st.Rep1;
                    st.Rep1 = offset;
                }
            }

            if (litCount > litLen - litPos)
            {
                throw Bad("literal run past the literals");
            }

            if ((long)(st.Len - blockStart) + litCount + matchLen > MaxBlockSize)
            {
                throw Bad("block output too large");
            }

            st.Reserve(litCount + matchLen);
            literals.AsSpan(litPos, litCount).CopyTo(st.Buf.AsSpan(st.Len));
            st.Len += litCount;
            litPos += litCount;

            if (offset > (uint)(st.Len - st.FrameStart))
            {
                throw Bad("match offset before the start of the frame");
            }

            var from = st.Len - (int)offset;
            if (offset >= (uint)matchLen)
            {
                st.Buf.AsSpan(from, matchLen).CopyTo(st.Buf.AsSpan(st.Len));
            }
            else
            {
                var buf = st.Buf;
                for (var k = 0; k < matchLen; k++)
                {
                    buf[st.Len + k] = buf[from + k];
                }
            }

            st.Len += matchLen;

            if (i + 1 < numSeq)
            {
                llState = ll.Base[llState] + (uint)bits.Read(ll.Bits[llState]);
                mlState = ml.Base[mlState] + (uint)bits.Read(ml.Bits[mlState]);
                ofState = of.Base[ofState] + (uint)bits.Read(of.Bits[ofState]);
            }

            if (bits.Pos < 0)
            {
                throw Bad("sequence bitstream overrun");
            }
        }

        if (bits.Pos < 0)
        {
            throw Bad("sequence bitstream overrun");
        }

        var rest = litLen - litPos;
        if ((long)(st.Len - blockStart) + rest > MaxBlockSize)
        {
            throw Bad("block output too large");
        }

        st.Reserve(rest);
        literals.AsSpan(litPos, rest).CopyTo(st.Buf.AsSpan(st.Len));
        st.Len += rest;
    }

    private static FseTable ReadTable(ReadOnlySpan<byte> blk, ref int p, int mode, int maxLog, int maxSymbol, Lazy<FseTable> predefined, ref FseTable? previous)
    {
        FseTable table;
        switch (mode)
        {
            case 0:
                table = predefined.Value;
                break;
            case 1:
                if (p >= blk.Length)
                {
                    throw Bad("truncated RLE table");
                }

                if (blk[p] > maxSymbol)
                {
                    throw Bad("RLE symbol out of range");
                }

                table = FseTable.Rle(blk[p++]);
                break;
            case 2:
                table = FseTable.Read(blk, ref p, maxLog, maxSymbol);
                break;
            default:
                table = previous ?? throw Bad("repeat table without a previous one");
                break;
        }

        previous = table;
        return table;
    }

    // ---- literals ----

    private static int DecodeLiterals(ReadOnlySpan<byte> blk, ref int p, State st)
    {
        if (p >= blk.Length)
        {
            throw Bad("missing literals section");
        }

        var b0 = blk[p];
        var type = b0 & 3;
        var sizeFormat = (b0 >> 2) & 3;
        var literals = st.Literals!;

        if (type <= 1)
        {
            int regen;
            switch (sizeFormat)
            {
                case 0:
                case 2:
                    regen = b0 >> 3;
                    p += 1;
                    break;
                case 1:
                    NeedBytes(blk, p, 2);
                    regen = (b0 >> 4) | (blk[p + 1] << 4);
                    p += 2;
                    break;
                default:
                    NeedBytes(blk, p, 3);
                    regen = (b0 >> 4) | (blk[p + 1] << 4) | (blk[p + 2] << 12);
                    p += 3;
                    break;
            }

            if (regen > MaxBlockSize)
            {
                throw Bad("literals too large");
            }

            if (type == 0)
            {
                if (regen > blk.Length - p)
                {
                    throw Bad("truncated raw literals");
                }

                blk.Slice(p, regen).CopyTo(literals);
                p += regen;
            }
            else
            {
                if (p >= blk.Length)
                {
                    throw Bad("truncated RLE literals");
                }

                literals.AsSpan(0, regen).Fill(blk[p++]);
            }

            return regen;
        }

        int regenSize, compSize, headerBytes;
        var streams4 = sizeFormat != 0;
        switch (sizeFormat)
        {
            case 0:
            case 1:
                headerBytes = 3;
                break;
            case 2:
                headerBytes = 4;
                break;
            default:
                headerBytes = 5;
                break;
        }

        NeedBytes(blk, p, headerBytes);
        ulong h = 0;
        for (var i = 0; i < headerBytes; i++)
        {
            h |= (ulong)blk[p + i] << (8 * i);
        }

        h >>= 4;
        var bitsPer = sizeFormat <= 1 ? 10 : sizeFormat == 2 ? 14 : 18;
        var mask = (1UL << bitsPer) - 1;
        regenSize = (int)(h & mask);
        compSize = (int)((h >> bitsPer) & mask);
        p += headerBytes;
        if (regenSize > MaxBlockSize || compSize > blk.Length - p)
        {
            throw Bad("compressed literals size");
        }

        var data = blk.Slice(p, compSize);
        p += compSize;
        var q = 0;
        if (type == 2)
        {
            st.Huff = HuffTable.Read(data, ref q, st.Token);
        }
        else if (st.Huff is null)
        {
            throw Bad("treeless literals without a Huffman table");
        }

        var huff = st.Huff;
        if (!streams4)
        {
            DecodeHuffStream(huff, data[q..], literals.AsSpan(0, regenSize));
        }
        else
        {
            if (data.Length - q < 6)
            {
                throw Bad("truncated jump table");
            }

            int s1 = BinaryPrimitives.ReadUInt16LittleEndian(data[q..]);
            int s2 = BinaryPrimitives.ReadUInt16LittleEndian(data[(q + 2)..]);
            int s3 = BinaryPrimitives.ReadUInt16LittleEndian(data[(q + 4)..]);
            q += 6;
            var rem = data.Length - q;
            if (s1 + s2 + s3 > rem)
            {
                throw Bad("jump table past the data");
            }

            var each = (regenSize + 3) / 4;
            var lastSize = regenSize - 3 * each;
            if (lastSize < 0)
            {
                throw Bad("literal stream sizes");
            }

            var lit = literals.AsSpan(0, regenSize);
            DecodeHuffStream(huff, data.Slice(q, s1), lit.Slice(0, each));
            DecodeHuffStream(huff, data.Slice(q + s1, s2), lit.Slice(each, each));
            DecodeHuffStream(huff, data.Slice(q + s1 + s2, s3), lit.Slice(2 * each, each));
            DecodeHuffStream(huff, data[(q + s1 + s2 + s3)..], lit.Slice(3 * each, lastSize));
        }

        return regenSize;
    }

    private static void NeedBytes(ReadOnlySpan<byte> blk, int p, int count)
    {
        if (p + count > blk.Length)
        {
            throw Bad("truncated section header");
        }
    }

    private static void DecodeHuffStream(HuffTable t, ReadOnlySpan<byte> stream, Span<byte> output)
    {
        if (output.Length == 0)
        {
            return;
        }

        var bits = new BackBits(stream);
        var maxBits = t.MaxBits;
        for (var i = 0; i < output.Length; i++)
        {
            var idx = (int)bits.Peek(maxBits);
            output[i] = t.Sym[idx];
            bits.Pos -= t.Bits[idx];
        }

        if (bits.Pos < 0)
        {
            throw Bad("literal bitstream overrun");
        }
    }

    // ---- tables ----

    private sealed class HuffTable
    {
        public int MaxBits;
        public byte[] Sym = [];
        public byte[] Bits = [];

        public static HuffTable Read(ReadOnlySpan<byte> data, ref int p, CancellationToken token)
        {
            if (p >= data.Length)
            {
                throw Bad("missing Huffman header");
            }

            int hdr = data[p++];
            Span<byte> weights = stackalloc byte[256];
            int count;
            if (hdr < 128)
            {
                if (hdr > data.Length - p || hdr == 0)
                {
                    throw Bad("Huffman weight stream size");
                }

                var fseData = data.Slice(p, hdr);
                p += hdr;
                var fp = 0;
                var table = FseTable.Read(fseData, ref fp, 6, 255);
                var bits = new BackBits(fseData[fp..]);
                var s1 = bits.Read(table.Log);
                var s2 = bits.Read(table.Log);
                count = 0;
                while (true)
                {
                    if (count >= 255)
                    {
                        throw Bad("too many Huffman weights");
                    }

                    weights[count++] = table.Sym[s1];
                    s1 = table.Base[s1] + (uint)bits.Read(table.Bits[s1]);
                    if (bits.Pos < 0)
                    {
                        weights[count++] = table.Sym[s2];
                        break;
                    }

                    if (count >= 255)
                    {
                        throw Bad("too many Huffman weights");
                    }

                    weights[count++] = table.Sym[s2];
                    s2 = table.Base[s2] + (uint)bits.Read(table.Bits[s2]);
                    if (bits.Pos < 0)
                    {
                        weights[count++] = table.Sym[s1];
                        break;
                    }
                }
            }
            else
            {
                count = hdr - 127;
                var bytes = (count + 1) / 2;
                if (bytes > data.Length - p)
                {
                    throw Bad("truncated Huffman weights");
                }

                for (var i = 0; i < count; i++)
                {
                    var b = data[p + (i >> 1)];
                    weights[i] = (byte)((i & 1) == 0 ? b >> 4 : b & 15);
                }

                p += bytes;
            }

            if (count > 255)
            {
                throw Bad("too many Huffman weights");
            }

            long sum = 0;
            for (var i = 0; i < count; i++)
            {
                if (weights[i] > 11)
                {
                    throw Bad("Huffman weight too large");
                }

                if (weights[i] > 0)
                {
                    sum += 1L << (weights[i] - 1);
                }
            }

            if (sum == 0)
            {
                throw Bad("empty Huffman table");
            }

            var maxBits = BitOperations.Log2((ulong)sum) + 1;
            var total = 1L << maxBits;
            var left = total - sum;
            if (maxBits > 11 || left == 0 || (left & (left - 1)) != 0)
            {
                throw Bad("Huffman weights do not complete the tree");
            }

            weights[count] = (byte)(BitOperations.Log2((ulong)left) + 1);
            var symbols = count + 1;
            var size = 1 << maxBits;
            var t = new HuffTable { MaxBits = maxBits, Sym = new byte[size], Bits = new byte[size] };
            var pos = 0;
            for (var w = 1; w <= maxBits; w++)
            {
                var span = 1 << (w - 1);
                var nb = (byte)(maxBits + 1 - w);
                for (var s = 0; s < symbols; s++)
                {
                    if (weights[s] != w)
                    {
                        continue;
                    }

                    t.Sym.AsSpan(pos, span).Fill((byte)s);
                    t.Bits.AsSpan(pos, span).Fill(nb);
                    pos += span;
                }
            }

            if (pos != size)
            {
                throw Bad("Huffman table not full");
            }

            token.ThrowIfCancellationRequested();
            return t;
        }
    }

    private sealed class FseTable
    {
        public int Log;
        public byte[] Sym = [];
        public byte[] Bits = [];
        public uint[] Base = [];

        public static FseTable Rle(byte symbol) => new() { Log = 0, Sym = [symbol], Bits = [0], Base = [0] };

        public static FseTable Read(ReadOnlySpan<byte> data, ref int p, int maxLog, int maxSymbol)
        {
            var reader = new ForwardBits(data[p..]);
            var log = (int)reader.Read(4) + 5;
            if (log > maxLog)
            {
                throw Bad("FSE accuracy too large");
            }

            var norm = new short[maxSymbol + 1];
            var remaining = (1 << log) + 1;
            var threshold = 1 << log;
            var nbBits = log + 1;
            var sym = 0;
            while (remaining > 1 && sym <= maxSymbol)
            {
                var max = (2 * threshold - 1) - remaining;
                var peek = (int)reader.Peek(nbBits);
                int count;
                if ((peek & (threshold - 1)) < max)
                {
                    count = peek & (threshold - 1);
                    reader.Skip(nbBits - 1);
                }
                else
                {
                    count = peek & (2 * threshold - 1);
                    if (count >= threshold)
                    {
                        count -= max;
                    }

                    reader.Skip(nbBits);
                }

                count--;
                remaining -= count < 0 ? -count : count;
                norm[sym++] = (short)count;
                if (count == 0)
                {
                    while (true)
                    {
                        var rep = (int)reader.Read(2);
                        sym += rep;
                        if (sym > maxSymbol + 1)
                        {
                            throw Bad("FSE zero run past the alphabet");
                        }

                        if (rep != 3)
                        {
                            break;
                        }
                    }
                }

                while (remaining < threshold)
                {
                    nbBits--;
                    threshold >>= 1;
                }
            }

            if (remaining != 1 || reader.Overrun)
            {
                throw Bad("FSE table does not add up");
            }

            p += reader.BytesUsed;
            return Build(norm.AsSpan(0, sym), log);
        }

        public static FseTable Build(ReadOnlySpan<short> norm, int log)
        {
            var size = 1 << log;
            var t = new FseTable { Log = log, Sym = new byte[size], Bits = new byte[size], Base = new uint[size] };
            Span<int> next = stackalloc int[256];
            var high = size - 1;
            for (var s = 0; s < norm.Length; s++)
            {
                if (norm[s] == -1)
                {
                    t.Sym[high--] = (byte)s;
                    next[s] = 1;
                }
                else
                {
                    next[s] = norm[s];
                }
            }

            var step = (size >> 1) + (size >> 3) + 3;
            var mask = size - 1;
            var pos = 0;
            for (var s = 0; s < norm.Length; s++)
            {
                for (var i = 0; i < norm[s]; i++)
                {
                    t.Sym[pos] = (byte)s;
                    do
                    {
                        pos = (pos + step) & mask;
                    }
                    while (pos > high);
                }
            }

            if (pos != 0)
            {
                throw Bad("FSE spread did not close");
            }

            for (var i = 0; i < size; i++)
            {
                var s = t.Sym[i];
                var n = next[s]++;
                var nb = log - BitOperations.Log2((uint)n);
                t.Bits[i] = (byte)nb;
                t.Base[i] = (uint)((n << nb) - size);
            }

            return t;
        }
    }

    // ---- bit readers ----

    private ref struct ForwardBits
    {
        private readonly ReadOnlySpan<byte> _d;
        private long _pos;

        public ForwardBits(ReadOnlySpan<byte> d)
        {
            _d = d;
            _pos = 0;
        }

        public readonly bool Overrun => _pos > (long)_d.Length * 8;

        public readonly int BytesUsed => (int)((_pos + 7) >> 3);

        public readonly uint Peek(int n)
        {
            var idx = (int)(_pos >> 3);
            ulong word = 0;
            for (var i = 0; i < 8 && idx + i < _d.Length; i++)
            {
                word |= (ulong)_d[idx + i] << (8 * i);
            }

            return (uint)((word >> (int)(_pos & 7)) & ((1UL << n) - 1));
        }

        public void Skip(int n) => _pos += n;

        public uint Read(int n)
        {
            var v = Peek(n);
            _pos += n;
            return v;
        }
    }

    private ref struct BackBits
    {
        private readonly ReadOnlySpan<byte> _d;

        /// <summary>Bits still to read; negative once the reader has gone past the start.</summary>
        public long Pos;

        public BackBits(ReadOnlySpan<byte> d)
        {
            _d = d;
            if (d.Length == 0 || d[^1] == 0)
            {
                throw Bad("bitstream has no end marker");
            }

            Pos = (long)(d.Length - 1) * 8 + BitOperations.Log2(d[^1]);
        }

        public readonly uint Peek(int n)
        {
            if (n == 0)
            {
                return 0;
            }

            var start = Pos - n;
            var lo = Math.Max(start, 0);
            var cnt = (int)(Pos - lo);
            if (cnt <= 0)
            {
                return 0;
            }

            var idx = (int)(lo >> 3);
            ulong word = 0;
            for (var i = 0; i < 8 && idx + i < _d.Length; i++)
            {
                word |= (ulong)_d[idx + i] << (8 * i);
            }

            var v = (word >> (int)(lo & 7)) & ((1UL << cnt) - 1);
            return (uint)(v << (int)(lo - start));
        }

        public uint Read(int n)
        {
            var v = Peek(n);
            Pos -= n;
            return v;
        }
    }
}
