using System.Buffers.Binary;

namespace XRay.ThreeD.Formats.PointCloud;

using XRay.ThreeD.Internal;
using static LazDecoder;

/// <summary>
/// The layered ("LAS 1.4") LASzip items, chunked: POINT14 (type 10), RGB14 (11), RGBNIR14 (12), WAVEPACKET14 (13) and BYTE14 (14), versions 3 and 4.
/// A chunk holds the first point of each item raw, the chunk's point count, the byte size of each layer of each item, then the layers: every field
/// of a point (position and return structure, height, classification, flags, intensity, scan angle, user data, point source, GPS time, colours, near
/// infrared, wave packet, each extra byte) is coded in a stream of its own, with up to four contexts selected by the scanner channel. Ported to C#
/// from the Rust decompressor of the laz-rs crate (Apache-2.0, itself a port of the LASzip reference by Martin Isenburg); see THIRD_PARTY_NOTICES.md.
/// The behaviour in which version 3 predicts from the new context's last value only once (the reference writers' behaviour) is kept, because it is
/// what files in the wild contain.
/// </summary>
internal static class LazLayered
{
    private const int GpsMulti = 500;
    private const int GpsMultiMinus = -10;
    private const int GpsMultiCodeFull = GpsMulti - GpsMultiMinus + 1;
    private const int GpsMultiTotal = GpsMulti - GpsMultiMinus + 5;

    private static readonly byte[,] NumberReturnMap6 =
    {
        { 0, 1, 2, 3, 4, 5, 3, 4, 4, 5, 5, 5, 5, 5, 5, 5 },
        { 1, 0, 1, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3, 3 },
        { 2, 1, 2, 4, 4, 4, 4, 4, 4, 4, 4, 3, 3, 3, 3, 3 },
        { 3, 3, 4, 5, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4 },
        { 4, 3, 4, 4, 5, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4 },
        { 5, 3, 4, 4, 4, 5, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4 },
        { 3, 3, 4, 4, 4, 4, 5, 4, 4, 4, 4, 4, 4, 4, 4, 4 },
        { 4, 3, 4, 4, 4, 4, 4, 5, 4, 4, 4, 4, 4, 4, 4, 4 },
        { 4, 3, 4, 4, 4, 4, 4, 4, 5, 4, 4, 4, 4, 4, 4, 4 },
        { 5, 3, 4, 4, 4, 4, 4, 4, 4, 5, 4, 4, 4, 4, 4, 4 },
        { 5, 3, 4, 4, 4, 4, 4, 4, 4, 4, 5, 4, 4, 4, 4, 4 },
        { 5, 3, 3, 4, 4, 4, 4, 4, 4, 4, 4, 5, 5, 4, 4, 4 },
        { 5, 3, 3, 4, 4, 4, 4, 4, 4, 4, 4, 5, 5, 5, 4, 4 },
        { 5, 3, 3, 4, 4, 4, 4, 4, 4, 4, 4, 4, 5, 5, 5, 4 },
        { 5, 3, 3, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 5, 5, 5 },
        { 5, 3, 3, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 4, 5, 5 },
    };

    private static readonly byte[,] NumberReturnLevel8 =
    {
        { 0, 1, 2, 3, 4, 5, 6, 7, 7, 7, 7, 7, 7, 7, 7, 7 },
        { 1, 0, 1, 2, 3, 4, 5, 6, 7, 7, 7, 7, 7, 7, 7, 7 },
        { 2, 1, 0, 1, 2, 3, 4, 5, 6, 7, 7, 7, 7, 7, 7, 7 },
        { 3, 2, 1, 0, 1, 2, 3, 4, 5, 6, 7, 7, 7, 7, 7, 7 },
        { 4, 3, 2, 1, 0, 1, 2, 3, 4, 5, 6, 7, 7, 7, 7, 7 },
        { 5, 4, 3, 2, 1, 0, 1, 2, 3, 4, 5, 6, 7, 7, 7, 7 },
        { 6, 5, 4, 3, 2, 1, 0, 1, 2, 3, 4, 5, 6, 7, 7, 7 },
        { 7, 6, 5, 4, 3, 2, 1, 0, 1, 2, 3, 4, 5, 6, 7, 7 },
        { 7, 7, 6, 5, 4, 3, 2, 1, 0, 1, 2, 3, 4, 5, 6, 7 },
        { 7, 7, 7, 6, 5, 4, 3, 2, 1, 0, 1, 2, 3, 4, 5, 6 },
        { 7, 7, 7, 7, 6, 5, 4, 3, 2, 1, 0, 1, 2, 3, 4, 5 },
        { 7, 7, 7, 7, 7, 6, 5, 4, 3, 2, 1, 0, 1, 2, 3, 4 },
        { 7, 7, 7, 7, 7, 7, 6, 5, 4, 3, 2, 1, 0, 1, 2, 3 },
        { 7, 7, 7, 7, 7, 7, 7, 6, 5, 4, 3, 2, 1, 0, 1, 2 },
        { 7, 7, 7, 7, 7, 7, 7, 7, 6, 5, 4, 3, 2, 1, 0, 1 },
        { 7, 7, 7, 7, 7, 7, 7, 7, 7, 6, 5, 4, 3, 2, 1, 0 },
    };

    public static bool IsLayeredItem(int type) => type is >= 10 and <= 14;

    /// <summary>Record size of an item, for the sum check.</summary>
    public static bool Supports(Item item) => item.Version is 3 or 4 && item.Type switch
    {
        10 => item.Size == 30,
        11 => item.Size == 6,
        12 => item.Size == 8,
        13 => item.Size == 29,
        14 => item.Size >= 1,
        _ => false,
    };

    internal abstract class LayeredItem
    {
        public int Size;

        /// <summary>Reads the first point's bytes (they seed the predictors) and copies them to the output.</summary>
        public abstract void InitFirst(ReadOnlySpan<byte> raw, ref int context);

        public abstract void ReadLayerSizes(ReadOnlySpan<byte> span, ref int pos);

        public abstract void ReadLayers(ReadOnlyMemory<byte> file, ref int pos, CancellationToken token);

        public abstract void Next(Span<byte> item, ref int context);
    }

    public static LayeredItem[] CreateItems(Item[] items)
    {
        var list = new List<LayeredItem>();
        foreach (var item in items)
        {
            var v4 = item.Version == 4;
            switch (item.Type)
            {
                case 10: list.Add(new Point14(v4)); break;
                case 11: list.Add(new Rgb14(v4)); break;
                case 12: list.Add(new Rgb14(v4)); list.Add(new Nir14(v4)); break;
                case 13: list.Add(new Wave14(v4)); break;
                default: list.Add(new Bytes14(item.Size, v4)); break;
            }
        }

        return list.ToArray();
    }

    private static Arithmetic? Layer(ReadOnlyMemory<byte> file, ref int pos, int size, CancellationToken token)
    {
        if (size <= 0)
        {
            return null;
        }

        if ((long)pos + size > file.Length)
        {
            throw new ModelFormatException("A LAZ layer runs past the end of the data.");
        }

        var decoder = new Arithmetic(file.Slice(pos, size), 0, token);
        pos += size;
        return decoder;
    }

    private static int U32(ReadOnlySpan<byte> s, ref int pos)
    {
        if (pos < 0 || pos + 4 > s.Length)
        {
            throw new ModelFormatException("The LAZ layer table is truncated.");
        }

        var v = BinaryPrimitives.ReadUInt32LittleEndian(s[pos..]);
        pos += 4;
        if (v > int.MaxValue)
        {
            throw new ModelFormatException("A LAZ layer size is out of range.");
        }

        return (int)v;
    }

    // ---- POINT14 --------------------------------------------------------------------------------------------------------

    private struct GpsSeq
    {
        public int Last, Next;
        public long[] Times;
        public int[] Diffs;
        public int[] Extreme;

        public static GpsSeq From(long first)
        {
            var s = new GpsSeq { Times = new long[4], Diffs = new int[4], Extreme = new int[4] };
            s.Times[0] = first;
            return s;
        }
    }

    private sealed class Point
    {
        public int X, Y, Z;
        public byte BitFields, Flags, Classification, UserData;
        public ushort Intensity, PointSource;
        public short ScanAngle;
        public long GpsTime;
        public bool GpsTimeChange;

        public int ReturnNumber => BitFields & 0xF;

        public int Returns => BitFields >> 4;

        public int ScannerChannel => (Flags >> 4) & 3;

        public Point Clone() => (Point)MemberwiseClone();

        public static Point Unpack(ReadOnlySpan<byte> b) => new()
        {
            X = BinaryPrimitives.ReadInt32LittleEndian(b),
            Y = BinaryPrimitives.ReadInt32LittleEndian(b[4..]),
            Z = BinaryPrimitives.ReadInt32LittleEndian(b[8..]),
            Intensity = BinaryPrimitives.ReadUInt16LittleEndian(b[12..]),
            BitFields = b[14],
            Flags = b[15],
            Classification = b[16],
            UserData = b[17],
            ScanAngle = BinaryPrimitives.ReadInt16LittleEndian(b[18..]),
            PointSource = BinaryPrimitives.ReadUInt16LittleEndian(b[20..]),
            GpsTime = BinaryPrimitives.ReadInt64LittleEndian(b[22..]),
        };

        public void Pack(Span<byte> b)
        {
            BinaryPrimitives.WriteInt32LittleEndian(b, X);
            BinaryPrimitives.WriteInt32LittleEndian(b[4..], Y);
            BinaryPrimitives.WriteInt32LittleEndian(b[8..], Z);
            BinaryPrimitives.WriteUInt16LittleEndian(b[12..], Intensity);
            b[14] = BitFields;
            b[15] = Flags;
            b[16] = Classification;
            b[17] = UserData;
            BinaryPrimitives.WriteInt16LittleEndian(b[18..], ScanAngle);
            BinaryPrimitives.WriteUInt16LittleEndian(b[20..], PointSource);
            BinaryPrimitives.WriteInt64LittleEndian(b[22..], GpsTime);
        }
    }

    private sealed class Point14Context
    {
        public Point Last = new();
        public ushort[] LastIntensities = new ushort[8];
        public Median5[] MedianX = new Median5[12];
        public Median5[] MedianY = new Median5[12];
        public int[] LastZ = new int[8];
        public SymbolModel[] ChangedValues = new SymbolModel[8];
        public SymbolModel ScannerChannel = new(3);
        public SymbolModel?[] NumberOfReturns = new SymbolModel?[16];
        public SymbolModel?[] ReturnNumber = new SymbolModel?[16];
        public SymbolModel ReturnNumberGpsSame = new(13);
        public SymbolModel?[] Classification = new SymbolModel?[64];
        public SymbolModel?[] ClassificationFlags = new SymbolModel?[64];
        public SymbolModel?[] UserData = new SymbolModel?[64];
        public SymbolModel GpsMulti = new(GpsMultiTotal);
        public SymbolModel GpsNoDiff = new(5);
        public IntegerModel Dx = new(32, 2);
        public IntegerModel Dy = new(32, 22);
        public IntegerModel Z = new(32, 20);
        public IntegerModel Intensity = new(16, 4);
        public IntegerModel ScanAngle = new(16, 2);
        public IntegerModel SourceId = new(16, 1);
        public IntegerModel Gps = new(32, 9);
        public GpsSeq Seq;

        public Point14Context(Point point)
        {
            Last = point.Clone();
            Last.GpsTimeChange = false;
            Array.Fill(LastIntensities, point.Intensity);
            Array.Fill(LastZ, point.Z);
            for (var i = 0; i < 12; i++)
            {
                MedianX[i] = Median5.Create();
                MedianY[i] = Median5.Create();
            }

            for (var i = 0; i < 8; i++)
            {
                ChangedValues[i] = new SymbolModel(128);
            }

            Seq = GpsSeq.From(point.GpsTime);
        }
    }

    private sealed class Point14 : LayeredItem
    {
        private readonly Point14Context?[] _contexts = new Point14Context?[4];
        private readonly bool _v4;
        private int _current;
        private Arithmetic? _xy, _z, _class, _flags, _intensity, _angle, _user, _source, _gps;
        private readonly int[] _sizes = new int[9];
        private CancellationToken _token;

        public Point14(bool v4)
        {
            _v4 = v4;
            Size = 30;
        }

        public override void InitFirst(ReadOnlySpan<byte> raw, ref int context)
        {
            var point = Point.Unpack(raw);
            _current = point.ScannerChannel;
            context = _current;
            Array.Clear(_contexts);
            _contexts[_current] = new Point14Context(point);
        }

        public override void ReadLayerSizes(ReadOnlySpan<byte> span, ref int pos)
        {
            for (var i = 0; i < 9; i++)
            {
                _sizes[i] = U32(span, ref pos);
            }
        }

        public override void ReadLayers(ReadOnlyMemory<byte> file, ref int pos, CancellationToken token)
        {
            _token = token;
            _xy = Layer(file, ref pos, _sizes[0], token);
            _z = Layer(file, ref pos, _sizes[1], token);
            _class = Layer(file, ref pos, _sizes[2], token);
            _flags = Layer(file, ref pos, _sizes[3], token);
            _intensity = Layer(file, ref pos, _sizes[4], token);
            _angle = Layer(file, ref pos, _sizes[5], token);
            _user = Layer(file, ref pos, _sizes[6], token);
            _source = Layer(file, ref pos, _sizes[7], token);
            _gps = Layer(file, ref pos, _sizes[8], token);
        }

        public override void Next(Span<byte> item, ref int context)
        {
            if (_xy is null)
            {
                throw new ModelFormatException("The LAZ point layer is empty.");
            }

            var c = _contexts[_current]!;
            var last = c.Last;
            var lpr = (last.ReturnNumber == 1 ? 1 : 0) + (last.ReturnNumber >= last.Returns ? 2 : 0) + (last.GpsTimeChange ? 4 : 0);
            var changed = _xy.DecodeSymbol(c.ChangedValues[lpr]);

            if ((changed & (1 << 6)) != 0)
            {
                var diff = _xy.DecodeSymbol(c.ScannerChannel);
                var channel = (_current + diff + 1) % 4;
                if (_contexts[channel] is null)
                {
                    _contexts[channel] = new Point14Context(c.Last);
                }

                _current = channel;
                if (!_v4)
                {
                    context = _current;
                }

                var nc = _contexts[_current]!;
                nc.Last.Flags = (byte)((nc.Last.Flags & ~0x30) | ((channel << 4) & 0x30));
                c = nc;
                last = c.Last;
            }

            if (_v4)
            {
                context = _current;
            }

            var sourceChanged = (changed & (1 << 5)) != 0;
            var gpsChanged = (changed & (1 << 4)) != 0;
            var angleChanged = (changed & (1 << 3)) != 0;

            var lastN = last.Returns;
            var lastR = last.ReturnNumber;
            int n;
            if ((changed & (1 << 2)) != 0)
            {
                n = _xy.DecodeSymbol(c.NumberOfReturns[lastN] ??= new SymbolModel(16));
            }
            else
            {
                n = lastN;
            }

            last.BitFields = (byte)((last.BitFields & 0x0F) | ((n << 4) & 0xF0));

            int r;
            switch (changed & 3)
            {
                case 0: r = lastR; break;
                case 1: r = (lastR + 1) % 16; break;
                case 2: r = (lastR + 15) % 16; break;
                default:
                    if (gpsChanged)
                    {
                        r = _xy.DecodeSymbol(c.ReturnNumber[lastR] ??= new SymbolModel(16));
                    }
                    else
                    {
                        r = (lastR + _xy.DecodeSymbol(c.ReturnNumberGpsSame) + 2) % 16;
                    }

                    break;
            }

            last.BitFields = (byte)((last.BitFields & 0xF0) | (r & 0x0F));

            var m = NumberReturnMap6[n, r];
            var l = NumberReturnLevel8[n, r];
            var cpr = (r == 1 ? 2 : 0) + (r >= n ? 1 : 0);

            var idx = (m << 1) | (gpsChanged ? 1 : 0);
            var median = c.MedianX[idx].Get();
            var dx = c.Dx.Decompress(_xy, median, n == 1 ? 1 : 0);
            last.X = unchecked(last.X + dx);
            c.MedianX[idx].Add(dx);

            median = c.MedianY[idx].Get();
            var kBits = c.Dx.K;
            var ctx = (n == 1 ? 1 : 0) + (kBits < 20 ? kBits & ~1 : 20);
            var dy = c.Dy.Decompress(_xy, median, ctx);
            last.Y = unchecked(last.Y + dy);
            c.MedianY[idx].Add(dy);

            if (_z is not null)
            {
                kBits = (c.Dx.K + c.Dy.K) / 2;
                ctx = (n == 1 ? 1 : 0) + (kBits < 18 ? kBits & ~1 : 18);
                last.Z = c.Z.Decompress(_z, c.LastZ[l], ctx);
                c.LastZ[l] = last.Z;
            }

            if (_class is not null)
            {
                var ccc = (((last.Classification & 0x1F) << 1) + (cpr == 3 ? 1 : 0));
                last.Classification = (byte)_class.DecodeSymbol(c.Classification[ccc] ??= new SymbolModel(256));
            }

            if (_flags is not null)
            {
                var lastFlags = (((last.Flags >> 7) & 1) << 5) | (((last.Flags >> 6) & 1) << 4) | (last.Flags & 0x0F);
                var flags = _flags.DecodeSymbol(c.ClassificationFlags[lastFlags] ??= new SymbolModel(64));
                last.Flags = (byte)((((flags >> 5) & 1) << 7) | (((flags >> 4) & 1) << 6) | ((last.ScannerChannel << 4) & 0x30) | (flags & 0x0F));
            }

            if (_intensity is not null)
            {
                var i = (cpr << 1) | (gpsChanged ? 1 : 0);
                last.Intensity = (ushort)c.Intensity.Decompress(_intensity, c.LastIntensities[i], cpr);
                c.LastIntensities[i] = last.Intensity;
            }

            if (_angle is not null && angleChanged)
            {
                last.ScanAngle = (short)c.ScanAngle.Decompress(_angle, last.ScanAngle, gpsChanged ? 1 : 0);
            }

            if (_user is not null)
            {
                last.UserData = (byte)_user.DecodeSymbol(c.UserData[last.UserData / 4] ??= new SymbolModel(256));
            }

            if (_source is not null && sourceChanged)
            {
                last.PointSource = (ushort)c.SourceId.Decompress(_source, last.PointSource, 0);
            }

            last.GpsTimeChange = gpsChanged;
            if (_gps is not null && gpsChanged)
            {
                ReadGps(c);
                last.GpsTime = c.Seq.Times[c.Seq.Last];
            }

            last.GpsTimeChange = gpsChanged;
            last.Pack(item);
        }

        private void ReadGps(Point14Context c)
        {
            var dec = _gps!;
            for (var depth = 0; ; depth++)
            {
                _token.ThrowIfCancellationRequested();
                if (depth > 16)
                {
                    throw new ModelFormatException("The LAZ GPS time sequence switches too often.");
                }

                ref var s = ref c.Seq;
                int multi;
                if (s.Diffs[s.Last] == 0)
                {
                    multi = dec.DecodeSymbol(c.GpsNoDiff);
                    if (multi == 0)
                    {
                        s.Diffs[s.Last] = c.Gps.Decompress(dec, 0, 0);
                        s.Times[s.Last] = unchecked(s.Times[s.Last] + s.Diffs[s.Last]);
                        s.Extreme[s.Last] = 0;
                        return;
                    }

                    if (multi == 1)
                    {
                        s.Next = (s.Next + 1) & 3;
                        var lastTime = s.Times[s.Last];
                        long high = c.Gps.Decompress(dec, (int)(lastTime >> 32), 8);
                        s.Times[s.Next] = unchecked((high << 32) | dec.ReadInt());
                        s.Last = s.Next;
                        s.Diffs[s.Last] = 0;
                        s.Extreme[s.Last] = 0;
                        return;
                    }

                    s.Last = (s.Last + multi - 1) & 3;
                    continue;
                }

                multi = dec.DecodeSymbol(c.GpsMulti);
                if (multi == 1)
                {
                    s.Times[s.Last] = unchecked(s.Times[s.Last] + c.Gps.Decompress(dec, s.Diffs[s.Last], 1));
                    s.Extreme[s.Last] = 0;
                    return;
                }

                if (multi < GpsMultiCodeFull)
                {
                    int diff;
                    if (multi == 0)
                    {
                        diff = c.Gps.Decompress(dec, 0, 7);
                        Extreme(ref s, diff);
                    }
                    else if (multi < GpsMulti)
                    {
                        diff = c.Gps.Decompress(dec, unchecked(multi * s.Diffs[s.Last]), multi < 10 ? 2 : 3);
                    }
                    else if (multi == GpsMulti)
                    {
                        diff = c.Gps.Decompress(dec, unchecked(GpsMulti * s.Diffs[s.Last]), 4);
                        Extreme(ref s, diff);
                    }
                    else
                    {
                        multi = GpsMulti - multi;
                        if (multi > GpsMultiMinus)
                        {
                            diff = c.Gps.Decompress(dec, unchecked(multi * s.Diffs[s.Last]), 5);
                        }
                        else
                        {
                            diff = c.Gps.Decompress(dec, unchecked(GpsMultiMinus * s.Diffs[s.Last]), 6);
                            Extreme(ref s, diff);
                        }
                    }

                    s.Times[s.Last] = unchecked(s.Times[s.Last] + diff);
                    return;
                }

                if (multi == GpsMultiCodeFull)
                {
                    s.Next = (s.Next + 1) & 3;
                    long high = c.Gps.Decompress(dec, (int)(s.Times[s.Last] >> 32), 8);
                    s.Times[s.Next] = unchecked((high << 32) | dec.ReadInt());
                    s.Last = s.Next;
                    s.Diffs[s.Last] = 0;
                    s.Extreme[s.Last] = 0;
                    return;
                }

                s.Last = (s.Last + multi - GpsMultiCodeFull) & 3;
            }
        }

        private static void Extreme(ref GpsSeq s, int diff)
        {
            if (++s.Extreme[s.Last] > 3)
            {
                s.Diffs[s.Last] = diff;
                s.Extreme[s.Last] = 0;
            }
        }
    }

    // ---- RGB14, NIR14, WAVEPACKET14, BYTE14 -------------------------------------------------------------------------------

    private sealed class RgbModels
    {
        public readonly SymbolModel Used = new(128);
        public readonly SymbolModel[] Diff = Enumerable.Range(0, 6).Select(_ => new SymbolModel(256)).ToArray();
    }

    private static int Fold(int n) => n < 0 ? n + 256 : n > 255 ? n - 256 : n;

    private static int Clamp(int n) => n < 0 ? 0 : n > 255 ? 255 : n;

    private sealed class Rgb14 : LayeredItem
    {
        private readonly bool _v4;
        private readonly RgbModels?[] _models = new RgbModels?[4];
        private readonly bool[] _unused = new bool[4];
        private readonly int[][] _last = { new int[3], new int[3], new int[3], new int[3] };
        private int _lastContext;
        private int _layerSize;
        private Arithmetic? _dec;

        public Rgb14(bool v4)
        {
            _v4 = v4;
            Size = 6;
            for (var i = 0; i < 4; i++)
            {
                _models[i] = new RgbModels();
            }
        }

        public override void InitFirst(ReadOnlySpan<byte> raw, ref int context)
        {
            Array.Fill(_unused, true);
            for (var i = 0; i < 3; i++)
            {
                _last[context][i] = BinaryPrimitives.ReadUInt16LittleEndian(raw[(2 * i)..]);
            }

            _unused[context] = false;
            _lastContext = context;
        }

        public override void ReadLayerSizes(ReadOnlySpan<byte> span, ref int pos) => _layerSize = U32(span, ref pos);

        public override void ReadLayers(ReadOnlyMemory<byte> file, ref int pos, CancellationToken token) => _dec = Layer(file, ref pos, _layerSize, token);

        public override void Next(Span<byte> item, ref int context)
        {
            var lastIdx = _lastContext;
            if (_lastContext != context)
            {
                _lastContext = context;
                if (_unused[context])
                {
                    _models[context] = new RgbModels();
                    Array.Copy(_last[lastIdx], _last[context], 3);
                    _unused[context] = false;
                    if (!_v4)
                    {
                        lastIdx = context;
                    }
                }

                if (_v4)
                {
                    lastIdx = context;
                }
            }

            var last = _last[lastIdx];
            if (_dec is not null)
            {
                Decode(_dec, _models[_lastContext]!, last);
            }

            for (var i = 0; i < 3; i++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(item[(2 * i)..], (ushort)last[i]);
            }
        }

        private static void Decode(Arithmetic dec, RgbModels m, int[] last)
        {
            var sym = dec.DecodeSymbol(m.Used);
            var cur = new int[3];
            cur[0] = (sym & 1) != 0 ? Fold(dec.DecodeSymbol(m.Diff[0]) + (last[0] & 255)) : last[0] & 0xFF;
            cur[0] |= (sym & 2) != 0 ? Fold(dec.DecodeSymbol(m.Diff[1]) + (last[0] >> 8)) << 8 : last[0] & 0xFF00;
            if ((sym & 64) != 0)
            {
                var diff = (cur[0] & 0xFF) - (last[0] & 0xFF);
                cur[1] = (sym & 4) != 0 ? Fold(dec.DecodeSymbol(m.Diff[2]) + Clamp(diff + (last[1] & 255))) : last[1] & 0xFF;
                if ((sym & 16) != 0)
                {
                    diff = (diff + ((cur[1] & 0xFF) - (last[1] & 0xFF))) / 2;
                    cur[2] = Fold(dec.DecodeSymbol(m.Diff[4]) + Clamp(diff + (last[2] & 255)));
                }
                else
                {
                    cur[2] = last[2] & 0xFF;
                }

                diff = (cur[0] >> 8) - (last[0] >> 8);
                cur[1] |= (sym & 8) != 0 ? Fold(dec.DecodeSymbol(m.Diff[3]) + Clamp(diff + (last[1] >> 8))) << 8 : last[1] & 0xFF00;
                if ((sym & 32) != 0)
                {
                    diff = (diff + ((cur[1] >> 8) - (last[1] >> 8))) / 2;
                    cur[2] |= Fold(dec.DecodeSymbol(m.Diff[5]) + Clamp(diff + (last[2] >> 8))) << 8;
                }
                else
                {
                    cur[2] |= last[2] & 0xFF00;
                }
            }
            else
            {
                cur[1] = cur[0];
                cur[2] = cur[0];
            }

            Array.Copy(cur, last, 3);
        }
    }

    private sealed class NirModels
    {
        public readonly SymbolModel Used = new(4);
        public readonly SymbolModel Lower = new(256);
        public readonly SymbolModel Upper = new(256);
    }

    private sealed class Nir14 : LayeredItem
    {
        private readonly bool _v4;
        private readonly NirModels[] _models = { new(), new(), new(), new() };
        private readonly bool[] _unused = new bool[4];
        private readonly ushort[] _last = new ushort[4];
        private int _lastContext;
        private int _layerSize;
        private Arithmetic? _dec;

        public Nir14(bool v4)
        {
            _v4 = v4;
            Size = 2;
        }

        public override void InitFirst(ReadOnlySpan<byte> raw, ref int context)
        {
            Array.Fill(_unused, true);
            _last[context] = BinaryPrimitives.ReadUInt16LittleEndian(raw);
            _unused[context] = false;
            _lastContext = context;
        }

        public override void ReadLayerSizes(ReadOnlySpan<byte> span, ref int pos) => _layerSize = U32(span, ref pos);

        public override void ReadLayers(ReadOnlyMemory<byte> file, ref int pos, CancellationToken token) => _dec = Layer(file, ref pos, _layerSize, token);

        public override void Next(Span<byte> item, ref int context)
        {
            var lastIdx = _lastContext;
            if (_lastContext != context)
            {
                _lastContext = context;
                if (_unused[context])
                {
                    _last[context] = _last[lastIdx];
                    _unused[context] = false;
                    if (!_v4)
                    {
                        lastIdx = context;
                    }
                }

                if (_v4)
                {
                    lastIdx = context;
                }
            }

            var m = _models[_lastContext];
            var last = _last[lastIdx];
            if (_dec is not null)
            {
                var sym = _dec.DecodeSymbol(m.Used);
                int value = (sym & 1) != 0 ? (byte)(_dec.DecodeSymbol(m.Lower) + (last & 0xFF)) : last & 0x00FF;
                if ((sym & 2) != 0)
                {
                    value |= ((byte)(_dec.DecodeSymbol(m.Upper) + (last >> 8)) << 8) & 0xFF00;
                }
                else
                {
                    value |= last & 0xFF00;
                }

                _last[lastIdx] = (ushort)value;
            }

            BinaryPrimitives.WriteUInt16LittleEndian(item, _last[lastIdx]);
        }
    }

    private sealed class WaveCoder
    {
        public int LastOffsetDiff;
        public int LastSymOffsetDiff;
        public readonly SymbolModel PacketIndex = new(256);
        public readonly SymbolModel[] OffsetDiffModel = { new(4), new(4), new(4), new(4) };
        public readonly IntegerModel OffsetDiff = new(32, 1);
        public readonly IntegerModel PacketSizeModel = new(32, 1);
        public readonly IntegerModel ReturnPointModel = new(32, 1);
        public readonly IntegerModel Xyz = new(32, 3);
    }

    private readonly record struct Wave(byte Index, ulong Offset, uint Size, float ReturnPoint, float Dx, float Dy, float Dz)
    {
        public static Wave Unpack(ReadOnlySpan<byte> b) => new(
            b[0], BinaryPrimitives.ReadUInt64LittleEndian(b[1..]), BinaryPrimitives.ReadUInt32LittleEndian(b[9..]),
            BinaryPrimitives.ReadSingleLittleEndian(b[13..]), BinaryPrimitives.ReadSingleLittleEndian(b[17..]),
            BinaryPrimitives.ReadSingleLittleEndian(b[21..]), BinaryPrimitives.ReadSingleLittleEndian(b[25..]));

        public void Pack(Span<byte> b)
        {
            b[0] = Index;
            BinaryPrimitives.WriteUInt64LittleEndian(b[1..], Offset);
            BinaryPrimitives.WriteUInt32LittleEndian(b[9..], Size);
            BinaryPrimitives.WriteSingleLittleEndian(b[13..], ReturnPoint);
            BinaryPrimitives.WriteSingleLittleEndian(b[17..], Dx);
            BinaryPrimitives.WriteSingleLittleEndian(b[21..], Dy);
            BinaryPrimitives.WriteSingleLittleEndian(b[25..], Dz);
        }
    }

    private static int Bits(float f) => BitConverter.SingleToInt32Bits(f);

    private static float Float(int bits) => BitConverter.Int32BitsToSingle(bits);

    private sealed class Wave14 : LayeredItem
    {
        private readonly bool _v4;
        private readonly WaveCoder[] _coders = { new(), new(), new(), new() };
        private readonly bool[] _unused = new bool[4];
        private readonly Wave[] _last = new Wave[4];
        private int _lastContext;
        private int _layerSize;
        private Arithmetic? _dec;

        public Wave14(bool v4)
        {
            _v4 = v4;
            Size = 29;
        }

        public override void InitFirst(ReadOnlySpan<byte> raw, ref int context)
        {
            Array.Fill(_unused, true);
            _last[context] = Wave.Unpack(raw);
            _unused[context] = false;
            _lastContext = context;
        }

        public override void ReadLayerSizes(ReadOnlySpan<byte> span, ref int pos) => _layerSize = U32(span, ref pos);

        public override void ReadLayers(ReadOnlyMemory<byte> file, ref int pos, CancellationToken token) => _dec = Layer(file, ref pos, _layerSize, token);

        public override void Next(Span<byte> item, ref int context)
        {
            var lastIdx = _lastContext;
            if (_lastContext != context)
            {
                _lastContext = context;
                if (_unused[context])
                {
                    _last[context] = _last[lastIdx];
                    _unused[context] = false;
                    if (!_v4)
                    {
                        lastIdx = context;
                    }
                }

                if (_v4)
                {
                    lastIdx = context;
                }
            }

            if (_dec is not null)
            {
                var c = _coders[_lastContext];
                var last = _last[lastIdx];
                var index = (byte)_dec.DecodeSymbol(c.PacketIndex);
                c.LastSymOffsetDiff = _dec.DecodeSymbol(c.OffsetDiffModel[c.LastSymOffsetDiff]);
                ulong offset;
                switch (c.LastSymOffsetDiff)
                {
                    case 0: offset = last.Offset; break;
                    case 1: offset = last.Offset + last.Size; break;
                    case 2:
                        c.LastOffsetDiff = c.OffsetDiff.Decompress(_dec, c.LastOffsetDiff, 0);
                        offset = unchecked(last.Offset + (ulong)(long)c.LastOffsetDiff);
                        break;
                    default:
                        var lower = _dec.ReadInt();
                        var upper = _dec.ReadInt();
                        offset = ((ulong)upper << 32) | lower;
                        break;
                }

                var size = (uint)c.PacketSizeModel.Decompress(_dec, (int)last.Size, 0);
                var returnPoint = Float(c.ReturnPointModel.Decompress(_dec, Bits(last.ReturnPoint), 0));
                var dx = Float(c.Xyz.Decompress(_dec, Bits(last.Dx), 0));
                var dy = Float(c.Xyz.Decompress(_dec, Bits(last.Dy), 1));
                var dz = Float(c.Xyz.Decompress(_dec, Bits(last.Dz), 2));
                _last[lastIdx] = new Wave(index, offset, size, returnPoint, dx, dy, dz);
            }

            _last[lastIdx].Pack(item);
        }
    }

    private sealed class Bytes14 : LayeredItem
    {
        private readonly bool _v4;
        private readonly int _count;
        private readonly SymbolModel[][] _models = new SymbolModel[4][];
        private readonly bool[] _unused = new bool[4];
        private readonly byte[][] _last;
        private readonly int[] _sizes;
        private readonly Arithmetic?[] _decoders;
        private int _lastContext;

        public Bytes14(int count, bool v4)
        {
            _v4 = v4;
            _count = count;
            Size = count;
            _sizes = new int[count];
            _decoders = new Arithmetic?[count];
            _last = new byte[4][];
            for (var i = 0; i < 4; i++)
            {
                _last[i] = new byte[count];
                _models[i] = NewModels(count);
            }
        }

        private static SymbolModel[] NewModels(int count)
        {
            var models = new SymbolModel[count];
            for (var i = 0; i < count; i++)
            {
                models[i] = new SymbolModel(256);
            }

            return models;
        }

        public override void InitFirst(ReadOnlySpan<byte> raw, ref int context)
        {
            Array.Fill(_unused, true);
            raw.CopyTo(_last[context]);
            _lastContext = context;
            _unused[context] = false;
        }

        public override void ReadLayerSizes(ReadOnlySpan<byte> span, ref int pos)
        {
            for (var i = 0; i < _count; i++)
            {
                _sizes[i] = U32(span, ref pos);
            }
        }

        public override void ReadLayers(ReadOnlyMemory<byte> file, ref int pos, CancellationToken token)
        {
            for (var i = 0; i < _count; i++)
            {
                _decoders[i] = Layer(file, ref pos, _sizes[i], token);
            }
        }

        public override void Next(Span<byte> item, ref int context)
        {
            var lastIdx = _lastContext;
            if (_lastContext != context)
            {
                _lastContext = context;
                if (_unused[context])
                {
                    _models[context] = NewModels(_count);
                    _unused[context] = false;
                    _last[lastIdx].CopyTo(_last[context], 0);
                    if (!_v4)
                    {
                        lastIdx = context;
                    }
                }

                if (_v4)
                {
                    lastIdx = context;
                }
            }

            var last = _last[lastIdx];
            var models = _models[context];
            for (var i = 0; i < _count; i++)
            {
                if (_decoders[i] is { } dec)
                {
                    last[i] = (byte)(last[i] + dec.DecodeSymbol(models[i]));
                }
            }

            last.CopyTo(item);
        }
    }

    // ---- chunk driver ---------------------------------------------------------------------------------------------------

    /// <summary>Decodes one chunk starting at <paramref name="chunkStart"/>; returns how many points were written to <paramref name="output"/>.</summary>
    public static int DecodeChunk(ReadOnlyMemory<byte> file, int chunkStart, int chunkEnd, Item[] itemTypes, int recordLength, long expected, byte[] output, int outputStart, CancellationToken token)
    {
        var span = file.Span;
        var items = CreateItems(itemTypes);
        var pos = chunkStart;
        var context = 0;
        var offset = 0;
        foreach (var item in items)
        {
            if (pos + item.Size > chunkEnd || pos + item.Size > span.Length)
            {
                throw new ModelFormatException("A LAZ chunk is too short for its first point.");
            }

            item.InitFirst(span.Slice(pos, item.Size), ref context);
            span.Slice(pos, item.Size).CopyTo(output.AsSpan(outputStart + offset));
            offset += item.Size;
            pos += item.Size;
        }

        var count = U32(span, ref pos);
        if (count <= 0 || count > expected)
        {
            count = (int)Math.Min(count <= 0 ? expected : count, expected);
        }

        foreach (var item in items)
        {
            item.ReadLayerSizes(span, ref pos);
        }

        foreach (var item in items)
        {
            item.ReadLayers(file, ref pos, token);
        }

        for (var p = 1; p < count; p++)
        {
            Cancel.Every(token, p);
            context = 0;
            offset = 0;
            var record = output.AsSpan(outputStart + p * recordLength, recordLength);
            foreach (var item in items)
            {
                item.Next(record.Slice(offset, item.Size), ref context);
                offset += item.Size;
            }
        }

        return count;
    }
}
