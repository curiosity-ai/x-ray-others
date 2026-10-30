using System.Buffers.Binary;

namespace XRay.ThreeD.Internal;

/// <summary>One decoded attribute: <see cref="Values"/> holds <see cref="NumComponents"/> numbers per output point.</summary>
internal sealed class DracoAttribute
{
    public int UniqueId;
    public int Type;
    public int DataType;
    public int NumComponents;
    public bool Normalized;
    public float[] Values = [];
}

/// <summary>The decoded geometry: one point per distinct (position, attributes) combination, triangles as indices into the points.</summary>
internal sealed class DracoGeometry
{
    public int NumPoints;
    public uint[] Indices = [];
    public List<DracoAttribute> Attributes = new();
}

/// <summary>
/// Decoder for the Draco bitstream (version 2.2: sequential and EdgeBreaker connectivity, standard and valence traversal, sequential
/// integer, quantised and normal attribute coders, the difference, parallelogram, constrained multi-parallelogram, texture coordinate and
/// geometric normal predictions, rANS symbol coding), written from the public bitstream specification (google.github.io/draco/spec).
/// Every count is checked against the input size, loops are bounded and anything inconsistent is a <see cref="ModelFormatException"/>.
/// </summary>
internal sealed partial class DracoDecoder
{
    private const int Sequential = 0;
    private const int EdgeBreaker = 1;
    private const int TopologyC = 0, TopologyS = 1, TopologyL = 3, TopologyR = 5, TopologyE = 7;

    private readonly byte[] _data;
    private readonly long _maxFaces;
    private readonly CancellationToken _cancel;
    private int _encoderType;
    private int _method;

    private DracoDecoder(byte[] data, long maxFaces, CancellationToken cancel)
    {
        _data = data;
        _maxFaces = maxFaces;
        _cancel = cancel;
    }

    /// <summary>Decodes a Draco buffer (a glTF <c>KHR_draco_mesh_compression</c> buffer view, or a .drc file).</summary>
    public static DracoGeometry Decode(byte[] data, long maxFaces, CancellationToken cancel)
    {
        return new DracoDecoder(data, Math.Min(maxFaces, 64L * data.Length + 64), cancel).Run();
    }


    private static ModelFormatException Bad(string message) => new("The Draco data is damaged: " + message);

    private DracoGeometry Run()
    {
        var r = new Reader(_data, 0, _data.Length);
        if (_data.Length < 11 || _data[0] != 'D' || _data[1] != 'R' || _data[2] != 'A' || _data[3] != 'C' || _data[4] != 'O')
        {
            throw Bad("the DRACO signature is missing.");
        }

        r.Pos = 5;
        int major = r.U8(), minor = r.U8();
        if (major != 2 || minor < 2)
        {
            throw new ModelFormatException($"Draco bitstream version {major}.{minor} is not supported (2.2 and later are).");
        }

        _encoderType = r.U8();
        _method = r.U8();
        var flags = r.U16();
        if (_encoderType is not (0 or 1) || _method is not (0 or 1))
        {
            throw Bad("unknown encoder type or method.");
        }

        if ((flags & 0x8000) != 0)
        {
            SkipMetadata(r);
        }

        if (_encoderType == 0)
        {
            return DecodePointCloud(r);
        }

        return _method == Sequential ? DecodeSequentialMesh(r) : DecodeEdgeBreakerMesh(r);
    }

    // ---- metadata (skipped: names and values are not needed)

    private static void SkipMetadata(Reader r)
    {
        var count = r.Var32();
        if (count > 4096)
        {
            throw Bad("too many metadata entries.");
        }

        for (var i = 0; i < count; i++)
        {
            r.Var32();
            SkipMetadataElement(r, 0);
        }

        SkipMetadataElement(r, 0);
    }

    private static void SkipMetadataElement(Reader r, int depth)
    {
        if (depth > 8)
        {
            throw Bad("the metadata is nested too deeply.");
        }

        var entries = r.Var32();
        for (uint i = 0; i < entries; i++)
        {
            r.Skip(r.U8());
            r.Skip((long)r.Var32());
        }

        var subs = r.Var32();
        for (uint i = 0; i < subs; i++)
        {
            r.Skip(r.U8());
            SkipMetadataElement(r, depth + 1);
        }
    }

    // ---- reading

    private sealed class Reader
    {
        public readonly byte[] Data;
        public int Pos;
        public readonly int End;

        public Reader(byte[] data, int pos, int end)
        {
            Data = data;
            Pos = pos;
            End = end;
        }

        public byte U8()
        {
            if (Pos >= End)
            {
                throw Bad("unexpected end of data.");
            }

            return Data[Pos++];
        }

        public ushort U16()
        {
            Need(2);
            var v = BinaryPrimitives.ReadUInt16LittleEndian(Data.AsSpan(Pos));
            Pos += 2;
            return v;
        }

        public uint U32()
        {
            Need(4);
            var v = BinaryPrimitives.ReadUInt32LittleEndian(Data.AsSpan(Pos));
            Pos += 4;
            return v;
        }

        public int I32() => (int)U32();

        public float F32() => BitConverter.UInt32BitsToSingle(U32());

        public uint Var32()
        {
            var v = Var64();
            if (v > uint.MaxValue)
            {
                throw Bad("a 32-bit count is too large.");
            }

            return (uint)v;
        }

        public ulong Var64()
        {
            ulong result = 0;
            for (var shift = 0; shift < 70; shift += 7)
            {
                var b = U8();
                if (shift < 64)
                {
                    result |= (ulong)(b & 0x7F) << shift;
                }

                if ((b & 0x80) == 0)
                {
                    return result;
                }
            }

            throw Bad("a variable-length integer is too long.");
        }

        public void Need(long n)
        {
            if (n < 0 || Pos + n > End)
            {
                throw Bad("unexpected end of data.");
            }
        }

        public void Skip(long n)
        {
            Need(n);
            Pos += (int)n;
        }

        public byte[] Bytes(ulong n)
        {
            if (n > (ulong)(End - Pos))
            {
                throw Bad("a block runs past the end of the data.");
            }

            var result = Data.AsSpan(Pos, (int)n).ToArray();
            Pos += (int)n;
            return result;
        }
    }

    /// <summary>Bits least significant first, as Draco's direct bit coder packs them.</summary>
    private sealed class BitReader
    {
        private readonly byte[] _data;
        private readonly int _start;
        private readonly int _end;
        private long _bit;

        public BitReader(byte[] data, int start, int end)
        {
            _data = data;
            _start = start;
            _end = end;
        }

        public long BitsRead => _bit;

        public uint Read(int count)
        {
            uint value = 0;
            for (var i = 0; i < count; i++)
            {
                var index = _start + (int)(_bit >> 3);
                if (index >= _end)
                {
                    throw Bad("a bit sequence runs past the end of the data.");
                }

                value |= (uint)((_data[index] >> (int)(_bit & 7)) & 1) << i;
                _bit++;
            }

            return value;
        }
    }

    // ---- rANS

    private const int IoBase = 256;

    private sealed class Rans
    {
        public byte[] Buf = [];
        public int Offset;
        public uint State;

        public void Init(byte[] buf, int size, uint lBase)
        {
            if (size <= 0 || size > buf.Length)
            {
                throw Bad("an empty rANS block.");
            }

            Buf = buf;
            var x = buf[size - 1] >> 6;
            switch (x)
            {
                case 0:
                    Offset = size - 1;
                    State = (uint)(buf[size - 1] & 0x3F);
                    break;
                case 1:
                    Need(size, 2);
                    Offset = size - 2;
                    State = (uint)(BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(size - 2)) & 0x3FFF);
                    break;
                case 2:
                    Need(size, 3);
                    Offset = size - 3;
                    State = (uint)((buf[size - 3] | (buf[size - 2] << 8) | (buf[size - 1] << 16)) & 0x3FFFFF);
                    break;
                default:
                    Need(size, 4);
                    Offset = size - 4;
                    State = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(size - 4)) & 0x3FFFFFFF;
                    break;
            }

            State += lBase;
        }

        private static void Need(int size, int n)
        {
            if (size < n)
            {
                throw Bad("a truncated rANS block.");
            }
        }

        /// <summary>One bit with probability of zero <paramref name="p0"/>/256 (the "rabs" coder).</summary>
        public bool ReadBit(int p0)
        {
            var p = 256u - (uint)p0;
            if (State < 4096)
            {
                if (Offset <= 0)
                {
                    throw Bad("a rANS bit block ran out of data.");
                }

                State = State * IoBase + Buf[--Offset];
            }

            var x = State;
            var quot = x / 256;
            var rem = x % 256;
            var xn = quot * p;
            var val = rem < p;
            State = val ? xn + rem : x - xn - p;
            return val;
        }
    }

    private sealed class SymbolTable
    {
        public int[] Prob = [];
        public int[] Cum = [];
        public ushort[] Lookup = [];
        public int Precision;
    }

    private static SymbolTable BuildSymbolTables(Reader r, uint numSymbols, int precision)
    {
        if (numSymbols == 0 || numSymbols > (1 << 20))
        {
            throw Bad("an unreasonable symbol count.");
        }

        var probs = new int[numSymbols];
        for (uint i = 0; i < numSymbols; i++)
        {
            var data = r.U8();
            var token = data & 3;
            if (token == 3)
            {
                var offset = data >> 2;
                if (i + offset >= numSymbols)
                {
                    throw Bad("a symbol table runs past its end.");
                }

                i += (uint)offset;
            }
            else
            {
                var prob = data >> 2;
                for (var j = 0; j < token; j++)
                {
                    prob |= r.U8() << (8 * (j + 1) - 2);
                }

                probs[i] = prob;
            }
        }

        var table = new SymbolTable { Prob = probs, Cum = new int[numSymbols], Lookup = new ushort[precision], Precision = precision };
        long cum = 0;
        for (var i = 0; i < probs.Length; i++)
        {
            table.Cum[i] = (int)cum;
            cum += probs[i];
            if (cum > precision)
            {
                throw Bad("a symbol table is inconsistent.");
            }

            for (var j = table.Cum[i]; j < cum; j++)
            {
                table.Lookup[j] = (ushort)Math.Min(i, ushort.MaxValue);
            }
        }

        return table;
    }

    private static int ReadSymbol(Rans ans, SymbolTable table, uint lBase)
    {
        while (ans.State < lBase && ans.Offset > 0)
        {
            ans.State = ans.State * IoBase + ans.Buf[--ans.Offset];
        }

        var quo = ans.State / (uint)table.Precision;
        var rem = (int)(ans.State % (uint)table.Precision);
        var symbol = table.Lookup[rem];
        ans.State = quo * (uint)table.Prob[symbol] + (uint)rem - (uint)table.Cum[symbol];
        return symbol;
    }

    /// <summary>The "symbols" coder: values either as rANS tags with raw bits (tagged) or as rANS symbols (raw).</summary>
    private uint[] DecodeSymbols(Reader r, long numValues, int numComponents)
    {
        if (numValues == 0)
        {
            return [];
        }

        if (numValues > _maxFaces * 3 + 4_000_000 && numValues > (long)r.End * 64)
        {
            throw Bad("an unreasonable number of values.");
        }

        var scheme = r.U8();
        var result = new uint[numValues];
        if (scheme == 0)
        {
            var numSymbols = r.Var32();
            if (numSymbols > 32)
            {
                throw Bad("too many tags.");
            }

            var table = BuildSymbolTables(r, numSymbols, 4096);
            var size = r.Var64();
            var ans = new Rans();
            var encoded = r.Bytes(size);
            ans.Init(encoded, encoded.Length, 16384);
            var bits = new BitReader(r.Data, r.Pos, r.End);
            var n = 0;
            for (long i = 0; i < numValues; i += numComponents)
            {
                if ((i & 4095) == 0)
                {
                    _cancel.ThrowIfCancellationRequested();
                }

                var length = ReadSymbol(ans, table, 16384);
                for (var j = 0; j < numComponents; j++)
                {
                    result[n++] = bits.Read(length);
                }
            }

            r.Skip((bits.BitsRead + 7) / 8);
        }
        else if (scheme == 1)
        {
            var maxBitLength = r.U8();
            var numSymbols = r.Var32();
            var precisionBits = Math.Clamp(3 * maxBitLength / 2, 12, 20);
            var precision = 1 << precisionBits;
            var table = BuildSymbolTables(r, numSymbols, precision);
            var size = r.Var64();
            var encoded = r.Bytes(size);
            var ans = new Rans();
            var lBase = (uint)precision * 4;
            ans.Init(encoded, encoded.Length, lBase);
            for (long i = 0; i < numValues; i++)
            {
                if ((i & 4095) == 0)
                {
                    _cancel.ThrowIfCancellationRequested();
                }

                result[i] = (uint)ReadSymbol(ans, table, lBase);
            }
        }
        else
        {
            throw Bad("an unknown symbol coding scheme.");
        }

        return result;
    }

    /// <summary>A bit coder block: probability of zero, size, then the rANS data.</summary>
    private static Rans ReadRansBits(Reader r, out int probZero)
    {
        probZero = r.U8();
        var size = r.Var32();
        var data = r.Bytes(size);
        var ans = new Rans();
        ans.Init(data, data.Length, 4096);
        return ans;
    }

    // ---- point clouds and sequential meshes

    private DracoGeometry DecodePointCloud(Reader r)
    {
        if (_method != Sequential)
        {
            throw new UnsupportedModelFormatException(ModelFormats.Gltf, "Draco point clouds in the kd-tree form are not supported.");
        }

        var numPoints = (long)r.U32();
        if (numPoints > _maxFaces * 3 + 1_000_000 || numPoints > (long)_data.Length * 64 + 64)
        {
            throw Bad("an unreasonable number of points.");
        }

        _numPoints = (int)numPoints;
        return DecodeAttributes(r, null);
    }

    private DracoGeometry DecodeSequentialMesh(Reader r)
    {
        var numFaces = (long)r.Var32();
        var numPoints = (long)r.Var32();
        var method = r.U8();
        if (numFaces > _maxFaces || numPoints > _maxFaces * 3 + 3 || (numFaces > 0 && numPoints == 0))
        {
            throw Bad("an unreasonable number of faces or points.");
        }

        var indices = new uint[numFaces * 3];
        if (method == 0)
        {
            var symbols = DecodeSymbols(r, numFaces * 3, 1);
            long last = 0;
            for (var i = 0; i < symbols.Length; i++)
            {
                long diff = symbols[i] >> 1;
                if ((symbols[i] & 1) != 0)
                {
                    diff = -diff;
                }

                last += diff;
                indices[i] = (uint)last;
            }
        }
        else if (method == 1)
        {
            for (var i = 0; i < indices.Length; i++)
            {
                indices[i] = numPoints < 256 ? r.U8() : numPoints < 65536 ? r.U16() : numPoints < (1 << 21) ? r.Var32() : r.U32();
            }
        }
        else
        {
            throw Bad("an unknown index coding.");
        }

        foreach (var index in indices)
        {
            if (index >= numPoints)
            {
                throw Bad("a face refers to a point that does not exist.");
            }
        }

        _numPoints = (int)numPoints;
        return DecodeAttributes(r, indices);
    }
}
