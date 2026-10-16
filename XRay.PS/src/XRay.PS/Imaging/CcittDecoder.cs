// A managed CCITT Group 3/4 decoder. The original decoded CCITT images by wrapping the stream in a
// one-strip TIFF and handing it to its imaging library (libtiff underneath), choosing the TIFF
// compression from K: G4 for K < 0, T.4 one-dimensional for K = 0 and Modified Huffman for K > 0.
// This decoder follows libtiff's run-length decoder for those three modes, including how it
// synchronises on EOL codes, pads past the end of data with zeros and recovers from bad codes, so
// the decoded bitmap (and whether decoding fails at all) matches it.

namespace XRay.PS.Imaging;

internal static class CcittDecoder
{
    /// <summary>TIFF compression schemes the original selected.</summary>
    public enum Mode
    {
        ModifiedHuffman = 2,
        Group3OneD = 3,
        Group4 = 4,
    }

    private sealed class EndOfData : Exception;

    private sealed class Failure : Exception;

    private enum State
    {
        Null,
        Pass,
        Horiz,
        V0,
        VR,
        VL,
        Ext,
        Eol,
        Term,
        MakeUp,
    }

    private readonly record struct Entry(State State, int Width, int Param);


    /// <summary>Decodes to packed rows (MSB first, rows padded to bytes) in the original's final
    /// form: black is 1 when <paramref name="blackIs1"/>, else 0, and rows are bottom to top.
    /// Returns null where the original's decoder reported an error.</summary>
    public static byte[]? Decode(byte[] data, int columns, int rows, Mode mode, bool blackIs1)
    {
        if (columns <= 0 || rows <= 0 || (long)columns * rows > 1_000_000_000L)
            return null;
        int rowBytes = (columns + 7) / 8;
        var raw = new byte[checked(rowBytes * rows)];
        var decoder = new Run(data, columns, mode);
        bool ok;
        try
        {
            ok = decoder.DecodeAll(raw, rowBytes, rows);
        }
        catch (Failure)
        {
            return null;
        }
        if (!ok)
            return null;
        // Raw bits mark black runs with 1. The imaging library then applied the photometric
        // interpretation chosen from BlackIs1 and packed its own bilevel mode, which leaves black
        // as 1 exactly when BlackIs1 is true.
        if (!blackIs1)
        {
            for (int i = 0; i < raw.Length; i++)
                raw[i] = (byte)~raw[i];
        }
        // The original flipped the rows into PostScript image sample order.
        if (rows > 1)
        {
            var flipped = new byte[raw.Length];
            for (int r = 0; r < rows; r++)
                Buffer.BlockCopy(raw, r * rowBytes, flipped, (rows - 1 - r) * rowBytes, rowBytes);
            raw = flipped;
        }
        return raw;
    }

    private sealed class Run(byte[] data, int lastX, Mode mode)
    {
        private int _pos;
        private ulong _acc;
        private int _avail;
        private int _eolCount;
        private int[] _curRuns = new int[RunCapacity(lastX)];
        private int[] _refRuns = new int[RunCapacity(lastX)];

        private static int RunCapacity(int width) => checked(2 * ((width + 31) / 32 * 32) + 4);
        private int _line;

        private bool NeedBits(int n)
        {
            while (_avail < n)
            {
                if (_pos >= data.Length)
                {
                    if (_avail == 0)
                        return false;
                    _acc <<= n - _avail;
                    _avail = n;
                    return true;
                }
                _acc = (_acc << 8) | data[_pos++];
                _avail += 8;
            }
            return true;
        }

        private int GetBits(int n) => (int)((_acc >> (_avail - n)) & ((1UL << n) - 1));

        private void ClrBits(int n)
        {
            _avail -= n;
            _acc &= _avail >= 64 ? ulong.MaxValue : (1UL << _avail) - 1;
        }

        // Byte alignment for Modified Huffman rows.
        private void AlignToByte() => ClrBits(_avail & 7);

        private Entry Lookup(Entry[] table, int width)
        {
            if (!NeedBits(width))
                throw new EndOfData();
            var entry = table[GetBits(width)];
            ClrBits(entry.Width);
            return entry;
        }

        public bool DecodeAll(byte[] output, int rowBytes, int rows)
        {
            _refRuns[0] = lastX;
            _refRuns[1] = 0;
            int refCount = _refRuns.Length;
            for (int row = 0; row < rows; row++)
            {
                int count;
                try
                {
                    switch (mode)
                    {
                        case Mode.Group4:
                            count = Expand2D(refCount);
                            if (_eolCount != 0)
                            {
                                // An EOL ends a G4 strip: the row is written and decoding stops.
                                Fill(output, row * rowBytes, _curRuns, count);
                                return _line > 0;
                            }
                            break;
                        case Mode.Group3OneD:
                            SyncEol();
                            count = Expand1D();
                            break;
                        default:
                            count = Expand1D();
                            AlignToByte();
                            break;
                    }
                }
                catch (EndOfData)
                {
                    // Premature end of data: the partial row is written, then G4 reports success
                    // when at least one line was decoded and the one-dimensional modes fail.
                    Fill(output, row * rowBytes, _partialRuns, _partialCount);
                    return mode == Mode.Group4 && _line > 0;
                }
                Fill(output, row * rowBytes, _curRuns, count);
                _line++;
                if (mode == Mode.Group4)
                {
                    // An imaginary change for the reference line, then swap.
                    if (count < _curRuns.Length)
                        _curRuns[count] = 0;
                    (_curRuns, _refRuns) = (_refRuns, _curRuns);
                    refCount = _refRuns.Length;
                }
            }
            return true;
        }

        private int[] _partialRuns = [];
        private int _partialCount;

        private void SyncEol()
        {
            if (_eolCount == 0)
            {
                while (true)
                {
                    if (!NeedBits(11))
                        throw new EndOfData();
                    if (GetBits(11) == 0)
                        break;
                    ClrBits(1);
                }
            }
            while (true)
            {
                if (!NeedBits(8))
                    throw new EndOfData();
                if (GetBits(8) != 0)
                    break;
                ClrBits(8);
            }
            while (GetBits(1) == 0)
                ClrBits(1);
            ClrBits(1);
            _eolCount = 0;
        }

        // Run accumulation state, mirroring libtiff's a0 / RunLength / pa.
        private int _a0;
        private int _runLength;
        private int _pa;

        private void SetValue(int x)
        {
            if (_pa >= _curRuns.Length)
                throw new Failure();
            long value = (long)_runLength + x;
            if (x < 0 || value > int.MaxValue)
                throw new Failure();
            _curRuns[_pa++] = (int)value;
            _a0 += x;
            _runLength = 0;
        }

        private void CleanupRuns()
        {
            if (_runLength != 0)
                SetValue(0);
            if (_a0 != lastX)
            {
                while (_a0 > lastX && _pa > 0)
                    _a0 -= _curRuns[--_pa];
                if (_a0 < lastX)
                {
                    if (_a0 < 0)
                        _a0 = 0;
                    if ((_pa & 1) != 0)
                        SetValue(0);
                    SetValue(lastX - _a0);
                }
                else if (_a0 > lastX)
                {
                    SetValue(lastX);
                    SetValue(0);
                }
            }
        }

        private void BeginRow()
        {
            _a0 = 0;
            _runLength = 0;
            _pa = 0;
        }

        private EndOfData PrematureEof()
        {
            CleanupRuns();
            _partialRuns = _curRuns;
            _partialCount = _pa;
            return new EndOfData();
        }

        private int Expand1D()
        {
            BeginRow();
            try
            {
                while (true)
                {
                    // White run.
                    while (true)
                    {
                        var e = Lookup(WhiteTable, 12);
                        if (e.State == State.Eol)
                        {
                            _eolCount = 1;
                            goto Done;
                        }
                        if (e.State == State.Term)
                        {
                            SetValue(e.Param);
                            break;
                        }
                        if (e.State == State.MakeUp)
                        {
                            _a0 += e.Param;
                            _runLength += e.Param;
                            continue;
                        }
                        goto Done;
                    }
                    if (_a0 >= lastX)
                        goto Done;
                    // Black run.
                    while (true)
                    {
                        var e = Lookup(BlackTable, 13);
                        if (e.State == State.Eol)
                        {
                            _eolCount = 1;
                            goto Done;
                        }
                        if (e.State == State.Term)
                        {
                            SetValue(e.Param);
                            break;
                        }
                        if (e.State == State.MakeUp)
                        {
                            _a0 += e.Param;
                            _runLength += e.Param;
                            continue;
                        }
                        goto Done;
                    }
                    if (_a0 >= lastX)
                        goto Done;
                    if (_pa >= 2 && _curRuns[_pa - 1] == 0 && _curRuns[_pa - 2] == 0)
                        _pa -= 2;
                }
            }
            catch (EndOfData)
            {
                throw PrematureEof();
            }
        Done:
            CleanupRuns();
            return _pa;
        }

        private int Expand2D(int refCount)
        {
            BeginRow();
            int pb = 0;
            int b1 = _refRuns[pb++];
            void CheckB1()
            {
                if (_pa != 0)
                {
                    while (b1 <= _a0 && b1 < lastX)
                    {
                        if (pb + 1 >= refCount)
                            throw new Failure();
                        b1 += _refRuns[pb] + _refRuns[pb + 1];
                        pb += 2;
                    }
                }
            }
            int NextRef()
            {
                if (pb >= refCount)
                    throw new Failure();
                return _refRuns[pb++];
            }
            try
            {
                while (_a0 < lastX)
                {
                    var e = Lookup(MainTable, 7);
                    switch (e.State)
                    {
                        case State.Pass:
                            CheckB1();
                            b1 += NextRef();
                            _runLength += b1 - _a0;
                            _a0 = b1;
                            b1 += NextRef();
                            break;
                        case State.Horiz:
                            if ((_pa & 1) != 0)
                            {
                                if (!HorizontalRun(BlackTable, 13) || !HorizontalRun(WhiteTable, 12))
                                    goto Eol;
                            }
                            else
                            {
                                if (!HorizontalRun(WhiteTable, 12) || !HorizontalRun(BlackTable, 13))
                                    goto Eol;
                            }
                            CheckB1();
                            break;
                        case State.V0:
                            CheckB1();
                            SetValue(b1 - _a0);
                            b1 += NextRef();
                            break;
                        case State.VR:
                            CheckB1();
                            SetValue(b1 - _a0 + e.Param);
                            b1 += NextRef();
                            break;
                        case State.VL:
                            CheckB1();
                            if (b1 < _a0 + e.Param)
                                goto Eol;
                            SetValue(b1 - _a0 - e.Param);
                            if (pb <= 0)
                                throw new Failure();
                            b1 -= _refRuns[--pb];
                            break;
                        case State.Ext:
                            if (_pa >= _curRuns.Length)
                                throw new Failure();
                            _curRuns[_pa++] = lastX - _a0;
                            goto Eol;
                        case State.Eol:
                            if (_pa >= _curRuns.Length)
                                throw new Failure();
                            _curRuns[_pa++] = lastX - _a0;
                            if (!NeedBits(4))
                                throw new EndOfData();
                            ClrBits(4);
                            _eolCount = 1;
                            goto Eol;
                        default:
                            goto Eol;
                    }
                }
                if (_runLength != 0)
                {
                    if (_runLength + _a0 < lastX)
                    {
                        // Expect a final V0.
                        if (!NeedBits(1))
                            throw new EndOfData();
                        if (GetBits(1) == 0)
                            goto Eol;
                        ClrBits(1);
                    }
                    SetValue(0);
                }
            }
            catch (EndOfData)
            {
                throw PrematureEof();
            }
        Eol:
            CleanupRuns();
            return _pa;
        }

        private bool HorizontalRun(Entry[] table, int width)
        {
            while (true)
            {
                var e = Lookup(table, width);
                if (e.State == State.Term)
                {
                    SetValue(e.Param);
                    return true;
                }
                if (e.State == State.MakeUp)
                {
                    _a0 += e.Param;
                    _runLength += e.Param;
                    continue;
                }
                return false;
            }
        }

        private void Fill(byte[] output, int offset, int[] runs, int count)
        {
            int x = 0;
            for (int i = 0; i < count && x < lastX; i++)
            {
                int run = runs[i];
                if (run <= 0)
                    continue;
                if ((long)x + run > lastX)
                    run = lastX - x;
                if ((i & 1) != 0)
                {
                    for (int px = x; px < x + run; px++)
                        output[offset + (px >> 3)] |= (byte)(0x80 >> (px & 7));
                }
                x += run;
            }
        }
    }

    // Terminating and make-up codes (T.4 tables 2 and 3), as (length, code, run).
    private static readonly (int Len, int Code, int Run)[] WhiteCodes =
    [
        (8, 0x35, 0), (6, 0x7, 1), (4, 0x7, 2), (4, 0x8, 3), (4, 0xB, 4), (4, 0xC, 5), (4, 0xE, 6), (4, 0xF, 7),
        (5, 0x13, 8), (5, 0x14, 9), (5, 0x7, 10), (5, 0x8, 11), (6, 0x8, 12), (6, 0x3, 13), (6, 0x34, 14), (6, 0x35, 15),
        (6, 0x2A, 16), (6, 0x2B, 17), (7, 0x27, 18), (7, 0xC, 19), (7, 0x8, 20), (7, 0x17, 21), (7, 0x3, 22), (7, 0x4, 23),
        (7, 0x28, 24), (7, 0x2B, 25), (7, 0x13, 26), (7, 0x24, 27), (7, 0x18, 28), (8, 0x2, 29), (8, 0x3, 30), (8, 0x1A, 31),
        (8, 0x1B, 32), (8, 0x12, 33), (8, 0x13, 34), (8, 0x14, 35), (8, 0x15, 36), (8, 0x16, 37), (8, 0x17, 38), (8, 0x28, 39),
        (8, 0x29, 40), (8, 0x2A, 41), (8, 0x2B, 42), (8, 0x2C, 43), (8, 0x2D, 44), (8, 0x4, 45), (8, 0x5, 46), (8, 0xA, 47),
        (8, 0xB, 48), (8, 0x52, 49), (8, 0x53, 50), (8, 0x54, 51), (8, 0x55, 52), (8, 0x24, 53), (8, 0x25, 54), (8, 0x58, 55),
        (8, 0x59, 56), (8, 0x5A, 57), (8, 0x5B, 58), (8, 0x4A, 59), (8, 0x4B, 60), (8, 0x32, 61), (8, 0x33, 62), (8, 0x34, 63),
        (5, 0x1B, 64), (5, 0x12, 128), (6, 0x17, 192), (7, 0x37, 256), (8, 0x36, 320), (8, 0x37, 384), (8, 0x64, 448), (8, 0x65, 512),
        (8, 0x68, 576), (8, 0x67, 640), (9, 0xCC, 704), (9, 0xCD, 768), (9, 0xD2, 832), (9, 0xD3, 896), (9, 0xD4, 960), (9, 0xD5, 1024),
        (9, 0xD6, 1088), (9, 0xD7, 1152), (9, 0xD8, 1216), (9, 0xD9, 1280), (9, 0xDA, 1344), (9, 0xDB, 1408), (9, 0x98, 1472), (9, 0x99, 1536),
        (9, 0x9A, 1600), (6, 0x18, 1664), (9, 0x9B, 1728),
    ];

    private static readonly (int Len, int Code, int Run)[] BlackCodes =
    [
        (10, 0x37, 0), (3, 0x2, 1), (2, 0x3, 2), (2, 0x2, 3), (3, 0x3, 4), (4, 0x3, 5), (4, 0x2, 6), (5, 0x3, 7),
        (6, 0x5, 8), (6, 0x4, 9), (7, 0x4, 10), (7, 0x5, 11), (7, 0x7, 12), (8, 0x4, 13), (8, 0x7, 14), (9, 0x18, 15),
        (10, 0x17, 16), (10, 0x18, 17), (10, 0x8, 18), (11, 0x67, 19), (11, 0x68, 20), (11, 0x6C, 21), (11, 0x37, 22), (11, 0x28, 23),
        (11, 0x17, 24), (11, 0x18, 25), (12, 0xCA, 26), (12, 0xCB, 27), (12, 0xCC, 28), (12, 0xCD, 29), (12, 0x68, 30), (12, 0x69, 31),
        (12, 0x6A, 32), (12, 0x6B, 33), (12, 0xD2, 34), (12, 0xD3, 35), (12, 0xD4, 36), (12, 0xD5, 37), (12, 0xD6, 38), (12, 0xD7, 39),
        (12, 0x6C, 40), (12, 0x6D, 41), (12, 0xDA, 42), (12, 0xDB, 43), (12, 0x54, 44), (12, 0x55, 45), (12, 0x56, 46), (12, 0x57, 47),
        (12, 0x64, 48), (12, 0x65, 49), (12, 0x52, 50), (12, 0x53, 51), (12, 0x24, 52), (12, 0x37, 53), (12, 0x38, 54), (12, 0x27, 55),
        (12, 0x28, 56), (12, 0x58, 57), (12, 0x59, 58), (12, 0x2B, 59), (12, 0x2C, 60), (12, 0x5A, 61), (12, 0x66, 62), (12, 0x67, 63),
        (10, 0xF, 64), (12, 0xC8, 128), (12, 0xC9, 192), (12, 0x5B, 256), (12, 0x33, 320), (12, 0x34, 384), (12, 0x35, 448), (13, 0x6C, 512),
        (13, 0x6D, 576), (13, 0x4A, 640), (13, 0x4B, 704), (13, 0x4C, 768), (13, 0x4D, 832), (13, 0x72, 896), (13, 0x73, 960), (13, 0x74, 1024),
        (13, 0x75, 1088), (13, 0x76, 1152), (13, 0x77, 1216), (13, 0x52, 1280), (13, 0x53, 1344), (13, 0x54, 1408), (13, 0x55, 1472), (13, 0x5A, 1536),
        (13, 0x5B, 1600), (13, 0x64, 1664), (13, 0x65, 1728),
    ];

    // Extended make-up codes shared by both colors (T.4 table 3a).
    private static readonly (int Len, int Code, int Run)[] ExtendedMakeUp =
    [
        (11, 0x8, 1792), (11, 0xC, 1856), (11, 0xD, 1920), (12, 0x12, 1984), (12, 0x13, 2048), (12, 0x14, 2112), (12, 0x15, 2176),
        (12, 0x16, 2240), (12, 0x17, 2304), (12, 0x1C, 2368), (12, 0x1D, 2432), (12, 0x1E, 2496), (12, 0x1F, 2560),
    ];

    // Declared after the code lists: static fields initialise in textual order.
    private static readonly Entry[] WhiteTable = BuildRunTable(white: true);
    private static readonly Entry[] BlackTable = BuildRunTable(white: false);
    private static readonly Entry[] MainTable = BuildMainTable();

    private static Entry[] BuildRunTable(bool white)
    {
        int width = white ? 12 : 13;
        var table = new Entry[1 << width];
        void Put(int len, int code, Entry entry)
        {
            int shift = width - len;
            int start = code << shift;
            for (int i = 0; i < 1 << shift; i++)
                table[start + i] = entry;
        }
        foreach (var (len, code, run) in white ? WhiteCodes : BlackCodes)
            Put(len, code, new Entry(run < 64 ? State.Term : State.MakeUp, len, run));
        foreach (var (len, code, run) in ExtendedMakeUp)
            Put(len, code, new Entry(State.MakeUp, len, run));
        // Eleven zeros start an EOL; the decoder consumes them and leaves the final 1.
        Put(11, 0x000, new Entry(State.Eol, 11, 0));
        return table;
    }

    private static Entry[] BuildMainTable()
    {
        var table = new Entry[128];
        void Put(int len, int code, Entry entry)
        {
            int shift = 7 - len;
            int start = code << shift;
            for (int i = 0; i < 1 << shift; i++)
                table[start + i] = entry;
        }
        Put(1, 0x1, new Entry(State.V0, 1, 0));
        Put(3, 0x3, new Entry(State.VR, 3, 1));
        Put(6, 0x3, new Entry(State.VR, 6, 2));
        Put(7, 0x3, new Entry(State.VR, 7, 3));
        Put(3, 0x2, new Entry(State.VL, 3, 1));
        Put(6, 0x2, new Entry(State.VL, 6, 2));
        Put(7, 0x2, new Entry(State.VL, 7, 3));
        Put(3, 0x1, new Entry(State.Horiz, 3, 0));
        Put(4, 0x1, new Entry(State.Pass, 4, 0));
        Put(7, 0x1, new Entry(State.Ext, 7, 0));
        Put(7, 0x0, new Entry(State.Eol, 7, 0));
        return table;
    }
}
