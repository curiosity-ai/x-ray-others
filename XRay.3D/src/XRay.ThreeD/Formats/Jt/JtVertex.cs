namespace XRay.ThreeD.Formats.Jt;

using XRay.ThreeD.Internal;

/// <summary>Decoded vertex or normal data: <see cref="Count"/> tuples of <see cref="Components"/> floats.</summary>
internal sealed class JtDecoded
{
    public static readonly JtDecoded None = new(0, 0, []);

    public JtDecoded(int count, int components, float[] data)
    {
        Count = count;
        Components = components;
        Data = data;
    }

    public int Count { get; }

    public int Components { get; }

    public float[] Data { get; }

    public float Get(int i, int j) => Data[i * Components + j];
}

internal struct JtQuant
{
    public float Min, Max;
    public byte Bits;
}

/// <summary>
/// The vertex and normal arrays of a shape LOD: per-component integer packages, dequantised (coordinates), rebuilt from sign, exponent and mantissa, taken as
/// raw floats, or decoded from the sextant/octant/angle code of unit normals (Deering's normal compression).
/// </summary>
internal sealed class JtVertexData
{
    private const int TableBits = 13;
    private const int TableSize = 1 << TableBits;
    private const int TableTail = 16;
    private const double MaxPsi = 0.615479709;

    private enum Scheme
    {
        Quantized,
        Deering,
        ExpMant,
        RawFloat,
    }

    private JtPackage[] _packages = [];
    private UnpackKind _unpack;
    private Scheme _scheme;
    private bool _packed;
    private int _bits;
    private JtQuant[] _quant = new JtQuant[3];

    public int OutVertexCount => _packages.Length == 0 ? 0 : _packages[0].OutValCount;

    private int OutComponents => _scheme switch
    {
        Scheme.Quantized or Scheme.RawFloat => _packages.Length,
        Scheme.Deering => 3,
        _ => _packages.Length / 2,
    };

    private static int ReadCount(JtCursor c)
    {
        var raw = c.I32();
        if (raw < 0)
        {
            throw c.Fail("A JT vertex count is negative");
        }

        return raw;
    }

    private static JtQuant[] ReadQuantizer(JtCursor c)
    {
        var q = new JtQuant[3];
        for (var i = 0; i < 3; i++)
        {
            q[i] = new JtQuant { Min = c.F32(), Max = c.F32(), Bits = c.U8() };
        }

        return q;
    }

    public static JtVertexData LoadQuantizedCoords(JtCursor c, CancellationToken token)
    {
        var quantizer = ReadQuantizer(c);
        var count = ReadCount(c);
        var data = new JtVertexData { _packages = new JtPackage[3], _unpack = UnpackKind.Lag1, _scheme = Scheme.Quantized, _quant = quantizer };
        data.Load(c, true, count, token);
        return data;
    }

    public static JtVertexData LoadCompressedCoords(JtCursor c, CancellationToken token)
    {
        var count = ReadCount(c);
        var components = c.U8();
        var quantizer = ReadQuantizer(c);
        var scheme = quantizer[0].Bits != 0 ? Scheme.Quantized : c.Major >= 10 ? Scheme.RawFloat : Scheme.ExpMant;
        var data = new JtVertexData { _packages = new JtPackage[PackageCount(scheme, components, false)], _unpack = UnpackKind.Lag1, _scheme = scheme, _quant = quantizer };
        data.Load(c, false, count, token);
        c.I32();
        return data;
    }

    public static JtVertexData LoadQuantizedNormals(JtCursor c, CancellationToken token)
    {
        var bits = c.U8();
        var count = ReadCount(c);
        var data = new JtVertexData { _packages = new JtPackage[4], _unpack = UnpackKind.Lag1, _scheme = Scheme.Deering, _bits = bits, _packed = false };
        data.Load(c, true, count, token);
        return data;
    }

    public static JtVertexData LoadCompressedNormals(JtCursor c, CancellationToken token)
    {
        var count = ReadCount(c);
        var components = c.U8();
        var bits = c.U8();
        Scheme scheme;
        var packed = false;
        if (bits != 0)
        {
            scheme = Scheme.Deering;
            packed = c.Major >= 10;
        }
        else
        {
            scheme = c.Major >= 10 ? Scheme.RawFloat : Scheme.ExpMant;
        }

        var data = new JtVertexData { _packages = new JtPackage[PackageCount(scheme, components, packed)], _unpack = UnpackKind.Null, _scheme = scheme, _bits = bits, _packed = packed };
        data.Load(c, false, count, token);
        c.I32();
        return data;
    }

    private static int PackageCount(Scheme scheme, int components, bool packed) => scheme switch
    {
        Scheme.ExpMant => components * 2,
        Scheme.Deering => packed ? 1 : 4,
        _ => components,
    };

    private void Load(JtCursor c, bool mark1, int count, CancellationToken token)
    {
        for (var i = 0; i < _packages.Length; i++)
        {
            _packages[i] = mark1 ? JtPackage.Load1(c, token) : JtPackage.Load2(c, token);
            if (_packages[i].OutValCount != count)
            {
                throw c.Fail("A JT vertex package holds " + _packages[i].OutValCount + " values, expected " + count);
            }
        }
    }

    public JtDecoded Decode(bool bigEndian, CancellationToken token)
    {
        var count = OutVertexCount;
        var comps = OutComponents;
        if ((long)count * comps > 1 << 28)
        {
            throw new ModelFormatException("A JT vertex array is too large.");
        }

        var data = new float[count * comps];
        switch (_scheme)
        {
            case Scheme.Quantized:
            {
                if (comps > 3)
                {
                    throw new ModelFormatException("Quantized JT vertex data has more than three components.");
                }

                for (var j = 0; j < comps; j++)
                {
                    var codes = _packages[j].Decode(bigEndian, _unpack, token);
                    var q = _quant[j];
                    double min = q.Min, max = q.Max;
                    var maxCode = q.Bits < 32 ? (double)(1u << q.Bits) : 4294967295.0;
                    var multiplier = (max - min) / maxCode;
                    for (var i = 0; i < codes.Length && i < count; i++)
                    {
                        Cancel.Every(token, i);
                        data[i * comps + j] = (float)(min + ((double)unchecked((uint)codes[i]) - 0.5) * multiplier);
                    }
                }

                break;
            }

            case Scheme.RawFloat:
                for (var j = 0; j < comps; j++)
                {
                    var codes = _packages[j].Decode(bigEndian, _unpack, token);
                    for (var i = 0; i < codes.Length && i < count; i++)
                    {
                        data[i * comps + j] = BitConverter.Int32BitsToSingle(codes[i]);
                    }
                }

                break;
            case Scheme.ExpMant:
                for (var j = 0; j < comps; j++)
                {
                    var exponent = _packages[j * 2].Decode(bigEndian, _unpack, token);
                    var mantissa = _packages[j * 2 + 1].Decode(bigEndian, _unpack, token);
                    for (var i = 0; i < Math.Min(Math.Min(exponent.Length, mantissa.Length), count); i++)
                    {
                        data[i * comps + j] = BitConverter.Int32BitsToSingle(unchecked((exponent[i] << 23) | mantissa[i]));
                    }
                }

                break;
            default:
                DecodeNormals(bigEndian, count, data, token);
                break;
        }

        return new JtDecoded(count, comps, data);
    }

    private void DecodeNormals(bool bigEndian, int count, float[] data, CancellationToken token)
    {
        if (_bits > TableBits)
        {
            throw new ModelFormatException("A JT normal code uses more bits than the lookup table provides.");
        }

        var table = LookupTable.Instance;
        var width = _bits;
        var mask = (1u << width) - 1;
        var offset = TableBits - width;
        var decoded = new int[_packed ? 1 : _packages.Length][];
        for (var k = 0; k < decoded.Length; k++)
        {
            decoded[k] = _packages[k].Decode(bigEndian, _unpack, token);
        }

        foreach (var d in decoded)
        {
            if (d.Length < count)
            {
                throw new ModelFormatException("A JT normal package is shorter than its count.");
            }
        }

        for (var i = 0; i < count; i++)
        {
            Cancel.Every(token, i);
            uint sextant, octant, thetaCode, psiCode;
            if (_packed)
            {
                var code = (uint)decoded[0][i];
                sextant = (code >> (2 * width + 3)) & 7;
                octant = (code >> (2 * width)) & 7;
                thetaCode = (code >> width) & mask;
                psiCode = code & mask;
            }
            else
            {
                sextant = (uint)decoded[0][i];
                octant = (uint)decoded[1][i];
                thetaCode = (uint)decoded[2][i];
                psiCode = (uint)decoded[3][i];
            }

            var ti = (long)(thetaCode + (sextant & 1)) << offset;
            var pi = (long)psiCode << offset;
            var (thetaSin, thetaCos) = ti < table.Theta.Length ? table.Theta[ti] : (0.0, 0.0);
            var (psiSin, psiCos) = pi < table.Psi.Length ? table.Psi[pi] : (0.0, 0.0);
            var x = (float)(psiCos * thetaCos);
            var y = (float)psiSin;
            var z = (float)(psiCos * thetaSin);
            float a, b, c;
            switch (sextant)
            {
                case 1: (a, b, c) = (z, y, x); break;
                case 2: (a, b, c) = (y, z, x); break;
                case 3: (a, b, c) = (y, x, z); break;
                case 4: (a, b, c) = (z, x, y); break;
                case 5: (a, b, c) = (x, z, y); break;
                default: (a, b, c) = (x, y, z); break;
            }

            if ((octant & 4) == 0)
            {
                a = -a;
            }

            if ((octant & 2) == 0)
            {
                b = -b;
            }

            if ((octant & 1) == 0)
            {
                c = -c;
            }

            data[i * 3] = a;
            data[i * 3 + 1] = b;
            data[i * 3 + 2] = c;
        }
    }

    private sealed class LookupTable
    {
        private static readonly double[] Phi =
        [
            0.904837, 0.818731, 0.740818, 0.670320, 0.606531, 0.548812, 0.449329, 0.367879,
            0.22313, 0.135335, 0.090484, 0.163746, 0.222245, 0.268128, 0.303265, 0.329287,
        ];

        public static readonly LookupTable Instance = new();

        public readonly (double Sin, double Cos)[] Theta;
        public readonly (double Sin, double Cos)[] Psi;

        private LookupTable()
        {
            var size = TableSize + TableTail;
            Theta = new (double, double)[size];
            Psi = new (double, double)[size];
            for (var i = 0; i <= TableSize; i++)
            {
                var back = (double)(TableSize - i);
                var t = Math.Asin(Math.Tan(MaxPsi * back / TableSize));
                Theta[i] = (Math.Sin(t), Math.Cos(t));
                var p = MaxPsi * ((double)i / TableSize);
                Psi[i] = (Math.Sin(p), Math.Cos(p));
            }

            for (var k = 0; k < TableTail - 1; k++)
            {
                var t = Math.Asin(Math.Tan(Phi[k]));
                Theta[TableSize + 1 + k] = (Math.Sin(t), Math.Cos(t));
                Psi[TableSize + 1 + k] = (Math.Sin(Phi[k]), Math.Cos(Phi[k]));
            }
        }
    }
}
