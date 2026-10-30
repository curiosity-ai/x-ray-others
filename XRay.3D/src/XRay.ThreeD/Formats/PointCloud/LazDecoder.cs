using System.Buffers.Binary;

namespace XRay.ThreeD.Formats.PointCloud;

using XRay.ThreeD.Internal;

/// <summary>
/// LAZ: the LASzip compression of LAS point records, written from the public LASzip format description. A "laszip encoded" VLR
/// lists the record's items and the chunk size; points come in chunks, each starting with one raw record followed by an arithmetic
/// coded stream (range coder with adaptive symbol and bit models, integers coded as a bit-length class plus a corrector) in which
/// every field is predicted from the previous point. Pointwise-chunked and unchunked coding of the point types 0 to 3 (POINT10, GPSTIME11,
/// RGB12 and extra bytes, version 2) is decoded here, and the layered 1.4 types (6 to 10) by <see cref="LazLayered"/>.
/// </summary>
internal static class LazDecoder
{
    public const string UserId = "laszip encoded";

    internal readonly record struct Item(int Type, int Size, int Version);

    internal sealed class Info
    {
        public int Compressor;
        public int Coder;
        public uint ChunkSize;
        public Item[] Items = [];
    }

    /// <summary>Finds and parses the LASzip VLR in a LAS file's header; null when there is none.</summary>
    public static Info? ReadInfo(ReadOnlySpan<byte> s)
    {
        if (s.Length < 227)
        {
            return null;
        }

        var position = (int)BinaryPrimitives.ReadUInt16LittleEndian(s[94..]);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(s[100..]);
        for (uint i = 0; i < count && position + 54 <= s.Length; i++)
        {
            var user = System.Text.Encoding.ASCII.GetString(s.Slice(position + 2, 16)).TrimEnd('\0', ' ');
            var id = BinaryPrimitives.ReadUInt16LittleEndian(s[(position + 18)..]);
            int length = BinaryPrimitives.ReadUInt16LittleEndian(s[(position + 20)..]);
            var body = position + 54;
            if (body + length > s.Length)
            {
                return null;
            }

            if (user == UserId && id == 22204 && length >= 34)
            {
                var r = s.Slice(body, length);
                var items = BinaryPrimitives.ReadUInt16LittleEndian(r[32..]);
                if (34 + items * 6 > length)
                {
                    return null;
                }

                var info = new Info
                {
                    Compressor = BinaryPrimitives.ReadUInt16LittleEndian(r),
                    Coder = BinaryPrimitives.ReadUInt16LittleEndian(r[2..]),
                    ChunkSize = BinaryPrimitives.ReadUInt32LittleEndian(r[12..]),
                    Items = new Item[items],
                };
                for (var k = 0; k < items; k++)
                {
                    var o = 34 + k * 6;
                    info.Items[k] = new Item(BinaryPrimitives.ReadUInt16LittleEndian(r[o..]), BinaryPrimitives.ReadUInt16LittleEndian(r[(o + 2)..]), BinaryPrimitives.ReadUInt16LittleEndian(r[(o + 4)..]));
                }

                return info;
            }

            position = body + length;
        }

        return null;
    }

    /// <summary>
    /// Decodes the points of a LAZ file into uncompressed LAS records, keeping every <paramref name="stride"/>th point.
    /// Returns the records and how many were kept.
    /// </summary>
    public static (byte[] Records, int Kept) Decode(ReadOnlyMemory<byte> file, Info info, long pointOffset, long count, int recordLength, int stride, LoadOptions options)
    {
        if (info.Compressor is not (1 or 2 or 3) || info.Coder != 0)
        {
            throw new UnsupportedModelFormatException(ModelFormats.Laz, $"LAZ coding (compressor {info.Compressor}, coder {info.Coder}) is not supported.");
        }

        var layered = info.Compressor == 3;
        var sizeSum = 0;
        foreach (var item in info.Items)
        {
            if (layered ? LazLayered.Supports(item) : (item.Type is 6 or 7 or 8 or 0) && item.Version == 2)
            {
                sizeSum += item.Size;
            }
            else
            {
                throw new UnsupportedModelFormatException(ModelFormats.Laz, $"LAZ item type {item.Type} version {item.Version} is not supported.");
            }
        }

        if (sizeSum != recordLength)
        {
            throw new UnsupportedModelFormatException(ModelFormats.Laz, "The LAZ items do not add up to the record length.");
        }

        var token = options.Cancellation;
        token.ThrowIfCancellationRequested();
        var chunkSize = info.ChunkSize;
        var span = file.Span;
        if (pointOffset + 8 > span.Length)
        {
            throw new ModelFormatException("The LAZ point data is missing.");
        }

        if (info.Compressor == 1)
        {
            return DecodePointwise(file, info, pointOffset, count, recordLength, stride, sizeSum, options);
        }

        var tableOffset = BinaryPrimitives.ReadInt64LittleEndian(span[(int)pointOffset..]);
        if (tableOffset < pointOffset + 8 || tableOffset + 8 > span.Length)
        {
            throw new ModelFormatException("The LAZ chunk table is missing or damaged.");
        }

        // Chunk table: version, chunk count, then (point count if chunks vary in size) and byte size of each chunk, arithmetic coded.
        var chunks = (int)BinaryPrimitives.ReadUInt32LittleEndian(span[((int)tableOffset + 4)..]);
        if (chunks < 0 || chunks > count + 1 || chunks > span.Length)
        {
            throw new ModelFormatException("The LAZ chunk table has an implausible size.");
        }

        var variable = chunkSize == uint.MaxValue;
        var counts = new long[chunks];
        var bytes = new long[chunks];
        var tableDecoder = new Arithmetic(file, (int)tableOffset + 8, token);
        var tableInts = new IntegerModel(32, 2);
        long prevCount = 0, prevBytes = 0;
        for (var i = 0; i < chunks; i++)
        {
            Cancel.Every(token, i);
            if (variable)
            {
                prevCount = counts[i] = unchecked((uint)tableInts.Decompress(tableDecoder, (int)prevCount, 0));
            }

            prevBytes = bytes[i] = unchecked((uint)tableInts.Decompress(tableDecoder, (int)prevBytes, 1));
        }

        var kept = (int)((count + stride - 1) / stride);
        var records = new byte[(long)kept * recordLength];
        var record = new byte[recordLength];
        var items = new ItemDecoder[info.Items.Length];
        var chunkStart = pointOffset + 8;
        long global = 0;
        var written = 0;
        for (var chunk = 0; chunk < chunks && global < count; chunk++)
        {
            token.ThrowIfCancellationRequested();
            var inChunk = variable ? counts[chunk] : Math.Min(chunkSize, count - global);
            inChunk = Math.Min(inChunk, count - global);
            if (inChunk <= 0)
            {
                break;
            }

            if (chunkStart + sizeSum > tableOffset)
            {
                throw new ModelFormatException("A LAZ chunk starts beyond the chunk table.");
            }

            if (layered)
            {
                var buffer = new byte[inChunk * recordLength];
                var decodedInChunk = LazLayered.DecodeChunk(file, (int)chunkStart, (int)Math.Min(tableOffset, chunkStart + bytes[chunk]), info.Items, recordLength, inChunk, buffer, 0, token);
                for (var p = 0; p < decodedInChunk; p++, global++)
                {
                    if (global % stride == 0 && written < kept)
                    {
                        buffer.AsSpan(p * recordLength, recordLength).CopyTo(records.AsSpan(written * recordLength));
                        written++;
                    }
                }

                chunkStart += bytes[chunk];
                continue;
            }

            // The first point of a chunk is stored raw; it seeds the predictors of every item.
            var offset = 0;
            for (var k = 0; k < items.Length; k++)
            {
                var item = info.Items[k];
                items[k] = item.Type switch { 6 => new Point10(), 7 => new GpsTime11(), 8 => new Rgb12(), _ => new ExtraBytes(item.Size) };
                items[k].Init(span.Slice((int)chunkStart + offset, item.Size));
                span.Slice((int)chunkStart + offset, item.Size).CopyTo(record.AsSpan(offset));
                offset += item.Size;
            }

            var decoder = new Arithmetic(file, (int)chunkStart + sizeSum, token);
            for (long p = 0; p < inChunk; p++, global++)
            {
                Cancel.Every(token, global);
                if (p > 0)
                {
                    offset = 0;
                    for (var k = 0; k < items.Length; k++)
                    {
                        items[k].Read(decoder, record.AsSpan(offset, info.Items[k].Size));
                        offset += info.Items[k].Size;
                    }
                }

                if (global % stride == 0 && written < kept)
                {
                    record.CopyTo(records.AsSpan(written * recordLength));
                    written++;
                }
            }

            chunkStart += bytes[chunk];
        }

        if (written == 0)
        {
            throw new ModelFormatException("The LAZ file holds no decodable points.");
        }

        return (records, written);
    }

    /// <summary>Unchunked LAZ (compressor 1): the first record raw at the point offset, then one range-coded stream for all other points; no chunk table.</summary>
    private static (byte[] Records, int Kept) DecodePointwise(ReadOnlyMemory<byte> file, Info info, long pointOffset, long count, int recordLength, int stride, int sizeSum, LoadOptions options)
    {
        var span = file.Span;
        var token = options.Cancellation;
        var kept = (int)((count + stride - 1) / stride);
        var records = new byte[(long)kept * recordLength];
        var record = new byte[recordLength];
        var items = new ItemDecoder[info.Items.Length];
        var start = (int)pointOffset;
        if (start + sizeSum > span.Length)
        {
            throw new ModelFormatException("The LAZ point data is missing.");
        }

        var offset = 0;
        for (var k = 0; k < items.Length; k++)
        {
            var item = info.Items[k];
            items[k] = item.Type switch { 6 => new Point10(), 7 => new GpsTime11(), 8 => new Rgb12(), _ => new ExtraBytes(item.Size) };
            items[k].Init(span.Slice(start + offset, item.Size));
            span.Slice(start + offset, item.Size).CopyTo(record.AsSpan(offset));
            offset += item.Size;
        }

        var decoder = new Arithmetic(file, start + sizeSum, token);
        var written = 0;
        try
        {
            for (long p = 0; p < count; p++)
            {
                Cancel.Every(token, p);
                if (p > 0)
                {
                    offset = 0;
                    for (var k = 0; k < items.Length; k++)
                    {
                        items[k].Read(decoder, record.AsSpan(offset, info.Items[k].Size));
                        offset += info.Items[k].Size;
                    }
                }

                if (p % stride == 0 && written < kept)
                {
                    record.CopyTo(records.AsSpan(written * recordLength));
                    written++;
                }
            }
        }
        catch (ModelFormatException) when (written > 0)
        {
            // A truncated unchunked file still holds the points that decoded.
        }

        if (written == 0)
        {
            throw new ModelFormatException("The LAZ file holds no decodable points.");
        }

        return (records, written);
    }

    // ---- range decoder and models --------------------------------------------------------------------------------------

    private const uint MinLength = 0x01000000;
    private const int BitLengthShift = 13;
    private const int BitMaxCount = 1 << 13;
    private const int DistLengthShift = 15;
    private const int DistMaxCount = 1 << 15;

    internal sealed class Arithmetic
    {
        private readonly ReadOnlyMemory<byte> _data;
        private readonly CancellationToken _token;
        private int _pos;
        private uint _value;
        private uint _length = 0xFFFFFFFF;
        private int _bytesRead;

        public Arithmetic(ReadOnlyMemory<byte> data, int start, CancellationToken token)
        {
            _data = data;
            _pos = start;
            _token = token;
            _value = (uint)(Next() << 24 | Next() << 16 | Next() << 8 | Next());
        }

        private int Next()
        {
            if (_pos >= _data.Length)
            {
                throw new ModelFormatException("The LAZ point data ends early.");
            }

            if ((++_bytesRead & 0xFFFF) == 0)
            {
                _token.ThrowIfCancellationRequested();
            }

            return _data.Span[_pos++];
        }

        private void Renormalise()
        {
            do
            {
                _value = (_value << 8) | (uint)Next();
            }
            while ((_length <<= 8) < MinLength);
        }

        public int DecodeBit(BitModel m)
        {
            var x = m.Bit0Prob * (_length >> BitLengthShift);
            int symbol;
            if (_value >= x)
            {
                symbol = 1;
                _value -= x;
                _length -= x;
            }
            else
            {
                symbol = 0;
                _length = x;
                m.Bit0Count++;
            }

            if (_length < MinLength)
            {
                Renormalise();
            }

            if (--m.BitsUntilUpdate == 0)
            {
                m.Update();
            }

            return symbol;
        }

        public int DecodeSymbol(SymbolModel m)
        {
            var y = _length;
            uint x = 0;
            var s = 0;
            _length >>= DistLengthShift;
            var n = m.Symbols;
            var k = n >> 1;
            do
            {
                var z = _length * m.Distribution[k];
                if (z > _value)
                {
                    n = k;
                    y = z;
                }
                else
                {
                    s = k;
                    x = z;
                }
            }
            while ((k = (s + n) >> 1) != s);

            _value -= x;
            _length = y - x;
            if (_length < MinLength)
            {
                Renormalise();
            }

            m.SymbolCount[s]++;
            if (--m.SymbolsUntilUpdate == 0)
            {
                m.Update();
            }

            return s;
        }

        public uint ReadBits(int bits)
        {
            if (bits > 19)
            {
                var low = ReadBits(16);
                var high = ReadBits(bits - 16) << 16;
                return high | low;
            }

            _length >>= bits;
            var symbol = _value / _length;
            _value -= _length * symbol;
            if (_length < MinLength)
            {
                Renormalise();
            }

            return symbol;
        }

        public uint ReadInt() => ReadBits(16) | (ReadBits(16) << 16);
    }

    internal sealed class BitModel
    {
        public uint Bit0Count;
        public uint BitCount;
        public uint Bit0Prob;
        public uint BitsUntilUpdate;
        public uint UpdateCycle;

        public BitModel() => Reset();

        public void Reset()
        {
            Bit0Count = 1;
            BitCount = 2;
            Bit0Prob = 1u << (BitLengthShift - 1);
            UpdateCycle = BitsUntilUpdate = 4;
        }

        public void Update()
        {
            if ((BitCount += UpdateCycle) > BitMaxCount)
            {
                BitCount = (BitCount + 1) >> 1;
                Bit0Count = (Bit0Count + 1) >> 1;
                if (Bit0Count == BitCount)
                {
                    BitCount++;
                }
            }

            var scale = 0x80000000u / BitCount;
            Bit0Prob = (Bit0Count * scale) >> (31 - BitLengthShift);
            UpdateCycle = (5 * UpdateCycle) >> 2;
            if (UpdateCycle > 64)
            {
                UpdateCycle = 64;
            }

            BitsUntilUpdate = UpdateCycle;
        }
    }

    internal sealed class SymbolModel
    {
        public readonly int Symbols;
        public readonly uint[] Distribution;
        public readonly uint[] SymbolCount;
        public uint TotalCount;
        public uint UpdateCycle;
        public uint SymbolsUntilUpdate;

        public SymbolModel(int symbols)
        {
            Symbols = symbols;
            Distribution = new uint[symbols];
            SymbolCount = new uint[symbols];
            Reset();
        }

        public void Reset()
        {
            TotalCount = 0;
            UpdateCycle = (uint)Symbols;
            for (var k = 0; k < Symbols; k++)
            {
                SymbolCount[k] = 1;
            }

            Update();
            SymbolsUntilUpdate = UpdateCycle = (uint)((Symbols + 6) >> 1);
        }

        public void Update()
        {
            if ((TotalCount += UpdateCycle) > DistMaxCount)
            {
                TotalCount = 0;
                for (var n = 0; n < Symbols; n++)
                {
                    TotalCount += SymbolCount[n] = (SymbolCount[n] + 1) >> 1;
                }
            }

            uint sum = 0;
            var scale = 0x80000000u / TotalCount;
            for (var k = 0; k < Symbols; k++)
            {
                Distribution[k] = (scale * sum) >> (31 - DistLengthShift);
                sum += SymbolCount[k];
            }

            UpdateCycle = (5 * UpdateCycle) >> 2;
            var max = (uint)((Symbols + 6) << 3);
            if (UpdateCycle > max)
            {
                UpdateCycle = max;
            }

            SymbolsUntilUpdate = UpdateCycle;
        }
    }

    /// <summary>An integer coded as a class (number of significant bits of the correction) and the correction's bits, relative to a prediction.</summary>
    internal sealed class IntegerModel
    {
        private readonly int _bitsHigh = 8;
        private readonly int _corrBits;
        private readonly uint _corrRange;
        private readonly int _corrMin;
        private readonly SymbolModel[] _bits;
        private readonly BitModel _corrector0 = new();
        private readonly SymbolModel[] _corrector;

        public int K { get; private set; }

        public IntegerModel(int bits, int contexts)
        {
            if (bits is > 0 and < 32)
            {
                _corrBits = bits;
                _corrRange = 1u << bits;
                _corrMin = -(int)(_corrRange / 2);
            }
            else
            {
                _corrBits = 32;
                _corrRange = 0;
                _corrMin = int.MinValue;
            }

            _bits = new SymbolModel[contexts];
            for (var i = 0; i < contexts; i++)
            {
                _bits[i] = new SymbolModel(_corrBits + 1);
            }

            _corrector = new SymbolModel[_corrBits + 1];
            for (var i = 1; i <= _corrBits; i++)
            {
                _corrector[i] = new SymbolModel(i <= _bitsHigh ? 1 << i : 1 << _bitsHigh);
            }
        }

        public int Decompress(Arithmetic dec, int pred, int context)
        {
            var real = unchecked(pred + ReadCorrector(dec, _bits[context]));
            if (real < 0)
            {
                real = unchecked(real + (int)_corrRange);
            }
            else if ((uint)real >= _corrRange)
            {
                real = unchecked(real - (int)_corrRange);
            }

            return real;
        }

        private int ReadCorrector(Arithmetic dec, SymbolModel mBits)
        {
            int c;
            K = dec.DecodeSymbol(mBits);
            var k = K;
            if (k != 0)
            {
                if (k < 32)
                {
                    if (k <= _bitsHigh)
                    {
                        c = dec.DecodeSymbol(_corrector[k]);
                    }
                    else
                    {
                        var k1 = k - _bitsHigh;
                        c = dec.DecodeSymbol(_corrector[k]);
                        var c1 = (int)dec.ReadBits(k1);
                        c = (c << k1) | c1;
                    }

                    if (c >= 1 << (k - 1))
                    {
                        c += 1;
                    }
                    else
                    {
                        c -= (int)((1u << k) - 1);
                    }
                }
                else
                {
                    c = _corrMin;
                }
            }
            else
            {
                c = dec.DecodeBit(_corrector0);
            }

            return c;
        }

        public void Reset()
        {
            foreach (var m in _bits)
            {
                m.Reset();
            }

            _corrector0.Reset();
            for (var i = 1; i <= _corrBits; i++)
            {
                _corrector[i].Reset();
            }
        }
    }

    /// <summary>Running median of the last five values, as LASzip keeps it for coordinate differences.</summary>
    internal struct Median5
    {
        private int _v0, _v1, _v2, _v3, _v4;
        private bool _high;

        public static Median5 Create() => new() { _high = true };

        public readonly int Get() => _v2;

        public void Add(int v)
        {
            if (_high)
            {
                if (v < _v2)
                {
                    _v4 = _v3;
                    _v3 = _v2;
                    if (v < _v0)
                    {
                        _v2 = _v1;
                        _v1 = _v0;
                        _v0 = v;
                    }
                    else if (v < _v1)
                    {
                        _v2 = _v1;
                        _v1 = v;
                    }
                    else
                    {
                        _v2 = v;
                    }
                }
                else
                {
                    if (v < _v3)
                    {
                        _v4 = _v3;
                        _v3 = v;
                    }
                    else
                    {
                        _v4 = v;
                    }

                    _high = false;
                }
            }
            else
            {
                if (_v2 < v)
                {
                    _v0 = _v1;
                    _v1 = _v2;
                    if (_v4 < v)
                    {
                        _v2 = _v3;
                        _v3 = _v4;
                        _v4 = v;
                    }
                    else if (_v3 < v)
                    {
                        _v2 = _v3;
                        _v3 = v;
                    }
                    else
                    {
                        _v2 = v;
                    }
                }
                else
                {
                    if (_v1 < v)
                    {
                        _v0 = _v1;
                        _v1 = v;
                    }
                    else
                    {
                        _v0 = v;
                    }

                    _high = true;
                }
            }
        }
    }

    // ---- items ---------------------------------------------------------------------------------------------------------

    internal abstract class ItemDecoder
    {
        public abstract void Init(ReadOnlySpan<byte> first);

        public abstract void Read(Arithmetic dec, Span<byte> item);
    }

    private static int Fold(int n) => n < 0 ? n + 256 : n > 255 ? n - 256 : n;

    private static int Clamp(int n) => n < 0 ? 0 : n > 255 ? 255 : n;

    private static readonly byte[,] ReturnMap =
    {
        { 15, 14, 13, 12, 11, 10, 9, 8 },
        { 14, 0, 1, 3, 6, 10, 10, 9 },
        { 13, 1, 2, 4, 7, 11, 11, 10 },
        { 12, 3, 4, 5, 8, 12, 12, 11 },
        { 11, 6, 7, 8, 9, 13, 13, 12 },
        { 10, 10, 11, 12, 13, 14, 14, 13 },
        { 9, 10, 11, 12, 13, 14, 15, 14 },
        { 8, 9, 10, 11, 12, 13, 14, 15 },
    };

    private static readonly byte[,] ReturnLevel =
    {
        { 0, 1, 2, 3, 4, 5, 6, 7 },
        { 1, 0, 1, 2, 3, 4, 5, 6 },
        { 2, 1, 0, 1, 2, 3, 4, 5 },
        { 3, 2, 1, 0, 1, 2, 3, 4 },
        { 4, 3, 2, 1, 0, 1, 2, 3 },
        { 5, 4, 3, 2, 1, 0, 1, 2 },
        { 6, 5, 4, 3, 2, 1, 0, 1 },
        { 7, 6, 5, 4, 3, 2, 1, 0 },
    };

    /// <summary>Type 6: x, y, z, intensity, return bits, classification, scan angle, user data, point source (20 bytes).</summary>
    private sealed class Point10 : ItemDecoder
    {
        private readonly byte[] _last = new byte[20];
        private readonly Median5[] _lastX = new Median5[16];
        private readonly Median5[] _lastY = new Median5[16];
        private readonly ushort[] _lastIntensity = new ushort[16];
        private readonly int[] _lastHeight = new int[8];
        private readonly SymbolModel _changed = new(64);
        private readonly SymbolModel?[] _bitByte = new SymbolModel?[256];
        private readonly SymbolModel?[] _classification = new SymbolModel?[256];
        private readonly SymbolModel?[] _userData = new SymbolModel?[256];
        private readonly IntegerModel _intensity = new(16, 4);
        private readonly SymbolModel[] _scanAngle = [new(256), new(256)];
        private readonly IntegerModel _pointSource = new(16, 1);
        private readonly IntegerModel _dx = new(32, 2);
        private readonly IntegerModel _dy = new(32, 22);
        private readonly IntegerModel _z = new(32, 20);

        public override void Init(ReadOnlySpan<byte> first)
        {
            for (var i = 0; i < 16; i++)
            {
                _lastX[i] = Median5.Create();
                _lastY[i] = Median5.Create();
            }

            first.CopyTo(_last);
            _last[12] = _last[13] = 0; // the intensity predictor starts at zero
        }

        public override void Read(Arithmetic dec, Span<byte> item)
        {
            var changed = dec.DecodeSymbol(_changed);
            if ((changed & 32) != 0)
            {
                var model = _bitByte[_last[14]] ??= new SymbolModel(256);
                _last[14] = (byte)dec.DecodeSymbol(model);
            }

            var r = _last[14] & 7;
            var n = (_last[14] >> 3) & 7;
            var m = ReturnMap[n, r];
            var l = ReturnLevel[n, r];
            if ((changed & 16) != 0)
            {
                _lastIntensity[m] = (ushort)_intensity.Decompress(dec, _lastIntensity[m], m < 3 ? m : 3);
            }

            BinaryPrimitives.WriteUInt16LittleEndian(_last.AsSpan(12), _lastIntensity[m]);
            if ((changed & 8) != 0)
            {
                var model = _classification[_last[15]] ??= new SymbolModel(256);
                _last[15] = (byte)dec.DecodeSymbol(model);
            }

            if ((changed & 4) != 0)
            {
                // The scan angle is a byte-wise difference, with one of two models picked by the scan direction flag.
                _last[16] = (byte)Fold(dec.DecodeSymbol(_scanAngle[(_last[14] >> 6) & 1]) + _last[16]);
            }

            if ((changed & 2) != 0)
            {
                var model = _userData[_last[17]] ??= new SymbolModel(256);
                _last[17] = (byte)dec.DecodeSymbol(model);
            }

            if ((changed & 1) != 0)
            {
                var value = _pointSource.Decompress(dec, BinaryPrimitives.ReadUInt16LittleEndian(_last.AsSpan(18)), 0);
                BinaryPrimitives.WriteUInt16LittleEndian(_last.AsSpan(18), (ushort)value);
            }

            var x = BinaryPrimitives.ReadInt32LittleEndian(_last);
            var y = BinaryPrimitives.ReadInt32LittleEndian(_last.AsSpan(4));
            var median = _lastX[m].Get();
            var diff = _dx.Decompress(dec, median, n == 1 ? 1 : 0);
            x = unchecked(x + diff);
            _lastX[m].Add(diff);
            median = _lastY[m].Get();
            var kBits = _dx.K;
            diff = _dy.Decompress(dec, median, (n == 1 ? 1 : 0) + (kBits < 20 ? ZeroBit0(kBits) : 20));
            y = unchecked(y + diff);
            _lastY[m].Add(diff);
            kBits = (_dx.K + _dy.K) / 2;
            var z = _z.Decompress(dec, _lastHeight[l], (n == 1 ? 1 : 0) + (kBits < 18 ? ZeroBit0(kBits) : 18));
            _lastHeight[l] = z;
            BinaryPrimitives.WriteInt32LittleEndian(_last, x);
            BinaryPrimitives.WriteInt32LittleEndian(_last.AsSpan(4), y);
            BinaryPrimitives.WriteInt32LittleEndian(_last.AsSpan(8), z);
            _last.CopyTo(item);
        }


        private static int ZeroBit0(int k) => k & ~1;
    }

    /// <summary>Type 7: the GPS time as a 64-bit integer, predicted from up to four recent sequences.</summary>
    private sealed class GpsTime11 : ItemDecoder
    {
        private const int Multi = 500;
        private const int MultiMinus = -10;
        private const int MultiUnchanged = Multi - MultiMinus + 1;
        private const int MultiCodeFull = Multi - MultiMinus + 2;
        private const int MultiTotal = Multi - MultiMinus + 6;

        private readonly long[] _lastTime = new long[4];
        private readonly int[] _lastDiff = new int[4];
        private readonly int[] _extreme = new int[4];
        private int _last;
        private int _next;
        private readonly SymbolModel _multi = new(MultiTotal);
        private readonly SymbolModel _zeroDiff = new(6);
        private readonly IntegerModel _time = new(32, 9);

        public override void Init(ReadOnlySpan<byte> first)
        {
            _lastTime[0] = BinaryPrimitives.ReadInt64LittleEndian(first);
        }

        public override void Read(Arithmetic dec, Span<byte> item)
        {
            for (var guard = 0; guard < 8; guard++)
            {
                if (Step(dec))
                {
                    break;
                }
            }

            BinaryPrimitives.WriteInt64LittleEndian(item, _lastTime[_last]);
        }

        private void Full(Arithmetic dec)
        {
            _next = (_next + 1) & 3;
            var high = (ulong)(uint)_time.Decompress(dec, (int)(_lastTime[_last] >> 32), 8);
            var low = dec.ReadInt();
            _lastTime[_next] = (long)((high << 32) | low);
            _last = _next;
            _lastDiff[_last] = 0;
            _extreme[_last] = 0;
        }

        /// <summary>One decoding step; false when the symbol only switched sequences and the step must run again.</summary>
        private bool Step(Arithmetic dec)
        {
            if (_lastDiff[_last] == 0)
            {
                var multi = dec.DecodeSymbol(_zeroDiff);
                if (multi == 1)
                {
                    _lastDiff[_last] = _time.Decompress(dec, 0, 0);
                    _lastTime[_last] += _lastDiff[_last];
                    _extreme[_last] = 0;
                }
                else if (multi == 2)
                {
                    Full(dec);
                }
                else if (multi > 2)
                {
                    _last = (_last + multi - 2) & 3;
                    return false;
                }

                return true;
            }
            else
            {
                var multi = dec.DecodeSymbol(_multi);
                if (multi == 1)
                {
                    _lastTime[_last] += _time.Decompress(dec, _lastDiff[_last], 1);
                    _extreme[_last] = 0;
                }
                else if (multi < MultiUnchanged)
                {
                    int diff;
                    if (multi == 0)
                    {
                        diff = _time.Decompress(dec, 0, 7);
                        Extreme(diff);
                    }
                    else if (multi < Multi)
                    {
                        diff = _time.Decompress(dec, multi * _lastDiff[_last], multi < 10 ? 2 : 3);
                    }
                    else if (multi == Multi)
                    {
                        diff = _time.Decompress(dec, Multi * _lastDiff[_last], 4);
                        Extreme(diff);
                    }
                    else
                    {
                        multi = Multi - multi;
                        if (multi > MultiMinus)
                        {
                            diff = _time.Decompress(dec, multi * _lastDiff[_last], 5);
                        }
                        else
                        {
                            diff = _time.Decompress(dec, MultiMinus * _lastDiff[_last], 6);
                            Extreme(diff);
                        }
                    }

                    _lastTime[_last] += diff;
                }
                else if (multi == MultiCodeFull)
                {
                    Full(dec);
                }
                else
                {
                    _last = (_last + multi - MultiCodeFull) & 3;
                    return false;
                }

                return true;
            }
        }

        private void Extreme(int diff)
        {
            if (++_extreme[_last] > 3)
            {
                _lastDiff[_last] = diff;
                _extreme[_last] = 0;
            }
        }
    }

    /// <summary>Type 8: red, green and blue as 16-bit values, coded byte by byte against the previous colour.</summary>
    private sealed class Rgb12 : ItemDecoder
    {
        private readonly int[] _last = new int[3];
        private readonly SymbolModel _used = new(128);
        private readonly SymbolModel[] _diff = Enumerable.Range(0, 6).Select(_ => new SymbolModel(256)).ToArray();

        public override void Init(ReadOnlySpan<byte> first)
        {
            for (var i = 0; i < 3; i++)
            {
                _last[i] = BinaryPrimitives.ReadUInt16LittleEndian(first[(2 * i)..]);
            }
        }

        public override void Read(Arithmetic dec, Span<byte> item)
        {
            var sym = dec.DecodeSymbol(_used);
            var cur = new int[3];
            if ((sym & 1) != 0)
            {
                cur[0] = Fold(dec.DecodeSymbol(_diff[0]) + (_last[0] & 255));
            }
            else
            {
                cur[0] = _last[0] & 0xFF;
            }

            if ((sym & 2) != 0)
            {
                cur[0] |= Fold(dec.DecodeSymbol(_diff[1]) + (_last[0] >> 8)) << 8;
            }
            else
            {
                cur[0] |= _last[0] & 0xFF00;
            }

            if ((sym & 64) != 0)
            {
                var diff = (cur[0] & 0xFF) - (_last[0] & 0xFF);
                if ((sym & 4) != 0)
                {
                    cur[1] = Fold(dec.DecodeSymbol(_diff[2]) + Clamp(diff + (_last[1] & 255)));
                }
                else
                {
                    cur[1] = _last[1] & 0xFF;
                }

                if ((sym & 16) != 0)
                {
                    diff = (diff + ((cur[1] & 0xFF) - (_last[1] & 0xFF))) / 2;
                    cur[2] = Fold(dec.DecodeSymbol(_diff[4]) + Clamp(diff + (_last[2] & 255)));
                }
                else
                {
                    cur[2] = _last[2] & 0xFF;
                }

                diff = (cur[0] >> 8) - (_last[0] >> 8);
                if ((sym & 8) != 0)
                {
                    cur[1] |= Fold(dec.DecodeSymbol(_diff[3]) + Clamp(diff + (_last[1] >> 8))) << 8;
                }
                else
                {
                    cur[1] |= _last[1] & 0xFF00;
                }

                if ((sym & 32) != 0)
                {
                    diff = (diff + ((cur[1] >> 8) - (_last[1] >> 8))) / 2;
                    cur[2] |= Fold(dec.DecodeSymbol(_diff[5]) + Clamp(diff + (_last[2] >> 8))) << 8;
                }
                else
                {
                    cur[2] |= _last[2] & 0xFF00;
                }
            }
            else
            {
                cur[1] = cur[0];
                cur[2] = cur[0];
            }

            for (var i = 0; i < 3; i++)
            {
                _last[i] = cur[i];
                BinaryPrimitives.WriteUInt16LittleEndian(item[(2 * i)..], (ushort)cur[i]);
            }
        }
    }

    /// <summary>Type 0: extra bytes, each predicted from the same byte of the previous point.</summary>
    private sealed class ExtraBytes : ItemDecoder
    {
        private readonly byte[] _last;
        private readonly SymbolModel[] _models;

        public ExtraBytes(int size)
        {
            _last = new byte[size];
            _models = new SymbolModel[size];
            for (var i = 0; i < size; i++)
            {
                _models[i] = new SymbolModel(256);
            }
        }

        public override void Init(ReadOnlySpan<byte> first) => first.CopyTo(_last);

        public override void Read(Arithmetic dec, Span<byte> item)
        {
            for (var i = 0; i < _last.Length; i++)
            {
                _last[i] = (byte)Fold(_last[i] + dec.DecodeSymbol(_models[i]));
            }

            _last.CopyTo(item);
        }
    }
}
