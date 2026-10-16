// A managed baseline and progressive JPEG decoder. The original decoded DCT images through its
// imaging library (libjpeg); this decoder follows libjpeg's accurate integer IDCT, its "fancy"
// chroma upsampling and its color conversion tables so decoded pixels match it.

namespace XRay.PS.Imaging;

/// <summary>Decodes JPEG streams to 8-bit RGB.</summary>
internal static class JpegDecoder
{
    private sealed class Component
    {
        public int Id;
        public int H;
        public int V;
        public int Tq;
        public int Td;
        public int Ta;
        public int BlocksPerLine;
        public int BlocksPerColumn;
        public int BufferBlocksPerLine;
        public int BufferBlocksPerColumn;
        public short[] Coefficients = [];
        public int Pred;
        public int DownsampledWidth;
        public int DownsampledHeight;
        public byte[] Samples = [];
        public int SampleStride;
    }

    private sealed class HuffmanTable
    {
        public readonly int[] MaxCode = new int[18];
        public readonly int[] ValPtr = new int[17];
        public readonly int[] MinCode = new int[17];
        public byte[] Values = [];
        public bool Defined;
    }

    private sealed class DecodeException(string message) : Exception(message);

    /// <summary>Decodes to RGB; returns null when the stream cannot be decoded.</summary>
    public static (byte[] Rgb, int Width, int Height)? DecodeToRgb(byte[] data)
    {
        var image = DecodeImage(data);
        return image is null ? null : (image.ToRgb(), image.Width, image.Height);
    }

    /// <summary>Decodes to the imaging library's mode (L, RGB or inverted CMYK), or null.</summary>
    public static DecodedImage? DecodeImage(byte[] data)
    {
        try
        {
            return new Decoder(data).Decode();
        }
        catch (Exception ex) when (ex is DecodeException or IndexOutOfRangeException or ArgumentException or OverflowException or OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Reads the frame size and component count without decoding.</summary>
    public static (int Width, int Height, int Components)? ReadInfo(byte[] data)
    {
        int offset = 2;
        if (data.Length < 2 || data[0] != 0xFF || data[1] != 0xD8)
            return null;
        while (offset + 4 <= data.Length)
        {
            if (data[offset] != 0xFF)
                return null;
            int marker = data[offset + 1];
            offset += 2;
            if (marker is 0xD8 or 0xD9 or 0x01 || marker is >= 0xD0 and <= 0xD7)
                continue;
            if (marker == 0xFF)
            {
                offset--;
                continue;
            }
            int length = (data[offset] << 8) | data[offset + 1];
            if (marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC) && offset + 8 <= data.Length)
                return ((data[offset + 5] << 8) | data[offset + 6], (data[offset + 3] << 8) | data[offset + 4], data[offset + 7]);
            offset += length;
        }
        return null;
    }

    private static readonly int[] ZigZag = JpegEncoder.ZigZag;

    private sealed class Decoder(byte[] data)
    {
        private int _pos;
        private readonly int[][] _quant = new int[4][];
        private readonly HuffmanTable[] _dc = [new(), new(), new(), new()];
        private readonly HuffmanTable[] _ac = [new(), new(), new(), new()];
        private readonly List<Component> _components = [];
        private int _width;
        private int _height;
        private bool _progressive;
        private bool _frameSeen;
        private int _restartInterval;
        private int _hMax = 1;
        private int _vMax = 1;
        private int _mcusPerLine;
        private int _mcusPerColumn;
        private bool _sawJfif;
        private bool _sawAdobe;
        private int _adobeTransform;
        private int _eobRun;

        // Bit reader state.
        private int _bitBuffer;
        private int _bitCount;
        private bool _hitMarker;

        public DecodedImage Decode()
        {
            if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
                throw new DecodeException("not a JPEG");
            _pos = 2;
            while (true)
            {
                int marker = NextMarker();
                if (marker == 0xD9)
                    break;
                switch (marker)
                {
                    case 0xC0:
                    case 0xC1:
                    case 0xC2:
                        ReadFrame(marker == 0xC2);
                        break;
                    case 0xC4:
                        ReadHuffmanTables();
                        break;
                    case 0xDB:
                        ReadQuantTables();
                        break;
                    case 0xDD:
                    {
                        int length = ReadU16();
                        _restartInterval = ReadU16();
                        _pos += length - 4;
                        break;
                    }
                    case 0xDA:
                        ReadScan();
                        break;
                    case 0xE0:
                    {
                        int length = ReadU16();
                        if (length >= 7 && _pos + 5 <= data.Length && data[_pos] == 'J' && data[_pos + 1] == 'F' && data[_pos + 2] == 'I' && data[_pos + 3] == 'F' && data[_pos + 4] == 0)
                        {
                            _sawJfif = true;
                            if (length >= 16 && _pos + 12 <= data.Length)
                            {
                                _densityUnits = data[_pos + 7];
                                _xDensity = (data[_pos + 8] << 8) | data[_pos + 9];
                                _yDensity = (data[_pos + 10] << 8) | data[_pos + 11];
                            }
                        }
                        _pos += length - 2;
                        break;
                    }
                    case 0xEE:
                    {
                        int length = ReadU16();
                        if (length >= 14 && _pos + 12 <= data.Length && data[_pos] == 'A' && data[_pos + 1] == 'd' && data[_pos + 2] == 'o' && data[_pos + 3] == 'b' && data[_pos + 4] == 'e')
                        {
                            _sawAdobe = true;
                            _adobeTransform = data[_pos + 11];
                        }
                        _pos += length - 2;
                        break;
                    }
                    case >= 0xC3 and <= 0xCF:
                        throw new DecodeException("unsupported JPEG process");
                    case >= 0xD0 and <= 0xD7:
                    case 0x01:
                        break;
                    default:
                    {
                        int length = ReadU16();
                        if (length < 2)
                            throw new DecodeException("bad marker length");
                        _pos += length - 2;
                        break;
                    }
                }
                if (_pos > data.Length)
                    throw new DecodeException("truncated");
            }
            if (!_frameSeen)
                throw new DecodeException("no frame");
            return Output();
        }

        private int NextMarker()
        {
            // Skip any garbage before the next marker, as libjpeg does (with a warning).
            while (_pos < data.Length && data[_pos] != 0xFF)
                _pos++;
            while (_pos < data.Length && data[_pos] == 0xFF)
                _pos++;
            if (_pos >= data.Length)
            {
                if (_frameSeen && _scanSeen)
                    return 0xD9;
                throw new DecodeException("truncated");
            }
            return data[_pos++];
        }

        private bool _scanSeen;

        private int ReadU16()
        {
            if (_pos + 2 > data.Length)
                throw new DecodeException("truncated");
            int v = (data[_pos] << 8) | data[_pos + 1];
            _pos += 2;
            return v;
        }

        private void ReadFrame(bool progressive)
        {
            if (_frameSeen)
                throw new DecodeException("duplicate frame");
            int length = ReadU16();
            int end = _pos + length - 2;
            int precision = data[_pos++];
            if (precision != 8)
                throw new DecodeException("unsupported precision");
            _height = ReadU16();
            _width = ReadU16();
            int count = data[_pos++];
            if (_width <= 0 || _height <= 0 || count is not (1 or 3 or 4))
                throw new DecodeException("bad frame");
            if ((long)_width * _height > 400_000_000)
                throw new DecodeException("image too large");
            for (int i = 0; i < count; i++)
            {
                var c = new Component { Id = data[_pos], H = data[_pos + 1] >> 4, V = data[_pos + 1] & 15, Tq = data[_pos + 2] };
                if (c.H is < 1 or > 4 || c.V is < 1 or > 4 || c.Tq > 3)
                    throw new DecodeException("bad component");
                _pos += 3;
                _components.Add(c);
            }
            _pos = end;
            _progressive = progressive;
            _hMax = _components.Max(c => c.H);
            _vMax = _components.Max(c => c.V);
            _mcusPerLine = CeilDiv(_width, 8 * _hMax);
            _mcusPerColumn = CeilDiv(_height, 8 * _vMax);
            foreach (var c in _components)
            {
                c.DownsampledWidth = CeilDiv(_width * c.H, _hMax);
                c.DownsampledHeight = CeilDiv(_height * c.V, _vMax);
                c.BlocksPerLine = CeilDiv(c.DownsampledWidth, 8);
                c.BlocksPerColumn = CeilDiv(c.DownsampledHeight, 8);
                c.BufferBlocksPerLine = _mcusPerLine * c.H;
                c.BufferBlocksPerColumn = _mcusPerColumn * c.V;
                c.Coefficients = new short[checked(c.BufferBlocksPerLine * c.BufferBlocksPerColumn * 64)];
            }
            _frameSeen = true;
        }

        private static int CeilDiv(int a, int b) => (a + b - 1) / b;

        private void ReadQuantTables()
        {
            int length = ReadU16();
            int end = _pos + length - 2;
            while (_pos < end)
            {
                int pq = data[_pos] >> 4;
                int tq = data[_pos] & 15;
                _pos++;
                if (tq > 3)
                    throw new DecodeException("bad quant table");
                var table = new int[64];
                for (int i = 0; i < 64; i++)
                {
                    int value = pq == 0 ? data[_pos++] : ReadU16();
                    table[ZigZag[i]] = value;
                }
                _quant[tq] = table;
            }
            _pos = end;
        }

        private void ReadHuffmanTables()
        {
            int length = ReadU16();
            int end = _pos + length - 2;
            while (_pos < end)
            {
                int tc = data[_pos] >> 4;
                int th = data[_pos] & 15;
                _pos++;
                if (th > 3 || tc > 1)
                    throw new DecodeException("bad huffman table");
                var counts = new int[17];
                int total = 0;
                for (int i = 1; i <= 16; i++)
                {
                    counts[i] = data[_pos++];
                    total += counts[i];
                }
                if (total > 256)
                    throw new DecodeException("bad huffman table");
                var values = new byte[total];
                Array.Copy(data, _pos, values, 0, total);
                _pos += total;
                var table = tc == 0 ? _dc[th] : _ac[th];
                BuildTable(table, counts, values);
            }
            _pos = end;
        }

        private static void BuildTable(HuffmanTable table, int[] counts, byte[] values)
        {
            int code = 0;
            int k = 0;
            for (int l = 1; l <= 16; l++)
            {
                table.ValPtr[l] = k;
                table.MinCode[l] = code;
                code += counts[l];
                k += counts[l];
                table.MaxCode[l] = counts[l] > 0 ? code - 1 : -1;
                code <<= 1;
            }
            table.MaxCode[17] = int.MaxValue;
            table.Values = values;
            table.Defined = true;
        }

        private void ReadScan()
        {
            if (!_frameSeen)
                throw new DecodeException("scan before frame");
            int length = ReadU16();
            int end = _pos + length - 2;
            int count = data[_pos++];
            var scanComponents = new List<Component>();
            for (int i = 0; i < count; i++)
            {
                int id = data[_pos];
                int tables = data[_pos + 1];
                _pos += 2;
                var c = _components.FirstOrDefault(x => x.Id == id) ?? throw new DecodeException("unknown component");
                c.Td = tables >> 4;
                c.Ta = tables & 15;
                if (c.Td > 3 || c.Ta > 3)
                    throw new DecodeException("bad table selector");
                scanComponents.Add(c);
            }
            int ss = data[_pos];
            int se = data[_pos + 1];
            int ah = data[_pos + 2] >> 4;
            int al = data[_pos + 2] & 15;
            _pos = end;
            _scanSeen = true;
            DecodeScan(scanComponents, ss, se, ah, al);
        }

        private void ResetBits()
        {
            _bitBuffer = 0;
            _bitCount = 0;
            _hitMarker = false;
        }

        private int ReadBit()
        {
            if (_bitCount == 0)
            {
                if (_hitMarker || _pos >= data.Length)
                {
                    if (_pos >= data.Length)
                        throw new DecodeException("truncated entropy data");
                    // libjpeg feeds zeros after a marker.
                    _bitBuffer = 0;
                    _bitCount = 8;
                }
                else
                {
                    int b = data[_pos];
                    if (b == 0xFF)
                    {
                        int next = _pos + 1 < data.Length ? data[_pos + 1] : -1;
                        if (next == 0)
                        {
                            _pos += 2;
                        }
                        else
                        {
                            _hitMarker = true;
                            b = 0;
                        }
                    }
                    else
                    {
                        _pos++;
                    }
                    _bitBuffer = b;
                    _bitCount = 8;
                }
            }
            _bitCount--;
            return (_bitBuffer >> _bitCount) & 1;
        }

        private int ReadBits(int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++)
                v = (v << 1) | ReadBit();
            return v;
        }

        private int DecodeHuffman(HuffmanTable table)
        {
            if (!table.Defined)
                throw new DecodeException("undefined huffman table");
            int code = ReadBit();
            int l = 1;
            while (code > table.MaxCode[l])
            {
                code = (code << 1) | ReadBit();
                l++;
                if (l > 16)
                    return 0; // libjpeg: corrupt data, treat as zero
            }
            int index = table.ValPtr[l] + code - table.MinCode[l];
            return index < table.Values.Length ? table.Values[index] : 0;
        }

        private int Receive(int s)
        {
            if (s == 0)
                return 0;
            if (s > 16)
                throw new DecodeException("bad magnitude");
            int v = ReadBits(s);
            return v < (1 << (s - 1)) ? v - (1 << s) + 1 : v;
        }

        private void DecodeScan(List<Component> comps, int ss, int se, int ah, int al)
        {
            ResetBits();
            foreach (var c in comps)
                c.Pred = 0;
            _eobRun = 0;
            int mcuCount;
            bool single = comps.Count == 1;
            if (single)
            {
                var c = comps[0];
                mcuCount = c.BlocksPerLine * c.BlocksPerColumn;
            }
            else
            {
                mcuCount = _mcusPerLine * _mcusPerColumn;
            }
            int restart = _restartInterval;
            int mcu = 0;
            while (mcu < mcuCount)
            {
                int todo = restart > 0 ? Math.Min(restart, mcuCount - mcu) : mcuCount;
                for (int n = 0; n < todo; n++, mcu++)
                {
                    if ((mcu & 0xFF) == 0)
                        Internal.Guard.Check();
                    if (single)
                    {
                        var c = comps[0];
                        int row = mcu / c.BlocksPerLine;
                        int col = mcu % c.BlocksPerLine;
                        DecodeBlock(c, row, col, ss, se, ah, al);
                    }
                    else
                    {
                        int mcuRow = mcu / _mcusPerLine;
                        int mcuCol = mcu % _mcusPerLine;
                        foreach (var c in comps)
                        {
                            for (int v = 0; v < c.V; v++)
                            {
                                for (int h = 0; h < c.H; h++)
                                    DecodeBlock(c, mcuRow * c.V + v, mcuCol * c.H + h, ss, se, ah, al);
                            }
                        }
                    }
                }
                if (mcu >= mcuCount)
                    break;
                // Expect a restart marker.
                ResetBits();
                while (_pos + 1 < data.Length && !(data[_pos] == 0xFF && data[_pos + 1] is >= 0xD0 and <= 0xD7))
                {
                    if (data[_pos] == 0xFF && data[_pos + 1] != 0 && data[_pos + 1] != 0xFF)
                        break;
                    _pos++;
                }
                if (_pos + 1 < data.Length && data[_pos] == 0xFF && data[_pos + 1] is >= 0xD0 and <= 0xD7)
                    _pos += 2;
                foreach (var c in comps)
                    c.Pred = 0;
                _eobRun = 0;
            }
            ResetBits();
        }

        private void DecodeBlock(Component c, int row, int col, int ss, int se, int ah, int al)
        {
            if (row >= c.BufferBlocksPerColumn || col >= c.BufferBlocksPerLine)
                return;
            int offset = (row * c.BufferBlocksPerLine + col) * 64;
            var coeffs = c.Coefficients;
            if (!_progressive)
            {
                int t = DecodeHuffman(_dc[c.Td]);
                int diff = t == 0 ? 0 : Receive(t);
                c.Pred += diff;
                coeffs[offset] = (short)c.Pred;
                int k = 1;
                var ac = _ac[c.Ta];
                while (k < 64)
                {
                    int rs = DecodeHuffman(ac);
                    int s = rs & 15;
                    int r = rs >> 4;
                    if (s == 0)
                    {
                        if (r < 15)
                            break;
                        k += 16;
                        continue;
                    }
                    k += r;
                    coeffs[offset + NaturalOrder(k)] = (short)Receive(s);
                    k++;
                }
                return;
            }
            if (ss == 0)
            {
                // DC scans.
                if (ah == 0)
                {
                    int t = DecodeHuffman(_dc[c.Td]);
                    int diff = t == 0 ? 0 : Receive(t);
                    c.Pred += diff;
                    coeffs[offset] = (short)(c.Pred << al);
                }
                else if (ReadBit() != 0)
                {
                    coeffs[offset] |= (short)(1 << al);
                }
                return;
            }
            if (ah == 0)
                DecodeAcFirst(c, coeffs, offset, ss, se, al);
            else
                DecodeAcRefine(c, coeffs, offset, ss, se, al);
        }

        private void DecodeAcFirst(Component c, short[] coeffs, int offset, int ss, int se, int al)
        {
            if (_eobRun > 0)
            {
                _eobRun--;
                return;
            }
            var ac = _ac[c.Ta];
            int k = ss;
            while (k <= se)
            {
                int rs = DecodeHuffman(ac);
                int s = rs & 15;
                int r = rs >> 4;
                if (s == 0)
                {
                    if (r < 15)
                    {
                        _eobRun = (1 << r) - 1;
                        if (r != 0)
                            _eobRun += ReadBits(r);
                        break;
                    }
                    k += 16;
                    continue;
                }
                k += r;
                coeffs[offset + NaturalOrder(k)] = (short)(Receive(s) * (1 << al));
                k++;
            }
        }

        private static int NaturalOrder(int k) => k <= 63 ? ZigZag[k] : 63;

        // libjpeg's decode_mcu_AC_refine.
        private void DecodeAcRefine(Component c, short[] coeffs, int offset, int ss, int se, int al)
        {
            int p1 = 1 << al;
            int m1 = -1 << al;
            var ac = _ac[c.Ta];
            int k = ss;
            if (_eobRun == 0)
            {
                for (; k <= se; k++)
                {
                    int rs = DecodeHuffman(ac);
                    int r = rs >> 4;
                    int s = rs & 15;
                    if (s != 0)
                    {
                        s = ReadBit() != 0 ? p1 : m1;
                    }
                    else if (r != 15)
                    {
                        _eobRun = 1 << r;
                        if (r != 0)
                            _eobRun += ReadBits(r);
                        break;
                    }
                    do
                    {
                        int z = offset + NaturalOrder(k);
                        if (coeffs[z] != 0)
                        {
                            if (ReadBit() != 0 && (coeffs[z] & p1) == 0)
                                coeffs[z] = (short)(coeffs[z] >= 0 ? coeffs[z] + p1 : coeffs[z] + m1);
                        }
                        else if (--r < 0)
                        {
                            break;
                        }
                        k++;
                    }
                    while (k <= se);
                    if (s != 0)
                        coeffs[offset + NaturalOrder(k)] = (short)s;
                }
            }
            if (_eobRun > 0)
            {
                for (; k <= se; k++)
                {
                    int z = offset + NaturalOrder(k);
                    if (coeffs[z] != 0 && ReadBit() != 0 && (coeffs[z] & p1) == 0)
                        coeffs[z] = (short)(coeffs[z] >= 0 ? coeffs[z] + p1 : coeffs[z] + m1);
                }
                _eobRun--;
            }
        }

        private DecodedImage Output()
        {
            foreach (var c in _components)
            {
                var q = _quant[c.Tq] ?? throw new DecodeException("missing quant table");
                c.SampleStride = c.BufferBlocksPerLine * 8;
                c.Samples = new byte[checked(c.SampleStride * c.BufferBlocksPerColumn * 8)];
                var workspace = new int[64];
                for (int row = 0; row < c.BufferBlocksPerColumn; row++)
                {
                    Internal.Guard.Check();
                    for (int col = 0; col < c.BufferBlocksPerLine; col++)
                        IdctIslow(c.Coefficients, (row * c.BufferBlocksPerLine + col) * 64, q, workspace, c.Samples, row * 8 * c.SampleStride + col * 8, c.SampleStride);
                }
            }
            int width = _width;
            int height = _height;
            var planes = new byte[_components.Count][];
            for (int i = 0; i < _components.Count; i++)
                planes[i] = Upsample(_components[i]);
            int n = width * height;
            var (xDpi, yDpi) = Density();
            if (_components.Count == 1)
                return new DecodedImage("L", width, height, planes[0]) { XDpi = xDpi, YDpi = yDpi };
            if (_components.Count == 3)
            {
                var rgb = new byte[checked(n * 3)];
                bool isRgb = ColorSpaceIsRgb();
                for (int i = 0; i < n; i++)
                {
                    if (isRgb)
                    {
                        rgb[i * 3] = planes[0][i];
                        rgb[i * 3 + 1] = planes[1][i];
                        rgb[i * 3 + 2] = planes[2][i];
                    }
                    else
                    {
                        YccToRgb(planes[0][i], planes[1][i], planes[2][i], out rgb[i * 3], out rgb[i * 3 + 1], out rgb[i * 3 + 2]);
                    }
                }
                return new DecodedImage("RGB", width, height, rgb) { XDpi = xDpi, YDpi = yDpi };
            }
            // Four components: CMYK, or YCCK when the Adobe marker says so. The imaging library
            // reads the samples as inverted CMYK (Adobe convention).
            bool ycck = _sawAdobe && _adobeTransform != 0;
            var cmyk = new byte[checked(n * 4)];
            for (int i = 0; i < n; i++)
            {
                int c0, c1, c2;
                if (ycck)
                {
                    YccToRgb(planes[0][i], planes[1][i], planes[2][i], out byte r, out byte g, out byte b);
                    c0 = 255 - r;
                    c1 = 255 - g;
                    c2 = 255 - b;
                }
                else
                {
                    c0 = planes[0][i];
                    c1 = planes[1][i];
                    c2 = planes[2][i];
                }
                cmyk[i * 4] = (byte)(255 - c0);
                cmyk[i * 4 + 1] = (byte)(255 - c1);
                cmyk[i * 4 + 2] = (byte)(255 - c2);
                cmyk[i * 4 + 3] = (byte)(255 - planes[3][i]);
            }
            return new DecodedImage("CMYK", width, height, cmyk) { XDpi = xDpi, YDpi = yDpi };
        }

        private int _densityUnits;
        private int _xDensity;
        private int _yDensity;

        private (double?, double?) Density()
        {
            if (_xDensity <= 0 || _yDensity <= 0)
                return (null, null);
            return _densityUnits switch
            {
                1 => (_xDensity, _yDensity),
                2 => (_xDensity * 2.54, _yDensity * 2.54),
                _ => (null, null),
            };
        }

        private static int MulDiv255(int a, int b)
        {
            int tmp = a * b + 128;
            return ((tmp >> 8) + tmp) >> 8;
        }

        private static byte Clip8(int v) => (byte)Math.Clamp(v, 0, 255);

        private bool ColorSpaceIsRgb()
        {
            if (_sawJfif)
                return false;
            if (_sawAdobe)
                return _adobeTransform == 0;
            return _components[0].Id == 82 && _components[1].Id == 71 && _components[2].Id == 66;
        }

        private byte[] Upsample(Component c)
        {
            int width = _width;
            int height = _height;
            var output = new byte[checked(width * height)];
            int hRatio = _hMax / c.H;
            int vRatio = _vMax / c.V;
            if (_hMax % c.H != 0 || _vMax % c.V != 0)
                throw new DecodeException("unsupported sampling");
            var src = c.Samples;
            int stride = c.SampleStride;
            int dw = c.DownsampledWidth;
            int dh = c.DownsampledHeight;
            if (hRatio == 1 && vRatio == 1)
            {
                for (int y = 0; y < height; y++)
                    Array.Copy(src, y * stride, output, y * width, width);
                return output;
            }
            int outWidth = dw * hRatio;
            var row = new byte[Math.Max(outWidth, width) + 2];
            if (hRatio == 2 && vRatio == 1)
            {
                bool fancy = dw > 2;
                for (int y = 0; y < height; y++)
                {
                    int s = y * stride;
                    if (fancy)
                        H2Fancy(src, s, dw, row);
                    else
                        Replicate(src, s, dw, 2, row);
                    Array.Copy(row, 0, output, y * width, width);
                }
                return output;
            }
            if (hRatio == 1 && vRatio == 2)
            {
                for (int y = 0; y < height; y++)
                {
                    int inRow = y / 2;
                    bool above = (y & 1) == 0;
                    int other = Math.Clamp(above ? inRow - 1 : inRow + 1, 0, dh - 1);
                    int bias = above ? 1 : 2;
                    int s0 = inRow * stride;
                    int s1 = other * stride;
                    for (int x = 0; x < width; x++)
                        output[y * width + x] = (byte)((src[s0 + x] * 3 + src[s1 + x] + bias) >> 2);
                }
                return output;
            }
            if (hRatio == 2 && vRatio == 2)
            {
                bool fancy = dw > 2;
                for (int y = 0; y < height; y++)
                {
                    int inRow = y / 2;
                    if (!fancy)
                    {
                        Replicate(src, inRow * stride, dw, 2, row);
                        Array.Copy(row, 0, output, y * width, width);
                        continue;
                    }
                    bool above = (y & 1) == 0;
                    int other = Math.Clamp(above ? inRow - 1 : inRow + 1, 0, dh - 1);
                    int s0 = inRow * stride;
                    int s1 = other * stride;
                    int o = 0;
                    int thisColSum = src[s0] * 3 + src[s1];
                    int nextColSum = src[s0 + 1] * 3 + src[s1 + 1];
                    row[o++] = (byte)((thisColSum * 4 + 8) >> 4);
                    row[o++] = (byte)((thisColSum * 3 + nextColSum + 7) >> 4);
                    int lastColSum = thisColSum;
                    thisColSum = nextColSum;
                    int idx = 2;
                    for (int col = dw - 2; col > 0; col--)
                    {
                        nextColSum = src[s0 + idx] * 3 + src[s1 + idx];
                        idx++;
                        row[o++] = (byte)((thisColSum * 3 + lastColSum + 8) >> 4);
                        row[o++] = (byte)((thisColSum * 3 + nextColSum + 7) >> 4);
                        lastColSum = thisColSum;
                        thisColSum = nextColSum;
                    }
                    row[o++] = (byte)((thisColSum * 3 + lastColSum + 8) >> 4);
                    row[o] = (byte)((thisColSum * 4 + 7) >> 4);
                    Array.Copy(row, 0, output, y * width, width);
                }
                return output;
            }
            // Generic integral upsampling: replicate.
            for (int y = 0; y < height; y++)
            {
                int inRow = Math.Min(y / vRatio, dh - 1);
                Replicate(src, inRow * stride, dw, hRatio, row);
                Array.Copy(row, 0, output, y * width, width);
            }
            return output;
        }

        private static void H2Fancy(byte[] src, int s, int dw, byte[] row)
        {
            int o = 0;
            int inValue = src[s];
            row[o++] = (byte)inValue;
            row[o++] = (byte)((inValue * 3 + src[s + 1] + 2) >> 2);
            int p = s + 1;
            for (int col = dw - 2; col > 0; col--)
            {
                inValue = src[p] * 3;
                p++;
                row[o++] = (byte)((inValue + src[p - 2] + 1) >> 2);
                row[o++] = (byte)((inValue + src[p] + 2) >> 2);
            }
            inValue = src[p];
            row[o++] = (byte)((inValue * 3 + src[p - 1] + 1) >> 2);
            row[o] = (byte)inValue;
        }

        private static void Replicate(byte[] src, int s, int dw, int factor, byte[] row)
        {
            int o = 0;
            for (int x = 0; x < dw; x++)
            {
                byte v = src[s + x];
                for (int f = 0; f < factor; f++)
                    row[o++] = v;
            }
        }
    }

    private static readonly int[] CrRTab = new int[256];
    private static readonly int[] CbBTab = new int[256];
    private static readonly int[] CrGTab = new int[256];
    private static readonly int[] CbGTab = new int[256];

#pragma warning disable CA1810 // static tables built once
    static JpegDecoder()
#pragma warning restore CA1810
    {
        const int scaleBits = 16;
        const int oneHalf = 1 << (scaleBits - 1);
        static int Fix(double x) => (int)(x * (1 << scaleBits) + 0.5);
        for (int i = 0, x = -128; i <= 255; i++, x++)
        {
            CrRTab[i] = (Fix(1.40200) * x + oneHalf) >> scaleBits;
            CbBTab[i] = (Fix(1.77200) * x + oneHalf) >> scaleBits;
            CrGTab[i] = -Fix(0.71414) * x;
            CbGTab[i] = -Fix(0.34414) * x + oneHalf;
        }
    }

    private static void YccToRgb(int y, int cb, int cr, out byte r, out byte g, out byte b)
    {
        r = (byte)Math.Clamp(y + CrRTab[cr], 0, 255);
        g = (byte)Math.Clamp(y + ((CbGTab[cb] + CrGTab[cr]) >> 16), 0, 255);
        b = (byte)Math.Clamp(y + CbBTab[cb], 0, 255);
    }

    private const int ConstBits = 13;
    private const int Pass1Bits = 2;
    private const int Fix0298631336 = 2446;
    private const int Fix0390180644 = 3196;
    private const int Fix0541196100 = 4433;
    private const int Fix0765366865 = 6270;
    private const int Fix0899976223 = 7373;
    private const int Fix1175875602 = 9633;
    private const int Fix1501321110 = 12299;
    private const int Fix1847759065 = 15137;
    private const int Fix1961570560 = 16069;
    private const int Fix2053119869 = 16819;
    private const int Fix2562915447 = 20995;
    private const int Fix3072711026 = 25172;

    private static long Descale(long x, int n) => (x + (1L << (n - 1))) >> n;

    private static byte RangeLimit(long x)
    {
        int v = (int)(((x & 1023) ^ 512) - 512);
        return (byte)Math.Clamp(v + 128, 0, 255);
    }

    // libjpeg's jpeg_idct_islow (jidctint.c).
    private static void IdctIslow(short[] coef, int offset, int[] q, int[] ws, byte[] output, int outOffset, int stride)
    {
        for (int ctr = 0; ctr < 8; ctr++)
        {
            int i = offset + ctr;
            if (coef[i + 8] == 0 && coef[i + 16] == 0 && coef[i + 24] == 0 && coef[i + 32] == 0 && coef[i + 40] == 0 && coef[i + 48] == 0 && coef[i + 56] == 0)
            {
                int dcval = coef[i] * q[ctr] << Pass1Bits;
                for (int k = 0; k < 8; k++)
                    ws[k * 8 + ctr] = dcval;
                continue;
            }
            long z2 = coef[i + 16] * q[16 + ctr];
            long z3 = coef[i + 48] * q[48 + ctr];
            long z1 = (z2 + z3) * Fix0541196100;
            long tmp2 = z1 + z3 * -Fix1847759065;
            long tmp3 = z1 + z2 * Fix0765366865;
            z2 = coef[i] * q[ctr];
            z3 = coef[i + 32] * q[32 + ctr];
            long tmp0 = (z2 + z3) << ConstBits;
            long tmp1 = (z2 - z3) << ConstBits;
            long tmp10 = tmp0 + tmp3;
            long tmp13 = tmp0 - tmp3;
            long tmp11 = tmp1 + tmp2;
            long tmp12 = tmp1 - tmp2;
            tmp0 = coef[i + 56] * q[56 + ctr];
            tmp1 = coef[i + 40] * q[40 + ctr];
            tmp2 = coef[i + 24] * q[24 + ctr];
            tmp3 = coef[i + 8] * q[8 + ctr];
            OddPart(ref tmp0, ref tmp1, ref tmp2, ref tmp3);
            ws[0 * 8 + ctr] = (int)Descale(tmp10 + tmp3, ConstBits - Pass1Bits);
            ws[7 * 8 + ctr] = (int)Descale(tmp10 - tmp3, ConstBits - Pass1Bits);
            ws[1 * 8 + ctr] = (int)Descale(tmp11 + tmp2, ConstBits - Pass1Bits);
            ws[6 * 8 + ctr] = (int)Descale(tmp11 - tmp2, ConstBits - Pass1Bits);
            ws[2 * 8 + ctr] = (int)Descale(tmp12 + tmp1, ConstBits - Pass1Bits);
            ws[5 * 8 + ctr] = (int)Descale(tmp12 - tmp1, ConstBits - Pass1Bits);
            ws[3 * 8 + ctr] = (int)Descale(tmp13 + tmp0, ConstBits - Pass1Bits);
            ws[4 * 8 + ctr] = (int)Descale(tmp13 - tmp0, ConstBits - Pass1Bits);
        }
        for (int ctr = 0; ctr < 8; ctr++)
        {
            int w = ctr * 8;
            int o = outOffset + ctr * stride;
            if (ws[w + 1] == 0 && ws[w + 2] == 0 && ws[w + 3] == 0 && ws[w + 4] == 0 && ws[w + 5] == 0 && ws[w + 6] == 0 && ws[w + 7] == 0)
            {
                byte outval = RangeLimit(Descale(ws[w], Pass1Bits + 3));
                for (int k = 0; k < 8; k++)
                    output[o + k] = outval;
                continue;
            }
            long z2 = ws[w + 2];
            long z3 = ws[w + 6];
            long z1 = (z2 + z3) * Fix0541196100;
            long tmp2 = z1 + z3 * -Fix1847759065;
            long tmp3 = z1 + z2 * Fix0765366865;
            long tmp0 = ((long)ws[w] + ws[w + 4]) << ConstBits;
            long tmp1 = ((long)ws[w] - ws[w + 4]) << ConstBits;
            long tmp10 = tmp0 + tmp3;
            long tmp13 = tmp0 - tmp3;
            long tmp11 = tmp1 + tmp2;
            long tmp12 = tmp1 - tmp2;
            tmp0 = ws[w + 7];
            tmp1 = ws[w + 5];
            tmp2 = ws[w + 3];
            tmp3 = ws[w + 1];
            OddPart(ref tmp0, ref tmp1, ref tmp2, ref tmp3);
            const int shift = ConstBits + Pass1Bits + 3;
            output[o] = RangeLimit(Descale(tmp10 + tmp3, shift));
            output[o + 7] = RangeLimit(Descale(tmp10 - tmp3, shift));
            output[o + 1] = RangeLimit(Descale(tmp11 + tmp2, shift));
            output[o + 6] = RangeLimit(Descale(tmp11 - tmp2, shift));
            output[o + 2] = RangeLimit(Descale(tmp12 + tmp1, shift));
            output[o + 5] = RangeLimit(Descale(tmp12 - tmp1, shift));
            output[o + 3] = RangeLimit(Descale(tmp13 + tmp0, shift));
            output[o + 4] = RangeLimit(Descale(tmp13 - tmp0, shift));
        }
    }

    private static void OddPart(ref long tmp0, ref long tmp1, ref long tmp2, ref long tmp3)
    {
        long z1 = tmp0 + tmp3;
        long z2 = tmp1 + tmp2;
        long z3 = tmp0 + tmp2;
        long z4 = tmp1 + tmp3;
        long z5 = (z3 + z4) * Fix1175875602;
        tmp0 *= Fix0298631336;
        tmp1 *= Fix2053119869;
        tmp2 *= Fix3072711026;
        tmp3 *= Fix1501321110;
        z1 *= -Fix0899976223;
        z2 *= -Fix2562915447;
        z3 *= -Fix1961570560;
        z4 *= -Fix0390180644;
        z3 += z5;
        z4 += z5;
        tmp0 += z1 + z3;
        tmp1 += z2 + z4;
        tmp2 += z2 + z3;
        tmp3 += z1 + z4;
    }
}
