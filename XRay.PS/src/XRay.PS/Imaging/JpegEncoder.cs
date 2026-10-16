// Ported from the JPEG part of image/encoders.py: a baseline 4:4:4 encoder with a direct float
// DCT and the original's simplified Huffman tables, reproduced exactly.

namespace XRay.PS.Imaging;

internal static class JpegEncoder
{
    private static readonly double[,] CosTable = BuildCosTable();

    private static readonly int[,] LumaQTable =
    {
        { 16, 11, 10, 16, 24, 40, 51, 61 },
        { 12, 12, 14, 19, 26, 58, 60, 55 },
        { 14, 13, 16, 24, 40, 57, 69, 56 },
        { 14, 17, 22, 29, 51, 87, 80, 62 },
        { 18, 22, 37, 56, 68, 109, 103, 77 },
        { 24, 35, 55, 64, 81, 104, 113, 92 },
        { 49, 64, 78, 87, 103, 121, 120, 101 },
        { 72, 92, 95, 98, 112, 100, 103, 99 },
    };

    private static readonly int[,] ChromaQTable =
    {
        { 17, 18, 24, 47, 99, 99, 99, 99 },
        { 18, 21, 26, 66, 99, 99, 99, 99 },
        { 24, 26, 56, 99, 99, 99, 99, 99 },
        { 47, 66, 99, 99, 99, 99, 99, 99 },
        { 99, 99, 99, 99, 99, 99, 99, 99 },
        { 99, 99, 99, 99, 99, 99, 99, 99 },
        { 99, 99, 99, 99, 99, 99, 99, 99 },
        { 99, 99, 99, 99, 99, 99, 99, 99 },
    };

    internal static readonly int[] ZigZag =
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5, 12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51, 58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    private static readonly byte[] DcBits = [0, 0, 0, 12, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] DcValues = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];
    private static readonly byte[] AcBits = [0, 0, 0, 0, 0, 0, 0, 162, 0, 0, 0, 0, 0, 0, 0, 0];

    private static readonly byte[] AcValues =
    [
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
        0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08, 0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0,
        0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7,
        0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5,
        0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2,
        0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
        0xF9, 0xFA,
    ];

    private static double[,] BuildCosTable()
    {
        var table = new double[8, 8];
        for (int u = 0; u < 8; u++)
        {
            for (int x = 0; x < 8; x++)
                table[u, x] = Math.Cos((2 * x + 1) * u * Math.PI / 16);
        }
        return table;
    }

    public static byte[] Encode(RasterSurface surface, int quality)
    {
        int width = surface.Width;
        int height = surface.Height;
        var (yPlane, cbPlane, crPlane) = ToYCbCrPlanes(surface.Pixels, width, height);
        var qtLuma = ScaleQuantTable(LumaQTable, quality);
        var qtChroma = ScaleQuantTable(ChromaQTable, quality);
        var dcTable = BuildHuffmanTable(DcBits, DcValues);
        var acTable = BuildHuffmanTable(AcBits, AcValues);
        var writer = new BitWriter();
        int prevY = 0, prevCb = 0, prevCr = 0;
        int paddedWidth = (width + 7) / 8 * 8;
        int paddedHeight = (height + 7) / 8 * 8;
        var block = new int[64];
        for (int by = 0; by < paddedHeight; by += 8)
        {
            Internal.Guard.Check();
            for (int bx = 0; bx < paddedWidth; bx += 8)
            {
                FillBlock(block, yPlane, width, height, bx, by);
                prevY = WriteBlock(writer, block, qtLuma, dcTable, acTable, prevY);
                FillBlock(block, cbPlane, width, height, bx, by);
                prevCb = WriteBlock(writer, block, qtChroma, dcTable, acTable, prevCb);
                FillBlock(block, crPlane, width, height, bx, by);
                prevCr = WriteBlock(writer, block, qtChroma, dcTable, acTable, prevCr);
            }
        }
        byte[] entropy = writer.Finish();
        using var ms = new MemoryStream();
        ms.WriteByte(0xFF);
        ms.WriteByte(0xD8);
        WriteSegment(ms, 0xE0, [(byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0]);
        var dqt = new byte[130];
        dqt[0] = 0;
        dqt[65] = 1;
        for (int i = 0; i < 64; i++)
        {
            int index = ZigZag[i];
            dqt[1 + i] = (byte)qtLuma[index / 8, index % 8];
            dqt[66 + i] = (byte)qtChroma[index / 8, index % 8];
        }
        WriteSegment(ms, 0xDB, dqt);
        WriteSegment(ms, 0xC0, [8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 3, 1, 0x11, 0, 2, 0x11, 1, 3, 0x11, 1]);
        var dht = new List<byte>();
        void AddTable(byte cls, byte[] bits, byte[] values)
        {
            dht.Add(cls);
            dht.AddRange(bits);
            dht.AddRange(values);
        }
        AddTable(0x00, DcBits, DcValues);
        AddTable(0x10, AcBits, AcValues);
        AddTable(0x01, DcBits, DcValues);
        AddTable(0x11, AcBits, AcValues);
        WriteSegment(ms, 0xC4, [.. dht]);
        WriteSegment(ms, 0xDA, [3, 1, 0, 2, 0x11, 3, 0x11, 0, 0x3F, 0]);
        ms.Write(entropy);
        ms.WriteByte(0xFF);
        ms.WriteByte(0xD9);
        return ms.ToArray();
    }

    private static void WriteSegment(Stream stream, byte marker, byte[] payload)
    {
        int length = payload.Length + 2;
        stream.WriteByte(0xFF);
        stream.WriteByte(marker);
        stream.WriteByte((byte)(length >> 8));
        stream.WriteByte((byte)length);
        stream.Write(payload);
    }

    private static (byte[] Y, byte[] Cb, byte[] Cr) ToYCbCrPlanes(byte[] pixels, int width, int height)
    {
        int count = width * height;
        var yPlane = new byte[count];
        var cbPlane = new byte[count];
        var crPlane = new byte[count];
        for (int i = 0; i < count; i++)
        {
            double r = pixels[i * 4], g = pixels[i * 4 + 1], b = pixels[i * 4 + 2];
            yPlane[i] = ClampByte(0.299 * r + 0.587 * g + 0.114 * b);
            cbPlane[i] = ClampByte(-0.168736 * r - 0.331264 * g + 0.5 * b + 128);
            crPlane[i] = ClampByte(0.5 * r - 0.418688 * g - 0.081312 * b + 128);
        }
        return (yPlane, cbPlane, crPlane);
    }

    private static byte ClampByte(double value) => (byte)Math.Clamp(Math.Round(value, MidpointRounding.ToEven), 0, 255);

    private static void FillBlock(int[] block, byte[] plane, int width, int height, int bx, int by)
    {
        for (int y = 0; y < 8; y++)
        {
            int sy = Math.Min(height - 1, by + y);
            for (int x = 0; x < 8; x++)
            {
                int sx = Math.Min(width - 1, bx + x);
                block[y * 8 + x] = plane[sy * width + sx] - 128;
            }
        }
    }

    private static int WriteBlock(BitWriter writer, int[] block, int[,] qt, (int Code, int Length)[] dcTable, (int Code, int Length)[] acTable, int prevDc)
    {
        var coeffs = DctQuantize(block, qt);
        Span<int> ordered = stackalloc int[64];
        for (int i = 0; i < 64; i++)
            ordered[i] = coeffs[ZigZag[i]];
        int dcDiff = ordered[0] - prevDc;
        prevDc = ordered[0];
        var (size, bits) = MagnitudeBits(dcDiff);
        var (code, length) = dcTable[size];
        writer.Write(code, length);
        if (size != 0)
            writer.Write(bits, size);
        int zeroRun = 0;
        for (int i = 1; i < 64; i++)
        {
            int coeff = ordered[i];
            if (coeff == 0)
            {
                zeroRun++;
                if (zeroRun == 16)
                {
                    (code, length) = acTable[0xF0];
                    writer.Write(code, length);
                    zeroRun = 0;
                }
                continue;
            }
            while (zeroRun > 15)
            {
                (code, length) = acTable[0xF0];
                writer.Write(code, length);
                zeroRun -= 16;
            }
            (size, bits) = MagnitudeBits(coeff);
            int symbol = (zeroRun << 4) | size;
            (code, length) = acTable[symbol];
            if (length == 0)
                throw new HostErrorException("KeyError", symbol.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.Write(code, length);
            writer.Write(bits, size);
            zeroRun = 0;
        }
        if (zeroRun != 0)
        {
            (code, length) = acTable[0];
            writer.Write(code, length);
        }
        return prevDc;
    }

    private static int[] DctQuantize(int[] block, int[,] qt)
    {
        var coeffs = new int[64];
        double invSqrt2 = 1.0 / Math.Sqrt(2);
        for (int v = 0; v < 8; v++)
        {
            for (int u = 0; u < 8; u++)
            {
                double total = 0.0;
                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                        total += block[y * 8 + x] * CosTable[u, x] * CosTable[v, y];
                }
                double cu = u == 0 ? invSqrt2 : 1.0;
                double cv = v == 0 ? invSqrt2 : 1.0;
                double value = 0.25 * cu * cv * total;
                coeffs[v * 8 + u] = (int)Math.Round(value / qt[v, u], MidpointRounding.ToEven);
            }
        }
        return coeffs;
    }

    private static (int Code, int Length)[] BuildHuffmanTable(byte[] bits, byte[] values)
    {
        var table = new (int, int)[256];
        int code = 0;
        int idx = 0;
        for (int length = 1; length <= bits.Length; length++)
        {
            for (int i = 0; i < bits[length - 1]; i++)
            {
                table[values[idx]] = (code, length);
                code++;
                idx++;
            }
            code <<= 1;
        }
        return table;
    }

    private static (int Size, int Bits) MagnitudeBits(int value)
    {
        if (value == 0)
            return (0, 0);
        int abs = Math.Abs(value);
        int size = 32 - System.Numerics.BitOperations.LeadingZeroCount((uint)abs);
        if (value < 0)
            value = (1 << size) + value - 1;
        return (size, value);
    }

    private static int[,] ScaleQuantTable(int[,] table, int quality)
    {
        quality = Math.Clamp(quality, 1, 100);
        double scale = quality < 50 ? 5000.0 / quality : 200 - quality * 2;
        var scaled = new int[8, 8];
        for (int r = 0; r < 8; r++)
        {
            for (int c = 0; c < 8; c++)
                scaled[r, c] = Math.Clamp((int)((table[r, c] * scale + 50) / 100), 1, 255);
        }
        return scaled;
    }

    private sealed class BitWriter
    {
        private long _buffer;
        private int _bits;
        private readonly MemoryStream _output = new();

        public void Write(int value, int length)
        {
            _buffer = (_buffer << length) | (uint)value;
            _bits += length;
            while (_bits >= 8)
            {
                _bits -= 8;
                byte b = (byte)((_buffer >> _bits) & 0xFF);
                _output.WriteByte(b);
                if (b == 0xFF)
                    _output.WriteByte(0x00);
            }
            _buffer &= _bits != 0 ? (1L << _bits) - 1 : 0;
        }

        public byte[] Finish()
        {
            if (_bits != 0)
                Write((1 << _bits) - 1, _bits);
            return _output.ToArray();
        }
    }
}
